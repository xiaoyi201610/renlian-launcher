# -*- coding: utf-8 -*-
"""
CK3 崩溃诊断收集器（集成版）
============================
移植自独立的 ck3_diag_collect.ps1，用 Python 重写，随启动器一起分发。

采集 8 类信息并打包成 zip：
  00_系统与硬件 / 01_游戏版本 / 02_崩溃记录 / 03_exceptions
  04_logs / 05_mod列表 / 06_启动器日志 / 07_dxdiag / 08_存档列表 / 99_摘要

设计要点：
  · 超大日志只保留 前800行 + 后3000行，避免收集包失控
  · 崩溃目录只取最近 2 次，且只复制小文件，大文件列清单
  · 页文件不足、没有 crashes 目录这类"关键信号"会写进摘要
"""

from __future__ import annotations

import os
import json
import time
import ctypes
import shutil
import tempfile
import zipfile
import subprocess

LOG_FILES = ("error.log", "game.log", "setup.log", "system.log",
             "gui_warnings.log", "multiplayer.log", "debug.log")
BIG_KB = 2048


class _MEMSTAT(ctypes.Structure):
    _fields_ = [
        ("dwLength", ctypes.c_ulong),
        ("dwMemoryLoad", ctypes.c_ulong),
        ("ullTotalPhys", ctypes.c_ulonglong),
        ("ullAvailPhys", ctypes.c_ulonglong),
        ("ullTotalPageFile", ctypes.c_ulonglong),
        ("ullAvailPageFile", ctypes.c_ulonglong),
        ("ullTotalVirtual", ctypes.c_ulonglong),
        ("ullAvailVirtual", ctypes.c_ulonglong),
        ("ullAvailExtendedVirtual", ctypes.c_ulonglong),
    ]


def memory_stats() -> dict:
    """物理内存与提交上限（GlobalMemoryStatusEx，比 WMI 快且不依赖 PowerShell）。"""
    try:
        ms = _MEMSTAT()
        ms.dwLength = ctypes.sizeof(_MEMSTAT)
        if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(ms)):
            gb = 1024 ** 3
            return {
                "totalPhysGB": round(ms.ullTotalPhys / gb, 2),
                "availPhysGB": round(ms.ullAvailPhys / gb, 2),
                "totalPageGB": round(ms.ullTotalPageFile / gb, 2),
                "availPageGB": round(ms.ullAvailPageFile / gb, 2),
            }
    except Exception:
        pass
    return {}


def _run_ps(script: str, timeout: int = 45) -> dict:
    """执行一段 PowerShell 脚本，结果经 UTF-8 文件回传（避免控制台中文乱码）。"""
    tmp = os.path.join(tempfile.gettempdir(), "ck3_ps_%d.json" % int(time.time() * 1000))
    cmd = (
        "[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script +
        "|ConvertTo-Json -Depth 6 -Compress|Out-File -LiteralPath '%s' -Encoding utf8"
        % tmp.replace("'", "''")
    )
    try:
        subprocess.run(
            ["powershell", "-NoProfile", "-NonInteractive", "-Command", cmd],
            capture_output=True, timeout=timeout,
        )
        if os.path.isfile(tmp):
            with open(tmp, "r", encoding="utf-8-sig", errors="ignore") as f:
                txt = f.read().strip()
            try:
                os.remove(tmp)
            except Exception:
                pass
            if txt:
                return json.loads(txt)
    except Exception:
        pass
    return {}


def _copy_trimmed(src: str, dst: str, max_kb: int = BIG_KB) -> bool:
    """复制文件；超过 max_kb 的只保留 前800行 + 后3000行。"""
    if not os.path.isfile(src):
        return False
    try:
        size = os.path.getsize(src)
        if size <= max_kb * 1024:
            shutil.copy2(src, dst)
            return True
        with open(src, "r", encoding="utf-8", errors="replace") as f:
            head = [next(f, "") for _ in range(800)]
        with open(src, "rb") as f:
            f.seek(max(0, size - 400000))
            tail = f.read().decode("utf-8", errors="replace").splitlines()[-3000:]
        with open(dst, "w", encoding="utf-8") as f:
            f.writelines(head)
            f.write("\n==== 原文件 %.1f MB 过大，这里只保留 前800行 + 后3000行 ====\n" % (size / 1048576))
            f.write("\n".join(tail))
        return True
    except Exception:
        return False


def _list(v):
    if isinstance(v, dict):
        return [v]
    return v or []


def collect(cfg: dict, include_dxdiag: bool = False, progress=None) -> dict:
    user_dir = cfg.get("userDir") or ""

    def step(msg):
        if progress:
            try:
                progress(msg)
            except Exception:
                pass

    host = os.environ.get("COMPUTERNAME") or "PC"
    stamp = time.strftime("%Y%m%d-%H%M%S")
    out = os.path.join(tempfile.mkdtemp(prefix="ck3diag_"), f"CK3_diag_{host}_{stamp}")
    os.makedirs(out, exist_ok=True)

    summary: list[str] = []
    items: list[dict] = []

    def item(name, ok, note=""):
        items.append({"name": name, "ok": bool(ok), "note": note})

    # ---------- 1 系统与硬件 ----------
    step("采集系统与硬件信息…")
    mem = {}
    try:
        mem = memory_stats()
        hw = _run_ps(
            "$o=Get-CimInstance Win32_OperatingSystem|Select-Object Caption,Version,BuildNumber,OSArchitecture;"
            "$g=@(Get-CimInstance Win32_VideoController|Select-Object Name,DriverVersion,DriverDate,VideoModeDescription);"
            "$p=@(Get-CimInstance Win32_PageFileUsage|Select-Object Name,AllocatedBaseSize,PeakUsage);"
            "$d=@(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3'|Select-Object DeviceID,FileSystem,"
            "@{n='FreeGB';e={[math]::Round($_.FreeSpace/1GB,1)}},"
            "@{n='SizeGB';e={[math]::Round($_.Size/1GB,1)}});"
            "$c=Get-CimInstance Win32_Processor|Select-Object -First 1 Name,NumberOfCores,NumberOfLogicalProcessors;"
            "[pscustomobject]@{os=$o;gpu=$g;pf=$p;disk=$d;cpu=$c}"
        )
        osi = hw.get("os") or {}
        cpu = hw.get("cpu") or {}
        gpus = _list(hw.get("gpu"))
        pfs = _list(hw.get("pf"))
        disks = _list(hw.get("disk"))

        L = ["==== 操作系统 ====", ""]
        for k, lab in (("Caption", "名称"), ("Version", "版本"),
                       ("BuildNumber", "内部版本"), ("OSArchitecture", "架构")):
            L.append("%s: %s" % (lab, osi.get(k, "")))
        L += ["", "==== CPU ====", ""]
        L.append("型号: %s" % cpu.get("Name", ""))
        L.append("核心/线程: %s / %s" % (cpu.get("NumberOfCores", ""),
                                        cpu.get("NumberOfLogicalProcessors", "")))
        L += ["", "==== 物理内存 ====", ""]
        L.append("TotalPhysicalMemory(GB): %s" % mem.get("totalPhysGB", ""))
        L.append("AvailablePhysicalMemory(GB): %s" % mem.get("availPhysGB", ""))
        L += ["", "==== 提交上限 / 虚拟内存（关键：大包靠这个撑住）====", ""]
        L.append("TotalPageFile(GB): %s" % mem.get("totalPageGB", ""))
        L.append("AvailablePageFile(GB): %s" % mem.get("availPageGB", ""))
        for pf in pfs:
            L.append("页文件: %s  分配 %s MB  峰值 %s MB" % (
                pf.get("Name", ""), pf.get("AllocatedBaseSize", ""), pf.get("PeakUsage", "")))
        if not pfs:
            L.append("！！没有检测到页文件：物理内存不足时游戏会被系统直接结束，且不留任何日志")
        L += ["", "==== 显卡 ====", ""]
        for g in gpus:
            L.append("%s  驱动 %s  日期 %s  %s" % (
                g.get("Name", ""), g.get("DriverVersion", ""),
                g.get("DriverDate", ""), g.get("VideoModeDescription", "")))
        L += ["", "==== 磁盘 ====", ""]
        for dk in disks:
            L.append("%s  %s  剩余 %s GB / 共 %s GB" % (
                dk.get("DeviceID", ""), dk.get("FileSystem", ""),
                dk.get("FreeGB", ""), dk.get("SizeGB", "")))
        with open(os.path.join(out, "00_系统与硬件.txt"), "w", encoding="utf-8") as f:
            f.write("\n".join(L))
        item("系统与硬件", True, "内存 %s GB / 提交上限 %s GB" % (
            mem.get("totalPhysGB", "?"), mem.get("totalPageGB", "?")))
        summary.append("物理内存 %s GB，提交上限 %s GB" % (
            mem.get("totalPhysGB", "?"), mem.get("totalPageGB", "?")))
        if mem and (mem["totalPageGB"] - mem["totalPhysGB"]) < 4:
            summary.append("【注意】提交上限与物理内存相差不到 4 GB —— 页文件可能被关闭或设得过小，"
                           "大包启动时容易被系统直接结束进程，且不留日志。")
    except Exception as ex:
        item("系统与硬件", False, str(ex))

    # ---------- 2 游戏版本 ----------
    step("读取游戏版本与运行环境…")
    try:
        syslog = os.path.join(user_dir, "logs", "system.log")
        got = False
        if os.path.isfile(syslog):
            keys = ("Git Version", "Build Timestamp", "System Memory", "GPU Name",
                    "GPU Dedicated", "CPU Model", "OS: ")
            keep = []
            with open(syslog, "r", encoding="utf-8", errors="replace") as f:
                for line in f:
                    if any(k in line for k in keys):
                        keep.append(line.strip())
            if keep:
                with open(os.path.join(out, "01_游戏版本与运行环境.txt"), "w", encoding="utf-8") as f:
                    f.write("\n".join(keep))
                got = True
        item("游戏版本", got, "来自 logs/system.log" if got else "system.log 里没有版本行")
    except Exception as ex:
        item("游戏版本", False, str(ex))

    # ---------- 3 崩溃记录 ----------
    step("收集崩溃记录…")
    try:
        cdir = os.path.join(user_dir, "crashes")
        crash_dirs = []
        if os.path.isdir(cdir):
            ds = [d for d in os.scandir(cdir) if d.is_dir()]
            ds.sort(key=lambda d: d.stat().st_mtime, reverse=True)
            crash_dirs = ds[:2]
        if crash_dirs:
            for d in crash_dirs:
                dst = os.path.join(out, "02_崩溃_" + d.name)
                os.makedirs(dst, exist_ok=True)
                for fn in ("exception.txt", "meta.yml", "pdx_settings.txt"):
                    src = os.path.join(d.path, fn)
                    if os.path.isfile(src):
                        try:
                            shutil.copy2(src, os.path.join(dst, fn))
                        except Exception:
                            pass
                rows = []
                for root, _dd, names in os.walk(d.path):
                    for n in names:
                        if n in ("exception.txt", "meta.yml", "pdx_settings.txt"):
                            continue
                        fp = os.path.join(root, n)
                        try:
                            mb = os.path.getsize(fp) / 1048576
                        except Exception:
                            mb = 0
                        rows.append("%s\t%.1f MB" % (os.path.relpath(fp, d.path), mb))
                if rows:
                    with open(os.path.join(dst, "_未收集的大文件.txt"), "w", encoding="utf-8") as f:
                        f.write("以下文件体积较大，未打包进诊断包；如需要请单独发送\n\n")
                        f.write("\n".join(rows))
            item("崩溃记录", True, "最近 %d 次" % len(crash_dirs))
            summary.append("最近崩溃时间：%s" % time.strftime(
                "%Y-%m-%d %H:%M", time.localtime(crash_dirs[0].stat().st_mtime)))
        else:
            with open(os.path.join(out, "02_没有crashes文件夹.txt"), "w", encoding="utf-8") as f:
                f.write("没有 crashes 文件夹。\n\n"
                        "说明游戏不是「崩溃」（崩溃会生成 crashes 目录），\n"
                        "更可能是被系统 / 杀毒软件直接结束了进程，或者白屏卡死后手动关闭。\n"
                        "这条信息本身很关键，请一并反馈。")
            item("崩溃记录", False, "没有 crashes 目录（不是崩溃，可能被强杀）")
            summary.append("没有任何崩溃记录 —— 更可能是被强制结束 / 白屏卡住后手动关闭。")

        exc = os.path.join(user_dir, "exceptions")
        if os.path.isdir(exc) and any(os.scandir(exc)):
            try:
                shutil.copytree(exc, os.path.join(out, "03_exceptions"), dirs_exist_ok=True)
            except Exception:
                pass
    except Exception as ex:
        item("崩溃记录", False, str(ex))

    # ---------- 4 游戏日志 ----------
    step("收集游戏日志（超大文件只保留头尾）…")
    try:
        lo = os.path.join(out, "04_logs")
        os.makedirs(lo, exist_ok=True)
        n = 0
        for name in LOG_FILES:
            if _copy_trimmed(os.path.join(user_dir, "logs", name),
                             os.path.join(lo, name),
                             1024 if name == "debug.log" else BIG_KB):
                n += 1
        err_src = os.path.join(user_dir, "logs", "error.log")
        note = ("error.log %.1f KB" % (os.path.getsize(err_src) / 1024)
                if os.path.isfile(err_src) else "没拿到 error.log")
        item("游戏日志", n > 0, "%d 个 · %s" % (n, note))
        if os.path.isfile(err_src):
            summary.append("error.log 大小：%.1f KB" % (os.path.getsize(err_src) / 1024))
    except Exception as ex:
        item("游戏日志", False, str(ex))

    # ---------- 5 mod 列表 + 启动器日志 ----------
    step("收集模组启用列表与启动器日志…")
    try:
        for fn in ("dlc_load.json", "pdx_settings.txt"):
            src = os.path.join(user_dir, fn)
            if os.path.isfile(src):
                try:
                    shutil.copy2(src, os.path.join(out, "05_" + fn))
                except Exception:
                    pass
        cnt = 0
        dl = os.path.join(user_dir, "dlc_load.json")
        if os.path.isfile(dl):
            try:
                with open(dl, "r", encoding="utf-8-sig") as f:
                    cnt = len(json.load(f).get("enabled_mods") or [])
            except Exception:
                pass
        with open(os.path.join(out, "05_启用mod数量.txt"), "w", encoding="utf-8") as f:
            f.write("启用 mod 数量：%d" % cnt)
        item("模组列表", True, "启用 %d 个" % cnt)
        summary.append("启用 mod 数量：%d" % cnt)

        ldir = os.path.join(os.environ.get("LOCALAPPDATA", ""),
                            "Paradox Interactive", "launcher-v2", "logs")
        lout = os.path.join(out, "06_启动器日志")
        got = 0
        if os.path.isdir(ldir):
            os.makedirs(lout, exist_ok=True)
            cands = []
            for root, _dd, names in os.walk(ldir):
                for nm in names:
                    if nm.lower().endswith((".log", ".txt", ".json")):
                        fp = os.path.join(root, nm)
                        try:
                            if os.path.getsize(fp) > 0:
                                cands.append((os.path.getmtime(fp), fp))
                        except Exception:
                            pass
            cands.sort(reverse=True)
            for _mt, fp in cands[:15]:
                if _copy_trimmed(fp, os.path.join(lout, os.path.basename(fp)), 512):
                    got += 1
        if got:
            item("启动器日志", True, "%d 个" % got)
    except Exception as ex:
        item("模组列表", False, str(ex))

    # ---------- 6 存档列表 ----------
    step("记录存档列表…")
    try:
        sv = os.path.join(user_dir, "save games")
        rows = []
        if os.path.isdir(sv):
            for root, _dd, names in os.walk(sv):
                for n in names:
                    if n.lower().endswith(".ck3"):
                        fp = os.path.join(root, n)
                        try:
                            st = os.stat(fp)
                            rows.append((st.st_mtime, n, st.st_size))
                        except Exception:
                            pass
        rows.sort(reverse=True)
        with open(os.path.join(out, "08_存档列表.txt"), "w", encoding="utf-8") as f:
            f.write("存档名\t大小(MB)\t修改时间\n")
            for mt, n, size in rows:
                f.write("%s\t%.1f\t%s\n" % (n, size / 1048576,
                                            time.strftime("%Y-%m-%d %H:%M", time.localtime(mt))))
        item("存档列表", True, "%d 个（只记名字，不含本体）" % len(rows))
    except Exception as ex:
        item("存档列表", False, str(ex))

    # ---------- 7 dxdiag ----------
    if include_dxdiag:
        step("生成 dxdiag 报告（约 10~40 秒，请稍候）…")
        try:
            dx = os.path.join(out, "07_dxdiag.txt")
            subprocess.Popen(["dxdiag.exe", "/t", dx],
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            for _ in range(45):
                time.sleep(1)
                if os.path.isfile(dx) and os.path.getsize(dx) > 2000:
                    break
            ok = os.path.isfile(dx) and os.path.getsize(dx) > 2000
            item("dxdiag", ok, "已生成" if ok else "超时未生成，已跳过")
        except Exception as ex:
            item("dxdiag", False, str(ex))
    else:
        item("dxdiag", True, "已跳过（可选）")

    # ---------- 8 摘要 ----------
    step("生成摘要并打包…")
    with open(os.path.join(out, "99_摘要.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join([
            "==== CK3 诊断摘要 ====",
            "采集时间：" + time.strftime("%Y-%m-%d %H:%M:%S"),
            "计算机名：" + host,
            "游戏版本：" + str(cfg.get("gameVersion") or ""),
            "",
        ] + summary))

    zip_path = out + ".zip"
    try:
        with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
            for root, _dd, names in os.walk(out):
                for n in names:
                    fp = os.path.join(root, n)
                    z.write(fp, os.path.join(os.path.basename(out),
                                             os.path.relpath(fp, out)))
    except Exception as ex:
        return {"ok": False, "error": "打包失败：%s" % ex, "dir": out}

    return {
        "ok": True,
        "zip": zip_path,
        "dir": out,
        "name": os.path.basename(zip_path),
        "size": os.path.getsize(zip_path),
        "summary": summary,
        "items": items,
    }

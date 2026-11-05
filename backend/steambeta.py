# -*- coding: utf-8 -*-
"""
Steam 版本分支（beta）读写
==========================
CK3 等 Paradox 游戏的「版本切换」本质是 Steam 的 beta 分支：
  - 分支列表：<Steam>/appcache/appinfo.vdf （v41 二进制 VDF，字符串表在文件尾部）
  - 当前选择：<库>/steamapps/appmanifest_<appid>.acf 的 UserConfig.BetaKey

本模块只做两件事：
  1) 读出所有可选分支（betakey / 描述 / buildid）与当前分支；
  2) 在 Steam **完全退出**时，安全地把 BetaKey 改成目标分支（自动备份 acf）。
改完后由玩家启动 Steam，Steam 会自行下载对应版本。

设计原则：
  - 只读解析失败时退回内置常见分支表，绝不抛异常影响启动器。
  - 写 acf 前必须确认 Steam 未运行，否则改动会被 Steam 覆盖（直接拒绝并提示）。
  - 写前备份 <acf>.bak，改坏可回滚。
"""

from __future__ import annotations

import os
import re
import struct
import shutil
import subprocess

# ---------------------------------------------------------------- 路径探测

def _steam_root_from_registry() -> str:
    try:
        import winreg  # noqa: PLC0415
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as k:
            v, _ = winreg.QueryValueEx(k, "SteamPath")
            if v and os.path.isdir(v):
                return os.path.normpath(v)
    except Exception:
        pass
    # HKCU\Software\Valve\Steam 也可能是 SteamExe，再退到常见安装位置
    for p in (
        r"C:\Program Files (x86)\Steam",
        r"C:\Program Files\Steam",
        r"D:\steam", r"D:\Steam", r"E:\steam", r"E:\Steam",
    ):
        if os.path.isdir(p):
            return p
    return ""


def steam_root() -> str:
    return _steam_root_from_registry()


def appinfo_path() -> str:
    root = steam_root()
    if not root:
        return ""
    p = os.path.join(root, "appcache", "appinfo.vdf")
    return p if os.path.isfile(p) else ""


def _parse_libraryfolders(root: str) -> list[str]:
    """返回所有库的 steamapps 目录（含主库）。"""
    out = []
    main = os.path.join(root, "steamapps")
    if os.path.isdir(main):
        out.append(main)
    vdf = os.path.join(main, "libraryfolders.vdf")
    if os.path.isfile(vdf):
        try:
            t = open(vdf, "r", encoding="utf-8", errors="ignore").read()
            for m in re.finditer(r'"path"\s+"([^"]+)"', t):
                lib = m.group(1).replace("\\\\", "\\")
                sa = os.path.join(lib, "steamapps")
                if os.path.isdir(sa) and sa not in out:
                    out.append(sa)
        except Exception:
            pass
    return out


def manifest_path(appid: str | int) -> str:
    """在所有 Steam 库里找到 appmanifest_<appid>.acf。"""
    appid = str(appid)
    root = steam_root()
    if not root:
        return ""
    for sa in _parse_libraryfolders(root):
        p = os.path.join(sa, f"appmanifest_{appid}.acf")
        if os.path.isfile(p):
            return p
    return ""


# ---------------------------------------------------------------- appinfo 解析

def _string_table(data: bytes) -> list[str] | None:
    """解析 v41 尾部字符串表。头部 offset8 存 uint64 偏移，偏移处前 4 字节是条数。"""
    try:
        if len(data) < 16:
            return None
        magic = struct.unpack_from("<I", data, 0)[0]
        if magic != 0x07564429:            # 只认 v41；其它版本走内置兜底
            return None
        tbl = struct.unpack_from("<Q", data, 8)[0]
        if not (0 < tbl < len(data) - 4):
            return None
        count = struct.unpack_from("<I", data, tbl)[0]
        if not (0 < count < 5_000_000):
            return None
        off = tbl + 4
        strings = []
        for _ in range(count):
            end = data.find(b"\x00", off)
            if end < 0:
                return None
            strings.append(data[off:end].decode("latin-1"))
            off = end + 1
        return strings
    except Exception:
        return None


# 内置兜底：appinfo 解析失败时仍可切换（CK3 常见分支）
_FALLBACK_CK3 = [
    ("public", "默认公开版"),
    ("open_beta", "Open beta for update 1.19.0"),
    ("1.20.0", "Old Version 1.20.0.4 (Crozier)"),
    ("1.19.0", "Old Version 1.19.0.6 (Scribe)"),
    ("1.18.4", "Old Version 1.18.4 (Crane)"),
    ("1.17.1", "Old version 1.17.1 (Ascendant)"),
    ("1.16.2", "Old version 1.16.2 (Chamfron)"),
    ("1.15.0", "Old version 1.15.0 (Crown)"),
]


def parse_branches(appid: str | int) -> list[dict]:
    """解析 appinfo.vdf，返回该 appid 的所有分支 [{key,desc,buildid}]（按文件顺序去重）。"""
    appid = int(appid)
    path = appinfo_path()
    if not path:
        return []
    try:
        data = open(path, "rb").read()
    except Exception:
        return []
    strings = _string_table(data)
    if not strings:
        return []

    def S(i: int) -> str | None:
        return strings[i] if 0 <= i < len(strings) else None

    def _parse_block(block: bytes) -> list[dict]:
        out, seen = [], set()
        patb = b"\x02\xe9\x01\x00\x00"        # type=int32, key="buildid"
        k = 0
        while True:
            m = block.find(patb, k)
            if m < 0:
                break
            k = m + 1
            if m < 6 or block[m - 6:m - 4] != b"\x08\x00":
                continue
            keyidx = struct.unpack_from("<I", block, m - 4)[0]
            key = S(keyidx)
            if not key or key in ("buildid", "description", "timeupdated"):
                continue
            bid = struct.unpack_from("<I", block, m + 5)[0] if m + 9 <= len(block) else 0
            dpos = block.find(b"\x01\xec\x01\x00\x00", m + 9)
            desc = ""
            if dpos >= 0:
                ds = dpos + 5
                de = block.find(b"\x00", ds)
                desc = block[ds:de].decode("latin-1").strip()
            sig = (key, desc)
            if sig in seen:
                continue
            seen.add(sig)
            out.append({"key": key, "desc": desc, "buildid": bid})
        return out

    # appid 的字节在文件里可能多次出现，逐个候选块解析，取「分支最多」的那块
    # —— 真正的 app 数据块分支最全，误命中的块通常 0~1 个，借此彻底避免串块。
    pat = struct.pack("<I", appid)
    pos = 0
    best: list[dict] = []
    while True:
        j = data.find(pat, pos)
        if j < 0:
            break
        pos = j + 1
        if j + 8 > len(data):
            continue
        size = struct.unpack_from("<I", data, j + 4)[0]
        if not (0 < size < 8_000_000) or j + 8 + size > len(data):
            continue
        br = _parse_block(data[j + 8:j + 8 + size])
        if len(br) > len(best):
            best = br
            if len(best) >= 20:       # 已足够像真实块，提前结束
                break
    return best


def branches_or_fallback(appid: str | int) -> list[dict]:
    br = parse_branches(appid)
    if br:
        return br
    return [{"key": k, "desc": d, "buildid": 0} for k, d in _FALLBACK_CK3]


# ---------------------------------------------------------------- 当前分支 / 写入

def get_beta(appid: str | int) -> dict:
    """读当前 BetaKey（默认 public）。"""
    p = manifest_path(appid)
    key = "public"
    if p:
        try:
            t = open(p, "r", encoding="utf-8", errors="ignore").read()
            m = re.search(r'"BetaKey"\s+"([^"]*)"', t)
            if m:
                key = m.group(1)
        except Exception:
            pass
    return {"key": key, "manifest": p}


def steam_running() -> bool:
    """Steam 是否在运行（用于切换前的守卫）。"""
    try:
        out = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq steam.exe", "/NH"],
            capture_output=True, text=True, timeout=8,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        txt = (out.stdout or "").lower()
        return "steam.exe" in txt
    except Exception:
        return False


def set_beta(appid: str | int, new_key: str) -> dict:
    """把 appmanifest 的 BetaKey 改为 new_key。

    安全约束：
      - Steam 运行中 → 拒绝（改动会被覆盖）。
      - 写前备份 <acf>.bak。
      - 只替换 UserConfig 里的 BetaKey 值，其余字节不动。
    """
    new_key = (new_key or "").strip()
    if not new_key:
        return {"ok": False, "error": "分支键为空"}
    p = manifest_path(appid)
    if not p:
        return {"ok": False, "error": "找不到该游戏的 Steam 清单（可能不是 Steam 安装）"}
    if steam_running():
        return {"ok": False, "error": "请先完全退出 Steam 再切换（否则改动会被覆盖）", "need_close": True}
    try:
        raw = open(p, "rb").read()
        text = raw.decode("utf-8", errors="ignore")
        backup = p + ".bak"
        try:
            shutil.copy2(p, backup)
        except Exception:
            backup = ""
        m = re.search(r'("BetaKey"\s*")([^"]*)(")', text)
        if m:
            if m.group(2) == new_key:
                return {"ok": True, "key": new_key, "unchanged": True, "manifest": p, "backup": backup}
            text2 = text[:m.start(2)] + new_key + text[m.end(2):]
        else:
            # 没有 BetaKey → 在 UserConfig { 后插入
            mu = re.search(r'("UserConfig"\s*\{)', text)
            if not mu:
                return {"ok": False, "error": "清单里没有 UserConfig 段，无法写入"}
            ins = mu.end()
            text2 = text[:ins] + '\n\t\t"BetaKey"\t\t"%s"' % new_key + text[ins:]
        open(p, "wb").write(text2.encode("utf-8", errors="ignore"))
        return {"ok": True, "key": new_key, "manifest": p, "backup": backup}
    except Exception as ex:
        return {"ok": False, "error": str(ex)}

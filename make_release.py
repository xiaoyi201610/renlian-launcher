# -*- coding: utf-8 -*-
"""
组装绿色免安装发布包
====================
输出：
  release/人联启动器/            <- 绿色目录，可直接拷走
  release/人联启动器_v1.0.0.zip  <- 压缩包，可直接分享

用法：python make_release.py
"""

import os
import sys
import shutil
import zipfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
DIST_EXE = os.path.join(HERE, "dist", "人联启动器.exe")
RELEASE = os.path.join(HERE, "release")
PKG_NAME = "人联启动器"
VERSION = "1.7.0.5"
PKG = os.path.join(RELEASE, "_stage_" + PKG_NAME)
ZIP_PATH = os.path.join(RELEASE, f"{PKG_NAME}_v{VERSION}.zip")


def die(msg):
    print("✗ " + msg)
    sys.exit(1)


KEEP = {"backups", "launcher-config.json", "启动日志.log", "achievement_icons"}
EXE_NAME = "人联启动器.exe"


def _is_runtime_item(name: str) -> bool:
    """运行时生成的东西：保留在目录里，但不打进发布包。

    包括用户配置、日志、备份目录，以及各种本地缓存
    （DLC 清单 / DLC 详情 / 成就数据 / 全球完成率 / 图标 PNG 缓存）。
    """
    if name in KEEP:
        return True
    if name.startswith(("achievement_data_", "achievement_global_", "dlc_data_", "wiki_cache_")):
        return True
    if name in ("dlc_detail_cache.json",
                "dlc-names-cn.json",      # 玩家自己维护的 DLC 中文名覆盖表
                "mod-aliases.json",       # 玩家改过的模组显示名
                "mod-collections.json"):  # 玩家的收藏夹
        return True
    # 启动器内嵌 webview 的浏览器档案目录：运行期生成，且常因进程未退出被锁，
    # 既不该进发布包，也避免 z.write 读被锁的 lockfile 导致整个打包失败。
    if name in (".edge-profile", "__pycache__", "eu4_mission_icons"):
        return True
    # 误落到启动器目录的 P 社数据库 / dlc_load（userDir 未配置时的历史 bug 产物）：
    # 既不该进发布包，也不该被当成用户数据带出去。
    if name in ("launcher-v2.sqlite", "launcher.sqlite", "dlc_load.json") or \
       name.startswith("launcher-v2.sqlite.") or name.endswith(".misplaced.bak"):
        return True
    return False


def main():
    if not os.path.isfile(DIST_EXE):
        die("找不到 dist/人联启动器.exe，请先运行 PyInstaller 打包。")

    # 用独立的临时暂存目录组装，避免覆盖/读取被「正在运行的启动器」锁住的
    # release/人联启动器/ 目录（否则会静默沿用旧 exe，或 z.write 读被锁的 lockfile 失败）。
    # 每次都重建，保证发布包里一定是刚构建好的新 exe。
    if os.path.isdir(PKG):
        shutil.rmtree(PKG, ignore_errors=True)
    os.makedirs(PKG, exist_ok=True)

    # 1. 主程序：直接来自刚构建的 dist（暂存目录每次重建，无需跳过判断）
    shutil.copy2(DIST_EXE, os.path.join(PKG, EXE_NAME))

    # 2. 启动脚本 / 说明 / 协议
    for fn in ("说明.txt", "LICENSE.txt"):
        src = os.path.join(HERE, "packaging", fn)
        if os.path.isfile(src):
            shutil.copy2(src, os.path.join(PKG, fn))

    # 3. 教程 + 配图
    tut_src = os.path.join(HERE, "tutorial")
    shutil.copy2(os.path.join(tut_src, "使用教程.html"), os.path.join(PKG, "使用教程.html"))
    img_src = os.path.join(tut_src, "img")
    img_dst = os.path.join(PKG, "img")
    os.makedirs(img_dst, exist_ok=True)
    for f in os.listdir(img_src):
        if f.lower().endswith((".png", ".jpg")):
            shutil.copy2(os.path.join(img_src, f), os.path.join(img_dst, f))

    # 4. 压缩（排除运行时产生的备份、配置、日志、各类缓存）
    files = []
    for root, dirs, names in os.walk(PKG):
        dirs[:] = [d for d in dirs if not _is_runtime_item(d)]
        for n in names:
            if _is_runtime_item(n):
                continue
            full = os.path.join(root, n)
            rel = os.path.relpath(full, PKG)
            files.append((full, os.path.join(PKG_NAME, rel)))

    zip_path = write_zip(files)

    size_mb = os.path.getsize(zip_path) / 1024 / 1024
    print(f"✓ 压缩包  ：{zip_path}  ({size_mb:.1f} MB)")
    print(f"  共 {len(files)} 个文件")
    for full, arc in files:
        print(f"    {arc}  ({os.path.getsize(full)/1024:.0f} KB)")

    # 暂存目录已打进压缩包。先别删——还要用它把发布内容同步进本地启动器目录。
    # 5. 同步更新「本地启动器目录」release/人联启动器/ ——
    #    用户实际双击运行的就是它，必须把本次发布的**全部内容**（exe/教程/说明/img）
    #    都刷进去，否则会出现「zip 是新的、本地是旧的」（exe 和教程各踩过一次坑）。
    #    只跳过运行时数据（backups / logs / config / .edge-profile / 各类缓存）。
    sync_live_folder(files)

    # 同步完成，暂存目录功成身退
    shutil.rmtree(PKG, ignore_errors=True)


def sync_live_folder(files):
    """把本次发布的全部文件同步进用户实际运行的 release/人联启动器/。

    files: [(暂存内完整路径, zip内相对路径), ...]，zip 内相对路径形如 "人联启动器/xxx"。
    只覆盖发布内容，绝不碰用户的运行时数据（backups/logs/config/.edge-profile）。
    主程序被占用时退化为 人联启动器_new.exe 并明确指引，绝不静默沿用旧 exe。
    """
    LIVE = os.path.join(RELEASE, PKG_NAME)
    os.makedirs(LIVE, exist_ok=True)
    new_exe = os.path.join(LIVE, "人联启动器_new.exe")
    locked = False
    n = 0
    for full, arc in files:
        rel = os.path.relpath(arc, PKG_NAME)   # 去掉顶层目录名 → 相对 LIVE 的路径
        dst = os.path.join(LIVE, rel)
        parent = os.path.dirname(dst)
        if parent:
            os.makedirs(parent, exist_ok=True)
        try:
            shutil.copy2(full, dst)
            n += 1
        except PermissionError:
            if os.path.basename(dst) == EXE_NAME:
                locked = True
                try:
                    shutil.copy2(full, new_exe)
                except Exception:
                    pass
            else:
                print(f"! 跳过被占用文件：{rel}")
    if locked:
        print("\n⚠ 本地启动器 " + EXE_NAME + " 正在运行，无法直接覆盖。")
        print("  我已生成新版本：" + new_exe)
        print("  请先【关闭正在运行的旧启动器】，然后二选一：")
        print("    方案A：直接双击 人联启动器_new.exe 即可用上新版本；")
        print("    方案B：把 人联启动器_new.exe 改名为 人联启动器.exe 覆盖旧的，以后照常用。")
        print("  （下次构建时若旧启动器已关，会自动覆盖回 人联启动器.exe 并清掉 _new 兜底文件）")
    else:
        # 全部覆盖成功，清掉之前可能留下的兜底文件
        if os.path.isfile(new_exe):
            try:
                os.remove(new_exe)
            except Exception:
                pass
    print(f"✓ 本地启动器目录已同步 {n} 个文件：{LIVE}")


def write_zip(files):
    """写压缩包。

    目标文件可能被资源管理器预览或杀软扫描独占，导致 PermissionError，
    这时退回带 _new 后缀的文件名，保证产物一定打得出来。
    """
    candidates = [ZIP_PATH, ZIP_PATH[:-4] + "_new.zip"]
    last = None
    for p in candidates:
        try:
            with zipfile.ZipFile(p, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
                for full, arc in files:
                    z.write(full, arc)
            if p != ZIP_PATH:
                print(f"! 原文件名被占用，已改存为：{os.path.basename(p)}")
            return p
        except PermissionError as ex:
            last = ex
            print(f"! {os.path.basename(p)} 被占用，换个名字重试…")
    die("压缩包写入失败（文件被其他程序占用）：%s" % last)


if __name__ == "__main__":
    main()

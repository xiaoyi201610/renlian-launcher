# -*- coding: utf-8 -*-
"""
CK3 Launcher - 核心逻辑层
=========================
负责：
  1. Steam / 游戏 / 用户数据 / 创意工坊 路径自动探测
  2. .mod 与 .dlc 描述文件解析（PDX 脚本格式）
  3. launcher-v2.sqlite 读写（播放集、mod 注册表、DLC 开关）
  4. dlc_load.json 双向同步（游戏真正读取的加载顺序）
  5. 写盘前自动备份
  6. 存档扫描 / 回收站删除 / 目录打开

设计原则：只写必要字段，写前必备份，双写保持与官方启动器一致。
"""

from __future__ import annotations

import os
import re
import sys
import json
import time
import uuid
import ctypes
import shutil
import sqlite3
import zipfile
import threading
import contextlib
import subprocess
import glob
import urllib.request
import urllib.error

# ---------------------------------------------------------------- 常量

STEAM_APPID = "1158310"          # Crusader Kings III（保留旧引用兼容）
GAME_FOLDER = "Crusader Kings III"
PDX_FOLDER = os.path.join("Paradox Interactive", GAME_FOLDER)


# ---------------------------------------------------------------- 多游戏定义
# P 社 Clausewitz 引擎全家桶：同一套 launcher-v2.sqlite / dlc_load.json / mod 目录机制。
# key 是内部代号（也用作配置分存键、下拉值）；字段用于自动探测与启动。
GAME_DEFS: dict[str, dict] = {
    "ck3": {
        "name": "十字军之王 III",
        "en": "Crusader Kings III",
        "folder": "Crusader Kings III",       # Steam common 目录名
        "pdx": "Crusader Kings III",          # Documents/Paradox Interactive 下目录名
        "appid": "1158310",                   # Steam AppID（工坊 content 目录）
        "exe": ("binaries", "ck3.exe"),       # 可执行文件相对路径
        "saveExt": ".ck3",
        "defaultVersion": "1.20",
    },
    "stellaris": {
        "name": "群星",
        "en": "Stellaris",
        "folder": "Stellaris",
        "pdx": "Stellaris",
        "appid": "281990",
        "exe": ("stellaris.exe",),
        "saveExt": ".sav",
        "defaultVersion": "3.14",
    },
    "hoi4": {
        "name": "钢铁雄心 IV",
        "en": "Hearts of Iron IV",
        "folder": "Hearts of Iron IV",
        "pdx": "Hearts of Iron IV",
        "appid": "394360",
        "exe": ("hoi4.exe",),
        "saveExt": ".hoi4",
        "defaultVersion": "1.15",
    },
    "vic3": {
        "name": "维多利亚 3",
        "en": "Victoria 3",
        "folder": "Victoria 3",
        "pdx": "Victoria 3",
        "appid": "529340",
        "exe": ("binaries", "victoria3.exe"),
        "saveExt": ".v3",
        "defaultVersion": "1.8",
    },
    "eu4": {
        "name": "欧陆风云 IV",
        "en": "Europa Universalis IV",
        "folder": "Europa Universalis IV",
        "pdx": "Europa Universalis IV",
        "appid": "236850",
        "exe": ("eu4.exe",),
        "saveExt": ".eu4",
        "defaultVersion": "1.37",
    },
    "eu5": {
        "name": "欧陆风云 V",
        "en": "Europa Universalis V",
        "folder": "Europa Universalis V",
        "pdx": "Europa Universalis V",
        "appid": "3450310",
        "exe": ("binaries", "eu5.exe"),
        "saveExt": ".eu5",
        "defaultVersion": "1.0",
    },
    "ck2": {
        "name": "十字军之王 II",
        "en": "Crusader Kings II",
        "folder": "Crusader Kings II",
        "pdx": "Crusader Kings II",
        "appid": "203770",
        "exe": ("CK2game.exe",),
        "saveExt": ".ck2",
        "defaultVersion": "3.3",
    },
    "imperator": {
        "name": "帝国：罗马",
        "en": "Imperator: Rome",
        "folder": "ImperatorRome",          # Steam common 目录名（无空格）
        "pdx": "Imperator",                 # Documents/Paradox Interactive 下目录名
        "appid": "859580",
        "exe": ("binaries", "imperator.exe"),
        "saveExt": ".rome",
        "defaultVersion": "2.0",
    },
    "skylines": {
        "name": "城市：天际线",
        "en": "Cities: Skylines",
        "folder": "Cities_Skylines",
        "pdx": "Cities_Skylines",
        "userBase": "Colossal Order",      # 天际线用户数据在 Colossal Order 下，非 Paradox Interactive
        "appid": "255710",
        "exe": ("Cities.exe",),
        "saveExt": ".crp",
        "defaultVersion": "1.18",
    },
    "skylines2": {
        "name": "城市：天际线 II",
        "en": "Cities: Skylines II",
        "folder": "Cities Skylines II",
        "pdx": "Cities Skylines II",
        "userBase": "Colossal Order",       # 天际线2用户数据在 Colossal Order 下
        "appid": "949230",
        "exe": ("Cities2.exe",),
        "saveExt": ".cok",
        "defaultVersion": "1.0",
    },
}

# 下拉展示顺序
GAME_ORDER = ["ck3", "stellaris", "hoi4", "vic3", "eu4", "eu5", "ck2",
              "imperator", "skylines", "skylines2"]

# 封面图：本地 static/img/games/ 下，前端经 /static/ 前缀访问
def game_cover(game: str, size: str = "sm") -> str:
    """返回某游戏封面图的本地静态 URL（前端 /static/ 前缀引用）。

    size：sm = 231×87 小横条（下拉用）；lg = 460×215 大横版（顶部用）。
    """
    key = game if game in GAME_DEFS else "ck3"
    fn = f"{key}.jpg" if size == "lg" else f"{key}-sm.jpg"
    return f"/static/img/games/{fn}"


def _game_content_dir(game_root: str, game: str = "ck3") -> str:
    """定位游戏的「内容目录」（放 dlc / dlc_metadata 的根）。

    CK3/HOI4 等是 <root>/game/，群星新版是 <root>/（dlc 直接放根下），
    也有 <root>/Data/ 的情况。返回实际存在 dlc 子目录的那个根。
    """
    d = GAME_DEFS.get(game, GAME_DEFS["ck3"])
    candidates = [
        os.path.join(game_root, "game"),
        os.path.join(game_root, "Data"),
        game_root,
    ]
    for c in candidates:
        if os.path.isdir(os.path.join(c, "dlc")):
            return c
    # 都没有则回退到 game/（老结构）
    return os.path.join(game_root, "game")


def _game_bin_dir(game_root: str, game: str = "ck3") -> str:
    """定位游戏的二进制目录（放 steam_api64.dll / 补丁）。"""
    d = GAME_DEFS.get(game, GAME_DEFS["ck3"])
    exe = d["exe"]
    # exe 的相对父目录即二进制目录
    return os.path.join(game_root, *exe[:-1]) if len(exe) > 1 else game_root


def _app_dir() -> str:
    """配置与备份目录：打包后是 exe 所在目录，源码运行时是项目根目录。"""
    if getattr(sys, "frozen", False):
        return os.path.dirname(os.path.abspath(sys.executable))
    return os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def _res_dir() -> str:
    """只读资源目录：打包后是 PyInstaller 的临时解压目录。"""
    if getattr(sys, "frozen", False):
        return getattr(sys, "_MEIPASS", _app_dir())
    return _app_dir()


APP_DIR = _app_dir()
RES_DIR = _res_dir()
CONFIG_PATH = os.path.join(APP_DIR, "launcher-config.json")
BACKUP_DIR = os.path.join(APP_DIR, "backups")
DLC_CACHE_PATH = os.path.join(APP_DIR, "dlc_meta_cache.json")  # 运行时刷新的 DLC 清单缓存
MOD_ALIAS_PATH = os.path.join(APP_DIR, "mod-aliases.json")     # mod 自定义显示名（按游戏分存）
MAX_BACKUPS = 12          # 每种文件最多保留几份
BACKUP_MIN_INTERVAL = 20  # 同一目标在这么多秒内只备份一次，避免连续保存刷屏

_backup_stamp: dict = {}

DEFAULT_CONFIG = {
    "currentGame": "ck3",        # 当前选中的游戏代号
    "games": {},                  # { 代号: { gameRoot/userDir/workshopDir/gameVersion/... } }
    # 以下为向后兼容的旧字段（仅 ck3 时映射到 games["ck3"]）
    "gameRoot": "",
    "userDir": "",
    "workshopDir": "",
    "gameVersion": "1.20",
    "autoBackup": True,
    "rememberLastPlayset": True,
    "lastPlayset": "",
    "minimizeOnLaunch": False,   # 启动游戏后最小化启动器
    "closeOnLaunch": False,      # 启动游戏后关闭启动器
    "useDataPathArg": False,     # 启动游戏时传 -dataPath（自定义数据目录时用）
    "uiLang": "zh",              # 界面语言：zh / en / fr / de / sv
    "updateUrl": "https://gitee.com/xiaoyi2233/renlian-launcher/raw/master/version.json",  # 版本自检地址（GitHub raw 被墙，默认用 Gitee raw；留空则不检查）
}


def _game_key(cfg: dict) -> str:
    """取当前游戏代号（未设置则默认 ck3）。"""
    k = cfg.get("currentGame") or "ck3"
    return k if k in GAME_DEFS else "ck3"


def resolve_startup_game() -> str:
    """首次启动时自动定位一个「已安装」的游戏，让玩家开箱即用。

    规则：
      1. 读主配置的 currentGame，若该游戏已安装（有 gameRoot 或 userDir），直接用它。
      2. 否则扫描所有游戏，返回第一个「已安装」的（按 GAME_ORDER 顺序）。
      3. 一个都没装，返回默认 ck3（界面会提示手动添加）。

    这保证玩家双击 exe 后，哪怕没装 CK3 只装了 HOI4，也会自动停在 HOI4，
    无需手动切换。只做「定位」，不写盘（写盘交给用户首次切换/保存时）。
    """
    cfg = load_config()
    cur = _game_key(cfg)

    def _installed(key: str) -> bool:
        g = _game_cfg(cfg, key)
        root = g.get("gameRoot") or ""
        udir = g.get("userDir") or ""
        if os.path.isdir(root) and os.path.isfile(os.path.join(root, *GAME_DEFS[key]["exe"])):
            return True
        if os.path.isdir(udir):
            return True
        # 配置里没存，尝试实时探测
        if detect_game_root(key) or detect_user_dir(key):
            return True
        return False

    if _installed(cur):
        return cur

    # 当前游戏未安装 → 找第一个已安装的
    for key in GAME_ORDER:
        if _installed(key):
            return key
    return cur


def _game_cfg(cfg: dict, key: str) -> dict:
    """取某游戏的独立配置（dict），自动带上默认 gameVersion。"""
    g = (cfg.get("games") or {}).get(key) or {}
    g = dict(g)
    # 未显式保存过 gameVersion 时，用该游戏的默认版本（而非 DEFAULT_CONFIG 的 1.20）
    if not g.get("gameVersion"):
        g["gameVersion"] = GAME_DEFS[key]["defaultVersion"]
    return g


def _resolve_profiles_into_flat(cfg: dict) -> None:
    """把每个游戏「当前激活版本配置(profile)」的值回填到扁平字段 gameRoot/userDir/
    workshopDir/gameVersion。

    这样全代码（模组/播放集/DLC/启动/-dataPath 隔离）只读扁平字段也能拿到激活版本
    的数据，无需逐处改。无 profiles 的游戏保持原扁平字段不动（向后兼容旧单配置）。
    """
    for key, g in (cfg.get("games") or {}).items():
        if not isinstance(g, dict):
            continue
        profiles = g.get("profiles") or []
        if not profiles:
            continue
        active = g.get("activeProfile") or profiles[0].get("id")
        p = next((x for x in profiles if x.get("id") == active), profiles[0])
        for fld in ("gameRoot", "userDir", "workshopDir", "gameVersion"):
            g[fld] = p.get(fld) or ""


def load_config(game: str | None = None) -> dict:
    """读取主配置。若指定 game，则返回该游戏的合并配置（games[key] 覆盖全局默认）。

    为兼容旧版：旧字段 gameRoot/userDir/workshopDir 自动归入 games["ck3"]。
    """
    cfg = dict(DEFAULT_CONFIG)
    raw = {}
    try:
        with open(CONFIG_PATH, "r", encoding="utf-8") as f:
            raw = json.load(f) or {}
    except Exception:
        pass
    cfg.update(raw)
    cfg["games"] = cfg.get("games") or {}

    # 向后兼容：旧单游戏字段迁入 games["ck3"]
    legacy = cfg["games"].get("ck3") or {}
    migrated = False
    for fld in ("gameRoot", "userDir", "workshopDir", "gameVersion"):
        if raw.get(fld) and not legacy.get(fld):
            legacy[fld] = raw[fld]
            migrated = True
    if legacy:
        cfg["games"]["ck3"] = legacy
    if migrated:
        # 迁移后清理旧顶层字段，避免歧义
        for fld in ("gameRoot", "userDir", "workshopDir", "gameVersion"):
            cfg.pop(fld, None)

    # 把每个游戏「激活版本配置」的值回填到扁平字段，使后续代码自动跟随切换
    _resolve_profiles_into_flat(cfg)

    # 选择返回：指定 game 则返回合并后的「该游戏视图」，否则返回主配置
    if game:
        key = game if game in GAME_DEFS else "ck3"
        merged = dict(DEFAULT_CONFIG)
        # 顶层通用开关
        for fld in ("autoBackup", "rememberLastPlayset", "minimizeOnLaunch",
                    "closeOnLaunch", "useDataPathArg"):
            merged[fld] = cfg.get(fld, DEFAULT_CONFIG[fld])
        merged["currentGame"] = key
        g = _game_cfg(cfg, key)
        merged.update(g)
        # 把版本配置(profiles) 也带进合并视图，前端顶栏下拉与设置页都依赖它
        # （无 profiles 的旧配置会在此合成一个「默认」版本项，保证向后兼容）
        profiles_view, active = list_profiles(cfg, key)
        merged["profiles"] = profiles_view
        merged["activeProfile"] = active
        # 自动探测补齐该游戏路径
        ud = (merged.get("userDir") or "").strip()
        if not ud:
            # 完全没配置 userDir → 自动探测（向后兼容旧单配置）
            detected = detect_user_dir(key)
            merged["userDir"] = detected or ""
        elif not os.path.isdir(ud):
            # 用户显式指定了目录（含版本配置 profile）但还不存在 → 直接创建，
            # 绝不回退到自动探测。否则新版本配置的独立数据目录会被自动探测劫持回
            # 默认数据目录，破坏「版本之间文件互相独立」。
            try:
                os.makedirs(ud, exist_ok=True)
            except Exception:
                pass
        gr = (merged.get("gameRoot") or "").strip()
        if not gr:
            # 没指定本体目录 → 自动探测（版本配置 profile 留空即表示复用默认安装）
            merged["gameRoot"] = detect_game_root(key) or ""
        # 指定了本体目录（即便是版本配置里的独立安装）则保留，不回退探测
        ws = (merged.get("workshopDir") or "").strip()
        if not ws:
            merged["workshopDir"] = detect_workshop_dir(merged.get("gameRoot") or "", key) or ""
        return merged

    # 未指定 game：返回主配置（含 games 表 + 当前游戏已就绪的视图字段）
    return cfg


def save_config(cfg: dict) -> None:
    """保存主配置。cfg 里的路径字段写回 games[currentGame]，顶层只留通用项。

    顶层不再写 gameRoot/userDir/workshopDir/gameVersion 这些旧字段（避免歧义），
    统一按 games[代号] 分存。
    """
    # games 表：优先用 cfg 自带的；否则从磁盘读现有配置的 games 表（保留其他游戏数据）
    if cfg.get("games"):
        games = dict(cfg["games"])
    else:
        games = {}
        try:
            with open(CONFIG_PATH, "r", encoding="utf-8") as f:
                old = json.load(f) or {}
                games = dict(old.get("games") or {})
        except Exception:
            pass

    data: dict = {}
    # 通用开关
    for fld in ("autoBackup", "rememberLastPlayset", "minimizeOnLaunch",
                "closeOnLaunch", "useDataPathArg", "uiLang", "updateUrl"):
        data[fld] = cfg.get(fld, DEFAULT_CONFIG[fld])
    data["currentGame"] = _game_key(cfg)
    data["games"] = games

    # 把当前游戏视图里的路径字段写进 games[currentGame]（同时同步到「激活」的版本配置）
    key = data["currentGame"]
    g = dict(games.get(key) or {})
    for fld in ("gameRoot", "userDir", "workshopDir", "lastPlayset"):
        if cfg.get(fld):
            g[fld] = cfg[fld]
    # gameVersion：只有和该游戏默认版本不同（用户显式改过）才写，避免脏数据
    gv = cfg.get("gameVersion")
    default_gv = GAME_DEFS[key]["defaultVersion"]
    if gv and gv != default_gv:
        g["gameVersion"] = gv
    else:
        g.pop("gameVersion", None)
    # 版本配置(profiles)：把路径字段同步进当前激活的那一条，保证切换回来不丢改动
    profiles = g.get("profiles") or []
    if profiles:
        active = g.get("activeProfile") or profiles[0]["id"]
        for p in profiles:
            if p["id"] == active:
                p["gameRoot"] = cfg.get("gameRoot") or ""
                p["userDir"] = cfg.get("userDir") or ""
                p["workshopDir"] = cfg.get("workshopDir") or ""
                p["gameVersion"] = cfg.get("gameVersion") or ""
                break
    games[key] = g
    data["games"] = games

    with open(CONFIG_PATH, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)


def set_current_game(key: str) -> dict:
    """切换当前游戏，返回切换后该游戏的合并配置。"""
    cfg = load_config()
    key = key if key in GAME_DEFS else "ck3"
    cfg["currentGame"] = key
    # 保存时用「该游戏的合并视图」，避免用主配置里恒为 1.20 的 gameVersion 污染
    merged = load_config(key)
    merged["currentGame"] = key
    # 顶层通用开关从主配置继承
    for fld in ("autoBackup", "rememberLastPlayset", "minimizeOnLaunch",
                "closeOnLaunch", "useDataPathArg"):
        merged[fld] = cfg.get(fld, DEFAULT_CONFIG[fld])
    save_config(merged)
    return load_config(key)


# ---------------------------------------------------------------- 版本配置（多版本切换 / 文件独立）
# 每个游戏可有多个「版本配置(profile)」：各自独立的 gameVersion / gameRoot / userDir /
# workshopDir。激活的那条的 userDir 不同 → 模组/播放集/DLC/存档全部隔离；配合设置里的
# 「启动游戏时传 -dataPath」，游戏运行时也真正去读该 userDir，实现版本间文件互相独立。
# 旧的单配置（只有扁平字段）会被兼容：首次新增 profile 时自动把现有配置作为「默认」版本。

def _read_raw_config() -> dict:
    try:
        with open(CONFIG_PATH, "r", encoding="utf-8") as f:
            return json.load(f) or {}
    except Exception:
        return {}


def _write_raw_config(data: dict) -> None:
    os.makedirs(os.path.dirname(CONFIG_PATH) or ".", exist_ok=True)
    with open(CONFIG_PATH, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)


def _new_profile_id(profiles: list) -> str:
    while True:
        pid = "p" + uuid.uuid4().hex[:8]
        if not any(p.get("id") == pid for p in profiles):
            return pid


def list_profiles(cfg: dict, code: str) -> tuple[list, str]:
    """返回 ([profile...带 active 标记], active_id)。无 profiles 时合成一个默认项（兼容旧配置）。"""
    g = (cfg.get("games") or {}).get(code) or {}
    profiles = g.get("profiles") or []
    if profiles:
        active = g.get("activeProfile") or profiles[0].get("id")
        out = [dict(p, active=(p.get("id") == active)) for p in profiles]
        return out, active
    defv = g.get("gameVersion") or GAME_DEFS.get(code, {}).get("defaultVersion", "")
    return [{
        "id": "default", "name": defv or "默认", "active": True,
        "gameVersion": g.get("gameVersion") or "",
        "gameRoot": g.get("gameRoot") or "",
        "userDir": g.get("userDir") or "",
        "workshopDir": g.get("workshopDir") or "",
    }], "default"


def switch_profile(code: str, pid: str) -> dict:
    """切换当前游戏的激活版本配置。"""
    raw = _read_raw_config()
    games = raw.get("games") or {}
    g = games.get(code) or {}
    profiles = g.get("profiles") or []
    if not any(p.get("id") == pid for p in profiles):
        return {"ok": False, "error": "版本配置不存在"}
    g["activeProfile"] = pid
    games[code] = g
    raw["games"] = games
    _write_raw_config(raw)
    return {"ok": True, "config": load_config(code)}


def add_profile(code: str, data: dict) -> dict:
    """新增一个版本配置。首次新增时把现有扁平配置作为「默认」版本保留，避免数据丢失。
    新增后自动切到它。"""
    raw = _read_raw_config()
    games = raw.get("games") or {}
    g = dict(games.get(code) or {})
    profiles = g.get("profiles") or []
    # 旧格式首次建 profile：先把现有扁平配置作为「默认」版本存进去
    if not profiles:
        legacy_has = any([g.get("gameRoot"), g.get("userDir"),
                          g.get("workshopDir"), g.get("gameVersion")])
        if legacy_has:
            profiles = [{
                "id": "default",
                "name": g.get("gameVersion") or GAME_DEFS.get(code, {}).get("defaultVersion", "默认"),
                "gameVersion": g.get("gameVersion") or "",
                "gameRoot": g.get("gameRoot") or "",
                "userDir": g.get("userDir") or "",
                "workshopDir": g.get("workshopDir") or "",
            }]
    name = (data.get("name") or "").strip() or "新版本"
    pid = _new_profile_id(profiles)
    udir = (data.get("userDir") or "").strip()
    if not udir:
        # 自动给新版本分配一个独立用户数据目录：避免空目录被自动探测回默认目录，
        # 从而破坏「版本之间文件互相独立」。放在默认数据目录的兄弟位置。
        base = (g.get("userDir") or detect_user_dir(code) or "").strip()
        if base:
            parent = os.path.dirname(base.rstrip("\\/"))
            leaf = os.path.basename(base.rstrip("\\/"))
            safe = "".join(ch for ch in name if ch not in '\\/:*?"<>|').strip() or pid
            cand = os.path.join(parent, f"{leaf} - {safe}")
            try:
                os.makedirs(cand, exist_ok=True)
            except Exception:
                pass
            udir = cand
    prof = {
        "id": pid, "name": name,
        "gameVersion": (data.get("gameVersion") or "").strip(),
        "gameRoot": (data.get("gameRoot") or "").strip(),
        "userDir": udir,
        "workshopDir": (data.get("workshopDir") or "").strip(),
    }
    profiles.append(prof)
    g["profiles"] = profiles
    g["activeProfile"] = pid
    games[code] = g
    raw["games"] = games
    _write_raw_config(raw)
    return {"ok": True, "config": load_config(code), "pid": pid}


def update_profile(code: str, pid: str, data: dict) -> dict:
    """修改某版本配置（name/gameVersion/gameRoot/userDir/workshopDir）。"""
    raw = _read_raw_config()
    games = raw.get("games") or {}
    g = games.get(code) or {}
    profiles = g.get("profiles") or []
    target = next((p for p in profiles if p.get("id") == pid), None)
    if not target:
        return {"ok": False, "error": "版本配置不存在"}
    for fld in ("name", "gameVersion", "gameRoot", "userDir", "workshopDir"):
        if fld in data:
            target[fld] = (data[fld] or "").strip()
    if not (target.get("name") or "").strip():
        target["name"] = "未命名"
    games[code] = g
    raw["games"] = games
    _write_raw_config(raw)
    return {"ok": True, "config": load_config(code)}


def delete_profile(code: str, pid: str) -> dict:
    """删除一个版本配置（至少保留一个）。若删的是激活项，自动回退到第一个。"""
    raw = _read_raw_config()
    games = raw.get("games") or {}
    g = games.get(code) or {}
    profiles = g.get("profiles") or []
    if not any(p.get("id") == pid for p in profiles):
        return {"ok": False, "error": "版本配置不存在"}
    if len(profiles) <= 1:
        return {"ok": False, "error": "至少保留一个版本配置"}
    profiles = [p for p in profiles if p.get("id") != pid]
    g["profiles"] = profiles
    if g.get("activeProfile") == pid:
        g["activeProfile"] = profiles[0].get("id")
    games[code] = g
    raw["games"] = games
    _write_raw_config(raw)
    return {"ok": True, "config": load_config(code)}


# ---------------------------------------------------------------- 版本自检

def version_greater(a: str, b: str) -> bool:
    """a 是否比 b 新（按点分数字逐段比较）。空版本视为最旧。"""
    def _nums(v: str):
        out = []
        for p in (v or "").split("."):
            try:
                out.append(int(re.sub(r"[^0-9].*$", "", p) or 0))
            except Exception:
                out.append(0)
        return out
    na, nb = _nums(a), _nums(b)
    for x, y in zip(na, nb):
        if x != y:
            return x > y
    return len(na) > len(nb)


def fetch_update_info(url: str, timeout: int = 8) -> dict:
    """拉取远程 version.json，返回规范化的更新信息（同源或跨源都不受 CORS 影响，

    因为由本地后端代理请求）。仅接受 http/https，带超时与异常兜底。
    """
    if not url or not url.startswith(("http://", "https://")):
        return {"ok": False, "error": "更新检查地址无效（需以 http:// 或 https:// 开头）"}
    try:
        req = urllib.request.Request(url, headers={"User-Agent": "RenlianLauncher"})
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read().decode("utf-8", errors="ignore")
        data = json.loads(raw)
        latest = str(data.get("version") or "").strip()
        if not latest:
            return {"ok": False, "error": "远程文件缺少 version 字段"}
        return {
            "ok": True,
            "latest": latest,
            "url": data.get("url") or "",
            "notes": data.get("notes") or "",
            "date": data.get("date") or "",
            "size": data.get("size") or "",
        }
    except urllib.error.URLError as e:
        reason = getattr(e, "reason", e)
        return {"ok": False, "error": f"网络错误：{reason}"}
    except Exception as e:  # noqa: BLE001
        return {"ok": False, "error": f"读取失败：{e}"}

def _paradox_user_dir() -> str:
    """定位 Paradox Interactive 用户数据根目录（Documents 下）。"""
    home = os.path.expanduser("~")
    for base in (os.path.join(home, "Documents"),
                 os.path.join(home, "OneDrive", "Documents"),
                 os.path.join(home, "OneDrive", "文档")):
        p = os.path.join(base, "Paradox Interactive")
        if os.path.isdir(p):
            return p
    return ""


def detect_installed_games() -> list[dict]:
    """扫描本机已安装/可识别的 P 社游戏。

    识别来源（优先级）：
      1. Documents/Paradox Interactive/ 下的游戏目录（用户数据，最全）
      2. Steam 库 common 目录（游戏本体）
      3. 手动添加的配置（load_config 里的 games 表）
    返回 [{key, name, en, gameRoot, userDir, installed}]。
    installed = 有游戏本体 或 有用户数据（任一即可操作）。
    """
    out = []
    roots = _steam_roots()
    pdx_root = _paradox_user_dir()
    # 手动添加过路径的游戏（配置里存了 gameRoot/userDir）
    cfg_all = load_config()
    manual = cfg_all.get("games") or {}

    for key in GAME_ORDER:
        d = GAME_DEFS[key]
        groot = None
        udir = None

        m = manual.get(key) or {}
        # 1) 手动配置优先：用户显式填的目录，只要目录存在就认（不强制 exe，避免填了不生效）
        mg = m.get("gameRoot")
        if mg and os.path.isdir(mg):
            groot = mg
        mu = m.get("userDir")
        if mu and os.path.isdir(mu):
            udir = mu

        # 2) 用户数据目录自动探测（Paradox Interactive 下）
        if not udir:
            for cand_base in [pdx_root]:
                if not cand_base:
                    continue
                cand = os.path.join(cand_base, d["pdx"])
                if os.path.isdir(cand):
                    udir = cand
        # 3) 标准探测兜底
        if not udir:
            udir = detect_user_dir(key)

        # 4) 游戏本体自动探测（Steam 库 common）
        if not groot:
            for r in roots:
                cand = os.path.join(r, "steamapps", "common", d["folder"])
                if os.path.isfile(os.path.join(cand, *d["exe"])):
                    groot = cand
                    break
        # 5) 游戏本体探测兜底（常见盘符）
        if not groot:
            g = detect_game_root(key)
            if g:
                groot = g

        installed = bool(groot or udir)
        out.append({
            "key": key,
            "name": d["name"],
            "en": d["en"],
            "gameRoot": groot or "",
            "userDir": udir or "",
            "installed": installed,
        })
    return out


# ---------------------------------------------------------------- 路径探测

def _steam_roots() -> list[str]:
    """从注册表 + libraryfolders.vdf 收集所有 Steam 库根目录。"""
    roots: list[str] = []
    steam = None
    try:
        import winreg
        for hive, path in (
            (winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam"),
            (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Valve\Steam"),
            (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Valve\Steam"),
        ):
            try:
                with winreg.OpenKey(hive, path) as k:
                    for name in ("SteamPath", "InstallPath"):
                        try:
                            v, _ = winreg.QueryValueEx(k, name)
                            if v and os.path.isdir(v):
                                steam = v
                                break
                        except OSError:
                            continue
            except OSError:
                continue
            if steam:
                break
    except Exception:
        pass

    if steam:
        roots.append(os.path.normpath(steam))
        vdf = os.path.join(steam, "steamapps", "libraryfolders.vdf")
        if os.path.isfile(vdf):
            try:
                with open(vdf, "r", encoding="utf-8", errors="ignore") as f:
                    for m in re.finditer(r'"path"\s+"([^"]+)"', f.read()):
                        p = os.path.normpath(m.group(1).replace("\\\\", "\\"))
                        if os.path.isdir(p) and p not in roots:
                            roots.append(p)
            except Exception:
                pass
    return roots


def detect_game_root(game: str = "ck3") -> str | None:
    d = GAME_DEFS.get(game, GAME_DEFS["ck3"])
    for root in _steam_roots():
        cand = os.path.join(root, "steamapps", "common", d["folder"])
        if os.path.isfile(os.path.join(cand, *d["exe"])):
            return cand
    # 兜底常见盘符
    for drive in "CDEFGH":
        cand = os.path.join(f"{drive}:\\", "steam", "steamapps", "common", d["folder"])
        if os.path.isfile(os.path.join(cand, *d["exe"])):
            return cand
    return None


def detect_workshop_dir(game_root: str = "", game: str = "ck3") -> str | None:
    appid = GAME_DEFS.get(game, GAME_DEFS["ck3"])["appid"]
    # 工坊目录优先跟随游戏所在库
    roots: list[str] = []
    if game_root:
        try:
            lib = os.path.abspath(os.path.join(game_root, "..", ".."))
            roots.append(lib)
        except Exception:
            pass
    roots.extend(r for r in _steam_roots() if r not in roots)
    for root in roots:
        cand = os.path.join(root, "steamapps", "workshop", "content", appid)
        if os.path.isdir(cand):
            return cand
    return None


def detect_user_dir(game: str = "ck3") -> str | None:
    d = GAME_DEFS.get(game, GAME_DEFS["ck3"])
    pdx = d["pdx"]
    # userBase：用户目录的父级（默认 Paradox Interactive，天际线等例外）
    base = d.get("userBase", "Paradox Interactive")
    folder = os.path.join(base, pdx)
    home = os.path.expanduser("~")
    cands = [os.path.join(home, "Documents", folder)]
    for sub in ("OneDrive", "OneDrive/文档", "OneDrive/Documents"):
        cands.append(os.path.join(home, sub, folder))
    # 部分游戏用户数据不在 Documents，而在 AppData（LocalLow / Local）
    #  天际线2 → AppData/LocalLow/Colossal Order/Cities Skylines II
    #  监狱建筑师 → AppData/Local/Introversion/Prison Architect
    for appdata in ("AppData/LocalLow", "AppData/Local", "AppData/Roaming"):
        cands.append(os.path.join(home, appdata, folder))
    for c in cands:
        if os.path.isdir(c):
            return os.path.normpath(c)
    return None


# ---------------------------------------------------------------- PDX 解析

_RE_KV = re.compile(r'\s*([A-Za-z0-9_.\-]+)\s*=\s*')


def parse_pdx(text: str) -> dict:
    """解析 Paradox 脚本格式（key=value / key="str" / key={ ... }）。"""
    text = text.replace("\r\n", "\n")
    # 去掉注释（粗略：行内 # 之后，不含引号处理，描述文件里足够）
    text = re.sub(r'(?m)(^|\s)#[^\n]*', r'\1', text)

    out: dict = {}
    i, n = 0, len(text)
    while i < n:
        m = _RE_KV.match(text, i)
        if not m:
            i += 1
            continue
        key = m.group(1)
        j = m.end()
        if j < n and text[j] == "{":
            depth, k = 0, j
            while k < n:
                if text[k] == "{":
                    depth += 1
                elif text[k] == "}":
                    depth -= 1
                    if depth == 0:
                        break
                k += 1
            out[key] = _parse_block(text[j + 1:k])
            i = k + 1
        elif j < n and text[j] == '"':
            k, buf = j + 1, []
            while k < n:
                c = text[k]
                if c == "\\" and k + 1 < n:
                    buf.append(text[k + 1])
                    k += 2
                    continue
                if c == '"':
                    break
                buf.append(c)
                k += 1
            out[key] = "".join(buf)
            i = k + 1
        else:
            m2 = re.compile(r'[^\s}]+').match(text, j)
            if m2:
                out[key] = m2.group(0)
                i = m2.end()
            else:
                i = j
    return out


def _parse_block(body: str):
    strs = re.findall(r'"((?:[^"\\]|\\.)*)"', body)
    if strs and "=" not in body:
        return strs
    if "=" in body:
        return parse_pdx(body)
    return strs


def parse_descriptor(path: str) -> dict:
    try:
        with open(path, "r", encoding="utf-8-sig", errors="ignore") as f:
            return parse_pdx(f.read())
    except Exception:
        return {}


# ---------------------------------------------------------------- 版本比较

def _vkey(v: str) -> tuple:
    parts = re.findall(r"\d+", v or "")
    return tuple(int(x) for x in parts[:4]) if parts else (0,)


def compat_state(supported: str, game_version: str) -> str:
    """ok / warn / bad —— 主次版本一致即视为可用。"""
    if not supported or supported.strip() in ("", "*"):
        return "ok"
    sup = supported.replace("x", "*").strip()
    gv = (game_version or "1.20").strip()
    if sup.endswith(".*"):
        base = _vkey(sup[:-2])
        return "ok" if _vkey(gv)[: len(base)] == base else "warn"
    sv, gvv = _vkey(sup), _vkey(gv)
    if sv == gvv or sv[:2] == gvv[:2]:
        return "ok"
    return "warn"


# ---------------------------------------------------------------- 文件工具

def _safe_rel(p: str) -> str:
    return p.replace("\\", "/")


def ensure_dir(p: str) -> None:
    os.makedirs(p, exist_ok=True)


def _quick_sig(path: str):
    """大小 + 首尾各 64KB 的 MD5，用于判断两个大文件内容是否相同。"""
    try:
        import hashlib
        size = os.path.getsize(path)
        h = hashlib.md5()
        with open(path, "rb") as f:
            h.update(f.read(65536))
            if size > 131072:
                f.seek(-65536, os.SEEK_END)
                h.update(f.read(65536))
        return (size, h.hexdigest())
    except Exception:
        return None


def backup_file(src: str, tag: str, min_interval: int = BACKUP_MIN_INTERVAL) -> str | None:
    """写盘前备份。带内容去重与时间窗口，避免产生一堆一模一样的副本。"""
    if not os.path.isfile(src):
        return None
    now = time.time()
    if min_interval and now - _backup_stamp.get(tag, 0) < min_interval:
        return None          # 刚刚备份过，跳过

    ensure_dir(BACKUP_DIR)
    existing = sorted(
        glob.glob(os.path.join(BACKUP_DIR, f"{tag}-*")),
        key=os.path.getmtime, reverse=True,
    )
    sig = _quick_sig(src)
    if existing and sig and _quick_sig(existing[0]) == sig:
        _backup_stamp[tag] = now
        return None          # 与上一份备份内容完全相同，不重复保存

    ts = time.strftime("%Y%m%d-%H%M%S")
    ext = os.path.splitext(src)[1]
    dst = os.path.join(BACKUP_DIR, f"{tag}-{ts}{ext}")
    n = 1
    while os.path.exists(dst):
        n += 1
        dst = os.path.join(BACKUP_DIR, f"{tag}-{ts}-{n}{ext}")
    try:
        shutil.copy2(src, dst)
        _backup_stamp[tag] = now
        _prune_backups(tag)
        return dst
    except Exception:
        return None


def _prune_backups(tag: str) -> None:
    try:
        files = sorted(
            glob.glob(os.path.join(BACKUP_DIR, f"{tag}-*")),
            key=os.path.getmtime,
            reverse=True,
        )
        for f in files[MAX_BACKUPS:]:
            try:
                os.remove(f)
            except Exception:
                pass
    except Exception:
        pass


def list_backups() -> list[dict]:
    out = []
    if not os.path.isdir(BACKUP_DIR):
        return out
    for f in os.listdir(BACKUP_DIR):
        p = os.path.join(BACKUP_DIR, f)
        if not os.path.isfile(p):
            continue
        try:
            st = os.stat(p)
            tag = "launcher-v2" if f.startswith("launcher-v2") else (
                "launcher" if f.startswith("launcher") else (
                "dlc_load" if f.startswith("dlc_load") else "other"))
            out.append({
                "name": f, "size": st.st_size,
                "mtime": int(st.st_mtime), "tag": tag,
            })
        except Exception:
            pass
    out.sort(key=lambda x: x["mtime"], reverse=True)
    return out


def clear_backups(tag: str | None = None) -> int:
    n = 0
    if not os.path.isdir(BACKUP_DIR):
        return 0
    for f in os.listdir(BACKUP_DIR):
        if tag and not f.startswith(tag):
            continue
        p = os.path.join(BACKUP_DIR, f)
        if os.path.isfile(p):
            try:
                os.remove(p)
                n += 1
            except Exception:
                pass
    _backup_stamp.clear()
    return n


def restore_backup(name: str, user_dir: str) -> dict:
    """把指定备份复制回游戏数据目录。"""
    src = os.path.join(BACKUP_DIR, os.path.basename(name))
    if not os.path.isfile(src):
        return {"ok": False, "error": "备份文件不存在"}
    if name.startswith("launcher"):
        dst = os.path.join(user_dir, "launcher-v2.sqlite" if name.startswith("launcher-v2") else "launcher.sqlite")
    elif name.startswith("dlc_load"):
        dst = os.path.join(user_dir, "dlc_load.json")
    else:
        return {"ok": False, "error": "无法识别的备份类型"}
    try:
        if os.path.isfile(dst):
            backup_file(dst, "before-restore")
        shutil.copy2(src, dst)
        _backup_stamp.clear()
        return {"ok": True, "restored": os.path.basename(dst)}
    except Exception as ex:
        return {"ok": False, "error": str(ex)}


def create_desktop_shortcut() -> dict:
    """在桌面创建启动器快捷方式。"""
    exe = sys.executable if getattr(sys, "frozen", False) else os.path.join(
        os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "启动启动器.bat")
    if not os.path.isfile(exe):
        alt = os.path.join(APP_DIR, "人联启动器.exe")
        if os.path.isfile(alt):
            exe = alt
        else:
            return {"ok": False, "error": "找不到可执行文件"}
    desktop = os.path.join(os.path.expanduser("~"), "Desktop")
    if not os.path.isdir(desktop):
        return {"ok": False, "error": "找不到桌面目录"}
    lnk = os.path.join(desktop, "人联启动器.lnk")
    icon = os.path.join(RES_DIR, "static", "icon.ico")
    ps = (
        '$s=(New-Object -ComObject WScript.Shell).CreateShortcut("%s");'
        '$s.TargetPath="%s";$s.WorkingDirectory="%s";'
        '%s$s.Description="人联启动器";$s.Save()'
    ) % (lnk, exe, os.path.dirname(exe),
         ('$s.IconLocation="%s";' % icon) if os.path.isfile(icon) else "")
    try:
        subprocess.run(["powershell", "-NoProfile", "-Command", ps],
                       capture_output=True, timeout=20)
        return {"ok": os.path.isfile(lnk), "path": lnk if os.path.isfile(lnk) else ""}
    except Exception as ex:
        return {"ok": False, "error": str(ex)}


def recycle_path(path: str) -> bool:
    """用 Windows Shell API 删除到回收站（可撤销）。"""
    try:
        import ctypes
        from ctypes import wintypes

        class SHFILEOPSTRUCTW(ctypes.Structure):
            _fields_ = [
                ("hwnd", wintypes.HWND),
                ("wFunc", wintypes.UINT),
                ("pFrom", wintypes.LPCWSTR),
                ("pTo", wintypes.LPCWSTR),
                ("fFlags", ctypes.c_uint16),
                ("fAnyOperationsAborted", wintypes.BOOL),
                ("hNameMappings", ctypes.c_void_p),
                ("lpszProgressTitle", wintypes.LPCWSTR),
            ]

        FO_DELETE = 3
        FOF_ALLOWUNDO = 0x0040
        FOF_NOCONFIRMATION = 0x0010
        FOF_SILENT = 0x0004
        op = SHFILEOPSTRUCTW()
        op.hwnd = None
        op.wFunc = FO_DELETE
        op.pFrom = os.path.abspath(path) + "\0\0"
        op.pTo = None
        op.fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT
        res = ctypes.windll.shell32.SHFileOperationW(ctypes.byref(op))
        return res == 0 and not op.fAnyOperationsAborted
    except Exception:
        return False


def open_path(path: str) -> bool:
    if not path:
        return False
    try:
        if os.path.exists(path):
            os.startfile(path)  # noqa: S606
            return True
    except Exception:
        pass
    return False


# ---------------------------------------------------------------- 启动器数据库

# P 社新启动器（Clausewitz 全家桶）统一用 launcher-v2.sqlite 存播放集；
# 但老游戏（如实测 CK2）官方启动器可能从没建过库 → 只有 0 字节空文件或干脆没有。
# 因此：①兼容个别老安装残留的 launcher.sqlite；②库/表缺失时自动建官方同构空表，
# 保证任何游戏都能自建播放集（对已有官方库零影响，IF NOT EXISTS 全部跳过）。
_SCHEMA_SQL = """
CREATE TABLE IF NOT EXISTS playsets(
  id TEXT PRIMARY KEY, name TEXT, isActive INTEGER, loadOrder TEXT,
  createdOn TEXT, updatedOn TEXT, isRemoved INTEGER, state TEXT, owned INTEGER,
  author TEXT, subscribersCount INTEGER, ratingsCount INTEGER, description TEXT,
  offDisk INTEGER);
CREATE TABLE IF NOT EXISTS playsets_mods(
  playsetId TEXT, modId TEXT, enabled INTEGER, position INTEGER);
CREATE TABLE IF NOT EXISTS playsets_dlcs(
  playsetId TEXT, dlcId TEXT, enabled INTEGER);
CREATE TABLE IF NOT EXISTS mods(
  id TEXT PRIMARY KEY, steamId TEXT, name TEXT, displayName TEXT, version TEXT,
  dirPath TEXT, archivePath TEXT, source TEXT, status TEXT, thumbnailPath TEXT,
  timeUpdated INTEGER, tags TEXT, isMetadataApplied INTEGER, metadataStatus TEXT);
"""


class LauncherDB:
    """P 社启动器数据库（launcher-v2.sqlite）访问封装。"""

    def __init__(self, user_dir: str, game_root: str = "", game: str = "ck3"):
        self.user_dir = user_dir
        self.game_root = game_root
        self.game = game
        primary_path = os.path.join(user_dir, "launcher-v2.sqlite")
        alt_path = os.path.join(user_dir, "launcher.sqlite")
        # 优先官方 v2 库；个别老安装只有 launcher.sqlite 时兼容读取
        if os.path.isfile(primary_path):
            self.path = primary_path
        elif os.path.isfile(alt_path):
            self.path = alt_path
        else:
            self.path = primary_path
        self.dlc_load_path = os.path.join(user_dir, "dlc_load.json")
        self._schema_ok = False

    # ---- 基础
    def exists(self) -> bool:
        # user_dir 为空时 path 是相对路径，指向启动器自己的安装目录。
        # 此时必须当作「没有库」，否则会把播放集写错地方、甚至跨游戏串数据。
        return bool(self.user_dir) and os.path.isfile(self.path)

    @contextlib.contextmanager
    def connect(self):
        """带自动提交/回滚与关闭的连接上下文（避免句柄泄漏锁住数据库）。"""
        if not self.user_dir:
            raise RuntimeError("未设置游戏用户目录，无法访问启动器数据库（请在启动器设置里指定）")
        conn = sqlite3.connect(self.path, timeout=8)
        conn.row_factory = sqlite3.Row
        # 空库/缺表（官方启动器从没建过库，如 CK2）→ 自动补齐官方同构表；
        # 已有官方库时全部 IF NOT EXISTS 跳过，零影响。每个实例只执行一次。
        if not self._schema_ok:
            conn.executescript(_SCHEMA_SQL)
            conn.commit()
            self._schema_ok = True
        try:
            yield conn
            conn.commit()
        except Exception:
            try:
                conn.rollback()
            except Exception:
                pass
            raise
        finally:
            try:
                conn.close()
            except Exception:
                pass

    def is_locked(self) -> bool:
        """官方启动器运行时会长期持锁，尝试独占连接判断。"""
        if not self.exists():
            return False
        try:
            c = sqlite3.connect(self.path, timeout=0.3)
            c.execute("BEGIN EXCLUSIVE")
            c.rollback()
            c.close()
            return False
        except sqlite3.OperationalError:
            return True
        except Exception:
            return False

    # ---- 播放集
    def list_playsets(self) -> list[dict]:
        if not self.exists():
            return []
        try:
            with self.connect() as c:
                rows = c.execute(
                    """SELECT p.id, p.name, p.isActive, p.updatedOn, p.createdOn,
                              (SELECT COUNT(*) FROM playsets_mods pm WHERE pm.playsetId = p.id) AS total,
                              (SELECT COUNT(*) FROM playsets_mods pm WHERE pm.playsetId = p.id AND pm.enabled = 1) AS enabled
                       FROM playsets p WHERE p.isRemoved = 0 ORDER BY p.isActive DESC, p.name"""
                ).fetchall()
            return [dict(r) for r in rows]
        except Exception:
            return []

    def active_playset(self) -> str | None:
        if not self.exists():
            return None
        try:
            with self.connect() as c:
                r = c.execute(
                    "SELECT id FROM playsets WHERE isActive = 1 AND isRemoved = 0 LIMIT 1"
                ).fetchone()
            return r["id"] if r else None
        except Exception:
            return None

    def get_playset(self, psid: str) -> dict | None:
        if not self.exists():
            return None
        try:
            with self.connect() as c:
                r = c.execute(
                    "SELECT id,name,isActive FROM playsets WHERE id=?", (psid,)
                ).fetchone()
                if not r:
                    return None
                rows = c.execute(
                    """SELECT pm.modId, pm.enabled, pm.position,
                              m.name, m.displayName, m.steamId, m.dirPath, m.source
                       FROM playsets_mods pm JOIN mods m ON m.id = pm.modId
                       WHERE pm.playsetId = ? ORDER BY pm.position""",
                    (psid,),
                ).fetchall()
                dlcs = c.execute(
                    "SELECT dlcId, enabled FROM playsets_dlcs WHERE playsetId=?", (psid,)
                ).fetchall()
            return {
                "id": r["id"],
                "name": r["name"],
                "isActive": bool(r["isActive"]),
                "mods": [dict(x) for x in rows],
                "dlcs": {d["dlcId"]: bool(d["enabled"]) for d in dlcs},
            }
        except Exception:
            return None

    # ---- DLC 加载状态
    def dlc_enabled_map(self, psid: str | None) -> dict:
        if not psid or not self.exists():
            return {}
        try:
            with self.connect() as c:
                rows = c.execute(
                    "SELECT dlcId, enabled FROM playsets_dlcs WHERE playsetId=?", (psid,)
                ).fetchall()
            return {r["dlcId"]: bool(r["enabled"]) for r in rows}
        except Exception:
            return {}

    def known_dlc_ids(self) -> set:
        """⚠ 已废弃，不要用来判断「是否拥有」。

        官方启动器会把**游戏目录里扫到的所有 DLC**（含没买的）都写进
        playsets_dlcs，所以这张表只能说明「这个播放集里有哪些 DLC 开关」，
        不能说明 Steam 是否拥有。判断有没有内容请用 `dlc_has_content()`。
        """
        if not self.exists():
            return set()
        try:
            with self.connect() as c:
                return {r[0] for r in c.execute("SELECT DISTINCT dlcId FROM playsets_dlcs")}
        except Exception:
            return set()

    # ---- dlc_load.json
    def read_dlc_load(self) -> dict:
        if not os.path.isfile(self.dlc_load_path):
            return {"disabled_dlcs": [], "enabled_mods": []}
        try:
            with open(self.dlc_load_path, "r", encoding="utf-8-sig") as f:
                d = json.load(f)
            d.setdefault("disabled_dlcs", [])
            d.setdefault("enabled_mods", [])
            return d
        except Exception:
            return {"disabled_dlcs": [], "enabled_mods": []}

    def write_dlc_load(self, enabled_mods: list[str], disabled_dlcs: list[str]) -> str | None:
        # user_dir 为空时路径是相对的，绝不能把 dlc_load.json 写进启动器安装目录
        if not self.user_dir:
            return None
        bak = backup_file(self.dlc_load_path, "dlc_load")
        data = {"disabled_dlcs": list(disabled_dlcs), "enabled_mods": list(enabled_mods)}
        tmp = self.dlc_load_path + ".tmp"
        with open(tmp, "w", encoding="utf-8", newline="\n") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
        os.replace(tmp, self.dlc_load_path)
        return bak

    # ---- mod 注册
    def mod_records(self) -> list[dict]:
        if not self.exists():
            return []
        try:
            with self.connect() as c:
                # gameRegistryId 是官方启动器写下的 .mod 相对路径，最权威。
                # 自建的简化库没有这列，动态探测避免 SELECT 整段失败。
                cols = {r[1] for r in c.execute("PRAGMA table_info(mods)")}
                extra = ", gameRegistryId" if "gameRegistryId" in cols else ""
                rows = c.execute(
                    """SELECT id, steamId, name, displayName, version, dirPath, archivePath,
                              source, status, thumbnailPath, timeUpdated""" + extra +
                    " FROM mods"
                ).fetchall()
            return [dict(r) for r in rows]
        except Exception:
            return []

    def ensure_mod(self, rec: dict, conn=None) -> str:
        """确保 mod 在注册表里存在，返回 modId(uuid)。不存在则新建。

        conn: 复用外层连接。write_playset 已持有写事务时，不能再开新连接，
        否则新连接抢不到写锁，会等 8 秒后抛 database is locked。
        """
        steam_id = rec.get("steamId")
        dir_path = rec.get("dirPath")
        # 本地 mod 的 .mod 描述文件里经常残留 remote_file_id（从工坊复制下来改的）。
        # 它只是元数据，绝不能拿它去复用注册表里的工坊记录，否则播放集里的
        # 本地 mod 会被静默顶替成工坊版（用户实测：single_heir 变工坊单一指定继承人）。
        is_local = str(rec.get("source") or "").strip().lower() == "local"
        own = conn is None
        ctx = self.connect() if own else contextlib.nullcontext(conn)
        with ctx as c:
            row = None
            if steam_id and not is_local:
                row = c.execute(
                    "SELECT id FROM mods WHERE steamId=? LIMIT 1", (str(steam_id),)
                ).fetchone()
            if row is None and dir_path:
                row = c.execute(
                    "SELECT id FROM mods WHERE dirPath=? LIMIT 1", (dir_path,)
                ).fetchone()
            if row is None and steam_id and is_local:
                row = c.execute(
                    "SELECT id FROM mods WHERE steamId=? AND source='local' LIMIT 1",
                    (str(steam_id),),
                ).fetchone()
            if row is not None:
                return row["id"]
            new_id = str(uuid.uuid4())
            c.execute(
                """INSERT INTO mods
                   (id, steamId, name, displayName, version, dirPath, source, status,
                    timeUpdated, tags, isMetadataApplied, metadataStatus)
                   VALUES (?,?,?,?,?,?,?,?,?,?,0,'not_applied')""",
                (
                    new_id,
                    str(steam_id) if (steam_id and not is_local) else None,
                    rec.get("name") or "",
                    rec.get("displayName") or rec.get("name") or "",
                    rec.get("version") or "",
                    dir_path,
                    rec.get("source") or "local",
                    "ready_to_play",
                    int(time.time()),
                    "[]",
                ),
            )
            if own:
                c.commit()
            return new_id

    # ---- 写播放集
    def write_playset(self, psid: str, items: list[dict], dlc_map: dict | None = None) -> dict:
        """
        items: [{'modFile': 'mod/ugc_x.mod', 'enabled': True, 'rec': {...}}] 有序
        同时同步 dlc_load.json（当该播放集为 active 时）。
        """
        bak_sql = backup_file(self.path, os.path.splitext(os.path.basename(self.path))[0])
        with self.connect() as c:
            # 清空原关系
            c.execute("DELETE FROM playsets_mods WHERE playsetId=?", (psid,))
            for pos, it in enumerate(items):
                mod_id = it.get("modId") or self.ensure_mod(it.get("rec") or {}, c)
                c.execute(
                    "INSERT INTO playsets_mods (playsetId, modId, enabled, position) VALUES (?,?,?,?)",
                    (psid, mod_id, 1 if it.get("enabled", True) else 0, pos),
                )
            if dlc_map is not None:
                c.execute("DELETE FROM playsets_dlcs WHERE playsetId=?", (psid,))
                for dlc_id, en in dlc_map.items():
                    c.execute(
                        "INSERT INTO playsets_dlcs (playsetId, dlcId, enabled) VALUES (?,?,?)",
                        (psid, dlc_id, 1 if en else 0),
                    )
            c.execute(
                "UPDATE playsets SET updatedOn=datetime('now') WHERE id=?", (psid,)
            )
            c.commit()

        # 只有当前激活的播放集才同步到 dlc_load.json，避免污染游戏实际读取的列表
        synced = False
        if self.active_playset() == psid:
            synced = self.sync_dlc_load(psid) is not None
        return {"sqliteBackup": bak_sql, "synced": synced}

    def sync_dlc_load(self, psid: str) -> str | None:
        """把播放集里启用的 mod 按 position 写入 dlc_load.json（同一文件保序去重）。

        disabled_dlcs 恒写空：CK3 1.20 引擎已忽略这个字段（无条件加载
        game/dlc/ 下所有 .dlc 文件），真正的 DLC 禁用靠改 .dlc 文件扩展名
        （set_dlc_file_state）。这里写空是为了和官方启动器/朗文保持一致。
        """
        ps = self.get_playset(psid)
        if not ps:
            return None
        mod_file_map = _mod_id_to_file_map(self)
        enabled_mods: list[str] = []
        seen: set = set()
        for m in ps["mods"]:
            if not m["enabled"]:
                continue
            f = mod_file_map.get(m["modId"])
            if not f:
                continue
            k = f.lower()
            if k in seen:      # 同一条 .mod 加载两次没有意义，去重
                continue
            seen.add(k)
            enabled_mods.append(f)
        return self.write_dlc_load(enabled_mods, [])

    def set_active(self, psid: str) -> bool:
        backup_file(self.path, os.path.splitext(os.path.basename(self.path))[0])
        with self.connect() as c:
            c.execute("UPDATE playsets SET isActive = 0 WHERE isActive = 1")
            c.execute("UPDATE playsets SET isActive = 1 WHERE id = ?", (psid,))
            c.commit()
        self.sync_dlc_load(psid)
        return True

    # ---- 播放集增删改
    def create_playset(self, name: str, clone_from: str | None = None) -> str:
        new_id = str(uuid.uuid4())
        backup_file(self.path, os.path.splitext(os.path.basename(self.path))[0])
        with self.connect() as c:
            c.execute(
                """INSERT INTO playsets (id, name, isActive, loadOrder, createdOn, isRemoved,
                                          state, owned, author, subscribersCount, ratingsCount, description, offDisk)
                   VALUES (?,?,0,'custom',datetime('now'),0,'private',1,'',0,0,'',0)""",
                (new_id, name),
            )
            if clone_from:
                src = c.execute(
                    "SELECT modId, enabled, position FROM playsets_mods WHERE playsetId=? ORDER BY position",
                    (clone_from,),
                ).fetchall()
                for r in src:
                    c.execute(
                        "INSERT INTO playsets_mods (playsetId, modId, enabled, position) VALUES (?,?,?,?)",
                        (new_id, r["modId"], r["enabled"], r["position"]),
                    )
                for r in c.execute(
                    "SELECT dlcId, enabled FROM playsets_dlcs WHERE playsetId=?", (clone_from,)
                ).fetchall():
                    c.execute(
                        "INSERT INTO playsets_dlcs (playsetId, dlcId, enabled) VALUES (?,?,?)",
                        (new_id, r["dlcId"], r["enabled"]),
                    )
            c.commit()
        return new_id

    def rename_playset(self, psid: str, name: str) -> bool:
        backup_file(self.path, os.path.splitext(os.path.basename(self.path))[0])
        with self.connect() as c:
            c.execute("UPDATE playsets SET name=? WHERE id=?", (name, psid))
            c.commit()
        return True

    def delete_playset(self, psid: str) -> bool:
        backup_file(self.path, os.path.splitext(os.path.basename(self.path))[0])
        with self.connect() as c:
            c.execute("DELETE FROM playsets_mods WHERE playsetId=?", (psid,))
            c.execute("DELETE FROM playsets_dlcs WHERE playsetId=?", (psid,))
            c.execute("DELETE FROM playsets WHERE id=?", (psid,))
            c.commit()
        return True


_PATHMAP_CACHE: dict = {"key": None, "data": {}}


def _path_to_modfile_map(user_dir: str) -> dict:
    """反查表：normcase(abspath(.mod 里的 path)) → "mod/<文件名>.mod"。

    用 .mod 目录签名缓存：真实 CK3 有 700 个 .mod，每次播放集读写都重扫
    会多出半秒延迟；描述文件没变化就直接复用。
    """
    mod_dir = os.path.join(user_dir, "mod") if user_dir else ""
    has = os.path.isdir(mod_dir)
    sig = _dir_sig(mod_dir) if has else ""
    key = (mod_dir, sig)
    if _PATHMAP_CACHE["key"] == key:
        return _PATHMAP_CACHE["data"]
    out: dict = {}
    if has:
        try:
            for fn in os.listdir(mod_dir):
                if not fn.lower().endswith(".mod"):
                    continue
                p = (parse_descriptor(os.path.join(mod_dir, fn)).get("path") or "").strip()
                if not p:
                    continue
                if not os.path.isabs(p):
                    p = os.path.join(user_dir, p)
                out[os.path.normcase(os.path.abspath(p))] = "mod/" + fn
        except Exception:
            pass
    _PATHMAP_CACHE.update({"key": key, "data": out})
    return out


def _mod_id_to_file_map(db: LauncherDB) -> dict:
    """modId(uuid) → dlc_load.json 里的 mod 文件相对路径。

    优先级（越靠前越权威）：
      1. 官方注册表里的 gameRegistryId —— 官方启动器自己写的 mod/xxx.mod
      2. 按 dirPath 反查 mod/*.mod 描述文件里的 path —— 本地 mod 的关键：
         本地 .mod 常残留 remote_file_id，绝不能据此写成 mod/ugc_<sid>.mod
      3. 工坊记录（source != local 且有 steamId）→ mod/ugc_<steamId>.mod
      4. dirPath 目录名 / name 兜底
    """
    by_path = _path_to_modfile_map(db.user_dir)
    out: dict = {}
    for r in db.mod_records():
        reg = (r.get("gameRegistryId") or "").strip().replace("\\", "/")
        if reg.lower().startswith("mod/") and reg.lower().endswith(".mod"):
            out[r["id"]] = reg
            continue
        dp = (r.get("dirPath") or "").strip().rstrip("\\/")
        if dp:
            hit = by_path.get(os.path.normcase(os.path.abspath(dp)))
            if hit:
                out[r["id"]] = hit
                continue
        sid = (r.get("steamId") or "").strip()
        src = (r.get("source") or "").strip().lower()
        base = os.path.basename(dp) if dp else ""
        if not sid and base.isdigit():
            # 工坊目录名就是 Steam ID，即使注册表里 steamId 为空也能推断
            sid = base
        if sid and src != "local":
            out[r["id"]] = f"mod/ugc_{sid}.mod"
            continue
        if base:
            out[r["id"]] = f"mod/{base}.mod"
            continue
        nm = (r.get("name") or "").strip()
        if nm:
            out[r["id"]] = f"mod/{nm}.mod"
    return out


# ---------------------------------------------------------------- Mod 索引

_INDEX_CACHE: dict = {"key": None, "data": None, "time": 0}


def _dir_sig(path: str) -> str:
    try:
        ents = []
        for e in os.scandir(path):
            if e.is_file() and e.name.lower().endswith(".mod"):
                st = e.stat()
                ents.append(f"{e.name}:{st.st_mtime_ns}:{st.st_size}")
        ents.sort()
        return str(hash(tuple(ents)))
    except Exception:
        return ""


def build_mod_index(cfg: dict, force: bool = False) -> list[dict]:
    """扫描所有 .mod 描述文件，合并 sqlite 元数据，生成统一 mod 列表。"""
    user_dir = cfg.get("userDir") or ""
    mod_dir = os.path.join(user_dir, "mod")
    game_version = cfg.get("gameVersion") or "1.20"
    has_mod_dir = os.path.isdir(mod_dir)

    if has_mod_dir:
        sig = _dir_sig(mod_dir)
        cache_key = f"{mod_dir}|{sig}|{game_version}"
        if not force and _INDEX_CACHE["key"] == cache_key and _INDEX_CACHE["data"]:
            return _INDEX_CACHE["data"]
    else:
        # 用户数据目录还没生成（游戏未启动过）：仍可扫工坊目录，用 workshopDir 做缓存键
        sig = ""
        cache_key = f"{cfg.get('workshopDir') or ''}|{game_version}"

    db = LauncherDB(user_dir, cfg.get("gameRoot") or "", _game_key(cfg))
    records = db.mod_records()
    by_steam = {}
    by_dir = {}
    for r in records:
        if r.get("steamId"):
            by_steam[str(r["steamId"])] = r
        dp = (r.get("dirPath") or "").strip().rstrip("\\/")
        if dp:
            by_dir[os.path.normcase(dp)] = r

    cache_dir = os.path.join(user_dir, ".launcher-cache")
    # 一次性建缩略图缓存索引，避免对每个 mod 做 glob
    thumb_cache: dict[str, str] = {}
    try:
        for d in os.scandir(cache_dir):
            if d.is_dir() and d.name.startswith("steam-mod-thumbnail-"):
                sid = d.name[len("steam-mod-thumbnail-"):]
                for f in os.scandir(d.path):
                    if f.is_file():
                        thumb_cache[sid] = f.path
                        break
    except Exception:
        pass

    # 收藏夹：modFile(小写) -> [收藏夹名]，一次性加载
    _col_map: dict[str, list[str]] = {}
    for _cname, _clist in _load_collections().get(cfg.get("currentGame") or "ck3", {}).items():
        for _mf in _clist:
            _col_map.setdefault(_mf, []).append(_cname)

    items: list[dict] = []

    entries = []
    if has_mod_dir:
        try:
            entries = sorted(os.scandir(mod_dir), key=lambda e: e.name.lower())
        except Exception:
            entries = []

    for e in entries:
        if not e.is_file() or not e.name.lower().endswith(".mod"):
            continue
        mod_file = "mod/" + e.name
        desc = parse_descriptor(e.path)
        name = (desc.get("name") or "").strip()
        path_attr = (desc.get("path") or "").strip().replace("/", "\\")
        remote_id = str(desc.get("remote_file_id") or "").strip()
        stem = os.path.splitext(e.name)[0]
        if not remote_id and stem.lower().startswith("ugc_"):
            remote_id = stem[4:]

        content_dir = path_attr
        if content_dir and not os.path.isabs(content_dir):
            content_dir = os.path.join(user_dir, content_dir)
        workshop_root = cfg.get("workshopDir") or ""
        in_workshop = bool(workshop_root) and os.path.isdir(content_dir) and \
            os.path.abspath(content_dir).lower().startswith(os.path.abspath(workshop_root).lower())
        # 本地 mod 的 .mod 里常残留 remote_file_id（从工坊复制下来自己改的）。
        # 只有 path 确实指向工坊目录时，才允许按它去匹配注册表里的工坊记录；
        # 否则一律按目录匹配本地记录，避免播放集里的本地 mod 被静默顶替成工坊版。
        rec = by_steam.get(remote_id) if (remote_id and in_workshop) else None
        if rec is None and content_dir:
            rec = by_dir.get(os.path.normcase(os.path.abspath(content_dir)))

        valid = bool(content_dir) and os.path.isdir(content_dir)
        # source 判定：优先看 path 指向的实际目录。
        #  - 指向 steamapps/workshop/content/  → 工坊 mod
        #  - 指向用户 mod/ 目录或其他本地位置 → 本地 mod
        # （不能只看 remote_file_id：本地 mod 的 .mod 文件里也常残留 remote_file_id）
        # 最高优先级：用户手动覆盖（本地/工坊），可修复自动判定错误。
        manual_src = get_mod_source_override(cfg.get("currentGame") or "ck3", mod_file)
        if manual_src:
            source = manual_src
        elif in_workshop:
            source = "steam"
        elif content_dir and os.path.isdir(content_dir):
            source = "local"
        else:
            # path 无效/缺失：回退到 rec.source 或 remote_id
            source = (rec.get("source") if rec else None) or ("steam" if remote_id else "local")
        is_ws = (source == "steam")

        # 缩略图
        thumb = None
        if path_attr:
            for fn in ("thumbnail.png", "thumbnail.jpg", "picture.png"):
                p = os.path.join(content_dir, fn)
                if os.path.isfile(p):
                    thumb = p
                    break
        if not thumb and rec and rec.get("thumbnailPath") and os.path.isfile(rec["thumbnailPath"]):
            thumb = rec["thumbnailPath"]
        if not thumb and remote_id:
            thumb = thumb_cache.get(remote_id, "")

        tags = desc.get("tags")
        if isinstance(tags, str):
            tags = [tags]
        if not isinstance(tags, list):
            tags = []

        supported = str(desc.get("supported_version") or "")
        original_name = name or stem
        # 自定义显示名（别名）优先，存的是小写 modFile 键
        alias = get_mod_alias(cfg.get("currentGame") or "ck3", mod_file)
        display_name = alias or original_name
        items.append(
            {
                "key": mod_file.lower(),
                "modFile": mod_file,
                "modId": rec["id"] if rec else None,
                # 本地 mod 的 steamId 必须是 None：下游 _rec_of / ensure_mod /
                # _mod_id_to_file_map 都拿它当身份，残留的工坊 ID 会把本地记录
                # 绑到工坊版。原值另存 originSteamId，仅供展示/参考。
                "steamId": (remote_id or None) if is_ws else None,
                "originSteamId": None if is_ws else (remote_id or None),
                "name": display_name,
                "originalName": original_name,   # 原始名，恢复用
                "aliased": bool(alias),           # 是否被自定义改名
                "version": str(desc.get("version") or ""),
                "supportedVersion": supported,
                "tags": tags,
                "source": source,
                "sourceOverridden": bool(manual_src),   # 是否被手动覆盖来源
                "collections": _col_map.get(mod_file.lower(), []),   # 所在收藏夹名
                "dirPath": os.path.abspath(content_dir) if content_dir else "",
                "thumb": thumb or "",
                "valid": valid,
                "compat": compat_state(supported, game_version) if valid else "bad",
                "size": int(rec.get("size") or 0) if rec else 0,
                "descriptor": e.name,
            }
        )

    # 工坊里已订阅但还没被注册成 .mod 的（新订阅）
    workshop = cfg.get("workshopDir") or ""
    known_steam = set()
    for i in items:
        if i.get("steamId"):
            known_steam.add(i["steamId"])
        if i.get("originSteamId"):
            # 本地 mod 里残留的工坊 ID 也参与去重，避免同一个订阅冒出两个条目
            known_steam.add(i["originSteamId"])
    if workshop and os.path.isdir(workshop):
        try:
            for e in os.scandir(workshop):
                if not e.is_dir() or e.name in known_steam:
                    continue
                # 描述文件两种来源：
                #  CK3 等 → descriptor.mod
                #  VIC3 等新引擎 → .metadata/metadata.json（含 name/version）
                dsc = os.path.join(e.path, "descriptor.mod")
                desc = {}
                if os.path.isfile(dsc):
                    desc = parse_descriptor(dsc)
                    nm = (desc.get("name") or e.name).strip()
                    supported = str(desc.get("supported_version") or "")
                    version = str(desc.get("version") or "")
                else:
                    mj = os.path.join(e.path, ".metadata", "metadata.json")
                    if os.path.isfile(mj):
                        try:
                            with open(mj, "r", encoding="utf-8-sig") as f:
                                mdata = json.load(f) or {}
                        except Exception:
                            mdata = {}
                        nm = (mdata.get("name") or e.name).strip()
                        supported = str(mdata.get("supported_game_version") or "")
                        version = str(mdata.get("version") or "")
                        desc = mdata
                    else:
                        nm = e.name
                        supported = ""
                        version = ""
                thumb = ""
                for fn in ("thumbnail.png", "thumbnail.jpg"):
                    p = os.path.join(e.path, fn)
                    if os.path.isfile(p):
                        thumb = p
                        break
                items.append(
                    {
                        "key": f"mod/ugc_{e.name}.mod".lower(),
                        "modFile": f"mod/ugc_{e.name}.mod",
                        "modId": None,
                        "steamId": e.name,
                        "name": nm,
                        "version": version,
                        "supportedVersion": supported,
                        "tags": desc.get("tags") if isinstance(desc.get("tags"), list) else [],
                        "source": "steam",
                        "dirPath": e.path,
                        "thumb": thumb,
                        "valid": False,
                        "pending": True,   # 需要注册描述文件
                        "compat": "warn",
                        "size": 0,
                        "descriptor": "",
                    }
                )
        except Exception:
            pass

    _INDEX_CACHE.update({"key": cache_key, "data": items, "time": time.time()})
    return items


def _dir_size(path: str) -> int:
    total = 0
    try:
        for root, _dirs, files in os.walk(path):
            for f in files:
                try:
                    total += os.path.getsize(os.path.join(root, f))
                except Exception:
                    pass
    except Exception:
        pass
    return total


def register_mod_descriptor(cfg: dict, steam_id: str) -> dict:
    """把工坊 mod 写成 mod/ugc_<id>.mod 描述文件（官方启动器同款命名）。

    注意：绝不能让 userDir 为空时写出相对路径 'mod\\ugc_x.mod'——那会在
    open() 时抛 FileNotFoundError，连带把「保存播放集」和「识别新订阅」都搞崩。
    """
    user_dir = (cfg.get("userDir") or "").strip()
    if not user_dir:
        return {"ok": False, "error": "未设置游戏用户目录，无法生成 .mod 描述文件"}
    workshop = cfg.get("workshopDir") or ""
    src = os.path.join(workshop, steam_id, "descriptor.mod")
    if not os.path.isfile(src):
        return {"ok": False, "error": "工坊目录里找不到 descriptor.mod"}
    desc = parse_descriptor(src)
    content = (
        f'version="{desc.get("version","")}"\n'
        f'name="{desc.get("name","")}"\n'
        f'supported_version="{desc.get("supported_version","")}"\n'
        f'remote_file_id="{steam_id}"\n'
        f'path="{os.path.join(workshop, steam_id).replace(chr(92), "/")}"\n'
    )
    if desc.get("tags"):
        content += "tags={\n" + "".join(f'\t"{t}"\n' for t in desc["tags"]) + "}\n"
    mod_dir = os.path.join(user_dir, "mod")
    dst = os.path.abspath(os.path.join(mod_dir, f"ugc_{steam_id}.mod"))
    # 保护：同名 .mod 可能是一个本地 mod 在用（用户把工坊 mod 复制下来自己改）。
    # 直接覆盖会把它的 path 洗成工坊目录，播放集里的本地 mod 就变成工坊版了。
    if os.path.isfile(dst):
        try:
            old_p = (parse_descriptor(dst).get("path") or "").strip()
            if old_p:
                if not os.path.isabs(old_p):
                    old_p = os.path.join(user_dir, old_p)
                if not os.path.abspath(old_p).lower().startswith(os.path.abspath(workshop).lower()):
                    return {"ok": False,
                            "error": f"ugc_{steam_id}.mod 已被本地模组占用，未覆盖"}
        except Exception:
            pass
    try:
        # 新装游戏/首次用官方启动器前，mod/ 目录往往还不存在 → 自动补建
        os.makedirs(mod_dir, exist_ok=True)
        with open(dst, "w", encoding="utf-8", newline="\n") as f:
            f.write(content)
    except OSError as ex:
        return {"ok": False, "error": "写入描述文件失败：%s" % ex}
    return {"ok": True, "path": dst}


# ---------------------------------------------------------------- mod 压缩包导入（拖拽安装）

def _zip_member_name(info) -> str:
    """修正 zip 内文件名编码。

    Windows 下用资源管理器或第三方工具打的中文 zip 通常没打 UTF-8 标志位，
    Python 会按 cp437 解码 → 中文全乱（解压出来一堆问号目录）。
    这里还原成 GBK 再解码，保持中文原样。
    """
    name = info.filename
    if getattr(info, "flag_bits", 0) & 0x800:
        return name
    try:
        return name.encode("cp437").decode("gbk")
    except Exception:
        return name


def _find_archive_tool() -> str:
    """找系统里的 7-Zip / UnRAR（装了能力最全，优先用）。"""
    cands = [
        r"C:\Program Files\7-Zip\7z.exe",
        r"C:\Program Files (x86)\7-Zip\7z.exe",
        r"C:\Program Files\WinRAR\UnRAR.exe",
        r"C:\Program Files (x86)\WinRAR\UnRAR.exe",
    ]
    for c in cands:
        if os.path.isfile(c):
            return c
    for exe in ("7z.exe", "7za.exe", "unrar.exe"):
        p = shutil.which(exe)
        if p:
            return p
    return ""


def _find_bsdtar() -> str:
    """Windows 10/11 自带的 tar.exe 其实是 bsdtar（libarchive），原生支持解 7z/rar/zip。

    这条路径让「没装 7-Zip / WinRAR 的机器」也能解开 7z，不用用户额外装东西。
    """
    root = os.environ.get("SystemRoot") or r"C:\Windows"
    p = os.path.join(root, "System32", "tar.exe")
    if os.path.isfile(p):
        return p
    return shutil.which("bsdtar") or ""


def _pick_extractor() -> tuple[str, str]:
    """挑一个能解 7z/rar 的工具，返回 (路径, 类型: 7z|unrar|bsdtar)。"""
    tool = _find_archive_tool()
    if tool:
        return tool, ("unrar" if tool.lower().endswith("unrar.exe") else "7z")
    tar = _find_bsdtar()
    if tar:
        return tar, "bsdtar"
    return "", ""


def _archive_kind(path: str) -> str:
    """按文件头判断压缩包类型（zip / 7z / rar），判断不出返回空串。"""
    try:
        with open(path, "rb") as f:
            head = f.read(8)
    except Exception:
        return ""
    if head[:4] in (b"PK\x03\x04", b"PK\x05\x06", b"PK\x07\x08"):
        return "zip"
    if head[:6] == b"7z\xbc\xaf\x27\x1c":
        return "7z"
    if head[:6] in (b"Rar!\x1a\x07",):
        return "rar"
    return ""


def _extract_archive(archive: str, dest: str) -> tuple[bool, str]:
    """把压缩包解到 dest。zip 自己解（顺手修中文名），rar/7z 交给 7-Zip。

    关键：外部解压器必须 stdin=DEVNULL + 空密码 + 超时。
    否则遇到「加密包 / 分卷包 / 损坏包」时 7-Zip 会停在交互提示上无限等待，
    请求永远不返回 → 前端遮罩不消失 → 表现为整个启动器「黑屏」。
    """
    os.makedirs(dest, exist_ok=True)
    kind = _archive_kind(archive)
    if not kind:
        return False, "这不是有效的压缩包（zip / 7z / rar），可能已损坏或扩展名被改过"

    if kind == "zip":
        try:
            with zipfile.ZipFile(archive) as z:
                root = os.path.abspath(dest)
                root_cmp = os.path.normcase(root) + os.sep
                for info in z.infolist():
                    name = _zip_member_name(info).replace("\\", "/")
                    parts = [s for s in name.split("/") if s not in ("", ".", "..")]
                    if not parts:
                        continue
                    target = os.path.join(root, *parts)
                    # 防 zip slip：解出来的路径必须落在 dest 里
                    if not os.path.normcase(os.path.abspath(target)).startswith(root_cmp):
                        continue
                    if name.endswith("/"):
                        os.makedirs(target, exist_ok=True)
                        continue
                    os.makedirs(os.path.dirname(target), exist_ok=True)
                    with z.open(info) as src, open(target, "wb") as out:
                        shutil.copyfileobj(src, out, 1024 * 256)
            return True, ""
        except RuntimeError as ex:
            msg = str(ex)
            if "password" in msg.lower() or "encrypted" in msg.lower():
                return False, "这个 zip 设了密码，解不开（请提供未加密的包）"
            return False, "解压失败：%s" % msg
        except Exception as ex:
            return False, "解压失败：%s" % ex

    tool, tkind = _pick_extractor()
    if not tool:
        return False, "这台机器没有可用的解压工具，解不了 .%s（可以先手动解压成文件夹，或打包成 zip 再拖）" % kind
    try:
        if tkind == "unrar":
            # -p- 表示不提示密码
            args = [tool, "x", "-y", "-p-", archive, dest + os.sep]
        elif tkind == "bsdtar":
            args = [tool, "-xf", archive, "-C", dest]
        else:
            # -p 后面不带值 = 空密码，遇到加密包直接报错而不是弹提示
            args = [tool, "x", "-y", "-p", "-o" + dest, archive]
        flags = 0x08000000 if os.name == "nt" else 0   # CREATE_NO_WINDOW
        p = subprocess.run(
            args,
            stdin=subprocess.DEVNULL,      # 绝不让它等键盘输入
            capture_output=True,
            timeout=300,                    # 5 分钟上限，防止无限挂起
            creationflags=flags,
        )
        if p.returncode not in (0, 1):
            out = (p.stdout or b"") + (p.stderr or b"")
            msg = out.decode("utf-8", "ignore").strip() or out.decode("gbk", "ignore").strip()
            low = msg.lower()
            if "password" in low or "encrypted" in low or "加密" in msg:
                return False, "这个压缩包设了密码，解不开（请提供未加密的包）"
            return False, "解压失败：%s" % (msg[-300:] or ("返回码 %d" % p.returncode))
        return True, ""
    except subprocess.TimeoutExpired:
        return False, "解压超时（超过 5 分钟），这个包可能太大、加密或已损坏"
    except Exception as ex:
        return False, "解压失败：%s" % ex


def _find_descriptor(root: str, max_depth: int = 4) -> str:
    """在解压目录里找最浅的 descriptor.mod（mod 内容根）。"""
    best, best_depth = "", 99
    for dirpath, dirnames, filenames in os.walk(root):
        rel = os.path.relpath(dirpath, root)
        depth = 0 if rel == "." else rel.count(os.sep) + 1
        if depth >= max_depth:
            dirnames[:] = []
        if "descriptor.mod" in [f.lower() for f in filenames]:
            if depth < best_depth:
                best = os.path.join(dirpath, "descriptor.mod")
                best_depth = depth
    return best


def _safe_mod_dirname(name: str, fallback: str) -> str:
    """把名字清成 ASCII 安全的目录名（CK3 对非 ASCII 路径兼容性差）。"""
    s = re.sub(r"[^0-9A-Za-z_\-]+", "_", (name or "").strip()).strip("_")
    s = re.sub(r"_+", "_", s)
    if len(s) > 64:
        s = s[:64].rstrip("_")
    return s or fallback


def install_mod_archive(cfg: dict, archive: str, orig_name: str = "") -> dict:
    """把拖进来的 mod 压缩包安装成本地 mod。

    流程：解压到临时目录 → 定位 descriptor.mod → 搬到 <用户目录>/mod/<名字>/ → 生成 <名字>.mod
    目标目录重名时自动加 _2/_3，绝不覆盖已有 mod。
    """
    user_dir = cfg.get("userDir") or ""
    if not user_dir or not os.path.isdir(user_dir):
        return {"ok": False, "error": "还没找到用户数据目录，先在「启动器设置」里指定后再拖入"}
    mod_dir = os.path.join(user_dir, "mod")
    try:
        os.makedirs(mod_dir, exist_ok=True)
    except Exception as ex:
        return {"ok": False, "error": "创建 mod 目录失败：%s" % ex}

    stem = os.path.splitext(os.path.basename(orig_name or archive))[0]
    work = os.path.join(APP_DIR, "tmp-import", "x_" + uuid.uuid4().hex[:8])
    os.makedirs(work, exist_ok=True)
    try:
        ok, err = _extract_archive(archive, work)
        if not ok:
            return {"ok": False, "error": err}

        desc_path = _find_descriptor(work)
        if desc_path:
            src_root = os.path.dirname(desc_path)
            desc = parse_descriptor(desc_path)
        else:
            # 兜底：包里只放了 .mod 描述文件（作者打包成「mod 文件夹 + 描述文件」的形式）
            src_root, desc = "", {}
            for dirpath, _dn, filenames in os.walk(work):
                hit = next((f for f in filenames if f.lower().endswith(".mod")), "")
                if hit:
                    desc = parse_descriptor(os.path.join(dirpath, hit))
                    src_root = dirpath
                    break
            if not src_root:
                listing = [x for x in os.listdir(work) if not x.startswith(".")][:6]
                if not listing:
                    return {
                        "ok": False,
                        "error": "解压出来是空的——这个压缩包可能已损坏、加密，或者是分卷包的一部分",
                    }
                return {
                    "ok": False,
                    "error": "这个包里找不到 descriptor.mod，不像一个 CK3 mod（顶层内容：%s）"
                             % "、".join(listing),
                }

        # 目录名：优先用包里的顶层文件夹名，其次用 mod 名/压缩包名
        rel = os.path.relpath(src_root, work)
        base = rel.split(os.sep)[0] if rel not in ("", ".") else (desc.get("name") or stem)
        dirname = _safe_mod_dirname(base, "local_" + time.strftime("%Y%m%d%H%M%S"))

        target = os.path.join(mod_dir, dirname)
        n = 2
        while os.path.exists(target):
            target = os.path.join(mod_dir, "%s_%d" % (dirname, n))
            n += 1
        dirname = os.path.basename(target)

        try:
            if os.path.abspath(src_root) == os.path.abspath(work):
                shutil.copytree(work, target)
            else:
                shutil.move(src_root, target)
        except Exception as ex:
            return {"ok": False, "error": "复制到 mod 目录失败：%s" % ex}

        # 生成描述文件 mod/<名字>.mod
        gver = str(cfg.get("gameVersion") or "1.20")
        lines = ['name="%s"' % (desc.get("name") or stem or dirname)]
        if desc.get("version"):
            lines.append('version="%s"' % desc["version"])
        if desc.get("supported_version"):
            lines.append('supported_version="%s"' % desc["supported_version"])
        else:
            lines.append('supported_version="%s.*"' % gver.split(".")[0])
        for fn in ("thumbnail.png", "thumbnail.jpg", "picture.png"):
            if os.path.isfile(os.path.join(target, fn)):
                lines.append('picture="%s"' % fn)
                break
        if desc.get("short_description"):
            lines.append('short_description="%s"' % str(desc["short_description"]).replace('"', "'"))
        if desc.get("dependencies"):
            dep = desc["dependencies"]
            dep = dep if isinstance(dep, list) else [dep]
            lines.append("dependencies={\n" + "".join('\t"%s"\n' % d for d in dep) + "}")
        if desc.get("tags"):
            lines.append("tags={\n" + "".join('\t"%s"\n' % t for t in desc["tags"]) + "}")
        lines.append('path="%s"' % os.path.abspath(target).replace("\\", "/"))

        mod_file = os.path.join(mod_dir, dirname + ".mod")
        with open(mod_file, "w", encoding="utf-8", newline="\n") as f:
            f.write("\n".join(lines) + "\n")

        # 缩略图：CK3 只认内容目录里的 thumbnail.png，若只有 jpg 顺手补一份
        return {
            "ok": True,
            "name": desc.get("name") or dirname,
            "dir": target,
            "modFile": os.path.basename(mod_file),
            "renamed": dirname != _safe_mod_dirname(base, ""),
        }
    except Exception as ex:
        return {"ok": False, "error": "安装失败：%s" % ex}
    finally:
        shutil.rmtree(work, ignore_errors=True)


def pack_mod_archive(cfg: dict, mod_file: str) -> dict:
    """把某个 mod 打包成可分享的 zip。

    结构（和本启动器的「拖入安装」完全兼容，别人拖回去就能装）：
        <目录名>/
            descriptor.mod
            common/ events/ localization/ ...
        <目录名>.mod          ← 顶层描述文件，方便手动作业
    """
    user_dir = cfg.get("userDir") or ""
    if not user_dir:
        return {"ok": False, "error": "未设置用户数据目录"}
    mod_dir = os.path.join(user_dir, "mod")
    full = os.path.abspath(os.path.join(user_dir, mod_file.replace("/", os.sep)))
    if not full.lower().startswith(os.path.abspath(mod_dir).lower() + os.sep):
        return {"ok": False, "error": "非法路径"}
    if not os.path.isfile(full):
        return {"ok": False, "error": "描述文件不存在"}

    desc = parse_descriptor(full)
    path_attr = (desc.get("path") or "").strip()
    if not path_attr:
        return {"ok": False, "error": "这个 .mod 里没有 path，找不到内容目录"}
    content_dir = path_attr.replace("/", os.sep)
    if not os.path.isabs(content_dir):
        content_dir = os.path.join(user_dir, content_dir)
    content_dir = os.path.abspath(content_dir)
    if not os.path.isdir(content_dir):
        return {"ok": False, "error": "内容目录不存在：%s" % content_dir}

    folder = os.path.basename(content_dir.rstrip("\\/"))
    # 目录名用 ASCII 安全的（和安装逻辑保持一致，避免解压后彼此不兼容）
    safe_folder = _safe_mod_dirname(folder, "mod_%s" % time.strftime("%Y%m%d%H%M%S"))
    mod_name = desc.get("name") or safe_folder

    # 先统计，好在打包前把体量和文件数告诉用户
    files: list[tuple[str, str]] = []      # (绝对路径, zip 内相对路径)
    total_bytes = 0
    for dirpath, dirnames, filenames in os.walk(content_dir):
        dirnames[:] = [d for d in dirnames if d not in (".git", "__pycache__", ".idea")]
        for fn in filenames:
            if fn in (".DS_Store", "Thumbs.db"):
                continue
            p = os.path.join(dirpath, fn)
            rel = os.path.relpath(p, content_dir).replace("\\", "/")
            files.append((p, "%s/%s" % (safe_folder, rel)))
            try:
                total_bytes += os.path.getsize(p)
            except Exception:
                pass
    if not files:
        return {"ok": False, "error": "内容目录是空的，没什么可打包的"}

    return {
        "ok": True,
        "needsPath": True,
        "name": mod_name,
        "folder": safe_folder,
        "fileCount": len(files),
        "sizeBytes": total_bytes,
        "contentDir": content_dir,
        "modFile": full,
    }


def write_mod_archive(cfg: dict, mod_file: str, dest_zip: str, on_progress=None) -> dict:
    """按 pack_mod_archive 的描述，把内容真正写成 zip。

    on_progress(done, total, cur_name) 会在每个文件写完后被调用，用于前端进度条。
    """
    info = pack_mod_archive(cfg, mod_file)
    if not info.get("ok"):
        return info
    user_dir = cfg.get("userDir") or ""
    content_dir = info["contentDir"]
    safe_folder = info["folder"]
    desc = parse_descriptor(info["modFile"])

    # 顶层 .mod：path 改写成相对形式，别人解压后直接扔进 mod 目录也能用
    lines = ['name="%s"' % (desc.get("name") or safe_folder)]
    if desc.get("version"):
        lines.append('version="%s"' % desc["version"])
    if desc.get("supported_version"):
        lines.append('supported_version="%s"' % desc["supported_version"])
    for fn in ("thumbnail.png", "thumbnail.jpg", "picture.png"):
        if os.path.isfile(os.path.join(content_dir, fn)):
            lines.append('picture="%s"' % fn)
            break
    if desc.get("short_description"):
        lines.append('short_description="%s"' % str(desc["short_description"]).replace('"', "'"))
    if desc.get("tags"):
        lines.append("tags={\n" + "".join('\t"%s"\n' % t for t in desc["tags"]) + "}")
    lines.append('path="mod/%s"' % safe_folder)
    mod_text = "\n".join(lines) + "\n"

    written = 0
    total_files = int(info.get("fileCount") or 0) + 1     # +1 顶层 .mod
    try:
        with zipfile.ZipFile(dest_zip, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
            # 1) 内容目录（含它自己的 descriptor.mod）
            for dirpath, dirnames, filenames in os.walk(content_dir):
                dirnames[:] = [d for d in dirnames if d not in (".git", "__pycache__", ".idea")]
                for fn in filenames:
                    if fn in (".DS_Store", "Thumbs.db"):
                        continue
                    p = os.path.join(dirpath, fn)
                    rel = os.path.relpath(p, content_dir).replace("\\", "/")
                    z.write(p, "%s/%s" % (safe_folder, rel))
                    written += 1
                    # 每 6 个文件报一次，既能让进度条动起来又不至于刷爆
                    if on_progress and written % 6 == 0:
                        try:
                            on_progress(written, total_files, rel)
                        except Exception:
                            pass
            # 2) 顶层描述文件（用相对 path，便携）
            z.writestr("%s.mod" % safe_folder, mod_text.encode("utf-8"))
            written += 1
            if on_progress:
                try:
                    on_progress(total_files, total_files, "%s.mod" % safe_folder)
                except Exception:
                    pass
    except Exception as ex:
        try:
            os.remove(dest_zip)
        except Exception:
            pass
        return {"ok": False, "error": "打包失败：%s" % ex}

    return {
        "ok": True,
        "path": dest_zip,
        "name": info["name"],
        "fileCount": written,
        "sizeBytes": os.path.getsize(dest_zip) if os.path.isfile(dest_zip) else 0,
    }


# ---------------------------------------------------------------- mod 自定义显示名（别名）

def _load_mod_aliases() -> dict:
    """读取 mod 别名映射 { 游戏key: { modFile(小写): 别名 } }。"""
    try:
        with open(MOD_ALIAS_PATH, "r", encoding="utf-8") as f:
            d = json.load(f)
        return d if isinstance(d, dict) else {}
    except Exception:
        return {}


def _save_mod_aliases(data: dict) -> None:
    try:
        with open(MOD_ALIAS_PATH, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
    except Exception:
        pass


def get_mod_alias(game: str, mod_file: str) -> str:
    """取某 mod 的自定义显示名（无则空串）。mod_file 用小写键存储。"""
    aliases = _load_mod_aliases()
    return (aliases.get(game) or {}).get((mod_file or "").lower(), "")


def set_mod_alias(game: str, mod_file: str, alias: str) -> bool:
    """设置某 mod 的自定义显示名。alias 为空 = 恢复原名（删除记录）。"""
    aliases = _load_mod_aliases()
    g = aliases.setdefault(game, {})
    key = (mod_file or "").lower()
    alias = (alias or "").strip()
    if alias:
        g[key] = alias
    else:
        g.pop(key, None)
    _save_mod_aliases(aliases)
    return True


# ---------------------------------------------------------------- mod 来源（本地/工坊）手动覆盖

MOD_SOURCE_OVERRIDE_PATH = os.path.join(APP_DIR, "mod-source-overrides.json")


def _load_source_overrides() -> dict:
    """读取 mod 来源手动覆盖 { 游戏key: { modFile(小写): "local"|"steam" } }。"""
    try:
        with open(MOD_SOURCE_OVERRIDE_PATH, "r", encoding="utf-8") as f:
            d = json.load(f)
        return d if isinstance(d, dict) else {}
    except Exception:
        return {}


def _save_source_overrides(data: dict) -> None:
    try:
        with open(MOD_SOURCE_OVERRIDE_PATH, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
    except Exception:
        pass


def get_mod_source_override(game: str, mod_file: str) -> str:
    """取某 mod 的来源手动覆盖（"local"/"steam"，无则空串）。"""
    overrides = _load_source_overrides()
    return (overrides.get(game) or {}).get((mod_file or "").lower(), "")


def set_mod_source_override(game: str, mod_file: str, source: str) -> bool:
    """手动覆盖某 mod 的来源标签。source 为空 = 清除覆盖（回到自动判定）。"""
    overrides = _load_source_overrides()
    g = overrides.setdefault(game, {})
    key = (mod_file or "").lower()
    source = (source or "").strip().lower()
    if source in ("local", "steam"):
        g[key] = source
    else:
        g.pop(key, None)
    _save_source_overrides(overrides)
    return True


# ---------------------------------------------------------------- mod 收藏夹

MOD_COLLECTIONS_PATH = os.path.join(APP_DIR, "mod-collections.json")


def _load_collections() -> dict:
    """读取收藏夹 { 游戏key: { 收藏夹名: [modFile(小写), ...] } }。"""
    try:
        with open(MOD_COLLECTIONS_PATH, "r", encoding="utf-8") as f:
            d = json.load(f)
        return d if isinstance(d, dict) else {}
    except Exception:
        return {}


def _save_collections(data: dict) -> None:
    try:
        with open(MOD_COLLECTIONS_PATH, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
    except Exception:
        pass


def list_collections(game: str) -> list[dict]:
    """列出某游戏的所有收藏夹，返回 [{name, count}]。"""
    cols = _load_collections().get(game, {})
    return [{"name": n, "count": len(v)} for n, v in cols.items()]


def get_collection_mods(game: str, name: str) -> list[str]:
    """取某收藏夹里的 modFile 列表。"""
    cols = _load_collections().get(game, {})
    return list(cols.get(name, []))


def create_collection(game: str, name: str) -> bool:
    """新建收藏夹。名字已存在则返回 False。"""
    name = (name or "").strip()
    if not name:
        return False
    cols = _load_collections()
    g = cols.setdefault(game, {})
    if name in g:
        return False
    g[name] = []
    _save_collections(cols)
    return True


def rename_collection(game: str, old: str, new: str) -> bool:
    """重命名收藏夹。"""
    old = (old or "").strip()
    new = (new or "").strip()
    if not old or not new:
        return False
    cols = _load_collections()
    g = cols.setdefault(game, {})
    if old not in g or new in g:
        return False
    g[new] = g.pop(old)
    _save_collections(cols)
    return True


def delete_collection(game: str, name: str) -> bool:
    """删除收藏夹（只删收藏夹，不动 mod）。"""
    cols = _load_collections()
    g = cols.get(game, {})
    if name not in g:
        return False
    del g[name]
    if g:
        cols[game] = g
    else:
        cols.pop(game, None)
    _save_collections(cols)
    return True


def toggle_mod_in_collection(game: str, name: str, mod_file: str) -> bool:
    """把 mod 加入/移出某收藏夹。返回是否已收藏。"""
    name = (name or "").strip()
    key = (mod_file or "").lower()
    if not name or not key:
        return False
    cols = _load_collections()
    g = cols.setdefault(game, {})
    lst = g.setdefault(name, [])
    if key in lst:
        lst.remove(key)
        added = False
    else:
        lst.append(key)
        added = True
    _save_collections(cols)
    return added


def mod_collections(game: str, mod_file: str) -> list[str]:
    """返回某 mod 所在的收藏夹名列表（前端标记用）。"""
    key = (mod_file or "").lower()
    cols = _load_collections().get(game, {})
    return [n for n, lst in cols.items() if key in lst]


def delete_mod(cfg: dict, mod_file: str) -> dict:
    """删除一个 mod：.mod 描述文件 + 内容目录（若有），均走回收站。

    mod_file 形如 "mod/ugc_123.mod" 或 "mod/xxx.mod"。
    只允许操作 userDir/mod 下的描述文件和其 path 指向的内容目录，防止误删。
    """
    user_dir = cfg.get("userDir") or ""
    if not user_dir:
        return {"ok": False, "error": "未设置用户数据目录"}
    mod_dir = os.path.join(user_dir, "mod")
    # 安全校验：描述文件必须在 mod 目录内
    full = os.path.abspath(os.path.join(user_dir, mod_file.replace("/", os.sep)))
    if not full.lower().startswith(os.path.abspath(mod_dir).lower() + os.sep):
        return {"ok": False, "error": "非法路径"}
    if not os.path.isfile(full):
        return {"ok": False, "error": "描述文件不存在"}

    # 读 path 拿到内容目录
    desc = parse_descriptor(full)
    path_attr = (desc.get("path") or "").strip().replace("/", "\\")
    content_dir = ""
    if path_attr and not os.path.isabs(path_attr):
        content_dir = os.path.abspath(os.path.join(user_dir, path_attr))
    elif path_attr:
        content_dir = os.path.abspath(path_attr)

    removed = []
    # 先删描述文件
    if recycle_path(full):
        removed.append(full)
    # 再删内容目录（工坊 mod 在 workshop 目录，本地 mod 在 mod 目录里）
    if content_dir and os.path.isdir(content_dir):
        # 只删「非 mod 目录本身」的内容目录，避免误删整个 mod 目录
        if os.path.normcase(content_dir) != os.path.normcase(mod_dir):
            if recycle_path(content_dir):
                removed.append(content_dir)
    return {"ok": True, "removed": removed}


def mod_workshop_url(game: str, steam_id: str) -> str:
    """返回某 mod 的创意工坊页面 URL。"""
    appid = GAME_DEFS.get(game, GAME_DEFS["ck3"])["appid"]
    return f"https://steamcommunity.com/sharedfiles/filedetails/?id={steam_id}"


# ---------------------------------------------------------------- DLC

# 官方中文包里 DLC 名称是英文原文，这里补一份中文对照（按目录名索引）
DLC_CN = {
    "dlc001_preorder": "神圣罗马帝国服饰",
    "dlc002_sp_day1": "阿拔斯宫廷风尚",
    "dlc003_fp1": "北方领主",
    "dlc004_ep1": "皇家宫廷",
    "dlc005_fp2": "伊比利亚的命运",
    "dlc006_bp1": "友与敌",
    "dlc007_ep2": "巡游与比武",
    "dlc008_sp2": "帝国风雅",
    "dlc009_bp2": "监护与守护",
    "dlc010_fp3": "波斯的遗产",
    "dlc011_ce1": "亡者传说",
    "dlc012_afr": "北非服饰",
    "dlc013_sp3": "卡佩华服",
    "dlc014_ep3": "权力之路",
    "dlc015_bp3": "流浪贵族",
    "dlc016_cp2": "西斯拉夫服饰",
    "dlc017_cp3": "中世纪遗迹",
    "dlc018_cp4": "极地服饰",
    "dlc019_sp4": "天下王冠",
    "dlc020_ce2": "草原可汗",
    "dlc021_bp4": "加冕典礼",
    "dlc022_ep4": "普天之下",
    "dlc023_cp5": "中古战阵服饰",
    "dlc024_cp6": "圣殿建筑",
    "dlc025_cp7": "北太平洋服饰",
    "dlc026_cp8": "东亚奇观",
    "dlc027_cp9": "天朝宫廷服饰",
    "dlc028_sp5": "权力象征",
    "dlc029_mp1": "王国之歌",
    "dlc030_ce3": "唯凭神意",
    # 下面几个是捆绑包 / 订阅 / 周边，没有 ep/fp 之类后缀，单独补
    "dlc_3077630": "权力之路 · 漫画书",
    "dlc_4208220": "订阅服务（全部 DLC 通行证）",
    "dlc_4232880": "第五章（捆绑包）",
    "dlc_4232910": "丝绸与白银",
}


def _has_cjk(text: str) -> bool:
    """文本里是否含中日韩汉字（用来判断 Steam 名是不是已经翻译过了）。"""
    return any("\u4e00" <= ch <= "\u9fff" for ch in (text or ""))


_dlc_cn_mod = None


def _dlc_cn_module():
    """惰性加载 dlc_cn 模块，并注入玩家覆盖表路径。加载失败返回 None。"""
    global _dlc_cn_mod
    if _dlc_cn_mod is None:
        try:
            from . import dlc_cn as _m
        except Exception:
            try:
                import dlc_cn as _m
            except Exception:
                _dlc_cn_mod = False
                return None
        _dlc_cn_mod = _m
        # 告诉它与玩家本地覆盖表的位置（那边不 import ck3core，避免循环依赖）
        try:
            _m.USER_PATH = os.path.join(APP_DIR, "dlc-names-cn.json")
        except Exception:
            pass
    return _dlc_cn_mod or None


def _dlc_cn_lookup(name: str) -> str:
    """按英文名查 DLC 中文名（玩家覆盖表 + 内置表，dlc_cn.py 负责优先级）。"""
    mod = _dlc_cn_module()
    if not mod:
        return ""
    try:
        return mod.lookup(name)
    except Exception:
        return ""


def _dlc_cn_user_lookup(name: str) -> str:
    """只查玩家本地覆盖表（dlc-names-cn.json），优先级高于所有内置表。"""
    mod = _dlc_cn_module()
    if not mod:
        return ""
    try:
        return mod.lookup_user(name)
    except Exception:
        return ""


def dlc_has_content(path: str) -> bool:
    """DLC 目录里除「.dlc 定义 + 缩略图」之外，是否还有实际内容。

    为什么这样判：Steam 只会给**已拥有**的 DLC 下载内容 depot。
    没买的 DLC，游戏目录里仍会留一个空壳（xxx.dlc 定义 + thumbnail.png，
    总共一百来 KB），让官方启动器能在列表里显示「可购买」，但内容文件
    （music/ gfx/ sound/ 等）一个都不会有 —— 游戏内也因此是锁死的。

    这是本地唯一能反映「这个 DLC 到底能不能用」的硬证据；
    playsets_dlcs 表里有没有记录**不代表拥有**（官方启动器会把所有能扫到的
    DLC 都写进播放集，所以不能拿它当解锁依据）。
    """
    try:
        other = 0
        size = 0
        for _root, _dirs, files in os.walk(path):
            for fn in files:
                low = fn.lower()
                if low.endswith(".dlc") or low.startswith("thumbnail."):
                    continue
                other += 1
                try:
                    size += os.path.getsize(os.path.join(_root, fn))
                except Exception:
                    pass
        return other > 0 and size >= 32768   # 32 KB 以下视为壳里的杂项
    except Exception:
        return False


# CK3 官方 DLC 分类（依据目录名后缀，来自 paradoxwikis 官方定义）
#   ep = Major Expansion    大型扩展包
#   ce = Core Expansion     核心扩展包
#   fp = Flavor Pack        风味包
#   bp = Event Pack         事件包
#   sp = Cosmetic Pack      装饰包（服饰/外观）
#   cp = Content Creator Pack 内容创作者包（服饰）
#   mp = Music Pack         音乐包
# 注意：dlc_metadata 里的 type 只有 major/medium/minor 三档，粒度太粗，
# 会把 fp/bp/sp/cp 全归到 minor，所以不能拿它分中文类别，要用目录后缀。
DLC_SUFFIX_CN = {
    "ep": "大型扩展包",
    "ce": "核心扩展包",
    "fp": "风味包",
    "bp": "事件包",
    "sp": "装饰包",
    "cp": "内容创作者包",
    "mp": "音乐包",
}
DLC_SUFFIX_ORDER = ["ep", "ce", "fp", "bp", "cp", "sp", "mp"]


def _dlc_category(dirname: str) -> tuple[str, str]:
    """从目录名后缀推官方分类，返回 (code, 中文名)。

    目录名形如 dlc003_fp1 / dlc002_sp_day1 / dlc012_afr / dlc001_preorder。
    """
    core = re.sub(r"^dlc\d+_", "", dirname)
    m = re.match(r"^([a-z]+)", core)
    code = m.group(1) if m else ""
    if code in DLC_SUFFIX_CN:
        return code, DLC_SUFFIX_CN[code]
    # afr / preorder 等特殊：afr=北非服饰(装饰包)，preorder=服饰(装饰包)
    if "attire" in dirname.lower() or code in ("afr", "preorder", "day1"):
        return "sp", "装饰包"
    return code or "other", "其他"


def read_dlc_metadata(game_root: str, game: str = "ck3") -> dict:
    """解析游戏自带的 DLC 元数据，返回「完整官方 DLC 清单」。

    返回 { steam_id(str): {"name", "type", "priority", "category"} }。

    兼容多种格式：
    - CK3/VIC3/HOI4：dlc_metadata/00_dlc_metadata.txt，`dlc001 = { steam_id = "123" }`
    - CK2：dlc_metadata/dlc_info.txt，`dlcs = { dlc001 = { steam_id = 123 } }`
      （steam_id 是不带引号的数字）
    """
    out = {}
    content = _game_content_dir(game_root, game)
    # 候选元数据文件（各游戏路径不同，逐个尝试）
    candidates = [
        os.path.join(content, "dlc_metadata", "00_dlc_metadata.txt"),      # CK3/VIC3
        os.path.join(content, "dlc_metadata", "dlc_info.txt"),             # CK2
        os.path.join(content, "dlc_metadata", "dlc_info", "00_dlc_info.txt"),  # EU4/HOI4
        os.path.join(content, "dlc_metadata", "00_dlc_info.txt"),
        os.path.join(content, "dlc_metadata", "dlc_metadata.txt"),
        os.path.join(content, "dlc_metadata.txt"),
    ]
    p = None
    for c in candidates:
        if os.path.isfile(c):
            p = c
            break
    if not p:
        return out
    try:
        txt = open(p, "r", encoding="utf-8-sig", errors="replace").read()
    except Exception:
        return out

    # 匹配 dlcNNN = { ... }（块内可能含嵌套，但 steam_id/name/type/priority/category 都在本层）
    for m in re.finditer(r"dlc_?\s*\d+\s*=\s*\{([^{}]*(?:\{[^{}]*\}[^{}]*)*)\}", txt):
        body = m.group(1)
        # steam_id 兼容带引号和不带引号
        sid = re.search(r'steam_id\s*=\s*"?(\d+)"?', body)
        if not sid:
            continue
        name = re.search(r'name\s*=\s*"([^"]+)"', body)
        t = re.search(r"type\s*=\s*(\w+)", body)
        pr = re.search(r"priority\s*=\s*(\d+)", body)
        cat = re.search(r"category\s*=\s*(\w+)", body)
        out[sid.group(1)] = {
            "name": name.group(1) if name else "",
            "type": t.group(1) if t else "minor",
            "priority": int(pr.group(1)) if pr else 0,
            "category": cat.group(1) if cat else "",
        }
    return out


def _scan_dlc_files(dlc_root: str) -> dict:
    """扫描 dlc 目录下的 .dlc 文件，兼容两种布局。

    返回 { steam_id(str): {"path", "dir", "name", "popsId", "thumb", "fileDisabled"} }。

    布局1（子目录型，CK3/VIC3/HOI4）：dlc/dlc001_xxx/dlc001.dlc
    布局2（平铺型，CK2）：dlc/dlc001.dlc（文件直接平铺，无子目录）
    """
    out = {}
    if not os.path.isdir(dlc_root):
        return out

    def _parse_one(dlc_path: str, dirname: str) -> None:
        d = parse_descriptor(dlc_path)
        sid = d.get("steam_id") or ""
        if not sid:
            return
        # 找缩略图（子目录里才有）
        thumb = ""
        if dirname:
            for fn in ("thumbnail.png", "thumbnail.jpg"):
                p = os.path.join(dlc_root, dirname, fn)
                if os.path.isfile(p):
                    thumb = p
                    break
        # 判断是否被改名禁用
        base = os.path.basename(dlc_path).lower()
        file_disabled = base.endswith(DISABLED_SUFFIX)
        out[sid] = {
            "path": dlc_path,
            "dir": dirname or os.path.splitext(os.path.basename(dlc_path))[0],
            "name": d.get("name") or "",
            "popsId": d.get("pops_id") or "",
            "thumb": thumb,
            "fileDisabled": file_disabled,
            "category": d.get("category") or "",   # .dlc 文件自带的分类（HOI4/EU4/Imperator 用）
        }

    try:
        for e in os.scandir(dlc_root):
            if e.is_dir():
                # 子目录型：找子目录里的 .dlc（含 .disabled）
                files = glob.glob(os.path.join(e.path, "*.dlc"))
                if not files:
                    files = glob.glob(os.path.join(e.path, "*.dlc" + DISABLED_SUFFIX))
                if files:
                    _parse_one(files[0], e.name)
            elif e.is_file() and e.name.lower().endswith(".dlc"):
                # 平铺型：dlc 目录下直接是 .dlc 文件
                _parse_one(e.path, "")
            elif e.is_file() and e.name.lower().endswith(".dlc" + DISABLED_SUFFIX):
                _parse_one(e.path, "")
    except Exception:
        pass
    return out


def list_dlcs(cfg: dict) -> list[dict]:
    root = cfg.get("gameRoot") or ""
    game = cfg.get("currentGame") or "ck3"
    content = _game_content_dir(root, game)
    dlc_root = os.path.join(content, "dlc")
    local_meta = read_dlc_metadata(root, game)   # 本地 metadata（分类/priority）
    files = _scan_dlc_files(dlc_root)             # 本地 dlc 目录（安装/禁用状态）
    out = []

    # 主清单：Steam 商店元数据库（最全，含中文名/封面/日期，支持运行时刷新）
    # 兜底：本地 metadata + dlc 目录文件（元数据库缺失时保证至少能列出）
    steam_meta = load_dlc_meta(game)

    # 合并所有 steam_id（Steam 元数据 + 本地 metadata + 本地文件）
    all_ids = set(steam_meta.keys()) | set(local_meta.keys()) | set(files.keys())

    for sid in sorted(all_ids):
        sm = steam_meta.get(sid, {})
        lm = local_meta.get(sid, {})
        f = files.get(sid, {})
        dirname = f.get("dir") or lm.get("dir") or f"dlc_{sid}"
        # 原始名称：Steam 商店名 > 本地 metadata > .dlc 文件 > steamid
        raw_name = sm.get("name") or lm.get("name") or f.get("name") or sid
        # 中文名查找顺序（玩家自定义优先）：
        #   ① 玩家本地覆盖表 dlc-names-cn.json —— 优先级最高
        #   ② CK3 目录名专用表（34 条最准）
        #   ③ 内置英文名对照表（全游戏）
        #   ④ 原始名本身已含汉字就直接用（其他游戏的 Steam 名可能是中文）
        cn_name = _dlc_cn_user_lookup(raw_name)
        if not cn_name:
            cn_name = DLC_CN.get(dirname, "") if game == "ck3" else ""
        if not cn_name:
            cn_name = _dlc_cn_lookup(raw_name)
        if not cn_name and _has_cjk(raw_name):
            cn_name = raw_name
        name = raw_name
        # 分类：优先 .dlc 文件自带 category > 本地 metadata category > 兜底按游戏规则
        cat_code, cat_cn = _dlc_category_of(game, dirname, lm, f.get("category") or "")
        out.append(
            {
                "popsId": f.get("popsId") or f"{game}_{dirname}",
                "dir": dirname,
                "name": name,
                "nameCn": cn_name or name,   # 中文名（内置对照表）
                "nameEn": name,              # 英文原名（卡片下方小字、搜索用）
                "steamId": sid,
                "affectsChecksum": True,
                "thumb": sm.get("header") or f.get("thumb") or "",
                "header": sm.get("header") or "",
                "installed": bool(f),       # 有实际 .dlc 文件即视为已安装
                "dlcType": cat_code,
                "typeCn": cat_cn,
                "metaType": lm.get("type", "minor"),
                "priority": lm.get("priority", 0),
                "releaseDate": sm.get("date") or "",
                "sizeBytes": 0,
                "fileDisabled": f.get("fileDisabled", False),
            }
        )
    # 排序：按分类组（各游戏专属顺序），再按 priority，最后按目录
    order = {c: i for i, c in enumerate(GAME_CATEGORY_ORDER.get(game, DLC_SUFFIX_ORDER))}
    out.sort(key=lambda x: (order.get(x["dlcType"], 99), -x["priority"], x["dir"]))
    return out


# 统一的 DLC 分类组（跨游戏），code -> 中文名
# 不同游戏对同一 code 的中文叫法可能不同（如 unit_pack 在 HOI4 叫「单位包」、EU4 叫「兵模包」），
# 各游戏专属覆盖见 GAME_CATEGORY_CN。
DLC_CATEGORY_CN = {
    "expansion": "大型扩展包",
    "core": "核心包",
    "major": "大型扩展包",
    "medium": "风味包",
    "minor": "内容包",
    "music": "音乐包",
    "event": "事件包",
    "content": "内容包",
    "content_pack": "内容包",
    "unit": "兵模包",
    "unit_pack": "兵模包",
    "portrait": "头像包",
    "dynasty_shield": "纹章包",
    "cosmetic": "装饰包",
    "extra": "装饰包",
    "other": "其他",
}

# 各游戏专属的分类中文名（覆盖 DLC_CATEGORY_CN 的默认叫法）。
# code 与官方启动器 / .dlc 文件的 category 字段一一对应。
GAME_CATEGORY_CN: dict[str, dict[str, str]] = {
    "hoi4": {
        "expansion": "扩展",
        "content": "内容",
        "music": "音乐",
        "unit_pack": "单位包",
    },
    "eu4": {
        "expansion": "扩展",
        "content_pack": "内容包",
        "music": "音乐包",
        "unit_pack": "兵模包",
    },
    "imperator": {
        "expansion": "扩展",
        "extra": "装饰包",
    },
    "ck2": {
        "expansion": "大型扩展包",
        "portrait": "头像包",
        "dynasty_shield": "纹章包",
        "music": "音乐包",
        "unit": "兵模包",
        "unit_pack": "兵模包",
        "content": "内容包",
        "other": "其他",
    },
    "stellaris": {
        "expansion": "大型扩展包",
        "species_pack": "物种包",
        "story_pack": "故事包",
        "music": "音乐包",
        "cosmetic": "装饰包",
    },
    "vic3": {
        "expansion": "大型扩展包",
        "music": "音乐包",
        "cosmetic": "装饰包",
    },
}

# 各游戏的分类显示顺序（越靠前越优先，未列出的兜底排最后）
GAME_CATEGORY_ORDER: dict[str, list[str]] = {
    "hoi4": ["expansion", "content", "music", "unit_pack"],
    "eu4": ["expansion", "content_pack", "unit_pack", "music"],
    "imperator": ["expansion", "extra"],
    "ck2": ["expansion", "portrait", "dynasty_shield", "unit", "unit_pack", "content", "music", "other"],
    "stellaris": ["expansion", "species_pack", "story_pack", "cosmetic", "music"],
    "vic3": ["expansion", "cosmetic", "music"],
}


def _dlc_category_of(game: str, dirname: str, lm: dict, file_cat: str = "") -> tuple[str, str]:
    """按游戏 + 本地 metadata + .dlc 文件自带 category 确定 DLC 分类。

    返回 (code, 中文名)。优先级：
      1. .dlc 文件自带的 category（HOI4/EU4/Imperator 最权威）
      2. 本地 metadata 的 category（CK2 用）
      3. CK3 用目录后缀
      4. 目录名关键词兜底
    中文名优先用该游戏专属映射 GAME_CATEGORY_CN，其次全局 DLC_CATEGORY_CN。
    """
    game_cn = GAME_CATEGORY_CN.get(game, {})

    def _cn(code: str) -> str:
        return game_cn.get(code) or DLC_CATEGORY_CN.get(code) or "内容包"

    # 1) .dlc 文件自带 category（HOI4/EU4/Imperator）
    if file_cat and file_cat not in ("none", "other"):
        return file_cat, _cn(file_cat)

    # 2) CK3：用目录后缀（ep/ce/fp/bp/sp/cp/mp），最精确
    if game == "ck3":
        code, cn = _dlc_category(dirname)
        if code in DLC_SUFFIX_CN:
            return code, cn

    # 3) 本地 metadata 的 category（CK2 等）
    cat = (lm.get("category") or "").strip()
    if cat:
        return cat, _cn(cat)

    # 4) 兜底：type 字段
    typ = lm.get("type") or ""
    if typ in ("major", "medium", "minor"):
        return typ, _cn(typ)

    # 5) 再兜底：目录名关键词
    dl = dirname.lower()
    if "music" in dl or "song" in dl:
        return "music", _cn("music")
    if "unit" in dl:
        return "unit_pack", _cn("unit_pack")
    if "portrait" in dl or "face" in dl:
        return "portrait", _cn("portrait")
    return "minor", _cn("minor")


# ---------------------------------------------------------------- Steam DLC 清单（可刷新）

def load_dlc_meta(game: str) -> dict:
    """加载某游戏的 DLC 元数据（steam_id(str) -> {name, date, header}）。

    优先级：运行时缓存（dlc_meta_cache.json，刷新后更新）> 内置快照（dlc_meta.py）。
    """
    # 1) 运行时缓存（最新，刷新后写入）
    try:
        with open(DLC_CACHE_PATH, "r", encoding="utf-8") as f:
            cache = json.load(f) or {}
        if isinstance(cache, dict) and cache.get(game):
            return {str(k): v for k, v in cache[game].items()}
    except Exception:
        pass
    # 2) 内置快照
    try:
        from . import dlc_meta as _dm
    except Exception:
        try:
            import dlc_meta as _dm
        except Exception:
            return {}
    m = (_dm.DLC_META or {}).get(game) or {}
    # 兼容旧版（CK3 专属 {int: {...}} 结构）
    if m and not isinstance(next(iter(m)), str):
        m = {str(k): v for k, v in m.items()}
    return m


def refresh_dlc_from_steam(game: str) -> dict:
    """从 Steam 商店 API 拉取某游戏的完整 DLC 清单，更新运行时缓存。

    返回 {ok, count, msg}。失败时不动缓存，返回错误信息。
    """
    import urllib.request
    appid = GAME_DEFS.get(game, GAME_DEFS["ck3"])["appid"]
    UA = {"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"}

    def _get(url, timeout=12):
        req = urllib.request.Request(url, headers=UA)
        # 禁用代理直连优先（Steam++ 会把 Steam 域名改写到 127.0.0.1 由本机转发）；
        # 失败回退系统代理。
        try:
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
            return json.loads(opener.open(req, timeout=timeout).read())
        except Exception:
            return json.loads(urllib.request.urlopen(req, timeout=timeout).read())

    try:
        # 1) 主游戏 appdetails，拿 dlc 列表
        parent = _get("https://store.steampowered.com/api/appdetails?appids=%s&l=schinese&cc=cn" % appid)
        data = parent.get(appid, {}).get("data")
        if not data:
            return {"ok": False, "count": 0, "msg": "Steam 未返回游戏数据（可能加速器未开或网络异常）"}
        dlc_ids = [str(x) for x in (data.get("dlc") or [])]

        # 2) 逐个拉 DLC 详情（中文名 + 封面 + 日期）
        meta = {}
        for did in dlc_ids:
            try:
                d = _get("https://store.steampowered.com/api/appdetails?appids=%s&l=schinese&cc=cn" % did)
                dd = d.get(did, {}).get("data") or {}
                if not dd:
                    continue
                rel = dd.get("release_date") or {}
                meta[did] = {
                    "name": dd.get("name") or "",
                    "date": rel.get("date", "") if isinstance(rel, dict) else "",
                    "header": dd.get("header_image") or "",
                }
            except Exception:
                continue

        if not meta:
            return {"ok": False, "count": 0, "msg": "未拉取到任何 DLC"}

        # 3) 更新运行时缓存（合并，不覆盖其他游戏）
        try:
            cache = {}
            with open(DLC_CACHE_PATH, "r", encoding="utf-8") as f:
                cache = json.load(f) or {}
        except Exception:
            cache = {}
        cache[game] = meta
        with open(DLC_CACHE_PATH, "w", encoding="utf-8") as f:
            json.dump(cache, f, ensure_ascii=False, indent=2)

        return {"ok": True, "count": len(meta), "msg": ""}
    except Exception as ex:
        return {"ok": False, "count": 0, "msg": str(ex)}


# ---------------------------------------------------------------- 解锁补丁检测


# ---------------------------------------------------------------- 真正禁用 DLC

DISABLED_SUFFIX = ".disabled"


def set_dlc_file_state(game_root: str, dir_name: str, enabled: bool,
                       game: str = "ck3") -> bool:
    """真正禁用/启用某个 DLC：改 .dlc 文件的扩展名。

    CK3 引擎靠扫描 game/dlc/*/xxx.dlc 发现 DLC，`disabled_dlcs` 字段在 1.20
    已失效。唯一可靠的禁用方式是把 xxx.dlc 改名为 xxx.dlc.disabled，引擎
    扫不到就不会加载。启用则是改回来。
    """
    if not game_root:
        return False
    content = _game_content_dir(game_root, game)
    dlc_dir = os.path.join(content, "dlc", dir_name)
    if not os.path.isdir(dlc_dir):
        return False
    try:
        # 找到当前实际的 .dlc 文件（可能是 xxx.dlc 或 xxx.dlc.disabled）
        cur = None
        for fn in os.listdir(dlc_dir):
            low = fn.lower()
            if low.endswith(".dlc") or low.endswith(DISABLED_SUFFIX):
                cur = os.path.join(dlc_dir, fn)
                break
        if cur is None:
            return False
        base = os.path.basename(cur)
        if enabled:
            # 启用：去掉 .disabled 后缀
            if base.lower().endswith(DISABLED_SUFFIX):
                new = os.path.join(dlc_dir, base[: -len(DISABLED_SUFFIX)])
                if not os.path.exists(new):
                    os.rename(cur, new)
                return True
            return True  # 已经是启用状态
        else:
            # 禁用：加 .disabled 后缀
            if base.lower().endswith(DISABLED_SUFFIX):
                return True  # 已经禁用
            new = os.path.join(dlc_dir, base + DISABLED_SUFFIX)
            if not os.path.exists(new):
                os.rename(cur, new)
            return True
    except Exception:
        return False


def dlc_file_disabled(game_root: str, dir_name: str, game: str = "ck3") -> bool:
    """某个 DLC 的 .dlc 文件是否已被改名禁用。"""
    if not game_root:
        return False
    content = _game_content_dir(game_root, game)
    dlc_dir = os.path.join(content, "dlc", dir_name)
    if not os.path.isdir(dlc_dir):
        return False
    try:
        for fn in os.listdir(dlc_dir):
            low = fn.lower()
            if low.endswith(DISABLED_SUFFIX):
                return True
            if low.endswith(".dlc"):
                return False
    except Exception:
        pass
    return False


def detect_dlc_patch(game_root: str, game: str = "ck3") -> dict:
    """检测游戏目录里是否有第三方 DLC 解锁补丁（CreamAPI / Goldberg 等）。

    只读文件系统，不做任何改动。返回：
      {
        "present": bool,          # 是否检测到补丁
        "type": "CreamAPI"|"其他"|"",
        "unlockedCount": int,     # 补丁声明解锁的 DLC 数量
        "proxyDll": "",           # 代理 dll 文件名
        "backupDll": "",          # 原版备份文件名
        "suspicious": bool,       # steam_api64.dll 体积疑似被还原 → 补丁可能失效
        "detail": "",             # 一句话说明
      }
    """
    res = {
        "present": False, "type": "", "unlockedCount": 0,
        "proxyDll": "", "backupDll": "", "suspicious": False, "detail": "",
        "binDir": "",
    }
    if not game_root:
        return res
    bin_dir = _game_bin_dir(game_root, game)
    res["binDir"] = bin_dir
    if not os.path.isdir(bin_dir):
        return res

    # 关键指纹文件
    cream_ini = os.path.join(bin_dir, "cream_api.ini")
    dll64 = os.path.join(bin_dir, "steam_api64.dll")
    dll64_o = os.path.join(bin_dir, "steam_api64_o.dll")

    has_cream = os.path.isfile(cream_ini)
    has_backup64 = os.path.isfile(dll64_o)

    # 体积指纹：原版 steam_api64.dll 约 289KB，CreamAPI 约 732KB
    dll64_size = os.path.getsize(dll64) if os.path.isfile(dll64) else 0

    if not (has_cream or has_backup64):
        return res

    # 补丁类型
    if has_cream:
        res["type"] = "CreamAPI"
        res["proxyDll"] = "steam_api64.dll"
        res["backupDll"] = "steam_api64_o.dll" if has_backup64 else ""
        # 解析 [dlc] 段的 appid 数量
        try:
            txt = open(cream_ini, "r", encoding="utf-8", errors="replace").read()
            seg = re.search(r"\[dlc\]\s*\n(.*?)(?=\n\s*\[|\Z)", txt, re.S)
            if seg:
                dlc_ids = re.findall(r"^\s*(\d{6,8})\s*=", seg.group(1), flags=re.M)
                res["unlockedCount"] = len(dlc_ids)
        except Exception:
            pass
    elif has_backup64:
        res["type"] = "其他（Goldberg 类）"
        res["proxyDll"] = "steam_api64.dll"
        res["backupDll"] = "steam_api64_o.dll"
        res["unlockedCount"] = 0

    res["present"] = True

    # 疑似失效：有补丁指纹，但代理 dll 体积回到了原版量级（Steam 更新还原）
    if res["present"] and 0 < dll64_size < 340000:
        res["suspicious"] = True
        res["detail"] = ("steam_api64.dll 体积疑似被还原为原版，补丁可能已失效，"
                         "建议重新覆盖补丁文件")
    else:
        res["detail"] = "补丁生效中" if res["present"] else ""

    return res


# ---------------------------------------------------------------- 存档

def list_saves(cfg: dict) -> list[dict]:
    user_dir = cfg.get("userDir") or ""
    root = os.path.join(user_dir, "save games")
    game = cfg.get("currentGame") or "ck3"
    ext = GAME_DEFS.get(game, GAME_DEFS["ck3"])["saveExt"].lower()
    out = []
    if not os.path.isdir(root):
        return out
    # 1) 直接放在 save games/ 根目录下的 .ck3 文件（CK3 默认布局，最常见）
    #    旧逻辑只扫子文件夹，导致顶层存档整段被跳过、启动器显示"没有找到存档"。
    try:
        for e in os.scandir(root):
            if not e.is_file() or not e.name.lower().endswith(ext):
                continue
            st = e.stat()
            out.append(
                {
                    "name": os.path.splitext(e.name)[0],
                    "file": e.name,
                    "path": e.path,
                    "group": "",
                    "size": st.st_size,
                    "mtime": int(st.st_mtime),
                }
            )
    except Exception:
        pass
    # 2) 子文件夹（每个子文件夹 = 一个"所在集"，兼容按分组存放的存档）
    for group in sorted(os.listdir(root)):
        gdir = os.path.join(root, group)
        if not os.path.isdir(gdir):
            continue
        try:
            for e in os.scandir(gdir):
                if not e.is_file() or not e.name.lower().endswith(ext):
                    continue
                st = e.stat()
                out.append(
                    {
                        "name": os.path.splitext(e.name)[0],
                        "file": e.name,
                        "path": e.path,
                        "group": group,
                        "size": st.st_size,
                        "mtime": int(st.st_mtime),
                    }
                )
        except Exception:
            continue
    out.sort(key=lambda x: x["mtime"], reverse=True)
    return out


# ---------------------------------------------------------------- Mod 冲突排查

# 单行走本地化键：key 后必须紧跟数字(如 :0)或引号(如 :")，避免误抓语言头/值内冒号
_LOC_RE = re.compile(r"^([^\s:]+)\s*:\s*(?:\d+|\")")

# 高严重度路径前缀：整文件覆盖通常意味着某 mod 的功能被悄悄废掉
_HIGH_IMPACT = (
    "common/defines", "common/landed_titles", "common/cultures",
    "common/religions", "common/scripted_effects", "common/scripted_triggers",
    "common/on_action", "common/decisions", "common/laws", "common/dynasties",
    "common/bookmarks", "common/province_terrain", "common/buildings",
    "common/casus_belli", "common/council_tasks", "common/governments",
    "common/hook_types", "common/interest_groups", "common/maa_types",
    "common/peace_treaties", "common/personal_deities", "common/religion_families",
    "common/religion_doctrines", "common/succession_laws", "common/title_names",
    "common/trade_routes", "common/vanished_counties", "common/war_tenets",
    "common/artifacts", "common/council_positions", "common/diplo_plans",
    "common/diplomatic_actions", "common/dynasty_houses", "common/great_holy_wars",
    "common/holy_order_names", "common/house_unify", "common/innovations",
    "common/maa_templates", "common/named_colors", "common/naming",
    "common/neutrals", "common/perks", "common/pools", "common/regnal_names",
    "common/scheme_types", "common/secrets", "common/society",
    "common/succession_laws", "common/trade_goods", "common/triggered_modifiers",
    "common/war_reasures", "common/age", "common/council_voting",
    "common/factions", "common/holy_sites", "common/province_wars",
    "common/statutes", "common/trade_policies", "common/war",
)

# 扫描时跳过这些版本控制/缓存/临时目录：mod 目录里混入的 .git/.svn/.hg/__pycache__ 等
# 元数据或缓存，原版与非原版都不会加载，当成"文件冲突"纯属误报噪声，必须剔除。
_SKIP_DIRS = {".git", ".svn", ".hg", "__pycache__", ".idea", ".vscode", "node_modules"}

_CONFLICT_CACHE: dict = {}


def detect_mod_conflicts(cfg: dict, psid: str, progress_cb=None) -> dict:
    """排查当前播放集里【已启用】mod 之间的冲突。

    返回四类结果：
      1. fileConflicts  同 relPath 被多个 mod 包含（按加载顺序，最后者获胜）
      2. locConflicts   本地化 .yml 键被多个 mod 定义（软冲突）
      3. missingDeps    mod 声明 dependencies 但依赖项未启用（尽力而为）
      4. replacePaths   mod 用 replace_path 整目录替换原版（风险提示）
    """
    user_dir = cfg.get("userDir") or ""
    if not user_dir:
        return {"ok": False, "error": "未配置用户数据目录"}
    db = LauncherDB(user_dir, cfg.get("gameRoot") or "", _game_key(cfg))
    ps = db.get_playset(psid)
    if not ps:
        return {"ok": False, "error": "找不到该播放集"}

    enabled = [m for m in ps["mods"] if m.get("enabled")]
    enabled.sort(key=lambda m: m.get("position", 0))
    total_mods = len(enabled)
    if progress_cb:
        try:
            progress_cb(0, total_mods)
        except Exception:
            pass

    def mname(m):
        return (m.get("displayName") or m.get("name") or m.get("modFile") or "未命名").strip() or "未命名"

    # 轻量缓存：启用集（modFile:position:dirPath.mtime）变化时失效，TTL 20s
    sig_parts = []
    for m in enabled:
        dp = (m.get("dirPath") or "").strip()
        mt = int(os.path.getmtime(dp)) if dp and os.path.isdir(dp) else 0
        sig_parts.append(f"{m.get('modFile')}:{m.get('position')}:{mt}")
    sig = psid + "|" + "|".join(sig_parts)
    now = time.time()
    cached = _CONFLICT_CACHE.get(sig)
    if cached and now - cached[0] < 20:
        return cached[1]

    filemap: dict = {}          # rel -> [(name, position, modFile)]
    keymap: dict = {}           # loc key -> set(name)
    total_files = 0

    for i, m in enumerate(enabled):
        dp = (m.get("dirPath") or "").strip()
        if not dp or not os.path.isdir(dp):
            if progress_cb:
                try:
                    progress_cb(i + 1, total_mods)
                except Exception:
                    pass
            continue
        nm = mname(m)
        pos = m.get("position", 0)
        mid = m.get("modId")
        try:
            for root, dirs, files in os.walk(dp):
                # 就地剪枝：跳过版本控制/缓存目录，避免把 .git/__pycache__ 等当成冲突误报
                dirs[:] = [d for d in dirs if d not in _SKIP_DIRS]
                for fn in files:
                    full = os.path.join(root, fn)
                    rel = os.path.relpath(full, dp).replace("\\", "/")
                    filemap.setdefault(rel, []).append((nm, pos, mid))
                    total_files += 1
                    if fn.endswith((".yml", ".yaml")):
                        try:
                            txt = open(full, encoding="utf-8-sig", errors="ignore").read()
                        except Exception:
                            continue
                        for line in txt.splitlines():
                            mm = _LOC_RE.match(line.strip())
                            if mm:
                                keymap.setdefault(mm.group(1), set()).add(nm)
        except Exception:
            pass
        if progress_cb:
            try:
                progress_cb(i + 1, total_mods)
            except Exception:
                pass

    def severity(rel):
        _, ext = os.path.splitext(rel)
        if rel.startswith(_HIGH_IMPACT):
            return "高"
        if ext in (".yml", ".yaml", ".png", ".dds", ".jpg", ".jpeg", ".webp", ".tga"):
            return "低"
        return "中"

    file_conflicts = []
    for rel, owners in filemap.items():
        if len(owners) > 1:
            owners_sorted = sorted(owners, key=lambda x: x[1])
            winner = owners_sorted[-1]
            losers = owners_sorted[:-1]
            file_conflicts.append({
                "rel": rel,
                "severity": severity(rel),
                "winner": winner[0],
                "winnerModId": winner[2],
                "losers": [o[0] for o in losers],
                "loserModIds": [o[2] for o in losers],
            })
    file_conflicts.sort(key=lambda c: ({"高": 0, "中": 1, "低": 2}[c["severity"]], c["rel"]))

    loc_conflicts = [{"key": k, "mods": sorted(v)} for k, v in keymap.items() if len(v) > 1]
    loc_conflicts.sort(key=lambda x: x["key"])

    # 描述符级别的依赖 / replace_path 检查
    mod_file_map = _mod_id_to_file_map(db)
    missing_deps = []
    replace_paths = []
    enabled_names = {(x.get("displayName") or x.get("name") or "").strip().lower() for x in enabled}
    enabled_steam = {(x.get("steamId") or "").strip().lower() for x in enabled}
    for m in enabled:
        mf = mod_file_map.get(m["modId"])
        if not mf:
            continue
        dpath = os.path.join(user_dir, mf)
        if not os.path.isfile(dpath):
            continue
        desc = parse_descriptor(dpath)
        deps = desc.get("dependencies")
        if deps:
            if isinstance(deps, str):
                deps = [deps]
            missing = [str(d) for d in deps
                       if str(d).strip().lower() not in enabled_names
                       and str(d).strip().lower() not in enabled_steam]
            if missing:
                missing_deps.append({"mod": mname(m), "deps": missing})
        rp = desc.get("replace_path")
        if rp:
            if isinstance(rp, str):
                rp = [rp]
            replace_paths.append({"mod": mname(m), "paths": [str(x) for x in rp]})

    result = {
        "ok": True,
        "playset": {"id": ps["id"], "name": ps["name"]},
        "scannedMods": len(enabled),
        "totalFiles": total_files,
        "fileConflicts": file_conflicts,
        "locConflicts": loc_conflicts,
        "missingDeps": missing_deps,
        "replacePaths": replace_paths,
    }
    _CONFLICT_CACHE[sig] = (now, result)
    # 别让缓存无限增长
    if len(_CONFLICT_CACHE) > 30:
        _CONFLICT_CACHE.clear()
    return result


# ---------------------------------------------------------------- 游戏启动

def game_exe(cfg: dict) -> str:
    game = cfg.get("currentGame") or "ck3"
    exe = GAME_DEFS.get(game, GAME_DEFS["ck3"])["exe"]
    return os.path.join(cfg.get("gameRoot") or "", *exe)


def game_exe_name(cfg: dict) -> str:
    game = cfg.get("currentGame") or "ck3"
    return GAME_DEFS.get(game, GAME_DEFS["ck3"])["exe"][-1]


def launch_game(cfg: dict, psid: str | None = None, debug: bool = False,
                skip_launcher: bool = True) -> dict:
    """启动游戏。跳过官方启动器，直接把播放集同步进 dlc_load.json 再拉起 exe。"""
    exe = game_exe(cfg)
    exe_name = game_exe_name(cfg)
    if not os.path.isfile(exe):
        return {"ok": False, "error": "找不到 %s，请在设置中重新指定游戏目录" % exe_name}

    db = LauncherDB(cfg.get("userDir") or "", cfg.get("gameRoot") or "", _game_key(cfg))
    target = psid or db.active_playset()
    if target:
        if psid and psid != db.active_playset():
            db.set_active(psid)
        else:
            db.sync_dlc_load(target)

    args = [exe]
    for a in (launcher_settings(cfg).get("exeArgs") or []):
        if a and a not in args:
            args.append(a)
    # 自定义数据目录时，把 -dataPath 传给游戏（用户可在设置里开启）
    if cfg.get("useDataPathArg") and cfg.get("userDir"):
        args.append('-dataPath="%s"' % cfg["userDir"].replace("/", "\\"))
    if debug and "-debug_mode" not in args:
        args.append("-debug_mode")
    try:
        subprocess.Popen(args, cwd=os.path.dirname(exe), close_fds=True)
        return {"ok": True, "args": args}
    except Exception as ex:
        return {"ok": False, "error": str(ex)}


def launch_official_launcher(cfg: dict) -> dict:
    """打开官方启动器。

    首选通过 Steam 拉起（steam://rungameid/<appid>），这样 Steam 会注入运行环境，
    官方启动器（dowser）能正常与 Steam 通讯，不会弹「Steam 运行时通讯错误」。

    不同游戏情况：
      - 有 dowser.exe（CK3/HOI4/EU4/VIC3/Imperator）：Steam 拉起即开官方启动器
      - 老游戏自带内嵌启动器（CK2 的 CK2game.exe、群星）：Steam 拉起即弹启动器界面

    兜底（Steam 没开 / Steam URL 失败时）：直接找 dowser.exe 或游戏 exe 裸启动。
    """
    game = cfg.get("currentGame") or "ck3"
    appid = GAME_DEFS.get(game, GAME_DEFS["ck3"])["appid"]

    # 1) 首选：通过 Steam 拉起（注入正确的 Steam 运行环境）
    try:
        os.startfile(f"steam://rungameid/{appid}")  # noqa: S606
        return {"ok": True, "via": "steam"}
    except Exception:
        pass

    # 2) 兜底：Steam URL 打不开（Steam 没装/没开），直接找 dowser.exe 裸启动
    root = cfg.get("gameRoot") or ""
    cands = [
        os.path.join(root, "launcher", "dowser.exe"),
        os.path.join(root, "launcher", "launcher.exe"),
        os.path.join(root, "dowser.exe"),          # HOI4/EU4 根目录
        os.path.join(root, "launcher.exe"),
    ]
    for rel in cands:
        if os.path.isfile(rel):
            try:
                subprocess.Popen([rel], cwd=os.path.dirname(rel), close_fds=True)
                return {"ok": True, "via": "dowser"}
            except Exception as ex:
                return {"ok": False, "error": str(ex)}

    # 3) 老游戏（CK2/群星）：没有独立 dowser，直接启动游戏 exe（自带启动器界面）
    exe = game_exe(cfg)
    if os.path.isfile(exe):
        try:
            subprocess.Popen([exe], cwd=os.path.dirname(exe), close_fds=True)
            return {"ok": True, "via": "game"}
        except Exception as ex:
            return {"ok": False, "error": str(ex)}

    return {"ok": False, "error": "找不到官方启动器"}


def game_version_of(cfg: dict) -> str:
    """从游戏自带 launcher-settings.json 读取真实版本号。"""
    s = launcher_settings(cfg)
    game = cfg.get("currentGame") or "ck3"
    default = GAME_DEFS.get(game, GAME_DEFS["ck3"])["defaultVersion"]
    return str(s.get("rawVersion") or s.get("version") or cfg.get("gameVersion") or default)


def launcher_settings(cfg: dict) -> dict:
    """读取 <game>/launcher/launcher-settings.json —— 官方版本/启动参数/链接来源。"""
    root = cfg.get("gameRoot") or ""
    for rel in ("launcher/launcher-settings.json", "launcher-settings.json"):
        p = os.path.join(root, rel)
        if os.path.isfile(p):
            try:
                with open(p, "r", encoding="utf-8-sig") as f:
                    return json.load(f) or {}
            except Exception:
                pass
    return {}


# ---------------------------------------------------------------- 内存管理

class _MEMSTATUS(ctypes.Structure):
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


def memory_info() -> dict:
    """物理内存 / 提交上限 / 系统缓存 / 分页文件。"""
    try:
        ms = _MEMSTATUS()
        ms.dwLength = ctypes.sizeof(_MEMSTATUS)
        if not ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(ms)):
            return {"ok": False, "error": "读取内存信息失败"}
        gb = 1024 ** 3
        total_phys = round(ms.ullTotalPhys / gb, 2)
        avail_phys = round(ms.ullAvailPhys / gb, 2)
        total_commit = round(ms.ullTotalPageFile / gb, 2)
        avail_commit = round(ms.ullAvailPageFile / gb, 2)

        cache_mb = 0
        try:
            pi = _PERFINFO()
            pi.cb = ctypes.sizeof(_PERFINFO)
            if ctypes.windll.psapi.GetPerformanceInfo(ctypes.byref(pi), pi.cb):
                cache_mb = round(pi.SystemCache * pi.PageSize / 1048576, 1)
        except Exception:
            pass

        pfs = read_pagefiles()
        return {
            "ok": True,
            "totalPhysGB": total_phys,
            "availPhysGB": avail_phys,
            "usedPhysGB": round(total_phys - avail_phys, 2),
            "totalCommitGB": total_commit,
            "usedCommitGB": round(total_commit - avail_commit, 2),
            "loadPercent": ms.dwMemoryLoad,
            "cacheMB": cache_mb,
            "pagefiles": pfs,
            "pagefileCount": len(pfs),
            "isAdmin": is_admin(),
        }
    except Exception as ex:
        return {"ok": False, "error": str(ex)}


def optimize_memory() -> dict:
    """一键优化，参考主流启动器的做法：

    ① 管理员权限下清空系统待机内存（Standby List）—— 这是能明显腾出内存的一步；
    ② 把所有进程的工作集标记为可换出（SetProcessWorkingSetSize(-1,-1)）；
    ③ 不结束任何进程，随时可重复执行。
    """
    before = memory_info()
    admin = is_admin()
    purged = False
    trimmed = 0

    if admin:
        _enable_privilege("SeProfileSingleProcessPrivilege")
        _enable_privilege("SeIncreaseQuotaPrivilege")
        purged = _purge_standby()

    k32 = ctypes.windll.kernel32
    psapi = ctypes.windll.psapi
    QUERY_LIMITED = 0x1000
    SET_QUOTA = 0x0100
    try:
        arr = (ctypes.c_ulong * 4096)()
        need = ctypes.c_ulong(0)
        if psapi.EnumProcesses(ctypes.byref(arr), ctypes.sizeof(arr), ctypes.byref(need)):
            count = need.value // ctypes.sizeof(ctypes.c_ulong)
            for i in range(count):
                pid = arr[i]
                if not pid:
                    continue
                h = k32.OpenProcess(QUERY_LIMITED | SET_QUOTA, False, pid)
                if not h:
                    continue
                try:
                    if k32.SetProcessWorkingSetSize(h, ctypes.c_size_t(-1), ctypes.c_size_t(-1)):
                        trimmed += 1
                except Exception:
                    pass
                finally:
                    k32.CloseHandle(h)
    except Exception as ex:
        return {"ok": False, "error": str(ex)}

    time.sleep(0.6)
    after = memory_info()
    freed = 0.0
    if before.get("ok") and after.get("ok"):
        freed = round(after["availPhysGB"] - before["availPhysGB"], 2)
        cache_freed = round(before.get("cacheMB", 0) - after.get("cacheMB", 0), 1)
    else:
        cache_freed = 0.0
    return {
        "ok": True,
        "admin": admin,
        "purged": purged,
        "trimmed": trimmed,
        "freedGB": max(0.0, freed),
        "cacheFreedMB": max(0.0, cache_freed),
        "after": after,
    }


# ---------------------------------------------------------------- 内存优化辅助

class _PERFINFO(ctypes.Structure):
    _fields_ = [
        ("cb", ctypes.c_ulong),
        ("CommitTotal", ctypes.c_size_t),
        ("CommitLimit", ctypes.c_size_t),
        ("CommitPeak", ctypes.c_size_t),
        ("PhysicalTotal", ctypes.c_size_t),
        ("PhysicalAvailable", ctypes.c_size_t),
        ("SystemCache", ctypes.c_size_t),
        ("KernelTotal", ctypes.c_size_t),
        ("KernelPaged", ctypes.c_size_t),
        ("KernelNonpaged", ctypes.c_size_t),
        ("PageSize", ctypes.c_size_t),
        ("HandleCount", ctypes.c_ulong),
        ("ProcessCount", ctypes.c_ulong),
        ("ThreadCount", ctypes.c_ulong),
    ]


def read_pagefiles() -> list[dict]:
    """从注册表读分页文件配置（比 WMI 快，且能拿到初始/最大值）。"""
    out = []
    try:
        import winreg
        key = winreg.OpenKey(
            winreg.HKEY_LOCAL_MACHINE,
            r"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
        )
        try:
            val, _ = winreg.QueryValueEx(key, "PagingFiles")
        finally:
            winreg.CloseKey(key)
        items = val if isinstance(val, (list, tuple)) else [val]
        for line in items:
            parts = str(line).split()
            if not parts:
                continue
            path = parts[0]
            init = int(parts[1]) if len(parts) > 1 else 0
            mx = int(parts[2]) if len(parts) > 2 else 0
            out.append({
                "path": path,
                "initialMB": init,
                "maxMB": mx,
                "auto": (init == 0 and mx == 0),
            })
    except Exception:
        pass
    return out


def is_admin() -> bool:
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except Exception:
        return False


def _enable_privilege(name: str) -> bool:
    """启用当前进程的某个特权（清待机内存需要 SeProfileSingleProcessPrivilege）。

    注意：必须显式声明 argtypes/restype。否则在 64 位下 GetCurrentProcess() 返回的
    伪句柄会被截断成 32 位，OpenProcessToken 静默失败，表现为「明明是管理员却没有权限」。
    """
    try:
        from ctypes import wintypes
        advapi = ctypes.windll.advapi32
        k32 = ctypes.windll.kernel32
        TOKEN_ADJUST_PRIVILEGES = 0x0020
        TOKEN_QUERY = 0x0008
        SE_PRIVILEGE_ENABLED = 0x0002

        class LUID(ctypes.Structure):
            _fields_ = [("LowPart", wintypes.DWORD), ("HighPart", wintypes.LONG)]

        class LUID_AND_ATTR(ctypes.Structure):
            _fields_ = [("Luid", LUID), ("Attributes", wintypes.DWORD)]

        class TOKEN_PRIVILEGES(ctypes.Structure):
            _fields_ = [("PrivilegeCount", wintypes.DWORD),
                        ("Privileges", LUID_AND_ATTR * 1)]

        k32.GetCurrentProcess.restype = wintypes.HANDLE
        advapi.OpenProcessToken.argtypes = [
            wintypes.HANDLE, wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE)]
        advapi.OpenProcessToken.restype = wintypes.BOOL
        advapi.LookupPrivilegeValueW.argtypes = [
            wintypes.LPCWSTR, wintypes.LPCWSTR, ctypes.POINTER(LUID)]
        advapi.LookupPrivilegeValueW.restype = wintypes.BOOL
        advapi.AdjustTokenPrivileges.argtypes = [
            wintypes.HANDLE, wintypes.BOOL, ctypes.c_void_p,
            wintypes.DWORD, ctypes.c_void_p, ctypes.c_void_p]
        advapi.AdjustTokenPrivileges.restype = wintypes.BOOL

        h = wintypes.HANDLE()
        if not advapi.OpenProcessToken(k32.GetCurrentProcess(),
                                       TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY,
                                       ctypes.byref(h)):
            return False
        try:
            luid = LUID()
            if not advapi.LookupPrivilegeValueW(None, name, ctypes.byref(luid)):
                return False
            tp = TOKEN_PRIVILEGES()
            tp.PrivilegeCount = 1
            tp.Privileges[0].Luid = luid
            tp.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED
            # 返回 True 即视为成功（部分未分配时 GetLastError 为 1300，可忽略）
            return bool(advapi.AdjustTokenPrivileges(
                h, False, ctypes.byref(tp), 0, None, None))
        finally:
            k32.CloseHandle(h)
    except Exception:
        return False


def _purge_standby() -> bool:
    """清空系统待机内存列表 —— 需要管理员权限 + SeProfileSingleProcessPrivilege。"""
    try:
        ntdll = ctypes.windll.ntdll
        ntdll.NtSetSystemInformation.argtypes = [
            ctypes.c_int, ctypes.c_void_p, ctypes.c_ulong]
        ntdll.NtSetSystemInformation.restype = ctypes.c_long
        # SystemMemoryListInformation = 80, MemoryPurgeStandbyList = 4
        cmd = ctypes.c_int(4)
        status = ntdll.NtSetSystemInformation(80, ctypes.byref(cmd), ctypes.sizeof(cmd))
        return status == 0
    except Exception:
        return False


def restart_as_admin() -> dict:
    """以管理员身份重新启动自己（用户点「以管理员身份优化」时用）。"""
    try:
        if getattr(sys, "frozen", False):
            exe = os.path.abspath(sys.executable)
            args = ""
        else:
            exe = sys.executable
            args = '"%s"' % os.path.join(os.path.dirname(os.path.dirname(
                os.path.abspath(__file__))), "launcher.py")
        r = ctypes.windll.shell32.ShellExecuteW(None, "runas", exe, args or None, None, 1)
        # 返回值 > 32 表示成功
        if r > 32:
            threading.Timer(0.6, lambda: os._exit(0)).start()
            return {"ok": True}
        return {"ok": False, "error": "用户取消了提权（%d）" % r}
    except Exception as ex:
        return {"ok": False, "error": str(ex)}


# ---------------------------------------------------------------- 窗口控制

def find_own_windows() -> list:
    """找出属于本进程的所有可见窗口句柄。"""
    hits = []
    try:
        u = ctypes.windll.user32
        pid = os.getpid()

        def cb(h, _l):
            if not u.IsWindowVisible(h):
                return True
            p = ctypes.c_ulong(0)
            u.GetWindowThreadProcessId(h, ctypes.byref(p))
            if p.value == pid:
                hits.append(h)
            return True

        CB = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
        u.EnumWindows(CB(cb), 0)
    except Exception:
        pass
    return hits


def minimize_windows() -> bool:
    """最小化启动器窗口（启动游戏后用）。"""
    ok = False
    try:
        u = ctypes.windll.user32
        for h in find_own_windows():
            u.ShowWindow(h, 6)   # SW_MINIMIZE
            ok = True
    except Exception:
        pass
    return ok


def open_windows_perf_options() -> bool:
    """打开 Windows「性能选项」对话框 —— 修改虚拟内存 / 页文件的地方。"""
    try:
        subprocess.Popen(["SystemPropertiesPerformance.exe"], close_fds=True)
        return True
    except Exception:
        return False

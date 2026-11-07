# -*- coding: utf-8 -*-
"""
Steam 成就解析
==============
数据来源（全部本地，除全球完成率外不联网）：
  1. `<Steam>/appcache/stats/UserGameStatsSchema_<appid>.bin`
     —— 成就定义（中英文名、描述、图标 hash、所属 DLC 分组）
  2. `<Steam>/appcache/stats/UserGameStats_<steamid>_<appid>.bin`
     —— 本地解锁记录（AchievementTimes：block + index → Unix 时间戳）
  3. Steam 公开接口 GetGlobalAchievementPercentagesForApp
     —— 全球完成率（无需 API Key，失败则回退本地缓存）

注意：这两个 .bin 是 Steam 的二进制 KeyValues（VDF），不是 JSON，
所以下面自带一个最小解析器 —— 格式很固定：
  0x00 object start / 0x08 object end / 0x01 string / 0x02 int32 / 0x07 uint64
"""

from __future__ import annotations

import os
import re
import json
import time
import struct
import threading
import urllib.request

import ck3core as core

_ICON_LOCK = threading.Lock()


# ------------------------------------------------------------------ 二进制 VDF

def parse_vdf_bin(data: bytes) -> dict:
    """解析 Steam 二进制 KeyValues，返回嵌套 dict。解析不出来的部分安静跳过。"""
    pos = 0
    n = len(data)

    def read_str() -> str:
        nonlocal pos
        end = data.find(b"\x00", pos)
        if end < 0:
            raise ValueError("字符串未闭合")
        s = data[pos:end].decode("utf-8", "replace")
        pos = end + 1
        return s

    def read_obj() -> dict:
        nonlocal pos
        out: dict = {}
        while pos < n:
            t = data[pos]
            pos += 1
            if t == 0x08:                      # 块结束
                return out
            if t == 0x00:                      # 子块
                k = read_str()
                out[k] = read_obj()
            elif t == 0x01:                    # 字符串
                k = read_str()
                out[k] = read_str()
            elif t == 0x02:                    # int32
                k = read_str()
                out[k] = struct.unpack("<i", data[pos:pos + 4])[0]
                pos += 4
            elif t == 0x07:                    # uint64
                k = read_str()
                out[k] = struct.unpack("<Q", data[pos:pos + 8])[0]
                pos += 8
            else:                              # 未知类型：停止，返回已解出的部分
                return out
        return out

    try:
        return read_obj()
    except Exception:
        return {}


# ------------------------------------------------------------------ 路径定位

def _stats_dirs() -> list[str]:
    """所有找到的 Steam `<root>/appcache/stats` 目录（大小写不同的同一路径只留一个）。"""
    out: list[str] = []
    seen: set = set()
    try:
        roots = core._steam_roots()
    except Exception:
        roots = []
    for root in roots:
        p = os.path.join(root, "appcache", "stats")
        key = os.path.normcase(os.path.abspath(p))
        if os.path.isdir(p) and key not in seen:
            seen.add(key)
            out.append(p)
    return out


def _schema_path(appid: str) -> str:
    for d in _stats_dirs():
        p = os.path.join(d, "UserGameStatsSchema_%s.bin" % appid)
        if os.path.isfile(p):
            return p
    return ""


def _unlock_paths(appid: str) -> list[str]:
    """所有账号的 UserGameStats_<steamid>_<appid>.bin（按体积从大到小）。"""
    found = []
    for d in _stats_dirs():
        try:
            for fn in os.listdir(d):
                if fn.startswith("UserGameStats_") and fn.endswith("_%s.bin" % appid):
                    p = os.path.join(d, fn)
                    found.append((os.path.getsize(p), p))
        except Exception:
            continue
    found.sort(reverse=True)
    return [p for _, p in found]


# ------------------------------------------------------------------ 成就定义

def _loc(m, lang: str = "schinese") -> str:
    """从 {'english':..,'schinese':..} 这类多语言 map 里取一个值。"""
    if not isinstance(m, dict):
        return ""
    return (m.get(lang) or m.get("english") or "").strip()


def load_definitions(appid: str) -> tuple[list[dict], dict]:
    """返回 (成就定义列表, 分组 {groupid: 名称})。"""
    path = _schema_path(appid)
    if not path:
        return [], {}
    try:
        with open(path, "rb") as f:
            tree = parse_vdf_bin(f.read())
    except Exception:
        return [], {}

    app = tree.get(appid) or {}
    groups: dict[str, str] = {}
    for gid, g in (app.get("groups") or {}).items():
        if isinstance(g, dict):
            nm = g.get("name")
            groups[str(gid)] = _loc(nm) if isinstance(nm, dict) else str(nm or "")

    defs: list[dict] = []
    for block, block_data in (app.get("stats") or {}).items():
        if not isinstance(block_data, dict):
            continue
        if str(block_data.get("type", "")).upper() != "ACHIEVEMENTS":
            continue
        for idx, item in (block_data.get("bits") or {}).items():
            if not isinstance(item, dict):
                continue
            disp = item.get("display") or {}
            name_map = disp.get("name") or {}
            desc_map = disp.get("desc") or {}
            gid = item.get("groupid")
            defs.append({
                "block": str(block),
                "idx": str(idx),
                "id": "%s_%s" % (block, idx),
                "apiName": item.get("name") or "",
                "name": _loc(name_map) or item.get("name") or "",
                "nameEn": _loc(name_map, "english"),
                "desc": _loc(desc_map),
                "descEn": _loc(desc_map, "english"),
                "hidden": bool(disp.get("hidden") or 0),
                "icon": disp.get("icon") or "",
                "iconGray": disp.get("icon_gray") or "",
                "group": groups.get(str(gid), "") if gid is not None else "",
            })
    return defs, groups


def load_unlocks(appid: str) -> tuple[dict, str]:
    """返回 ({(block, idx): unix_ts}, 命中文件路径)。

    多个 Steam 账号各有一份缓存，取「解锁条数最多」的那份。
    """
    best: dict = {}
    best_path = ""
    for p in _unlock_paths(appid):
        try:
            with open(p, "rb") as f:
                tree = parse_vdf_bin(f.read())
        except Exception:
            continue
        cache = tree.get("cache") or {}
        got: dict = {}
        for block, bd in cache.items():
            if not isinstance(bd, dict):
                continue
            for idx, ts in (bd.get("AchievementTimes") or {}).items():
                try:
                    v = int(ts)
                except Exception:
                    continue
                if v > 0:
                    got[(str(block), str(idx))] = v
        if len(got) > len(best):
            best, best_path = got, p
    return best, best_path


# ------------------------------------------------------------------ 全球完成率

_GLOBAL_MEM: dict[str, dict] = {}


def _global_cache_path(appid: str) -> str:
    return os.path.join(core.APP_DIR, "achievement_global_%s.json" % appid)


def load_global_percent(appid: str, max_age: int = 86400) -> dict[str, float]:
    """全球完成率 {apiName: percent}。内存 → 磁盘缓存 → 在线接口。"""
    if appid in _GLOBAL_MEM:
        return _GLOBAL_MEM[appid]

    cache_file = _global_cache_path(appid)
    disk: dict[str, float] = {}
    cache_time = 0
    try:
        with open(cache_file, "r", encoding="utf-8") as f:
            c = json.load(f)
        disk = {str(k): float(v) for k, v in (c.get("data") or {}).items()}
        cache_time = float(c.get("time") or 0)
    except Exception:
        pass

    # 缓存还新鲜就直接用
    if disk and (time.time() - cache_time) < max_age:
        _GLOBAL_MEM[appid] = disk
        return disk

    # 拉线上
    url = ("https://api.steampowered.com/ISteamUserStats/"
           "GetGlobalAchievementPercentagesForApp/v2/?gameid=%s" % appid)
    try:
        req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
        with urllib.request.urlopen(req, timeout=8) as r:
            raw = json.loads(r.read().decode("utf-8"))
        items = ((raw.get("achievementpercentages") or {}).get("achievements")) or []
        online = {str(i.get("name")): float(i.get("percent") or 0) for i in items if i.get("name")}
        if online:
            _GLOBAL_MEM[appid] = online
            try:
                with open(cache_file, "w", encoding="utf-8") as f:
                    json.dump({"time": time.time(), "data": online}, f, ensure_ascii=False)
            except Exception:
                pass
            return online
    except Exception:
        pass

    # 线上失败：用旧缓存（哪怕过期），总比没有强
    _GLOBAL_MEM[appid] = disk
    return disk


# ------------------------------------------------------------------ 汇总

RARE_PERCENT = 5.0


CACHE_VERSION = 2      # 结构变动时 +1，旧缓存自动失效重算


def _data_cache_path(appid: str) -> str:
    return os.path.join(core.APP_DIR, "achievement_data_%s.json" % appid)


def read_data_cache(appid: str) -> dict | None:
    """读本地总缓存（成就定义 + 解锁状态 + 全球完成率）。没有就返回 None。"""
    try:
        with open(_data_cache_path(appid), "r", encoding="utf-8") as f:
            d = json.load(f)
        if (isinstance(d, dict) and d.get("ok") and d.get("items")
                and d.get("v") == CACHE_VERSION):
            return d
    except Exception:
        pass
    return None


def write_data_cache(appid: str, data: dict) -> None:
    try:
        with open(_data_cache_path(appid), "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False)
    except Exception:
        pass


def icon_url(appid: str, h: str) -> str:
    if not h:
        return ""
    return ("https://cdn.cloudflare.steamstatic.com/steamcommunity/"
            "public/images/apps/%s/%s.jpg" % (appid, h))


# ------------------------------------------------------------------ 本地成就图标

# 游戏本体自带全套成就图标（DDS 纹理）：
#   <gameRoot>/game/gfx/interface/icons/achievements/<成就key>.dds         已解锁
#   <gameRoot>/game/gfx/interface/icons/achievements/<成就key>_notachieved.dds  未解锁
# 浏览器显示不了 DDS，所以按需转成 PNG 缓存到启动器目录，之后直接读缓存。

def _icon_cache_dir(appid: str) -> str:
    d = os.path.join(core.APP_DIR, "achievement_icons", appid)
    os.makedirs(d, exist_ok=True)
    return d


# 各游戏成就图标目录相对游戏根目录的位置：
#   CK3 / Vic3 / Imperator: game/gfx/interface/icons/achievements
#   Stellaris:              gfx/interface/icons/achievements
#   HOI4:                   gfx/achievements
#   EU4:                    gfx/interface/achievements
_ICON_REL_DIRS = (
    ("game", "gfx", "interface", "icons", "achievements"),
    ("gfx", "interface", "icons", "achievements"),
    ("gfx", "achievements"),
    ("gfx", "interface", "achievements"),
)


def _dds_dirs(game_root: str) -> list[str]:
    if not game_root:
        return []
    out = []
    for rel in _ICON_REL_DIRS:
        p = os.path.join(game_root, *rel)
        if os.path.isdir(p):
            out.append(p)
    return out


def _dds_dir(game_root: str) -> str:
    """兼容旧调用：返回第一个存在的图标目录。"""
    dirs = _dds_dirs(game_root)
    return dirs[0] if dirs else ""


# 未解锁图标的命名后缀（各游戏不同）：
#   CK3 / Vic3 / Imperator: _notachieved
#   Stellaris:              _locked
#   HOI4:                   _grey / _not_eligible
#   EU4:                    只有彩版，没有灰版
_GRAY_SUFFIXES = ("_notachieved", "_locked", "_grey", "_gray", "_not_eligible")


def _icon_bases(api_name: str) -> list[str]:
    """图标文件名候选：原名、规范化名、去/加前缀名、修正常见拼写错误。"""
    raw = re.sub(r'[\\/:*?"<>|]', "", (api_name or "").strip())
    if not raw:
        return []
    norm = re.sub(r"[^0-9A-Za-z_]", "_", raw)
    out = [raw]
    if norm != raw:
        out.append(norm)
    # Steam 成就表里有拼写错误（achieviement_），游戏文件用正确拼写
    if raw.startswith("achieviement_"):
        out.append("achievement_" + raw[len("achieviement_"):])
    for pre in ("achievement_", "ach_"):
        if raw.startswith(pre) and len(raw) > len(pre):
            out.append(raw[len(pre):])
            if norm.startswith(pre):
                out.append(norm[len(pre):])
        else:
            out.append(pre + raw)
    seen, uniq = set(), []
    for n in out:
        if n and n not in seen:
            seen.add(n)
            uniq.append(n)
    return uniq


def _find_dds(game_root: str, api_name: str, gray: bool = False) -> str:
    """在游戏目录里找成就 DDS：依次尝试各目录 / 文件名 / 解锁状态后缀。"""
    bases = _icon_bases(api_name)
    if not bases:
        return ""
    suffixes = list(_GRAY_SUFFIXES) if gray else [""]
    for d in _dds_dirs(game_root):
        for base in bases:
            for suf in suffixes:
                p = os.path.join(d, base + suf + ".dds")
                if os.path.isfile(p):
                    return p
    return ""


# 个别游戏的部分成就（多为未安装 DLC）没有单独图标文件，
# 用游戏自带的成就奖杯图作占位，避免只剩“名字首字”。
_GENERIC_ICONS = {
    "281990": ("gfx", "interface", "buttons", "main_menu_achievements.dds"),  # Stellaris
}


def _generic_dds(appid: str, game_root: str) -> str:
    rel = _GENERIC_ICONS.get(appid)
    if not rel or not game_root:
        return ""
    p = os.path.join(game_root, *rel)
    return p if os.path.isfile(p) else ""


def local_icon_png(appid: str, game_root: str, api_name: str, gray: bool = False) -> str:
    """返回本地 PNG 图标路径（必要时从游戏 DDS 转一次并存缓存）。找不到返回空串。"""
    name = re.sub(r"[^0-9A-Za-z_]", "_", api_name or "")
    if not name:
        return ""
    cache_dir = _icon_cache_dir(appid)
    out = os.path.join(cache_dir, name + ("_gray" if gray else "") + ".png")
    if os.path.isfile(out) and os.path.getsize(out) > 0:
        return out

    src = _find_dds(game_root, api_name, gray)
    if not src and gray:
        src = _find_dds(game_root, api_name, False)   # 没有灰版就退回彩版
    if not src:
        src = _generic_dds(appid, game_root)          # 再退回通用成就图标
    if not src:
        return ""
    # 串行转换：Pillow 解码是 CPU 密集，几十个请求并发抢 CPU 反而更慢
    with _ICON_LOCK:
        if os.path.isfile(out) and os.path.getsize(out) > 0:
            return out
        try:
            from PIL import Image          # 延迟导入：没装也不影响其它功能
            im = Image.open(src)
            im = im.convert("RGBA")
            if max(im.size) > 128:
                im.thumbnail((128, 128), Image.LANCZOS)
            im.save(out, "PNG", optimize=True)
            return out
        except Exception:
            return ""


def icon_http_url(api_name: str, gray: bool = False) -> str:
    """前端 <img src> 用：走后端接口，按需转换并返回 PNG。"""
    if not api_name:
        return ""
    from urllib.parse import quote
    return "/api/ach-icon?n=%s&g=%d" % (quote(api_name), 1 if gray else 0)


def collect(appid: str, with_global: bool = True, force: bool = False,
            game_root: str = "") -> dict:
    """组装成前端要的结构。

    默认优先返回本地缓存（读一次 Steam 之后就存下来，秒开）；
    force=True 时忽略缓存，重新读 Steam 缓存文件并重新拉全球完成率。
    """
    if not force:
        cached = read_data_cache(appid)
        if cached:
            cached["fromCache"] = True
            return cached

    if force:
        _GLOBAL_MEM.pop(appid, None)
        try:
            os.remove(_global_cache_path(appid))
        except Exception:
            pass

    defs, groups = load_definitions(appid)
    if not defs:
        return {
            "ok": False,
            "error": "没找到 Steam 成就数据。请先在 Steam 里运行过一次游戏，"
                     "且 Steam 安装目录下存在 appcache/stats 缓存。",
        }

    unlocks, unlock_src = load_unlocks(appid)
    percents = load_global_percent(appid) if with_global else {}

    items = []
    unlocked_count = 0
    rare_total = 0
    rare_unlocked = 0
    for d in defs:
        key = (d["block"], d["idx"])
        ts = unlocks.get(key, 0)
        pct = percents.get(d["apiName"]) if d["apiName"] else None
        is_rare = (pct is not None and pct < RARE_PERCENT)
        if ts:
            unlocked_count += 1
            if is_rare:
                rare_unlocked += 1
        if is_rare:
            rare_total += 1
        items.append({
            "id": d["id"],
            "apiName": d["apiName"],
            "name": d["name"] or d["nameEn"] or d["apiName"],
            "nameEn": d["nameEn"],
            "desc": d["desc"] or d["descEn"],
            "hidden": d["hidden"],
            "group": d["group"],
            "icon": icon_url(appid, d["icon"]),
            "iconGray": icon_url(appid, d["iconGray"] or d["icon"]),
            # 游戏本体自带的图标（不依赖 Steam CDN，国内也能显示）
            "iconLocal": icon_http_url(d["apiName"], False),
            "iconGrayLocal": icon_http_url(d["apiName"], True),
            "unlocked": bool(ts),
            "time": ts,
            "timeText": time.strftime("%Y-%m-%d", time.localtime(ts)) if ts else "",
            "percent": round(pct, 1) if pct is not None else None,
            "rare": is_rare,
        })

    # 解锁的排前面、时间新的靠上；未解锁的按全球完成率从高到低（更容易的先看到）
    items.sort(key=lambda x: (0 if x["unlocked"] else 1,
                              -(x["time"] or 0) if x["unlocked"] else (x["percent"] or 0)))

    groups_used = sorted({i["group"] for i in items if i["group"]})
    result = {
        "ok": True,
        "v": CACHE_VERSION,
        "appid": appid,
        "total": len(items),
        "unlocked": unlocked_count,
        "rareTotal": rare_total,
        "rareUnlocked": rare_unlocked,
        "hasGlobal": bool(percents),
        "groups": groups_used,
        "items": items,
        "unlockSource": os.path.basename(unlock_src) if unlock_src else "",
        "cachedAt": int(time.time()),
        "fromCache": False,
    }
    write_data_cache(appid, {k: v for k, v in result.items() if k != "fromCache"})
    return result

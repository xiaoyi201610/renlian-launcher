# -*- coding: utf-8 -*-
"""
CK3 Launcher - HTTP API 服务
============================
仅绑定 127.0.0.1，供本地窗口（pywebview / Edge app 模式）调用。
所有写操作都经过 core 层，自带备份。
"""

from __future__ import annotations

import os
import io
import json
import time
import shutil
import mimetypes
import threading
import traceback
import urllib.request
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler
from urllib.parse import urlparse, parse_qs, unquote, quote

import ck3core as core
import achievements
import diagnose
import steamdlc
import steambeta
import wiki
import windlg

APP_DIR = core.APP_DIR
STATIC_DIR = os.path.join(core.RES_DIR, "static")
WORKSHOP_APP = core.STEAM_APPID


def _cur_appid() -> str:
    """当前游戏的 Steam AppID（工坊/商店 URL 用）。"""
    return core.GAME_DEFS.get(CTX.game, core.GAME_DEFS["ck3"])["appid"]


class Ctx:
    """全局上下文（配置 + 缓存）。"""

    def __init__(self):
        # 初始加载：自动定位一个「已安装」的游戏（当前游戏没装则回退到第一个已装的），
        # 再取该游戏的合并视图。这样首次打开、玩家没装 CK3 只装了别的游戏时，也能开箱即用。
        startup = core.resolve_startup_game()
        self.cfg = core.load_config(startup)
        self.lock = threading.Lock()
        self.mod_index: list[dict] = []
        self.mod_by_file: dict[str, dict] = {}
        self.index_time = 0.0

    @property
    def game(self) -> str:
        return core._game_key(self.cfg)

    def reload_cfg(self):
        self.cfg = core.load_config(self.game)

    def switch_game(self, key: str):
        """切换当前游戏，重载该游戏配置并清空 mod 缓存。"""
        with self.lock:
            self.cfg = core.set_current_game(key)
            self.mod_index = []
            self.mod_by_file = {}

    def mods(self, force: bool = False) -> list[dict]:
        with self.lock:
            if force or not self.mod_index:
                self.mod_index = core.build_mod_index(self.cfg, force=force)
                self.mod_by_file = {m["modFile"].lower(): m for m in self.mod_index}
                self.index_time = time.time()
            return self.mod_index

    def db(self) -> core.LauncherDB:
        return core.LauncherDB(self.cfg.get("userDir") or "",
                               self.cfg.get("gameRoot") or "",
                               self.game)


CTX = Ctx()


# ---------------------------------------------------------------- 业务封装

def api_consistency() -> dict:
    """对比「当前播放集启用列表」与「游戏实际读取的 dlc_load.json」。"""
    db = CTX.db()
    psid = db.active_playset()
    if not psid:
        return {"ok": True, "psid": None, "extra": [], "missing": [], "orderDiff": False}
    ps = db.get_playset(psid)
    if not ps:
        return {"ok": True, "psid": None, "extra": [], "missing": [], "orderDiff": False}
    id2f = core._mod_id_to_file_map(db)
    CTX.mods()
    want, seen = [], set()
    for m in ps["mods"]:
        if not m["enabled"]:
            continue
        f = id2f.get(m["modId"])
        if f and f.lower() not in seen:
            seen.add(f.lower())
            want.append(f)
    have = db.read_dlc_load().get("enabled_mods") or []
    want_set, have_set = set(want), set(have)

    def label(f):
        sid = os.path.basename(f).replace("ugc_", "").replace(".mod", "")
        meta = CTX.mod_by_file.get((f or "").lower()) or {}
        return {"file": f, "name": meta.get("name") or sid, "steamId": sid}

    extra = [label(x) for x in have if x not in want_set]
    missing = [label(x) for x in want if x not in have_set]
    order_diff = (not extra and not missing and want != have)
    return {
        "ok": True,
        "psid": psid,
        "playsetName": ps["name"],
        "extra": extra,          # 游戏会加载，但播放集里是禁用的
        "missing": missing,      # 播放集启用了，但游戏不会加载
        "orderDiff": order_diff, # 集合一致但顺序不同
        "wantCount": len(want),
        "haveCount": len(have),
    }


def api_gamedata() -> dict:
    """「游戏文件」页需要的汇总信息。"""
    cfg = CTX.cfg
    mods = CTX.mods()
    saves = core.list_saves(cfg)
    settings = core.launcher_settings(cfg)
    default_user = core.detect_user_dir(CTX.game) or ""
    cur = cfg.get("userDir") or ""
    return {
        "ok": True,
        "gameRoot": cfg.get("gameRoot") or "",
        "exe": core.game_exe(cfg),
        "exeExists": os.path.isfile(core.game_exe(cfg)),
        "version": core.game_version_of(cfg),
        "versionFull": settings.get("version") or "",
        "userDir": cur,
        "defaultUserDir": default_user,
        "isDefaultUserDir": bool(default_user) and os.path.normcase(cur) == os.path.normcase(default_user),
        "workshopDir": cfg.get("workshopDir") or "",
        "savesDir": os.path.join(cur, "save games"),
        "modDir": os.path.join(cur, "mod"),
        "modCount": len(mods),
        "localModCount": sum(1 for m in mods if m.get("source") == "local"),
        "steamModCount": sum(1 for m in mods if m.get("source") == "steam"),
        "saveCount": len(saves),
        "useDataPathArg": bool(cfg.get("useDataPathArg")),
    }


def api_state() -> dict:
    cfg = CTX.cfg
    db = CTX.db()
    playsets = db.list_playsets()
    active = db.active_playset()
    dl = db.read_dlc_load()
    ver = core.game_version_of(cfg)
    settings = core.launcher_settings(cfg)
    cur = core._game_key(cfg)
    games = core.detect_installed_games()
    for g in games:
        g["cover"] = core.game_cover(g["key"])            # 小横条封面
        g["coverHeader"] = core.game_cover(g["key"], "lg")  # 大横版封面
    return {
        "config": cfg,
        "currentGame": cur,
        "games": games,
        "gameDefs": {k: {"name": v["name"], "en": v["en"]}
                     for k, v in core.GAME_DEFS.items()},
        "version": ver,
        "versionFull": settings.get("version") or ver,
        "gameRoot": cfg.get("gameRoot"),
        "userDir": cfg.get("userDir"),
        "workshopDir": cfg.get("workshopDir"),
        "exeExists": os.path.isfile(core.game_exe(cfg)),
        "dbExists": db.exists(),
        "dbLocked": db.is_locked(),
        "playsets": playsets,
        "activePlayset": active,
        "dlcLoad": {
            "enabledCount": len(dl.get("enabled_mods") or []),
            "disabledDlcCount": len(dl.get("disabled_dlcs") or []),
        },
        "browserModUrl": settings.get("browserModUrl") or f"https://steamcommunity.com/app/{_cur_appid()}/workshop/",
        "browserDlcUrl": settings.get("browserDlcUrl") or f"https://store.steampowered.com/dlc/{_cur_appid()}/",
        "backupDir": core.BACKUP_DIR,
    }


def api_playset_detail(psid: str) -> dict:
    db = CTX.db()
    CTX.mods()
    ps = db.get_playset(psid)
    if not ps:
        return {"ok": False, "error": "播放集不存在"}
    id2file = core._mod_id_to_file_map(db)
    items = []
    for m in ps["mods"]:
        mid = m["modId"]
        f = id2file.get(mid)
        meta = CTX.mod_by_file.get((f or "").lower()) or {}
        items.append(
            {
                "uid": mid,                    # 稳定身份，前端拖拽与保存都用它
                "modId": mid,
                "modFile": f,
                "enabled": bool(m["enabled"]),
                "position": m["position"],
                "known": bool(meta),
                "name": meta.get("name") or m.get("displayName") or m.get("name") or "(未注册的模组)",
                "thumb": meta.get("thumb") or "",
                "steamId": meta.get("steamId") or m.get("steamId"),
                "supportedVersion": meta.get("supportedVersion") or "",
                "compat": meta.get("compat") or "warn",
                "source": meta.get("source") or m.get("source") or "",
            }
        )
    return {
        "ok": True,
        "id": ps["id"],
        "name": ps["name"],
        "isActive": ps["isActive"],
        "items": items,
        "dlcs": ps["dlcs"],
    }


def _require_user_dir() -> str:
    """返回空串表示可用；否则返回给前端显示的友好错误。

    未安装 / 从没跑过官方启动器的游戏，userDir 可能是空的。此时所有播放集
    写操作都会退化成相对路径（把数据写进启动器安装目录），DB 层会直接抛
    RuntimeError → 后端 500 → 前端显示红字或「点了没反应」。
    """
    ud = (CTX.cfg.get("userDir") or "").strip()
    if not ud or not os.path.isdir(ud):
        return "找不到该游戏的用户数据目录，请在「启动器设置」里手动指定"
    return ""


def _playset_op(fn, what: str) -> dict:
    """播放集写操作统一兜底：目录校验 + 异常转友好错误。"""
    err = _require_user_dir()
    if err:
        return {"ok": False, "error": err}
    try:
        return fn()
    except Exception as ex:
        return {"ok": False, "error": "%s失败：%s" % (what, ex)}


def api_create_playset(body: dict) -> dict:
    def _do():
        psid = CTX.db().create_playset(body.get("name") or "新播放集",
                                       body.get("cloneFrom") or None)
        return {"ok": True, "id": psid}
    return _playset_op(_do, "新建播放集")


def api_rename_playset(body: dict) -> dict:
    def _do():
        CTX.db().rename_playset(body.get("id"), body.get("name") or "未命名")
        return {"ok": True}
    return _playset_op(_do, "重命名播放集")


def api_delete_playset(body: dict) -> dict:
    def _do():
        CTX.db().delete_playset(body.get("id"))
        return {"ok": True}
    return _playset_op(_do, "删除播放集")


def api_activate_playset(body: dict) -> dict:
    def _do():
        CTX.db().set_active(body.get("id"))
        return {"ok": True}
    return _playset_op(_do, "切换播放集")


def api_sync_playset(body: dict) -> dict:
    def _do():
        db = CTX.db()
        psid = body.get("id") or db.active_playset()
        r = db.sync_dlc_load(psid)
        return {"ok": r is not None, "backup": os.path.basename(r or "")}
    return _playset_op(_do, "同步播放集")


def api_save_playset(payload: dict) -> dict:
    psid = payload.get("id")
    raw = payload.get("items") or []
    if not psid:
        return {"ok": False, "error": "缺少播放集 id"}
    err = _require_user_dir()
    if err:
        return {"ok": False, "error": err}
    db = CTX.db()
    if not db.exists():
        return {"ok": False, "error": "找不到启动器数据库（%s）" % os.path.basename(db.path)}
    if db.is_locked():
        return {"ok": False, "error": "官方启动器正在运行，请先关闭它再保存"}

    CTX.mods()
    write_items = []
    auto_registered = []
    for it in raw:
        mid = (it.get("modId") or "").strip()
        mf = (it.get("modFile") or "").strip()
        if not mid and not mf:
            continue
        if mid:
            # 存量修复：旧版本可能已把本地 mod 存成了工坊记录（播放集里显示工坊版）。
            # 只要这个 modFile 在索引里判定为本地，就按目录重新绑定身份，纠正脏数据。
            mf_meta = {}
            if mf:
                mf_meta = CTX.mod_by_file.get(mf.lower()) or {}
            if mf and mf_meta.get("source") == "local":
                write_items.append({"rec": _rec_of(mf),
                                    "enabled": bool(it.get("enabled", True)), "modId": None})
                continue
            # 已有记录：直接用原 modId，绝不做二次推断，避免身份错位
            write_items.append({"modId": mid, "enabled": bool(it.get("enabled", True)), "rec": None})
            continue
        meta = CTX.mod_by_file.get(mf.lower()) or {}
        if meta.get("pending") and meta.get("steamId"):
            # 自动补齐 .mod 描述文件只是附带的便利功能；失败（工坊文件缺失、
            # 目录没权限等）绝不能连累播放集保存主流程。
            try:
                r = core.register_mod_descriptor(CTX.cfg, meta["steamId"])
                if r.get("ok"):
                    auto_registered.append(meta["steamId"])
                    CTX.mods(force=True)
                    meta = CTX.mod_by_file.get(mf.lower(), meta)
            except Exception:
                pass
        rec = _rec_of(mf)
        write_items.append({"rec": rec, "enabled": bool(it.get("enabled", True)), "modId": None})

    if payload.get("setActive"):
        try:
            with db.connect() as c:
                c.execute("UPDATE playsets SET isActive = 0 WHERE isActive = 1")
                c.execute("UPDATE playsets SET isActive = 1 WHERE id = ?", (psid,))
        except Exception:
            pass

    try:
        res = db.write_playset(psid, write_items, payload.get("dlcs"))
    except Exception as ex:
        return {"ok": False, "error": "写入播放集失败：%s" % ex}
    return {
        "ok": True,
        "saved": len(write_items),
        "synced": res.get("synced", False),
        "autoRegistered": auto_registered,
        "backup": os.path.basename(res.get("sqliteBackup") or ""),
    }


def api_export_playset(psid: str) -> dict:
    d = api_playset_detail(psid)
    if not d.get("ok"):
        return d
    db = CTX.db()
    ps = db.get_playset(psid)
    return {
        "ok": True,
        "format": "ck3launcher-playset",
        "formatVersion": 1,
        "exportedAt": time.strftime("%Y-%m-%d %H:%M:%S"),
        "gameVersion": core.game_version_of(CTX.cfg),
        "name": d["name"],
        "dlcs": ps["dlcs"] if ps else {},
        "mods": [
            {
                "steamId": i["steamId"],
                "name": i["name"],
                "modFile": i["modFile"],
                "supportedVersion": i["supportedVersion"],
                "enabled": i["enabled"],
                "position": idx,
            }
            for idx, i in enumerate(d["items"])
        ],
    }


def _mod_installed(meta: dict) -> bool:
    """模组文件是否真的在本地：dirPath 指向的目录（或压缩包）必须存在。

    不能只看索引里有没有这条：用户手动删掉工坊目录后，mod/ugc_*.mod 描述文件
    还在，索引照样把它列成 source=steam，但它其实已经没装了。
    """
    if not meta:
        return False
    dp = (meta.get("dirPath") or "").strip()
    if dp:
        return os.path.isdir(dp) or os.path.isfile(dp)
    return bool(meta.get("valid"))


def api_import_playset(payload: dict) -> dict:
    err = _require_user_dir()
    if err:
        return {"ok": False, "error": err}
    data = payload.get("data") or {}
    name = payload.get("name") or data.get("name") or "导入的播放集"
    mods = data.get("mods") or []
    if not mods:
        return {"ok": False, "error": "播放集文件里没有 mod 条目"}

    # 强制重建索引：导入前用户可能刚删过工坊目录，缓存里的 valid/source 会是旧值
    CTX.mods(force=True)
    by_steam = {m["steamId"]: m for m in CTX.mod_index if m.get("steamId")}
    by_file = CTX.mod_by_file
    by_name = {m["name"]: m for m in CTX.mod_index}

    seen: set = set()
    ordered: list[dict] = []
    missing: list[str] = []
    uninstalled: list[str] = []
    missing_steam_ids: list[str] = []
    for i, m in enumerate(sorted(mods, key=lambda x: x.get("position", 0))):
        hit = None
        sid = str(m.get("steamId") or "").strip()
        # 优先按 modFile 精确匹配（唯一标识，避免同一 steamId 的本地/工坊双版本撞车）
        mf = (m.get("modFile") or "").strip().lower()
        if mf:
            hit = by_file.get(mf)
        # 其次按 steamId 匹配
        if hit is None:
            if sid and sid in by_steam:
                hit = by_steam[sid]
        # 最后按名称兜底
        if hit is None:
            hit = by_name.get(m.get("name") or "")
        if hit is None:
            missing.append(m.get("name") or sid or "?")
            # 本地没有、但带工坊 ID → 记下来，导入后自动去 Steam 订阅
            if sid.isdigit():
                missing_steam_ids.append(sid)
            continue
        key = hit["modFile"].lower()
        if key in seen:
            continue
        seen.add(key)

        # 索引里有记录，但实际文件/工坊目录已经不存在 → 也要订阅
        if not _mod_installed(hit):
            uninstalled.append(hit.get("name") or m.get("name") or sid or "?")
            hid = str(hit.get("steamId") or sid or "").strip()
            if hid.isdigit():
                missing_steam_ids.append(hid)

        # 仍然写入播放集：.mod 描述文件还在，Steam 下载完成后同路径即可直接启用
        ordered.append({"modFile": hit["modFile"], "enabled": bool(m.get("enabled", True))})

    db = CTX.db()
    try:
        new_id = db.create_playset(name)
    except Exception as ex:
        return {"ok": False, "error": "导入播放集失败：%s" % ex}
    write_items = []
    for it in ordered:
        meta = CTX.mod_by_file.get(it["modFile"].lower()) or {}
        rec = {
            "steamId": meta.get("steamId"),
            "name": meta.get("name"),
            "displayName": meta.get("name"),
            "version": meta.get("version") or "",
            "dirPath": meta.get("dirPath") or "",
            "source": meta.get("source") or "local",
        }
        write_items.append({"rec": rec, "enabled": it["enabled"]})
    try:
        db.write_playset(new_id, write_items, data.get("dlcs"))
    except Exception as ex:
        return {"ok": False, "error": "导入播放集失败：%s" % ex}

    # 缺失且带工坊 ID 的 mod：直接让 Steam 订阅（下载是异步的，导入时还不存在文件）
    missing_steam_ids = list(dict.fromkeys(missing_steam_ids))
    sub = {"asked": len(missing_steam_ids), "subscribed": 0, "failed": [], "error": ""}
    if missing_steam_ids:
        try:
            r = steamdlc.subscribe(missing_steam_ids, CTX.cfg.get("gameRoot") or "",
                                   _cur_appid())
            sub["subscribed"] = len(r.get("subscribed") or [])
            sub["failed"] = r.get("failed") or []
            if not (r.get("ok") or sub["subscribed"]):
                sub["error"] = r.get("msg") or "自动订阅失败"
        except Exception as ex:
            sub["error"] = "自动订阅失败：%s" % ex

    return {
        "ok": True,
        "id": new_id,
        "name": name,
        "imported": len(ordered),
        "missing": missing,
        "uninstalled": uninstalled,
        "subscribe": sub,
    }


_dlc_base_mem: dict = {}
# 缓存结构版本：字段含义变了就 +1，旧缓存自动失效重算
# v2：items 增加 nameEn，nameCn 改为真正的中文名（v1 存的 nameCn 是英文原文）
# v3：中文名覆盖全部游戏（dlc_cn.py），旧缓存里的英文 nameCn 必须重算
_DLC_CACHE_VER = 3


def _dlc_cache_path(game: str) -> str:
    return os.path.join(APP_DIR, "dlc_data_%s.json" % game)


def _dlc_names_path() -> str:
    return os.path.join(APP_DIR, "dlc-names-cn.json")


def _dlc_names_mtime() -> float:
    """玩家本地 DLC 中文名覆盖表的修改时间（没有文件返回 0）。

    缓存里存一份，读缓存时对比：文件改过就让缓存作废，玩家改完名字
    不用点「重新校验」也能立刻看到效果。
    """
    try:
        p = _dlc_names_path()
        return os.path.getmtime(p) if os.path.isfile(p) else 0.0
    except Exception:
        return 0.0


def _dlc_base(game: str, force: bool = False) -> tuple[dict, bool]:
    """DLC 的「静态部分」——清单 / 图标 / 介绍 / 文件状态 / Steam 授权判定。

    这部分最重（要扫目录、还要问 Steam 是否已购买），所以缓存在本地：
      · 默认直接读缓存（秒开，完全不碰 Steam）
      · force=True（玩家点「重新校验」）才重新扫 + 重新问 Steam，并覆盖缓存
      · 玩家改过 dlc-names-cn.json（本地中文名覆盖表）也会自动作废缓存
    返回 (base, from_cache)。
    """
    names_mtime = _dlc_names_mtime()
    if force:
        _dlc_base_mem.pop(game, None)
        try:
            os.remove(_dlc_cache_path(game))
        except Exception:
            pass
    elif game in _dlc_base_mem:
        if _dlc_base_mem[game].get("namesMtime", 0) == names_mtime:
            return _dlc_base_mem[game], True
        _dlc_base_mem.pop(game, None)      # 名字表改过，内存缓存作废
    else:
        try:
            with open(_dlc_cache_path(game), "r", encoding="utf-8") as f:
                cached = json.load(f)
            if isinstance(cached, dict) and cached.get("items") \
                    and cached.get("v") == _DLC_CACHE_VER \
                    and cached.get("namesMtime", 0) == names_mtime:
                _dlc_base_mem[game] = cached
                return cached, True
        except Exception:
            pass

    items = core.list_dlcs(CTX.cfg)

    # ① 先检测解锁补丁。命中就直接按「全可用」处理，不再调 Steam
    #    （补丁会把 steam_api64.dll 换成代理，调了会崩或返回假值）。
    patch = core.detect_dlc_patch(CTX.cfg.get("gameRoot") or "", game)

    # ② 补丁没命中才问 Steam（和 Steam 库、游戏内锁定同源）。
    #    查不到就标「未知」，绝不默认「拥有」。
    game_appid = core.GAME_DEFS.get(game, core.GAME_DEFS["ck3"])["appid"]
    if patch["present"]:
        sr = {"ok": True, "msg": "", "owned": {}}
        for x in items:
            sid = x.get("steamId") or ""
            if sid.isdigit():
                sr["owned"][int(sid)] = True
    else:
        sids = [x["steamId"] for x in items if x.get("steamId")]
        sr = steamdlc.query(sids, CTX.cfg.get("gameRoot") or "", game_appid)

    for d in items:
        d["fileDisabled"] = bool(d.get("fileDisabled", False))
        sid = d.get("steamId") or ""
        # owned 判定（三级）：
        #  ① 补丁命中 → 全 owned（补丁解锁）
        #  ② Steam 查询 ok → 用真实授权值（steam/none）
        #  ③ Steam 查询失败（老游戏 SDK 无法校验）→ 回退本地文件判断：
        #     - 本地 dlc 目录里有 .dlc 文件（installed）→ 视为「文件已存在」，owned=True
        #     - 本地也没有 → ownedKnown=False，显示「无法校验」
        if patch["present"]:
            d["owned"] = True
            d["ownedKnown"] = True
        elif sr.get("ok") and sid.isdigit():
            d["owned"] = bool(sr["owned"].get(int(sid), False))
            d["ownedKnown"] = True
        elif d.get("installed"):
            d["owned"] = True
            d["ownedKnown"] = False   # 非 Steam 确认，来源记为 file
        else:
            d["owned"] = False
            d["ownedKnown"] = False
        # 来源：补丁解锁 / 原生拥有 / 文件已存在 / 未拥有 / 未知
        if patch["present"]:
            d["ownedSource"] = "patch"
        elif sr.get("ok") and sid.isdigit():
            d["ownedSource"] = "steam" if d["owned"] else "none"
        elif d.get("installed"):
            d["ownedSource"] = "file"
        else:
            d["ownedSource"] = "unknown"

    base = {
        "v": _DLC_CACHE_VER,
        "namesMtime": names_mtime,
        "items": items,
        "patch": patch,
        "steamOk": bool(sr.get("ok")),
        "steamMsg": sr.get("msg") or "",
        "cachedAt": int(time.time()),
    }
    _dlc_base_mem[game] = base
    try:
        with open(_dlc_cache_path(game), "w", encoding="utf-8") as f:
            json.dump(base, f, ensure_ascii=False)
    except Exception:
        pass
    return base, False


def api_dlcs(psid: str | None, force: bool = False, pull_steam: bool = False) -> dict:
    db = CTX.db()
    target = psid or db.active_playset()
    state = db.dlc_enabled_map(target)
    dl = db.read_dlc_load()
    disabled = set(dl.get("disabled_dlcs") or [])

    # 「重新校验」= 重扫本地文件 + 重新问 Steam 是否拥有（快，几秒内）
    # 更新 DLC 名称/图标/日期（要连 Steam 商店，弱网下可能 20 秒）单独用 ?steam=1 触发，
    # 不跟「重新校验」捆在一起，免得点一下转半天。
    refresh_info = None
    if pull_steam:
        refresh_info = core.refresh_dlc_from_steam(CTX.game)

    base, from_cache = _dlc_base(CTX.game, force)
    # 深拷贝：下面的 enabled 计算会改字段，不能污染缓存的原始对象
    items = [dict(x) for x in (base.get("items") or [])]
    patch = base.get("patch") or {"present": False}
    sr = {"ok": base.get("steamOk"), "msg": base.get("steamMsg")}

    for d in items:
        if state:
            d["enabled"] = bool(state.get(d["popsId"], False))
        else:
            d["enabled"] = d["popsId"] not in disabled
        # 真正生效状态以 .dlc 文件为准（文件被改名禁用 = 未启用）
        d["fileDisabled"] = bool(d.get("fileDisabled", False))
        if d["fileDisabled"]:
            d["enabled"] = False
        # owned / ownedKnown / ownedSource 已在上面的 _dlc_base 里判好（可缓存的部分）
        if not d["owned"]:
            d["enabled"] = False

    return {
        "ok": True,
        "playset": target,
        "items": items,
        "patch": patch,
        "steamOk": bool(sr.get("ok")),
        "steamMsg": sr.get("msg") or "",
        "cachedAt": base.get("cachedAt") or 0,
        "fromCache": bool(from_cache),
        "ownedCount": sum(1 for x in items if x["owned"]),
        "enabledCount": sum(1 for x in items if x["owned"] and x["enabled"]),
        # 分来源统计（前端展示「原生拥有 / 补丁解锁 / 文件已存在 / 无法校验」数量）
        "bySource": {
            "steam": sum(1 for x in items if x.get("ownedSource") == "steam"),
            "patch": sum(1 for x in items if x.get("ownedSource") == "patch"),
            "file": sum(1 for x in items if x.get("ownedSource") == "file"),
            "unknown": sum(1 for x in items if x.get("ownedSource") == "unknown"),
            "none": sum(1 for x in items if x.get("ownedSource") == "none"),
        },
        "refresh": refresh_info,
    }


# ---- DLC 商店详情（实时从 Steam API 拉取，带缓存）----
_dlc_detail_cache: dict = {}
_dlc_detail_loaded = False


def _detail_cache_path() -> str:
    return os.path.join(APP_DIR, "dlc_detail_cache.json")


def _load_detail_cache() -> None:
    """从磁盘恢复 DLC 详情缓存（7 天内的）。重启后不用重新拉 Steam。"""
    global _dlc_detail_loaded
    if _dlc_detail_loaded:
        return
    _dlc_detail_loaded = True
    try:
        with open(_detail_cache_path(), "r", encoding="utf-8") as f:
            disk = json.load(f)
        now = time.time()
        for k, v in (disk or {}).items():
            if isinstance(v, dict) and v.get("t") and (now - float(v["t"])) < 86400 * 7:
                _dlc_detail_cache.setdefault(k, v)
    except Exception:
        pass


def _save_detail_cache() -> None:
    try:
        with open(_detail_cache_path(), "w", encoding="utf-8") as f:
            json.dump(_dlc_detail_cache, f, ensure_ascii=False)
    except Exception:
        pass


def api_dlc_detail(steam_id: str) -> dict:
    """拉取某个 DLC 的 Steam 商店详情（中文），带 1 小时缓存。"""
    sid = (steam_id or "").strip()
    if not sid.isdigit():
        return {"ok": False, "error": "无效的 DLC ID"}
    _load_detail_cache()
    cache = _dlc_detail_cache.get(sid)
    if cache and time.time() - cache["t"] < 3600:
        return cache["d"]
    try:
        url = ("https://store.steampowered.com/api/appdetails"
               "?appids=%s&l=schinese&cc=cn" % sid)
        req = urllib.request.Request(url, headers={
            "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)",
            "Accept-Language": "zh-CN,zh;q=0.9",
        })
        # 先禁用代理直连（Steam++ 会把 Steam 域名改写到 127.0.0.1 由本机加速进程转发，
        # 直连才能通）；失败再回退走系统代理（某些环境需要系统代理才能出去）。
        raw = None
        try:
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
            raw = opener.open(req, timeout=12).read()
        except Exception:
            raw = urllib.request.urlopen(req, timeout=12).read()
        d = json.loads(raw.decode("utf-8", errors="replace"))
        app = d.get(sid, {})
        data = app.get("data") if app.get("success") else {}
        if not data:
            result = {"ok": False, "error": "Steam 商店没有这个 DLC 的数据"}
        else:
            result = {
                "ok": True,
                "steamId": sid,
                "name": data.get("name") or "",
                "shortDescription": data.get("short_description") or "",
                "descriptionHtml": data.get("detailed_description") or data.get("about_the_game") or "",
                "headerImage": data.get("header_image") or "",
                "screenshots": [s.get("path_thumbnail") or s.get("path_full") or ""
                                for s in (data.get("screenshots") or [])],
                "releaseDate": (data.get("release_date") or {}).get("date") or "",
                "isFree": bool(data.get("is_free")),
            }
        # 只有成功才缓存；失败不缓存，下次重试
        if result.get("ok"):
            _dlc_detail_cache[sid] = {"t": time.time(), "d": result}
            _save_detail_cache()
        return result
    except Exception as ex:
        # 网络失败时，有旧缓存就先用旧的（标 stale），总比空白强
        if cache:
            stale = dict(cache["d"])
            stale["stale"] = True
            return stale
        return {"ok": False, "error": "拉取失败：%s" % ex}


def _rec_of(mod_file: str) -> dict:
    """把 modFile 转成注册表记录所需字段。"""
    meta = CTX.mod_by_file.get((mod_file or "").lower()) or {}
    return {
        "steamId": meta.get("steamId"),
        "name": meta.get("name") or os.path.splitext(os.path.basename(mod_file))[0],
        "displayName": meta.get("name"),
        "version": meta.get("version") or "",
        "dirPath": meta.get("dirPath") or "",
        "source": meta.get("source") or "local",
    }


def api_set_dlcs(payload: dict) -> dict:
    psid = payload.get("playsetId") or CTX.db().active_playset()
    mapping = payload.get("dlcs") or {}
    db = CTX.db()
    if not psid:
        return {"ok": False, "error": "没有激活的播放集"}
    CTX.mods()
    cur = api_playset_detail(psid)
    if not cur.get("ok"):
        return cur
    merged = dict(cur["dlcs"])
    for k, v in mapping.items():
        merged[k] = bool(v)
    items = []
    for i in cur["items"]:
        items.append({"modId": i["modId"], "enabled": i["enabled"], "rec": None})
    res = db.write_playset(psid, items, merged)

    # 真正禁用/启用：改游戏目录里对应 .dlc 文件的扩展名
    # （disabled_dlcs 字段在 1.20 已失效，引擎靠扫描 .dlc 文件发现 DLC）
    game_root = CTX.cfg.get("gameRoot") or ""
    pops_to_dir = {}
    for d in core.list_dlcs(CTX.cfg):
        pops_to_dir[d["popsId"]] = d["dir"]
    changed = []
    failed = []
    for pops, en in merged.items():
        d = pops_to_dir.get(pops)
        if not d:
            continue
        ok = core.set_dlc_file_state(game_root, d, bool(en))
        (changed if ok else failed).append(d)
    return {"ok": True, "synced": res.get("synced", False), "dlcs": merged,
            "dlcFilesChanged": len(changed), "dlcFilesFailed": len(failed)}


def api_saves() -> dict:
    return {"ok": True, "items": core.list_saves(CTX.cfg)}


def api_open_statistics() -> dict:
    cfg = CTX.cfg
    return {
        "ok": True,
        "paths": {
            "game": cfg.get("gameRoot"),
            "user": cfg.get("userDir"),
            "mod": os.path.join(cfg.get("userDir") or "", "mod"),
            "workshop": cfg.get("workshopDir"),
            "saves": os.path.join(cfg.get("userDir") or "", "save games"),
            "logs": os.path.join(cfg.get("userDir") or "", "logs"),
            "dlcLoad": os.path.join(cfg.get("userDir") or "", "dlc_load.json"),
            "sqlite": CTX.db().path,
            "gameData": os.path.join(cfg.get("gameRoot") or "", "game"),
            "common": os.path.join(cfg.get("gameRoot") or "", "game", "common"),
            "events": os.path.join(cfg.get("gameRoot") or "", "game", "events"),
            "localization": os.path.join(cfg.get("gameRoot") or "", "game", "localization"),
            "gui": os.path.join(cfg.get("gameRoot") or "", "game", "gui"),
            "backup": core.BACKUP_DIR,
        },
    }


# ---------------------------------------------------------------- HTTP 层

class Handler(BaseHTTPRequestHandler):
    server_version = "CK3Launcher/1.0"
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):  # 静默日志
        pass

    # ---- 工具
    def _send(self, code: int, body: bytes, ctype: str = "application/json; charset=utf-8",
              extra: dict | None = None):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        for k, v in (extra or {}).items():
            self.send_header(k, v)
        self.end_headers()
        try:
            self.wfile.write(body)
        except Exception:
            pass

    def _json(self, obj, code: int = 200):
        self._send(code, json.dumps(obj, ensure_ascii=False).encode("utf-8"))

    def _file(self, path: str, ctype: str | None = None):
        try:
            with open(path, "rb") as f:
                data = f.read()
        except Exception:
            self._send(404, b"not found", "text/plain")
            return
        ct = ctype or mimetypes.guess_type(path)[0] or "application/octet-stream"
        if path.lower().endswith((".js",)):
            ct = "application/javascript; charset=utf-8"
        elif path.lower().endswith(".css"):
            ct = "text/css; charset=utf-8"
        elif path.lower().endswith(".html"):
            ct = "text/html; charset=utf-8"
        self._send(200, data, ct, {"Cache-Control": "no-cache"})

    def _path_ok(self, p: str) -> bool:
        """限制可读取的绝对路径范围，避免任意文件读取。"""
        roots = [
            CTX.cfg.get("gameRoot") or "",
            CTX.cfg.get("userDir") or "",
            CTX.cfg.get("workshopDir") or "",
            core.BACKUP_DIR,
        ]
        p = os.path.abspath(p)
        for r in roots:
            if r and os.path.abspath(p).lower().startswith(os.path.abspath(r).lower()):
                return True
        return False

    def _body(self) -> dict:
        try:
            n = int(self.headers.get("Content-Length") or 0)
            if n <= 0:
                return {}
            return json.loads(self.rfile.read(n).decode("utf-8"))
        except Exception:
            return {}

    # ---- 路由
    def do_GET(self):
        try:
            u = urlparse(self.path)
            q = {k: v[0] for k, v in parse_qs(u.query).items()}
            p = unquote(u.path)

            if p == "/" or p == "/index.html":
                self._file(os.path.join(STATIC_DIR, "index.html"))
                return
            if p.startswith("/static/"):
                rel = p[len("/static/"):].replace("..", "")
                self._file(os.path.join(STATIC_DIR, rel))
                return
            if p == "/api/state":
                self._json(api_state())
                return
            if p == "/api/mods":
                idx = CTX.mods(force=q.get("force") == "1")
                self._json({"ok": True, "items": idx, "count": len(idx)})
                return
            if p == "/api/mod/pack/progress":
                self._json(pack_mod_progress())
                return
            if p == "/api/collections":
                self._json({"ok": True, "items": core.list_collections(CTX.game)})
                return
            if p == "/api/playsets":
                self._json({"ok": True, "items": CTX.db().list_playsets()})
                return
            if p == "/api/playset":
                self._json(api_playset_detail(q.get("id") or CTX.db().active_playset() or ""))
                return
            if p == "/api/mod/conflicts":
                self._json(core.detect_mod_conflicts(
                    CTX.cfg, q.get("playset") or CTX.db().active_playset() or ""))
                return
            if p == "/api/mod/conflicts/stream":
                # 流式排查：逐 mod 回报进度，避免大播放集扫描时界面卡死无反馈
                self.send_response(200)
                self.send_header("Content-Type", "application/x-ndjson; charset=utf-8")
                self.send_header("Cache-Control", "no-cache")
                self.send_header("X-Accel-Buffering", "no")
                self.end_headers()

                def _emit(scanned, total):
                    try:
                        self.wfile.write((json.dumps(
                            {"type": "progress", "scanned": scanned, "total": total},
                            ensure_ascii=False) + "\n").encode("utf-8"))
                        self.wfile.flush()
                    except Exception:
                        pass

                try:
                    res = core.detect_mod_conflicts(
                        CTX.cfg, q.get("playset") or CTX.db().active_playset() or "",
                        progress_cb=_emit)
                    self.wfile.write((json.dumps(
                        {"type": "done", "result": res}, ensure_ascii=False) + "\n").encode("utf-8"))
                    self.wfile.flush()
                except Exception as e:
                    try:
                        self.wfile.write((json.dumps(
                            {"type": "error", "error": str(e)}, ensure_ascii=False) + "\n").encode("utf-8"))
                        self.wfile.flush()
                    except Exception:
                        pass
                return
            if p == "/api/export":
                self._json(api_export_playset(q.get("id") or CTX.db().active_playset() or ""))
                return
            if p == "/api/dlcs":
                force = (q.get("force") or [""])[0] in ("1", "true", "yes")
                pull = (q.get("steam") or [""])[0] in ("1", "true", "yes")
                if force:
                    steamdlc.reset_cache()   # 清掉授权缓存，强制重新问 Steam
                self._json(api_dlcs(q.get("id"), force, pull))
                return
            if p == "/api/dlcs/refresh":
                # 独立刷新接口：重新拉 Steam DLC 清单 + 重置 owned 缓存
                steamdlc.reset_cache()
                r = core.refresh_dlc_from_steam(CTX.game)
                self._json({"ok": r.get("ok"), "count": r.get("count"),
                            "msg": r.get("msg")})
                return
            if p == "/api/dlc/detail":
                self._json(api_dlc_detail(q.get("id") or ""))
                return
            if p == "/api/saves":
                self._json(api_saves())
                return
            if p == "/api/achievements":
                # 默认走本地缓存（秒开）；?force=1 才重新读 Steam 并重拉全球完成率
                appid = core.GAME_DEFS.get(CTX.game, core.GAME_DEFS["ck3"])["appid"]
                try:
                    self._json(achievements.collect(
                        appid,
                        force=q.get("force") == "1",
                        game_root=CTX.cfg.get("gameRoot") or "",
                    ))
                except Exception as ex:
                    self._json({"ok": False, "error": "读取成就失败：%s" % ex})
                return
            if p == "/api/ach-icon":
                # 成就图标：从游戏本体 DDS 转 PNG（转一次缓存到启动器目录），不走 Steam CDN
                name = (q.get("n") or "").strip()
                gray = (q.get("g") or "0") == "1"
                appid = core.GAME_DEFS.get(CTX.game, core.GAME_DEFS["ck3"])["appid"]
                img = achievements.local_icon_png(
                    appid, CTX.cfg.get("gameRoot") or "", name, gray)
                if img and os.path.isfile(img):
                    try:
                        with open(img, "rb") as f:
                            data = f.read()
                        self._send(200, data, "image/png",
                                   {"Cache-Control": "public, max-age=86400"})
                    except Exception:
                        self._send(404, b"", "image/png")
                else:
                    self._send(404, b"", "image/png")
                return
            if p == "/api/paths":
                self._json(api_open_statistics())
                return
            if p == "/api/backups":
                items = core.list_backups()
                total = sum(x["size"] for x in items)
                self._json({"ok": True, "items": items, "total": total,
                            "dir": core.BACKUP_DIR, "max": core.MAX_BACKUPS})
                return
            if p == "/api/thumb":
                fp = q.get("p") or ""
                if fp and self._path_ok(fp) and os.path.isfile(fp):
                    self._file(fp)
                else:
                    self._send(404, b"", "image/png")
                return
            if p == "/api/ping":
                self._json({"ok": True})
                return
            if p == "/api/consistency":
                self._json(api_consistency())
                return
            if p == "/api/mem":
                self._json(core.memory_info())
                return
            if p == "/api/gamedata":
                self._json(api_gamedata())
                return
            if p == "/api/wiki":
                # 百科：从游戏本体脚本 + 官方本地化抽取（离线、带缓存）
                try:
                    self._json(wiki.load(
                        CTX.game,
                        CTX.cfg.get("gameRoot") or "",
                        CTX.cfg.get("uiLang") or "zh",
                        force=q.get("refresh") == "1",
                    ))
                except Exception as ex:
                    self._json({"ok": False, "supported": True,
                                "error": "读取百科失败：%s" % ex})
                return
            if p == "/api/wiki/focus":
                # 单棵国策树的节点（前端切树时才拉，首屏不塞几 MB 数据）
                try:
                    self._json(wiki.focus_tree(
                        CTX.game, CTX.cfg.get("gameRoot") or "",
                        q.get("tree") or "", CTX.cfg.get("uiLang") or "zh"))
                except Exception as ex:
                    self._json({"ok": False, "error": "读取国策树失败：%s" % ex})
                return
            if p == "/api/wiki/focus-icon":
                # 国策图标：从游戏 DDS 转 PNG（转一次缓存），浏览器显示不了 DDS
                img = wiki.focus_icon(CTX.cfg.get("gameRoot") or "", q.get("n") or "")
                if img and os.path.isfile(img):
                    try:
                        with open(img, "rb") as f:
                            data = f.read()
                        self._send(200, data, "image/png",
                                   {"Cache-Control": "public, max-age=604800"})
                    except Exception:
                        self._send(404, b"", "image/png")
                else:
                    self._send(404, b"", "image/png")
                return
            self._send(404, b"not found", "text/plain")
        except Exception:
            self._json({"ok": False, "error": traceback.format_exc(limit=3)}, 500)

    def _raw_upload(self) -> dict:
        """接收拖入的压缩包（裸二进制 body），文件名放在 X-File-Name 头里。"""
        try:
            n = int(self.headers.get("Content-Length") or 0)
            if n <= 0:
                return {"ok": False, "error": "空文件"}
            if n > 400 * 1024 * 1024:
                return {"ok": False, "error": "文件太大（上限 400MB）"}
            raw = self.headers.get("X-File-Name") or ""
            try:
                fname = unquote(raw.encode("latin-1").decode("utf-8"))
            except Exception:
                fname = unquote(raw)
            fname = fname or "mod.zip"
            safe = "".join(ch for ch in os.path.basename(fname) if ch not in '\\/:*?"<>|') or "mod.zip"
            tmp_dir = os.path.join(APP_DIR, "tmp-import")
            os.makedirs(tmp_dir, exist_ok=True)
            tmp_path = os.path.join(tmp_dir, "up_" + str(int(time.time() * 1000)) + "_" + safe)
            left = n
            with open(tmp_path, "wb") as f:
                while left > 0:
                    chunk = self.rfile.read(min(262144, left))
                    if not chunk:
                        break
                    f.write(chunk)
                    left -= len(chunk)
            try:
                r = core.install_mod_archive(CTX.cfg, tmp_path, fname)
            finally:
                try:
                    os.remove(tmp_path)
                except Exception:
                    pass
            if r.get("ok"):
                CTX.mods(force=True)
            return r
        except Exception as ex:
            return {"ok": False, "error": "接收失败：%s" % ex}

    def do_POST(self):
        try:
            u = urlparse(self.path)
            p = unquote(u.path)
            if p == "/api/mod/import-archive":
                self._json(self._raw_upload()); return
            if p == "/api/mod/pick-archive":
                self._json(pick_mod_archive()); return
            if p == "/api/dlc/names-file":
                self._json(open_dlc_names_file()); return
            body = self._body()

            if p == "/api/ui-lang":
                # 界面语言：zh / en / fr / de / sv，存进主配置，下次打开沿用
                lang = (body.get("lang") or "").strip().lower()
                if lang not in ("zh", "en", "fr", "de", "sv"):
                    self._json({"ok": False, "error": "不支持的语言: %s" % lang}); return
                CTX.cfg["uiLang"] = lang
                core.save_config(CTX.cfg)
                self._json({"ok": True, "lang": lang}); return

            if p == "/api/check-update":
                # 版本自检：本地后端代理拉取远程 version.json，绕开 webview 直连外网的 CORS 限制。
                # 走 POST JSON 体（与前端 post() 一致），不再依赖查询串。
                url = (body.get("url") or "").strip()
                current = (body.get("current") or "").strip()
                info = core.fetch_update_info(url)
                if info.get("ok"):
                    info["current"] = current
                    info["updateAvailable"] = bool(current) and core.version_greater(info["latest"], current)
                self._json(info); return

            if p == "/api/mod/pack/prepare":
                mf = body.get("modFile") or ""
                if not mf:
                    self._json({"ok": False, "error": "缺少 modFile"}); return
                self._json(pack_mod_prepare(mf)); return
            if p == "/api/mod/pack/run":
                mf = body.get("modFile") or ""
                dest = body.get("path") or ""
                self._json(pack_mod_run(mf, dest)); return

            if p == "/api/playset/save":
                self._json(api_save_playset(body)); return
            if p == "/api/playset/create":
                self._json(api_create_playset(body)); return
            if p == "/api/playset/rename":
                self._json(api_rename_playset(body)); return
            if p == "/api/playset/delete":
                self._json(api_delete_playset(body)); return
            if p == "/api/playset/activate":
                self._json(api_activate_playset(body)); return
            if p == "/api/playset/sync":
                self._json(api_sync_playset(body)); return
            if p == "/api/backup":
                db = CTX.db()
                made = []
                for src, tag in ((db.path, "launcher-v2"), (db.dlc_load_path, "dlc_load")):
                    f = core.backup_file(src, tag, min_interval=0)
                    if f:
                        made.append(os.path.basename(f))
                self._json({"ok": True, "files": made, "dir": core.BACKUP_DIR}); return
            if p == "/api/backups/clear":
                n = core.clear_backups(body.get("tag") or None)
                self._json({"ok": True, "removed": n}); return
            if p == "/api/backups/restore":
                r = core.restore_backup(body.get("name") or "", CTX.cfg.get("userDir") or "")
                self._json(r); return
            if p == "/api/shortcut":
                self._json(core.create_desktop_shortcut()); return
            if p == "/api/mem/optimize":
                self._json(core.optimize_memory()); return
            if p == "/api/mem/admin-restart":
                self._json(core.restart_as_admin()); return
            if p == "/api/win/perf":
                self._json({"ok": core.open_windows_perf_options()}); return
            if p == "/api/launcher/minimize":
                self._json({"ok": core.minimize_windows()}); return
            if p == "/api/diag/collect":
                r = diagnose.collect(CTX.cfg, include_dxdiag=bool(body.get("dxdiag")))
                self._json(r); return
            if p == "/api/diag/save-as":
                src = body.get("zip") or ""
                # 只允许保存本程序生成的诊断包
                if not (os.path.isfile(src) and "ck3diag_" in src and src.lower().endswith(".zip")):
                    self._json({"ok": False, "error": "诊断包不存在或已过期"}); return
                self._json(save_binary_dialog(os.path.basename(src), src)); return
            if p == "/api/diag/open-dir":
                d = body.get("dir") or ""
                if d and "ck3diag_" in d and os.path.isdir(d):
                    self._json({"ok": core.open_path(d)}); return
                self._json({"ok": False, "error": "目录不存在"}); return
            if p == "/api/playset/import":
                self._json(api_import_playset(body)); return
            if p == "/api/dlcs/set":
                self._json(api_set_dlcs(body)); return
            if p == "/api/saves/delete":
                paths = body.get("paths") or []
                done, denied = [], []
                for fp in paths:
                    if self._path_ok(fp) and core.recycle_path(fp):
                        done.append(fp)
                    else:
                        denied.append(fp)
                self._json({"ok": bool(done), "done": done, "failed": denied}); return
            if p == "/api/open":
                target = body.get("path") or ""
                if body.get("reveal") and os.path.isfile(target):
                    ok = subprocess_reveal(target)
                else:
                    ok = core.open_path(target)
                self._json({"ok": ok}); return
            if p == "/api/open-tutorial":
                self._json(open_tutorial()); return
            if p == "/api/launch":
                self._json(core.launch_game(
                    CTX.cfg, body.get("psid") or None, bool(body.get("debug"))
                )); return
            if p == "/api/launch-official":
                self._json(core.launch_official_launcher(CTX.cfg)); return
            if p == "/api/url":
                url = body.get("url") or ""
                if url.startswith("http"):
                    try:
                        os.startfile(url)  # noqa: S606
                        self._json({"ok": True})
                    except Exception as ex:
                        self._json({"ok": False, "error": str(ex)})
                else:
                    self._json({"ok": False, "error": "invalid url"})
                return
            if p == "/api/register-mod":
                self._json(core.register_mod_descriptor(CTX.cfg, str(body.get("steamId") or "")))
                return
            if p == "/api/mod/rename":
                # 设置/恢复 mod 自定义显示名（alias 为空 = 恢复原名）
                mf = body.get("modFile") or ""
                alias = body.get("alias") or ""
                if not mf:
                    self._json({"ok": False, "error": "缺少 modFile"}); return
                core.set_mod_alias(CTX.game, mf, alias)
                CTX.mods(force=True)
                self._json({"ok": True}); return
            if p == "/api/mod/source":
                # 手动覆盖 mod 来源标签（source: local/steam/空=恢复自动判定）
                mf = body.get("modFile") or ""
                src = body.get("source") or ""
                if not mf:
                    self._json({"ok": False, "error": "缺少 modFile"}); return
                core.set_mod_source_override(CTX.game, mf, src)
                CTX.mods(force=True)
                self._json({"ok": True}); return
            if p == "/api/collection/create":
                name = body.get("name") or ""
                ok = core.create_collection(CTX.game, name)
                self._json({"ok": ok, "error": "" if ok else "名字已存在或为空"}); return
            if p == "/api/collection/rename":
                ok = core.rename_collection(CTX.game, body.get("old") or "", body.get("new") or "")
                self._json({"ok": ok, "error": "" if ok else "重命名失败"}); return
            if p == "/api/collection/delete":
                name = body.get("name") or ""
                core.delete_collection(CTX.game, name)
                self._json({"ok": True}); return
            if p == "/api/collection/toggle":
                # 收藏/取消收藏某 mod：{ name, modFile } → 返回 {added}
                name = body.get("name") or ""
                mf = body.get("modFile") or ""
                added = core.toggle_mod_in_collection(CTX.game, name, mf)
                self._json({"ok": True, "added": added}); return
            if p == "/api/mod/delete":
                mf = body.get("modFile") or ""
                if not mf:
                    self._json({"ok": False, "error": "缺少 modFile"}); return
                r = core.delete_mod(CTX.cfg, mf)
                if r.get("ok"):
                    CTX.mods(force=True)
                self._json(r); return
            if p == "/api/register-all":
                CTX.mods(force=True)
                pend = [m for m in CTX.mod_index if m.get("pending") and m.get("steamId")]
                done, first_err = [], ""
                failed = 0
                for m in pend:
                    # 单个 mod 注册失败不能中断整批，也不能让接口 500 后前端「没反应」
                    try:
                        r = core.register_mod_descriptor(CTX.cfg, m["steamId"])
                        if r.get("ok"):
                            done.append(m["steamId"])
                        else:
                            failed += 1
                            if not first_err:
                                first_err = str(r.get("error") or "未知错误")
                    except Exception as ex:
                        failed += 1
                        if not first_err:
                            first_err = str(ex)
                if done:
                    CTX.mods(force=True)
                self._json({"ok": True, "registered": done, "failed": failed,
                            "error": first_err}); return
            if p == "/api/config":
                cfg = dict(CTX.cfg)
                for k in ("gameRoot", "userDir", "workshopDir", "gameVersion"):
                    if body.get(k):
                        cfg[k] = body[k]
                # 版本自检地址：顶层全局配置（与 uiLang 同级），留空则不检查
                if "updateUrl" in body:
                    cfg["updateUrl"] = (body["updateUrl"] or "").strip()
                for k in ("autoBackup", "rememberLastPlayset",
                          "minimizeOnLaunch", "closeOnLaunch", "useDataPathArg"):
                    if k in body:
                        cfg[k] = bool(body[k])
                core.save_config(cfg)
                CTX.reload_cfg()
                CTX.mods(force=True)
                self._json({"ok": True, "config": CTX.cfg}); return
            if p == "/api/switch-game":
                key = body.get("game") or ""
                CTX.switch_game(key)
                self._json({"ok": True, "state": api_state()}); return
            if p == "/api/add-game":
                # 手动添加游戏：指定 gameRoot（可选 userDir），写入配置 games[key]
                key = body.get("game") or ""
                groot = body.get("gameRoot") or ""
                udir = body.get("userDir") or ""
                if key not in core.GAME_DEFS:
                    self._json({"ok": False, "error": "未知游戏"}); return
                cfg = core.load_config()
                games = cfg.get("games") or {}
                g = dict(games.get(key) or {})
                if groot:
                    g["gameRoot"] = groot
                if udir:
                    g["userDir"] = udir
                games[key] = g
                cfg["games"] = games
                core.save_config(cfg)
                self._json({"ok": True, "state": api_state()}); return
            if p == "/api/profile/switch":
                # 切换当前游戏的激活版本配置
                pid = body.get("id") or ""
                code = CTX.game
                r = core.switch_profile(code, pid)
                if not r.get("ok"):
                    self._json(r); return
                CTX.reload_cfg(); CTX.mods(force=True)
                self._json({"ok": True, "state": api_state()}); return
            if p == "/api/profile/add":
                code = CTX.game
                r = core.add_profile(code, body)
                if not r.get("ok"):
                    self._json(r); return
                CTX.reload_cfg(); CTX.mods(force=True)
                self._json({"ok": True, "state": api_state()}); return
            if p == "/api/profile/update":
                code = CTX.game
                pid = body.get("id") or ""
                r = core.update_profile(code, pid, body)
                if not r.get("ok"):
                    self._json(r); return
                CTX.reload_cfg(); CTX.mods(force=True)
                self._json({"ok": True, "state": api_state()}); return
            if p == "/api/profile/delete":
                code = CTX.game
                pid = body.get("id") or ""
                r = core.delete_profile(code, pid)
                if not r.get("ok"):
                    self._json(r); return
                CTX.reload_cfg(); CTX.mods(force=True)
                self._json({"ok": True, "state": api_state()}); return
            if p == "/api/steam/branches":
                # 读出 Steam 版本分支（betakey/描述/buildid）与当前分支
                code = CTX.game
                appid = core.GAME_DEFS.get(code, core.GAME_DEFS["ck3"]).get("appid", "1158310")
                cur = steambeta.get_beta(appid)
                self._json({
                    "ok": True,
                    "appid": appid,
                    "current": cur.get("key") or "public",
                    "manifest": cur.get("manifest") or "",
                    "steamRunning": steambeta.steam_running(),
                    "branches": steambeta.branches_or_fallback(appid),
                }); return
            if p == "/api/steam/switch":
                # 切换 Steam 版本分支（写 appmanifest BetaKey + 置 StateFlags 需更新）
                # restart=true 时自动：关 Steam → 写清单 → 开 Steam（由 Steam 自行下载该分支）
                code = CTX.game
                gd = core.GAME_DEFS.get(code, core.GAME_DEFS["ck3"])
                appid = gd.get("appid", "1158310")
                exe_name = (gd.get("exe") or ("", "ck3.exe"))[-1]
                if steambeta.process_running(exe_name):
                    self._json({"ok": False, "error": "检测到游戏进程仍在（可能是上次残留的僵尸进程）。请先完全退出游戏；若已关闭，请重启电脑清掉残留进程后再切换（Steam 无法更新被占用的文件）"}); return
                key = body.get("key") or ""
                restart = bool(body.get("restart"))
                if restart and steambeta.steam_running():
                    if not steambeta.quit_steam(wait_seconds=35):
                        self._json({"ok": False, "error": "Steam 未能自动关闭，请手动完全退出 Steam 后再试"}); return
                r = steambeta.set_beta(appid, key)
                if r.get("ok") and restart:
                    steambeta.launch_steam()
                    r["steamRestarted"] = True
                self._json(r); return
            if p == "/api/pick-dir":
                self._json({"ok": True, "path": pick_directory(body.get("initial") or "")}); return
            if p == "/api/playset/save-as":
                psid = body.get("id")
                d = api_export_playset(psid) if psid else {}
                if not d.get("ok"):
                    self._json({"ok": False, "error": d.get("error") or "导出失败"}); return
                safe = "".join(ch for ch in (d.get("name") or "playset")
                               if ch not in '\\/:*?"<>|') or "playset"
                self._json(save_file_dialog(
                    f"{safe}.json",
                    json.dumps(d, ensure_ascii=False, indent=2),
                )); return
            if p == "/api/playset/open-file":
                r = open_file_dialog()
                if not r.get("ok") or r.get("cancelled"):
                    self._json(r); return
                try:
                    data = json.loads(r.get("content") or "")
                except Exception as ex:
                    self._json({"ok": False, "error": "文件解析失败：%s" % ex}); return
                self._json({"ok": True, "path": r.get("path"), "data": data}); return
            if p == "/api/reveal-in-explorer":
                self._json({"ok": subprocess_reveal(body.get("path") or "")}); return
            if p == "/api/exit":
                self._json({"ok": True})
                threading.Timer(0.3, lambda: os._exit(0)).start()
                return
            self._send(404, b"not found", "text/plain")
        except Exception:
            self._json({"ok": False, "error": traceback.format_exc(limit=3)}, 500)


def subprocess_reveal(path: str) -> bool:
    if not path:
        return False
    try:
        p = os.path.abspath(path)
        if os.path.isfile(p):
            import subprocess
            subprocess.Popen(["explorer", "/select,", p.replace("/", "\\")])
            return True
        return core.open_path(os.path.dirname(p))
    except Exception:
        return False


def pick_directory(initial: str = "") -> str:
    """原生文件夹选择框（tkinter 在请求线程里会失败，已改用 Win32 API）。"""
    return windlg.pick_folder("选择文件夹", initial)


def _dlg_result(path: str, on_ok=None) -> dict:
    """把 windlg 的返回值统一成 {ok, path, cancelled} / {ok:false, error}。"""
    if path.startswith(windlg.ERR_PREFIX):
        return {"ok": False, "error": "系统文件对话框打不开（%s）" % path[len(windlg.ERR_PREFIX):]}
    if not path:
        return {"ok": True, "path": "", "cancelled": True}
    if on_ok:
        try:
            on_ok(path)
        except Exception as ex:
            return {"ok": False, "error": "写入失败：%s" % ex}
    return {"ok": True, "path": path, "cancelled": False}


def save_file_dialog(default_name: str, content: str, title: str = "导出播放集") -> dict:
    """原生「另存为」，把文本内容写到用户选的位置。"""
    src = None
    path = windlg.save_file(
        title=title, default_name=default_name,
        filters=[("JSON 文件", "*.json"), ("所有文件", "*.*")], def_ext="json",
    )

    def _write(p):
        with open(p, "w", encoding="utf-8", newline="\n") as f:
            f.write(content)

    return _dlg_result(path, _write)


def open_file_dialog(title: str = "选择播放集文件") -> dict:
    """原生「打开」，读回内容。"""
    path = windlg.open_file(
        title=title, filters=[("JSON 文件", "*.json"), ("所有文件", "*.*")],
    )
    if path.startswith(windlg.ERR_PREFIX):
        return {"ok": False, "error": "系统文件对话框打不开"}
    if not path:
        return {"ok": True, "cancelled": True}
    try:
        with open(path, "r", encoding="utf-8-sig") as f:
            return {"ok": True, "path": path, "content": f.read(), "cancelled": False}
    except Exception as ex:
        return {"ok": False, "error": "读取失败：%s" % ex}


# ---- mod 打包导出：进度状态（供前端轮询）----
_pack_lock = threading.Lock()
_pack_state: dict = {"phase": "idle", "done": 0, "total": 0, "cur": "", "ok": None, "msg": ""}


def _pack_set(**kw):
    with _pack_lock:
        _pack_state.update(kw)


def pack_mod_progress() -> dict:
    with _pack_lock:
        return dict(_pack_state)


def pack_mod_prepare(mod_file: str) -> dict:
    """第一步：统计体量 + 弹保存对话框选位置。不写文件，所以很快。"""
    info = core.pack_mod_archive(CTX.cfg, mod_file)
    if not info.get("ok"):
        return info
    safe = "".join(ch for ch in (info.get("name") or "mod")
                   if ch not in '\\/:*?"<>|').strip() or "mod"
    # 先告诉前端「我在弹窗了」，这样界面能显示提示而不是干等
    _pack_set(phase="choose", done=0, total=info.get("fileCount", 0),
              cur="", ok=None, msg="正在选择保存位置")
    path = windlg.save_file(
        title="导出 Mod 压缩包",
        default_name=safe + ".zip",
        filters=[("ZIP 压缩包", "*.zip"), ("所有文件", "*.*")],
        def_ext="zip",
    )
    res = _dlg_result(path)
    if res.get("cancelled") or not res.get("ok"):
        _pack_set(phase="idle", ok=None, msg="")
        return res
    _pack_set(phase="ready", done=0, total=info.get("fileCount", 0) + 1,
              cur="", ok=None, msg="")
    return {
        "ok": True,
        "path": path,
        "name": info.get("name") or safe,
        "folder": info.get("folder") or safe,
        "fileCount": info.get("fileCount", 0) + 1,   # +1：会额外写顶层 .mod
        "sizeBytes": info.get("sizeBytes", 0),
    }


def pack_mod_run(mod_file: str, dest_zip: str) -> dict:
    """第二步：真正写 zip，过程中持续更新进度。"""
    if not dest_zip:
        return {"ok": False, "error": "缺少保存路径"}
    total_guess = (core.pack_mod_archive(CTX.cfg, mod_file).get("fileCount") or 0) + 1
    _pack_set(phase="packing", done=0, total=total_guess, cur="", ok=None,
              msg="正在压缩")

    def _cb(done, total, cur):
        _pack_set(done=done, total=total, cur=cur)

    try:
        r = core.write_mod_archive(CTX.cfg, mod_file, dest_zip, on_progress=_cb)
    except Exception as ex:
        r = {"ok": False, "error": "打包失败：%s" % ex}
    if r.get("ok"):
        _pack_set(phase="done", done=r.get("fileCount", 0),
                  total=r.get("fileCount", 0), cur="", ok=True,
                  msg="打包完成")
    else:
        _pack_set(phase="error", ok=False, msg=r.get("error") or "打包失败")
    return r


def open_dlc_names_file() -> dict:
    """确保「DLC 中文名覆盖表」存在（不存在就写一份带示例的模板），并用系统默认程序打开。

    玩家可以在这里自己加 / 改 DLC 中文名，改完保存回启动器即生效。
    """
    path = _dlc_names_path()
    try:
        import dlc_cn
        ok, err = dlc_cn.ensure_user_file(path)
    except Exception as ex:
        return {"ok": False, "error": "初始化失败：%s" % ex}
    if not ok:
        return {"ok": False, "error": "创建文件失败：%s" % err}

    opened = core.open_path(path)
    return {
        "ok": True,
        "path": path,
        "opened": opened,
        "error": "" if opened else "文件已生成在程序目录，但系统没有关联的 .json 打开程序，请手动打开",
    }


def open_tutorial() -> dict:
    """用系统默认浏览器打开「使用教程.html」。

    发布包布局：和 exe 同级；源码运行时：在 tutorial/ 子目录里。两边都找一遍。
    """
    for p in (os.path.join(APP_DIR, "使用教程.html"),
              os.path.join(APP_DIR, "tutorial", "使用教程.html"),
              os.path.join(core.RES_DIR, "tutorial", "使用教程.html")):
        if os.path.isfile(p):
            if core.open_path(p):
                return {"ok": True, "path": p}
            return {"ok": False, "error": "系统里没有能打开 HTML 的浏览器"}
    return {"ok": False, "error": "找不到「使用教程.html」，可能被杀毒软件误删了"}


def pick_mod_archive() -> dict:
    """原生「打开」选压缩包，直接就地解压安装（拖放不灵时的备用入口）。"""
    path = windlg.open_file(
        title="选择 Mod 压缩包",
        filters=[("Mod 压缩包", "*.zip;*.7z;*.rar"), ("ZIP 压缩包", "*.zip"),
                 ("所有文件", "*.*")],
    )
    if path.startswith(windlg.ERR_PREFIX):
        return {"ok": False, "error": "系统文件对话框打不开"}
    if not path:
        return {"ok": True, "cancelled": True}
    r = core.install_mod_archive(CTX.cfg, path, os.path.basename(path))
    if r.get("ok"):
        CTX.mods(force=True)
    return r


def save_binary_dialog(default_name: str, src_file: str, title: str = "保存诊断包") -> dict:
    """原生「另存为」，把已有文件复制到用户选的位置。"""
    path = windlg.save_file(
        title=title, default_name=default_name,
        filters=[("ZIP 压缩包", "*.zip"), ("所有文件", "*.*")], def_ext="zip",
    )
    return _dlg_result(path, lambda p: shutil.copy2(src_file, p))


class Server(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def handle_error(self, request, client_address):
        """连接被对端强关不算错误。

        百科首屏一次要几十个图标请求，用户切页或关窗口时这些请求会被中断，
        socketserver 默认会把整段 traceback 打到控制台，看着像出事了，其实没有。
        """
        import sys
        et = sys.exc_info()[0]
        if et and issubclass(et, (ConnectionResetError, ConnectionAbortedError, BrokenPipeError)):
            return
        super().handle_error(request, client_address)


def serve(port: int = 0) -> tuple[ThreadingHTTPServer, int]:
    httpd = Server(("127.0.0.1", port), Handler)
    actual = httpd.server_address[1]
    t = threading.Thread(target=httpd.serve_forever, daemon=True)
    t.start()
    return httpd, actual


if __name__ == "__main__":
    s, port = serve(8765)
    print(f"http://127.0.0.1:{port}")
    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        pass

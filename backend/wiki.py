# -*- coding: utf-8 -*-
"""百科数据：从游戏本体脚本 + 官方本地化抽取结构化资料。

设计要点
--------
- **完全离线**：只读 `gameRoot` 下的脚本与本地化，不联网、不抓 wiki 正文
  （paradoxwikis 有 Fastly JS 人机校验，程序直连拿不到内容；页面只做外链）。
- **随版本自动更新**：游戏更新 / 装新 DLC 后，目录 mtime 变化 → 缓存自动失效重建。
- **可扩展**：新增分类（宗教、建筑、革新…）只需写一个 `_collect_xxx()` 并登记到 `CATEGORIES`。

数据结构（返回给前端）
--------------------
{
  "ok": true, "supported": true, "game": "ck3", "lang": "simp_chinese",
  "counts": {"maa":111, "cultures":244, "traditions":198},
  "maa": [...], "cultures": [...], "traditions": [...]
}
"""

import os
import re
import json
import glob
import time
import threading

try:                                    # 缓存放程序目录，和 dlc_data_*.json 等保持一致
    from ck3core import APP_DIR
except Exception:                       # 单独跑测试时兜底
    APP_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

WIKI_VER = 3                            # 解析逻辑变更时 +1，强制重建旧缓存

# 目录名（相对 gameRoot）
D_MAA = os.path.join("common", "men_at_arms_types")
D_CULT = os.path.join("common", "culture", "cultures")
D_TRAD = os.path.join("common", "culture", "traditions")
D_PILLAR = os.path.join("common", "culture", "pillars")
D_LOC = "localization"

# 目前只有 CK3 有对应脚本结构；其他游戏返回 supported=false
SUPPORTED_GAMES = {"ck3"}

# 界面语言 → 游戏本地化目录
LANG_MAP = {
    "zh": "simp_chinese",
    "en": "english",
    "fr": "french",
    "de": "german",
    "sv": "english",          # 游戏没有瑞典语本地化，回落英文
}

# 兵种类别：类型键 → (中文, English)。本地化能命中时优先用本地化，这里只是兜底。
TYPE_NAMES = {
    "skirmishers": ("散兵", "Skirmishers"),
    "heavy_infantry": ("重步兵", "Heavy Infantry"),
    "archers": ("弓箭手", "Archers"),
    "pikemen": ("长枪兵", "Pikemen"),
    "light_cavalry": ("轻骑兵", "Light Cavalry"),
    "heavy_cavalry": ("重骑兵", "Heavy Cavalry"),
    "archer_cavalry": ("弓骑兵", "Horse Archers"),
    "camel_cavalry": ("骆驼骑兵", "Camel Cavalry"),
    "elephant_cavalry": ("战象", "War Elephants"),
    "siege_weapon": ("攻城器械", "Siege Weapons"),
    "gunpowder": ("铳手", "Gunpowder Units"),
    "peasant_militia": ("农民民兵", "Peasant Militia"),
    "nomadic_horde": ("游牧部众", "Nomadic Horde"),
}

# 某些类别的本地化键名不统一（ accolade_ 前缀 / _attribute 后缀 ），按优先级尝试
TYPE_LOC_CANDIDATES = {
    "skirmishers": ("accolade_skirmishers", "accolade_skirmisher", "skirmisher_attribute"),
    "heavy_infantry": ("accolade_heavy_infantry", "heavy_infantry_attribute"),
    "archers": ("accolade_archers", "archers_attribute"),
    "pikemen": ("accolade_pikes", "pike_attribute"),
    "light_cavalry": ("accolade_light_cavalry", "light_cavalry_attribute"),
    "heavy_cavalry": ("accolade_heavy_cavalry", "heavy_cavalry_attribute"),
    "archer_cavalry": ("accolade_horse_archer", "horse_archer_attribute"),
    "gunpowder": ("gunpowder_attribute", "accolade_gunpowder"),
}

# 传统分类：文件名主干 → 中文标签
TRAD_CATS = {
    "combat": "战斗",
    "realm": "领地",
    "societal": "社会",
    "maa": "军事",
    "regional": "地域",
    "ritual": "仪式",
}

_LOCK = threading.Lock()
_MEM = {}          # 进程内缓存：key -> (sig, data)


# ---------------------------------------------------------------- 基础解析

def _read(path: str) -> str:
    """读脚本，去 BOM 去注释。"""
    try:
        with open(path, encoding="utf-8-sig", errors="ignore") as f:
            txt = f.read()
    except Exception:
        return ""
    return re.sub(r"#[^\n]*", "", txt)


def _top_blocks(text: str) -> list:
    """取出深度 0 的 `key = { ... }`，返回 [(key, body), ...]。"""
    out = []
    n = len(text)
    pat = re.compile(r"([A-Za-z_][A-Za-z0-9_]*)\s*=\s*\{")
    i = 0
    while i < n:
        m = pat.search(text, i)
        if not m:
            break
        depth = text.count("{", 0, m.start()) - text.count("}", 0, m.start())
        if depth != 0:
            i = m.end()
            continue
        start = m.end()
        d, j = 1, start
        while j < n and d > 0:
            c = text[j]
            if c == "{":
                d += 1
            elif c == "}":
                d -= 1
            j += 1
        out.append((m.group(1), text[start:j - 1]))
        i = j
    return out


def _sub(body: str, name: str):
    """取 body 内 `name = { ... }` 的内部文本（只找第一层）。"""
    m = re.search(r"(?:^|[\s{])" + re.escape(name) + r"\s*=\s*\{", body)
    if not m:
        return None
    start = m.end()
    d, j = 1, start
    while j < len(body) and d > 0:
        c = body[j]
        if c == "{":
            d += 1
        elif c == "}":
            d -= 1
        j += 1
    return body[start:j - 1]


def _val(body: str, name: str):
    """取 `name = value` 的标量值（忽略块）。"""
    m = re.search(r"(?:^|[\s{])" + re.escape(name) + r"\s*=\s*([^\s{}]+)", body)
    return m.group(1).strip() if m else None


def _int(v):
    try:
        return int(float(v))
    except Exception:
        return None


def _pairs(block: str) -> dict:
    """把 `k = v` 形式的简单块解析成 dict（忽略嵌套块）。"""
    out = {}
    if not block:
        return out
    for m in re.finditer(r"([A-Za-z_][A-Za-z0-9_]*)\s*=\s*([^\s{}]+)", block):
        out[m.group(1)] = m.group(2)
    return out


def _list(block: str) -> list:
    """把块内的裸标识符列表取出来（traditions / parents 这类）。"""
    if not block:
        return []
    return re.findall(r"([A-Za-z_][A-Za-z0-9_]*)", block)


def _read_defines(path: str) -> dict:
    """脚本顶部的 `@var = 123` 常量表，用来还原 provision_cost 之类。"""
    out = {}
    try:
        with open(path, encoding="utf-8-sig", errors="ignore") as f:
            for line in f:
                m = re.match(r"\s*@([A-Za-z0-9_]+)\s*=\s*(\d+(?:\.\d+)?)\s*$", line)
                if m:
                    out[m.group(1)] = m.group(2)
    except Exception:
        pass
    return out


# ---------------------------------------------------------------- 本地化

_LOC_PAT = re.compile(r'^\s*([A-Za-z0-9_.\-]+):\d*\s*"((?:[^"\\]|\\.)*)"')
_REF_PAT = re.compile(r"\$([A-Za-z0-9_.\-]+)\$")


def _scan_loc(loc_dir: str, needed: set) -> dict:
    """扫一遍本地化，只保留 needed 里的键（全量 30 万条，不能全留）。返回原始文本。"""
    out = {}
    if not os.path.isdir(loc_dir):
        return out
    for root, _dirs, names in os.walk(loc_dir):
        for n in names:
            if not n.lower().endswith(".yml"):
                continue
            try:
                with open(os.path.join(root, n), encoding="utf-8-sig", errors="ignore") as f:
                    for line in f:
                        m = _LOC_PAT.match(line)
                        if m and m.group(1) in needed and m.group(1) not in out:
                            out[m.group(1)] = m.group(2)
            except Exception:
                continue
    return out


def _resolve(text: str, raw: dict, depth: int = 3) -> str:
    """把 `$other_key$` 引用就地展开（游戏里传统名常这么写）。"""
    if not text or "$" not in text:
        return text

    def rep(m):
        k = m.group(1)
        if k in raw and depth > 0:
            return _resolve(raw[k], raw, depth - 1)
        return k

    return _REF_PAT.sub(rep, text)


def _clean(s: str) -> str:
    """去掉本地化里的内联格式标记，保留可读文本。"""
    s = s.replace("\\n", " ")
    s = re.sub(r"#[A-Za-z0-9_]+\s*;?", "", s)
    s = re.sub(r"\[([A-Za-z0-9_.\-]+)\|E\]", r"\1", s)
    return s.strip()


def _finalize(raw: dict) -> dict:
    """引用展开 + 清洗，得到可直接显示的译名表。"""
    return {k: _clean(_resolve(v, raw)) for k, v in raw.items()}


def _pick(loc: dict, keys) -> str:
    for k in keys:
        v = loc.get(k)
        if v:
            return v
    return ""


# ---------------------------------------------------------------- 各分类抽取

def _collect_maa(game_root: str, loc: dict, loc_lang: str) -> list:
    out = []
    for f in sorted(glob.glob(os.path.join(game_root, D_MAA, "*.txt"))):
        defines = _read_defines(f)
        text = _read(f)
        for key, body in _top_blocks(text):
            typ = _val(body, "type") or ""

            def num(name):
                v = _val(body, name)
                if v is None:
                    return None
                if v.startswith("@"):
                    v = defines.get(v[1:], "")
                return _int(v)

            terrain = []
            tb = _sub(body, "terrain_bonus")
            if tb:
                for tk, tv in _top_blocks(tb):
                    p = _pairs(tv)
                    terrain.append({
                        "key": tk,
                        "name": loc.get(tk) or tk,
                        "damage": _int(p.get("damage")),
                        "toughness": _int(p.get("toughness")),
                    })

            counters = []
            cb = _sub(body, "counters")
            if cb:
                for ck, cv in _pairs(cb).items():
                    counters.append({
                        "key": ck,
                        "name": _type_name(ck, loc, loc_lang),
                        "mult": _int(cv),
                    })

            winter = []
            wb = _sub(body, "winter_bonus")
            if wb:
                for wk, wv in _top_blocks(wb):
                    p = _pairs(wv)
                    winter.append({
                        "key": wk,
                        "name": loc.get(wk) or wk,
                        "damage": _int(p.get("damage")),
                        "toughness": _int(p.get("toughness")),
                    })

            flags = []
            if _val(body, "special_recruit_only") in ("yes", "yes_no"):
                flags.append("special")
            if _val(body, "allowed_in_hired_troops") == "yes":
                flags.append("hired")
            mr = num("max_regiments")
            if mr:
                flags.append("max:%d" % mr)
            if _val(body, "siege_tier"):
                flags.append("siege")

            out.append({
                "key": key,
                "name": loc.get(key) or key,
                "type": typ,
                "typeName": _type_name(typ, loc, loc_lang),
                "damage": num("damage"),
                "toughness": num("toughness"),
                "pursuit": num("pursuit"),
                "screen": num("screen"),
                "stack": num("stack"),
                "siege": num("siege_value"),
                "provision": num("provision_cost"),
                "terrain": terrain,
                "counters": counters,
                "winter": winter,
                "flags": flags,
                "src": os.path.basename(f),
            })
    out.sort(key=lambda x: (x["type"], x["name"]))
    return out


def _type_name(typ: str, loc: dict, loc_lang: str) -> str:
    if not typ:
        return ""
    cands = list(TYPE_LOC_CANDIDATES.get(typ, ())) + [typ + "_attribute", "accolade_" + typ, typ]
    v = _pick(loc, cands)
    if v and not _looks_raw(v, typ) and not (loc_lang == "simp_chinese" and not _has_cjk(v)):
        return v
    pair = TYPE_NAMES.get(typ)
    if pair:
        return pair[0] if loc_lang == "simp_chinese" else pair[1]
    return typ


def _has_cjk(s: str) -> bool:
    return any("\u4e00" <= c <= "\u9fff" for c in (s or ""))


def _looks_raw(v: str, typ: str) -> bool:
    """判断取到的名字是不是「原始键」而非真译名。

    游戏里大量类型名写成 `light_cavalry: "[light_cavalry|E]"` 这种自引用，
    清洗后正好等于键名本身 —— 这种要丢掉，改用内置译名表。
    """
    s = (v or "").strip().lower()
    if not s or s.startswith("["):
        return True
    norm = s.replace(" ", "_")
    return norm in (typ, typ + "s") or norm == typ.rstrip("s")


def _pillar(pkey: str, loc: dict) -> dict:
    if not pkey:
        return {}
    return {
        "key": pkey,
        "name": loc.get(pkey + "_name") or loc.get(pkey) or pkey,
        "desc": loc.get(pkey + "_desc") or "",
    }


def _collect_cultures(game_root: str, loc: dict, trad_map: dict) -> list:
    out = []
    for f in sorted(glob.glob(os.path.join(game_root, D_CULT, "*.txt"))):
        for key, body in _top_blocks(_read(f)):
            trad_keys = _list(_sub(body, "traditions"))
            parents = _list(_sub(body, "parents"))
            out.append({
                "key": key,
                "name": loc.get(key) or key,
                "ethos": _pillar(_val(body, "ethos") or "", loc),
                "heritage": _pillar(_val(body, "heritage") or "", loc),
                "language": _pillar(_val(body, "language") or "", loc),
                "martial": _pillar(_val(body, "martial_custom") or "", loc),
                "head": _pillar(_val(body, "head_determination") or "", loc),
                "traditions": [trad_map.get(k, {"key": k, "name": loc.get(k + "_name") or k}) for k in trad_keys],
                "parents": [{"key": p, "name": loc.get(p) or p} for p in parents],
                "created": _val(body, "created") or "",
            })
    out.sort(key=lambda x: x["name"])
    return out


def _collect_traditions(game_root: str, loc: dict) -> tuple:
    """返回 (明细列表, key→简要 映射)。分类取自文件名。"""
    items, mapping = [], {}
    for f in sorted(glob.glob(os.path.join(game_root, D_TRAD, "*.txt"))):
        stem = os.path.basename(f)[:-4]
        cat_key = re.sub(r"^[0-9]+_", "", stem).replace("_traditions", "")
        cat = TRAD_CATS.get(cat_key)
        if not cat:
            cat = "扩展内容" if cat_key != "debug" else "调试"
        for key, body in _top_blocks(_read(f)):
            if cat == "调试":
                continue
            it = {
                "key": key,
                "name": loc.get(key + "_name") or loc.get(key) or key,
                "desc": loc.get(key + "_desc") or "",
                "cat": cat,
            }
            items.append(it)
            mapping[key] = {"key": key, "name": it["name"]}
    items.sort(key=lambda x: (x["cat"], x["name"]))
    return items, mapping


# ---------------------------------------------------------------- 缓存与入口

def _sig(paths) -> str:
    """用目录 mtime + 文件数做指纹，游戏更新后自动失效。"""
    parts = []
    for p in paths:
        try:
            st = os.stat(p)
            parts.append("%s:%d:%d" % (os.path.basename(p.rstrip("\\/")), int(st.st_mtime), int(st.st_size)))
        except Exception:
            parts.append("-")
    return "|".join(parts)


def _cache_path(game: str, loc_lang: str) -> str:
    return os.path.join(APP_DIR, "wiki_cache_%s_%s.json" % (game, loc_lang))


def _scripts_root(game_root: str) -> str:
    """CK3 的脚本在 `<安装目录>\\game\\` 下；也兼容直接指向该层的情况。"""
    game_root = (game_root or "").strip()
    if not game_root:
        return ""
    inner = os.path.join(game_root, "game")
    if os.path.isdir(os.path.join(inner, D_MAA)):
        return inner
    return game_root


def load(game: str, game_root: str, ui_lang: str = "zh", force: bool = False) -> dict:
    """读取（并缓存）百科数据。"""
    game = (game or "ck3").lower()
    if game not in SUPPORTED_GAMES:
        return {"ok": True, "supported": False, "game": game, "maa": [], "cultures": [],
                "traditions": [], "counts": {"maa": 0, "cultures": 0, "traditions": 0}}

    game_root = _scripts_root(game_root)
    if not game_root or not os.path.isdir(os.path.join(game_root, D_MAA)):
        return {"ok": False, "supported": True, "game": game,
                "error": "没找到游戏本体目录（common/men_at_arms_types），请在设置里确认游戏根目录"}

    loc_lang = LANG_MAP.get((ui_lang or "zh").lower(), "simp_chinese")
    dirs = [os.path.join(game_root, d) for d in (D_MAA, D_CULT, D_TRAD)]
    loc_dir = os.path.join(game_root, D_LOC, loc_lang)
    dirs.append(loc_dir)
    sig = _sig(dirs)
    cpath = _cache_path(game, loc_lang)

    mem_key = "%s|%s|%s" % (game, loc_lang, game_root.lower())
    with _LOCK:
        hit = _MEM.get(mem_key)
        if hit and hit[0] == sig and not force:
            data = dict(hit[1])
            data["cached"] = True
            return data

        if not force:
            try:
                with open(cpath, encoding="utf-8") as f:
                    blob = json.load(f)
                if blob.get("wiki_ver") == WIKI_VER and blob.get("sig") == sig \
                        and (blob.get("root") or "").lower() == game_root.lower():
                    data = blob.get("data") or {}
                    _MEM[mem_key] = (sig, data)
                    data = dict(data)
                    data["cached"] = True
                    return data
            except Exception:
                pass

        t0 = time.time()
        # 1) 先扫脚本，收集需要用到的本地化键（避免把 30 万条全塞内存）
        needed = set()
        for f in glob.glob(os.path.join(game_root, D_MAA, "*.txt")):
            for key, body in _top_blocks(_read(f)):
                needed.add(key)
                t = _val(body, "type") or ""
                if t:
                    needed.update(TYPE_LOC_CANDIDATES.get(t, ()))
                    needed.update((t + "_attribute", "accolade_" + t, t))
                tb = _sub(body, "terrain_bonus")
                if tb:
                    needed.update(k for k, _ in _top_blocks(tb))
                wb = _sub(body, "winter_bonus")
                if wb:
                    needed.update(k for k, _ in _top_blocks(wb))
                cb = _sub(body, "counters")
                if cb:
                    needed.update(_pairs(cb).keys())

        raw_trads = []
        for f in glob.glob(os.path.join(game_root, D_TRAD, "*.txt")):
            for key, _body in _top_blocks(_read(f)):
                raw_trads.append(key)
        for key, body in [(k, b) for f in glob.glob(os.path.join(game_root, D_CULT, "*.txt"))
                          for k, b in _top_blocks(_read(f))]:
            needed.add(key)
            for pk in ("ethos", "heritage", "language", "martial_custom", "head_determination"):
                v = _val(body, pk)
                if v:
                    needed.update((v, v + "_name", v + "_desc"))
            needed.update(_list(_sub(body, "parents")))
        for key in raw_trads:
            needed.update((key, key + "_name", key + "_desc"))

        raw = _scan_loc(loc_dir, needed)

        # 引用展开：部分译名写成 `$其他键$`，把被引用的键也补扫回来再展开
        refs = set()
        for v in raw.values():
            refs.update(_REF_PAT.findall(v))
        refs -= set(raw.keys())
        if refs:
            for k, v in _scan_loc(loc_dir, refs).items():
                raw.setdefault(k, v)
        loc = _finalize(raw)

        # 2) 正式抽取
        traditions, trad_map = _collect_traditions(game_root, loc)
        maa = _collect_maa(game_root, loc, loc_lang)
        cultures = _collect_cultures(game_root, loc, trad_map)

        data = {
            "ok": True,
            "supported": True,
            "game": game,
            "lang": loc_lang,
            "uiLang": ui_lang,
            "maa": maa,
            "cultures": cultures,
            "traditions": traditions,
            "counts": {"maa": len(maa), "cultures": len(cultures), "traditions": len(traditions)},
            "buildMs": int((time.time() - t0) * 1000),
            "types": sorted({m["type"] for m in maa if m["type"]}),
            "tradCats": sorted({t["cat"] for t in traditions}),
        }
        _MEM[mem_key] = (sig, data)

        try:                            # 落盘缓存（丢包也无所谓，只是下次重建）
            with open(cpath, "w", encoding="utf-8") as f:
                json.dump({"wiki_ver": WIKI_VER, "sig": sig, "root": game_root, "data": data},
                          f, ensure_ascii=False)
        except Exception:
            pass

        out = dict(data)
        out["cached"] = False
        return out

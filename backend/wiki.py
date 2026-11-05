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

WIKI_VER = 15                            # 解析逻辑变更时 +1，强制重建旧缓存

# 目录名（相对 gameRoot）
D_MAA = os.path.join("common", "men_at_arms_types")
D_CULT = os.path.join("common", "culture", "cultures")
D_TRAD = os.path.join("common", "culture", "traditions")
D_PILLAR = os.path.join("common", "culture", "pillars")
D_LOC = "localization"
D_SV = os.path.join("common", "script_values")   # 脚本值：效果里 ScriptValue('x') 的真实数字
D_RELIGION = os.path.join("common", "religion", "religion_types")
D_TENET = os.path.join("common", "religion", "tenet_types")
D_DOCTRINE = os.path.join("common", "religion", "doctrine_types")
D_INNOV = os.path.join("common", "culture", "innovations")

# 百科分类注册表：以后加新分类，写一个 _collect_xxx() 并在这里登记即可
CATEGORIES = [
    {"id": "maa", "dir": D_MAA, "collect": "_collect_maa"},
    {"id": "cultures", "dir": D_CULT, "collect": "_collect_cultures"},
    {"id": "traditions", "dir": D_TRAD, "collect": "_collect_traditions"},
    {"id": "religions", "dir": D_RELIGION, "collect": "_collect_religions"},
    {"id": "tenets", "dir": D_TENET, "collect": "_collect_tenets"},
    {"id": "doctrines", "dir": D_DOCTRINE, "collect": "_collect_doctrines"},
    {"id": "innovations", "dir": D_INNOV, "collect": "_collect_innovations"},
]

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

# 冬季等级：游戏里没有对应本地化键，只能内置译名
WINTER_NAMES = {
    "mild_winter": ("温和冬季", "Mild Winter"),
    "normal_winter": ("普通冬季", "Normal Winter"),
    "harsh_winter": ("严寒冬季", "Harsh Winter"),
}

# 基础克制闭环：轻步 → 重步 → 长枪 → 轻重骑兵 → 弓箭手 → 轻步
COUNTER_CYCLE = [
    ("skirmishers", "heavy_infantry"),
    ("heavy_infantry", "pikemen"),
    ("pikemen", "light_cavalry"),
    ("pikemen", "heavy_cavalry"),
    ("light_cavalry", "archers"),
    ("heavy_cavalry", "archers"),
    ("archers", "skirmishers"),
]

# 地形/冬季加成里要展示的属性
MOD_KEYS = ("damage", "toughness", "pursuit", "screen")

# 常见修正键的中文名：游戏里没有单独本地化，这里补一份（只收高频的）
MODIFIER_NAMES = {
    "accolade_glory_gain_mult": "荣耀获取",
    "knight_limit": "骑士数量上限",
    "character_travel_speed_mult": "旅行速度",
    "movement_speed": "行军速度",
    "movement_speed_land_raiding": "陆上劫掠行军速度",
    "men_at_arms_recruitment_cost": "兵士招募花费",
    "knight_effectiveness_mult": "骑士战斗力",
    "character_travel_safety_mult": "旅行安全",
    "hard_casualty_modifier": "伤亡人数",
    "cultural_acceptance_gain_mult": "文化接受度增长",
    "diplomatic_range_mult": "外交范围",
    "maa_toughness_mult": "兵士坚韧",
    "maa_damage_mult": "兵士伤害",
    "prowess_per_piety_level": "每级虔诚提供勇武",
    "faith_conversion_piety_cost_mult": "改信花费",
    "development_growth": "发展度成长",
    "monthly_income_mult": "每月收入",
    "dynasty_house_opinion": "家族好感",
}

# `partners = { ... }` 里的键（婚姻类传统没有 parameters，效果全在这）
PARTNER_NAMES = {
    "number_of_spouses": "配偶数量上限",
    "number_of_consorts": "伴侣数量上限",
    "number_of_consorts_by_tier": "按头衔等级的伴侣上限",
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


def _sub_all(body: str, name: str) -> list:
    """取 body 内所有 `name = { ... }` 的内部文本（用于 dlc_tradition 这种重复块）。"""
    out = []
    pat = re.compile(r"(?:^|[\s{])" + re.escape(name) + r"\s*=\s*\{")
    i = 0
    while True:
        m = pat.search(body, i)
        if not m:
            break
        start = m.end()
        d, j = 1, start
        while j < len(body) and d > 0:
            c = body[j]
            if c == "{":
                d += 1
            elif c == "}":
                d -= 1
            j += 1
        out.append(body[start:j - 1])
        i = j
    return out


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
_REF_PAT = re.compile(r"\$([A-Za-z0-9_.\-]+)\$")          # $变量$
_TOK_PAT = re.compile(r"\[([A-Za-z0-9_.\-]+)\|E\]", re.I)  # [house|E]
_FUNC_PAT = re.compile(r"\[[^\[\]]*\([^\[\]]*\)[^\[\]]*\]")  # [GetTrait('x').GetName(...)]
_TRAIT_PAT = re.compile(r"GetTrait\(\s*'([A-Za-z0-9_\-]+)'\s*\)", re.I)
_ICON_PAT = re.compile(r"\[[a-z0-9_]+_i\]")                # 图标占位 [prestige_i]
_MARK_PAT = re.compile(r"#[A-Za-z0-9_!]*")                 # 排版标记 #P #! #bold
_SV_PAT = re.compile(r"\[EmptyScope\.ScriptValue\(\s*'([A-Za-z0-9_]+)'\s*\)\s*\|0\]")


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


def _concept(key: str, raw: dict) -> str:
    """`[house|E]` 这类引用：优先直取，其次 game_concept_ 前缀，最后退回键名。"""
    for k in (key, "game_concept_" + key):
        v = raw.get(k)
        if v:
            return v
    return key


def _trait_name(key: str, raw: dict) -> str:
    for k in (key, "trait_" + key, "game_concept_" + key):
        v = raw.get(k)
        if v:
            return v
    return _title(key)


def _resolve(text: str, raw: dict, sv: dict | None = None, depth: int = 3) -> str:
    """展开 $变量$、[概念|E]、[GetTrait('x')...] 与脚本值，丢掉算不出来的函数。"""
    if not text:
        return text
    sv = sv or {}
    if "$" in text:
        def rep(m):
            k = m.group(1)
            if k in raw and depth > 0:
                return _resolve(raw[k], raw, sv, depth - 1)
            return k
        text = _REF_PAT.sub(rep, text)
    # 脚本值：[EmptyScope.ScriptValue('x')|0] → 脚本里定义的真实数字
    text = _SV_PAT.sub(lambda m: str(sv.get(m.group(1), "?")), text)
    if "[" in text:
        def frep(m):
            s = m.group(0)
            tm = _TRAIT_PAT.search(s)
            if tm:
                return _trait_name(tm.group(1), raw)
            return ""          # 其他脚本函数（取对象名等）直接丢掉，别留孤立的 ?
        text = _FUNC_PAT.sub(frep, text)
        text = _TOK_PAT.sub(lambda m: _concept(m.group(1), raw), text)
    return text


def _ref_keys(text: str) -> set:
    """一段文本里所有需要回查本地化的键（用于二次扫描）。"""
    ks = set()
    ks.update(_REF_PAT.findall(text))
    ks.update(_TOK_PAT.findall(text))
    for t in _TRAIT_PAT.findall(text):
        ks.update((t, "trait_" + t, "game_concept_" + t))
    ks.update("game_concept_" + k for k in list(ks))
    return ks


def _clean(s: str) -> str:
    """去掉排版标记、图标占位，压掉多余空白。"""
    s = s.replace("\\n", " ")
    s = _MARK_PAT.sub("", s)
    s = _ICON_PAT.sub("", s)
    s = re.sub(r"@[A-Za-z0-9_]+!?", "", s)   # @piety_icon! / @government_type_x! 这类内联标记
    s = re.sub(r"\[[^\[\]]{1,80}\]", "", s)   # 剩下的脚本路径引用，如 [ROOT.Faith.XXX]
    s = re.sub(r"[ \t]{2,}", " ", s)
    return s.strip()


def _title(key: str) -> str:
    """脚本键 → 可读英文名（`light_footmen` → `Light Footmen`）。"""
    return " ".join(w[:1].upper() + w[1:] for w in str(key or "").split("_") if w)


def _finalize(raw: dict, sv: dict | None = None) -> dict:
    """引用展开 + 清洗，得到可直接显示的译名表。"""
    return {k: _clean(_resolve(v, raw, sv)) for k, v in raw.items()}


def _pick(loc: dict, keys) -> str:
    for k in keys:
        v = loc.get(k)
        if v:
            return v
    return ""


# ---------------------------------------------------------------- 各分类抽取

def _mod_list(inner: str, loc: dict) -> list:
    """解析 terrain_bonus / winter_bonus 这类「条目 = { damage = X ... }」块。"""
    out = []
    for bk, bv in _top_blocks(inner):
        mods = {}
        for k, v in _pairs(bv).items():
            if k in MOD_KEYS:
                iv = _int(v)
                if iv is not None:
                    mods[k] = iv
        out.append({"key": bk, "name": loc.get(bk) or bk, "mods": mods})
    return out


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

            terrain = _mod_list(_sub(body, "terrain_bonus") or "", loc)

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
                    pair = WINTER_NAMES.get(wk)
                    wname = loc.get(wk) or (pair[0] if (pair and loc_lang == "simp_chinese")
                                            else (pair[1] if pair else None)) or _title(wk)
                    mods = {}
                    for k, v in _pairs(wv).items():
                        if k in MOD_KEYS:
                            iv = _int(v)
                            if iv is not None:
                                mods[k] = iv
                    winter.append({"key": wk, "name": wname, "mods": mods})

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
                "nameEn": _title(key),
                "flavor": loc.get(key + "_flavor") or "",
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


def _fmt_num(v: str) -> str:
    """把修正值格式化成 +10 / -5 / +25% 这样。"""
    try:
        f = float(v)
    except Exception:
        return ""
    if 0 < abs(f) < 1:
        return "%+.0f%%" % (f * 100)
    if f == int(f):
        return "%+d" % int(f)
    return "%+g" % f


def _collect_script_values(game_root: str) -> dict:
    """读 `common/script_values/*.txt` 的顶层常量，用来还原效果里的 ScriptValue('x')。"""
    out, refs = {}, {}
    for f in glob.glob(os.path.join(game_root, D_SV, "*.txt")):
        text = _read(f)
        for m in re.finditer(r"^([A-Za-z0-9_]+)\s*=\s*(-?\d+(?:\.\d+)?)\s*$", text, re.M):
            out[m.group(1)] = m.group(2)
        # 顶层别名：`xxx_gain = minor_development_progress_gain`
        for m in re.finditer(r"^([A-Za-z0-9_]+)\s*=\s*([A-Za-z_][A-Za-z0-9_]*)\s*$", text, re.M):
            if m.group(2) not in ("yes", "no", "true", "false"):
                refs[m.group(1)] = m.group(2)
        for k, b in _top_blocks(text):
            v = _val(b, "value")
            if not v:
                continue
            if re.fullmatch(r"-?\d+(?:\.\d+)?", v):
                out.setdefault(k, v)
            elif re.fullmatch(r"[A-Za-z0-9_]+", v):
                refs[k] = v          # 别名：`xxx_tt = { value = xxx }`
    # 别名链解析（最多 3 层），让 `_tt` 这类包装键也能取到数字
    for _ in range(3):
        for k, v in list(refs.items()):
            if v in out:
                out.setdefault(k, out[v])
                refs.pop(k, None)
    return out


def _trad_effects(body: str, loc: dict) -> list:
    """传统效果：parameters 对应的官方说明 + 有译名的修正值 + 婚姻类 partners。"""
    eff = []
    pb = _sub(body, "parameters")
    if pb:
        for p in _pairs(pb).keys():
            t = loc.get("culture_parameter_" + p)
            if t:
                eff.append(t)
    # 婚姻类传统（多配偶制 / 纳妾制）没有 parameters，效果写在 partners 里
    partners = _sub(body, "partners")
    if partners:
        for k, v in _pairs(partners).items():
            nm = PARTNER_NAMES.get(k) or loc.get(k)
            if nm and re.fullmatch(r"-?\d+(?:\.\d+)?", v):
                eff.append("%s %s" % (nm, v))
    for block in ("character_modifier", "culture_modifier"):
        mb = _sub(body, block)
        if not mb:
            continue
        for k, v in _pairs(mb).items():
            if k.startswith("ai_"):        # AI 行为参数，对玩家没意义
                continue
            nm = loc.get(k) or MODIFIER_NAMES.get(k)
            if nm:
                eff.append("%s %s" % (nm, _fmt_num(v)))
    # 去重保序
    seen, out = set(), []
    for e in eff:
        e = e.strip()
        if e and e not in seen:
            seen.add(e)
            out.append(e)
    return out


def _collect_cultures(game_root: str, loc: dict, trad_map: dict) -> list:
    out = []
    for f in sorted(glob.glob(os.path.join(game_root, D_CULT, "*.txt"))):
        for key, body in _top_blocks(_read(f)):
            parents = _list(_sub(body, "parents"))

            trad_entries = []
            for k in _list(_sub(body, "traditions")):
                trad_entries.append(_trad_entry(k, loc, trad_map, False, ""))
            # DLC 传统：traditions 块为空的文化（如「汉」）全靠这个
            for dbody in _sub_all(body, "dlc_tradition"):
                tk = _val(dbody, "trait")
                if tk:
                    trad_entries.append(_trad_entry(tk, loc, trad_map, True,
                                                    _val(dbody, "requires_dlc_flag") or ""))

            out.append({
                "key": key,
                "name": loc.get(key) or key,
                "ethos": _pillar(_val(body, "ethos") or "", loc),
                "heritage": _pillar(_val(body, "heritage") or "", loc),
                "language": _pillar(_val(body, "language") or "", loc),
                "martial": _pillar(_val(body, "martial_custom") or "", loc),
                "head": _pillar(_val(body, "head_determination") or "", loc),
                "traditions": trad_entries,
                "parents": [{"key": p, "name": loc.get(p) or p} for p in parents],
                "created": _val(body, "created") or "",
            })
    out.sort(key=lambda x: x["name"])
    return out


def _trad_entry(key: str, loc: dict, trad_map: dict, dlc: bool, requires: str) -> dict:
    base = trad_map.get(key) or {}
    return {
        "key": key,
        "name": base.get("name") or loc.get(key + "_name") or loc.get(key) or key,
        "desc": base.get("desc") or loc.get(key + "_desc") or "",
        "effects": base.get("effects") or [],
        "dlc": dlc,
        "requires": requires,
    }


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
                "effects": _trad_effects(body, loc),
                "cat": cat,
            }
            items.append(it)
            mapping[key] = {"key": key, "name": it["name"], "desc": it["desc"],
                            "effects": it["effects"]}
    items.sort(key=lambda x: (x["cat"], x["name"]))
    return items, mapping


# ---------------------------------------------------------------- 扩展百科（通用条目）

def _generic_effects(body: str, loc: dict, prefixes=()) -> list:
    """从 parameters / special_parameters / 修正块里抽效果文案。

    `parameters` 有两种写法：裸标识符列表 或 `k = v`，都要能读。
    """
    eff = []
    for blk in ("parameters", "special_parameters"):
        pb = _sub(body, blk)
        if not pb:
            continue
        if "=" in pb:
            pairs, keys = _pairs(pb), list(_pairs(pb).keys())
        else:
            pairs, keys = {}, _list(pb)
        for p in keys:
            txt = None
            for pre in prefixes:
                txt = loc.get(pre + p)
                if txt:
                    break
            if txt:
                eff.append(txt)
            else:
                v = pairs.get(p)
                if v and re.fullmatch(r"-?\d+(?:\.\d+)?", v):
                    nm = PARTNER_NAMES.get(p) or MODIFIER_NAMES.get(p) or loc.get(p)
                    if nm:
                        eff.append("%s %s" % (nm, v))
    for blk in ("character_modifier", "personal_tenet_modifier", "culture_modifier"):
        mb = _sub(body, blk)
        if not mb:
            continue
        for k, v in _pairs(mb).items():
            nm = loc.get(k) or MODIFIER_NAMES.get(k)
            if nm:
                eff.append("%s %s" % (nm, _fmt_num(v)))
    seen, out = set(), []
    for e in eff:
        e = e.strip()
        if e and e not in seen:
            seen.add(e)
            out.append(e)
    return out


def _entry(key, name, desc, cat_key, cat_label, attrs=None, effects=None, links=None):
    return {
        "key": key, "name": name, "desc": desc,
        "cat": cat_label or "", "catKey": cat_key or "",
        "attrs": attrs or [], "effects": effects or [], "links": links or [],
    }


def _collect_religions(game_root: str, loc: dict, doc_names: dict) -> list:
    out = []
    for f in sorted(glob.glob(os.path.join(game_root, D_RELIGION, "*.txt"))):
        for key, body in _top_blocks(_read(f)):
            fam = ""
            rd = _sub(body, "religion_details")
            if rd:
                fam = _val(rd, "family") or ""
            docs = re.findall(r"^\tdoctrine\s*=\s*([a-z_0-9]+)", body, re.M)
            # 去重保序
            docs = list(dict.fromkeys(docs))
            out.append(_entry(
                key, loc.get(key) or key, loc.get(key + "_desc") or "",
                fam, loc.get(fam) or fam or "其他",
                attrs=[{"label": "教义", "value": "%d 项" % len(docs)}],
                links=[doc_names.get(d) or d for d in docs],
            ))
    out.sort(key=lambda x: x["name"])
    return out


def _collect_tenets(game_root: str, loc: dict) -> list:
    out = []
    for f in sorted(glob.glob(os.path.join(game_root, D_TENET, "*.txt"))):
        for key, body in _top_blocks(_read(f)):
            dlc = _val(body, "requires_dlc_flag") or ""
            cat = "需 DLC" if dlc else "通用"
            out.append(_entry(
                key, loc.get(key + "_name") or loc.get(key) or _title(key),
                loc.get(key + "_desc") or "",
                "dlc" if dlc else "base", cat,
                effects=_generic_effects(body, loc,
                                         ("doctrine_parameter_", "culture_parameter_", "tenet_parameter_")),
            ))
    out.sort(key=lambda x: x["name"])
    return out


def _collect_doctrines(game_root: str, loc: dict) -> list:
    out = []
    for f in sorted(glob.glob(os.path.join(game_root, D_DOCTRINE, "*.txt"))):
        for key, body in _top_blocks(_read(f)):
            g = _val(body, "doctrine_group_type") or ""
            gname = loc.get(g + "_name") or loc.get(g) or g or "其他"
            out.append(_entry(
                key, loc.get(key + "_name") or loc.get(key) or _title(key),
                loc.get(key + "_desc") or "",
                g, gname,
                effects=_generic_effects(body, loc, ("doctrine_parameter_", "culture_parameter_")),
            ))
    out.sort(key=lambda x: (x["cat"], x["name"]))
    return out


def _collect_innovations(game_root: str, loc: dict) -> list:
    out = []
    for f in sorted(glob.glob(os.path.join(game_root, D_INNOV, "*.txt"))):
        for key, body in _top_blocks(_read(f)):
            era = _val(body, "culture_era") or ""
            grp = _val(body, "group") or ""
            skill = _val(body, "skill") or ""
            attrs = []
            for label, v in (("类别", grp), ("相关能力", skill)):
                nm = loc.get(v) or (v if v else "")
                if nm:
                    attrs.append({"label": label, "value": nm})
            out.append(_entry(
                key, loc.get(key) or key, loc.get(key + "_desc") or "",
                era, loc.get(era) or era or "其他",
                attrs=attrs,
                effects=_generic_effects(body, loc, ("culture_parameter_",)),
            ))
    out.sort(key=lambda x: (x["cat"], x["name"]))
    return out


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
                "traditions": [], "religions": [], "tenets": [], "doctrines": [],
                "innovations": [], "counterCycle": [], "typesInfo": [],
                "counts": {"maa": 0, "cultures": 0, "traditions": 0,
                           "religions": 0, "tenets": 0, "doctrines": 0, "innovations": 0}}

    game_root = _scripts_root(game_root)
    if not game_root or not os.path.isdir(os.path.join(game_root, D_MAA)):
        return {"ok": False, "supported": True, "game": game,
                "error": "没找到游戏本体目录（common/men_at_arms_types），请在设置里确认游戏根目录"}

    loc_lang = LANG_MAP.get((ui_lang or "zh").lower(), "simp_chinese")
    dirs = [os.path.join(game_root, d) for d in
            (D_MAA, D_CULT, D_TRAD, D_SV, D_RELIGION, D_TENET, D_DOCTRINE, D_INNOV)]
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
                needed.update((key, key + "_flavor"))
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
            for key, body in _top_blocks(_read(f)):
                raw_trads.append(key)
                # 传统效果：parameters → culture_parameter_*；修正块 → 修正名
                pb = _sub(body, "parameters")
                if pb:
                    needed.update("culture_parameter_" + p for p in _pairs(pb).keys())
                for block in ("character_modifier", "culture_modifier"):
                    mb = _sub(body, block)
                    if mb:
                        needed.update(_pairs(mb).keys())
        for key, body in [(k, b) for f in glob.glob(os.path.join(game_root, D_CULT, "*.txt"))
                          for k, b in _top_blocks(_read(f))]:
            needed.add(key)
            for pk in ("ethos", "heritage", "language", "martial_custom", "head_determination"):
                v = _val(body, pk)
                if v:
                    needed.update((v, v + "_name", v + "_desc"))
            needed.update(_list(_sub(body, "parents")))
            for dbody in _sub_all(body, "dlc_tradition"):
                tk = _val(dbody, "trait")
                if tk:
                    needed.update((tk, tk + "_name", tk + "_desc"))
        for key in raw_trads:
            needed.update((key, key + "_name", key + "_desc"))

        # 扩展百科：宗教 / 信条 / 教义 / 革新
        for d, suffixes in ((D_RELIGION, ("", "_desc")),
                            (D_TENET, ("_name", "_desc")),
                            (D_DOCTRINE, ("_name", "_desc")),
                            (D_INNOV, ("", "_desc"))):
            for fp in glob.glob(os.path.join(game_root, d, "*.txt")):
                for k, body in _top_blocks(_read(fp)):
                    for suf in suffixes:
                        needed.add(k + suf)
                    # 分类字段
                    for fld in ("doctrine_group_type", "culture_era", "group", "skill"):
                        v = _val(body, fld)
                        if v:
                            needed.update((v, v + "_name", "game_concept_" + v))
                    rd = _sub(body, "religion_details")
                    if rd:
                        fam = _val(rd, "family")
                        if fam:
                            needed.update((fam, fam + "_name"))
                    # 效果参数（各前缀都试）
                    for blk in ("parameters", "special_parameters", "character_modifier",
                                "personal_tenet_modifier", "culture_modifier"):
                        inner = _sub(body, blk)
                        if not inner:
                            continue
                        ps = (list(_pairs(inner).keys()) if "=" in inner else _list(inner))
                        for p in ps:
                            needed.update((p, "culture_parameter_" + p,
                                           "doctrine_parameter_" + p,
                                           "tenet_parameter_" + p))
                    # 宗教引用的教义名
                    for dk in re.findall(r"^\tdoctrine\s*=\s*([a-z_0-9]+)", body, re.M):
                        needed.update((dk, dk + "_name"))

        raw = _scan_loc(loc_dir, needed)

        # 引用展开：译名/效果里会引用 `$键$`、`[概念|E]`、`[GetTrait('x')]`，
        # 把这些被引用的键也补扫回来（做两轮，够覆盖二级引用）
        for _round in range(2):
            refs = set()
            for v in raw.values():
                refs.update(_ref_keys(v))
            refs -= set(raw.keys())
            if not refs:
                break
            for k, v in _scan_loc(loc_dir, refs).items():
                raw.setdefault(k, v)
        sv = _collect_script_values(game_root)
        loc = _finalize(raw, sv)

        # 2) 正式抽取
        traditions, trad_map = _collect_traditions(game_root, loc)
        maa = _collect_maa(game_root, loc, loc_lang)
        cultures = _collect_cultures(game_root, loc, trad_map)
        doctrines = _collect_doctrines(game_root, loc)
        doc_names = {d["key"]: d["name"] for d in doctrines}
        religions = _collect_religions(game_root, loc, doc_names)
        tenets = _collect_tenets(game_root, loc)
        innovations = _collect_innovations(game_root, loc)

        # 3) 克制关系：闭环 + 每个兵种的「被谁克制」
        tname = {}
        for m in maa:
            tname.setdefault(m["type"], m["typeName"])
        cycle = [{"from": {"key": a, "name": tname.get(a, a)},
                  "to": {"key": b, "name": tname.get(b, b)}}
                 for a, b in COUNTER_CYCLE]
        countered = {}
        for a, b in COUNTER_CYCLE:
            countered.setdefault(b, []).append({"key": a, "name": tname.get(a, a)})
        for m in maa:
            m["counteredBy"] = countered.get(m["type"], [])

        cnt = {}
        for m in maa:
            cnt[m["type"]] = cnt.get(m["type"], 0) + 1
        types_info = [{"key": t, "name": tname.get(t, t), "count": cnt[t]}
                      for t in sorted(cnt, key=lambda x: -cnt[x])]

        data = {
            "ok": True,
            "supported": True,
            "game": game,
            "lang": loc_lang,
            "uiLang": ui_lang,
            "maa": maa,
            "cultures": cultures,
            "traditions": traditions,
            "religions": religions,
            "tenets": tenets,
            "doctrines": doctrines,
            "innovations": innovations,
            "counts": {
                "maa": len(maa), "cultures": len(cultures), "traditions": len(traditions),
                "religions": len(religions), "tenets": len(tenets),
                "doctrines": len(doctrines), "innovations": len(innovations),
            },
            "buildMs": int((time.time() - t0) * 1000),
            "types": sorted({m["type"] for m in maa if m["type"]}),
            "typesInfo": types_info,
            "counterCycle": cycle,
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

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

WIKI_VER = 30                            # 解析逻辑变更时 +1，强制重建旧缓存

# 勋号兵种（any_accolade）里 accolade_attribute_level 的 type 取值全集：
# 部分是 _attribute 后缀的勋号属性键（在 accolades 本地化里有中文名），
# 部分直接是兵种类型名（heavy_infantry 等，靠 _type_name 兜底）。
# 这里集中列出，构建时补扫进 loc，保证招募条件能显示中文属性名。
ACCOLADE_ATTR_KEYS = (
    "archer_attribute", "archer_cavalry", "archers", "camel_cavalry", "camelry_attribute",
    "crossbowmen_attribute", "elephant_cavalry", "elephantry_attribute", "gunpowder",
    "gunpowder_attribute", "heavy_cavalry", "heavy_infantry", "horse_archer_attribute",
    "lancer_attribute", "light_cavalry", "outrider_attribute", "pike_attribute", "pikemen",
    "skirmisher_attribute", "skirmishers", "vanguard_attribute",
)

# 文化兵种解锁映射（构建时填充）：
#   UNLOCK_MAA : token(如 unlock_maa_archers_of_the_nile / unlock_infantry_samurai_units)
#                → [(kind, name)]   供 can_recruit 分支反查授予它的传统/革新
#   MAA_UNLOCK : maa_key(如 archers_of_the_nile / caballero)
#                → {"grantors":[(kind,name)], "region":地区令牌|None}
#                供「无 can_recruit、纯靠革新解锁」的文化兵种补文化/地区限制
UNLOCK_MAA = {}
MAA_UNLOCK = {}

# 地理区域令牌 → 中文/英文名（地理区域定义里无 name 字段，引擎才生成显示名，
# 这里只收录确实出现在兵种革新 region 限定上的几个，其余走兜底美化）。
REGION_NAMES = {
    "world_europe_west_iberia": ("伊比利亚地区", "Iberia"),
    "ghw_region_germany": ("德意志地区", "Germany"),
    "ghw_region_britannia": ("不列颠地区", "Britannia"),
    "world_india": ("印度地区", "India"),
    "custom_sahel_proper": ("萨赫勒地区", "The Sahel"),
    "world_innovation_camels": ("沙漠/骆驼地区", "Camel regions"),
    "world_asia_north_east": ("东北亚地区", "Northeast Asia"),
    "custom_eastern_roman_empire": ("东罗马帝国地区", "Eastern Roman Empire"),
    "world_himalaya": ("喜马拉雅地区", "Himalaya"),
    "world_steppe": ("草原地区", "Steppe"),
    "world_transoxiana": ("河中地区", "Transoxiana"),
}

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
    "accolade_glory_gain_mult": ("荣耀获取","Glory gain"),
    "knight_limit": ("骑士数量上限","Knight limit"),
    "character_travel_speed_mult": ("旅行速度","Travel speed"),
    "movement_speed": ("行军速度","Movement speed"),
    "movement_speed_land_raiding": ("陆上劫掠行军速度","Land raiding movement speed"),
    "men_at_arms_recruitment_cost": ("兵士招募花费","Men-at-arms recruitment cost"),
    "knight_effectiveness_mult": ("骑士战斗力","Knight effectiveness"),
    "character_travel_safety_mult": ("旅行安全","Travel safety"),
    "hard_casualty_modifier": ("伤亡人数","Casualties"),
    "cultural_acceptance_gain_mult": ("文化接受度增长","Cultural acceptance gain"),
    "diplomatic_range_mult": ("外交范围","Diplomatic range"),
    "maa_toughness_mult": ("兵士坚韧","Men-at-arms toughness"),
    "maa_damage_mult": ("兵士伤害","Men-at-arms damage"),
    "prowess_per_piety_level": ("每级虔诚提供勇武","Prowess per piety level"),
    "faith_conversion_piety_cost_mult": ("改信花费","Faith conversion cost"),
    "development_growth": ("发展度成长","Development growth"),
    "monthly_income_mult": ("每月收入","Monthly income"),
    "dynasty_house_opinion": ("家族好感","Dynasty house opinion"),
    "development_growth_factor": ("发展度成长","Development growth"),
    "men_at_arms_cap": ("兵士上限","Men-at-arms cap"),
    "men_at_arms_limit": ("兵士数量上限","Men-at-arms limit"),
    "men_at_arms_title_limit": ("按头衔的兵士上限","Men-at-arms limit per title"),
    "men_at_arms_maintenance": ("兵士维护费","Men-at-arms maintenance"),
    "monthly_income_from_herd_mult": ("畜群月收入","Monthly income from herds"),
    "naval_movement_speed_mult": ("海军航行速度","Naval movement speed"),
    "domain_tax_mult": ("领地税收","Domain tax"),
    "different_culture_opinion": ("异文化好感","Different culture opinion"),
    "same_faith_opinion": ("同信仰好感","Same faith opinion"),
    "siege_phase_time": ("围城阶段耗时","Siege phase time"),
    "vassal_limit": ("封臣上限","Vassal limit"),
    "same_culture_mercenary_hire_cost_mult": ("同文化雇佣兵花费","Same culture mercenary cost"),
    "mercenary_count_mult": ("雇佣兵数量","Mercenary count"),
    "building_slot_add": ("建筑槽位","Building slots"),
    "epidemic_resistance": ("瘟疫抗性","Epidemic resistance"),
}

# 引擎动态生成的 custom 文案（官方本地化里只有函数，清洗后为空）
STAT_NAMES = {
    "damage": ("伤害","Damage"), "toughness": ("坚韧","Toughness"),
    "pursuit": ("追击","Pursuit"), "screen": ("掩护","Screen"),
    "siege_value": ("攻城","Siege"), "stack": ("规模","Stack"),
}

def _custom_name(key: str, zh: bool = True) -> str:
    m = re.fullmatch(r"unlock_siege_maa_(first|second|third|fourth)_level", key)
    if m:
        cn = {"first": "一", "second": "二", "third": "三", "fourth": "四"}[m.group(1)]
        en = {"first": 1, "second": 2, "third": 3, "fourth": 4}[m.group(1)]
        return ("解锁第%s级地域攻城器械兵士" % cn) if zh else ("Unlocks tier %d regional siege men-at-arms" % en)
    if key.startswith("eligible_accolade_") and key.endswith("_attribute"):
        return "可授予对应勋章属性" if zh else "Grants access to an accolade attribute"
    return ""

# `partners = { ... }` 里的键（婚姻类传统没有 parameters，效果全在这）
PARTNER_NAMES = {
    "number_of_spouses": ("配偶数量上限","Max spouses"),
    "number_of_consorts": ("伴侣数量上限","Max consorts"),
    "number_of_consorts_by_tier": ("按头衔等级的伴侣上限","Consort limit by tier"),
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
_GETTER_PAT = re.compile(r"Get[A-Za-z]+\(\s*['\"]?([A-Za-z0-9_\-]+)['\"]?\s*\)")
_VALUE_REF_PAT = re.compile(r"\$([A-Za-z0-9_]+)\|[^$]*\$")   # $OPINION|=V$ 这类数值占位

# 数值占位：引擎现场算，这里给个可读名字而不是乱码
VALUE_NAMES = {
    "OPINION": ("好感度", "Opinion"),
    "VALUE": ("数值", "Value"),
    "MOD_COMBAT_COUNTER_RESISTANCE": ("克制抗性", "Counter Resistance"),
}

# 本地化里大量 `$任务键$` / `[概念|E]` 引用散落在这些前缀下，
# 只按「本次用到的键」精确扫描会漏掉，扫描时按前缀一并收进来。
WIDE_PREFIXES = ("game_concept_", "task_", "knight_culture_", "MAPMODE_", "trait_",
                 "culture_parameter_", "doctrine_parameter_", "tenet_parameter_")

# 引擎动态生成的文本（loc 里值本身是脚本调用），直接给可读名字
DYNAMIC_NAMES = {
    "knight_culture_player_plural_no_tooltip_lowercase": ("骑士", "Knights"),
    "knight_culture_player_no_tooltip_lowercase": ("骑士", "Knight"),
}

# 游戏里没有独立本地化文案的机制参数，给一份简明的说明
PARAMETER_NAMES = {
    "faith_coherence_high": ("信仰凝聚力较高", "High faith coherence"),
    "faith_coherence_normal": ("信仰凝聚力中等", "Normal faith coherence"),
    "faith_coherence_low": ("信仰凝聚力较低", "Low faith coherence"),
    "can_only_be_learned_in_person": ("只能当面学习（口述传承）", "Can only be learned in person"),
}

# 兵种分类顺序：基础兵种按游戏内克制链排，特殊/地域兵种在后
MAA_TYPE_ORDER = [
    "skirmishers", "heavy_infantry", "archers", "gunpowder", "pikemen",
    "light_cavalry", "heavy_cavalry", "camel_cavalry", "archer_cavalry",
    "elephant_cavalry", "siege_weapon", "nomadic_horde", "peasant_militia",
]
# 文化时代顺序（不能按条目数排，否则「晚期」会跑到「早期」前面）
ERA_ORDER = [
    "culture_era_tribal", "culture_era_early_medieval",
    "culture_era_high_medieval", "culture_era_late_medieval",
]


def _order_index(seq, key, default=999):
    try:
        return seq.index(key)
    except ValueError:
        return default


def _scan_loc(loc_dir: str, needed: set, prefixes=()) -> dict:
    """扫一遍本地化，只保留 needed 里的键（或匹配前缀的键）。返回原始文本。"""
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
                        if not m:
                            continue
                        k = m.group(1)
                        if k not in out and (k in needed or (prefixes and k.startswith(prefixes))):
                            out[k] = m.group(2)
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


def _getter_name(key: str, raw: dict) -> str:
    """`[GetMaA('house_guard').GetName]` 这类：把括号里的键解成中文名。"""
    for cand in (key, key + "_name", "trait_" + key, "game_concept_" + key):
        v = raw.get(cand)
        if v and not v.lstrip().startswith("["):
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
            if "GetTextIcon" in s or "GetIcon" in s:
                return ""          # 图标函数不能展开成文字，否则「宫廷礼制宫廷礼制…」
            tm = _TRAIT_PAT.search(s)
            if tm:
                return _trait_name(tm.group(1), raw)
            gm = _GETTER_PAT.search(s)
            if gm:
                return _getter_name(gm.group(1), raw)
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
    for g in _GETTER_PAT.findall(text):
        ks.update((g, g + "_name", "trait_" + g, "game_concept_" + g))
    ks.update("game_concept_" + k for k in list(ks))
    return ks


def _clean(s: str, zh: bool = True) -> str:
    """去掉排版标记、图标占位，压掉多余空白。"""
    s = s.replace("\\n", " ")
    s = _MARK_PAT.sub("", s)
    s = _ICON_PAT.sub("", s)
    s = re.sub(r"@[A-Za-z0-9_]+!?", "", s)   # @piety_icon! / @government_type_x! 这类内联标记
    s = re.sub(r"\[[^\[\]]{1,80}\]", "", s)   # 剩下的脚本路径引用，如 [ROOT.Faith.XXX]
    s = _VALUE_REF_PAT.sub(
        lambda m: (VALUE_NAMES.get(m.group(1)) or (_title(m.group(1)),) * 2)[0 if zh else 1], s)
    s = re.sub(r"[ \t]{2,}", " ", s)
    return s.strip()


def _title(key: str) -> str:
    """脚本键 → 可读英文名（`light_footmen` → `Light Footmen`）。"""
    return " ".join(w[:1].upper() + w[1:] for w in str(key or "").split("_") if w)


def _finalize(raw: dict, sv: dict | None = None, zh: bool = True) -> dict:
    """引用展开 + 清洗，得到可直接显示的译名表。"""
    out = {k: _clean(_resolve(v, raw, sv), zh) for k, v in raw.items()}
    # 兜底：还有漏网的 $key$，绝不能把美元符号带到界面上
    for k, v in list(out.items()):
        if "$" in v:
            out[k] = _clean(_REF_PAT.sub(
                lambda m: out.get(m.group(1)) or raw.get(m.group(1)) or _title(m.group(1)), v), zh)
    return out


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


def _load_cost_values(game_root: str) -> dict:
    """解析 common/script_values 里的招募花费与维护费。

    两种写法：`@名字 = 45`（常量）、`名字 = @[base * mult]`（不带 @ 的别名）。
    注意文件里存在同名自引用别名，必须分两套命名空间，优先用 @ 常量。
    """
    at, plain = {}, {}
    for f in glob.glob(os.path.join(game_root, D_SV, "*.txt")):
        try:
            text = _read(f)
        except Exception:
            continue
        for line in text.splitlines():
            line = line.split("#", 1)[0].rstrip()
            m = re.match(r"^\s*(@?)([A-Za-z0-9_]+)\s*=\s*(.+?)\s*$", line)
            if not m:
                continue
            k, v = m.group(2), m.group(3).strip()
            if not re.fullmatch(r"-?[\d.]+|@\[[^\]]*\]", v):
                continue
            (at if m.group(1) else plain)[k] = v
    out, busy = {}, set()

    def ev(name, depth=0):
        if name in out:
            return out[name]
        if depth > 4 or name in busy:
            return None
        expr = at.get(name)
        if expr is None:
            expr = plain.get(name)
        if expr is None:
            return None
        busy.add(name)
        e = expr[2:-1] if expr.startswith("@[") and expr.endswith("]") else expr
        # 算式里的引用可能是 @名字，也可能是裸名字
        e = re.sub(r"@?([A-Za-z_][A-Za-z0-9_]*)",
                   lambda mm: str(ev(mm.group(1), depth + 1) or 0), e)
        v = None
        if re.fullmatch(r"[\d.\s+\-*/()]+", e):
            try:
                v = float(eval(e, {"__builtins__": {}}, {}))
            except Exception:
                v = None
        else:
            try:
                v = float(expr)
            except Exception:
                v = None
        busy.discard(name)
        if v is not None:
            out[name] = v
        return v

    for k in list(at) + list(plain):
        ev(k)
    return out


def _accolade_attr_name(t: str, loc: dict, loc_lang: str) -> str:
    """勋号属性类型名：优先本地化键（archer_attribute → 弓箭手），
    否则按兵种类型名兜底（heavy_infantry → 重步兵），都没有退回原始键。"""
    v = loc.get(t)
    if v and not _looks_raw(v, t):
        return v
    tn = _type_name(t, loc, loc_lang)
    if tn and tn != t:
        return tn
    return t


def _region_name(tok: str, zh: bool) -> str:
    """地理区域令牌 → 可读名。REGION_NAMES 收录了兵种革新上实际出现的几个；
    其余令牌走兜底：去掉 world_/ghw_region_/custom_/_proper 前缀并美化。"""
    if tok in REGION_NAMES:
        return REGION_NAMES[tok][0 if zh else 1]
    s = tok
    for p in ("world_", "ghw_region_", "custom_", "_proper"):
        s = s.replace(p, "")
    return s.replace("_", " ").strip().title()


def _resolve_name(name: str, loc: dict) -> str:
    """解析本地化里没展开干净的 `$引用键$`（如 innovation_tiefutu → $tiefutu$ → 铁浮屠）。"""
    if not name:
        return name
    for _ in range(3):
        m = re.search(r"\$([A-Za-z_][A-Za-z0-9_]*)\$", name)
        if not m:
            break
        inner = loc.get(m.group(1)) or loc.get(m.group(1) + "_name")
        if not inner:
            break
        name = name[:m.start()] + inner + name[m.end():]
    return name


def _unlock_prefix(kind: str, zh: bool) -> str:
    """解锁授予者种类 → 招募条件前缀。"""
    if kind == "tradition":
        return "需要传统：" if zh else "Requires tradition: "
    if kind == "innovation":
        return "需要革新：" if zh else "Requires innovation: "
    if kind == "dynasty_legacy":
        return "需要家族传承：" if zh else "Requires dynasty legacy: "
    return ""


def _load_unlock_maa(game_root: str, loc: dict):
    """扫描 traditions + innovations，建立两套解锁映射（见上方全局变量说明）。

    文化兵种的解锁有两类写法：
      - 参数写法：传统里 `unlock_maa_<x> = yes`，兵种 can_recruit 用
        `valid_for_maa_trigger = { PARAMETER = unlock_maa_<x> }`
      - 等号写法：革新里 `unlock_maa = <x>`（如 innovation_caballeros 解锁 caballero），
        兵种本身常无 can_recruit 块，纯靠该革新（并可能带 region 地区限定）解锁
    注：家族传承（dynasty_legacy）解锁兵种走的不是 unlock_maa，而是兵种自身
    `can_recruit = { dynasty ?= { has_dynasty_perk = X } }`（如家族近卫由 warfare_legacy_5
    『家族近卫』解锁），由 `_recruit_reqs` 的 has_dynasty_perk 分支处理，故这里不扫 dynasty_legacies。
    本地化里 `culture_parameter_unlock_maa_<x>` 的原文是『可以招募[GetMaA('x').GetName]兵士』，
    纯属废话，不能当条件显示。
    """
    token_map, key_map = {}, {}
    def add_token(tok, kind, name):
        token_map.setdefault(tok, [])
        if (kind, name) not in token_map[tok]:
            token_map[tok].append((kind, name))
    def add_key(mk, kind, name, region):
        e = key_map.setdefault(mk, {"grantors": [], "region": None})
        if (kind, name) not in e["grantors"]:
            e["grantors"].append((kind, name))
        if region and not e["region"]:
            e["region"] = region
    def scan(folder, kind):
        for f in sorted(glob.glob(os.path.join(game_root, folder, "*.txt"))):
            try:
                text = _read(f)
            except Exception:
                continue
            for key, body in _top_blocks(text):
                if "unlock_" not in body:
                    continue
                # 授予者（传统/革新）名：可能带/不带 innovation_ 前缀，且可能含 $引用$ 未展开
                name = _resolve_name(
                    (loc.get(key) or loc.get(key + "_name")
                     or loc.get(key.replace("innovation_", ""))
                     or loc.get(key.replace("innovation_", "") + "_name")
                     or _title(key)), loc)
                region = re.search(r"region\s*=\s*([a-z_0-9]+)", body)
                region = region.group(1) if region else None
                # 参数写法 unlock_maa_<x> = yes
                for tok in re.findall(r"unlock_maa_([A-Za-z0-9_]+)", body):
                    if re.search(r"\bunlock_maa_%s\s*=\s*yes\b" % re.escape(tok), body):
                        add_token("unlock_maa_" + tok, kind, name)
                        add_key(tok, kind, name, region)
                # 等号写法 unlock_maa = <x>
                for mk in re.findall(r"unlock_maa\s*=\s*([a-z_0-9]+)", body):
                    add_token("unlock_maa_" + mk, kind, name)
                    add_key(mk, kind, name, region)
                # 其余 unlock_<x> = yes（如 heritage 的 unlock_infantry_samurai_units）
                for tok in set(re.findall(r"unlock_[A-Za-z0-9_]+", body)):
                    if re.search(r"\b%s\s*=\s*yes\b" % re.escape(tok), body):
                        add_token(tok, kind, name)
    scan(D_TRAD, "tradition")
    scan(D_INNOV, "innovation")
    return token_map, key_map


def _maa_unlock_extra(key: str, zh: bool, maa_name: str = "") -> list:
    """文化兵种即使没有 can_recruit 块，也可能由某革新/传统/家族传承解锁（并带地区限定）。
    返回补充的招募条件：『需要革新/传统/家族传承：X』+ 『限定地区：Y』。
    若授予者名与兵种自身名相同（如 caballero 的革新就叫『伊比利亚骑手』）则跳过，避免重复；
    但家族传承即使同名也要保留（『需要家族传承：家族近卫』本身即有信息量）。"""
    e = MAA_UNLOCK.get(key)
    if not e:
        return []
    out = []
    for kind, name in e["grantors"]:
        if kind != "dynasty_legacy" and name == maa_name:
            continue
        out.append(_unlock_prefix(kind, zh) + name)
    if e["region"]:
        out.append(("限定地区：" if zh else "Restricted region: ") + _region_name(e["region"], zh))
    return out


def _merge_recruit(a: list, b: list) -> list:
    """合并两套招募条件并去重（保留首次出现顺序）。"""
    out = []
    for t in (a or []) + (b or []):
        if t and t not in out:
            out.append(t)
    return out[:6]


def _recruit_reqs(body: str, loc: dict, zh: bool = True) -> list:
    """把 can_recruit 条件块翻译成可读的具体解锁条件。"""
    cr = _sub(body, "can_recruit") or ""
    if not cr:
        return []
    out = []

    def add(t):
        if t and t not in out:
            out.append(t)

    # 文化/特殊兵种：valid_for_maa_trigger = { PARAMETER = unlock_<x> }
    #   <x> 由某个传统/革新授予 → 显示「需要传统/革新：X」，而不是废话『可以招募X兵士』
    for p in re.findall(r"PARAMETER\s*=\s*(unlock_[A-Za-z0-9_]+)", cr):
        got = UNLOCK_MAA.get(p)
        if got:
            for kind, name in got:
                add(_unlock_prefix(kind, zh) + name)
        else:
            t = loc.get("culture_parameter_" + p) or loc.get(p + "_name") or loc.get(p)
            if t and "可以招募" not in (t or ""):
                add(t)
    # 其他文化参数（非 unlock_，通常是文化特性开关）
    for p in re.findall(r"PARAMETER\s*=\s*([A-Za-z0-9_]+)", cr):
        if p.startswith("unlock_"):
            continue
        t = loc.get("culture_parameter_" + p) or loc.get(p + "_name") or loc.get(p)
        if t and "可以招募" not in (t or ""):
            add(t)
    # 勋号兵种：any_accolade = { accolade_attribute_level = { type = X level >= N } }
    accolade_hit = False
    for al in (_sub_all(cr, "accolade_attribute_level") or []):
        t = _val(al, "type")
        if not t:
            continue
        nm = _accolade_attr_name(t, loc, "simp_chinese" if zh else "english")
        lvl = None
        m = re.search(r"level\s*>=\s*(\d+)", al)
        if not m:
            m = re.search(r"level\s*=\s*(\d+)", al)
        if m:
            lvl = int(m.group(1))
        s = ("需要勋号：" if zh else "Requires accolade: ") + nm
        if lvl is not None:
            s += ("（属性等级 ≥ %d）" % lvl) if zh else (" (attribute level ≥ %d)" % lvl)
        add(s)
        accolade_hit = True
    # 革新
    for i in re.findall(r"has_innovation\s*=\s*([a-z_0-9]+)", cr):
        t = loc.get(i + "_name") or loc.get(i)
        if t:
            add(("需要革新：" if zh else "Requires innovation: ") + t)
    # NOT 块是文化层面的「禁止拥有某类兵种」限制（如「该文化统治者无法招募重步/重骑/战象」），
    # 属于文化整体规则而非单个兵种的解锁条件，对玩家了解如何招募该兵种没有意义，故不再展示。
    # 但仍需把 NOT 块整体剔除，避免其中的 has_cultural_parameter 被误当作正向招募要求。
    cr_pos = cr
    for nb in (_sub_all(cr, "NOT") or []):
        cr_pos = cr_pos.replace(nb, "")
    for p in re.findall(r"has_cultural_parameter\s*=\s*([a-z_0-9]+)", cr_pos):
        # unlock_<x> 由传统/革新授予 → 显示「需要传统/革新：X」，而不是废话『可以招募X兵士』
        if p.startswith("unlock_"):
            got = UNLOCK_MAA.get(p)
            if got:
                for kind, name in got:
                    add(_unlock_prefix(kind, zh) + name)
                continue
        t = loc.get("culture_parameter_" + p) or loc.get(p + "_name")
        if t and "可以招募" not in (t or ""):
            add(("需文化特性：" if zh else "Requires cultural pillar: ") + t)
    # 政体
    for g in re.findall(r"has_government\s*=\s*([a-z_0-9]+)", cr):
        t = loc.get(g) or loc.get(g + "_name")
        if t:
            add(("限定政体：" if zh else "Government: ") + t)
    # 特殊触发器（脚本名，游戏里没有本地化键）
    special = {
        "culture_uses_eastern_siege_weapons_trigger": ("仅限东方攻城器械文化", "Eastern siege weapons culture only"),
        "is_landless_adventurer": ("仅限无地冒险者", "Landless adventurer only"),
        "mpo_can_recruit_nomad_maa_trigger": ("需游牧制（斡儿朵）", "Requires nomadic government"),
    }
    for k, pair in special.items():
        if k in cr:
            add(pair[0] if zh else pair[1])
    # 兜底：any_accolade 但没解析出具体属性
    if "any_accolade" in cr and not accolade_hit:
        add(("需要对应勋号/勋章", "Requires an appropriate accolade") if zh else "Requires an appropriate accolade")
    # 家族传承（dynasty perk）：如家族近卫 requires warfare_legacy_5『家族近卫』
    for p in re.findall(r"has_dynasty_perk\s*=\s*([a-z_0-9]+)", cr):
        t = loc.get(p) or loc.get(p + "_name")
        if t:
            add(("需要家族传承：" if zh else "Requires dynasty legacy: ") + t)
    return out[:8]


def _type_aliases(raw: dict, zh: bool = True) -> dict:
    """部分兵种类型键是自引用（值就是 `[key|E]`），概念展开后会变成空白。
    统一覆盖成可读类型名，按界面语言给中文或英文。"""
    for k, pair in TYPE_NAMES.items():
        v = raw.get(k, "")
        if not v or v.strip() in ("[%s|E]" % k, "[%s|E]" % k.lower()):
            raw[k] = pair[0] if zh else pair[1]
    return raw


def _trait_aliases(game_root: str, raw: dict) -> dict:
    """特质的显示名常写在 `name = { ... desc = trait_xxx }` 里，而不是 `trait_<key>`。

    例：hajjaj 的真实名字键是 trait_hajji（朝觐者）。把别名直接注入本地化表，
    [GetTrait('hajjaj').GetName] 就不会再显示成英文 Hajjaj。
    """
    for f in glob.glob(os.path.join(game_root, "common", "traits", "*.txt")):
        try:
            text = _read(f)
        except Exception:
            continue
        for k, b in _top_blocks(text):
            nb = _sub(b, "name")
            if not nb:
                continue
            m = re.search(r"\bdesc\s*=\s*([A-Za-z0-9_]+)", nb)
            if m and m.group(1) in raw and k not in raw:
                raw[k] = raw[m.group(1)]
    return raw


def _collect_maa(game_root: str, loc: dict, loc_lang: str, costs: dict) -> list:
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

            def cost_of(field):
                """buy_cost = { gold = xxx_recruitment_cost } → 真实数值。"""
                blk = _sub(body, field)
                if not blk:
                    return None
                g = (_val(blk, "gold") or "").lstrip("@")
                if not g:
                    return None
                v = costs.get(g)
                if v is None:
                    v = _int(g)
                if v is None:
                    return None
                return round(v, 2) if v % 1 else int(v)

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
                "cost": cost_of("buy_cost"),
                "maintLow": cost_of("low_maintenance_cost"),
                "maintHigh": cost_of("high_maintenance_cost"),
                "recruit": _merge_recruit(
                    _recruit_reqs(body, loc, loc_lang == "simp_chinese"),
                    _maa_unlock_extra(key, loc_lang == "simp_chinese", loc.get(key) or key),
                ),
                "terrain": terrain,
                "counters": counters,
                "winter": winter,
                "flags": flags,
                "src": os.path.basename(f),
            })
    out.sort(key=lambda x: (_order_index(MAA_TYPE_ORDER, x["type"]), x["name"]))
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


def _mn(mapping: dict, key: str, zh: bool = True):
    """内置译名表取值：兼容纯字符串与 (中文, English) 两种写法。"""
    v = mapping.get(key)
    if not v:
        return None
    if isinstance(v, (tuple, list)):
        return v[0] if zh else v[1]
    return v


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


# 修正块里有些值不是数字，而是 script_values 的名字（例：taiga_travel_danger = taiga_low_danger_reduction）
_SV_NUM = {}
_DANGER_KEY = re.compile(r"^[a-z_]+_travel_danger$")


def _mod_number(v: str):
    """修正值：数字直接返回；脚本值名从 _SV_NUM 查真实数字。"""
    if not v:
        return None
    v = v.strip()
    if re.fullmatch(r"-?\d+(?:\.\d+)?", v):
        return float(v)
    x = _SV_NUM.get(v)
    return float(x) if x is not None else None


def _mod_line(k: str, v: str, nm: str, zh: bool = True) -> str:
    """把「针叶林危险」这种半截文案补成「降低针叶林地形危险 19 点」。"""
    val = _mod_number(v)
    if val is None:
        return nm.strip()                     # yes / 触发器标记，只留名字
    if _DANGER_KEY.match(k):
        terrain = (nm.replace("危险", "").strip() if zh
                   else re.sub(r"\s*Danger\s*$", "", nm, flags=re.I).strip())
        amount = int(round(abs(val)))
        down = val < 0
        if zh:
            return "%s%s地形危险 %d 点" % ("降低" if down else "提高", terrain, amount)
        return "%s %s travel danger by %d" % ("Reduce" if down else "Increase", terrain, amount)
    return "%s %s" % (nm, _fmt_num(str(val)))


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


def _trad_effects(body: str, loc: dict, zh: bool = True) -> list:
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
            nm = _mn(PARTNER_NAMES, k, zh) or loc.get(k)
            if nm and re.fullmatch(r"-?\d+(?:\.\d+)?", v):
                eff.append("%s %s" % (nm, v))
    for block in ("character_modifier", "culture_modifier"):
        mb = _sub(body, block)
        if not mb:
            continue
        for k, v in _pairs(mb).items():
            if k.startswith("ai_"):        # AI 行为参数，对玩家没意义
                continue
            nm = loc.get(k) or _mn(MODIFIER_NAMES, k, zh)
            if nm:
                eff.append(_mod_line(k, v, nm, zh))
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


def _collect_traditions(game_root: str, loc: dict, zh: bool = True) -> tuple:
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
                "effects": _trad_effects(body, loc, zh),
                "cat": cat,
            }
            items.append(it)
            mapping[key] = {"key": key, "name": it["name"], "desc": it["desc"],
                            "effects": it["effects"]}
    items.sort(key=lambda x: (x["cat"], x["name"]))
    return items, mapping


# ---------------------------------------------------------------- 扩展百科（通用条目）

def _loc_any(loc: dict, key: str, first=None) -> str:
    """效果文案的键名没有统一前缀，按命中率从高到低依次尝试。

    实测：`custom` / `unlock_law` / `unlock_maa` 大多是 `<key>_name`，
    `unlock_building` 是 `building_<key>`，`parameters` 是 `culture_parameter_<key>`。
    """
    cands = list(first or ()) + [
        key + "_name", key, "culture_parameter_" + key,
        "doctrine_parameter_" + key, "tenet_parameter_" + key,
        "game_concept_" + key, "building_" + key,
    ]
    for c in cands:
        v = loc.get(c)
        if v:
            return v
    return ""


def _generic_effects(body: str, loc: dict, prefixes=(), zh=True) -> list:
    """从 parameters / special_parameters / 修正块里抽效果文案。

    `parameters` 有两种写法：裸标识符列表 或 `k = v`，都要能读。
    革新还有 `unlock_law` / `custom`，教义有没本地化的机制参数，都要补上。
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
                pn = PARAMETER_NAMES.get(p)
                if pn:
                    eff.append(pn[0] if zh else pn[1])
                else:
                    v = pairs.get(p)
                    if v and re.fullmatch(r"-?\d+(?:\.\d+)?", v):
                        nm = _mn(PARTNER_NAMES, p, zh) or _mn(MODIFIER_NAMES, p, zh) or loc.get(p)
                        if nm:
                            eff.append("%s %s" % (nm, v))
    # 革新解锁的法律 / 自定义效果
    for law in re.findall(r"^\tunlock_law\s*=\s*([a-z_0-9]+)", body, re.M):
        nm = _loc_any(loc, law)
        if nm:
            eff.append(("解锁：" if zh else "Unlocks: ") + nm)
    for c in re.findall(r"^\tcustom\s*=\s*([a-z_0-9]+)", body, re.M):
        t = _loc_any(loc, c) or _custom_name(c, zh)
        if t:
            eff.append(t)
    # maa_upgrade = { type = heavy_infantry  toughness = 10 } 这种块
    for ub in _sub_all(body, "maa_upgrade") or []:
        t = _val(ub, "type")
        if not t:
            continue
        tn = loc.get(t) or loc.get(t + "_attribute") or t
        bits = []
        for k, v in _pairs(ub).items():
            if k == "type":
                continue
            pair = STAT_NAMES.get(k)
            nm = (pair[0] if zh else pair[1]) if pair else (loc.get(k) or _mn(MODIFIER_NAMES, k, zh))
            if nm:
                bits.append("%s %s" % (nm, _fmt_num(v)))
        if bits:
            eff.append(("%s%s" % (tn, "：" if zh else ": ")) + ("、".join(bits) if zh else ", ".join(bits)))
    # 革新的其它解锁字段（这些以前整段漏掉，导致 40 条革新没有效果）
    for fld, pair in (
        ("unlock_maa", ("解锁兵士：", "Unlocks men-at-arms: ")),
        ("unlock_building", ("解锁建筑：", "Unlocks building: ")),
        ("unlock_casus_belli", ("解锁宣战理由：", "Unlocks casus belli: ")),
        ("maa_upgrade", ("兵士升级：", "Men-at-arms upgrade: ")),
    ):
        for v in re.findall(r"^\t" + fld + r"\s*=\s*([a-z_0-9]+)", body, re.M):
            t = _loc_any(loc, v)
            if t:
                eff.append((pair[0] if zh else pair[1]) + t)
    for blk in ("character_modifier", "personal_tenet_modifier", "culture_modifier",
                "county_modifier", "province_modifier", "clergy_modifier"):
        mb = _sub(body, blk)
        if not mb:
            continue
        for k, v in _pairs(mb).items():
            nm = loc.get(k) or _mn(MODIFIER_NAMES, k, zh)
            if nm:
                eff.append(_mod_line(k, v, nm, zh))
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


def _collect_tenets(game_root: str, loc: dict, zh=True) -> list:
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
                                         ("doctrine_parameter_", "culture_parameter_", "tenet_parameter_"), zh),
            ))
    out.sort(key=lambda x: x["name"])
    return out


def _collect_doctrines(game_root: str, loc: dict, zh=True) -> list:
    out = []
    for f in sorted(glob.glob(os.path.join(game_root, D_DOCTRINE, "*.txt"))):
        for key, body in _top_blocks(_read(f)):
            g = _val(body, "doctrine_group_type") or ""
            gname = loc.get(g + "_name") or loc.get(g) or g or "其他"
            out.append(_entry(
                key, loc.get(key + "_name") or loc.get(key) or _title(key),
                loc.get(key + "_desc") or "",
                g, gname,
                effects=_generic_effects(body, loc, ("doctrine_parameter_", "culture_parameter_"), zh),
            ))
    out.sort(key=lambda x: (x["cat"], x["name"]))
    return out


def _collect_innovations(game_root: str, loc: dict, zh=True) -> list:
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
                effects=_generic_effects(body, loc, ("culture_parameter_",), zh),
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
                # 兵种招募条件引用的本地化键（家族传承 has_dynasty_perk / 革新 / 政体 / 文化参数）
                # 例：house_guard 的 can_recruit = { dynasty ?= { has_dynasty_perk = warfare_legacy_5 } }
                #     需加载 warfare_legacy_5_name（『家族近卫』），否则显示不出「需要家族传承」。
                cr_body = _sub(body, "can_recruit")
                if cr_body:
                    for fld in ("has_dynasty_perk", "has_innovation", "has_government",
                                "has_cultural_parameter", "PARAMETER"):
                        for v in re.findall(r"\b" + fld + r"\s*=\s*([A-Za-z_0-9]+)", cr_body):
                            needed.update((v, v + "_name", "culture_parameter_" + v,
                                           "game_concept_" + v))

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
                    # 革新解锁 / 兵种招募条件的文案键
                    for fld in ("unlock_law", "custom", "unlock_maa", "unlock_building",
                                "unlock_casus_belli", "maa_upgrade", "has_innovation",
                                "has_government", "has_dynasty_perk", "has_cultural_parameter"):
                        for v in re.findall(r"^\t" + fld + r"\s*=\s*([a-z_0-9]+)", body, re.M):
                            needed.update((v, v + "_name", "culture_parameter_" + v,
                                           "building_" + v, "game_concept_" + v))
                    cr = _sub(body, "can_recruit")
                    if cr:
                        for fld in ("PARAMETER", "has_innovation", "has_government",
                                    "has_cultural_parameter", "has_dynasty_perk"):
                            for v in re.findall(r"\b" + fld + r"\s*=\s*([A-Za-z_0-9]+)", cr):
                                needed.update((v, v + "_name", "culture_parameter_" + v,
                                               "game_concept_" + v))
                    # 宗教引用的教义名
                    for dk in re.findall(r"^\tdoctrine\s*=\s*([a-z_0-9]+)", body, re.M):
                        needed.update((dk, dk + "_name"))

        raw = _scan_loc(loc_dir, needed, WIDE_PREFIXES)
        # 勋号兵种属性名（archer_attribute → 弓箭手 等）默认不在 needed/WIDE 里，
        # 显式补扫，保证招募条件能显示中文属性名。
        raw.update(_scan_loc(loc_dir, set(ACCOLADE_ATTR_KEYS), ()))
        _trait_aliases(game_root, raw)   # hajjaj → 朝觐者 这类特质别名
        _type_aliases(raw, loc_lang == "simp_chinese")   # heavy_infantry → 重步兵（自引用键）

        # 引用展开：译名/效果里会引用 `$键$`、`[概念|E]`、`[GetMaA('x')]`，
        # 把这些被引用的键也补扫回来（做两轮，够覆盖二级引用）
        for _round in range(2):
            refs = set()
            for v in raw.values():
                refs.update(_ref_keys(v))
            refs -= set(raw.keys())
            if not refs:
                break
            for k, v in _scan_loc(loc_dir, refs, WIDE_PREFIXES).items():
                raw.setdefault(k, v)
        # 引擎动态文本：loc 里的值是脚本调用，直接覆盖成可读名字
        zh = (loc_lang == "simp_chinese")
        for k, pair in DYNAMIC_NAMES.items():
            raw[k] = pair[0] if zh else pair[1]
        sv = _collect_script_values(game_root)
        loc = _finalize(raw, sv, zh)

        # 2) 正式抽取（脚本值表要先算，效果里会引用）
        global _SV_NUM, UNLOCK_MAA, MAA_UNLOCK
        costs = _load_cost_values(game_root)
        _SV_NUM = costs
        # 文化兵种招募解锁：unlock_maa_<x> 参数各传统/革新授予，反向建表
        UNLOCK_MAA, MAA_UNLOCK = _load_unlock_maa(game_root, loc)
        traditions, trad_map = _collect_traditions(game_root, loc, zh)
        maa = _collect_maa(game_root, loc, loc_lang, costs)
        cultures = _collect_cultures(game_root, loc, trad_map)
        doctrines = _collect_doctrines(game_root, loc, zh)
        doc_names = {d["key"]: d["name"] for d in doctrines}
        religions = _collect_religions(game_root, loc, doc_names)
        tenets = _collect_tenets(game_root, loc, zh)
        innovations = _collect_innovations(game_root, loc, zh)

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

        # 展示用的 5 节点闭环：轻骑兵 + 重骑兵合并成「轻/重骑兵」一组
        cav = "轻/重骑兵" if loc_lang == "simp_chinese" else "Light/Heavy Cavalry"
        cn = lambda k: tname.get(k, k)
        chain = [
            {"fromName": cn("skirmishers"), "toName": cn("heavy_infantry")},
            {"fromName": cn("heavy_infantry"), "toName": cn("pikemen")},
            {"fromName": cn("pikemen"), "toName": cav},
            {"fromName": cav, "toName": cn("archers")},
            {"fromName": cn("archers"), "toName": cn("skirmishers")},
        ]

        cnt = {}
        for m in maa:
            cnt[m["type"]] = cnt.get(m["type"], 0) + 1
        types_info = [{"key": t, "name": tname.get(t, t), "count": cnt[t]}
                      for t in sorted(cnt, key=lambda x: (_order_index(MAA_TYPE_ORDER, x), x))]

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
            "catOrders": {"innovations": ERA_ORDER, "maa": MAA_TYPE_ORDER},
            "counterCycle": cycle,
            "counterChain": chain,
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

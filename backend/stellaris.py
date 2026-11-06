# -*- coding: utf-8 -*-
"""群星百科数据：从游戏本体脚本 + 官方本地化抽取结构化资料。

设计要点
--------
- **完全离线**：只读 `<Stellaris>\\common\\...` 与 `localisation\\<lang>`。
- **随版本自动更新**：目录 mtime 变化 → 缓存失效重建。
- 与 CK3 百科共用前端条目结构：`{key,name,desc,cat,catKey,attrs,effects,links}`
  + 每条 tab 自带 `layout`，前端据此选渲染分支。

群星本地化的特殊标记（必须清洗，否则界面上全是符号）
--------------------------------------------------
- `£pop£` `£unity£`   图标内联 → 直接删
- `§Y ... §!`         颜色区间 → 删标记留正文
- `$r_unity$` `$unity$` 变量 → 递归展开（最多 3 层）
- `$t$`               制表/换行 → 换成换行（在效果里当分隔符）
- `[GetWorker]`       取值函数 → 删
- `\\n`               字面换行 → 真换行
"""

import os
import re
import glob
import time
import threading

try:
    from ck3core import APP_DIR
except Exception:
    APP_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

SL_VER = 1                     # 解析逻辑变更时 +1，强制重建缓存

# 界面语言 → 本地化目录（群星没有瑞典语，回落英文）
LANG_MAP = {
    "zh": "simp_chinese", "en": "english", "fr": "french",
    "de": "german", "sv": "english",
}

D_CIVIC = os.path.join("common", "governments", "civics")
D_AP = os.path.join("common", "ascension_perks")
D_TRAD = os.path.join("common", "traditions")
D_EDICT = os.path.join("common", "edicts")
D_POLICY = os.path.join("common", "policies")
D_RELIC = os.path.join("common", "relics")
D_SPEC = os.path.join("common", "specimens")
D_TECH = os.path.join("common", "technology")
D_TRAIT = os.path.join("common", "traits")
D_LOC = "localisation"

# 只收物种特质：排除领袖特质、文档文件
TRAIT_FILES = ("00_species_traits", "01_species_traits", "02_species_traits",
               "03_species_traits", "04_species_traits", "05_species_traits",
               "06_species_traits", "07_species_traits", "08_species_traits",
               "09_species_traits", "10_species_traits", "11_species_traits")

# 资源/图标的「无图标」化后可能留下空洞，这里给几个高频变量直接定名
VAR_NAMES = {
    "t": "\n",
    "RESEARCH": "研究",
}

# tab 元数据：id → (图标, 中文名, 英文名, layout)
TABS = [
    ("civics", "🏛", "国民理念", "Civics", "generic"),
    ("origins", "🌱", "起源", "Origins", "generic"),
    ("aps", "✨", "飞升天赋", "Ascension Perks", "generic"),
    ("traditions", "✦", "传统", "Traditions", "generic"),
    ("edicts", "📜", "法令", "Edicts", "generic"),
    ("policies", "⚖", "政策", "Policies", "generic"),
    ("relics", "🏺", "遗物", "Relics", "generic"),
    ("specimens", "🔬", "标本", "Specimens", "generic"),
    ("tech", "⚙", "科技", "Technology", "generic"),
    ("traits", "🧬", "物种特质", "Species Traits", "generic"),
]

_LOCK = threading.Lock()
_MEM = {}


# ---------------------------------------------------------------- 基础解析

def _read(path: str) -> str:
    try:
        with open(path, encoding="utf-8-sig", errors="ignore") as f:
            txt = f.read()
    except Exception:
        return ""
    # 群星注释是 #，但要避开字符串里的 #（本地化不在这里，脚本里极少见）
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
    """取 body **顶层**的 `name = { ... }`。

    必须是顶层扫描：群星脚本里 `ai_weight` 内部也有嵌套的 `modifier = { factor = ... }`，
    按「第一个文本命中」抓会抓到嵌套块，效果数值就全空了。
    """
    for k, b in _top_blocks(body or ""):
        if k == name:
            return b
    return None


def _deep(body: str, name: str):
    """取 body 内任意深度的 `name = { ... }`（第一个文本命中）。"""
    m = re.search(r"(?:^|[\s{])" + re.escape(name) + r"\s*=\s*\{", body or "")
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
    m = re.search(r"(?:^|[\s{])" + re.escape(name) + r"\s*=\s*([^\s{}]+)", body)
    return m.group(1).strip().strip('"') if m else None


def _pairs(block: str) -> dict:
    """`k = v` 简单键值（忽略嵌套块）。"""
    out = {}
    if not block:
        return out
    for m in re.finditer(r"([A-Za-z_][A-Za-z0-9_]*)\s*=\s*([^\s{}]+)", block):
        out[m.group(1)] = m.group(2)
    return out


def _flat(block: str) -> list:
    """把块里所有 `k = v`（含嵌套）按出现顺序摊平，用于 modifier 汇总。"""
    return re.findall(r"([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(-?\d+(?:\.\d+)?)", block or "")


# ---------------------------------------------------------------- 本地化

_LOC_PAT = re.compile(r'^\s*([A-Za-z0-9_.\-]+):\d*\s*"((?:[^"\\]|\\.)*)"', re.M)
_ICON_PAT = re.compile(r"£[^£]*£")
_COLOR_PAT = re.compile(r"§[A-Za-z0-9!]")
# 变量有三种写法，都要吃掉：
#   $unity$                       普通本地化键
#   $tech_x_desc.1$               分段文本（.N 是序号，本体是 tech_x_desc）
#   $@replicatory_insurance|0$    脚本值 + 数字格式（离线算不出来，直接丢）
_VAR_PAT = re.compile(r"\$(@?[A-Za-z0-9_.]+)(?:\|[^$]*)?\$")
_FUNC_PAT = re.compile(r"\[[A-Za-z0-9_.]*\([^\[\]]*\)[^\[\]]*\]")
# 群星方括号语法有两种：
#   ['concept_x'] / ['building:building_x']   概念或带类型的引用 → 取最后一段概念名
#   ['icon_key', £icon£$var$文本]             图标+文本 → 只取逗号后的文本
_TOK2_PAT = re.compile(r"\[\s*'[^'\]]*'\s*,\s*([^\[\]]*)\]")
_TOK1_PAT = re.compile(r"\[\s*['\"]?(?:[A-Za-z0-9_]+:)?([A-Za-z0-9_.\-]+)['\"]?\s*\]")

# 引擎内置变量（本地化里查不到，必须硬编码）
ENGINE_VARS = {
    "t": "\n", "n": "\n", "NEW_LINE": "\n", "TABBED_NEW_LINE": "\n",
    "TAB": " ", "SPACE": " ",
}

# 资源名：中文汉化里 `$r_unity$凝聚力花费` 这种会展开成「凝聚力凝聚力花费」，
# 展开后做一次资源词去重（只处理 3 字以上，避免误伤「人人」「看看」这类叠词）。
RESOURCE_WORDS = (
    "凝聚力", "影响力", "易爆微粒", "稀有水晶", "活体金属", "纳米机器人",
    "消费品", "工程学研究", "物理学研究", "社会学研究", "帝国规模", "舰队容量",
    "行政容量", "暗物质", "星尘", "泽珞", "合金", "矿物", "食物", "能源",
)


def _load_loc(loc_dir: str) -> dict:
    out = {}
    for f in glob.glob(os.path.join(loc_dir, "*.yml")):
        try:
            raw = open(f, encoding="utf-8-sig", errors="ignore").read()
        except Exception:
            continue
        for m in _LOC_PAT.finditer(raw):
            out[m.group(1)] = m.group(2)
    return out


def _resolve(text: str, loc: dict, depth: int = 4) -> str:
    """展开变量、去图标/颜色标记、去取值函数，返回可直接显示的纯文本。"""
    if not text:
        return ""
    s = text

    # 交替执行「方括号引用」和「变量」展开：图标文本里常常又套变量，
    # 单跑一轮会留下 `['concept_x', 发电区划]` 这种半成品。
    for _ in range(depth):
        before = s
        s = _TOK2_PAT.sub(lambda m: m.group(1), s)          # [icon, 文本] → 文本

        def tok1(m):
            v = loc.get(m.group(1))
            if not v:
                return ""
            return _ICON_PAT.sub("", _COLOR_PAT.sub("", v))
        s = _TOK1_PAT.sub(tok1, s)                          # ['concept_x'] → 概念名

        def rep(m):
            k = m.group(1)
            if k.startswith("@"):          # 脚本值，离线算不出来
                return ""
            if k in ENGINE_VARS:
                return ENGINE_VARS[k]
            v = loc.get(k)
            if v is None:
                v = loc.get(k.upper())
            if v is None and "." in k:     # $tech_x_desc.1$ → 段号，取本体
                base = k.rsplit(".", 1)[0]
                v = loc.get(base) or loc.get(base.upper())
            return v if v is not None else ""
        s = _VAR_PAT.sub(rep, s)
        if s == before:
            break

    s = s.replace("\\n", "\n")
    s = _ICON_PAT.sub("", s)
    s = _COLOR_PAT.sub("", s)
    s = _FUNC_PAT.sub("", s)
    s = _VAR_PAT.sub("", s)                 # 还剩的未定义变量直接删
    s = s.replace("\xa0", " ")
    # 汉化把资源图标名和后续词重复了一遍（「凝聚力凝聚力花费」），压掉一次
    for w in RESOURCE_WORDS:
        s = re.sub(re.escape(w) + r"(?=" + re.escape(w) + r")", "", s)
    # 拆行：去掉「仅符号」的空行，去掉列表前缀破折号（前端用列表渲染）
    lines = []
    for ln in s.split("\n"):
        ln = re.sub(r"[ \t]+", " ", ln).strip()
        ln = re.sub(r"^[—–\-·]\s*", "", ln)
        # 句首残留的「的」：原文是 `[EVENT_TARGET_0.GetName]的植物…`，
        # 目标名被删掉后会剩下「的植物逐渐演化…」
        if ln.startswith("的") and len(ln) > 1:
            ln = ln[1:].lstrip()
        if ln and not re.fullmatch(r"[\s—–\-·:：,，。.、]+", ln):
            lines.append(ln)
    return "\n".join(lines).strip()


def _title(key: str) -> str:
    return " ".join(w.capitalize() for w in str(key or "").split("_") if w)


def _cjk(s: str) -> bool:
    return bool(re.search(r"[\u4e00-\u9fff]", s or ""))


def _loc_any(loc: dict, *keys):
    for k in keys:
        if not k:
            continue
        v = loc.get(k)
        if v:
            return v
    return None


# ---------------------------------------------------------------- 修饰器

# 群星修饰器的显示名：优先 `mod_<key>`（中文/英文都是这个），其次 `MOD_<KEY>`，最后才裸键
def _mod_name(k: str, loc: dict, zh: bool = True) -> str:
    for cand in ("mod_" + k, "MOD_" + k.upper(), k):
        v = loc.get(cand)
        if v:
            r = _resolve(v, loc)
            if r:
                return r
    return ""


def _fmt_mod_val(k: str, v: str) -> str:
    """群星修饰器值：`*_mult` 一律是百分比，其余按原值。"""
    try:
        f = float(v)
    except Exception:
        return str(v)
    pct = k.endswith("_mult") or k.endswith("_mult_add") or "_mult_" in k
    if pct:
        return "%+g%%" % (f * 100)
    if abs(f) >= 1:
        return "%+g" % f
    return "%+g" % f


def _mod_lines(*blocks, loc=None, zh=True) -> list:
    """把若干 modifier 块摊平成「名称 数值」行，同键多值用 / 合并。"""
    agg = {}
    order = []
    for b in blocks:
        for k, v in _flat(b):
            nm = _mod_name(k, loc, zh)
            if not nm:
                continue
            if k not in agg:
                agg[k] = {"name": nm, "vals": []}
                order.append(k)
            val = _fmt_mod_val(k, v)
            if val not in agg[k]["vals"]:
                agg[k]["vals"].append(val)
    out = []
    for k in order:
        it = agg[k]
        out.append("%s %s" % (it["name"], " / ".join(it["vals"])))
    return out


def _dedup(seq) -> list:
    seen, out = set(), []
    for x in seq:
        x = (x or "").strip()
        if x and x not in seen:
            seen.add(x)
            out.append(x)
    return out


# ---------------------------------------------------------------- 条目

def _entry(key, name, desc, cat_key, cat_label, attrs=None, effects=None, links=None, src=""):
    return {
        "key": key, "name": name, "desc": desc,
        "cat": cat_label or "", "catKey": cat_key or "",
        "attrs": attrs or [], "effects": effects or [],
        "links": links or [], "src": src or "",
    }


def _attr(label, value, zh=True):
    return {"label": label if zh else label, "value": value}


def _nm(name, en, zh=True):
    return name if zh else en


# 分类标签表：(中文, English)
CIVIC_CATS = {
    "00_civics": ("常规", "Standard"),
    "01_special_civics": ("特殊", "Special"),
    "02_gestalt_civics": ("蜂巢 / 机械", "Gestalt"),
    "03_corporate_civics": ("企业", "Corporate"),
}
EDICT_CATS = {
    "00_edicts": ("常规法令", "Standard"),
    "01_campaigns": ("战役议程", "Campaigns"),
    "02_ambitions": ("宏图野心", "Ambitions"),
    "03_sacrifices": ("献祭", "Sacrifices"),
    "04_machine_age_edicts": ("机械时代", "Machine Age"),
    "05_grand_archive_edicts": ("大档案馆", "Grand Archive"),
    "06_biogenesis_edicts": ("生物起源", "Biogenesis"),
    "06_shroud_edicts": ("帷幕", "Shroud"),
    "08_nomads_edicts": ("游牧", "Nomads"),
}
AREA_NAMES = {
    "physics": ("物理学", "Physics"),
    "society": ("社会学", "Society"),
    "engineering": ("工程学", "Engineering"),
}
TIER_NAMES = ("一", "二", "三", "四", "五", "六")
SPECIMEN_TYPES = {
    "xeno_geology": ("异星地质", "Xeno Geology"),
    "aesthetic_wonder": ("美学奇观", "Aesthetic Wonder"),
    "historical_item": ("历史文物", "Historical Item"),
    "organic": ("有机生命", "Organic"),
    "relic": ("远古遗物", "Relic"),
    "space_fauna": ("太空生物", "Space Fauna"),
    "precursor": ("先驱者", "Precursor"),
}
TRAIT_CATS = {
    "normal": ("常规特质", "Standard"),
    "planet_pops_traits": ("行星人口", "Planet Pops"),
    "planet_pref": ("星球偏好", "Planet Preference"),
    "robotic": ("机械特质", "Robotic"),
    "cyborg": ("义体特质", "Cyborg"),
    "psionic": ("灵能特质", "Psionic"),
    "overtuned": ("过度特化", "Overtuned"),
    "malleable": ("可塑特质", "Malleable"),
    "mutation": ("变异特质", "Mutation"),
}


def _cat_label(table: dict, key: str, zh: bool = True) -> tuple:
    """返回 (catKey, 显示名)。"""
    if key in table:
        zh_l, en_l = table[key]
        return key, (zh_l if zh else en_l)
    return key or "", (key or "")


# ---------------------------------------------------------------- 各类收集

def _civic_effects(body, key, loc, zh=True):
    """国民理念 / 起源：脚本里 `description` 指向一段效果文案，`modifier` 是数值修正。"""
    out = []
    d = _val(body, "description")
    if d and loc.get(d):
        out.extend(_resolve(loc[d], loc).split("\n"))
    nd = _val(body, "negative_description")
    if nd and loc.get(nd):
        out.extend(_resolve(loc[nd], loc).split("\n"))
    out.extend(_mod_lines(_sub(body, "modifier"), loc=loc, zh=zh))
    # 换体（游牧 / 石质之类）带来的另一套效果
    for sw in _sub_all(body, "swap_type"):
        for fld in ("description", "negative_description"):
            v = _val(sw, fld)
            if v and loc.get(v):
                out.extend(_resolve(loc[v], loc).split("\n"))
        out.extend(_mod_lines(_sub(sw, "modifier"), loc=loc, zh=zh))
    return _dedup(out)


def _block_from(s: str, start: int) -> str:
    """从 `{` 后一位开始，取到与之配平的 `}` 之前。"""
    d, j = 1, start
    while j < len(s) and d > 0:
        c = s[j]
        if c == "{":
            d += 1
        elif c == "}":
            d -= 1
        j += 1
    return s[start:j - 1]


def _authority_split(pot: str) -> tuple:
    """把 `authority` 限定拆成（允许, 排除）。

    群星里两种写法都要认，只取 value 会把「不能是企业」写成「可用：企业」：
      - 块内否定：authority = { NOT = { value = auth_corporate } }   → 排除
      - 块外否定：NOT = { authority = { value = auth_corporate } }   → 排除
      - 正面：    authority = { value = auth_imperial }              → 允许
    """
    allow, deny = set(), set()
    for m in re.finditer(r"authority\s*=\s*\{", pot or ""):
        blk = _block_from(pot, m.end())
        vals = set(re.findall(r"value\s*=\s*(auth_[a-z_]+)", blk))
        if not vals:
            continue
        before = pot[max(0, m.start() - 60):m.start()]
        outer_not = bool(re.search(r"NOT\s*=\s*\{\s*[^{}]*$", before))
        inner_not = "NOT" in blk
        (deny if (inner_not or outer_not) else allow).update(vals)
    return allow - deny, deny - allow


def _civic_attrs(body, key, loc, zh=True):
    attrs = []
    # 思潮 / 政体限制：`potential` 里 `authority` 的取值
    pot = (_sub(body, "potential") or "") + "\n" + (_sub(body, "possible") or "")
    allow, deny = _authority_split(pot)
    for vals, zh_l, en_l in ((allow, "可用政体", "Authority"),
                             (deny, "不可用政体", "Not available for")):
        if vals:
            names = [loc.get(a) or _title(a) for a in sorted(vals)]
            attrs.append(_attr(_nm(zh_l, en_l, zh), "、".join(dict.fromkeys(names))))
    # 是否可在开局后修改
    mdf = _sub(body, "modification")
    if mdf is not None:
        if re.search(r"^\s*no\s*$", (mdf or "").strip(), re.M):
            attrs.append(_attr(_nm("开局后", "After start", zh), _nm("不可增删", "Locked", zh)))
    elif _val(body, "modification") == "no":
        attrs.append(_attr(_nm("开局后", "After start", zh), _nm("不可增删", "Locked", zh)))
    # 起源标记
    if _val(body, "advanced_start") == "yes":
        attrs.append(_attr(_nm("开局", "Start", zh), _nm("进阶开局", "Advanced", zh)))
    return attrs


def _collect_civics_origins(root, loc, zh=True):
    civics, origins = [], []
    for f in sorted(glob.glob(os.path.join(root, D_CIVIC, "*.txt"))):
        stem = os.path.splitext(os.path.basename(f))[0]
        is_origin_file = "origins" in stem
        cat_key, cat_label = _cat_label(CIVIC_CATS, stem, zh)
        for key, body in _top_blocks(_read(f)):
            name = _resolve(loc.get(key) or "", loc)
            if not name:
                continue
            desc = _resolve(loc.get(key + "_desc") or "", loc)
            eff = _civic_effects(body, key, loc, zh)
            is_origin = (_val(body, "is_origin") == "yes") or is_origin_file
            if is_origin:
                attrs = _civic_attrs(body, key, loc, zh)
                if "non_playable" in stem:
                    attrs.append(_attr(_nm("可用性", "Availability", zh), _nm("不可选", "Not playable", zh)))
                origins.append(_entry(key, name, desc, "origin",
                                      _nm("起源", "Origins", zh), attrs, eff, src=stem))
            else:
                civics.append(_entry(key, name, desc, cat_key, cat_label,
                                     _civic_attrs(body, key, loc, zh), eff, src=stem))
    return civics, origins


def _collect_aps(root, loc, zh=True):
    out = []
    for f in sorted(glob.glob(os.path.join(root, D_AP, "*.txt"))):
        stem = os.path.splitext(os.path.basename(f))[0]
        for key, body in _top_blocks(_read(f)):
            name = _resolve(loc.get(key) or "", loc)
            if not name:
                continue
            eff = []
            # 解锁提示：on_enabled 里的 custom_tooltip（"this perk unlocks X"）
            for tt in re.findall(r'custom_tooltip\s*=\s*"([A-Za-z0-9_.]+)"', body):
                v = loc.get(tt)
                if v:
                    eff.extend(_resolve(v, loc).split("\n"))
            eff.extend(_mod_lines(_sub(body, "modifier"), loc=loc, zh=zh))
            out.append(_entry(key, name, _resolve(loc.get(key + "_desc") or "", loc),
                              "ap", _nm("飞升天赋", "Ascension Perk", zh), None,
                              _dedup(eff), src=stem))
    return out


def _collect_traditions(root, loc, zh=True):
    out = []
    for f in sorted(glob.glob(os.path.join(root, D_TRAD, "*.txt"))):
        stem = os.path.splitext(os.path.basename(f))[0]      # 00_adaptability
        tree_key = "tradition_" + re.sub(r"^\d+_", "", stem)
        tree_name = _resolve(loc.get(tree_key) or "", loc) or _title(tree_key)
        tree_desc = _resolve(loc.get(tree_key + "_desc") or "", loc)
        for key, body in _top_blocks(_read(f)):
            name = _resolve(loc.get(key) or "", loc)
            if not name:
                continue
            eff = []
            for tb in _sub_all(body, "triggered_modifier"):
                eff.extend(_mod_lines(tb, loc=loc, zh=zh))
            eff.extend(_mod_lines(_sub(body, "modifier"), loc=loc, zh=zh))
            # 解锁议程 / 建筑这类非数值效果
            for fld, label in (("unlocks_agenda", _nm("解锁议程", "Unlocks agenda", zh)),
                               ("unlocks_building", _nm("解锁建筑", "Unlocks building", zh))):
                for v in re.findall(r"\b" + fld + r"\s*=\s*([A-Za-z0-9_]+)", body):
                    nm = _resolve(loc.get(v) or "", loc)
                    if nm:
                        eff.append("%s：%s" % (label, nm))
            # 传统自带的效果文案
            ev = loc.get(key + "_effects")
            if ev:
                eff[:0] = _resolve(ev, loc).split("\n")
            attrs = []
            if key.endswith("_adopt"):
                attrs.append(_attr(_nm("类型", "Type", zh), _nm("开端", "Adoption", zh)))
            elif key.endswith("_finish"):
                attrs.append(_attr(_nm("类型", "Type", zh), _nm("完成", "Finisher", zh)))
            elif key.endswith("_adopt"):
                attrs.append(_attr(_nm("类型", "Type", zh), _nm("开端", "Adoption", zh)))
            out.append(_entry(key, name, "", tree_key, tree_name, attrs,
                              _dedup(eff), src=tree_desc))
    return out


def _collect_edicts(root, loc, zh=True):
    out = []
    for f in sorted(glob.glob(os.path.join(root, D_EDICT, "*.txt"))):
        stem = os.path.splitext(os.path.basename(f))[0]
        if stem.startswith("99_"):
            continue
        cat_key, cat_label = _cat_label(EDICT_CATS, stem, zh)
        for key, body in _top_blocks(_read(f)):
            # 法令的名称键带 `edict_` 前缀（edict_evacuation_protocols），不是裸键
            nk = "edict_" + key
            name = _resolve(_loc_any(loc, nk, key) or "", loc)
            if not name:
                continue
            desc = _resolve(_loc_any(loc, nk + "_desc", key + "_desc") or "", loc)
            eff = []
            ev = _loc_any(loc, nk + "_effects", key + "_effects")
            if ev:
                eff.extend(_resolve(ev, loc).split("\n"))
            eff.extend(_mod_lines(_sub(body, "modifier"), loc=loc, zh=zh))
            attrs = []
            # 花费：resources 块里的 unity / influence
            res = _sub(body, "resources") or ""
            cost = _sub(res, "cost") or ""
            for rk, rv in _pairs(cost).items():
                if rk in ("category", "multiplier"):
                    continue
                nm = _mod_name(rk, loc, zh) or _title(rk)
                attrs.append(_attr(nm, _resolve(rv, loc)))
            out.append(_entry(key, name, desc, cat_key, cat_label, attrs,
                              _dedup(eff), src=stem))
    return out


def _collect_policies(root, loc, zh=True):
    out = []
    for f in sorted(glob.glob(os.path.join(root, D_POLICY, "*.txt"))):
        for gkey, gbody in _top_blocks(_read(f)):
            gname = _resolve(loc.get("policy_" + gkey) or loc.get(gkey) or "", loc) or _title(gkey)
            gdesc = _resolve(loc.get("policy_" + gkey + "_desc") or "", loc)
            for obody in _sub_all(gbody, "option"):
                name_key = _val(obody, "name")
                if not name_key:
                    continue
                name = _resolve(loc.get(name_key) or "", loc)
                if not name:
                    continue
                eff = []
                ev = loc.get(name_key + "_effects")
                if ev:
                    eff.extend(_resolve(ev, loc).split("\n"))
                eff.extend(_mod_lines(_sub(obody, "modifier"), loc=loc, zh=zh))
                attrs = []
                if gdesc:
                    attrs.append(_attr(_nm("政策组", "Policy group", zh), gname))
                out.append(_entry(name_key, name, _resolve(loc.get(name_key + "_desc") or "", loc),
                                  gkey, gname, attrs, _dedup(eff), src=gdesc))
    return out


def _collect_relics(root, loc, zh=True):
    out = []
    for f in sorted(glob.glob(os.path.join(root, D_RELIC, "*.txt"))):
        stem = os.path.splitext(os.path.basename(f))[0]
        if stem.startswith("99_"):
            continue
        for key, body in _top_blocks(_read(f)):
            name = _resolve(loc.get(key) or "", loc)
            if not name:
                continue
            eff = []
            # 遗物的效果几乎都在 active_effect / passive_effect 的 custom_tooltip 里，
            # 不是 modifier 数值块（只有少数被动遗物才直接写 modifier）
            for act in _sub_all(body, "passive_effect") + _sub_all(body, "active_effect"):
                for tt in re.findall(r"custom_tooltip\s*=\s*\"?([A-Za-z0-9_.]+)", act):
                    v = loc.get(tt)
                    if v:
                        eff.extend(_resolve(v, loc).split("\n"))
                eff.extend(_mod_lines(act, loc=loc, zh=zh))
            eff.extend(_mod_lines(_sub(body, "modifier"), loc=loc, zh=zh))
            out.append(_entry(key, name, _resolve(loc.get(key + "_desc") or "", loc),
                              "relic", _nm("遗物", "Relic", zh), None, _dedup(eff), src=stem))
    return out


def _collect_specimens(root, loc, zh=True):
    out = []
    for f in sorted(glob.glob(os.path.join(root, D_SPEC, "*.txt"))):
        for key, body in _top_blocks(_read(f)):
            name = _resolve(loc.get(key) or "", loc)
            if not name:
                continue
            typ = _val(body, "type") or "other"
            cat_key, cat_label = _cat_label(SPECIMEN_TYPES, typ, zh)
            if typ not in SPECIMEN_TYPES:
                cat_label = _title(typ)
            eff = []
            for tb in _sub_all(body, "triggered_country_modifier"):
                eff.extend(_mod_lines(tb, loc=loc, zh=zh))
            eff.extend(_mod_lines(_sub(body, "country_modifier"), loc=loc, zh=zh))
            # 藏品的说明不叫 `<key>_desc`，而是 `_desc_short` / `_desc_details`
            desc = _resolve(_loc_any(loc, key + "_desc_short", key + "_desc") or "", loc)
            det = _resolve(loc.get(key + "_desc_details") or "", loc)
            if det:
                eff.append(det)
            attrs = []
            rar = _val(body, "RARITY") or re.search(r"RARITY\s*=\s*(\w+)", body)
            if rar is not None:
                r = rar if isinstance(rar, str) else rar.group(1)
                RAR = {"common": _nm("普通", "Common", zh), "uncommon": _nm("罕见", "Uncommon", zh),
                       "rare": _nm("稀有", "Rare", zh), "epic": _nm("史诗", "Epic", zh),
                       "exceptional": _nm("珍奇", "Exceptional", zh)}
                attrs.append(_attr(_nm("稀有度", "Rarity", zh), RAR.get(r, _title(r))))
            out.append(_entry(key, name, desc, cat_key, cat_label, attrs,
                              _dedup(eff), src=os.path.basename(f)))
    return out


def _collect_tech(root, loc, zh=True):
    out = []
    for f in sorted(glob.glob(os.path.join(root, D_TECH, "*.txt"))):
        stem = os.path.splitext(os.path.basename(f))[0]
        if stem.startswith("000_"):
            continue
        for key, body in _top_blocks(_read(f)):
            name = _resolve(loc.get(key) or "", loc)
            if not name:
                continue
            area = _val(body, "area") or ""
            cat_key, cat_label = _cat_label(AREA_NAMES, area, zh)
            if area not in AREA_NAMES:
                cat_label = _title(area) if area else _nm("其他", "Other", zh)
            attrs = []
            tier = _val(body, "tier")
            if tier and tier.isdigit():
                n = int(tier)
                attrs.append(_attr(_nm("科技层级", "Tier", zh),
                                   (TIER_NAMES[n - 1] if 0 < n <= len(TIER_NAMES) else str(n)) if zh else str(n)))
            cat = _sub(body, "category")
            if cat:
                cnames = []
                for c in re.findall(r"([a-z_]+)", cat or ""):
                    nm = _resolve(loc.get("tech_cat_" + c) or loc.get(c) or "", loc)
                    if nm:
                        cnames.append(nm)
                if cnames:
                    attrs.append(_attr(_nm("科技类别", "Category", zh), "、".join(dict.fromkeys(cnames))))
            if _val(body, "is_rare") == "yes":
                attrs.append(_attr(_nm("稀有", "Rare", zh), _nm("是", "Yes", zh)))
            if _val(body, "is_dangerous") == "yes":
                attrs.append(_attr(_nm("危险", "Dangerous", zh), _nm("是", "Yes", zh)))
            eff = []
            # 解锁内容：feature_flags → 对应本地化（舰船部件 / 建筑 / 政策选项）
            for ff in re.findall(r"feature_flags\s*=\s*\{([^}]*)\}", body):
                for flag in re.findall(r"([a-z0-9_]+)", ff):
                    nm = _resolve(loc.get("tech_" + flag) or loc.get(flag) or "", loc)
                    if nm and nm not in name:
                        eff.append(nm)
            eff.extend(_mod_lines(_sub(body, "modifier"), loc=loc, zh=zh))
            out.append(_entry(key, name, _resolve(loc.get(key + "_desc") or "", loc),
                              cat_key, cat_label, attrs, _dedup(eff), src=stem))
    return out


def _collect_traits(root, loc, zh=True):
    out = []
    for f in sorted(glob.glob(os.path.join(root, D_TRAIT, "*.txt"))):
        stem = os.path.splitext(os.path.basename(f))[0]
        if stem.startswith("000_"):
            continue
        # 只收物种特质：领袖特质是另一套（level 分类），不要混进来
        txt = _read(f)
        if "species_possible_add" not in txt and "allowed_archetypes" not in txt:
            continue
        for key, body in _top_blocks(txt):
            name = _resolve(loc.get(key) or "", loc)
            if not name:
                continue
            cat = _val(body, "category") or ""
            if not cat:
                # 星球偏好（栖息地适应）这类脚本里不写 category，
                # 命名也不统一：trait_pc_* / trait_machine_pc_* / trait_*_planet_preference
                if key.startswith("trait_pc_") or "preference" in key:
                    cat = "planet_pref"
                else:
                    cat = "normal"           # 蜂巢思维、水生、穴居者这类也没写 category
            cat_key, cat_label = _cat_label(TRAIT_CATS, cat, zh)
            if cat not in TRAIT_CATS:
                cat_label = _title(cat) if cat else _nm("其他", "Other", zh)
            attrs = []
            cost = _val(body, "cost")
            if cost:
                attrs.append(_attr(_nm("基因点数", "Trait points", zh), cost))
            tags = _sub(body, "tags")
            if tags:
                tn = []
                for t in re.findall(r"([a-z_]+)", tags or ""):
                    nm = _resolve(loc.get("trait_tag_" + t) or "", loc)
                    if nm:
                        tn.append(nm)
                    elif t in ("positive",):
                        tn.append(_nm("正面", "Positive", zh))
                    elif t == "negative":
                        tn.append(_nm("负面", "Negative", zh))
                if tn:
                    attrs.append(_attr(_nm("类型", "Type", zh), "、".join(dict.fromkeys(tn))))
            opp = _sub(body, "opposites")
            links = []
            if opp:
                for o in re.findall(r'"([a-z_]+)"', opp):
                    nm = _resolve(loc.get(o) or "", loc)
                    if nm:
                        links.append(nm)
            eff = _mod_lines(_sub(body, "modifier"), loc=loc, zh=zh)
            if not eff:
                dv = loc.get(key + "_effects") or loc.get(key + "_desc")
                if dv:
                    eff = _resolve(dv, loc).split("\n")
            out.append(_entry(key, name, _resolve(loc.get(key + "_desc") or "", loc),
                              cat_key, cat_label, attrs, _dedup(eff), links, src=stem))
    return out


# ---------------------------------------------------------------- 缓存与入口

def _sig(dirs) -> str:
    parts = []
    for p in dirs:
        try:
            st = os.stat(p)
            parts.append("%s:%d:%d" % (os.path.basename(p.rstrip("\\/")), int(st.st_mtime), int(st.st_size)))
        except Exception:
            parts.append("-")
    return "|".join(parts)


def _cache_path(loc_lang: str) -> str:
    return os.path.join(APP_DIR, "wiki_cache_stellaris_%s.json" % loc_lang)


def game_root_of(hint: str = "") -> str:
    """定位群星安装目录：优先用设置里的路径，其次按 Steam 库常见位置探测。"""
    hint = (hint or "").strip().rstrip("\\/")
    if hint and os.path.isdir(os.path.join(hint, D_CIVIC)):
        return hint
    if hint and os.path.isdir(os.path.join(hint, "Stellaris", D_CIVIC)):
        return os.path.join(hint, "Stellaris")
    cands = []
    for drive in ("C", "D", "E", "F", "G"):
        for sub in ("Steam", "SteamLibrary", "Games", "Program Files (x86)\\Steam"):
            cands.append("%s:\\%s\\steamapps\\common\\Stellaris" % (drive, sub))
    for c in cands:
        if os.path.isdir(os.path.join(c, D_CIVIC)):
            return c
    return ""


def _game_version(root: str) -> str:
    try:
        import json as _json
        with open(os.path.join(root, "launcher-settings.json"), encoding="utf-8-sig") as f:
            return (_json.load(f) or {}).get("rawVersion") or ""
    except Exception:
        return ""


def build(root: str, loc_lang: str, zh: bool = True) -> dict:
    """现场抽取全部百科数据。"""
    loc = _load_loc(os.path.join(root, D_LOC, loc_lang))
    civics, origins = _collect_civics_origins(root, loc, zh)
    data = {
        "ok": True, "supported": True, "game": "stellaris", "lang": loc_lang,
        "gameName": _nm("群星", "Stellaris", zh),
        "gameVer": _game_version(root),
        "civics": civics, "origins": origins,
        "aps": _collect_aps(root, loc, zh),
        "traditions": _collect_traditions(root, loc, zh),
        "edicts": _collect_edicts(root, loc, zh),
        "policies": _collect_policies(root, loc, zh),
        "relics": _collect_relics(root, loc, zh),
        "specimens": _collect_specimens(root, loc, zh),
        "tech": _collect_tech(root, loc, zh),
        "traits": _collect_traits(root, loc, zh),
    }
    tabs, counts, orders = [], {}, {}
    for tid, icon, zh_l, en_l, layout in TABS:
        arr = data.get(tid) or []
        counts[tid] = len(arr)
        # 分类顺序 = 首次出现顺序，保证「物理学/社会学/工程学」这类不按条目数乱排
        order, seen = [], set()
        for it in arr:
            k = it.get("catKey") or ""
            if k and k not in seen:
                seen.add(k)
                order.append(k)
        orders[tid] = order
        tabs.append({"id": tid, "icon": icon, "label": _nm(zh_l, en_l, zh),
                     "layout": layout, "count": len(arr)})
    data["tabs"] = tabs
    data["counts"] = counts
    data["catOrders"] = orders
    data["total"] = sum(counts.values())
    return data


def load(game_root: str = "", ui_lang: str = "zh", force: bool = False) -> dict:
    """读取（并缓存）群星百科。"""
    ui_lang = (ui_lang or "zh").lower()
    loc_lang = LANG_MAP.get(ui_lang, "simp_chinese")
    zh = loc_lang != "english"

    root = game_root_of(game_root)
    if not root:
        return {"ok": False, "supported": True, "game": "stellaris",
                "error": _nm("没找到《群星》安装目录，请在设置里把游戏根目录指向 Stellaris 文件夹",
                             "Stellaris install directory not found. Set the game root in Settings.", zh)}

    dirs = [os.path.join(root, d) for d in (D_CIVIC, D_AP, D_TRAD, D_EDICT,
                                            D_POLICY, D_RELIC, D_SPEC, D_TECH, D_TRAIT)]
    dirs.append(os.path.join(root, D_LOC, loc_lang))
    sig = "%s|%s" % (loc_lang, _sig(dirs))
    cpath = _cache_path(loc_lang)

    with _LOCK:
        hit = _MEM.get(sig)
        if hit and not force:
            d = dict(hit)
            d["cached"] = True
            return d

        if not force:
            try:
                with open(cpath, encoding="utf-8") as f:
                    blob = json.load(f)
                if blob.get("ver") == SL_VER and blob.get("sig") == sig \
                        and (blob.get("root") or "").lower() == root.lower():
                    data = blob.get("data") or {}
                    _MEM[sig] = data
                    d = dict(data)
                    d["cached"] = True
                    return d
            except Exception:
                pass

        data = build(root, loc_lang, zh)
        _MEM[sig] = data
        try:
            with open(cpath, "w", encoding="utf-8") as f:
                json.dump({"ver": SL_VER, "sig": sig, "root": root, "data": data},
                          f, ensure_ascii=False)
        except Exception:
            pass
        return data



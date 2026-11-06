# -*- coding: utf-8 -*-
"""钢铁雄心4 百科数据：从游戏本体脚本 + 官方本地化抽取结构化资料。

设计要点
--------
- **完全离线**：只读 `<HOI4>\\common\\...`、`interface\\*.gfx` 与 `localisation\\<lang>`。
- 与 CK3 / 群星百科共用前端条目结构 `{key,name,desc,cat,catKey,attrs,effects,links}`，
  tab 自带 `layout`，前端据此选渲染分支（generic / focus）。
- 国策树单独走 `focus` layout：后端给每个 focus 的绝对网格坐标 + 前置/互斥关系，
  前端画布按坐标绘制并支持缩放拖动。

HOI4 本地化的特殊标记（必须清洗）
------------------------------
- `§Y ... §!`        颜色区间 → 删标记留正文
- `£icon£`           内联图标 → 删
- `$KEY$`            引用其它本地化键 → 递归展开
- `\\n`              字面换行 → 真换行
- `[?var]` / `[Root.GetName]`  运行时取值 → 删（脚本变量无静态值）

修饰器单位的判定
--------------
HOI4 的 modifier 数值本身不带单位语义：`political_power_factor = 0.25` 是 +25%，
而 `political_power_gain = 0.5` 是每天 +0.5 点。判定链：
1. 本地化里有 `<key>_tt` 模板 → 从 `$RIGHT|+=%1$` 这类格式串里读出「百分比 / 小数位」；
2. `common/modifier_definitions` 里显式声明（value_type）；
3. 兜底：键名以 `_factor` 结尾，或 0<|v|<1 且键名不带 `_add/_value/_amount/_gain` → 百分比。
"""

import os
import re
import glob
import json
import time
import bisect
import threading

try:
    from ck3core import APP_DIR
except Exception:
    APP_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

H4_VER = 10                      # 解析逻辑变更时 +1，强制重建缓存

LANG_MAP = {
    "zh": "simp_chinese", "en": "english", "fr": "french",
    "de": "german", "sv": "english",
}

D_IDEAS = os.path.join("common", "ideas")
D_CHAR = os.path.join("common", "characters")
D_NF = os.path.join("common", "national_focus")
D_CL = os.path.join("common", "country_leader")
D_UL = os.path.join("common", "unit_leader")
D_MODDEF = os.path.join("common", "modifier_definitions")
D_LOC = "localisation"

# tab 元数据：id → (图标, 中文名, 英文名, layout)
TABS = [
    ("ideas", "💡", "国家理念", "Ideas", "generic"),
    ("officers", "🎖", "军官团与顾问", "Officers & Advisors", "generic"),
    ("focus", "🌳", "国策树", "National Focus", "focus"),
]

# ideas 顶层分类 → (中文名, 归入哪个页面分组)
# 游戏内分类标题大多能直接查到本地化（political_advisor / army_chief …），
# 只有 spirit / country 这类没有，需要手工补。
IDEA_CATS = [
    ("country", "国家精神", "country"),
    ("army_spirit", "陆军精神", "spirit"),
    ("army_command_spirit", "陆军指挥精神", "spirit"),
    ("division_command_spirit", "师级指挥精神", "spirit"),
    ("academy_spirit", "陆军学院精神", "spirit"),
    ("navy_spirit", "海军精神", "spirit"),
    ("navy_command_spirit", "海军指挥精神", "spirit"),
    ("naval_academy_spirit", "海军学院精神", "spirit"),
    ("air_force_spirit", "空军精神", "spirit"),
    ("air_force_command_spirit", "空军指挥精神", "spirit"),
    ("air_academy_spirit", "空军学院精神", "spirit"),
    ("political_advisor", "政治顾问", "advisor"),
    ("army_chief", "陆军部长", "advisor"),
    ("navy_chief", "海军部长", "advisor"),
    ("air_chief", "空军部长", "advisor"),
    ("high_command", "总司令", "advisor"),
    ("theorist", "理论家", "advisor"),
    ("industrial_concern", "工业企业", "designer"),
    ("aircraft_manufacturer", "飞机制造商", "designer"),
    ("materiel_manufacturer", "装备制造商", "designer"),
    ("naval_manufacturer", "造船厂", "designer"),
    ("tank_manufacturer", "坦克设计商", "designer"),
    ("economy", "经济法案", "law"),
    ("mobilization_laws", "动员法案", "law"),
    ("trade_laws", "贸易法案", "law"),
    ("conscription_laws", "征兵法案", "law"),
    ("naval_command_spirit", "海军指挥精神", "spirit"),
]
IDEA_CAT_LABEL = {k: v for k, v, _ in IDEA_CATS}
IDEA_CAT_GROUP = {k: g for k, _, g in IDEA_CATS}

# 顾问槽位（advisor slot）→ 中文
SLOT_LABEL = {
    "political_advisor": "政治顾问",
    "army_chief": "陆军部长",
    "navy_chief": "海军部长",
    "air_chief": "空军部长",
    "high_command": "总司令",
    "theorist": "理论家",
    "industrial_concern": "工业企业",
    "aircraft_manufacturer": "飞机制造商",
    "materiel_manufacturer": "装备制造商",
    "naval_manufacturer": "造船厂",
    "tank_manufacturer": "坦克设计商",
}

# 将领特质类别（unit_leader/00_traits.txt 的 trait_type）
UL_TRAIT_TYPE = {
    "basic_trait": "基础",
    "status_trait": "状态",
    "personality_trait": "性格",
    "assignable_trait": "可任命",
    "basic_terrain_trait": "地形",
    "assignable_terrain_trait": "可任命地形",
    "exile": "流亡",
}

_LOCK = threading.Lock()
_MEM = {}


# ---------------------------------------------------------------- 基础解析

_AT_DEF = re.compile(r"^\s*@([A-Za-z0-9_]+)\s*=\s*(-?\d+(?:\.\d+)?)\s*$", re.M)
_AT_USE = re.compile(r"@([A-Za-z0-9_]+)")
# 这些在国策树解析里会被调用上万次，必须预编译
_BLK_RE = re.compile(r"([A-Za-z0-9_\-.]+)\s*=\s*\{")
_SCAL_RE = re.compile(r"([A-Za-z0-9_\-.]+)\s*=\s*([^\s{}\n]+)")
_NUM_RE = re.compile(r"([A-Za-z0-9_]+)\s*=\s*(-?\d+(?:\.\d+)?)")


def _at_subst(text: str) -> str:
    """把 `@Name = 10` 这类文件内脚本变量替换成数值（HOI4 大量使用）。"""
    defs = dict(_AT_DEF.findall(text or ""))
    if not defs:
        return text
    return _AT_USE.sub(lambda m: defs.get(m.group(1), m.group(0)), text)


def _read(path: str) -> str:
    try:
        with open(path, encoding="utf-8-sig", errors="ignore") as f:
            txt = f.read()
    except Exception:
        return ""
    return _at_subst(re.sub(r"#[^\n]*", "", txt))


def _match_brace(text: str, start: int) -> int:
    """从 `{` 位置开始，返回与之配对的 `}` 的下标；找不到返回 -1。"""
    depth = 0
    for i in range(start, len(text)):
        c = text[i]
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return i
    return -1


_TOK = re.compile(
    r"([A-Za-z0-9_\-.]+)\s*=\s*\{|\{|\}"
    r"|([A-Za-z0-9_\-.]+)\s*=\s*(\"(?:[^\"\\]|\\.)*\"|[^\s{}\n]+)"
)


def _scan_pairs(text: str):
    """单遍扫描脚本，返回 (blocks, scalars)。

    blocks  = [(key, body, depth, start)]，depth=0 表示最外层，start 是 body 的绝对起点；
    scalars = [(key, value, depth)]。

    HOI4 的缩进极不统一（tab / 空格混用，同一文件里还有反缩进），
    所以只能靠括号配对判断层级，不能靠缩进。整个百科要解析上万次，
    这里用一条正则 + 栈一次扫完（纯 C 层扫描，比逐字符快一个量级）。
    """
    blocks, scal, stack = [], [], []
    for m in _TOK.finditer(text or ""):
        g1 = m.group(1)
        if g1:
            stack.append((g1, m.end()))
        elif m.group(0) == "{":
            stack.append((None, m.end()))
        elif m.group(0) == "}":
            if stack:
                k, st = stack.pop()
                if k:
                    blocks.append((k, text[st:m.start()], len(stack), st))
        elif m.group(2):
            scal.append((m.group(2), m.group(3).strip('"'), len(stack), m.start()))
    return blocks, scal


def _by_depth(items, depth):
    """按层级分组并按起点排序，方便用区间二分查找。"""
    out = [it for it in items if it[-2] == depth]
    out.sort(key=lambda x: x[-1])
    return out


def _in_range(sorted_items, starts, lo, hi):
    """取起点落在 [lo, hi) 内的项（sorted_items 按起点排序，starts 是起点列表）。"""
    i = bisect.bisect_left(starts, lo)
    out = []
    n = len(sorted_items)
    while i < n and starts[i] < hi:
        out.append(sorted_items[i])
        i += 1
    return out


def _blocks(text: str) -> list:
    """取出所有 `key = { ... }`（含嵌套），返回 [(key, body)]。"""
    return [(k, b) for k, b, _, _ in _scan_pairs(text)[0]]


def _top_blocks(text: str) -> list:
    """只取最外层块。"""
    return [(k, b) for k, b, d, _ in _scan_pairs(text)[0] if d == 0]


def _top_scalars(text: str) -> list:
    """只取最外层 `key = value`。"""
    return [(k, v) for k, v, d, _ in _scan_pairs(text)[1] if d == 0]


def _top_sub(text: str, key: str):
    """只在**顶层**找 `key = { ... }`。

    不能用 `_sub`：HOI4 的 `ai_will_do = { modifier = { factor = 0 } }` 之类
    会把同名的 `modifier` 嵌在别的块里，按文本第一命中就会抓错（群星那边踩过同样的坑）。
    """
    for k, b, d, _ in _scan_pairs(text)[0]:
        if d == 0 and k == key:
            return b
    return None


def _val(body: str, key: str):
    """取 `key = value`（非块）的裸值。"""
    m = re.search(r"(?<![A-Za-z0-9_])" + re.escape(key) + r"\s*=\s*([^\s{}\n]+)", body or "")
    return m.group(1).strip('"') if m else None


def _num(v):
    try:
        return float(v)
    except Exception:
        return None


# ---------------------------------------------------------------- 本地化

_LOC_CACHE = {}


def _load_loc(loc_dir: str) -> dict:
    """读整个本地化目录（HOI4 一个语言几百个 yml，约 30 万键）。

    HOI4 yml 格式：文件首行 `l_simp_chinese:`，正文 ` key:0 "文本"`，
    冒号后的 0 是语言版本号，可以省略。文本可能跨行，所以按引号状态机扫。
    """
    if loc_dir in _LOC_CACHE:
        return _LOC_CACHE[loc_dir]
    loc = {}
    files = sorted(glob.glob(os.path.join(loc_dir, "*.yml")))
    for f in files:
        try:
            with open(f, encoding="utf-8-sig", errors="ignore") as fh:
                txt = fh.read()
        except Exception:
            continue
        # 键名允许前导空格与字母数字下划线点号，值用双引号
        i, n = 0, len(txt)
        while i < n:
            nl = txt.find("\n", i)
            if nl < 0:
                nl = n
            line = txt[i:nl]
            i = nl + 1
            m = re.match(r"\s*([A-Za-z0-9_.\-]+)\s*:\s*\d*\s*\"", line)
            if not m:
                continue
            key = m.group(1)
            # 从引号开始扫到未转义的收尾引号，允许跨行
            # 行首绝对位置 = i - len(line) - 1（i 已移到下一行行首）
            line_start = i - len(line) - 1
            buf = []
            k = line_start + m.end()
            while k < n:
                c = txt[k]
                if c == "\\" and k + 1 < n:
                    buf.append(txt[k:k + 2])
                    k += 2
                    continue
                if c == '"':
                    break
                buf.append(c)
                k += 1
            loc[key] = "".join(buf)
            # 跳过已被吃掉的换行
            i = k + 1
            while i < n and txt[i] == "\n":
                i += 1
    _LOC_CACHE[loc_dir] = loc
    return loc


_COLOR = re.compile(r"§[A-Za-z0-9!]")
_ICON = re.compile(r"£[^£\s]{1,40}£")
_SCRIPT = re.compile(r"\[[^\[\]]{0,80}\]")
_REF = re.compile(r"\$([A-Za-z0-9_.\-]+)(?:\|[^$]*)?\$")


def _loc_clean(s: str, loc: dict, depth: int = 0) -> str:
    """清洗本地化文本：展开 `$REF$`、删颜色/图标/运行时取值标记。"""
    if not s:
        return ""
    s = str(s)

    def rep(m):
        k = m.group(1)
        if k.upper() == "RIGHT":
            return ""
        v = loc.get(k)
        return _loc_clean(v, loc, depth + 1) if (v and depth < 3) else ""

    s = _REF.sub(rep, s)
    s = s.replace("\\n", "\n")
    s = _COLOR.sub("", s)
    s = _ICON.sub("", s)
    s = _SCRIPT.sub("", s)
    s = s.replace("\\t", " ").replace("\t", " ")
    s = re.sub(r"[ \u00a0]+", " ", s)
    s = re.sub(r"\n{3,}", "\n\n", s)
    return s.strip()


def _L(loc: dict, *keys) -> str:
    """按顺序取第一个非空本地化键。"""
    for k in keys:
        if k and loc.get(k):
            return _loc_clean(loc[k], loc)
    return ""


# ---------------------------------------------------------------- 修饰器

_MOD_NAME = {}          # 小写键 → MODIFIER_ 键（含去掉 country_ / _factor 的别名）
_MOD_SPEC = {}          # 小写键 → (is_pct, decimals)

# HOI4 有一批修饰器只在代码里命名，本地化里没有 `MODIFIER_` 键（中英文都没有），
# 游戏里靠 C++ 内部的 ModifierType 映射显示。这里按中文版的实际文案补齐高频项，
# 剩下的低频键退化成英文短语（好过露出裸键）。
MOD_ALIAS = {
    # 工厂产出
    "industrial_capacity_factory": "民用工厂产出",
    "industrial_capacity_dockyard": "船坞产出",
    # 建造速度（按建筑类型）
    "production_speed_arms_factory_factor": "军用工厂建造速度",
    "production_speed_industrial_complex_factor": "民用工厂建造速度",
    "production_speed_dockyard_factor": "船坞建造速度",
    "production_speed_infrastructure_factor": "基础设施建造速度",
    "production_speed_bunker_factor": "地堡建造速度",
    "production_speed_coastal_bunker_factor": "海岸地堡建造速度",
    "production_speed_rail_way_factor": "铁路建造速度",
    "production_speed_supply_node_factor": "补给枢纽建造速度",
    "production_speed_air_base_factor": "空军基地建造速度",
    "production_speed_radar_station_factor": "雷达站建造速度",
    "production_speed_synthetic_refinery_factor": "合成炼油厂建造速度",
    "production_speed_fuel_silo_factor": "燃料库建造速度",
    "production_speed_anti_air_building_factor": "防空炮建造速度",
    "production_speed_naval_base_factor": "海军基地建造速度",
    "production_speed_nuclear_reactor_factor": "核反应堆建造速度",
    "production_speed_nuclear_facility_factor": "核设施建造速度",
    # 意识形态漂移 / 接受度
    "communism_drift": "共产主义支持度变化",
    "fascism_drift": "法西斯主义支持度变化",
    "democratic_drift": "民主主义支持度变化",
    "neutrality_drift": "中立主义支持度变化",
    "communism_acceptance": "共产主义接受度",
    "fascism_acceptance": "法西斯主义接受度",
    "democratic_acceptance": "民主主义接受度",
    "neutrality_acceptance": "中立主义接受度",
    # 紧张度门槛
    "send_volunteers_tension": "派遣志愿军所需紧张度",
    "lend_lease_tension": "租借法案所需紧张度",
    "join_faction_tension": "加入阵营所需紧张度",
    "generate_wargoal_tension": "制造战争借口所需紧张度",
    "guarantee_tension": "保障独立所需紧张度",
    # 顾问/设计商花费
    "political_advisor_cost_factor": "政治顾问花费",
    "army_chief_cost_factor": "陆军部长花费",
    "navy_chief_cost_factor": "海军部长花费",
    "air_chief_cost_factor": "空军部长花费",
    "high_command_cost_factor": "总司令花费",
    "theorist_cost_factor": "理论家花费",
    "industrial_concern_cost_factor": "工业企业花费",
    "tank_manufacturer_cost_factor": "坦克设计商花费",
    "aircraft_manufacturer_cost_factor": "飞机制造商花费",
    "materiel_manufacturer_cost_factor": "装备制造商花费",
    "naval_manufacturer_cost_factor": "造船厂花费",
    # 经验
    "experience_gain_army_factor": "陆军经验获取",
    "experience_gain_navy_factor": "海军经验获取",
    "experience_gain_air_factor": "空军经验获取",
    "experience_gain_army": "陆军经验获取",
    "experience_gain_navy": "海军经验获取",
    "experience_gain_air": "空军经验获取",
    "experience_gain_factor": "经验获取",
    # 法案花费
    "economy_cost_factor": "经济法案花费",
    "mobilization_laws_cost_factor": "动员法案花费",
    "trade_laws_cost_factor": "贸易法案花费",
    "conscription_cost_factor": "征兵法案花费",
    # 人口 / 人力
    "non_core_manpower": "非核心人力",
    "monthly_population": "每月人口",
    "recruitable_population": "可征募人口",
    # 稳定度相关
    "party_popularity_stability_factor": "党派支持度对稳定度影响",
    "war_stability_factor": "战争对稳定度影响",
    "offensive_war_stability_factor": "进攻战争对稳定度影响",
    "defensive_war_stability_factor": "防御战争对稳定度影响",
    # 抵抗 / 顺从
    "resistance_damage_to_garrison": "驻军所受抵抗伤害",
    "resistance_damage_to_garrison_on_our_occupied_states": "我方被占领区驻军所受抵抗伤害",
    "compliance_gain": "每日顺从度增长",
    "foreign_subversive_activites": "外国颠覆活动",
    # 装备 / 海军 / 空军
    "refit_speed": "改装速度",
    "refit_ic_cost": "改装工业花费",
    "convoy_escort_efficiency": "护航效率",
    "sortie_efficiency": "出击效率",
    "fighter_sortie_efficiency": "战斗机出击效率",
    "naval_invasion_prep_speed": "海军入侵准备速度",
    "movement": "移动速度",
    # 外交 / 附庸
    "extra_trade_to_overlord_factor": "对宗主国的额外贸易",
    "subjects_autonomy_gain": "附庸自治度增长",
    "overlord_trade_cost_factor": "宗主国贸易花费",
    # 指挥点数
    "command_cap_increase": "指挥点数上限",
    "command_power": "指挥点数",
    "command_power_gain": "每日指挥点数增长",
    "max_command_power": "指挥点数上限",
    # 军工组织 / 其它
    "military_industrial_organization_funds_gain": "军工组织资金获取",
    "military_industrial_organization_research_bonus": "军工组织研究加成",
    "promote_cost_factor": "晋升花费",
    "critical_receive_chance": "被暴击概率",
    "specialization_nuclear_speed_factor": "核研究速度",
    "bunker_max_level_terrain_limit": "地堡最高等级地形限制",
    "supply_combat_penalties_on_core_factor": "本土补给战斗惩罚",
    "army_sub_unit_militia_attack_factor": "民兵攻击",
    "army_sub_unit_militia_defence_factor": "民兵防御",
    "army_sub_unit_militia_max_org_factor": "民兵最大组织度",
    "modifier_army_sub_unit_militia_attack_factor": "民兵攻击",
    "modifier_army_sub_unit_militia_defence_factor": "民兵防御",
    "modifier_army_sub_unit_militia_max_org_factor": "民兵最大组织度",
}

# 这些键语义上确定是百分比，不受「0<|v|<1」启发式限制
FORCE_PCT = ("_drift", "_acceptance", "_tension", "_speed", "_efficiency",
             "_chance", "weekly_", "daily_")
# 这些后缀是绝对量（点 / 个 / 天），即使值小于 1 也不是百分比
ABS_SUFFIX = ("_add", "_value", "_amount", "_count", "_cap", "_cap_add",
              "_cost", "_level", "_size", "_limit")


def _build_mod_index(loc: dict):
    """建立修饰器名称索引。

    HOI4 的本地化键是 `MODIFIER_<大写键名>`，但脚本里的键名与本地化键经常
    不完全对应（多了 country_ 前缀、少了 _factor/_mult 后缀），所以做双向归一。
    """
    _MOD_NAME.clear()
    _MOD_SPEC.clear()
    for k in loc:
        # 本地化里 `MODIFIER_` 与 `MODIFIERS_`（复数）两种前缀都在用
        if k.startswith("MODIFIER_"):
            raw = k[len("MODIFIER_"):].lower()
        elif k.startswith("MODIFIERS_"):
            raw = k[len("MODIFIERS_"):].lower()
        else:
            continue
        _MOD_NAME.setdefault(raw, k)
        # 别名：去掉 country_/state_ 前缀
        for pre in ("country_", "state_", "unit_", "army_", "navy_", "air_"):
            if raw.startswith(pre):
                _MOD_NAME.setdefault(raw[len(pre):], k)
        # 别名：去掉 _factor / _mult 后缀
        for suf in ("_factor", "_mult", "_modifier"):
            if raw.endswith(suf):
                _MOD_NAME.setdefault(raw[:-len(suf)], k)
    # 值格式：`<key>_tt` 里形如 $RIGHT|+=%1$ / $RIGHT|+=2$
    for k in loc:
        if not k.endswith("_tt"):
            continue
        base = k[:-3].lower()
        tpl = loc[k] or ""
        m = re.search(r"\$RIGHT\|[+\-=]*(%?)(\d?)\$", tpl)
        if m:
            _MOD_SPEC[base] = (bool(m.group(1)), int(m.group(2) or 0))
    # 去掉 _tt 后缀别名，方便 `political_power_factor` 直接命中
    for k in list(_MOD_SPEC):
        for suf in ("_factor", "_gain", "_cost"):
            if k.endswith(suf):
                _MOD_SPEC.setdefault(k[:-len(suf)], _MOD_SPEC[k])


def _mod_name(key: str) -> str:
    """修饰器显示名。先查人工别名表，再查本地化索引，最后返回空。"""
    lo = (key or "").lower()
    if lo in MOD_ALIAS:
        return MOD_ALIAS[lo]
    for pre in ("modifier_", "modifiers_"):
        if lo.startswith(pre):
            lo = lo[len(pre):]
            if lo in MOD_ALIAS:
                return MOD_ALIAS[lo]
    # 有一批修饰器本地化用的是裸小写键（fighter_design_cost_factor、
    # modifier_unit_cavalry_design_cost_factor），不走 MODIFIER_ 前缀
    for cand in (lo, "modifier_" + lo, lo.replace("country_", ""),
                 lo.replace("_factor", ""), lo.replace("_mult", ""),
                 lo.replace("_add", "")):
        if _LOC_CUR.get(cand):
            return _loc_clean(_LOC_CUR[cand], _LOC_CUR)
        k = _MOD_NAME.get(cand)
        if k:
            return _loc_clean(_LOC_CUR.get(k, ""), _LOC_CUR)
    return ""


def _mod_pct(key: str, val: float) -> bool:
    lo = (key or "").lower()
    spec = _MOD_SPEC.get(lo)
    if spec:
        return spec[0]
    if lo.endswith("_factor") or lo.endswith("_mult"):
        return True
    if any(lo.endswith(s) for s in ABS_SUFFIX):
        return False
    if any(s in lo for s in FORCE_PCT):
        return True
    return 0 < abs(val) < 1


def _fmt_num(v: float, pct: bool, dec: int = 0) -> str:
    sign = "+" if v > 0 else ("-" if v < 0 else "")
    a = abs(v)
    if pct:
        a *= 100
        dec = 0 if dec <= 0 else dec
    else:
        dec = 0 if float(a).is_integer() else (dec or 1)
    s = ("%." + str(dec) + "f") % a
    if "." in s:
        s = s.rstrip("0").rstrip(".")
    return sign + s + ("%" if pct else "")


def _mod_line(key: str, val):
    """把一条 modifier 变成「政治点数 +25%」这样的可读文本。"""
    v = _num(val)
    if v is None:
        return "%s：%s" % (key, val)
    name = _mod_name(key)
    if not name:
        # 本地化确实没有这个键（HOI4 有一批只在代码里命名的修饰器），
        # 退化成可读的英文短语，至少比裸键好认。
        name = key.replace("_", " ")
    pct = _mod_pct(key, v)
    spec = _MOD_SPEC.get((key or "").lower())
    dec = spec[1] if spec and pct else 0
    return "%s %s" % (name, _fmt_num(v, pct, dec))


_LOC_CUR = {}


def _mod_block(body, loc=None):
    """解析 `modifier = { ... }` / `research_bonus` 等，返回文本行列表。"""
    out = []
    if not body:
        return out
    for k, v in re.findall(r"([A-Za-z0-9_]+)\s*=\s*(-?\d+(?:\.\d+)?)", body):
        out.append(_mod_line(k, v))
    # 效果写成自定义文案的情况（`custom_modifier_tooltip = xxx_tt`）：
    # 游戏里就是在悬浮框显示一段文字，数值全在那段本地化里，单看脚本取不到，
    # 所以直接把那段文案搬过来当效果行。全库 ideas 里有 183 处这么写。
    if loc:
        for ck in re.findall(r"custom_modifier_tooltip\s*=\s*([A-Za-z0-9_]+)", body):
            txt = _L(loc, ck)
            if not txt:
                continue
            # 这类文案常是多行列表（「- xxx\n- yyy」），列表项里显示不了换行，
            # 用分号连起来读着还算顺；句末已经是句号的就不再补分号
            txt = re.sub(r"([。！？；.!?;])\s*\n+\s*", r"\1", txt)
            txt = re.sub(r"\s*\n+\s*", "；", txt).strip("； ")
            if txt and txt not in out:
                out.append(txt)
    return out


def _equip_name(key: str, loc: dict) -> str:
    """装备/装备组的中文名。loc 里既有 `infantry_equipment` 也有 `EQUIPMENT_...`。"""
    for cand in (key, key + "_name", "EQUIPMENT_" + key.upper(),
                 key.replace("_equipment", "")):
        if loc.get(cand):
            return _loc_clean(loc[cand], loc)
    return key.replace("_", " ")


def _equip_bonus_lines(body: str, loc: dict) -> list:
    """设计商的 `equipment_bonus = { <装备> = { build_cost_ic = -0.1 } }`。"""
    out = []
    for eq, eb in _top_blocks(body or ""):
        lines = _mod_block(eb, loc)
        nm = _equip_name(eq, loc)
        for ln in lines:
            out.append("%s：%s" % (nm, ln))
    return out


# ---------------------------------------------------------------- 理念（Ideas）

# 不想进百科的分类：隐藏理念是给事件/脚本临时用的，界面上也不显示
SKIP_IDEA_CATS = {"hidden_ideas"}

# 少量只在个别国家文件里出现的分类，给了中文名免得界面上突兀
CAT_FALLBACK = {
    "economy": "经济法案",
    "mobilization_laws": "动员法案",
    "trade_laws": "贸易法案",
    "conscription_laws": "征兵法案",
    "land": "陆军将领",
    "operative": "特工",
}

# 脚本里留的测试条目，游戏里不会正常出现，百科也不收
IDEA_SKIP = {"FIN_test", "ITA_test", "MTG_TEST_NAVAL_IDEA"}

# 这 48 条游戏自己的本地化是运行时取值（写成 `[GER.GetBulwarkName]` 这种取脚本结果的
# 表达式，静态读不到内容），或者干脆没登记键 —— 界面上就会漏出一串原始 key。
# 数量固定、都是有名有姓的条目，手工补中文名最省事，也避免以后再冒裸键。
IDEA_NAME_FIX = {
    # 国家精神
    "GER_bulwark_against_bolshevism": "反布尔什维克壁垒",
    "special_economic_zones": "特别经济区",
    "AST_no_foreign_battlefields_idea_2": "不在海外流血",
    "AST_monarchist_intervention_spirit": "君主派干预精神",
    "idea_RAJ_growing_independence_movement_0": "日益壮大的独立运动",
    "idea_RAJ_growing_independence_movement_1": "独立运动愈演愈烈",
    "idea_RAJ_to_shake_the_world": "震撼世界",
    "CHI_obsolete_fortifications": "过时的防御工事",
    "CHI_local_suppliers": "本地供应商",
    "CZE_strike_at_the_heart_of_bolshevism_idea": "直击布尔什维克心脏",
    "GRE_memories_of_the_anatolian_catastrophe": "安纳托利亚灾难的记忆",
    "GRE_cooperate_with_foreign_monopolies_spirit": "与外国垄断资本合作",
    "GRE_protectors_of_the_mediterranean_spirit": "地中海守护者",
    "GRE_improving_our_defensive_lines": "加固我们的防线",
    "idea_INS_radio_democratic": "广播电台：民主派",
    "idea_INS_radio_fascist": "广播电台：法西斯派",
    "idea_INS_radio_communist": "广播电台：共产派",
    "idea_INS_radio_sukarno": "广播电台：苏加诺",
    "ITA_the_fight_against_stalinism": "对抗斯大林主义",
    "NOR_mobile_government_ns": "流动政府",
    "idea_PAR_prevent_total_state_bankcruptcy": "避免国家彻底破产",
    "idea_GUYA_weapons_licensing": "武器生产许可",
    "SWE_small_scale_strikes": "小规模罢工",
    "SWE_political_commisars": "政治委员",
    "TUR_debt_repayments": "偿还外债",
    "YUG_idea_princess_alexandrine_britain": "亚历山德琳公主：英国",
    "YUG_idea_princess_alexandra_germany": "亚历山德拉公主：德国",
    "YUG_idea_princess_marie_italy": "玛丽公主：意大利",
    # 工业企业与设计商
    "GER_reichswerke": "帝国工厂",
    "GER_reichswerke_improved": "帝国工厂（扩建）",
    "GER_reichswerke_improved_2": "帝国工厂（再次扩建）",
    "ARG_rubin_design_bureau": "鲁宾设计局",
    "AST_kirov": "基洛夫工厂",
    "BEL_kirov": "基洛夫工厂（比利时）",
    "ETH_cie": "埃塞俄比亚铁路公司",
    "machinefabriek_braat": "布拉特机械厂",
    "nsm_amsterdam_ins": "阿姆斯特丹航运公司",
    "artillerie_inrichtingen_ins": "炮兵器材厂",
    "artillerie_inrichtingen_pyrotechnische": "烟火器材厂",
    "bio_pharma": "生物制药公司",
    "fokker_ins": "福克飞机厂",
    "idea_INS_royal_dutch_shell_concern": "荷兰皇家壳牌",
    "idea_INS_bank_of_java_concern": "爪哇银行",
    "idea_INS_telkom_indonesia_concern": "印尼电信公司",
    "idea_INS_staatspoorwegen_ss_concern": "荷兰国家铁路",
    "germano_mexican_tanks": "德墨坦克合作",
    "russo_mexican_tanks": "俄墨坦克合作",
    "american_mexican_tanks": "美墨坦克合作",
}


def _scan_ideas(root: str, loc: dict):
    """扫 common/ideas，返回 (条目列表, key → {traits, modifier, cost})。

    结构是 `ideas = { <分类> = { <理念> = { ... } } }`，每个文件只解析一次，
    再按层级 + 起点区间归属，避免 5000+ 条目反复扫描正文。

    条目结构对齐 CK3/群星百科：`{key,name,desc,cat,catKey,attrs,effects,links}`。
    顾问类理念（army_chief 等）本身没有名字与效果，效果写在 leader_traits 里，
    所以这里只把它们的 traits / 花费记进 index，供「军官团与顾问」页引用。
    """
    items, idx = [], {}
    for f in sorted(glob.glob(os.path.join(root, D_IDEAS, "*.txt"))):
        base = os.path.basename(f)
        if base.startswith("_debug"):
            continue
        txt = _read(f)
        if "ideas" not in txt:
            continue
        all_blocks, all_scal = _scan_pairs(txt)
        bd = {d: _by_depth(all_blocks, d) for d in (0, 1, 2, 3)}
        sd = {d: _by_depth(all_scal, d) for d in (3,)}
        bs = {d: [it[-1] for it in v] for d, v in bd.items()}
        ss = {d: [it[-1] for it in v] for d, v in sd.items()}

        for k0, body, _, st in bd[0]:
            if k0 != "ideas":
                continue
            en = st + len(body)
            for cat, cbody, _, cs in _in_range(bd[1], bs[1], st, en):
                if cat in SKIP_IDEA_CATS:
                    continue
                cat_label = IDEA_CAT_LABEL.get(cat)
                if cat_label is None:
                    cat_label = CAT_FALLBACK.get(cat) or cat.replace("_", " ")
                cen = cs + len(cbody)
                for key, ib, _, is_ in _in_range(bd[2], bs[2], cs, cen):
                    ien = is_ + len(ib)
                    scal = {k: v for k, v, _, _ in _in_range(sd[3], ss[3], is_, ien)}
                    subs = {}
                    for sk, sb, _, _ in _in_range(bd[3], bs[3], is_, ien):
                        subs.setdefault(sk, sb)
                    name = scal.get("name") or key
                    traits = []
                    tb = subs.get("traits")
                    if tb:
                        traits = re.findall(r"[A-Za-z0-9_]+", tb)
                    mod = subs.get("modifier")
                    idx[key] = {
                        "cat": cat,
                        "traits": traits,
                        "modifier": mod or "",
                        "cost": scal.get("cost"),
                    }
                    desc = _L(loc, key + "_desc")
                    effects = []
                    if mod:
                        effects += _mod_block(mod, loc)
                    rb = subs.get("research_bonus")
                    if rb:
                        for rk, rv in re.findall(
                                r"([A-Za-z0-9_]+)\s*=\s*(-?\d+(?:\.\d+)?)", rb):
                            nm = _loc_clean(loc.get(rk, ""), loc) or rk.replace("_", " ")
                            effects.append("%s研究速度 %s" % (nm, _fmt_num(float(rv), True)))
                    eb = subs.get("equipment_bonus")
                    if eb:
                        effects += _equip_bonus_lines(eb, loc)
                    for sub_key in ("targeted_modifier", "research_bonus_air", "rule"):
                        sb = subs.get(sub_key)
                        if sb:
                            effects += _mod_block(sb, loc)
                    attrs = []
                    cost = scal.get("cost")
                    if cost:
                        attrs.append({"label": "政治点数花费", "value": cost})
                    rcost = scal.get("removal_cost")
                    if rcost and rcost not in ("-1", "0"):
                        attrs.append({"label": "移除花费", "value": rcost})
                    disp = _flatten(_L(loc, name))
                    # 块内 `name = xxx` 指向的键常常没有本地化（游戏自己的疏漏，
                    # 如 `NOR_crush_stalin_ns` 写 `name = NOR_crush_stalin`，
                    # 而中文只登记了前者），这时退回用条目 key 再查一次。
                    if not disp and name != key:
                        disp = _flatten(_L(loc, key))
                    if not disp:
                        disp = IDEA_NAME_FIX.get(key, "")
                    if key in IDEA_SKIP:
                        continue
                    # 「国家理念」里不收录三类条目：
                    # ① 通用顾问槽位（`ADU_democratic_guy` 这种）：脚本里只写 picture 和
                    #    traits，人名是运行时从随机名池生成的，本地化里当然没有键，
                    #    效果也全挂在特质上。这类条目在「军官团与顾问」页有正经版本，
                    #    放进「国家理念」只会显示成一串原始键（顾问分组 984 条里 941 条如此）。
                    # ② 游戏内部的占位条目（如 `SWE_historical_trade_with_Germany_dummy`）：
                    #    只有 allowed 和图片，没名字没说明没效果。
                    # ③ 名字实在查不到、手工表里也没有的：宁可先不显示，也不给用户看裸键。
                    if IDEA_CAT_GROUP.get(cat) == "advisor" and not effects and not disp:
                        continue
                    if (not effects and not disp and not desc
                            and not traits and key.endswith("_dummy")):
                        continue
                    if not effects and not disp and not desc and not traits:
                        continue
                    if not disp:
                        continue
                    items.append({
                        "key": key,
                        "name": disp or key,
                        "desc": desc,
                        "cat": cat_label,
                        "catKey": cat,
                        "effects": effects,
                        "attrs": attrs,
                        "links": [],
                        "traits": [_L(loc, t) or t for t in traits],
                        # 原始特质 key，供 _build 里回填效果用（回填完会删掉，不下发前端）
                        "traitKeys": list(traits),
                    })
    return items, idx


# ---------------------------------------------------------------- 特质

UL_TYPE_LABEL = {
    "all": "陆军通用",
    "field_marshal": "元帅",
    "corps_commander": "军团长",
    "navy": "海军将领",
    "air": "空军将领",
    "land": "陆军将领",
    "operative": "特工",
}
# 将领的六项技能：脚本里是整数，直接显示成 +N
UL_SKILLS = [
    ("attack_skill", "攻击"),
    ("defense_skill", "防御"),
    ("planning_skill", "计划"),
    ("logistics_skill", "后勤"),
    ("maneuvering_skill", "机动"),
    ("coordination_skill", "协调"),
]


UL_SKILL_KEYS = {k for k, _ in UL_SKILLS} | {k + "_factor" for k, _ in UL_SKILLS}

# 特质块里的非效果字段（其余数值键一律当修饰器）
TRAIT_SKIP = {
    "type", "trait_type", "gui_row", "gui_column", "random", "show_in_combat",
    "levels", "cost", "x", "y", "name", "desc", "icon", "sprite", "picture",
    "ai_will_do", "custom_effect_tooltip", "override_effect_tooltip",
    "custom_prerequisite_tooltip", "custom_gain_xp_trigger_tooltip",
    "gain_xp_on_spotting", "mutually_exclusive", "num_parents_needed",
    "ledger", "removal_cost", "available", "visible", "allowed",
    "prerequisites", "gain_xp", "gain_xp_leader", "special_force",
}

# 这几个特质游戏自己没登记中文键（连英文都没有），游戏内也会显示成原始键。
# 数量很少，手工补上，免得「军官团与顾问」里混进一串英文下划线。
TRAIT_NAME_FIX = {
    "ww_one_trauma": "一战创伤",
    "tenacious_defender": "顽强防御者",
    "political_prisoner": "政治犯",
    "assassination_survivor": "暗杀幸存者",
    "union_man": "工会分子",
    "SWE_former_autocrat_upgraded": "前独裁者（转型后）",
    "personal_union_trait": "共主联邦",
    "light_aircraft_manufacturer_3": "轻型飞机制造商（三级）",
    "fast_tank_manufacturer_2": "快速坦克制造商（二级）",
    "infantry_tank_manufacturer_2": "步兵坦克制造商（二级）",
    "CZE_exiled_president": "流亡总统",
    "INS_secret_commander": "秘密指挥官",
}


def _trait_item(key: str, body: str, loc: dict, kind: str):
    typ = _val(body, "type") or "all"
    if kind == "cl":
        cat, cat_key = "国家领袖与顾问", "cl"
    else:
        cat = UL_TYPE_LABEL.get(typ, typ)
        cat_key = "ul_" + typ
    effects = []
    # 特质的效果可以写在 `modifier = {}` 里，也可以像 dictator 那样直接平铺在块内，
    # 所以先取顶层标量，再把白名单之外的数值键都当 modifier。
    scal = _top_scalars(body)
    for k, v in scal:
        if k in TRAIT_SKIP or k in UL_SKILL_KEYS or k.startswith("ai_"):
            continue
        nv = _num(v)
        if nv is None or nv == 0:
            continue
        effects.append(_mod_line(k, v))
    for sub_key in ("modifier", "non_shared_modifier", "corps_commander_modifier",
                    "field_marshal_modifier", "sub_unit_modifiers"):
        sb = _top_sub(body, sub_key)
        if sb:
            effects += _mod_block(sb, loc)
    # 文案型效果：custom_effect_tooltip 直接指向本地化键
    for tip_key in ("custom_effect_tooltip", "override_effect_tooltip"):
        tip = _val(body, tip_key)
        if tip:
            txt = _L(loc, tip)
            if txt:
                effects.append(_flatten(txt))
    attrs = []
    for k, label in UL_SKILLS:
        v = _val(body, k)
        if v and _num(v) and float(v) != 0:
            attrs.append({"label": label, "value": "+" + str(int(float(v)))})
    ttype = _val(body, "trait_type")
    if ttype:
        attrs.insert(0, {"label": "类别", "value": UL_TRAIT_TYPE.get(ttype, ttype)})
    return {
        "key": key,
        "name": _flatten(_L(loc, key)) or TRAIT_NAME_FIX.get(key) or key,
        "desc": _L(loc, key + "_desc"),
        "cat": cat,
        "catKey": cat_key,
        "effects": effects,
        "attrs": attrs,
        "links": [],
        "type": typ,
    }


def _flatten(txt: str) -> str:
    """把多行 tooltip 压成一行，作为单条效果显示。"""
    return re.sub(r"\s+", " ", txt or "").strip()


def _scan_traits(root: str, loc: dict):
    items = []
    for f in sorted(glob.glob(os.path.join(root, D_CL, "*.txt"))):
        txt = _read(f)
        for k, b in _top_blocks(txt):
            if k != "leader_traits":
                continue
            for tk, tb in _top_blocks(b):
                items.append(_trait_item(tk, tb, loc, "cl"))
    for f in sorted(glob.glob(os.path.join(root, D_UL, "*.txt"))):
        txt = _read(f)
        for k, b in _top_blocks(txt):
            if k != "leader_traits":
                continue
            for tk, tb in _top_blocks(b):
                items.append(_trait_item(tk, tb, loc, "ul"))
    return items


# ---------------------------------------------------------------- 顾问

def _scan_advisors(root: str, loc: dict, idea_idx: dict, trait_fx: dict):
    """扫 common/characters 的 advisor 块。

    顾问本人的加成并不写在角色上：`idea_token` 指向 common/ideas 里的一个理念，
    理念再挂 `traits`，真正的数值在 leader_traits 里。三者要串起来才看得到效果。
    """
    items, seen = [], set()
    for f in sorted(glob.glob(os.path.join(root, D_CHAR, "*.txt"))):
        txt = _read(f)
        for k, b in _blocks(txt):
            if k != "characters":
                continue
            for ck, cb in _top_blocks(b):
                ab = _top_sub(cb, "advisor")
                if not ab:
                    continue
                slot = _val(ab, "slot") or "political_advisor"
                token = _val(ab, "idea_token") or ck
                name = _L(loc, _val(cb, "name") or ck)
                if not name:
                    continue
                sig = (slot, name, token)
                if sig in seen:
                    continue
                seen.add(sig)
                idea = idea_idx.get(token) or {}
                traits = []
                tb = _top_sub(ab, "traits")
                if tb:
                    traits += re.findall(r"[A-Za-z0-9_]+", tb)
                for t in idea.get("traits") or []:
                    if t not in traits:
                        traits.append(t)
                effects = []
                for t in traits:
                    fx = trait_fx.get(t)
                    if not fx:
                        continue
                    if fx["desc"]:
                        effects.append("【%s】%s" % (fx["name"], _flatten(fx["desc"])))
                    effects += fx["effects"]
                if idea.get("modifier"):
                    effects += _mod_block(idea["modifier"], loc)
                attrs = [{"label": "槽位", "value": SLOT_LABEL.get(slot, slot)}]
                if idea.get("cost"):
                    attrs.append({"label": "政治点数花费", "value": idea["cost"]})
                items.append({
                    "key": ck,
                    "name": name,
                    "desc": "",
                    "cat": SLOT_LABEL.get(slot, slot),
                    "catKey": "adv_" + slot,
                    "effects": effects,
                    "attrs": attrs,
                    "links": [_L(loc, t) or t for t in traits],
                    "traits": [_L(loc, t) or t for t in traits],
                })
    return items


# ---------------------------------------------------------------- 国策树

def _all_blocks(text: str):
    """单遍扫描取出所有 `key = { ... }`，返回 [(key, body, depth)]。

    国策树单个文件可以有 400+ 节点、上兆字符，用 `_top_blocks` 那种
    「每个块都从头数一遍括号」的做法会退化成 O(n²)，所以这里用栈一次扫完。
    """
    out, stack = [], []
    for m in re.finditer(r"([A-Za-z0-9_\-.]+)\s*=\s*\{|\{|\}", text or ""):
        tok = m.group(0)
        if tok == "}":
            if stack:
                key, st, dep = stack.pop()
                if key:
                    out.append((key, text[st:m.start()], dep))
        elif m.group(1):
            stack.append((m.group(1), m.end(), len(stack)))
        else:
            stack.append((None, m.end(), len(stack)))
    return out


# focus 奖励里能可靠直译的指令
REWARD_PCT = {
    "add_stability": "稳定度",
    "add_war_support": "战争支持度",
    "add_breakthrough_progress": "突破进度",
}
REWARD_ABS = {
    "add_political_power": "政治点数",
    "army_experience": "陆军经验",
    "navy_experience": "海军经验",
    "air_experience": "空军经验",
    "add_command_power": "指挥点数",
    "add_research_slot": "研究槽",
    "add_manpower": "人力",
    "add_fuel": "燃料",
    "add_equipment_to_stockpile": "装备库存",
}
# 这些块只是条件分支/遍历，里面的奖励仍然是真奖励，递归取出来
REWARD_DESCEND = {
    "if", "else_if", "else", "hidden_effect", "random_list",
    "random_owned_controlled_state", "every_owned_state", "every_core_state",
    "random_core_state", "every_controlled_state", "every_state",
    "every_other_country", "random_other_country", "every_country",
    "every_army_leader", "random_army_leader", "every_navy_leader",
    "every_air_leader", "every_character", "random_character",
    "any_owned_state", "random_owned_state", "capital_scope",
}


def _focus_reward(body: str, loc: dict, idea_idx: dict) -> list:
    """把 completion_reward 里的脚本翻成人话。

    HOI4 的国策奖励是纯脚本，游戏内由引擎渲染成 tooltip。这里只翻「玩家真正能看懂」
    的部分：custom_effect_tooltip 文案、理念/研究加成/稳定度/政治点数这类，
    条件分支（if / random_* / every_*）递归展开 —— 条件本身不显示，
    否则一条国策能刷出十几行。
    """
    if not body:
        return []
    blocks, scal = _scan_pairs(body)
    bd = {d: _by_depth(blocks, d) for d in (0, 1, 2, 3, 4)}
    sd = {d: _by_depth(scal, d) for d in (0, 1, 2, 3, 4)}
    bs = {d: [it[-1] for it in v] for d, v in bd.items()}
    ss = {d: [it[-1] for it in v] for d, v in sd.items()}
    return _reward_walk(bd, bs, sd, ss, 0, len(body), 0, loc, idea_idx, 0)


def _reward_walk(bd, bs, sd, ss, lo, hi, base, loc, idea_idx, depth) -> list:
    out = []
    if depth > 4:
        return out
    for k, v, _, _ in _in_range(sd.get(base, []), ss.get(base, []), lo, hi):
        if k == "custom_effect_tooltip":
            txt = _L(loc, v)
            if txt and txt not in ("跳过一行", "-"):
                out.append(_flatten(txt))
        elif k == "effect_tooltip":
            txt = _L(loc, v)
            if txt:
                out.append(_flatten(txt))
        elif k == "add_ideas":
            out.append("获得国家精神：%s" % (_L(loc, v) or v))
        elif k == "remove_ideas":
            out.append("移除国家精神：%s" % (_L(loc, v) or v))
        elif k == "show_ideas_tooltip":
            nm = _L(loc, v) or v
            if out and out[-1].rstrip().endswith(("：", ":")):
                out[-1] = out[-1].rstrip() + nm
            else:
                out.append("解锁：%s" % nm)
        elif k in REWARD_PCT or k in REWARD_ABS:
            nv = _num(v)
            if nv is None or nv == 0:
                continue
            out.append("%s %s" % (REWARD_PCT.get(k) or REWARD_ABS[k],
                                  _fmt_num(nv, k in REWARD_PCT)))
    for k, b, _, s0 in _in_range(bd.get(base, []), bs.get(base, []), lo, hi):
        if k == "add_tech_bonus":
            nm = _L(loc, _val(b, "name") or "") or "研究加成"
            bonus = _num(_val(b, "bonus") or "") or 0
            uses = _val(b, "uses") or "1"
            cats = re.findall(r"\bcategory\s*=\s*([A-Za-z0-9_]+)", b)
            cnames = [_loc_clean(loc.get(c, ""), loc) or c for c in cats]
            out.append("%s +%d%%（%s 次）%s" % (
                nm, int(round(bonus * 100)), uses,
                "：" + "、".join(cnames) if cnames else ""))
        elif k == "add_breakthrough_progress":
            v = _num(_val(b, "value") or "") or 0
            if v:
                out.append("突破进度 %s" % _fmt_num(v, True))
        elif k == "add_doctrine_cost_reduction":
            nm = _L(loc, _val(b, "name") or "") or "学说花费"
            v = _num(_val(b, "cost_reduction") or "") or 0
            uses = _val(b, "uses") or "1"
            out.append("%s -%d%%（%s 次）" % (nm, int(round(v * 100)), uses))
        elif k == "add_timed_idea":
            nm = _L(loc, _val(b, "idea") or "") or _val(b, "idea") or ""
            days = _val(b, "days") or ""
            out.append("获得限时国家精神：%s%s" % (nm, "（%s 天）" % days if days else ""))
        elif k == "swap_ideas":
            rm = _L(loc, _val(b, "remove_idea") or "") or ""
            ad = _L(loc, _val(b, "add_idea") or "") or ""
            if rm and ad:
                out.append("国家精神更替：%s → %s" % (rm, ad))
        elif k == "add_dynamic_modifier":
            nm = _L(loc, _val(b, "modifier") or "") or _val(b, "modifier") or ""
            if nm:
                out.append("获得动态修正：%s" % nm)
        elif k == "add_ideas":
            nm = _L(loc, _val(b, "idea") or "") or ""
            if nm:
                out.append("获得国家精神：%s" % nm)
        elif k in REWARD_DESCEND:
            out += _reward_walk(bd, bs, sd, ss, s0, s0 + len(b), base + 1,
                                loc, idea_idx, depth + 1)
    return out


# 需要手工补名的国策树（脚本里没给可用信息，不是解析漏了）：
#   horn_of_africa_tree           —— country 块只写 original_tag = AFA/ORO/QEM…，
#                                    这批 DLC tag 的简体中文本地化根本没有键
#   china_communist_warlord_focus —— country 块是空的（只有 factor = 0）
TREE_NAME_FIX = {
    "horn_of_africa_tree": "埃塞俄比亚之角",
    "china_communist_warlord_focus": "中国共产军阀",
}

# 筛选项里脚本自己写错的两个键（`INDUSTRIAL` 是单数笔误、`MISSIOLINI` 少了 ITA_ 前缀），
# 本地化里当然查不到，手工补。
FILTER_FIX = {
    "FOCUS_FILTER_INDUSTRIAL": "工业",
    "FOCUS_FILTER_MISSIOLINI": "墨索里尼的任务",
    "FOCUS_FILTER_BALANCE_OF_POWERS": "权力平衡",
}

# country 块里出现的大写三字母词，如果只是条件关键字就别当国家 tag
TAG_STOP = {"AND", "OR", "NOT", "DLC", "HAS", "ANY", "ALL", "YES", "NO", "THE"}


def _focus_tree_name(tags, is_default, loc, tid=""):
    """国策树标题：默认树叫「通用国策树」，其余按国家 tag 名拼。

    tag 多于 4 个的（可释放国家树一次列十几个）只显示前 3 个，否则下拉框被撑爆。
    """
    if is_default:
        return "通用国策树"
    names = []
    for t in tags:
        nm = _L(loc, t)
        if nm and nm not in names:
            names.append(nm)
    if not names:
        # 引擎脚本本身没给国家信息：`horn_of_africa` 的 tag 在中文里没本地化，
        # `china_communist_warlord_focus` 的 country 块干脆是空的。
        fixed = TREE_NAME_FIX.get(tid)
        if fixed:
            return fixed
        return "（未标注国家）"
    if len(names) > 4:
        return "%s 等 %d 国" % (" / ".join(names[:3]), len(names))
    return " / ".join(names)


def _scan_focus(root: str, loc: dict, idea_idx: dict):
    """扫 common/national_focus，返回每棵树：{id,name,tags,focuses:[...]}。

    坐标是相对网格：`relative_position_id` 指向另一个 focus，绝对坐标 = 父坐标 + (x,y)。
    游戏里一格横向约 96px、纵向约 130px，前端按这个比例画。

    整个目录上万个节点，所以**每个文件只解析一次**：一次拿到所有层级块与标量，
    后面按「起点区间 + 层级」二分归属，避免逐节点重复扫描正文。
    """
    trees = []
    for f in sorted(glob.glob(os.path.join(root, D_NF, "*.txt"))):
        txt = _read(f)
        if "focus_tree" not in txt:
            continue
        all_blocks, all_scal = _scan_pairs(txt)
        bd = {d: _by_depth(all_blocks, d) for d in (0, 1, 2)}
        sd = {d: _by_depth(all_scal, d) for d in (1, 2)}
        bs = {d: [it[-1] for it in v] for d, v in bd.items()}
        ss = {d: [it[-1] for it in v] for d, v in sd.items()}

        for _, body, _, st in bd[0]:
            en = st + len(body)
            kids = _in_range(bd[1], bs[1], st, en)
            tscal = {k: v for k, v, _, _ in _in_range(sd[1], ss[1], st, en)}
            tid = tscal.get("id")
            if not tid:
                continue
            cb = next((b for kk, b, _, _ in kids if kk == "country"), "")
            tags = re.findall(r"\btag\s*=\s*([A-Z]{3})\b", cb)
            tags += [t for t in re.findall(r"\boriginal_tag\s*=\s*([A-Z]{3})\b", cb)
                     if t not in tags]
            if not tags:
                # 有些树不写 tag，只判国家专属 flag（如 `NOR_AAT = yes`），
                # 就把 flag 的三字母前缀当 tag 用。
                for t in re.findall(r"\b([A-Z]{3})_[A-Za-z0-9_]*\s*=", cb):
                    if t not in tags and t not in TAG_STOP:
                        tags.append(t)
            is_default = (tscal.get("default") or "").lower() == "yes"

            nodes, raw = [], {}
            for kk, nb, _, fs in kids:
                if kk != "focus":
                    continue
                fen = fs + len(nb)
                fscal = {k: v for k, v, _, _ in _in_range(sd[2], ss[2], fs, fen)}
                fsubs = {}
                for sk, sb, _, _ in _in_range(bd[2], bs[2], fs, fen):
                    fsubs.setdefault(sk, sb)
                fid = fscal.get("id")
                if not fid:
                    continue
                pre = fsubs.get("prerequisite") or ""
                mutex = fsubs.get("mutually_exclusive") or ""
                node = {
                    "id": fid,
                    "x": int(_num(fscal.get("x")) or 0),
                    "y": int(_num(fscal.get("y")) or 0),
                    "rel": fscal.get("relative_position_id") or "",
                    "icon": fscal.get("icon") or "",
                    "cost": fscal.get("cost") or "",
                    "pre": re.findall(r"\bfocus\s*=\s*([A-Za-z0-9_]+)", pre),
                    "mutex": re.findall(r"\bfocus\s*=\s*([A-Za-z0-9_]+)", mutex),
                    "filters": [_L(loc, k) or FILTER_FIX.get(k, "")
                                for k in re.findall(r"\bFOCUS_FILTER_[A-Z0-9_]+",
                                                    fsubs.get("search_filters") or "")],
                    "bypass": bool(fsubs.get("bypass")),
                }
                raw[fid] = node
                reward = fsubs.get("completion_reward")
                node["fx"] = _focus_reward(reward, loc, idea_idx) if reward else []
                node["nm"] = _flatten(_L(loc, fid)) or fid
                node["ds"] = _L(loc, fid + "_desc")
                nodes.append(node)
            if not nodes:
                continue
            # 相对坐标展开：`relative_position_id` 指向父节点，(x,y) 是相对偏移。
            # 父节点可能排在自己后面，也可能连成好几层，所以要反复解到稳定。
            # 关键：**没有 rel 的节点本身就是绝对坐标**，必须先标记为已解析，
            # 否则它的子节点会一直等不到父节点就绪，坐标停在相对值上（整棵树挤成一条线）。
            for n in nodes:
                if not n["rel"]:
                    n["_abs"] = True
            for _ in range(40):
                changed = False
                for n in nodes:
                    if n.get("_abs"):
                        continue
                    p = raw.get(n["rel"])
                    if p is None:
                        n["_abs"] = True        # 指向别的树/不存在的节点，按绝对坐标处理
                        changed = True
                        continue
                    if p.get("_abs"):
                        n["x"] += p["x"]
                        n["y"] += p["y"]
                        n["_abs"] = True
                        changed = True
                if not changed:
                    break
            for n in nodes:
                n.pop("_abs", None)
                n.pop("rel", None)
            xs = [n["x"] for n in nodes]
            ys = [n["y"] for n in nodes]
            trees.append({
                "id": tid,
                "name": _focus_tree_name(tags, is_default, loc, tid),
                "tags": tags,
                "default": is_default,
                "x0": min(xs), "y0": min(ys), "x1": max(xs), "y1": max(ys),
                "n": len(nodes),
                "focuses": nodes,
            })
    return trees


# ---------------------------------------------------------------- 入口

def _sig(dirs) -> str:
    parts = []
    for p in dirs:
        try:
            st = os.stat(p)
            parts.append("%s:%d:%d" % (os.path.basename(p.rstrip("\\/")),
                                       int(st.st_mtime), int(st.st_size)))
        except Exception:
            parts.append("-")
    return "|".join(parts)


def _cache_path(loc_lang: str) -> str:
    return os.path.join(APP_DIR, "wiki_cache_hoi4_%s.json" % loc_lang)


def game_root_of(hint: str = "") -> str:
    """定位钢铁雄心安装目录。HOI4 的脚本直接在根目录的 common 下（无 game/ 下沉）。"""
    hint = (hint or "").strip().rstrip("\\/")
    for cand in (hint, os.path.join(hint, "Hearts of Iron IV") if hint else ""):
        if cand and os.path.isdir(os.path.join(cand, D_IDEAS)):
            return cand
    for drive in ("C", "D", "E", "F", "G"):
        for sub in ("Steam", "SteamLibrary", "Games", "Program Files (x86)\\Steam"):
            c = "%s://%s//steamapps//common//Hearts of Iron IV" % (drive, sub)
            if os.path.isdir(os.path.join(c, D_IDEAS)):
                return c
    return ""


def _game_version(root: str) -> str:
    try:
        with open(os.path.join(root, "launcher-settings.json"), encoding="utf-8-sig") as f:
            return (json.load(f) or {}).get("rawVersion") or ""
    except Exception:
        return ""


def _build(root: str, loc_lang: str) -> dict:
    loc = _load_loc(os.path.join(root, D_LOC, loc_lang))
    _LOC_CUR.clear()
    _LOC_CUR.update(loc)
    _build_mod_index(loc)

    ideas, idea_idx = _scan_ideas(root, loc)
    traits = _scan_traits(root, loc)
    trait_fx = {t["key"]: t for t in traits}
    advisors = _scan_advisors(root, loc, idea_idx, trait_fx)
    trees = _scan_focus(root, loc, idea_idx)

    # 有些顾问型理念（如白俄罗斯那批 `BLR_boris_berman`）自己没有 modifier，
    # 效果全写在 `traits = { prince_of_terror }` 上，只按理念本体取会显示成「无效果」。
    # 这里沿着 traits → 特质效果再展开一次，等 trait_fx 建好之后补。
    for it in ideas:
        if it["effects"]:
            continue
        for t in it.pop("traitKeys", []):
            fx = trait_fx.get(t)
            if not fx:
                continue
            if fx["desc"]:
                it["effects"].append("【%s】%s" % (fx["name"], _flatten(fx["desc"])))
            it["effects"] += fx["effects"]
    for it in ideas:
        it.pop("traitKeys", None)

    # 「军官团与顾问」= 顾问（按槽位）+ 特质（将领/领袖）
    officers = advisors + traits
    idea_order = [k for k, _, _ in IDEA_CATS]
    off_order = ["adv_political_advisor", "adv_high_command", "adv_army_chief",
                 "adv_navy_chief", "adv_air_chief", "adv_theorist"]
    off_order += ["ul_corps_commander", "ul_field_marshal", "ul_all", "ul_land",
                  "ul_navy", "ul_air", "ul_operative", "cl"]
    # 只保留实际出现的分类，避免下拉里挂空选项
    have_i = {x["catKey"] for x in ideas}
    have_o = {x["catKey"] for x in officers}
    idea_order = [k for k in idea_order if k in have_i] + sorted(have_i - set(idea_order))
    off_order = [k for k in off_order if k in have_o] + sorted(have_o - set(off_order))

    ntrees = len(trees)
    nfocus = sum(t["n"] for t in trees)
    return {
        "ok": True,
        "supported": True,
        "game": "hoi4",
        "gameVersion": _game_version(root),
        "tabs": [
            {"id": "ideas", "icon": "💡", "label": "国家理念",
             "count": len(ideas), "layout": "generic"},
            {"id": "officers", "icon": "🎖", "label": "军官团与顾问",
             "count": len(officers), "layout": "generic"},
            {"id": "focus", "icon": "🌳", "label": "国策树",
             "count": ntrees, "layout": "focus"},
        ],
        "ideas": ideas,
        "officers": officers,
        "trees": trees,
        "counts": {"ideas": len(ideas), "officers": len(officers),
                   "focus": ntrees, "focusNodes": nfocus},
        "catOrders": {"ideas": idea_order, "officers": off_order},
    }


_FOCUS = {}      # 树 id → 完整树（含 focuses），供 /api/wiki/focus 单棵取用


def focus_tree(tid: str):
    """取单棵国策树的完整节点数据（前端切树时按需拉，避免首屏 5MB）。"""
    return _FOCUS.get((tid or "").strip())


def load(game_root: str = "", ui_lang: str = "zh", force: bool = False) -> dict:
    """读取（并缓存）钢铁雄心百科。"""
    ui_lang = (ui_lang or "zh").lower()
    loc_lang = LANG_MAP.get(ui_lang, "simp_chinese")

    root = game_root_of(game_root)
    if not root:
        return {"ok": False, "supported": True, "game": "hoi4",
                "error": "没找到《钢铁雄心 IV》安装目录，请在设置里把游戏根目录指向 Hearts of Iron IV 文件夹"}

    dirs = [os.path.join(root, d) for d in (D_IDEAS, D_CHAR, D_NF, D_CL, D_UL)]
    dirs.append(os.path.join(root, D_LOC, loc_lang))
    sig = "%s|%s" % (loc_lang, _sig(dirs))
    cpath = _cache_path(loc_lang)

    with _LOCK:
        hit = _MEM.get(sig)
        if hit and not force:
            _fill_focus(hit)
            d = dict(hit)
            d["tabs"] = [dict(t) for t in hit.get("tabs") or []]
            d.pop("trees", None)
            d["cached"] = True
            return d

        if not force:
            try:
                with open(cpath, encoding="utf-8") as f:
                    blob = json.load(f)
                if blob.get("ver") == H4_VER and blob.get("sig") == sig \
                        and (blob.get("root") or "").lower() == root.lower():
                    data = blob.get("data") or {}
                    _MEM[sig] = data
                    _fill_focus(data)
                    d = dict(data)
                    d["tabs"] = [dict(t) for t in data.get("tabs") or []]
                    d.pop("trees", None)
                    d["cached"] = True
                    return d
            except Exception:
                pass

        t0 = time.time()
        data = _build(root, loc_lang)
        data["buildSeconds"] = round(time.time() - t0, 1)
        _MEM.clear()
        _MEM[sig] = data
        _fill_focus(data)
        try:
            # focusTrees 是从 trees 派生的瘦身视图，不入缓存，免得同一份数据存两遍
            slim_data = {k: v for k, v in data.items() if k != "focusTrees"}
            with open(cpath, "w", encoding="utf-8") as f:
                json.dump({"ver": H4_VER, "sig": sig, "root": root, "data": slim_data},
                          f, ensure_ascii=False)
        except Exception:
            pass
        d = dict(data)
        d["tabs"] = [dict(t) for t in data.get("tabs") or []]
        d.pop("trees", None)
        return d


def _fill_focus(data: dict):
    """把完整的国策树灌进内存索引，同时给返回体做瘦身。"""
    _FOCUS.clear()
    for t in data.get("trees") or []:
        _FOCUS[t.get("id")] = t
    slim = []
    for t in data.get("trees") or []:
        slim.append({k: v for k, v in t.items() if k != "focuses"})
    data["focusTrees"] = slim


# ---------------------------------------------------------------- 国策图标

# 国策图标是 DDS 纹理（<HOI4>/gfx/interface/goals/*.dds），浏览器显示不了，
# 所以按需转成 PNG 缓存到启动器目录（沿用成就图标那套做法）。
_GFX_MAP = {}
_GFX_LOCK = threading.Lock()
_ICON_LOCK = threading.Lock()
_ICON_DIR = None


def _gfx_index(root: str) -> dict:
    """解析 interface/*.gfx，建立 `GFX_名字 → dds 相对路径` 索引。"""
    global _GFX_MAP
    if _GFX_MAP:
        return _GFX_MAP
    with _GFX_LOCK:
        if _GFX_MAP:
            return _GFX_MAP
        m = {}
        # 注意大小写：goals.gfx 里 3000 多条全是 `SpriteType`（大写 S），
        # 只写小写会整文件漏掉，图标的 texturefile 就全部查不到。
        for f in glob.glob(os.path.join(root, "interface", "*.gfx")):
            try:
                with open(f, encoding="utf-8-sig", errors="ignore") as fh:
                    txt = fh.read()
            except Exception:
                continue
            for mm in re.finditer(r"spriteType\s*=\s*\{(.*?)\}", txt, re.S | re.I):
                body = mm.group(1)
                n = re.search(r'name\s*=\s*"([^"]+)"', body, re.I)
                t = re.search(r'texturefile\s*=\s*"([^"]+)"', body, re.I)
                if n and t:
                    m.setdefault(n.group(1), t.group(1))
        _GFX_MAP = m
    return _GFX_MAP


def _icon_dir() -> str:
    global _ICON_DIR
    if _ICON_DIR:
        return _ICON_DIR
    d = os.path.join(APP_DIR, "hoi4_focus_icons")
    try:
        os.makedirs(d, exist_ok=True)
    except Exception:
        pass
    _ICON_DIR = d
    return d


_DDS_DIR_LOCK = threading.Lock()
_DDS_DIR_CACHE = {}


def _dir_dds(d: str) -> list:
    """列一次目录里的 dds 文件名并缓存（缩略图请求很密集，别反复 listdir）。"""
    key = (d or "").lower()
    with _DDS_DIR_LOCK:
        got = _DDS_DIR_CACHE.get(key)
    if got is not None:
        return got
    try:
        files = [f for f in os.listdir(d) if f.lower().endswith(".dds")]
    except Exception:
        files = []
    with _DDS_DIR_LOCK:
        _DDS_DIR_CACHE[key] = files
    return files


def _resolve_dds(root: str, rel: str) -> str:
    """把 texturefile 指向的相对路径解析成真实文件，找不到就纠错。

    游戏本体自己就有一批拼写错误——`interface/goals.gfx` 里
    `GFX_goal_generic_build_navy` 写的是 `goal_generic_build_nay.dds`（少个 v），
    这类图在游戏里照常显示（引擎按 sprite 名兜底），我们不修就永远是空白格。
    所以先按忽略大小写找，再在同目录做相似名匹配。
    """
    if not rel:
        return ""
    cand = os.path.join(root, rel.replace("/", os.sep))
    if os.path.isfile(cand):
        return cand
    d = os.path.dirname(cand)
    base = os.path.basename(cand)
    files = _dir_dds(d)
    if not files:
        return ""
    low = base.lower()
    for f in files:
        if f.lower() == low:
            return os.path.join(d, f)
    try:
        import difflib
        hit = difflib.get_close_matches(base, files, n=1, cutoff=0.86)
    except Exception:
        hit = []
    if hit:
        return os.path.join(d, hit[0])
    return ""


def _dds_raw_image(src: str):
    """Pillow 打不开的 DDS 自己解（未压缩 32bpp）。

    有一部分国策图标用的是 DX10 扩展头 + `R8G8B8A8_UINT` 这类 DXGI 编号，
    Pillow 的 DdsImagePlugin 直接抛 `Unimplemented DXGI format 91`。
    这类文件其实就是未压缩的每像素 4 字节，读头拿到宽高后自己拼 RGBA 即可。
    """
    import struct
    from PIL import Image
    try:
        with open(src, "rb") as f:
            b = f.read()
    except Exception:
        return None
    if len(b) < 128 or b[:4] != b"DDS ":
        return None
    height = struct.unpack_from("<I", b, 12)[0]
    width = struct.unpack_from("<I", b, 16)[0]
    fourcc = b[84:88]
    bits = struct.unpack_from("<I", b, 88)[0]
    rmask = struct.unpack_from("<I", b, 92)[0]
    head = 128
    dxgi = -1
    if fourcc == b"DX10":
        if len(b) < 148:
            return None
        dxgi = struct.unpack_from("<I", b, 128)[0]
        head = 148
    if bits == 0 and dxgi >= 0:
        bgra = dxgi in (87, 88)      # B8G8R8A8 / B8G8R8X8 字节序是 BGRA
        bits = 32
    else:
        bgra = rmask == 0x00FF0000
    if bits != 32 or width <= 0 or height <= 0 or width > 4096 or height > 4096:
        return None
    need = head + width * height * 4
    if len(b) < need:
        return None
    im = Image.frombytes("RGBA", (width, height), b[head:need])
    if bgra:
        r, g, bl, a = im.split()
        im = Image.merge("RGBA", (bl, g, r, a))
    return im


def focus_icon_png(game_root: str, gfx_name: str) -> str:
    """返回国策图标的本地 PNG 路径（必要时从 DDS 转一次），失败返回空串。"""
    gfx_name = (gfx_name or "").strip()
    if not gfx_name:
        return ""
    root = game_root_of(game_root)
    if not root:
        return ""
    safe = re.sub(r"[^0-9A-Za-z_]", "_", gfx_name)
    out = os.path.join(_icon_dir(), safe + ".png")
    if os.path.isfile(out) and os.path.getsize(out) > 0:
        return out

    rel = _gfx_index(root).get(gfx_name) or ""
    src = _resolve_dds(root, rel) if rel else ""
    if not src:
        # 索引里没有就按命名规律猜一次（GFX_goal_x → goal_x.dds），同样允许纠错
        guess = gfx_name[4:] if gfx_name.startswith("GFX_") else gfx_name
        src = _resolve_dds(root, "gfx/interface/goals/" + guess + ".dds")
    if not src:
        return ""

    # 不串行加锁：首屏一次要几十个图标，Pillow 解码时释放 GIL，
    # 多线程并行才有意义。写文件走临时名 + 原子替换，避免读到半成品。
    if os.path.isfile(out) and os.path.getsize(out) > 0:
        return out
    try:
        from PIL import Image          # 延迟导入：没装也不影响其它功能
        try:
            im = Image.open(src).convert("RGBA")
        except NotImplementedError:
            im = _dds_raw_image(src)   # 未压缩 32bpp 的 DX10 变体，自己解
            if im is None:
                return ""
        w, h = im.size
        # 多帧精灵（横向排列）只取第一帧
        if h and w > h * 1.4:
            im = im.crop((0, 0, h, h))
        if max(im.size) > 96:
            im.thumbnail((96, 96), Image.LANCZOS)
        tmp = "%s.%d.tmp" % (out, threading.get_ident())
        im.save(tmp, "PNG", optimize=True)
        os.replace(tmp, out)
        return out
    except Exception:
        return ""


def focus_icon_url(gfx_name: str) -> str:
    """前端 <img src> 用：按需转换并返回 PNG。"""
    if not gfx_name:
        return ""
    from urllib.parse import quote
    return "/api/wiki/focus-icon?n=%s" % quote(gfx_name)

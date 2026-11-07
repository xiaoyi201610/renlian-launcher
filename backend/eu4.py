# -*- coding: utf-8 -*-
"""欧陆风云 IV 百科：思想组 / 陆军 / 陆军单位 / 海军单位 / 海军 / 时代 / 任务树。

只读游戏本体脚本与本地化，离线解析；任务树复用前端钢铁雄心的国策树画布。
"""

import glob
import json
import os
import re
import threading
import time

try:
    from paths import APP_DIR
except Exception:
    APP_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

EU4_VER = 6
_LOCK = threading.RLock()
_MEM = {}
_TREES = {}
_SLIM = []
_GFX = {}

LANG_MAP = {
    "zh": "simp_chinese",
    "en": "english",
    "fr": "french",
    "de": "german",
    "sv": "english",
    "es": "spanish",
    "ru": "russian",
    "pl": "polish",
}

TABS = [
    {"id": "ideas", "icon": "💡", "layout": "generic"},
    {"id": "army", "icon": "⚔", "layout": "generic"},
    {"id": "landunits", "icon": "🪖", "layout": "generic"},
    {"id": "navalunits", "icon": "⛵", "layout": "generic"},
    {"id": "navy", "icon": "⚓", "layout": "generic"},
    {"id": "ages", "icon": "🕰", "layout": "generic"},
    {"id": "missions", "icon": "🗺", "layout": "focus"},
]

LAND_TYPES = ("infantry", "cavalry", "artillery")
NAVAL_TYPES = ("heavy_ship", "light_ship", "galley", "transport", "frigate")

TYPE_CN = {
    "infantry": "步兵", "cavalry": "骑兵", "artillery": "炮兵",
    "heavy_ship": "重型船", "light_ship": "轻型船", "galley": "桨帆船",
    "transport": "运输船", "frigate": "护卫舰",
}
TYPE_EN = {
    "infantry": "Infantry", "cavalry": "Cavalry", "artillery": "Artillery",
    "heavy_ship": "Heavy Ship", "light_ship": "Light Ship", "galley": "Galley",
    "transport": "Transport", "frigate": "Frigate",
}

GROUP_CN = {
    "western": "西方", "eastern": "东方", "ottoman": "奥斯曼", "muslim": "穆斯林",
    "indian": "印度", "chinese": "中华", "nomad": "游牧", "african": "非洲",
    "american": "美洲", "aboriginal": "原住民", "andean": "安第斯",
    "sub_saharan": "撒哈拉以南",
    "mesoamerican": "中美", "north_american": "北美", "south_american": "南美",
    "high_american": "中美高地", "central_american": "中美",
}

CAT_CN = {
    "ADM": "行政", "DIP": "外交", "MIL": "军事",
    "national": "国家/文化", "mechanics": "陆战机制",
    "doctrine": "海军学说", "naval_mechanics": "海军机制",
}

MOD_CN = {
    "global_manpower_modifier": "全球人力",
    "land_morale": "陆军士气",
    "naval_morale": "海军士气",
    "discipline": "纪律",
    "infantry_power": "步兵战斗力",
    "cavalry_power": "骑兵战斗力",
    "artillery_power": "炮兵战斗力",
    "shock_damage": "冲击伤害",
    "fire_damage": "火力伤害",
    "siege_ability": "围城能力",
    "defensiveness": "防御力",
    "global_tax_modifier": "税收",
    "production_efficiency": "生产效率",
    "global_trade_power": "贸易力量",
    "trade_steering": "贸易引导",
    "trade_efficiency": "贸易效率",
    "merchants": "商人",
    "colonists": "殖民者",
    "missionaries": "传教士",
    "diplomats": "外交官",
    "possible_policy": "可用政策",
    "diplomatic_reputation": "外交声望",
    "improve_relation_modifier": "改善关系",
    "ae_impact": "侵略扩张",
    "stability_cost_modifier": "稳定度花费",
    "development_cost": "发展度花费",
    "technology_cost": "科技花费",
    "military_tech_cost_modifier": "军事科技花费",
    "diplomatic_tech_cost_modifier": "外交科技花费",
    "administrative_tech_cost_modifier": "行政科技花费",
    "advisor_cost": "顾问花费",
    "merc_maintenance_modifier": "雇佣兵维护",
    "land_maintenance_modifier": "陆军维护",
    "naval_maintenance_modifier": "海军维护",
    "manpower_recovery_speed": "人力恢复",
    "garrison_size": "驻军规模",
    "global_unrest": "全国叛乱",
    "unrest": "叛乱",
    "tolerance_own": "国教容忍",
    "tolerance_heretic": "异端容忍",
    "tolerance_heathen": "异教容忍",
    "legitimacy": "正统性",
    "republican_tradition": "共和传统",
    "devotion": "虔诚",
    "meritocracy": "贤能值",
    "horde_unity": "部落统一",
    "yearly_absolutism": "年度专制",
    "yearly_corruption": "年度腐败",
    "corruption": "腐败",
    "inflation": "通胀",
    "inflation_reduction": "通胀降低",
    "war_exhaustion": "战争疲惫",
    "army_tradition": "陆军传统",
    "navy_tradition": "海军传统",
    "army_tradition_decay": "陆军传统衰减",
    "navy_tradition_decay": "海军传统衰减",
    "prestige": "威望",
    "power_projection": "实力投射",
    "global_autonomy": "自治度",
    "local_autonomy": "本地自治度",
    "stability": "稳定度",
    "core_creation": "造核花费",
    "hostile_attrition": "敌方损耗",
    "attrition": "损耗",
    "land_forcelimit": "陆军上限",
    "naval_forcelimit": "海军上限",
    "land_forcelimit_modifier": "陆军上限",
    "naval_forcelimit_modifier": "海军上限",
    "max_manpower": "最大人力",
    "manpower": "人力",
    "global_manpower": "人力",
    "sailors": "水手",
    "global_sailors": "水手",
    "ship_cost": "船只花费",
    "leader_land_shock": "陆军将领冲击",
    "leader_land_fire": "陆军将领火力",
    "leader_land_maneuver": "陆军将领机动",
    "leader_siege": "将领围城",
    "leader_naval_shock": "海军将领冲击",
    "leader_naval_fire": "海军将领火力",
    "leader_naval_maneuver": "海军将领机动",
    "land_attrition": "陆军损耗",
    "naval_attrition": "海军损耗",
    "reinforce_speed": "补充速度",
    "movement_speed": "移动速度",
    "administrative_efficiency": "行政效率",
    "governing_capacity": "治理容量",
    "governing_capacity_modifier": "治理容量",
    "state_maintenance_modifier": "州维护",
    "build_cost": "建筑花费",
    "build_time": "建筑时间",
    "improve_development": "发展度",
    "free_leader_slots": "免费将领槽位",
    "num_of_pronoiars": "普罗尼亚",
    "num_of_age_rewards": "时代奖励",
    "may_explore": "可探索未知海域",
    "may_perform_slave_raid_on_same_religion": "可袭击同宗教国家",
    "own_coast_naval_combat_bonus": "近海战斗加成",
    "blockade_efficiency": "封锁效率",
    "privateer_efficiency": "私掠效率",
    "trade_ship_power": "贸易船力量",
    "global_ship_cost": "船只花费",
    "heavy_ship_power": "重型船战斗力",
    "light_ship_power": "轻型船战斗力",
    "galley_power": "桨帆船战斗力",
    "cav_to_inf_ratio": "骑兵比例",
    "cavalry_cost": "骑兵花费",
    "general_cost": "将领花费",
    "fabricate_claims_cost": "制造宣称花费",
    "unjustified_demands": "不正当要求",
    "global_prov_trade_power_modifier": "省份贸易力量",
    "mercenary_cost": "雇佣兵花费",
    "global_regiment_cost": "团花费",
    "monthly_reform_progress": "月度改革进度",
    "global_trade_goods_size_modifier": "贸易品规模",
    "caravan_power": "商队力量",
    "religious_unity": "宗教统一",
    "num_of_accepted_cultures": "可接受文化数",
    "num_of_generals": "将领数", "num_of_admirals": "海军将领数",
    "num_of_colonists": "殖民者数", "num_of_explorers": "探险家数",
    "num_of_ports": "港口数", "num_of_merchants": "商人团数",
    "num_of_diplomats": "外交官数",
    "reform_level": "改革等级", "crown_land_share": "王室领地份额",
    "legitimacy_equivalent": "合法性等价", "manpower_percentage": "人力百分比",
    "share_of_starting_income": "起始收入占比",
    "monthly_mil": "每月军事点数", "monthly_adm": "每月行政点数",
    "monthly_dip": "每月外交点数", "treasury": "国库",
    "monthly_income": "月收入", "owns_core_province": "拥有核心省份",
    "owns_or_non_sovereign_subject_of": "拥有或为其非主权附属",
    "owns": "拥有省份", "trader": "商人", "temple": "神庙",
}

FLAT_MODS = {
    "yearly_absolutism", "yearly_devotion", "yearly_legitimacy",
    "yearly_republican_tradition", "yearly_corruption",
    "yearly_army_professionalism", "army_tradition_decay",
    "navy_tradition_decay",
    "war_exhaustion", "inflation", "monthly_war_exhaustion",
    "num_of_age_rewards", "num_of_pronoiars",
}

SPECIAL_YES = {
    "may_explore": ("可探索未知海域", "Cannot explore"),
    "may_perform_slave_raid_on_same_religion": ("可袭击同宗教国家", "Slave raids"),
}

ARMY_GUIDE = [
    ("army_frontline", "战斗宽度与战线", "Combat width & front line",
     "前排由步兵与骑兵接敌，后排炮兵输出；战斗宽度随军事科技提升，超宽部队无法参战。",
     "Infantry and cavalry hold the front while artillery fires from the back. Combat width grows with military technology; excess regiments cannot engage.",
     ["前排：步兵/骑兵", "后排：炮兵", "宽度由军事科技决定"], "Front: infantry/cavalry; back: artillery; width grows with military tech"),
    ("army_phases", "火力与冲击阶段", "Fire & shock phases",
     "每场陆战分火力、冲击两个阶段。火力阶段看火力点数与炮兵，冲击阶段看冲击点数与骑兵。",
     "Land battles alternate fire and shock phases. Fire favors fire pips and artillery; shock favors shock pips and cavalry.",
     ["火力阶段：火力点数、炮兵", "冲击阶段：冲击点数、骑兵"], "Fire phase: fire pips and artillery; shock phase: shock pips and cavalry"),
    ("army_tactics", "战术与骰子", "Tactics & dice",
     "双方将领比较机动，决定战术与骰子修正；战术等级降低受到的伤害，地形与渡河再叠加修正。",
     "Leader maneuver decides tactics and dice modifiers. Higher tactics reduce damage taken; terrain and river crossings add further modifiers.",
     ["将领机动影响战术", "战术降低伤亡", "地形/渡河影响骰子"], "Maneuver affects tactics; tactics reduce casualties; terrain and rivers modify rolls"),
    ("army_terrain", "地形与渡河", "Terrain & river crossings",
     "山地、森林、沼泽、丘陵提供防御加成；渡河、登陆、跨越海峡会带来进攻惩罚。",
     "Mountains, forests, marshes and hills favor the defender; river crossing, naval landing and strait crossing penalize the attacker.",
     ["防御地形：山地/森林/沼泽/丘陵", "进攻惩罚：渡河/登陆"], "Defensive terrain: mountains/forests/marshes/hills; attacker penalty: rivers/landings"),
    ("army_composition", "步骑炮编成", "Army composition",
     "步兵填满前排，骑兵保护两翼，炮兵放后排。骑兵比例受科技与政府限制，超出部分不参与战斗。",
     "Infantry fills the front, cavalry protects flanks, artillery occupies the back. Cavalry ratio is limited by tech and government; excess cavalry does not fight.",
     ["步兵：前排主力", "骑兵：侧翼与冲击", "炮兵：后排火力"], "Infantry: front line; cavalry: flanks and shock; artillery: back-row fire"),
    ("army_attrition", "损耗与补给", "Attrition & supply",
     "部队在补给不足、敌军领地、恶劣地形或围城时产生损耗；补给上限随发展度与建筑提高。",
     "Attrition rises from low supply, enemy territory, harsh terrain and sieges. Supply limit improves with development and buildings.",
     ["损耗来源：缺补给/敌境/恶劣地形/围城", "提高补给：发展度与建筑"], "Attrition: low supply, enemy land, harsh terrain, sieges; improve supply with development and buildings"),
    ("army_morale", "士气与纪律", "Morale & discipline",
     "士气决定部队能撑多久，纪律直接增减造成的伤害；士气耗尽部队退出战斗并撤退。",
     "Morale determines how long regiments stay in battle; discipline directly modifies damage dealt and taken. A regiment at zero morale retreats.",
     ["士气：战斗持续时间", "纪律：伤害增减", "士气归零则撤退"], "Morale: battle endurance; discipline: damage modifier; zero morale retreats"),
    ("army_tech", "军事科技与兵种", "Military tech & units",
     "提升军事科技解锁新兵种、提高战斗宽度与战术；新兵种通常拥有更高的火力/冲击点数。",
     "Military tech unlocks new unit types, raises combat width and tactics. Newer units usually carry higher fire/shock pips.",
     ["军事科技解锁兵种", "科技提升宽度与战术"], "Military tech unlocks units and raises width and tactics"),
]

NAVY_GUIDE = [
    ("navy_ship_roles", "舰种定位", "Ship roles",
     "重型船负责主力海战，轻型船负责贸易与私掠，桨帆船在近海有加成，运输船用于运兵与登陆。",
     "Heavy ships win fleet battles, light ships protect trade and privateer, galleys excel in inland seas, transports move and land armies.",
     ["重型船：主力海战", "轻型船：贸易/私掠", "桨帆船：近海", "运输船：运兵"], "Heavy: battle; light: trade/privateer; galley: inland seas; transport: armies"),
    ("navy_phases", "海战流程", "Naval combat",
     "海战同样分火力与冲击阶段；船体、火炮、海军士气与将领点数共同决定伤害与沉船。",
     "Naval battles alternate fire and shock. Hull, cannons, naval morale and leader pips decide damage and losses.",
     ["火力/冲击阶段", "船体与火炮决定伤害"], "Fire/shock phases; hull and cannons decide damage"),
    ("navy_tradition", "海军传统与将领", "Naval tradition & leaders",
     "海军传统提高海军士气与战斗表现；海军将领的火力、冲击、机动决定舰队上限。",
     "Naval tradition improves morale and performance; admiral fire, shock and maneuver set the fleet's ceiling.",
     ["海军传统：士气与战斗", "将领：火力/冲击/机动"], "Tradition: morale and combat; admiral: fire/shock/maneuver"),
    ("navy_blockade", "封锁与贸易", "Blockade & trade",
     "舰队封锁敌方港口会降低其税收与贸易，并提高战争分数；轻型船保护贸易可增加贸易收入。",
     "Blockading enemy ports cuts their tax and trade and adds war score; light ships protecting trade increase trade income.",
     ["封锁港口：削弱税收/贸易", "保护贸易：增加收入"], "Blockade: cut tax/trade; protect trade: raise income"),
    ("navy_maintenance", "维护与补给", "Maintenance & supply",
     "舰队在海上会消耗维护费与水手；远离己方港口、恶劣海况会增加损耗。",
     "Fleets cost maintenance and sailors at sea; distance from friendly ports and rough seas increase attrition.",
     ["海上维护：金币/水手", "远海与恶劣海况增加损耗"], "Sea maintenance: ducats/sailors; distant or rough waters add attrition"),
    ("navy_landing", "登陆与运输", "Landing & transport",
     "运输船把陆军送到敌方海岸；登陆作战会承受进攻惩罚，需先取得制海权。",
     "Transports deliver armies to enemy coasts; naval landings suffer an attacker penalty and require naval supremacy.",
     ["运输船装载陆军", "登陆承受惩罚", "需要制海权"], "Transports carry armies; landings take a penalty; naval supremacy required"),
    ("navy_flagship", "旗舰", "Flagship",
     "旗舰提供全舰队光环加成；旗舰被击沉会带来额外士气与威望损失。",
     "A flagship grants fleet-wide auras; losing it costs extra morale and prestige.",
     ["旗舰：全舰队光环", "沉没：额外损失"], "Flagship: fleet auras; sinking: extra losses"),
    ("navy_doctrine", "海军学说选择", "Naval doctrines",
     "海军学说提供长期国家修正，按玩法选择贸易、近海或主力舰队方向；同时只能启用有限数量。",
     "Naval doctrines give permanent country modifiers; choose trade, coastal or battle-fleet focus. Only a limited number can be active.",
     ["学说：长期国家修正", "按玩法选择方向"], "Doctrines: permanent modifiers; choose a playstyle"),
]


# ---------------------------------------------------------------- 基础解析

def _unquote(s):
    s = (s or "").strip()
    if len(s) >= 2 and s[0] == '"' and s[-1] == '"':
        return s[1:-1]
    return s


def _read(path):
    try:
        with open(path, encoding="utf-8-sig", errors="replace") as f:
            text = f.read()
    except OSError:
        return ""
    out = []
    for line in text.splitlines():
        buf = []
        q = False
        for ch in line:
            if ch == '"':
                q = not q
                buf.append(ch)
            elif ch == "#" and not q:
                break
            else:
                buf.append(ch)
        out.append("".join(buf))
    return "\n".join(out)


def _entries(text):
    """把脚本体拆成 [(key, value, is_block)]，value 为块内文本或标量。"""
    out = []
    i, n = 0, len(text or "")
    while i < n:
        ch = text[i]
        if ch in " \t\r\n}=":
            i += 1
            continue
        start = i
        while i < n and text[i] not in "= \t\r\n{}":
            i += 1
        key = _unquote(text[start:i])
        if not key:
            i += 1
            continue
        while i < n and text[i] in " \t\r\n":
            i += 1
        if i >= n or text[i] != "=":
            continue
        i += 1
        while i < n and text[i] in " \t\r\n":
            i += 1
        if i < n and text[i] == "{":
            depth, j, q = 1, i + 1, False
            while j < n:
                c = text[j]
                if c == '"':
                    q = not q
                elif not q:
                    if c == "{":
                        depth += 1
                    elif c == "}":
                        depth -= 1
                        if depth == 0:
                            break
                j += 1
            out.append((key, text[i + 1:j], True))
            i = j + 1
        else:
            start, q = i, False
            while i < n:
                c = text[i]
                if c == '"':
                    q = not q
                if not q and c in "\r\n":
                    break
                i += 1
            out.append((key, _unquote(text[start:i]), False))
    return out


def _blocks(text):
    return [(k, v) for k, v, isb in _entries(text) if isb]


def _sub(body, key):
    for k, v, isb in _entries(body):
        if k == key and isb:
            return v
    return ""


def _scalar(body, key, default=""):
    for k, v, isb in _entries(body):
        if k == key and not isb:
            return v
    return default


def _all_scalars(body, key):
    return [v for k, v, isb in _entries(body) if k == key and not isb]


def _list(body, key):
    b = _sub(body, key)
    if not b:
        return []
    return [x for x in re.split(r"[\s,]+", b.strip()) if x and x not in ("{", "}")]


def _pretty(key):
    s = (key or "").strip()
    s = re.sub(r"_(ideas|mission|missions|desc|title)$", "", s, flags=re.I)
    s = s.replace("_", " ").strip()
    if not s:
        return key or ""
    return " ".join(w[:1].upper() + w[1:] if w.islower() else w for w in s.split())


def _num(v):
    try:
        f = float(str(v).replace(",", "."))
    except Exception:
        return None
    if abs(f - round(f)) < 1e-9:
        return "%+d" % int(round(f)) if f else "0"
    return ("%+.2f" % f).rstrip("0").rstrip(".")


def _is_yes(v):
    return str(v).strip().lower() in ("yes", "true")


def _is_no(v):
    return str(v).strip().lower() in ("no", "false")


def _mod_value(key, v, zh=True):
    if _is_yes(v):
        if key in SPECIAL_YES:
            return SPECIAL_YES[key][0] if zh else SPECIAL_YES[key][1]
        return "已启用" if zh else "Enabled"
    if _is_no(v):
        return None if key in SPECIAL_YES else ("已停用" if zh else "Disabled")
    f = None
    try:
        f = float(str(v))
    except Exception:
        return str(v) or None
    if key not in FLAT_MODS and abs(f) <= 1 and abs(f - round(f)) > 1e-9:
        pct = f * 100
        if abs(pct - round(pct)) < 0.05:
            return "%+.0f%%" % round(pct)
        return ("%+.1f%%" % pct).rstrip("0").rstrip(".")
    return _num(f)


def _mod_label(key, loc, zh=True):
    if zh and key in MOD_CN:
        return MOD_CN[key]
    return loc.get(key) or _pretty(key)


def _mod_lines(body, loc, zh=True, limit=0):
    out = []
    for k, v, isb in _entries(body):
        if isb:
            continue
        if k in ("trigger", "ai_will_do", "effect", "free", "category",
                 "monarch_power", "potential", "important", "removed_effect"):
            continue
        val = _mod_value(k, v, zh)
        if val is None:
            continue
        out.append("%s %s" % (_mod_label(k, loc, zh), val))
        if limit and len(out) >= limit:
            break
    return out


TRIG = {
    "army_size_percentage": ("陆军规模达到陆军上限的 {}%", "Army size at {}% of force limit"),
    "navy_size_percentage": ("海军规模达到海军上限的 {}%", "Navy size at {}% of force limit"),
    "army_size": ("陆军规模达到 {}", "Army size of {}"),
    "navy_size": ("海军规模达到 {}", "Navy size of {}"),
    "grown_by_development": ("发展度增长 {}", "Grow development by {}"),
    "grown_by_states": ("州数增长 {}", "Grow states by {}"),
    "total_development": ("总发展度达到 {}", "Total development of {}"),
    "num_of_allies": ("盟友数 {}", "Allies: {}"),
    "num_of_vassals": ("附庸数 {}", "Vassals: {}"),
    "vassal": ("附庸：{}", "Vassal: {}"),
    "num_of_heavy_ship": ("重型船数量 {}", "Heavy ships: {}"),
    "num_of_light_ship": ("轻型船数量 {}", "Light ships: {}"),
    "num_of_galley": ("桨帆船数量 {}", "Galleys: {}"),
    "num_of_transport": ("运输船数量 {}", "Transports: {}"),
    "num_of_ship": ("舰船数量 {}", "Ships: {}"),
    "num_of_continents": ("拥有领地的大洲数 {}", "Continents: {}"),
    "has_institution": ("已接受思潮：{}", "Institution embraced: {}"),
    "full_idea_group": ("完成理念组：{}", "Completed idea group: {}"),
    "religion": ("宗教：{}", "Religion: {}"),
    "religion_group": ("宗教组：{}", "Religion group: {}"),
    "culture_group": ("文化组：{}", "Culture group: {}"),
    "tag": ("国家：{}", "Country: {}"),
    "government": ("政体：{}", "Government: {}"),
    "government_rank": ("政府等级 {}", "Government rank {}"),
}


def _trigger_lines(body, loc, zh=True, limit=0):
    out = []
    for k, v, isb in _entries(body):
        if isb:
            if k == "custom_trigger_tooltip":
                tip = _scalar(v, "tooltip")
                if tip:
                    x = _tip(loc.get(tip) or _pretty(tip), zh)
                    if x:
                        out.append(x)
            elif k == "calc_true_if":
                amt = _scalar(v, "amount")
                if amt:
                    out.append(("满足其中 %s 项条件" % amt) if zh else ("Meet %s of the conditions" % amt))
            continue
        if k in ("limit", "scope", "who", "tooltip"):
            continue
        if k in TRIG:
            fmt = TRIG[k][0] if zh else TRIG[k][1]
            val = v
            if k in ("has_institution", "full_idea_group", "religion", "religion_group",
                     "culture_group", "tag", "government"):
                val = loc.get(val) or _pretty(val)
            elif k.endswith("percentage"):
                try:
                    val = "%g" % (float(val) * 100)
                except Exception:
                    pass
            out.append(fmt.format(val))
        elif re.match(r"^[+-]?\d+(\.\d+)?$", str(v)):
            label = MOD_CN.get(k) if zh else None
            out.append("%s：%s" % (label or _pretty(k), v))
        if limit and len(out) >= limit:
            break
    return out


EFF_CN = {
    "add_prestige": "威望", "add_army_tradition": "陆军传统",
    "add_navy_tradition": "海军传统", "add_adm_power": "行政点数",
    "add_dip_power": "外交点数", "add_mil_power": "军事点数",
    "add_manpower": "人力", "add_years_of_income": "年收入",
    "add_base_tax": "基础税收", "add_base_production": "基础生产",
    "add_base_manpower": "基础人力", "add_stability": "稳定度",
    "add_legitimacy": "正统性", "add_republican_tradition": "共和传统",
    "add_mercantilism": "重商主义", "add_inflation": "通胀",
    "add_war_exhaustion": "战争疲惫", "add_absolutism": "专制",
    "add_power_projection": "实力投射", "add_splendor": "辉煌",
    "add_monarch_power": "君主点数", "add_devotion": "虔诚",
    "add_army_professionalism": "陆军职业度",
}

TIP_CN = {
    "SEPERATOR TOOLTIP": "",
    "SEPARATOR TOOLTIP": "",
    "Alternatively:": "或者：",
    "This mission cannot be completed as it is a Branching Mission.": "分支任务，无法直接完成。",
    "This is a Branching Mission. Complete 'Our Religious Direction' Mission to preview each branch.":
        "这是分支任务，完成「Our Religious Direction」任务后可预览各分支。",
    "This Power Projection is permanent.": "此次威望投射为永久。",
    "The effects of this mission depend on future paths of Branching Missions.":
        "该任务的效果取决于后续分支任务的选择。",
    "This modifier gets upgraded by later missions.": "该修正会由后续任务升级。",
    "This mission grants a Dynamic Reward based on how we complete it":
        "该任务会根据完成方式给予动态奖励。",
    "gains a claim on every Neighboring Province.": "获得对所有相邻省份的宣称",
    "gains a permanent claim on every Neighboring Province.": "获得对所有相邻省份的永久宣称",
    "gains a permanent claim on every Neighboring Area.": "获得对所有相邻地区的永久宣称",
    "gains a permanent claim on every province in our Home Region.": "获得本国所在地区所有省份的永久宣称",
}


TIP_LBL = {
    "Num Of Generals": "将领数", "Num Of Admirals": "海军将领数",
    "Num Of Colonists": "殖民者数", "Num Of Explorers": "探险家数",
    "Num Of Ports": "港口数", "Num Of Merchants": "商人团数",
    "Num Of Diplomats": "外交官数",
    "Reform Level": "改革等级", "Crown Land Share": "王室领地份额",
    "Legitimacy Equivalent": "合法性等价", "Manpower Percentage": "人力百分比",
    "Share Of Starting Income": "起始收入占比",
    "Monthly Mil": "每月军事点数", "Monthly Adm": "每月行政点数",
    "Monthly Dip": "每月外交点数", "Treasury": "国库",
    "Monthly Income": "月收入", "Owns Core Province": "拥有核心省份",
    "Owns Or Non Sovereign Subject Of": "拥有或为其非主权附属",
    "Owns": "拥有省份", "Trader": "商人", "Temple": "神庙",
}


def _tip(s, zh):
    """常见 custom_tooltip 文案转中文；返回空串表示丢弃。"""
    if not s or not zh:
        return s
    if s in TIP_CN:
        return TIP_CN[s]
    if s.endswith(" Empty"):
        return ""
    m = re.match(r"^This is a Branching Mission\. Complete (.+?) Mission to preview each branch\.$", s)
    if m:
        return "这是分支任务。完成「%s」任务后可预览各分支。" % m.group(1)
    m = re.match(r"^If chooses (.+?) Path, the following effects will occur:$", s)
    if m:
        return "若选择「%s」路线，将发生以下效果：" % m.group(1)
    m = re.match(
        r"^(Stability|Religious Unity|Prestige|Reform Level|Legitimacy Equivalent|"
        r"Crown Land Share|Num Of Generals|Num Of Admirals|Manpower Percentage)：(.+)$", s)
    if m:
        cn = {"Stability": "稳定度", "Religious Unity": "宗教统一", "Prestige": "威望",
              "Reform Level": "改革等级", "Legitimacy Equivalent": "合法性等价",
              "Crown Land Share": "王室领地份额", "Num Of Generals": "将领数",
              "Num Of Admirals": "海军将领数", "Manpower Percentage": "人力百分比"}
        return "%s：%s" % (cn[m.group(1)], m.group(2))
    m = re.match(r"^([A-Za-z][A-Za-z ]*)：(.+)$", s)
    if m and m.group(1) in TIP_LBL:
        return "%s：%s" % (TIP_LBL[m.group(1)], m.group(2))
    if re.match(r"^[A-Z]{2,4} This Is A Branching Mission Always = No$", s):
        return "这是分支任务"
    return s


def _effect_lines(body, loc, zh=True, limit=0):
    out = []
    for k, v, isb in _entries(body):
        if isb:
            if k in ("add_country_modifier", "add_province_modifier"):
                nm = _scalar(v, "name")
                dur = _scalar(v, "duration")
                label = loc.get(nm) or _pretty(nm)
                if dur:
                    try:
                        yrs = int(float(dur) / 365)
                    except Exception:
                        yrs = 0
                    line = ("获得修正：%s（%d 年）" % (label, yrs)) if zh else ("Gain modifier: %s (%d years)" % (label, yrs))
                else:
                    line = ("获得修正：%s" % label) if zh else ("Gain modifier: %s" % label)
                out.append(line)
            elif k == "add_claim":
                out.append("获得宣称" if zh else "Gain a claim")
            elif k == "add_permanent_claim":
                out.append("获得永久宣称" if zh else "Gain a permanent claim")
            elif k == "add_core":
                out.append("获得核心" if zh else "Gain a core")
            elif k == "custom_tooltip":
                tip = _scalar(v, "tooltip")
                if tip:
                    x = _tip(loc.get(tip) or _pretty(tip), zh)
                    if x:
                        out.append(x)
            elif k in EFF_CN:
                pass
            if limit and len(out) >= limit:
                break
            continue
        if k == "custom_tooltip":
            if v:
                x = _tip(loc.get(v) or _pretty(v), zh)
                if x:
                    out.append(x)
        elif k in EFF_CN:
            f = None
            try:
                f = float(v)
            except Exception:
                f = None
            if f is not None:
                out.append("%s %s" % (EFF_CN[k] if zh else _pretty(k), _num(f)))
        elif k in ("add_army_tradition_or_mil_power",):
            out.append("陆军传统或军事点数 +%s" % v if zh else "Army tradition or mil power +%s" % v)
        if limit and len(out) >= limit:
            break
    return out


def _clean_loc(s):
    s = s.replace("\\n", " ").replace('\\"', '"')
    s = re.sub(r"\[[^\]]*\]", "", s)
    s = re.sub(r"§.", "", s)
    s = re.sub(r"£[A-Za-z0-9_\-]+£", "", s)
    s = re.sub(r"\$[A-Za-z0-9_.:\-]+\$", "", s)
    s = re.sub(r"\s+", " ", s).strip()
    return s


_LOC_RE = re.compile(r'^([^\s:]+)\s*:\s*\d*\s*"((?:[^"\\]|\\.)*)"')


def _load_loc(loc_dir, lang):
    d = {}
    langs = ["english"]
    if lang and lang != "english":
        langs.append(lang)
    for lg in langs:
        files = sorted(glob.glob(os.path.join(loc_dir, "*_%s.yml" % lg)))
        for path in files:
            try:
                with open(path, encoding="utf-8-sig", errors="replace") as f:
                    for line in f:
                        m = _LOC_RE.match(line.lstrip())
                        if not m:
                            continue
                        d[m.group(1)] = _clean_loc(m.group(2))
            except OSError:
                continue
    return d


def _loc_dir(root, lang):
    loc = os.path.join(root, "localisation")
    if lang and glob.glob(os.path.join(loc, "*_%s.yml" % lang)):
        return loc, lang
    return loc, "english"


def _tech_units(path):
    out, idx, last_year = {}, 0, ""
    for key, body, isb in _entries(_read(path)):
        if key != "technology" or not isb:
            continue
        year = _scalar(body, "year") or last_year
        last_year = year
        for en in _all_scalars(body, "enable"):
            for u in re.split(r"\s+", en.strip()):
                if u:
                    out[u] = {"tech": idx, "year": year}
        idx += 1
    return out


def _game_version(root):
    for fn in ("launcher-settings.json", "launcher-settings.json"):
        p = os.path.join(root, fn)
        if os.path.isfile(p):
            try:
                with open(p, encoding="utf-8-sig") as f:
                    blob = json.load(f)
                v = blob.get("version") or blob.get("rawVersion") or ""
                if v:
                    return str(v)
            except Exception:
                pass
    return ""


def game_root_of(hint=""):
    hint = (hint or "").strip().strip('"')
    cands = [hint] if hint else []
    for d in "CDEFG":
        cands.append(d + ":\\steam\\steamapps\\common\\Europa Universalis IV")
        cands.append(d + ":\\SteamLibrary\\steamapps\\common\\Europa Universalis IV")
        cands.append(d + ":\\Games\\Europa Universalis IV")
    seen = set()
    for c in cands:
        c = os.path.normpath(c) if c else ""
        if not c or c.lower() in seen:
            continue
        seen.add(c.lower())
        if os.path.isdir(os.path.join(c, "common", "ideas")) and \
                (os.path.isfile(os.path.join(c, "eu4.exe")) or
                 os.path.isdir(os.path.join(c, "missions"))):
            return c
    return ""


# ---------------------------------------------------------------- 思想组

def _scan_ideas(root, loc, zh=True):
    ideas = []
    generic_files = {"00_basic_ideas.txt", "zz_group_ideas.txt"}
    files = sorted(glob.glob(os.path.join(root, "common", "ideas", "*.txt")))
    for path in files:
        fn = os.path.basename(path)
        for gk, gb in _blocks(_read(path)):
            if not gk.endswith("_ideas"):
                continue
            cat = _scalar(gb, "category").upper()
            if cat in ("ADM", "DIP", "MIL"):
                ck, catname = cat, CAT_CN[cat]
            else:
                ck, catname = "national", CAT_CN["national"]
            bonus = _mod_lines(_sub(gb, "bonus"), loc, zh, 30)
            start = _mod_lines(_sub(gb, "start"), loc, zh, 30)
            secs = []
            awards = []
            if start:
                awards.append({"label": "初始理念" if zh else "Starting ideas", "effects": start})
            if bonus:
                awards.append({"label": "完成奖励" if zh else "Completion bonus", "effects": bonus})
            if awards:
                secs.append({"label": "组奖励" if zh else "Group bonuses", "kind": "tiers", "items": awards})
            its = []
            reserved = {"bonus", "start", "trigger", "free", "ai_will_do", "category",
                        "important", "effect"}
            for ik, ib, isb in _entries(gb):
                if not isb or ik in reserved:
                    continue
                eff = _mod_lines(ib, loc, zh, 30)
                its.append({"label": loc.get(ik) or _pretty(ik), "effects": eff})
            if its:
                secs.append({"label": ("理念 (%d)" % len(its)) if zh else ("Ideas (%d)" % len(its)),
                             "kind": "tiers", "items": its})
            name = loc.get(gk) or _pretty(gk)
            desc = ("%s · %d 条理念" % (catname, len(its))) if zh else ("%s · %d ideas" % (catname, len(its)))
            ideas.append({
                "key": gk, "name": name, "cat": catname, "catKey": ck,
                "desc": desc, "effects": [], "secs": secs,
                "attrs": [{"label": "类别" if zh else "Category", "value": catname},
                          {"label": "理念数" if zh else "Ideas", "value": str(len(its))}],
            })
    order = ["ADM", "DIP", "MIL", "national"]
    return ideas, {"ideas": order}


# ---------------------------------------------------------------- 单位

def _scan_units(root, loc, techs, zh=True):
    land, naval = [], []
    files = sorted(glob.glob(os.path.join(root, "common", "units", "*.txt")))
    for path in files:
        key = os.path.splitext(os.path.basename(path))[0]
        body = _read(path)
        t = _scalar(body, "type").lower()
        if t not in LAND_TYPES and t not in NAVAL_TYPES:
            continue
        is_land = t in LAND_TYPES
        grp = re.sub(r"_tech$", "", _scalar(body, "unit_type").lower())
        grp_name = GROUP_CN.get(grp) if zh else GROUP_EN.get(grp)
        grp_name = grp_name or _pretty(grp)
        name = loc.get(key) or _pretty(key)
        info = techs.get(key) or {}
        tech = info.get("tech")
        year = info.get("year") or ""
        if is_land:
            typ = TYPE_CN.get(t, t) if zh else TYPE_EN.get(t, _pretty(t))
            attrs = [
                {"label": "类型" if zh else "Type", "value": typ},
                {"label": "科技组" if zh else "Tech group", "value": grp_name},
                {"label": "军事科技" if zh else "Mil tech", "value": str(tech) if tech is not None else "—"},
                {"label": "年份" if zh else "Year", "value": str(year) or "—"},
                {"label": "机动" if zh else "Maneuver", "value": _scalar(body, "maneuver") or "—"},
                {"label": "火力（攻/防）" if zh else "Fire (off/def)",
                 "value": "%s / %s" % (_scalar(body, "offensive_fire") or "0",
                                        _scalar(body, "defensive_fire") or "0")},
                {"label": "冲击（攻/防）" if zh else "Shock (off/def)",
                 "value": "%s / %s" % (_scalar(body, "offensive_shock") or "0",
                                        _scalar(body, "defensive_shock") or "0")},
                {"label": "士气（攻/防）" if zh else "Morale (off/def)",
                 "value": "%s / %s" % (_scalar(body, "offensive_morale") or "0",
                                        _scalar(body, "defensive_morale") or "0")},
            ]
            desc = ("%s · %s" % (typ, grp_name)) + ((" · 军科 %s" % tech) if tech is not None else "")
            land.append({"key": key, "name": name, "cat": typ, "catKey": t,
                         "desc": desc, "effects": [], "attrs": attrs})
        else:
            typ = TYPE_CN.get(t, t) if zh else TYPE_EN.get(t, _pretty(t))
            attrs = [
                {"label": "类型" if zh else "Type", "value": typ},
                {"label": "船体" if zh else "Hull", "value": _scalar(body, "hull_size") or "—"},
                {"label": "火炮" if zh else "Cannons", "value": _scalar(body, "base_cannons") or "—"},
                {"label": "封锁" if zh else "Blockade", "value": _scalar(body, "blockade") or "—"},
                {"label": "航速" if zh else "Speed", "value": _scalar(body, "sail_speed") or "—"},
                {"label": "水手" if zh else "Sailors", "value": _scalar(body, "sailors") or "—"},
                {"label": "贸易力量" if zh else "Trade power", "value": _scalar(body, "trade_power") or "—"},
            ]
            if tech is not None:
                attrs.insert(1, {"label": "军事科技" if zh else "Mil tech", "value": str(tech)})
            desc = ("%s · %s" % (typ, grp_name)) + ((" · 军科 %s" % tech) if tech is not None else "")
            naval.append({"key": key, "name": name, "cat": typ, "catKey": t,
                          "desc": desc, "effects": [], "attrs": attrs})
    land.sort(key=lambda x: (LAND_TYPES.index(x["catKey"]), _sort_tech(techs, x["key"]), x["name"]))
    naval.sort(key=lambda x: (NAVAL_TYPES.index(x["catKey"]), _sort_tech(techs, x["key"]), x["name"]))
    orders = {"landunits": list(LAND_TYPES), "navalunits": list(NAVAL_TYPES)}
    return land, naval, orders


GROUP_EN = {
    "western": "Western", "eastern": "Eastern", "ottoman": "Ottoman",
    "muslim": "Muslim", "indian": "Indian", "chinese": "Chinese",
    "nomad": "Nomad", "african": "African", "american": "American",
    "aboriginal": "Aboriginal", "andean": "Andean", "mesoamerican": "Mesoamerican",
    "sub_saharan": "Sub-Saharan",
    "north_american": "North American", "south_american": "South American",
    "high_american": "High American", "central_american": "Central American",
}


def _sort_tech(techs, key):
    t = (techs.get(key) or {}).get("tech")
    return t if isinstance(t, int) else 999


def _scan_army(zh=True):
    out = []
    for key, cn, en, dcn, den, ecn, een in ARMY_GUIDE:
        out.append({
            "key": key, "name": cn if zh else en,
            "cat": ("陆战机制" if zh else "Land warfare"), "catKey": "mechanics",
            "desc": dcn if zh else den,
            "effects": [ecn] if zh else [een],
            "attrs": [],
        })
    return out, {"army": ["mechanics"]}


# ---------------------------------------------------------------- 海军

def _cond_lines(body, loc, zh):
    out = []
    for k, v, isb in _entries(body):
        if isb:
            continue
        if k == "is_primitive":
            out.append("非原始国家" if (_is_no(v) and zh) else ("Non-primitive" if _is_no(v) else _pretty(k)))
        elif k in ("tag", "culture_group", "religion_group", "government"):
            val = loc.get(v) or _pretty(v)
            out.append(("%s：%s" % (_pretty(k), val)) if zh else ("%s: %s" % (_pretty(k), val)))
        elif _is_yes(v) or _is_no(v):
            continue
    return out


def _scan_navy(root, loc, zh=True):
    out = []
    for path in sorted(glob.glob(os.path.join(root, "common", "naval_doctrines", "*.txt"))):
        for key, body in _blocks(_read(path)):
            eff = _mod_lines(_sub(body, "country_modifier"), loc, zh, 30)
            cost = _scalar(body, "cost")
            cond = _cond_lines(_sub(body, "can_select"), loc, zh)
            attrs = []
            if cost:
                try:
                    value = float(cost)
                    attrs.append({"label": "维护费用" if zh else "Maintenance",
                                  "value": ("每名水手 %.2f 金币" % value) if zh else ("%.2f ducats / sailor" % value)})
                except Exception:
                    attrs.append({"label": "维护费用" if zh else "Maintenance", "value": cost})
            secs = []
            if cond:
                secs.append({"label": "选择条件" if zh else "Requirements",
                             "kind": "list", "items": cond})
            out.append({
                "key": key, "name": loc.get(key) or _pretty(key),
                "cat": ("海军学说" if zh else "Naval doctrine"), "catKey": "doctrine",
                "desc": loc.get(key + "_desc") or "",
                "effects": eff, "attrs": attrs, "secs": secs,
            })
    for key, cn, en, dcn, den, ecn, een in NAVY_GUIDE:
        out.append({
            "key": key, "name": cn if zh else en,
            "cat": ("海军机制" if zh else "Naval mechanics"), "catKey": "mechanics",
            "desc": dcn if zh else den,
            "effects": [ecn] if zh else [een],
            "attrs": [],
        })
    out.sort(key=lambda x: (0 if x["catKey"] == "mechanics" else 1, x["name"]))
    return out, {"navy": ["mechanics", "doctrine"]}


# ---------------------------------------------------------------- 时代

def _scan_ages(root, loc, zh=True):
    out, keys = [], []
    for path in sorted(glob.glob(os.path.join(root, "common", "ages", "*.txt"))):
        for key, body in _blocks(_read(path)):
            if not _scalar(body, "start") and not _sub(body, "objectives"):
                continue
            name = loc.get(key) or _pretty(key)
            start = _scalar(body, "start")
            objs, abils = [], []
            for ok, ob, isb in _entries(_sub(body, "objectives")):
                if not isb:
                    continue
                cond = _trigger_lines(ob, loc, zh, 6) or _effect_lines(ob, loc, zh, 6)
                objs.append({"label": loc.get(ok) or _pretty(ok), "effects": cond})
            for ak, ab, isb in _entries(_sub(body, "abilities")):
                if not isb:
                    continue
                eff = _mod_lines(_sub(ab, "modifier"), loc, zh, 10)
                if not eff:
                    eff = _effect_lines(_sub(ab, "effect"), loc, zh, 10)
                abils.append({"label": loc.get(ak) or _pretty(ak), "effects": eff})
            secs = []
            if objs:
                secs.append({"label": "时代目标" if zh else "Age objectives",
                             "kind": "tiers", "items": objs})
            if abils:
                secs.append({"label": "时代能力" if zh else "Age abilities",
                             "kind": "tiers", "items": abils})
            can = _trigger_lines(_sub(body, "can_start"), loc, zh, 4)
            attrs = [
                {"label": "起始年份" if zh else "Start year", "value": start or "—"},
                {"label": "目标数" if zh else "Objectives", "value": str(len(objs))},
                {"label": "能力数" if zh else "Abilities", "value": str(len(abils))},
            ]
            if can:
                attrs.append({"label": "开始条件" if zh else "Start conditions", "value": "；".join(can)})
            out.append({
                "key": key, "name": name, "cat": name, "catKey": key,
                "desc": loc.get(key + "_desc") or (("从 %s 年开始" % start) if zh and start else ""),
                "effects": [], "attrs": attrs, "secs": secs,
            })
            keys.append(key)
    out.sort(key=lambda x: _age_year(x), )
    return out, {"ages": keys}


def _age_year(it):
    for a in it.get("attrs") or []:
        if a.get("label") in ("起始年份", "Start year"):
            try:
                return int(a.get("value"))
            except Exception:
                break
    return 9999


# ---------------------------------------------------------------- 任务树

def _slug(s):
    s = re.sub(r"[^a-z0-9]+", "_", (s or "").lower()).strip("_")
    return s or "tree"


def _tree_name(stem):
    s = re.sub(r"^\d+_", "", stem)
    s = re.sub(r"_?Missions?$", "", s, flags=re.I)
    s = s.replace("_", " ").strip()
    return s or "Missions"


def _looks_series(body):
    es = _entries(body)
    has_slot = any(not isb and k == "slot" for k, v, isb in es)
    has_mission = any(isb and _looks_mission(v) for k, v, isb in es)
    return has_slot and has_mission


def _looks_mission(body):
    es = _entries(body)
    for k, v, isb in es:
        if k in ("trigger", "effect", "required_missions", "provinces_to_highlight"):
            return True
        if k == "icon" and not isb:
            return True
    return False


def _scan_missions(root, loc, zh=True):
    trees = {}
    generic_ids = {"00_generic_missions", "01_generic_european_missions"}
    for path in sorted(glob.glob(os.path.join(root, "missions", "*.txt"))):
        stem = os.path.splitext(os.path.basename(path))[0]
        tid = _slug(stem)
        if tid in trees:
            continue
        series, implicit = [], []
        for key, body, isb in _entries(_read(path)):
            if not isb:
                continue
            children = [(k, v) for k, v, b in _entries(body) if b]
            nested = [(k, v) for k, v in children if _looks_series(v)]
            if nested:
                series.extend(nested)
            elif _looks_series(body):
                series.append((key, body))
            elif _looks_mission(body):
                implicit.append((key, body))
        if implicit:
            series.append(("__implicit__", "slot = 1\n" + "\n".join(
                "%s = { %s }" % (k, v) for k, v in implicit)))
        if not series:
            continue

        ordered = []
        for i, (sk, sb) in enumerate(series):
            slot = _scalar(sb, "slot")
            try:
                sv = int(slot)
            except Exception:
                sv = 99
            ordered.append((sv, i, sk, sb))
        ordered.sort(key=lambda x: (x[0], x[1]))

        nodes, byraw = [], {}
        for col, (slotv, _o, sk, sb) in enumerate(ordered):
            for mk, mb, isb in _entries(sb):
                if not isb or not _looks_mission(mb):
                    continue
                raw = mk
                nid = raw if raw not in byraw else "%s~%d" % (raw, len(nodes))
                byraw.setdefault(raw, nid)
                pre_raw = _list(mb, "required_missions")
                nodes.append({
                    "id": nid, "raw": raw,
                    "nm": loc.get(raw + "_title") or _pretty(raw),
                    "ds": loc.get(raw + "_desc") or "",
                    "icon": _scalar(mb, "icon") or "",
                    "pre": pre_raw,
                    "fx": _effect_lines(_sub(mb, "effect"), loc, zh, 6),
                    "cond": _trigger_lines(_sub(mb, "trigger"), loc, zh, 6),
                    "x": col, "y": 0,
                    "s": slotv if slotv != 99 else 1,
                })
        if not nodes:
            continue

        # 前置 → 节点 id
        for n in nodes:
            n["pre"] = [byraw[p] for p in n["pre"] if p in byraw]

        # 纵向层级：反复松弛，环则封顶
        byid = {n["id"]: n for n in nodes}
        for _ in range(len(nodes) + 1):
            changed = False
            for n in nodes:
                y = 0
                for p in n["pre"]:
                    q = byid.get(p)
                    if q:
                        y = max(y, q["y"] + 1)
                if y > len(nodes) - 1:
                    y = len(nodes) - 1
                if y != n["y"]:
                    n["y"] = y
                    changed = True
            if not changed:
                break

        for n in nodes:
            n.pop("raw", None)
        x1 = max(n["x"] for n in nodes)
        y1 = max(n["y"] for n in nodes)
        trees[tid] = {
            "id": tid, "name": _tree_name(stem), "n": len(nodes),
            "default": tid in generic_ids, "x0": 0, "x1": x1, "y0": 0, "y1": y1,
            "focuses": nodes,
        }
    slim = [{"id": t["id"], "name": t["name"], "n": t["n"], "default": t["default"]}
            for t in trees.values()]
    slim.sort(key=lambda x: (0 if x["default"] else 1, x["name"].lower()))
    return trees, slim


# ---------------------------------------------------------------- 缓存与入口

def _sig(root, loc_lang):
    parts = []
    dirs = [
        os.path.join(root, "common", "ideas"),
        os.path.join(root, "common", "units"),
        os.path.join(root, "common", "technologies"),
        os.path.join(root, "common", "ages"),
        os.path.join(root, "common", "naval_doctrines"),
        os.path.join(root, "missions"),
        os.path.join(root, "localisation"),
        os.path.join(root, "interface"),
    ]
    for d in dirs:
        try:
            files = glob.glob(os.path.join(d, "**", "*"), recursive=True)
            files = [f for f in files if os.path.isfile(f)]
            size = sum(os.path.getsize(f) for f in files)
            mt = max((os.path.getmtime(f) for f in files), default=0)
            parts.append("%s:%d:%d:%d" % (os.path.basename(d), len(files), size, int(mt)))
        except Exception:
            parts.append("-")
    parts.append(loc_lang)
    return "|".join(parts)


def _cache_path(loc_lang):
    return os.path.join(APP_DIR, "wiki_cache_eu4_%s.json" % (loc_lang or "english"))


def _build(root, loc_lang, zh):
    loc = _load_loc(os.path.join(root, "localisation"), loc_lang)
    techs = _tech_units(os.path.join(root, "common", "technologies", "mil.txt"))
    ideas, idea_orders = _scan_ideas(root, loc, zh)
    land, naval, unit_orders = _scan_units(root, loc, techs, zh)
    army, army_orders = _scan_army(zh)
    navy, navy_orders = _scan_navy(root, loc, zh)
    ages, age_orders = _scan_ages(root, loc, zh)
    trees, slim = _scan_missions(root, loc, zh)
    counts = {
        "ideas": len(ideas), "army": len(army), "landunits": len(land),
        "navalunits": len(naval), "navy": len(navy), "ages": len(ages),
        "missions": sum(t["n"] for t in trees.values()),
    }
    tabs = []
    for t in TABS:
        n = len(slim) if t["id"] == "missions" else counts.get(t["id"], 0)
        tabs.append({"id": t["id"], "icon": t["icon"], "layout": t["layout"], "count": n})
    cat = {}
    cat.update(idea_orders)
    cat.update(unit_orders)
    cat.update(army_orders)
    cat.update(navy_orders)
    cat.update(age_orders)
    data = {
        "ok": True, "supported": True, "game": "eu4",
        "gameName": "Europa Universalis IV", "gameVer": _game_version(root),
        "loc": loc_lang, "tabs": tabs, "counts": counts,
        "ideas": ideas, "army": army, "landunits": land, "navalunits": naval,
        "navy": navy, "ages": ages, "catOrders": cat,
        "missionTrees": slim,
    }
    return data, trees


def load(game_root="", ui_lang="zh", force=False):
    root = game_root_of(game_root)
    if not root:
        return {"ok": False, "supported": True, "game": "eu4",
                "error": "没找到《欧陆风云 IV》本体目录（common/ideas），请在设置里确认游戏根目录"}
    lang = LANG_MAP.get((ui_lang or "zh").lower(), "english")
    loc_dir, loc_lang = _loc_dir(root, lang)
    zh = (ui_lang or "zh").lower() == "zh"
    sig = _sig(root, loc_lang)
    mem_key = "%s|%s" % (root.lower(), loc_lang)
    with _LOCK:
        hit = _MEM.get(mem_key)
        if hit and hit[0] == sig and not force:
            data = dict(hit[1])
            data["cached"] = True
            return data
        cpath = _cache_path(loc_lang)
        if not force:
            try:
                with open(cpath, encoding="utf-8") as f:
                    blob = json.load(f)
                if blob.get("ver") == EU4_VER and blob.get("sig") == sig \
                        and (blob.get("root") or "").lower() == root.lower():
                    data = blob.get("data") or {}
                    trees = blob.get("trees") or {}
                    _TREES.clear()
                    _TREES.update(trees)
                    _MEM[mem_key] = (sig, data)
                    data = dict(data)
                    data["cached"] = True
                    return data
            except Exception:
                pass

        t0 = time.time()
        data, trees = _build(root, loc_lang, zh)
        data["buildSeconds"] = round(time.time() - t0, 1)
        _TREES.clear()
        _TREES.update(trees)
        _MEM[mem_key] = (sig, data)
        try:
            with open(cpath, "w", encoding="utf-8") as f:
                json.dump({"ver": EU4_VER, "sig": sig, "root": root,
                           "data": data, "trees": trees}, f, ensure_ascii=False)
        except Exception:
            pass
        data = dict(data)
        data["cached"] = False
        return data


def mission_tree(tid):
    if not _TREES:
        load("", "zh", False)
    t = _TREES.get(tid or "")
    if not t:
        return {"ok": False, "error": "not found"}
    d = dict(t)
    d["ok"] = True
    return d


def _gfx_map(root):
    key = (root or "").lower()
    if key in _GFX:
        return _GFX[key]
    out = {}
    for path in glob.glob(os.path.join(root, "interface", "**", "*.gfx"), recursive=True):
        for k, body in _blocks(_read(path)):
            if k != "spriteTypes":
                continue
            for sk, sb, isb in _entries(body):
                if not isb or sk != "spriteType":
                    continue
                nm = _scalar(sb, "name")
                tex = _scalar(sb, "texturefile")
                if nm and tex:
                    out[nm] = tex.replace("\\\\", "/").replace("//", "/").replace("\\", "/")
    _GFX[key] = out
    return out


def mission_icon_png(game_root, gfx_name):
    root = game_root_of(game_root)
    if not root or not gfx_name:
        return ""
    safe = re.sub(r"[^A-Za-z0-9_.-]", "_", gfx_name)[:120]
    outdir = os.path.join(APP_DIR, "eu4_mission_icons")
    png = os.path.join(outdir, safe + ".png")
    if os.path.isfile(png):
        return png
    tex = _gfx_map(root).get(gfx_name) or ("gfx/interface/missions/%s.dds" % gfx_name)
    dds = os.path.join(root, tex.replace("/", os.sep))
    if not os.path.isfile(dds):
        return ""
    try:
        from PIL import Image
        os.makedirs(outdir, exist_ok=True)
        im = Image.open(dds)
        if im.mode not in ("RGBA", "RGB"):
            im = im.convert("RGBA")
        im.save(png, "PNG")
        return png
    except Exception:
        return ""

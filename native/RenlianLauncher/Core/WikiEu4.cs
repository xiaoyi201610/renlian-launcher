using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RenlianLauncher.Core;

/// <summary>
/// 欧陆风云 IV 百科（旧版 backend/eu4.py 的原生移植）：
/// 思想组 / 陆战机制 / 陆军单位 / 海军单位 / 海军学说 / 时代 / 任务树。
/// 任务树复用前端钢铁雄心的国策画布，因此节点字段与 hoi4 focus 同形（x/y/pre/fx）。
///
/// 解析口径与旧版一致：
///   · `_entries` 顺序拆 key/value/块，块内 `}` 与引号成对；
///   · 修正值按「整数字面量 / ±100% 百分比 / 开关」三种展示，词条名先查 MOD_CN 再查本地化；
///   · 任务树把 `slot` 当列、`required_missions` 反复松弛成纵坐标，同名前置转节点 id。
/// 中文汉化 mod（双字节本地化）与工坊/文档目录的 mod 也会合并进来。
/// </summary>
internal static class WikiEu4
{
    private const int E4Ver = 2;

    private static readonly string D_TECH = Path.Combine("common", "technologies");
    private static readonly string D_IDEA = Path.Combine("common", "ideas");
    private static readonly string D_UNIT = Path.Combine("common", "units");
    private static readonly string D_AGE = Path.Combine("common", "ages");
    private static readonly string D_DOCTRINE = Path.Combine("common", "naval_doctrines");
    private static readonly string D_MISS = "missions";
    private static readonly string D_LOC = "localisation";

    private static readonly Dictionary<string, string> LangMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh"] = "simp_chinese", ["en"] = "english", ["fr"] = "french",
        ["de"] = "german", ["sv"] = "english", ["es"] = "spanish",
        ["ru"] = "russian", ["pl"] = "polish",
    };

    private static readonly (string Id, string Icon, string Layout)[] Tabs =
    {
        ("ideas", "💡", "generic"), ("army", "⚔", "generic"),
        ("landunits", "🪖", "generic"), ("navalunits", "⛵", "generic"),
        ("navy", "⚓", "generic"), ("ages", "🕰", "generic"),
        ("missions", "🗺", "focus"),
    };

    private static readonly string[] LandTypes = { "infantry", "cavalry", "artillery" };
    private static readonly string[] NavalTypes = { "heavy_ship", "light_ship", "galley", "transport", "frigate" };

    private static readonly Dictionary<string, string> TypeCn = new(StringComparer.Ordinal)
    {
        ["infantry"] = "步兵", ["cavalry"] = "骑兵", ["artillery"] = "炮兵",
        ["heavy_ship"] = "重型船", ["light_ship"] = "轻型船", ["galley"] = "桨帆船",
        ["transport"] = "运输船", ["frigate"] = "护卫舰",
    };
    private static readonly Dictionary<string, string> TypeEn = new(StringComparer.Ordinal)
    {
        ["infantry"] = "Infantry", ["cavalry"] = "Cavalry", ["artillery"] = "Artillery",
        ["heavy_ship"] = "Heavy Ship", ["light_ship"] = "Light Ship", ["galley"] = "Galley",
        ["transport"] = "Transport", ["frigate"] = "Frigate",
    };

    // 汉化 mod 文本的标记字节（文件里是真实的控制字符）
    private const string LocMarks = "\u0010\u0011\u0012\u0013";

    private static readonly Dictionary<string, (string Gold, string Manpower)> UnitCost = new(StringComparer.Ordinal)
    {
        ["infantry"] = ("INFANTRY_COST", "INF_REGIMENT_MANPOWER_COST"),
        ["cavalry"] = ("CAVALRY_COST", "CAV_REGIMENT_MANPOWER_COST"),
        ["artillery"] = ("ARTILLERY_COST", "ART_REGIMENT_MANPOWER_COST"),
    };
    private static readonly Dictionary<string, (string Gold, string Sailor)> ShipCost = new(StringComparer.Ordinal)
    {
        ["heavy_ship"] = ("HEAVY_SHIP_COST", "HEAVY_SHIP_SAILORS_COST"),
        ["light_ship"] = ("LIGHT_SHIP_COST", "LIGHT_SHIP_SAILORS_COST"),
        ["galley"] = ("GALLEY_COST", "GALLEY_SHIP_SAILORS_COST"),
        ["transport"] = ("TRANSPORT_COST", "TRANSPORT_SHIP_SAILORS_COST"),
        ["frigate"] = ("LIGHT_SHIP_COST", "LIGHT_SHIP_SAILORS_COST"),
    };

    private static readonly Dictionary<string, string> GroupCn = new(StringComparer.Ordinal)
    {
        ["western"] = "西方", ["eastern"] = "东方", ["ottoman"] = "奥斯曼", ["muslim"] = "穆斯林",
        ["indian"] = "印度", ["chinese"] = "中华", ["nomad"] = "游牧", ["african"] = "非洲",
        ["american"] = "美洲", ["aboriginal"] = "原住民", ["andean"] = "安第斯",
        ["sub_saharan"] = "撒哈拉以南", ["mesoamerican"] = "中美",
        ["north_american"] = "北美", ["south_american"] = "南美",
        ["high_american"] = "中美高地", ["central_american"] = "中美",
        ["nomad_group"] = "游牧", ["polynesian"] = "波利尼西亚",
        ["_universal"] = "通用（火炮）",
    };
    private static readonly Dictionary<string, string> GroupEn = new(StringComparer.Ordinal)
    {
        ["western"] = "Western", ["eastern"] = "Eastern", ["ottoman"] = "Ottoman",
        ["muslim"] = "Muslim", ["indian"] = "Indian", ["chinese"] = "Chinese",
        ["nomad"] = "Nomad", ["african"] = "African", ["american"] = "American",
        ["aboriginal"] = "Aboriginal", ["andean"] = "Andean", ["mesoamerican"] = "Mesoamerican",
        ["sub_saharan"] = "Sub-Saharan", ["north_american"] = "North American",
        ["south_american"] = "South American", ["high_american"] = "High American",
        ["central_american"] = "Central American", ["nomad_group"] = "Nomad",
        ["polynesian"] = "Polynesian", ["_universal"] = "Universal (Artillery)",
    };

    private static readonly Dictionary<string, string> CatCn = new(StringComparer.Ordinal)
    {
        ["ADM"] = "行政", ["DIP"] = "外交", ["MIL"] = "军事",
        ["national"] = "国家/文化", ["mechanics"] = "陆战机制",
        ["doctrine"] = "海军学说", ["naval_mechanics"] = "海军机制",
    };

    private static readonly Dictionary<string, string> ModCn = new(StringComparer.Ordinal)
    {
        ["global_manpower_modifier"] = "全球人力", ["land_morale"] = "陆军士气",
        ["naval_morale"] = "海军士气", ["discipline"] = "纪律",
        ["infantry_power"] = "步兵战斗力", ["cavalry_power"] = "骑兵战斗力",
        ["artillery_power"] = "炮兵战斗力", ["shock_damage"] = "冲击伤害",
        ["fire_damage"] = "火力伤害", ["siege_ability"] = "围城能力",
        ["defensiveness"] = "防御力", ["global_tax_modifier"] = "税收",
        ["production_efficiency"] = "生产效率", ["global_trade_power"] = "贸易力量",
        ["trade_steering"] = "贸易引导", ["trade_efficiency"] = "贸易效率",
        ["merchants"] = "商人", ["colonists"] = "殖民者", ["missionaries"] = "传教士",
        ["diplomats"] = "外交官", ["possible_policy"] = "可用政策",
        ["diplomatic_reputation"] = "外交声望", ["improve_relation_modifier"] = "改善关系",
        ["ae_impact"] = "侵略扩张", ["stability_cost_modifier"] = "稳定度花费",
        ["development_cost"] = "发展度花费", ["technology_cost"] = "科技花费",
        ["military_tech_cost_modifier"] = "军事科技花费",
        ["diplomatic_tech_cost_modifier"] = "外交科技花费",
        ["administrative_tech_cost_modifier"] = "行政科技花费",
        ["advisor_cost"] = "顾问花费", ["merc_maintenance_modifier"] = "雇佣兵维护",
        ["land_maintenance_modifier"] = "陆军维护", ["naval_maintenance_modifier"] = "海军维护",
        ["manpower_recovery_speed"] = "人力恢复", ["garrison_size"] = "驻军规模",
        ["global_unrest"] = "全国叛乱", ["unrest"] = "叛乱",
        ["tolerance_own"] = "国教容忍", ["tolerance_heretic"] = "异端容忍",
        ["tolerance_heathen"] = "异教容忍", ["legitimacy"] = "正统性",
        ["republican_tradition"] = "共和传统", ["devotion"] = "虔诚",
        ["meritocracy"] = "贤能值", ["horde_unity"] = "部落统一",
        ["yearly_absolutism"] = "年度专制", ["yearly_corruption"] = "年度腐败",
        ["corruption"] = "腐败", ["inflation"] = "通胀",
        ["inflation_reduction"] = "通胀降低", ["war_exhaustion"] = "战争疲惫",
        ["army_tradition"] = "陆军传统", ["navy_tradition"] = "海军传统",
        ["army_tradition_decay"] = "陆军传统衰减", ["navy_tradition_decay"] = "海军传统衰减",
        ["prestige"] = "威望", ["power_projection"] = "实力投射",
        ["global_autonomy"] = "自治度", ["local_autonomy"] = "本地自治度",
        ["stability"] = "稳定度", ["core_creation"] = "造核花费",
        ["hostile_attrition"] = "敌方损耗", ["attrition"] = "损耗",
        ["land_forcelimit"] = "陆军上限", ["naval_forcelimit"] = "海军上限",
        ["land_forcelimit_modifier"] = "陆军上限", ["naval_forcelimit_modifier"] = "海军上限",
        ["max_manpower"] = "最大人力", ["manpower"] = "人力",
        ["global_manpower"] = "人力", ["sailors"] = "水手",
        ["global_sailors"] = "水手", ["ship_cost"] = "船只花费",
        ["leader_land_shock"] = "陆军将领冲击", ["leader_land_fire"] = "陆军将领火力",
        ["leader_land_maneuver"] = "陆军将领机动", ["leader_siege"] = "将领围城",
        ["leader_naval_shock"] = "海军将领冲击", ["leader_naval_fire"] = "海军将领火力",
        ["leader_naval_maneuver"] = "海军将领机动", ["land_attrition"] = "陆军损耗",
        ["naval_attrition"] = "海军损耗", ["reinforce_speed"] = "补充速度",
        ["movement_speed"] = "移动速度", ["administrative_efficiency"] = "行政效率",
        ["governing_capacity"] = "治理容量", ["governing_capacity_modifier"] = "治理容量",
        ["state_maintenance_modifier"] = "州维护", ["build_cost"] = "建筑花费",
        ["build_time"] = "建筑时间", ["improve_development"] = "发展度",
        ["free_leader_slots"] = "免费将领槽位", ["num_of_pronoiars"] = "普罗尼亚",
        ["num_of_age_rewards"] = "时代奖励", ["may_explore"] = "可探索未知海域",
        ["may_perform_slave_raid_on_same_religion"] = "可袭击同宗教国家",
        ["own_coast_naval_combat_bonus"] = "近海战斗加成",
        ["blockade_efficiency"] = "封锁效率", ["privateer_efficiency"] = "私掠效率",
        ["trade_ship_power"] = "贸易船力量", ["global_ship_cost"] = "船只花费",
        ["heavy_ship_power"] = "重型船战斗力", ["light_ship_power"] = "轻型船战斗力",
        ["galley_power"] = "桨帆船战斗力", ["cav_to_inf_ratio"] = "骑兵比例",
        ["cavalry_cost"] = "骑兵花费", ["general_cost"] = "将领花费",
        ["fabricate_claims_cost"] = "制造宣称花费",
        ["unjustified_demands"] = "不正当要求",
        ["global_prov_trade_power_modifier"] = "省份贸易力量",
        ["mercenary_cost"] = "雇佣兵花费", ["global_regiment_cost"] = "团花费",
        ["monthly_reform_progress"] = "月度改革进度",
        ["global_trade_goods_size_modifier"] = "贸易品规模",
        ["caravan_power"] = "商队力量", ["religious_unity"] = "宗教统一",
        ["num_of_accepted_cultures"] = "可接受文化数",
        ["num_of_generals"] = "将领数", ["num_of_admirals"] = "海军将领数",
        ["num_of_colonists"] = "殖民者数", ["num_of_explorers"] = "探险家数",
        ["num_of_ports"] = "港口数", ["num_of_merchants"] = "商人团数",
        ["num_of_diplomats"] = "外交官数", ["reform_level"] = "改革等级",
        ["crown_land_share"] = "王室领地份额", ["legitimacy_equivalent"] = "合法性等价",
        ["manpower_percentage"] = "人力百分比", ["share_of_starting_income"] = "起始收入占比",
        ["monthly_mil"] = "每月军事点数", ["monthly_adm"] = "每月行政点数",
        ["monthly_dip"] = "每月外交点数", ["treasury"] = "国库",
        ["monthly_income"] = "月收入", ["owns_core_province"] = "拥有核心省份",
        ["owns_or_non_sovereign_subject_of"] = "拥有或为其非主权附属",
        ["owns"] = "拥有省份", ["trader"] = "商人", ["temple"] = "神庙",
    };

    private static readonly HashSet<string> FlatMods = new(StringComparer.Ordinal)
    {
        "yearly_absolutism", "yearly_devotion", "yearly_legitimacy",
        "yearly_republican_tradition", "yearly_corruption",
        "yearly_army_professionalism", "army_tradition_decay",
        "navy_tradition_decay", "war_exhaustion", "inflation", "monthly_war_exhaustion",
        "num_of_age_rewards", "num_of_pronoiars",
    };

    private static readonly Dictionary<string, (string Zh, string En)> SpecialYes = new(StringComparer.Ordinal)
    {
        ["may_explore"] = ("可探索未知海域", "Cannot explore"),
        ["may_perform_slave_raid_on_same_religion"] = ("可袭击同宗教国家", "Slave raids"),
    };

    private static readonly (string Key, string Cn, string En, string DCn, string DEn, string ECn, string EEn)[] ArmyGuide =
    {
        ("army_frontline", "战斗宽度与战线", "Combat width & front line",
            "前排由步兵与骑兵接敌，后排炮兵输出；战斗宽度随军事科技提升，超宽部队无法参战。",
            "Infantry and cavalry hold the front while artillery fires from the back. Combat width grows with military technology; excess regiments cannot engage.",
            "前排：步兵/骑兵；后排：炮兵；宽度由军事科技决定",
            "Front: infantry/cavalry; back: artillery; width grows with military tech"),
        ("army_phases", "火力与冲击阶段", "Fire & shock phases",
            "每场陆战分火力、冲击两个阶段。火力阶段看火力点数与炮兵，冲击阶段看冲击点数与骑兵。",
            "Land battles alternate fire and shock phases. Fire favors fire pips and artillery; shock favors shock pips and cavalry.",
            "火力阶段：火力点数、炮兵；冲击阶段：冲击点数、骑兵",
            "Fire phase: fire pips and artillery; shock phase: shock pips and cavalry"),
        ("army_tactics", "战术与骰子", "Tactics & dice",
            "双方将领比较机动，决定战术与骰子修正；战术等级降低受到的伤害，地形与渡河再叠加修正。",
            "Leader maneuver decides tactics and dice modifiers. Higher tactics reduce damage taken; terrain and river crossings add further modifiers.",
            "将领机动影响战术；战术降低伤亡；地形/渡河影响骰子",
            "Maneuver affects tactics; tactics reduce casualties; terrain and rivers modify rolls"),
        ("army_terrain", "地形与渡河", "Terrain & river crossings",
            "山地、森林、沼泽、丘陵提供防御加成；渡河、登陆、跨越海峡会带来进攻惩罚。",
            "Mountains, forests, marshes and hills favor the defender; river crossing, naval landing and strait crossing penalize the attacker.",
            "防御地形：山地/森林/沼泽/丘陵；进攻惩罚：渡河/登陆",
            "Defensive terrain: mountains/forests/marshes/hills; attacker penalty: rivers/landings"),
        ("army_composition", "步骑炮编成", "Army composition",
            "步兵填满前排，骑兵保护两翼，炮兵放后排。骑兵比例受科技与政府限制，超出部分不参与战斗。",
            "Infantry fills the front, cavalry protects flanks, artillery occupies the back. Cavalry ratio is limited by tech and government; excess cavalry does not fight.",
            "步兵：前排主力；骑兵：侧翼与冲击；炮兵：后排火力",
            "Infantry: front line; cavalry: flanks and shock; artillery: back-row fire"),
        ("army_attrition", "损耗与补给", "Attrition & supply",
            "部队在补给不足、敌军领地、恶劣地形或围城时产生损耗；补给上限随发展度与建筑提高。",
            "Attrition rises from low supply, enemy territory, harsh terrain and sieges. Supply limit improves with development and buildings.",
            "损耗来源：缺补给/敌境/恶劣地形/围城；提高补给：发展度与建筑",
            "Attrition: low supply, enemy land, harsh terrain, sieges; improve supply with development and buildings"),
        ("army_morale", "士气与纪律", "Morale & discipline",
            "士气决定部队能撑多久，纪律直接增减造成的伤害；士气耗尽部队退出战斗并撤退。",
            "Morale determines how long regiments stay in battle; discipline directly modifies damage dealt and taken. A regiment at zero morale retreats.",
            "士气：战斗持续时间；纪律：伤害增减；士气归零则撤退",
            "Morale: battle endurance; discipline: damage modifier; zero morale retreats"),
        ("army_tech", "军事科技与兵种", "Military tech & units",
            "提升军事科技解锁新兵种、提高战斗宽度与战术；新兵种通常拥有更高的火力/冲击点数。",
            "Military tech unlocks new unit types, raises combat width and tactics. Newer units usually carry higher fire/shock pips.",
            "军事科技解锁兵种；科技提升宽度与战术",
            "Military tech unlocks units and raises width and tactics"),
    };

    private static readonly (string Key, string Cn, string En, string DCn, string DEn, string ECn, string EEn)[] NavyGuide =
    {
        ("navy_ship_roles", "舰种定位", "Ship roles",
            "重型船负责主力海战，轻型船负责贸易与私掠，桨帆船在近海有加成，运输船用于运兵与登陆。",
            "Heavy ships win fleet battles, light ships protect trade and privateer, galleys excel in inland seas, transports move and land armies.",
            "重型船：主力海战；轻型船：贸易/私掠；桨帆船：近海；运输船：运兵",
            "Heavy: battle; light: trade/privateer; galley: inland seas; transport: armies"),
        ("navy_phases", "海战流程", "Naval combat",
            "海战同样分火力与冲击阶段；船体、火炮、海军士气与将领点数共同决定伤害与沉船。",
            "Naval battles alternate fire and shock. Hull, cannons, naval morale and leader pips decide damage and losses.",
            "火力/冲击阶段；船体与火炮决定伤害",
            "Fire/shock phases; hull and cannons decide damage"),
        ("navy_tradition", "海军传统与将领", "Naval tradition & leaders",
            "海军传统提高海军士气与战斗表现；海军将领的火力、冲击、机动决定舰队上限。",
            "Naval tradition improves morale and performance; admiral fire, shock and maneuver set the fleet's ceiling.",
            "海军传统：士气与战斗；将领：火力/冲击/机动",
            "Tradition: morale and combat; admiral: fire/shock/maneuver"),
        ("navy_blockade", "封锁与贸易", "Blockade & trade",
            "舰队封锁敌方港口会降低其税收与贸易，并提高战争分数；轻型船保护贸易可增加贸易收入。",
            "Blockading enemy ports cuts their tax and trade and adds war score; light ships protecting trade increase trade income.",
            "封锁港口：削弱税收/贸易；保护贸易：增加收入",
            "Blockade: cut tax/trade; protect trade: raise income"),
        ("navy_maintenance", "维护与补给", "Maintenance & supply",
            "舰队在海上会消耗维护费与水手；远离己方港口、恶劣海况会增加损耗。",
            "Fleets cost maintenance and sailors at sea; distance from friendly ports and rough seas increase attrition.",
            "海上维护：金币/水手；远海与恶劣海况增加损耗",
            "Sea maintenance: ducats/sailors; distant or rough waters add attrition"),
        ("navy_landing", "登陆与运输", "Landing & transport",
            "运输船把陆军送到敌方海岸；登陆作战会承受进攻惩罚，需先取得制海权。",
            "Transports deliver armies to enemy coasts; naval landings suffer an attacker penalty and require naval supremacy.",
            "运输船装载陆军；登陆承受惩罚；需要制海权",
            "Transports carry armies; landings take a penalty; naval supremacy required"),
        ("navy_flagship", "旗舰", "Flagship",
            "旗舰提供全舰队光环加成；旗舰被击沉会带来额外士气与威望损失。",
            "A flagship grants fleet-wide auras; losing it costs extra morale and prestige.",
            "旗舰：全舰队光环；沉没：额外损失",
            "Flagship: fleet auras; sinking: extra losses"),
        ("navy_doctrine", "海军学说选择", "Naval doctrines",
            "海军学说提供长期国家修正，按玩法选择贸易、近海或主力舰队方向；同时只能启用有限数量。",
            "Naval doctrines give permanent country modifiers; choose trade, coastal or battle-fleet focus. Only a limited number can be active.",
            "学说：长期国家修正；按玩法选择方向",
            "Doctrines: permanent modifiers; choose a playstyle"),
    };

    // 汉化 mod 没有覆盖的修正/条件名词，按英文文案兜底
    private static readonly Dictionary<string, string> ModEnCn = new(StringComparer.Ordinal)
    {
        ["leader land maneuver"] = "陆军将领机动",
        ["leader naval maneuver"] = "海军将领机动",
        ["light ship cost"] = "轻型战舰花费",
        ["heavy ship cost"] = "重型战舰花费",
        ["sailor maintenance modifier"] = "水手维护费",
        ["global colonial growth"] = "全球殖民增长",
        ["global sailors modifier"] = "全局水手修正",
        ["trade range modifier"] = "贸易范围修正",
        ["global regiment recruit speed"] = "全局陆军招募速度",
        ["movement speed in fleet modifier"] = "舰队移动速度修正",
        ["global spy defence"] = "全局间谍防御",
        ["range"] = "射程",
        ["global ship recruit speed"] = "全球船只招募速度",
        ["sailors recovery speed"] = "水手恢复速度",
        ["landing penalty"] = "登陆惩罚",
        ["loyalty change on revoked"] = "撤销时忠诚变化",
        ["global tariffs"] = "全局关税",
        ["reinforce cost modifier"] = "补充成本修正",
        ["shock damage received"] = "所受冲击伤害",
        ["yearly religion organization"] = "年度宗教组织度",
        ["merc leader army tradition"] = "雇佣兵将领陆军传统",
        ["global prosperity growth"] = "全球繁荣度增长",
        ["state governing cost"] = "州治理成本",
        ["justify trade conflict cost"] = "制造贸易冲突花费",
        ["inward perfection"] = "内圣之道",
        ["fire damage received"] = "所受火力伤害",
        ["morale damage received"] = "所受士气打击",
        ["regiment disembark speed"] = "军团登陆速度",
        ["imperial mandate"] = "天命",
        ["num of trade companies"] = "贸易公司数量",
        ["num accepted cultures"] = "已接受文化数",
        ["num of owned and controlled institutions"] = "拥有并控制的思潮数",
    };

    private static readonly Dictionary<string, (string Zh, string En)> Trig = new(StringComparer.Ordinal)
    {
        ["army_size_percentage"] = ("陆军规模达到陆军上限的 {}%", "Army size at {}% of force limit"),
        ["navy_size_percentage"] = ("海军规模达到海军上限的 {}%", "Navy size at {}% of force limit"),
        ["army_size"] = ("陆军规模达到 {}", "Army size of {}"),
        ["navy_size"] = ("海军规模达到 {}", "Navy size of {}"),
        ["grown_by_development"] = ("发展度增长 {}", "Grow development by {}"),
        ["grown_by_states"] = ("州数增长 {}", "Grow states by {}"),
        ["total_development"] = ("总发展度达到 {}", "Total development of {}"),
        ["num_of_allies"] = ("盟友数 {}", "Allies: {}"),
        ["num_of_vassals"] = ("附庸数 {}", "Vassals: {}"),
        ["vassal"] = ("附庸：{}", "Vassal: {}"),
        ["num_of_heavy_ship"] = ("重型船数量 {}", "Heavy ships: {}"),
        ["num_of_light_ship"] = ("轻型船数量 {}", "Light ships: {}"),
        ["num_of_galley"] = ("桨帆船数量 {}", "Galleys: {}"),
        ["num_of_transport"] = ("运输船数量 {}", "Transports: {}"),
        ["num_of_ship"] = ("舰船数量 {}", "Ships: {}"),
        ["num_of_continents"] = ("拥有领地的大洲数 {}", "Continents: {}"),
        ["has_institution"] = ("已接受思潮：{}", "Institution embraced: {}"),
        ["full_idea_group"] = ("完成理念组：{}", "Completed idea group: {}"),
        ["religion"] = ("宗教：{}", "Religion: {}"),
        ["religion_group"] = ("宗教组：{}", "Religion group: {}"),
        ["culture_group"] = ("文化组：{}", "Culture group: {}"),
        ["tag"] = ("国家：{}", "Country: {}"),
        ["government"] = ("政体：{}", "Government: {}"),
        ["government_rank"] = ("政府等级 {}", "Government rank {}"),
    };

    private static readonly Dictionary<string, string> EffCn = new(StringComparer.Ordinal)
    {
        ["add_prestige"] = "威望", ["add_army_tradition"] = "陆军传统",
        ["add_navy_tradition"] = "海军传统", ["add_adm_power"] = "行政点数",
        ["add_dip_power"] = "外交点数", ["add_mil_power"] = "军事点数",
        ["add_manpower"] = "人力", ["add_years_of_income"] = "年收入",
        ["add_base_tax"] = "基础税收", ["add_base_production"] = "基础生产",
        ["add_base_manpower"] = "基础人力", ["add_stability"] = "稳定度",
        ["add_legitimacy"] = "正统性", ["add_republican_tradition"] = "共和传统",
        ["add_mercantilism"] = "重商主义", ["add_inflation"] = "通胀",
        ["add_war_exhaustion"] = "战争疲惫", ["add_absolutism"] = "专制",
        ["add_power_projection"] = "实力投射", ["add_splendor"] = "辉煌",
        ["add_monarch_power"] = "君主点数", ["add_devotion"] = "虔诚",
        ["add_army_professionalism"] = "陆军职业度",
    };

    private static readonly Dictionary<string, string> TipCn = new(StringComparer.Ordinal)
    {
        ["SEPERATOR TOOLTIP"] = "",
        ["SEPARATOR TOOLTIP"] = "",
        ["Alternatively:"] = "或者：",
        ["This mission cannot be completed as it is a Branching Mission."] = "分支任务，无法直接完成。",
        ["This is a Branching Mission. Complete 'Our Religious Direction' Mission to preview each branch."] =
            "这是分支任务，完成「Our Religious Direction」任务后可预览各分支。",
        ["This Power Projection is permanent."] = "此次威望投射为永久。",
        ["The effects of this mission depend on future paths of Branching Missions."] =
            "该任务的效果取决于后续分支任务的选择。",
        ["This modifier gets upgraded by later missions."] = "该修正会由后续任务升级。",
        ["This mission grants a Dynamic Reward based on how we complete it"] =
            "该任务会根据完成方式给予动态奖励。",
        ["gains a claim on every Neighboring Province."] = "获得对所有相邻省份的宣称",
        ["gains a permanent claim on every Neighboring Province."] = "获得对所有相邻省份的永久宣称",
        ["gains a permanent claim on every Neighboring Area."] = "获得对所有相邻地区的永久宣称",
        ["gains a permanent claim on every province in our Home Region."] = "获得本国所在地区所有省份的永久宣称",
    };

    private static readonly Dictionary<string, string> TipLbl = new(StringComparer.Ordinal)
    {
        ["Num Of Generals"] = "将领数", ["Num Of Admirals"] = "海军将领数",
        ["Num Of Colonists"] = "殖民者数", ["Num Of Explorers"] = "探险家数",
        ["Num Of Ports"] = "港口数", ["Num Of Merchants"] = "商人团数",
        ["Num Of Diplomats"] = "外交官数", ["Reform Level"] = "改革等级",
        ["Crown Land Share"] = "王室领地份额", ["Legitimacy Equivalent"] = "合法性等价",
        ["Manpower Percentage"] = "人力百分比", ["Share Of Starting Income"] = "起始收入占比",
        ["Monthly Mil"] = "每月军事点数", ["Monthly Adm"] = "每月行政点数",
        ["Monthly Dip"] = "每月外交点数", ["Treasury"] = "国库",
        ["Monthly Income"] = "月收入", ["Owns Core Province"] = "拥有核心省份",
        ["Owns Or Non Sovereign Subject Of"] = "拥有或为其非主权附属",
        ["Owns"] = "拥有省份", ["Trader"] = "商人", ["Temple"] = "神庙",
    };

    private static readonly HashSet<string> TreePrefix = new(StringComparer.Ordinal)
    {
        "AFR", "EMP", "DH", "WOC", "SCA", "KOK", "MND", "DOM", "GC",
        "RB", "TR", "LVT", "PP", "EOC",
    };

    private static readonly HashSet<string> TreeSkip = new(StringComparer.Ordinal)
    {
        "zz", "zzz", "zzzz", "zzzzz", "flavor", "shared", "and", "of", "the",
    };

    private static readonly Dictionary<string, string> TreeWords = new(StringComparer.Ordinal)
    {
        ["generic"] = "通用", ["european"] = "欧洲", ["african"] = "非洲", ["asian"] = "亚洲",
        ["central"] = "中部", ["east"] = "东部", ["west"] = "西部", ["north"] = "北部",
        ["south"] = "南部", ["horn"] = "之角", ["natives"] = "原住民", ["native"] = "原住民",
        ["northeastern"] = "东北部", ["northwestern"] = "西北部",
        ["southeastern"] = "东南部", ["southwestern"] = "西南部",
        ["new"] = "新", ["world"] = "世界", ["spice"] = "香料", ["islands"] = "群岛",
        ["minor"] = "小国", ["minors"] = "小国", ["pirate"] = "海盗", ["pirates"] = "海盗",
        ["hansa"] = "汉萨", ["mamluk"] = "马穆鲁克", ["mamluks"] = "马穆鲁克",
        ["hannoverian"] = "汉诺威", ["iroquoians"] = "易洛魁", ["philippine"] = "菲律宾",
        ["gotlandish"] = "哥特兰", ["trapezunite"] = "特拉比松",
        ["styriantyrol"] = "施蒂利亚-蒂罗尔", ["savoyard"] = "萨伏依",
        ["piedmontese"] = "皮埃蒙特", ["fla"] = "佛兰德", ["brb"] = "布拉班特",
        ["brb fla"] = "布拉班特/佛兰德", ["usa"] = "美国",
        ["daimyo"] = "大名", ["britain"] = "不列颠", ["california"] = "加利福尼亚",
        ["dreamtime"] = "梦幻时代", ["high"] = "高原", ["tags"] = "国家",
    };

    // ------------------------------------------------------------ 运行期状态

    private static readonly object StateLock = new();
    private static readonly Dictionary<string, JsonObject> MissionTrees = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _enCn = new(StringComparer.Ordinal);

    // ------------------------------------------------------------ 入口

    /// <summary>读取（并缓存）欧陆百科；找不到本体目录时返回旧版同形空结构。</summary>
    public static JsonObject Load(string gameRoot, string uiLang, bool force)
    {
        uiLang = string.IsNullOrWhiteSpace(uiLang) ? "zh" : uiLang.Trim().ToLowerInvariant();
        var root = GameRootOf(gameRoot);
        if (root.Length == 0)
            return new JsonObject
            {
                ["ok"] = false,
                ["supported"] = true,
                ["game"] = "eu4",
                ["error"] = "没找到《欧陆风云 IV》本体目录（common/ideas），请在设置里确认游戏根目录",
            };

        var lang = LangMap.TryGetValue(uiLang, out var l) ? l : "english";
        var (locDir, locLang) = LocDir(root, lang);
        var zh = uiLang == "zh";
        var modDirs = zh ? LocModDirs(root) : new List<string>();
        var tag = zh ? "zh" : locLang;

        var sigPaths = new List<string>
        {
            Path.Combine(root, D_IDEA), Path.Combine(root, D_UNIT), Path.Combine(root, D_TECH),
            Path.Combine(root, "common", "defines.lua"), Path.Combine(root, D_AGE),
            Path.Combine(root, D_DOCTRINE), Path.Combine(root, D_MISS),
            Path.Combine(root, D_LOC), Path.Combine(root, "interface"),
        };
        sigPaths.AddRange(modDirs);
        var sig = E4Ver + "|" + locLang + "|" + tag + "|" + Sig(sigPaths);

        long seconds = 0;
        var data = Wiki.Cached("eu4", tag, root, sig, force, () =>
        {
            var t0 = Environment.TickCount64;
            var d = Build(root, locDir, locLang, zh, modDirs);
            seconds = Environment.TickCount64 - t0;
            return d;
        }, out var cached);
        if (!cached) data["buildSeconds"] = Math.Round(seconds / 1000.0, 1);

        // 完整任务树留内存索引，响应只给 missionTrees 瘦身列表（与旧版一致）
        if (data["trees"] is JsonObject fullTrees)
        {
            lock (StateLock)
            {
                MissionTrees.Clear();
                foreach (var (id, t) in fullTrees)
                    if (t is JsonObject to) MissionTrees[id] = to;
            }
            var slim = new JsonArray();
            foreach (var (_, t) in fullTrees.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (t is not JsonObject to) continue;
                slim.Add(CloneSlim(to));
            }
            data["missionTrees"] = slim;
            data.Remove("trees");
        }
        return data;
    }

    /// <summary>单棵任务树的完整节点。</summary>
    public static JsonObject MissionTree(string gameRoot, string tid, string uiLang)
    {
        JsonObject? t;
        lock (StateLock) MissionTrees.TryGetValue((tid ?? "").Trim(), out t);
        if (t is null)
        {
            try { Load(gameRoot, uiLang, false); } catch { /* 冷启动构建失败按 not found 处理 */ }
            lock (StateLock) MissionTrees.TryGetValue((tid ?? "").Trim(), out t);
        }
        if (t is null) return new JsonObject { ["ok"] = false, ["error"] = "not found" };
        t["ok"] = true;
        return t;
    }

    private static JsonObject CloneSlim(JsonObject tree)
    {
        var slim = new JsonObject();
        foreach (var key in new[] { "id", "name", "n", "default" })
            if (tree[key] is { } v) slim[key] = v.DeepClone();
        return slim;
    }

    private static string GameRootOf(string hint)
    {
        hint = (hint ?? "").Trim().Trim('"');
        var cands = new List<string>();
        if (hint.Length > 0) cands.Add(hint);
        foreach (var d in "CDEFG")
        {
            cands.Add(d + ":\\steam\\steamapps\\common\\Europa Universalis IV");
            cands.Add(d + ":\\SteamLibrary\\steamapps\\common\\Europa Universalis IV");
            cands.Add(d + ":\\Games\\Europa Universalis IV");
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in cands)
        {
            var c = (raw ?? "").TrimEnd('\\', '/');
            if (c.Length == 0 || !seen.Add(c)) continue;
            if (Directory.Exists(Path.Combine(c, D_IDEA)) &&
                (File.Exists(Path.Combine(c, "eu4.exe")) || Directory.Exists(Path.Combine(c, D_MISS))))
                return c;
        }
        return "";
    }

    /// <summary>缓存签名：只取目录 mtime + 条目数，避免每次请求遍历全部脚本。</summary>
    private static string Sig(IEnumerable<string> paths)
    {
        var parts = new List<string>();
        foreach (var p in paths)
        {
            try
            {
                var isDir = Directory.Exists(p);
                var mtime = new DateTimeOffset(isDir
                    ? Directory.GetLastWriteTimeUtc(p) : File.GetLastWriteTimeUtc(p)).ToUnixTimeSeconds();
                var size = isDir
                    ? Directory.EnumerateFileSystemEntries(p).Count() : new FileInfo(p).Length;
                parts.Add($"{Path.GetFileName(p.TrimEnd('\\', '/'))}:{mtime}:{size}");
            }
            catch { parts.Add("-"); }
        }
        return string.Join("|", parts);
    }

    private static string GameVersion(string root)
    {
        try
        {
            var p = Path.Combine(root, "launcher-settings.json");
            if (!File.Exists(p)) return "";
            var blob = JsonNode.Parse(File.ReadAllText(p, Encoding.UTF8)) as JsonObject;
            return blob?["version"]?.ToString() ?? blob?["rawVersion"]?.ToString() ?? "";
        }
        catch { return ""; }
    }

    // ------------------------------------------------------------ 基础解析

    private static string Unquote(string? s)
    {
        s = (s ?? "").Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') return s[1..^1];
        return s;
    }

    /// <summary>读脚本：去掉行内 # 注释（引号内不算）。</summary>
    private static string ReadEu4(string path)
    {
        string text;
        try { text = File.ReadAllText(path, Encoding.UTF8); }
        catch { return ""; }
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        var sb = new StringBuilder(text.Length);
        foreach (var line in text.Split('\n'))
        {
            var buf = new StringBuilder();
            var q = false;
            foreach (var ch in line)
            {
                if (ch == '"') { q = !q; buf.Append(ch); }
                else if (ch == '#' && !q) break;
                else buf.Append(ch);
            }
            sb.Append(buf).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>把脚本体拆成 (key, value, isBlock)，value 为块内文本或标量。</summary>
    private static List<(string Key, string Val, bool IsBlock)> Entries(string? text)
    {
        var outp = new List<(string, string, bool)>();
        text ??= "";
        int i = 0, n = text.Length;
        while (i < n)
        {
            var ch = text[i];
            if (ch is ' ' or '\t' or '\r' or '\n' or '}' or '=') { i++; continue; }
            var start = i;
            while (i < n && "= \t\r\n{}".IndexOf(text[i]) < 0) i++;
            var key = Unquote(text.Substring(start, i - start));
            if (key.Length == 0) { i++; continue; }
            while (i < n && " \t\r\n".IndexOf(text[i]) >= 0) i++;
            if (i >= n || text[i] != '=') continue;
            i++;
            while (i < n && " \t\r\n".IndexOf(text[i]) >= 0) i++;
            if (i < n && text[i] == '{')
            {
                int depth = 1, j = i + 1;
                var q = false;
                while (j < n)
                {
                    var c = text[j];
                    if (c == '"') q = !q;
                    else if (!q)
                    {
                        if (c == '{') depth++;
                        else if (c == '}')
                        {
                            depth--;
                            if (depth == 0) break;
                        }
                    }
                    j++;
                }
                outp.Add((key, text.Substring(i + 1, Math.Max(0, j - (i + 1))), true));
                i = j + 1;
            }
            else
            {
                var st = i;
                var q = false;
                while (i < n)
                {
                    var c = text[i];
                    if (c == '"') q = !q;
                    if (!q && (c == '\r' || c == '\n')) break;
                    i++;
                }
                outp.Add((key, Unquote(text.Substring(st, i - st)), false));
            }
        }
        return outp;
    }

    private static List<(string Key, string Val)> Blocks(string? text) =>
        Entries(text).Where(e => e.IsBlock).Select(e => (e.Key, e.Val)).ToList();

    private static string Sub(string? body, string key)
    {
        foreach (var e in Entries(body))
            if (e.Key == key && e.IsBlock) return e.Val;
        return "";
    }

    private static string Scalar(string? body, string key, string def = "")
    {
        foreach (var e in Entries(body))
            if (e.Key == key && !e.IsBlock) return e.Val;
        return def;
    }

    private static List<string> AllScalars(string? body, string key) =>
        Entries(body).Where(e => e.Key == key && !e.IsBlock).Select(e => e.Val).ToList();

    private static List<string> ListOf(string? body, string key)
    {
        var b = Sub(body, key);
        if (b.Length == 0) return new List<string>();
        return Regex.Split(b.Trim(), @"[\s,]+")
            .Where(x => x.Length > 0 && x != "{" && x != "}").ToList();
    }

    private static string Pretty(string? key)
    {
        var s = (key ?? "").Trim();
        s = Regex.Replace(s, "_(ideas|mission|missions|desc|title)$", "", RegexOptions.IgnoreCase);
        s = s.Replace("_", " ").Trim();
        if (s.Length == 0) return key ?? "";
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length > 0 && w == w.ToLowerInvariant()
                ? char.ToUpperInvariant(w[0]) + w[1..] : w);
        return string.Join(" ", words);
    }

    /// <summary>带符号数值：+3 / 0 / +0.5（去掉多余的 0）。</summary>
    private static string NumText(double f)
    {
        if (Math.Abs(f - Math.Round(f)) < 1e-9) return f == 0 ? "0" : ((int)Math.Round(f)).ToString("+0;-0");
        return (f >= 0 ? "+" : "") + f.ToString("0.00", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
    }

    /// <summary>成本数字：10.0 → 10，0.5 → 0.5，无效 → —。</summary>
    private static string Cost(double? v)
    {
        if (v is null) return "—";
        var f = v.Value;
        if (Math.Abs(f - Math.Round(f)) < 1e-9) return ((long)Math.Round(f)).ToString(CultureInfo.InvariantCulture);
        return f.ToString("0.00", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
    }

    /// <summary>读 common/defines.lua 里的数值键（招募成本用）。</summary>
    private static Dictionary<string, double> Defines(string root)
    {
        var outp = new Dictionary<string, double>(StringComparer.Ordinal);
        var path = Path.Combine(root, "common", "defines.lua");
        try
        {
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                var m = Regex.Match(line, @"^\s*([A-Za-z0-9_]+)\s*=\s*([-+]?[0-9.]+)");
                if (!m.Success) continue;
                if (double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    outp[m.Groups[1].Value] = d;
            }
        }
        catch { }
        return outp;
    }

    private static bool IsYes(string? v) =>
        (v ?? "").Trim().ToLowerInvariant() is "yes" or "true";

    private static bool IsNo(string? v) =>
        (v ?? "").Trim().ToLowerInvariant() is "no" or "false";

    private static string? ModValue(string key, string v, bool zh)
    {
        if (IsYes(v))
        {
            if (SpecialYes.TryGetValue(key, out var sp)) return zh ? sp.Zh : sp.En;
            return zh ? "已启用" : "Enabled";
        }
        if (IsNo(v)) return SpecialYes.ContainsKey(key) ? null : (zh ? "已停用" : "Disabled");
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
            return v.Length > 0 ? v : null;
        if (!FlatMods.Contains(key) && Math.Abs(f) <= 1 && Math.Abs(f - Math.Round(f)) > 1e-9)
        {
            var pct = f * 100;
            if (Math.Abs(pct - Math.Round(pct)) < 0.05)
                return ((int)Math.Round(pct)).ToString("+0;-0", CultureInfo.InvariantCulture) + "%";
            return (pct >= 0 ? "+" : "") + pct.ToString("0.0", CultureInfo.InvariantCulture)
                .TrimEnd('0').TrimEnd('.') + "%";
        }
        return NumText(f);
    }

    private static bool HasCjk(string? s) => Regex.IsMatch(s ?? "", "[\u4e00-\u9fff]");

    private static string NormMod(string? s)
    {
        s = (s ?? "").ToLowerInvariant();
        s = s.Replace("modifer", "modifier").Replace("recieved", "received").Replace("manuever", "maneuver");
        s = Regex.Replace(s, "[^0-9a-z%+\\-]+", " ");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// <summary>英文文案 → 汉化文案的反查表（键名拼写常有出入时兜底）。</summary>
    private static Dictionary<string, string> BuildEnCn(Dictionary<string, string> enLoc, Dictionary<string, string> loc)
    {
        var rev = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in enLoc)
        {
            if (!loc.TryGetValue(k, out var z) || z == v || v.Length == 0 || v.Length > 60) continue;
            var nk = NormMod(v);
            if (nk.Length > 0) rev.TryAdd(nk, z);
        }
        return rev;
    }

    private static string ModLabel(string key, Dictionary<string, string> loc, bool zh)
    {
        if (zh && ModCn.TryGetValue(key, out var direct)) return direct;
        var v = "";
        foreach (var k in new[] { key, "MODIFIER_" + key.ToUpperInvariant(), key.ToUpperInvariant() })
        {
            if (!loc.TryGetValue(k, out var t) || t.Length == 0) continue;
            if (!zh) return t;
            if (HasCjk(t)) return t;
            if (v.Length == 0) v = t;
        }
        if (zh)
        {
            foreach (var src in new[] { v, Pretty(key) })
            {
                if (src.Length == 0) continue;
                var nm = NormMod(src);
                if (ModEnCn.TryGetValue(nm, out var t) || _enCn.TryGetValue(nm, out t)) return t;
                const string sfx = " modifier";
                if (nm.EndsWith(sfx, StringComparison.Ordinal) &&
                    _enCn.TryGetValue(nm[..^sfx.Length], out var t2)) return t2;
            }
        }
        return v.Length > 0 ? v : Pretty(key);
    }

    private static List<string> ModLines(string? body, Dictionary<string, string> loc, bool zh, int limit = 0)
    {
        var outp = new List<string>();
        foreach (var e in Entries(body))
        {
            if (e.IsBlock) continue;
            var k = e.Key;
            if (k is "trigger" or "ai_will_do" or "effect" or "free" or "category"
                or "monarch_power" or "potential" or "important" or "removed_effect") continue;
            var val = ModValue(k, e.Val, zh);
            if (val is null) continue;
            outp.Add(ModLabel(k, loc, zh) + " " + val);
            if (limit > 0 && outp.Count >= limit) break;
        }
        return outp;
    }

    private static string Tip(string? s, bool zh)
    {
        if (string.IsNullOrEmpty(s) || !zh) return s ?? "";
        if (TipCn.TryGetValue(s, out var t)) return t;
        if (s.EndsWith(" Empty", StringComparison.Ordinal)) return "";
        var m = Regex.Match(s, @"^This is a Branching Mission\. Complete (.+?) Mission to preview each branch\.$");
        if (m.Success) return "这是分支任务。完成「" + m.Groups[1].Value + "」任务后可预览各分支。";
        m = Regex.Match(s, @"^If chooses (.+?) Path, the following effects will occur:$");
        if (m.Success) return "若选择「" + m.Groups[1].Value + "」路线，将发生以下效果：";
        m = Regex.Match(s, "^(Stability|Religious Unity|Prestige|Reform Level|Legitimacy Equivalent|" +
                           "Crown Land Share|Num Of Generals|Num Of Admirals|Manpower Percentage)：(.+)$");
        if (m.Success)
        {
            var cn = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Stability"] = "稳定度", ["Religious Unity"] = "宗教统一", ["Prestige"] = "威望",
                ["Reform Level"] = "改革等级", ["Legitimacy Equivalent"] = "合法性等价",
                ["Crown Land Share"] = "王室领地份额", ["Num Of Generals"] = "将领数",
                ["Num Of Admirals"] = "海军将领数", ["Manpower Percentage"] = "人力百分比",
            };
            return cn[m.Groups[1].Value] + "：" + m.Groups[2].Value;
        }
        m = Regex.Match(s, "^([A-Za-z][A-Za-z ]*)：(.+)$");
        if (m.Success && TipLbl.TryGetValue(m.Groups[1].Value, out var lbl))
            return lbl + "：" + m.Groups[2].Value;
        if (Regex.IsMatch(s, "^[A-Z]{2,4} This Is A Branching Mission Always = No$")) return "这是分支任务";
        return s;
    }

    private static List<string> TriggerLines(string? body, Dictionary<string, string> loc, bool zh, int limit = 0)
    {
        var outp = new List<string>();
        foreach (var e in Entries(body))
        {
            var k = e.Key;
            var v = e.Val;
            if (e.IsBlock)
            {
                if (k == "custom_trigger_tooltip")
                {
                    var tip = Scalar(v, "tooltip");
                    if (tip.Length > 0)
                    {
                        var x = Tip(loc.TryGetValue(tip, out var tv) ? tv : Pretty(tip), zh);
                        if (x.Length > 0) outp.Add(x);
                    }
                }
                else if (k == "calc_true_if")
                {
                    var amt = Scalar(v, "amount");
                    if (amt.Length > 0)
                        outp.Add(zh ? $"满足其中 {amt} 项条件" : $"Meet {amt} of the conditions");
                }
                continue;
            }
            if (k is "limit" or "scope" or "who" or "tooltip") continue;
            if (Trig.TryGetValue(k, out var trig))
            {
                var val = v;
                if (k is "has_institution" or "full_idea_group" or "religion" or "religion_group"
                    or "culture_group" or "tag" or "government")
                    val = loc.TryGetValue(val, out var lv) ? lv : Pretty(val);
                else if (k.EndsWith("percentage", StringComparison.Ordinal))
                {
                    if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                        val = (d * 100).ToString("G6", CultureInfo.InvariantCulture);
                }
                outp.Add((zh ? trig.Zh : trig.En).Replace("{}", val));
            }
            else if (Regex.IsMatch(v, @"^[+-]?\d+(\.\d+)?$"))
            {
                var label = zh && ModCn.TryGetValue(k, out var mc) ? mc : null;
                if (zh && label is null)
                {
                    var nm = NormMod(Pretty(k));
                    label = ModEnCn.TryGetValue(nm, out var me) ? me : (_enCn.TryGetValue(nm, out var en) ? en : null);
                }
                outp.Add((label ?? Pretty(k)) + "：" + v);
            }
            if (limit > 0 && outp.Count >= limit) break;
        }
        return outp;
    }

    private static List<string> EffectLines(string? body, Dictionary<string, string> loc, bool zh, int limit = 0)
    {
        var outp = new List<string>();
        foreach (var e in Entries(body))
        {
            var k = e.Key;
            var v = e.Val;
            if (e.IsBlock)
            {
                if (k is "add_country_modifier" or "add_province_modifier")
                {
                    var nm = Scalar(v, "name");
                    var dur = Scalar(v, "duration");
                    var label = loc.TryGetValue(nm, out var mv) ? mv : Pretty(nm);
                    if (dur.Length > 0)
                    {
                        var yrs = double.TryParse(dur, NumberStyles.Float, CultureInfo.InvariantCulture, out var dd)
                            ? (int)(dd / 365) : 0;
                        outp.Add(zh ? $"获得修正：{label}（{yrs} 年）" : $"Gain modifier: {label} ({yrs} years)");
                    }
                    else outp.Add(zh ? "获得修正：" + label : "Gain modifier: " + label);
                }
                else if (k == "add_claim") outp.Add(zh ? "获得宣称" : "Gain a claim");
                else if (k == "add_permanent_claim") outp.Add(zh ? "获得永久宣称" : "Gain a permanent claim");
                else if (k == "add_core") outp.Add(zh ? "获得核心" : "Gain a core");
                else if (k == "custom_tooltip")
                {
                    var tip = Scalar(v, "tooltip");
                    if (tip.Length > 0)
                    {
                        var x = Tip(loc.TryGetValue(tip, out var tv) ? tv : Pretty(tip), zh);
                        if (x.Length > 0) outp.Add(x);
                    }
                }
                if (limit > 0 && outp.Count >= limit) break;
                continue;
            }
            if (k == "custom_tooltip")
            {
                if (v.Length > 0)
                {
                    var x = Tip(loc.TryGetValue(v, out var tv) ? tv : Pretty(v), zh);
                    if (x.Length > 0) outp.Add(x);
                }
            }
            else if (EffCn.TryGetValue(k, out var eff))
            {
                if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                    outp.Add((zh ? eff : Pretty(k)) + " " + NumText(f));
            }
            else if (k == "add_army_tradition_or_mil_power")
            {
                outp.Add(zh ? "陆军传统或军事点数 +" + v : "Army tradition or mil power +" + v);
            }
            if (limit > 0 && outp.Count >= limit) break;
        }
        return outp;
    }

    private static List<string> CondLines(string? body, Dictionary<string, string> loc, bool zh)
    {
        var outp = new List<string>();
        foreach (var e in Entries(body))
        {
            if (e.IsBlock) continue;
            var k = e.Key;
            var v = e.Val;
            if (k == "is_primitive")
                outp.Add(IsNo(v) ? (zh ? "非原始国家" : "Non-primitive") : Pretty(k));
            else if (k is "tag" or "culture_group" or "religion_group" or "government")
            {
                var val = loc.TryGetValue(v, out var lv) ? lv : Pretty(v);
                outp.Add(Pretty(k) + (zh ? "：" : ": ") + val);
            }
        }
        return outp;
    }

    private static string CleanLoc(string s)
    {
        s = s.Replace("\\n", " ").Replace("\\\"", "\"");
        s = Regex.Replace(s, @"\[[^\]]*\]", "");
        s = Regex.Replace(s, "§.", "");
        s = Regex.Replace(s, "£[A-Za-z0-9_\\-]+£", "");
        s = Regex.Replace(s, @"\$[A-Za-z0-9_.:\-]+\$", "");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    private static readonly Regex LocRe = new("^([^\\s:]+)\\s*:\\s*\\d*\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

    /// <summary>英文词条先加载，再用目标语言覆盖同键。</summary>
    private static Dictionary<string, string> LoadLoc(string locDir, string lang)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        var langs = new List<string> { "english" };
        if (!string.IsNullOrEmpty(lang) && lang != "english") langs.Add(lang);
        foreach (var lg in langs)
        {
            var files = new List<string>();
            try { files = Directory.GetFiles(locDir, "*_" + lg + ".yml").OrderBy(p => p, StringComparer.Ordinal).ToList(); }
            catch { }
            foreach (var path in files)
            {
                try
                {
                    foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        var m = LocRe.Match(raw.TrimStart());
                        if (m.Success) d[m.Groups[1].Value] = CleanLoc(m.Groups[2].Value);
                    }
                }
                catch { }
            }
        }
        return d;
    }

    private static (string Dir, string Lang) LocDir(string root, string lang)
    {
        var loc = Path.Combine(root, D_LOC);
        if (!string.IsNullOrEmpty(lang))
        {
            try { if (Directory.GetFiles(loc, "*_" + lang + ".yml").Length > 0) return (loc, lang); }
            catch { }
        }
        return (loc, "english");
    }

    // ------------------------------------------------ 民间汉化 mod（双字节本地化）

    /// <summary>把文件里的 cp1252 字符还原成原始字节。</summary>
    private static int? LocByte(char ch)
    {
        var o = (int)ch;
        if (o < 0x100) return o;
        foreach (var (cp, b) in Cp1252High)
            if (cp == o) return b;
        return null;
    }

    private static readonly (int Cp, int Byte)[] Cp1252High =
    {
        (0x20AC, 0x80), (0x201A, 0x82), (0x0192, 0x83), (0x201E, 0x84), (0x2026, 0x85),
        (0x2020, 0x86), (0x2021, 0x87), (0x02C6, 0x88), (0x2030, 0x89), (0x0160, 0x8A),
        (0x2039, 0x8B), (0x0152, 0x8C), (0x017D, 0x8E), (0x2018, 0x91), (0x2019, 0x92),
        (0x201C, 0x93), (0x201D, 0x94), (0x2022, 0x95), (0x2013, 0x96), (0x2014, 0x97),
        (0x02DC, 0x98), (0x2122, 0x99), (0x0161, 0x9A), (0x203A, 0x9B), (0x0153, 0x9C),
        (0x017E, 0x9E), (0x0178, 0x9F),
    };

    /// <summary>汉化 mod 文本：标记字节 + 低字节 + 高字节 → Unicode 码位。</summary>
    private static string DecodeLoc(string? s)
    {
        s ??= "";
        var outp = new StringBuilder(s.Length);
        int i = 0, n = s.Length;
        while (i < n)
        {
            var c = s[i];
            if (LocMarks.IndexOf(c) >= 0 && i + 2 < n)
            {
                var b1 = LocByte(s[i + 1]);
                var b2 = LocByte(s[i + 2]);
                if (b1 is not null && b2 is not null)
                {
                    var mark = (int)c;
                    if (mark == 0x11) b1 = (b1.Value - 0x0E) & 0xFF;
                    else if (mark == 0x12) b2 = (b2.Value + 0x09) & 0xFF;
                    else if (mark == 0x13)
                    {
                        b1 = (b1.Value - 0x0E) & 0xFF;
                        b2 = (b2.Value + 0x09) & 0xFF;
                    }
                    var cp = b1.Value | (b2.Value << 8);
                    if (cp is >= 0x20 and <= 0x2FFFF and not (>= 0xD800 and <= 0xDFFF))
                    {
                        outp.Append((char)cp);
                        i += 3;
                        continue;
                    }
                }
            }
            outp.Append(c);
            i++;
        }
        return outp.ToString();
    }

    private static Dictionary<string, string> LoadLocDir(string locDir)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        List<string> files;
        try { files = Directory.GetFiles(locDir, "*_l_english.yml").OrderBy(p => p, StringComparer.Ordinal).ToList(); }
        catch { return d; }
        if (files.Count == 0)
        {
            try { files = Directory.GetFiles(locDir, "*_english.yml").OrderBy(p => p, StringComparer.Ordinal).ToList(); }
            catch { return d; }
        }
        foreach (var path in files)
        {
            try
            {
                foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var m = LocRe.Match(raw.TrimStart());
                    if (m.Success) d[m.Groups[1].Value] = CleanLoc(DecodeLoc(m.Groups[2].Value));
                }
            }
            catch { }
        }
        return d;
    }

    /// <summary>判断目录是不是中文汉化 mod：descriptor 名称，或本地化里能解出汉字。</summary>
    private static bool IsCnMod(string d)
    {
        try
        {
            var desc = Path.Combine(d, "descriptor.mod");
            if (File.Exists(desc))
            {
                var txt = File.ReadAllText(desc, Encoding.UTF8).ToLowerInvariant();
                if (txt.Contains("chinese") || txt.Contains("translation") || txt.Contains("中文") || txt.Contains("汉化"))
                    return true;
            }
        }
        catch { }
        List<string> files;
        try { files = Directory.GetFiles(Path.Combine(d, D_LOC), "*.yml").OrderBy(p => p, StringComparer.Ordinal).Take(6).ToList(); }
        catch { return false; }
        foreach (var path in files)
        {
            try
            {
                var lines = File.ReadAllLines(path, Encoding.UTF8);
                for (var i = 0; i < lines.Length && i < 300; i++)
                    if (HasCjk(DecodeLoc(lines[i]))) return true;
            }
            catch { }
        }
        return false;
    }

    /// <summary>工坊与文档目录里已安装的中文汉化 mod。</summary>
    private static List<string> LocModDirs(string root)
    {
        var cands = new List<string>();
        try
        {
            var steamapps = Directory.GetParent(root)?.Parent?.FullName ?? "";
            var ws = Path.Combine(steamapps, "workshop", "content", "236850");
            if (Directory.Exists(ws))
                foreach (var name in Directory.GetDirectories(ws).OrderBy(p => p, StringComparer.Ordinal))
                    if (Directory.Exists(Path.Combine(name, D_LOC))) cands.Add(name);
        }
        catch { }
        try
        {
            var mdir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Paradox Interactive", "Europa Universalis IV", "mod");
            if (Directory.Exists(mdir))
                foreach (var p in Directory.GetFileSystemEntries(mdir).OrderBy(p => p, StringComparer.Ordinal))
                {
                    if (Directory.Exists(p))
                    {
                        if (Directory.Exists(Path.Combine(p, D_LOC))) cands.Add(p);
                    }
                    else if (p.EndsWith(".mod", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var m = Regex.Match(File.ReadAllText(p, Encoding.UTF8), "path\\s*=\\s*\"([^\"]+)\"");
                            if (!m.Success) continue;
                            var q = Path.GetFullPath(Path.Combine(mdir, m.Groups[1].Value));
                            if (Directory.Exists(Path.Combine(q, D_LOC))) cands.Add(q);
                        }
                        catch { }
                    }
                }
        }
        catch { }
        var outp = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in cands)
        {
            var k = Path.GetFullPath(p);
            if (!seen.Add(k) || !IsCnMod(k)) continue;
            outp.Add(k);
        }
        outp.Sort(StringComparer.OrdinalIgnoreCase);
        return outp;
    }

    private static (Dictionary<string, (int? Tech, string Year)> Units, int Idx, string LastYear)
        TechUnits(string path)
    {
        var outp = new Dictionary<string, (int? Tech, string Year)>(StringComparer.Ordinal);
        int idx = 0;
        var lastYear = "";
        foreach (var e in Entries(ReadEu4(path)))
        {
            if (e.Key != "technology" || !e.IsBlock) continue;
            var year = Scalar(e.Val, "year");
            if (year.Length == 0) year = lastYear;
            lastYear = year;
            foreach (var en in AllScalars(e.Val, "enable"))
                foreach (var u in Regex.Split(en.Trim(), @"\s+"))
                    if (u.Length > 0) outp[u] = (idx, year);
            idx++;
        }
        return (outp, idx, lastYear);
    }

    // ------------------------------------------------------------ 思想组

    private static (List<JsonObject> Items, Dictionary<string, List<string>> Orders) ScanIdeas(
        string root, Dictionary<string, string> loc, bool zh)
    {
        var ideas = new List<JsonObject>();
        var files = new List<string>();
        try { files = Directory.GetFiles(Path.Combine(root, D_IDEA), "*.txt").OrderBy(p => p, StringComparer.Ordinal).ToList(); }
        catch { }
        foreach (var path in files)
        {
            foreach (var (gk, gb) in Blocks(ReadEu4(path)))
            {
                if (!gk.EndsWith("_ideas", StringComparison.Ordinal)) continue;
                var cat = Scalar(gb, "category").ToUpperInvariant();
                string ck, catname;
                if (cat is "ADM" or "DIP" or "MIL") { ck = cat; catname = CatCn[cat]; }
                else { ck = "national"; catname = CatCn["national"]; }

                var bonus = ModLines(Sub(gb, "bonus"), loc, zh, 30);
                var start = ModLines(Sub(gb, "start"), loc, zh, 30);
                var secs = new JsonArray();
                var awards = new JsonArray();
                if (start.Count > 0)
                    awards.Add(new JsonObject
                    {
                        ["label"] = zh ? "初始理念" : "Starting ideas",
                        ["effects"] = ToArr(start),
                    });
                if (bonus.Count > 0)
                    awards.Add(new JsonObject
                    {
                        ["label"] = zh ? "完成奖励" : "Completion bonus",
                        ["effects"] = ToArr(bonus),
                    });
                if (awards.Count > 0)
                    secs.Add(new JsonObject
                    {
                        ["label"] = zh ? "组奖励" : "Group bonuses",
                        ["kind"] = "tiers",
                        ["items"] = awards,
                    });

                var its = new JsonArray();
                var reserved = new HashSet<string>(StringComparer.Ordinal)
                {
                    "bonus", "start", "trigger", "free", "ai_will_do", "category", "important", "effect",
                };
                foreach (var e in Entries(gb))
                {
                    if (!e.IsBlock || reserved.Contains(e.Key)) continue;
                    var eff = ModLines(e.Val, loc, zh, 30);
                    its.Add(new JsonObject
                    {
                        ["label"] = ModLabel(e.Key, loc, zh),
                        ["effects"] = ToArr(eff),
                    });
                }
                if (its.Count > 0)
                    secs.Add(new JsonObject
                    {
                        ["label"] = zh ? $"理念 ({its.Count})" : $"Ideas ({its.Count})",
                        ["kind"] = "tiers",
                        ["items"] = its,
                    });

                ideas.Add(new JsonObject
                {
                    ["key"] = gk,
                    ["name"] = ModLabel(gk, loc, zh),
                    ["cat"] = catname,
                    ["catKey"] = ck,
                    ["desc"] = zh ? $"{catname} · {its.Count} 条理念" : $"{catname} · {its.Count} ideas",
                    ["effects"] = new JsonArray(),
                    ["secs"] = secs,
                    ["attrs"] = new JsonArray
                    {
                        Attr(zh ? "类别" : "Category", catname),
                        Attr(zh ? "理念数" : "Ideas", its.Count.ToString(CultureInfo.InvariantCulture)),
                    },
                });
            }
        }
        var order = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["ideas"] = new List<string> { "ADM", "DIP", "MIL", "national" },
        };
        return (ideas, order);
    }

    // ------------------------------------------------------------ 单位

    private static int SortTech(Dictionary<string, (int? Tech, string Year)> techs, string key) =>
        techs.TryGetValue(key, out var t) && t.Tech is not null ? t.Tech.Value : 999;

    private static (List<JsonObject> Land, List<JsonObject> Naval, Dictionary<string, List<string>> Orders,
        List<JsonObject> Groups) ScanUnits(string root, Dictionary<string, string> loc,
        Dictionary<string, (int? Tech, string Year)> techs, bool zh, Dictionary<string, double> defs)
    {
        var land = new List<JsonObject>();
        var naval = new List<JsonObject>();
        var grpCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var files = new List<string>();
        try { files = Directory.GetFiles(Path.Combine(root, D_UNIT), "*.txt").OrderBy(p => p, StringComparer.Ordinal).ToList(); }
        catch { }

        foreach (var path in files)
        {
            var key = Path.GetFileNameWithoutExtension(path);
            var body = ReadEu4(path);
            var t = Scalar(body, "type").ToLowerInvariant();
            var isLand = LandTypes.Contains(t);
            var isNaval = NavalTypes.Contains(t);
            if (!isLand && !isNaval) continue;
            var grp = Regex.Replace(Scalar(body, "unit_type").ToLowerInvariant(), "_tech$", "");
            if (isLand && grp.Length == 0) grp = "_universal";
            var grpName = (zh ? GroupCn.GetValueOrDefault(grp) : GroupEn.GetValueOrDefault(grp)) ?? Pretty(grp);
            if (grp == "_universal" && zh) grpName = GroupCn["_universal"];
            var name = loc.TryGetValue(key, out var lv) ? lv : Pretty(key);
            techs.TryGetValue(key, out var info);
            var tech = info.Tech;
            var year = info.Year ?? "";

            var attrs = new JsonArray();
            string typ, desc;
            if (isLand)
            {
                typ = zh ? TypeCn.GetValueOrDefault(t, t) : TypeEn.GetValueOrDefault(t, Pretty(t));
                var (goldKey, mpKey) = UnitCost.TryGetValue(t, out var uc) ? uc : ("", "");
                var gold = Cost(defs.TryGetValue(goldKey, out var gv) ? gv : null);
                var mp = defs.TryGetValue(mpKey, out var mvv) ? (double?)mvv : null;
                var men = mp is not null ? Cost(mp.Value * 1000) : "";
                var goldTxt = gold != "—" ? (zh ? gold + " 金币" : gold + " ducats") : "—";
                var menTxt = men.Length > 0 ? (zh ? men + " 人" : men + " men") : "—";
                attrs.Add(Attr(zh ? "类型" : "Type", typ));
                attrs.Add(Attr(zh ? "科技组" : "Tech group", grpName.Length > 0 ? grpName : "—"));
                attrs.Add(Attr(zh ? "军事科技" : "Mil tech", tech?.ToString(CultureInfo.InvariantCulture) ?? "—"));
                attrs.Add(Attr(zh ? "年份" : "Year", year.Length > 0 ? year : "—"));
                attrs.Add(Attr(zh ? "招募花费" : "Recruit cost", goldTxt));
                attrs.Add(Attr(zh ? "人力成本" : "Manpower cost", menTxt));
                attrs.Add(Attr(zh ? "机动" : "Maneuver", Scalar(body, "maneuver") is { Length: > 0 } mv ? mv : "—"));
                attrs.Add(Attr(zh ? "火力（攻/防）" : "Fire (off/def)",
                    (Scalar(body, "offensive_fire") is { Length: > 0 } of ? of : "0") + " / " +
                    (Scalar(body, "defensive_fire") is { Length: > 0 } df ? df : "0")));
                attrs.Add(Attr(zh ? "冲击（攻/防）" : "Shock (off/def)",
                    (Scalar(body, "offensive_shock") is { Length: > 0 } os ? os : "0") + " / " +
                    (Scalar(body, "defensive_shock") is { Length: > 0 } ds ? ds : "0")));
                attrs.Add(Attr(zh ? "士气（攻/防）" : "Morale (off/def)",
                    (Scalar(body, "offensive_morale") is { Length: > 0 } om ? om : "0") + " / " +
                    (Scalar(body, "defensive_morale") is { Length: > 0 } dm ? dm : "0")));

                desc = typ + (grpName.Length > 0 ? " · " + grpName : "");
                if (tech is not null) desc += zh ? $" · 军科 {tech}" : $" · Mil tech {tech}";
                if (gold != "—") desc += zh ? $" · {gold} 金币" : $" · {gold} ducats";
                if (grp.Length > 0) grpCounts[grp] = grpCounts.GetValueOrDefault(grp) + 1;
                land.Add(new JsonObject
                {
                    ["key"] = key, ["name"] = name, ["cat"] = typ, ["catKey"] = t,
                    ["grpKey"] = grp, ["desc"] = desc, ["effects"] = new JsonArray(), ["attrs"] = attrs,
                });
            }
            else
            {
                typ = zh ? TypeCn.GetValueOrDefault(t, t) : TypeEn.GetValueOrDefault(t, Pretty(t));
                var (goldKey, sailKey) = ShipCost.TryGetValue(t, out var sc) ? sc : ("", "");
                var gold = Cost(defs.TryGetValue(goldKey, out var gv) ? gv : null);
                var crew = Scalar(body, "sailors");
                if (crew.Length == 0) crew = Cost(defs.TryGetValue(sailKey, out var sv) ? sv : null);
                attrs.Add(Attr(zh ? "类型" : "Type", typ));
                attrs.Add(Attr(zh ? "船体" : "Hull", Scalar(body, "hull_size") is { Length: > 0 } hv ? hv : "—"));
                attrs.Add(Attr(zh ? "火炮" : "Cannons", Scalar(body, "base_cannons") is { Length: > 0 } cv ? cv : "—"));
                attrs.Add(Attr(zh ? "封锁" : "Blockade", Scalar(body, "blockade") is { Length: > 0 } bv ? bv : "—"));
                attrs.Add(Attr(zh ? "航速" : "Speed", Scalar(body, "sail_speed") is { Length: > 0 } sp ? sp : "—"));
                attrs.Add(Attr(zh ? "水手成本" : "Sailor cost",
                    crew != "—" ? (zh ? crew + " 人" : crew + " sailors") : "—"));
                attrs.Add(Attr(zh ? "贸易力量" : "Trade power", Scalar(body, "trade_power") is { Length: > 0 } tp ? tp : "—"));
                var pos = 1;
                if (tech is not null) { attrs.Insert(1, Attr(zh ? "军事科技" : "Mil tech", tech.Value.ToString(CultureInfo.InvariantCulture))); pos = 2; }
                if (gold != "—")
                    attrs.Insert(pos, Attr(zh ? "建造花费" : "Build cost", zh ? gold + " 金币" : gold + " ducats"));

                desc = typ + (grpName.Length > 0 ? " · " + grpName : "");
                if (tech is not null) desc += zh ? $" · 军科 {tech}" : $" · Mil tech {tech}";
                if (gold != "—") desc += zh ? $" · {gold} 金币" : $" · {gold} ducats";
                naval.Add(new JsonObject
                {
                    ["key"] = key, ["name"] = name, ["cat"] = typ, ["catKey"] = t,
                    ["desc"] = desc, ["effects"] = new JsonArray(), ["attrs"] = attrs,
                });
            }
        }

        land.Sort((a, b) =>
        {
            var c = Array.IndexOf(LandTypes, (string?)a["catKey"]).CompareTo(Array.IndexOf(LandTypes, (string?)b["catKey"]));
            if (c != 0) return c;
            c = SortTech(techs, (string?)a["key"] ?? "").CompareTo(SortTech(techs, (string?)b["key"] ?? ""));
            return c != 0 ? c : string.CompareOrdinal((string?)a["name"], (string?)b["name"]);
        });
        naval.Sort((a, b) =>
        {
            var c = Array.IndexOf(NavalTypes, (string?)a["catKey"]).CompareTo(Array.IndexOf(NavalTypes, (string?)b["catKey"]));
            if (c != 0) return c;
            c = SortTech(techs, (string?)a["key"] ?? "").CompareTo(SortTech(techs, (string?)b["key"] ?? ""));
            return c != 0 ? c : string.CompareOrdinal((string?)a["name"], (string?)b["name"]);
        });

        var groups = grpCounts
            .Select(kv => new
            {
                Key = kv.Key,
                Name = (zh ? GroupCn.GetValueOrDefault(kv.Key) : GroupEn.GetValueOrDefault(kv.Key)) ?? Pretty(kv.Key),
                N = kv.Value,
            })
            .OrderByDescending(g => g.N)
            .ThenBy(g => g.Name, StringComparer.Ordinal)
            .Select(g => new JsonObject { ["key"] = g.Key, ["name"] = g.Name, ["n"] = g.N })
            .ToList();

        var orders = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["landunits"] = LandTypes.ToList(),
            ["navalunits"] = NavalTypes.ToList(),
        };
        return (land, naval, orders, groups);
    }

    // ------------------------------------------------------------ 陆战 / 海军

    private static (List<JsonObject> Items, Dictionary<string, List<string>> Orders) ScanArmy(bool zh)
    {
        var outp = new List<JsonObject>();
        foreach (var g in ArmyGuide)
            outp.Add(new JsonObject
            {
                ["key"] = g.Key,
                ["name"] = zh ? g.Cn : g.En,
                ["cat"] = zh ? "陆战机制" : "Land warfare",
                ["catKey"] = "mechanics",
                ["desc"] = zh ? g.DCn : g.DEn,
                ["effects"] = ToArr(new[] { zh ? g.ECn : g.EEn }),
                ["attrs"] = new JsonArray(),
            });
        return (outp, new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["army"] = new List<string> { "mechanics" },
        });
    }

    private static (List<JsonObject> Items, Dictionary<string, List<string>> Orders) ScanNavy(
        string root, Dictionary<string, string> loc, bool zh)
    {
        var outp = new List<JsonObject>();
        var dir = Path.Combine(root, D_DOCTRINE);
        List<string> files;
        try { files = Directory.GetFiles(dir, "*.txt").OrderBy(p => p, StringComparer.Ordinal).ToList(); }
        catch { files = new List<string>(); }
        foreach (var path in files)
        {
            foreach (var (key, body) in Blocks(ReadEu4(path)))
            {
                var eff = ModLines(Sub(body, "country_modifier"), loc, zh, 30);
                var cost = Scalar(body, "cost");
                var cond = CondLines(Sub(body, "can_select"), loc, zh);
                var attrs = new JsonArray();
                if (cost.Length > 0)
                {
                    if (double.TryParse(cost, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                        attrs.Add(Attr(zh ? "维护费用" : "Maintenance",
                            zh ? $"每名水手 {value:0.00} 金币" : $"{value:0.00} ducats / sailor"));
                    else attrs.Add(Attr(zh ? "维护费用" : "Maintenance", cost));
                }
                var secs = new JsonArray();
                if (cond.Count > 0)
                    secs.Add(new JsonObject
                    {
                        ["label"] = zh ? "选择条件" : "Requirements",
                        ["kind"] = "list",
                        ["items"] = ToArr(cond),
                    });
                outp.Add(new JsonObject
                {
                    ["key"] = key,
                    ["name"] = loc.TryGetValue(key, out var nm) ? nm : Pretty(key),
                    ["cat"] = zh ? "海军学说" : "Naval doctrine",
                    ["catKey"] = "doctrine",
                    ["desc"] = loc.TryGetValue(key + "_desc", out var ds) ? ds : "",
                    ["effects"] = ToArr(eff),
                    ["attrs"] = attrs,
                    ["secs"] = secs,
                });
            }
        }
        foreach (var g in NavyGuide)
            outp.Add(new JsonObject
            {
                ["key"] = g.Key,
                ["name"] = zh ? g.Cn : g.En,
                ["cat"] = zh ? "海军机制" : "Naval mechanics",
                ["catKey"] = "mechanics",
                ["desc"] = zh ? g.DCn : g.DEn,
                ["effects"] = ToArr(new[] { zh ? g.ECn : g.EEn }),
                ["attrs"] = new JsonArray(),
            });
        outp = outp
            .OrderBy(x => (string?)x["catKey"] == "mechanics" ? 0 : 1)
            .ThenBy(x => (string?)x["name"], StringComparer.Ordinal)
            .ToList();
        return (outp, new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["navy"] = new List<string> { "mechanics", "doctrine" },
        });
    }

    // ------------------------------------------------------------ 时代

    private static (List<JsonObject> Items, Dictionary<string, List<string>> Orders) ScanAges(
        string root, Dictionary<string, string> loc, bool zh)
    {
        var outp = new List<JsonObject>();
        var keys = new List<string>();
        var dir = Path.Combine(root, D_AGE);
        List<string> files;
        try { files = Directory.GetFiles(dir, "*.txt").OrderBy(p => p, StringComparer.Ordinal).ToList(); }
        catch { files = new List<string>(); }
        foreach (var path in files)
        {
            foreach (var (key, body) in Blocks(ReadEu4(path)))
            {
                var start = Scalar(body, "start");
                if (start.Length == 0 && Sub(body, "objectives").Length == 0) continue;
                var name = loc.TryGetValue(key, out var nm) ? nm : Pretty(key);
                var objs = new JsonArray();
                var abils = new JsonArray();
                foreach (var e in Entries(Sub(body, "objectives")))
                {
                    if (!e.IsBlock) continue;
                    var cond = TriggerLines(e.Val, loc, zh, 6);
                    if (cond.Count == 0) cond = EffectLines(e.Val, loc, zh, 6);
                    objs.Add(new JsonObject
                    {
                        ["label"] = loc.TryGetValue(e.Key, out var lv) ? lv : Pretty(e.Key),
                        ["effects"] = ToArr(cond),
                    });
                }
                foreach (var e in Entries(Sub(body, "abilities")))
                {
                    if (!e.IsBlock) continue;
                    var eff = ModLines(Sub(e.Val, "modifier"), loc, zh, 10);
                    if (eff.Count == 0) eff = EffectLines(Sub(e.Val, "effect"), loc, zh, 10);
                    abils.Add(new JsonObject
                    {
                        ["label"] = loc.TryGetValue(e.Key, out var lv) ? lv : Pretty(e.Key),
                        ["effects"] = ToArr(eff),
                    });
                }
                var secs = new JsonArray();
                if (objs.Count > 0)
                    secs.Add(new JsonObject
                    {
                        ["label"] = zh ? "时代目标" : "Age objectives",
                        ["kind"] = "tiers",
                        ["items"] = objs,
                    });
                if (abils.Count > 0)
                    secs.Add(new JsonObject
                    {
                        ["label"] = zh ? "时代能力" : "Age abilities",
                        ["kind"] = "tiers",
                        ["items"] = abils,
                    });
                var can = TriggerLines(Sub(body, "can_start"), loc, zh, 4);
                var attrs = new JsonArray
                {
                    Attr(zh ? "起始年份" : "Start year", start.Length > 0 ? start : "—"),
                    Attr(zh ? "目标数" : "Objectives", objs.Count.ToString(CultureInfo.InvariantCulture)),
                    Attr(zh ? "能力数" : "Abilities", abils.Count.ToString(CultureInfo.InvariantCulture)),
                };
                if (can.Count > 0)
                    attrs.Add(Attr(zh ? "开始条件" : "Start conditions", string.Join("；", can)));

                outp.Add(new JsonObject
                {
                    ["key"] = key,
                    ["name"] = name,
                    ["cat"] = name,
                    ["catKey"] = key,
                    ["desc"] = loc.TryGetValue(key + "_desc", out var ds) ? ds
                        : (zh && start.Length > 0 ? $"从 {start} 年开始" : ""),
                    ["effects"] = new JsonArray(),
                    ["attrs"] = attrs,
                    ["secs"] = secs,
                });
                keys.Add(key);
            }
        }
        outp = outp.OrderBy(AgeYear).ToList();
        return (outp, new Dictionary<string, List<string>>(StringComparer.Ordinal) { ["ages"] = keys });
    }

    private static int AgeYear(JsonObject it)
    {
        if (it["attrs"] is not JsonArray attrs) return 9999;
        foreach (var a in attrs)
        {
            if (a is not JsonObject ao) continue;
            var label = ao["label"]?.ToString();
            if (label is "起始年份" or "Start year")
                    return int.TryParse(ao["value"]?.ToString(), out var y) ? y : 9999;
        }
        return 9999;
    }

    // ------------------------------------------------------------ 任务树

    private static string Slug(string? s)
    {
        var v = Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        return v.Length > 0 ? v : "tree";
    }

    /// <summary>英文词条 → 汉化词条，供任务树名里的国名/形容词自动汉化。</summary>
    private static Dictionary<string, (string Key, string Zh)> RevLoc(
        Dictionary<string, string> enLoc, Dictionary<string, string> loc)
    {
        var rev = new Dictionary<string, (string Key, string Zh)>(StringComparer.Ordinal);
        foreach (var (k, v) in enLoc)
        {
            if (!loc.TryGetValue(k, out var z) || z.Length == 0 || v.Length == 0 || z == v || v.Length > 40) continue;
            var key = v.Trim().ToLowerInvariant();
            if (key.Length == 0) continue;
            if (!rev.TryGetValue(key, out var cur) ||
                (k.EndsWith("_ADJ", StringComparison.Ordinal) && !cur.Key.EndsWith("_ADJ", StringComparison.Ordinal)))
                rev[key] = (k, z);
        }
        return rev;
    }

    /// <summary>任务树文件名 → (汉化名, DLC/地区分组代号)。</summary>
    private static (string Name, string Code) TreeParts(string stem, Dictionary<string, (string Key, string Zh)>? rev, bool zh)
    {
        var s = Regex.Replace(stem ?? "", @"^\d+_", "").Trim();
        s = Regex.Replace(s, "_?Missions?$", "", RegexOptions.IgnoreCase).Trim();
        s = s.Replace("_", " ").Trim();
        if (s.Length == 0) return ("Missions", "");
        if (!zh || rev is null) return (s, "");
        var words = Regex.Split(s, "[ \t\\-]+").Where(w => w.Length > 0).ToList();
        var codes = new List<string>();
        while (words.Count > 0 && (TreeSkip.Contains(words[0].ToLowerInvariant()) ||
                                   TreePrefix.Contains(words[0].ToUpperInvariant())))
        {
            var w = words[0];
            if (TreePrefix.Contains(w.ToUpperInvariant()) && w.ToUpperInvariant() != "AFR")
                codes.Add(w.ToUpperInvariant());
            words.RemoveAt(0);
        }
        if (words.Count == 0) return (s, string.Join("/", codes));
        var phrase = string.Join(" ", words).ToLowerInvariant();
        string cn;
        if (rev.TryGetValue(phrase, out var hit)) cn = hit.Zh;
        else if (TreeWords.TryGetValue(phrase, out var direct)) cn = direct;
        else
        {
            var parts = new List<string>();
            foreach (var w in words)
            {
                var lw = w.ToLowerInvariant();
                if (TreeSkip.Contains(lw)) continue;
                parts.Add(rev.TryGetValue(lw, out var h) ? h.Zh : (TreeWords.GetValueOrDefault(lw) ?? w));
            }
            cn = string.Concat(parts);
        }
        return (cn.Length > 0 ? cn : s, string.Join("/", codes));
    }

    private static bool LooksSeries(string body)
    {
        var es = Entries(body);
        var hasSlot = es.Any(e => !e.IsBlock && e.Key == "slot");
        var hasMission = es.Any(e => e.IsBlock && LooksMission(e.Val));
        return hasSlot && hasMission;
    }

    private static bool LooksMission(string body)
    {
        foreach (var e in Entries(body))
        {
            if (e.Key is "trigger" or "effect" or "required_missions" or "provinces_to_highlight") return true;
            if (e.Key == "icon" && !e.IsBlock) return true;
        }
        return false;
    }

    private sealed class MsNode
    {
        public string Id = "";
        public string Raw = "";
        public string Nm = "";
        public string Ds = "";
        public string Icon = "";
        public List<string> Pre = new();
        public List<string> Fx = new();
        public List<string> Cond = new();
        public int X, Y, S = 1;
    }

    private sealed class TreeItem
    {
        public string Path = "";
        public string Stem = "";
        public string Tid = "";
        public string Base = "";
        public string Code = "";
        public string Name = "";
    }

    private static (Dictionary<string, JsonObject> Trees, JsonArray Slim) ScanMissions(
        string root, Dictionary<string, string> loc, bool zh, Dictionary<string, (string Key, string Zh)>? rev)
    {
        var trees = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var genericIds = new HashSet<string>(StringComparer.Ordinal) { "00_generic_missions", "01_generic_european_missions" };
        var items = new List<TreeItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        List<string> files;
        try { files = Directory.GetFiles(Path.Combine(root, D_MISS), "*.txt").OrderBy(p => p, StringComparer.Ordinal).ToList(); }
        catch { files = new List<string>(); }
        foreach (var path in files)
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            var tid = Slug(stem);
            if (!seen.Add(tid)) continue;
            var (nm, code) = TreeParts(stem, rev, zh);
            items.Add(new TreeItem { Path = path, Stem = stem, Tid = tid, Base = nm, Code = code });
        }
        // 同名任务树（本体版 / DLC 版）加代号区分，如「奥地利」与「奥地利（EMP）」
        var dup = items.GroupBy(i => i.Base, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var it in items)
            it.Name = it.Code.Length > 0 && dup.GetValueOrDefault(it.Base) > 1
                ? $"{it.Base}（{it.Code}）" : it.Base;
        // 仍重名的再带文件序号
        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var it in items)
        {
            if (used.GetValueOrDefault(it.Name) > 0)
            {
                var m = Regex.Match(it.Stem, @"^(\d+)_");
                it.Name = it.Name + "·" + (m.Success ? m.Groups[1].Value
                    : (used.GetValueOrDefault(it.Name) + 1).ToString(CultureInfo.InvariantCulture));
            }
            used[it.Name] = used.GetValueOrDefault(it.Name) + 1;
        }

        foreach (var it in items)
        {
            var series = new List<(string Key, string Val)>();
            var implicitMissions = new List<(string Key, string Val)>();
            foreach (var e in Entries(ReadEu4(it.Path)))
            {
                if (!e.IsBlock) continue;
                var children = Entries(e.Val).Where(c => c.IsBlock).Select(c => (c.Key, c.Val)).ToList();
                var nested = children.Where(c => LooksSeries(c.Val)).ToList();
                if (nested.Count > 0) series.AddRange(nested);
                else if (LooksSeries(e.Val)) series.Add((e.Key, e.Val));
                else if (LooksMission(e.Val)) implicitMissions.Add((e.Key, e.Val));
            }
            if (implicitMissions.Count > 0)
                series.Add(("__implicit__", "slot = 1\n" +
                    string.Join("\n", implicitMissions.Select(m => m.Key + " = { " + m.Val + " }"))));
            if (series.Count == 0) continue;

            var ordered = new List<(int Slot, int Index, string Key, string Val)>();
            for (var i = 0; i < series.Count; i++)
            {
                var slot = Scalar(series[i].Val, "slot");
                var sv = int.TryParse(slot, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 99;
                ordered.Add((sv, i, series[i].Key, series[i].Val));
            }
            ordered.Sort((a, b) => a.Slot != b.Slot ? a.Slot.CompareTo(b.Slot) : a.Index.CompareTo(b.Index));

            var nodes = new List<MsNode>();
            var byraw = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var col = 0; col < ordered.Count; col++)
            {
                var (slotv, _, _, sb) = ordered[col];
                foreach (var e in Entries(sb))
                {
                    if (!e.IsBlock || !LooksMission(e.Val)) continue;
                    var raw = e.Key;
                    var nid = !byraw.ContainsKey(raw) ? raw : raw + "~" + nodes.Count;
                    byraw.TryAdd(raw, nid);
                    var ttl = loc.TryGetValue(raw + "_title", out var tv) ? tv : "";
                    if (ttl.Length == 0)
                    {
                        var low = raw.ToLowerInvariant();
                        ttl = low.Contains("branch") ? "分支任务" : low.Contains("dummy") ? "占位任务" : "可选任务";
                    }
                    nodes.Add(new MsNode
                    {
                        Id = nid,
                        Raw = raw,
                        Nm = ttl,
                        Ds = loc.TryGetValue(raw + "_desc", out var dsv) ? dsv : "",
                        Icon = Scalar(e.Val, "icon"),
                        Pre = ListOf(e.Val, "required_missions"),
                        Fx = EffectLines(Sub(e.Val, "effect"), loc, zh, 6),
                        Cond = TriggerLines(Sub(e.Val, "trigger"), loc, zh, 6),
                        X = col,
                        Y = 0,
                        S = slotv != 99 ? slotv : 1,
                    });
                }
            }
            if (nodes.Count == 0) continue;

            // 前置 → 节点 id
            foreach (var n in nodes)
                n.Pre = n.Pre.Where(p => byraw.ContainsKey(p)).Select(p => byraw[p]).ToList();

            // 纵向层级：反复松弛，环则封顶
            var byid = nodes.ToDictionary(n => n.Id, n => n, StringComparer.Ordinal);
            for (var pass = 0; pass <= nodes.Count; pass++)
            {
                var changed = false;
                foreach (var n in nodes)
                {
                    var y = 0;
                    foreach (var p in n.Pre)
                        if (byid.TryGetValue(p, out var q)) y = Math.Max(y, q.Y + 1);
                    if (y > nodes.Count - 1) y = nodes.Count - 1;
                    if (y != n.Y) { n.Y = y; changed = true; }
                }
                if (!changed) break;
            }

            var focusArr = new JsonArray();
            foreach (var n in nodes)
                focusArr.Add(new JsonObject
                {
                    ["id"] = n.Id,
                    ["nm"] = n.Nm,
                    ["ds"] = n.Ds,
                    ["icon"] = n.Icon,
                    ["pre"] = ToArr(n.Pre),
                    ["fx"] = ToArr(n.Fx),
                    ["cond"] = ToArr(n.Cond),
                    ["x"] = n.X,
                    ["y"] = n.Y,
                    ["s"] = n.S,
                });

            trees[it.Tid] = new JsonObject
            {
                ["id"] = it.Tid,
                ["name"] = it.Name,
                ["n"] = nodes.Count,
                ["default"] = genericIds.Contains(it.Tid),
                ["x0"] = 0,
                ["x1"] = nodes.Max(n => n.X),
                ["y0"] = 0,
                ["y1"] = nodes.Max(n => n.Y),
                ["focuses"] = focusArr,
            };
        }

        var slim = new JsonArray();
        foreach (var t in trees.Values
                     .OrderBy(t => (bool?)t["default"] == true ? 0 : 1)
                     .ThenBy(t => (string?)t["name"], StringComparer.OrdinalIgnoreCase))
            slim.Add(new JsonObject
            {
                ["id"] = t["id"]?.ToString() ?? "",
                ["name"] = t["name"]?.ToString() ?? "",
                ["n"] = (t["focuses"] as JsonArray)?.Count ?? 0,
                ["default"] = (bool?)t["default"] == true,
            });
        return (trees, slim);
    }

    // ------------------------------------------------------------ 构建

    private static JsonObject Build(string root, string locDir, string locLang, bool zh, List<string> modDirs)
    {
        var loc = LoadLoc(locDir, locLang);
        var enLoc = new Dictionary<string, string>(loc, StringComparer.Ordinal);
        if (zh)
            foreach (var md in modDirs)
                foreach (var (k, v) in LoadLocDir(Path.Combine(md, D_LOC)))
                    loc[k] = v;
        var rev = zh ? RevLoc(enLoc, loc) : null;
        _enCn = zh ? BuildEnCn(enLoc, loc) : new Dictionary<string, string>(StringComparer.Ordinal);

        var defs = Defines(root);
        var (techs, _, _) = TechUnits(Path.Combine(root, D_TECH, "mil.txt"));
        var (ideas, ideaOrders) = ScanIdeas(root, loc, zh);
        var (land, naval, unitOrders, unitGroups) = ScanUnits(root, loc, techs, zh, defs);
        var (army, armyOrders) = ScanArmy(zh);
        var (navy, navyOrders) = ScanNavy(root, loc, zh);
        var (ages, ageOrders) = ScanAges(root, loc, zh);
        var (trees, slim) = ScanMissions(root, loc, zh, rev);

        var counts = new JsonObject
        {
            ["ideas"] = ideas.Count,
            ["army"] = army.Count,
            ["landunits"] = land.Count,
            ["navalunits"] = naval.Count,
            ["navy"] = navy.Count,
            ["ages"] = ages.Count,
            ["missions"] = trees.Values.Sum(t => (t["focuses"] as JsonArray)?.Count ?? 0),
        };
        var tabCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["ideas"] = ideas.Count,
            ["army"] = army.Count,
            ["landunits"] = land.Count,
            ["navalunits"] = naval.Count,
            ["navy"] = navy.Count,
            ["ages"] = ages.Count,
            ["missions"] = slim.Count,
        };

        var tabs = new JsonArray();
        foreach (var (id, icon, layout) in Tabs)
            tabs.Add(new JsonObject
            {
                ["id"] = id,
                ["icon"] = icon,
                ["layout"] = layout,
                ["count"] = tabCounts.GetValueOrDefault(id),
            });

        var cat = new JsonObject();
        foreach (var src in new[] { ideaOrders, unitOrders, armyOrders, navyOrders, ageOrders })
            foreach (var (k, v) in src) cat[k] = ToArr(v);

        var treeObj = new JsonObject();
        foreach (var (k, v) in trees) treeObj[k] = v;

        return new JsonObject
        {
            ["ok"] = true,
            ["supported"] = true,
            ["game"] = "eu4",
            ["gameName"] = "Europa Universalis IV",
            ["gameVer"] = GameVersion(root),
            ["loc"] = locLang,
            ["tabs"] = tabs,
            ["counts"] = counts,
            ["ideas"] = ToArrObj(ideas),
            ["army"] = ToArrObj(army),
            ["landunits"] = ToArrObj(land),
            ["navalunits"] = ToArrObj(naval),
            ["navy"] = ToArrObj(navy),
            ["ages"] = ToArrObj(ages),
            ["catOrders"] = cat,
            ["unitGroups"] = ToArrObj(unitGroups),
            ["missionTrees"] = slim,
            ["trees"] = treeObj,
        };
    }

    // ------------------------------------------------------------ 小工具

    private static JsonObject Attr(string label, string value) => new()
    {
        ["label"] = label,
        ["value"] = value,
    };

    private static JsonArray ToArr(IEnumerable<string> items)
    {
        var a = new JsonArray();
        foreach (var s in items) a.Add(s);
        return a;
    }

    private static JsonArray ToArrObj(IEnumerable<JsonObject> items)
    {
        var a = new JsonArray();
        foreach (var o in items) a.Add(o);
        return a;
    }
}

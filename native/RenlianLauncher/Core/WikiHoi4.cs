using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RenlianLauncher.Core;

/// <summary>
/// 钢铁雄心 IV 百科（旧版 backend/hoi4.py 的原生移植）：
/// 从 common/ideas、common/characters、common/national_focus、
/// common/country_leader、common/unit_leader 与 localisation/&lt;lang&gt;
/// 抽取国家理念 / 军官团与顾问 / 国策树。
///
/// 与旧版保持同一解析口径：
///   · 理念按 `ideas = { 分类 = { 条目 = { ... } } }` 的三层归属，单文件只扫一遍；
///   · 国策树按 `relative_position_id` 反复松弛成绝对网格坐标，前置/互斥保持脚本顺序；
///   · 修饰器名走 `MODIFIER_` 索引 + 人工别名 + 裸小写键四级兜底，单位按 `_tt` 模板判定。
/// 国策树完整节点只进内存索引，/api/wiki 响应里给瘦身的 focusTrees（与旧版一致）。
/// </summary>
internal static class WikiHoi4
{
    private const int H4Ver = 2;                 // 解析逻辑变更时 +1，参与缓存签名

    private static readonly string D_IDEAS = Path.Combine("common", "ideas");
    private static readonly string D_CHAR = Path.Combine("common", "characters");
    private static readonly string D_NF = Path.Combine("common", "national_focus");
    private static readonly string D_CL = Path.Combine("common", "country_leader");
    private static readonly string D_UL = Path.Combine("common", "unit_leader");
    private static readonly string D_MODDEF = Path.Combine("common", "modifier_definitions");
    private static readonly string D_LOC = "localisation";

    private static readonly Dictionary<string, string> LangMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh"] = "simp_chinese", ["en"] = "english", ["fr"] = "french",
        ["de"] = "german", ["sv"] = "english",
    };

    // ideas 顶层分类 → (中文名, 页面分组)
    private static readonly (string Key, string Label, string Group)[] IdeaCats =
    {
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
    };

    private static readonly Dictionary<string, string> IdeaCatLabel = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> IdeaCatGroup = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, string> SlotLabel = new(StringComparer.Ordinal)
    {
        ["political_advisor"] = "政治顾问",
        ["army_chief"] = "陆军部长",
        ["navy_chief"] = "海军部长",
        ["air_chief"] = "空军部长",
        ["high_command"] = "总司令",
        ["theorist"] = "理论家",
        ["industrial_concern"] = "工业企业",
        ["aircraft_manufacturer"] = "飞机制造商",
        ["materiel_manufacturer"] = "装备制造商",
        ["naval_manufacturer"] = "造船厂",
        ["tank_manufacturer"] = "坦克设计商",
    };

    private static readonly Dictionary<string, string> UlTraitType = new(StringComparer.Ordinal)
    {
        ["basic_trait"] = "基础",
        ["status_trait"] = "状态",
        ["personality_trait"] = "性格",
        ["assignable_trait"] = "可任命",
        ["basic_terrain_trait"] = "地形",
        ["assignable_terrain_trait"] = "可任命地形",
        ["exile"] = "流亡",
    };

    // 不在百科里展示的理念分类 / 条目
    private static readonly HashSet<string> SkipIdeaCats = new(StringComparer.Ordinal) { "hidden_ideas" };
    private static readonly HashSet<string> IdeaSkip = new(StringComparer.Ordinal)
    {
        "FIN_test", "ITA_test", "MTG_TEST_NAVAL_IDEA",
    };

    private static readonly Dictionary<string, string> CatFallback = new(StringComparer.Ordinal)
    {
        ["economy"] = "经济法案", ["mobilization_laws"] = "动员法案",
        ["trade_laws"] = "贸易法案", ["conscription_laws"] = "征兵法案",
        ["land"] = "陆军将领", ["operative"] = "特工",
    };

    // 游戏自己的本地化是运行时取值（[GER.GetBulwarkName]）或干脆没登记，手工补名
    private static readonly Dictionary<string, string> IdeaNameFix = new(StringComparer.Ordinal)
    {
        ["GER_bulwark_against_bolshevism"] = "反布尔什维克壁垒",
        ["special_economic_zones"] = "特别经济区",
        ["AST_no_foreign_battlefields_idea_2"] = "不在海外流血",
        ["AST_monarchist_intervention_spirit"] = "君主派干预精神",
        ["idea_RAJ_growing_independence_movement_0"] = "日益壮大的独立运动",
        ["idea_RAJ_growing_independence_movement_1"] = "独立运动愈演愈烈",
        ["idea_RAJ_to_shake_the_world"] = "震撼世界",
        ["CHI_obsolete_fortifications"] = "过时的防御工事",
        ["CHI_local_suppliers"] = "本地供应商",
        ["CZE_strike_at_the_heart_of_bolshevism_idea"] = "直击布尔什维克心脏",
        ["GRE_memories_of_the_anatolian_catastrophe"] = "安纳托利亚灾难的记忆",
        ["GRE_cooperate_with_foreign_monopolies_spirit"] = "与外国垄断资本合作",
        ["GRE_protectors_of_the_mediterranean_spirit"] = "地中海守护者",
        ["GRE_improving_our_defensive_lines"] = "加固我们的防线",
        ["idea_INS_radio_democratic"] = "广播电台：民主派",
        ["idea_INS_radio_fascist"] = "广播电台：法西斯派",
        ["idea_INS_radio_communist"] = "广播电台：共产派",
        ["idea_INS_radio_sukarno"] = "广播电台：苏加诺",
        ["ITA_the_fight_against_stalinism"] = "对抗斯大林主义",
        ["NOR_mobile_government_ns"] = "流动政府",
        ["idea_PAR_prevent_total_state_bankcruptcy"] = "避免国家彻底破产",
        ["idea_GUYA_weapons_licensing"] = "武器生产许可",
        ["SWE_small_scale_strikes"] = "小规模罢工",
        ["SWE_political_commisars"] = "政治委员",
        ["TUR_debt_repayments"] = "偿还外债",
        ["YUG_idea_princess_alexandrine_britain"] = "亚历山德琳公主：英国",
        ["YUG_idea_princess_alexandra_germany"] = "亚历山德拉公主：德国",
        ["YUG_idea_princess_marie_italy"] = "玛丽公主：意大利",
        ["GER_reichswerke"] = "帝国工厂",
        ["GER_reichswerke_improved"] = "帝国工厂（扩建）",
        ["GER_reichswerke_improved_2"] = "帝国工厂（再次扩建）",
        ["ARG_rubin_design_bureau"] = "鲁宾设计局",
        ["AST_kirov"] = "基洛夫工厂",
        ["BEL_kirov"] = "基洛夫工厂（比利时）",
        ["ETH_cie"] = "埃塞俄比亚铁路公司",
        ["machinefabriek_braat"] = "布拉特机械厂",
        ["nsm_amsterdam_ins"] = "阿姆斯特丹航运公司",
        ["artillerie_inrichtingen_ins"] = "炮兵器材厂",
        ["artillerie_inrichtingen_pyrotechnische"] = "烟火器材厂",
        ["bio_pharma"] = "生物制药公司",
        ["fokker_ins"] = "福克飞机厂",
        ["idea_INS_royal_dutch_shell_concern"] = "荷兰皇家壳牌",
        ["idea_INS_bank_of_java_concern"] = "爪哇银行",
        ["idea_INS_telkom_indonesia_concern"] = "印尼电信公司",
        ["idea_INS_staatspoorwegen_ss_concern"] = "荷兰国家铁路",
        ["germano_mexican_tanks"] = "德墨坦克合作",
        ["russo_mexican_tanks"] = "俄墨坦克合作",
        ["american_mexican_tanks"] = "美墨坦克合作",
    };

    // 只在代码里命名、本地化里没有 MODIFIER_ 键的高频修饰器
    private static readonly Dictionary<string, string> ModAlias = new(StringComparer.Ordinal)
    {
        ["industrial_capacity_factory"] = "民用工厂产出",
        ["industrial_capacity_dockyard"] = "船坞产出",
        ["production_speed_arms_factory_factor"] = "军用工厂建造速度",
        ["production_speed_industrial_complex_factor"] = "民用工厂建造速度",
        ["production_speed_dockyard_factor"] = "船坞建造速度",
        ["production_speed_infrastructure_factor"] = "基础设施建造速度",
        ["production_speed_bunker_factor"] = "地堡建造速度",
        ["production_speed_coastal_bunker_factor"] = "海岸地堡建造速度",
        ["production_speed_rail_way_factor"] = "铁路建造速度",
        ["production_speed_supply_node_factor"] = "补给枢纽建造速度",
        ["production_speed_air_base_factor"] = "空军基地建造速度",
        ["production_speed_radar_station_factor"] = "雷达站建造速度",
        ["production_speed_synthetic_refinery_factor"] = "合成炼油厂建造速度",
        ["production_speed_fuel_silo_factor"] = "燃料库建造速度",
        ["production_speed_anti_air_building_factor"] = "防空炮建造速度",
        ["production_speed_naval_base_factor"] = "海军基地建造速度",
        ["production_speed_nuclear_reactor_factor"] = "核反应堆建造速度",
        ["production_speed_nuclear_facility_factor"] = "核设施建造速度",
        ["communism_drift"] = "共产主义支持度变化",
        ["fascism_drift"] = "法西斯主义支持度变化",
        ["democratic_drift"] = "民主主义支持度变化",
        ["neutrality_drift"] = "中立主义支持度变化",
        ["communism_acceptance"] = "共产主义接受度",
        ["fascism_acceptance"] = "法西斯主义接受度",
        ["democratic_acceptance"] = "民主主义接受度",
        ["neutrality_acceptance"] = "中立主义接受度",
        ["send_volunteers_tension"] = "派遣志愿军所需紧张度",
        ["lend_lease_tension"] = "租借法案所需紧张度",
        ["join_faction_tension"] = "加入阵营所需紧张度",
        ["generate_wargoal_tension"] = "制造战争借口所需紧张度",
        ["guarantee_tension"] = "保障独立所需紧张度",
        ["political_advisor_cost_factor"] = "政治顾问花费",
        ["army_chief_cost_factor"] = "陆军部长花费",
        ["navy_chief_cost_factor"] = "海军部长花费",
        ["air_chief_cost_factor"] = "空军部长花费",
        ["high_command_cost_factor"] = "总司令花费",
        ["theorist_cost_factor"] = "理论家花费",
        ["industrial_concern_cost_factor"] = "工业企业花费",
        ["tank_manufacturer_cost_factor"] = "坦克设计商花费",
        ["aircraft_manufacturer_cost_factor"] = "飞机制造商花费",
        ["materiel_manufacturer_cost_factor"] = "装备制造商花费",
        ["naval_manufacturer_cost_factor"] = "造船厂花费",
        ["experience_gain_army_factor"] = "陆军经验获取",
        ["experience_gain_navy_factor"] = "海军经验获取",
        ["experience_gain_air_factor"] = "空军经验获取",
        ["experience_gain_army"] = "陆军经验获取",
        ["experience_gain_navy"] = "海军经验获取",
        ["experience_gain_air"] = "空军经验获取",
        ["experience_gain_factor"] = "经验获取",
        ["economy_cost_factor"] = "经济法案花费",
        ["mobilization_laws_cost_factor"] = "动员法案花费",
        ["trade_laws_cost_factor"] = "贸易法案花费",
        ["conscription_cost_factor"] = "征兵法案花费",
        ["non_core_manpower"] = "非核心人力",
        ["monthly_population"] = "每月人口",
        ["recruitable_population"] = "可征募人口",
        ["party_popularity_stability_factor"] = "党派支持度对稳定度影响",
        ["war_stability_factor"] = "战争对稳定度影响",
        ["offensive_war_stability_factor"] = "进攻战争对稳定度影响",
        ["defensive_war_stability_factor"] = "防御战争对稳定度影响",
        ["resistance_damage_to_garrison"] = "驻军所受抵抗伤害",
        ["resistance_damage_to_garrison_on_our_occupied_states"] = "我方被占领区驻军所受抵抗伤害",
        ["compliance_gain"] = "每日顺从度增长",
        ["foreign_subversive_activites"] = "外国颠覆活动",
        ["refit_speed"] = "改装速度",
        ["refit_ic_cost"] = "改装工业花费",
        ["convoy_escort_efficiency"] = "护航效率",
        ["sortie_efficiency"] = "出击效率",
        ["fighter_sortie_efficiency"] = "战斗机出击效率",
        ["naval_invasion_prep_speed"] = "海军入侵准备速度",
        ["movement"] = "移动速度",
        ["extra_trade_to_overlord_factor"] = "对宗主国的额外贸易",
        ["subjects_autonomy_gain"] = "附庸自治度增长",
        ["overlord_trade_cost_factor"] = "宗主国贸易花费",
        ["command_cap_increase"] = "指挥点数上限",
        ["command_power"] = "指挥点数",
        ["command_power_gain"] = "每日指挥点数增长",
        ["max_command_power"] = "指挥点数上限",
        ["military_industrial_organization_funds_gain"] = "军工组织资金获取",
        ["military_industrial_organization_research_bonus"] = "军工组织研究加成",
        ["promote_cost_factor"] = "晋升花费",
        ["critical_receive_chance"] = "被暴击概率",
        ["specialization_nuclear_speed_factor"] = "核研究速度",
        ["bunker_max_level_terrain_limit"] = "地堡最高等级地形限制",
        ["supply_combat_penalties_on_core_factor"] = "本土补给战斗惩罚",
        ["army_sub_unit_militia_attack_factor"] = "民兵攻击",
        ["army_sub_unit_militia_defence_factor"] = "民兵防御",
        ["army_sub_unit_militia_max_org_factor"] = "民兵最大组织度",
        ["modifier_army_sub_unit_militia_attack_factor"] = "民兵攻击",
        ["modifier_army_sub_unit_militia_defence_factor"] = "民兵防御",
        ["modifier_army_sub_unit_militia_max_org_factor"] = "民兵最大组织度",
    };

    private static readonly string[] ForcePct =
    {
        "_drift", "_acceptance", "_tension", "_speed", "_efficiency",
        "_chance", "weekly_", "daily_",
    };

    private static readonly string[] AbsSuffix =
    {
        "_add", "_value", "_amount", "_count", "_cap", "_cap_add",
        "_cost", "_level", "_size", "_limit",
    };

    // 将领/领袖特质
    private static readonly Dictionary<string, string> UlTypeLabel = new(StringComparer.Ordinal)
    {
        ["all"] = "陆军通用", ["field_marshal"] = "元帅", ["corps_commander"] = "军团长",
        ["navy"] = "海军将领", ["air"] = "空军将领", ["land"] = "陆军将领", ["operative"] = "特工",
    };

    private static readonly (string Key, string Label)[] UlSkills =
    {
        ("attack_skill", "攻击"), ("defense_skill", "防御"), ("planning_skill", "计划"),
        ("logistics_skill", "后勤"), ("maneuvering_skill", "机动"), ("coordination_skill", "协调"),
    };

    private static readonly HashSet<string> UlSkillKeys = new(StringComparer.Ordinal)
    {
        "attack_skill", "defense_skill", "planning_skill", "logistics_skill", "maneuvering_skill",
        "coordination_skill", "attack_skill_factor", "defense_skill_factor", "planning_skill_factor",
        "logistics_skill_factor", "maneuvering_skill_factor", "coordination_skill_factor",
    };

    private static readonly HashSet<string> TraitSkip = new(StringComparer.Ordinal)
    {
        "type", "trait_type", "gui_row", "gui_column", "random", "show_in_combat",
        "levels", "cost", "x", "y", "name", "desc", "icon", "sprite", "picture",
        "ai_will_do", "custom_effect_tooltip", "override_effect_tooltip",
        "custom_prerequisite_tooltip", "custom_gain_xp_trigger_tooltip",
        "gain_xp_on_spotting", "mutually_exclusive", "num_parents_needed",
        "ledger", "removal_cost", "available", "visible", "allowed",
        "prerequisites", "gain_xp", "gain_xp_leader", "special_force",
    };

    private static readonly Dictionary<string, string> TraitNameFix = new(StringComparer.Ordinal)
    {
        ["ww_one_trauma"] = "一战创伤",
        ["tenacious_defender"] = "顽强防御者",
        ["political_prisoner"] = "政治犯",
        ["assassination_survivor"] = "暗杀幸存者",
        ["union_man"] = "工会分子",
        ["SWE_former_autocrat_upgraded"] = "前独裁者（转型后）",
        ["personal_union_trait"] = "共主联邦",
        ["light_aircraft_manufacturer_3"] = "轻型飞机制造商（三级）",
        ["fast_tank_manufacturer_2"] = "快速坦克制造商（二级）",
        ["infantry_tank_manufacturer_2"] = "步兵坦克制造商（二级）",
        ["CZE_exiled_president"] = "流亡总统",
        ["INS_secret_commander"] = "秘密指挥官",
    };

    // 国策奖励里能可靠直译的指令
    private static readonly Dictionary<string, string> RewardPct = new(StringComparer.Ordinal)
    {
        ["add_stability"] = "稳定度",
        ["add_war_support"] = "战争支持度",
        ["add_breakthrough_progress"] = "突破进度",
    };

    private static readonly Dictionary<string, string> RewardAbs = new(StringComparer.Ordinal)
    {
        ["add_political_power"] = "政治点数",
        ["army_experience"] = "陆军经验",
        ["navy_experience"] = "海军经验",
        ["air_experience"] = "空军经验",
        ["add_command_power"] = "指挥点数",
        ["add_research_slot"] = "研究槽",
        ["add_manpower"] = "人力",
        ["add_fuel"] = "燃料",
        ["add_equipment_to_stockpile"] = "装备库存",
    };

    private static readonly HashSet<string> RewardDescend = new(StringComparer.Ordinal)
    {
        "if", "else_if", "else", "hidden_effect", "random_list",
        "random_owned_controlled_state", "every_owned_state", "every_core_state",
        "random_core_state", "every_controlled_state", "every_state",
        "every_other_country", "random_other_country", "every_country",
        "every_army_leader", "random_army_leader", "every_navy_leader",
        "every_air_leader", "every_character", "random_character",
        "any_owned_state", "random_owned_state", "capital_scope",
    };

    private static readonly Dictionary<string, string> TreeNameFix = new(StringComparer.Ordinal)
    {
        ["horn_of_africa_tree"] = "埃塞俄比亚之角",
        ["china_communist_warlord_focus"] = "中国共产军阀",
    };

    private static readonly Dictionary<string, string> FilterFix = new(StringComparer.Ordinal)
    {
        ["FOCUS_FILTER_INDUSTRIAL"] = "工业",
        ["FOCUS_FILTER_MISSIOLINI"] = "墨索里尼的任务",
        ["FOCUS_FILTER_BALANCE_OF_POWERS"] = "权力平衡",
    };

    private static readonly HashSet<string> TagStop = new(StringComparer.Ordinal)
    {
        "AND", "OR", "NOT", "DLC", "HAS", "ANY", "ALL", "YES", "NO", "THE",
    };

    // ------------------------------------------------------------ 运行期状态

    private static readonly object StateLock = new();
    private static readonly Dictionary<string, JsonObject> FocusTrees = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _locCur = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> ModNameIdx = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (bool Pct, int Dec)> ModSpec = new(StringComparer.Ordinal);

    // ------------------------------------------------------------ 入口

    /// <summary>读取（并缓存）钢铁雄心百科。失败时返回与旧版同形的空结构。</summary>
    public static JsonObject Load(string gameRoot, string uiLang, bool force)
    {
        uiLang = string.IsNullOrWhiteSpace(uiLang) ? "zh" : uiLang.Trim().ToLowerInvariant();
        var locLang = LangMap.TryGetValue(uiLang, out var ll) ? ll : "simp_chinese";

        var root = GameRoot(gameRoot);
        if (root.Length == 0)
            return new JsonObject
            {
                ["ok"] = false,
                ["supported"] = true,
                ["game"] = "hoi4",
                ["error"] = "没找到《钢铁雄心 IV》安装目录，请在设置里把游戏根目录指向 Hearts of Iron IV 文件夹",
            };

        var dirs = new[] { D_IDEAS, D_CHAR, D_NF, D_CL, D_UL }
            .Select(d => Path.Combine(root, d)).ToList();
        dirs.Add(Path.Combine(root, D_LOC, locLang));
        var sig = H4Ver + "|" + locLang + "|" + Sig(dirs);

        long ms = 0;
        var data = Wiki.Cached("hoi4", locLang, root, sig, force, () =>
        {
            var t0 = Environment.TickCount64;
            var d = Build(root, locLang);
            ms = Environment.TickCount64 - t0;
            return d;
        }, out var cached);
        if (!cached) data["buildMs"] = (int)ms;

        // 完整国策树只留在内存索引里，响应给瘦身视图（与旧版 focusTrees 一致）
        if (data["trees"] is JsonArray full)
        {
            lock (StateLock)
            {
                FocusTrees.Clear();
                foreach (var t in full)
                    if (t is JsonObject to) FocusTrees[(string?)to["id"] ?? ""] = to;
            }
            var slim = new JsonArray();
            foreach (var t in full)
            {
                if (t is not JsonObject to) continue;
                var s = new JsonObject();
                foreach (var kv in to)
                    if (kv.Key != "focuses") s[kv.Key] = kv.Value?.DeepClone();
                slim.Add(s);
            }
            data["focusTrees"] = slim;
            data.Remove("trees");
        }
        return data;
    }

    /// <summary>单棵国策树的完整节点（前端切树时按需拉取）。</summary>
    public static JsonObject FocusTree(string gameRoot, string tid, string uiLang)
    {
        JsonObject? t;
        lock (StateLock) FocusTrees.TryGetValue((tid ?? "").Trim(), out t);
        if (t is null)
        {
            try { Load(gameRoot, uiLang, false); } catch { /* 冷启动构建失败按 not found 处理 */ }
            lock (StateLock) FocusTrees.TryGetValue((tid ?? "").Trim(), out t);
        }
        if (t is null) return new JsonObject { ["ok"] = false, ["error"] = "not found" };
        t["ok"] = true;
        return t;
    }

    private static string GameRoot(string hint)
    {
        hint = (hint ?? "").Trim().Trim('"').TrimEnd('\\', '/');
        foreach (var cand in new[] { hint, hint.Length > 0 ? Path.Combine(hint, "Hearts of Iron IV") : "" })
            if (cand.Length > 0 && Directory.Exists(Path.Combine(cand, D_IDEAS))) return cand;
        // 设置里没填对时按 Steam 库常见位置兜底探测（与旧版一致）
        foreach (var drive in new[] { "C", "D", "E", "F", "G" })
            foreach (var sub in new[] { "Steam", "SteamLibrary", "Games", "Program Files (x86)\\Steam" })
            {
                var c = Path.Combine(drive + ":\\", sub, "steamapps", "common", "Hearts of Iron IV");
                if (Directory.Exists(Path.Combine(c, D_IDEAS))) return c;
            }
        return "";
    }

    private static string Sig(IEnumerable<string> paths)
    {
        var parts = new List<string>();
        foreach (var p in paths)
        {
            try
            {
                var mtime = new DateTimeOffset(Directory.Exists(p)
                    ? Directory.GetLastWriteTimeUtc(p) : File.GetLastWriteTimeUtc(p)).ToUnixTimeSeconds();
                var size = Directory.Exists(p)
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
            return blob?["rawVersion"]?.ToString() ?? "";
        }
        catch { return ""; }
    }

    // ------------------------------------------------------------ 基础解析

    private sealed record Blk(string Key, string Body, int Depth, int Start);
    private sealed record Sca(string Key, string Value, int Depth, int Start);

    private static readonly Regex AtDefRe =
        new(@"^\s*@([A-Za-z0-9_]+)\s*=\s*(-?\d+(?:\.\d+)?)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex AtUseRe = new(@"@([A-Za-z0-9_]+)", RegexOptions.Compiled);
    private static readonly Regex TokRe = new(
        @"([A-Za-z0-9_\-.]+)\s*=\s*\{|\{|\}|([A-Za-z0-9_\-.]+)\s*=\s*(""(?:[^""\\]|\\.)*""|[^\s{}\n]+)",
        RegexOptions.Compiled);

    /// <summary>单遍扫描脚本，返回所有块与标量（带层级与起点），供区间二分归属。</summary>
    private static (List<Blk> Blocks, List<Sca> Scalars) ScanPairs(string text)
    {
        var blocks = new List<Blk>();
        var scal = new List<Sca>();
        var stack = new List<(string? Key, int Start)>();
        for (var m = TokRe.Match(text ?? ""); m.Success; m = m.NextMatch())
        {
            var g1 = m.Groups[1].Value;
            if (g1.Length > 0)
            {
                stack.Add((g1, m.Index + m.Length));
            }
            else if (m.Value == "{")
            {
                stack.Add((null, m.Index + m.Length));
            }
            else if (m.Value == "}")
            {
                if (stack.Count == 0) continue;
                var (k, st) = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                if (k is not null)
                    blocks.Add(new Blk(k, text.Substring(st, m.Index - st), stack.Count, st));
            }
            else if (m.Groups[2].Success)
            {
                scal.Add(new Sca(m.Groups[2].Value, m.Groups[3].Value.Trim('"'), stack.Count, m.Index));
            }
        }
        return (blocks, scal);
    }

    private static List<Blk> ByDepth(List<Blk> all, int depth) =>
        all.Where(b => b.Depth == depth).OrderBy(b => b.Start).ToList();

    private static List<Sca> ByDepth(List<Sca> all, int depth) =>
        all.Where(s => s.Depth == depth).OrderBy(s => s.Start).ToList();

    private static int LowerBound<T>(List<T> items, int value, Func<T, int> start)
    {
        int lo = 0, hi = items.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (start(items[mid]) < value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static List<Blk> InRange(List<Blk> items, int lo, int hi)
    {
        var res = new List<Blk>();
        for (var i = LowerBound(items, lo, b => b.Start); i < items.Count; i++)
        {
            if (items[i].Start >= hi) break;
            res.Add(items[i]);
        }
        return res;
    }

    private static List<Sca> InRange(List<Sca> items, int lo, int hi)
    {
        var res = new List<Sca>();
        for (var i = LowerBound(items, lo, s => s.Start); i < items.Count; i++)
        {
            if (items[i].Start >= hi) break;
            res.Add(items[i]);
        }
        return res;
    }

    /// <summary>把 `@Name = 10` 这类文件内脚本变量替换成数值（HOI4 大量使用）。</summary>
    private static string AtSubst(string text)
    {
        var defs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in AtDefRe.Matches(text)) defs[m.Groups[1].Value] = m.Groups[2].Value;
        if (defs.Count == 0) return text;
        return AtUseRe.Replace(text, m => defs.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }

    private static string ReadScript(string path)
    {
        try
        {
            var t = File.ReadAllText(path, Encoding.UTF8);
            if (t.Length > 0 && t[0] == '\uFEFF') t = t[1..];
            t = Regex.Replace(t, "#[^\n]*", "");
            return AtSubst(t);
        }
        catch { return ""; }
    }

    private static List<(string Key, string Body)> TopBlocks(string text) =>
        ByDepth(ScanPairs(text).Blocks, 0).Select(b => (b.Key, b.Body)).ToList();

    private static List<(string Key, string Value)> TopScalars(string text) =>
        ByDepth(ScanPairs(text).Scalars, 0).Select(s => (s.Key, s.Value)).ToList();

    /// <summary>只在顶层找 `key = { ... }`（避免抓到 ai_will_do 里的同名嵌套块）。</summary>
    private static string? SubTop(string? body, string key)
    {
        if (string.IsNullOrEmpty(body)) return null;
        foreach (var b in ByDepth(ScanPairs(body).Blocks, 0))
            if (b.Key == key) return b.Body;
        return null;
    }

    private static string? Val(string? body, string key)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var m = Regex.Match(body, "(?<![A-Za-z0-9_])" + Regex.Escape(key) + @"\s*=\s*([^\s{}\n]+)");
        return m.Success ? m.Groups[1].Value.Trim('"') : null;
    }

    private static double? Num(string? v)
    {
        if (string.IsNullOrEmpty(v)) return null;
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    // ------------------------------------------------------------ 本地化

    private static readonly Regex LocLineRe =
        new("^\\s*([A-Za-z0-9_.\\-]+)\\s*:\\s*\\d*\\s*\"", RegexOptions.Compiled);
    private static readonly Regex ColorRe = new("§[A-Za-z0-9!]", RegexOptions.Compiled);
    private static readonly Regex IconRe = new("£[^£\\s]{1,40}£", RegexOptions.Compiled);
    private static readonly Regex ScriptRe = new(@"\[[^\[\]]{0,80}\]", RegexOptions.Compiled);
    private static readonly Regex RefRe = new(@"\$([A-Za-z0-9_.\-]+)(?:\|[^$]*)?\$", RegexOptions.Compiled);

    /// <summary>读整个本地化目录（yml，一个语言几百个文件）。</summary>
    private static Dictionary<string, string> LoadLoc(string locDir)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(locDir)) return d;
        foreach (var f in Directory.GetFiles(locDir, "*.yml").OrderBy(p => p, StringComparer.Ordinal))
        {
            string txt;
            try { txt = File.ReadAllText(f, Encoding.UTF8); } catch { continue; }
            if (txt.Length > 0 && txt[0] == '\uFEFF') txt = txt[1..];
            int i = 0, n = txt.Length;
            while (i < n)
            {
                var nl = txt.IndexOf('\n', i);
                if (nl < 0) nl = n;
                var line = txt.Substring(i, nl - i);
                i = nl + 1;
                var m = LocLineRe.Match(line);
                if (!m.Success) continue;
                var key = m.Groups[1].Value;
                var lineStart = i - line.Length - 1;
                var sb = new StringBuilder();
                var k = lineStart + m.Index + m.Length;
                while (k < n)
                {
                    var c = txt[k];
                    if (c == '\\' && k + 1 < n) { sb.Append(txt, k, 2); k += 2; continue; }
                    if (c == '"') break;
                    sb.Append(c);
                    k++;
                }
                d[key] = sb.ToString();
                i = k + 1;
                while (i < n && txt[i] == '\n') i++;
            }
        }
        return d;
    }

    /// <summary>清洗本地化文本：展开 `$REF$`、删颜色 / 图标 / 运行时取值标记。</summary>
    private static string CleanLoc(string s, Dictionary<string, string> loc, int depth = 0)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = RefRe.Replace(s, m =>
        {
            var k = m.Groups[1].Value;
            if (k.Equals("RIGHT", StringComparison.OrdinalIgnoreCase)) return "";
            if (loc.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v) && depth < 3)
                return CleanLoc(v, loc, depth + 1);
            return "";
        });
        s = s.Replace("\\n", "\n");
        s = ColorRe.Replace(s, "");
        s = IconRe.Replace(s, "");
        s = ScriptRe.Replace(s, "");
        s = s.Replace("\\t", " ").Replace("\t", " ");
        s = Regex.Replace(s, "[ \u00a0]+", " ");
        s = Regex.Replace(s, "\n{3,}", "\n\n");
        return s.Trim();
    }

    /// <summary>按顺序取第一个非空本地化键。</summary>
    private static string L(Dictionary<string, string> loc, params string?[] keys)
    {
        foreach (var k in keys)
            if (!string.IsNullOrEmpty(k) && loc.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v))
                return CleanLoc(v, loc);
        return "";
    }

    private static string Flatten(string txt) => Regex.Replace(txt ?? "", @"\s+", " ").Trim();

    // ------------------------------------------------------------ 修饰器

    private static void BuildModIndex(Dictionary<string, string> loc)
    {
        ModNameIdx.Clear();
        ModSpec.Clear();
        foreach (var k in loc.Keys)
        {
            string raw;
            if (k.StartsWith("MODIFIER_", StringComparison.Ordinal)) raw = k[9..].ToLowerInvariant();
            else if (k.StartsWith("MODIFIERS_", StringComparison.Ordinal)) raw = k[10..].ToLowerInvariant();
            else continue;
            ModNameIdx.TryAdd(raw, k);
            foreach (var pre in new[] { "country_", "state_", "unit_", "army_", "navy_", "air_" })
                if (raw.StartsWith(pre, StringComparison.Ordinal)) ModNameIdx.TryAdd(raw[pre.Length..], k);
            foreach (var suf in new[] { "_factor", "_mult", "_modifier" })
                if (raw.EndsWith(suf, StringComparison.Ordinal)) ModNameIdx.TryAdd(raw[..^suf.Length], k);
        }
        foreach (var k in loc.Keys)
        {
            if (!k.EndsWith("_tt", StringComparison.Ordinal)) continue;
            var baseKey = k[..^3].ToLowerInvariant();
            var tpl = loc[k] ?? "";
            var m = Regex.Match(tpl, @"\$RIGHT\|[+\-=]*(%?)(\d?)\$");
            if (m.Success)
                ModSpec[baseKey] = (m.Groups[1].Value.Length > 0, m.Groups[2].Value.Length > 0
                    ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0);
        }
        foreach (var k in ModSpec.Keys.ToList())
            foreach (var suf in new[] { "_factor", "_gain", "_cost" })
                if (k.EndsWith(suf, StringComparison.Ordinal)) ModSpec.TryAdd(k[..^suf.Length], ModSpec[k]);
    }

    private static string ModName(string key)
    {
        var lo = (key ?? "").ToLowerInvariant();
        if (ModAlias.TryGetValue(lo, out var direct)) return direct;
        foreach (var pre in new[] { "modifier_", "modifiers_" })
            if (lo.StartsWith(pre, StringComparison.Ordinal))
            {
                lo = lo[pre.Length..];
                if (ModAlias.TryGetValue(lo, out var hit)) return hit;
            }
        foreach (var cand in new[]
                 {
                     lo, "modifier_" + lo, lo.Replace("country_", ""),
                     lo.Replace("_factor", ""), lo.Replace("_mult", ""), lo.Replace("_add", ""),
                 })
        {
            if (_locCur.TryGetValue(cand, out var t) && !string.IsNullOrEmpty(t))
                return CleanLoc(t, _locCur);
            if (ModNameIdx.TryGetValue(cand, out var mk) && _locCur.TryGetValue(mk, out var mv))
                return CleanLoc(mv, _locCur);
        }
        return "";
    }

    private static bool ModPct(string key, double val)
    {
        var lo = (key ?? "").ToLowerInvariant();
        if (ModSpec.TryGetValue(lo, out var spec)) return spec.Pct;
        if (lo.EndsWith("_factor", StringComparison.Ordinal) || lo.EndsWith("_mult", StringComparison.Ordinal))
            return true;
        if (AbsSuffix.Any(s => lo.EndsWith(s, StringComparison.Ordinal))) return false;
        if (ForcePct.Any(s => lo.Contains(s, StringComparison.Ordinal))) return true;
        return Math.Abs(val) > 0 && Math.Abs(val) < 1;
    }

    private static string FmtNum(double v, bool pct, int dec = 0)
    {
        var sign = v > 0 ? "+" : (v < 0 ? "-" : "");
        var a = Math.Abs(v);
        if (pct)
        {
            a *= 100;
            if (dec <= 0) dec = 0;
        }
        else
        {
            dec = Math.Abs(a - Math.Round(a)) < 1e-9 ? 0 : (dec > 0 ? dec : 1);
        }
        var s = a.ToString("F" + dec, CultureInfo.InvariantCulture);
        if (s.Contains('.')) s = s.TrimEnd('0').TrimEnd('.');
        return sign + s + (pct ? "%" : "");
    }

    /// <summary>把一条 modifier 变成「政治点数 +25%」这样的可读文本。</summary>
    private static string ModLine(string key, string val)
    {
        var v = Num(val);
        if (v is null) return key + "：" + val;
        var name = ModName(key);
        if (string.IsNullOrEmpty(name)) name = key.Replace("_", " ");
        var pct = ModPct(key, v.Value);
        var dec = 0;
        if (ModSpec.TryGetValue((key ?? "").ToLowerInvariant(), out var spec) && pct) dec = spec.Dec;
        return name + " " + FmtNum(v.Value, pct, dec);
    }

    private static readonly Regex ModNumRe =
        new(@"([A-Za-z0-9_]+)\s*=\s*(-?\d+(?:\.\d+)?)", RegexOptions.Compiled);

    private static List<string> ModBlock(string? body)
    {
        var outp = new List<string>();
        if (string.IsNullOrEmpty(body)) return outp;
        foreach (Match m in ModNumRe.Matches(body)) outp.Add(ModLine(m.Groups[1].Value, m.Groups[2].Value));
        foreach (Match m in Regex.Matches(body, @"custom_modifier_tooltip\s*=\s*([A-Za-z0-9_]+)"))
        {
            var txt = L(_locCur, m.Groups[1].Value);
            if (string.IsNullOrEmpty(txt)) continue;
            txt = Regex.Replace(txt, @"([。！？；.!?;])\s*\n+\s*", "$1");
            txt = Regex.Replace(txt, @"\s*\n+\s*", "；").Trim('；', ' ');
            if (txt.Length > 0 && !outp.Contains(txt)) outp.Add(txt);
        }
        return outp;
    }

    private static string EquipName(string key, Dictionary<string, string> loc)
    {
        foreach (var cand in new[] { key, key + "_name", "EQUIPMENT_" + key.ToUpperInvariant() })
            if (loc.TryGetValue(cand, out var v) && !string.IsNullOrEmpty(v))
                return CleanLoc(v, loc);
        var stripped = key.Replace("_equipment", "");
        if (loc.TryGetValue(stripped, out var sv) && !string.IsNullOrEmpty(sv)) return CleanLoc(sv, loc);
        return key.Replace("_", " ");
    }

    private static List<string> EquipBonusLines(string? body, Dictionary<string, string> loc)
    {
        var outp = new List<string>();
        if (string.IsNullOrEmpty(body)) return outp;
        foreach (var (eq, eb) in TopBlocks(body))
        {
            var nm = EquipName(eq, loc);
            foreach (var ln in ModBlock(eb)) outp.Add(nm + "：" + ln);
        }
        return outp;
    }

    // ------------------------------------------------------------ 理念

    private static (List<JsonObject> Items, Dictionary<string, JsonObject> Idx,
        Dictionary<string, List<string>> TraitKeys) ScanIdeas(string root, Dictionary<string, string> loc)
    {
        var items = new List<JsonObject>();
        var idx = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var traitKeys = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var f in Wiki.Glob(root, D_IDEAS))
        {
            var baseName = Path.GetFileName(f);
            if (baseName.StartsWith("_debug", StringComparison.Ordinal)) continue;
            var txt = ReadScript(f);
            if (!txt.Contains("ideas", StringComparison.Ordinal)) continue;

            var (allBlocks, allScal) = ScanPairs(txt);
            var bd0 = ByDepth(allBlocks, 0); var bd1 = ByDepth(allBlocks, 1);
            var bd2 = ByDepth(allBlocks, 2); var bd3 = ByDepth(allBlocks, 3);
            var sd3 = ByDepth(allScal, 3);

            foreach (var top in bd0)
            {
                if (top.Key != "ideas") continue;
                var en = top.Start + top.Body.Length;
                foreach (var cat in InRange(bd1, top.Start, en))
                {
                    if (SkipIdeaCats.Contains(cat.Key)) continue;
                    var catLabel = IdeaCatLabel.TryGetValue(cat.Key, out var lbl)
                        ? lbl
                        : (CatFallback.TryGetValue(cat.Key, out var fb) ? fb : cat.Key.Replace("_", " "));
                    var cen = cat.Start + cat.Body.Length;
                    foreach (var idea in InRange(bd2, cat.Start, cen))
                    {
                        var key = idea.Key;
                        var ien = idea.Start + idea.Body.Length;
                        var scal = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var s in InRange(sd3, idea.Start, ien)) scal.TryAdd(s.Key, s.Value);
                        var subs = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var s in InRange(bd3, idea.Start, ien)) subs.TryAdd(s.Key, s.Body);

                        var tkeys = new List<string>();
                        if (subs.TryGetValue("traits", out var tb))
                            tkeys = Regex.Matches(tb, "[A-Za-z0-9_]+").Select(m => m.Value).ToList();

                        scal.TryGetValue("name", out var nameAttr);
                        var name = string.IsNullOrEmpty(nameAttr) ? key : nameAttr;
                        subs.TryGetValue("modifier", out var mod);
                        scal.TryGetValue("cost", out var cost);

                        var info = new JsonObject
                        {
                            ["cat"] = cat.Key,
                            ["traits"] = ToArr(tkeys),
                            ["modifier"] = mod ?? "",
                            ["cost"] = cost is null ? null : JsonValue.Create(cost),
                        };
                        idx[key] = info;

                        var desc = L(loc, key + "_desc");
                        var effects = new List<string>();
                        if (!string.IsNullOrEmpty(mod)) effects.AddRange(ModBlock(mod));
                        if (subs.TryGetValue("research_bonus", out var rb))
                            foreach (Match m in ModNumRe.Matches(rb))
                            {
                                var nm = CleanLoc(loc.TryGetValue(m.Groups[1].Value, out var rv) ? rv : "", loc);
                                if (nm.Length == 0) nm = m.Groups[1].Value.Replace("_", " ");
                                var rn = Num(m.Groups[2].Value);
                                effects.Add(nm + "研究速度 " + FmtNum(rn ?? 0, true));
                            }
                        if (subs.TryGetValue("equipment_bonus", out var eb))
                            effects.AddRange(EquipBonusLines(eb, loc));
                        foreach (var subKey in new[] { "targeted_modifier", "research_bonus_air", "rule" })
                            if (subs.TryGetValue(subKey, out var sb))
                                effects.AddRange(ModBlock(sb));

                        var attrs = new List<JsonObject>();
                        if (!string.IsNullOrEmpty(cost))
                            attrs.Add(new JsonObject { ["label"] = "政治点数花费", ["value"] = cost });
                        scal.TryGetValue("removal_cost", out var rcost);
                        if (!string.IsNullOrEmpty(rcost) && rcost is not ("-1" or "0"))
                            attrs.Add(new JsonObject { ["label"] = "移除花费", ["value"] = rcost });

                        var disp = Flatten(L(loc, name));
                        if (disp.Length == 0 && name != key) disp = Flatten(L(loc, key));
                        if (disp.Length == 0 && IdeaNameFix.TryGetValue(key, out var fixedName)) disp = fixedName;
                        if (IdeaSkip.Contains(key)) continue;

                        // 通用顾问槽位 / 占位条目 / 查不到名字的：不进「国家理念」，避免露出裸键
                        var isAdvisor = IdeaCatGroup.TryGetValue(cat.Key, out var grp) && grp == "advisor";
                        if (isAdvisor && effects.Count == 0 && disp.Length == 0) continue;
                        if (effects.Count == 0 && disp.Length == 0 && desc.Length == 0
                            && tkeys.Count == 0 && key.EndsWith("_dummy", StringComparison.Ordinal)) continue;
                        if (effects.Count == 0 && disp.Length == 0 && desc.Length == 0 && tkeys.Count == 0) continue;
                        if (disp.Length == 0) continue;

                        var item = new JsonObject
                        {
                            ["key"] = key,
                            ["name"] = disp,
                            ["desc"] = desc,
                            ["cat"] = catLabel,
                            ["catKey"] = cat.Key,
                            ["effects"] = ToArr(effects),
                            ["attrs"] = ToArrObj(attrs),
                            ["links"] = new JsonArray(),
                            ["traits"] = ToArr(tkeys.Select(t => L(loc, t) is { Length: > 0 } tv ? tv : t)),
                        };
                        traitKeys[key] = tkeys;
                        items.Add(item);
                    }
                }
            }
        }
        return (items, idx, traitKeys);
    }

    // ------------------------------------------------------------ 特质

    private static JsonObject TraitItem(string key, string body, Dictionary<string, string> loc, string kind)
    {
        var typ = Val(body, "type") ?? "all";
        string cat, catKey;
        if (kind == "cl") { cat = "国家领袖与顾问"; catKey = "cl"; }
        else
        {
            cat = UlTypeLabel.TryGetValue(typ, out var l) ? l : typ;
            catKey = "ul_" + typ;
        }

        var effects = new List<string>();
        // 效果既可写在 modifier 块里，也可像 dictator 那样平铺在块内
        foreach (var (k, v) in TopScalars(body))
        {
            if (TraitSkip.Contains(k) || UlSkillKeys.Contains(k) || k.StartsWith("ai_", StringComparison.Ordinal)) continue;
            var nv = Num(v);
            if (nv is null || nv == 0) continue;
            effects.Add(ModLine(k, v));
        }
        foreach (var subKey in new[]
                 {
                     "modifier", "non_shared_modifier", "corps_commander_modifier",
                     "field_marshal_modifier", "sub_unit_modifiers",
                 })
        {
            var sb = SubTop(body, subKey);
            if (!string.IsNullOrEmpty(sb)) effects.AddRange(ModBlock(sb));
        }
        foreach (var tipKey in new[] { "custom_effect_tooltip", "override_effect_tooltip" })
        {
            var tip = Val(body, tipKey);
            if (string.IsNullOrEmpty(tip)) continue;
            var txt = L(loc, tip);
            if (txt.Length > 0) effects.Add(Flatten(txt));
        }

        var attrs = new List<JsonObject>();
        foreach (var (k, label) in UlSkills)
        {
            var v = Val(body, k);
            var nv = Num(v);
            if (v is not null && nv is not null && nv != 0)
                attrs.Add(new JsonObject { ["label"] = label, ["value"] = "+" + (int)nv.Value });
        }
        var ttype = Val(body, "trait_type");
        if (!string.IsNullOrEmpty(ttype))
            attrs.Insert(0, new JsonObject
            {
                ["label"] = "类别",
                ["value"] = UlTraitType.TryGetValue(ttype, out var tl) ? tl : ttype,
            });

        return new JsonObject
        {
            ["key"] = key,
            ["name"] = Flatten(L(loc, key)) is { Length: > 0 } nm
                ? nm : (TraitNameFix.TryGetValue(key, out var fx) ? fx : key),
            ["desc"] = L(loc, key + "_desc"),
            ["cat"] = cat,
            ["catKey"] = catKey,
            ["effects"] = ToArr(effects),
            ["attrs"] = ToArrObj(attrs),
            ["links"] = new JsonArray(),
            ["type"] = typ,
        };
    }

    private static List<JsonObject> ScanTraits(string root, Dictionary<string, string> loc)
    {
        var items = new List<JsonObject>();
        foreach (var f in Wiki.Glob(root, D_CL))
            foreach (var (k, b) in TopBlocks(ReadScript(f)))
            {
                if (k != "leader_traits") continue;
                foreach (var (tk, tb) in TopBlocks(b)) items.Add(TraitItem(tk, tb, loc, "cl"));
            }
        foreach (var f in Wiki.Glob(root, D_UL))
            foreach (var (k, b) in TopBlocks(ReadScript(f)))
            {
                if (k != "leader_traits") continue;
                foreach (var (tk, tb) in TopBlocks(b)) items.Add(TraitItem(tk, tb, loc, "ul"));
            }
        return items;
    }

    // ------------------------------------------------------------ 顾问

    private static List<JsonObject> ScanAdvisors(string root, Dictionary<string, string> loc,
        Dictionary<string, JsonObject> ideaIdx, Dictionary<string, JsonObject> traitFx)
    {
        var items = new List<JsonObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in Wiki.Glob(root, D_CHAR))
        {
            var txt = ReadScript(f);
            foreach (var blk in ScanPairs(txt).Blocks)
            {
                if (blk.Key != "characters") continue;
                var b = blk.Body;
                foreach (var (ck, cb) in TopBlocks(b))
                {
                    var ab = SubTop(cb, "advisor");
                    if (ab is null) continue;
                    var slot = Val(ab, "slot") ?? "political_advisor";
                    var token = Val(ab, "idea_token") ?? ck;
                    var name = L(loc, Val(cb, "name") ?? ck);
                    if (name.Length == 0) continue;
                    var sig = slot + "|" + name + "|" + token;
                    if (!seen.Add(sig)) continue;

                    ideaIdx.TryGetValue(token, out var idea);
                    var traits = new List<string>();
                    var tb = SubTop(ab, "traits");
                    if (!string.IsNullOrEmpty(tb))
                        traits.AddRange(Regex.Matches(tb, "[A-Za-z0-9_]+").Select(m => m.Value));
                    if (idea?["traits"] is JsonArray itraits)
                        foreach (var t in itraits)
                        {
                            var ts = t?.ToString() ?? "";
                            if (ts.Length > 0 && !traits.Contains(ts)) traits.Add(ts);
                        }

                    var effects = new List<string>();
                    foreach (var t in traits)
                    {
                        if (!traitFx.TryGetValue(t, out var fx)) continue;
                        var fdesc = fx["desc"]?.ToString() ?? "";
                        if (fdesc.Length > 0)
                            effects.Add("【" + (fx["name"]?.ToString() ?? t) + "】" + Flatten(fdesc));
                        if (fx["effects"] is JsonArray feffs)
                            foreach (var e in feffs) effects.Add(e?.ToString() ?? "");
                    }
                    if (idea?["modifier"]?.ToString() is { Length: > 0 } imod)
                        effects.AddRange(ModBlock(imod));

                    var attrs = new List<JsonObject>
                    {
                        new() { ["label"] = "槽位", ["value"] = SlotLabel.TryGetValue(slot, out var sl) ? sl : slot },
                    };
                    if (idea?["cost"]?.ToString() is { Length: > 0 } icost)
                        attrs.Add(new JsonObject { ["label"] = "政治点数花费", ["value"] = icost });

                    var dispTraits = traits.Select(t => L(loc, t) is { Length: > 0 } tv ? tv : t).ToList();
                    items.Add(new JsonObject
                    {
                        ["key"] = ck,
                        ["name"] = name,
                        ["desc"] = "",
                        ["cat"] = SlotLabel.TryGetValue(slot, out var cl) ? cl : slot,
                        ["catKey"] = "adv_" + slot,
                        ["effects"] = ToArr(effects.Where(e => e.Length > 0)),
                        ["attrs"] = ToArrObj(attrs),
                        ["links"] = ToArr(dispTraits),
                        ["traits"] = ToArr(dispTraits),
                    });
                }
            }
        }
        return items;
    }

    // ------------------------------------------------------------ 国策树

    private sealed class FocusNode
    {
        public string Id = "";
        public int X, Y;
        public string Rel = "";
        public string Icon = "";
        public string Cost = "";
        public List<string> Pre = new();
        public List<string> Mutex = new();
        public List<string> Filters = new();
        public bool Bypass;
        public List<string> Fx = new();
        public string Nm = "";
        public string Ds = "";
        public bool Abs;
    }

    private static List<string> FocusReward(string body) => body.Length == 0
        ? new List<string>()
        : RewardWalk(Prepare(body), 0, body.Length, 0, 0);

    private sealed class RewardCtx
    {
        public required List<Blk> Blocks;
        public required List<Sca> Scalars;
    }

    private static RewardCtx Prepare(string body)
    {
        var (blocks, scal) = ScanPairs(body);
        return new RewardCtx
        {
            Blocks = blocks,
            Scalars = scal,
        };
    }

    private static List<string> RewardWalk(RewardCtx ctx, int lo, int hi, int depthBase, int depth)
    {
        var outp = new List<string>();
        if (depth > 4) return outp;
        var bd = ByDepth(ctx.Blocks, depthBase);
        var sd = ByDepth(ctx.Scalars, depthBase);

        foreach (var s in InRange(sd, lo, hi))
        {
            var k = s.Key;
            var v = s.Value;
            switch (k)
            {
                case "custom_effect_tooltip":
                    var txt = L(_locCur, v);
                    if (txt.Length > 0 && txt != "跳过一行" && txt != "-") outp.Add(Flatten(txt));
                    break;
                case "effect_tooltip":
                    var et = L(_locCur, v);
                    if (et.Length > 0) outp.Add(Flatten(et));
                    break;
                case "add_ideas":
                    outp.Add("获得国家精神：" + (L(_locCur, v) is { Length: > 0 } ai ? ai : v));
                    break;
                case "remove_ideas":
                    outp.Add("移除国家精神：" + (L(_locCur, v) is { Length: > 0 } ri ? ri : v));
                    break;
                case "show_ideas_tooltip":
                    var nm = L(_locCur, v) is { Length: > 0 } si ? si : v;
                    if (outp.Count > 0 && outp[^1].TrimEnd().EndsWith("：", StringComparison.Ordinal))
                        outp[^1] = outp[^1].TrimEnd() + nm;
                    else outp.Add("解锁：" + nm);
                    break;
                default:
                    if (RewardPct.ContainsKey(k) || RewardAbs.ContainsKey(k))
                    {
                        var nv = Num(v);
                        if (nv is null || nv == 0) break;
                        outp.Add((RewardPct.TryGetValue(k, out var pl) ? pl : RewardAbs[k])
                                 + " " + FmtNum(nv.Value, RewardPct.ContainsKey(k)));
                    }
                    break;
            }
        }

        foreach (var b in InRange(bd, lo, hi))
        {
            var k = b.Key;
            var bb = b.Body;
            switch (k)
            {
                case "add_tech_bonus":
                {
                    var nm = L(_locCur, Val(bb, "name") ?? "");
                    if (nm.Length == 0) nm = "研究加成";
                    var bonus = Num(Val(bb, "bonus"));
                    var uses = Val(bb, "uses") ?? "1";
                    var cats = Regex.Matches(bb, @"\bcategory\s*=\s*([A-Za-z0-9_]+)")
                        .Select(m => CleanLoc(_locCur.TryGetValue(m.Groups[1].Value, out var cv) ? cv : "", _locCur)
                             is { Length: > 0 } cn ? cn : m.Groups[1].Value).ToList();
                    outp.Add($"{nm} +{(int)Math.Round((bonus ?? 0) * 100)}%（{uses} 次）"
                             + (cats.Count > 0 ? "：" + string.Join("、", cats) : ""));
                    break;
                }
                case "add_breakthrough_progress":
                {
                    var v = Num(Val(bb, "value")) ?? 0;
                    if (v != 0) outp.Add("突破进度 " + FmtNum(v, true));
                    break;
                }
                case "add_doctrine_cost_reduction":
                {
                    var nm = L(_locCur, Val(bb, "name") ?? "");
                    if (nm.Length == 0) nm = "学说花费";
                    var v = Num(Val(bb, "cost_reduction")) ?? 0;
                    var uses = Val(bb, "uses") ?? "1";
                    outp.Add($"{nm} -{(int)Math.Round(v * 100)}%（{uses} 次）");
                    break;
                }
                case "add_timed_idea":
                {
                    var idea = Val(bb, "idea") ?? "";
                    var nm = L(_locCur, idea) is { Length: > 0 } ti ? ti : idea;
                    var days = Val(bb, "days") ?? "";
                    outp.Add("获得限时国家精神：" + nm + (days.Length > 0 ? $"（{days} 天）" : ""));
                    break;
                }
                case "swap_ideas":
                {
                    var rm = L(_locCur, Val(bb, "remove_idea") ?? "");
                    var ad = L(_locCur, Val(bb, "add_idea") ?? "");
                    if (rm.Length > 0 && ad.Length > 0) outp.Add($"国家精神更替：{rm} → {ad}");
                    break;
                }
                case "add_dynamic_modifier":
                {
                    var mo = Val(bb, "modifier") ?? "";
                    var nm = L(_locCur, mo) is { Length: > 0 } dm ? dm : mo;
                    if (nm.Length > 0) outp.Add("获得动态修正：" + nm);
                    break;
                }
                case "add_ideas":
                {
                    var nm = L(_locCur, Val(bb, "idea") ?? "");
                    if (nm.Length > 0) outp.Add("获得国家精神：" + nm);
                    break;
                }
                default:
                    if (RewardDescend.Contains(k))
                        outp.AddRange(RewardWalk(ctx, b.Start, b.Start + bb.Length, depthBase + 1, depth + 1));
                    break;
            }
        }
        return outp;
    }

    private static string FocusTreeName(List<string> tags, bool isDefault, Dictionary<string, string> loc, string tid)
    {
        if (isDefault) return "通用国策树";
        var names = new List<string>();
        foreach (var t in tags)
        {
            var nm = L(loc, t);
            if (nm.Length > 0 && !names.Contains(nm)) names.Add(nm);
        }
        if (names.Count == 0)
            return TreeNameFix.TryGetValue(tid, out var fix) ? fix : "（未标注国家）";
        if (names.Count > 4) return $"{string.Join(" / ", names.Take(3))} 等 {names.Count} 国";
        return string.Join(" / ", names);
    }

    private static JsonArray ScanFocus(string root, Dictionary<string, string> loc)
    {
        var trees = new JsonArray();
        foreach (var f in Wiki.Glob(root, D_NF))
        {
            var txt = ReadScript(f);
            if (!txt.Contains("focus_tree", StringComparison.Ordinal)) continue;

            var (allBlocks, allScal) = ScanPairs(txt);
            var bd0 = ByDepth(allBlocks, 0);
            var bd1 = ByDepth(allBlocks, 1);
            var bd2 = ByDepth(allBlocks, 2);
            var sd1 = ByDepth(allScal, 1);
            var sd2 = ByDepth(allScal, 2);

            foreach (var top in bd0)
            {
                var en = top.Start + top.Body.Length;
                var kids = InRange(bd1, top.Start, en);
                var tid = "";
                foreach (var s in InRange(sd1, top.Start, en)) if (s.Key == "id") { tid = s.Value; break; }
                if (tid.Length == 0) continue;

                var cb = kids.FirstOrDefault(k => k.Key == "country")?.Body ?? "";
                var tags = new List<string>();
                foreach (Match m in Regex.Matches(cb, @"\btag\s*=\s*([A-Z]{3})\b"))
                    if (!tags.Contains(m.Groups[1].Value)) tags.Add(m.Groups[1].Value);
                foreach (Match m in Regex.Matches(cb, @"\boriginal_tag\s*=\s*([A-Z]{3})\b"))
                    if (!tags.Contains(m.Groups[1].Value)) tags.Add(m.Groups[1].Value);
                if (tags.Count == 0)
                {
                    // 不写 tag、只判国家专属 flag 的树，把 flag 的三字母前缀当 tag
                    foreach (Match m in Regex.Matches(cb, @"\b([A-Z]{3})_[A-Za-z0-9_]*\s*="))
                    {
                        var t = m.Groups[1].Value;
                        if (!tags.Contains(t) && !TagStop.Contains(t)) tags.Add(t);
                    }
                }
                var isDefault = string.Equals(
                    InRange(sd1, top.Start, en).FirstOrDefault(s => s.Key == "default")?.Value,
                    "yes", StringComparison.OrdinalIgnoreCase);

                var nodes = new List<FocusNode>();
                var raw = new Dictionary<string, FocusNode>(StringComparer.Ordinal);
                foreach (var kid in kids)
                {
                    if (kid.Key != "focus") continue;
                    var fen = kid.Start + kid.Body.Length;
                    var fscal = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var s in InRange(sd2, kid.Start, fen)) fscal.TryAdd(s.Key, s.Value);
                    var fsubs = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var s in InRange(bd2, kid.Start, fen)) fsubs.TryAdd(s.Key, s.Body);

                    if (!fscal.TryGetValue("id", out var fid) || string.IsNullOrEmpty(fid)) continue;
                    var pre = fsubs.GetValueOrDefault("prerequisite") ?? "";
                    var mutex = fsubs.GetValueOrDefault("mutually_exclusive") ?? "";
                    var node = new FocusNode
                    {
                        Id = fid,
                        X = (int)(Num(fscal.GetValueOrDefault("x")) ?? 0),
                        Y = (int)(Num(fscal.GetValueOrDefault("y")) ?? 0),
                        Rel = fscal.GetValueOrDefault("relative_position_id") ?? "",
                        Icon = fscal.GetValueOrDefault("icon") ?? "",
                        Cost = fscal.GetValueOrDefault("cost") ?? "",
                        Pre = Regex.Matches(pre, @"\bfocus\s*=\s*([A-Za-z0-9_]+)").Select(m => m.Groups[1].Value).ToList(),
                        Mutex = Regex.Matches(mutex, @"\bfocus\s*=\s*([A-Za-z0-9_]+)").Select(m => m.Groups[1].Value).ToList(),
                        Bypass = fsubs.ContainsKey("bypass"),
                    };
                    var sf = fsubs.GetValueOrDefault("search_filters") ?? "";
                    foreach (Match m in Regex.Matches(sf, @"\bFOCUS_FILTER_[A-Z0-9_]+"))
                    {
                        var fn = L(loc, m.Value);
                        if (fn.Length == 0) fn = FilterFix.GetValueOrDefault(m.Value, "");
                        node.Filters.Add(fn);
                    }
                    var reward = fsubs.GetValueOrDefault("completion_reward");
                    node.Fx = string.IsNullOrEmpty(reward) ? new List<string>() : FocusReward(reward);
                    node.Nm = Flatten(L(loc, fid)) is { Length: > 0 } fnm ? fnm : fid;
                    node.Ds = L(loc, fid + "_desc");
                    raw[fid] = node;
                    nodes.Add(node);
                }
                if (nodes.Count == 0) continue;

                // 相对坐标展开：`relative_position_id` 指向父节点，(x,y) 是相对偏移
                foreach (var n in nodes) if (n.Rel.Length == 0) n.Abs = true;
                for (var pass = 0; pass < 40; pass++)
                {
                    var changed = false;
                    foreach (var n in nodes)
                    {
                        if (n.Abs) continue;
                        if (!raw.TryGetValue(n.Rel, out var p))
                        {
                            n.Abs = true; changed = true; continue;
                        }
                        if (!p.Abs) continue;
                        n.X += p.X;
                        n.Y += p.Y;
                        n.Abs = true;
                        changed = true;
                    }
                    if (!changed) break;
                }

                var focusArr = new JsonArray();
                foreach (var n in nodes)
                    focusArr.Add(new JsonObject
                    {
                        ["id"] = n.Id,
                        ["x"] = n.X,
                        ["y"] = n.Y,
                        ["icon"] = n.Icon,
                        ["cost"] = n.Cost,
                        ["pre"] = ToArr(n.Pre),
                        ["mutex"] = ToArr(n.Mutex),
                        ["filters"] = ToArr(n.Filters),
                        ["bypass"] = n.Bypass,
                        ["fx"] = ToArr(n.Fx),
                        ["nm"] = n.Nm,
                        ["ds"] = n.Ds,
                    });

                trees.Add(new JsonObject
                {
                    ["id"] = tid,
                    ["name"] = FocusTreeName(tags, isDefault, loc, tid),
                    ["tags"] = ToArr(tags),
                    ["default"] = isDefault,
                    ["x0"] = nodes.Min(n => n.X),
                    ["y0"] = nodes.Min(n => n.Y),
                    ["x1"] = nodes.Max(n => n.X),
                    ["y1"] = nodes.Max(n => n.Y),
                    ["n"] = nodes.Count,
                    ["focuses"] = focusArr,
                });
            }
        }
        return trees;
    }

    // ------------------------------------------------------------ 构建

    private static JsonObject Build(string root, string locLang)
    {
        var loc = LoadLoc(Path.Combine(root, D_LOC, locLang));
        _locCur = loc;
        BuildModIndex(loc);
        foreach (var (k, label, group) in IdeaCats)
        {
            IdeaCatLabel[k] = label;
            IdeaCatGroup[k] = group;
        }

        var (ideas, ideaIdx, traitKeys) = ScanIdeas(root, loc);
        var traits = ScanTraits(root, loc);
        var traitFx = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var t in traits) traitFx[(string?)t["key"] ?? ""] = t;
        var advisors = ScanAdvisors(root, loc, ideaIdx, traitFx);
        var trees = ScanFocus(root, loc);

        // 顾问型理念自身没 modifier、效果写在特质上：traitFx 建好后补一次
        foreach (var it in ideas)
        {
            if (it["effects"] is JsonArray eff && eff.Count > 0) continue;
            var key = (string?)it["key"] ?? "";
            if (!traitKeys.TryGetValue(key, out var tks)) continue;
            var effects = new List<string>();
            foreach (var t in tks)
            {
                if (!traitFx.TryGetValue(t, out var fx)) continue;
                var fxDesc = fx["desc"]?.ToString() ?? "";
                if (fxDesc.Length > 0)
                    effects.Add("【" + (fx["name"]?.ToString() ?? t) + "】" + Flatten(fxDesc));
                if (fx["effects"] is JsonArray feffs)
                    foreach (var e in feffs) effects.Add(e?.ToString() ?? "");
            }
            it["effects"] = ToArr(effects.Where(e => e.Length > 0));
        }

        // 「军官团与顾问」= 顾问（按槽位）+ 特质（将领/领袖）
        var officers = new List<JsonObject>();
        officers.AddRange(advisors);
        officers.AddRange(traits);

        var haveI = ideas.Select(x => (string?)x["catKey"] ?? "").ToHashSet(StringComparer.Ordinal);
        var haveO = officers.Select(x => (string?)x["catKey"] ?? "").ToHashSet(StringComparer.Ordinal);
        var ideaOrder = IdeaCats.Select(c => c.Key).Where(haveI.Contains).ToList();
        ideaOrder.AddRange(haveI.Where(k => !ideaOrder.Contains(k)).OrderBy(k => k, StringComparer.Ordinal));
        var offOrderCandidates = new[]
        {
            "adv_political_advisor", "adv_high_command", "adv_army_chief", "adv_navy_chief",
            "adv_air_chief", "adv_theorist", "ul_corps_commander", "ul_field_marshal", "ul_all",
            "ul_land", "ul_navy", "ul_air", "ul_operative", "cl",
        };
        var offOrder = offOrderCandidates.Where(haveO.Contains).ToList();
        offOrder.AddRange(haveO.Where(k => !offOrder.Contains(k)).OrderBy(k => k, StringComparer.Ordinal));

        var version = GameVersion(root);
        var tabs = new JsonArray
        {
            Tab("ideas", "💡", "国家理念", ideas.Count, "generic"),
            Tab("officers", "🎖", "军官团与顾问", officers.Count, "generic"),
            Tab("focus", "🌳", "国策树", trees.Count, "focus"),
        };

        return new JsonObject
        {
            ["ok"] = true,
            ["supported"] = true,
            ["game"] = "hoi4",
            ["gameName"] = "Hearts of Iron IV",
            ["gameVersion"] = version,
            ["gameVer"] = version,
            ["loc"] = locLang,
            ["tabs"] = tabs,
            ["ideas"] = ToArrObj(ideas),
            ["officers"] = ToArrObj(officers),
            ["trees"] = trees,
            ["counts"] = new JsonObject
            {
                ["ideas"] = ideas.Count,
                ["officers"] = officers.Count,
                ["focus"] = trees.Count,
                ["focusNodes"] = trees.Sum(n => (n as JsonObject)?["focuses"] is JsonArray fa ? fa.Count : 0),
            },
            ["catOrders"] = new JsonObject
            {
                ["ideas"] = ToArr(ideaOrder),
                ["officers"] = ToArr(offOrder),
            },
        };
    }

    // ------------------------------------------------------------ 小工具

    private static JsonObject Tab(string id, string icon, string label, int count, string layout) => new()
    {
        ["id"] = id,
        ["icon"] = icon,
        ["label"] = label,
        ["count"] = count,
        ["layout"] = layout,
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

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RenlianLauncher.Core;

/// <summary>
/// 群星百科（旧版 backend/stellaris.py 的原生移植）：
/// 从游戏本体脚本 common/governments/civics、ascension_perks、traditions、
/// edicts、policies、relics、specimens、technology、traits 与 localisation/&lt;lang&gt;
/// 抽取国民理念 / 起源 / 飞升天赋 / 传统 / 法令 / 政策 / 遗物 / 标本 / 科技 / 物种特质。
///
/// 本地化清洗规则与旧版一致：£icon£、§Y 颜色码、$var$ 变量、[concept] 引用全部展开或删除，
/// 修饰器名走 `mod_` / `MOD_` / 规范化索引 / 手工别名四级兜底。
/// </summary>
internal static class WikiStellaris
{
    private const int SlVer = 1;                 // 解析逻辑变更时 +1，参与缓存签名

    private static readonly string D_CIVIC = Path.Combine("common", "governments", "civics");
    private static readonly string D_AP = Path.Combine("common", "ascension_perks");
    private static readonly string D_TRAD = Path.Combine("common", "traditions");
    private static readonly string D_TRADCAT = Path.Combine("common", "tradition_categories");
    private static readonly string D_EDICT = Path.Combine("common", "edicts");
    private static readonly string D_POLICY = Path.Combine("common", "policies");
    private static readonly string D_RELIC = Path.Combine("common", "relics");
    private static readonly string D_SPEC = Path.Combine("common", "specimens");
    private static readonly string D_TECH = Path.Combine("common", "technology");
    private static readonly string D_TRAIT = Path.Combine("common", "traits");
    private static readonly string D_SV = Path.Combine("common", "scripted_variables");
    private static readonly string D_LOC = "localisation";

    // 界面语言 → 本地化目录（群星没有瑞典语，回落英文）
    private static readonly Dictionary<string, string> LangMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh"] = "simp_chinese", ["en"] = "english", ["fr"] = "french",
        ["de"] = "german", ["sv"] = "english",
    };

    // tab 元数据：id → (图标, 中文名, 英文名, layout)
    private static readonly (string Id, string Icon, string Zh, string En, string Layout)[] Tabs =
    {
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
    };

    private static readonly Dictionary<string, (string Zh, string En)> CivicCats = new(StringComparer.Ordinal)
    {
        ["00_civics"] = ("常规", "Standard"),
        ["01_special_civics"] = ("特殊", "Special"),
        ["02_gestalt_civics"] = ("蜂巢 / 机械", "Gestalt"),
        ["03_corporate_civics"] = ("企业", "Corporate"),
    };

    private static readonly Dictionary<string, (string Zh, string En)> EdictCats = new(StringComparer.Ordinal)
    {
        ["00_edicts"] = ("常规法令", "Standard"),
        ["01_campaigns"] = ("战役议程", "Campaigns"),
        ["02_ambitions"] = ("宏图野心", "Ambitions"),
        ["03_sacrifices"] = ("献祭", "Sacrifices"),
        ["04_machine_age_edicts"] = ("机械时代", "Machine Age"),
        ["05_grand_archive_edicts"] = ("大档案馆", "Grand Archive"),
        ["06_biogenesis_edicts"] = ("生物起源", "Biogenesis"),
        ["06_shroud_edicts"] = ("帷幕", "Shroud"),
        ["08_nomads_edicts"] = ("游牧", "Nomads"),
    };

    private static readonly Dictionary<string, (string Zh, string En)> AreaNames = new(StringComparer.Ordinal)
    {
        ["physics"] = ("物理学", "Physics"),
        ["society"] = ("社会学", "Society"),
        ["engineering"] = ("工程学", "Engineering"),
    };

    private static readonly string[] TierNames = { "一", "二", "三", "四", "五", "六" };

    private static readonly Dictionary<string, (string Zh, string En)> SpecimenTypes = new(StringComparer.Ordinal)
    {
        ["xeno_geology"] = ("异星地质", "Xeno Geology"),
        ["aesthetic_wonder"] = ("美学奇观", "Aesthetic Wonder"),
        ["historical_item"] = ("历史文物", "Historical Item"),
        ["organic"] = ("有机生命", "Organic"),
        ["relic"] = ("远古遗物", "Relic"),
        ["space_fauna"] = ("太空生物", "Space Fauna"),
        ["precursor"] = ("先驱者", "Precursor"),
    };

    private static readonly Dictionary<string, (string Zh, string En)> TraitCats = new(StringComparer.Ordinal)
    {
        ["normal"] = ("常规特质", "Standard"),
        ["planet_pops_traits"] = ("行星人口", "Planet Pops"),
        ["planet_pref"] = ("星球偏好", "Planet Preference"),
        ["robotic"] = ("机械特质", "Robotic"),
        ["cyborg"] = ("义体特质", "Cyborg"),
        ["psionic"] = ("灵能特质", "Psionic"),
        ["overtuned"] = ("过度特化", "Overtuned"),
        ["malleable"] = ("可塑特质", "Malleable"),
        ["mutation"] = ("变异特质", "Mutation"),
    };

    // 科技子分类（脚本里的 `category`），顺序按领域分组，前端二级筛选照此排序
    private static readonly Dictionary<string, (string Zh, string En, string Area)> TechCats = new(StringComparer.Ordinal)
    {
        ["archaeostudies"] = ("远古学", "Archaeostudies", "society"),
        ["biology"] = ("生物学", "Biology", "society"),
        ["statecraft"] = ("治国术", "Statecraft", "society"),
        ["new_worlds"] = ("新世界理论", "New Worlds", "society"),
        ["military_theory"] = ("军事理论", "Military Theory", "society"),
        ["psionics"] = ("灵能理论", "Psionics", "society"),
        ["computing"] = ("计算技术", "Computing", "physics"),
        ["particles"] = ("粒子物理", "Particles", "physics"),
        ["field_manipulation"] = ("力场操控", "Field Manipulation", "physics"),
        ["voidcraft"] = ("宇航技术", "Voidcraft", "engineering"),
        ["materials"] = ("材料科学", "Materials", "engineering"),
        ["propulsion"] = ("推进力学", "Propulsion", "engineering"),
        ["industry"] = ("工业技术", "Industry", "engineering"),
    };

    // 传统树替换条件的通用文案（本地化里没有）
    private static readonly Dictionary<string, (string Zh, string En)> SwapConds = new(StringComparer.Ordinal)
    {
        ["has_federations_dlc"] = ("拥有联邦 DLC", "Requires Federations DLC"),
        ["has_megacorp_dlc"] = ("拥有巨型企业 DLC", "Requires Megacorp DLC"),
        ["has_ancient_relics_dlc"] = ("拥有远古遗迹 DLC", "Requires Ancient Relics DLC"),
        ["has_apocalypse_dlc"] = ("拥有启示录 DLC", "Requires Apocalypse DLC"),
        ["has_galactic_paragons_dlc"] = ("拥有银河典范 DLC", "Requires Galactic Paragons DLC"),
        ["has_first_contact_dlc"] = ("拥有第一次接触 DLC", "Requires First Contact DLC"),
        ["has_overlord_dlc"] = ("拥有霸主 DLC", "Requires Overlord DLC"),
        ["is_machine_empire"] = ("采用机械智能政体", "Machine Intelligence"),
        ["is_hive_empire"] = ("采用蜂巢思维政体", "Hive Mind"),
        ["is_gestalt"] = ("采用格式塔意识", "Gestalt Consciousness"),
        ["is_megacorp"] = ("企业政府", "Megacorporation"),
        ["is_regular_empire"] = ("常规帝国", "Regular Empire"),
        ["is_psionic"] = ("拥有灵能", "Psionic"),
        ["is_cyborg"] = ("拥有义体", "Cybernetic"),
        ["is_robotic"] = ("机械体", "Robotic"),
    };

    // 本地化里彻底没有的修饰器，按游戏内文案手工补
    private static readonly Dictionary<string, (string Zh, string En)> ModAlias = new(StringComparer.Ordinal)
    {
        ["ascension_perks_add"] = ("飞升天赋槽", "Ascension Perk Slot"),
        ["cohesion_add"] = ("联邦团结度", "Federation Cohesion"),
        ["envoys_add"] = ("外交使节", "Envoys"),
        ["country_envoys_add"] = ("外交使节", "Envoys"),
        ["num_tech_alternatives_add"] = ("备选科技", "Research Alternatives"),
        ["country_tech_alternatives_add"] = ("备选科技", "Research Alternatives"),
        ["all_technology_research_speed"] = ("研究速度", "Research Speed"),
        ["country_all_tech_research_speed"] = ("研究速度", "Research Speed"),
        ["scientist_initial_skill"] = ("科学官起始技能等级", "Scientist Starting Skill Level"),
        ["ship_anomaly_research_speed_mult"] = ("异常现象研究速度", "Anomaly Research Speed"),
        ["country_ship_anomaly_research_speed_mult"] = ("异常现象研究速度", "Anomaly Research Speed"),
        ["country_edict_cap_add"] = ("法令上限", "Edict Cap"),
        ["country_edict_length_mult"] = ("法令持续时间", "Edict Duration"),
        ["country_trust_cap_add"] = ("信任上限", "Trust Cap"),
        ["country_unity_produces_mult"] = ("凝聚力产出", "Unity Production"),
        ["country_influence_produces_mult"] = ("影响力产出", "Influence Production"),
        ["country_administrative_cap_add"] = ("行政容量", "Administrative Capacity"),
        ["country_starbase_capacity_add"] = ("恒星基地容量", "Starbase Capacity"),
        ["country_naval_cap_add"] = ("海军容量", "Naval Capacity"),
        ["country_fleet_cap_add"] = ("指挥上限", "Command Limit"),
        ["society_research_speed_mult"] = ("社会学研究速度", "Society Research Speed"),
        ["physics_research_speed_mult"] = ("物理学研究速度", "Physics Research Speed"),
        ["engineering_research_speed_mult"] = ("工程学研究速度", "Engineering Research Speed"),
    };

    // 引擎内置变量（本地化里查不到）
    private static readonly Dictionary<string, string> EngineVars = new(StringComparer.Ordinal)
    {
        ["t"] = "\n", ["n"] = "\n", ["NEW_LINE"] = "\n", ["TABBED_NEW_LINE"] = "\n",
        ["TAB"] = " ", ["SPACE"] = " ",
    };

    // 资源名重复词（汉化把图标名和后续词重复了一遍）
    private static readonly string[] ResourceWords =
    {
        "凝聚力", "影响力", "易爆微粒", "稀有水晶", "活体金属", "纳米机器人",
        "消费品", "工程学研究", "物理学研究", "社会学研究", "帝国规模", "舰队容量",
        "行政容量", "暗物质", "星尘", "泽珞", "合金", "矿物", "食物", "能源",
    };

    // ------------------------------------------------------------ 正则

    private static readonly Regex AtDefRe = new(@"^\s*@([A-Za-z0-9_]+)\s*=\s*(-?\d+(?:\.\d+)?)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex AtUseRe = new(@"@([A-Za-z0-9_]+)", RegexOptions.Compiled);
    private static readonly Regex CommentRe = new(@"#[^\n]*", RegexOptions.Compiled);
    private static readonly Regex LocRe = new("^\\s*([A-Za-z0-9_.\\-]+):\\d*\\s*\"((?:[^\"\\\\]|\\\\.)*)\"",
        RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex IconRe = new("£[^£]*£", RegexOptions.Compiled);
    private static readonly Regex ColorRe = new("§[A-Za-z0-9!]", RegexOptions.Compiled);
    private static readonly Regex VarRe = new(@"\$(@?[A-Za-z0-9_.]+)(?:\|([^$]*))?\$", RegexOptions.Compiled);
    private static readonly Regex FuncRe = new(@"\[[A-Za-z0-9_.]*\([^\[\]]*\)[^\[\]]*\]", RegexOptions.Compiled);
    private static readonly Regex Tok2Re = new(@"\[\s*'[^'\]]*'\s*,\s*([^\[\]]*)\]", RegexOptions.Compiled);
    private static readonly Regex Tok1Re = new(@"\[\s*['""]?(?:[A-Za-z0-9_]+:)?([A-Za-z0-9_.\-]+)['""]?\s*\]",
        RegexOptions.Compiled);
    private static readonly Regex FlatRe = new(@"([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(-?\d+(?:\.\d+)?)",
        RegexOptions.Compiled);
    private static readonly Regex PairRe = new(@"([A-Za-z_][A-Za-z0-9_]*)\s*=\s*([^\s{}]+)",
        RegexOptions.Compiled);

    private static Dictionary<string, string> _globalAt = new(StringComparer.Ordinal);

    // ------------------------------------------------------------ 对外入口

    public static JsonObject Load(string gameRoot, string uiLang, bool force)
    {
        uiLang = string.IsNullOrWhiteSpace(uiLang) ? "zh" : uiLang.ToLowerInvariant();
        var locLang = LangMap.TryGetValue(uiLang, out var ll) ? ll : "simp_chinese";
        var zh = locLang != "english";

        var root = GameRoot(gameRoot);
        if (root.Length == 0)
            return new JsonObject
            {
                ["ok"] = false,
                ["supported"] = true,
                ["game"] = "stellaris",
                ["error"] = Nm("没找到《群星》安装目录，请在设置里把游戏根目录指向 Stellaris 文件夹",
                    "Stellaris install directory not found. Set the game root in Settings.", zh),
            };

        var dirs = new[]
        {
            D_CIVIC, D_AP, D_TRAD, D_TRADCAT, D_EDICT, D_POLICY, D_RELIC, D_SPEC, D_TECH, D_TRAIT, D_SV,
        }.Select(d => Path.Combine(root, d)).ToList();
        dirs.Add(Path.Combine(root, D_LOC, locLang));
        var sig = SlVer + "|" + locLang + "|" + Sig(dirs);

        long ms = 0;
        var data = Wiki.Cached("stellaris", locLang, root, sig, force, () =>
        {
            var t0 = Environment.TickCount64;
            var d = Build(root, locLang, zh);
            ms = Environment.TickCount64 - t0;
            return d;
        }, out var cached);
        if (!cached) data["buildMs"] = (int)ms;
        return data;
    }

    private static string GameRoot(string hint)
    {
        hint = (hint ?? "").Trim().Trim('"').TrimEnd('\\', '/');
        foreach (var cand in new[] { hint, hint.Length > 0 ? Path.Combine(hint, "Stellaris") : "" })
        {
            if (cand.Length > 0 && Directory.Exists(Path.Combine(cand, D_CIVIC))) return cand;
        }
        // 设置里没填对时，按 Steam 库常见位置兜底探测（与旧版一致）
        foreach (var drive in new[] { "C", "D", "E", "F", "G" })
        {
            foreach (var sub in new[] { "Steam", "SteamLibrary", "Games", "Program Files (x86)\\Steam" })
            {
                var c = Path.Combine(drive + ":\\", sub, "steamapps", "common", "Stellaris");
                if (Directory.Exists(Path.Combine(c, D_CIVIC))) return c;
            }
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

    // ------------------------------------------------------------ 基础解析

    private sealed class Loc
    {
        public readonly Dictionary<string, string> Raw = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> At = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Scripted = new(StringComparer.Ordinal);
        public Dictionary<string, string> ModIdx = new(StringComparer.Ordinal);

        public string? Get(string key) => Raw.TryGetValue(key, out var v) ? v : null;
    }

    private static string AtSubst(string text, Dictionary<string, string> fileDefs)
    {
        if (fileDefs.Count == 0 && _globalAt.Count == 0) return text;
        return AtUseRe.Replace(text, m =>
        {
            var k = m.Groups[1].Value;
            if (fileDefs.TryGetValue(k, out var v)) return v;
            if (_globalAt.TryGetValue(k, out var g)) return g;
            return m.Value;
        });
    }

    private static string ReadScript(string path)
    {
        try
        {
            var t = File.ReadAllText(path, Encoding.UTF8);
            if (t.Length > 0 && t[0] == '\uFEFF') t = t[1..];
            t = CommentRe.Replace(t, "");
            var defs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in AtDefRe.Matches(t)) defs[m.Groups[1].Value] = m.Groups[2].Value;
            return AtSubst(t, defs);
        }
        catch { return ""; }
    }

    /// <summary>取 body **顶层**的 `name = { ... }`（避免抓到 ai_weight 里的嵌套块）。</summary>
    private static string? SubTop(string? body, string name)
    {
        foreach (var (k, b) in Wiki.TopBlocks(body ?? ""))
            if (k == name) return b;
        return null;
    }

    private static string? Val(string? body, string name)
    {
        var v = Wiki.Val(body ?? "", name);
        return v?.Trim().Trim('"');
    }

    private static Dictionary<string, string> Pairs(string? block)
    {
        var o = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(block)) return o;
        foreach (Match m in PairRe.Matches(block)) o[m.Groups[1].Value] = m.Groups[2].Value;
        return o;
    }

    private static List<(string Key, string Val)> Flat(string? block)
    {
        var res = new List<(string, string)>();
        if (string.IsNullOrEmpty(block)) return res;
        foreach (Match m in FlatRe.Matches(block)) res.Add((m.Groups[1].Value, m.Groups[2].Value));
        return res;
    }

    private static string BlockFrom(string s, int start)
    {
        var d = 1;
        var j = start;
        while (j < s.Length && d > 0)
        {
            if (s[j] == '{') d++;
            else if (s[j] == '}') d--;
            j++;
        }
        return s.Substring(start, Math.Max(0, j - 1 - start));
    }

    // ------------------------------------------------------------ 本地化

    private static Dictionary<string, string> LoadAtVars(string root)
    {
        var o = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in new[]
                 {
                     "common/scripted_variables", "common/traditions", "common/tradition_categories",
                     "common/edicts", "common/technology", "common/policies",
                     "common/ascension_perks", "common/governments/civics",
                 })
        {
            var pd = Path.Combine(root, d.Replace('/', '\\'));
            if (!Directory.Exists(pd)) continue;
            foreach (var f in Directory.GetFiles(pd, "*.txt"))
            {
                string raw;
                try { raw = File.ReadAllText(f, Encoding.UTF8); } catch { continue; }
                foreach (Match m in AtDefRe.Matches(raw)) o.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
            }
        }
        return o;
    }

    private static Dictionary<string, string> LoadScriptedLoc(string root)
    {
        var o = new Dictionary<string, string>(StringComparer.Ordinal);
        var pd = Path.Combine(root, "common", "scripted_loc");
        if (!Directory.Exists(pd)) return o;
        foreach (var f in Directory.GetFiles(pd, "*.txt"))
        {
            var txt = ReadScript(f);
            foreach (Match m in Regex.Matches(txt, @"defined_text\s*=\s*\{"))
            {
                var body = BlockFrom(txt, m.Index + m.Length);
                var nm = Val(body, "name");
                var dflt = Val(body, "default");
                if (!string.IsNullOrEmpty(nm) && !string.IsNullOrEmpty(dflt)) o.TryAdd(nm, dflt);
            }
        }
        return o;
    }

    private static Dictionary<string, string> LoadLoc(string locDir)
    {
        var o = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(locDir)) return o;
        IEnumerable<string> files;
        try { files = Directory.GetFiles(locDir, "*.yml"); } catch { return o; }
        foreach (var f in files)
        {
            string raw;
            try { raw = File.ReadAllText(f, Encoding.UTF8); } catch { continue; }
            if (raw.Length > 0 && raw[0] == '\uFEFF') raw = raw[1..];
            foreach (Match m in LocRe.Matches(raw)) o[m.Groups[1].Value] = m.Groups[2].Value;
        }
        return o;
    }

    private static string FmtAt(string val, string fmt)
    {
        if (!double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return val;
        fmt = (fmt ?? "").Trim();
        if (fmt is "0" or "0.0") return f.ToString("0", CultureInfo.InvariantCulture);
        if (fmt.Length > 0 && fmt.All(char.IsDigit)) return f.ToString("F" + fmt, CultureInfo.InvariantCulture);
        return f.ToString("G6", CultureInfo.InvariantCulture);
    }

    /// <summary>展开变量、去图标/颜色标记、去取值函数，返回可直接显示的纯文本。</summary>
    private static string Resolve(string? text, Loc loc, int depth = 4)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text;

        for (var i = 0; i < depth; i++)
        {
            var before = s;
            s = Tok2Re.Replace(s, m => m.Groups[1].Value);
            s = Tok1Re.Replace(s, m =>
            {
                var key = m.Groups[1].Value;
                var v = loc.Get(key);
                if (string.IsNullOrEmpty(v) && loc.Scripted.TryGetValue(key, out var sk))
                    v = loc.Get(sk);
                if (string.IsNullOrEmpty(v)) return "";
                return IconRe.Replace(ColorRe.Replace(v, ""), "");
            });
            s = VarRe.Replace(s, m =>
            {
                var k = m.Groups[1].Value;
                var fmt = m.Groups[2].Value;
                if (k.StartsWith('@'))
                    return loc.At.TryGetValue(k[1..], out var av) ? FmtAt(av, fmt) : "";
                if (EngineVars.TryGetValue(k, out var ev)) return ev;
                var v = loc.Get(k);
                if (v is null) v = loc.Get(k.ToUpperInvariant());
                if (v is null && k.Contains('.'))
                {
                    var b = k[..k.LastIndexOf('.')];
                    v = loc.Get(b) ?? loc.Get(b.ToUpperInvariant());
                }
                return v ?? "";
            });
            if (s == before) break;
        }

        s = s.Replace("\\n", "\n");
        s = IconRe.Replace(s, "");
        s = ColorRe.Replace(s, "");
        s = FuncRe.Replace(s, "");
        s = VarRe.Replace(s, "");
        s = s.Replace('\u00a0', ' ');
        foreach (var w in ResourceWords)
            s = Regex.Replace(s, Regex.Escape(w) + "(?=" + Regex.Escape(w) + ")", "");

        var lines = new List<string>();
        foreach (var raw in s.Split('\n'))
        {
            var ln = Regex.Replace(raw, "[ \t]+", " ").Trim();
            ln = Regex.Replace(ln, "^[—–\\-·]\\s*", "");
            if (ln.StartsWith('的') && ln.Length > 1) ln = ln[1..].TrimStart();
            if (ln.Length > 0 && !Regex.IsMatch(ln, "^[\\s—–\\-·:：,，。.、]+$")) lines.Add(ln);
        }
        return string.Join("\n", lines).Trim();
    }

    private static string? LocAny(Loc loc, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (string.IsNullOrEmpty(k)) continue;
            var v = loc.Get(k);
            if (!string.IsNullOrEmpty(v)) return v;
        }
        return null;
    }

    // ------------------------------------------------------------ 修饰器

    private static readonly string[] ModNormSuffix = { "_mult_add", "_mult", "_add", "_value", "_modifier" };

    private static string ModNorm(string? k)
    {
        var s = (k ?? "").ToLowerInvariant();
        foreach (var pre in new[] { "mod_", "modifier_" })
        {
            if (!s.StartsWith(pre, StringComparison.Ordinal)) continue;
            s = s[pre.Length..];
            break;
        }
        foreach (var suf in ModNormSuffix)
        {
            if (s.EndsWith(suf, StringComparison.Ordinal) && s.Length > suf.Length + 2)
            {
                s = s[..^suf.Length];
                break;
            }
        }
        return s;
    }

    private static Dictionary<string, string> BuildModIdx(Dictionary<string, string> raw)
    {
        var idx = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in raw)
        {
            if (!kv.Key.StartsWith("mod_", StringComparison.Ordinal) &&
                !kv.Key.StartsWith("MOD_", StringComparison.Ordinal)) continue;
            var n = ModNorm(kv.Key);
            if (n.Length > 0) idx.TryAdd(n, kv.Value);
        }
        return idx;
    }

    private static string ModName(string k, Loc loc, bool zh)
    {
        var cands = new List<string> { k, "mod_" + k, "MOD_" + k.ToUpperInvariant() };
        var basis = k ?? "";
        foreach (var suf in new[] { "_mult_add", "_mult" })
        {
            if (!basis.EndsWith(suf, StringComparison.Ordinal)) continue;
            var b = basis[..^suf.Length];
            cands.Add(b);
            cands.Add("mod_" + b);
            cands.Add("MOD_" + b.ToUpperInvariant());
        }
        foreach (var cand in cands)
        {
            var v = loc.Get(cand);
            if (string.IsNullOrEmpty(v)) continue;
            var r = Resolve(v, loc);
            if (r.Length > 0) return r;
        }
        var n = ModNorm(k);
        loc.ModIdx.TryGetValue(n, out var mv);
        if (string.IsNullOrEmpty(mv)) loc.ModIdx.TryGetValue("country_" + n, out mv);
        if (string.IsNullOrEmpty(mv) && n.StartsWith("country_", StringComparison.Ordinal))
            loc.ModIdx.TryGetValue(n["country_".Length..], out mv);
        if (!string.IsNullOrEmpty(mv))
        {
            var r = Resolve(mv, loc);
            if (r.Length > 0) return r;
        }
        if (ModAlias.TryGetValue(k, out var al)) return Nm(al.Zh, al.En, zh);
        return "";
    }

    private static string PlusG(double v)
    {
        var s = v.ToString("G6", CultureInfo.InvariantCulture);
        return s.StartsWith('-') ? s : "+" + s;
    }

    private static string FmtModVal(string k, string v)
    {
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return v;
        var pct = k.EndsWith("_mult", StringComparison.Ordinal) ||
                  k.EndsWith("_mult_add", StringComparison.Ordinal) ||
                  k.Contains("_mult_", StringComparison.Ordinal) ||
                  k.Contains("_multiplier", StringComparison.Ordinal) ||
                  k.EndsWith("_speed", StringComparison.Ordinal) ||
                  k.Contains("habitability", StringComparison.Ordinal) ||
                  k.EndsWith("_chance", StringComparison.Ordinal) ||
                  k.Contains("_chance_", StringComparison.Ordinal);
        if (!pct && Math.Abs(f) > 0 && Math.Abs(f) < 1 &&
            !k.EndsWith("_add", StringComparison.Ordinal) &&
            !k.EndsWith("_value", StringComparison.Ordinal) &&
            !k.EndsWith("_count", StringComparison.Ordinal) &&
            !k.EndsWith("_cost", StringComparison.Ordinal))
            pct = true;
        return pct ? PlusG(f * 100) + "%" : PlusG(f);
    }

    /// <summary>把若干 modifier 块摊平成「名称 数值」行，同键多值用 / 合并。</summary>
    private static List<string> ModLines(Loc loc, bool zh, params string?[] blocks)
    {
        var agg = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var b in blocks)
        {
            foreach (var (k, v) in Flat(b))
            {
                var nm = ModName(k, loc, zh);
                if (nm.Length == 0) continue;
                if (!agg.TryGetValue(k, out var vals))
                {
                    vals = new List<string>();
                    agg[k] = vals;
                    names[k] = nm;
                    order.Add(k);
                }
                var val = FmtModVal(k, v);
                if (!vals.Contains(val)) vals.Add(val);
            }
        }
        var outp = new List<string>();
        foreach (var k in order)
            outp.Add($"{names[k]} {string.Join(" / ", agg[k])}");
        return outp;
    }

    private static List<string> Dedup(IEnumerable<string?> seq)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var outp = new List<string>();
        foreach (var x0 in seq)
        {
            var x = (x0 ?? "").Trim();
            if (x.Length == 0 || !seen.Add(x)) continue;
            outp.Add(x);
        }
        return outp;
    }

    // ------------------------------------------------------------ 条目辅助

    private static JsonObject Entry(string key, string name, string desc, string catKey, string catLabel,
        IEnumerable<JsonObject>? attrs = null, IEnumerable<string>? effects = null,
        IEnumerable<string>? links = null, string src = "", JsonObject? extra = null)
    {
        var o = new JsonObject
        {
            ["key"] = key,
            ["name"] = name,
            ["desc"] = desc,
            ["cat"] = catLabel ?? "",
            ["catKey"] = catKey ?? "",
            ["attrs"] = AttrArr(attrs),
            ["effects"] = StrArr(effects),
            ["links"] = StrArr(links),
            ["src"] = src ?? "",
        };
        if (extra is not null)
            foreach (var kv in extra.ToList())
                o[kv.Key] = kv.Value?.DeepClone();
        return o;
    }

    private static JsonArray StrArr(IEnumerable<string>? items)
    {
        var a = new JsonArray();
        if (items is not null) foreach (var s in items) a.Add(s);
        return a;
    }

    private static JsonArray AttrArr(IEnumerable<JsonObject>? items)
    {
        var a = new JsonArray();
        if (items is not null) foreach (var s in items) a.Add(s);
        return a;
    }

    private static JsonArray ObjArr(IEnumerable<JsonObject> items)
    {
        var a = new JsonArray();
        foreach (var s in items) a.Add(s);
        return a;
    }

    private static JsonObject Attr(string label, string value) => new() { ["label"] = label, ["value"] = value };

    private static string Nm(string name, string en, bool zh) => zh ? name : en;

    private static (string Key, string Label) CatLabel(Dictionary<string, (string Zh, string En)> table,
        string key, bool zh)
    {
        if (table.TryGetValue(key, out var pair)) return (key, zh ? pair.Zh : pair.En);
        return (key ?? "", key ?? "");
    }

    // ------------------------------------------------------------ 各类收集

    private static List<string> CivicEffects(string body, Loc loc, bool zh)
    {
        var outp = new List<string>();
        var d = Val(body, "description");
        if (!string.IsNullOrEmpty(d) && loc.Get(d) is { } dv)
            outp.AddRange(Resolve(dv, loc).Split('\n'));
        var nd = Val(body, "negative_description");
        if (!string.IsNullOrEmpty(nd) && loc.Get(nd) is { } ndv)
            outp.AddRange(Resolve(ndv, loc).Split('\n'));
        outp.AddRange(ModLines(loc, zh, SubTop(body, "modifier")));
        foreach (var sw in Wiki.SubAll(body, "swap_type"))
        {
            foreach (var fld in new[] { "description", "negative_description" })
            {
                var v = Val(sw, fld);
                if (!string.IsNullOrEmpty(v) && loc.Get(v) is { } ev)
                    outp.AddRange(Resolve(ev, loc).Split('\n'));
            }
            outp.AddRange(ModLines(loc, zh, SubTop(sw, "modifier")));
        }
        return Dedup(outp);
    }

    private static (HashSet<string> Allow, HashSet<string> Deny) AuthoritySplit(string pot)
    {
        var allow = new HashSet<string>(StringComparer.Ordinal);
        var deny = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(pot ?? "", @"authority\s*=\s*\{"))
        {
            var blk = BlockFrom(pot, m.Index + m.Length);
            var vals = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match vm in Regex.Matches(blk, @"value\s*=\s*(auth_[a-z_]+)")) vals.Add(vm.Groups[1].Value);
            if (vals.Count == 0) continue;
            var from = Math.Max(0, m.Index - 60);
            var before = pot.Substring(from, m.Index - from);
            var outerNot = Regex.IsMatch(before, @"NOT\s*=\s*\{\s*[^{}]*$");
            var innerNot = blk.Contains("NOT", StringComparison.Ordinal);
            var target = innerNot || outerNot ? deny : allow;
            foreach (var v in vals) target.Add(v);
        }
        var a = new HashSet<string>(allow, StringComparer.Ordinal);
        a.ExceptWith(deny);
        var d = new HashSet<string>(deny, StringComparer.Ordinal);
        d.ExceptWith(allow);
        return (a, d);
    }

    private static List<JsonObject> CivicAttrs(string body, Loc loc, bool zh)
    {
        var attrs = new List<JsonObject>();
        var pot = (SubTop(body, "potential") ?? "") + "\n" + (SubTop(body, "possible") ?? "");
        var (allow, deny) = AuthoritySplit(pot);
        foreach (var (vals, zhL, enL) in new[]
                 {
                     (allow, "可用政体", "Authority"),
                     (deny, "不可用政体", "Not available for"),
                 })
        {
            if (vals.Count == 0) continue;
            var names = vals.OrderBy(a => a, StringComparer.Ordinal)
                .Select(a => loc.Get(a) ?? Wiki.Title(a))
                .Distinct(StringComparer.Ordinal);
            attrs.Add(Attr(Nm(zhL, enL, zh), string.Join("、", names)));
        }
        var mdf = SubTop(body, "modification");
        if (mdf is not null)
        {
            if (Regex.IsMatch(mdf.Trim(), @"^\s*no\s*$", RegexOptions.Multiline))
                attrs.Add(Attr(Nm("开局后", "After start", zh), Nm("不可增删", "Locked", zh)));
        }
        else if (Val(body, "modification") == "no")
        {
            attrs.Add(Attr(Nm("开局后", "After start", zh), Nm("不可增删", "Locked", zh)));
        }
        if (Val(body, "advanced_start") == "yes")
            attrs.Add(Attr(Nm("开局", "Start", zh), Nm("进阶开局", "Advanced", zh)));
        return attrs;
    }

    private static (List<JsonObject> Civics, List<JsonObject> Origins) CollectCivicsOrigins(
        string root, Loc loc, bool zh)
    {
        var civics = new List<JsonObject>();
        var origins = new List<JsonObject>();
        foreach (var f in Wiki.Glob(root, D_CIVIC))
        {
            var stem = Path.GetFileNameWithoutExtension(f);
            var isOriginFile = stem.Contains("origins", StringComparison.Ordinal);
            var (catKey, catLabel) = CatLabel(CivicCats, stem, zh);
            foreach (var (key, body) in Wiki.TopBlocks(ReadScript(f)))
            {
                var name = Resolve(loc.Get(key) ?? "", loc);
                if (name.Length == 0) continue;
                var desc = Resolve(loc.Get(key + "_desc") ?? "", loc);
                var eff = CivicEffects(body, loc, zh);
                var isOrigin = Val(body, "is_origin") == "yes" || isOriginFile;
                if (isOrigin)
                {
                    var attrs = CivicAttrs(body, loc, zh);
                    if (stem.Contains("non_playable", StringComparison.Ordinal))
                        attrs.Add(Attr(Nm("可用性", "Availability", zh), Nm("不可选", "Not playable", zh)));
                    origins.Add(Entry(key, name, desc, "origin", Nm("起源", "Origins", zh),
                        attrs, eff, null, stem));
                }
                else
                {
                    civics.Add(Entry(key, name, desc, catKey, catLabel,
                        CivicAttrs(body, loc, zh), eff, null, stem));
                }
            }
        }
        return (civics, origins);
    }

    private static List<JsonObject> CollectAps(string root, Loc loc, bool zh)
    {
        var outp = new List<JsonObject>();
        foreach (var f in Wiki.Glob(root, D_AP))
        {
            var stem = Path.GetFileNameWithoutExtension(f);
            foreach (var (key, body) in Wiki.TopBlocks(ReadScript(f)))
            {
                var name = Resolve(loc.Get(key) ?? "", loc);
                if (name.Length == 0) continue;
                var eff = new List<string>();
                foreach (Match m in Regex.Matches(body, @"custom_tooltip\s*=\s*""([A-Za-z0-9_.]+)"""))
                {
                    var v = loc.Get(m.Groups[1].Value);
                    if (!string.IsNullOrEmpty(v)) eff.AddRange(Resolve(v, loc).Split('\n'));
                }
                eff.AddRange(ModLines(loc, zh, SubTop(body, "modifier")));
                outp.Add(Entry(key, name, Resolve(loc.Get(key + "_desc") ?? "", loc),
                    "ap", Nm("飞升天赋", "Ascension Perk", zh), null, Dedup(eff), null, stem));
            }
        }
        return outp;
    }

    private static string SwapCond(string k, Loc loc, bool zh)
    {
        if (SwapConds.TryGetValue(k, out var pair)) return Nm(pair.Zh, pair.En, zh);
        foreach (var cand in new[] { "TRIGGER_" + k.ToUpperInvariant(), k, "trigger_" + k, k.ToUpperInvariant() })
        {
            var s = loc.Get(cand);
            if (string.IsNullOrEmpty(s)) continue;
            var r = Resolve(s, loc);
            if (r.Length > 0) return r;
        }
        return "";
    }

    private static List<string> CtKeys(string? body)
    {
        var outp = new List<string>();
        body ??= "";
        foreach (var fld in new[] { "custom_tooltip", "custom_tooltip_with_modifiers" })
        {
            foreach (Match m in Regex.Matches(body, Regex.Escape(fld) + @"\s*=\s*\{([^{}]*)\}"))
                outp.AddRange(Regex.Matches(m.Groups[1].Value, @"[A-Za-z0-9_.]+")
                    .Select(x => x.Value));
            var v = Val(body, fld);
            if (!string.IsNullOrEmpty(v) && v is not ("yes" or "no")) outp.Add(v);
        }
        return outp;
    }

    private static List<string> TradEffects(string body, Loc loc, string root, bool zh)
    {
        var eff = new List<string>();
        foreach (var ct in CtKeys(body))
        {
            var cv = Resolve(loc.Get(ct) ?? "", loc);
            if (cv.Length > 0) eff.AddRange(cv.Split('\n'));
        }
        if (root.Length > 0)
        {
            foreach (Match m in Regex.Matches(body ?? "", @"inline_script\s*=\s*\{\s*script\s*=\s*([A-Za-z0-9_/]+)"))
            {
                var pm = Path.Combine(root, "common", "inline_scripts",
                    m.Groups[1].Value.Replace('/', '\\') + ".txt");
                if (!File.Exists(pm)) continue;
                var ib = ReadScript(pm);
                foreach (var ct in CtKeys(ib))
                {
                    var cv = Resolve(loc.Get(ct) ?? "", loc);
                    if (cv.Length > 0) eff.AddRange(cv.Split('\n'));
                }
                eff.AddRange(ModLines(loc, zh, SubTop(ib, "modifier")));
            }
        }
        var ctext = string.Join("\n", eff);
        var mods = new List<string>();
        foreach (var tb in Wiki.SubAll(body ?? "", "triggered_modifier"))
            mods.AddRange(ModLines(loc, zh, tb));
        mods.AddRange(ModLines(loc, zh, SubTop(body, "modifier")));
        foreach (var ml in mods)
        {
            var idx = ml.LastIndexOf(' ');
            var nm = idx > 0 ? ml[..idx] : "";
            if (nm.Length > 0 && ctext.Contains(nm, StringComparison.Ordinal)) continue;
            eff.Add(ml);
        }
        return eff;
    }

    private static List<JsonObject> CollectTraditions(string root, Loc loc, bool zh)
    {
        var outp = new List<JsonObject>();
        foreach (var f in Wiki.Glob(root, D_TRAD))
        {
            var stem = Path.GetFileNameWithoutExtension(f);
            var treeKey = "tradition_" + Regex.Replace(stem, @"^\d+_", "");
            var treeName = Resolve(loc.Get(treeKey) ?? "", loc);
            if (treeName.Length == 0) treeName = Wiki.Title(treeKey);
            var treeDesc = Resolve(loc.Get(treeKey + "_desc") ?? "", loc);
            foreach (var (key, body) in Wiki.TopBlocks(ReadScript(f)))
            {
                var name = Resolve(loc.Get(key) ?? "", loc);
                if (name.Length == 0) continue;
                var eff = TradEffects(body, loc, root, zh);
                foreach (var (fld, label) in new[]
                         {
                             ("unlocks_agenda", Nm("解锁议程", "Unlocks agenda", zh)),
                             ("unlocks_building", Nm("解锁建筑", "Unlocks building", zh)),
                             ("unlocks_edict", Nm("解锁法令", "Unlocks edict", zh)),
                         })
                {
                    foreach (Match m in Regex.Matches(body, @"\b" + fld + @"\s*=\s*([A-Za-z0-9_]+)"))
                    {
                        var v = m.Groups[1].Value;
                        var nm = Resolve(LocAny(loc, v, "council_agenda_agenda_" + v + "_name",
                            "council_agenda_agenda_" + v, "edict_" + v) ?? "", loc);
                        if (nm.Length > 0 && !eff.Any(e => e.Contains(nm, StringComparison.Ordinal)))
                            eff.Add($"{label}：{nm}");
                    }
                }
                var ev = loc.Get(key + "_effects");
                if (!string.IsNullOrEmpty(ev))
                    eff.InsertRange(0, Resolve(ev, loc).Split('\n'));
                var attrs = new List<JsonObject>();
                if (key.EndsWith("_adopt", StringComparison.Ordinal))
                    attrs.Add(Attr(Nm("类型", "Type", zh), Nm("开端", "Adoption", zh)));
                else if (key.EndsWith("_finish", StringComparison.Ordinal))
                    attrs.Add(Attr(Nm("类型", "Type", zh), Nm("完成", "Finisher", zh)));

                var variants = TradVariants(body, loc, zh, root);
                var ent = Entry(key, name, "", treeKey, treeName, attrs, Dedup(eff), null, treeDesc);
                if (variants.Count > 0) ent["variants"] = ObjArr(variants);
                outp.Add(ent);
            }
        }

        // 树内顺序：开端 → 中间的传统 → 完成
        var treeSeq = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var it in outp)
        {
            var ck = it["catKey"]?.ToString() ?? "";
            if (!treeSeq.ContainsKey(ck)) treeSeq[ck] = treeSeq.Count;
        }
        var indexed = outp.Select((it, i) => (it, i)).ToList();
        indexed.Sort((a, b) =>
        {
            var ka = treeSeq.TryGetValue(a.it["catKey"]?.ToString() ?? "", out var va) ? va : 99;
            var kb = treeSeq.TryGetValue(b.it["catKey"]?.ToString() ?? "", out var vb) ? vb : 99;
            if (ka != kb) return ka - kb;
            var sa = TradSortKey(a.it["key"]?.ToString() ?? "");
            var sb = TradSortKey(b.it["key"]?.ToString() ?? "");
            return sa != sb ? sa - sb : a.i - b.i;
        });
        return indexed.Select(x => x.it).ToList();
    }

    private static int TradSortKey(string key) =>
        key.EndsWith("_adopt", StringComparison.Ordinal) ? 0 :
        key.EndsWith("_finish", StringComparison.Ordinal) ? 2 : 1;

    private static JsonObject CollectTradTrees(string root, Loc loc, bool zh)
    {
        var outp = new JsonObject();
        foreach (var f in Wiki.Glob(root, D_TRADCAT))
        {
            foreach (var (tk, body) in Wiki.TopBlocks(ReadScript(f)))
            {
                var descs = new List<JsonObject>();
                foreach (var blk in Wiki.SubAll(body, "desc"))
                {
                    var txtKey = Val(blk, "text");
                    var trg = SubTop(blk, "trigger") ?? "";
                    var conds = new List<string>();
                    foreach (Match m in Regex.Matches(trg, @"([a-z_][a-z0-9_]*)\s*=\s*yes"))
                    {
                        var c = SwapCond(m.Groups[1].Value, loc, zh);
                        if (c.Length > 0) conds.Add(c);
                    }
                    var txt = Resolve(loc.Get(txtKey ?? "") ?? "", loc);
                    if (txt.Length == 0) continue;
                    descs.Add(new JsonObject
                    {
                        ["cond"] = StrArr(Dedup(conds)),
                        ["text"] = txt,
                    });
                }
                var dk = tk + "_desc";
                if (loc.Get(dk) is { } dkv && !descs.Any(d => (d["cond"] as JsonArray)?.Count == 0))
                    descs.Insert(0, new JsonObject { ["cond"] = new JsonArray(), ["text"] = Resolve(dkv, loc) });

                int? aiWeight = null;
                var aw = SubTop(body, "ai_weight") ?? "";
                var awm = Regex.Match(aw, @"factor\s*=\s*(-?\d+)");
                if (awm.Success && int.TryParse(awm.Groups[1].Value, out var awv)) aiWeight = awv;

                var order = new List<string>();
                var tlist = SubTop(body, "traditions") ?? "";
                foreach (Match m in Regex.Matches(tlist, "\"([a-z0-9_]+)\"")) order.Add(m.Groups[1].Value);

                // 同一段文字会挂在多个政体条件下，合并 cond
                var seenTxt = new HashSet<string>(StringComparer.Ordinal);
                var uniq = new List<JsonObject>();
                foreach (var d in descs)
                {
                    var txt = d["text"]?.ToString() ?? "";
                    if (seenTxt.Add(txt))
                    {
                        uniq.Add(d);
                        continue;
                    }
                    foreach (var u in uniq.Where(u => (u["text"]?.ToString() ?? "") == txt))
                    {
                        var merged = new List<string>();
                        if (u["cond"] is JsonArray ua)
                            foreach (var x in ua) if (x is not null) merged.Add(x.ToString());
                        if (d["cond"] is JsonArray da)
                            foreach (var x in da) if (x is not null) merged.Add(x.ToString());
                        u["cond"] = StrArr(Dedup(merged));
                        break;
                    }
                }

                outp[tk] = new JsonObject
                {
                    ["descs"] = ObjArr(uniq),
                    ["aiWeight"] = aiWeight is null ? null : JsonValue.Create(aiWeight.Value),
                    ["order"] = StrArr(order),
                };
            }
        }
        return outp;
    }

    private static List<JsonObject> TradVariants(string body, Loc loc, bool zh, string root)
    {
        var outp = new List<JsonObject>();
        foreach (var blk in Wiki.SubAll(body, "tradition_swap"))
        {
            var nmk = Val(blk, "name");
            var inheritName = Val(blk, "inherit_name") == "yes";
            var nm = "";
            if (!string.IsNullOrEmpty(nmk) && !inheritName) nm = Resolve(loc.Get(nmk) ?? "", loc);
            var desc = string.IsNullOrEmpty(nmk) ? "" : Resolve(loc.Get(nmk + "_desc") ?? "", loc);
            var eff = Val(blk, "inherit_effects") != "yes"
                ? TradEffects(blk, loc, root, zh)
                : new List<string>();
            var cond = new List<string>();
            var trg = SubTop(blk, "trigger") ?? "";
            foreach (Match m in Regex.Matches(trg, @"([a-z_][a-z0-9_]*)\s*=\s*yes"))
            {
                var c = SwapCond(m.Groups[1].Value, loc, zh);
                if (c.Length > 0) cond.Add(c);
            }
            if (nm.Length == 0 && desc.Length == 0 && eff.Count == 0) continue;
            outp.Add(new JsonObject
            {
                ["name"] = nm,
                ["desc"] = desc,
                ["cond"] = StrArr(Dedup(cond)),
                ["effects"] = StrArr(Dedup(eff)),
                ["inheritEffects"] = Val(blk, "inherit_effects") == "yes",
            });
        }
        return outp;
    }

    private static List<JsonObject> CollectEdicts(string root, Loc loc, bool zh)
    {
        var outp = new List<JsonObject>();
        foreach (var f in Wiki.Glob(root, D_EDICT))
        {
            var stem = Path.GetFileNameWithoutExtension(f);
            if (stem.StartsWith("99_", StringComparison.Ordinal)) continue;
            var (catKey, catLabel) = CatLabel(EdictCats, stem, zh);
            foreach (var (key, body) in Wiki.TopBlocks(ReadScript(f)))
            {
                var nk = "edict_" + key;
                var name = Resolve(LocAny(loc, nk, key) ?? "", loc);
                if (name.Length == 0) continue;
                var desc = Resolve(LocAny(loc, nk + "_desc", key + "_desc") ?? "", loc);
                var eff = new List<string>();
                var ev = LocAny(loc, nk + "_effects", key + "_effects");
                if (!string.IsNullOrEmpty(ev)) eff.AddRange(Resolve(ev, loc).Split('\n'));
                eff.AddRange(ModLines(loc, zh, SubTop(body, "modifier")));

                var attrs = new List<JsonObject>();
                var res = SubTop(body, "resources") ?? "";
                var cost = SubTop(res, "cost") ?? "";
                foreach (var kv in Pairs(cost))
                {
                    if (kv.Key is "category" or "multiplier" or "trigger" or "potential" or "value") continue;
                    var nm = ModName(kv.Key, loc, zh);
                    if (nm.Length == 0) nm = Resolve(loc.Get(kv.Key) ?? "", loc);
                    if (nm.Length == 0) nm = Wiki.Title(kv.Key);
                    if (!nm.EndsWith("花费", StringComparison.Ordinal) &&
                        !nm.EndsWith("Cost", StringComparison.Ordinal) &&
                        !nm.EndsWith("cost", StringComparison.Ordinal))
                        nm += Nm("花费", " cost", zh);
                    attrs.Add(Attr(nm, Resolve(kv.Value, loc)));
                }
                outp.Add(Entry(key, name, desc, catKey, catLabel, attrs, Dedup(eff), null, stem));
            }
        }
        return outp;
    }

    private static List<JsonObject> CollectPolicies(string root, Loc loc, bool zh)
    {
        var outp = new List<JsonObject>();
        foreach (var f in Wiki.Glob(root, D_POLICY))
        {
            foreach (var (gkey, gbody) in Wiki.TopBlocks(ReadScript(f)))
            {
                var gname = Resolve(loc.Get("policy_" + gkey) ?? loc.Get(gkey) ?? "", loc);
                if (gname.Length == 0) gname = Wiki.Title(gkey);
                var gdesc = Resolve(loc.Get("policy_" + gkey + "_desc") ?? "", loc);
                foreach (var obody in Wiki.SubAll(gbody, "option"))
                {
                    var nameKey = Val(obody, "name");
                    if (string.IsNullOrEmpty(nameKey)) continue;
                    var name = Resolve(loc.Get(nameKey) ?? "", loc);
                    if (name.Length == 0) continue;
                    var eff = new List<string>();
                    var ev = loc.Get(nameKey + "_effects");
                    if (!string.IsNullOrEmpty(ev)) eff.AddRange(Resolve(ev, loc).Split('\n'));
                    eff.AddRange(ModLines(loc, zh, SubTop(obody, "modifier")));
                    var attrs = new List<JsonObject>();
                    if (gdesc.Length > 0)
                        attrs.Add(Attr(Nm("政策组", "Policy group", zh), gname));
                    outp.Add(Entry(nameKey, name, Resolve(loc.Get(nameKey + "_desc") ?? "", loc),
                        gkey, gname, attrs, Dedup(eff), null, gdesc));
                }
            }
        }
        return outp;
    }

    private static List<JsonObject> CollectRelics(string root, Loc loc, bool zh)
    {
        var outp = new List<JsonObject>();
        foreach (var f in Wiki.Glob(root, D_RELIC))
        {
            var stem = Path.GetFileNameWithoutExtension(f);
            if (stem.StartsWith("99_", StringComparison.Ordinal)) continue;
            foreach (var (key, body) in Wiki.TopBlocks(ReadScript(f)))
            {
                var name = Resolve(loc.Get(key) ?? "", loc);
                if (name.Length == 0) continue;
                var eff = new List<string>();
                foreach (var act in Wiki.SubAll(body, "passive_effect")
                             .Concat(Wiki.SubAll(body, "active_effect")))
                {
                    foreach (Match m in Regex.Matches(act, @"custom_tooltip\s*=\s*""?([A-Za-z0-9_.]+)"))
                    {
                        var v = loc.Get(m.Groups[1].Value);
                        if (!string.IsNullOrEmpty(v)) eff.AddRange(Resolve(v, loc).Split('\n'));
                    }
                    eff.AddRange(ModLines(loc, zh, act));
                }
                eff.AddRange(ModLines(loc, zh, SubTop(body, "modifier")));
                outp.Add(Entry(key, name, Resolve(loc.Get(key + "_desc") ?? "", loc),
                    "relic", Nm("遗物", "Relic", zh), null, Dedup(eff), null, stem));
            }
        }
        return outp;
    }

    private static List<JsonObject> CollectSpecimens(string root, Loc loc, bool zh)
    {
        var outp = new List<JsonObject>();
        foreach (var f in Wiki.Glob(root, D_SPEC))
        {
            var stem = Path.GetFileName(f);
            foreach (var (key, body) in Wiki.TopBlocks(ReadScript(f)))
            {
                var name = Resolve(loc.Get(key) ?? "", loc);
                if (name.Length == 0) continue;
                var typ = Val(body, "type") ?? "other";
                var (catKey, catLabel) = CatLabel(SpecimenTypes, typ, zh);
                if (!SpecimenTypes.ContainsKey(typ)) catLabel = Wiki.Title(typ);

                var eff = new List<string>();
                foreach (var tb in Wiki.SubAll(body, "triggered_country_modifier"))
                    eff.AddRange(ModLines(loc, zh, tb));
                eff.AddRange(ModLines(loc, zh, SubTop(body, "country_modifier")));

                var desc = Resolve(LocAny(loc, key + "_desc_short", key + "_desc") ?? "", loc);
                var det = Resolve(loc.Get(key + "_desc_details") ?? "", loc);
                if (det.Length > 0) eff.Add(det);

                var attrs = new List<JsonObject>();
                var rar = Val(body, "RARITY");
                if (rar is null)
                {
                    var rm = Regex.Match(body, @"RARITY\s*=\s*(\w+)");
                    if (rm.Success) rar = rm.Groups[1].Value;
                }
                if (rar is not null)
                {
                    var rarNames = new Dictionary<string, (string Zh, string En)>(StringComparer.Ordinal)
                    {
                        ["common"] = ("普通", "Common"), ["uncommon"] = ("罕见", "Uncommon"),
                        ["rare"] = ("稀有", "Rare"), ["epic"] = ("史诗", "Epic"),
                        ["exceptional"] = ("珍奇", "Exceptional"),
                    };
                    var label = rarNames.TryGetValue(rar, out var rp) ? Nm(rp.Zh, rp.En, zh) : Wiki.Title(rar);
                    attrs.Add(Attr(Nm("稀有度", "Rarity", zh), label));
                }
                outp.Add(Entry(key, name, desc, catKey, catLabel, attrs, Dedup(eff), null, stem));
            }
        }
        return outp;
    }

    private static string TechCatName(string c, Loc loc, bool zh)
    {
        var v = loc.Get("a_" + c);
        if (!string.IsNullOrEmpty(v) && HasCjk(v) == zh) return Resolve(v, loc);
        if (TechCats.TryGetValue(c, out var t)) return Nm(t.Zh, t.En, zh);
        if (!string.IsNullOrEmpty(v)) return Resolve(v, loc);
        return Wiki.Title(c);
    }

    private static bool HasCjk(string s) => s.Any(c => c >= '\u4e00' && c <= '\u9fff');

    private static List<JsonObject> CollectTech(string root, Loc loc, bool zh)
    {
        var outp = new List<JsonObject>();
        foreach (var f in Wiki.Glob(root, D_TECH))
        {
            var stem = Path.GetFileNameWithoutExtension(f);
            if (stem.StartsWith("000_", StringComparison.Ordinal)) continue;
            foreach (var (key, body) in Wiki.TopBlocks(ReadScript(f)))
            {
                var name = Resolve(loc.Get(key) ?? "", loc);
                if (name.Length == 0) continue;

                var area = Val(body, "area") ?? "";
                var (areaKey, areaLabel) = CatLabel(AreaNames, area, zh);
                if (!AreaNames.ContainsKey(area))
                    areaLabel = area.Length > 0 ? Wiki.Title(area) : Nm("其他", "Other", zh);

                var catblk = SubTop(body, "category");
                List<string> cats;
                if (catblk is not null)
                    cats = Regex.Matches(catblk, "[a-z_]+").Select(m => m.Value)
                        .Where(c => c != "category").ToList();
                else
                {
                    var cv = Val(body, "category");
                    cats = string.IsNullOrEmpty(cv) ? new List<string>() : new List<string> { cv };
                }
                cats = cats.Distinct(StringComparer.Ordinal).Where(c => c.Length > 0).ToList();
                var ck = cats.Count > 0 ? cats[0] : "";
                var cl = ck.Length > 0 ? TechCatName(ck, loc, zh) : Nm("其他", "Other", zh);

                var tier = Val(body, "tier") ?? "";
                var tn = int.TryParse(tier, out var tv0) ? tv0 : -1;

                var attrs = new List<JsonObject>();
                if (tn >= 0)
                {
                    string tv;
                    if (tn == 0) tv = Nm("特殊", "Special", zh);
                    else
                    {
                        var baseName = tn > 0 && tn <= TierNames.Length ? TierNames[tn - 1] : tn.ToString();
                        tv = zh ? baseName + "级" : "Tier " + tn;
                    }
                    attrs.Add(Attr(Nm("科技层级", "Tier", zh), tv));
                }
                if (cats.Count > 0)
                    attrs.Add(Attr(Nm("科技类别", "Category", zh),
                        string.Join("、", cats.Select(c => TechCatName(c, loc, zh)))));

                var rare = Val(body, "is_rare") == "yes";
                var danger = Val(body, "is_dangerous") == "yes";
                var repeat = Val(body, "levels") == "-1";
                if (rare) attrs.Add(Attr(Nm("稀有", "Rare", zh), Nm("是", "Yes", zh)));
                if (danger) attrs.Add(Attr(Nm("危险", "Dangerous", zh), Nm("是", "Yes", zh)));
                if (repeat) attrs.Add(Attr(Nm("循环", "Repeatable", zh), Nm("是", "Yes", zh)));

                var eff = new List<string>();
                foreach (Match fm in Regex.Matches(body, @"feature_flags\s*=\s*\{([^}]*)\}"))
                {
                    foreach (Match gm in Regex.Matches(fm.Groups[1].Value, @"([a-z0-9_]+)"))
                    {
                        var flag = gm.Groups[1].Value;
                        var nm = Resolve(loc.Get("tech_" + flag) ?? loc.Get(flag) ?? "", loc);
                        if (nm.Length > 0 && !name.Contains(nm, StringComparison.Ordinal)) eff.Add(nm);
                    }
                }
                foreach (var ct in CtKeys(body))
                {
                    var cv = Resolve(loc.Get(ct) ?? "", loc);
                    if (cv.Length == 0 || eff.Any(e => e == cv)) continue;
                    eff.AddRange(cv.Split('\n'));
                }
                eff.AddRange(ModLines(loc, zh, SubTop(body, "modifier")));

                var extra = new JsonObject
                {
                    ["areaKey"] = areaKey,
                    ["area"] = areaLabel,
                    ["cats"] = StrArr(cats),
                    ["tier"] = tn,
                    ["rare"] = rare,
                    ["dangerous"] = danger,
                    ["repeatable"] = repeat,
                };
                outp.Add(Entry(key, name, Resolve(loc.Get(key + "_desc") ?? "", loc),
                    ck, cl, attrs, Dedup(eff), null, stem, extra));
            }
        }
        return outp;
    }

    private static List<JsonObject> CollectTraits(string root, Loc loc, bool zh)
    {
        var outp = new List<JsonObject>();
        foreach (var f in Wiki.Glob(root, D_TRAIT))
        {
            var stem = Path.GetFileNameWithoutExtension(f);
            if (stem.StartsWith("000_", StringComparison.Ordinal)) continue;
            var txt = ReadScript(f);
            // 只收物种特质：领袖特质是另一套，不要混进来
            if (!txt.Contains("species_possible_add", StringComparison.Ordinal) &&
                !txt.Contains("allowed_archetypes", StringComparison.Ordinal)) continue;

            foreach (var (key, body) in Wiki.TopBlocks(txt))
            {
                var name = Resolve(loc.Get(key) ?? "", loc);
                if (name.Length == 0) continue;

                var cat = Val(body, "category") ?? "";
                if (cat.Length == 0)
                    cat = key.StartsWith("trait_pc_", StringComparison.Ordinal) ||
                          key.Contains("preference", StringComparison.Ordinal)
                        ? "planet_pref" : "normal";
                var (catKey, catLabel) = CatLabel(TraitCats, cat, zh);
                if (!TraitCats.ContainsKey(cat))
                    catLabel = cat.Length > 0 ? Wiki.Title(cat) : Nm("其他", "Other", zh);

                var attrs = new List<JsonObject>();
                var cost = Val(body, "cost");
                if (!string.IsNullOrEmpty(cost))
                    attrs.Add(Attr(Nm("基因点数", "Trait points", zh), cost));

                var tags = SubTop(body, "tags");
                if (tags is not null)
                {
                    var tn = new List<string>();
                    foreach (Match m in Regex.Matches(tags, "([a-z_]+)"))
                    {
                        var t = m.Groups[1].Value;
                        var nm = Resolve(loc.Get("trait_tag_" + t) ?? "", loc);
                        if (nm.Length > 0) tn.Add(nm);
                        else if (t == "positive") tn.Add(Nm("正面", "Positive", zh));
                        else if (t == "negative") tn.Add(Nm("负面", "Negative", zh));
                    }
                    if (tn.Count > 0)
                        attrs.Add(Attr(Nm("类型", "Type", zh),
                            string.Join("、", tn.Distinct(StringComparer.Ordinal))));
                }

                var links = new List<string>();
                var opp = SubTop(body, "opposites");
                if (opp is not null)
                {
                    foreach (Match m in Regex.Matches(opp, "\"([a-z_]+)\""))
                    {
                        var nm = Resolve(loc.Get(m.Groups[1].Value) ?? "", loc);
                        if (nm.Length > 0) links.Add(nm);
                    }
                }

                var eff = ModLines(loc, zh, SubTop(body, "modifier"));
                if (eff.Count == 0)
                {
                    var dv = loc.Get(key + "_effects") ?? loc.Get(key + "_desc");
                    if (!string.IsNullOrEmpty(dv)) eff = Resolve(dv, loc).Split('\n').ToList();
                }

                outp.Add(Entry(key, name, Resolve(loc.Get(key + "_desc") ?? "", loc),
                    catKey, catLabel, attrs, Dedup(eff), links, stem));
            }
        }
        return outp;
    }

    // ------------------------------------------------------------ 构建

    private static JsonObject Build(string root, string locLang, bool zh)
    {
        _globalAt = LoadAtVars(root);
        var loc = new Loc();
        foreach (var kv in LoadLoc(Path.Combine(root, D_LOC, locLang))) loc.Raw[kv.Key] = kv.Value;
        foreach (var kv in _globalAt) loc.At[kv.Key] = kv.Value;
        foreach (var kv in LoadScriptedLoc(root)) loc.Scripted[kv.Key] = kv.Value;
        loc.ModIdx = BuildModIdx(loc.Raw);

        var (civics, origins) = CollectCivicsOrigins(root, loc, zh);
        var aps = CollectAps(root, loc, zh);
        var traditions = CollectTraditions(root, loc, zh);
        var edicts = CollectEdicts(root, loc, zh);
        var policies = CollectPolicies(root, loc, zh);
        var relics = CollectRelics(root, loc, zh);
        var specimens = CollectSpecimens(root, loc, zh);
        var tech = CollectTech(root, loc, zh);
        var traits = CollectTraits(root, loc, zh);
        var treeInfo = CollectTradTrees(root, loc, zh);

        var data = new JsonObject
        {
            ["ok"] = true,
            ["supported"] = true,
            ["game"] = "stellaris",
            ["lang"] = locLang,
            ["gameName"] = Nm("群星", "Stellaris", zh),
            ["gameVer"] = GameVersion(root),
            ["civics"] = ObjArr(civics),
            ["origins"] = ObjArr(origins),
            ["aps"] = ObjArr(aps),
            ["traditions"] = ObjArr(traditions),
            ["edicts"] = ObjArr(edicts),
            ["policies"] = ObjArr(policies),
            ["relics"] = ObjArr(relics),
            ["specimens"] = ObjArr(specimens),
            ["tech"] = ObjArr(tech),
            ["traits"] = ObjArr(traits),
            ["treeInfo"] = treeInfo,
        };

        var lists = new Dictionary<string, IReadOnlyList<JsonObject>>(StringComparer.Ordinal)
        {
            ["civics"] = civics, ["origins"] = origins, ["aps"] = aps, ["traditions"] = traditions,
            ["edicts"] = edicts, ["policies"] = policies, ["relics"] = relics, ["specimens"] = specimens,
            ["tech"] = tech, ["traits"] = traits,
        };

        var tabs = new JsonArray();
        var counts = new JsonObject();
        var orders = new JsonObject();
        var total = 0;
        foreach (var (id, icon, zhl, enl, layout) in Tabs)
        {
            var arr = lists[id];
            counts[id] = arr.Count;
            total += arr.Count;

            var order = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var it in arr)
            {
                var k = it["catKey"]?.ToString() ?? "";
                if (k.Length > 0 && seen.Add(k)) order.Add(k);
            }
            if (id == "tech")
                order = TechCats.Keys.Where(k => arr.Any(it => it["catKey"]?.ToString() == k)).ToList();
            orders[id] = StrArr(order);
            tabs.Add(new JsonObject
            {
                ["id"] = id,
                ["icon"] = icon,
                ["label"] = Nm(zhl, enl, zh),
                ["layout"] = layout,
                ["count"] = arr.Count,
            });
        }
        data["tabs"] = tabs;
        data["counts"] = counts;
        data["catOrders"] = orders;
        data["total"] = total;

        // 科技的两级筛选：领域 → 子分类
        var techAreas = new JsonArray();
        foreach (var (ak, azh, aen) in new[]
                 {
                     ("society", "社会学", "Society"),
                     ("physics", "物理学", "Physics"),
                     ("engineering", "工程学", "Engineering"),
                 })
        {
            var cats = new JsonArray();
            foreach (var kv in TechCats)
            {
                if (kv.Value.Area != ak) continue;
                var n = tech.Count(t => (t["cats"] as JsonArray)?.Any(x => x?.ToString() == kv.Key) == true);
                if (n == 0) continue;
                cats.Add(new JsonObject { ["key"] = kv.Key, ["label"] = Nm(kv.Value.Zh, kv.Value.En, zh), ["n"] = n });
            }
            var nArea = tech.Count(t => t["areaKey"]?.ToString() == ak);
            if (nArea == 0) continue;
            techAreas.Add(new JsonObject
            {
                ["key"] = ak, ["label"] = Nm(azh, aen, zh), ["n"] = nArea, ["cats"] = cats,
            });
        }
        data["techAreas"] = techAreas;

        var techTiers = new JsonArray();
        for (var i = 0; i <= 5; i++)
        {
            var n = tech.Count(t => IntOf(t["tier"]) == i);
            if (n == 0) continue;
            techTiers.Add(new JsonObject { ["v"] = i, ["n"] = n });
        }
        data["techTiers"] = techTiers;

        data["techFlags"] = new JsonArray
        {
            new JsonObject
            {
                ["key"] = "dangerous", ["label"] = Nm("危险科技", "Dangerous", zh),
                ["n"] = tech.Count(t => t["dangerous"]?.GetValue<bool>() == true),
            },
            new JsonObject
            {
                ["key"] = "rare", ["label"] = Nm("稀有科技", "Rare", zh),
                ["n"] = tech.Count(t => t["rare"]?.GetValue<bool>() == true),
            },
            new JsonObject
            {
                ["key"] = "repeatable", ["label"] = Nm("循环科技", "Repeatable", zh),
                ["n"] = tech.Count(t => t["repeatable"]?.GetValue<bool>() == true),
            },
        };
        return data;
    }

    private static int IntOf(JsonNode? n) =>
        int.TryParse(n?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static string GameVersion(string root)
    {
        try
        {
            var p = Path.Combine(root, "launcher-settings.json");
            if (!File.Exists(p)) return "";
            var txt = File.ReadAllText(p, Encoding.UTF8).TrimStart('\uFEFF');
            var m = Regex.Match(txt, "\"rawVersion\"\\s*:\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : "";
        }
        catch { return ""; }
    }
}

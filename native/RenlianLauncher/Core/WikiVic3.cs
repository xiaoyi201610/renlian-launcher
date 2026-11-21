using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RenlianLauncher.Core;

/// <summary>
/// 维多利亚 3 百科（旧版 backend/vic3.py 的原生移植）：
///   game/common/laws/*.txt            单条法律（group / icon / progressiveness / modifier …）
///   game/common/law_groups/*.txt      法律组（大类、审议周期、意识形态影响倍率）
///   game/common/ideologies/*.txt      各意识形态对每条法律的立场
///   game/common/interest_groups/*.txt 利益集团（意识形态、权重、按国家给的特质）
///   game/common/interest_group_traits/*.txt 集团特质（好感档位、效果）
///   game/localization/&lt;lang&gt;        官方本地化
/// 注意维3 的脚本在 `game/` 下再套一层，本地化目录叫 `localization`（美式拼写）。
/// </summary>
internal static class WikiVic3
{
    private const int V3Ver = 7;                 // 解析逻辑变更时 +1，参与缓存签名

    private static readonly string D_LAWS = Path.Combine("common", "laws");
    private static readonly string D_GROUPS = Path.Combine("common", "law_groups");
    private static readonly string D_IDEO = Path.Combine("common", "ideologies");
    private static readonly string D_IG = Path.Combine("common", "interest_groups");
    private static readonly string D_IGT = Path.Combine("common", "interest_group_traits");
    private static readonly string D_DLC = "dlc";
    private static readonly string D_LOC = "localization";

    private static readonly Dictionary<string, string> LangMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh"] = "simp_chinese", ["en"] = "english", ["fr"] = "french", ["de"] = "german",
        ["sv"] = "english", ["ru"] = "russian", ["ja"] = "japanese", ["ko"] = "korean",
        ["pl"] = "polish", ["es"] = "spanish", ["pt"] = "braz_por",
    };

    private static readonly (string Id, string Icon, string Label, string Layout)[] Tabs =
    {
        ("laws", "⚖", "法律", "generic"),
        ("igs", "🏛", "利益集团", "generic"),
        ("igtraits", "✧", "集团特质", "generic"),
    };

    private static readonly Dictionary<string, string> ApprovalLabel = new(StringComparer.Ordinal)
    {
        ["angry"] = "愤怒", ["unhappy"] = "不满", ["neutral"] = "中立",
        ["happy"] = "满意", ["loyal"] = "忠诚",
    };
    private static readonly string[] ApprovalOrder = { "angry", "unhappy", "neutral", "happy", "loyal" };

    // 集团权重：脚本里都是 `key = { value = … }`，界面只取基础值
    private static readonly (string Key, string Label)[] IgWeights =
    {
        ("pop_weight", "人口权重"),
        ("monarch_weight", "君主权重"),
        ("agitator_weight", "鼓动者权重"),
        ("commander_weight", "指挥官权重"),
        ("magnate_weight", "巨头权重"),
        ("executive_weight", "高管权重"),
        ("noble_chance", "贵族加入概率"),
        ("female_commander_chance", "女性指挥官"),
        ("female_executive_chance", "女性高管"),
        ("female_monarch_chance", "女性君主"),
        ("female_politician_chance", "女性政治家"),
        ("female_agitator_chance", "女性鼓动者"),
    };

    private static readonly string[] AreaOrder = { "power_structure", "economy", "human_rights" };
    private static readonly Dictionary<string, string> AreaLabel = new(StringComparer.Ordinal)
    {
        ["power_structure"] = "权力结构", ["economy"] = "经济", ["human_rights"] = "人权",
    };

    private static readonly Dictionary<string, string> StanceLabel = new(StringComparer.Ordinal)
    {
        ["strongly_approve"] = "强烈支持", ["approve"] = "支持", ["neutral"] = "中立",
        ["disapprove"] = "反对", ["strongly_disapprove"] = "强烈反对",
    };
    private static readonly Dictionary<string, int> StanceV = new(StringComparer.Ordinal)
    {
        ["strongly_approve"] = 2, ["approve"] = 1, ["neutral"] = 0,
        ["disapprove"] = -1, ["strongly_disapprove"] = -2,
    };
    private static readonly string[] StanceOrder =
    {
        "strongly_approve", "approve", "neutral", "disapprove", "strongly_disapprove",
    };

    // 税率档位（法律里的 tax_modifier_* 块）
    private static readonly (string Key, string Label)[] TaxTiers =
    {
        ("tax_modifier_very_low", "极低税率"),
        ("tax_modifier_low", "低税率"),
        ("tax_modifier_medium", "中等税率"),
        ("tax_modifier_high", "高税率"),
        ("tax_modifier_very_high", "极高税率"),
    };

    // 脚本里没有本地化的修饰器（极少，兜底成可读中文）
    private static readonly Dictionary<string, string> ModAlias = new(StringComparer.Ordinal)
    {
        ["country_legitimacy_headofstate_add"] = "国家元首所属利益集团执政带来的合法性",
    };

    // ------------------------------------------------------------ 正则

    private static readonly Regex LocRe = new("^\\s*([A-Za-z0-9_.\\-]+):(\\d+)?\\s*\"((?:[^\"\\\\]|\\\\.)*)\"",
        RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex FuncRe = new(@"\[Concept\('([^']+)'[^\]]*\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GetxRe = new(@"\[(Get[A-Za-z]+)\(\s*'([^']+)'\s*\)\.([A-Za-z]+)\]",
        RegexOptions.Compiled);
    private static readonly Regex BrackRe = new(@"\[([^\[\]]+)\]", RegexOptions.Compiled);
    private static readonly Regex VarRe = new(@"\$([^$]+)\$", RegexOptions.Compiled);
    private static readonly Regex IconRe = new(@"@[a-zA-Z0-9_]+!", RegexOptions.Compiled);
    private static readonly Regex FmtRe = new(@"#[a-zA-Z]+", RegexOptions.Compiled);
    private static readonly Regex ModLineRe =
        new(@"([a-z][a-z0-9_]*)\s*=\s*(-?\d+(?:\.\d+)?|yes|no)\b", RegexOptions.Compiled);
    private static readonly Regex ListItemRe = new(@"[a-zA-Z_][a-zA-Z0-9_]*", RegexOptions.Compiled);

    // ------------------------------------------------------------ 对外入口

    public static JsonObject Load(string gameRoot, string uiLang, bool force)
    {
        uiLang = string.IsNullOrWhiteSpace(uiLang) ? "zh" : uiLang.Trim().ToLowerInvariant();
        var lang = LangMap.TryGetValue(uiLang, out var l) ? l : "simp_chinese";

        var root = GameRoot(gameRoot);
        if (root.Length == 0)
            return new JsonObject
            {
                ["ok"] = false,
                ["supported"] = true,
                ["game"] = "vic3",
                ["gameName"] = "Victoria 3",
                ["tabs"] = new JsonArray(),
                ["laws"] = new JsonArray(),
                ["lawGroups"] = new JsonArray(),
                ["lawAreas"] = new JsonArray(),
                ["igs"] = new JsonArray(),
                ["igTraits"] = new JsonArray(),
                ["igList"] = new JsonArray(),
                ["error"] = "没找到游戏本体目录，请在设置里确认《维多利亚 3》的安装位置",
            };

        var gdir = GameDir(root);
        var sig = string.Join("|", new[]
        {
            V3Ver.ToString(), root, lang,
            Mtime(gdir, D_LAWS), Mtime(gdir, D_IG), Mtime(gdir, D_IGT),
        });

        long ms = 0;
        var data = Wiki.Cached("vic3", lang, root, sig, force, () =>
        {
            var t0 = Environment.TickCount64;
            var d = Build(root, uiLang);
            ms = Environment.TickCount64 - t0;
            return d;
        }, out var cached);
        data["buildSeconds"] = cached ? 0 : Math.Round(ms / 1000.0, 2);
        return data;
    }

    private static string Mtime(string gdir, string rel)
    {
        try { return rel + "=" + new DateTimeOffset(Directory.GetLastWriteTimeUtc(Path.Combine(gdir, rel))).ToUnixTimeSeconds(); }
        catch { return "0"; }
    }

    private static string GameRoot(string hint)
    {
        hint = (hint ?? "").Trim().Trim('"').TrimEnd('\\', '/');
        var cands = new List<string>();
        if (hint.Length > 0)
        {
            cands.Add(hint);
            cands.Add(Path.Combine(hint, "Victoria 3"));
        }
        foreach (var c in cands)
        {
            if (Directory.Exists(Path.Combine(c, "game", D_LAWS))) return c;
            if (Directory.Exists(Path.Combine(c, D_LAWS))) return c;
        }
        return "";
    }

    /// <summary>脚本目录（游戏本体在 game/ 下再套一层）。</summary>
    private static string GameDir(string root) =>
        Directory.Exists(Path.Combine(root, "game", D_LAWS)) ? Path.Combine(root, "game") : root;

    private static string GameVersion(string root)
    {
        try
        {
            var p = Path.Combine(root, "launcher", "launcher-settings.json");
            if (File.Exists(p))
            {
                var t = Read(p);
                var m = Regex.Match(t, "\"rawVersion\"\\s*:\\s*\"([^\"]+)\"");
                if (m.Success) return m.Groups[1].Value;
                m = Regex.Match(t, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                if (m.Success) return m.Groups[1].Value;
            }
            var b = Path.Combine(root, "game", "checksum_manifest.txt");
            if (File.Exists(b)) return File.GetLastWriteTime(b).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        catch { }
        return "";
    }

    // ------------------------------------------------------------ 基础工具

    private static string Read(string path)
    {
        try { return File.ReadAllText(path, Encoding.UTF8).TrimStart('\uFEFF'); }
        catch { return ""; }
    }

    private static IEnumerable<string> Files(string dir, bool recursive = false)
    {
        try
        {
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            return Directory.GetFiles(dir, "*.txt", recursive
                ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.Ordinal);
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>取第一条 `key = { … }` 的块正文（不穿透嵌套）。</summary>
    private static string Sub(string? body, string key)
    {
        if (string.IsNullOrEmpty(body)) return "";
        var m = Regex.Match(body, @"(?:^|[\s{])" + Regex.Escape(key) + @"\s*=\s*\{");
        if (!m.Success) return "";
        var st = body.IndexOf('{', m.Index + m.Length - 1);
        if (st < 0) return "";
        var d = 0;
        for (var j = st; j < body.Length; j++)
        {
            if (body[j] == '{') d++;
            else if (body[j] == '}')
            {
                d--;
                if (d == 0) return body[(st + 1)..j];
            }
        }
        return "";
    }

    private static string Scalar(string? body, string key)
    {
        if (string.IsNullOrEmpty(body)) return "";
        var m = Regex.Match(body, @"(?:^|[\s{])" + Regex.Escape(key) + @"\s*=\s*""?([^""\s{}]+)""?");
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>取顶层 `name = { … }` 的正文（只从行首匹配）。</summary>
    private static string BlockBody(string text, string name)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var m = Regex.Match(text, "^" + Regex.Escape(name) + @"\s*=\s*\{", RegexOptions.Multiline);
        if (!m.Success) return "";
        var st = text.IndexOf('{', m.Index + m.Length - 1);
        if (st < 0) return "";
        var d = 0;
        for (var j = st; j < text.Length; j++)
        {
            if (text[j] == '{') d++;
            else if (text[j] == '}')
            {
                d--;
                if (d == 0) return text[(st + 1)..j];
            }
        }
        return "";
    }

    private static List<string> ListBlock(string? body, string key)
    {
        var b = Sub(body, key);
        if (b.Length == 0) return new List<string>();
        return ListItemRe.Matches(b).Select(m => m.Value).ToList();
    }

    // ------------------------------------------------------------ 本地化

    private static Dictionary<string, string> LoadLoc(string locDir)
    {
        var o = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(locDir)) return o;
        IEnumerable<string> files;
        try { files = Directory.GetFiles(locDir, "*.yml", SearchOption.AllDirectories); }
        catch { return o; }
        foreach (var f in files)
        {
            var txt = Read(f);
            if (txt.Length == 0) continue;
            foreach (Match m in LocRe.Matches(txt)) o.TryAdd(m.Groups[1].Value, m.Groups[3].Value);
        }
        return o;
    }

    private static string Clean(string? s, Dictionary<string, string> loc, int depth = 0)
    {
        if (string.IsNullOrEmpty(s) || depth > 8) return s ?? "";

        string SubKey(string k) =>
            loc.TryGetValue(k, out var raw) ? Clean(raw, loc, depth + 1) : "";

        var t = FuncRe.Replace(s, m => SubKey(m.Groups[1].Value));
        t = GetxRe.Replace(t, m => SubKey(m.Groups[2].Value));
        t = BrackRe.Replace(t, m =>
        {
            var k = m.Groups[1].Value.Split('|')[0].Trim();
            return k.StartsWith("concept_", StringComparison.Ordinal) || loc.ContainsKey(k) ? SubKey(k) : "";
        });
        t = VarRe.Replace(t, m => SubKey(m.Groups[1].Value.Split('|')[0].Trim()));
        t = IconRe.Replace(t, "");
        t = FmtRe.Replace(t, "");
        t = t.Replace("#!", "");
        t = t.Replace("[Nbsp]", " ").Replace("\\n", " ").Replace("\\t", " ");
        t = t.Replace("”", "").Replace("“", "");
        return Regex.Replace(t, @"\s+", " ").Trim();
    }

    private static string L(Dictionary<string, string> loc, string key) =>
        Clean(loc.TryGetValue(key, out var v) ? v : "", loc);

    private static string LocOrKey(Dictionary<string, string> loc, string key)
    {
        var s = L(loc, key);
        return s.Length > 0 ? s : key;
    }

    // ------------------------------------------------------------ 修饰器

    private static string Sign(string s) => s.StartsWith('-') ? s : "+" + s;

    private static string TrimNum(double f)
    {
        if (Math.Abs(f) < 1e-12) f = 0;
        var s = f.ToString("F4", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        return s.Length == 0 || s == "-" ? "0" : s;
    }

    /// <summary>按修饰器后缀判定单位：`_mult`/`_factor`/无后缀按百分比，`_add` 为绝对值（0~1 仍按比率）。</summary>
    private static string FmtNum(string key, string val)
    {
        if (!double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return val;
        var pct = !key.EndsWith("_add", StringComparison.Ordinal) || (Math.Abs(f) < 1 && f != 0);
        if (pct)
        {
            var x = f * 100.0;
            if (Math.Abs(x - Math.Round(x)) < 1e-6) return Sign(((long)Math.Round(x)).ToString(CultureInfo.InvariantCulture)) + "%";
            return Sign(TrimNum(x)) + "%";
        }
        if (Math.Abs(f - Math.Round(f)) < 1e-9)
            return Sign(((long)Math.Round(f)).ToString(CultureInfo.InvariantCulture));
        return Sign(TrimNum(f));
    }

    private static List<string> ModLines(string? body, Dictionary<string, string> loc)
    {
        var outp = new List<string>();
        if (string.IsNullOrEmpty(body)) return outp;
        foreach (Match m in ModLineRe.Matches(body))
        {
            var key = m.Groups[1].Value;
            var val = m.Groups[2].Value;
            var name = ModAlias.TryGetValue(key, out var al) ? al : L(loc, key);
            if (name.Length == 0) continue;

            string line;
            if (val is "yes" or "no")
            {
                line = name + "：" + (val == "yes" ? "是" : "否");
            }
            else
            {
                // `_add` 为 0 的没有信息量（脚本里写 0 是为了显式覆盖父级）
                if (key.EndsWith("_add", StringComparison.Ordinal) &&
                    double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var fv) && fv == 0)
                    continue;
                line = name + " " + FmtNum(key, val);
            }
            if (!outp.Contains(line)) outp.Add(line);
        }
        return outp;
    }

    // ------------------------------------------------------------ 法律

    private sealed class Group
    {
        public string Key = "";
        public string Area = "";
        public int Days = 100;
        public string IdeoMult = "";
        public int Order;
    }

    private static List<Group> ScanGroups(string gdir)
    {
        var outp = new List<Group>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in Files(gdir))
        {
            var txt = Read(f);
            if (txt.Length == 0) continue;
            foreach (Match m in Regex.Matches(txt, @"^(lawgroup_[a-z0-9_]+)\s*=\s*\{", RegexOptions.Multiline))
            {
                var key = m.Groups[1].Value;
                if (!seen.Add(key)) continue;
                var body = BlockBody(txt, key);
                var cat = Scalar(body, "law_group_category");
                if (cat.Length == 0) cat = "power_structure";
                var days = Scalar(body, "base_enactment_days");
                outp.Add(new Group
                {
                    Key = key,
                    Area = cat,
                    Days = days.Length > 0 && days.All(char.IsDigit) ? int.Parse(days, CultureInfo.InvariantCulture) : 100,
                    IdeoMult = Scalar(body, "ideological_opinion_impact"),
                    Order = outp.Count,
                });
            }
        }
        return outp;
    }

    private static string IconRel(string field)
    {
        if (string.IsNullOrEmpty(field)) return "";
        var b = Path.GetFileName(field.Replace('\\', '/'));
        return Path.GetFileNameWithoutExtension(b);
    }

    private static List<string> CondLines(string body, Dictionary<string, string> loc)
    {
        var outp = new List<string>();
        if (string.IsNullOrEmpty(body)) return outp;
        foreach (Match m in Regex.Matches(body, @"custom_tooltip\s*=\s*([a-z0-9_]+)"))
        {
            var t = L(loc, m.Groups[1].Value);
            if (t.Length > 0 && !outp.Contains(t)) outp.Add(t);
        }
        return outp.Take(4).ToList();
    }

    private static JsonObject Law(
        string key, string body, Dictionary<string, string> loc, Dictionary<string, Group> gmap)
    {
        var groupKey = Scalar(body, "group");
        gmap.TryGetValue(groupKey, out var g);
        var prog = Scalar(body, "progressiveness");
        var progV = int.TryParse(prog, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pv) ? pv : 0;

        var effects = ModLines(Sub(body, "modifier"), loc);
        var accFx = ModLines(Sub(body, "acceptance_modifier"), loc);

        var instKey = Scalar(body, "institution");
        var instFx = ModLines(Sub(body, "institution_modifier"), loc);
        if (instKey.Length > 0 && instFx.Count > 0)
        {
            var iname = LocOrKey(loc, instKey);
            instFx = instFx.Select(x => iname + " " + x).ToList();
        }

        var taxTiers = new JsonArray();
        foreach (var (tk, tlabel) in TaxTiers)
        {
            var tb = Sub(body, tk);
            if (tb.Length == 0) continue;
            var tl = ModLines(tb, loc);
            if (tl.Count == 0) continue;
            taxTiers.Add(new JsonObject { ["label"] = tlabel, ["effects"] = StrArr(tl) });
        }

        var techs = ListBlock(body, "unlocking_technologies").Select(t => LocOrKey(loc, t)).ToList();
        var disallow = ListBlock(body, "disallowing_laws").Select(t => LocOrKey(loc, t)).ToList();
        var reqOr = ListBlock(body, "requires_law_or").Select(t => LocOrKey(loc, t)).ToList();
        var unlockLaws = ListBlock(body, "unlocking_laws").Select(t => LocOrKey(loc, t)).ToList();

        var parent = Scalar(body, "parent");
        var cond = CondLines(Sub(body, "can_enact"), loc);
        var name = LocOrKey(loc, key);
        var desc = L(loc, key + "_desc");

        var attrs = new List<JsonObject>();
        if (g is not null && g.Days > 0)
            attrs.Add(new JsonObject { ["label"] = "审议周期", ["value"] = g.Days + " 天" });
        if (g is not null && g.IdeoMult.Length > 0 &&
            double.TryParse(g.IdeoMult, NumberStyles.Float, CultureInfo.InvariantCulture, out var mf) &&
            Math.Abs(mf - 1.0) > 1e-6)
            attrs.Add(new JsonObject
            {
                ["label"] = "意识形态影响",
                ["value"] = "×" + mf.ToString("G6", CultureInfo.InvariantCulture),
            });

        return new JsonObject
        {
            ["key"] = key,
            ["name"] = name,
            ["desc"] = desc,
            ["cat"] = LocOrKey(loc, groupKey),
            ["catKey"] = groupKey,
            ["area"] = g is not null && AreaLabel.TryGetValue(g.Area, out var al) ? al : "",
            ["icon"] = IconRel(Scalar(body, "icon")),
            ["prog"] = progV,
            ["attrs"] = ObjArr(attrs),
            ["effects"] = StrArr(effects),
            ["accFx"] = StrArr(accFx),
            ["instFx"] = StrArr(instFx),
            ["taxTiers"] = taxTiers,
            ["techs"] = StrArr(techs),
            ["disallow"] = StrArr(disallow),
            ["reqOr"] = StrArr(reqOr),
            ["unlockLaws"] = StrArr(unlockLaws),
            ["cond"] = StrArr(cond),
            ["parent"] = parent,
            ["stances"] = new JsonArray(),
            ["subLaws"] = new JsonArray(),
            ["secs"] = new JsonArray(),
        };
    }

    private static Dictionary<string, List<JsonObject>> ScanStances(string idir, Dictionary<string, string> loc)
    {
        var outp = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        var fnameLabel = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["00_ig_ideologies.txt"] = "利益集团",
            ["00_ig_ideologies_flavored.txt"] = "利益集团·特色",
            ["00_ig_ideologies_event.txt"] = "事件意识形态",
            ["03_ig_ideologies_movement.txt"] = "政治运动",
            ["00_rlit_ideologies.txt"] = "宗教意识形态",
            ["01_character_ideologies.txt"] = "人物意识形态",
            ["02_character_french_flavored.txt"] = "人物意识形态·法国",
        };
        foreach (var f in Files(idir))
        {
            var txt = Read(f);
            if (txt.Length == 0) continue;
            var src = fnameLabel.TryGetValue(Path.GetFileName(f), out var sl) ? sl : "意识形态";
            foreach (Match m in Regex.Matches(txt, @"^([a-z][a-z0-9_]+)\s*=\s*\{", RegexOptions.Multiline))
            {
                var ideoKey = m.Groups[1].Value;
                if (ideoKey.StartsWith("law", StringComparison.Ordinal) || ideoKey == "icon") continue;
                var body = BlockBody(txt, ideoKey);
                if (body.Length == 0 || !body.Contains("lawgroup_", StringComparison.Ordinal)) continue;
                var iname = LocOrKey(loc, ideoKey);
                foreach (Match lm in Regex.Matches(body,
                             @"(law_[a-z0-9_]+)\s*=\s*(strongly_approve|approve|neutral|disapprove|strongly_disapprove)"))
                {
                    var law = lm.Groups[1].Value;
                    var stance = lm.Groups[2].Value;
                    if (!outp.TryGetValue(law, out var list))
                    {
                        list = new List<JsonObject>();
                        outp[law] = list;
                    }
                    list.Add(new JsonObject
                    {
                        ["key"] = ideoKey,
                        ["name"] = iname,
                        ["v"] = StanceV.TryGetValue(stance, out var sv) ? sv : 0,
                        ["label"] = StanceLabel.TryGetValue(stance, out var slab) ? slab : stance,
                        ["src"] = src,
                    });
                }
            }
        }
        return outp;
    }

    // ------------------------------------------------------------ 利益集团

    private static string HsvCss(string body)
    {
        var m = Regex.Match(body, @"color\s*=\s*hsv\s*\{\s*([\d.]+)\s+([\d.]+)\s+([\d.]+)");
        if (!m.Success) return "";
        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ||
            !double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ||
            !double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return "";
        var (r, g, b) = HsvToRgb(h % 1.0, Math.Min(s, 1.0), Math.Min(v, 1.0));
        return $"#{To255(r):x2}{To255(g):x2}{To255(b):x2}";
    }

    private static int To255(double x) => (int)Math.Round(x * 255);

    private static (double R, double G, double B) HsvToRgb(double h, double s, double v)
    {
        if (s <= 0) return (v, v, v);
        var i = (int)Math.Floor(h * 6);
        var f = h * 6 - i;
        var p = v * (1 - s);
        var q = v * (1 - f * s);
        var t = v * (1 - (1 - f) * s);
        return (i % 6) switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
    }

    /// <summary>把 `if / else_if / else` 切成 (kind, 块正文, 前置注释)——用于 on_enable 的国家分支。</summary>
    private static List<(string Kind, string Body, string Note)> SplitBranches(string body)
    {
        var outp = new List<(string, string, string)>();
        if (string.IsNullOrEmpty(body)) return outp;
        var pat = new Regex(@"\b(if|else_if|else)\s*=\s*\{");
        var i = 0;
        var prev = 0;
        while (true)
        {
            var m = pat.Match(body, i);
            if (!m.Success) break;
            var lead = body[prev..m.Index];
            var cm = Regex.Matches(lead, @"#\s*([^\n]+)");
            var note = cm.Count > 0 ? cm[^1].Groups[1].Value.Trim() : "";
            var st = body.IndexOf('{', m.Index + m.Length - 1);
            if (st < 0) break;
            var lnEnd = body.IndexOf('\n', st);
            var inline = Regex.Matches(body[(st + 1)..(lnEnd > 0 ? lnEnd : body.Length)], @"#\s*([^\n]+)");
            if (inline.Count > 0) note = inline[^1].Groups[1].Value.Trim();
            var d = 0;
            var end = -1;
            for (var j = st; j < body.Length; j++)
            {
                if (body[j] == '{') d++;
                else if (body[j] == '}')
                {
                    d--;
                    if (d == 0) { end = j; break; }
                }
            }
            if (end < 0) break;
            outp.Add((m.Groups[1].Value, body[(st + 1)..end], note));
            prev = end + 1;
            i = end + 1;
        }
        return outp;
    }

    private static Dictionary<string, string> DlcNames(string gdir, Dictionary<string, string> loc)
    {
        var o = new Dictionary<string, string>(StringComparer.Ordinal);
        var ddir = Path.Combine(gdir, D_DLC);
        if (!Directory.Exists(ddir)) return o;
        foreach (var name in Directory.GetDirectories(ddir).Select(Path.GetFileName))
        {
            if (name is null) continue;
            var parts = name.Split('_', 2);
            if (parts.Length < 2 || !parts[0].ToLowerInvariant().StartsWith("dlc", StringComparison.Ordinal)) continue;
            var code = parts[1];
            var label = "";
            try
            {
                var jf = Directory.GetFiles(Path.Combine(ddir, name), "*.dlc.json").FirstOrDefault();
                if (jf is not null)
                {
                    using var doc = JsonDocument.Parse(Read(jf));
                    if (doc.RootElement.TryGetProperty("displayName", out var dn) &&
                        dn.TryGetProperty("en", out var en))
                        label = (en.GetString() ?? "").Trim();
                }
            }
            catch { label = ""; }
            if (label.Length == 0) label = L(loc, parts[0]);
            if (label.Length > 0) o[code] = label;
        }
        return o;
    }

    private static List<JsonObject> ScanIgs(string gdir, Dictionary<string, string> loc,
        Dictionary<string, string> dlcs)
    {
        var outp = new List<JsonObject>();
        foreach (var f in Files(Path.Combine(gdir, D_IG)))
        {
            var txt = Read(f);
            if (txt.Length == 0) continue;
            foreach (Match m in Regex.Matches(txt, @"^(ig_[a-z0-9_]+)\s*=\s*\{", RegexOptions.Multiline))
            {
                var key = m.Groups[1].Value;
                var body = BlockBody(txt, key);

                var ideos = ListBlock(body, "ideologies").Select(ik => new JsonObject
                {
                    ["key"] = ik, ["name"] = LocOrKey(loc, ik), ["desc"] = L(loc, ik + "_desc"),
                }).ToList();
                var charIdeos = ListBlock(body, "character_ideologies").Select(ik => new JsonObject
                {
                    ["key"] = ik, ["name"] = LocOrKey(loc, ik),
                }).ToList();
                var baseTraits = ListBlock(body, "traits").Select(tk => new JsonObject
                {
                    ["key"] = tk, ["name"] = LocOrKey(loc, tk),
                }).ToList();

                var weights = new List<JsonObject>();
                foreach (var (wk, wlabel) in IgWeights)
                {
                    var wb = Sub(body, wk);
                    if (wb.Length == 0) continue;
                    var v = Scalar(wb, "value");
                    if (v.Length == 0) continue;
                    var shown = double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var fv)
                        ? fv.ToString("G6", CultureInfo.InvariantCulture) : v;
                    weights.Add(new JsonObject { ["label"] = wlabel, ["value"] = shown });
                }

                var excl = new List<JsonObject>();
                foreach (var (kind, blk, note) in SplitBranches(Sub(body, "on_enable")))
                {
                    var tks = Regex.Matches(blk, @"set_ig_trait\s*=\s*ig_trait:([a-z0-9_]+)")
                        .Select(x => x.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();
                    if (tks.Count == 0) continue;
                    var tags = Regex.Matches(blk, @"\bc:([A-Z]{3})\b")
                        .Select(x => x.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();
                    var dlcKeys = Regex.Matches(blk, @"has_dlc_feature\s*=\s*([a-z0-9_]+)")
                        .Select(x => x.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();

                    string who;
                    if (tags.Count > 0)
                    {
                        who = string.Join(" / ", tags.Select(t => LocOrKey(loc, t)));
                    }
                    else
                    {
                        var rels = Regex.Matches(blk, @"country_has_state_religion\s*=\s*rel:([a-z0-9_]+)")
                            .Select(x => x.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();
                        var cults = Regex.Matches(blk, @"country_has_primary_culture\s*=\s*cu:([a-z0-9_]+)")
                            .Select(x => x.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();
                        if (rels.Count > 0)
                        {
                            who = "国教：" + string.Join(" / ", rels.Select(r => LocOrKey(loc, r)));
                            if (cults.Count > 0)
                                who += "，主流文化 " + string.Join(" / ", cults.Select(c => LocOrKey(loc, c)));
                        }
                        else if (cults.Count > 0)
                        {
                            who = "主流文化：" + string.Join(" / ", cults.Select(c => LocOrKey(loc, c)));
                        }
                        else if (note.Length > 0) who = "其它条件：" + note;
                        else if (kind == "else") who = "其他国家";
                        else who = "其它条件";
                    }
                    if (dlcKeys.Count > 0)
                    {
                        var names = dlcKeys.Select(dk => dlcs.TryGetValue(
                            dk.Replace("_content", ""), out var dn) ? dn : "").Where(x => x.Length > 0).ToList();
                        who += "（需 " + (names.Count > 0 ? string.Join("、", names) : "DLC") + "）";
                    }

                    excl.Add(new JsonObject
                    {
                        ["who"] = who,
                        ["traits"] = StrArr(tks.Select(tk => LocOrKey(loc, tk))),
                        ["tkeys"] = StrArr(tks),
                        ["dlc"] = StrArr(dlcKeys),
                        ["tags"] = StrArr(tags),
                        ["uncond"] = kind == "else",
                    });
                }

                outp.Add(new JsonObject
                {
                    ["key"] = key,
                    ["name"] = LocOrKey(loc, key),
                    ["desc"] = L(loc, key + "_desc"),
                    ["icon"] = IconRel(Scalar(body, "texture")),
                    ["iconKind"] = "ig",
                    ["cat"] = "利益集团",
                    ["color"] = HsvCss(body),
                    ["index"] = Scalar(body, "index"),
                    ["ideos"] = ObjArr(ideos),
                    ["charIdeos"] = ObjArr(charIdeos),
                    ["baseTraits"] = ObjArr(baseTraits),
                    ["weights"] = ObjArr(weights),
                    ["exclusive"] = ObjArr(excl),
                    ["secs"] = new JsonArray(),
                });
            }
        }

        // 按脚本里的 index 排序（8 个集团固定顺序）
        var indexed = outp.Select((it, i) => (it, i)).ToList();
        indexed.Sort((a, b) =>
        {
            var ia = int.TryParse(a.it["index"]?.ToString(), out var va) ? va : 99;
            var ib = int.TryParse(b.it["index"]?.ToString(), out var vb) ? vb : 99;
            return ia != ib ? ia - ib : a.i - b.i;
        });
        return indexed.Select(x => x.it).ToList();
    }

    private static List<JsonObject> ScanIgTraits(string tdir, Dictionary<string, string> loc,
        Dictionary<string, List<string>> owner, Dictionary<string, JsonObject> igMeta,
        Dictionary<string, int> igOrder)
    {
        var outp = new List<JsonObject>();
        foreach (var f in Files(tdir))
        {
            var txt = Read(f);
            if (txt.Length == 0) continue;
            foreach (Match m in Regex.Matches(txt, @"^(ig_trait_[a-z0-9_]+)\s*=\s*\{", RegexOptions.Multiline))
            {
                var key = m.Groups[1].Value;
                var body = BlockBody(txt, key);
                owner.TryGetValue(key, out var owners);
                owners ??= new List<string>();
                // 归属：优先按脚本里的引用，读不到再退回文件名（00_landed_interest → landowners）
                if (owners.Count == 0)
                {
                    var guess = Path.GetFileName(f).Replace("_traits.txt", "")
                        .Split('_', 2).Last().Replace("landed_interest", "landowners");
                    owners = new List<string> { "ig_" + guess };
                }
                var cat = string.Join("、", owners.Select(o =>
                    igMeta.TryGetValue(o, out var meta) ? meta["name"]?.ToString() ?? o : o));
                var catKey = owners[0];
                var color = igMeta.TryGetValue(catKey, out var cm) ? cm["color"]?.ToString() ?? "" : "";

                var lo = Scalar(body, "min_approval");
                var hi = Scalar(body, "max_approval");
                string cond;
                if (lo.Length > 0) cond = (ApprovalLabel.TryGetValue(lo, out var ll) ? ll : lo) + "及以上";
                else if (hi.Length > 0) cond = (ApprovalLabel.TryGetValue(hi, out var hl) ? hl : hi) + "及以下";
                else cond = "始终生效";

                var effects = ModLines(Sub(body, "modifier"), loc);
                var approvalKey = lo.Length > 0 ? lo : hi;
                var attrs = new List<JsonObject>
                {
                    new() { ["label"] = "生效条件", ["value"] = cond },
                };
                if (owners.Count > 1)
                    attrs.Add(new JsonObject { ["label"] = "归属", ["value"] = owners.Count + " 个集团共用" });

                outp.Add(new JsonObject
                {
                    ["key"] = key,
                    ["name"] = LocOrKey(loc, key),
                    ["desc"] = L(loc, key + "_desc"),
                    ["icon"] = IconRel(Scalar(body, "icon")),
                    ["iconKind"] = "igtrait",
                    ["cat"] = cat,
                    ["catKey"] = catKey,
                    ["color"] = color,
                    ["approval"] = cond,
                    ["approvalKey"] = approvalKey,
                    ["attrs"] = ObjArr(attrs),
                    ["effects"] = StrArr(effects),
                    ["secs"] = new JsonArray(),
                });
            }
        }

        var indexed = outp.Select((it, i) => (it, i)).ToList();
        indexed.Sort((a, b) =>
        {
            var ca = a.it["catKey"]?.ToString() ?? "";
            var cb = b.it["catKey"]?.ToString() ?? "";
            var oa = igOrder.TryGetValue(ca, out var va) ? va : 99;
            var ob = igOrder.TryGetValue(cb, out var vb) ? vb : 99;
            if (oa != ob) return oa - ob;
            var aa = a.it["approvalKey"]?.ToString() ?? "";
            var ab = b.it["approvalKey"]?.ToString() ?? "";
            var sa = Array.IndexOf(ApprovalOrder, aa);
            var sb = Array.IndexOf(ApprovalOrder, ab);
            if (sa < 0) sa = 9;
            if (sb < 0) sb = 9;
            if (sa != sb) return sa - sb;
            return string.Compare(a.it["name"]?.ToString(), b.it["name"]?.ToString(), StringComparison.Ordinal);
        });
        return indexed.Select(x => x.it).ToList();
    }

    // ------------------------------------------------------------ 构建

    private static JsonObject Build(string root, string uiLang)
    {
        var lang = LangMap.TryGetValue(uiLang, out var l) ? l : "simp_chinese";
        var gdir = GameDir(root);
        var loc = LoadLoc(Path.Combine(gdir, D_LOC, lang));
        if (loc.Count == 0) loc = LoadLoc(Path.Combine(gdir, D_LOC, "english"));

        var groups = ScanGroups(Path.Combine(gdir, D_GROUPS));
        var items = new List<JsonObject>();
        var gmap = groups.ToDictionary(g => g.Key, StringComparer.Ordinal);
        {
            var byKey = new Dictionary<string, (string Key, string Body)>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var f in Files(Path.Combine(gdir, D_LAWS)))
            {
                var txt = Read(f);
                if (txt.Length == 0) continue;
                foreach (Match m in Regex.Matches(txt, @"^(law_[a-z0-9_]+)\s*=\s*\{", RegexOptions.Multiline))
                {
                    var key = m.Groups[1].Value;
                    if (key.StartsWith("lawgroup_", StringComparison.Ordinal)) continue;
                    var body = BlockBody(txt, key);
                    if (!byKey.ContainsKey(key)) order.Add(key);
                    byKey[key] = (key, body);
                }
            }
            foreach (var key in order) items.Add(Law(key, byKey[key].Body, loc, gmap));
        }

        var stances = ScanStances(Path.Combine(gdir, D_IDEO), loc);
        var assets = items.ToDictionary(it => it["key"]!.ToString()!, StringComparer.Ordinal);

        // 变体继承父法的意识形态立场
        foreach (var (k, lst) in stances)
            if (assets.TryGetValue(k, out var asset))
                asset["stances"] = ObjArr(lst);

        foreach (var it in items)
        {
            var p = it["parent"]?.ToString() ?? "";
            if (p.Length > 0 && assets.TryGetValue(p, out var parent))
            {
                var sub = parent["subLaws"] as JsonArray ?? new JsonArray();
                sub.Add(new JsonObject { ["key"] = it["key"]?.ToString(), ["name"] = it["name"]?.ToString() });
                parent["subLaws"] = sub;
            }
        }
        foreach (var it in items)
        {
            var st = it["stances"] as JsonArray;
            var p = it["parent"]?.ToString() ?? "";
            if ((st is null || st.Count == 0) && p.Length > 0 && assets.TryGetValue(p, out var parent))
            {
                if (parent["stances"] is JsonArray pv && pv.Count > 0)
                {
                    var copied = new JsonArray();
                    foreach (var x in pv)
                    {
                        if (x is not JsonObject xo) continue;
                        var clone = new JsonObject();
                        foreach (var kv in xo) clone[kv.Key] = kv.Value?.DeepClone();
                        clone["inherited"] = true;
                        copied.Add(clone);
                    }
                    it["stances"] = copied;
                }
            }
        }

        // 排序：大类 → 组（脚本顺序）→ 进步性升序
        foreach (var it in items)
        {
            var gk = it["catKey"]?.ToString() ?? "";
            gmap.TryGetValue(gk, out var g);
            var aorder = 9;
            if (g is not null)
            {
                var ai = Array.IndexOf(AreaOrder, g.Area);
                if (ai >= 0) aorder = ai;
            }
            it["_gorder"] = g?.Order ?? 999;
            it["_aorder"] = aorder;
            var sum = 0;
            if (it["stances"] is JsonArray st)
                foreach (var x in st)
                    if (x is JsonObject xo) sum += IntOf(xo["v"]);
            it["stanceSum"] = sum;
        }
        var sorted = items.Select((it, i) => (it, i)).ToList();
        sorted.Sort((a, b) =>
        {
            var x = IntOf(a.it["_aorder"]);
            var y = IntOf(b.it["_aorder"]);
            if (x != y) return x - y;
            x = IntOf(a.it["_gorder"]);
            y = IntOf(b.it["_gorder"]);
            if (x != y) return x - y;
            x = IntOf(a.it["prog"]);
            y = IntOf(b.it["prog"]);
            if (x != y) return x - y;
            var c = string.Compare(a.it["name"]?.ToString(), b.it["name"]?.ToString(), StringComparison.Ordinal);
            return c != 0 ? c : a.i - b.i;
        });
        items = sorted.Select(x => x.it).ToList();
        foreach (var it in items)
        {
            it.Remove("_gorder");
            it.Remove("_aorder");
        }

        // 互斥是双向的：脚本只写一边，界面两边都要看得到
        var rev = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var it in items)
        {
            if (it["disallow"] is not JsonArray arr) continue;
            var nm = it["name"]?.ToString() ?? "";
            foreach (var x in arr)
            {
                var k = x?.ToString() ?? "";
                if (!rev.TryGetValue(k, out var lst))
                {
                    lst = new List<string>();
                    rev[k] = lst;
                }
                if (!lst.Contains(nm)) lst.Add(nm);
            }
        }
        foreach (var it in items)
        {
            var nm = it["name"]?.ToString() ?? "";
            if (!rev.TryGetValue(nm, out var lst)) continue;
            var arr = it["disallow"] as JsonArray ?? new JsonArray();
            foreach (var x in lst)
                if (!arr.Any(y => y?.ToString() == x)) arr.Add(x);
            it["disallow"] = arr;
        }

        // 详情页的附加区块：前端按 kind 统一渲染（list / chips / tiers / stances）
        foreach (var it in items)
        {
            var secs = new JsonArray();
            var accFx = it["accFx"] as JsonArray;
            if (accFx is { Count: > 0 })
                secs.Add(new JsonObject
                {
                    ["label"] = "接受度等级规则", ["kind"] = "list", ["items"] = accFx.DeepClone(),
                    ["hint"] = "按人群的接受度等级（暴力敌视 / 文化抹除 / 公开偏见 / 二等公民 / 完全接纳）分别生效",
                });
            var instFx = it["instFx"] as JsonArray;
            if (instFx is { Count: > 0 })
                secs.Add(new JsonObject
                {
                    ["label"] = "机构效果", ["kind"] = "list", ["items"] = instFx.DeepClone(),
                });
            var taxTiers = it["taxTiers"] as JsonArray;
            if (taxTiers is { Count: > 0 })
                secs.Add(new JsonObject
                {
                    ["label"] = "税率档位", ["kind"] = "tiers", ["items"] = taxTiers.DeepClone(),
                    ["hint"] = "五个税率等级下，这条法律分别提供多少税收",
                });

            if (it["stances"] is JsonArray st && st.Count > 0)
            {
                var sgroups = new JsonArray();
                foreach (var sk in StanceOrder)
                {
                    var arr = st.OfType<JsonObject>()
                        .Where(x => x["label"]?.ToString() == StanceLabel[sk])
                        .OrderBy(x => x["name"]?.ToString(), StringComparer.Ordinal)
                        .ToList();
                    if (arr.Count == 0) continue;
                    var seen = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
                    var uniq = new List<JsonObject>();
                    foreach (var x in arr)
                    {
                        var nm = x["name"]?.ToString() ?? "";
                        if (seen.TryGetValue(nm, out var cur))
                        {
                            cur["dup"] = IntOf(cur["dup"]) + 1;
                            var srcs = cur["srcs"] as JsonArray;
                            var s2 = x["src"]?.ToString() ?? "";
                            if (srcs is not null && s2.Length > 0 && !srcs.Any(y => y?.ToString() == s2))
                                srcs.Add(s2);
                            continue;
                        }
                        var src = x["src"]?.ToString() ?? "";
                        var curObj = new JsonObject
                        {
                            ["name"] = nm, ["src"] = src,
                            ["inh"] = x["inherited"]?.GetValue<bool>() == true,
                            ["dup"] = 1,
                            ["srcs"] = src.Length > 0 ? new JsonArray { src } : new JsonArray(),
                        };
                        seen[nm] = curObj;
                        uniq.Add(curObj);
                    }
                    sgroups.Add(new JsonObject
                    {
                        ["key"] = sk,
                        ["label"] = StanceLabel[sk],
                        ["v"] = StanceV[sk],
                        ["items"] = ObjArr(uniq),
                        ["n"] = uniq.Count,
                        ["raw"] = arr.Count,
                    });
                }
                var sn = sgroups.Sum(g => IntOf(g?["n"]));
                secs.Add(new JsonObject
                {
                    ["label"] = "意识形态立场", ["kind"] = "stances", ["groups"] = sgroups, ["n"] = sn,
                    ["hint"] = "利益集团、政治运动与人物意识形态在立法表决时的态度；变体法律沿用父法的立场",
                });
            }

            void Chips(string label, string field, bool bad = false)
            {
                if (it[field] is not JsonArray a || a.Count == 0) return;
                var o = new JsonObject { ["label"] = label, ["kind"] = "chips", ["items"] = a.DeepClone() };
                if (bad) o["bad"] = true;
                secs.Add(o);
            }
            Chips("解锁科技", "techs");
            Chips("需要先采用其中一条", "reqOr");
            Chips("互斥法律", "disallow", true);
            Chips("采用后可解锁", "unlockLaws");

            if (it["cond"] is JsonArray cond && cond.Count > 0)
                secs.Add(new JsonObject
                {
                    ["label"] = "制定条件", ["kind"] = "list", ["items"] = cond.DeepClone(),
                });
            if (it["subLaws"] is JsonArray subs && subs.Count > 0)
            {
                var nm = it["name"]?.ToString() ?? "";
                secs.Add(new JsonObject
                {
                    ["label"] = "变体法律", ["kind"] = "chips",
                    ["items"] = StrArr(subs.OfType<JsonObject>()
                        .Select(v => (v["name"]?.ToString() ?? "") + "（" + nm + "）")),
                    ["hint"] = "由特定文化或政体替换本法律，效果与立场各自独立",
                });
            }
            it["secs"] = secs;
        }

        // 组清单（带条数），供前端筛选
        var cnt = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var it in items)
        {
            var k = it["catKey"]?.ToString() ?? "";
            cnt[k] = cnt.TryGetValue(k, out var c) ? c + 1 : 1;
        }
        var lawGroups = new List<JsonObject>();
        foreach (var g in groups)
        {
            if (!cnt.TryGetValue(g.Key, out var n) || n == 0) continue;
            lawGroups.Add(new JsonObject
            {
                ["key"] = g.Key,
                ["label"] = LocOrKey(loc, g.Key),
                ["desc"] = L(loc, g.Key + "_desc"),
                ["area"] = g.Area,
                ["areaLabel"] = AreaLabel.TryGetValue(g.Area, out var gal) ? gal : "",
                ["days"] = g.Days,
                ["ideoMult"] = g.IdeoMult,
                ["n"] = n,
            });
        }
        var lawAreas = new JsonArray();
        foreach (var a in AreaOrder)
        {
            if (!lawGroups.Any(g => g["area"]?.ToString() == a)) continue;
            lawAreas.Add(new JsonObject
            {
                ["key"] = a,
                ["label"] = AreaLabel[a],
                ["n"] = lawGroups.Count(g => g["area"]?.ToString() == a),
            });
        }

        // ---- 利益集团 ----
        var igs = ScanIgs(gdir, loc, DlcNames(gdir, loc));
        var owner = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var it in igs)
        {
            var ik = it["key"]?.ToString() ?? "";
            if (it["baseTraits"] is JsonArray bts)
                foreach (var t in bts.OfType<JsonObject>())
                {
                    var tk = t["key"]?.ToString() ?? "";
                    if (!owner.TryGetValue(tk, out var lst))
                    {
                        lst = new List<string>();
                        owner[tk] = lst;
                    }
                    if (!lst.Contains(ik)) lst.Add(ik);
                }
            if (it["exclusive"] is JsonArray exs)
                foreach (var e in exs.OfType<JsonObject>())
                    if (e["tkeys"] is JsonArray tks)
                        foreach (var tk0 in tks)
                        {
                            var tk = tk0?.ToString() ?? "";
                            if (!owner.TryGetValue(tk, out var lst))
                            {
                                lst = new List<string>();
                                owner[tk] = lst;
                            }
                            if (!lst.Contains(ik)) lst.Add(ik);
                        }
        }
        var igMeta = igs.ToDictionary(it => it["key"]!.ToString()!, StringComparer.Ordinal);
        var igOrder = igs.Select((it, i) => (it, i))
            .ToDictionary(x => x.it["key"]!.ToString()!, x => x.i, StringComparer.Ordinal);
        var igTraits = ScanIgTraits(Path.Combine(gdir, D_IGT), loc, owner, igMeta, igOrder);

        foreach (var it in igs)
        {
            var attrs = new List<JsonObject>
            {
                new() { ["label"] = "基础特质", ["value"] = ((it["baseTraits"] as JsonArray)?.Count ?? 0) + " 个" },
                new() { ["label"] = "默认意识形态", ["value"] = ((it["ideos"] as JsonArray)?.Count ?? 0) + " 个" },
            };
            var exCount = (it["exclusive"] as JsonArray)?.Count ?? 0;
            if (exCount > 0)
                attrs.Add(new JsonObject { ["label"] = "国家专属", ["value"] = exCount + " 组" });
            it["attrs"] = ObjArr(attrs);
            it["effects"] = new JsonArray();

            var secs = new JsonArray();
            if (it["ideos"] is JsonArray ideos && ideos.Count > 0)
                secs.Add(new JsonObject
                {
                    ["label"] = "默认意识形态", ["kind"] = "list",
                    ["items"] = StrArr(ideos.OfType<JsonObject>().Select(x =>
                    {
                        var nm = x["name"]?.ToString() ?? "";
                        var ds = x["desc"]?.ToString() ?? "";
                        return ds.Length > 0 ? nm + "：" + ds : nm;
                    })),
                    ["hint"] = "集团本身对各类法律的表决态度由这套意识形态决定",
                });
            if (it["charIdeos"] is JsonArray cis && cis.Count > 0)
                secs.Add(new JsonObject
                {
                    ["label"] = "人物意识形态", ["kind"] = "chips",
                    ["items"] = StrArr(cis.OfType<JsonObject>().Select(x => x["name"]?.ToString() ?? "")),
                    ["hint"] = "该集团的领袖角色可能带有的个人倾向",
                });
            if (it["baseTraits"] is JsonArray bts2 && bts2.Count > 0)
                secs.Add(new JsonObject
                {
                    ["label"] = "基础特质", ["kind"] = "chips",
                    ["items"] = StrArr(bts2.OfType<JsonObject>().Select(x => x["name"]?.ToString() ?? "")),
                });
            if (it["weights"] is JsonArray ws && ws.Count > 0)
                secs.Add(new JsonObject
                {
                    ["label"] = "政治力量权重", ["kind"] = "list",
                    ["items"] = StrArr(ws.OfType<JsonObject>().Select(w =>
                        (w["label"]?.ToString() ?? "") + "：" + (w["value"]?.ToString() ?? ""))),
                    ["hint"] = "该集团从各来源获得政治力量的基础倍率，实际值还会被法律与条件修正",
                });
            if (it["exclusive"] is JsonArray exs2 && exs2.Count > 0)
                secs.Add(new JsonObject
                {
                    ["label"] = "按国家获得的特质", ["kind"] = "list",
                    ["items"] = StrArr(exs2.OfType<JsonObject>().Select(e =>
                        (e["who"]?.ToString() ?? "") + " → " +
                        string.Join("、", (e["traits"] as JsonArray)?.Select(t => t?.ToString() ?? "") ?? Array.Empty<string>()))),
                    ["hint"] = "该集团在这些国家成立时会额外带上这些特质",
                });
            it["secs"] = secs;
        }

        // 特质页按归属集团分类
        var icnt = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var it in igTraits)
        {
            var k = it["catKey"]?.ToString() ?? "";
            icnt[k] = icnt.TryGetValue(k, out var c) ? c + 1 : 1;
        }
        var igList = new JsonArray();
        foreach (var it in igs)
        {
            var k = it["key"]?.ToString() ?? "";
            igList.Add(new JsonObject
            {
                ["key"] = k,
                ["label"] = it["name"]?.ToString() ?? "",
                ["color"] = it["color"]?.ToString() ?? "",
                ["n"] = icnt.TryGetValue(k, out var n) ? n : 0,
            });
        }

        var tabs = new JsonArray();
        foreach (var (id, icon, label, layout) in Tabs)
        {
            var count = id switch { "laws" => items.Count, "igs" => igs.Count, "igtraits" => igTraits.Count, _ => 0 };
            tabs.Add(new JsonObject
            {
                ["id"] = id, ["icon"] = icon, ["label"] = label, ["layout"] = layout, ["count"] = count,
            });
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["supported"] = true,
            ["game"] = "vic3",
            ["gameName"] = "Victoria 3",
            ["gameVer"] = GameVersion(root),
            ["gameRoot"] = root,
            ["uiLang"] = uiLang,
            ["tabs"] = tabs,
            ["laws"] = ObjArr(items),
            ["lawGroups"] = ObjArr(lawGroups),
            ["lawAreas"] = lawAreas,
            ["igs"] = ObjArr(igs),
            ["igTraits"] = ObjArr(igTraits),
            ["igList"] = igList,
            ["cached"] = false,
            ["buildSeconds"] = 0,
        };
    }

    // ------------------------------------------------------------ JSON 辅助

    private static int IntOf(JsonNode? n) =>
        int.TryParse(n?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static JsonArray StrArr(IEnumerable<string> items)
    {
        var a = new JsonArray();
        foreach (var s in items) a.Add(s);
        return a;
    }

    private static JsonArray ObjArr(IEnumerable<JsonObject> items)
    {
        var a = new JsonArray();
        foreach (var s in items) a.Add(s);
        return a;
    }
}

using System.Text;

namespace RenlianLauncher.Core;

/// <summary>Paradox 脚本（.mod / descriptor）极简解析：key=value、key={ a b }。</summary>
public static class Pdx
{
    public static Dictionary<string, object> ParseFile(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path, Encoding.UTF8));
        }
        catch
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static string? Str(Dictionary<string, object> d, string key) =>
        d.TryGetValue(key, out var v) && v is string s ? s : null;

    public static List<string> List(Dictionary<string, object> d, string key) =>
        d.TryGetValue(key, out var v) && v is List<string> l ? l : new List<string>();

    public static Dictionary<string, object> Parse(string text)
    {
        var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var tokens = Tokenize(text);
        var i = 0;
        while (i < tokens.Count)
        {
            var key = tokens[i];
            if (key is "{" or "}")
            {
                i++;
                continue;
            }
            if (i + 1 < tokens.Count && tokens[i + 1] == "=")
            {
                i += 2;
                if (i < tokens.Count && tokens[i] == "{")
                {
                    var list = new List<string>();
                    var depth = 1;
                    i++;
                    while (i < tokens.Count && depth > 0)
                    {
                        if (tokens[i] == "{") depth++;
                        else if (tokens[i] == "}") depth--;
                        else if (tokens[i] != "=") list.Add(tokens[i]);
                        i++;
                    }
                    map[key] = list;
                }
                else if (i < tokens.Count)
                {
                    map[key] = tokens[i];
                    i++;
                }
            }
            else
            {
                i++;
            }
        }
        return map;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var i = 0;
        var n = text.Length;
        while (i < n)
        {
            var c = text[i];
            if (c is ' ' or '\t' or '\r' or '\n') { i++; continue; }
            if (c == '#')
            {
                while (i < n && text[i] != '\n') i++;
                continue;
            }
            if (c is '{' or '}' or '=')
            {
                tokens.Add(c.ToString());
                i++;
                continue;
            }
            if (c == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < n && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < n) i++;
                    sb.Append(text[i]);
                    i++;
                }
                i++; // 跳过结尾引号
                tokens.Add(sb.ToString());
                continue;
            }
            var bare = new StringBuilder();
            while (i < n && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '=' or '#' or '"'))
            {
                bare.Append(text[i]);
                i++;
            }
            tokens.Add(bare.ToString());
        }
        return tokens;
    }
}

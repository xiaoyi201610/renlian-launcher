using System.Collections.Generic;

namespace RenlianLauncher.Core;

/// <summary>一款 P 社游戏的静态定义（与旧版 GAME_DEFS 保持一致）。</summary>
public sealed record GameDef(
    string Key,
    string Name,
    string En,
    string Folder,
    string Pdx,
    string AppId,
    string[] Exe,
    string SaveExt,
    string DefaultVersion,
    string UserBase = "Paradox Interactive");

public static class GameDefs
{
    public static readonly Dictionary<string, GameDef> All = new()
    {
        ["ck3"] = new("ck3", "十字军之王 III", "Crusader Kings III", "Crusader Kings III",
            "Crusader Kings III", "1158310", new[] { "binaries", "ck3.exe" }, ".ck3", "1.20"),
        ["stellaris"] = new("stellaris", "群星", "Stellaris", "Stellaris",
            "Stellaris", "281990", new[] { "stellaris.exe" }, ".sav", "3.14"),
        ["hoi4"] = new("hoi4", "钢铁雄心 IV", "Hearts of Iron IV", "Hearts of Iron IV",
            "Hearts of Iron IV", "394360", new[] { "hoi4.exe" }, ".hoi4", "1.15"),
        ["vic3"] = new("vic3", "维多利亚 3", "Victoria 3", "Victoria 3",
            "Victoria 3", "529340", new[] { "binaries", "victoria3.exe" }, ".v3", "1.8"),
        ["eu4"] = new("eu4", "欧陆风云 IV", "Europa Universalis IV", "Europa Universalis IV",
            "Europa Universalis IV", "236850", new[] { "eu4.exe" }, ".eu4", "1.37"),
        ["eu5"] = new("eu5", "欧陆风云 V", "Europa Universalis V", "Europa Universalis V",
            "Europa Universalis V", "3450310", new[] { "binaries", "eu5.exe" }, ".eu5", "1.0"),
        ["ck2"] = new("ck2", "十字军之王 II", "Crusader Kings II", "Crusader Kings II",
            "Crusader Kings II", "203770", new[] { "CK2game.exe" }, ".ck2", "3.3"),
        ["imperator"] = new("imperator", "帝国：罗马", "Imperator: Rome", "ImperatorRome",
            "Imperator", "859580", new[] { "binaries", "imperator.exe" }, ".rome", "2.0"),
        ["skylines"] = new("skylines", "城市：天际线", "Cities: Skylines", "Cities_Skylines",
            "Cities_Skylines", "255710", new[] { "Cities.exe" }, ".crp", "1.18", "Colossal Order"),
        ["skylines2"] = new("skylines2", "城市：天际线 II", "Cities: Skylines II", "Cities Skylines II",
            "Cities Skylines II", "949230", new[] { "Cities2.exe" }, ".cok", "1.0", "Colossal Order"),
    };

    public static readonly string[] Order =
    {
        "ck3", "stellaris", "hoi4", "vic3", "eu4", "eu5", "ck2", "imperator", "skylines", "skylines2"
    };

    /// <summary>使用 content_load.json 而不是 dlc_load.json 的游戏。</summary>
    public static readonly HashSet<string> ContentLoad = new() { "vic3", "eu5" };

    public static GameDef Get(string? key) =>
        key is not null && All.TryGetValue(key, out var d) ? d : All["ck3"];

    public static string Cover(string key, bool large = false)
    {
        var k = All.ContainsKey(key) ? key : "ck3";
        return $"/static/img/games/{(large ? k + ".jpg" : k + "-sm.jpg")}";
    }

    public static string ExePath(GameDef g, string gameRoot) =>
        Path.Combine(new[] { gameRoot }.Concat(g.Exe).ToArray());
}

// Read-only achievement metadata extractor for GameMaker .win files.
//
// Decompiles every script whose name contains "achievement", scans for
// `achievements_add(...)` calls (or other registration patterns), and emits one
// JSON line per achievement on stdout. WPR's GameMakerAchievementExtractor parses
// those lines and persists them into AchievementContext.
//
// This script does NOT modify the .win — it's safe to run on the user's install
// folder copy without any backup. Run with:
//   UndertaleModCli.exe load <game.win> -s wpr_extract_achievements.csx
//
// Output format (one JSON object per line, prefixed with WPR_GMACH:):
//   WPR_GMACH:{"key":"first20","scriptName":"first20","display":"first20","goal":20}

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UndertaleModLib;
using Underanalyzer.Decompiler;

EnsureDataLoaded();

GlobalDecompileContext gdc = new(Data);
var settings = Data.ToolInfo.DecompilerSettings;

// Briquid Mini-style: achievements_add(googlePlayCode, key, codeIos, ackUrl, loEntry, icon, goal, scriptId, ...).
// We only need the human-meaningful fields; the rest are platform-specific identifiers.
var addPattern = new Regex(
    @"achievements_add\(\s*""([^""]*)""\s*,\s*""([^""]*)""\s*,\s*""([^""]*)""\s*,\s*""([^""]*)""\s*,\s*""([^""]*)""\s*,\s*(\d+)\s*,\s*(\d+)\s*",
    RegexOptions.Singleline);

int found = 0;
foreach (var code in Data.Code.Where(c =>
    c.Name.Content.Contains("achievement", StringComparison.OrdinalIgnoreCase)))
{
    string src;
    try { src = new DecompileContext(gdc, code, settings).DecompileToString(); }
    catch { continue; }

    foreach (Match m in addPattern.Matches(src))
    {
        string androidCode = m.Groups[1].Value;
        string key         = m.Groups[2].Value;
        string display     = m.Groups[5].Value;  // lo_entry — localization key, often human-friendly
        string iconStr     = m.Groups[6].Value;
        string goalStr     = m.Groups[7].Value;
        int.TryParse(iconStr, out int icon);
        int.TryParse(goalStr, out int goal);

        // Single-line JSON so the consumer can just read line-by-line.
        // No external JSON dep — quote escape any quotes/backslashes ourselves.
        string j = "{"
            + "\"key\":" + JsonStr(key) + ","
            + "\"androidCode\":" + JsonStr(androidCode) + ","
            + "\"display\":" + JsonStr(display) + ","
            + "\"icon\":" + icon + ","
            + "\"goal\":" + goal
            + "}";
        Console.WriteLine("WPR_GMACH:" + j);
        found++;
    }
}

Console.WriteLine($"WPR_GMACH_DONE:{found}");

static string JsonStr(string s)
{
    var sb = new System.Text.StringBuilder("\"");
    foreach (char c in s)
    {
        switch (c)
        {
            case '"':  sb.Append("\\\""); break;
            case '\\': sb.Append("\\\\"); break;
            case '\n': sb.Append("\\n"); break;
            case '\r': sb.Append("\\r"); break;
            case '\t': sb.Append("\\t"); break;
            default:
                if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                else sb.Append(c);
                break;
        }
    }
    sb.Append('"');
    return sb.ToString();
}

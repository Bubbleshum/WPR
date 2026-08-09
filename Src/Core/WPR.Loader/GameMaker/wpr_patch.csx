// WPR achievement bridge patcher.
//
// Mutates a GameMaker .win so the running game (under YoYo's Runner.exe) emits
// WPR_ACH:* lines on stdout when achievements are registered / unlocked. WPR's
// Runner stdout listener parses those lines and pipes them into AchievementContext
// so they show up in the WPR UI like XNA achievements.
//
// Mutations:
// 1. gml_Script_achievements_add — prepend defensive instance-create so the
//    achievements singleton exists when god's PreCreate fires (fixes the FATAL
//    "Unable to find any instance for object index '0' name 'achievements'"),
//    then append a WPR_ACH:register:<key> log line.
// 2. gml_Script_achievements_recompute_single — capture the prior reached
//    value, run the original body, emit WPR_ACH:unlock:<key> on the transition.

using System;
using System.Linq;
using UndertaleModLib;
using UndertaleModLib.Compiler;
using Underanalyzer.Decompiler;

EnsureDataLoaded();

GlobalDecompileContext gdc = new(Data);
var settings = Data.ToolInfo.DecompilerSettings;

string Decompile(string name)
{
    var c = Data.Code.FirstOrDefault(x => x.Name.Content == name)
        ?? throw new Exception($"Script not found: {name}");
    return new DecompileContext(gdc, c, settings).DecompileToString();
}

string addOrig = Decompile("gml_Script_achievements_add");
string recompOrig = Decompile("gml_Script_achievements_recompute_single");

// Patch 1: achievements_add — make defensive against missing instance.
//
// On real WP, this is always called from inside achievements_define's
// `with(achievements){...}` block, after the instance has been created and
// `count = 0` set. On desktop Runner, something fires achievements_add too
// early (before achievements_define runs). Rather than try to bootstrap the
// instance from this scope (which doesn't work cleanly because variable
// resolution happens in the caller's scope, not the achievements scope),
// just early-return when the instance is missing. The legitimate calls from
// achievements_define still register all 23 achievements correctly.
//
// `argument[1]` is the achievement key (e.g. "destroyjustbuilt").
// Don't touch argument[*] in the early-return — some callers invoke this script
// with zero args (likely script_execute round-tripping through reordered indices),
// and arg access would FATAL even before count++ does.
string addPatched = @"
if (!instance_exists(achievements))
{
    show_debug_message(""WPR_ACH:premature"");
    exit;
}
" + addOrig + @"
show_debug_message(""WPR_ACH:register:"" + string(argument[1]));
";

// Patch 2: achievements_recompute_single — log the moment a `reached` flag flips.
string recompPatched = @"
var __wpr_prev_reached = achievements.reached[argument0];
" + recompOrig + @"
if (achievements.reached[argument0] && !__wpr_prev_reached)
{
    show_debug_message(""WPR_ACH:unlock:"" + string(achievements.key[argument0]));
}
";

CodeImportGroup importGroup = new(Data);
importGroup.QueueReplace("gml_Script_achievements_add", addPatched);
importGroup.QueueReplace("gml_Script_achievements_recompute_single", recompPatched);
importGroup.Import();

Console.WriteLine("WPR patch applied: achievements_add + achievements_recompute_single");

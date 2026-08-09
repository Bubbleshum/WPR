// Surgical bytecode-only neutralizer for GameMaker scripts that crash before
// achievements_define has run.
//
// Unlike wpr_patch.csx, this does NOT invoke the GML compiler (Underanalyzer).
// It only mutates the Instructions list of one specific script, replacing the
// entire body with a single Exit opcode. That's the lowest-risk save we can do
// through UMT — no new variable bindings, no scope rewrites, no other scripts
// touched. The hope is that older runtimes (e.g. Runner 2.1.4.200, the only
// one that can actually load 2018-era .win files) will accept the round-trip
// because nothing about variable resolution changes.
//
// Why this is safe: the achievement *catalog* is already populated at install
// time by wpr_extract_achievements.csx (read-only decompile). The in-game
// achievements_add registration becomes a no-op, which is fine because we
// don't have a reliable in-game unlock signal for this game anyway. WPR's UI
// shows achievements from the install-time catalog regardless of whether the
// running game registers them with itself.
//
// Output: WPR_NEUTRALIZE: lines on stdout, plus the script's standard
// "WPR patch applied" line that GameMakerWinPatcher checks for.

using System;
using System.Linq;
using UndertaleModLib;
using UndertaleModLib.Models;

EnsureDataLoaded();

string[] targets = { "gml_Script_achievements_add" };

int neutralized = 0;
foreach (var name in targets)
{
    var code = Data.Code.FirstOrDefault(c => c.Name?.Content == name);
    if (code == null)
    {
        Console.WriteLine($"WPR_NEUTRALIZE: script not found: {name}");
        continue;
    }

    code.Instructions.Clear();
    code.Instructions.Add(new UndertaleInstruction()
    {
        Kind = UndertaleInstruction.Opcode.Exit,
        Type1 = UndertaleInstruction.DataType.Int32,
    });
    code.UpdateLength();

    neutralized++;
    Console.WriteLine($"WPR_NEUTRALIZE: {name} replaced with single Exit");
}

Console.WriteLine($"WPR patch applied: neutralized {neutralized} script(s)");

using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// What Jetpack Joyride and Fruit Ninja 2013 (Mandreel, a <c>DrawingSurface</c> on a landscape
/// page, drawn through ANGLE) needed from the runtime beneath them: a batch shader whose constant
/// palette is read relatively and its last register directly, the folders a WP8 install's local
/// folder starts with, and a surface measured and shown in the page's own orientation.
/// </summary>
public sealed class MandreelSurfaceSuite : IContractSuite
{
    public string Name => "mandreel surface";

    public void Run(Harness h)
    {
        ConstantArrays(h);
        SharedFolders(h);
        LandscapeShells(h);
    }

    // Shader model 2 tokens: register type in bits 28-30 (and 11-12), number in 0-10, swizzle or
    // write mask in 16-23, relative addressing in bit 13 (followed by the address register token).
    private const uint V0 = 0x90E40000, R0Src = 0x80E40000, R0Dst = 0x800F0000, A0Dst = 0xB00F0000, OPos = 0xC00F0000, A0X = 0xB0000000;

    private static uint Const(int register, bool relative = false) => 0xA0E40000u | (relative ? 0x2000u : 0) | (uint)register;

    private static byte[] Program(params uint[] body)
    {
        uint[] tokens = [0xFFFE0200, .. body, 0x0000FFFF];
        return tokens.SelectMany(BitConverter.GetBytes).ToArray();
    }

    private static void ConstantArrays(Harness h)
    {
        // Jetpack Joyride's sprite batcher: c2[a0.x] per sprite, c0/c1 and c122 read directly, all
        // in one constant buffer (c0-c122).
        byte[] batcher = Program(
            0x0200002E, A0Dst, V0,                              // mova a0, v0
            0x04000005, R0Dst, V0, Const(2, relative: true), A0X, // mul r0, v0, c2[a0.x]
            0x03000002, R0Dst, R0Src, Const(122),               // add r0, r0, c122
            0x03000002, R0Dst, R0Src, Const(0),                 // add r0, r0, c0
            0x03000002, R0Dst, R0Src, Const(1),                 // add r0, r0, c1
            0x02000001, OPos, R0Src);                           // mov oPos, r0

        var (relative, direct) = Aon9Shader.ConstantReads(batcher);
        h.Check("a relative read is told from a direct one", relative.SetEquals([2]) && direct.SetEquals([0, 1, 122]),
            $"relative [{string.Join(",", relative)}] direct [{string.Join(",", direct)}]");

        List<Aon9ConstantArray> arrays = Aon9Shader.ConstantArraysFor(batcher, isPixel: false, registers: 123, [new Aon9ConstantMap(0, 0, 123, 0)]);
        string shape = string.Join(" ", arrays.Select(a => $"{a.Name}[{a.Start}+{a.Count}]"));
        h.Check("the indexed run ends before a register above it that is read directly (MojoShader's HLSL cannot read one inside it)",
            shape == "vs_c[0+2] vs_c1[2+120] vs_c2[122+1]", shape);

        // Skinning with nothing read above the palette: the run still reaches the end of its map.
        byte[] skinned = Program(
            0x0200002E, A0Dst, V0,
            0x04000005, R0Dst, V0, Const(33, relative: true), A0X,
            0x03000002, R0Dst, R0Src, Const(4),
            0x02000001, OPos, R0Src);
        arrays = Aon9Shader.ConstantArraysFor(skinned, isPixel: false, registers: 120, [new Aon9ConstantMap(0, 0, 120, 0)]);
        shape = string.Join(" ", arrays.Select(a => $"{a.Name}[{a.Start}+{a.Count}]"));
        h.Check("... and with no direct read above it, the run reaches the end of its constant buffer", shape == "vs_c[0+33] vs_c1[33+87]", shape);
    }

    private static void SharedFolders(Harness h)
    {
        _ = h.S.Files;
        foreach (string folder in FileLibrary.SharedFolders)
        {
            h.Check($"the local folder starts with {folder}, as a phone installs it", Directory.Exists(Path.Combine(h.Sandbox, folder)));
        }

        // Mandreel writes every save there without creating the folder first.
        string path = h.E.WinRt.LocalFolderPath + @"\/shared/ShellContent/offlineProfile_MainSaveData.dat";
        long file = h.Call("fopen", h.Narrow(path), h.Narrow("wb"));
        h.Check("a save opened under Local/shared/ShellContent is created", file != 0, $"errno {h.Errno()}");
        if (file != 0)
        {
            h.Call("fclose", file);
        }

        h.Check("... and lands in the sandbox's Shared/ShellContent",
            File.Exists(Path.Combine(h.Sandbox, "Shared", "ShellContent", "offlineProfile_MainSaveData.dat")));
    }

    /// <summary>The installed Mandreel titles' inferred pages (the real data root, or WPR_DATA_ROOT's).</summary>
    private static void LandscapeShells(Harness h)
    {
        string local = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA")!, "WPR");
        List<string> folders = new[] { Environment.GetEnvironmentVariable("WPR_DATA_ROOT"), local }
            .Where(root => !string.IsNullOrEmpty(root) && Directory.Exists(Path.Combine(root!, "AppData")))
            .SelectMany(root => Directory.GetDirectories(Path.Combine(root!, "AppData")))
            .Where(folder => File.Exists(Path.Combine(folder, "__MandreelWP8Direct3DInterop.winmd")))
            .ToList();
        if (folders.Count == 0)
        {
            Console.WriteLine("  (skipped Mandreel pages: none installed)");
            return;
        }

        foreach (string folder in folders)
        {
            XamlShell? shell = XamlShellInference.Infer(Path.Combine(folder, "PhoneDLL1.dll"), WinmdReader.Load(Directory.GetFiles(folder, "*.winmd")));
            h.Check($"{Path.GetFileName(folder)}: a DrawingSurface on a landscape page is shown as drawn, not turned",
                shell is { DrawingSurface: true, Landscape: true, PresentsUnturned: true },
                shell is null ? "no shell" : $"DrawingSurface={shell.DrawingSurface} Landscape={shell.Landscape}");
        }
    }
}

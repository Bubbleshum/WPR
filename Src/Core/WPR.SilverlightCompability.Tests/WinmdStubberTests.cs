using System;
using System.IO;
using Mono.Cecil;
using Xunit;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// Readying an install twice (the managed-page stamp was bumped, or the marker was lost) must
/// keep the component's native DLL. The second pass used to move its own earlier stub over the
/// <c>.native_original</c>, the only copy of the native component, and the title then failed at
/// load with "Expected an ARMNT image (0x01C4), got 0x014C" (Assassin's Creed Pirates).
/// </summary>
public class WinmdStubberTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "wpr-stubber-" + Guid.NewGuid().ToString("N"));

    public WinmdStubberTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Writes <c>name.winmd</c>: metadata with one public class, as a component's .winmd is.</summary>
    private void WriteWinmd(string name)
    {
        using AssemblyDefinition asm = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition(name, new Version(1, 0, 0, 0)), name, ModuleKind.Dll);
        asm.MainModule.Types.Add(new TypeDefinition(name, "Component",
            TypeAttributes.Public | TypeAttributes.Class, asm.MainModule.TypeSystem.Object));
        asm.Write(Path.Combine(_folder, name + ".winmd"));
    }

    [Fact]
    public void A_second_preparation_keeps_the_native_component()
    {
        WriteWinmd("SparkApp");
        byte[] native = [0x4D, 0x5A, 0xA1, 0xB2, 0xC3, 0xD4];
        File.WriteAllBytes(Path.Combine(_folder, "SparkApp.dll"), native);

        WPR.WinmdStubber.StubInPlace(_folder, bridged: true);
        WPR.WinmdStubber.StubInPlace(_folder, bridged: true);

        Assert.Equal(native, File.ReadAllBytes(Path.Combine(_folder, "SparkApp.dll.native_original")));
        Assert.NotEqual(native, File.ReadAllBytes(Path.Combine(_folder, "SparkApp.dll")));
    }

    [Fact]
    public void A_component_with_no_native_dll_of_its_name_gets_no_backup()
    {
        // Alex's metadata is FusionComponent.winmd; its native code is AlexXamlComponent.dll.
        WriteWinmd("FusionComponent");

        WPR.WinmdStubber.StubInPlace(_folder, bridged: true);
        WPR.WinmdStubber.StubInPlace(_folder, bridged: true);

        Assert.True(File.Exists(Path.Combine(_folder, "FusionComponent.dll")));
        Assert.False(File.Exists(Path.Combine(_folder, "FusionComponent.dll.native_original")));
    }
}

using System;
using System.Reflection;
using System.Reflection.Emit;
using WPR.WindowsCompability;
using WPR.Xna.Compat;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// The installer renames a game assembly whose name has punctuation in it ("Zuma's Revenge!" is
/// installed as "Zuma_s Revenge_"), but names in the game's content and data keep the original.
/// </summary>
public class InstalledAssemblyNamesTests
{
    [Fact]
    public void ZumasReaderName_IsRewrittenToTheInstalledAssembly()
    {
        // Verbatim from Zuma's Revenge! Content/levels/levels.xnb.
        Assert.Equal("ZumasRevenge.LevelsXmlReader, Zuma_s Revenge_",
            InstalledAssemblyNames.StandardiseTypeName("ZumasRevenge.LevelsXmlReader, Zuma's Revenge!"));
    }

    [Fact]
    public void OnlySimpleNamesChange_NotTypesOrVersionFields()
    {
        string name =
            "System.Collections.Generic.List`1[[Game.It's, Bob's Game!, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null]]," +
            " mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";
        Assert.Equal(
            "System.Collections.Generic.List`1[[Game.It's, Bob_s Game_, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null]]," +
            " mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089",
            InstalledAssemblyNames.StandardiseTypeName(name));
    }

    [Fact]
    public void EveryGenericArgumentAndTheOuterAssemblyAreRewritten()
    {
        Assert.Equal("Outer`2[[Inner`1[[X, A_b]], C_d],[Y, E_f]], G_h",
            InstalledAssemblyNames.StandardiseTypeName("Outer`2[[Inner`1[[X, A'b]], C!d],[Y, E@f]], G#h"));
    }

    [Theory]
    [InlineData("Microsoft.Xna.Framework.Content.Texture2DReader, Microsoft.Xna.Framework.Graphics, Version=4.0.0.0, Culture=neutral, PublicKeyToken=842cf8be1de50553")]
    [InlineData("Game.Reader")]
    [InlineData("Game.Odd'Name")]
    [InlineData("Game.Grid[,], Game")]
    public void NamesWithNothingToRename_AreUnchanged(string name)
    {
        Assert.Equal(name, InstalledAssemblyNames.StandardiseTypeName(name));
    }

    [Fact]
    public void Type2_ResolvesATypeByItsAssemblysOriginalName()
    {
        // Stand-in for the installed game: an assembly carrying the standardised name.
        var builder = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("Wpr_s Renamed Test_"), AssemblyBuilderAccess.Run);
        Type defined = builder.DefineDynamicModule("m").DefineType("RenamedTest.Thing", TypeAttributes.Public).CreateType();

        Assert.Same(defined, Type2.GetType("RenamedTest.Thing, Wpr's Renamed Test!", true));
    }
}

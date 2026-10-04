using System;
using System.Collections.Generic;
using WPR.WindowsCompability;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// Type2.GetType is what a game's Type.GetType(string, bool) is patched to call. The names here
/// are the shapes WP7 serialisers write: assembly-qualified, WP7 assembly versions, and generic
/// arguments carrying their own assembly-qualified names.
/// </summary>
public class Type2Tests
{
    public sealed class GameType { }

    private const string Xna = "Microsoft.Xna.Framework, Version=4.0.0.0, Culture=neutral, PublicKeyToken=842cf8be1de50553";
    private const string Corlib4 = "mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";
    private const string Corlib2 = "mscorlib, Version=2.0.5.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e";

    [Fact]
    public void GenericWithAnXnaArgument_ResolvesToTheWprXnaType()
    {
        // Verbatim from Skulls of the Shogun's Content/Maps/Overworld Map/Overworld_Map.apt.
        // The comma-splitting parser this replaced turned it into "…Vector2, FNA, mscorlib".
        Type? t = Type2.GetType($"System.Collections.Generic.List`1[[Microsoft.Xna.Framework.Vector2, {Xna}]], {Corlib4}", true);
        Assert.Equal(typeof(List<Microsoft.Xna.Framework.Vector2>), t);
    }

    [Fact]
    public void XnaType_ResolvesToWprFrameworkXna_NotFna()
    {
        Assert.Equal(typeof(Microsoft.Xna.Framework.Vector2), Type2.GetType($"Microsoft.Xna.Framework.Vector2, {Xna}", true));
        Assert.Equal(typeof(Microsoft.Xna.Framework.Color),
            Type2.GetType("Microsoft.Xna.Framework.Color, Microsoft.Xna.Framework.Graphics, Version=4.0.0.0, Culture=neutral, PublicKeyToken=842cf8be1de50553", true));
    }

    [Fact]
    public void Wp7CorlibVersion_Resolves()
    {
        Assert.Equal(typeof(int), Type2.GetType($"System.Int32, {Corlib2}", true));
    }

    [Fact]
    public void TwoQualifiedArguments_IncludingTheCallersOwnType()
    {
        string own = typeof(GameType).AssemblyQualifiedName!;
        Type? t = Type2.GetType($"System.Collections.Generic.Dictionary`2[[System.String, {Corlib2}],[{own}]], {Corlib4}", true);
        Assert.Equal(typeof(Dictionary<string, GameType>), t);
    }

    [Fact]
    public void UnqualifiedName_StillResolvesAgainstTheCaller()
    {
        Assert.Equal(typeof(GameType), Type2.GetType(typeof(GameType).FullName!, false));
    }

    [Fact]
    public void UnknownType_WithoutThrow_IsNull()
    {
        Assert.Null(Type2.GetType($"No.Such.Type, {Corlib4}", false));
        Assert.Null(Type2.GetType("System.Collections.Generic.List`1[[, ]], ", false));
    }
}

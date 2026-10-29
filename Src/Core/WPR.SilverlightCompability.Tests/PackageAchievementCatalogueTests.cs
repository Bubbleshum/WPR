using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using WPR.Achievements;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>Achievement catalogues generated from a package (WPR.Loader/Achievements).</summary>
public sealed class PackageAchievementCatalogueTests
{
    /// <summary>A package held in memory.</summary>
    private sealed class MemoryPackage : IPackageFiles
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
        public MemoryPackage Add(string path, byte[] bytes) { _files[path] = bytes; return this; }
        public IReadOnlyList<string> Paths => _files.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        public long Length(string path) => _files.TryGetValue(path, out byte[]? b) ? b.Length : 0;
        public byte[] ReadAll(string path) => _files[path];
        public void Dispose() { }
    }

    // ---- XLAST ----

    private static byte[] Xlast(string[] strings, (int Index, uint Id)[] pairs, bool magic = true, byte[]? altRecords = null)
    {
        using MemoryStream m = new();
        using BinaryWriter w = new(m, Encoding.UTF8);
        if (magic) { w.Write(-1); w.Write(1); }
        w.Write(strings.Length);
        foreach (string s in strings) w.Write(s);
        if (altRecords is null) w.Write(0);
        else w.Write(altRecords);
        w.Write(pairs.Length);
        foreach ((int i, uint id) in pairs) { w.Write(i); w.Write(id); }
        w.Write(new byte[24]);
        return m.ToArray();
    }

    [Fact]
    public void Xlast_takes_the_pair_table_not_the_whole_string_table()
    {
        // Achievements then leaderboards share the string table; two pairs select the achievements.
        byte[] data = Xlast(new[] { "First Step ", "Winner", "BEST_SCORE" }, new[] { (0, 7u), (1, 9u) });
        IReadOnlyList<XlastAchievement>? parsed = XlastAchievementReader.Parse(data);
        Assert.NotNull(parsed);
        Assert.Equal(new[] { ("First Step ", 7u), ("Winner", 9u) }, parsed!.Select(a => (a.Key, a.XboxId)));
    }

    [Fact]
    public void Xlast_without_magic_and_with_fixed_records_in_the_alt_section_still_parses()
    {
        // WP8-era SDKs put fixed-size records where the alternative strings would be.
        byte[] records = new byte[] { 2, 0, 0, 0, 0xAA, 0xBB, 0xCC, 0xDD, 0x10, 0x20, 0x30, 0x40 };
        byte[] data = Xlast(new[] { "a1", "a2", "LB" }, new[] { (0, 21u), (1, 22u) }, magic: false, altRecords: records);
        IReadOnlyList<XlastAchievement>? parsed = XlastAchievementReader.Parse(data);
        Assert.Equal(new[] { 21u, 22u }, parsed!.Select(a => a.XboxId));
    }

    [Fact]
    public void Xlast_garbage_is_null()
    {
        Assert.Null(XlastAchievementReader.Parse(new byte[] { 1, 2, 3 }));
        Assert.Null(XlastAchievementReader.Parse(Encoding.ASCII.GetBytes("not an xlast configuration at all")));
    }

    // ---- assemblies built for the test ----

    /// <summary>
    /// A game assembly: a key array and a parallel icon-name array in <c>Game.Data</c>'s static
    /// constructor, an <c>Achievement</c> object holding a key copied from the array, and an award
    /// wrapper. <paramref name="callSignedInGamer"/> makes the wrapper call XNA's SignedInGamer;
    /// otherwise it is a stub, as in a build with Xbox LIVE stripped.
    /// </summary>
    private static byte[] GameAssembly(string[] keys, string[] icons, bool callSignedInGamer, byte[]? xlast = null)
    {
        AssemblyDefinition asm = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Game", new Version(1, 0)), "Game.dll", ModuleKind.Dll);
        ModuleDefinition module = asm.MainModule;
        TypeReference str = module.TypeSystem.String;
        TypeReference voidT = module.TypeSystem.Void;

        TypeDefinition data = new("Game", "Data", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
        module.Types.Add(data);
        FieldDefinition keyField = new("KEYS", FieldAttributes.Public | FieldAttributes.Static, new ArrayType(str));
        FieldDefinition iconField = new("ICONS", FieldAttributes.Public | FieldAttributes.Static, new ArrayType(str));
        data.Fields.Add(keyField);
        data.Fields.Add(iconField);
        MethodDefinition cctor = new(".cctor", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName | MethodAttributes.HideBySig, voidT);
        data.Methods.Add(cctor);
        ILProcessor il = cctor.Body.GetILProcessor();
        foreach ((FieldDefinition field, string[] values) in new[] { (keyField, keys), (iconField, icons) })
        {
            il.Emit(OpCodes.Ldc_I4, values.Length);
            il.Emit(OpCodes.Newarr, str);
            for (int i = 0; i < values.Length; i++)
            {
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldstr, values[i]);
                il.Emit(OpCodes.Stelem_Ref);
            }
            il.Emit(OpCodes.Stsfld, field);
        }
        il.Emit(OpCodes.Ret);

        // class Achievement { public string m_Key; }
        TypeDefinition achievement = new("Game", "Achievement", TypeAttributes.Public, module.TypeSystem.Object);
        module.Types.Add(achievement);
        FieldDefinition keyOf = new("m_Key", FieldAttributes.Public, str);
        achievement.Fields.Add(keyOf);

        // static void AwardAchievement(string key) { [SignedInGamer.AwardAchievement(key)] }
        TypeDefinition live = new("Game", "Live", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
        module.Types.Add(live);
        MethodDefinition award = new("AwardAchievement", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, voidT);
        award.Parameters.Add(new ParameterDefinition("key", ParameterAttributes.None, str));
        live.Methods.Add(award);
        il = award.Body.GetILProcessor();
        if (callSignedInGamer)
        {
            AssemblyNameReference gamerServices = new("Microsoft.Xna.Framework.GamerServices", new Version(4, 0, 0, 0));
            module.AssemblyReferences.Add(gamerServices);
            TypeReference gamer = new("Microsoft.Xna.Framework.GamerServices", "SignedInGamer", module, gamerServices);
            MethodReference real = new("AwardAchievement", voidT, gamer) { HasThis = true };
            real.Parameters.Add(new ParameterDefinition(str));
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Callvirt, real);
        }
        il.Emit(OpCodes.Ret);

        // static void Unlock(Achievement a, int i) { a.m_Key = Data.KEYS[i]; AwardAchievement(a.m_Key); AwardAchievement("DIRECT"); }
        MethodDefinition unlock = new("Unlock", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, voidT);
        unlock.Parameters.Add(new ParameterDefinition("a", ParameterAttributes.None, achievement));
        unlock.Parameters.Add(new ParameterDefinition("i", ParameterAttributes.None, module.TypeSystem.Int32));
        live.Methods.Add(unlock);
        il = unlock.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldsfld, keyField);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Stfld, keyOf);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keyOf);
        il.Emit(OpCodes.Call, award);
        il.Emit(OpCodes.Ldstr, "DIRECT_KEY");
        il.Emit(OpCodes.Call, award);
        il.Emit(OpCodes.Ret);

        if (xlast is not null) module.Resources.Add(new EmbeddedResource(XlastAchievementReader.ResourceName, ManifestResourceAttributes.Public, xlast));

        using MemoryStream m = new();
        asm.Write(m);
        return m.ToArray();
    }

    private static byte[] Png(int size = 64)
    {
        byte[] rgba = new byte[size * size * 4];
        Array.Fill(rgba, (byte)200);
        return WPR.Achievements.Png.Encode(rgba, size, size);
    }

    [Fact]
    public void Il_fallback_finds_keys_through_an_array_a_field_and_a_stub_wrapper()
    {
        byte[] game = GameAssembly(new[] { "ACH_RUN", "ACH_JUMP" }, new[] { "icon_run", "icon_jump" }, callSignedInGamer: false);
        using MemoryPackage package = new MemoryPackage()
            .Add("Game.dll", game)
            .Add("Content/res/icon_jump_open.png", Png())
            .Add("Content/res/icon_jump_lock.png", Png(32));

        GeneratedCatalogue catalogue = PackageAchievementCatalogue.Build(package);

        Assert.Equal("il", catalogue.KeySource);
        Assert.Equal(new[] { "ACH_RUN", "ACH_JUMP", "DIRECT_KEY" }, catalogue.Achievements.Select(a => a.Key));
        GeneratedAchievement jump = catalogue.Achievements[1];
        Assert.True(jump.NameFromKey);
        Assert.Equal("Jump", jump.Name);
        // The icon named in the parallel array, the unlocked variant.
        Assert.NotNull(jump.Icon);
        Assert.Equal(64, BinaryPrimitives.ReadInt32BigEndian(jump.Icon.AsSpan(16)));
        Assert.Null(catalogue.Achievements[0].Icon);
    }

    [Fact]
    public void A_package_with_no_award_calls_has_no_catalogue()
    {
        AssemblyDefinition asm = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Plain", new Version(1, 0)), "Plain.dll", ModuleKind.Dll);
        using MemoryStream m = new();
        asm.Write(m);
        using MemoryPackage package = new MemoryPackage().Add("Plain.dll", m.ToArray()).Add("native.dll", new byte[] { 0x4D, 0x5A, 1, 2 });
        Assert.Same(GeneratedCatalogue.Empty, PackageAchievementCatalogue.Build(package));
    }

    // ---- Xbox SPA ----

    /// <summary>An XDBF with one PNG image (namespace 2) followed by the gzipped UTF-16 gameconfig.</summary>
    private static byte[] Spa(string gameconfig, uint imageId, byte[] image)
    {
        using MemoryStream m = new();
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); m.Write(b); }
        m.Write(new byte[] { 0x4D, 0x5A, 0, 0 }); // a module's header first
        m.Write("XDBF"u8);
        U32(0x10000); U32(1); U32(1); U32(1); U32(0);
        Span<byte> entry = stackalloc byte[18];
        BinaryPrimitives.WriteUInt16BigEndian(entry, 2);
        BinaryPrimitives.WriteUInt64BigEndian(entry[2..], imageId);
        BinaryPrimitives.WriteUInt32BigEndian(entry[10..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(entry[14..], (uint)image.Length);
        m.Write(entry);
        m.Write(new byte[8]); // the free table
        m.Write(image);
        using (GZipStream gz = new(m, CompressionLevel.Fastest, leaveOpen: true))
        {
            gz.Write(new byte[] { 0xFF, 0xFE });
            gz.Write(Encoding.Unicode.GetBytes(gameconfig));
        }
        m.Write(new byte[16]);
        return m.ToArray();
    }

    private const string GameConfig = """
        <XboxLiveSubmissionProject><GameConfigProject titleId="0x12345678">
          <Achievements>
            <Achievement id="7" friendlyName="GET_BLUE_BIRD" titleStringId="13" descriptionStringId="14" unachievedStringId="12" showUnachieved="false" cred="15" imageId="3" />
            <Achievement id="9" friendlyName="WIN" titleStringId="20" descriptionStringId="21" cred="10" imageId="4" />
          </Achievements>
          <LocalizedStrings defaultLocale="en-US">
            <LocalizedString id="32768" friendlyName="X_STRINGID_TITLENAME"><Translation locale="en-US">Test Game</Translation></LocalizedString>
            <LocalizedString id="12"><Translation locale="fr-FR">Débloquer</Translation><Translation locale="en-US">Unlock Blue bird</Translation></LocalizedString>
            <LocalizedString id="13"><Translation locale="en-US">Split It!</Translation></LocalizedString>
            <LocalizedString id="14"><Translation locale="en-US">You unlocked Blue bird</Translation></LocalizedString>
            <LocalizedString id="20"><Translation locale="en-US">Winner</Translation></LocalizedString>
          </LocalizedStrings>
        </GameConfigProject></XboxLiveSubmissionProject>
        """;

    [Fact]
    public void Spa_gives_ids_names_points_secrecy_and_images()
    {
        byte[] icon = Png();
        using MemoryPackage package = new MemoryPackage().Add("xbl.dll", Spa(GameConfig, 3, icon));

        XboxSpa? spa = XboxSpaAchievementReader.Find(package);
        Assert.NotNull(spa);
        Assert.Equal("Test Game", spa!.TitleName);

        GeneratedCatalogue catalogue = PackageAchievementCatalogue.Build(package);
        Assert.Equal("spa", catalogue.KeySource);
        GeneratedAchievement first = catalogue.Achievements[0];
        Assert.Equal(("7", "Split It!", "Unlock Blue bird", 15, true, false), (first.Key, first.Name, first.Description, first.GamerScore, first.Secret, first.NameFromKey));
        Assert.Equal(icon, first.Icon);
        GeneratedAchievement second = catalogue.Achievements[1];
        Assert.Equal(("9", "Winner", 10, false), (second.Key, second.Name, second.GamerScore, second.Secret));
        Assert.Null(second.Icon); // image 4 is not in the table
    }

    [Fact]
    public void Xlast_keys_of_a_GamerServices_game_take_their_text_from_the_spa_by_id_and_padding_from_the_il()
    {
        // XLAST pads "ACH_RUN" to "ACH_RUN "; the game's own constant is trimmed.
        byte[] xlast = Xlast(new[] { "ACH_RUN ", "ACH_JUMP", "LEADERBOARD" }, new[] { (0, 7u), (1, 9u) });
        byte[] game = GameAssembly(new[] { "ACH_RUN", "ACH_JUMP" }, new[] { "x", "y" }, callSignedInGamer: true, xlast: xlast);
        using MemoryPackage package = new MemoryPackage().Add("Game.dll", game).Add("xbl.dll", Spa(GameConfig, 3, Png()));

        GeneratedCatalogue catalogue = PackageAchievementCatalogue.Build(package);

        Assert.Equal("xlast", catalogue.KeySource);
        Assert.Equal(new[] { "ACH_RUN", "ACH_JUMP" }, catalogue.Achievements.Select(a => a.Key));
        Assert.Equal(new[] { "Split It!", "Winner" }, catalogue.Achievements.Select(a => a.Name));
    }

    [Fact]
    public void Xlast_of_a_game_that_does_not_call_SignedInGamer_is_keyed_by_xbox_id()
    {
        byte[] xlast = Xlast(new[] { "GET_BLUE_BIRD", "WIN" }, new[] { (0, 7u), (1, 9u) });
        byte[] game = GameAssembly(new[] { "q" }, new[] { "q" }, callSignedInGamer: false, xlast: xlast);
        using MemoryPackage package = new MemoryPackage().Add("Game.dll", game);

        GeneratedCatalogue catalogue = PackageAchievementCatalogue.Build(package);
        Assert.Equal("xbox-id", catalogue.KeySource);
        Assert.Equal(new[] { "7", "9" }, catalogue.Achievements.Select(a => a.Key));
        // No SPA: the XLAST identifier made readable.
        Assert.Equal(new[] { "Get Blue Bird", "Win" }, catalogue.Achievements.Select(a => a.Name));
    }

    // ---- string tables, names, the on-disk form ----

    [Fact]
    public void String_tables_name_a_key_only_when_one_entry_claims_each_role()
    {
        PackageStringTables tables = PackageStringTables.FromEntries(new KeyValuePair<string, string>[]
        {
            new("ACH_TourOfDuty_NAME", "Tour of Duty"),
            new("ACH_TourOfDuty_DESC", "Served a full tour"),
            new("ACH_TourOfDuty_HOWTO", "Complete every mission"),
            new("A_NAME_Other", "One"),
            new("B_NAME_Other", "Two"),
        });
        KeyText? text = tables.Find("TourOfDuty");
        Assert.Equal(("Tour of Duty", "Complete every mission"), (text!.Name, text.Description));
        Assert.Null(tables.Find("Other")?.Name); // two candidates: no guess
        Assert.Null(tables.Find("Missing"));
        Assert.Null(tables.Find("12")); // a number names nothing
    }

    [Theory]
    [InlineData("ACHIEVEMENT_COLLECT_STARS", "Collect Stars")]
    [InlineData("GET_BLUE_BIRD", "Get Blue Bird")]
    [InlineData("TourOfDuty", "Tour Of Duty")]
    [InlineData("achiev_04_bag_lady", "04 Bag Lady")]
    [InlineData("12", "Achievement 12")]
    public void Keys_are_made_readable(string key, string expected) =>
        Assert.Equal(expected, PackageAchievementCatalogue.Readable(key));

    [Fact]
    public void A_written_catalogue_is_in_the_bundled_format_and_carries_its_generator_version()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wpr-gencat-" + Guid.NewGuid().ToString("N"));
        try
        {
            GeneratedCatalogue catalogue = new("il", null, new[]
            {
                new GeneratedAchievement("K1", "Collect Stars", true, "", 0, false, Png()),
                new GeneratedAchievement("K2", "Named", false, "Text", 10, true, null),
            });
            PackageAchievementCatalogue.Write(catalogue, dir, "bda6fdfb-6556-4a85-a077-573e2f5c177c");

            Assert.Equal(PackageAchievementCatalogue.GeneratorVersion, PackageAchievementCatalogue.WrittenVersion(dir));
            string json = File.ReadAllText(System.IO.Path.Combine(dir, "achievements.json"));
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
            var entries = doc.RootElement.GetProperty("Achievements").EnumerateArray().ToList();
            Assert.Equal("K1", entries[0].GetProperty("Key").GetString());
            Assert.True(entries[0].GetProperty("NameFromKey").GetBoolean());
            Assert.True(File.Exists(System.IO.Path.Combine(dir, entries[0].GetProperty("Icon").GetString()!)));
            Assert.True(entries[1].GetProperty("Secret").GetBoolean());
            Assert.False(entries[1].TryGetProperty("Icon", out _));

            // Rewriting replaces the folder whole.
            PackageAchievementCatalogue.Write(GeneratedCatalogue.Empty, dir, "x");
            Assert.Single(Directory.GetFiles(dir));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
        Assert.Equal(0, PackageAchievementCatalogue.WrittenVersion(dir));
    }

    [Fact]
    public void A_colour_texture_xnb_becomes_an_unpremultiplied_png()
    {
        using MemoryStream m = new();
        using BinaryWriter w = new(m, Encoding.UTF8);
        w.Write("XNBw"u8.ToArray()); w.Write((byte)5); w.Write((byte)0); w.Write(0);
        w.Write((byte)1); w.Write("Microsoft.Xna.Framework.Content.Texture2DReader, Microsoft.Xna.Framework.Graphics"); w.Write(0);
        w.Write((byte)0); w.Write((byte)1);
        w.Write(0); w.Write(16); w.Write(16); w.Write(1); w.Write(16 * 16 * 4);
        for (int i = 0; i < 256; i++) { w.Write((byte)64); w.Write((byte)0); w.Write((byte)0); w.Write((byte)128); }
        byte[]? png = XnbTexture.ToPng(m.ToArray());
        Assert.NotNull(png);
        Assert.Equal(16, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.True(PackageImages.IconFits(png));
    }
}

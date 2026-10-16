using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;
using TypeAttributes = Mono.Cecil.TypeAttributes;
using MethodAttributes = Mono.Cecil.MethodAttributes;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// A package with a missing, unreadable or incomplete manifest is completed from its contents
/// (<see cref="PackageManifests"/>), and refused only when nothing in it can run.
/// </summary>
public class PackageManifestsTests
{
    private const string Guid1 = "4c3e2a10-7d55-4b8e-9a51-0f6d1c2b3a49";

    // ---- package builders --------------------------------------------------------------------

    private static ZipArchive Zip(params (string Name, byte[] Bytes)[] entries)
    {
        MemoryStream ms = new MemoryStream();
        using (ZipArchive w = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] bytes) in entries)
            {
                using Stream s = w.CreateEntry(name).Open();
                s.Write(bytes);
            }
        }
        ms.Position = 0;
        return new ZipArchive(ms, ZipArchiveMode.Read);
    }

    private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>A managed assembly with one public class per (namespace, name, base) entry.</summary>
    private static byte[] Assembly(string name, string? title, string? guid,
        params (string Ns, string Name, string BaseNs, string BaseName, string BaseAssembly, string? Xaml)[] types)
    {
        AssemblyDefinition asm = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition(name, new Version(2, 3, 0, 0)), name, ModuleKind.Dll);
        ModuleDefinition module = asm.MainModule;
        if (title != null)
        {
            asm.CustomAttributes.Add(new CustomAttribute(module.ImportReference(typeof(AssemblyTitleAttribute).GetConstructor(new[] { typeof(string) })))
            { ConstructorArguments = { new CustomAttributeArgument(module.TypeSystem.String, title) } });
        }
        if (guid != null)
        {
            asm.CustomAttributes.Add(new CustomAttribute(module.ImportReference(typeof(GuidAttribute).GetConstructor(new[] { typeof(string) })))
            { ConstructorArguments = { new CustomAttributeArgument(module.TypeSystem.String, guid) } });
        }

        foreach (var t in types)
        {
            TypeReference baseRef = t.BaseAssembly == name
                ? module.GetType(t.BaseNs + "." + t.BaseName)
                : new TypeReference(t.BaseNs, t.BaseName, module, new AssemblyNameReference(t.BaseAssembly, new Version(1, 0, 0, 0)));
            TypeDefinition type = new TypeDefinition(t.Ns, t.Name, TypeAttributes.Public | TypeAttributes.Class, baseRef);
            if (t.Xaml != null)
            {
                MethodDefinition init = new MethodDefinition("InitializeComponent", MethodAttributes.Public, module.TypeSystem.Void);
                ILProcessor il = init.Body.GetILProcessor();
                il.Emit(OpCodes.Ldstr, t.Xaml);
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ret);
                type.Methods.Add(init);
            }
            module.Types.Add(type);
        }

        using MemoryStream ms = new MemoryStream();
        asm.Write(ms);
        return ms.ToArray();
    }

    /// <summary>A minimal native ARM PE image: MZ, PE, machine 0x01C4, no CLR directory.</summary>
    private static byte[] ArmExe(byte salt = 0)
    {
        byte[] b = new byte[0x200];
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        BitConverter.GetBytes(0x40).CopyTo(b, 0x3C);
        b[0x40] = (byte)'P'; b[0x41] = (byte)'E';
        BitConverter.GetBytes((ushort)0x01C4).CopyTo(b, 0x44);
        BitConverter.GetBytes((ushort)0x10B).CopyTo(b, 0x40 + 24);
        b[0x1FF] = salt;
        return b;
    }

    private static byte[] XnaGame(string title = "Space Thing") =>
        Assembly("SpaceThing", title, Guid1, ("SpaceThing", "Game1", "Microsoft.Xna.Framework", "Game", "Microsoft.Xna.Framework.Game", null));

    private static string App(PackageManifests m, string attribute) =>
        ((XmlElement)m.WmAppManifest.SelectSingleNode("//App")!).GetAttribute(attribute);

    // ---- tests -------------------------------------------------------------------------------

    [Fact]
    public void Whole_manifests_are_used_as_they_are()
    {
        using ZipArchive zip = Zip(
            ("WMAppManifest.xml", Text("<Deployment><App ProductID=\"{11111111-2222-3333-4444-555555555555}\" Title=\"Real\" RuntimeType=\"XNA\" Version=\"1.1.0.0\" /></Deployment>")),
            ("AppManifest.xaml", Text("<Deployment xmlns=\"http://schemas.microsoft.com/client/2007/deployment\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" EntryPointAssembly=\"SpaceThing\" EntryPointType=\"SpaceThing.Game1\"><Deployment.Parts><AssemblyPart x:Name=\"SpaceThing\" Source=\"SpaceThing.dll\" /></Deployment.Parts></Deployment>")),
            ("SpaceThing.dll", XnaGame()));

        PackageManifests m = PackageManifests.Load(zip, "x.xap")!;
        Assert.False(m.IsSynthesised);
        Assert.Equal("Real", App(m, "Title"));
        Assert.NotNull(m.AppManifest);
    }

    [Fact]
    public void An_xna_package_with_no_manifests_is_inspected()
    {
        using ZipArchive zip = Zip(("SpaceThing.dll", XnaGame()), ("ApplicationIcon.png", new byte[] { 1 }));

        PackageManifests m = PackageManifests.Load(zip, "Space Thing v1.xap")!;

        Assert.Contains("wmappmanifest-missing", m.Synthesised);
        Assert.Contains("appmanifest-missing", m.Synthesised);
        Assert.Equal("XNA", App(m, "RuntimeType"));
        Assert.Equal("Space Thing", App(m, "Title"));
        Assert.Equal("{" + Guid1 + "}", App(m, "ProductID"));
        Assert.Equal("2.3.0.0", App(m, "Version"));
        Assert.Equal("ApplicationIcon.png", m.WmAppManifest.SelectSingleNode("//IconPath")!.InnerText);

        XmlElement deployment = m.AppManifest!.DocumentElement!;
        Assert.Equal("SpaceThing", deployment.GetAttribute("EntryPointAssembly"));
        Assert.Equal("SpaceThing.Game1", deployment.GetAttribute("EntryPointType"));
        // The installer resolves the entry file the way it always has, from the AssemblyPart.
        Assert.Equal("SpaceThing.dll", PackageManifests.ResolveEntryAssemblyFile(m.AppManifest, zip));
    }

    [Fact]
    public void Without_a_title_the_package_file_name_is_used()
    {
        using ZipArchive zip = Zip(("SpaceThing.dll", Assembly("SpaceThing", null, Guid1,
            ("SpaceThing", "Game1", "Microsoft.Xna.Framework", "Game", "Microsoft.Xna.Framework.Game", null))));
        Assert.Equal("Space Thing v1", App(PackageManifests.Load(zip, "Space Thing v1.xap")!, "Title"));
    }

    [Fact]
    public void A_game_derived_through_an_engine_library_is_found_in_the_top_assembly()
    {
        byte[] engine = Assembly("Engine", "Engine", null,
            ("Engine", "GameBase", "Microsoft.Xna.Framework", "Game", "Microsoft.Xna.Framework.Game", null));
        byte[] game = Assembly("Racer", "Racer", null, ("Racer", "RacerGame", "Engine", "GameBase", "Engine", null));
        // The game references Engine, so Engine is a library and Racer the entry.
        using ZipArchive zip = Zip(("Engine.dll", engine), ("Racer.dll", game));

        PackageManifests m = PackageManifests.Load(zip, null)!;
        Assert.Equal("XNA", App(m, "RuntimeType"));
        Assert.Equal("Racer.RacerGame", m.AppManifest!.DocumentElement!.GetAttribute("EntryPointType"));
        Assert.Equal("Racer", App(m, "Title"));
    }

    [Fact]
    public void A_silverlight_package_gets_its_application_and_start_page()
    {
        byte[] asm = Assembly("Flow", "Flow", Guid1,
            ("Flow", "App", "System.Windows", "Application", "System.Windows", null),
            ("Flow", "AboutPage", "Microsoft.Phone.Controls", "PhoneApplicationPage", "Microsoft.Phone", "/Flow;component/AboutPage.xaml"),
            ("Flow", "MainPage", "Microsoft.Phone.Controls", "PhoneApplicationPage", "Microsoft.Phone", "/Flow;component/Views/MainPage.xaml"));
        using ZipArchive zip = Zip(("Flow.dll", asm));

        PackageManifests m = PackageManifests.Load(zip, null)!;
        Assert.Equal("Silverlight", App(m, "RuntimeType"));
        Assert.Equal("Flow.App", m.AppManifest!.DocumentElement!.GetAttribute("EntryPointType"));
        Assert.Equal("Views/MainPage.xaml",
            ((XmlElement)m.WmAppManifest.SelectSingleNode("//DefaultTask")!).GetAttribute("NavigationPage"));
    }

    [Fact]
    public void A_loading_page_starts_before_a_main_menu()
    {
        byte[] asm = Assembly("Flowerz", "Flowerz", Guid1,
            ("Flowerz", "App", "System.Windows", "Application", "System.Windows", null),
            ("Flowerz.Pages", "MainMenu", "Microsoft.Phone.Controls", "PhoneApplicationPage", "Microsoft.Phone", "/Flowerz;component/Pages/MainMenu.xaml"),
            ("Flowerz.Pages", "Loading", "Microsoft.Phone.Controls", "PhoneApplicationPage", "Microsoft.Phone", "/Flowerz;component/Pages/Loading.xaml"));
        using ZipArchive zip = Zip(("Flowerz.dll", asm));
        Assert.Equal("Pages/Loading.xaml",
            ((XmlElement)PackageManifests.Load(zip, null)!.WmAppManifest.SelectSingleNode("//DefaultTask")!).GetAttribute("NavigationPage"));
    }

    [Fact]
    public void A_native_package_runs_its_arm_executable_under_a_stable_id()
    {
        string Id()
        {
            using ZipArchive zip = Zip(("Game.exe", ArmExe()), ("readme.txt", Text("hi")));
            PackageManifests m = PackageManifests.Load(zip, null)!;
            Assert.Equal("Modern Native", App(m, "RuntimeType"));
            Assert.Equal("Game.exe", ((XmlElement)m.WmAppManifest.SelectSingleNode("//DefaultTask")!).GetAttribute("ImagePath"));
            Assert.Null(m.AppManifest);
            return App(m, "ProductID");
        }

        string first = Id();
        Assert.Equal(first, Id());
        Assert.True(Guid.TryParse(first.Trim('{', '}'), out _));

        using ZipArchive other = Zip(("Game.exe", ArmExe(salt: 7)));
        Assert.NotEqual(first, App(PackageManifests.Load(other, null)!, "ProductID"));
    }

    [Fact]
    public void An_appx_manifest_names_the_identity_and_the_executable()
    {
        const string appx = """
            <Package xmlns="http://schemas.microsoft.com/appx/2010/manifest" xmlns:mp="http://schemas.microsoft.com/appx/2014/phone/manifest">
              <Identity Name="Studio.Game" Publisher="CN=x" Version="3.1.0.0" />
              <mp:PhoneIdentity PhoneProductId="aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" PhonePublisherId="00000000-0000-0000-0000-000000000000" />
              <Properties><DisplayName>Galaxy Game</DisplayName><PublisherDisplayName>Studio</PublisherDisplayName><Logo>Assets\StoreLogo.png</Logo></Properties>
              <Applications><Application Id="App" Executable="Real.exe" EntryPoint="Real.App" /></Applications>
            </Package>
            """;
        using ZipArchive zip = Zip(("AppxManifest.xml", Text(appx)), ("Helper.exe", ArmExe(1)), ("Real.exe", ArmExe(2)));

        PackageManifests m = PackageManifests.Load(zip, null)!;
        Assert.Equal("{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}", App(m, "ProductID"));
        Assert.Equal("Galaxy Game", App(m, "Title"));
        Assert.Equal("3.1.0.0", App(m, "Version"));
        Assert.Equal("Real.exe", ((XmlElement)m.WmAppManifest.SelectSingleNode("//DefaultTask")!).GetAttribute("ImagePath"));
    }

    [Fact]
    public void An_unreadable_manifest_is_replaced()
    {
        using ZipArchive zip = Zip(("WMAppManifest.xml", Text("<Deployment><App Title=")), ("SpaceThing.dll", XnaGame()));
        PackageManifests m = PackageManifests.Load(zip, null)!;
        Assert.Contains("wmappmanifest-invalid", m.Synthesised);
        Assert.Equal("XNA", App(m, "RuntimeType"));
        Assert.True(m.WmAppManifestSynthesised);
    }

    [Fact]
    public void An_incomplete_manifest_keeps_what_it_has()
    {
        using ZipArchive zip = Zip(
            ("WMAppManifest.xml", Text("<Deployment><App ProductID=\"{11111111-2222-3333-4444-555555555555}\" Title=\"Kept\"><Tasks><DefaultTask Name=\"_default\" NavigationPage=\"Start.xaml\" /></Tasks></App></Deployment>")),
            ("AppManifest.xaml", Text("<Deployment xmlns=\"http://schemas.microsoft.com/client/2007/deployment\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" EntryPointAssembly=\"SpaceThing\" EntryPointType=\"SpaceThing.Game1\"><Deployment.Parts><AssemblyPart x:Name=\"SpaceThing\" Source=\"SpaceThing.dll\" /></Deployment.Parts></Deployment>")),
            ("SpaceThing.dll", XnaGame()));

        PackageManifests m = PackageManifests.Load(zip, null)!;
        Assert.Equal(new[] { "wmappmanifest-incomplete" }, m.Synthesised);
        Assert.Equal("Kept", App(m, "Title"));
        Assert.Equal("{11111111-2222-3333-4444-555555555555}", App(m, "ProductID"));
        Assert.Equal("XNA", App(m, "RuntimeType"));          // from the AppManifest's entry type
        Assert.Equal("2.3.0.0", App(m, "Version"));
        Assert.Single(m.WmAppManifest.SelectNodes("//DefaultTask")!.Cast<XmlNode>());
        Assert.False(m.AppManifestSynthesised);
    }

    [Fact]
    public void A_missing_appmanifest_alone_is_synthesised()
    {
        using ZipArchive zip = Zip(
            ("WMAppManifest.xml", Text("<Deployment><App ProductID=\"{11111111-2222-3333-4444-555555555555}\" Title=\"T\" RuntimeType=\"XNA\" Version=\"1.0.0.0\" /></Deployment>")),
            ("SpaceThing.dll", XnaGame()));

        PackageManifests m = PackageManifests.Load(zip, null)!;
        Assert.Equal(new[] { "appmanifest-missing" }, m.Synthesised);
        Assert.False(m.WmAppManifestSynthesised);
        Assert.Equal("SpaceThing.Game1", m.AppManifest!.DocumentElement!.GetAttribute("EntryPointType"));

        // A preview does not look at AppManifest.xaml at all.
        Assert.False(PackageManifests.Load(zip, null, needEntryPoint: false)!.IsSynthesised);
    }

    [Fact]
    public void Nothing_runnable_is_still_refused()
    {
        using ZipArchive zip = Zip(("readme.txt", Text("no game here")), ("lib.dll", Assembly("Lib", "Lib", Guid1)));
        Assert.Null(PackageManifests.Load(zip, "junk.xap"));
    }

    [Fact]
    public void Synthesised_manifests_are_written_beside_the_package_files()
    {
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wpr-manifest-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(System.IO.Path.Combine(folder, "WMAppManifest.xml"), "<Deployment><App Title=");
            using ZipArchive zip = Zip(("WMAppManifest.xml", Text("<Deployment><App Title=")), ("SpaceThing.dll", XnaGame()));
            PackageManifests m = PackageManifests.Load(zip, null)!;
            m.WriteInto(folder);

            Assert.Equal("<Deployment><App Title=", File.ReadAllText(System.IO.Path.Combine(folder, "WMAppManifest.xml.original")));
            XmlDocument written = new XmlDocument();
            written.Load(System.IO.Path.Combine(folder, "WMAppManifest.xml"));
            Assert.Equal("XNA", ((XmlElement)written.SelectSingleNode("//App")!).GetAttribute("RuntimeType"));
            Assert.True(File.Exists(System.IO.Path.Combine(folder, "AppManifest.xaml")));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void An_icon_is_guessed_from_png_names()
    {
        using ZipArchive tile = Zip(("Content/a.png", new byte[1]), ("AngryBirds_Tile_173x173.png", new byte[1]), ("splash.png", new byte[1]));
        Assert.Equal("AngryBirds_Tile_173x173.png", PackageInspection.GuessIcon(tile));

        using ZipArchive icon = Zip(("assets/icons/Tiles/FlipCycleTileSmall.png", new byte[1]), ("assets/icons/ApplicationIcon.png", new byte[1]));
        Assert.Equal("assets/icons/ApplicationIcon.png", PackageInspection.GuessIcon(icon));

        using ZipArchive none = Zip(("Content/deep/dir/icon.png", new byte[1]), ("splash.png", new byte[1]));
        Assert.Null(PackageInspection.GuessIcon(none));
    }

    [Fact]
    public void Pe_images_are_classified()
    {
        Assert.Equal(PackageInspection.PeKind.NativeArm, PackageInspection.ClassifyPe(ArmExe()));
        Assert.Equal(PackageInspection.PeKind.Managed, PackageInspection.ClassifyPe(XnaGame()));
        Assert.Equal(PackageInspection.PeKind.NotPe, PackageInspection.ClassifyPe(Text("PK not a pe at all, long enough to look at the header fields")));
    }
}

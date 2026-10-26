using System;
using System.IO;
using System.IO.Compression;
using System.Resources;
using System.Text;
using System.Xml;
using Mono.Cecil;
using WPR.WindowsCompability;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// The platform members Microsoft Bingo (WP8, managed XAML over MvvmLight, Coding4Fun and a
/// native FX renderer) needed, and the install rule that keeps it on the managed host.
/// </summary>
public class BingoPlatformTests
{
    private const string Presentation = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    // ---- install classification ---------------------------------------------------------------

    private static byte[] ManagedAppWithXaml(string name, params (string Key, string Xaml)[] documents)
    {
        using MemoryStream resources = new();
        using (ResourceWriter writer = new(resources))
        {
            foreach ((string key, string xaml) in documents)
            {
                writer.AddResource(key, new MemoryStream(Encoding.UTF8.GetBytes(xaml)), closeAfterWrite: true);
            }
            writer.Generate();
        }

        AssemblyDefinition asm = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition(name, new Version(1, 0, 0, 0)), name, ModuleKind.Dll);
        asm.MainModule.Resources.Add(new EmbeddedResource(name + ".g.resources", ManifestResourceAttributes.Public, resources.ToArray()));
        using MemoryStream ms = new();
        asm.Write(ms);
        return ms.ToArray();
    }

    /// <summary>A native ARM DLL that imports d3d11.dll: MZ, PE, machine 0x01C4, no CLR directory.</summary>
    private static byte[] ArmD3DComponent()
    {
        byte[] b = new byte[0x400];
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        BitConverter.GetBytes(0x40).CopyTo(b, 0x3C);
        b[0x40] = (byte)'P'; b[0x41] = (byte)'E';
        BitConverter.GetBytes((ushort)0x01C4).CopyTo(b, 0x44);
        Encoding.ASCII.GetBytes("d3d11.dll").CopyTo(b, 0x300);
        return b;
    }

    private static string? Classify(byte[] entryAssembly)
    {
        const string manifest = "<Deployment><App ProductID=\"{6fa9195f-61fd-4213-b809-b6d7caeddb9f}\" RuntimeType=\"Silverlight\">"
            + "<ActivatableClasses><InProcessServer><Path>Fx.dll</Path><ActivatableClass ActivatableClassId=\"Studio.Renderer\" /></InProcessServer></ActivatableClasses>"
            + "</App></Deployment>";
        using MemoryStream ms = new();
        using (ZipArchive w = new(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string n, byte[] bytes) { using Stream s = w.CreateEntry(n).Open(); s.Write(bytes); }
            Add("WMAppManifest.xml", Encoding.UTF8.GetBytes(manifest));
            Add("AppManifest.xaml", Encoding.UTF8.GetBytes("<Deployment EntryPointAssembly=\"App\" EntryPointType=\"App.App\" />"));
            Add("App.dll", entryAssembly);
            Add("Fx.dll", ArmD3DComponent());
        }
        ms.Position = 0;
        using ZipArchive zip = new(ms, ZipArchiveMode.Read);
        XmlDocument doc = new();
        doc.LoadXml(manifest);
        return NativeXamlComponent.Find(zip, doc.SelectSingleNode("//App")!);
    }

    [Fact]
    public void A_surface_only_in_a_user_control_keeps_the_managed_host()
    {
        byte[] app = ManagedAppWithXaml("App",
            ("frame/mainframe.xaml", $"<phone:PhoneApplicationPage xmlns:phone=\"clr-namespace:Microsoft.Phone.Controls\" {Presentation}><Grid /></phone:PhoneApplicationPage>"),
            ("view/fxwindow.xaml", $"<uc:ProjectUserControl xmlns:uc=\"clr-namespace:App\" {Presentation}><DrawingSurface x:Name=\"Fx\" /></uc:ProjectUserControl>"));
        Assert.Null(Classify(app));
    }

    [Fact]
    public void A_surface_on_a_page_is_a_Direct3D_XAML_game()
    {
        byte[] app = ManagedAppWithXaml("App",
            ("mainpage.xaml", $"<phone:PhoneApplicationPage xmlns:phone=\"clr-namespace:Microsoft.Phone.Controls\" {Presentation}><DrawingSurfaceBackgroundGrid /></phone:PhoneApplicationPage>"));
        Assert.Equal("Fx.dll", Classify(app));
    }

    [Fact]
    public void No_surface_in_XAML_is_a_Direct3D_XAML_game()
    {
        // FIFA and Tetris Blitz build theirs in code.
        byte[] app = ManagedAppWithXaml("App",
            ("mainpage.xaml", $"<phone:PhoneApplicationPage xmlns:phone=\"clr-namespace:Microsoft.Phone.Controls\" {Presentation}><Grid /></phone:PhoneApplicationPage>"));
        Assert.Equal("Fx.dll", Classify(app));
    }

    // ---- shims ------------------------------------------------------------------------------

    [Fact]
    public void FontFamily_resources_and_attributes_build()
    {
        var grid = (Grid)XamlReader.Load(
            $"<Grid {Presentation}><Grid.Resources><FontFamily x:Key=\"Body\">/Fonts/a.ttf#Sans</FontFamily></Grid.Resources>"
            + "<TextBlock x:Name=\"t\" FontFamily=\"Segoe WP\" /></Grid>");
        Assert.Equal(new FontFamily("/Fonts/a.ttf#Sans"), grid.Resources["Body"]);
        Assert.Equal("Segoe WP", ((TextBlock)grid.Children[0]).FontFamily.Source);
    }

    [Fact]
    public void AddHandler_subscribes_the_named_event()
    {
        var element = new Grid();
        int calls = 0;
        MouseButtonEventHandler handler = (_, _) => calls++;
        element.AddHandler(UIElement.MouseLeftButtonDownEvent, handler, true);
        element.RaiseMouseLeftButtonDown(new Point(1, 1));
        element.RemoveHandler(UIElement.MouseLeftButtonDownEvent, handler);
        element.RaiseMouseLeftButtonDown(new Point(1, 1));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void TransformBounds_boxes_the_transformed_corners()
    {
        var transform = new MatrixTransform(new Matrix(0, 1, -1, 0, 10, 20)); // a quarter turn, then moved
        Rect r = transform.TransformBounds(new Rect(0, 0, 4, 2));
        Assert.Equal(new Rect(8, 20, 2, 4), r);
    }

    [Fact]
    public void BitmapImage_reads_its_constructor_address_back()
    {
        var uri = new Uri("/Assets/a.png", UriKind.Relative);
        Assert.Same(uri, new BitmapImage(uri).UriSource);
    }

    private sealed class Selector : ContentControl
    {
        public object? Seen;
        protected override void OnContentChanged(object? oldContent, object? newContent) => Seen = newContent;
    }

    [Fact]
    public void ContentControl_calls_its_override_point()
    {
        var selector = new Selector { Content = "model" };
        Assert.Equal("model", selector.Seen);
    }

    [Fact]
    public void Type_GetType_with_one_argument_finds_a_type_by_assembly_qualified_name()
    {
        Assert.Equal(typeof(BingoPlatformTests), Type2.GetType(typeof(BingoPlatformTests).AssemblyQualifiedName!));
        Assert.Null(Type2.GetType("No.Such.Type, NoSuchAssembly"));
    }

    [Fact]
    public void Manipulation_values_are_classes_with_properties()
    {
        var args = new ManipulationDeltaEventArgs();
        Assert.NotNull(args.DeltaManipulation);
        Assert.NotNull(typeof(ManipulationDelta).GetProperty(nameof(ManipulationDelta.Translation)));
        Assert.False(typeof(ManipulationVelocities).IsValueType);
    }
}

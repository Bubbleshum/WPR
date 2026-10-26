using System.Reflection;
using System.Reflection.Emit;
using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// A page that feeds touch to its component from <c>Touch.FrameReported</c> (Asphalt 7: Heat calls
/// <c>AddPoint</c>/<c>UpdatePoint</c>/<c>RemovePoint</c> and has no manipulation handler): the
/// handler's calls are found and mapped by the <c>TouchPoint.Action</c> each one sits under, not by
/// name or order; <c>GetTouchPoints(null)</c> means portrait root coordinates; the host turns a
/// landscape composition point into the portrait DIPs that handler would read.
/// </summary>
public sealed class TouchFrameSuite : IContractSuite
{
    public string Name => "touch frame (Touch.FrameReported pages)";

    private const string ClassId = "Comp.Direct3DBackground";

    public void Run(Harness h)
    {
        string folder = Path.Combine(h.Sandbox, "touchframe");
        Directory.CreateDirectory(folder);

        // Calls named so nothing but the Action comparisons can place them, written Up, Down, Move.
        WritePage(Path.Combine(folder, "Page.dll"), relativeToRoot: true);
        XamlTouchFrame? frame = XamlTouchFrames.Infer(folder, ClassId);
        h.Check("a Touch.FrameReported handler calling the component is found", frame is not null);
        h.Check("   each call is placed by the TouchPoint.Action it sits under (1 down, 2 move, 3 up)",
            frame is { Down: "Gamma", Move: "Alpha", Up: "Beta" }, $"{frame}");
        h.Check("   GetTouchPoints(null) reads root (portrait) coordinates", frame?.RelativeToRoot == true);
        h.Check("   another class's calls are not taken", XamlTouchFrames.Infer(folder, "Other.Class") is null);

        WritePage(Path.Combine(folder, "Page.dll"), relativeToRoot: false);
        h.Check("GetTouchPoints(this) reads page coordinates", XamlTouchFrames.Infer(folder, ClassId)?.RelativeToRoot == false);

        // The host side: a point in the 1280x768 landscape composition, as the handler reads it.
        WinRtRuntime rt = h.E.WinRt;
        FieldInfo shellField = typeof(WinRtRuntime).GetField("_shell", BindingFlags.NonPublic | BindingFlags.Instance)!;
        object? saved = shellField.GetValue(rt);
        try
        {
            var shell = new XamlShell("x.dll", ClassId, [], []) { TouchFrame = new XamlTouchFrame("A", "U", "R", true) };
            shellField.SetValue(rt, shell);
            MethodInfo position = typeof(WinRtRuntime).GetMethod("TouchFramePosition", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var centre = ((float X, float Y))position.Invoke(rt, [shell.TouchFrame, 640f, 384f])!;
            h.Check("the composition's centre is the portrait window's centre (240, 400 DIPs)",
                Math.Abs(centre.X - 240) < 1 && Math.Abs(centre.Y - 400) < 1, $"{centre}");
            var topLeft = ((float X, float Y))position.Invoke(rt, [shell.TouchFrame, 0f, 0f])!;
            h.Check("   the landscape top-left is the portrait top-right (the phone turned anticlockwise)",
                Math.Abs(topLeft.X - 480) < 1 && Math.Abs(topLeft.Y) < 1, $"{topLeft}");

            var pageShell = shell with { Landscape = true, TouchFrame = new XamlTouchFrame("A", "U", "R", false) };
            shellField.SetValue(rt, pageShell);
            var onPage = ((float X, float Y))position.Invoke(rt, [pageShell.TouchFrame, 1280f, 768f])!;
            h.Check("relative to a landscape page, the composition's corner is the page's (800, 480 DIPs)",
                Math.Abs(onPage.X - 800) < 1 && Math.Abs(onPage.Y - 480) < 1, $"{onPage}");
        }
        finally
        {
            shellField.SetValue(rt, saved);
        }

        // An installed Asphalt 7 (the real data root, or WPR_DATA_ROOT's).
        string local = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA")!, "WPR");
        string? asphalt = new[] { Environment.GetEnvironmentVariable("WPR_DATA_ROOT"), local }
            .Where(root => !string.IsNullOrEmpty(root))
            .Select(root => Path.Combine(root!, "AppData", "013e9108-6170-496f-9328-3b57f2b4732f"))
            .FirstOrDefault(Directory.Exists);
        if (asphalt is null)
        {
            Console.WriteLine("  (skipped Asphalt 7: not installed)");
            return;
        }

        XamlTouchFrame? a7 = XamlTouchFrames.Infer(asphalt, "PhoneDirect3DXamlAppComponent.Direct3DBackground");
        h.Check("Asphalt 7: AddPoint / UpdatePoint / RemovePoint, root coordinates",
            a7 is { Down: "AddPoint", Move: "UpdatePoint", Up: "RemovePoint", RelativeToRoot: true }, $"{a7}");

        // Its back key: OnBackKeyPress(true) answers an int the page tests with "> 0" (close).
        FieldInfo metadataField = typeof(WinRtRuntime).GetField("_metadata", BindingFlags.NonPublic | BindingFlags.Instance)!;
        object? savedMetadata = metadataField.GetValue(rt), savedShell = shellField.GetValue(rt);
        try
        {
            metadataField.SetValue(rt, WinmdReader.Load(Directory.GetFiles(asphalt, "*.winmd")));
            shellField.SetValue(rt, new XamlShell("Asphalt7HybridComponent.dll", "PhoneDirect3DXamlAppComponent.Direct3DBackground", [], []));
            MethodInfo means = typeof(WinRtRuntime).GetMethod("BackAnswerMeansTrue", BindingFlags.NonPublic | BindingFlags.Instance)!;
            bool Means(string method, long answer) => (bool)means.Invoke(rt, [method, answer])!;
            h.Check("an Int32 back answer means 'close' when greater than 0",
                Means("OnBackKeyPress", 1) && Means("OnBackKeyPress", 0x100) && !Means("OnBackKeyPress", 0) && !Means("OnBackKeyPress", 0xFFFFFFFF));
            h.Check("   a Boolean answer by its low byte", Means("IsOpenIGP", 1) && !Means("IsOpenIGP", 0x100));
        }
        finally
        {
            metadataField.SetValue(rt, savedMetadata);
            shellField.SetValue(rt, savedShell);
        }
    }

    /// <summary>
    /// A page assembly: <c>Subscribe</c> hands <c>Handler</c> to <c>System.Windows.Input.Touch.add_FrameReported</c>;
    /// <c>Handler</c> reads the points and calls the component under <c>get_Action</c> comparisons.
    /// </summary>
    private static void WritePage(string path, bool relativeToRoot)
    {
        var builder = new PersistedAssemblyBuilder(new AssemblyName("Page"), typeof(object).Assembly);
        ModuleBuilder module = builder.DefineDynamicModule("Page");

        TypeBuilder touch = module.DefineType("System.Windows.Input.Touch", TypeAttributes.Public | TypeAttributes.Class);
        MethodBuilder add = touch.DefineMethod("add_FrameReported", MethodAttributes.Public | MethodAttributes.Static, typeof(void), [typeof(object)]);
        add.GetILGenerator().Emit(OpCodes.Ret);

        TypeBuilder args = module.DefineType("System.Windows.Input.TouchFrameEventArgs", TypeAttributes.Public | TypeAttributes.Class);
        MethodBuilder points = args.DefineMethod("GetTouchPoints", MethodAttributes.Public | MethodAttributes.Static, typeof(object), [typeof(object)]);
        points.GetILGenerator().Emit(OpCodes.Ldnull);
        points.GetILGenerator().Emit(OpCodes.Ret);

        TypeBuilder point = module.DefineType("System.Windows.Input.TouchPoint", TypeAttributes.Public | TypeAttributes.Class);
        MethodBuilder action = point.DefineMethod("get_Action", MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes);
        action.GetILGenerator().Emit(OpCodes.Ldc_I4_0);
        action.GetILGenerator().Emit(OpCodes.Ret);

        TypeBuilder component = module.DefineType(ClassId, TypeAttributes.Public | TypeAttributes.Class);
        MethodBuilder Method(string name)
        {
            MethodBuilder m = component.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static, typeof(void), [typeof(int), typeof(int), typeof(int)]);
            m.GetILGenerator().Emit(OpCodes.Ret);
            return m;
        }

        MethodBuilder alpha = Method("Alpha"), beta = Method("Beta"), gamma = Method("Gamma");

        TypeBuilder page = module.DefineType("Shell.MainPage", TypeAttributes.Public | TypeAttributes.Class);
        MethodBuilder handler = page.DefineMethod("Touch_FrameReported", MethodAttributes.Public | MethodAttributes.Static, typeof(void), [typeof(object), typeof(object)]);
        ILGenerator il = handler.GetILGenerator();
        il.Emit(relativeToRoot ? OpCodes.Ldnull : OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, points);
        il.Emit(OpCodes.Pop);
        foreach ((int value, MethodBuilder call) in new[] { (3, beta), (1, gamma), (2, alpha) })
        {
            Label next = il.DefineLabel();
            il.Emit(OpCodes.Call, action);
            il.Emit(OpCodes.Ldc_I4, value);
            il.Emit(OpCodes.Bne_Un, next);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ldc_I4_2);
            il.Emit(OpCodes.Ldc_I4_3);
            il.Emit(OpCodes.Call, call);
            il.MarkLabel(next);
        }

        il.Emit(OpCodes.Ret);

        MethodBuilder subscribe = page.DefineMethod("Subscribe", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
        ILGenerator sub = subscribe.GetILGenerator();
        sub.Emit(OpCodes.Ldnull);
        sub.Emit(OpCodes.Ldftn, handler);
        sub.Emit(OpCodes.Newobj, typeof(Action<object, object>).GetConstructor([typeof(object), typeof(IntPtr)])!);
        sub.Emit(OpCodes.Call, add);
        sub.Emit(OpCodes.Ret);

        foreach (TypeBuilder type in new[] { touch, args, point, component, page })
        {
            type.CreateType();
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        builder.Save(path);
    }
}

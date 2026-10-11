using System.Collections;
using System.Reflection;
using WPR.Common;
using WPR.Engine.Launchers;
using WPR.Engine.Vibration;
using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// The host wiring of a WP8 native title (Wp8HostServices, AppLifecycle.cs, the per-title local
/// folder): Launcher.LaunchUriAsync through LauncherBackend with WP7's link policy, VibrationDevice
/// through VibrationBackend and the player's switch, the local folder path from the package's
/// ProductID, and the app lifecycle raised to handlers the guest registered with add_*.
/// </summary>
public sealed class HostWiringSuite : IContractSuite
{
    public string Name => "host wiring";

    private const long SOk = 0;

    private Harness _h = null!;

    private ArmEmulator E => _h.E;

    private static readonly FieldInfo Traps = typeof(ArmEmulator).GetField("_traps", BindingFlags.NonPublic | BindingFlags.Instance)!;

    public void Run(Harness h)
    {
        _h = h;
        Launcher(h);
        Vibration(h);
        LocalFolder(h);
        Lifecycle(h);
    }

    // -------------------------------------------------------------------------------------------
    // Launcher
    // -------------------------------------------------------------------------------------------

    private sealed class RecordingLauncher : IUriLauncher
    {
        public List<Uri> Opened { get; } = new();

        public bool TryOpen(Uri uri)
        {
            Opened.Add(uri);
            return true;
        }
    }

    private void Launcher(Harness h)
    {
        IUriLauncher? previous = LauncherBackend.Uri;
        var launcher = new RecordingLauncher();
        LauncherBackend.SetUriLauncher(launcher);
        try
        {
            Wp8HostServices services = Wp8HostServices.Attach(E.WinRt);
            Func<string, bool> hook = E.WinRt.UriLauncher!;

            h.Check("https is opened through LauncherBackend", hook("https://example.com/a?b=1") && launcher.Opened.LastOrDefault()?.AbsoluteUri == "https://example.com/a?b=1");
            h.Check("http is opened", hook("http://example.org/") && launcher.Opened.Count == 2);
            h.Check("vnd.youtube: is rewritten to youtube.com, as WebBrowserTask does",
                hook("vnd.youtube:abc123") && launcher.Opened.Last().AbsoluteUri == "https://www.youtube.com/watch?v=abc123");

            int before = launcher.Opened.Count;
            string[] refused = ["file:///C:/Windows/win.ini", "ms-settings:", "mailto:someone@example.com", "intent://x#Intent;end", "zune:navigate?appid=1", "not a uri"];
            bool allRefused = refused.All(u => !hook(u));
            h.Check("every other scheme is refused (answers false)", allRefused);
            h.Check("a refused URI never reaches the launcher", launcher.Opened.Count == before);
            h.Check("a refusal is recorded", services.Log.Count(l => l.StartsWith("launch refused", StringComparison.Ordinal)) == refused.Length, string.Join("; ", services.Log.TakeLast(3)));

            // Through the guest's own path: Launcher.LaunchUriAsync(Uri) answers the hook's verdict.
            long uriFactory = Factory("Windows.Foundation.Uri", "Windows.Foundation.IUriRuntimeClassFactory");
            long statics = Factory("Windows.System.Launcher", "Windows.System.ILauncherStatics");
            int launchSlot = WinRtSystemInterfaces.Slot("Windows.System.ILauncherStatics", "LaunchUriAsync");
            long webOp = Ret(statics, launchSlot, Ret(uriFactory, 6, HS("https://wpr.example/")));
            h.Check("LaunchUriAsync(https) goes through the hook and answers true",
                webOp != 0 && E.ReadMemory(ResultBytes(webOp, 1), 1)[0] == 1 && launcher.Opened.Last().Host == "wpr.example");
            int count = launcher.Opened.Count;
            long fileOp = Ret(statics, launchSlot, Ret(uriFactory, 6, HS("file:///C:/secret.txt")));
            h.Check("LaunchUriAsync(file:) answers false and opens nothing",
                fileOp != 0 && E.ReadMemory(ResultBytes(fileOp, 1), 1)[0] == 0 && launcher.Opened.Count == count);

            LauncherBackend.SetUriLauncher(null);
            h.Check("no launcher on the platform: answers false, recorded", !hook("https://example.com/") && services.Log.Last().Contains("no launcher", StringComparison.Ordinal));
        }
        finally
        {
            LauncherBackend.SetUriLauncher(previous);
        }
    }

    // -------------------------------------------------------------------------------------------
    // Vibration
    // -------------------------------------------------------------------------------------------

    private sealed class RecordingMotor : IVibrationProvider
    {
        public List<TimeSpan> Started { get; } = new();

        public int Stops { get; private set; }

        public bool IsSupported => true;

        public void Vibrate(TimeSpan duration, float intensity) => Started.Add(duration);

        public void Stop() => Stops++;
    }

    private void Vibration(Harness h)
    {
        IVibrationProvider? previousDevice = VibrationBackend.Device;
        Configuration? previousConfig = Configuration.Current;
        string configFolder = Path.Combine(Path.GetTempPath(), "wp8-contract-config");
        Directory.CreateDirectory(configFolder);
        var motor = new RecordingMotor();
        try
        {
            VibrationBackend.SetDevice(motor);
            Configuration.Current = new Configuration(configFolder) { VibrationEnabled = true };
            Wp8HostServices services = Wp8HostServices.Attach(E.WinRt);

            long factory = E.WinRt.GetActivationFactory("Windows.Phone.Devices.Notification.VibrationDevice")!.Value;
            long device = Ret(factory, 6);
            Invoke(device, 6, 0, 2_500_000, 0);   // Vibrate(TimeSpan 0.25 s) in r2:r3
            h.Check("VibrationDevice.Vibrate reaches VibrationBackend.Device for the duration", motor.Started.SequenceEqual([TimeSpan.FromMilliseconds(250)]));
            Invoke(device, 7);
            h.Check("VibrationDevice.Cancel stops the motor", motor.Stops == 1);

            Configuration.Current.VibrationEnabled = false;
            Invoke(device, 6, 0, 2_500_000, 0);
            h.Check("with vibration turned off, Vibrate does not start the motor", motor.Started.Count == 1);
            h.Check("and the muted request is recorded", services.Log.Any(l => l.Contains("turned off", StringComparison.Ordinal)));
            Invoke(device, 7);
            h.Check("Cancel still stops the motor with vibration turned off", motor.Stops == 2);

            Wp8HostServices.StopVibration();
            h.Check("the host's teardown/deactivation stop reaches the motor", motor.Stops == 3);

            VibrationBackend.SetDevice(null);
            Configuration.Current.VibrationEnabled = true;
            Invoke(device, 6, 0, 2_500_000, 0);
            h.Check("no motor on the platform: Vibrate still succeeds and nothing runs", motor.Started.Count == 1);
        }
        finally
        {
            VibrationBackend.SetDevice(previousDevice);
            Configuration.Current = previousConfig;
        }
    }

    // -------------------------------------------------------------------------------------------
    // Per-title local folder
    // -------------------------------------------------------------------------------------------

    private static readonly MethodInfo Resolve = typeof(FileLibrary).GetMethod("Resolve", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(string), typeof(bool).MakeByRefType()])!;

    private static (string Path, bool Writable) ResolveIn(ArmEmulator emulator, string guestPath)
    {
        object?[] args = [guestPath, false];
        string path = (string)Resolve.Invoke(emulator.Stubs.Files, args)!;
        return (path, (bool)args[1]!);
    }

    private void LocalFolder(Harness h)
    {
        string first = WinRtRuntime.ReadProductId(Path.GetDirectoryName(h.Image)!)!;
        string firstPath = E.WinRt.LocalFolderPath;
        h.Check("the local folder carries this title's product id", first is not null && firstPath == WinRtRuntime.LocalFolderPathFor(first) &&
            firstPath.Contains("{" + first.ToUpperInvariant() + "}", StringComparison.Ordinal), firstPath);
        (string resolved, bool writable) = ResolveIn(E, firstPath + @"\save.bin");
        h.Check("its local folder maps into the sandbox", writable && resolved == Path.Combine(h.Sandbox, "save.bin"), resolved);
        h.Check("ApplicationData.LocalFolder.Path is that path", LocalFolderPathThroughWinRt(h) == firstPath);

        // A second installed title, whose product id differs.
        string apps = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA")!, "WPR", "AppData");
        string? other = Directory.Exists(apps)
            ? Directory.EnumerateDirectories(apps)
                .Where(d => !string.Equals(Path.GetFileName(d), first, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(d, "WMAppManifest.xml")))
                .SelectMany(d => Directory.EnumerateFiles(d, "*.exe"))
                .FirstOrDefault(IsArmImage)
            : null;
        if (other is null)
        {
            Console.WriteLine("SKIP  no second installed WP8 exe title to compare local folders with");
            return;
        }

        string secondId = WinRtRuntime.ReadProductId(Path.GetDirectoryName(other)!)!;
        Harness second = new(other, h.Sandbox);
        try
        {
            string secondPath = second.E.WinRt.LocalFolderPath;
            h.Check($"a second title ({Path.GetFileName(other)}) gets its own local folder path",
                secondId is not null && secondPath == WinRtRuntime.LocalFolderPathFor(secondId) && secondPath != firstPath, secondPath);
            (string mapped, bool canWrite) = ResolveIn(second.E, secondPath + @"\save.bin");
            h.Check("and its own path maps into the sandbox", canWrite && mapped == Path.Combine(h.Sandbox, "save.bin"), mapped);
            (string foreign, _) = ResolveIn(second.E, firstPath + @"\save.bin");
            h.Check("the first title's local path is not the second title's local folder", foreign != mapped, foreign);
            _h = second;
            h.Check("its ApplicationData.LocalFolder.Path is its own", LocalFolderPathThroughWinRt(second) == secondPath);
        }
        finally
        {
            _h = h;
            HostStubs.SandboxRoot = h.Sandbox;
            second.E.Dispose();
        }
    }

    private static bool IsArmImage(string path)
    {
        try
        {
            return PeImage.Load(path).Machine == PeImage.MachineArmNt;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private string LocalFolderPathThroughWinRt(Harness h)
    {
        long statics = Factory("Windows.Storage.ApplicationData", "Windows.Storage.IApplicationDataStatics");
        long appData = Ret(statics, WinRtSystemInterfaces.Slot("Windows.Storage.IApplicationDataStatics", "get_Current"));
        long local = Ret(appData, WinRtSystemInterfaces.Slot("Windows.Storage.IApplicationData", "get_LocalFolder"));
        long item = QI(local, "Windows.Storage.IStorageItem");
        return Str(Ret(item, WinRtSystemInterfaces.Slot("Windows.Storage.IStorageItem", "get_Path")));
    }

    // -------------------------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------------------------

    private static readonly MethodInfo DeliverLifecycle = typeof(WinRtRuntime).GetMethod("DeliverLifecycle", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private bool Deliver(Action continueWith) => (bool)DeliverLifecycle.Invoke(E.WinRt, [continueWith])!;

    /// <summary>A guest delegate whose Invoke is image code (never run here); r12 names it when it is called.</summary>
    private (long Handler, long Invoke) FakeDelegate(int index)
    {
        PeImage pe = PeImage.Load(_h.Image);
        long invoke = (pe.ImageBase + pe.Sections.First(s => s.Name.StartsWith(".text", StringComparison.Ordinal)).VirtualAddress + 0x10 + (index * 0x10)) | 1;
        long vtable = _h.Buffer(16);
        E.WriteUInt32(vtable + 12, (uint)invoke);
        long handler = _h.Buffer(8);
        E.WriteUInt32(handler, (uint)vtable);
        return (handler, invoke);
    }

    /// <summary>The handler the runtime just called: (invoke target, r0, r1, r2).</summary>
    private (long Target, long Handler, long Sender, long Args) Called()
    {
        _returnTrap = E.ReadRegister(10) & ~1L;
        return (E.ReadRegister(78), E.ReadRegister(Harness.R0) & 0xFFFFFFFFL, E.ReadRegister(Harness.R1) & 0xFFFFFFFFL, E.ReadRegister(Harness.R2) & 0xFFFFFFFFL);
    }

    /// <summary>The return trap the runtime set in lr for the handler last called (calls made "inside" the handler overwrite lr).</summary>
    private long _returnTrap;

    /// <summary>The guest handler returns S_OK: run the return trap the runtime set in lr.</summary>
    private void ReturnFromHandler()
    {
        long lr = _returnTrap;
        _returnTrap = 0;
        if (lr == 0)
        {
            Called();
            lr = _returnTrap;
        }

        object trap = ((IDictionary)Traps.GetValue(E)!)[lr] ?? throw new InvalidOperationException($"lr 0x{lr:X} is not a return trap");
        Action handler = (Action)trap.GetType().GetProperty("Handler")!.GetValue(trap)!;
        E.WriteRegister(Harness.R0, 0);
        handler();
    }

    private void Lifecycle(Harness h)
    {
        WinRtRuntime rt = E.WinRt;
        long coreWindow = (long)typeof(WinRtRuntime).GetField("_coreWindow", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(rt)!;
        long core = Factory("Windows.ApplicationModel.Core.CoreApplication", "Windows.ApplicationModel.Core.ICoreApplication");

        var activated = FakeDelegate(1);
        var visibility = FakeDelegate(2);
        var suspending = FakeDelegate(3);
        var resuming = FakeDelegate(4);

        long token = _h.Buffer(8);
        h.Check("CoreWindow.add_Activated accepts a handler", Invoke(coreWindow, WinRtSystemInterfaces.Slot("Windows.UI.Core.ICoreWindow", "add_Activated"), activated.Handler, token) == SOk);
        long visibilityToken = _h.Buffer(8);
        h.Check("CoreWindow.add_VisibilityChanged accepts a handler", Invoke(coreWindow, WinRtSystemInterfaces.Slot("Windows.UI.Core.ICoreWindow", "add_VisibilityChanged"), visibility.Handler, visibilityToken) == SOk);
        h.Check("the two subscriptions have distinct, non-zero tokens", _h.U64(token) != 0 && _h.U64(token) != _h.U64(visibilityToken));
        h.Check("CoreApplication.add_Suspending accepts a handler", Invoke(core, WinRtSystemInterfaces.Slot("Windows.ApplicationModel.Core.ICoreApplication", "add_Suspending"), suspending.Handler, _h.Buffer(8)) == SOk);
        h.Check("CoreApplication.add_Resuming accepts a handler", Invoke(core, WinRtSystemInterfaces.Slot("Windows.ApplicationModel.Core.ICoreApplication", "add_Resuming"), resuming.Handler, _h.Buffer(8)) == SOk);

        h.Check("nothing to deliver while the host is in the foreground", !Deliver(() => { }));
        rt.InjectVisibility(true);
        h.Check("a foreground request while already in the foreground raises nothing", !Deliver(() => { }));

        // ---- to the background: Activated(Deactivated), VisibilityChanged(false), Suspending ----
        rt.SuspendingDeadline = TimeSpan.FromHours(1);
        bool continued = false;
        rt.InjectVisibility(false);
        h.Check("going to the background is delivered", Deliver(() => continued = true));

        var call = Called();
        h.Check("1. CoreWindow.Activated is raised first, on the window", call.Target == activated.Invoke && call.Handler == activated.Handler && call.Sender == coreWindow);
        long state = Ret(call.Args, WinRtSystemInterfaces.Slot("Windows.UI.Core.IWindowActivatedEventArgs", "get_WindowActivationState"));
        h.Check("   with WindowActivationState Deactivated", state == 1, $"{state}");
        long handled = QI(call.Args, "Windows.UI.Core.ICoreWindowEventArgs");
        h.Check("   and its args are ICoreWindowEventArgs (Handled)", handled != 0 && Invoke(handled, WinRtSystemInterfaces.Slot("Windows.UI.Core.ICoreWindowEventArgs", "put_Handled"), 1) == SOk);
        ReturnFromHandler();

        call = Called();
        h.Check("2. CoreWindow.VisibilityChanged follows", call.Target == visibility.Invoke && call.Sender == coreWindow);
        h.Check("   with Visible false", (Ret(call.Args, WinRtSystemInterfaces.Slot("Windows.UI.Core.IVisibilityChangedEventArgs", "get_Visible")) & 0xFF) == 0);
        h.Check("   and CoreWindow.Visible already answers false", !rt.WindowVisible);
        ReturnFromHandler();

        call = Called();
        h.Check("3. CoreApplication.Suspending comes last", call.Target == suspending.Invoke && call.Handler == suspending.Handler);
        long operation = Ret(call.Args, WinRtSystemInterfaces.Slot("Windows.ApplicationModel.ISuspendingEventArgs", "get_SuspendingOperation"));
        h.Check("   SuspendingEventArgs.SuspendingOperation is an object", operation != 0);
        long deadlineOut = _h.Buffer(8);
        Invoke(operation, WinRtSystemInterfaces.Slot("Windows.ApplicationModel.ISuspendingOperation", "get_Deadline"), deadlineOut);
        DateTime deadline = DateTime.FromFileTimeUtc((long)_h.U64(deadlineOut));
        h.Check("   Deadline is in the future (a Windows.Foundation.DateTime)", deadline > DateTime.UtcNow, deadline.ToString("O"));
        long deferral = Ret(operation, WinRtSystemInterfaces.Slot("Windows.ApplicationModel.ISuspendingOperation", "GetDeferral"));
        h.Check("   GetDeferral answers a deferral", deferral != 0);
        ReturnFromHandler();
        h.Check("the main loop continues once the handlers have run", continued);
        h.Check("with a deferral outstanding the app is not yet suspended", !rt.IsSuspended);

        rt.InjectVisibility(true);
        h.Check("a return to the foreground is held until the suspension finishes", !Deliver(() => { }) && !rt.WindowVisible);
        h.Check("SuspendingDeferral.Complete succeeds", Invoke(deferral, WinRtSystemInterfaces.Slot("Windows.ApplicationModel.ISuspendingDeferral", "Complete")) == SOk);

        // ---- back to the foreground: Resuming, VisibilityChanged(true), Activated(CodeActivated) ----
        continued = false;
        h.Check("once completed the app is suspended and the held resume is delivered", Deliver(() => continued = true) && rt.AppLifecycleLog.Contains("suspended: every deferral completed"));
        call = Called();
        h.Check("1. CoreApplication.Resuming is raised first", call.Target == resuming.Invoke && call.Handler == resuming.Handler);
        ReturnFromHandler();
        call = Called();
        h.Check("2. VisibilityChanged(true)", call.Target == visibility.Invoke && (Ret(call.Args, WinRtSystemInterfaces.Slot("Windows.UI.Core.IVisibilityChangedEventArgs", "get_Visible")) & 0xFF) == 1);
        ReturnFromHandler();
        call = Called();
        h.Check("3. Activated(CodeActivated)", call.Target == activated.Invoke &&
            Ret(call.Args, WinRtSystemInterfaces.Slot("Windows.UI.Core.IWindowActivatedEventArgs", "get_WindowActivationState")) == 0);
        ReturnFromHandler();
        h.Check("the app is running and visible again", continued && rt.WindowVisible && !rt.IsSuspended);

        // ---- a deferral never completed: the deadline ends the suspension ----
        rt.InjectVisibility(false);
        Deliver(() => { });
        ReturnFromHandler();   // Activated
        ReturnFromHandler();   // VisibilityChanged
        call = Called();
        Ret(Ret(call.Args, WinRtSystemInterfaces.Slot("Windows.ApplicationModel.ISuspendingEventArgs", "get_SuspendingOperation")),
            WinRtSystemInterfaces.Slot("Windows.ApplicationModel.ISuspendingOperation", "GetDeferral"));
        ReturnFromHandler();   // Suspending
        h.Check("an uncompleted deferral keeps the app suspending", !Deliver(() => { }) && !rt.IsSuspended);
        typeof(WinRtRuntime).GetField("_suspendDeadline", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(rt, DateTime.UtcNow.AddSeconds(-1));
        Deliver(() => { });
        h.Check("past the deadline the app is suspended anyway, and that is recorded",
            rt.IsSuspended && rt.AppLifecycleLog.Any(l => l.StartsWith("suspended: deadline passed", StringComparison.Ordinal)));

        // ---- an unsubscribed handler is not raised ----
        int remove = WinRtSystemInterfaces.Slot("Windows.UI.Core.ICoreWindow", "remove_VisibilityChanged");
        ulong vt = _h.U64(visibilityToken);
        h.Check("remove_VisibilityChanged with its token succeeds", Invoke(coreWindow, remove, 0, (long)(vt & 0xFFFFFFFF), (long)(vt >> 32)) == SOk);
        rt.InjectVisibility(true);
        Deliver(() => { });
        ReturnFromHandler();   // Resuming
        call = Called();
        h.Check("after removal VisibilityChanged is skipped: Activated follows Resuming", call.Target == activated.Invoke);
        ReturnFromHandler();
        h.Check("the lifecycle log names every step", rt.AppLifecycleLog.Count(l => l.Contains("CoreApplication::Suspending", StringComparison.Ordinal)) == 2, string.Join(" | ", rt.AppLifecycleLog));
    }

    // -------------------------------------------------------------------------------------------
    // Calling WinRT the way guest code does: through the vtable slot, with the trap's handler.
    // -------------------------------------------------------------------------------------------

    private long Invoke(long self, int slot, params long[] words)
    {
        long function = E.ReadUInt32(E.ReadUInt32(self) + (slot * 4)) & ~1L;
        object trap = ((IDictionary)Traps.GetValue(E)!)[function] ?? throw new InvalidOperationException($"slot {slot} of 0x{self:X} is not a trap");
        Action handler = (Action)trap.GetType().GetProperty("Handler")!.GetValue(trap)!;

        int[] registers = [Harness.R0, Harness.R1, Harness.R2, Harness.R3];
        E.WriteRegister(Harness.R0, self);
        for (int i = 0; i < words.Length && i < 3; i++)
        {
            E.WriteRegister(registers[i + 1], words[i]);
        }

        for (int i = 3; i < words.Length; i++)
        {
            E.WriteUInt32(_h.Stack + ((i - 3) * 4), (uint)words[i]);
        }

        E.WriteRegister(Harness.SP, _h.Stack);
        E.WriteRegister(10, 0x1001);   // lr: somewhere to "return" to
        handler();
        return E.ReadRegister(Harness.R0) & 0xFFFFFFFFL;
    }

    private long Ret(long self, int slot, params long[] words)
    {
        long result = _h.Buffer(8);
        return Invoke(self, slot, [.. words, result]) == SOk ? E.ReadUInt32(result) : 0;
    }

    private long ResultBytes(long operation, int size)
    {
        long result = _h.Buffer(Math.Max(size, 8));
        Invoke(operation, 8, result);
        return result;
    }

    private long Factory(string className, string iface) => E.WinRt.GetActivationFactory(className, WinRtSystemInterfaces.Find(iface)!.Iid)!.Value;

    private long QI(long self, string iface)
    {
        long iid = _h.Buffer(16);
        E.WriteMemory(iid, WinRtSystemInterfaces.Find(iface)!.Iid.ToByteArray());
        long result = _h.Buffer(8);
        return Invoke(self, 0, iid, result) == SOk ? E.ReadUInt32(result) : 0;
    }

    private long HS(string text)
    {
        long result = _h.Buffer(8);
        _h.Call("WindowsCreateString", _h.Wide(text), (long)text.Length, result);
        return E.ReadUInt32(result);
    }

    private string Str(long hstring)
    {
        if (hstring == 0)
        {
            return string.Empty;
        }

        long length = _h.Buffer(8);
        long chars = _h.Call("WindowsGetStringRawBuffer", hstring, length);
        return System.Text.Encoding.Unicode.GetString(E.ReadMemory(chars, (int)E.ReadUInt32(length) * 2));
    }
}

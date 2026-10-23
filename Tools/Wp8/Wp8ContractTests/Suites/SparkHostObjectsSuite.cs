using System.Collections;
using System.Reflection;
using System.Text;
using WPR.Engine.Launchers;
using WPR.Wp8Native;
using WPR.XboxLive;

namespace Wp8ContractTests.Suites;

/// <summary>
/// The Spark2 host objects (SparkHostObjects.cs): <c>SparkApp.CSharpFunctions</c> and
/// <c>LuaMobileSDK.ICallback</c>, which Assassin's Creed Pirates' managed shell handed its engine.
/// IUnknown/IInspectable behaviour, every member's result and out-parameter through its real vtable
/// slot, the invalid-argument answers, and the layout against the package winmds when they are found
/// (<c>WPR_SPARK_PACKAGE</c>, a folder holding SparkApp.winmd and LuaMobileSDK.winmd).
/// </summary>
public sealed class SparkHostObjectsSuite : IContractSuite
{
    public string Name => "spark host objects";

    private const long SOk = 0;
    private const long ENoInterface = 0x80004002;
    private const long EPointer = 0x80004003;

    private static readonly Guid IidUnknown = new("00000000-0000-0000-c000-000000000046");
    private static readonly Guid IidInspectable = new("af86e2e0-b12d-4c6a-9c5a-d7aa65101e90");
    private static readonly Guid IidAgile = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");
    private static readonly Guid IidFunctions = new("427c39ff-d636-3db5-a84d-973d60db98bb");
    private static readonly Guid IidCallback = new("c7bb211b-e6f6-3a85-a4c7-5477d3903cc2");

    // CSharpFunctions slots, in SparkApp.winmd's order from 6.
    private const int MicrophoneCreate = 6, MicrophoneDestroy = 7, MicrophoneStartRecording = 8, MicrophoneStopRecording = 9,
        MicrophoneGetDecibel = 10, GetMediaPlayerState = 11, GameHasControlMediaPlayer = 12, StopMediaPlayer = 13,
        PauseMediaPlayer = 14, PlayMediaPlayer = 15, NavigateToUrl = 16, ShowPopup = 17, GetResponse = 18,
        ResetResponse = 19, IsInternetAvailable = 20;

    // ICallback slots.
    private const int StartNewSession = 6, LogEvent = 7, EndTimedEvent = 8, SetAge = 9, SetGender = 10, SetLocation = 11, SetUseHttps = 12;

    private static readonly FieldInfo Traps = typeof(ArmEmulator).GetField("_traps", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private Harness _h = null!;

    private ArmEmulator E => _h.E;

    public void Run(Harness h)
    {
        _h = h;
        long functions = Create("CreateCSharpFunctions");
        long callback = Create("CreateLuaMobileCallback");
        h.Check("CreateCSharpFunctions answers an object", functions != 0);
        h.Check("CreateLuaMobileCallback answers an object", callback != 0);

        Unknown(functions, IidFunctions, IidCallback, "SparkApp.CSharpFunctions", 15);
        Unknown(callback, IidCallback, IidFunctions, "LuaMobileSDK.ICallback", 7);
        Layout(functions, callback);
        Functions(functions);
        Popups();
        Navigate(functions);
        Callback(callback);
        Metadata();
        h.Check("no slot drift after creating both objects", E.WinRt.SlotDrift.Count == 0, string.Join("; ", E.WinRt.SlotDrift.Take(3)));
    }

    private long Create(string method)
        => (long)typeof(WinRtRuntime).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(E.WinRt, null)!;

    // -------------------------------------------------------------------------------------------
    // IUnknown / IInspectable
    // -------------------------------------------------------------------------------------------

    private void Unknown(long self, Guid own, Guid other, string className, int members)
    {
        string n = className.Split('.')[^1];
        _h.Check($"{n}: QI(own IID) is the object", QI(self, own) == self);
        _h.Check($"{n}: QI(IUnknown) is the object", QI(self, IidUnknown) == self);
        _h.Check($"{n}: QI(IInspectable) is the object", QI(self, IidInspectable) == self);
        _h.Check($"{n}: QI(IAgileObject) is the object (a CCW is agile)", QI(self, IidAgile) == self);

        long result = Out(4);
        E.WriteUInt32(result, 0xDEADBEEF);
        _h.Check($"{n}: QI(the other Spark interface) is E_NOINTERFACE", Invoke(self, 0, IidPointer(other), result) == ENoInterface);
        _h.Check($"{n}: ... and nulls the out-pointer", E.ReadUInt32(result) == 0);
        _h.Check($"{n}: QI with a null out-pointer is E_POINTER", Invoke(self, 0, IidPointer(own), 0) == EPointer);
        _h.Check($"{n}: QI with a null IID is E_POINTER", Invoke(self, 0, 0, Out(4)) == EPointer);

        long before = Invoke(self, 1);
        _h.Check($"{n}: AddRef answers the new count", Invoke(self, 1) == before + 1, $"{before}");
        _h.Check($"{n}: Release answers the new count", Invoke(self, 2) == before && Invoke(self, 2) == before - 1);

        long count = Out(4), iids = Out(4);
        _h.Check($"{n}: GetIids is S_OK", Invoke(self, 3, count, iids) == SOk);
        _h.Check($"{n}: GetIids lists exactly the interface",
            E.ReadUInt32(count) == 1 && new Guid(E.ReadMemory(E.ReadUInt32(iids), 16)) == own);
        long name = Out(4);
        _h.Check($"{n}: GetRuntimeClassName is S_OK", Invoke(self, 4, name) == SOk);
        _h.Check($"{n}: GetRuntimeClassName is {className}", Str(E.ReadUInt32(name)) == className, Str(E.ReadUInt32(name)));
        long trust = Out(4);
        E.WriteUInt32(trust, 7);
        _h.Check($"{n}: GetTrustLevel is BaseTrust", Invoke(self, 5, trust) == SOk && E.ReadUInt32(trust) == 0);

        // A pointer that is not one of these objects, but carries the same vtable: members refuse it.
        long impostor = Out(8);
        E.WriteUInt32(impostor, E.ReadUInt32(self));
        _h.Check($"{n}: a member called on an untracked pointer is E_POINTER", Invoke(impostor, 6 + members - 1, Out(8)) == EPointer);
    }

    private void Layout(long functions, long callback)
    {
        string[] expected =
        [
            "MicrophoneCreate", "MicrophoneDestroy", "MicrophoneStartRecording", "MicrophoneStopRecording", "MicrophoneGetDecibel",
            "GetMediaPlayerState", "GameHasControlMediaPlayer", "StopMediaPlayer", "PauseMediaPlayer", "PlayMediaPlayer",
            "NavigateToUrl", "ShowPopup", "GetResponse", "ResetResponse", "IsInternetAvailable",
        ];
        bool all = expected.Select((m, i) => SlotName(functions, 6 + i).EndsWith("::" + m, StringComparison.Ordinal)).All(x => x);
        _h.Check("CSharpFunctions: slots 6-20 are the winmd's members in order", all, string.Join(", ", Enumerable.Range(6, 15).Select(s => SlotName(functions, s).Split("::")[^1])));
        string[] lua = ["StartNewSession", "LogEvent", "EndTimedEvent", "SetAge", "SetGender", "SetLocation", "SetUseHttps"];
        _h.Check("ICallback: slots 6-12 are the winmd's members in order",
            lua.Select((m, i) => SlotName(callback, 6 + i).EndsWith("::" + m, StringComparison.Ordinal)).All(x => x));
    }

    // -------------------------------------------------------------------------------------------
    // CSharpFunctions
    // -------------------------------------------------------------------------------------------

    private void Functions(long f)
    {
        bool micOk = new[] { MicrophoneCreate, MicrophoneStartRecording, MicrophoneStopRecording, MicrophoneDestroy }.All(s => Invoke(f, s) == SOk);
        _h.Check("microphone members all succeed with no microphone", micOk);

        long level = Out(8);
        E.WriteMemory(level, BitConverter.GetBytes(-1.0));
        _h.Check("MicrophoneGetDecibel: S_OK", Invoke(f, MicrophoneGetDecibel, level) == SOk);
        _h.Check("MicrophoneGetDecibel: 0 dB (no buffer ever arrives)", BitConverter.ToDouble(E.ReadMemory(level, 8)) == 0.0);
        _h.Check("MicrophoneGetDecibel: null out-pointer is E_POINTER", Invoke(f, MicrophoneGetDecibel, 0) == EPointer);

        _h.Check("GetMediaPlayerState: Stopped", Str(Ret(f, GetMediaPlayerState)) == "Stopped");
        _h.Check("GetMediaPlayerState: null out-pointer is E_POINTER", Invoke(f, GetMediaPlayerState, 0) == EPointer);

        long control = Out(4);
        E.WriteMemory(control, [0, 0xAA, 0xAA, 0xAA]);
        _h.Check("GameHasControlMediaPlayer: S_OK, true", Invoke(f, GameHasControlMediaPlayer, control) == SOk && E.ReadMemory(control, 1)[0] == 1);
        _h.Check("GameHasControlMediaPlayer: writes one byte (a WinRT Boolean)", E.ReadMemory(control + 1, 3).All(b => b == 0xAA));
        _h.Check("GameHasControlMediaPlayer: null out-pointer is E_POINTER", Invoke(f, GameHasControlMediaPlayer, 0) == EPointer);

        _h.Check("Stop/Pause/PlayMediaPlayer succeed", Invoke(f, StopMediaPlayer) == SOk && Invoke(f, PauseMediaPlayer) == SOk && Invoke(f, PlayMediaPlayer) == SOk);
        _h.Check("... and the player stays Stopped (nothing queued on it)", Str(Ret(f, GetMediaPlayerState)) == "Stopped");

        long online = Out(4);
        E.WriteMemory(online, [0xAA]);
        _h.Check("IsInternetAvailable: S_OK, false (this runtime is offline)", Invoke(f, IsInternetAvailable, online) == SOk && E.ReadMemory(online, 1)[0] == 0);
        IXboxLiveHost? previousHost = E.WinRt.XboxHost;
        E.WinRt.XboxHost = new OnlineHost();
        E.WriteMemory(online, [0xAA]);
        _h.Check("IsInternetAvailable: true once an Xbox Live host serves the game's online menus",
            Invoke(f, IsInternetAvailable, online) == SOk && E.ReadMemory(online, 1)[0] == 1);
        E.WinRt.XboxHost = previousHost;
        _h.Check("IsInternetAvailable: null out-pointer is E_POINTER", Invoke(f, IsInternetAvailable, 0) == EPointer);

        _h.Check("GetResponse: 0 before any popup", Ret(f, GetResponse) == 0);
        _h.Check("GetResponse: null out-pointer is E_POINTER", Invoke(f, GetResponse, 0) == EPointer);
        _h.Check("calls are counted", E.WinRt.SparkHostCalls.GetValueOrDefault("CSharpFunctions::GetMediaPlayerState") == 3);
    }

    /// <summary>ShowPopup(title r1, message r2, cancel r3, button1, button2, five bools on the stack).</summary>
    private long Popup(long f, string title, string? cancel, string? button1, string? button2, int bold = 0)
        => Invoke(f, ShowPopup, HS(title), HS("message"), HS(cancel ?? "empty"), HS(button1 ?? "empty"), HS(button2 ?? "empty"), bold, bold, bold, bold, bold);

    private void Popups()
    {
        long f = Create("CreateCSharpFunctions");
        _h.Check("ShowPopup: S_OK", Popup(f, "Quit?", "No", "Yes", null, 1) == SOk);
        _h.Check("ShowPopup: answered with the first button (cancel = 1)", Ret(f, GetResponse) == 1);
        _h.Check("ShowPopup: the popup is logged", E.WinRt.SystemClassLog.Any(l => l.StartsWith("[spark] ShowPopup \"Quit?\"", StringComparison.Ordinal)), E.WinRt.SystemClassLog.LastOrDefault() ?? "");
        _h.Check("ResetResponse: S_OK, and GetResponse is 0 again", Invoke(f, ResetResponse) == SOk && Ret(f, GetResponse) == 0);

        Popup(f, "Rate", null, "Rate now", "Later");
        _h.Check("no cancel button: answered with button1 (2)", Ret(f, GetResponse) == 2);
        Popup(f, "Info", null, null, "OK");
        _h.Check("only button2: answered 3", Ret(f, GetResponse) == 3);
        Invoke(f, ResetResponse);
        Popup(f, "Notice", null, null, null);
        _h.Check("no buttons at all: no answer (0), as on the phone", Ret(f, GetResponse) == 0);

        Invoke(f, ShowPopup, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        _h.Check("null HSTRINGs: S_OK; an empty label is still a button (cancel)", Ret(f, GetResponse) == 1);

        SparkPopup? seen = null;
        E.WinRt.SparkPopupHost = p =>
        {
            seen = p;
            return SparkPopup.ResponseButton2;
        };
        Popup(f, "Buy", "Cancel", "Buy", "Restore");
        _h.Check("SparkPopupHost answers the popup", Ret(f, GetResponse) == 3);
        _h.Check("SparkPopupHost sees the texts, absent buttons as null",
            seen is { Title: "Buy", Message: "message", Cancel: "Cancel", Button1: "Buy", Button2: "Restore" });
        Popup(f, "Two", "Cancel", "OK", null);
        _h.Check("a host answer naming an absent button falls back to the default", Ret(f, GetResponse) == 1);
        E.WinRt.SparkPopupHost = _ => throw new InvalidOperationException();
        Popup(f, "Throw", null, "OK", null);
        _h.Check("a host that throws: the default answer, no fault", Ret(f, GetResponse) == 2);
        E.WinRt.SparkPopupHost = null;

        long other = Create("CreateCSharpFunctions");
        _h.Check("each object has its own response", Ret(other, GetResponse) == 0);
    }

    private void Navigate(long f)
    {
        var opened = new List<string>();
        E.WinRt.UriLauncher = u =>
        {
            opened.Add(u);
            return true;
        };
        _h.Check("NavigateToUrl(https): S_OK, opened unchanged", Invoke(f, NavigateToUrl, HS("https://www.ubisoft.com/a")) == SOk && opened.LastOrDefault() == "https://www.ubisoft.com/a");
        Invoke(f, NavigateToUrl, HS("www.assassinscreed.com"));
        _h.Check("NavigateToUrl(bare host): http:// is prefixed, as the shell does", opened.LastOrDefault() == "http://www.assassinscreed.com");
        Invoke(f, NavigateToUrl, HS("zune:navigate?appid=1"));
        _h.Check("NavigateToUrl(zune:): passed as is to the launcher hook (its policy decides)", opened.LastOrDefault() == "zune:navigate?appid=1");
        int count = opened.Count;
        _h.Check("NavigateToUrl(null HSTRING): S_OK, nothing opened", Invoke(f, NavigateToUrl, 0) == SOk && opened.Count == count);
        E.WinRt.UriLauncher = _ => throw new InvalidOperationException();
        _h.Check("NavigateToUrl: a throwing launcher is contained", Invoke(f, NavigateToUrl, HS("https://x.example/")) == SOk);
        E.WinRt.UriLauncher = null;
        _h.Check("NavigateToUrl with no launcher: S_OK, recorded", Invoke(f, NavigateToUrl, HS("https://y.example/")) == SOk
            && E.WinRt.SystemClassLog.Last().Contains("no launcher", StringComparison.Ordinal));

        // Through the real host wiring: WebLinkPolicy refuses zune: and opens http(s).
        IUriLauncher? previous = LauncherBackend.Uri;
        var recorder = new Recorder();
        LauncherBackend.SetUriLauncher(recorder);
        try
        {
            Wp8HostServices.Attach(E.WinRt);
            Invoke(f, NavigateToUrl, HS("zune:navigate?appid=1"));
            _h.Check("with the host wiring: a zune: link never reaches the platform launcher", recorder.Opened.Count == 0);
            Invoke(f, NavigateToUrl, HS("www.ubisoft.com"));
            _h.Check("with the host wiring: a bare host is opened as http", recorder.Opened.SingleOrDefault()?.AbsoluteUri == "http://www.ubisoft.com/");
        }
        finally
        {
            LauncherBackend.SetUriLauncher(previous);
            E.WinRt.UriLauncher = null;
        }
    }

    private sealed class Recorder : IUriLauncher
    {
        public List<Uri> Opened { get; } = new();

        public bool TryOpen(Uri uri)
        {
            Opened.Add(uri);
            return true;
        }
    }

    // -------------------------------------------------------------------------------------------
    // ICallback
    // -------------------------------------------------------------------------------------------

    private void Callback(long c)
    {
        _h.Check("StartNewSession: S_OK", Invoke(c, StartNewSession, HS("FLURRYKEY")) == SOk);
        _h.Check("StartNewSession: logged, not sent", E.WinRt.SystemClassLog.Any(l => l.Contains("StartNewSession(FLURRYKEY)", StringComparison.Ordinal)));
        _h.Check("LogEvent(id, null map, timed): S_OK", Invoke(c, LogEvent, HS("level_start"), 0, 1) == SOk);
        _h.Check("LogEvent: the timed flag is read from its own word", E.WinRt.SystemClassLog.Any(l => l.Contains("LogEvent(level_start, timed=True)", StringComparison.Ordinal)));
        _h.Check("EndTimedEvent: S_OK", Invoke(c, EndTimedEvent, HS("level_start"), 0) == SOk);
        _h.Check("SetAge / SetGender: S_OK", Invoke(c, SetAge, 30) == SOk && Invoke(c, SetGender, 5) == SOk);
        _h.SetD64(0, 48.85);
        _h.SetD64(1, 2.35);
        _h.SetS32(4, 10f);
        _h.Check("SetLocation (d0, d1, s4): S_OK", Invoke(c, SetLocation) == SOk);
        _h.Check("SetLocation: no location in the log", !E.WinRt.SystemClassLog.Any(l => l.Contains("48.85", StringComparison.Ordinal)));
        _h.Check("SetUseHttps: S_OK", Invoke(c, SetUseHttps) == SOk);
        _h.Check("ICallback calls are counted", E.WinRt.SparkHostCalls.GetValueOrDefault("ICallback::SetUseHttps") == 1);
    }

    // -------------------------------------------------------------------------------------------
    // The package winmds
    // -------------------------------------------------------------------------------------------

    private void Metadata()
    {
        string? folder = Environment.GetEnvironmentVariable("WPR_SPARK_PACKAGE");
        string[] candidates = folder is null ? [] : [folder];
        string? found = candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, "SparkApp.winmd")) && File.Exists(Path.Combine(d, "LuaMobileSDK.winmd")));
        if (found is null)
        {
            Console.WriteLine("      (WPR_SPARK_PACKAGE not set to an AC Pirates package: layout not compared with the winmds)");
            return;
        }

        WinmdReader metadata = WinmdReader.Load(Path.Combine(found, "SparkApp.winmd"), Path.Combine(found, "LuaMobileSDK.winmd"));
        Type rt = typeof(WinRtRuntime);
        MethodInfo compare = rt.GetMethod("CompareSparkLayout", BindingFlags.NonPublic | BindingFlags.Static)!;
        List<string> Compare(string name, Guid iid, string[] members) => (List<string>)compare.Invoke(null, [metadata, name, iid, members])!;
        string[] Members(string field) => (string[])rt.GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        List<string> functions = Compare("SparkApp.CSharpFunctions", IidFunctions, Members("SparkCSharpFunctionsMembers"));
        _h.Check("CSharpFunctions matches SparkApp.winmd (IID, slots, signatures)", functions.Count == 0, string.Join("; ", functions));
        List<string> callback = Compare("LuaMobileSDK.ICallback", IidCallback, Members("LuaMobileCallbackMembers"));
        _h.Check("ICallback matches LuaMobileSDK.winmd", callback.Count == 0, string.Join("; ", callback));

        string[] swapped = Members("SparkCSharpFunctionsMembers").ToArray();
        (swapped[0], swapped[1]) = (swapped[1], swapped[0]);
        _h.Check("a member out of order is reported as drift", Compare("SparkApp.CSharpFunctions", IidFunctions, swapped).Count == 2);
        _h.Check("a wrong IID is reported as drift", Compare("SparkApp.CSharpFunctions", Guid.NewGuid(), Members("SparkCSharpFunctionsMembers")).Count == 1);
    }

    // -------------------------------------------------------------------------------------------
    // Calling through the vtable, as guest code does
    // -------------------------------------------------------------------------------------------

    /// <summary>Calls slot <paramref name="slot"/>: r0 = this, then the words (the fourth onwards on the stack); answers r0.</summary>
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
        E.WriteRegister(10, 0x1001);
        handler();
        return E.ReadRegister(Harness.R0) & 0xFFFFFFFFL;
    }

    private string SlotName(long self, int slot)
    {
        long function = E.ReadUInt32(E.ReadUInt32(self) + (slot * 4)) & ~1L;
        object? trap = ((IDictionary)Traps.GetValue(E)!)[function];
        return trap?.GetType().GetProperty("Name")!.GetValue(trap) as string ?? "";
    }

    /// <summary>A member with an out-pointer last: the word it writes.</summary>
    private long Ret(long self, int slot, params long[] words)
    {
        long result = Out(8);
        return Invoke(self, slot, [.. words, result]) == SOk ? E.ReadUInt32(result) : -1;
    }

    private long QI(long self, Guid iid)
    {
        long result = Out(4);
        return Invoke(self, 0, IidPointer(iid), result) == SOk ? E.ReadUInt32(result) : 0;
    }

    private long IidPointer(Guid iid)
    {
        long p = _h.Buffer(16);
        E.WriteMemory(p, iid.ToByteArray());
        return p;
    }

    private long Out(int size) => _h.Buffer(Math.Max(size, 8));

    private long HS(string text)
    {
        long result = Out(4);
        _h.Call("WindowsCreateString", _h.Wide(text), text.Length, result);
        return E.ReadUInt32(result);
    }

    private string Str(long hstring)
    {
        if (hstring is 0 or -1)
        {
            return string.Empty;
        }

        long length = Out(4);
        long chars = _h.Call("WindowsGetStringRawBuffer", hstring, length);
        return Encoding.Unicode.GetString(E.ReadMemory(chars, (int)E.ReadUInt32(length) * 2));
    }

    /// <summary>The smallest Xbox Live host: its presence is all IsInternetAvailable asks about.</summary>
    private sealed class OnlineHost : IXboxLiveHost
    {
        public string Gamertag => "Tester";
        public IReadOnlyList<XboxAchievement> GetAchievements() => [];
        public void UnlockAchievement(uint achievementId) { }
        public void PostResult(uint leaderboardId, int aggregation, long value) { }
        public XboxLeaderboardPage? ReadLeaderboard(uint leaderboardId, uint skip, uint max, bool titleView) => null;
    }
}

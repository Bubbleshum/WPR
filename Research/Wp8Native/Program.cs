using System.Diagnostics;
using UnicornEngine;
using UnicornEngine.Const;
using WPR.Wp8Native;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        WPR WP8 native probe

          WPR.Wp8Probe <path-to-arm-exe> [instruction-budget]

        Loads an ARMv7 Thumb-2 PE, runs it on an emulated CPU, and reports every
        call it makes across the import boundary. Point it at the executable inside
        an unpacked WP8 XAP, e.g. AngryBirdsRio.exe.
        """);
    return 0;
}

// The file log is the most useful part of the report once the image is really running,
// and also the longest. WPR_FILES raises the twenty entries shown by default.
int FileLogLimit = int.TryParse(Environment.GetEnvironmentVariable("WPR_FILES"), out int fileLimit)
    ? fileLimit
    : 20;

string path = args[0];
long budget = args.Length > 1 ? long.Parse(args[1]) : 5_000_000;

if (!File.Exists(path))
{
    Console.Error.WriteLine($"No such file: {path}");
    return 1;
}

PeImage image = PeImage.Load(path);

Console.WriteLine(Rule("IMAGE"));
Console.WriteLine($"  file          {Path.GetFileName(path)}");
Console.WriteLine($"  machine       0x{image.Machine:X4} {(image.Machine == PeImage.MachineArmNt ? "ARMNT (ARMv7 Thumb-2)" : "UNEXPECTED")}");
Console.WriteLine($"  managed       {(image.IsManaged ? "yes - this has IL, the patcher can handle it" : "no - native code only")}");
Console.WriteLine($"  image base    0x{image.ImageBase:X8}   size 0x{image.SizeOfImage:X}");
Console.WriteLine($"  entry point   0x{image.EntryPoint:X8}   thumb={image.EntryIsThumb}");
Console.WriteLine($"  sections      {image.Sections.Count}");
foreach (PeSection s in image.Sections)
{
    Console.WriteLine($"      {s.Name,-9} 0x{image.ImageBase + s.VirtualAddress:X8}  vsize {s.VirtualSize,9:N0}");
}

Console.WriteLine();
Console.WriteLine(Rule("IMPORT SURFACE"));
foreach (IGrouping<string, ImportedFunction> group in image.Imports
             .GroupBy(i => i.Dll, StringComparer.OrdinalIgnoreCase)
             .OrderByDescending(g => g.Count()))
{
    Console.WriteLine($"  {group.Key,-45} {group.Count(),4}");
}

Console.WriteLine($"  {"TOTAL",-45} {image.Imports.Count,4} functions across " +
                  $"{image.Imports.Select(i => i.Dll).Distinct(StringComparer.OrdinalIgnoreCase).Count()} DLLs");

ArmEmulator emulator;
try
{
    // Block counting costs a managed callback per basic block, so it is worth its
    // keep on a short diagnostic run and not on a long one.
    emulator = new ArmEmulator(
        image,
        imageDirectory: Path.GetDirectoryName(Path.GetFullPath(path))!,
        collectBlockStats: budget <= 10_000_000);
}
catch (DllNotFoundException)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("Static analysis above is complete, but the CPU could not start:");
    Console.Error.WriteLine("the native unicorn library is missing.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("UnicornEngine.Unicorn 2.1.3 ships no win-x64 runtime. See README.md");
    Console.Error.WriteLine("for the two ways round it (run under WSL, or supply unicorn.dll).");
    return 2;
}

Console.WriteLine();
Console.WriteLine(Rule("EXECUTION"));
List<string> damagedBefore = emulator.VerifyTrapPage();
Console.WriteLine($"  trap page before the run: {(damagedBefore.Count == 0 ? "intact" : $"{damagedBefore.Count} BAD")}");
foreach (string bad in damagedBefore.Take(4))
{
    Console.WriteLine($"      {bad}");
}

Console.WriteLine($"  trapped {emulator.TrappedImportCount} imports, budget {budget:N0} instructions");
foreach (string cell in emulator.DataImportCells)
{
    Console.WriteLine($"  data import   {cell}");
}

Console.WriteLine();

// The two blocks in the loader thread's failing path: the PPL continuation handle, and
// the shared_ptr control block its destructor trips over.
if (Environment.GetEnvironmentVariable("WPR_WATCH") == "1")
{
    emulator.WatchAllocationsFrom(0x004A24D2, 0x004A35DA);
}

// WPR_WATCH_ALLOC=0x0045539E watches every block a given instruction hands out, and records
// the call chain that asked for each.
if (Environment.GetEnvironmentVariable("WPR_WATCH_ALLOC") is { Length: > 0 } from)
{
    emulator.WatchAllocationsFrom(Convert.ToInt64(from, 16));
}

// WPR_WATCH_ADDR=0x502FF9B0:12 logs every write into that range, with the instruction
// that made it. For a fixed address rather than an allocation, which is what a container
// living in a stack local needs.
if (Environment.GetEnvironmentVariable("WPR_WATCH_ADDR") is { Length: > 0 } spec)
{
    string[] parts = spec.Split(':');
    emulator.WatchWrites(
        Convert.ToInt64(parts[0], 16),
        parts.Length > 1 ? long.Parse(parts[1]) : 16,
        "requested range");
}

// WPR_TRACE=0x00534E3A,0x00462494 logs the register file every time those instructions run.
if (Environment.GetEnvironmentVariable("WPR_TRACE") is { Length: > 0 } trace)
{
    foreach (string point in trace.Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        emulator.TraceAt(Convert.ToInt64(point.Trim(), 16), "trace");
    }
}

// WPR_LUATRACE=0x00471CE4 reads the Lua call stack whenever that address runs, taking r0 as
// the lua_State. For this title that address is luaL_argerror.
if (Environment.GetEnvironmentVariable("WPR_LUATRACE") is { Length: > 0 } luaTrace)
{
    emulator.LuaTraceAt(Convert.ToInt64(luaTrace.Trim(), 16));
}

// WPR_SCREENSHOT=path[:frame[+every]] rasterises presented frames and writes them as PNGs.
// With +every the name gains a six-digit frame number and the run produces a contact sheet
// rather than one picture, which is the difference between one guess per run and twenty.
if (Environment.GetEnvironmentVariable("WPR_SCREENSHOT") is { Length: > 0 } shot)
{
    int colon = shot.LastIndexOf(':');
    string tail = colon > 1 ? shot[(colon + 1)..] : string.Empty;
    string[] parts = tail.Split('+');

    bool hasFrame = parts.Length >= 1 && int.TryParse(parts[0], out int wantFrame) && wantFrame >= 0;
    emulator.Direct3D.ScreenshotPath = hasFrame ? shot[..colon] : shot;
    emulator.Direct3D.ScreenshotFrame = hasFrame ? int.Parse(parts[0]) : 1;

    if (hasFrame && parts.Length == 2 && int.TryParse(parts[1], out int every) && every > 0)
    {
        emulator.Direct3D.ScreenshotEvery = every;
    }
}

Stopwatch clock = Stopwatch.StartNew();
Stopwatch runClock = Stopwatch.StartNew();
string? fault = emulator.RunEntryPoint(budget);
runClock.Stop();
clock.Stop();

Console.WriteLine($"  stopped       {fault ?? emulator.StopReason ?? "instruction budget exhausted"}");
List<string> damagedAfter = emulator.VerifyTrapPage();
Console.WriteLine($"  trap page     {(damagedAfter.Count == 0 ? "still intact after the run" : $"{damagedAfter.Count} slots DAMAGED")}");
foreach (string bad in damagedAfter.Take(4))
{
    Console.WriteLine($"      {bad}");
}

Console.WriteLine($"  final PC      0x{emulator.ReadRegister(UnicornEngine.Const.Arm.UC_ARM_REG_PC):X8}");
Console.WriteLine(emulator.BlockStatsCollected
    ? $"  blocks        {emulator.BlocksExecuted:N0} entries / {emulator.CodeBytesExecuted:N0} bytes of code"
    : "  blocks        not counted (block hook off above a 10M budget)");
Console.WriteLine($"  lazy pages    {emulator.LazyPagesMapped}");
Console.WriteLine($"  cpu           {emulator.Cpu.Capabilities.Name}");
{
    long finalPc = emulator.ReadRegister(Arm.UC_ARM_REG_PC);
    long r3 = emulator.ReadRegister(Arm.UC_ARM_REG_R3);
    Console.WriteLine($"  trap at pc    {emulator.TrapNameAt(finalPc) ?? "(not a trap slot)"}; trap at r3: {emulator.TrapNameAt(r3) ?? "(not a trap slot)"}; slots allocated: {emulator.TrapSlotsAllocated}");
}
Console.WriteLine($"  final regs    r0={emulator.ReadRegister(Arm.UC_ARM_REG_R0):X8} r1={emulator.ReadRegister(Arm.UC_ARM_REG_R1):X8} " +
                  $"r2={emulator.ReadRegister(Arm.UC_ARM_REG_R2):X8} r3={emulator.ReadRegister(Arm.UC_ARM_REG_R3):X8} " +
                  $"r12={emulator.ReadRegister(Arm.UC_ARM_REG_R12):X8} sp={emulator.ReadRegister(Arm.UC_ARM_REG_SP):X8} " +
                  $"lr={emulator.ReadRegister(Arm.UC_ARM_REG_LR):X8} cpsr={emulator.ReadRegister(Arm.UC_ARM_REG_CPSR):X8}");
Console.WriteLine($"  main loop     {emulator.ProcessEventsCalls} call(s) to CoreDispatcher::ProcessEvents");
Console.WriteLine(emulator.GuestStoresCounted
    ? $"  guest stores  {emulator.GuestStores:N0} into the heap and stack"
    : "  guest stores  not counted (WPR_STORES=1 counts them, at the cost of a hook per store)");
Console.WriteLine($"  graphics      {emulator.Direct3D.Summary()}");
if (emulator.Direct3D.ScreenshotSummary is not null)
{
    Console.WriteLine($"  screenshot    {emulator.Direct3D.ScreenshotSummary}");
    foreach (string note in emulator.Direct3D.Frame.Notes)
    {
        Console.WriteLine($"                {note}");
    }
}
Console.WriteLine($"  audio         {emulator.XAudio2.Summary()}");
foreach (string line in emulator.WinRt.AsyncCompleted)
{
    Console.WriteLine($"  async         {line}");
}

foreach (string line in emulator.Sync.ConcurrencyWaits.Take(10))
{
    Console.WriteLine($"  concrt        {line}");
}

foreach (string line in emulator.ConstructedExceptions)
{
    Console.WriteLine($"  exception     {line}");
}

Console.WriteLine($"  sync          {emulator.Sync.Summary()}");
foreach (string line in emulator.Sync.Log.Take(8))
{
    Console.WriteLine($"  sync          {line}");
}

Console.WriteLine($"  input         {emulator.WinRt.SubscriptionSummary()}");
foreach (string line in emulator.InputDelivered)
{
    Console.WriteLine($"  input         {line}");
}
if (emulator.UndeliveredThrows.Count > 0)
{
    Console.WriteLine($"  UNDELIVERED   {emulator.UndeliveredThrows.Count} throw(s) the runtime was asked for and did not deliver;");
    Console.WriteLine("                each one let the caller continue with a value it should never have seen");
    foreach (IGrouping<string, string> group in emulator.UndeliveredThrows
                 .GroupBy(t => t, StringComparer.Ordinal))
    {
        Console.WriteLine($"    {group.Count(),4}  {group.Key}");
    }
}

if (emulator.ThreadDeaths.Count > 0)
{
    Console.WriteLine($"  THREADS DIED  {emulator.ThreadDeaths.Count} background callback(s) were abandoned;");
    Console.WriteLine("                the run carried on without them, as a real device would");
    foreach (string death in emulator.ThreadDeaths)
    {
        Console.WriteLine($"    {death}");
    }
}

// Only the one that ended the run. A contained null call is already reported above as a
// thread death, with the same registers.
if (emulator.TrapPageWrite is not null)
{
    Console.WriteLine($"  TRAP WRITE    {emulator.TrapPageWrite}");
}

if (emulator.Overflows.Count > 0)
{
    Console.WriteLine($"  OVERFLOWED    {emulator.Overflows.Count} host write(s) ran past the end of their block:");
    foreach (string line in emulator.Overflows)
    {
        Console.WriteLine($"    {line}");
    }
}

if (emulator.HostFailure is not null)
{
    Console.WriteLine($"  HOST FAILED   {emulator.HostFailure}");
}

if (emulator.Runaway is not null)
{
    Console.WriteLine($"  RUNAWAY       {emulator.Runaway}");
}

if (emulator.HeapExecution is not null)
{
    Console.WriteLine($"  RAN OFF       {emulator.HeapExecution}");
}

if (emulator.UncontainedNullCall is not null)
{
    Console.WriteLine($"  NULL CALL     {emulator.UncontainedNullCall}");
    Console.WriteLine($"                last trap entered: {emulator.LastTrap ?? "(none)"}");
}

if (emulator.RejectedWrites.Count > 0)
{
    Console.WriteLine($"  bad writes    {emulator.RejectedWrites.Count} refused:");
    foreach (string rejected in emulator.RejectedWrites.Take(5))
    {
        Console.WriteLine($"                {rejected}");
    }
}

if (emulator.NullDataAccesses > 0)
{
    Console.WriteLine($"  null reads    {emulator.NullDataAccesses} (tolerated, zero-filled)");
}

if (emulator.Files.Log.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"  file access ({emulator.Files.OpenedSuccessfully} opened, {emulator.Files.OpenFailed} failed):");
    foreach (string entry in emulator.Files.Log.Take(FileLogLimit))
    {
        Console.WriteLine($"    {entry}");
    }

    if (emulator.Files.Log.Count > FileLogLimit)
    {
        Console.WriteLine($"    ... and {emulator.Files.Log.Count - FileLogLimit} more");
    }
}

if (emulator.Thrown is ThrownException thrown)
{
    Console.WriteLine();
    Console.WriteLine($"  thrown: {thrown.TypeName}  (object at 0x{thrown.Object:X8})");
    foreach (string alsoCatchableAs in thrown.CatchableTypes.Skip(1))
    {
        Console.WriteLine($"    also catchable as {alsoCatchableAs}");
    }

    if (emulator.CatchCandidates.Count == 0)
    {
        Console.WriteLine("    no matching catch in any unwound frame");
    }

    foreach (CatchCandidate candidate in emulator.CatchCandidates)
    {
        Console.WriteLine($"    -> {candidate}");
    }

    foreach (string transfer in emulator.TransferLog)
    {
        Console.WriteLine($"    {transfer}");
    }
}

foreach (string text in emulator.ThrownText)
{
    Console.WriteLine($"    message: \"{text}\"");
}

if (emulator.ThrowStack.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"  stack at the throw ({emulator.Unwinder.FunctionCount:N0} .pdata entries: " +
                      $"{emulator.Unwinder.PackedCount:N0} packed, {emulator.Unwinder.XdataCount:N0} xdata):");
    foreach (UnwoundFrame frame in emulator.ThrowStack)
    {
        Console.WriteLine($"    {frame}");
        if (frame.FunctionRva != 0)
        {
            Console.WriteLine($"        {emulator.ExceptionModel.DescribeFrame(frame)}");
        }
    }
}

if (emulator.RecentBlocks.Count > 0)
{
    // Deduplicate runs of the same address so a tight loop reads as a loop rather than
    // filling the trail with one block repeated.
    List<string> trail = new();
    long previous = -1;
    int repeats = 0;
    foreach (long block in emulator.RecentBlocks)
    {
        if (block == previous)
        {
            repeats++;
            continue;
        }

        if (repeats > 0)
        {
            trail[^1] += $" (x{repeats + 1})";
            repeats = 0;
        }

        trail.Add($"0x{block:X8}");
        previous = block;
    }

    if (repeats > 0)
    {
        trail[^1] += $" (x{repeats + 1})";
    }

    Console.WriteLine($"  last blocks   {string.Join(" -> ", trail.TakeLast(12))}");
}

Console.WriteLine($"  static init   {emulator.StaticInitialisersRun} C++ initialisers ran");
foreach (string formatted in emulator.FormattedStrings.TakeLast(8))
{
    Console.WriteLine($"  formatted     \"{formatted}\"");
}

foreach (string line in emulator.InitialiserLog.Take(12))
{
    Console.WriteLine($"                {line}");
}

Console.WriteLine($"  heap used     {emulator.HeapUsed / 1024 / 1024:N0} MB, " +
                  $"{emulator.BytesReused / 1024 / 1024:N0} MB served from the free list, " +
                  $"{emulator.FreeBlockCount:N0} blocks free" +
                  (emulator.HeapExhausted ? "  <- EXHAUSTED, run stopped early" : ""));
Console.WriteLine($"  elapsed       {clock.Elapsed.TotalSeconds:F2}s");

Console.WriteLine();
Console.WriteLine($"  {emulator.CallOrderTotal:N0} import calls, {emulator.CallCounts.Count} distinct");
Console.WriteLine();
Console.WriteLine("  most called:");
foreach (KeyValuePair<string, int> entry in emulator.CallCounts.OrderByDescending(e => e.Value).Take(28))
{
    Console.WriteLine($"    {entry.Value,8:N0}  {entry.Key}");
}

// The last thing the image was doing, which is the only part of an 800,000-entry call
// order that says anything about a run that stopped making progress.
if (ScriptDumper.Requested is { } dump)
{
    if (dump.Frame <= 0)
    {
        emulator.Scripts.Scan(emulator, dump.Directory);
    }

    Console.WriteLine();
    Console.WriteLine(Rule("SCRIPTS RECOVERED"));
    foreach (string line in emulator.Scripts.Log)
    {
        Console.WriteLine($"  {line}");
    }
}

if (emulator.WinRt.Slots.Any)
{
    Console.WriteLine();
    Console.WriteLine(Rule("VTABLE SLOTS CALLED ON STAND-INS"));
    Console.WriteLine("  slot 0-5 is IInspectable; member N is the Nth member in metadata order");
    foreach (string line in emulator.WinRt.Slots.Report(
                 Environment.GetEnvironmentVariable("WPR_SLOTS")))
    {
        Console.WriteLine($"  {line}");
    }
}

Console.WriteLine();
Console.WriteLine(Rule("WHERE THE TIME WENT"));
foreach (string line in emulator.Samples.Report(emulator.Unwinder))
{
    Console.WriteLine($"  {line}");
}

foreach (string line in emulator.Samples.ArgumentSamples)
{
    Console.WriteLine($"    {line}");
}

if (PcSampler.ArgumentSite == 0)
{
    Console.WriteLine("  (WPR_ARGS=0xADDR shows r0-r3 for the first calls from one site)");
}

if (!PcSampler.SamplingBlocks)
{
    Console.WriteLine("  (WPR_SAMPLE=n also samples one basic block in n, which sees code that");
    Console.WriteLine("   calls nothing - at the cost of a block hook on every block)");
}

Console.WriteLine();
Console.WriteLine("  last calls, oldest first:");
foreach (string call in emulator.CallOrder.TakeLast(24))
{
    Console.WriteLine($"    {call}");
}

Console.WriteLine();
Console.WriteLine("  in order:");
int index = 0;
foreach (string call in emulator.CallOrder.Take(40))
{
    Console.WriteLine($"    {++index,3}. {call}");
}

if (emulator.CallOrderTotal > 40)
{
    Console.WriteLine($"    ... and {emulator.CallOrderTotal - 40:N0} more");
    Console.WriteLine();
    Console.WriteLine("  last 12 before the run ended:");
    long position = emulator.CallOrderTotal - 12;
    foreach (string call in emulator.CallOrder.TakeLast(12))
    {
        Console.WriteLine($"    {++position,3}. {call}");
    }
}

if (emulator.RequestedWinRtClasses.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"  WinRT classes requested ({emulator.RequestedWinRtClasses.Count}):");
    foreach (string requested in emulator.RequestedWinRtClasses)
    {
        Console.WriteLine($"    {requested}");
    }
}

if (emulator.VtableCalls.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"  calls through synthesised vtables ({emulator.VtableCalls.Count}):");
    foreach (string call in emulator.VtableCalls)
    {
        Console.WriteLine($"    {call}");
    }
}

if (emulator.TaskCollectionLog.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("  PPL task machinery:");
    foreach (string entry in emulator.TaskCollectionLog.Take(10))
    {
        Console.WriteLine($"    {entry}");
    }
}

if (emulator.ShapedObjectCalls.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"  calls on shaped stand-in objects ({emulator.ShapedObjectCalls.Count}):");
    foreach (string entry in emulator.ShapedObjectCalls.Take(10))
    {
        Console.WriteLine($"    {entry}");
    }
}

if (emulator.DeferredLog.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("  deferred work (threads stand in as queued callbacks):");
    foreach (string item in emulator.DeferredLog)
    {
        Console.WriteLine($"    {item}");
    }
}

if (emulator.WinRt.Lifecycle.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("  view lifecycle:");
    foreach (string step in emulator.WinRt.Lifecycle)
    {
        Console.WriteLine($"    {step}");
    }
}

if (emulator.WinRt.UnimplementedCalls.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("  reached, but not implemented (this is the to-do list):");
    foreach (string call in emulator.WinRt.UnimplementedCalls)
    {
        Console.WriteLine($"    {call}");
    }
}

foreach (string line in emulator.TraceLog)
{
    Console.WriteLine($"  TRACE {line}");
}

foreach (string stack in emulator.AllocationStacks)
{
    Console.WriteLine($"  ALLOC {stack}");
}

if (emulator.WriteLog.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine(Rule("WRITE WATCH"));
    foreach (string line in emulator.WriteLog)
    {
        Console.WriteLine($"  {line}");
    }
}

if (emulator.ImprovisedClasses.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("  classes answered with a stand-in (each is a real class to write):");
    foreach (string name in emulator.ImprovisedClasses)
    {
        Console.WriteLine($"      {name}");
    }
}

Console.WriteLine();
Console.WriteLine(Rule("DIRECT3D"));
if (emulator.Direct3D.Log.Count == 0)
{
    Console.WriteLine("  never reached");
}

foreach (string line in emulator.Direct3D.Log.Take(40))
{
    Console.WriteLine($"  {line}");
}

if (emulator.Direct3D.ResourcesCreated.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("  resources created:");
    foreach (KeyValuePair<string, int> kind in emulator.Direct3D.ResourcesCreated.OrderByDescending(k => k.Value))
    {
        Console.WriteLine($"      {kind.Value,5}  {kind.Key}");
    }
}

if (emulator.Direct3D.Unimplemented.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("  slots reached with nothing behind them:");
    foreach (string line in emulator.Direct3D.Unimplemented.Take(30))
    {
        Console.WriteLine($"      {line}");
    }
}

if (emulator.Direct3D.UnknownIids.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("  interfaces asked for by an IID this layer does not know:");
    foreach (string line in emulator.Direct3D.UnknownIids)
    {
        Console.WriteLine($"      {line}");
    }
}

Console.WriteLine();
Console.WriteLine(Rule("FILE RESOLUTION"));
foreach (string probePath in new[]
         {
             @"assets\FusionXbox.json",
             @"ASSETS\DATA\SHADERS\DX11\2d-sprite.fxo",   // deliberately wrong case
             "assets/data/scripts/Bank.lua",
             @"C:\Applications\Install\WMAppManifest.xml", // a drive-qualified path
             "assets/data/does-not-exist.bin",
         })
{
    Console.WriteLine($"  {emulator.Files.CheckResolution(probePath)}");
}

Console.WriteLine();
Console.WriteLine(Rule("VTABLE BRIDGE  (emulated -> host)"));
bool bridgeOk = VtableProof.Run(emulator, Console.Out);

Console.WriteLine(Rule("CALLBACK BRIDGE  (host -> emulated -> host)"));
bool callbackOk = VtableProof.RunCallbackProof(emulator, Console.Out);

emulator.Dispose();

Console.WriteLine();
if (ArmEmulator.TimingStubs)
{
    Console.WriteLine(Rule("TIME INSIDE HOST STUBS"));
    foreach (string line in emulator.HostTime(runClock.Elapsed.TotalSeconds))
    {
        Console.WriteLine($"  {line}");
    }

    Console.WriteLine();
}

if (emulator.Direct3D.Phases.Count > 0)
{
    Console.WriteLine(Rule("HOW THE LOAD SPENT ITS TIME"));
    foreach (string phase in emulator.Direct3D.Phases)
    {
        Console.WriteLine($"  {phase}");
    }

    Console.WriteLine();
}

if (emulator.LuaTracebacks.Count > 0)
{
    Console.WriteLine(Rule("LUA CALL STACK AT ERROR"));
    foreach (string line in emulator.LuaTracebacks)
    {
        Console.WriteLine($"  {line}");
    }

    Console.WriteLine();
}

if (emulator.Stubs.ThrowHistory.Count > 0)
{
    Console.WriteLine(Rule("EVERY THROW, IN ORDER"));
    foreach (string line in emulator.Stubs.ThrowHistory)
    {
        Console.WriteLine($"  {line}");
    }

    Console.WriteLine();
}

Console.WriteLine(Rule("THROUGHPUT"));
RunThroughputBenchmark();

// The two bridges are the load-bearing claims, so a failure is a failed run.
return bridgeOk && callbackOk ? 0 : 3;

static void RunThroughputBenchmark()
{
    Measure("no hooks       ", false);
    Measure("one code hook  ", true);
    MeasureBoundary();
    MeasureBinding();
    MeasureWrites("stores, rwx page", false);
    MeasureWrites("stores, rw page", true);
    MeasureLoads();
    MeasureUnrolledStores();
    MeasureMixed("mixed, no hook", false);
    MeasureMixed("mixed, code hook", true);
    MeasureMixedIntr();

    // Off by default: firing an svc with an interrupt hook installed kills the process on this
    // build, which is the whole finding. WPR_SVCPROBE=1 re-runs the proof.
    if (Environment.GetEnvironmentVariable("WPR_SVCPROBE") is { Length: > 0 })
    {
        ProveSvcTrap();
    }

    // The same proof against dynarmic through the wprcpu shim, plus the mixed loop for the
    // like-for-like figure. Needs wprcpu.dll beside the executable.
    if (Environment.GetEnvironmentVariable("WPR_DYNPROBE") is { Length: > 0 })
    {
        ProveDynarmicTrap();
    }

    static void Measure(string label, bool withCodeHook)
    {
        const long code = 0x1000;
        const long count = 40_000_000;

        using Unicorn uc = new(Common.UC_ARCH_ARM, Common.UC_MODE_THUMB);
        uc.MemMap(code, 0x1000, Common.UC_PROT_ALL);

        // loop: subs r0, #1 ; bne loop - a dependent ALU pair, the pessimistic case for a JIT.
        uc.MemWrite(code, [0x01, 0x38, 0xFD, 0xD1]);
        uc.RegWrite(Arm.UC_ARM_REG_R0, 0xFFFFFFFFL);

        // A hook over a range this loop never enters. If that still costs, the cost is not
        // the callback - it is that having any code hook at all stops Unicorn chaining its
        // translation blocks, which is a property of the mechanism and not of the range.
        if (withCodeHook)
        {
            uc.AddCodeHook((_, _, _, _) => { }, null, 0xA0000000, 0xA0001000);
        }

        Stopwatch clock = Stopwatch.StartNew();
        uc.EmuStart(code | 1, 0, 0, count);
        clock.Stop();

        double mips = count / clock.Elapsed.TotalSeconds / 1e6;
        Console.WriteLine($"  {label} {count:N0} Thumb-2 instructions in " +
                          $"{clock.Elapsed.TotalSeconds:F2}s = {mips:F0} MIPS");
    }
}

// How much a single crossing into a host stub costs, which is the number that decides
// whether the answer to "make it faster" is a faster CPU or fewer calls into the host.
// The image makes millions of these, so a microsecond here is minutes on a run.
static void MeasureBoundary()
{
    const long code = 0x1000;
    const long trap = 0xA0000000;
    const long count = 200_000;

    using Unicorn uc = new(Common.UC_ARCH_ARM, Common.UC_MODE_THUMB);
    uc.MemMap(code, 0x1000, Common.UC_PROT_ALL);
    uc.MemMap(trap, 0x1000, Common.UC_PROT_READ | Common.UC_PROT_EXEC);

    // loop: blx r1 ; subs r0, #1 ; bne loop
    uc.MemWrite(code, [0x88, 0x47, 0x01, 0x38, 0xFC, 0xD1]);

    // The trap slot itself: bx lr, the same shape the import traps use.
    uc.MemWrite(trap, [0x70, 0x47]);

    uc.RegWrite(Arm.UC_ARM_REG_R0, count);
    uc.RegWrite(Arm.UC_ARM_REG_R1, trap | 1);

    long crossings = 0;
    uc.AddCodeHook(
        (Unicorn u, long address, int size, object? _) =>
        {
            // What every import trap does before its handler runs: default the tail-call
            // register to the return address.
            crossings++;
            u.RegWrite(Arm.UC_ARM_REG_R12, u.RegRead(Arm.UC_ARM_REG_LR));
        },
        null,
        trap,
        trap + 0x1000);

    Stopwatch clock = Stopwatch.StartNew();
    uc.EmuStart(code | 1, 0, 0, count * 3);
    clock.Stop();

    double each = clock.Elapsed.TotalSeconds / Math.Max(1, crossings) * 1e6;
    Console.WriteLine($"  host crossing   {crossings:N0} trap(s) in {clock.Elapsed.TotalSeconds:F2}s = " +
                      $"{each:F2} us each");
}

// The binding's own overhead, measured separately, because a stub that reads six registers
// and two strings pays this per access and there is no way to tell from the outside.
static void MeasureBinding()
{
    const long data = 0x1000;
    const int count = 200_000;

    using Unicorn uc = new(Common.UC_ARCH_ARM, Common.UC_MODE_THUMB);
    uc.MemMap(data, 0x1000, Common.UC_PROT_ALL);

    Stopwatch clock = Stopwatch.StartNew();
    for (int i = 0; i < count; i++)
    {
        uc.RegRead(Arm.UC_ARM_REG_R0);
    }

    clock.Stop();
    double read = clock.Elapsed.TotalSeconds / count * 1e9;

    byte[] buffer = new byte[4];
    clock.Restart();
    for (int i = 0; i < count; i++)
    {
        uc.MemRead(data, buffer);
    }

    clock.Stop();
    double mem = clock.Elapsed.TotalSeconds / count * 1e9;

    Console.WriteLine($"  binding         RegRead {read:F0} ns, MemRead(4) {mem:F0} ns");
}

// What a memory-write hook costs the code being watched. A write hook fires per store,
// not per watched store - an empty watch list is still a callback for every one - so this
// is the price of having the diagnostic available rather than of using it.
static void MeasureWrites(string label, bool withoutExec)
{
    const long code = 0x1000;
    const long data = 0x2000;
    const long count = 10_000_000;

    using Unicorn uc = new(Common.UC_ARCH_ARM, Common.UC_MODE_THUMB);
    uc.MemMap(code, 0x1000, Common.UC_PROT_ALL);
    uc.MemMap(
        data,
        0x1000,
        withoutExec ? Common.UC_PROT_READ | Common.UC_PROT_WRITE : Common.UC_PROT_ALL);

    // loop: str r2, [r3] ; subs r0, #1 ; bne loop
    uc.MemWrite(code, [0x1A, 0x60, 0x01, 0x38, 0xFC, 0xD1]);
    uc.RegWrite(Arm.UC_ARM_REG_R0, count);
    uc.RegWrite(Arm.UC_ARM_REG_R3, data);


    Stopwatch clock = Stopwatch.StartNew();
    uc.EmuStart(code | 1, 0, 0, count * 3);
    clock.Stop();

    double mips = count * 3 / clock.Elapsed.TotalSeconds / 1e6;
    Console.WriteLine($"  {label,-18} {count:N0} store(s) in {clock.Elapsed.TotalSeconds:F2}s = {mips:F0} MIPS");
}

// Loads, for comparison with stores. If both are slow the cost is the memory path itself
// rather than anything to do with write tracking.
static void MeasureLoads()
{
    const long code = 0x1000;
    const long data = 0x2000;
    const long count = 10_000_000;

    using Unicorn uc = new(Common.UC_ARCH_ARM, Common.UC_MODE_THUMB);
    uc.MemMap(code, 0x1000, Common.UC_PROT_ALL);
    uc.MemMap(data, 0x1000, Common.UC_PROT_ALL);

    // loop: ldr r2, [r3] ; subs r0, #1 ; bne loop
    uc.MemWrite(code, [0x1A, 0x68, 0x01, 0x38, 0xFC, 0xD1]);
    uc.RegWrite(Arm.UC_ARM_REG_R0, count);
    uc.RegWrite(Arm.UC_ARM_REG_R3, data);

    Stopwatch clock = Stopwatch.StartNew();
    uc.EmuStart(code | 1, 0, 0, count * 3);
    clock.Stop();

    double mips = count * 3 / clock.Elapsed.TotalSeconds / 1e6;
    Console.WriteLine($"  loads, no hook     {count:N0} load(s) in {clock.Elapsed.TotalSeconds:F2}s = {mips:F0} MIPS");
}

// A loop shaped like real code - a load, an add, a store, a call and a conditional branch -
// rather than the two-instruction loop the headline figure uses. Real code crosses a basic
// block every few instructions, and a code hook stops Unicorn chaining blocks, so this is
// the measurement that says what having any code hook at all costs the image.
static void MeasureMixed(string label, bool withCodeHook)
{
    const long code = 0x1000;
    const long data = 0x2000;
    const long count = 5_000_000;

    using Unicorn uc = new(Common.UC_ARCH_ARM, Common.UC_MODE_THUMB);
    uc.MemMap(code, 0x1000, Common.UC_PROT_ALL);
    uc.MemMap(data, 0x1000, Common.UC_PROT_ALL);

    // loop: ldr r2,[r3] ; adds r2,#1 ; str r2,[r3] ; blx r4 ; subs r0,#1 ; bne loop
    // leaf: adds r1,#1 ; bx lr
    uc.MemWrite(code,
    [
        0x1A, 0x68, 0x01, 0x32, 0x1A, 0x60, 0xA0, 0x47, 0x01, 0x38, 0xF9, 0xD1, 0xFE, 0xE7, 0x00, 0x00,
        0x01, 0x31, 0x70, 0x47,
    ]);

    uc.RegWrite(Arm.UC_ARM_REG_R0, count);
    uc.RegWrite(Arm.UC_ARM_REG_R3, data);
    uc.RegWrite(Arm.UC_ARM_REG_R4, (code + 16) | 1);

    if (withCodeHook)
    {
        uc.AddCodeHook((_, _, _, _) => { }, null, 0xA0000000, 0xA0001000);
    }

    // Eight instructions round the loop, counting the leaf and its return.
    long instructions = count * 8;

    Stopwatch clock = Stopwatch.StartNew();
    uc.EmuStart(code | 1, 0, 0, instructions);
    clock.Stop();

    double mips = instructions / clock.Elapsed.TotalSeconds / 1e6;
    Console.WriteLine($"  {label,-18} {instructions:N0} instruction(s) in " +
                      $"{clock.Elapsed.TotalSeconds:F2}s = {mips:F0} MIPS");
}

// Eight stores in one basic block, against one store in a three-instruction block. If a
// store costs the same either way it really is the store; if it gets far cheaper when
// unrolled, the cost is per *block* - a store forcing the translation block to end - and the
// number that matters for real code is how often it stores, not how many it makes.
static void MeasureUnrolledStores()
{
    const long code = 0x1000;
    const long data = 0x2000;
    const long iterations = 2_000_000;

    using Unicorn uc = new(Common.UC_ARCH_ARM, Common.UC_MODE_THUMB);
    uc.MemMap(code, 0x1000, Common.UC_PROT_ALL);
    uc.MemMap(data, 0x1000, Common.UC_PROT_ALL);

    // loop: str r2,[r3] x8 ; subs r0,#1 ; bne loop
    uc.MemWrite(code,
    [
        0x1A, 0x60, 0x1A, 0x60, 0x1A, 0x60, 0x1A, 0x60,
        0x1A, 0x60, 0x1A, 0x60, 0x1A, 0x60, 0x1A, 0x60,
        0x01, 0x38, 0xF5, 0xD1,
    ]);

    uc.RegWrite(Arm.UC_ARM_REG_R0, iterations);
    uc.RegWrite(Arm.UC_ARM_REG_R3, data);

    long instructions = iterations * 10;
    long stores = iterations * 8;

    Stopwatch clock = Stopwatch.StartNew();
    uc.EmuStart(code | 1, 0, 0, instructions);
    clock.Stop();

    double mips = instructions / clock.Elapsed.TotalSeconds / 1e6;
    double each = clock.Elapsed.TotalSeconds / stores * 1e9;
    Console.WriteLine($"  stores, unrolled x8 {stores:N0} store(s) in {clock.Elapsed.TotalSeconds:F2}s = " +
                      $"{mips:F0} MIPS, {each:F0} ns each");
}

// The same mixed loop with an *interrupt* hook installed instead of a code hook. An svc-based
// trap would use one of these, and the question this answers is whether it costs what a code
// hook costs: a code hook stops Unicorn chaining translation blocks whether or not it ever
// fires, and if UC_HOOK_INTR does the same then moving the import traps onto svc buys nothing.
static void MeasureMixedIntr()
{
    const long code = 0x1000;
    const long data = 0x2000;
    const long count = 5_000_000;

    using Unicorn uc = new(Common.UC_ARCH_ARM, Common.UC_MODE_THUMB);
    uc.MemMap(code, 0x1000, Common.UC_PROT_ALL);
    uc.MemMap(data, 0x1000, Common.UC_PROT_ALL);

    uc.MemWrite(code,
    [
        0x1A, 0x68, 0x01, 0x32, 0x1A, 0x60, 0xA0, 0x47, 0x01, 0x38, 0xF9, 0xD1, 0xFE, 0xE7, 0x00, 0x00,
        0x01, 0x31, 0x70, 0x47,
    ]);

    uc.RegWrite(Arm.UC_ARM_REG_R0, count);
    uc.RegWrite(Arm.UC_ARM_REG_R3, data);
    uc.RegWrite(Arm.UC_ARM_REG_R4, (code + 16) | 1);

    uc.AddInterruptHook((Unicorn _, int _, object? _) => { }, null, 1, ulong.MaxValue);

    long instructions = count * 8;

    Stopwatch clock = Stopwatch.StartNew();
    uc.EmuStart(code | 1, 0, 0, instructions);
    clock.Stop();

    double mips = instructions / clock.Elapsed.TotalSeconds / 1e6;
    Console.WriteLine($"  mixed, intr hook   {instructions:N0} instruction(s) in " +
                      $"{clock.Elapsed.TotalSeconds:F2}s = {mips:F0} MIPS");
}

// Can an interrupt hook stand in for a trap at all? The mechanism has to do three things:
// fire on svc, report where it happened, and let the handler choose where execution resumes.
// Proving that on twenty instructions is the difference between a two-line fix and a crash
// three million calls into a run with no report.
static void ProveSvcTrap()
{
    const long code = 0x1000;

    using Unicorn uc = new(Common.UC_ARCH_ARM, Common.UC_MODE_THUMB);
    uc.MemMap(code, 0x1000, Common.UC_PROT_ALL);

    // 0: svc #0     - the trap
    // 2: bx r12     - the tail call the slot always ended with
    // 4: movs r0, #7 - where r12 points; proves both halves ran
    // 6: b .        - park
    uc.MemWrite(code, [0x00, 0xDF, 0x60, 0x47, 0x07, 0x20, 0xFE, 0xE7]);
    uc.RegWrite(Arm.UC_ARM_REG_R0, 0);
    uc.RegWrite(Arm.UC_ARM_REG_R1, 0);

    long seenAt = -1;
    long spBefore = uc.RegRead(Arm.UC_ARM_REG_SP);

    uc.AddInterruptHook(
        (Unicorn u, int number, object? _) =>
        {
            seenAt = u.RegRead(Arm.UC_ARM_REG_PC);

            // Deliberately NOT writing the PC: that kills the process outright on this build.
            // The question here is whether execution simply carries on past the svc, which
            // would let a trap slot be svc followed by the bx r12 it already had.
            u.RegWrite(Arm.UC_ARM_REG_R12, code + 4);
        },
        null,
        1,
        ulong.MaxValue);

    string outcome;
    try
    {
        uc.EmuStart(code | 1, 0, 0, 8);
        long r0 = uc.RegRead(Arm.UC_ARM_REG_R0);
        long r1 = uc.RegRead(Arm.UC_ARM_REG_R1);
        long sp = uc.RegRead(Arm.UC_ARM_REG_SP);
        outcome = $"fired at 0x{seenAt:X}, r0={r0} (want 7), r1={r1}, " +
                  $"sp {(sp == spBefore ? "unchanged" : $"CHANGED to 0x{sp:X}")}";
    }
    catch (UnicornEngineException ex)
    {
        outcome = $"threw {ex.Message} (fired at 0x{seenAt:X})";
    }

    Console.WriteLine($"  svc as a trap   {outcome}");
}

// Does the trap mechanism work on dynarmic: a slot of svc #0 ; bx r12, a handler that sets
// r12, and the CPU continuing where the handler said? Then the mixed loop, so the two engines
// are compared on the same instructions in the same process.
static void ProveDynarmicTrap()
{
    const uint code = 0x1000;
    const uint slot = 0xA0000000;

    using var cpu = new DynarmicCpu();
    cpu.Map(code, 0x1000, DynarmicNative.ProtAll);
    cpu.Map(slot, 0x10000, DynarmicNative.ProtRead | DynarmicNative.ProtExec);

    // 0: blx r1       - call the trap slot as an import would
    // 2: movs r0, #7  - runs after the trap returns via bx r12 = lr
    // 4: b .          - park
    cpu.TryWrite(code, [0x88, 0x47, 0x07, 0x20, 0xFE, 0xE7]);
    cpu.TryWrite(slot, DynarmicCpu.TrapSlot);

    uint seenPc = 0;
    int fired = 0;
    cpu.TrapEntered += pc =>
    {
        fired++;
        seenPc = pc;
        cpu[12] = cpu[14];      // r12 = lr: plain return, exactly what EnterTrap defaults to
        cpu[3] = 42;
    };

    cpu[0] = 0;
    cpu[1] = slot | 1;
    int outcome = cpu.Run(code | 1, 40);

    string verdict = fired == 1 && cpu[0] == 7 && cpu[3] == 42 && seenPc == slot + 2 ? "PASS" : "FAIL";
    Console.WriteLine($"  dynarmic trap   {verdict}: fired {fired}x at pc 0x{seenPc:X} (slot 0x{slot:X}), " +
                      $"r0={cpu[0]} (want 7), r3={cpu[3]} (want 42), outcome {outcome}");

    // A store into the trap page must be refused, not land.
    bool refused = false;
    cpu.UnmappedAccess = (access, address, size) =>
    {
        refused = access == DynarmicNative.AccessWrite && address >= slot && address < slot + 0x10000;
        return false;
    };

    // str r0, [r1] with r1 = slot (r0 is 7 from above) ; b .
    cpu.TryWrite(code, [0x08, 0x60, 0xFE, 0xE7]);
    cpu[1] = slot;
    outcome = cpu.Run(code | 1, 10);
    Console.WriteLine($"  trap page write {(refused && outcome == DynarmicNative.OutcomeFault ? "PASS" : "FAIL")}: " +
                      $"refused={refused} outcome={outcome} (want {DynarmicNative.OutcomeFault})");

    // Lazy mapping: a load from a page nobody mapped reaches the handler, which maps it and
    // asks for the access to be retried. The image's whole lazily-mapped address space rests
    // on this one semantic, so it is proved here rather than discovered in a run.
    const uint lazy = 0x30000000;
    int lazyCalls = 0;
    cpu.UnmappedAccess = (access, address, size) =>
    {
        lazyCalls++;
        if (address >= lazy && address < lazy + 0x1000)
        {
            cpu.Map(lazy, 0x1000, DynarmicNative.ProtAll);
            return true;
        }

        return false;
    };

    // ldr r0, [r1] ; adds r0, #5 ; b .    with r1 = lazy page, which reads as zero once mapped
    cpu.TryWrite(code, [0x08, 0x68, 0x05, 0x30, 0xFE, 0xE7]);
    cpu[0] = 99;
    cpu[1] = lazy;
    outcome = cpu.Run(code | 1, 10);
    Console.WriteLine($"  lazy page       {(lazyCalls == 1 && cpu[0] == 5 && cpu.IsMapped(lazy) && outcome == DynarmicNative.OutcomeBudget ? "PASS" : "FAIL")}: " +
                      $"handler called {lazyCalls}x, r0={cpu[0]} (want 5), mapped={cpu.IsMapped(lazy)}, outcome={outcome}");

    // The mixed loop, same bytes as the Unicorn figure above.
    const uint data = 0x2000;
    cpu.UnmappedAccess = null;
    cpu.Map(data, 0x1000, DynarmicNative.ProtAll);
    cpu.TryWrite(code,
    [
        0x1A, 0x68, 0x01, 0x32, 0x1A, 0x60, 0xA0, 0x47, 0x01, 0x38, 0xF9, 0xD1, 0xFE, 0xE7, 0x00, 0x00,
        0x01, 0x31, 0x70, 0x47,
    ]);

    const long count = 5_000_000;
    cpu[0] = (uint)count;
    cpu[3] = data;
    cpu[4] = (code + 16) | 1;
    long instructions = count * 8;

    Stopwatch clock = Stopwatch.StartNew();
    cpu.Run(code | 1, (ulong)instructions);
    clock.Stop();

    double mips = instructions / clock.Elapsed.TotalSeconds / 1e6;
    Console.WriteLine($"  mixed, dynarmic    {instructions:N0} instruction(s) in " +
                      $"{clock.Elapsed.TotalSeconds:F2}s = {mips:F0} MIPS (from C#, through the shim)");
}

// ASCII only: the Windows console default code page mangles box-drawing characters.
static string Rule(string label) => $"-- {label} " + new string('-', Math.Max(0, 58 - label.Length));

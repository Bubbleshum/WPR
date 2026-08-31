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

Stopwatch clock = Stopwatch.StartNew();
string? fault = emulator.RunEntryPoint(budget);
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
Console.WriteLine($"  main loop     {emulator.ProcessEventsCalls} call(s) to CoreDispatcher::ProcessEvents");
Console.WriteLine($"  graphics      {emulator.Direct3D.Summary()}");
Console.WriteLine($"  audio         {emulator.XAudio2.Summary()}");
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
Console.WriteLine($"  {emulator.CallOrder.Count} import calls, {emulator.CallCounts.Count} distinct");
Console.WriteLine();
Console.WriteLine("  most called:");
foreach (KeyValuePair<string, int> entry in emulator.CallCounts.OrderByDescending(e => e.Value).Take(8))
{
    Console.WriteLine($"    {entry.Value,8:N0}  {entry.Key}");
}

Console.WriteLine();
Console.WriteLine("  in order:");
int index = 0;
foreach (string call in emulator.CallOrder.Take(40))
{
    Console.WriteLine($"    {++index,3}. {call}");
}

if (emulator.CallOrder.Count > 40)
{
    Console.WriteLine($"    ... and {emulator.CallOrder.Count - 40} more");
    Console.WriteLine();
    Console.WriteLine("  last 12 before the run ended:");
    int position = emulator.CallOrder.Count - 12;
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
Console.WriteLine(Rule("THROUGHPUT"));
RunThroughputBenchmark();

// The two bridges are the load-bearing claims, so a failure is a failed run.
return bridgeOk && callbackOk ? 0 : 3;

static void RunThroughputBenchmark()
{
    const long code = 0x1000;
    const long count = 40_000_000;

    using Unicorn uc = new(Common.UC_ARCH_ARM, Common.UC_MODE_THUMB);
    uc.MemMap(code, 0x1000, Common.UC_PROT_ALL);

    // loop: subs r0, #1 ; bne loop  - a dependent ALU pair, the pessimistic case for a JIT.
    uc.MemWrite(code, [0x01, 0x38, 0xFD, 0xD1]);
    uc.RegWrite(Arm.UC_ARM_REG_R0, 0xFFFFFFFFL);

    Stopwatch clock = Stopwatch.StartNew();
    uc.EmuStart(code | 1, 0, 0, count);
    clock.Stop();

    double mips = count / clock.Elapsed.TotalSeconds / 1e6;
    Console.WriteLine($"  {count:N0} Thumb-2 instructions in {clock.Elapsed.TotalSeconds:F2}s = {mips:F0} MIPS");
}

// ASCII only: the Windows console default code page mangles box-drawing characters.
static string Rule(string label) => $"-- {label} " + new string('-', Math.Max(0, 58 - label.Length));

using System.Diagnostics;
using System.Globalization;
using WPR.Wp8Native;

// Wp8Run <arm exe> [budget] [sandbox]: the entry point on dynarmic, headless, then one report.
if (args.Length < 1)
{
    Console.Error.WriteLine("usage: Wp8Run <arm exe> [instruction budget] [sandbox folder]");
    return 2;
}

string exe = Path.GetFullPath(args[0]);
long budget = args.Length > 1 ? (long)double.Parse(args[1], CultureInfo.InvariantCulture) : 2_000_000_000L;
string sandbox = args.Length > 2
    ? Path.GetFullPath(args[2])
    : Path.Combine(Path.GetTempPath(), "wp8run", Path.GetFileNameWithoutExtension(exe));

// Games are read-only inputs: everything the title writes goes to a throwaway sandbox.
if (Directory.Exists(sandbox))
{
    Directory.Delete(sandbox, recursive: true);
}

Directory.CreateDirectory(sandbox);
HostStubs.SandboxRoot = sandbox;
Environment.SetEnvironmentVariable("WPR_TAP", "0");

Console.WriteLine($"image   {exe}");
Console.WriteLine($"budget  {budget:N0}");

var emulator = new ArmEmulator(PeImage.Load(exe), Path.GetDirectoryName(exe)!, collectBlockStats: false);
int frames = 0;
var clock = new Stopwatch();
TimeSpan? firstFrame = null;
long firstFrameRetired = 0, firstFrameCalls = 0;
emulator.Direct3D.FrameBuilt += _ =>
{
    if (frames++ == 0)
    {
        firstFrame = clock.Elapsed;
        firstFrameRetired = emulator.Cpu.InstructionsRetired;
        firstFrameCalls = emulator.CallOrderTotal;
    }
};
Console.WriteLine($"cpu     {emulator.Cpu.Capabilities.Name}");
Console.Out.Flush();

clock.Start();
string? fault;
try
{
    fault = emulator.RunEntryPoint(budget);
}
catch (Exception ex)
{
    fault = "host exception: " + ex;
}

clock.Stop();
Console.WriteLine($"outcome {fault ?? "(no fault)"}");
Console.WriteLine($"stop    {emulator.StopReason ?? "(none)"}");
Console.WriteLine($"retired {emulator.Cpu.InstructionsRetired:N0} in {clock.Elapsed.TotalSeconds:F1}s");
Console.WriteLine($"frames  {frames}");
double seconds = Math.Max(clock.Elapsed.TotalSeconds, 1e-9);
Console.WriteLine($"rate    {emulator.Cpu.InstructionsRetired / seconds / 1e6:F1} M instructions/s, {emulator.CallOrderTotal / seconds:N0} host calls/s");
Console.WriteLine(firstFrame is { } ff
    ? $"first   frame after {ff.TotalSeconds:F3}s, {firstFrameRetired:N0} instructions, {firstFrameCalls:N0} host calls"
    : "first   no frame presented");
Console.WriteLine($"modules {string.Join("; ", emulator.Modules.Select(m => m.Name))}");
const int Lr = 10, Pc = 11;   // Unicorn register ids; the Arm const class is internal
Console.WriteLine($"pc=0x{emulator.ReadRegister(Pc):X8} lr=0x{emulator.ReadRegister(Lr):X8} " +
                  string.Join(" ", Enumerable.Range(0, 13).Select(r => $"r{r}=0x{emulator.ReadRegister(66 + r):X8}")));
Console.WriteLine($"lazy pages mapped {emulator.LazyPagesMapped}, null data reads {emulator.NullDataAccesses}");
Console.WriteLine($"imports {emulator.CallOrderTotal:N0} calls");
IReadOnlyList<string> order = emulator.CallOrder;

// A fingerprint of the kept call order (first and last stretches), for before/after comparisons.
string fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
    System.Text.Encoding.UTF8.GetBytes(string.Join('\n', order))))[..16];
Console.WriteLine($"order   {order.Count} kept, sha256 {fingerprint}");
foreach (string call in order.Take(25))
{
    Console.WriteLine("  first: " + call);
}

foreach (string call in order.Skip(Math.Max(25, order.Count - 15)))
{
    Console.WriteLine("  last:  " + call);
}

return 0;

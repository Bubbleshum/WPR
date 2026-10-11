// Runs every contract suite (Suites/*.cs) against one real WP8 image.
//
//   Wp8ContractTests [<arm exe>] [<suite name filter>]
using Wp8ContractTests;

string image = args.Length > 0 && File.Exists(args[0])
    ? args[0]
    : Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA")!, @"WPR\AppData\e94059a2-135c-420e-8e60-bcda5fc3ec30\AngryBirdsRio.exe");
string? filter = args.Length > 1 ? args[1] : args.Length == 1 && !File.Exists(args[0]) ? args[0] : null;

if (!File.Exists(image))
{
    Console.Error.WriteLine($"No WP8 image at {image}. Pass the path of an installed WP8 title's ARM .exe.");
    return 2;
}

string sandbox = Path.Combine(Path.GetTempPath(), "wp8-contract-sandbox");
if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);

var suites = typeof(Harness).Assembly.GetTypes()
    .Where(t => typeof(IContractSuite).IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false })
    .Select(t => (IContractSuite)Activator.CreateInstance(t)!)
    .Where(s => filter is null || s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
    .OrderBy(s => s.Name, StringComparer.Ordinal)
    .ToList();

// A fresh emulator per suite: suites must not see each other's state. A shared one let one suite's
// guest stop (StopReason is sticky) fail a later suite's "and the run carries on" check.
int passed = 0, failed = 0;
foreach (IContractSuite suite in suites)
{
    Console.WriteLine($"== {suite.Name}");
    Harness h = new(image, sandbox);
    try
    {
        suite.Run(h);
    }
    catch (Exception ex)
    {
        h.Check($"{suite.Name} ran to completion", false, ex.GetType().Name + ": " + ex.Message);
    }

    passed += h.Passed;
    failed += h.Failed;
}

Console.WriteLine($"\n{passed} passed, {failed} failed ({suites.Count} suite(s))");
return failed == 0 ? 0 : 1;

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

Harness h = new(image, sandbox);
foreach (IContractSuite suite in suites)
{
    Console.WriteLine($"== {suite.Name}");
    try
    {
        suite.Run(h);
    }
    catch (Exception ex)
    {
        h.Check($"{suite.Name} ran to completion", false, ex.GetType().Name + ": " + ex.Message);
    }
}

Console.WriteLine($"\n{h.Passed} passed, {h.Failed} failed ({suites.Count} suite(s))");
return h.Failed == 0 ? 0 : 1;

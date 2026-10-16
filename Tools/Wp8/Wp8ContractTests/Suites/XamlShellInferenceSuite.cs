using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// The page description read from a Direct3D/XAML title's managed shell (<see cref="XamlShellInference"/>),
/// checked against the hand-written ones in <see cref="XamlShells"/> for the installed titles that
/// have one: the same class, the same way of getting the instance, the same launch, loaded and
/// lifecycle calls in the same order, the same back-key decision. Titles not installed are skipped.
/// </summary>
public sealed class XamlShellInferenceSuite : IContractSuite
{
    private static readonly string Apps = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA")!, "WPR", "AppData");

    private static readonly (string ProductId, string Component)[] Titles =
    [
        ("fac2552c-25bb-420c-b0b5-bb3059418933", "AlexXamlComponent.dll"),
        ("0cb4e588-5759-4150-bef0-6866345fa26b", "MC4HybridComponent.dll"),
        ("40ae56ef-6238-40e1-a2ad-62cac8b13d93", "SparkApp.dll"),
    ];

    public string Name => "XAML shell inference";

    public void Run(Harness h)
    {
        foreach ((string productId, string component) in Titles)
        {
            string folder = Path.Combine(Apps, productId);
            string dll = Path.Combine(folder, component);
            if (!File.Exists(dll))
            {
                Console.WriteLine($"  (skipped {component}: not installed)");
                continue;
            }

            XamlShell known = XamlShells.ForComponent(component)!;
            XamlShell? inferred = XamlShellInference.Infer(dll, WinmdReader.Load(Directory.GetFiles(folder, "*.winmd")));
            h.Check($"{component}: a description is inferred", inferred is not null);
            if (inferred is null)
            {
                continue;
            }

            static string[] Names(IEnumerable<XamlShellStep> steps) => steps.Select(s => s.Method).ToArray();
            h.Check($"{component}: same class and instance", inferred.ClassId == known.ClassId && inferred.InstanceFrom == known.InstanceFrom,
                $"{inferred.ClassId} via {inferred.InstanceFrom}");
            h.Check($"{component}: same launch calls", Names(inferred.Launch).SequenceEqual(Names(known.Launch)), string.Join(",", Names(inferred.Launch)));
            // The page's OrientationChanged handler is run after Loaded when the inference finds
            // one; Modern Combat 4's hand-written description leaves its OnOrientationChanged out.
            static string[] Loaded(IEnumerable<XamlShellStep> steps) => Names(steps).Where(n => n != "OnOrientationChanged").ToArray();
            h.Check($"{component}: same loaded calls", Loaded(inferred.Loaded).SequenceEqual(Loaded(known.Loaded)), string.Join(",", Names(inferred.Loaded)));
            h.Check($"{component}: same lifecycle calls",
                Names(inferred.Deactivated).SequenceEqual(Names(known.Deactivated)) && Names(inferred.Activated).SequenceEqual(Names(known.Activated)));
            h.Check($"{component}: same back key", inferred.BackKey?.QuitIf == known.BackKey?.QuitIf && inferred.BackKey?.QuitWhen == known.BackKey?.QuitWhen,
                $"{inferred.BackKey?.QuitIf} quit when {inferred.BackKey?.QuitWhen}");
            h.Check($"{component}: marked inferred, background grid", inferred.Inferred && !inferred.DrawingSurface);
        }
    }
}

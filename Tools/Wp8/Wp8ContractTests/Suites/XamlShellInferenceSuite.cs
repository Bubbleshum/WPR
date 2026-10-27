using Wp8ContractTests.Fixtures;
using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// The page description read from a Direct3D/XAML title's managed shell (<see cref="XamlShellInference"/>),
/// checked against the hand-written ones in <see cref="HandWrittenShells"/> for the installed titles that
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

    private static IEnumerable<(string Name, string[] Lines)> Sections(XamlShell s)
    {
        yield return ("instance", [$"{s.ClassId} via {s.InstanceFrom ?? "activation"} ({string.Join(", ", s.InstanceArguments.Select(Value))})"]);
        yield return ("Launch", s.Launch.Select(Step).ToArray());
        yield return ("Loaded", s.Loaded.Select(Step).ToArray());
        yield return ("Deactivated", s.Deactivated.Select(Step).ToArray());
        yield return ("Activated", s.Activated.Select(Step).ToArray());
        yield return ("BackKey", s.BackKey is null ? [] :
            [$"{s.BackKey.QuitIf} quit when {s.BackKey.QuitWhen}", .. s.BackKey.Otherwise.Select(x => "otherwise " + Step(x)), .. s.BackKey.BeforeQuit.Select(x => "before quit " + Step(x))]);
    }

    private static string Step(XamlShellStep s) => $"{(s.Class is null ? "" : s.Class + ".")}{s.Method}({string.Join(", ", s.Arguments.Select(Value))})";

    private static string Value(object? v) => v switch
    {
        null => "null",
        XamlShell.Callback => "callback(returns null)",
        string t => $"\"{t}\"",
        XamlCallback c => $"callback(returns {Value(c.Returns is 0 or false ? null : c.Returns)}{string.Concat(c.Then.Select(t => "; then " + Step(t)))})",
        XamlHostObject o => $"host {o.InterfaceName}",
        float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f",
        _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "?",
    };

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

            XamlShell known = HandWrittenShells.ForComponent(component)!;
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

            // Every step, argument, callback answer and follow-up, section by section, as the
            // hand-written page has them. Inference is the only description WPR has, so this is
            // what keeps the three titles playing what their pages played.
            foreach ((string section, string[] mine, string[] theirs) in Sections(inferred).Zip(Sections(known), (a, b) => (a.Name, a.Lines, b.Lines)))
            {
                string[] m = component == "MC4HybridComponent.dll" && section == "Loaded" ? mine.Where(l => !l.StartsWith("OnOrientationChanged", StringComparison.Ordinal)).ToArray() : mine;
                // Where the written page answered an event with a bare no-op, the inference may also
                // run what the C# handler calls back (Alex's CloseInput clears the text; MC4's
                // LaunchGLLive turns GLLive off): more than the written page did, so compared
                // without those follow-ups.
                m = m.Select((line, i) => i < theirs.Length && !theirs[i].Contains("; then", StringComparison.Ordinal)
                    ? System.Text.RegularExpressions.Regex.Replace(line, @"(; then [^;]*?\))+\)", ")") : line).ToArray();
                int at = Enumerable.Range(0, Math.Max(m.Length, theirs.Length)).FirstOrDefault(i => i >= m.Length || i >= theirs.Length || m[i] != theirs[i], -1);
                h.Check($"{component}: {section} matches the hand-written page in full", at < 0,
                    at < 0 ? "" : $"#{at}: inferred '{(at < m.Length ? m[at] : "-")}' vs written '{(at < theirs.Length ? theirs[at] : "-")}'");
            }

            // Sizes and scales the page computes come out as the hand-written description has them.
            static bool Numeric(object? v) => v is float or int or double or SizeF;
            foreach (XamlShellStep step in known.Loaded)
            {
                XamlShellStep? mine = inferred.Loaded.FirstOrDefault(s => s.Method == step.Method);
                if (mine is null || mine.Arguments.Length != step.Arguments.Length || !step.Arguments.Any(Numeric))
                {
                    continue;
                }

                h.Check($"{component}: {step.Method} arguments as written", step.Arguments.Zip(mine.Arguments).All(p => !Numeric(p.First) || Equals(p.First, p.Second)),
                    string.Join(", ", mine.Arguments));
            }
        }

        // 2020: My Country computes its view rectangle and scale factor from Host.Content
        // (MainPage.UpdateViewDimensions, ScaleFactor / 100f): answered by name they were
        // (0, 0, 0, 0) and 160, and every touch it scaled by them missed. Checked when installed
        // (the real data root, or WPR_DATA_ROOT's).
        string? myCountry = Installed("40c1edab-4f3d-475c-aae0-b378464241a4", "MyCountry.dll");
        if (myCountry is null)
        {
            Console.WriteLine("  (skipped MyCountry.dll: not installed)");
        }
        else
        {
            XamlShell? country = XamlShellInference.Infer(Path.Combine(myCountry, "MyCountry.dll"), WinmdReader.Load(Directory.GetFiles(myCountry, "*.winmd")));
            XamlShellStep? view = country?.Loaded.FirstOrDefault(s => s.Method == "SetViewDimensions");
            XamlShellStep? scale = country?.Loaded.FirstOrDefault(s => s.Method == "set_ScaleFactor");
            h.Check("MyCountry.dll: SetViewDimensions(0, 0, physical width, physical height)",
                view is not null && view.Arguments.SequenceEqual(new object?[] { 0, 0, Wp8Display.PhysicalWidth, Wp8Display.PhysicalHeight }),
                view is null ? "missing" : string.Join(", ", view.Arguments));
            h.Check("MyCountry.dll: ScaleFactor is Host.Content.ScaleFactor / 100", scale is not null && Equals(scale.Arguments[0], Wp8Display.Scale / 100f),
                scale is null ? "missing" : string.Join(", ", scale.Arguments));
        }

        // N.O.V.A. 3's OrientationChanged handler calls OnNormalOrientation for LandscapeLeft and
        // OnFlippedOrientation for LandscapeRight. Both were collected, the flipped one ran last and
        // the game drew upside down: only the arm a LandscapeLeft page takes may be described.
        string? nova = Installed("08312124-6716-4498-b554-cc23f4240e56", "Direct3DBackgroundComponent.dll");
        if (nova is null)
        {
            Console.WriteLine("  (skipped N.O.V.A. 3: not installed)");
        }
        else
        {
            XamlShell? shell = XamlShellInference.Infer(Path.Combine(nova, "Direct3DBackgroundComponent.dll"), WinmdReader.Load(Directory.GetFiles(nova, "*.winmd")));
            string[] loaded = shell?.Loaded.Select(s => s.Method).ToArray() ?? [];
            h.Check("N.O.V.A. 3: only the LandscapeLeft orientation call", loaded.Contains("OnNormalOrientation") && !loaded.Contains("OnFlippedOrientation"),
                string.Join(",", loaded));
        }
    }

    /// <summary>A title's install folder in WPR_DATA_ROOT or the real data root, when <paramref name="file"/> is in it.</summary>
    private static string? Installed(string productId, string file) =>
        new[] { Environment.GetEnvironmentVariable("WPR_DATA_ROOT"), Path.GetDirectoryName(Apps) }
            .Where(root => !string.IsNullOrEmpty(root))
            .Select(root => Path.Combine(root!, "AppData", productId))
            .FirstOrDefault(folder => File.Exists(Path.Combine(folder, file)));
}

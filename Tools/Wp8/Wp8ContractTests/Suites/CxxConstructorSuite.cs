using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// Imported MSVC C++ constructors (<c>??0Class@…</c>) after shape-guessing was retired
/// (the runtime convergence plan, 1e): an implemented one runs its handler; an unimplemented one is
/// an ordinary defaulted import that returns <c>this</c>, leaves the object alone and is recorded;
/// and every system constructor the in-scope titles import has a handler, so none of them is
/// defaulted in the first place.
/// </summary>
public sealed class CxxConstructorSuite : IContractSuite
{
    private static readonly string Apps = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA")!, "WPR", "AppData");

    /// <summary>The 11 WP8 native titles regression work covers (Terraria, Halo and GTA excluded).</summary>
    private static readonly (string ProductId, string Name)[] InScope =
    [
        ("fac2552c-25bb-420c-b0b5-bb3059418933", "Amazing Alex"),
        ("9168c4f3-217b-4a29-b543-7513bb4ae2ed", "Angry Birds"),
        ("e94059a2-135c-420e-8e60-bcda5fc3ec30", "Angry Birds Rio"),
        ("08c71378-2cc5-48ce-9995-13206cef8e2f", "Angry Birds Seasons"),
        ("2e36df2b-44be-405d-873a-79fa190c9b35", "Angry Birds Space"),
        ("7f48eacf-1239-4877-88a4-abe9d3ddfad7", "Angry Birds Star Wars"),
        ("5026b325-f461-4a4a-9ff9-4a5de698d58a", "Angry Birds Star Wars II"),
        ("4d1a2f37-39e0-4e64-812e-478c92d6c87d", "Angry Birds Stella"),
        ("0cb4e588-5759-4150-bef0-6866345fa26b", "Modern Combat 4"),
        ("5e3298a7-d116-41cb-9627-2dd793d1528a", "MONOPOLY"),
        ("40ae56ef-6238-40e1-a2ad-62cac8b13d93", "AC Pirates"),
    ];

    public string Name => "C++ constructors";

    public void Run(Harness h)
    {
        Classification(h);
        UnknownConstructor(h);
        KnownConstructor(h);
        Coverage(h);
        GuessedWinRtSlots(h);
    }

    /// <summary>
    /// The remaining shape-guessing - a stand-in class's member the metadata does not describe - is
    /// counted, so it reaches the summary line; one the metadata does describe is not.
    /// </summary>
    private static void GuessedWinRtSlots(Harness h)
    {
        long factory = h.E.WinRt.GetActivationFactory("Contoso.Nowhere.Widget")!.Value;
        int before = h.E.WinRt.GuessedSlots.Values.Sum();
        h.CallMethod(factory, 6, 0, 0);
        h.CallMethod(factory, 6, 0, 0);
        h.Check("a guessed stand-in member is counted in GuessedSlots",
            h.E.WinRt.GuessedSlots.Values.Sum() == before + 2 && h.E.WinRt.GuessedSlots.Count == 1,
            string.Join(", ", h.E.WinRt.GuessedSlots.Select(p => $"{p.Key} x{p.Value}")));
    }

    private static void Classification(Harness h)
    {
        h.Check("IsCxxConstructor: ??0 is a constructor", HostStubs.IsCxxConstructor("??0critical_section@Concurrency@@QAA@XZ"));
        h.Check("IsCxxConstructor: ??1 (a destructor) is not", !HostStubs.IsCxxConstructor("??1critical_section@Concurrency@@QAA@XZ"));
        h.Check("IsCxxConstructor: a C function is not", !HostStubs.IsCxxConstructor("malloc"));
    }

    private static void UnknownConstructor(Harness h)
    {
        const string ctor = "??0Unheard@Of@@QAA@XZ";
        const string fullName = "MSVCP110.dll!" + ctor;
        h.Check("an invented constructor is not implemented", !h.S.IsImplemented(ctor));

        // A zeroed block, as operator new hands it over: `this` comes back, nothing is written.
        long zeroed = h.E.AllocateHeap(32);
        h.E.WriteMemory(zeroed, new byte[32]);
        long result = h.CallImport(fullName, zeroed);
        h.Check("unknown constructor returns this", result == zeroed, $"0x{result:X8} vs 0x{zeroed:X8}");
        h.Check("unknown constructor leaves a zeroed object's first word alone (no synthetic vtable)",
            h.E.ReadUInt32(zeroed) == 0, $"0x{h.E.ReadUInt32(zeroed):X8}");
        h.Check("unknown constructor is recorded as defaulted",
            h.S.DefaultedCalls.TryGetValue(ctor, out int calls) && calls == 1, string.Join(", ", h.S.DefaultedCalls.Keys));

        // A recycled block whose first word is string data ("89AB", what Angry Birds Go! had at
        // offset 0): the old LooksLikeVtable probed it; nothing reads or writes it now.
        long recycled = h.E.AllocateHeap(32);
        h.E.WriteUInt32(recycled, 0x42413938);
        result = h.CallImport(fullName, recycled);
        h.Check("unknown constructor returns this on a recycled block", result == recycled);
        h.Check("unknown constructor leaves a recycled block's first word alone",
            h.E.ReadUInt32(recycled) == 0x42413938, $"0x{h.E.ReadUInt32(recycled):X8}");
        h.Check("the second call is counted", h.S.DefaultedCalls[ctor] == 2, $"{h.S.DefaultedCalls[ctor]}");

        // A derived constructor sets its vptr before calling the base: that word survives.
        long derived = h.E.AllocateHeap(32);
        h.E.WriteUInt32(derived, 0x00401235);
        h.CallImport(fullName, derived);
        h.Check("unknown constructor leaves a vptr a derived constructor set", h.E.ReadUInt32(derived) == 0x00401235);

        // A null this (a failed placement) is answered null, not dereferenced.
        h.Check("unknown constructor with a null this returns null", h.CallImport(fullName, 0) == 0);
        h.Check("no guest stop from any of it", h.E.StopReason is null, h.E.StopReason ?? "");
    }

    private static void KnownConstructor(Harness h)
    {
        // An implemented constructor still runs its handler: critical_section's ctor builds the lock
        // and returns this, and is never counted as defaulted.
        const string ctor = "??0critical_section@Concurrency@@QAA@XZ";
        h.Check("critical_section's constructor is implemented", h.S.IsImplemented(ctor));
        long self = h.E.AllocateHeap(64);
        h.E.WriteMemory(self, new byte[64]);
        long result = h.CallImport("MSVCR110.dll!" + ctor, self);
        h.Check("critical_section's constructor returns this", result == self);
        h.Check("critical_section's constructor is not defaulted", !h.S.DefaultedCalls.ContainsKey(ctor));
    }

    /// <summary>
    /// Every constructor the in-scope titles import from a system DLL (one not shipped in the
    /// package, so the host answers it) has a handler. This is the static half of the measurement
    /// that retired ConstructShapedObject; a new title that imports one we lack shows up in its
    /// run's defaulted=[…] list instead.
    /// </summary>
    private static void Coverage(Harness h)
    {
        int titles = 0, constructors = 0;
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach ((string productId, string name) in InScope)
        {
            string dir = Path.Combine(Apps, productId);
            if (!Directory.Exists(dir))
            {
                Console.WriteLine($"SKIP  {name} is not installed");
                continue;
            }

            titles++;
            string[] files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var bundled = new HashSet<string>(files.Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                PeImage image;
                try
                {
                    image = PeImage.Load(file);
                }
                catch (Exception)
                {
                    continue;   // not a PE this loader reads (managed, x86, data)
                }

                if (image.Machine != PeImage.MachineArmNt || image.IsManaged)
                {
                    continue;
                }

                foreach (ImportedFunction import in image.Imports)
                {
                    if (!HostStubs.IsCxxConstructor(import.Name) || bundled.Contains(import.Dll))
                    {
                        continue;
                    }

                    constructors++;
                    if (!h.S.IsImplemented(import.Name))
                    {
                        missing.Add($"{name}: {import.Dll}!{import.Name}");
                    }
                }
            }
        }

        if (titles == 0)
        {
            Console.WriteLine("SKIP  no in-scope WP8 title is installed");
            return;
        }

        h.Check($"every system constructor the {titles} installed in-scope title(s) import has a handler ({constructors} import(s))",
            missing.Count == 0, string.Join("; ", missing));
    }
}

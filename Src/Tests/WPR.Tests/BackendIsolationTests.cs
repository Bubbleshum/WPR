using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Mono.Cecil;
using Xunit;

namespace WPR.Tests
{
    /// <summary>
    /// Architecture fitness function for the layered migration
    /// (see <c>Plans/ARCHITECTURE-MIGRATION.md</c>, Stage 0).
    ///
    /// It scans the solution's build outputs and asserts that the set of
    /// WPR-owned assemblies with a direct reference to a rendering backend
    /// (<c>FNA</c> or <c>Vortice.*</c>) matches the documented baseline in
    /// <see cref="KnownBackendLeaks"/>.
    ///
    /// <list type="bullet">
    /// <item>A <b>new</b> leak (an assembly that isn't in the baseline and isn't a
    /// permitted backend adapter) fails the test immediately — that is the guard
    /// against re-introducing the coupling the migration is removing.</item>
    /// <item>Once a migration stage removes a leak, the test fails with a
    /// "no longer references a backend" message telling you to shrink the baseline,
    /// so each win gets locked in.</item>
    /// </list>
    ///
    /// The migration's FNA-severance milestone (Stage 5) is reached when
    /// <see cref="KnownBackendLeaks"/> is empty and the only backend referrers are
    /// the <c>WPR.Backend.*</c> adapters in <see cref="AllowedReferrers"/>.
    /// </summary>
    public class BackendIsolationTests
    {
        private const string BackendAssemblyFna = "FNA";
        private const string BackendPrefixVortice = "Vortice.";

        /// <summary>Assemblies permitted to reference a backend: the adapters.
        /// They do not exist yet — created in Stages 4/7.</summary>
        private static readonly HashSet<string> AllowedReferrers =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "WPR.Backend.FNA",
                "WPR.Backend.Direct3D11",
                // The FAudio audio backend (Src/Audio/, split out of WPR.Backend.FNA on
                // 2026-09-01). It must reference FNA for the same reason the graphics adapter
                // does: the FAudio/FACT P/Invokes are compiled INTO FNA.dll, and FNA registers its
                // DllImport resolver (FNADllMap) only for P/Invokes whose declaring assembly is
                // FNA — so re-declaring the natives here would break native library resolution.
                // Its sibling WPR.Audio.AndroidMediaPlayer is deliberately NOT here: it plugs into
                // the same seam over a platform API and references no backend at all, which is the
                // shape a second audio implementation is expected to have.
                "WPR.Audio.FAudio",
            };

        /// <summary>The documented leak baseline as of Stage 0 (2026-08-05), read
        /// out of the current <c>.csproj</c> graph. Burn this down stage by stage;
        /// it is expected to be empty at Stage 5's exit gate.</summary>
        private static readonly HashSet<string> KnownBackendLeaks =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // WPR.Runtime was here (game-loop host -> FNA). Removed 2026-08-07: the host
                // (ApplicationLaunch) moved to WPR.Backend.FNA in Stage 4 and Runtime's dead
                // FNA/facade refs were dropped, so WPR.Runtime.dll no longer references a backend.
                //
                // The 8 pure XNA facades (Microsoft.Xna.Framework[.Game/.Graphics/.Audio/.Content/
                // .Input/.Input.Touch/.Media]) were here too. Removed 2026-08-07 with the XnaFacades
                // project itself — games are rescoped to FNA/WPR.Framework.Xna directly by the patcher,
                // so the forwarder assemblies had no runtime consumer. GamerServices below is a REAL
                // impl (not a facade) and stays.
                // Microsoft.Xna.Framework.GamerServices was here (-> FNA for GameComponent).
                // Removed 2026-08-30: the assembly no longer exists. Its 42 API types moved into
                // WPR.Framework.Xna, and the single FNA-derived type — GamerServicesComponent,
                // which subclasses FNA's spine GameComponent — moved to WPR.Backend.FNA/Compat/,
                // where a backend reference is expected rather than a leak. That was the whole of
                // the "de-FNA is Stage 5d" work this entry was holding open.
                // (ApplicationPatcher.Version 19, reinstall-forcing.)
                // Microsoft.Devices.Sensors was here (-> FNA for Vector3). Removed 2026-08-29
                // (Stage 5d): Vector3 has lived in WPR.Framework.Xna since 5a, so the project now
                // references that directly instead of reaching it through FNA.Core. Its sibling
                // System.Device (WPR.Framework.Devices.Location) was always FNA-clean.

                // WPR.XnaCompability was here (-> FNA). The assembly no longer exists at all:
                // its FNA-derived type (the WP7 GraphicsDeviceManager override) moved to
                // WPR.Backend.FNA/Compat/, and its remaining two — the GraphicsDevice /
                // GraphicsAdapter display-mode overrides, which only ever subclassed WPR-owned
                // types — moved to WPR.Framework.Xna as WPR.Xna.Compat.*. Project deleted
                // 2026-08-29 (ApplicationPatcher.Version 16, reinstall-forcing).

                // WPR.Framework.Silverlight was here (-> Vortice.Direct3D11/DXGI/D3DCompiler).
                // Removed 2026-08-29 (Stage 5e): the three D3D11 renderers moved into the new
                // WPR.Backend.Direct3D11 — the second backend the ADR §1.3 called for — and the
                // framework now sees only the ISurfaceRendererBackend seam, filled in by the
                // launcher. This was the last non-spine leak in the baseline.

                // WPR.Platform.Windows and WPR.Platform.Android were the last two entries, and
                // both cleared on 2026-09-01 (Stage 5). Neither was ever a project reference —
                // FNA's spine types flowed in through the WPR core and were used in the heads' IL:
                //
                //   Windows: TiltInputXnaComponent : GameComponent and
                //     TiltOverlayXnaComponent : DrawableGameComponent, plus XnaLauncher reaching
                //     Game.Window.Handle to set the SDL window icon and Game.Components to attach
                //     them. The components moved to WPR.Backend.FNA/Input/ (they derive from spine
                //     types, so their assembly must reference FNA); the head keeps the half that
                //     knows what a key MEANS, behind WPR.Xna.Rhi.IKeyboardEmulationHost, because that
                //     half shares a binding table with the Silverlight host and therefore speaks
                //     Avalonia.Input.Key. The icon now travels down as pixels (GameWindowIcon).
                //
                //   Android: Game as a TYPE REFERENCE ONLY, with no member touched — purely the
                //     Action<Game> parameter in FnaGameHost's ctor, which the head never passed
                //     but which its call site named as part of the full signature. Replaced by
                //     GameWindowIcon, and the hook's work moved inside the backend.
                //
                // Note what this did NOT require: the spine stage. A GameComponent still derives
                // from FNA's, and Game is still a backend-defined game-facing identity — but a
                // type deriving from a backend type is only a *leak* when it lives outside an
                // allowed referrer. The plan asserted this baseline was blocked on the
                // window-compositing product call; it was not.
            };

        [Fact]
        public void Backend_references_match_documented_baseline()
        {
            var srcDir = FindSolutionDir();
            Assert.True(
                srcDir != null,
                "Could not locate WPR.sln above the test output directory " +
                $"('{AppContext.BaseDirectory}').");

            // "Engine" and "Modules" joined the list on 2026-09-01 as those tiers were created;
            // without them WPR.Engine.* and every pluggable module would escape the guard entirely.
            // ("Audio" was here briefly; those projects now live under Modules/Audio.)
            var searchRoots = new[] { "Core", "Backends", "Platforms", "Engine", "Modules" }
                .Select(s => Path.Combine(srcDir!, s))
                .Where(Directory.Exists)
                .ToList();

            // Only assemblies a project in THIS TREE actually produces are governed. The name
            // filter below is about *which* of those WPR owns; this is about excluding binaries
            // that merely happen to sit in a scanned bin/ folder.
            //
            // That is not hypothetical: CLAUDE.md documents a one-game console harness that is
            // deliberately built into the desktop head's output directory (it needs the native
            // SDL2/FNA3D/FAudio DLLs that only that project copies there). It references
            // WPR.Backend.FNA, so it references FNA, and being named "wprharness" it sailed
            // through IsWprOwned and failed this gate — for a scratch tool the architecture has
            // no opinion about. Added 2026-09-01.
            var governed = ProducedAssemblyNames(srcDir!);
            Assert.True(
                governed.Count > 0,
                $"Found no .csproj files under '{srcDir}' — the project scan that decides which " +
                "assemblies this test governs is broken, so nothing would be checked.");

            // assembly simple name -> distinct backend refs found across every
            // built copy / target framework of that assembly.
            var backendRefs = new Dictionary<string, SortedSet<string>>(
                StringComparer.OrdinalIgnoreCase);
            var scanned = 0;

            foreach (var root in searchRoots)
            {
                foreach (var dll in Directory.EnumerateFiles(
                             root, "*.dll", SearchOption.AllDirectories))
                {
                    if (IsUnderObj(dll)) continue;

                    var name = Path.GetFileNameWithoutExtension(dll);
                    if (!governed.Contains(name)) continue;
                    if (!IsWprOwned(name)) continue;

                    AssemblyDefinition asm;
                    try { asm = AssemblyDefinition.ReadAssembly(dll); }
                    catch { continue; } // native/unreadable sibling, skip

                    using (asm)
                    {
                        scanned++;
                        var leaks = asm.Modules
                            .SelectMany(m => m.AssemblyReferences)
                            .Where(IsBackendRef)
                            .Select(r => r.Name);

                        if (!backendRefs.TryGetValue(name, out var set))
                            backendRefs[name] = set =
                                new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var l in leaks) set.Add(l);
                    }
                }
            }

            Assert.True(
                scanned > 0,
                "No WPR-owned assemblies were found under Core/, Backends/, Platforms/, Engine/ or Modules/ bin folders. " +
                "Build the whole solution in your IDE before running this fitness test.");

            var offenders = backendRefs
                .Where(kv => kv.Value.Count > 0 && !AllowedReferrers.Contains(kv.Key))
                .Select(kv => kv.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var newLeaks = offenders
                .Except(KnownBackendLeaks, StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var resolved = KnownBackendLeaks
                .Except(offenders, StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Assert.False(
                newLeaks.Count > 0,
                "New backend leak(s) detected — move the code behind WPR.Abstractions " +
                "or into a WPR.Backend.* adapter:\n  " +
                string.Join("\n  ", newLeaks.Select(
                    n => $"{n} -> {string.Join(", ", backendRefs[n])}")));

            Assert.False(
                resolved.Count > 0,
                "These assemblies no longer reference a backend — remove them from " +
                "KnownBackendLeaks to lock the win in:\n  " +
                string.Join("\n  ", resolved));
        }

        private static bool IsBackendRef(AssemblyNameReference r) =>
            r.Name.Equals(BackendAssemblyFna, StringComparison.OrdinalIgnoreCase)
            || r.Name.StartsWith(BackendPrefixVortice, StringComparison.OrdinalIgnoreCase);

        /// <summary>True for assemblies WPR authors/ships (and therefore governs),
        /// excluding this test assembly and third-party packages.</summary>
        /// <summary>
        /// Every assembly simple-name that a <c>.csproj</c> under <paramref name="srcDir"/>
        /// produces — its <c>&lt;AssemblyName&gt;</c> where one is set, otherwise the project
        /// file name.
        ///
        /// <para>Reading <c>AssemblyName</c> is not optional here: several projects deliberately
        /// ship under a WP7 identity that games bind by name, so project and assembly disagree
        /// (<c>WPR.Framework.Phone</c> → <c>Microsoft.Phone</c>,
        /// <c>WPR.Framework.Devices.Location</c> → <c>System.Device</c>, <c>FNA.Core</c> →
        /// <c>FNA</c>). Matching on project file names alone would drop exactly the assemblies
        /// §3.2 of the migration plan cares most about.</para>
        /// </summary>
        private static HashSet<string> ProducedAssemblyNames(string srcDir)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var proj in Directory.EnumerateFiles(srcDir, "*.csproj", SearchOption.AllDirectories))
            {
                if (IsUnderObj(proj)) continue;

                string? assemblyName = null;
                try
                {
                    assemblyName = XDocument.Load(proj)
                        .Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "AssemblyName")
                        ?.Value.Trim();
                }
                catch
                {
                    // Unreadable/malformed csproj: fall back to the file name rather than
                    // silently dropping a project out of the governed set.
                }

                names.Add(string.IsNullOrEmpty(assemblyName)
                    ? Path.GetFileNameWithoutExtension(proj)
                    : assemblyName!);
            }
            return names;
        }

        private static bool IsWprOwned(string simpleName) =>
            (simpleName.StartsWith("WPR", StringComparison.OrdinalIgnoreCase)
                 && !simpleName.Equals("WPR.Tests", StringComparison.OrdinalIgnoreCase))
            || simpleName.StartsWith("Microsoft.Xna.Framework", StringComparison.OrdinalIgnoreCase)
            || simpleName.StartsWith("Microsoft.Phone", StringComparison.OrdinalIgnoreCase)
            || simpleName.StartsWith("Microsoft.Devices", StringComparison.OrdinalIgnoreCase)
            || simpleName.Equals("System.Device", StringComparison.OrdinalIgnoreCase);

        private static bool IsUnderObj(string path) =>
            path.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase);

        private static string? FindSolutionDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "WPR.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }
    }
}

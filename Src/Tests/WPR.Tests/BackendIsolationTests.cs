using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Xunit;

namespace WPR.Tests
{
    /// <summary>
    /// Architecture fitness function for the layered migration
    /// (see <c>docs/ARCHITECTURE-MIGRATION.md</c>, Stage 0).
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
                "Microsoft.Xna.Framework.GamerServices", // -> FNA; real EF/scraper impl, de-FNA is Stage 5d
                "Microsoft.Devices.Sensors",              // -> FNA (Vector3); built by the WPR.Framework.Devices.Sensors project, AssemblyName kept as the WP7 identity (no forwarder). De-FNA is Stage 5d. Its sibling System.Device (WPR.Framework.Devices.Location) is FNA-clean.
                "WPR.XnaCompability",                     // patch-target lib -> FNA
                "WPR.Framework.Silverlight",              // -> Vortice.Direct3D11 (renamed from WPR.SilverlightCompability in Stage 3)

                // Surfaced by this fitness test itself (not a direct FNA
                // ProjectReference — FNA types flow in through the WPR core and
                // are used in these assemblies' IL). WPR.UI is shared UI that
                // should end up FNA-free (burn down); WPR.UI.Android is a launcher
                // and, once split into WPR.Android, moves to AllowedReferrers.
                // Reclassified at Stage 6/7.
                "WPR.UI",
                "WPR.UI.Android",
            };

        [Fact]
        public void Backend_references_match_documented_baseline()
        {
            var srcDir = FindSolutionDir();
            Assert.True(
                srcDir != null,
                "Could not locate WPR.sln above the test output directory " +
                $"('{AppContext.BaseDirectory}').");

            var searchRoots = new[] { "Core", "UI", "Backends" }
                .Select(s => Path.Combine(srcDir!, s))
                .Where(Directory.Exists)
                .ToList();

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
                "No WPR-owned assemblies were found under Core/ or UI/ bin folders. " +
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

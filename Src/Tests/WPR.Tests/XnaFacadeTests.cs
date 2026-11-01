using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Xunit;

namespace WPR.Tests
{
    /// <summary>
    /// The XNA facades (Src/Facades/Xna) forward every public Microsoft.Xna.* type WPR implements.
    /// Games bind those facades by their own references, so a type missing from
    /// XnaForwarders.cs is a TypeLoadException in every game that uses it. Like
    /// BackendIsolationTests this reads the built assemblies: build the desktop head first.
    /// </summary>
    public class XnaFacadeTests
    {
        // Where the implementations of game-facing Microsoft.Xna.* types live.
        private static readonly string[] Implementations =
        {
            "WPR.Framework.Xna", "WPR.Backend.FNA", "WPR.Framework.Silverlight",
        };

        [Fact]
        public void Every_implemented_xna_type_is_forwarded_by_every_facade()
        {
            var output = FindHeadOutput();
            Assert.True(output != null, "Build WPR.Platform.Windows first: no output with the XNA facades was found.");

            var implemented = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var name in Implementations)
            {
                using var asm = AssemblyDefinition.ReadAssembly(Path.Combine(output!, name + ".dll"));
                foreach (var t in asm.MainModule.Types)
                    if (t.IsPublic && (t.Namespace ?? "").StartsWith("Microsoft.Xna.Framework", StringComparison.Ordinal))
                        implemented.Add(t.FullName);
            }
            Assert.NotEmpty(implemented);

            var facades = Directory.GetFiles(output!, "Microsoft.Xna.Framework*.dll");
            Assert.Equal(8, facades.Length);
            foreach (var facade in facades)
            {
                using var asm = AssemblyDefinition.ReadAssembly(facade);
                Assert.DoesNotContain(asm.MainModule.Types, t => t.Name != "<Module>");
                var forwarded = asm.MainModule.ExportedTypes
                    .Where(e => e.IsForwarder)
                    .Select(e => e.FullName)
                    .ToHashSet(StringComparer.Ordinal);
                var missing = implemented.Where(t => !forwarded.Contains(t)).ToList();
                Assert.True(missing.Count == 0,
                    $"{Path.GetFileName(facade)} does not forward: {string.Join(", ", missing)}. " +
                    "Add them to Src/Facades/Xna/XnaForwarders.cs.");
            }
        }

        private static string? FindHeadOutput()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "WPR.sln")))
                dir = dir.Parent;
            if (dir == null) return null;
            var bin = Path.Combine(dir.FullName, "Platforms", "WPR.Platform.Windows", "bin");
            if (!Directory.Exists(bin)) return null;
            return Directory.EnumerateFiles(bin, "Microsoft.Xna.Framework.dll", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName)
                .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d!, "Microsoft.Xna.Framework.dll")))
                .FirstOrDefault();
        }
    }
}

using System;
using System.IO;
using Mono.Cecil;

namespace IlProbe
{
    // Finds assemblies the .NET Android linker cannot rewrite. LinkAssembliesNoShrink runs Cecil
    // over every resolved assembly, and Cecil's ModuleWriter throws
    // "Writing mixed-mode assemblies is not supported" for any module without the ILOnly flag
    // (i.e. one carrying native code as well as IL).
    //   usage: ilprobe --mixed <dir>
    internal static class Mixed
    {
        public static void Run(string[] args)
        {
            string dir = args[Array.IndexOf(args, "--mixed") + 1];
            int scanned = 0, mixed = 0;
            foreach (var f in Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories))
            {
                ModuleDefinition m;
                try { m = ModuleDefinition.ReadModule(f); }
                catch { continue; }
                scanned++;
                bool ilOnly = (m.Attributes & ModuleAttributes.ILOnly) != 0;
                if (!ilOnly)
                {
                    mixed++;
                    Console.WriteLine($"MIXED-MODE: {f}");
                    Console.WriteLine($"            attributes={m.Attributes} arch={m.Architecture}");
                }
                m.Dispose();
            }
            Console.WriteLine($"scanned={scanned} mixedMode={mixed}");
        }
    }
}

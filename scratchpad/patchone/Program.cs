using System;
using System.IO;
using WPR.Common;

namespace PatchOne
{
    // Patches ONE assembly with the real ApplicationPatcher, in place, exactly as an install would.
    // usage: patchone <path-to-dll>   (a .dll.original sibling is created if missing)
    internal static class Program
    {
        public static void Main(string[] args)
        {
            if (args.Length < 1) { Console.Error.WriteLine("usage: patchone <dll>"); return; }
            string path = Path.GetFullPath(args[0]);
            Configuration.Current = new Configuration(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WPR"));
            Console.Error.WriteLine($"[patchone] patcher v{WPR.ApplicationPatcher.Version} monoRelocationDisabled={WPR.ApplicationPatcher.MonoRelocationDisabled}");
            var patcher = new WPR.ApplicationPatcher();
            patcher.PatchDll(path);
            Console.Error.WriteLine($"[patchone] done: {path} ({new FileInfo(path).Length} bytes)");
        }
    }
}

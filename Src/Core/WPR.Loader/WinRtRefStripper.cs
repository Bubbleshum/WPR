using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;

using WPR.Common;

namespace WPR
{
    /// <summary>
    /// Removes <c>ContentType=WindowsRuntime</c> from every <see cref="AssemblyNameReference"/>
    /// in every .dll in the install folder.
    ///
    /// User-app DLLs that were compiled against a .winmd carry WinRT-flagged asm refs (e.g.
    /// <c>WinPhoneRunnerAppInterop.dll</c> references <c>WinPhoneRunnerAppComponent</c> and
    /// <c>Windows</c> with the WinRT bit set). On net8.0 the JIT routes those through
    /// <c>System.Runtime.InteropServices.WindowsRuntime</c> — which is unsupported and unconditionally
    /// throws <see cref="PlatformNotSupportedException"/> the first time the method is JITted.
    ///
    /// Stripping the bit makes the runtime treat the references as ordinary managed assemblies
    /// and resolve them against our managed stubs (see <see cref="WinmdStubber"/> and
    /// <see cref="WindowsTypeSynthesizer"/>).
    /// </summary>
    public static class WinRtRefStripper
    {
        private const AssemblyAttributes WinRtFlag = (AssemblyAttributes)0x0200;

        public static void StripInPlace(string installFolder)
        {
            string[] dlls;
            try { dlls = Directory.GetFiles(installFolder, "*.dll"); }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppInstall, $"WinRtRefStripper: enumerate failed: {ex}");
                return;
            }

            int touched = 0;
            foreach (var dllPath in dlls)
            {
                try
                {
                    if (StripOne(dllPath)) touched++;
                }
                catch (Exception ex)
                {
                    Log.Warn(LogCategory.AppInstall, $"WinRtRefStripper: skip {Path.GetFileName(dllPath)}: {ex.Message}");
                }
            }

            if (touched > 0)
                Log.Info(LogCategory.AppInstall, $"WinRtRefStripper: stripped WinRT flag from refs in {touched} dll(s)");
        }

        /// <summary>
        /// Returns true if any reference was changed and the file was rewritten.
        /// </summary>
        private static bool StripOne(string dllPath)
        {
            // Read into memory so we can rewrite in place without holding a file lock.
            byte[] bytes = File.ReadAllBytes(dllPath);

            using var ms = new MemoryStream(bytes, writable: false);
            AssemblyDefinition asm;
            try { asm = AssemblyDefinition.ReadAssembly(ms); }
            catch
            {
                // Native dll, mixed-mode, or otherwise unreadable — leave alone.
                return false;
            }

            using (asm)
            {
                bool changed = false;
                foreach (var r in asm.MainModule.AssemblyReferences)
                {
                    if ((r.Attributes & WinRtFlag) != 0)
                    {
                        r.Attributes &= ~WinRtFlag;
                        changed = true;
                    }
                }

                if (!changed) return false;

                // Write to a temp path then move over the original. Avoids partial-write
                // corruption if Cecil throws.
                string tmpPath = dllPath + ".striprewrite";
                if (File.Exists(tmpPath)) File.Delete(tmpPath);
                asm.Write(tmpPath);
                // Cecil may keep the input MemoryStream alive until Dispose; we already wrote
                // to a separate file, so it's safe to swap.
                File.Delete(dllPath);
                File.Move(tmpPath, dllPath);

                Log.Info(LogCategory.AppInstall, $"WinRtRefStripper: cleaned {Path.GetFileName(dllPath)}");
                return true;
            }
        }
    }
}

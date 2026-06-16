using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace WPR.WindowsCompability
{

    public abstract class Type2
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Type? GetType(string typeName, bool throwOnError)
        {
            if (typeName == null)
            {
                throw new ArgumentNullException("Type name is null!");
            }

            var stuffs = typeName.Split(',');
            if (stuffs.Length >= 2)
            {
                bool patched = false;
                for (int i = 1; i < stuffs.Length; i += 4)
                {
                    if (stuffs[i].Contains("Microsoft.Xna.Framework"))
                    {
                        if (!stuffs[i].Equals("Microsoft.Xna.Framework.GamerServices"))
                        {
                            stuffs[i] = "FNA";
                            patched = true;
                        }
                    }
                }
                if (patched)
                {
                    typeName = stuffs[0];
                    for (int i = 1; i < stuffs.Length; i += 4)
                    {
                        typeName += $", {stuffs[i]}";
                    }
                }
            }

            // If the type is assembly-qualified, search every loaded ALC for an assembly
            // matching the simple name and look the type up via Assembly.GetType — a pure
            // managed lookup that doesn't trigger the CLR's cross-ALC collectibility
            // check. Falling through to Type.GetType has the binder treat *some* assembly
            // up the stack as the "requesting assembly":
            //
            //  - When the caller is the main user DLL (collectible userAlc), Type.GetType
            //    sees this method's assembly (WPR.WindowsCompability — non-collectible,
            //    Default ALC) and rejects loading the user assembly back into Default ALC.
            //  - When the caller is a SIBLING library DLL (e.g. Krome.dll, loaded into
            //    Default ALC by design — see ApplicationLaunch.cs static ctor), and the
            //    target type lives in the main collectible user DLL (e.g. AsteroidsDeluxe),
            //    Type.GetType again routes through Default ALC's resolver, which returns
            //    the userAlc-loaded assembly, and the CLR rejects the resulting Default→
            //    userAlc reference. The Krome→AsteroidsDeluxe crash is this case.
            //
            // Searching AssemblyLoadContext.All catches both: we hand back the right
            // Assembly object and call .GetType on it directly, bypassing the binder.
            int commaIdx = typeName.IndexOf(',');
            if (commaIdx >= 0)
            {
                string typeOnly = typeName.Substring(0, commaIdx).Trim();
                string asmSimpleName = typeName.Substring(commaIdx + 1).Trim().Split(',')[0].Trim();

                // Resolve against the CALLER's own ALC first. Matching on simple name
                // across every ALC in the process is unsafe: when two games are resident
                // at once, each ships its own copy of a common dependency (e.g.
                // SkinnedModel) under the same simple name but a different version and
                // contents. A blind AssemblyLoadContext.All scan can hand ilomilo
                // Ghostscape's SkinnedModel 1.0.0.1 — which has no PAnimTrigger and a
                // different AnimationClip.Read — so the lookup throws TypeLoadException /
                // MissingMethodException against the wrong assembly. Searching the
                // requesting assembly's ALC first binds the sibling that actually shipped
                // with the caller; the broad All scan stays only as the cross-ALC
                // fallback (a Default-ALC helper resolving a type in the collectible user
                // assembly — see the Krome -> AsteroidsDeluxe note above).
                AssemblyLoadContext? callerAlc = null;
                try
                {
                    callerAlc = AssemblyLoadContext.GetLoadContext(Assembly.GetCallingAssembly());
                }
                catch { /* fall through to the unordered scan below */ }

                if (callerAlc != null)
                {
                    var t = FindTypeInAlc(callerAlc, asmSimpleName, typeOnly);
                    if (t != null) return t;
                }

                foreach (var alc in AssemblyLoadContext.All)
                {
                    if (ReferenceEquals(alc, callerAlc)) continue; // already searched above
                    var t = FindTypeInAlc(alc, asmSimpleName, typeOnly);
                    if (t != null) return t;
                }
            }

            return Type.GetType(typeName, throwOnError);
        }

        // Return the first type matching <paramref name="typeOnly"/> from an assembly in
        // <paramref name="alc"/> whose simple name equals <paramref name="asmSimpleName"/>,
        // or null if none of that ALC's assemblies carry the type.
        private static Type? FindTypeInAlc(AssemblyLoadContext alc, string asmSimpleName, string typeOnly)
        {
            foreach (var asm in alc.Assemblies)
            {
                if (string.Equals(asm.GetName().Name, asmSimpleName, StringComparison.OrdinalIgnoreCase))
                {
                    var t = asm.GetType(typeOnly, throwOnError: false);
                    if (t != null) return t;
                }
            }
            return null;
        }
    }

    //RnD
    /*
    public abstract class WritableBitmap
    {
        public static Type? GetType(string typeName, bool throwOnError)
        {
            if (typeName == null)
            {
                throw new ArgumentNullException("Type name is null!");
            }

            var stuffs = typeName.Split(',');
            if (stuffs.Length >= 2)
            {
                bool patched = false;
                for (int i = 1; i < stuffs.Length; i += 4)
                {
                    if (stuffs[i].Contains("Microsoft.Xna.Framework"))
                    {
                        if (!stuffs[i].Equals("Microsoft.Xna.Framework.GamerServices"))
                        {
                            stuffs[i] = "FNA";
                            patched = true;
                        }
                    }
                }
                if (patched)
                {
                    typeName = stuffs[0];
                    for (int i = 1; i < stuffs.Length; i += 4)
                    {
                        typeName += $", {stuffs[i]}";
                    }
                }
            }

            return Type.GetType(typeName);
        }
    }
    */
}

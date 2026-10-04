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

            // Look the caller up HERE: this method is NoInlining, so the calling assembly is the
            // game code whose Type.GetType call the patcher redirected to us.
            Assembly? caller = null;
            try
            {
                caller = Assembly.GetCallingAssembly();
            }
            catch { /* every lookup below degrades to the unordered scan */ }

            // An assembly-qualified name is resolved by the runtime's own type-name parser, with
            // WPR supplying the assembly for every name in it. The parser is the only thing that
            // gets nesting right: a generic argument carries its own assembly-qualified name in
            // [[...]], so commas do not delimit anything on their own. The hand-rolled split this
            // replaces assumed "type, assembly, version, culture, token" and stepped through the
            // pieces four at a time; given
            //   List`1[[Microsoft.Xna.Framework.Vector2, Microsoft.Xna.Framework, Version=…,
            //   Culture=…, PublicKeyToken=…]], mscorlib, Version=…
            // it glued the pieces back together as "List`1[[…Vector2, FNA, mscorlib", which the
            // runtime rejects as "The given assembly name was invalid. File name: 'FNA,  mscorlib'".
            // Skulls of the Shogun is the reference case: its SharpSerializer world map
            // (Content/Maps/Overworld Map/Overworld_Map.apt) names exactly that type, and the
            // unhandled throw on its loading thread killed the game at the first map.
            //
            // It also renamed the XNA assemblies to "FNA", which has been wrong since the spine
            // relocation: FNA defines no XNA API at all now, and every Microsoft.Xna.Framework.*
            // type a game can name lives in WPR.Framework.Xna (see the resolver below).
            //
            // WPR hands the parser the Assembly objects itself and the type is then looked up with
            // Assembly.GetType — a pure managed lookup that doesn't trigger the CLR's cross-ALC
            // collectibility check. Letting the binder load the assemblies has it treat *some*
            // assembly up the stack as the "requesting assembly":
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
            if (typeName.IndexOf(',') >= 0)
            {
                AssemblyLoadContext? callerAlc = null;
                try
                {
                    if (caller != null) callerAlc = AssemblyLoadContext.GetLoadContext(caller);
                }
                catch { /* fall through to the unordered scan */ }

                Type? resolved = null;
                try
                {
                    resolved = Type.GetType(
                        typeName,
                        name => ResolveAssembly(name, callerAlc),
                        (asm, name, ignoreCase) =>
                            asm != null ? asm.GetType(name, false, ignoreCase)
                                        : caller?.GetType(name, false, ignoreCase),
                        throwOnError: false);
                }
                catch (Exception) when (!throwOnError)
                {
                    // A malformed name; WP7's Type.GetType(name, false) answered null for that.
                }
                if (resolved != null) return resolved;
            }
            else
            {
                // Unqualified type name (no assembly part). The CLR's
                // Type.GetType(name) resolves the name only against the *requesting*
                // assembly plus CoreLib — and because this shim now stands between the
                // real caller and the CLR, the requesting assembly is
                // WPR.WindowsCompability (this DLL), NOT the assembly that actually
                // called Type.GetType. So a type defined in the caller's own assembly
                // is invisible and GetType returns null where the unpatched call would
                // have found it.
                //
                // Concrete casualty: the Shiva engine cross-compiled to C# via llvm2cs.
                // Bridge.Initialize (in S3DClientNative_WP7) calls
                //   Type.GetType("com.indigen.llvm2cs.generated.Program", false)
                // to hand RT.startProgram the generated program class. That type lives in
                // S3DClientNative_WP7 itself, so the shim's fall-through to Type.GetType
                // returned null; RT then silently skipped program init (getProgramClass()
                // was null), no hermes_* functions ever registered, the DLMalloc allocator
                // could not find hermes_dlmalloc, and the engine rendered nothing — Babel
                // Rising 3D NRE'd every frame, then hard-crashed on the first focus loss
                // inside hermes_enginePause.
                //
                // Restore the unpatched semantics: resolve against the calling assembly
                // first (this is exactly the assembly the CLR would have treated as the
                // requesting assembly if the call had not been redirected here). CoreLib
                // types still resolve via the Type.GetType fall-through below.
                if (caller != null)
                {
                    var t = caller.GetType(typeName, throwOnError: false);
                    if (t != null) return t;
                }
            }

            return Type.GetType(typeName, throwOnError);
        }

        // The assembly a name inside a type name refers to.
        //
        // * Any Microsoft.Xna.Framework* assembly is WPR.Framework.Xna. That is where the patcher
        //   rescopes every XNA type a game binds (WprFrameworkXnaTypes), so a type name written
        //   by the game at runtime has to land in the same place. The WP7 GraphicsDeviceManager
        //   override is in WPR.Backend.FNA, but it derives from the WPR.Framework.Xna type of the
        //   same name, which is what a serialised name means anyway.
        // * Anything else is looked for by simple name in the CALLER's load context first, then
        //   in every context. Matching on simple name across every ALC in the process is unsafe:
        //   when two games are resident at once, each ships its own copy of a common dependency
        //   (e.g. SkinnedModel) under the same simple name but a different version and contents.
        //   A blind AssemblyLoadContext.All scan can hand ilomilo Ghostscape's SkinnedModel
        //   1.0.0.1 — which has no PAnimTrigger and a different AnimationClip.Read — so the lookup
        //   throws TypeLoadException / MissingMethodException against the wrong assembly. The
        //   broad scan stays as the cross-ALC fallback (a Default-ALC helper resolving a type in
        //   the collectible user assembly — see the Krome -> AsteroidsDeluxe note above).
        // * Framework names (mscorlib, System, System.Core, …, at WP7's 2.0.5.0 or desktop 4.0.0.0)
        //   go to the Default context's binder, which maps them to their facades; Assembly.GetType
        //   follows the facades' type forwarders into CoreLib.
        private static Assembly? ResolveAssembly(AssemblyName name, AssemblyLoadContext? callerAlc)
        {
            string? simple = name.Name;
            if (string.IsNullOrEmpty(simple)) return null;

            if (simple.StartsWith("Microsoft.Xna.Framework", StringComparison.OrdinalIgnoreCase))
            {
                return typeof(Microsoft.Xna.Framework.Vector2).Assembly;
            }

            if (callerAlc != null)
            {
                Assembly? own = FindAssembly(callerAlc, simple);
                if (own != null) return own;
            }

            foreach (var alc in AssemblyLoadContext.All)
            {
                if (ReferenceEquals(alc, callerAlc)) continue; // already searched above
                Assembly? found = FindAssembly(alc, simple);
                if (found != null) return found;
            }

            try
            {
                return AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(simple));
            }
            catch
            {
                return null;
            }
        }

        private static Assembly? FindAssembly(AssemblyLoadContext alc, string simpleName)
        {
            foreach (var asm in alc.Assemblies)
            {
                if (string.Equals(asm.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                {
                    return asm;
                }
            }
            return null;
        }
    }

}

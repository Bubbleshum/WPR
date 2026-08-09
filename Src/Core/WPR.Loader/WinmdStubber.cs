using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

using WPR.Common;

namespace WPR
{
    /// <summary>
    /// Replaces hybrid Silverlight + WinRT app components (.winmd metadata + native .dll) with
    /// managed stub DLLs that mimic the metadata shape but have no-op method bodies. Lets the
    /// user's IL JIT and run on regular .NET 8 without a WinRT activation runtime.
    ///
    /// Strategy:
    ///   1. Read each *.winmd in the install folder with Cecil.
    ///   2. Synthesize a managed assembly with the same name, version, and type layout (classes,
    ///      interfaces, structs, enums, methods, properties, events, fields). Method bodies just
    ///      return default(T) or void.
    ///   3. Backup the original native DLL (renamed to <c>.native_original</c>) and write the
    ///      stub in its place — the user's IL still resolves <c>WinPhoneRunnerAppComponent.dll</c>
    ///      from the same name, but now it loads as managed.
    ///   4. Move the .winmd aside (renamed to <c>.original</c>) so nothing else picks it up.
    ///
    /// Phase 2 — see <see cref="WindowsTypeSynthesizer"/> — generates additional stubs for any
    /// external <c>Windows.*</c> WinRT type referenced by the new stubs (those normally come
    /// from the OS WinRT registry and are absent on a non-Windows-Runtime host).
    /// </summary>
    public static class WinmdStubber
    {
        /// <summary>
        /// WinRT ↔ .NET projection map. On a real WinRT host the CLR projects these WinRT
        /// interfaces to their BCL equivalents, so managed code (the user's IL, and any class
        /// that implements a WinRT callback interface) is written against the BCL type. Our
        /// stubs must use the same BCL types or the interface map won't line up — e.g. a
        /// callback declaring <c>LogEvent(IMap&lt;string,string&gt;)</c> in the winmd is
        /// implemented in C# as <c>LogEvent(IDictionary&lt;string,string&gt;)</c>; if the stub
        /// keeps the raw WinRT <c>IMap`2</c> the class fails to load with "does not have an
        /// implementation". Keyed by the open generic's Cecil FullName (with `arity suffix).
        /// </summary>
        private static readonly Dictionary<string, Type> WinRtProjections = new(StringComparer.Ordinal)
        {
            ["Windows.Foundation.Collections.IMap`2"]         = typeof(IDictionary<,>),
            ["Windows.Foundation.Collections.IMapView`2"]     = typeof(IReadOnlyDictionary<,>),
            ["Windows.Foundation.Collections.IVector`1"]      = typeof(IList<>),
            ["Windows.Foundation.Collections.IVectorView`1"]  = typeof(IReadOnlyList<>),
            ["Windows.Foundation.Collections.IIterable`1"]    = typeof(IEnumerable<>),
            ["Windows.Foundation.Collections.IIterator`1"]    = typeof(IEnumerator<>),
            ["Windows.Foundation.Collections.IKeyValuePair`2"] = typeof(KeyValuePair<,>),
        };

        /// <summary>
        /// WinRT value-type structs that the .NET WinRT projection relocates into
        /// <c>System.Runtime.WindowsRuntime</c>. User IL compiled against the projection
        /// references them from that assembly (e.g.
        /// <c>[System.Runtime.WindowsRuntime]Windows.Foundation.Size</c>), while the raw winmd
        /// scopes them to <c>Windows</c>. If the stub keeps the <c>Windows</c> scope, a call like
        /// <c>set_WindowBounds([System.Runtime.WindowsRuntime]Size)</c> won't bind to the stub's
        /// <c>set_WindowBounds([Windows]Size)</c>. Re-scope so both sides name the same type.
        /// (WindowsTypeSynthesizer then materializes the struct under that scope.)
        /// </summary>
        private const string ProjectedValueTypeScope = "System.Runtime.WindowsRuntime";
        private static readonly HashSet<string> ProjectedValueTypes = new(StringComparer.Ordinal)
        {
            "Windows.Foundation.Size",
            "Windows.Foundation.Point",
            "Windows.Foundation.Rect",
        };

        /// <summary>
        /// Find every .winmd in <paramref name="installFolder"/> and replace it + its native
        /// peer .dll with a managed stub. Best-effort — failures are logged, not thrown.
        /// </summary>
        public static IReadOnlyList<string> StubInPlace(string installFolder)
        {
            var stubbedAssemblies = new List<string>();
            string[] winmds;
            try { winmds = Directory.GetFiles(installFolder, "*.winmd"); }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppInstall, $"WinmdStubber: enumerate failed: {ex}");
                return stubbedAssemblies;
            }

            if (winmds.Length == 0) return stubbedAssemblies;

            Log.Info(LogCategory.AppInstall, $"WinmdStubber: found {winmds.Length} .winmd file(s) — generating managed stubs");

            foreach (var winmdPath in winmds)
            {
                try
                {
                    string asmName = StubOne(winmdPath, installFolder);
                    stubbedAssemblies.Add(asmName);
                }
                catch (Exception ex)
                {
                    Log.Warn(LogCategory.AppInstall, $"WinmdStubber: stub failed for {Path.GetFileName(winmdPath)}: {ex}");
                }
            }

            return stubbedAssemblies;
        }

        private static string StubOne(string winmdPath, string installFolder)
        {
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(installFolder);
            var rp = new ReaderParameters { AssemblyResolver = resolver };

            string asmName;
            string destDll;

            // Scope src/dst tightly so Cecil releases the .winmd file handle before we try to
            // rename it. Holding `src` open across File.Move silently leaves the .winmd in place.
            {
                using AssemblyDefinition src = AssemblyDefinition.ReadAssembly(winmdPath, rp);
                ModuleDefinition srcMod = src.MainModule;
                asmName = src.Name.Name;

                // Don't stub system .winmd files even if a user accidentally bundled one.
                if (asmName.Equals("Windows", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info(LogCategory.AppInstall, $"WinmdStubber: skipping system metadata '{asmName}.winmd'");
                    return asmName;
                }

                Log.Info(LogCategory.AppInstall, $"WinmdStubber: stubbing {asmName} ({srcMod.Types.Count} top-level types)");

                using AssemblyDefinition dst = AssemblyDefinition.CreateAssembly(
                    new AssemblyNameDefinition(asmName, src.Name.Version ?? new Version(8, 0, 0, 0)),
                    asmName,
                    ModuleKind.Dll);
                ModuleDefinition dstMod = dst.MainModule;

                // 1. Declare every type (incl. nested) up-front so cross-references can resolve.
                var typeMap = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
                foreach (var t in srcMod.Types) DeclareTypeRecursive(t, dstMod, parent: null, typeMap);

                // 2. Wire base types and interfaces — needs all types declared.
                foreach (var t in srcMod.Types) WireBaseAndInterfaces(t, typeMap, dstMod);

                // 3. Populate fields, methods, properties, events with stub bodies.
                foreach (var t in srcMod.Types) PopulateMembers(t, typeMap, dstMod);

                // 4. Strip WindowsRuntime ContentType from any imported asm refs. ImportReference
                //    preserves the source's flags, but on net8.0 a WinRT-flagged binding goes
                //    through System.Runtime.InteropServices.WindowsRuntime (unsupported on this
                //    runtime) and throws PlatformNotSupportedException at first activation.
                foreach (var r in dstMod.AssemblyReferences)
                {
                    const AssemblyAttributes WinRtFlag = (AssemblyAttributes)0x0200;
                    r.Attributes &= ~WinRtFlag;
                }

                // 5. Save: backup native dll, then write stub in its place.
                destDll = Path.Combine(installFolder, asmName + ".dll");
                if (File.Exists(destDll))
                {
                    string backup = destDll + ".native_original";
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Move(destDll, backup);
                }
                dst.Write(destDll);
            } // src + dst disposed here — file handles released.

            // Move the .winmd out of the way so the runtime doesn't pick it up.
            string winmdBackup = winmdPath + ".original";
            if (File.Exists(winmdBackup)) File.Delete(winmdBackup);
            File.Move(winmdPath, winmdBackup);

            Log.Info(LogCategory.AppInstall, $"WinmdStubber: wrote {destDll}");
            return asmName;
        }

        private static void DeclareTypeRecursive(
            TypeDefinition src,
            ModuleDefinition dstMod,
            TypeDefinition? parent,
            Dictionary<string, TypeDefinition> map)
        {
            // WindowsRuntime flag tells the loader this is a WinRT type; strip it so the runtime
            // treats it as a regular managed type.
            const TypeAttributes WindowsRuntimeFlag = (TypeAttributes)0x00004000;
            TypeAttributes attrs = src.Attributes & ~WindowsRuntimeFlag;

            var dst = new TypeDefinition(
                parent == null ? src.Namespace : "",
                src.Name,
                attrs);

            if (parent == null) dstMod.Types.Add(dst);
            else parent.NestedTypes.Add(dst);

            map[src.FullName] = dst;

            foreach (var nested in src.NestedTypes)
                DeclareTypeRecursive(nested, dstMod, dst, map);
        }

        private static void WireBaseAndInterfaces(
            TypeDefinition src,
            Dictionary<string, TypeDefinition> map,
            ModuleDefinition dstMod)
        {
            TypeDefinition dst = map[src.FullName];

            if (src.BaseType != null)
            {
                try { dst.BaseType = ResolveTypeRef(src.BaseType, map, dstMod); }
                catch (Exception ex)
                {
                    Log.Warn(LogCategory.AppInstall, $"WinmdStubber: could not wire base of {src.FullName}: {ex.Message}");
                }
            }

            foreach (var iface in src.Interfaces)
            {
                try
                {
                    var resolved = ResolveTypeRef(iface.InterfaceType, map, dstMod);
                    dst.Interfaces.Add(new InterfaceImplementation(resolved));
                }
                catch (Exception ex)
                {
                    Log.Warn(LogCategory.AppInstall, $"WinmdStubber: skipping interface {iface.InterfaceType.FullName} on {src.FullName}: {ex.Message}");
                }
            }

            foreach (var nested in src.NestedTypes) WireBaseAndInterfaces(nested, map, dstMod);
        }

        private static void PopulateMembers(
            TypeDefinition src,
            Dictionary<string, TypeDefinition> map,
            ModuleDefinition dstMod)
        {
            TypeDefinition dst = map[src.FullName];

            // Fields — required for enum values, struct layouts, and any constants the user reads.
            foreach (var f in src.Fields)
            {
                try
                {
                    var ft = ResolveTypeRef(f.FieldType, map, dstMod);
                    var nf = new FieldDefinition(f.Name, f.Attributes, ft);
                    if (f.HasConstant)
                    {
                        nf.Constant = f.Constant;
                        nf.HasConstant = true;
                    }
                    dst.Fields.Add(nf);
                }
                catch (Exception ex)
                {
                    Log.Warn(LogCategory.AppInstall, $"WinmdStubber: skipping field {src.FullName}.{f.Name}: {ex.Message}");
                }
            }

            foreach (var m in src.Methods)
            {
                try
                {
                    var nm = CloneMethod(m, dst, map, dstMod);
                    dst.Methods.Add(nm);
                }
                catch (Exception ex)
                {
                    Log.Warn(LogCategory.AppInstall, $"WinmdStubber: skipping method {src.FullName}.{m.Name}: {ex.Message}");
                }
            }

            // Properties — point at the cloned getter/setter we just added.
            foreach (var p in src.Properties)
            {
                try
                {
                    var pt = ResolveTypeRef(p.PropertyType, map, dstMod);
                    var np = new PropertyDefinition(p.Name, p.Attributes, pt);
                    if (p.GetMethod != null) np.GetMethod = FindMethod(dst, p.GetMethod);
                    if (p.SetMethod != null) np.SetMethod = FindMethod(dst, p.SetMethod);
                    dst.Properties.Add(np);
                }
                catch (Exception ex)
                {
                    Log.Warn(LogCategory.AppInstall, $"WinmdStubber: skipping property {src.FullName}.{p.Name}: {ex.Message}");
                }
            }

            foreach (var e in src.Events)
            {
                try
                {
                    var et = ResolveTypeRef(e.EventType, map, dstMod);
                    var ne = new EventDefinition(e.Name, e.Attributes, et);
                    if (e.AddMethod != null) ne.AddMethod = FindMethod(dst, e.AddMethod);
                    if (e.RemoveMethod != null) ne.RemoveMethod = FindMethod(dst, e.RemoveMethod);
                    if (e.InvokeMethod != null) ne.InvokeMethod = FindMethod(dst, e.InvokeMethod);
                    dst.Events.Add(ne);
                }
                catch (Exception ex)
                {
                    Log.Warn(LogCategory.AppInstall, $"WinmdStubber: skipping event {src.FullName}.{e.Name}: {ex.Message}");
                }
            }

            foreach (var nested in src.NestedTypes) PopulateMembers(nested, map, dstMod);
        }

        private static MethodDefinition? FindMethod(TypeDefinition type, MethodDefinition srcMethod)
        {
            // Match by name + parameter count (good enough for accessor lookup; .winmd accessors
            // are uniquely named within a property/event scope).
            return type.Methods.FirstOrDefault(m =>
                m.Name == srcMethod.Name &&
                m.Parameters.Count == srcMethod.Parameters.Count);
        }

        private static MethodDefinition CloneMethod(
            MethodDefinition src,
            TypeDefinition dstType,
            Dictionary<string, TypeDefinition> map,
            ModuleDefinition dstMod)
        {
            var ret = ResolveTypeRef(src.ReturnType, map, dstMod);

            // WinRT allows protected methods to implement public interface members. CLR's loader
            // rejects this with "does not have an implementation". Force all member access to
            // Public on the stub so the inherited interface contracts are satisfied.
            var attrs = (src.Attributes & ~Mono.Cecil.MethodAttributes.MemberAccessMask)
                        | Mono.Cecil.MethodAttributes.Public;
            var nm = new MethodDefinition(src.Name, attrs, ret);

            // Replace WinRT runtime impl with managed IL — we'll provide a body.
            nm.ImplAttributes = (src.ImplAttributes
                                  & ~MethodImplAttributes.Runtime
                                  & ~MethodImplAttributes.CodeTypeMask)
                                | MethodImplAttributes.IL
                                | MethodImplAttributes.Managed;

            // Drop Pinvoke / unmanaged flag: native bodies don't exist here.
            nm.IsPInvokeImpl = false;

            foreach (var p in src.Parameters)
            {
                nm.Parameters.Add(new ParameterDefinition(
                    p.Name,
                    p.Attributes,
                    ResolveTypeRef(p.ParameterType, map, dstMod)));
            }

            // Abstract methods (interface members) and instance ctors of interfaces stay bodiless.
            if (nm.IsAbstract || dstType.IsInterface)
            {
                // Cecil exposes HasBody read-only; leaving Body unset is enough — the writer
                // serializes the method without a body when IsAbstract is true.
                return nm;
            }

            // Stub body: return default(T).
            nm.Body = new MethodBody(nm) { InitLocals = true };
            ILProcessor il = nm.Body.GetILProcessor();

            if (ret.FullName == "System.Void")
            {
                // Instance ctors must call a base ctor before ret; bare 'ret' is invalid IL and
                // some runtime paths throw on it.
                if (nm.IsConstructor && !nm.IsStatic && dstType.BaseType != null && !dstType.IsInterface)
                {
                    var objCtor = dstMod.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!);
                    il.Append(il.Create(OpCodes.Ldarg_0));
                    il.Append(il.Create(OpCodes.Call, objCtor));
                }
                il.Append(il.Create(OpCodes.Ret));
            }
            else
            {
                EmitDefaultReturn(il, nm.Body, ret);
            }

            return nm;
        }

        /// <summary>
        /// Emit IL that pushes a default value of <paramref name="t"/> on the stack and returns it.
        /// Works for value types (initobj) and reference types (ldnull).
        /// </summary>
        private static void EmitDefaultReturn(ILProcessor il, MethodBody body, TypeReference t)
        {
            // Reference types and pointer / by-ref types: ldnull / conv.* and return.
            if (!IsLikelyValueType(t))
            {
                il.Append(il.Create(OpCodes.Ldnull));
                il.Append(il.Create(OpCodes.Ret));
                return;
            }

            // Value type: declare a local, initobj, load, return.
            var local = new VariableDefinition(t);
            body.Variables.Add(local);
            il.Append(il.Create(OpCodes.Ldloca_S, local));
            il.Append(il.Create(OpCodes.Initobj, t));
            il.Append(il.Create(OpCodes.Ldloc_S, local));
            il.Append(il.Create(OpCodes.Ret));
        }

        private static bool IsLikelyValueType(TypeReference t)
        {
            // Quick checks first — avoid resolving when we already know.
            if (t is ByReferenceType || t is PointerType || t is ArrayType) return false;
            if (t.IsPrimitive) return true;

            string fn = t.FullName;
            if (fn == "System.Void" || fn == "System.IntPtr" || fn == "System.UIntPtr") return true;

            // Known WinRT value-type structs — WindowsTypeSynthesizer emits these as real structs
            // (see its KnownValueTypeFields), so stub members that return them must default via
            // initobj, not ldnull. The synthesized type isn't on disk yet when we stub, so we
            // can't Resolve() it — recognise it by name.
            if (fn == "Windows.Foundation.Size" || fn == "Windows.Foundation.Point" ||
                fn == "Windows.Foundation.Rect")
                return true;

            // Try to resolve. ResolveTypeRef may have given us a TypeDefinition (local clone).
            try
            {
                if (t is TypeDefinition td) return td.IsValueType;
                var resolved = t.Resolve();
                if (resolved != null) return resolved.IsValueType;
            }
            catch { /* unresolvable — treat as ref type and emit ldnull */ }

            return false;
        }

        private static AssemblyNameReference GetOrAddAssemblyRef(ModuleDefinition mod, string name)
        {
            foreach (var r in mod.AssemblyReferences)
                if (r.Name == name) return r;
            var anr = new AssemblyNameReference(name, new Version(8, 0, 0, 0));
            mod.AssemblyReferences.Add(anr);
            return anr;
        }

        /// <summary>
        /// Map a TypeReference from the source module into something valid for dstMod. Handles
        /// generic instances, arrays, by-ref, and pointer types recursively. If a plain type
        /// matches a clone we made earlier in this assembly, reuse the local TypeDefinition.
        /// </summary>
        private static TypeReference ResolveTypeRef(
            TypeReference src,
            Dictionary<string, TypeDefinition> map,
            ModuleDefinition dstMod)
        {
            switch (src)
            {
                case GenericInstanceType git:
                {
                    var elem = ResolveTypeRef(git.ElementType, map, dstMod);
                    var clone = new GenericInstanceType(elem);
                    foreach (var arg in git.GenericArguments)
                        clone.GenericArguments.Add(ResolveTypeRef(arg, map, dstMod));
                    return clone;
                }
                case ArrayType at:
                {
                    var elem = ResolveTypeRef(at.ElementType, map, dstMod);
                    return at.IsVector ? new ArrayType(elem) : new ArrayType(elem, at.Rank);
                }
                case ByReferenceType brt:
                    return new ByReferenceType(ResolveTypeRef(brt.ElementType, map, dstMod));
                case PointerType pt:
                    return new PointerType(ResolveTypeRef(pt.ElementType, map, dstMod));
                case GenericParameter gp:
                    // Generic parameter references — Cecil's import preserves identity for these
                    // when the owner has been cloned; for our shapes (rare in WinRT components)
                    // we just import as-is.
                    return dstMod.ImportReference(gp);
            }

            // WinRT type with a BCL projection (IMap`2 → IDictionary`2, etc.). Substitute the
            // BCL type so stubbed signatures match the projected types the user's managed code
            // is compiled against. Matched on the open generic's FullName; the surrounding
            // GenericInstanceType case re-applies the captured arguments.
            if (WinRtProjections.TryGetValue(src.FullName, out var netType))
                return dstMod.ImportReference(netType);

            // WinRT value type the projection relocates into System.Runtime.WindowsRuntime —
            // re-scope so the stub names the same type the user IL does (see ProjectedValueTypes).
            if (ProjectedValueTypes.Contains(src.FullName))
                return new TypeReference(src.Namespace, src.Name, dstMod,
                    GetOrAddAssemblyRef(dstMod, ProjectedValueTypeScope), valueType: true);

            // Plain TypeReference. Prefer our local clone if there is one.
            if (map.TryGetValue(src.FullName, out var local)) return local;

            // External type — import a reference to it. The runtime will resolve at load time.
            return dstMod.ImportReference(src);
        }
    }
}

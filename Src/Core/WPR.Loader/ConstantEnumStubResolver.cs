using System;
using System.Collections.Generic;

using Mono.Cecil;

namespace WPR
{
    /// <summary>
    /// The assembly resolver <see cref="ApplicationPatcher.PatchDll"/> hands to Cecil: a
    /// <see cref="DefaultAssemblyResolver"/> plus one narrow fallback, for the single resolve
    /// Cecil performs while *writing* a module.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The resolve.</b> A constant — a <c>const</c> field, a parameter default, a property
    /// default — records its value in the Constant table together with an <c>ElementType</c>.
    /// Cecil does not preserve the one it read; it recomputes it in
    /// <c>MetadataBuilder.GetConstantType</c>, and for anything that is not a corlib primitive
    /// that means <c>CheckedResolve()</c> on the declared type, purely to ask "is this an enum,
    /// and if so what integer is behind it?". Nothing else in a write resolves.
    /// </para>
    /// <para>
    /// <b>Why it fails.</b> The declared type is normally one of ours by the time Write runs —
    /// the patcher has just rescoped <c>Microsoft.Xna.Framework.Graphics</c> onto
    /// <c>WPR.Framework.Xna</c> — so Cecil goes looking for <c>WPR.Framework.Xna.dll</c> on
    /// disk. On Windows it finds it: <see cref="AppContext.BaseDirectory"/> is the WPR bin.
    /// <b>On Android there are no managed assemblies on disk at all</b> — they are embedded in
    /// the APK and mapped out of it — so the resolve throws, <see cref="ApplicationPatcher"/>
    /// logs the write failure and leaves that one DLL UNPATCHED. The game then still names
    /// <c>Microsoft.Xna.Framework.Game, PublicKeyToken=842cf8be1de50553</c> at launch and dies
    /// with a FileNotFoundException wrapped in an AggregateException, which reads as a missing
    /// XNA runtime rather than as a failed install.
    /// </para>
    /// <para>
    /// Measured on a phone with 36 games installed: <b>Beards and Beaks</b> (a
    /// <c>SpriteEffects</c> const field) and <b>Chickens Can't Fly</b> (a GamerServices enum as
    /// a parameter default) each lost their main assembly this way and neither would launch.
    /// Both stack traces are the same five frames, ending in <c>GetConstantType</c>.
    /// </para>
    /// <para>
    /// <b>The fallback.</b> When the real assembly cannot be found, hand Cecil a synthetic one
    /// carrying an enum shell for each type a constant in this module is declared as. The
    /// underlying integer type is taken from the constant's own boxed value, which Cecil read
    /// out of the original Constant blob — so it *is* what the game's compiler wrote, not a
    /// guess, and the emitted row is identical to the one a real assembly would have produced.
    /// The stub is consulted and thrown away; it is never written, never referenced, and never
    /// consulted at all on a machine where the real assembly is present.
    /// </para>
    /// <para>
    /// Deliberately narrow. A resolve for any *other* reason still throws, and still lands in
    /// the existing "left UNPATCHED" log — worse than fixing it, better than a stub that
    /// answers questions it has no business answering. Widening this into "unpack the APK's
    /// assemblies so Cecil can read them" was the alternative; it costs several MB of duplicated
    /// assemblies on disk and a dependency on .NET for Android's assembly-store layout, to serve
    /// a question the constant already answers.
    /// </para>
    /// </remarks>
    internal sealed class ConstantEnumStubResolver : IAssemblyResolver
    {
        private readonly DefaultAssemblyResolver real = new DefaultAssemblyResolver();

        /// <summary>Synthesised stand-ins, keyed by the assembly reference's FullName.</summary>
        private readonly Dictionary<string, AssemblyDefinition> stubs =
            new Dictionary<string, AssemblyDefinition>(StringComparer.Ordinal);

        internal void AddSearchDirectory(string directory)
        {
            real.AddSearchDirectory(directory);
        }

        public AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            try { return real.Resolve(name); }
            catch (AssemblyResolutionException) { return Stub(name); }
        }

        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            try { return real.Resolve(name, parameters); }
            catch (AssemblyResolutionException) { return Stub(name); }
        }

        private AssemblyDefinition Stub(AssemblyNameReference name)
        {
            if (stubs.TryGetValue(name.FullName, out AssemblyDefinition? stub))
            {
                return stub;
            }

            // Nothing primed for this scope, so the resolve is for something other than a
            // constant's type and inventing an answer would be guessing. Let it throw.
            throw new AssemblyResolutionException(name);
        }

        public void Dispose()
        {
            real.Dispose();
            foreach (AssemblyDefinition stub in stubs.Values)
            {
                stub.Dispose();
            }
            stubs.Clear();
        }

        /// <summary>
        /// Walks every constant in <paramref name="module"/> and prepares a stub for each one
        /// whose declared type lives in an assembly that cannot be found. Call this AFTER all
        /// rescoping and immediately before Write — the scope Cecil asks for is the renamed one,
        /// not the one the game shipped with.
        /// </summary>
        /// <returns>How many enum shells were synthesised.</returns>
        internal int PrimeConstantTypes(ModuleDefinition module)
        {
            int synthesized = 0;

            foreach (TypeDefinition type in module.GetTypes())
            {
                foreach (FieldDefinition field in type.Fields)
                {
                    if (field.HasConstant)
                    {
                        synthesized += Prime(field.FieldType, field.Constant);
                    }
                }

                foreach (PropertyDefinition property in type.Properties)
                {
                    if (property.HasConstant)
                    {
                        synthesized += Prime(property.PropertyType, property.Constant);
                    }
                }

                foreach (MethodDefinition method in type.Methods)
                {
                    // A default value on the return "parameter" is legal metadata and Cecil
                    // writes it through the same AddConstant path, so it resolves too.
                    if (method.MethodReturnType.HasConstant)
                    {
                        synthesized += Prime(
                            method.MethodReturnType.ReturnType,
                            method.MethodReturnType.Constant);
                    }

                    if (!method.HasParameters)
                    {
                        continue;
                    }

                    foreach (ParameterDefinition parameter in method.Parameters)
                    {
                        if (parameter.HasConstant)
                        {
                            synthesized += Prime(parameter.ParameterType, parameter.Constant);
                        }
                    }
                }
            }

            return synthesized;
        }

        private int Prime(TypeReference? type, object? constant)
        {
            // Cecil returns ElementType.Class for a null constant without resolving anything,
            // and a non-integral value cannot be an enum — either way there is no question to
            // answer.
            if (type == null || constant == null || !IsIntegral(constant))
            {
                return 0;
            }

            // A TypeDefinition resolves to itself, and a generic parameter or instantiation is
            // answered by GetConstantType without a resolve.
            if (type is TypeDefinition || type.IsGenericParameter || type.IsGenericInstance)
            {
                return 0;
            }

            // Only a type Cecil cannot name with an ElementType reaches CheckedResolve:
            // GetConstantType switches on the reference's etype and resolves ONLY in the
            // ElementType.None arm. MetadataType reports that arm as ValueType or Class, so
            // anything else here is a primitive Cecil answers from the signature alone.
            //
            // This gate is load-bearing, not an optimisation. A plain `const int` has FieldType
            // System.Int32, whose Scope is the corlib AssemblyNameReference — so without it every
            // such field tried to resolve mscorlib, and on Android (where nothing resolves) that
            // minted a stub assembly literally named "mscorlib". See the note in Stub creation
            // for what that then did. Contre Jour is the reference case: Mokus2D.dll and
            // FarseerPhysicsXNA.dll both failed to patch, so the game launched against unpatched
            // IL still naming Microsoft.Xna.Framework.Game and died before its first frame.
            if (type.MetadataType is not (MetadataType.ValueType or MetadataType.Class))
            {
                return 0;
            }

            // A ModuleDefinition or ModuleReference scope never reaches the assembly resolver.
            if (type.Scope is not AssemblyNameReference scope)
            {
                return 0;
            }

            if (!stubs.TryGetValue(scope.FullName, out AssemblyDefinition? stub))
            {
                try
                {
                    real.Resolve(scope);
                    return 0;   // the real assembly is right there; leave Cecil to it
                }
                catch (AssemblyResolutionException)
                {
                    // NOT named after the scope, deliberately. Cecil decides a module IS the core
                    // library from its assembly name alone when there is no image to check the
                    // AssemblyRef table against (ModuleDefinition.IsCoreLibrary), and a created
                    // stub never has an image. So a stub named "mscorlib" — or System.Runtime,
                    // System.Private.CoreLib, netstandard — gets a CoreTypeSystem, whose
                    // .Int32/.Object look System.Int32 up INSIDE the stub instead of referencing
                    // corlib. The stub is empty and imageless, so that read NullReferences out of
                    // Mono.Cecil and the whole DLL is logged as "left UNPATCHED".
                    //
                    // Nothing checks this name: the resolver is asked for `scope` and hands back
                    // an AssemblyDefinition, and Cecil then looks the type up in its main module
                    // without comparing identities. The dictionary is still keyed by the real
                    // scope, so lookups are unaffected.
                    stub = AssemblyDefinition.CreateAssembly(
                        new AssemblyNameDefinition(
                            scope.Name + "!wpr-constant-stub",
                            scope.Version ?? new Version(0, 0, 0, 0)),
                        scope.Name,
                        ModuleKind.Dll);

                    stubs[scope.FullName] = stub;
                }
            }

            return AddEnumShell(stub.MainModule, type, constant) ? 1 : 0;
        }

        /// <summary>
        /// Adds the one shape <c>GetConstantType</c> interrogates: a type whose base is
        /// <c>System.Enum</c> (that is Cecil's whole test for <c>IsEnum</c>) holding a single
        /// instance field, whose type is what <c>GetEnumUnderlyingType</c> returns.
        /// </summary>
        private static bool AddEnumShell(ModuleDefinition stub, TypeReference type, object constant)
        {
            if (Find(stub, type) != null)
            {
                return false;
            }

            TypeDefinition? declaring = type.IsNested ? AddShell(stub, type.DeclaringType) : null;

            TypeDefinition shell = new TypeDefinition(
                type.Namespace,
                type.Name,
                (declaring == null ? TypeAttributes.Public : TypeAttributes.NestedPublic)
                    | TypeAttributes.Sealed,
                new TypeReference("System", "Enum", stub, stub.TypeSystem.CoreLibrary));

            shell.Fields.Add(new FieldDefinition(
                "value__",
                FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName,
                UnderlyingTypeOf(stub, constant)));

            Attach(stub, declaring, shell);
            return true;
        }

        /// <summary>
        /// A plain class shell, for the declaring types of a nested enum. Cecil resolves a
        /// nested type by resolving its declaring type first, so the chain has to exist.
        /// </summary>
        private static TypeDefinition AddShell(ModuleDefinition stub, TypeReference type)
        {
            TypeDefinition? existing = Find(stub, type);
            if (existing != null)
            {
                return existing;
            }

            TypeDefinition? declaring = type.IsNested ? AddShell(stub, type.DeclaringType) : null;

            TypeDefinition shell = new TypeDefinition(
                type.Namespace,
                type.Name,
                declaring == null ? TypeAttributes.Public : TypeAttributes.NestedPublic,
                stub.TypeSystem.Object);

            Attach(stub, declaring, shell);
            return shell;
        }

        private static void Attach(
            ModuleDefinition stub, TypeDefinition? declaring, TypeDefinition shell)
        {
            if (declaring == null)
            {
                stub.Types.Add(shell);
            }
            else
            {
                declaring.NestedTypes.Add(shell);
            }
        }

        private static TypeDefinition? Find(ModuleDefinition stub, TypeReference type)
        {
            if (!type.IsNested)
            {
                return stub.GetType(type.Namespace, type.Name);
            }

            TypeDefinition? declaring = Find(stub, type.DeclaringType);
            if (declaring == null)
            {
                return null;
            }

            foreach (TypeDefinition nested in declaring.NestedTypes)
            {
                if (string.Equals(nested.Name, type.Name, StringComparison.Ordinal))
                {
                    return nested;
                }
            }

            return null;
        }

        private static bool IsIntegral(object constant)
        {
            return constant is bool || constant is char
                || constant is sbyte || constant is byte
                || constant is short || constant is ushort
                || constant is int || constant is uint
                || constant is long || constant is ulong;
        }

        /// <summary>
        /// The enum's underlying type, read off the constant Cecil already parsed out of the
        /// original Constant blob. That blob's ElementType was written by the game's own
        /// compiler, so this is a record of the answer rather than an inference about it.
        /// </summary>
        private static TypeReference UnderlyingTypeOf(ModuleDefinition stub, object constant)
        {
            return constant switch
            {
                bool => stub.TypeSystem.Boolean,
                char => stub.TypeSystem.Char,
                sbyte => stub.TypeSystem.SByte,
                byte => stub.TypeSystem.Byte,
                short => stub.TypeSystem.Int16,
                ushort => stub.TypeSystem.UInt16,
                int => stub.TypeSystem.Int32,
                uint => stub.TypeSystem.UInt32,
                long => stub.TypeSystem.Int64,
                _ => stub.TypeSystem.UInt64,
            };
        }
    }
}

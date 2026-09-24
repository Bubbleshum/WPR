using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace WPR.WindowsCompability
{
    /// <summary>
    /// Answers <see cref="MemberInfo.MetadataToken"/> with the value the member had in the
    /// game's <em>pristine</em> assembly, before WPR patched it.
    ///
    /// <para>Every call site of <c>get_MetadataToken</c> in a patched game is rewritten to
    /// <see cref="Resolve"/> by <c>ApplicationPatcher.PreserveOriginalMetadataTokens</c>, which
    /// also embeds the lookup table this reads. Both halves exist for one reason: <b>Cecil does
    /// not preserve TypeDef row ids.</b> It re-emits the TypeDef table in its own traversal order
    /// — every top-level type immediately followed by its nested types, depth first — and an
    /// assembly whose original table interleaved them differently comes back renumbered, even
    /// though nothing about the types themselves changed.</para>
    ///
    /// <para>That is invisible to ordinary game code and fatal to an obfuscator. Eazfuscator.NET
    /// derives its string-decryption key from the metadata tokens of its own helper types, so a
    /// renumbered table yields a wrong key, and the key is used as a byte offset into the
    /// encrypted string blob: the game seeks an <c>UnmanagedMemoryStream</c> to a garbage
    /// position and dies on the first string it decrypts. The Treasures of Montezuma is the
    /// reference case — <c>ArgumentOutOfRangeException: value ('-180695550') must be a
    /// non-negative value</c> out of <c>Game..ctor</c>, before a single frame.</para>
    ///
    /// <para><b>Types only.</b> MethodDef and FieldDef rows are renumbered by the same rewrite and
    /// are deliberately not remapped: nothing in the library keys on them, and a table covering
    /// them would be far larger. A non-<see cref="Type"/> member therefore falls through to its
    /// real (post-patch) token, which is the behaviour that was there before this shim.</para>
    ///
    /// <para><b>No inverse.</b> A game that fed a token back to <c>Module.ResolveType</c> would see
    /// a pre-patch token it cannot resolve, so the patcher skips any module that calls the
    /// <c>Module.Resolve*</c> family outright rather than half-remapping it.</para>
    /// </summary>
    public static class OriginalMetadataTokens
    {
        /// <summary>
        /// Name of the manifest resource the patcher embeds. Read by name rather than by index:
        /// it sits alongside whatever resources the game already shipped.
        /// </summary>
        public const string ResourceName = "WPR.OriginalMetadataTokens";

        /// <summary>
        /// Per-module table, keyed by the reflection spelling of the type name (nested types use
        /// '+'). A module with no table caches a null so the miss is paid once.
        /// </summary>
        private static readonly ConcurrentDictionary<Module, Dictionary<string, int>?> Maps =
            new ConcurrentDictionary<Module, Dictionary<string, int>?>();

        /// <summary>
        /// Replacement for <c>MemberInfo.get_MetadataToken</c> at every call site in a patched
        /// game. The instance becomes argument zero, so the evaluation stack is unchanged and
        /// only the callee moves — the same shape <c>RedirectIsolatedStorageCalls</c> uses.
        /// </summary>
        public static int Resolve(MemberInfo member)
        {
            if (member == null)
            {
                return 0;
            }

            if (member is not Type type)
            {
                return member.MetadataToken;
            }

            // A constructed generic reports its definition's token anyway; ask the definition so
            // the name we look up is the one the patcher recorded.
            if (type.IsConstructedGenericType)
            {
                type = type.GetGenericTypeDefinition();
            }

            int token = type.MetadataToken;
            string? name = type.FullName;
            if (name == null)
            {
                // An open generic parameter has no FullName and no row of its own.
                return token;
            }

            Dictionary<string, int>? map = MapFor(type.Module);
            if (map != null && map.TryGetValue(name, out int original))
            {
                return original;
            }

            return token;
        }

        private static Dictionary<string, int>? MapFor(Module module)
        {
            return Maps.GetOrAdd(module, static m =>
            {
                try
                {
                    using Stream? stream = m.Assembly.GetManifestResourceStream(ResourceName);
                    if (stream == null)
                    {
                        return null;
                    }

                    using BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, false);
                    int count = reader.ReadInt32();
                    if (count <= 0)
                    {
                        return null;
                    }

                    Dictionary<string, int> map =
                        new Dictionary<string, int>(count, StringComparer.Ordinal);
                    for (int i = 0; i < count; i++)
                    {
                        string name = reader.ReadString();
                        map[name] = reader.ReadInt32();
                    }

                    return map;
                }
                catch (Exception)
                {
                    // A torn or absent table must not take the game down: every caller can live
                    // with the real token, which is what it had before this existed.
                    return null;
                }
            });
        }
    }
}

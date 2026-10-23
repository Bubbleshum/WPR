using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Serialization;
using System.Xml;

namespace WPR.WindowsCompability
{
    /// <summary>
    /// Resolves a data contract the serializer does not know to a type in any loaded assembly,
    /// so a document whose values are a game's own classes can be read back without a
    /// known-types list. <see cref="IsolatedStorageSettings2"/> reads with it: Silverlight kept
    /// the list beside the settings, WPR's file has none.
    /// </summary>
    /// <remarks>
    /// A contract's name and namespace come from its <see cref="DataContractAttribute"/>, then
    /// the assembly's <see cref="ContractNamespaceAttribute"/>, then the serializer's default
    /// (<c>http://schemas.datacontract.org/2004/07/</c> + the CLR namespace). Generic types are
    /// not matched by name; the serializer's own resolver handles the ones it knows. Game
    /// assemblies (collectible contexts) are searched before the Default context.
    /// <para>Collections of framework types are named in the serializer's Arrays namespace
    /// (<c>ArrayOfKeyValueOfstringint</c>, <c>ArrayOfstring</c>) and defined by no loaded type,
    /// so they are built from the name: <c>ArrayOfKeyValueOf</c>KV is a
    /// <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/>, any other <c>ArrayOf</c>T
    /// a <see cref="System.Collections.Generic.List{T}"/> (an array and a list share the name, and
    /// the file does not say which it was). Gravity Guy 2 keeps a Dictionary&lt;string, int&gt; and
    /// a Dictionary&lt;string, Guid&gt; beside its save; unresolved, the read failed and the whole
    /// file was thrown away at every launch.</para>
    /// </remarks>
    internal sealed class LoadedTypeContractResolver : DataContractResolver
    {
        private const string DefaultNamespacePrefix = "http://schemas.datacontract.org/2004/07/";

        private const string ArraysNamespace = "http://schemas.microsoft.com/2003/10/Serialization/Arrays";

        // The serializer's names for the primitives it knows (XML Schema names, plus its own
        // char, guid and duration). anyType is object.
        private static readonly System.Collections.Generic.Dictionary<string, Type> Primitives = new()
        {
            ["anyType"] = typeof(object), ["string"] = typeof(string), ["boolean"] = typeof(bool),
            ["int"] = typeof(int), ["long"] = typeof(long), ["short"] = typeof(short),
            ["byte"] = typeof(sbyte), ["unsignedByte"] = typeof(byte), ["unsignedShort"] = typeof(ushort),
            ["unsignedInt"] = typeof(uint), ["unsignedLong"] = typeof(ulong), ["float"] = typeof(float),
            ["double"] = typeof(double), ["decimal"] = typeof(decimal), ["dateTime"] = typeof(DateTime),
            ["char"] = typeof(char), ["guid"] = typeof(Guid), ["duration"] = typeof(TimeSpan),
            ["base64Binary"] = typeof(byte[]), ["anyURI"] = typeof(Uri),
        };

        private static readonly ConcurrentDictionary<(string Name, string Namespace), Type?> Cache = new();

        public override Type? ResolveName(string typeName, string? typeNamespace, Type? declaredType, DataContractResolver knownTypeResolver)
        {
            Type? known = knownTypeResolver.ResolveName(typeName, typeNamespace, declaredType, null);
            if (known != null)
            {
                return known;
            }

            if (typeNamespace == ArraysNamespace)
            {
                return ArraysContract(typeName);
            }

            return Find(typeName, typeNamespace ?? string.Empty);
        }

        /// <summary>The collection a name in the Arrays namespace stands for, or null.</summary>
        internal static Type? ArraysContract(string name)
        {
            if (!name.StartsWith("ArrayOf", StringComparison.Ordinal))
            {
                return null;
            }

            string item = name.Substring("ArrayOf".Length);
            if (item.StartsWith("KeyValueOf", StringComparison.Ordinal))
            {
                string pair = item.Substring("KeyValueOf".Length);
                for (int split = 1; split < pair.Length; split++)
                {
                    Type? key = ItemContract(pair.Substring(0, split));
                    Type? value = key == null ? null : ItemContract(pair.Substring(split));
                    if (value != null)
                    {
                        return typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(key!, value);
                    }
                }

                return null;
            }

            Type? element = ItemContract(item);
            return element == null ? null : typeof(System.Collections.Generic.List<>).MakeGenericType(element);
        }

        private static Type? ItemContract(string name) =>
            Primitives.TryGetValue(name, out Type? primitive) ? primitive : ArraysContract(name);

        public override bool TryResolveType(Type type, Type? declaredType, DataContractResolver knownTypeResolver,
            out XmlDictionaryString? typeName, out XmlDictionaryString? typeNamespace)
        {
            return knownTypeResolver.TryResolveType(type, declaredType, null, out typeName, out typeNamespace);
        }

        /// <summary>The loaded type whose data contract is <paramref name="name"/> in <paramref name="ns"/>, or null.</summary>
        internal static Type? Find(string name, string ns)
        {
            // A miss is not cached: the assembly that defines it may not be loaded yet.
            if (Cache.TryGetValue((name, ns), out Type? cached) && cached != null)
            {
                return cached;
            }

            Type? found = null;
            foreach (Assembly assembly in AssembliesGameFirst())
            {
                found = FindIn(assembly, name, ns);
                if (found != null)
                {
                    break;
                }
            }

            Cache[(name, ns)] = found;
            return found;
        }

        private static System.Collections.Generic.IEnumerable<Assembly> AssembliesGameFirst()
        {
            foreach (AssemblyLoadContext context in AssemblyLoadContext.All.Where(c => c.IsCollectible))
            {
                foreach (Assembly assembly in context.Assemblies)
                {
                    yield return assembly;
                }
            }

            foreach (Assembly assembly in AssemblyLoadContext.Default.Assemblies)
            {
                if (!assembly.IsDynamic)
                {
                    yield return assembly;
                }
            }
        }

        private static Type? FindIn(Assembly assembly, string name, string ns)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray()!;
            }
            catch
            {
                return null;
            }

            foreach (Type type in types)
            {
                if (type.IsGenericTypeDefinition || type.ContainsGenericParameters)
                {
                    continue;
                }

                if (ContractName(type) == name && ContractNamespace(type) == ns)
                {
                    return type;
                }
            }

            return null;
        }

        private static string ContractName(Type type)
        {
            DataContractAttribute? contract = type.GetCustomAttribute<DataContractAttribute>(inherit: false);
            if (!string.IsNullOrEmpty(contract?.Name))
            {
                return contract!.Name!;
            }

            // A nested type's default contract name is Outer.Inner.
            return type.IsNested && type.DeclaringType != null ? ContractName(type.DeclaringType) + "." + type.Name : type.Name;
        }

        private static string ContractNamespace(Type type)
        {
            DataContractAttribute? contract = type.GetCustomAttribute<DataContractAttribute>(inherit: false);
            if (contract?.Namespace != null)
            {
                return contract.Namespace;
            }

            string clrNamespace = type.Namespace ?? string.Empty;
            foreach (ContractNamespaceAttribute mapping in type.Assembly.GetCustomAttributes<ContractNamespaceAttribute>())
            {
                if (mapping.ClrNamespace == clrNamespace)
                {
                    return mapping.ContractNamespace;
                }
            }

            return DefaultNamespacePrefix + clrNamespace;
        }
    }
}

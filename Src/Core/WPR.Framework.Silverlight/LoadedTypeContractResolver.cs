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
    /// not matched here; the serializer's own resolver handles the ones it knows. Game
    /// assemblies (collectible contexts) are searched before the Default context.
    /// </remarks>
    internal sealed class LoadedTypeContractResolver : DataContractResolver
    {
        private const string DefaultNamespacePrefix = "http://schemas.datacontract.org/2004/07/";

        private static readonly ConcurrentDictionary<(string Name, string Namespace), Type?> Cache = new();

        public override Type? ResolveName(string typeName, string? typeNamespace, Type? declaredType, DataContractResolver knownTypeResolver)
        {
            Type? known = knownTypeResolver.ResolveName(typeName, typeNamespace, declaredType, null);
            if (known != null)
            {
                return known;
            }

            return Find(typeName, typeNamespace ?? string.Empty);
        }

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

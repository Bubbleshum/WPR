using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace WPR.Wp8Native
{
    /// <summary>
    /// Vtable layouts read from a WinRT metadata file (<c>.winmd</c>) the game ships in its XAP.
    /// </summary>
    /// <remarks>
    /// A WinRT interface's vtable is IInspectable's six slots followed by its methods in
    /// declaration order, so the metadata is the fact and a guessed slot number is a guess - the
    /// same lesson as <c>IPointerPoint</c> in WinRtRuntime. A runtime class has no vtable of its
    /// own: its instance methods live on the interfaces it implements, and its constructors and
    /// statics on factory/static interfaces named by attributes.
    /// </remarks>
    public sealed class WinmdReader
    {
        public const int InspectableSlots = 6;

        /// <summary>Methods of one interface, in vtable order (slot = <see cref="InspectableSlots"/> + index).</summary>
        public sealed record InterfaceLayout(string FullName, IReadOnlyList<MethodLayout> Methods, Guid? Iid = null)
        {
            public MethodLayout? Find(string name) => Methods.FirstOrDefault(m => m.Name == name);
        }

        /// <param name="ParameterTypes">Each parameter's type name, as declared.</param>
        public sealed record MethodLayout(string Name, int Slot, IReadOnlyList<string> ParameterTypes, string ReturnType);

        private readonly Dictionary<string, InterfaceLayout> _interfaces = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _classInterfaces = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _defaultInterface = new(StringComparer.Ordinal);

        public static WinmdReader Load(params string[] paths)
        {
            WinmdReader reader = new();
            foreach (string path in paths)
            {
                reader.Read(path);
            }

            return reader;
        }

        public IEnumerable<string> InterfaceNames => _interfaces.Keys;

        public InterfaceLayout? Interface(string fullName) => _interfaces.GetValueOrDefault(fullName);

        /// <summary>The runtime class's default interface - the vtable an activated instance hands back.</summary>
        public InterfaceLayout? DefaultInterface(string className)
            => _defaultInterface.TryGetValue(className, out string? name) ? Interface(name) : null;

        /// <summary>Every interface the class implements, default first.</summary>
        public IEnumerable<InterfaceLayout> InterfacesOf(string className)
        {
            if (!_classInterfaces.TryGetValue(className, out List<string>? names))
            {
                yield break;
            }

            foreach (string name in names.OrderBy(n => _defaultInterface.GetValueOrDefault(className) == n ? 0 : 1))
            {
                if (Interface(name) is { } layout)
                {
                    yield return layout;
                }
            }
        }

        /// <summary>The interface of <paramref name="className"/> that declares <paramref name="method"/>.</summary>
        public (InterfaceLayout Interface, MethodLayout Method)? FindMethod(string className, string method)
        {
            foreach (InterfaceLayout layout in InterfacesOf(className))
            {
                if (layout.Find(method) is { } found)
                {
                    return (layout, found);
                }
            }

            return null;
        }

        private void Read(string path)
        {
            using FileStream stream = File.OpenRead(path);
            using PEReader pe = new(stream);
            MetadataReader md = pe.GetMetadataReader();

            foreach (TypeDefinitionHandle handle in md.TypeDefinitions)
            {
                TypeDefinition type = md.GetTypeDefinition(handle);
                string name = FullName(md, type);
                bool isInterface = (type.Attributes & System.Reflection.TypeAttributes.Interface) != 0;

                if (isInterface)
                {
                    List<MethodLayout> methods = new();
                    int index = 0;
                    foreach (MethodDefinitionHandle mh in type.GetMethods())
                    {
                        MethodDefinition method = md.GetMethodDefinition(mh);
                        (IReadOnlyList<string> parameters, string returns) = Signature(md, method);
                        methods.Add(new MethodLayout(md.GetString(method.Name), InspectableSlots + index++, parameters, returns));
                    }

                    _interfaces[name] = new InterfaceLayout(name, methods, ReadGuid(md, type));
                    continue;
                }

                List<string> implemented = new();
                foreach (InterfaceImplementationHandle ih in type.GetInterfaceImplementations())
                {
                    InterfaceImplementation impl = md.GetInterfaceImplementation(ih);
                    string? interfaceName = TypeName(md, impl.Interface);
                    if (interfaceName is null)
                    {
                        continue;
                    }

                    implemented.Add(interfaceName);
                    foreach (CustomAttributeHandle ah in impl.GetCustomAttributes())
                    {
                        if (AttributeName(md, md.GetCustomAttribute(ah)) == "Windows.Foundation.Metadata.DefaultAttribute")
                        {
                            _defaultInterface[name] = interfaceName;
                        }
                    }
                }

                if (implemented.Count > 0)
                {
                    _classInterfaces[name] = implemented;
                }
            }
        }

        /// <summary>The IID, from Windows.Foundation.Metadata.GuidAttribute(UInt32, UInt16, UInt16, Byte x8).</summary>
        private static Guid? ReadGuid(MetadataReader md, TypeDefinition type)
        {
            foreach (CustomAttributeHandle ah in type.GetCustomAttributes())
            {
                CustomAttribute attribute = md.GetCustomAttribute(ah);
                if (AttributeName(md, attribute) != "Windows.Foundation.Metadata.GuidAttribute")
                {
                    continue;
                }

                BlobReader blob = md.GetBlobReader(attribute.Value);
                if (blob.Length < 2 + 16 || blob.ReadUInt16() != 1)
                {
                    return null;
                }

                return new Guid(blob.ReadUInt32(), blob.ReadUInt16(), blob.ReadUInt16(),
                    blob.ReadByte(), blob.ReadByte(), blob.ReadByte(), blob.ReadByte(),
                    blob.ReadByte(), blob.ReadByte(), blob.ReadByte(), blob.ReadByte());
            }

            return null;
        }

        private static string FullName(MetadataReader md, TypeDefinition type)
        {
            string ns = md.GetString(type.Namespace);
            string name = md.GetString(type.Name);
            if (!type.GetDeclaringType().IsNil)
            {
                return FullName(md, md.GetTypeDefinition(type.GetDeclaringType())) + "+" + name;
            }

            return ns.Length == 0 ? name : ns + "." + name;
        }

        private static string? TypeName(MetadataReader md, EntityHandle handle) => handle.Kind switch
        {
            HandleKind.TypeDefinition => FullName(md, md.GetTypeDefinition((TypeDefinitionHandle)handle)),
            HandleKind.TypeReference => RefName(md, md.GetTypeReference((TypeReferenceHandle)handle)),
            HandleKind.TypeSpecification => "generic",
            _ => null,
        };

        private static string RefName(MetadataReader md, TypeReference reference)
        {
            string ns = md.GetString(reference.Namespace);
            string name = md.GetString(reference.Name);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        private static string? AttributeName(MetadataReader md, CustomAttribute attribute)
        {
            EntityHandle owner = attribute.Constructor.Kind switch
            {
                HandleKind.MemberReference => md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
                HandleKind.MethodDefinition => md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
                _ => default,
            };

            return owner.IsNil ? null : TypeName(md, owner);
        }

        private static (IReadOnlyList<string>, string) Signature(MetadataReader md, MethodDefinition method)
        {
            try
            {
                MethodSignature<string> sig = method.DecodeSignature(new NameProvider(md), null);
                return (sig.ParameterTypes, sig.ReturnType);
            }
            catch (Exception)
            {
                return (Array.Empty<string>(), "?");
            }
        }

        /// <summary>Type names good enough to tell a float from an int from an object.</summary>
        private sealed class NameProvider(MetadataReader md) : ISignatureTypeProvider<string, object?>
        {
            public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
            public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
                => FullName(md, md.GetTypeDefinition(handle));
            public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
                => RefName(md, md.GetTypeReference(handle));
            public string GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle, byte rawTypeKind) => "spec";
            public string GetSZArrayType(string elementType) => elementType + "[]";
            public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[,]";
            public string GetByReferenceType(string elementType) => elementType + "&";
            public string GetPointerType(string elementType) => elementType + "*";
            public string GetGenericInstantiation(string genericType, System.Collections.Immutable.ImmutableArray<string> typeArguments)
                => genericType + "<" + string.Join(",", typeArguments) + ">";
            public string GetGenericTypeParameter(object? context, int index) => "T" + index;
            public string GetGenericMethodParameter(object? context, int index) => "M" + index;
            public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
            public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
            public string GetPinnedType(string elementType) => elementType;
        }
    }
}

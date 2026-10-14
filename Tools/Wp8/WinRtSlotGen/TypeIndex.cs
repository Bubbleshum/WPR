using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

/// <summary>
/// Every type defined across the WinMetadata files, and the ABI shape of the ones signatures
/// reach. Generator-only: the runtime reads the emitted table, never metadata.
/// </summary>
internal sealed class TypeIndex
{
    /// <summary>I interface, C runtime class, D delegate, E enum, S struct.</summary>
    public sealed record Shape(char Kind, int Size, int Align, int Hfa);

    private static readonly Dictionary<string, Shape> Primitives = new(StringComparer.Ordinal)
    {
        ["Boolean"] = new('S', 1, 1, 0),
        ["Byte"] = new('S', 1, 1, 0),
        ["SByte"] = new('S', 1, 1, 0),
        ["Char"] = new('S', 2, 2, 0),
        ["Int16"] = new('S', 2, 2, 0),
        ["UInt16"] = new('S', 2, 2, 0),
        ["Int32"] = new('S', 4, 4, 0),
        ["UInt32"] = new('S', 4, 4, 0),
        ["Int64"] = new('S', 8, 8, 0),
        ["UInt64"] = new('S', 8, 8, 0),
        ["Single"] = new('S', 4, 4, 1),
        ["Double"] = new('S', 8, 8, 1),
        ["String"] = new('S', 4, 4, 0),   // HSTRING
        ["Object"] = new('I', 4, 4, 0),   // IInspectable*
    };

    /// <summary>
    /// The CLR projections metadata names instead of the WinRT type: they are references into
    /// mscorlib, not definitions in any .winmd, so their ABI shape is stated here.
    /// </summary>
    private static readonly Dictionary<string, Shape> Projections = new(StringComparer.Ordinal)
    {
        ["System.TimeSpan"] = new('S', 8, 8, 0),                 // Windows.Foundation.TimeSpan { Int64 }
        ["System.DateTimeOffset"] = new('S', 8, 8, 0),           // Windows.Foundation.DateTime { Int64 }
        ["System.Guid"] = new('S', 16, 4, 0),
        ["System.Runtime.InteropServices.WindowsRuntime.EventRegistrationToken"] = new('S', 8, 8, 0),
        ["System.Exception"] = new('S', 4, 4, 0),                // Windows.Foundation.HResult { Int32 }
        ["System.Type"] = new('S', 8, 4, 0),                     // Windows.UI.Xaml.Interop.TypeName { HSTRING, Int32 }
        ["System.Uri"] = new('C', 4, 4, 0),
        ["System.EventHandler`1"] = new('D', 4, 4, 0),
        ["System.Nullable`1"] = new('I', 4, 4, 0),               // IReference<T>
        ["System.Collections.Generic.IEnumerable`1"] = new('I', 4, 4, 0),
        ["System.Collections.Generic.IEnumerator`1"] = new('I', 4, 4, 0),
        ["System.Collections.Generic.IList`1"] = new('I', 4, 4, 0),
        ["System.Collections.Generic.IReadOnlyList`1"] = new('I', 4, 4, 0),
        ["System.Collections.Generic.IDictionary`2"] = new('I', 4, 4, 0),
        ["System.Collections.Generic.IReadOnlyDictionary`2"] = new('I', 4, 4, 0),
        ["System.Collections.Generic.KeyValuePair`2"] = new('I', 4, 4, 0), // IKeyValuePair<K,V>
        ["System.IDisposable"] = new('I', 4, 4, 0),             // IClosable
    };

    private readonly Dictionary<string, (MetadataReader Reader, TypeDefinition Type)> _types = new(StringComparer.Ordinal);
    private readonly List<PEReader> _keepAlive = new();

    public TypeIndex(IEnumerable<string> files)
    {
        foreach (string file in files)
        {
            PEReader pe = new(File.OpenRead(file));
            _keepAlive.Add(pe);
            MetadataReader md = pe.GetMetadataReader();
            foreach (TypeDefinitionHandle handle in md.TypeDefinitions)
            {
                TypeDefinition type = md.GetTypeDefinition(handle);
                string ns = md.GetString(type.Namespace);
                if (ns.Length > 0)
                {
                    _types.TryAdd(ns + "." + md.GetString(type.Name), (md, type));
                }
            }
        }
    }

    /// <summary>Adds the shape of <paramref name="typeName"/> (as a signature prints it) and of anything a struct of it contains.</summary>
    public void Collect(string typeName, IDictionary<string, Shape> into)
    {
        string name = Strip(typeName);
        if (name.Length == 0 || Primitives.ContainsKey(name) || name is "Void" or "spec" || name.StartsWith('T') && name.Length <= 3)
        {
            return;
        }

        if (!into.ContainsKey(name) && ShapeOf(name) is { } shape)
        {
            into[name] = shape;
        }
    }

    /// <summary>The type a parameter carries, without by-ref, array or generic-argument decoration.</summary>
    public static string Strip(string typeName)
    {
        string name = typeName.TrimEnd('&');
        if (name.EndsWith("[]", StringComparison.Ordinal))
        {
            name = name[..^2];
        }

        int angle = name.IndexOf('<');
        return angle > 0 ? name[..angle] : name;
    }

    private readonly Dictionary<string, Shape?> _shapes = new(StringComparer.Ordinal);

    private Shape? ShapeOf(string name)
    {
        if (Primitives.TryGetValue(name, out Shape? primitive))
        {
            return primitive;
        }

        if (Projections.TryGetValue(name, out Shape? projected))
        {
            return projected;
        }

        if (_shapes.TryGetValue(name, out Shape? known))
        {
            return known;
        }

        _shapes[name] = null;   // a struct cannot contain itself; this also stops a cycle
        Shape? shape = Compute(name);
        _shapes[name] = shape;
        return shape;
    }

    private Shape? Compute(string name)
    {
        if (!_types.TryGetValue(name, out var entry))
        {
            return null;
        }

        (MetadataReader md, TypeDefinition type) = entry;
        if ((type.Attributes & TypeAttributes.Interface) != 0)
        {
            return new Shape('I', 4, 4, 0);
        }

        string? baseName = BaseName(md, type);
        if (baseName == "System.MulticastDelegate")
        {
            return new Shape('D', 4, 4, 0);
        }

        if (baseName == "System.Enum")
        {
            // WinRT enums are Int32, or UInt32 for flags: four bytes either way.
            return new Shape('E', 4, 4, 0);
        }

        if (baseName != "System.ValueType")
        {
            return new Shape('C', 4, 4, 0);
        }

        int size = 0, align = 1, floats = 0, doubles = 0, others = 0;
        foreach (FieldDefinitionHandle fh in type.GetFields())
        {
            FieldDefinition field = md.GetFieldDefinition(fh);
            if ((field.Attributes & FieldAttributes.Static) != 0)
            {
                continue;
            }

            string fieldType = Strip(field.DecodeSignature(new Names(md), null));
            Shape? member = ShapeOf(fieldType);
            if (member is null)
            {
                return null;
            }

            size = (size + member.Align - 1) / member.Align * member.Align + member.Size;
            align = Math.Max(align, member.Align);
            if (fieldType == "Single") floats++;
            else if (fieldType == "Double") doubles++;
            else if (member.Hfa > 0 && member.Size == member.Hfa * 4) floats += member.Hfa;
            else if (member.Hfa > 0) doubles += member.Hfa;
            else others++;
        }

        size = (size + align - 1) / align * align;
        int hfa = others == 0 && (floats == 0) != (doubles == 0) && floats + doubles <= 4 ? floats + doubles : 0;
        return new Shape('S', size, align, hfa);
    }

    private static string? BaseName(MetadataReader md, TypeDefinition type)
    {
        if (type.BaseType.IsNil || type.BaseType.Kind != HandleKind.TypeReference)
        {
            return null;
        }

        TypeReference reference = md.GetTypeReference((TypeReferenceHandle)type.BaseType);
        return md.GetString(reference.Namespace) + "." + md.GetString(reference.Name);
    }

    private sealed class Names(MetadataReader md) : ISignatureTypeProvider<string, object?>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            TypeDefinition t = md.GetTypeDefinition(handle);
            return md.GetString(t.Namespace) + "." + md.GetString(t.Name);
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            TypeReference r = md.GetTypeReference(handle);
            return md.GetString(r.Namespace) + "." + md.GetString(r.Name);
        }

        public string GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle, byte rawTypeKind) => "spec";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[,]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";
        public string GetGenericTypeParameter(object? context, int index) => "T" + index;
        public string GetGenericMethodParameter(object? context, int index) => "M" + index;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
    }
}

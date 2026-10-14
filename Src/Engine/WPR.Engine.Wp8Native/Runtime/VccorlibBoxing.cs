using System.Globalization;
using System.Text;

namespace WPR.Wp8Native
{
    /// <summary>
    /// Boxed values: what vccorlib makes when C++/CX converts a value type or a String to
    /// Object^. On a phone it activates Windows.Foundation.PropertyValue; the object that comes
    /// back implements IPropertyValue and IReference&lt;T&gt;, and that pair is what this builds.
    /// </summary>
    public sealed partial class HostStubs
    {
        /// <summary>Windows.Foundation.PropertyType.</summary>
        internal enum PropertyType
        {
            Empty = 0, UInt8 = 1, Int16 = 2, UInt16 = 3, Int32 = 4, UInt32 = 5, Int64 = 6, UInt64 = 7,
            Single = 8, Double = 9, Char16 = 10, Boolean = 11, String = 12, Inspectable = 13,
            DateTime = 14, TimeSpan = 15, Guid = 16, Point = 17, Size = 18, Rect = 19, OtherType = 20,
            OtherTypeArray = 1044,
        }

        /// <summary>A boxed value: its type and its bytes (for a String, the HSTRING handle).</summary>
        internal sealed record BoxedValue(PropertyType Type, byte[] Raw);

        /// <summary>What each boxable type is called in a WinRT signature, its size, and vccorlib's name and TypeCode for it.</summary>
        internal static readonly IReadOnlyDictionary<PropertyType, (string Signature, int Size, string Name, int TypeCode)> BoxableTypes =
            new Dictionary<PropertyType, (string, int, string, int)>
            {
                [PropertyType.UInt8] = ("u1", 1, "UInt8", 6),
                [PropertyType.Int16] = ("i2", 2, "Int16", 7),
                [PropertyType.UInt16] = ("u2", 2, "UInt16", 8),
                [PropertyType.Int32] = ("i4", 4, "Int32", 9),
                [PropertyType.UInt32] = ("u4", 4, "UInt32", 10),
                [PropertyType.Int64] = ("i8", 8, "Int64", 11),
                [PropertyType.UInt64] = ("u8", 8, "UInt64", 12),
                [PropertyType.Single] = ("f4", 4, "Single", 13),
                [PropertyType.Double] = ("f8", 8, "Double", 14),
                [PropertyType.Char16] = ("c2", 2, "Char16", 4),
                [PropertyType.Boolean] = ("b1", 1, "Boolean", 3),
                [PropertyType.String] = ("string", 4, "Platform.String", 18),
                [PropertyType.Guid] = ("g16", 16, "Platform.Guid", 23),
                [PropertyType.DateTime] = ("struct(Windows.Foundation.DateTime;i8)", 8, "Windows.Foundation.DateTime", 16),
                [PropertyType.TimeSpan] = ("struct(Windows.Foundation.TimeSpan;i8)", 8, "Windows.Foundation.TimeSpan", 19),
                [PropertyType.Point] = ("struct(Windows.Foundation.Point;f4;f4)", 8, "Windows.Foundation.Point", 20),
                [PropertyType.Size] = ("struct(Windows.Foundation.Size;f4;f4)", 8, "Windows.Foundation.Size", 21),
                [PropertyType.Rect] = ("struct(Windows.Foundation.Rect;f4;f4;f4;f4)", 16, "Windows.Foundation.Rect", 22),
            };

        /// <summary>Platform::TypeCode -> PropertyType, as vccorlib's CreateValue switches on it.</summary>
        internal static PropertyType? PropertyTypeForTypeCode(int typeCode) =>
            BoxableTypes.Where(p => p.Value.TypeCode == typeCode).Select(p => (PropertyType?)p.Key).FirstOrDefault();

        /// <summary>IReference&lt;T&gt; for a boxable type.</summary>
        internal static Guid ReferenceIid(PropertyType type) => ParameterizedIid(IidReferenceGeneric, BoxableTypes[type].Signature);

        /// <summary>IReferenceArray&lt;T&gt; for a boxable element type.</summary>
        internal static Guid ReferenceArrayIid(PropertyType type) => ParameterizedIid(IidReferenceArrayGeneric, BoxableTypes[type].Signature);

        private readonly Dictionary<PropertyType, HostClass> _boxClasses = new();

        /// <summary>Boxes a value; returns its IInspectable (Object^), refcount 1.</summary>
        internal long Box(PropertyType type, byte[] raw) =>
            CreateHostObject(BoxClass(type), 8, new BoxedValue(type, raw)).Address;

        /// <summary>The boxed value behind an interface pointer, when the host boxed it.</summary>
        internal BoxedValue? Unbox(long pointer) => HostObjectFor(pointer)?.State as BoxedValue;

        private HostClass BoxClass(PropertyType type)
        {
            if (_boxClasses.TryGetValue(type, out HostClass? known))
            {
                return known;
            }

            string name = BoxableTypes[type].Name.Replace("Platform.", string.Empty, StringComparison.Ordinal);
            var methods = new List<(string, Action<HostObject>)>
            {
                ("get_Type", o => { WriteOut(Arg(1), (long)((BoxedValue)o.State!).Type); Return(SOk); }),
                ("get_IsNumericScalar", o =>
                {
                    WriteBool(Arg(1), ((BoxedValue)o.State!).Type is >= PropertyType.UInt8 and <= PropertyType.Double);
                    Return(SOk);
                }),
            };

            PropertyType[] scalarGetters =
            [
                PropertyType.UInt8, PropertyType.Int16, PropertyType.UInt16, PropertyType.Int32, PropertyType.UInt32,
                PropertyType.Int64, PropertyType.UInt64, PropertyType.Single, PropertyType.Double, PropertyType.Char16,
                PropertyType.Boolean, PropertyType.String, PropertyType.Guid, PropertyType.DateTime, PropertyType.TimeSpan,
                PropertyType.Point, PropertyType.Size, PropertyType.Rect,
            ];

            foreach (PropertyType wanted in scalarGetters)
            {
                methods.Add(($"Get{wanted}", o => Return(ReadBoxedAs((BoxedValue)o.State!, wanted, Arg(1)))));
            }

            string[] arrays =
            [
                "UInt8", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64", "Single", "Double", "Char16", "Boolean",
                "String", "Inspectable", "Guid", "DateTime", "TimeSpan", "Point", "Size", "Rect",
            ];

            // GetXxxArray(UINT32* length, T** value) on a scalar is a type mismatch, with both outs empty.
            foreach (string array in arrays)
            {
                methods.Add(($"Get{array}Array", _ =>
                {
                    WriteOut(Arg(1), 0);
                    WriteOut(Arg(2), 0);
                    Return(TypeMismatch);
                }));
            }

            var boxClass = new HostClass(
                $"Windows.Foundation.IReference`1<{name}>",
                [
                    new HostInterface("IPropertyValue", [IidPropertyValue], true, [.. methods]),
                    new HostInterface($"IReference<{name}>", [ReferenceIid(type)], true,
                        ("get_Value", o =>
                        {
                            BoxedValue boxed = (BoxedValue)o.State!;
                            WriteBoxedRaw(boxed, Arg(1));
                            Return(Arg(1) == 0 ? EPointer : SOk);
                        })),
                ]);

            _boxClasses[type] = boxClass;
            return boxClass;
        }

        private void WriteBool(long address, bool value)
        {
            if (address != 0)
            {
                _emulator.WriteBoolean(address, value);
            }
        }

        private void WriteBoxedRaw(BoxedValue boxed, long destination)
        {
            if (destination == 0)
            {
                return;
            }

            if (boxed.Type == PropertyType.String)
            {
                // The caller owns the HSTRING it is handed; strings here are immutable, so sharing is a copy.
                _emulator.WriteUInt32(destination, BitConverter.ToUInt32(boxed.Raw));
                return;
            }

            _emulator.WriteMemory(destination, boxed.Raw);
        }

        /// <summary>
        /// IPropertyValue::GetXxx: the value as <paramref name="wanted"/>. Numeric scalars convert
        /// between each other when the value fits (DISP_E_OVERFLOW when it does not); every other
        /// type converts only to itself (TYPE_E_TYPEMISMATCH).
        /// </summary>
        private long ReadBoxedAs(BoxedValue boxed, PropertyType wanted, long destination)
        {
            if (destination == 0)
            {
                return EPointer;
            }

            if (boxed.Type == wanted)
            {
                WriteBoxedRaw(boxed, destination);
                return SOk;
            }

            bool numericSource = boxed.Type is >= PropertyType.UInt8 and <= PropertyType.Double;
            bool numericTarget = wanted is >= PropertyType.UInt8 and <= PropertyType.Double;
            if (!numericSource || !numericTarget)
            {
                return TypeMismatch;
            }

            double asDouble = NumericValue(boxed, out Int128 asInteger, out bool integral);
            if (wanted is PropertyType.Single or PropertyType.Double)
            {
                if (wanted == PropertyType.Single && double.IsFinite(asDouble) && Math.Abs(asDouble) > float.MaxValue)
                {
                    return Overflow;
                }

                _emulator.WriteMemory(destination, wanted == PropertyType.Single
                    ? BitConverter.GetBytes((float)asDouble)
                    : BitConverter.GetBytes(asDouble));
                return SOk;
            }

            if (!integral)
            {
                if (!double.IsFinite(asDouble) || Math.Floor(asDouble) != asDouble)
                {
                    return TypeMismatch;
                }

                if (Math.Abs(asDouble) > 1.8e19)
                {
                    return Overflow;
                }

                asInteger = (Int128)asDouble;
            }

            (Int128 min, Int128 max) = wanted switch
            {
                PropertyType.UInt8 => ((Int128)byte.MinValue, (Int128)byte.MaxValue),
                PropertyType.Int16 => (short.MinValue, short.MaxValue),
                PropertyType.UInt16 => (ushort.MinValue, ushort.MaxValue),
                PropertyType.Int32 => (int.MinValue, int.MaxValue),
                PropertyType.UInt32 => (uint.MinValue, uint.MaxValue),
                PropertyType.Int64 => (long.MinValue, long.MaxValue),
                _ => (ulong.MinValue, ulong.MaxValue),
            };

            if (asInteger < min || asInteger > max)
            {
                return Overflow;
            }

            int size = BoxableTypes[wanted].Size;
            byte[] bytes = BitConverter.GetBytes((ulong)(long)(asInteger & ulong.MaxValue))[..size];
            _emulator.WriteMemory(destination, bytes);
            return SOk;
        }

        private static double NumericValue(BoxedValue boxed, out Int128 integer, out bool integral)
        {
            byte[] r = boxed.Raw;
            integral = true;
            integer = boxed.Type switch
            {
                PropertyType.UInt8 => r[0],
                PropertyType.Int16 => BitConverter.ToInt16(r),
                PropertyType.UInt16 => BitConverter.ToUInt16(r),
                PropertyType.Int32 => BitConverter.ToInt32(r),
                PropertyType.UInt32 => BitConverter.ToUInt32(r),
                PropertyType.Int64 => BitConverter.ToInt64(r),
                PropertyType.UInt64 => BitConverter.ToUInt64(r),
                _ => 0,
            };

            switch (boxed.Type)
            {
                case PropertyType.Single:
                    integral = false;
                    return BitConverter.ToSingle(r);
                case PropertyType.Double:
                    integral = false;
                    return BitConverter.ToDouble(r);
                default:
                    return (double)integer;
            }
        }

        /// <summary>
        /// The text vccorlib's ToString gives a boxed value: the printf formats its own
        /// <c>default::int32::ToString</c> and friends use, "true"/"false", the character, the
        /// string, the GUID in braces. Null for the structure types, which it does not format.
        /// </summary>
        internal string? FormatBoxed(BoxedValue boxed)
        {
            byte[] r = boxed.Raw;
            return boxed.Type switch
            {
                PropertyType.UInt8 => r[0].ToString(CultureInfo.InvariantCulture),
                PropertyType.Int16 => BitConverter.ToInt16(r).ToString(CultureInfo.InvariantCulture),
                PropertyType.UInt16 => BitConverter.ToUInt16(r).ToString(CultureInfo.InvariantCulture),
                PropertyType.Int32 => BitConverter.ToInt32(r).ToString(CultureInfo.InvariantCulture),
                PropertyType.UInt32 => BitConverter.ToUInt32(r).ToString(CultureInfo.InvariantCulture),
                PropertyType.Int64 => BitConverter.ToInt64(r).ToString(CultureInfo.InvariantCulture),
                PropertyType.UInt64 => BitConverter.ToUInt64(r).ToString(CultureInfo.InvariantCulture),
                PropertyType.Single => FormatG(BitConverter.ToSingle(r)),
                PropertyType.Double => FormatG(BitConverter.ToDouble(r)),
                PropertyType.Char16 => ((char)BitConverter.ToUInt16(r)).ToString(),
                PropertyType.Boolean => r[0] != 0 ? "true" : "false",
                PropertyType.String => _strings.ReadText(BitConverter.ToUInt32(r)),
                PropertyType.Guid => FormatGuid(new Guid(r)),
                _ => null,
            };
        }

        /// <summary>Platform::Guid::ToString: lower-case hex in braces.</summary>
        internal static string FormatGuid(Guid value) => "{" + value.ToString("D", CultureInfo.InvariantCulture) + "}";

        /// <summary>
        /// MSVC 2012's printf <c>%G</c>: six significant digits, trailing zeros dropped, exponent form
        /// below 1e-4 or from 1e6 with a three-digit exponent, and its own spellings of the
        /// non-finite values.
        /// </summary>
        internal static string FormatG(double value)
        {
            if (double.IsNaN(value))
            {
                return "1.#QNAN";
            }

            if (double.IsInfinity(value))
            {
                return value > 0 ? "1.#INF" : "-1.#INF";
            }

            if (value == 0)
            {
                return BitConverter.DoubleToInt64Bits(value) < 0 ? "-0" : "0";
            }

            string scientific = value.ToString("E5", CultureInfo.InvariantCulture);   // d.dddddE+ddd
            int exponent = int.Parse(scientific[(scientific.IndexOf('E') + 1)..], CultureInfo.InvariantCulture);
            if (exponent < -4 || exponent >= 6)
            {
                string mantissa = scientific[..scientific.IndexOf('E')];
                string tail = scientific[(scientific.IndexOf('E') + 1)..];
                return TrimFraction(mantissa) + "E" + tail[0] + tail[1..].PadLeft(3, '0');
            }

            return TrimFraction(value.ToString("F" + (5 - exponent).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
        }

        private static string TrimFraction(string number) =>
            number.Contains('.', StringComparison.Ordinal) ? number.TrimEnd('0').TrimEnd('.') : number;

        /// <summary>The text of a host-boxed value as an HSTRING, or 0.</summary>
        private long BoxedToHString(BoxedValue boxed)
        {
            string? text = FormatBoxed(boxed);
            return string.IsNullOrEmpty(text) ? 0 : _strings.Create(Encoding.Unicode.GetBytes(text));
        }
    }
}

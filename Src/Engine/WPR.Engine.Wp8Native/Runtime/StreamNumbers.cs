using System.Globalization;
using System.Numerics;
using System.Text;

namespace WPR.Wp8Native
{
    /// <summary>
    /// What VC11's num_put and num_get do for the "C" locale: the text an inserter writes for a
    /// number and its format flags, and the grammar an extractor accepts.
    /// </summary>
    /// <remarks>
    /// num_put formats through the CRT's sprintf, so the details are MSVCR110's: a three-digit
    /// exponent (<c>1.000000e+010</c>), and infinities and NaNs written as <c>1.#INF</c>,
    /// <c>1.#QNAN</c> and <c>-1.#IND</c> with the "digits" after the point rounded like any other
    /// (so <c>%.2f</c> of infinity is <c>1.#J</c>). A floating precision of 0 or less means 6
    /// unless the format is fixed. Extraction follows VC11's _Getifld / _Getffld and the strto*
    /// family: a value that does not fit, or no digits at all, sets failbit and leaves the
    /// target alone (VC11's behaviour; C++11 would also store 0 or the limit).
    /// </remarks>
    public static class StreamNumbers
    {
        public const uint Uppercase = 0x0004, ShowBase = 0x0008, ShowPoint = 0x0010, ShowPos = 0x0020,
            Left = 0x0040, Right = 0x0080, Internal = 0x0100, Dec = 0x0200, Oct = 0x0400, Hex = 0x0800,
            Scientific = 0x1000, Fixed = 0x2000, AdjustField = Left | Right | Internal, BaseField = Dec | Oct | Hex,
            FloatField = Scientific | Fixed;

        /// <summary>A formatted number, and where internal padding goes (after a sign or 0x).</summary>
        public readonly record struct Formatted(string Text, int PrefixLength);

        /// <summary>
        /// An integer as num_put writes it. <paramref name="bits"/> is the width oct and hex
        /// reinterpret a negative value at (operator&lt;&lt;(short) passes 16, int and long 32).
        /// </summary>
        public static Formatted FormatInteger(long value, bool signed, int bits, uint flags)
        {
            uint basefield = flags & BaseField;
            bool upper = (flags & Uppercase) != 0;
            if (basefield is Hex or Oct)
            {
                ulong bitsValue = bits >= 64 ? unchecked((ulong)value) : unchecked((ulong)value) & ((1UL << bits) - 1);
                if (basefield == Hex)
                {
                    string hex = bitsValue.ToString(upper ? "X" : "x", CultureInfo.InvariantCulture);
                    return (flags & ShowBase) != 0 && bitsValue != 0
                        ? new Formatted((upper ? "0X" : "0x") + hex, 2)
                        : new Formatted(hex, 0);
                }

                string oct = ToOctal(bitsValue);
                return new Formatted((flags & ShowBase) != 0 && bitsValue != 0 ? "0" + oct : oct, 0);
            }

            if (!signed)
            {
                return new Formatted(unchecked((ulong)value).ToString(CultureInfo.InvariantCulture), 0);
            }

            if (value < 0)
            {
                return new Formatted(value.ToString(CultureInfo.InvariantCulture), 1);
            }

            string text = value.ToString(CultureInfo.InvariantCulture);
            return (flags & ShowPos) != 0 ? new Formatted("+" + text, 1) : new Formatted(text, 0);
        }

        private static string ToOctal(ulong value)
        {
            if (value == 0)
            {
                return "0";
            }

            StringBuilder sb = new();
            while (value != 0)
            {
                sb.Insert(0, (char)('0' + (int)(value & 7)));
                value >>= 3;
            }

            return sb.ToString();
        }

        /// <summary>A floating value as num_put writes it: the sprintf conversion its flags choose.</summary>
        public static Formatted FormatFloat(double value, uint flags, long precision)
        {
            uint floatfield = flags & FloatField;
            bool upper = (flags & Uppercase) != 0;
            char conversion = floatfield switch
            {
                Fixed => 'f',
                Scientific => upper ? 'E' : 'e',
                FloatField => upper ? 'A' : 'a',
                _ => upper ? 'G' : 'g',
            };

            int p = precision <= 0 && floatfield != Fixed ? 6 : (int)Math.Min(precision, 4096);
            string body = Printf(value, conversion, p, alternate: (flags & ShowPoint) != 0);
            string sign = string.Empty;
            if (body.StartsWith('-'))
            {
                sign = "-";
                body = body[1..];
            }
            else if ((flags & ShowPos) != 0)
            {
                sign = "+";
            }

            int prefix = sign.Length;
            if (conversion is 'a' or 'A' && body.StartsWith("0", StringComparison.Ordinal) && body.Length > 1)
            {
                prefix += 2;
            }

            return new Formatted(sign + body, prefix);
        }

        /// <summary>MSVCR110's printf for one floating conversion (f, e, E, g, G, a, A) at a precision.</summary>
        public static string Printf(double value, char conversion, int precision, bool alternate)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return Special(value, conversion, precision, alternate);
            }

            switch (conversion)
            {
                case 'f':
                case 'F':
                {
                    string text = value.ToString("F" + precision, CultureInfo.InvariantCulture);
                    return alternate && precision == 0 ? text + "." : text;
                }

                case 'e':
                case 'E':
                {
                    string text = value.ToString((conversion == 'E' ? "E" : "e") + precision, CultureInfo.InvariantCulture);
                    return alternate && precision == 0 ? text.Insert(text.IndexOfAny(['e', 'E']), ".") : text;
                }

                case 'a':
                case 'A':
                    return HexFloat(value, precision, conversion == 'A');

                default:
                {
                    // %g: P significant digits; %e when the exponent X (after rounding to P digits)
                    // is below -4 or at least P, else %f with P-1-X decimals. Trailing zeros go unless #.
                    int significant = precision == 0 ? 1 : precision;
                    string asE = value.ToString("e" + (significant - 1), CultureInfo.InvariantCulture);
                    int exponent = int.Parse(asE[(asE.IndexOf('e') + 1)..], CultureInfo.InvariantCulture);
                    string text = exponent < -4 || exponent >= significant
                        ? value.ToString((conversion == 'G' ? "E" : "e") + (significant - 1), CultureInfo.InvariantCulture)
                        : value.ToString("F" + (significant - 1 - exponent), CultureInfo.InvariantCulture);
                    if (!alternate)
                    {
                        int exponentAt = text.IndexOfAny(['e', 'E']);
                        string mantissa = exponentAt < 0 ? text : text[..exponentAt];
                        string suffix = exponentAt < 0 ? string.Empty : text[exponentAt..];
                        if (mantissa.Contains('.'))
                        {
                            mantissa = mantissa.TrimEnd('0').TrimEnd('.');
                        }

                        text = mantissa + suffix;
                    }
                    else if (!text.Contains('.'))
                    {
                        int exponentAt = text.IndexOfAny(['e', 'E']);
                        text = exponentAt < 0 ? text + "." : text.Insert(exponentAt, ".");
                    }

                    return text;
                }
            }
        }

        /// <summary>
        /// MSVCR110's infinities and NaNs: "1.#INF", "1.#QNAN", "1.#SNAN" or "1.#IND" (the
        /// default NaN, 0xFFF8...), whose characters after the point are rounded to the precision
        /// as if they were digits - %.2f of infinity is "1.#J", %.3f is "1.#IO".
        /// </summary>
        private static string Special(double value, char conversion, int precision, bool alternate)
        {
            ulong bits = BitConverter.DoubleToUInt64Bits(value);
            bool negative = (bits >> 63) != 0;
            string tail;
            if (double.IsInfinity(value))
            {
                tail = "#INF";
            }
            else if (bits == 0xFFF8000000000000UL)
            {
                tail = "#IND";
            }
            else
            {
                tail = (bits & 0x0008000000000000UL) != 0 ? "#QNAN" : "#SNAN";
            }

            char lower = char.ToLowerInvariant(conversion);
            int fraction = lower == 'g' ? Math.Max(0, (precision == 0 ? 1 : precision) - 1) : precision;
            StringBuilder digits = new("1" + tail);
            while (digits.Length < fraction + 1)
            {
                digits.Append('0');
            }

            if (digits.Length > fraction + 1)
            {
                bool roundUp = digits[fraction + 1] >= '5';
                digits.Length = fraction + 1;
                if (roundUp)
                {
                    digits[fraction] = (char)(digits[fraction] + 1);
                }
            }

            string text = digits.Length > 1 ? $"{digits[0]}.{digits.ToString(1, digits.Length - 1)}" : digits.ToString() + (alternate ? "." : string.Empty);
            if (lower == 'g' && !alternate && text.Contains('.'))
            {
                text = text.TrimEnd('0').TrimEnd('.');
            }

            if (lower == 'e')
            {
                text += conversion == 'E' ? "E+000" : "e+000";
            }
            else if (lower == 'a')
            {
                text += conversion == 'A' ? "P+0" : "p+0";
            }

            return (negative ? "-" : string.Empty) + text;
        }

        private static string HexFloat(double value, int precision, bool upper)
        {
            ulong bits = BitConverter.DoubleToUInt64Bits(value);
            bool negative = (bits >> 63) != 0;
            int exponent = (int)((bits >> 52) & 0x7FF);
            ulong mantissa = bits & 0xFFFFFFFFFFFFFUL;
            int lead = 1;
            if (exponent == 0)
            {
                lead = 0;
                exponent = mantissa == 0 ? 1023 : -1022 + 1023;
            }

            int e = exponent - 1023;

            // 13 hex digits of fraction; round to the precision.
            string fraction = mantissa.ToString("x13", CultureInfo.InvariantCulture);
            if (precision < 13)
            {
                BigInteger full = new BigInteger(lead) << 52 | mantissa;
                int drop = (13 - precision) * 4;
                BigInteger half = BigInteger.One << (drop - 1);
                BigInteger rounded = (full + half) >> drop;
                lead = (int)(rounded >> (precision * 4));
                BigInteger fractionBits = rounded & ((BigInteger.One << (precision * 4)) - 1);
                fraction = precision == 0 ? string.Empty : fractionBits.ToString("x", CultureInfo.InvariantCulture).TrimStart('0').PadLeft(precision, '0');
                if (fraction.Length > precision)
                {
                    fraction = fraction[^precision..];
                }
            }
            else
            {
                fraction = fraction.PadRight(precision, '0');
            }

            string text = $"0x{lead}{(fraction.Length > 0 ? "." + fraction : string.Empty)}p{(e >= 0 ? "+" : "-")}{Math.Abs(e)}";
            if (upper)
            {
                text = text.ToUpperInvariant();
            }

            return (negative ? "-" : string.Empty) + text;
        }

        /// <summary>
        /// Pads a formatted field to <paramref name="width"/>: left adjust pads after, internal
        /// pads between the prefix (sign, 0x) and the digits, anything else pads before.
        /// </summary>
        public static string Pad(Formatted field, long width, char fill, uint flags)
        {
            string text = field.Text;
            if (width <= text.Length)
            {
                return text;
            }

            string padding = new(fill, (int)Math.Min(width - text.Length, 1 << 20));
            return (flags & AdjustField) switch
            {
                Left => text + padding,
                Internal => text[..field.PrefixLength] + padding + text[field.PrefixLength..],
                _ => padding + text,
            };
        }

        /// <summary>
        /// An extractor's view of the characters in front of it, fed one at a time. Feed answers
        /// whether the character belongs to the field (and so is consumed).
        /// </summary>
        public abstract class Scanner
        {
            protected readonly StringBuilder Text = new();

            public abstract bool Feed(int c);

            public override string ToString() => Text.ToString();
        }

        /// <summary>VC11's _Getifld: [sign] then digits in the base basefield chooses (none: from a 0 or 0x prefix).</summary>
        public sealed class IntegerScanner : Scanner
        {
            private int _state;
            private bool _seenDigit;

            public IntegerScanner(uint flags)
            {
                uint basefield = flags & BaseField;
                Base = basefield == Oct ? 8 : basefield == Hex ? 16 : basefield == 0 ? 0 : 10;
            }

            /// <summary>The base read in: 0 until a prefix decides it, which then means 10.</summary>
            public int Base { get; private set; }

            public bool Negative { get; private set; }

            public bool SeenDigit => _seenDigit;

            public override bool Feed(int c)
            {
                switch (_state)
                {
                    case 0:
                        _state = 1;
                        if (c is '+' or '-')
                        {
                            Negative = c == '-';
                            return true;
                        }

                        return Feed(c);

                    case 1:
                        if (c == '0')
                        {
                            _seenDigit = true;
                            _state = 2;
                            return true;
                        }

                        _state = 3;
                        return Feed(c);

                    case 2:
                        if (c is 'x' or 'X' && Base is 0 or 16)
                        {
                            Base = 16;
                            _seenDigit = false;
                            _state = 3;
                            return true;
                        }

                        if (Base == 0)
                        {
                            Base = 8;
                        }

                        _state = 3;
                        return Feed(c);

                    default:
                        if (Digit(c) < (Base == 0 ? 10 : Base))
                        {
                            if (Text.Length > 0 || c != '0')
                            {
                                Text.Append((char)c);
                            }

                            _seenDigit = true;
                            return true;
                        }

                        return false;
                }
            }

            private static int Digit(int c) => c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => 99,
            };

            /// <summary>The magnitude read, or null for no digits / more than 64 bits (ERANGE).</summary>
            public BigInteger? Magnitude()
            {
                if (!_seenDigit)
                {
                    return null;
                }

                BigInteger value = BigInteger.Zero;
                int radix = Base == 0 ? 10 : Base;
                foreach (char ch in Text.ToString())
                {
                    value = (value * radix) + Digit(ch);
                }

                return value;
            }

            /// <summary>strtol/strtoll: a signed value of <paramref name="bits"/> bits, or null.</summary>
            public long? AsSigned(int bits)
            {
                BigInteger? m = Magnitude();
                if (m is null)
                {
                    return null;
                }

                BigInteger v = Negative ? -m.Value : m.Value;
                BigInteger min = -(BigInteger.One << (bits - 1)), max = (BigInteger.One << (bits - 1)) - 1;
                return v < min || v > max ? null : (long)v;
            }

            /// <summary>strtoul/strtoull: the magnitude must fit <paramref name="bits"/> bits; a sign negates modulo 2^bits.</summary>
            public ulong? AsUnsigned(int bits)
            {
                BigInteger? m = Magnitude();
                BigInteger limit = BigInteger.One << bits;
                if (m is null || m.Value >= limit)
                {
                    return null;
                }

                BigInteger v = Negative ? (limit - m.Value) % limit : m.Value;
                return (ulong)v;
            }
        }

        /// <summary>VC11's _Getffld: [sign] digits [. digits] [e [sign] digits], at least one mantissa digit.</summary>
        public sealed class FloatScanner : Scanner
        {
            private int _state;
            private bool _mantissaDigit;
            private bool _exponentStarted;
            private bool _exponentDigit;

            public override bool Feed(int c)
            {
                switch (_state)
                {
                    case 0:
                        _state = 1;
                        if (c is '+' or '-')
                        {
                            Text.Append((char)c);
                            return true;
                        }

                        return Feed(c);
                    case 1:
                    case 2:
                        if (c is >= '0' and <= '9')
                        {
                            Text.Append((char)c);
                            _mantissaDigit = true;
                            return true;
                        }

                        if (c == '.' && _state == 1)
                        {
                            Text.Append('.');
                            _state = 2;
                            return true;
                        }

                        if (c is 'e' or 'E' && _mantissaDigit)
                        {
                            Text.Append('e');
                            _exponentStarted = true;
                            _state = 3;
                            return true;
                        }

                        return false;
                    case 3:
                        _state = 4;
                        if (c is '+' or '-')
                        {
                            Text.Append((char)c);
                            return true;
                        }

                        return Feed(c);
                    default:
                        if (c is >= '0' and <= '9')
                        {
                            Text.Append((char)c);
                            _exponentDigit = true;
                            return true;
                        }

                        return false;
                }
            }

            /// <summary>The value read, or null for a malformed field or one out of range for the target.</summary>
            public double? AsDouble(bool single)
            {
                if (!_mantissaDigit || (_exponentStarted && !_exponentDigit))
                {
                    return null;
                }

                if (!double.TryParse(Text.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                {
                    return null;
                }

                if (double.IsInfinity(v) || (single && float.IsInfinity((float)v)))
                {
                    return null;
                }

                return v;
            }
        }

        /// <summary>boolalpha extraction: the longest prefix of "false" or "true"; a full name succeeds.</summary>
        public sealed class NameScanner : Scanner
        {
            private readonly string[] _names;

            public NameScanner(params string[] names) => _names = names;

            public override bool Feed(int c)
            {
                string next = Text.ToString() + (char)c;
                if (_names.Contains(Text.ToString()) || !_names.Any(n => n.StartsWith(next, StringComparison.Ordinal)))
                {
                    return false;
                }

                Text.Append((char)c);
                return true;
            }

            /// <summary>The index of the name read, or -1.</summary>
            public int Match => Array.IndexOf(_names, Text.ToString());
        }
    }
}

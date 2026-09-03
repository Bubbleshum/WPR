using System.Globalization;
using System.Text;

namespace WPR.Wp8Native
{
    /// <summary>
    /// A C <c>printf</c> format interpreter, operating on emulated memory.
    /// </summary>
    /// <remarks>
    /// Written because the image formats a string, parses it back, and throws when the
    /// result is empty - which is what a stubbed <c>sprintf</c> produces. Everything here
    /// follows the C locale.
    /// </remarks>
    public sealed class PrintfFormatter
    {
        private readonly ArmEmulator _emulator;
        private readonly CallFrame _frame;

        public PrintfFormatter(ArmEmulator emulator, CallFrame frame)
        {
            _emulator = emulator;
            _frame = frame;
        }

        public string Format(string format, VarArgReader arguments)
        {
            StringBuilder output = new();

            for (int i = 0; i < format.Length; i++)
            {
                if (format[i] != '%')
                {
                    output.Append(format[i]);
                    continue;
                }

                if (i + 1 < format.Length && format[i + 1] == '%')
                {
                    output.Append('%');
                    i++;
                    continue;
                }

                i++;
                output.Append(FormatOne(format, ref i, arguments));
            }

            return output.ToString();
        }

        private string FormatOne(string format, ref int index, VarArgReader arguments)
        {
            // --- flags ---
            bool leftAlign = false, forceSign = false, spaceSign = false, alternate = false, zeroPad = false;
            while (index < format.Length)
            {
                switch (format[index])
                {
                    case '-': leftAlign = true; break;
                    case '+': forceSign = true; break;
                    case ' ': spaceSign = true; break;
                    case '#': alternate = true; break;
                    case '0': zeroPad = true; break;
                    default: goto flagsDone;
                }

                index++;
            }

        flagsDone:

            // --- width, which may itself be an argument ---
            int width = 0;
            if (index < format.Length && format[index] == '*')
            {
                width = arguments.NextInt32();
                if (width < 0)
                {
                    leftAlign = true;
                    width = -width;
                }

                index++;
            }
            else
            {
                while (index < format.Length && char.IsAsciiDigit(format[index]))
                {
                    width = (width * 10) + (format[index++] - '0');
                }
            }

            // --- precision ---
            int precision = -1;
            if (index < format.Length && format[index] == '.')
            {
                index++;
                precision = 0;
                if (index < format.Length && format[index] == '*')
                {
                    precision = arguments.NextInt32();
                    index++;
                }
                else
                {
                    while (index < format.Length && char.IsAsciiDigit(format[index]))
                    {
                        precision = (precision * 10) + (format[index++] - '0');
                    }
                }
            }

            // --- length modifier ---
            bool isLong64 = false;
            bool isWide = false;
            while (index < format.Length)
            {
                if (format.AsSpan(index).StartsWith("I64", StringComparison.Ordinal))
                {
                    isLong64 = true;
                    index += 3;
                    continue;
                }

                char modifier = format[index];
                if (modifier is 'h' or 'j' or 't' or 'z' or 'L' or 'I')
                {
                    index++;
                    continue;
                }

                if (modifier is 'l' or 'w')
                {
                    // "ll" is 64-bit; a single "l" before s or c means wide.
                    if (modifier == 'l' && index + 1 < format.Length && format[index + 1] == 'l')
                    {
                        isLong64 = true;
                        index += 2;
                        continue;
                    }

                    isWide = true;
                    index++;
                    continue;
                }

                break;
            }

            if (index >= format.Length)
            {
                return string.Empty;
            }

            char conversion = format[index];

            switch (conversion)
            {
                case 'd':
                case 'i':
                {
                    long value = isLong64 ? arguments.NextInt64() : arguments.NextInt32();
                    string digits = Math.Abs(value).ToString(CultureInfo.InvariantCulture);
                    string sign = value < 0 ? "-" : forceSign ? "+" : spaceSign ? " " : string.Empty;
                    return PadNumber(digits, sign, precision, width, leftAlign, zeroPad);
                }

                case 'u':
                {
                    ulong value = isLong64 ? arguments.NextUInt64() : arguments.NextUInt32();
                    return PadNumber(
                        value.ToString(CultureInfo.InvariantCulture),
                        string.Empty, precision, width, leftAlign, zeroPad);
                }

                case 'x':
                case 'X':
                {
                    ulong value = isLong64 ? arguments.NextUInt64() : arguments.NextUInt32();
                    string digits = value.ToString(conversion == 'x' ? "x" : "X", CultureInfo.InvariantCulture);
                    string prefix = alternate && value != 0 ? (conversion == 'x' ? "0x" : "0X") : string.Empty;
                    return PadNumber(digits, prefix, precision, width, leftAlign, zeroPad);
                }

                case 'o':
                {
                    ulong value = isLong64 ? arguments.NextUInt64() : arguments.NextUInt32();
                    string digits = Convert.ToString((long)value, 8);
                    return PadNumber(
                        alternate && !digits.StartsWith('0') ? "0" + digits : digits,
                        string.Empty, precision, width, leftAlign, zeroPad);
                }

                case 'p':
                {
                    long pointer = arguments.NextPointer();
                    return Pad(pointer.ToString("X8", CultureInfo.InvariantCulture), width, leftAlign, false);
                }

                case 'f':
                case 'F':
                case 'e':
                case 'E':
                case 'g':
                case 'G':
                {
                    double value = arguments.NextDouble();
                    string text = FormatFloat(value, conversion, precision < 0 ? 6 : precision, alternate);
                    string sign = string.Empty;
                    if (!text.StartsWith('-') && (forceSign || spaceSign))
                    {
                        sign = forceSign ? "+" : " ";
                    }

                    return PadNumber(text.TrimStart('-'), text.StartsWith('-') ? "-" : sign,
                        -1, width, leftAlign, zeroPad);
                }

                case 'c':
                {
                    int raw = arguments.NextInt32();
                    return Pad(((char)(isWide ? raw & 0xFFFF : raw & 0xFF)).ToString(), width, leftAlign, false);
                }

                case 's':
                case 'S':
                {
                    long pointer = arguments.NextPointer();
                    bool wide = conversion == 'S' || isWide;
                    string text = pointer == 0
                        ? "(null)"
                        : wide ? _emulator.ReadUtf16String(pointer) : _frame.ReadNarrowString(pointer);

                    if (precision >= 0 && text.Length > precision)
                    {
                        text = text[..precision];
                    }

                    return Pad(text, width, leftAlign, false);
                }

                case 'n':
                    // Writes the character count back through a pointer. MSVC disables it by
                    // default and it is a well-known hazard; the argument is consumed and
                    // otherwise ignored.
                    arguments.NextPointer();
                    return string.Empty;

                default:
                    // Not a conversion we know. Emit it literally rather than silently
                    // dropping it, and consume nothing.
                    return "%" + conversion;
            }
        }

        private static string FormatFloat(double value, char conversion, int precision, bool alternate)
        {
            if (double.IsNaN(value))
            {
                return char.IsUpper(conversion) ? "NAN" : "nan";
            }

            if (double.IsInfinity(value))
            {
                string text = value < 0 ? "-inf" : "inf";
                return char.IsUpper(conversion) ? text.ToUpperInvariant() : text;
            }

            switch (char.ToLowerInvariant(conversion))
            {
                case 'f':
                    return value.ToString("F" + precision, CultureInfo.InvariantCulture);

                case 'e':
                    return NormaliseExponent(
                        value.ToString((conversion == 'E' ? "E" : "e") + precision, CultureInfo.InvariantCulture));

                default:
                {
                    // %g: whichever of %e and %f is shorter, with trailing zeros removed
                    // unless # was given. Precision 0 is treated as 1.
                    int significant = precision == 0 ? 1 : precision;
                    int exponent = value == 0 ? 0 : (int)Math.Floor(Math.Log10(Math.Abs(value)));

                    string text = exponent < -4 || exponent >= significant
                        ? NormaliseExponent(value.ToString(
                            (char.IsUpper(conversion) ? "E" : "e") + Math.Max(0, significant - 1),
                            CultureInfo.InvariantCulture))
                        : value.ToString("F" + Math.Max(0, significant - 1 - exponent), CultureInfo.InvariantCulture);

                    if (!alternate && text.Contains('.'))
                    {
                        // Trim the fraction, but never into the exponent.
                        int exponentAt = text.IndexOfAny(['e', 'E']);
                        string mantissa = exponentAt < 0 ? text : text[..exponentAt];
                        string suffix = exponentAt < 0 ? string.Empty : text[exponentAt..];
                        mantissa = mantissa.TrimEnd('0').TrimEnd('.');
                        text = mantissa + suffix;
                    }

                    return text;
                }
            }
        }

        /// <summary>
        /// .NET writes a three-digit exponent (<c>e+006</c>); C writes at least two
        /// (<c>e+06</c>). Anything reading the output back expects the C form.
        /// </summary>
        private static string NormaliseExponent(string text)
        {
            int at = text.IndexOfAny(['e', 'E']);
            if (at < 0 || at + 2 >= text.Length)
            {
                return text;
            }

            string mantissa = text[..(at + 2)];       // includes the sign
            string digits = text[(at + 2)..].TrimStart('0');
            return mantissa + (digits.Length < 2 ? digits.PadLeft(2, '0') : digits);
        }

        /// <summary>
        /// Applies precision (a minimum digit count for integers) and then width, keeping
        /// any sign or base prefix outside the zero padding.
        /// </summary>
        private static string PadNumber(
            string digits, string prefix, int precision, int width, bool leftAlign, bool zeroPad)
        {
            if (precision >= 0)
            {
                digits = digits.PadLeft(precision, '0');
                zeroPad = false;    // an explicit precision defeats the 0 flag
            }

            string body = prefix + digits;
            if (body.Length >= width)
            {
                return body;
            }

            if (leftAlign)
            {
                return body.PadRight(width);
            }

            return zeroPad
                ? prefix + digits.PadLeft(width - prefix.Length, '0')
                : body.PadLeft(width);
        }

        private static string Pad(string text, int width, bool leftAlign, bool zeroPad)
        {
            if (text.Length >= width)
            {
                return text;
            }

            return leftAlign ? text.PadRight(width) : text.PadLeft(width, zeroPad ? '0' : ' ');
        }
    }
}

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

        /// <summary>
        /// True while formatting for a wide function (swprintf and friends), where a bare %s and %c
        /// take wide arguments and %S / %C narrow ones - the reverse of the narrow functions. An h
        /// prefix always means narrow and l or w always means wide.
        /// </summary>
        private bool _wideFunction;

        public string Format(string format, VarArgReader arguments) => Format(format, arguments, wideFunction: false);

        public string Format(string format, VarArgReader arguments, bool wideFunction)
        {
            _wideFunction = wideFunction;
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
            bool isShort = false;
            while (index < format.Length)
            {
                if (format.AsSpan(index).StartsWith("I64", StringComparison.Ordinal))
                {
                    isLong64 = true;
                    index += 3;
                    continue;
                }

                // I32 is the explicit 32-bit size, which is what an int already is here.
                if (format.AsSpan(index).StartsWith("I32", StringComparison.Ordinal))
                {
                    index += 3;
                    continue;
                }

                char modifier = format[index];

                // h: a short for an integer conversion, a narrow string or character for s and c.
                if (modifier == 'h')
                {
                    isShort = true;
                    index++;
                    continue;
                }

                if (modifier is 'j' or 't' or 'z' or 'L' or 'I')
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
                    if (isShort)
                    {
                        value = unchecked((short)value);
                    }

                    // The magnitude as unsigned, so long.MinValue has one; and precision 0 with a
                    // value of 0 prints no digits at all.
                    ulong magnitude = value < 0 ? unchecked((ulong)-value) : (ulong)value;
                    string digits = precision == 0 && value == 0 ? string.Empty : magnitude.ToString(CultureInfo.InvariantCulture);
                    string sign = value < 0 ? "-" : forceSign ? "+" : spaceSign ? " " : string.Empty;
                    return PadNumber(digits, sign, precision, width, leftAlign, zeroPad);
                }

                case 'u':
                {
                    ulong value = isLong64 ? arguments.NextUInt64() : arguments.NextUInt32();
                    if (isShort)
                    {
                        value = (ushort)value;
                    }

                    return PadNumber(
                        precision == 0 && value == 0 ? string.Empty : value.ToString(CultureInfo.InvariantCulture),
                        string.Empty, precision, width, leftAlign, zeroPad);
                }

                case 'x':
                case 'X':
                {
                    ulong value = isLong64 ? arguments.NextUInt64() : arguments.NextUInt32();
                    if (isShort)
                    {
                        value = (ushort)value;
                    }

                    string digits = precision == 0 && value == 0
                        ? string.Empty
                        : value.ToString(conversion == 'x' ? "x" : "X", CultureInfo.InvariantCulture);
                    string prefix = alternate && value != 0 ? (conversion == 'x' ? "0x" : "0X") : string.Empty;
                    return PadNumber(digits, prefix, precision, width, leftAlign, zeroPad);
                }

                case 'o':
                {
                    ulong value = isLong64 ? arguments.NextUInt64() : arguments.NextUInt32();
                    if (isShort)
                    {
                        value = (ushort)value;
                    }

                    string digits = precision == 0 && value == 0 ? string.Empty : Convert.ToString((long)value, 8);
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
                case 'C':
                {
                    int raw = arguments.NextInt32();
                    bool wide = WideArgument(conversion == 'C', isWide, isShort);
                    return Pad(((char)(wide ? raw & 0xFFFF : raw & 0xFF)).ToString(), width, leftAlign, false);
                }

                case 's':
                case 'S':
                {
                    long pointer = arguments.NextPointer();
                    bool wide = WideArgument(conversion == 'S', isWide, isShort);

                    // A precision bounds what is read as well as what is printed: it is how a
                    // caller prints a character array that is not NUL-terminated.
                    int limit = precision >= 0 ? precision : CallFrame.SaneLengthLimit;
                    string text = pointer == 0
                        ? "(null)"
                        : wide ? _emulator.ReadUtf16String(pointer, limit) : _frame.ReadNarrowString(pointer, limit);

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

        /// <summary>
        /// Whether a %s / %c argument is wide: l or w says wide and h says narrow outright;
        /// otherwise the lower-case conversion matches the function (narrow for printf, wide for
        /// wprintf) and the upper-case one is the other kind.
        /// </summary>
        private bool WideArgument(bool upperCase, bool longPrefix, bool shortPrefix)
            => longPrefix || (!shortPrefix && (upperCase ? !_wideFunction : _wideFunction));

        /// <summary>
        /// %e %f %g as the VC11 CRT renders them - 17 significant digits then zeros, a
        /// three-digit exponent, 1.#INF and friends; see <see cref="CrtFloatText"/>.
        /// </summary>
        private static string FormatFloat(double value, char conversion, int precision, bool alternate)
            => char.ToLowerInvariant(conversion) switch
            {
                'f' => CrtFloatText.Fixed(value, precision, alternate),
                'e' => CrtFloatText.Exponential(value, precision, char.IsUpper(conversion), alternate),
                _ => CrtFloatText.General(value, precision, char.IsUpper(conversion), alternate),
            };

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

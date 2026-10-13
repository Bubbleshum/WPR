using System.Globalization;
using System.Text;

namespace WPR.Wp8Native
{
    public sealed partial class CrtStrings
    {
        // ------------------------------------------------------------------ the printf family

        /// <summary>How a printf variant bounds its output; each one is a different documented contract.</summary>
        private enum Bound
        {
            /// <summary>_scprintf: nothing written, the length answered.</summary>
            CountOnly,

            /// <summary>sprintf: the caller promised the room.</summary>
            Unbounded,

            /// <summary>
            /// _snprintf (MSDN): len &lt; count stores it terminated; len == count stores it
            /// unterminated and answers len; longer stores count characters and answers -1.
            /// </summary>
            Count,

            /// <summary>
            /// _swprintf_c (the conforming swprintf): at most count-1 characters, always
            /// terminated, -1 if that was not all of it.
            /// </summary>
            Conforming,

            /// <summary>sprintf_s: all of it, or an empty buffer, ERANGE and -1.</summary>
            Secure,

            /// <summary>
            /// _snprintf_s: count &lt; size bounds the output at count characters (-1 if cut);
            /// otherwise as sprintf_s, except that _TRUNCATE keeps what fits and answers -1.
            /// </summary>
            SecureCount,
        }

        /// <summary>One printf variant: where its arguments are, by core position (-1 for absent).</summary>
        private void Printf(
            Dictionary<string, Action> handlers, string name, bool wide, Bound bound,
            int buffer, int size, int count, int format, int arguments, bool vaList)
            => handlers[name] = () =>
            {
                VarArgReader reader = vaList
                    ? VarArgReader.FromList(_emulator, A(arguments))
                    : new VarArgReader(_emulator, arguments);
                Emit(wide, bound,
                    buffer < 0 ? 0 : A(buffer),
                    size < 0 ? 0 : A(size),
                    count < 0 ? 0 : A(count),
                    A(format), reader);
            };

        private void Emit(bool wide, Bound bound, long buffer, long size, long count, long format, VarArgReader arguments)
        {
            if (format == 0)
            {
                SetErrno(Einval);
                ReturnInt(-1);
                return;
            }

            // Validation the CRT does before formatting anything.
            switch (bound)
            {
                case Bound.Unbounded when buffer == 0:
                case Bound.Count when buffer == 0 && count != 0:
                case Bound.Conforming when buffer == 0 && count != 0:
                case Bound.Secure when buffer == 0 || size == 0:
                    SetErrno(Einval);
                    ReturnInt(-1);
                    return;

                case Bound.SecureCount when count == 0 && buffer == 0 && size == 0:
                    ReturnInt(0);
                    return;

                case Bound.SecureCount when buffer == 0 || size == 0:
                    SetErrno(Einval);
                    ReturnInt(-1);
                    return;
            }

            string text = new PrintfFormatter(_emulator, _frame).Format(ReadText(format, wide), arguments, wide);

            // A narrow function given a wide string or character it cannot represent in the C
            // locale fails the whole call, as the CRT's wctomb-based output does.
            if (!wide && text.Any(c => c > 0xFF))
            {
                if (bound is Bound.Secure or Bound.SecureCount)
                {
                    WriteChar(buffer, 0, wide);
                }

                SetErrno(Eilseq);
                ReturnInt(-1);
                return;
            }

            int length = text.Length;
            switch (bound)
            {
                case Bound.CountOnly:
                    ReturnInt(length);
                    return;

                case Bound.Unbounded:
                    WriteText(buffer, text, wide);
                    ReturnInt(length);
                    return;

                case Bound.Count:
                    if (buffer == 0)
                    {
                        ReturnInt(length);   // count is 0: the size needed
                    }
                    else if (length < count)
                    {
                        WriteText(buffer, text, wide);
                        ReturnInt(length);
                    }
                    else
                    {
                        WriteText(buffer, text[..(int)Math.Min(count, length)], wide, terminate: false);
                        ReturnInt(length == count ? length : -1);
                    }

                    return;

                case Bound.Conforming:
                    if (length < count)
                    {
                        WriteText(buffer, text, wide);
                        ReturnInt(length);
                    }
                    else
                    {
                        if (count > 0)
                        {
                            WriteText(buffer, text[..(int)(count - 1)], wide);
                        }

                        ReturnInt(-1);
                    }

                    return;

                case Bound.Secure:
                    if (length < size)
                    {
                        WriteText(buffer, text, wide);
                        ReturnInt(length);
                    }
                    else
                    {
                        WriteChar(buffer, 0, wide);
                        SetErrno(Erange);
                        ReturnInt(-1);
                    }

                    return;

                case Bound.SecureCount:
                    if (count < size)
                    {
                        bool fits = length <= count;
                        WriteText(buffer, fits ? text : text[..(int)count], wide);
                        ReturnInt(fits ? length : -1);
                    }
                    else if (length < size)
                    {
                        WriteText(buffer, text, wide);
                        ReturnInt(length);
                    }
                    else if (count == TruncateCount)
                    {
                        WriteText(buffer, text[..(int)(size - 1)], wide);
                        ReturnInt(-1);
                    }
                    else
                    {
                        WriteChar(buffer, 0, wide);
                        SetErrno(Erange);
                        ReturnInt(-1);
                    }

                    return;
            }
        }

        private void RegisterFormatting(Dictionary<string, Action> handlers)
        {
            const bool N = false, W = true, List = true, Var = false;

            // sprintf itself stays CrtLibrary's (it also feeds the probe's formatted-string log).
            Printf(handlers, "_sprintf_l", N, Bound.Unbounded, 0, -1, -1, 1, 3, Var);
            Printf(handlers, "vsprintf", N, Bound.Unbounded, 0, -1, -1, 1, 2, List);
            Printf(handlers, "_vsprintf_l", N, Bound.Unbounded, 0, -1, -1, 1, 3, List);
            Printf(handlers, "_swprintf", W, Bound.Unbounded, 0, -1, -1, 1, 2, Var);
            Printf(handlers, "__swprintf_l", W, Bound.Unbounded, 0, -1, -1, 1, 3, Var);
            Printf(handlers, "_vswprintf", W, Bound.Unbounded, 0, -1, -1, 1, 2, List);
            Printf(handlers, "__vswprintf_l", W, Bound.Unbounded, 0, -1, -1, 1, 3, List);

            Printf(handlers, "_snprintf", N, Bound.Count, 0, -1, 1, 2, 3, Var);
            Printf(handlers, "_snprintf_l", N, Bound.Count, 0, -1, 1, 2, 4, Var);
            Printf(handlers, "_vsnprintf", N, Bound.Count, 0, -1, 1, 2, 3, List);
            Printf(handlers, "_vsnprintf_l", N, Bound.Count, 0, -1, 1, 2, 4, List);
            Printf(handlers, "_snwprintf", W, Bound.Count, 0, -1, 1, 2, 3, Var);
            Printf(handlers, "_snwprintf_l", W, Bound.Count, 0, -1, 1, 2, 4, Var);
            Printf(handlers, "_vsnwprintf", W, Bound.Count, 0, -1, 1, 2, 3, List);
            Printf(handlers, "_vsnwprintf_l", W, Bound.Count, 0, -1, 1, 2, 4, List);

            Printf(handlers, "_swprintf_c", W, Bound.Conforming, 0, -1, 1, 2, 3, Var);
            Printf(handlers, "_swprintf_c_l", W, Bound.Conforming, 0, -1, 1, 2, 4, Var);
            Printf(handlers, "_vswprintf_c", W, Bound.Conforming, 0, -1, 1, 2, 3, List);
            Printf(handlers, "_vswprintf_c_l", W, Bound.Conforming, 0, -1, 1, 2, 4, List);

            Printf(handlers, "sprintf_s", N, Bound.Secure, 0, 1, -1, 2, 3, Var);
            Printf(handlers, "_sprintf_s_l", N, Bound.Secure, 0, 1, -1, 2, 4, Var);
            Printf(handlers, "vsprintf_s", N, Bound.Secure, 0, 1, -1, 2, 3, List);
            Printf(handlers, "_vsprintf_s_l", N, Bound.Secure, 0, 1, -1, 2, 4, List);
            Printf(handlers, "swprintf_s", W, Bound.Secure, 0, 1, -1, 2, 3, Var);
            Printf(handlers, "_swprintf_s_l", W, Bound.Secure, 0, 1, -1, 2, 4, Var);
            Printf(handlers, "vswprintf_s", W, Bound.Secure, 0, 1, -1, 2, 3, List);
            Printf(handlers, "_vswprintf_s_l", W, Bound.Secure, 0, 1, -1, 2, 4, List);

            Printf(handlers, "_snprintf_s", N, Bound.SecureCount, 0, 1, 2, 3, 4, Var);
            Printf(handlers, "_snprintf_s_l", N, Bound.SecureCount, 0, 1, 2, 3, 5, Var);
            Printf(handlers, "_vsnprintf_s", N, Bound.SecureCount, 0, 1, 2, 3, 4, List);
            Printf(handlers, "_vsnprintf_s_l", N, Bound.SecureCount, 0, 1, 2, 3, 5, List);
            Printf(handlers, "_snwprintf_s", W, Bound.SecureCount, 0, 1, 2, 3, 4, Var);
            Printf(handlers, "_snwprintf_s_l", W, Bound.SecureCount, 0, 1, 2, 3, 5, Var);
            Printf(handlers, "_vsnwprintf_s", W, Bound.SecureCount, 0, 1, 2, 3, 4, List);
            Printf(handlers, "_vsnwprintf_s_l", W, Bound.SecureCount, 0, 1, 2, 3, 5, List);

            Printf(handlers, "_scprintf", N, Bound.CountOnly, -1, -1, -1, 0, 1, Var);
            Printf(handlers, "_scprintf_l", N, Bound.CountOnly, -1, -1, -1, 0, 2, Var);
            Printf(handlers, "_vscprintf", N, Bound.CountOnly, -1, -1, -1, 0, 1, List);
            Printf(handlers, "_vscprintf_l", N, Bound.CountOnly, -1, -1, -1, 0, 2, List);
            Printf(handlers, "_scwprintf", W, Bound.CountOnly, -1, -1, -1, 0, 1, Var);
            Printf(handlers, "_scwprintf_l", W, Bound.CountOnly, -1, -1, -1, 0, 2, Var);
            Printf(handlers, "_vscwprintf", W, Bound.CountOnly, -1, -1, -1, 0, 1, List);
            Printf(handlers, "_vscwprintf_l", W, Bound.CountOnly, -1, -1, -1, 0, 2, List);
        }

        // ------------------------------------------------------------------ the scanf family

        private void RegisterScanning(Dictionary<string, Action> handlers)
        {
            // int sscanf(const char* input, const char* format, ...), and the rest.
            Scanf(handlers, "sscanf", wide: false, secure: false, firstVariadic: 2);
            Scanf(handlers, "_sscanf_l", wide: false, secure: false, firstVariadic: 3);
            Scanf(handlers, "sscanf_s", wide: false, secure: true, firstVariadic: 2);
            Scanf(handlers, "_sscanf_s_l", wide: false, secure: true, firstVariadic: 3);
            Scanf(handlers, "swscanf", wide: true, secure: false, firstVariadic: 2);
            Scanf(handlers, "_swscanf_l", wide: true, secure: false, firstVariadic: 3);
            Scanf(handlers, "swscanf_s", wide: true, secure: true, firstVariadic: 2);
            Scanf(handlers, "_swscanf_s_l", wide: true, secure: true, firstVariadic: 3);
        }

        private void Scanf(Dictionary<string, Action> handlers, string name, bool wide, bool secure, int firstVariadic)
            => handlers[name] = () =>
            {
                long input = A(0), format = A(1);
                if (input == 0 || format == 0)
                {
                    SetErrno(Einval);
                    ReturnInt(-1);
                    return;
                }

                ReturnInt(Scan(ReadText(input, wide), ReadText(format, wide), new VarArgReader(_emulator, firstVariadic), wide, secure));
            };

        /// <summary>
        /// The scanf engine (VC11's <c>input.c</c> semantics): %d %i %u %o %x %X %p, %e %f %g
        /// (float, or double with l / L), %c %C %s %S %[set] (ranges like a-z, ^ to negate, ] first
        /// to include it), %n, %%, a field width, * to suppress, and the size prefixes h hh l ll I64
        /// I32. Integers wrap, as the CRT's do. The secure forms take a size (in characters) after
        /// each %c %s %[ pointer; a field that does not fit stores an empty string, sets ENOMEM and
        /// ends the scan.
        /// </summary>
        /// <returns>The fields assigned, or EOF (-1) when the input ran out before anything matched.</returns>
        private int Scan(string input, string format, VarArgReader arguments, bool wide, bool secure)
        {
            int at = 0;
            int assigned = 0;
            bool matched = false;

            int Finish(bool inputFailure)
            {
                LastScanConsumed = at;
                return inputFailure && !matched ? -1 : assigned;
            }

            for (int f = 0; f < format.Length; f++)
            {
                char fc = format[f];
                if (IsSpace(fc, wide))
                {
                    while (at < input.Length && IsSpace(input[at], wide)) at++;
                    continue;
                }

                if (fc != '%' || (f + 1 < format.Length && format[f + 1] == '%'))
                {
                    if (fc == '%')
                    {
                        f++;
                        while (at < input.Length && IsSpace(input[at], wide)) at++;
                    }

                    if (at >= input.Length)
                    {
                        return Finish(inputFailure: true);
                    }

                    if (input[at] != fc)
                    {
                        return Finish(inputFailure: false);
                    }

                    at++;
                    continue;
                }

                f++;
                bool suppress = false;
                if (f < format.Length && format[f] == '*')
                {
                    suppress = true;
                    f++;
                }

                int width = 0;
                while (f < format.Length && char.IsAsciiDigit(format[f]))
                {
                    width = (width * 10) + (format[f++] - '0');
                }

                if (width == 0)
                {
                    width = int.MaxValue;
                }

                // Size prefixes.
                int size = 0;           // 0 default, 1 hh, 2 h, 4 l, 8 ll / I64
                bool longPrefix = false, shortPrefix = false;
                while (f < format.Length)
                {
                    if (format.AsSpan(f).StartsWith("I64", StringComparison.Ordinal)) { size = 8; f += 3; continue; }
                    if (format.AsSpan(f).StartsWith("I32", StringComparison.Ordinal)) { size = 4; f += 3; continue; }
                    char p = format[f];
                    if (p == 'h') { size = size == 2 ? 1 : 2; shortPrefix = true; f++; continue; }
                    if (p == 'l') { size = size == 4 ? 8 : 4; longPrefix = true; f++; continue; }
                    if (p is 'L' or 'q') { size = 8; longPrefix = true; f++; continue; }
                    if (p is 'w') { longPrefix = true; f++; continue; }
                    if (p is 'j' or 'z' or 't' or 'I') { f++; continue; }
                    break;
                }

                if (f >= format.Length)
                {
                    break;
                }

                char conversion = format[f];
                if (conversion == 'n')
                {
                    if (!suppress)
                    {
                        StoreInteger(arguments.NextPointer(), (ulong)at, size);
                    }

                    continue;
                }

                if (conversion is not ('c' or 'C' or '['))
                {
                    while (at < input.Length && IsSpace(input[at], wide)) at++;
                }

                if (at >= input.Length)
                {
                    return Finish(inputFailure: true);
                }

                switch (conversion)
                {
                    case 'd' or 'i' or 'u' or 'o' or 'x' or 'X' or 'p':
                    {
                        int radix = conversion switch { 'o' => 8, 'x' or 'X' or 'p' => 16, 'i' => 0, _ => 10 };
                        int start = at;
                        bool negative = false;
                        if (input[at] is '+' or '-')
                        {
                            negative = input[at] == '-';
                            at++;
                        }

                        int digits = 0;
                        bool zero = at < input.Length && at - start < width && input[at] == '0';
                        if (zero && at + 1 < input.Length && at + 1 - start < width && input[at + 1] is 'x' or 'X' && radix is 0 or 16)
                        {
                            radix = 16;
                            at += 2;
                            digits++;   // the 0 of the prefix is itself a digit
                        }
                        else if (radix == 0)
                        {
                            radix = zero ? 8 : 10;
                        }

                        ulong value = 0;
                        while (at < input.Length && at - start < width)
                        {
                            int digit = DigitValue(input[at], wide);
                            if (digit < 0 || digit >= radix)
                            {
                                break;
                            }

                            value = unchecked((value * (ulong)radix) + (ulong)digit);
                            at++;
                            digits++;
                        }

                        if (digits == 0)
                        {
                            return Finish(inputFailure: false);
                        }

                        if (negative)
                        {
                            value = unchecked(0 - value);
                        }

                        matched = true;
                        if (!suppress)
                        {
                            StoreInteger(arguments.NextPointer(), value, conversion == 'p' ? 4 : size);
                            assigned++;
                        }

                        break;
                    }

                    case 'e' or 'E' or 'f' or 'g' or 'G':
                    {
                        int start = at;
                        int end = Math.Min(input.Length, width == int.MaxValue ? input.Length : start + width);
                        int scan = start;
                        if (scan < end && input[scan] is '+' or '-') scan++;
                        int digits = 0;
                        while (scan < end && char.IsAsciiDigit(input[scan])) { scan++; digits++; }
                        if (scan < end && input[scan] == '.')
                        {
                            scan++;
                            while (scan < end && char.IsAsciiDigit(input[scan])) { scan++; digits++; }
                        }

                        if (digits == 0)
                        {
                            return Finish(inputFailure: false);
                        }

                        int mantissaEnd = scan;
                        if (scan < end && input[scan] is 'e' or 'E')
                        {
                            int exponent = scan + 1;
                            if (exponent < end && input[exponent] is '+' or '-') exponent++;
                            int exponentDigits = exponent;
                            while (exponent < end && char.IsAsciiDigit(input[exponent])) exponent++;

                            // The CRT consumes the e (and sign) even with no exponent digits after.
                            // Without exponent digits the value is the mantissa alone.
                            scan = exponent;
                            if (exponent != exponentDigits)
                            {
                                mantissaEnd = exponent;
                            }
                        }

                        at = scan;
                        string number = input[start..mantissaEnd];
                        double value = double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                            ? parsed
                            : number.Contains("e-", StringComparison.OrdinalIgnoreCase) ? 0 : double.PositiveInfinity;

                        matched = true;
                        if (!suppress)
                        {
                            long target = arguments.NextPointer();
                            if (size is 4 or 8)
                            {
                                _emulator.WriteMemory(target, BitConverter.GetBytes(value));
                            }
                            else
                            {
                                _emulator.WriteMemory(target, BitConverter.GetBytes((float)value));
                            }

                            assigned++;
                        }

                        break;
                    }

                    case 'c' or 'C' or 's' or 'S' or '[':
                    {
                        bool wideTarget = longPrefix || (!shortPrefix && (char.IsUpper(conversion) ? !wide : wide));
                        string field;
                        if (conversion is 'c' or 'C')
                        {
                            int count = width == int.MaxValue ? 1 : width;
                            if (at + count > input.Length)
                            {
                                return Finish(inputFailure: true);   // the input ended inside the field
                            }

                            field = input.Substring(at, count);
                        }
                        else if (conversion is 's' or 'S')
                        {
                            int start = at;
                            while (at < input.Length && at - start < width && !IsSpace(input[at], wide)) at++;
                            field = input[start..at];
                            at = start;
                        }
                        else
                        {
                            (Func<char, bool> member, int close) = ParseSet(format, f);
                            if (close < 0)
                            {
                                return Finish(inputFailure: false);
                            }

                            f = close;
                            int start = at;
                            while (at < input.Length && at - start < width && member(input[at])) at++;
                            field = input[start..at];
                            at = start;
                            if (field.Length == 0)
                            {
                                return Finish(inputFailure: false);
                            }
                        }

                        at += field.Length;
                        matched = true;
                        if (suppress)
                        {
                            break;
                        }

                        long target = arguments.NextPointer();
                        bool terminate = conversion is not ('c' or 'C');
                        if (secure)
                        {
                            long room = arguments.NextUInt32();
                            if (field.Length + (terminate ? 1 : 0) > room)
                            {
                                if (room > 0)
                                {
                                    WriteChar(target, 0, wideTarget);
                                }

                                SetErrno(Enomem);
                                LastScanConsumed = at;
                                return assigned;
                            }
                        }

                        if (!wideTarget && field.Any(c => c > 0xFF))
                        {
                            field = new string(field.Select(c => c > 0xFF ? '?' : c).ToArray());
                        }

                        WriteText(target, field, wideTarget, terminate);
                        assigned++;
                        break;
                    }

                    default:
                        // Not a conversion: the CRT treats the format as broken and stops.
                        return Finish(inputFailure: false);
                }
            }

            LastScanConsumed = at;
            return assigned;
        }

        /// <summary>How many input characters the last <see cref="Scan"/> consumed: what fscanf advances the stream by.</summary>
        internal int LastScanConsumed { get; private set; }

        /// <summary>
        /// The scanf engine over text that came from somewhere other than a guest string (fscanf's
        /// stream): the fields assigned, or EOF, and how much of <paramref name="input"/> was consumed.
        /// </summary>
        internal int ScanText(string input, string format, VarArgReader arguments, bool wide, bool secure, out int consumed)
        {
            int result = Scan(input, format, arguments, wide, secure);
            consumed = LastScanConsumed;
            return result;
        }

        /// <summary>Stores an integer conversion's result at the width its size prefix names.</summary>
        private void StoreInteger(long target, ulong value, int size)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            int length = size switch { 1 => 1, 2 => 2, 8 => 8, _ => 4 };
            _emulator.WriteMemory(target, bytes[..length]);
        }

        /// <summary>A %[ set starting at <paramref name="open"/> (the '['): its membership test and the index of its ']'.</summary>
        private static (Func<char, bool> Member, int Close) ParseSet(string format, int open)
        {
            int at = open + 1;
            bool invert = at < format.Length && format[at] == '^';
            if (invert) at++;
            int first = at;
            if (at < format.Length && format[at] == ']') at++;   // a leading ] is a member
            while (at < format.Length && format[at] != ']') at++;
            if (at >= format.Length)
            {
                return (_ => false, -1);
            }

            string body = format[first..at];
            List<(char Low, char High)> ranges = new();
            for (int i = 0; i < body.Length; i++)
            {
                // a-z is a range; a - at either end is itself.
                if (i + 2 < body.Length && body[i + 1] == '-')
                {
                    char low = body[i], high = body[i + 2];
                    ranges.Add(low <= high ? (low, high) : (high, low));
                    i += 2;
                }
                else
                {
                    ranges.Add((body[i], body[i]));
                }
            }

            bool Member(char c) => ranges.Any(r => c >= r.Low && c <= r.High) != invert;
            return (Member, at);
        }

        // ------------------------------------------------------------------ error text

        /// <summary>The CRT's _sys_errlist, verbatim (including its "Resource device" for EBUSY).</summary>
        private static readonly string[] ErrorMessages =
        [
            "No error", "Operation not permitted", "No such file or directory", "No such process",
            "Interrupted function call", "Input/output error", "No such device or address", "Arg list too long",
            "Exec format error", "Bad file descriptor", "No child processes", "Resource temporarily unavailable",
            "Not enough space", "Permission denied", "Bad address", "Unknown error", "Resource device",
            "File exists", "Improper link", "No such device", "Not a directory", "Is a directory",
            "Invalid argument", "Too many open files in system", "Too many open files",
            "Inappropriate I/O control operation", "Unknown error", "File too large", "No space left on device",
            "Invalid seek", "Read-only file system", "Too many links", "Broken pipe", "Domain error",
            "Result too large", "Unknown error", "Resource deadlock avoided", "Unknown error", "Filename too long",
            "No locks available", "Function not implemented", "Directory not empty", "Illegal byte sequence",
            "Unknown error",
        ];

        private static string ErrorMessage(int error)
            => error >= 0 && error < ErrorMessages.Length ? ErrorMessages[error] : ErrorMessages[^1];

        private readonly Dictionary<int, long> _strerrorBuffers = new();
        private readonly Dictionary<int, long> _wcserrorBuffers = new();

        private void RegisterErrorText(Dictionary<string, Action> handlers)
        {
            // char* strerror(int) / wchar_t* _wcserror(int): the message, in a per-thread buffer.
            handlers["strerror"] = () => ErrorText(wide: false, _strerrorBuffers);
            handlers["_wcserror"] = () => ErrorText(wide: true, _wcserrorBuffers);

            // errno_t strerror_s(char* buffer, size_t size, int error) and _wcserror_s: the message,
            // cut to fit (the CRT copies it with _TRUNCATE, so a short buffer is not an error).
            handlers["strerror_s"] = () => ErrorTextSecure(wide: false);
            handlers["_wcserror_s"] = () => ErrorTextSecure(wide: true);
        }

        private void ErrorText(bool wide, Dictionary<int, long> buffers)
        {
            long buffer = ThreadBuffer(buffers, 96 * Unit(wide));
            WriteText(buffer, ErrorMessage(S(0)), wide);
            _frame.Return(buffer);
        }

        private void ErrorTextSecure(bool wide)
        {
            long buffer = A(0), size = A(1);
            int error = S(2);
            if (buffer == 0 || size == 0)
            {
                Fail(Einval);
                return;
            }

            string message = ErrorMessage(error);
            WriteText(buffer, message.Length < size ? message : message[..(int)(size - 1)], wide);
            ReturnInt(0);
        }
    }
}

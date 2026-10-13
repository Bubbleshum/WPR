using System.Globalization;
using System.Text;

namespace WPR.Wp8Native
{
    /// <summary>
    /// The C maths library, <c>rand</c>, and <c>sscanf</c> - all of which used to fall through to
    /// the default stub and return zero.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured before this existed, in one run to the main menu: <c>sinf</c> and <c>cosf</c>
    /// 183,000 calls each, <c>pow</c> 156,000, <c>floorf</c> 52,000, <c>ceilf</c> 35,000 - every one
    /// answered 0. The visible casualty was text: a popup's message is laid out with
    /// floorf/ceilf, came out zero lines tall, and the dialog drew its box and no glyphs. Lua's
    /// <c>^</c> operator is <c>pow</c>, so script arithmetic was wrong too, and every rotation the
    /// engine built from sinf/cosf was the zero matrix's idea of a rotation.
    /// </para>
    /// <para>
    /// <b>Hard-float.</b> Windows on ARM uses AAPCS-VFP: float and double arguments and results
    /// travel in s0-s15 / d0-d7, not r0-r3, which is why these read through
    /// <see cref="CallFrame.FloatArg"/> and <see cref="CallFrame.DoubleArg"/>. Variadic functions
    /// (<c>sscanf</c>) are the exception - the base standard applies to them - and take core
    /// registers as before.
    /// </para>
    /// </remarks>
    public sealed class MathLibrary
    {
        private readonly ArmEmulator _emulator;
        private readonly CallFrame _frame;

        // MSVC's rand: a 32-bit LCG, 15 bits out. Seeded with 1 as the CRT is.
        private uint _randState = 1;

        public MathLibrary(ArmEmulator emulator, CallFrame frame)
        {
            _emulator = emulator;
            _frame = frame;
        }

        public void RegisterInto(Dictionary<string, Action> handlers)
        {
            Unary(handlers, "sin", Math.Sin, MathF.Sin);
            Unary(handlers, "cos", Math.Cos, MathF.Cos);
            Unary(handlers, "tan", Math.Tan, MathF.Tan);
            Unary(handlers, "asin", Math.Asin, MathF.Asin);
            Unary(handlers, "acos", Math.Acos, MathF.Acos);
            Unary(handlers, "atan", Math.Atan, MathF.Atan);
            Unary(handlers, "sinh", Math.Sinh, MathF.Sinh);
            Unary(handlers, "cosh", Math.Cosh, MathF.Cosh);
            Unary(handlers, "tanh", Math.Tanh, MathF.Tanh);
            Unary(handlers, "exp", Math.Exp, MathF.Exp);
            Unary(handlers, "log", Math.Log, MathF.Log);
            Unary(handlers, "log10", Math.Log10, MathF.Log10);
            Unary(handlers, "sqrt", Math.Sqrt, MathF.Sqrt);
            Unary(handlers, "floor", Math.Floor, MathF.Floor);
            Unary(handlers, "ceil", Math.Ceiling, MathF.Ceiling);
            Unary(handlers, "fabs", Math.Abs, MathF.Abs);

            Binary(handlers, "pow", Math.Pow, MathF.Pow);
            Binary(handlers, "atan2", Math.Atan2, MathF.Atan2);
            // C fmod truncates towards zero, which is exactly what C#'s % does on floating point.
            Binary(handlers, "fmod", (x, y) => x % y, (x, y) => x % y);

            // double ldexp(double, int) / double frexp(double, int*): the int travels in r0.
            handlers["ldexp"] = () => _frame.ReturnDouble(Math.ScaleB(_frame.DoubleArg(0), _frame.SignedArg(0)));
            handlers["frexp"] = () =>
            {
                double value = _frame.DoubleArg(0);
                int exponent = value == 0 || double.IsNaN(value) || double.IsInfinity(value)
                    ? 0
                    : Math.ILogB(value) + 1;
                if (_frame.Arg(0) != 0) _emulator.WriteUInt32(_frame.Arg(0), unchecked((uint)exponent));
                _frame.ReturnDouble(value == 0 ? value : Math.ScaleB(value, -exponent));
            };

            // double modf(double, double*)
            handlers["modf"] = () =>
            {
                double value = _frame.DoubleArg(0);
                double whole = Math.Truncate(value);
                if (_frame.Arg(0) != 0)
                {
                    _emulator.WriteMemory(_frame.Arg(0), BitConverter.GetBytes(whole));
                }

                _frame.ReturnDouble(value - whole);
            };

            // float modff(float, float*): s0 in, the pointer in r0 (hard-float leaves the core
            // registers to the non-float arguments). Missed the first time round while modf was
            // done: Angry Birds Classic calls it ~17,000 times a run to split its parallax scroll
            // into a tile index and an offset, and answered 0 with the whole part never written,
            // its backdrop stopped two-thirds of the way across the screen.
            handlers["modff"] = () =>
            {
                float value = _frame.FloatArg(0);
                float whole = MathF.Truncate(value);
                if (_frame.Arg(0) != 0)
                {
                    _emulator.WriteMemory(_frame.Arg(0), BitConverter.GetBytes(whole));
                }

                _frame.ReturnFloat(value - whole);
            };

            handlers["rand"] = () =>
            {
                _randState = unchecked((_randState * 214013u) + 2531011u);
                _frame.Return((_randState >> 16) & 0x7FFF);
            };
            handlers["srand"] = () => _randState = (uint)_frame.Arg(0);

            handlers["sscanf"] = () => _frame.Return(Scan(_frame.ReadNarrowString(_frame.Arg(0)), _frame.ReadNarrowString(_frame.Arg(1)), 2));
            handlers["sscanf_s"] = () => _frame.Return(Scan(_frame.ReadNarrowString(_frame.Arg(0)), _frame.ReadNarrowString(_frame.Arg(1)), 2, secure: true));
        }

        private void Unary(Dictionary<string, Action> handlers, string name, Func<double, double> d, Func<float, float> f)
        {
            handlers[name] = () => _frame.ReturnDouble(d(_frame.DoubleArg(0)));
            handlers[name + "f"] = () =>
            {
                float x = _frame.FloatArg(0);
                if (TraceLeft > 0)
                {
                    TraceLeft--;
                    Trace.Add($"{name}f: s0={x} (r0=0x{_frame.Arg(0):X8}) -> {f(x)}");
                }

                _frame.ReturnFloat(f(x));
            };
        }

        /// <summary>
        /// The first few float calls with both s0 and r0, so the hard-float assumption is a
        /// measurement: plausible values in s0 and junk in r0 is AAPCS-VFP.
        /// </summary>
        public List<string> Trace { get; } = new();

        private int TraceLeft = 6;

        private void Binary(Dictionary<string, Action> handlers, string name, Func<double, double, double> d, Func<float, float, float> f)
        {
            handlers[name] = () => _frame.ReturnDouble(d(_frame.DoubleArg(0), _frame.DoubleArg(1)));
            handlers[name + "f"] = () => _frame.ReturnFloat(f(_frame.FloatArg(0), _frame.FloatArg(1)));
        }

        /// <summary>
        /// sscanf over the conversions a game's config parsing uses: %d %i %u %x %X %o %f %e %g
        /// (with l for double), %s, %c, %[set], %n, %%, field widths and * suppression. Returns
        /// the number of fields assigned, or -1 (EOF) when input ran out before the first.
        /// </summary>
        private int Scan(string input, string format, int firstVariadic, bool secure = false)
        {
            var args = new VarArgReader(_emulator, firstVariadic);
            int at = 0;
            int assigned = 0;
            bool anyConversion = false;

            for (int f = 0; f < format.Length; f++)
            {
                char fc = format[f];
                if (char.IsWhiteSpace(fc))
                {
                    while (at < input.Length && char.IsWhiteSpace(input[at])) at++;
                    continue;
                }

                if (fc != '%')
                {
                    if (at >= input.Length || input[at] != fc) return Done();
                    at++;
                    continue;
                }

                f++;
                if (f >= format.Length) break;
                if (format[f] == '%')
                {
                    while (at < input.Length && char.IsWhiteSpace(input[at])) at++;
                    if (at >= input.Length || input[at] != '%') return Done();
                    at++;
                    continue;
                }

                bool suppress = false;
                if (format[f] == '*') { suppress = true; f++; }

                int width = 0;
                while (f < format.Length && char.IsAsciiDigit(format[f])) width = (width * 10) + (format[f++] - '0');
                if (width == 0) width = int.MaxValue;

                string size = "";
                while (f < format.Length && format[f] is 'h' or 'l' or 'L' or 'I' or 'q' or 'j' or 'z' or 't') size += format[f++];
                if (f >= format.Length) break;
                char conv = format[f];

                if (conv == 'n')
                {
                    if (!suppress) _emulator.WriteUInt32(args.NextUInt32(), (uint)at);
                    continue;
                }

                if (conv is not ('c' or '['))
                {
                    while (at < input.Length && char.IsWhiteSpace(input[at])) at++;
                }

                if (at >= input.Length) return Done();
                anyConversion = true;

                switch (conv)
                {
                    case 'd' or 'i' or 'u' or 'x' or 'X' or 'o':
                    {
                        int radix = conv switch { 'x' or 'X' => 16, 'o' => 8, 'i' => 0, _ => 10 };
                        int start = at;
                        bool negative = false;
                        if (at < input.Length && width > 0 && input[at] is '+' or '-')
                        {
                            negative = input[at] == '-';
                            at++;
                        }

                        if (radix is 0 or 16 && at + 1 < input.Length && input[at] == '0' && input[at + 1] is 'x' or 'X')
                        {
                            radix = 16;
                            at += 2;
                        }
                        else if (radix == 0)
                        {
                            radix = at < input.Length && input[at] == '0' ? 8 : 10;
                        }

                        ulong value = 0;
                        int digits = 0;
                        while (at < input.Length && at - start < width && Digit(input[at], radix) is var dg and >= 0)
                        {
                            value = unchecked((value * (ulong)radix) + (ulong)dg);
                            at++;
                            digits++;
                        }

                        if (digits == 0) return Done();
                        if (negative) value = unchecked((ulong)-(long)value);
                        if (!suppress)
                        {
                            long target = args.NextUInt32();
                            if (size is "ll" or "I64" or "q" or "j") _emulator.WriteMemory(target, BitConverter.GetBytes(value));
                            else if (size == "hh") _emulator.WriteMemory(target, [(byte)value]);
                            else if (size == "h") _emulator.WriteMemory(target, BitConverter.GetBytes((ushort)value));
                            else _emulator.WriteUInt32(target, unchecked((uint)value));
                            assigned++;
                        }

                        break;
                    }

                    case 'f' or 'e' or 'E' or 'g' or 'G' or 'a':
                    {
                        int start = at;
                        while (at < input.Length && at - start < width &&
                               (char.IsAsciiDigit(input[at]) || input[at] == '.' ||
                                (at == start && input[at] is '+' or '-') ||
                                (at > start && input[at] is 'e' or 'E') ||
                                (at > start && input[at - 1] is 'e' or 'E' && input[at] is '+' or '-')))
                        {
                            at++;
                        }

                        if (!double.TryParse(input[start..at], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                        {
                            return Done();
                        }

                        if (!suppress)
                        {
                            long target = args.NextUInt32();
                            if (size is "l" or "L") _emulator.WriteMemory(target, BitConverter.GetBytes(value));
                            else _emulator.WriteMemory(target, BitConverter.GetBytes((float)value));
                            assigned++;
                        }

                        break;
                    }

                    case 's':
                    {
                        int start = at;
                        while (at < input.Length && at - start < width && !char.IsWhiteSpace(input[at])) at++;
                        if (!suppress)
                        {
                            long target = args.NextUInt32();
                            if (secure) args.NextUInt32(); // buffer size
                            _emulator.WriteMemory(target, [.. Encoding.Latin1.GetBytes(input[start..at]), 0]);
                            assigned++;
                        }

                        break;
                    }

                    case 'c':
                    {
                        int count = width == int.MaxValue ? 1 : width;
                        if (at + count > input.Length) return Done();
                        if (!suppress)
                        {
                            long target = args.NextUInt32();
                            if (secure) args.NextUInt32();
                            _emulator.WriteMemory(target, Encoding.Latin1.GetBytes(input.Substring(at, count)));
                            assigned++;
                        }

                        at += count;
                        break;
                    }

                    case '[':
                    {
                        int close = format.IndexOf(']', f + 2);
                        if (close < 0) return Done();
                        string set = format[(f + 1)..close];
                        bool invert = set.StartsWith('^');
                        if (invert) set = set[1..];
                        f = close;

                        int start = at;
                        while (at < input.Length && at - start < width && (set.Contains(input[at]) != invert)) at++;
                        if (at == start) return Done();
                        if (!suppress)
                        {
                            long target = args.NextUInt32();
                            if (secure) args.NextUInt32();
                            _emulator.WriteMemory(target, [.. Encoding.Latin1.GetBytes(input[start..at]), 0]);
                            assigned++;
                        }

                        break;
                    }

                    default:
                        return Done();
                }
            }

            return assigned;

            int Done() => assigned == 0 && !anyConversion && at >= input.Length ? -1 : assigned;
        }

        private static int Digit(char c, int radix)
        {
            int v = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'z' => c - 'a' + 10,
                >= 'A' and <= 'Z' => c - 'A' + 10,
                _ => 99,
            };
            return v < radix ? v : -1;
        }
    }
}

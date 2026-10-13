using System.Globalization;
using System.Text;

namespace WPR.Wp8Native
{
    public sealed partial class CrtStrings
    {
        // ------------------------------------------------------------------ multibyte <-> wide (C locale)

        private void RegisterMultibyte(Dictionary<string, Action> handlers)
        {
            // int mbtowc(wchar_t* pwc, const char* s, size_t n): one byte is one character.
            // NULL s asks about shift states (there are none: 0); n == 0 converts nothing (0).
            handlers["mbtowc"] = () =>
            {
                long target = A(0), source = A(1), count = A(2);
                if (source == 0 || count == 0)
                {
                    _frame.Return(0);
                    return;
                }

                byte value = _emulator.ReadMemory(source, 1)[0];
                WriteChar(target, value, wide: true);
                _frame.Return(value == 0 ? 0 : 1);
            };
            handlers["_mbtowc_l"] = handlers["mbtowc"];

            // int mblen(const char* s, size_t n): 0 for NULL, an empty string or n == 0, else 1.
            handlers["mblen"] = () =>
            {
                long source = A(0), count = A(1);
                _frame.Return(source == 0 || count == 0 || _emulator.ReadMemory(source, 1)[0] == 0 ? 0 : 1);
            };
            handlers["_mblen_l"] = handlers["mblen"];

            // int wctomb(char* s, wchar_t wc): one byte, or -1 / EILSEQ above 255.
            handlers["wctomb"] = () =>
            {
                long target = A(0);
                int value = (int)(A(1) & 0xFFFF);
                if (target == 0)
                {
                    _frame.Return(0);
                    return;
                }

                if (value > 255)
                {
                    SetErrno(Eilseq);
                    ReturnInt(-1);
                    return;
                }

                WriteChar(target, value, wide: false);
                _frame.Return(1);
            };
            handlers["_wctomb_l"] = handlers["wctomb"];

            // errno_t wctomb_s(int* ret, char* dst, size_t size, wchar_t wc).
            handlers["wctomb_s"] = () =>
            {
                long result = A(0), target = A(1), size = A(2);
                int value = (int)(A(3) & 0xFFFF);
                if (target == 0 && size > 0)
                {
                    WriteInt(result, 0);   // no state-dependent encodings
                    ReturnInt(0);
                    return;
                }

                WriteInt(result, -1);
                if (value > 255)
                {
                    if (target != 0 && size > 0)
                    {
                        _emulator.WriteMemory(target, new byte[Math.Min(size, 16)]);
                    }

                    Fail(Eilseq);
                    return;
                }

                if (target != 0)
                {
                    if (size == 0)
                    {
                        Fail(Erange);
                        return;
                    }

                    WriteChar(target, value, wide: false);
                }

                WriteInt(result, 1);
                ReturnInt(0);
            };
            handlers["_wctomb_s_l"] = handlers["wctomb_s"];

            // wint_t btowc(int c) / int wctob(wint_t c).
            handlers["btowc"] = () => _frame.Return(S(0) == -1 ? Weof : (A(0) & 0xFF));
            handlers["wctob"] = () =>
            {
                int value = (int)(A(0) & 0xFFFF);
                ReturnInt(value <= 255 ? value : -1);
            };

            // size_t mbrtowc(wchar_t* pwc, const char* s, size_t n, mbstate_t*): as mbtowc, with
            // (size_t)-2 for "incomplete" when n is 0. mbrlen is the same without the store.
            handlers["mbrtowc"] = () => RestartableToWide(A(0), A(1), A(2));
            handlers["mbrlen"] = () => RestartableToWide(0, A(0), A(1));

            // size_t wcrtomb(char* s, wchar_t wc, mbstate_t*).
            handlers["wcrtomb"] = () =>
            {
                long target = A(0);
                int value = (int)(A(1) & 0xFFFF);
                if (target == 0)
                {
                    _frame.Return(1);
                    return;
                }

                if (value > 255)
                {
                    SetErrno(Eilseq);
                    _frame.Return(0xFFFFFFFF);
                    return;
                }

                WriteChar(target, value, wide: false);
                _frame.Return(1);
            };

            // int mbsinit(const mbstate_t*): the C locale never leaves the initial state.
            handlers["mbsinit"] = () => _frame.Return(1);

            // size_t wcstombs(char* dst, const wchar_t* src, size_t n): -1 / EILSEQ for a character
            // above 255. Converting stops at n bytes; the terminator is stored when it fits.
            handlers["wcstombs"] = () =>
            {
                long target = A(0), source = A(1), count = A(2);
                (long converted, bool failed) = WideToNarrow(target, source, count);
                if (failed)
                {
                    SetErrno(Eilseq);
                    _frame.Return(0xFFFFFFFF);
                    return;
                }

                _frame.Return(converted);
            };
            handlers["_wcstombs_l"] = handlers["wcstombs"];

            // errno_t mbstowcs_s(size_t* converted, wchar_t* dst, size_t sizeInWords, const char* src, size_t count).
            handlers["mbstowcs_s"] = () => ConvertSecure(toWide: true);
            handlers["_mbstowcs_s_l"] = handlers["mbstowcs_s"];

            // errno_t wcstombs_s(size_t* converted, char* dst, size_t sizeInBytes, const wchar_t* src, size_t count).
            handlers["wcstombs_s"] = () => ConvertSecure(toWide: false);
            handlers["_wcstombs_s_l"] = handlers["wcstombs_s"];
        }

        private void WriteInt(long address, int value)
        {
            if (address != 0)
            {
                _emulator.WriteUInt32(address, unchecked((uint)value));
            }
        }

        private void RestartableToWide(long target, long source, long count)
        {
            if (source == 0)
            {
                _frame.Return(0);
                return;
            }

            if (count == 0)
            {
                _frame.Return(0xFFFFFFFE);   // (size_t)-2: incomplete
                return;
            }

            byte value = _emulator.ReadMemory(source, 1)[0];
            WriteChar(target, value, wide: true);
            _frame.Return(value == 0 ? 0 : 1);
        }

        /// <summary>
        /// The C locale's <c>_wcstombs_l_helper</c>. With a destination: converts up to
        /// <paramref name="count"/> bytes, storing the terminator if it is reached within them,
        /// and answers the bytes before it. Without one: the whole length. A character above 255
        /// fails the conversion.
        /// </summary>
        private (long Converted, bool Failed) WideToNarrow(long target, long source, long count)
        {
            if (target == 0)
            {
                string all = Wide(source);
                return (all.Length, all.Any(c => c > 255));
            }

            string text = Wide(source, count);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] > 255)
                {
                    WriteText(target, text[..i], wide: false, terminate: false);
                    return (0, true);
                }
            }

            WriteText(target, text, wide: false, terminate: text.Length < count);
            return (text.Length, false);
        }

        /// <summary>
        /// mbstowcs_s / wcstombs_s (VC11 _mbstowcs_s_l / _wcstombs_s_l). The converted count
        /// includes the terminator. A destination too small fails with ERANGE and is emptied,
        /// unless count is _TRUNCATE, which stores what fits and answers STRUNCATE.
        /// </summary>
        private void ConvertSecure(bool toWide)
        {
            long converted = A(0), target = A(1), size = A(2), source = A(3), count = A(4);
            bool targetWide = toWide;

            if (!((target == 0 && size == 0) || (target != 0 && size > 0)))
            {
                Fail(Einval);
                return;
            }

            WriteChar(target, 0, targetWide);
            WriteInt(converted, 0);
            if (source == 0)
            {
                Fail(Einval);
                return;
            }

            long limit = count > size ? size : count;
            long length;
            if (toWide)
            {
                // The C locale's _mbstowcs_l_helper: every byte becomes the character of that value.
                string text = target == 0 ? Narrow(source) : Narrow(source, limit);
                if (target != 0)
                {
                    WriteText(target, text, wide: true, terminate: false);
                }

                length = text.Length;
            }
            else
            {
                (length, bool failed) = WideToNarrow(target, source, limit);
                if (failed)
                {
                    WriteChar(target, 0, targetWide);
                    Fail(Eilseq);
                    return;
                }
            }

            length++;   // the terminator
            int result = 0;
            if (target != 0)
            {
                if (length > size)
                {
                    if (count != TruncateCount)
                    {
                        WriteChar(target, 0, targetWide);
                        Fail(Erange);
                        return;
                    }

                    length = size;
                    result = Struncate;
                }

                WriteChar(target + ((length - 1) * Unit(targetWide)), 0, targetWide);
            }

            WriteInt(converted, (int)length);
            ReturnInt(result);
        }

        // ------------------------------------------------------------------ text -> numbers

        private static bool IsSpace(char c, bool wide) => ((wide ? WideClass(c) : NarrowClass(c)) & Space) != 0;

        /// <summary>
        /// The value of a digit in any base up to 36, or -1. Wide functions also take every Unicode
        /// decimal digit (fullwidth, Arabic-Indic, ...), as the CRT's <c>_wchartodigit</c> does;
        /// letters are ASCII in both.
        /// </summary>
        private static int DigitValue(char c, bool wide)
        {
            if (c is >= '0' and <= '9') return c - '0';
            if (c is >= 'a' and <= 'z') return c - 'a' + 10;
            if (c is >= 'A' and <= 'Z') return c - 'A' + 10;
            if (wide && c >= 0x80 && char.IsDigit(c)) return (int)char.GetNumericValue(c);
            return -1;
        }

        /// <summary>
        /// VC11's <c>strtoxl</c> / <c>wcstoxl</c> / <c>strtoxq</c>: whitespace, a sign, an optional
        /// 0x for base 16 (base 0 takes the base from the prefix), then digits.
        /// </summary>
        /// <returns>
        /// The value's bits. <paramref name="consumed"/> is 0 when no digit was read - the end
        /// pointer then goes back to the start of the string, even past "0x".
        /// <paramref name="overflow"/> means the result was clamped: to the type's maximum (or
        /// minimum, for a negative signed value).
        /// </returns>
        private static ulong ParseInteger(string text, int radix, bool isUnsigned, int bits, bool wide, out int consumed, out bool overflow)
        {
            consumed = 0;
            overflow = false;
            int at = 0;
            while (at < text.Length && IsSpace(text[at], wide)) at++;
            bool negative = false;
            if (at < text.Length && text[at] is '+' or '-')
            {
                negative = text[at] == '-';
                at++;
            }

            bool startsWithZero = at < text.Length && DigitValue(text[at], wide) == 0;
            bool hexPrefix = startsWithZero && at + 1 < text.Length && text[at + 1] is 'x' or 'X';
            if (radix == 0)
            {
                radix = !startsWithZero ? 10 : hexPrefix ? 16 : 8;
            }

            if (radix == 16 && hexPrefix)
            {
                at += 2;
            }

            ulong typeMax = bits == 64 ? ulong.MaxValue : uint.MaxValue;
            ulong limit = isUnsigned ? typeMax : negative ? 1UL << (bits - 1) : (1UL << (bits - 1)) - 1;
            ulong value = 0;
            int digits = 0;
            while (at < text.Length)
            {
                int digit = DigitValue(text[at], wide);
                if (digit < 0 || digit >= radix)
                {
                    break;
                }

                if (!overflow)
                {
                    if (value > (limit - (ulong)digit) / (ulong)radix)
                    {
                        overflow = true;
                    }
                    else
                    {
                        value = (value * (ulong)radix) + (ulong)digit;
                    }
                }

                at++;
                digits++;
            }

            if (digits == 0)
            {
                return 0;
            }

            consumed = at;
            if (overflow)
            {
                return isUnsigned ? typeMax : negative ? 1UL << (bits - 1) : limit;
            }

            return negative ? unchecked((ulong)-(long)value) & typeMax : value;
        }

        /// <summary>
        /// VC11's strtod grammar: whitespace, a sign, digits with an optional point (at least one
        /// digit), then an exponent introduced by e, E, d or D - taken only if a digit follows.
        /// </summary>
        /// <param name="range">The result overflowed to infinity, or underflowed to zero.</param>
        private static double ParseDouble(string text, bool wide, out int consumed, out bool range)
        {
            consumed = 0;
            range = false;
            int at = 0;
            while (at < text.Length && IsSpace(text[at], wide)) at++;
            int start = at;
            if (at < text.Length && text[at] is '+' or '-') at++;

            int digits = 0;
            bool nonZero = false;
            while (at < text.Length && char.IsAsciiDigit(text[at]))
            {
                nonZero |= text[at] != '0';
                at++;
                digits++;
            }

            if (at < text.Length && text[at] == '.')
            {
                at++;
                while (at < text.Length && char.IsAsciiDigit(text[at]))
                {
                    nonZero |= text[at] != '0';
                    at++;
                    digits++;
                }
            }

            if (digits == 0)
            {
                return 0;
            }

            int mantissaEnd = at;
            if (at < text.Length && text[at] is 'e' or 'E' or 'd' or 'D')
            {
                int exponent = at + 1;
                if (exponent < text.Length && text[exponent] is '+' or '-') exponent++;
                if (exponent < text.Length && char.IsAsciiDigit(text[exponent]))
                {
                    while (exponent < text.Length && char.IsAsciiDigit(text[exponent])) exponent++;
                    at = exponent;
                }
            }

            consumed = at;
            string number = at > mantissaEnd
                ? text[start..mantissaEnd] + "e" + text[(mantissaEnd + 1)..at]
                : text[start..at];

            double value;
            try
            {
                value = double.Parse(number, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            catch (OverflowException)
            {
                value = number.StartsWith('-') ? double.NegativeInfinity : double.PositiveInfinity;
            }
            catch (FormatException)
            {
                // An exponent too long for the parser: its sign says which way it went.
                bool small = number.Contains("e-", StringComparison.Ordinal);
                value = small ? 0 : double.PositiveInfinity;
                if (number.StartsWith('-')) value = -value;
            }

            range = double.IsInfinity(value) || (value == 0 && nonZero);
            return value;
        }

        /// <summary>
        /// The text a number parser will look at: read in growing steps and stopped at the first
        /// character <paramref name="mayContinue"/> rejects, since no parse can go past one. The
        /// parsers see exactly what reading up to <paramref name="max"/> would have shown them up to
        /// that point, so their results are unchanged - but strtod(cursor) over a big buffer no
        /// longer reads 64 KB per call (Angry Birds Seasons spent most of its frames in here) and
        /// strtol no longer reads up to 64 MB.
        /// </summary>
        private string ReadNumberText(long source, bool wide, long max, Func<char, bool> mayContinue)
        {
            long length = Math.Min(64, max);
            while (true)
            {
                string text = ReadText(source, wide, length);
                if (text.Length < length || length >= max)
                {
                    return text;   // the terminator, the end of readable memory, or the cap
                }

                foreach (char c in text)
                {
                    if (!mayContinue(c))
                    {
                        return text;
                    }
                }

                length = Math.Min(length * 16, max);
            }
        }

        /// <summary>A strtol-family handler: (text[, end][, base]), result in r0 or r0:r1.</summary>
        private Action IntegerParser(bool wide, int bits, bool isUnsigned, bool hasEnd, bool hasBase) => () =>
        {
            long source = A(0);
            long end = hasEnd ? A(1) : 0;
            int radix = hasBase ? S(2) : 10;
            ulong result = 0;

            // The CRT stores the start as the end pointer before anything can fail.
            if (end != 0)
            {
                _emulator.WriteUInt32(end, (uint)source);
            }

            if (source == 0 || (radix != 0 && (radix < 2 || radix > 36)))
            {
                SetErrno(Einval);
            }
            else
            {
                string text = ReadNumberText(source, wide, CallFrame.SaneLengthLimit, c => IsSpace(c, wide) || c is '+' or '-' || DigitValue(c, wide) >= 0);
                result = ParseInteger(text, radix, isUnsigned, bits, wide, out int consumed, out bool overflow);
                if (overflow)
                {
                    SetErrno(Erange);
                }

                if (end != 0)
                {
                    _emulator.WriteUInt32(end, (uint)(source + (consumed * Unit(wide))));
                }
            }

            if (bits == 64)
            {
                _frame.Return64(unchecked((long)result));
            }
            else
            {
                _frame.Return((uint)result);
            }
        };

        /// <summary>A strtod-family handler: (text[, end]), result in d0.</summary>
        private Action DoubleParser(bool wide, bool hasEnd) => () =>
        {
            long source = A(0);
            long end = hasEnd ? A(1) : 0;
            if (source == 0)
            {
                if (end != 0) _emulator.WriteUInt32(end, 0);
                SetErrno(Einval);
                _frame.ReturnDouble(0);
                return;
            }

            string text = ReadNumberText(source, wide, 64 * 1024,
                c => IsSpace(c, wide) || char.IsAsciiDigit(c) || c is '+' or '-' or '.' or 'e' or 'E' or 'd' or 'D');
            double value = ParseDouble(text, wide, out int consumed, out bool range);
            if (range)
            {
                SetErrno(Erange);
            }

            if (end != 0)
            {
                _emulator.WriteUInt32(end, (uint)(source + (consumed * Unit(wide))));
            }

            _frame.ReturnDouble(value);
        };

        // ------------------------------------------------------------------ numbers -> text

        private static string ToRadix(ulong value, int radix)
        {
            if (value == 0)
            {
                return "0";
            }

            StringBuilder digits = new();
            while (value != 0)
            {
                int digit = (int)(value % (ulong)radix);
                digits.Insert(0, (char)(digit < 10 ? '0' + digit : 'a' + digit - 10));
                value /= (ulong)radix;
            }

            return digits.ToString();
        }

        /// <summary>
        /// The text the _itoa family writes: a minus sign only for a negative value of a signed
        /// type in base 10; in every other base the bits are written as unsigned.
        /// </summary>
        private static string IntegerText(ulong bits, int width, bool signed, int radix)
        {
            ulong mask = width == 64 ? ulong.MaxValue : uint.MaxValue;
            bits &= mask;
            bool negative = signed && radix == 10 && (bits >> (width - 1)) != 0;
            if (negative)
            {
                return "-" + ToRadix(unchecked((0 - bits) & mask), radix);
            }

            return ToRadix(bits, radix);
        }

        /// <summary>
        /// The _itoa / _itow family. 64-bit values arrive in r0:r1, which pushes the buffer,
        /// size and radix along: (value, buffer, radix) or (value, buffer, size, radix).
        /// </summary>
        private Action IntegerToText(bool wide, int width, bool signed, bool secure) => () =>
        {
            int first = width == 64 ? 2 : 1;
            ulong bits = width == 64 ? (ulong)(A(0) | (A(1) << 32)) : (ulong)A(0);
            long buffer = A(first);
            long size = secure ? A(first + 1) : CallFrame.SaneLengthLimit;
            int radix = S(secure ? first + 2 : first + 1);

            if (!secure)
            {
                if (buffer == 0 || radix < 2 || radix > 36)
                {
                    WriteChar(buffer, 0, wide);
                    SetErrno(Einval);
                }
                else
                {
                    WriteText(buffer, IntegerText(bits, width, signed, radix), wide);
                }

                _frame.Return(buffer);
                return;
            }

            if (buffer == 0 || size == 0)
            {
                Fail(Einval);
                return;
            }

            WriteChar(buffer, 0, wide);
            bool negative = signed && radix == 10 && ((bits >> (width - 1)) & 1) != 0;
            if (size <= (negative ? 2 : 1))
            {
                Fail(Erange);
                return;
            }

            if (radix < 2 || radix > 36)
            {
                Fail(Einval);
                return;
            }

            string text = IntegerText(bits, width, signed, radix);
            if (text.Length >= size)
            {
                Fail(Erange);
                return;
            }

            WriteText(buffer, text, wide);
            ReturnInt(0);
        };

        private void RegisterNumbers(Dictionary<string, Action> handlers)
        {
            foreach (bool wide in new[] { false, true })
            {
                string strto = wide ? "wcsto" : "strto";
                string tox = wide ? "_wto" : "_ato";

                // long strtol(s, end, base) / unsigned long strtoul / __int64 _strtoi64 / _strtoui64.
                handlers[strto + "l"] = IntegerParser(wide, 32, isUnsigned: false, hasEnd: true, hasBase: true);
                handlers[strto + "ul"] = IntegerParser(wide, 32, isUnsigned: true, hasEnd: true, hasBase: true);
                handlers["_" + strto + "i64"] = IntegerParser(wide, 64, isUnsigned: false, hasEnd: true, hasBase: true);
                handlers["_" + strto + "ui64"] = IntegerParser(wide, 64, isUnsigned: true, hasEnd: true, hasBase: true);
                foreach (string suffix in new[] { "l", "ul", "i64", "ui64" })
                {
                    string plain = suffix.StartsWith('i') || suffix.StartsWith("ui", StringComparison.Ordinal) ? "_" + strto + suffix : strto + suffix;
                    handlers["_" + strto + suffix + "_l"] = handlers[plain];
                }

                // atoi / atol are strtol(s, NULL, 10): clamped, with ERANGE. _atoi64 is _strtoi64.
                Action toInt = IntegerParser(wide, 32, isUnsigned: false, hasEnd: false, hasBase: false);
                Action toInt64 = IntegerParser(wide, 64, isUnsigned: false, hasEnd: false, hasBase: false);
                handlers[wide ? "_wtoi" : "atoi"] = toInt;
                handlers[wide ? "_wtol" : "atol"] = toInt;
                handlers[tox + "i64"] = toInt64;
                handlers[tox + "i_l"] = toInt;
                handlers[tox + "l_l"] = toInt;
                handlers[tox + "i64_l"] = toInt64;

                // double strtod(s, end) / wcstod; atof / _wtof.
                handlers[strto + "d"] = DoubleParser(wide, hasEnd: true);
                handlers["_" + strto + "d_l"] = handlers[strto + "d"];
                handlers[wide ? "_wtof" : "atof"] = DoubleParser(wide, hasEnd: false);
                handlers[tox + "f_l"] = handlers[wide ? "_wtof" : "atof"];

                // _itoa(value, buffer, radix) and _itoa_s(value, buffer, size, radix), and the rest.
                string to = wide ? "tow" : "toa";
                foreach (bool secure in new[] { false, true })
                {
                    string s = secure ? "_s" : string.Empty;
                    handlers["_i" + to + s] = IntegerToText(wide, 32, signed: true, secure);
                    handlers["_l" + to + s] = IntegerToText(wide, 32, signed: true, secure);
                    handlers["_ul" + to + s] = IntegerToText(wide, 32, signed: false, secure);
                    handlers["_i64" + to + s] = IntegerToText(wide, 64, signed: true, secure);
                    handlers["_ui64" + to + s] = IntegerToText(wide, 64, signed: false, secure);
                }
            }
        }

        // ------------------------------------------------------------------ _ecvt / _fcvt / _gcvt

        private readonly Dictionary<int, long> _cvtBuffers = new();

        private void RegisterFloatText(Dictionary<string, Action> handlers)
        {
            // char* _ecvt(double value, int count, int* dec, int* sign) and
            // char* _fcvt(double value, int count, int* dec, int* sign): the digits in a per-thread
            // buffer. Hard-float: the double is in d0 and the integers are r0-r2.
            handlers["_ecvt"] = () => Cvt(fixedPoint: false);
            handlers["_fcvt"] = () => Cvt(fixedPoint: true);

            // errno_t _ecvt_s(char* buffer, size_t size, double value, int count, int* dec, int* sign)
            // and _fcvt_s: buffer r0, size r1, value d0, count r2, dec r3, sign on the stack.
            handlers["_ecvt_s"] = () => CvtSecure(fixedPoint: false);
            handlers["_fcvt_s"] = () => CvtSecure(fixedPoint: true);

            // char* _gcvt(double value, int digits, char* buffer).
            handlers["_gcvt"] = () =>
            {
                long buffer = A(1);
                if (buffer == 0)
                {
                    SetErrno(Einval);
                    _frame.Return(0);
                    return;
                }

                WriteText(buffer, CrtFloatText.Gcvt(_frame.DoubleArg(0), Math.Max(S(0), 1)), wide: false);
                _frame.Return(buffer);
            };

            // errno_t _gcvt_s(char* buffer, size_t size, double value, int digits).
            handlers["_gcvt_s"] = () =>
            {
                long buffer = A(0), size = A(1);
                int digits = S(2);
                double value = _frame.DoubleArg(0);
                if (buffer == 0 || size == 0)
                {
                    Fail(Einval);
                    return;
                }

                WriteChar(buffer, 0, wide: false);
                if ((uint)digits >= size)
                {
                    Fail(Erange);
                    return;
                }

                string text = CrtFloatText.Gcvt(value, Math.Max(digits, 1));
                if (text.Length >= size)
                {
                    Fail(Erange);
                    return;
                }

                WriteText(buffer, text, wide: false);
                ReturnInt(0);
            };
        }

        private (string Digits, int Point, bool Negative) Digits(double value, int count, bool fixedPoint)
            => fixedPoint ? CrtFloatText.Fcvt(value, count) : CrtFloatText.Ecvt(value, count);

        private void Cvt(bool fixedPoint)
        {
            double value = _frame.DoubleArg(0);
            int count = S(0);
            long point = A(1), sign = A(2);
            (string digits, int decimalPoint, bool negative) = Digits(value, count, fixedPoint);
            long buffer = ThreadBuffer(_cvtBuffers, CrtFloatText.CvtBufferSize + 1);
            WriteText(buffer, digits, wide: false);
            WriteInt(point, decimalPoint);
            WriteInt(sign, negative ? 1 : 0);
            _frame.Return(buffer);
        }

        private void CvtSecure(bool fixedPoint)
        {
            long buffer = A(0), size = A(1), point = A(3), sign = A(4);
            int count = S(2);
            double value = _frame.DoubleArg(0);
            if (buffer == 0 || size == 0)
            {
                Fail(Einval);
                return;
            }

            WriteChar(buffer, 0, wide: false);
            if (point == 0 || sign == 0)
            {
                Fail(Einval);
                return;
            }

            (string digits, int decimalPoint, bool negative) = Digits(value, count, fixedPoint);
            if (digits.Length >= size)
            {
                Fail(Erange);
                return;
            }

            WriteText(buffer, digits, wide: false);
            WriteInt(point, decimalPoint);
            WriteInt(sign, negative ? 1 : 0);
            ReturnInt(0);
        }
    }
}

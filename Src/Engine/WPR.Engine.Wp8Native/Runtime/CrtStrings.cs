using System.Globalization;
using System.Text;

namespace WPR.Wp8Native
{
    /// <summary>
    /// MSVCR110's strings, characters, multibyte/wide conversion, number&lt;-&gt;text conversion
    /// and formatted text, to the documented contracts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The locale is "C"</b>, because that is what a WP8 title has: nothing in the library
    /// calls <c>setlocale</c> successfully, so every <c>_l</c> variant is answered as its plain
    /// form and the locale argument is ignored. In the C locale a narrow character is one byte and
    /// maps to the wide character of the same value (Latin-1), a wide character above 255 has no
    /// narrow form (<c>EILSEQ</c>), case mapping is ASCII only, and the narrow classification table
    /// is ASCII only. The wide classification functions are the exception the CRT itself makes:
    /// <c>iswalpha</c> and friends classify by Unicode in every locale.
    /// </para>
    /// <para>
    /// <b>Secure (<c>_s</c>) variants</b> follow the CRT's invalid-parameter contract as it reads
    /// when the handler lets execution continue: the documented errno value is stored and returned
    /// (or -1 returned, for the printf family), and the destination is emptied where the CRT
    /// empties it (<c>_RESET_STRING</c>). The CRT's default handler would end the process instead;
    /// a game must never be stopped here, and every title was written against the return values.
    /// </para>
    /// <para>
    /// Where the C standard and VC11 differ, VC11 wins, since that is what the games were tested
    /// against: <c>strtol("0x", ...)</c> leaves the end pointer at the start of the string, and
    /// <c>strtod</c> accepts <c>d</c>/<c>D</c> as an exponent marker but not <c>inf</c>, <c>nan</c>
    /// or hexadecimal (all three arrived with the VS2015 CRT).
    /// </para>
    /// <para>
    /// Some of these replace earlier handlers registered by <see cref="CrtLibrary"/>,
    /// <see cref="CrtExtras"/> and <see cref="MathLibrary"/>, by registering later under the same
    /// name: strtol/strtoul/strtod/atoi/atol/atof/_wtoi64 (errno and range), sprintf_s,
    /// vsprintf_s, _snprintf, _vsnprintf, _vsnprintf_s, _vscprintf (truncation and errno
    /// contracts), strcpy_s/strcat_s (errno, emptying), _stricmp/_strnicmp (ASCII-only folding),
    /// tolower/toupper (C locale, EOF), wcstombs (EILSEQ), strerror (the real messages) and
    /// sscanf/sscanf_s (the scanner below).
    /// </para>
    /// </remarks>
    public sealed partial class CrtStrings
    {
        private const int Einval = 22;
        private const int Erange = 34;
        private const int Eilseq = 42;
        private const int Enomem = 12;
        private const int Struncate = 80;

        /// <summary>_TRUNCATE, ((size_t)-1): "copy what fits" to the _s functions.</summary>
        private const long TruncateCount = 0xFFFFFFFF;

        /// <summary>_NLSCMPERROR: what the case-insensitive compares answer for a NULL argument.</summary>
        private const int NlsCompareError = int.MaxValue;

        private const int Weof = 0xFFFF;

        private readonly ArmEmulator _emulator;
        private readonly CallFrame _frame;
        private Dictionary<string, Action>? _handlers;

        public CrtStrings(ArmEmulator emulator, CallFrame frame)
        {
            _emulator = emulator;
            _frame = frame;
        }

        public void RegisterInto(Dictionary<string, Action> handlers)
        {
            _handlers = handlers;
            RegisterCharacters(handlers);
            RegisterNarrowStrings(handlers);
            RegisterWideStrings(handlers);
            RegisterMultibyte(handlers);
            RegisterNumbers(handlers);
            RegisterFloatText(handlers);
            RegisterFormatting(handlers);
            RegisterScanning(handlers);
            RegisterErrorText(handlers);
        }

        // ------------------------------------------------------------------ plumbing

        private long A(int index) => _frame.Arg(index);

        private int S(int index) => _frame.SignedArg(index);

        private void ReturnInt(int value) => _frame.Return(unchecked((uint)value));

        /// <summary>
        /// Stores errno. errno lives in guest memory and its address is whatever <c>_errno()</c>
        /// answers, so that handler is asked (r0 is preserved around it); a thread library that
        /// gives each thread its own errno is then honoured without this class knowing.
        /// </summary>
        private void SetErrno(int value)
        {
            if (_handlers is null || !_handlers.TryGetValue("_errno", out Action? errno))
            {
                return;
            }

            long saved = _frame.Arg(0);
            errno();
            long address = _frame.Arg(0);
            _frame.Return(saved);
            if (address != 0)
            {
                _emulator.WriteUInt32(address, (uint)value);
            }
        }

        /// <summary>Stores errno and returns it: the shape of every _s failure.</summary>
        private void Fail(int error)
        {
            SetErrno(error);
            ReturnInt(error);
        }

        private static int Unit(bool wide) => wide ? 2 : 1;

        /// <summary>
        /// Reads a NUL-terminated string of at most <paramref name="max"/> characters. Narrow text
        /// comes back one char per byte (Latin-1, the C locale's mapping), wide text one char per
        /// UTF-16 unit. Reads never cross into a page they do not need, and a string running into
        /// unmapped memory ends there rather than faulting the host.
        /// </summary>
        private string ReadText(long address, bool wide, long max = CallFrame.SaneLengthLimit)
        {
            if (address == 0 || max <= 0)
            {
                return string.Empty;
            }

            int unit = Unit(wide);
            StringBuilder text = new();
            long at = address;
            while (text.Length < max)
            {
                long room = 0x1000 - (at & 0xFFF);
                long units = Math.Min(Math.Min(room / unit, 256), max - text.Length);
                if (units <= 0)
                {
                    units = 1;   // an odd address one byte short of a page: read across it
                }

                byte[] block;
                try
                {
                    block = _emulator.ReadMemory(at, (int)(units * unit));
                }
                catch (CpuMemoryException)
                {
                    break;
                }

                for (int i = 0; i < units; i++)
                {
                    char c = wide ? (char)(block[i * 2] | (block[(i * 2) + 1] << 8)) : (char)block[i];
                    if (c == 0)
                    {
                        return text.ToString();
                    }

                    text.Append(c);
                }

                at += units * unit;
            }

            return text.ToString();
        }

        private string Narrow(long address, long max = CallFrame.SaneLengthLimit) => ReadText(address, false, max);

        private string Wide(long address, long max = CallFrame.SaneLengthLimit) => ReadText(address, true, max);

        /// <summary>Writes text (narrow: the low byte of each char) and, by default, its terminator.</summary>
        private void WriteText(long address, string text, bool wide, bool terminate = true)
        {
            if (address == 0)
            {
                return;
            }

            int count = text.Length + (terminate ? 1 : 0);
            byte[] bytes = new byte[count * Unit(wide)];
            for (int i = 0; i < text.Length; i++)
            {
                if (wide)
                {
                    bytes[i * 2] = (byte)text[i];
                    bytes[(i * 2) + 1] = (byte)(text[i] >> 8);
                }
                else
                {
                    bytes[i] = (byte)text[i];
                }
            }

            if (bytes.Length > 0)
            {
                _emulator.WriteMemory(address, bytes);
            }
        }

        /// <summary>Writes one character - _RESET_STRING is <c>WriteChar(dst, 0)</c>.</summary>
        private void WriteChar(long address, int value, bool wide)
        {
            if (address != 0)
            {
                _emulator.WriteMemory(address, wide ? [(byte)value, (byte)(value >> 8)] : [(byte)value]);
            }
        }

        /// <summary>A guest copy the image's own free() can release.</summary>
        private long Allocate(string text, bool wide)
        {
            long copy = _emulator.AllocateHeap((text.Length + 1) * Unit(wide));
            WriteText(copy, text, wide);
            return copy;
        }

        /// <summary>A buffer per guest thread, the way the CRT keeps strerror's and _ecvt's.</summary>
        private long ThreadBuffer(Dictionary<int, long> buffers, int size)
        {
            int thread = _emulator.CurrentThreadId;
            if (!buffers.TryGetValue(thread, out long buffer))
            {
                buffer = _emulator.AllocateHeap(size);
                buffers[thread] = buffer;
            }

            return buffer;
        }

        // ------------------------------------------------------------------ characters

        private const int Upper = 0x1, Lower = 0x2, Digit = 0x4, Space = 0x8, Punct = 0x10,
            Control = 0x20, Blank = 0x40, Hex = 0x80, Alpha = 0x100 | Upper | Lower;

        /// <summary>The C locale's ctype table (<c>_ctype + 1</c>): ASCII classes, nothing above 0x7F.</summary>
        private static readonly ushort[] CType = BuildCType();

        private static ushort[] BuildCType()
        {
            ushort[] table = new ushort[256];
            for (int c = 0; c < 128; c++)
            {
                int m = 0;
                if (c < 0x20 || c == 0x7F) m |= Control;
                if (c is >= 0x09 and <= 0x0D or 0x20) m |= Space;
                if (c == 0x20) m |= Blank;
                if (c is >= '0' and <= '9') m |= Digit | Hex;
                if (c is >= 'A' and <= 'Z') m |= Upper | 0x100;
                if (c is >= 'a' and <= 'z') m |= Lower | 0x100;
                if (c is >= 'A' and <= 'F' or >= 'a' and <= 'f') m |= Hex;
                if (c is > 0x20 and < 0x7F && (m & (Digit | 0x100)) == 0) m |= Punct;
                table[c] = (ushort)m;
            }

            return table;
        }

        /// <summary>The narrow class of an int argument: EOF and anything outside a byte is in no class.</summary>
        private static int NarrowClass(int c) => c is >= 0 and <= 255 ? CType[c] : 0;

        /// <summary>
        /// The wide class (<c>iswctype</c>). ASCII from the C table; above it, Unicode - the CRT
        /// answers from its <c>_wctype</c> table below 256 and from <c>GetStringTypeW</c> above, in
        /// every locale. WEOF is in no class.
        /// </summary>
        private static int WideClass(int c)
        {
            c &= 0xFFFF;
            if (c < 0x80)
            {
                return CType[c];
            }

            if (c == Weof)
            {
                return 0;
            }

            char ch = (char)c;
            int m = 0;
            if (char.IsUpper(ch)) m |= Upper;
            if (char.IsLower(ch)) m |= Lower;
            if (char.IsLetter(ch)) m |= 0x100;
            if (char.IsDigit(ch)) m |= Digit;
            if (char.IsWhiteSpace(ch)) m |= Space;
            if (char.IsPunctuation(ch) || char.IsSymbol(ch)) m |= Punct;
            if (char.IsControl(ch)) m |= Control;
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.SpaceSeparator) m |= Blank;
            if (c is >= 0xFF10 and <= 0xFF19 or >= 0xFF21 and <= 0xFF26 or >= 0xFF41 and <= 0xFF46) m |= Hex;
            return m;
        }

        private static int AsciiLower(int c) => c is >= 'A' and <= 'Z' ? c + 32 : c;

        private static int AsciiUpper(int c) => c is >= 'a' and <= 'z' ? c - 32 : c;

        private void RegisterCharacters(Dictionary<string, Action> handlers)
        {
            (string Name, int Mask)[] classes =
            [
                ("alpha", Alpha), ("upper", Upper), ("lower", Lower), ("digit", Digit), ("xdigit", Hex),
                ("space", Space), ("punct", Punct), ("alnum", Alpha | Digit), ("print", Blank | Punct | Alpha | Digit),
                ("graph", Punct | Alpha | Digit), ("cntrl", Control),
            ];

            foreach ((string name, int mask) in classes)
            {
                // int is<class>(int c) is CrtLibrary's; the _l forms (c, locale) are the same in "C".
                handlers[$"_is{name}_l"] = () => _frame.Return(NarrowClass(S(0)) & mask);

                // int isw<class>(wint_t c) and _isw<class>_l(c, locale).
                handlers[$"isw{name}"] = () => _frame.Return(WideClass((int)A(0)) & mask);
                handlers[$"_isw{name}_l"] = handlers[$"isw{name}"];
            }

            // int _isctype(int c, int mask) / _isctype_l(c, mask, locale).
            handlers["_isctype"] = () => _frame.Return(NarrowClass(S(0)) & (int)A(1));
            handlers["_isctype_l"] = handlers["_isctype"];

            // int iswctype(wint_t c, wctype_t mask), and its old name is_wctype.
            handlers["iswctype"] = () => _frame.Return(WideClass((int)A(0)) & (int)(A(1) & 0xFFFF));
            handlers["is_wctype"] = handlers["iswctype"];
            handlers["_iswctype_l"] = handlers["iswctype"];
            handlers["iswascii"] = () => _frame.Return((A(0) & 0xFFFF) < 0x80 ? 1 : 0);
            handlers["__isascii"] = () => _frame.Return((uint)A(0) < 0x80 ? 1 : 0);
            handlers["__toascii"] = () => _frame.Return(A(0) & 0x7F);

            // int tolower(int) / toupper(int): ASCII in the C locale, and EOF (or anything that is
            // not a byte) comes back unchanged. The earlier handlers masked the argument to a byte,
            // so tolower(EOF) answered 255, and folded Latin-1 letters the C locale does not.
            handlers["tolower"] = () => ReturnInt(AsciiLower(S(0)));
            handlers["toupper"] = () => ReturnInt(AsciiUpper(S(0)));
            handlers["_tolower_l"] = handlers["tolower"];
            handlers["_toupper_l"] = handlers["toupper"];

            // _tolower / _toupper are the unchecked macros' functions: they assume the argument
            // is a letter of the other case and convert unconditionally.
            handlers["_tolower"] = () => ReturnInt(S(0) - 'A' + 'a');
            handlers["_toupper"] = () => ReturnInt(S(0) - 'a' + 'A');

            // wint_t towlower(wint_t) / towupper(wint_t): the CRT maps only ASCII while the locale
            // is "C" (__ascii_towlower); WEOF and everything else come back unchanged.
            handlers["towlower"] = () => _frame.Return(AsciiLower((int)(A(0) & 0xFFFF)));
            handlers["towupper"] = () => _frame.Return(AsciiUpper((int)(A(0) & 0xFFFF)));
            handlers["_towlower_l"] = handlers["towlower"];
            handlers["_towupper_l"] = handlers["towupper"];
        }

        // ------------------------------------------------------------------ strings, both widths

        /// <summary>C-locale case folding for the _stricmp family: ASCII only.</summary>
        private static int Fold(char c) => AsciiLower(c);

        /// <summary>
        /// The difference of the first pair of characters that differ (after folding when asked),
        /// looking at no more than <paramref name="count"/> - the CRT returns that difference, not
        /// just its sign. Characters compare unsigned.
        /// </summary>
        private static int CompareText(string left, string right, long count, bool fold)
        {
            for (long i = 0; i < count; i++)
            {
                int a = i < left.Length ? left[(int)i] : 0;
                int b = i < right.Length ? right[(int)i] : 0;
                if (fold)
                {
                    a = Fold((char)a);
                    b = Fold((char)b);
                }

                if (a != b || a == 0)
                {
                    return a - b;
                }
            }

            return 0;
        }

        /// <summary>The compare family: (a, b) or (a, b, count), optionally case-insensitive.</summary>
        private void Compare(bool wide, bool fold, bool counted, bool validate)
        {
            long left = A(0), right = A(1);
            long count = counted ? A(2) : long.MaxValue;
            if (counted && count == 0)
            {
                _frame.Return(0);
                return;
            }

            if (validate && (left == 0 || right == 0))
            {
                SetErrno(Einval);
                ReturnInt(NlsCompareError);
                return;
            }

            long limit = Math.Min(count, CallFrame.SaneLengthLimit);
            ReturnInt(CompareText(ReadText(left, wide, limit), ReadText(right, wide, limit), limit, fold));
        }

        /// <summary>errno_t strcpy_s(dst, size, src) and wcscpy_s.</summary>
        private void CopySecure(bool wide)
        {
            long destination = A(0), size = A(1), source = A(2);
            if (destination == 0 || size == 0)
            {
                Fail(Einval);
                return;
            }

            if (source == 0)
            {
                WriteChar(destination, 0, wide);
                Fail(Einval);
                return;
            }

            string text = ReadText(source, wide, size);
            if (text.Length >= size)
            {
                WriteChar(destination, 0, wide);
                Fail(Erange);
                return;
            }

            WriteText(destination, text, wide);
            ReturnInt(0);
        }

        /// <summary>
        /// errno_t strcat_s(dst, size, src) and wcscat_s. A destination with no terminator inside
        /// its size is EINVAL, not ERANGE.
        /// </summary>
        private void ConcatenateSecure(bool wide)
        {
            long destination = A(0), size = A(1), source = A(2);
            if (destination == 0 || size == 0)
            {
                Fail(Einval);
                return;
            }

            if (source == 0)
            {
                WriteChar(destination, 0, wide);
                Fail(Einval);
                return;
            }

            int existing = ReadText(destination, wide, size).Length;
            if (existing >= size)
            {
                WriteChar(destination, 0, wide);
                Fail(Einval);
                return;
            }

            string text = ReadText(source, wide, size - existing);
            if (existing + text.Length >= size)
            {
                WriteChar(destination, 0, wide);
                Fail(Erange);
                return;
            }

            WriteText(destination + (existing * Unit(wide)), text, wide);
            ReturnInt(0);
        }

        /// <summary>
        /// errno_t strncpy_s(dst, size, src, count) and wcsncpy_s: at most count characters, always
        /// terminated. If they do not fit, _TRUNCATE copies what does and answers STRUNCATE; any
        /// other count empties the destination and answers ERANGE.
        /// </summary>
        private void CopyCountedSecure(bool wide)
        {
            long destination = A(0), size = A(1), source = A(2), count = A(3);
            if (count == 0 && destination == 0 && size == 0)
            {
                ReturnInt(0);
                return;
            }

            if (destination == 0 || size == 0)
            {
                Fail(Einval);
                return;
            }

            if (count == 0)
            {
                WriteChar(destination, 0, wide);
                ReturnInt(0);
                return;
            }

            if (source == 0)
            {
                WriteChar(destination, 0, wide);
                Fail(Einval);
                return;
            }

            string text = ReadText(source, wide, count == TruncateCount ? size : Math.Min(count, size));
            if (text.Length < size)
            {
                WriteText(destination, text, wide);
                ReturnInt(0);
            }
            else if (count == TruncateCount)
            {
                WriteText(destination, text[..(int)(size - 1)], wide);
                ReturnInt(Struncate);
            }
            else
            {
                WriteChar(destination, 0, wide);
                Fail(Erange);
            }
        }

        /// <summary>errno_t strncat_s(dst, size, src, count) and wcsncat_s; see <see cref="CopyCountedSecure"/>.</summary>
        private void ConcatenateCountedSecure(bool wide)
        {
            long destination = A(0), size = A(1), source = A(2), count = A(3);
            if (count == 0 && destination == 0 && size == 0)
            {
                ReturnInt(0);
                return;
            }

            if (destination == 0 || size == 0)
            {
                Fail(Einval);
                return;
            }

            if (count != 0 && source == 0)
            {
                WriteChar(destination, 0, wide);
                Fail(Einval);
                return;
            }

            int existing = ReadText(destination, wide, size).Length;
            if (existing >= size)
            {
                WriteChar(destination, 0, wide);
                Fail(Einval);
                return;
            }

            long room = size - existing;
            string text = count == 0 ? string.Empty : ReadText(source, wide, count == TruncateCount ? room : Math.Min(count, room));
            long at = destination + (existing * Unit(wide));
            if (existing + text.Length < size)
            {
                WriteText(at, text, wide);
                ReturnInt(0);
            }
            else if (count == TruncateCount)
            {
                WriteText(at, text[..(int)(size - 1 - existing)], wide);
                ReturnInt(Struncate);
            }
            else
            {
                WriteChar(destination, 0, wide);
                Fail(Erange);
            }
        }

        /// <summary>char* strncpy / wchar_t* wcsncpy(dst, src, count): pads with NULs, does not terminate a long source.</summary>
        private void CopyPadded(bool wide)
        {
            long destination = A(0), source = A(1), count = A(2);
            if (destination != 0 && count > 0 && count <= CallFrame.SaneLengthLimit)
            {
                string text = ReadText(source, wide, count);
                WriteText(destination, text.PadRight((int)count, '\0'), wide, terminate: false);
            }

            _frame.Return(destination);
        }

        /// <summary>errno_t _strlwr_s / _strupr_s / _wcslwr_s / _wcsupr_s(str, size): in place, ASCII in "C".</summary>
        private void ChangeCaseSecure(bool wide, bool upper)
        {
            long text = A(0), size = A(1);
            if (text == 0 || size == 0)
            {
                Fail(Einval);
                return;
            }

            string value = ReadText(text, wide, size);
            if (value.Length >= size)
            {
                WriteChar(text, 0, wide);
                Fail(Einval);
                return;
            }

            WriteText(text, ChangeCase(value, upper), wide);
            ReturnInt(0);
        }

        private static string ChangeCase(string text, bool upper)
        {
            char[] chars = text.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                chars[i] = (char)(upper ? AsciiUpper(chars[i]) : AsciiLower(chars[i]));
            }

            return new string(chars);
        }

        /// <summary>strchr-shaped search; returns a pointer into the haystack or NULL.</summary>
        private void Find(bool wide, Func<string, int> search)
        {
            long start = A(0);
            if (start == 0)
            {
                _frame.Return(0);
                return;
            }

            int at = search(ReadText(start, wide));
            _frame.Return(at < 0 ? 0 : start + (at * Unit(wide)));
        }

        /// <summary>strtok-style tokenising: the token and where the next call resumes.</summary>
        private (long Token, long Next) Tokenise(long start, long delimiters, bool wide)
        {
            if (start == 0)
            {
                return (0, 0);
            }

            string text = ReadText(start, wide);
            string set = ReadText(delimiters, wide);
            int unit = Unit(wide);
            int at = 0;
            while (at < text.Length && set.Contains(text[at]))
            {
                at++;
            }

            if (at >= text.Length)
            {
                return (0, start + (text.Length * unit));
            }

            int end = at;
            while (end < text.Length && !set.Contains(text[end]))
            {
                end++;
            }

            if (end < text.Length)
            {
                WriteChar(start + (end * unit), 0, wide);
                return (start + (at * unit), start + ((end + 1) * unit));
            }

            return (start + (at * unit), start + (end * unit));
        }

        private readonly Dictionary<int, long> _wcstokNext = new();

        private void RegisterNarrowStrings(Dictionary<string, Action> handlers)
        {
            // size_t strnlen(const char* s, size_t max).
            handlers["strnlen"] = () => _frame.Return(A(0) == 0 ? 0 : Narrow(A(0), A(1)).Length);

            // void* memchr(const void* s, int c, size_t n): the first byte equal to (unsigned char)c.
            handlers["memchr"] = () =>
            {
                long start = A(0), count = A(2);
                byte needle = (byte)A(1);
                for (long done = 0; start != 0 && done < count;)
                {
                    int chunk = (int)Math.Min(4096, count - done);
                    int at = Array.IndexOf(_emulator.ReadMemory(start + done, chunk), needle);
                    if (at >= 0)
                    {
                        _frame.Return(start + done + at);
                        return;
                    }

                    done += chunk;
                }

                _frame.Return(0);
            };

            // int _memicmp(const void*, const void*, size_t): ASCII folding; NULL with a count is EINVAL.
            handlers["_memicmp"] = () =>
            {
                long left = A(0), right = A(1), count = A(2);
                if (count == 0)
                {
                    _frame.Return(0);
                    return;
                }

                if (left == 0 || right == 0)
                {
                    SetErrno(Einval);
                    ReturnInt(NlsCompareError);
                    return;
                }

                byte[] a = _emulator.ReadMemory(left, (int)Math.Min(count, CallFrame.SaneLengthLimit));
                byte[] b = _emulator.ReadMemory(right, a.Length);
                for (int i = 0; i < a.Length; i++)
                {
                    int difference = AsciiLower(a[i]) - AsciiLower(b[i]);
                    if (difference != 0)
                    {
                        ReturnInt(difference);
                        return;
                    }
                }

                _frame.Return(0);
            };
            handlers["_memicmp_l"] = handlers["_memicmp"];

            // int _stricmp(a, b) / _strnicmp(a, b, n): ASCII-only folding to lower case in "C".
            handlers["_stricmp"] = () => Compare(wide: false, fold: true, counted: false, validate: true);
            handlers["_strnicmp"] = () => Compare(wide: false, fold: true, counted: true, validate: true);
            handlers["_stricmp_l"] = handlers["_stricmp"];
            handlers["_strnicmp_l"] = handlers["_strnicmp"];
            handlers["_stricoll"] = handlers["_stricmp"];
            handlers["_strnicoll"] = handlers["_strnicmp"];
            handlers["_strncoll"] = () => Compare(wide: false, fold: false, counted: true, validate: true);

            handlers["strcpy_s"] = () => CopySecure(wide: false);
            handlers["strcat_s"] = () => ConcatenateSecure(wide: false);
            handlers["strncpy_s"] = () => CopyCountedSecure(wide: false);
            handlers["strncat_s"] = () => ConcatenateCountedSecure(wide: false);

            handlers["_strlwr_s"] = () => ChangeCaseSecure(wide: false, upper: false);
            handlers["_strupr_s"] = () => ChangeCaseSecure(wide: false, upper: true);
            handlers["_strlwr_s_l"] = handlers["_strlwr_s"];
            handlers["_strupr_s_l"] = handlers["_strupr_s"];
        }

        private void RegisterWideStrings(Dictionary<string, Action> handlers)
        {
            // wcslen is HostStubs' own.
            handlers["wcsnlen"] = () => _frame.Return(A(0) == 0 ? 0 : Wide(A(0), A(1)).Length);

            // wcscmp returns the difference of the first differing pair, compared unsigned.
            handlers["wcscmp"] = () => Compare(wide: true, fold: false, counted: false, validate: false);
            handlers["wcsncmp"] = () => Compare(wide: true, fold: false, counted: true, validate: false);
            handlers["wcscoll"] = handlers["wcscmp"];        // collation in "C" is code-point order
            handlers["_wcscoll_l"] = handlers["wcscmp"];
            handlers["_wcsncoll"] = handlers["wcsncmp"];
            handlers["_wcsicmp"] = () => Compare(wide: true, fold: true, counted: false, validate: true);
            handlers["_wcsnicmp"] = () => Compare(wide: true, fold: true, counted: true, validate: true);
            handlers["_wcsicmp_l"] = handlers["_wcsicmp"];
            handlers["_wcsnicmp_l"] = handlers["_wcsnicmp"];
            handlers["_wcsicoll"] = handlers["_wcsicmp"];
            handlers["_wcsicoll_l"] = handlers["_wcsicmp"];
            handlers["_wcsnicoll"] = handlers["_wcsnicmp"];

            // wchar_t* wcscpy(dst, src) / wcscat(dst, src).
            handlers["wcscpy"] = () =>
            {
                WriteText(A(0), Wide(A(1)), wide: true);
                _frame.Return(A(0));
            };
            handlers["wcscat"] = () =>
            {
                long destination = A(0);
                WriteText(destination + (Wide(destination).Length * 2), Wide(A(1)), wide: true);
                _frame.Return(destination);
            };

            // wchar_t* wcsncpy(dst, src, n): pads with NULs. wcsncat(dst, src, n): at most n, always terminated.
            handlers["wcsncpy"] = () => CopyPadded(wide: true);
            handlers["wcsncat"] = () =>
            {
                long destination = A(0);
                long count = A(2);
                string text = count <= 0 ? string.Empty : Wide(A(1), count);
                WriteText(destination + (Wide(destination).Length * 2), text, wide: true);
                _frame.Return(destination);
            };

            handlers["wcscpy_s"] = () => CopySecure(wide: true);
            handlers["wcscat_s"] = () => ConcatenateSecure(wide: true);
            handlers["wcsncpy_s"] = () => CopyCountedSecure(wide: true);
            handlers["wcsncat_s"] = () => ConcatenateCountedSecure(wide: true);

            // Searching. Looking for NUL finds the terminator, which is a real position.
            handlers["wcschr"] = () =>
            {
                char needle = (char)(A(1) & 0xFFFF);
                Find(true, text => needle == 0 ? text.Length : text.IndexOf(needle));
            };
            handlers["wcsrchr"] = () =>
            {
                char needle = (char)(A(1) & 0xFFFF);
                Find(true, text => needle == 0 ? text.Length : text.LastIndexOf(needle));
            };
            handlers["wcsstr"] = () =>
            {
                string needle = Wide(A(1));
                Find(true, text => text.IndexOf(needle, StringComparison.Ordinal));
            };
            handlers["wcspbrk"] = () =>
            {
                char[] set = Wide(A(1)).ToCharArray();
                Find(true, text => set.Length == 0 ? -1 : text.IndexOfAny(set));
            };
            handlers["wcscspn"] = () =>
            {
                string text = Wide(A(0)), set = Wide(A(1));
                int at = 0;
                while (at < text.Length && !set.Contains(text[at])) at++;
                _frame.Return(at);
            };
            handlers["wcsspn"] = () =>
            {
                string text = Wide(A(0)), set = Wide(A(1));
                int at = 0;
                while (at < text.Length && set.Contains(text[at])) at++;
                _frame.Return(at);
            };

            // wchar_t* _wcsdup(const wchar_t*): a malloc'd copy, NULL for NULL.
            handlers["_wcsdup"] = () => _frame.Return(A(0) == 0 ? 0 : Allocate(Wide(A(0)), wide: true));

            // wchar_t* wcstok(wchar_t* s, const wchar_t* delim): VC11's two-argument form, which
            // keeps its position per thread, as strtok does.
            handlers["wcstok"] = () =>
            {
                int thread = _emulator.CurrentThreadId;
                long start = A(0) != 0 ? A(0) : _wcstokNext.GetValueOrDefault(thread);
                (long token, long next) = Tokenise(start, A(1), wide: true);
                _wcstokNext[thread] = next;
                _frame.Return(token);
            };

            // wchar_t* wcstok_s(wchar_t* s, const wchar_t* delim, wchar_t** context).
            handlers["wcstok_s"] = () =>
            {
                long context = A(2);
                if (A(1) == 0 || context == 0 || (A(0) == 0 && _emulator.ReadUInt32(context) == 0))
                {
                    SetErrno(Einval);
                    _frame.Return(0);
                    return;
                }

                long start = A(0) != 0 ? A(0) : _emulator.ReadUInt32(context);
                (long token, long next) = Tokenise(start, A(1), wide: true);
                _emulator.WriteUInt32(context, (uint)next);
                _frame.Return(token);
            };

            // wchar_t* _wcslwr / _wcsupr(wchar_t*): in place, ASCII only in "C".
            handlers["_wcslwr"] = () =>
            {
                WriteText(A(0), ChangeCase(Wide(A(0)), upper: false), wide: true);
                _frame.Return(A(0));
            };
            handlers["_wcsupr"] = () =>
            {
                WriteText(A(0), ChangeCase(Wide(A(0)), upper: true), wide: true);
                _frame.Return(A(0));
            };
            handlers["_wcslwr_s"] = () => ChangeCaseSecure(wide: true, upper: false);
            handlers["_wcsupr_s"] = () => ChangeCaseSecure(wide: true, upper: true);
            handlers["_wcslwr_s_l"] = handlers["_wcslwr_s"];
            handlers["_wcsupr_s_l"] = handlers["_wcsupr_s"];
        }
    }
}

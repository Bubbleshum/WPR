using System.Globalization;
using System.Text;

namespace WPR.Wp8Native
{
    /// <summary>
    /// The CRT imports a static sweep of all seven native Angry Birds titles found with no
    /// implementation (WPR_UNHANDLED=1 lists them): string and number conversion, va_list
    /// formatting, time, locale, std::exception, qsort, process exit, and the network calls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every one of these used to answer 0 from the default stub, and for most of them 0 is a
    /// confident wrong answer rather than an error: <c>_stricmp</c> said every pair of strings
    /// was equal, <c>getaddrinfo</c> said a lookup succeeded and returned no address,
    /// <c>localeconv</c> returned a null table. The same sweep is what found <c>modff</c>.
    /// </para>
    /// <para>
    /// <b>Networking is answered as "no connection"</b>, consistently: DNS fails with
    /// WSAHOST_NOT_FOUND and sockets cannot be created. These games are written for a phone that
    /// is offline, and every service they talk to is long gone, so that is both the honest answer
    /// and the one with a tested code path behind it.
    /// </para>
    /// </remarks>
    public sealed class CrtExtras
    {
        private readonly ArmEmulator _emulator;
        private readonly CallFrame _frame;
        private long _lconv;
        private long _unknownException;

        public CrtExtras(ArmEmulator emulator, CallFrame frame)
        {
            _emulator = emulator;
            _frame = frame;
        }

        /// <summary>What printf would have printed, for the report.</summary>
        public List<string> Printed { get; } = new();

        public void RegisterInto(Dictionary<string, Action> handlers)
        {
            // --- strings and numbers ---
            handlers["_stricmp"] = () => _frame.Return(Math.Sign(string.Compare(
                Read(0), Read(1), StringComparison.OrdinalIgnoreCase)));
            handlers["_strnicmp"] = () =>
            {
                int count = (int)_frame.Arg(2);
                _frame.Return(Math.Sign(string.Compare(
                    Truncate(Read(0), count), Truncate(Read(1), count), StringComparison.OrdinalIgnoreCase)));
            };
            handlers["strtol"] = () => ParseInteger(signed: true);
            handlers["atol"] = () => _frame.Return(unchecked((uint)(int)LeadingInteger(Read(0), 10, out _)));
            handlers["_wtoi64"] = () => _frame.Return64(LeadingInteger(_emulator.ReadUtf16String(_frame.Arg(0), 64), 10, out _));
            handlers["atof"] = () => _frame.ReturnDouble(LeadingDouble(Read(0), out _));

            // div_t div(int, int): an 8-byte struct, returned in r0:r1 under AAPCS.
            handlers["div"] = () =>
            {
                int numerator = _frame.SignedArg(0), denominator = _frame.SignedArg(1);
                int quotient = denominator == 0 ? 0 : numerator / denominator;
                int remainder = denominator == 0 ? 0 : numerator % denominator;
                _frame.Return64(((long)(uint)remainder << 32) | (uint)quotient);
            };

            // --- formatting from a va_list ---
            // int _vsnprintf_s(char* buffer, size_t size, size_t count, const char* format, va_list)
            handlers["_vsnprintf_s"] = () =>
            {
                long buffer = _frame.Arg(0);
                long size = _frame.Arg(1);
                long count = _frame.Arg(2);
                string text = Format(_frame.Arg(3), _frame.Arg(4));
                const long truncate = 0xFFFFFFFF; // _TRUNCATE
                long room = count == truncate ? size - 1 : Math.Min(count, size - 1);
                if (buffer == 0 || size <= 0)
                {
                    _frame.Return(unchecked((uint)-1));
                    return;
                }

                bool fits = text.Length <= room;
                _frame.WriteNarrowString(buffer, fits ? text : text[..(int)Math.Max(0, room)]);
                _frame.Return(fits ? (uint)text.Length : unchecked((uint)-1));
            };
            // int vsprintf_s(char* buffer, size_t size, const char* format, va_list): the whole
            // result or nothing - an overflow empties the buffer and answers -1.
            handlers["vsprintf_s"] = () =>
            {
                long buffer = _frame.Arg(0);
                long size = _frame.Arg(1);
                string text = Format(_frame.Arg(2), _frame.Arg(3));
                if (buffer == 0 || size <= 0 || text.Length >= size)
                {
                    if (buffer != 0 && size > 0)
                    {
                        _frame.WriteNarrowString(buffer, string.Empty);
                    }

                    _frame.Return(unchecked((uint)-1));
                    return;
                }

                _frame.WriteNarrowString(buffer, text);
                _frame.Return((uint)text.Length);
            };

            // int MultiByteToWideChar(CodePage, Flags, const char* source, int sourceBytes,
            // wchar_t* destination, int destinationChars). A length of -1 means "up to and
            // including the NUL"; a destination of zero size asks how much room is needed.
            handlers["MultiByteToWideChar"] = () =>
            {
                long source = _frame.Arg(2);
                int bytes = _frame.SignedArg(3);
                long destination = _frame.Arg(4);
                int room = _frame.SignedArg(5);
                byte[] raw = bytes < 0 ? [.. NarrowBytes(source), 0] : (source == 0 ? [] : _emulator.ReadMemory(source, bytes));
                System.Text.Encoding encoding = _frame.Arg(0) == 65001 ? System.Text.Encoding.UTF8 : System.Text.Encoding.Latin1;
                char[] chars = encoding.GetChars(raw);
                if (room == 0)
                {
                    _frame.Return((uint)chars.Length);
                    return;
                }

                if (chars.Length > room || destination == 0)
                {
                    _frame.Return(0); // ERROR_INSUFFICIENT_BUFFER
                    return;
                }

                _emulator.WriteMemory(destination, System.Text.Encoding.Unicode.GetBytes(chars));
                _frame.Return((uint)chars.Length);
            };

            // int WideCharToMultiByte(CodePage, Flags, const wchar_t* source, int sourceChars,
            // char* destination, int destinationBytes, default char*, bool* usedDefault).
            handlers["WideCharToMultiByte"] = () =>
            {
                long source = _frame.Arg(2);
                int count = _frame.SignedArg(3);
                long destination = _frame.Arg(4);
                int room = _frame.SignedArg(5);
                string text = count < 0 ? WideText(source) + "\0" : (source == 0 ? "" : System.Text.Encoding.Unicode.GetString(_emulator.ReadMemory(source, count * 2)));
                System.Text.Encoding encoding = _frame.Arg(0) == 65001 ? System.Text.Encoding.UTF8 : System.Text.Encoding.Latin1;
                byte[] bytes = encoding.GetBytes(text);
                if (room == 0)
                {
                    _frame.Return((uint)bytes.Length);
                    return;
                }

                if (bytes.Length > room || destination == 0)
                {
                    _frame.Return(0);
                    return;
                }

                _emulator.WriteMemory(destination, bytes);
                _frame.Return((uint)bytes.Length);
            };

            // int _vsnprintf(char* buffer, size_t count, const char* format, va_list): writes at
            // most count characters, NUL only if there is room, and answers -1 when it truncates.
            handlers["_vsnprintf"] = () =>
            {
                long buffer = _frame.Arg(0);
                int count = (int)_frame.Arg(1);
                string text = Format(_frame.Arg(2), _frame.Arg(3));
                if (buffer == 0 || count <= 0)
                {
                    _frame.Return(count == 0 ? (uint)text.Length : unchecked((uint)-1));
                    return;
                }

                byte[] bytes = System.Text.Encoding.Latin1.GetBytes(text);
                if (bytes.Length < count)
                {
                    _emulator.WriteMemory(buffer, [.. bytes, 0]);
                    _frame.Return((uint)bytes.Length);
                    return;
                }

                _emulator.WriteMemory(buffer, bytes[..count]);
                _frame.Return(bytes.Length == count ? (uint)count : unchecked((uint)-1));
            };

            // char* strtok(char* text, const char* delimiters): MSVC keeps the position per thread.
            // Unimplemented it answered NULL, so Modern Combat 4 parsed none of its config files.
            handlers["strtok"] = () =>
            {
                int thread = _emulator.CurrentThreadId;
                long start = _frame.Arg(0) != 0 ? _frame.Arg(0) : _strtokNext.GetValueOrDefault(thread);
                (long token, long next) = Tokenise(start, _frame.Arg(1));
                _strtokNext[thread] = next;
                _frame.Return(token);
            };

            // char* strtok_s(char* text, const char* delimiters, char** context).
            handlers["strtok_s"] = () =>
            {
                long context = _frame.Arg(2);
                long start = _frame.Arg(0) != 0 ? _frame.Arg(0) : (context == 0 ? 0 : _emulator.ReadUInt32(context));
                (long token, long next) = Tokenise(start, _frame.Arg(1));
                if (context != 0)
                {
                    _emulator.WriteUInt32(context, (uint)next);
                }

                _frame.Return(token);
            };

            // char* _strdup(const char*): a malloc'd copy, so the image's own free() releases it.
            handlers["_strdup"] = () =>
            {
                long source = _frame.Arg(0);
                if (source == 0)
                {
                    _frame.Return(0);
                    return;
                }

                byte[] bytes = NarrowBytes(source);
                long copy = _emulator.AllocateHeap(bytes.Length + 1);
                _emulator.WriteMemory(copy, [.. bytes, 0]);
                _frame.Return(copy);
            };
            handlers["strdup"] = handlers["_strdup"];

            // char* _strlwr / _strupr(char*): in place.
            handlers["_strlwr"] = () => ChangeCase(_frame.Arg(0), upper: false);
            handlers["_strupr"] = () => ChangeCase(_frame.Arg(0), upper: true);

            // size_t mbstowcs(wchar_t* destination, const char* source, size_t count).
            handlers["mbstowcs"] = () =>
            {
                string text = System.Text.Encoding.Latin1.GetString(NarrowBytes(_frame.Arg(1)));
                long destination = _frame.Arg(0);
                int count = (int)_frame.Arg(2);
                if (destination == 0)
                {
                    _frame.Return((uint)text.Length);
                    return;
                }

                string written = text.Length < count ? text + "\0" : text[..count];
                _emulator.WriteMemory(destination, System.Text.Encoding.Unicode.GetBytes(written));
                _frame.Return((uint)Math.Min(text.Length, count));
            };

            // size_t wcstombs(char* destination, const wchar_t* source, size_t count).
            handlers["wcstombs"] = () =>
            {
                byte[] bytes = System.Text.Encoding.Latin1.GetBytes(WideText(_frame.Arg(1)));
                long destination = _frame.Arg(0);
                int count = (int)_frame.Arg(2);
                if (destination == 0)
                {
                    _frame.Return((uint)bytes.Length);
                    return;
                }

                _emulator.WriteMemory(destination, bytes.Length < count ? [.. bytes, 0] : bytes[..count]);
                _frame.Return((uint)Math.Min(bytes.Length, count));
            };

            // int _isnan(double) / _finite(double): the double is in d0.
            handlers["_isnan"] = () => _frame.Return(double.IsNaN(_frame.DoubleArg(0)) ? 1 : 0);
            handlers["_finite"] = () => _frame.Return(double.IsFinite(_frame.DoubleArg(0)) ? 1 : 0);

            // BOOL GetUserPreferredUILanguages(DWORD flags, ULONG* count, PZZWSTR buffer, ULONG* size):
            // one language, en-US, as a double-NUL-terminated list.
            handlers["GetUserPreferredUILanguages"] = () =>
            {
                byte[] list = System.Text.Encoding.Unicode.GetBytes("en-US\0\0");
                if (_frame.Arg(1) != 0)
                {
                    _emulator.WriteUInt32(_frame.Arg(1), 1);
                }

                long buffer = _frame.Arg(2);
                long size = _frame.Arg(3);
                uint room = size == 0 ? 0 : _emulator.ReadUInt32(size);
                if (size != 0)
                {
                    _emulator.WriteUInt32(size, (uint)(list.Length / 2));
                }

                if (buffer != 0 && room >= list.Length / 2)
                {
                    _emulator.WriteMemory(buffer, list);
                }

                _frame.Return(1);
            };

            handlers["_vscprintf"] = () => _frame.Return((uint)Format(_frame.Arg(0), _frame.Arg(1)).Length);
            handlers["vprintf"] = () => Print(Format(_frame.Arg(0), _frame.Arg(1)));
            handlers["printf"] = () => Print(new PrintfFormatter(_emulator, _frame).Format(Read(0), new VarArgReader(_emulator, 1)));

            // --- locale ---
            handlers["localeconv"] = () => _frame.Return(LocaleConventions());

            // --- time ---
            handlers["GetSystemTime"] = () => WriteSystemTime(_frame.Arg(0), DateTime.UtcNow);
            handlers["SystemTimeToFileTime"] = () =>
            {
                byte[] st = _emulator.ReadMemory(_frame.Arg(0), 16);
                ushort Field(int i) => BitConverter.ToUInt16(st, i * 2);
                try
                {
                    var time = new DateTime(Field(0), Field(1), Field(3), Field(4), Field(5), Field(6), Field(7), DateTimeKind.Utc);
                    _emulator.WriteMemory(_frame.Arg(1), BitConverter.GetBytes(time.ToFileTimeUtc()));
                    _frame.Return(1);
                }
                catch (ArgumentException)
                {
                    _frame.Return(0);
                }
            };

            // errno_t _ftime64_s(struct __timeb64*): time64 at 0, millitm at 8, timezone at 10, dstflag at 12.
            handlers["_ftime64_s"] = () =>
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                long target = _frame.Arg(0);
                if (target != 0)
                {
                    byte[] block = new byte[16];
                    BitConverter.GetBytes(now.ToUnixTimeSeconds()).CopyTo(block, 0);
                    BitConverter.GetBytes((ushort)now.Millisecond).CopyTo(block, 8);
                    _emulator.WriteMemory(target, block);
                }

                _frame.Return(0);
            };

            // double _difftime64(__time64_t end, __time64_t start): two 64-bit ints in r0:r1, r2:r3.
            handlers["_difftime64"] = () =>
            {
                long end = (long)((ulong)(uint)_frame.Arg(0) | ((ulong)(uint)_frame.Arg(1) << 32));
                long start = (long)((ulong)(uint)_frame.Arg(2) | ((ulong)(uint)_frame.Arg(3) << 32));
                _frame.ReturnDouble(end - start);
            };

            // int xtime_get(xtime*, int base): {__time64_t sec; long nsec}, base TIME_UTC = 1.
            handlers["xtime_get"] = () =>
            {
                long target = _frame.Arg(0);
                long ticks = DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks;
                if (target != 0)
                {
                    _emulator.WriteMemory(target, [.. BitConverter.GetBytes(ticks / TimeSpan.TicksPerSecond),
                        .. BitConverter.GetBytes((int)(ticks % TimeSpan.TicksPerSecond * 100))]);
                }

                _frame.Return(_frame.Arg(1));
            };

            // long long _Xtime_get_ticks(): 100ns units since 1970.
            handlers["_Xtime_get_ticks"] = () => _frame.Return64(DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks);

            // long _Xtime_diff_to_millis2(const xtime* later, const xtime* earlier)
            handlers["_Xtime_diff_to_millis2"] = () =>
            {
                long Millis(long xtime) => (BitConverter.ToInt64(_emulator.ReadMemory(xtime, 8)) * 1000) +
                                           (BitConverter.ToInt32(_emulator.ReadMemory(xtime + 8, 4)) / 1_000_000);
                long difference = Millis(_frame.Arg(0)) - Millis(_frame.Arg(1));
                _frame.Return(unchecked((uint)(int)Math.Max(0, difference)));
            };

            // A sleep cannot be honoured on the guest's only thread without stalling the frame
            // that asked; returning at once is what every other wait here does.
            handlers["_Thrd_sleep"] = () => _frame.Return(0);
            handlers["_Thrd_yield"] = () => _frame.Return(0);

            // --- std::exception ---
            // Layout: vfptr, _Mywhat (char*), _Mydofree (bool).
            handlers["??0exception@std@@QAA@XZ"] = () => ConstructException(_frame.Arg(0), 0);
            handlers["??0exception@std@@QAA@ABQBD@Z"] = () =>
            {
                long message = _frame.Arg(1) == 0 ? 0 : _emulator.ReadUInt32(_frame.Arg(1), 0);
                ConstructException(_frame.Arg(0), message);
            };
            handlers["??0exception@std@@QAA@ABV01@@Z"] = () =>
                ConstructException(_frame.Arg(0), _frame.Arg(1) == 0 ? 0 : _emulator.ReadUInt32(_frame.Arg(1) + 4, 0));
            handlers["??1exception@std@@UAA@XZ"] = () => _frame.Return(0);
            handlers["?what@exception@std@@UBAPBDXZ"] = () =>
            {
                long message = _emulator.ReadUInt32(_frame.Arg(0) + 4, 0);
                _frame.Return(message != 0 ? message : UnknownException());
            };
            handlers["??1type_info@@UAA@XZ"] = () => _frame.Return(0);

            // --- process exit: the game is done, so the run is. ---
            handlers["exit"] = () => _emulator.Stop($"the image called exit({_frame.SignedArg(0)})");
            handlers["_exit"] = () => _emulator.Stop($"the image called _exit({_frame.SignedArg(0)})");
            handlers["abort"] = () => _emulator.Stop("the image called abort()");
            handlers["?terminate@@YAXXZ"] = () => _emulator.Stop("the image called std::terminate()");
            handlers["_purecall"] = () => _emulator.Stop("the image called a pure virtual function");

            // --- networking: offline ---
            handlers["getaddrinfo"] = () =>
            {
                if (_frame.Arg(3) != 0)
                {
                    _emulator.WriteUInt32(_frame.Arg(3), 0);
                }

                _frame.Return(11001); // WSAHOST_NOT_FOUND
            };
            handlers["freeaddrinfo"] = () => _frame.Return(0);
            handlers["FormatMessageW"] = () => _frame.Return(0);

            // --- qsort ---
            handlers["qsort"] = Qsort;
        }

        /// <summary>
        /// Winsock by ordinal - the import table names these by number only. Answered as a
        /// machine whose network is down.
        /// </summary>
        public bool TryWinsock(string fullName)
        {
            if (!fullName.StartsWith("WS2_32", StringComparison.OrdinalIgnoreCase) &&
                !fullName.StartsWith("ws2_32", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string ordinal = fullName[(fullName.IndexOf('!') + 1)..];
            const uint socketError = 0xFFFFFFFF;
            switch (ordinal)
            {
                case "#115": _frame.Return(0); break;            // WSAStartup
                case "#116": _frame.Return(0); break;            // WSACleanup
                case "#111": _frame.Return(10050); break;        // WSAGetLastError: WSAENETDOWN
                case "#3": _frame.Return(0); break;              // closesocket
                case "#21": _frame.Return(0); break;             // setsockopt
                case "#10": _frame.Return(0); break;             // ioctlsocket
                default: _frame.Return(socketError); break;      // socket, connect, send, recv, select...
            }

            return true;
        }

        private string Read(int argument) => _frame.ReadNarrowString(_frame.Arg(argument));

        private static string Truncate(string text, int count) => count <= 0 ? string.Empty : text.Length <= count ? text : text[..count];

        private string Format(long format, long vaList)
            => new PrintfFormatter(_emulator, _frame).Format(_frame.ReadNarrowString(format), VarArgReader.FromList(_emulator, vaList));

        private void Print(string text)
        {
            if (Printed.Count < 200)
            {
                Printed.Add(text.TrimEnd('\n'));
            }

            _frame.Return((uint)text.Length);
        }

        /// <summary>long strtol(const char*, char** end, int base), base 0 meaning "from the prefix".</summary>
        private void ParseInteger(bool signed)
        {
            string text = Read(0);
            int radix = (int)_frame.Arg(2);
            long value = LeadingInteger(text, radix, out int consumed);
            if (_frame.Arg(1) != 0)
            {
                _emulator.WriteUInt32(_frame.Arg(1), (uint)(_frame.Arg(0) + consumed));
            }

            value = Math.Clamp(value, int.MinValue, int.MaxValue);
            _frame.Return(unchecked((uint)(int)value));
        }

        private static long LeadingInteger(string text, int radix, out int consumed)
        {
            int at = 0;
            while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
            bool negative = false;
            if (at < text.Length && text[at] is '+' or '-')
            {
                negative = text[at] == '-';
                at++;
            }

            if ((radix == 0 || radix == 16) && at + 1 < text.Length && text[at] == '0' && text[at + 1] is 'x' or 'X')
            {
                radix = 16;
                at += 2;
            }
            else if (radix == 0)
            {
                radix = at < text.Length && text[at] == '0' ? 8 : 10;
            }

            long value = 0;
            int start = at;
            while (at < text.Length)
            {
                int digit = text[at] switch
                {
                    >= '0' and <= '9' => text[at] - '0',
                    >= 'a' and <= 'z' => text[at] - 'a' + 10,
                    >= 'A' and <= 'Z' => text[at] - 'A' + 10,
                    _ => 99,
                };
                if (digit >= radix) break;
                value = unchecked((value * radix) + digit);
                at++;
            }

            consumed = at == start ? 0 : at;
            return negative ? -value : value;
        }

        private static double LeadingDouble(string text, out int consumed)
        {
            int at = 0;
            while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
            int start = at;
            while (at < text.Length && (char.IsAsciiDigit(text[at]) || text[at] == '.' ||
                   (at == start && text[at] is '+' or '-') ||
                   (at > start && text[at] is 'e' or 'E') ||
                   (at > start && text[at - 1] is 'e' or 'E' && text[at] is '+' or '-')))
            {
                at++;
            }

            consumed = at;
            return double.TryParse(text[start..at], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0;
        }

        /// <summary>The "C" locale's lconv: "." for the decimal point, and empty everything else.</summary>
        private long LocaleConventions()
        {
            if (_lconv != 0)
            {
                return _lconv;
            }

            long dot = _emulator.AllocateHeap(8);
            _emulator.WriteMemory(dot, [(byte)'.', 0]);
            long empty = _emulator.AllocateHeap(8);
            _emulator.WriteMemory(empty, [0, 0, 0, 0]);
            long charMax = _emulator.AllocateHeap(8);
            _emulator.WriteMemory(charMax, [0x7F, 0]);

            // 10 char* fields, then 8 chars of CHAR_MAX, then the wide pointers (VC11 adds 8).
            _lconv = _emulator.AllocateHeap(0x60);
            byte[] block = new byte[0x60];
            for (int i = 0; i < 10; i++)
            {
                BitConverter.GetBytes((uint)(i == 0 ? dot : empty)).CopyTo(block, i * 4);
            }

            for (int i = 0; i < 8; i++)
            {
                block[40 + i] = 0x7F;
            }

            for (int i = 0; i < 8; i++)
            {
                BitConverter.GetBytes((uint)(i == 0 ? dot : empty)).CopyTo(block, 48 + (i * 4));
            }

            _emulator.WriteMemory(_lconv, block);
            return _lconv;
        }

        private void WriteSystemTime(long target, DateTime time)
        {
            if (target != 0)
            {
                ushort[] fields = [(ushort)time.Year, (ushort)time.Month, (ushort)time.DayOfWeek, (ushort)time.Day,
                    (ushort)time.Hour, (ushort)time.Minute, (ushort)time.Second, (ushort)time.Millisecond];
                _emulator.WriteMemory(target, fields.SelectMany(BitConverter.GetBytes).ToArray());
            }

            _frame.Return(0);
        }

        private void ConstructException(long self, long message)
        {
            if (self != 0)
            {
                long copy = 0;
                if (message != 0)
                {
                    byte[] text = Encoding.Latin1.GetBytes(_frame.ReadNarrowString(message, 4096));
                    copy = _emulator.AllocateHeap(text.Length + 1);
                    _emulator.WriteMemory(copy, [.. text, 0]);
                }

                _emulator.WriteUInt32(self + 4, (uint)copy);
                _emulator.WriteUInt32(self + 8, copy != 0 ? 1u : 0u);
            }

            _frame.Return(self);
        }

        private long UnknownException()
        {
            if (_unknownException == 0)
            {
                byte[] text = Encoding.Latin1.GetBytes("Unknown exception");
                _unknownException = _emulator.AllocateHeap(text.Length + 1);
                _emulator.WriteMemory(_unknownException, [.. text, 0]);
            }

            return _unknownException;
        }

        /// <summary>
        /// void qsort(void* base, size_t count, size_t size, int (*compare)(const void*, const void*)):
        /// an insertion sort over guest memory, asking the image's comparator through a callback
        /// for each step. Quadratic, which is fine for the small tables games sort.
        /// </summary>
        private void Qsort()
        {
            long array = _frame.Arg(0);
            int count = (int)_frame.Arg(1);
            int size = (int)_frame.Arg(2);
            long compare = _frame.Arg(3);
            long returnTo = _emulator.ReturnAddress;

            if (array == 0 || count < 2 || size <= 0 || compare == 0)
            {
                _frame.Return(0);
                return;
            }

            // Scratch for the element being inserted, so the comparator sees real memory.
            long held = _emulator.AllocateHeap(size);

            void Insert(int i)
            {
                if (i >= count)
                {
                    _frame.Return(0);
                    _emulator.ContinueAt(returnTo);
                    return;
                }

                _emulator.WriteMemory(held, _emulator.ReadMemory(array + ((long)i * size), size));
                Shift(i, i - 1);
            }

            void Shift(int i, int j)
            {
                if (j < 0)
                {
                    Place(i, 0);
                    return;
                }

                long candidate = array + ((long)j * size);
                _emulator.CallEmulated("qsort comparator", compare, [candidate, held], onReturn: () =>
                {
                    int order = unchecked((int)(uint)_frame.Arg(0));
                    if (order > 0)
                    {
                        _emulator.WriteMemory(candidate + size, _emulator.ReadMemory(candidate, size));
                        Shift(i, j - 1);
                    }
                    else
                    {
                        Place(i, j + 1);
                    }
                }, recycle: true);
            }

            void Place(int i, int slot)
            {
                _emulator.WriteMemory(array + ((long)slot * size), _emulator.ReadMemory(held, size));
                Insert(i + 1);
            }

            Insert(1);
        }
            private readonly Dictionary<int, long> _strtokNext = new();

        /// <summary>
        /// One strtok step from <paramref name="start"/>: skips leading delimiters, terminates the
        /// token in place, and answers it with where the next call resumes (0 at the end).
        /// </summary>
        private (long Token, long Next) Tokenise(long start, long delimiters)
        {
            if (start == 0)
            {
                return (0, 0);
            }

            HashSet<byte> separators = [.. NarrowBytes(delimiters)];
            long at = start;
            while (true)
            {
                byte b = _emulator.ReadMemory(at, 1)[0];
                if (b == 0)
                {
                    return (0, 0);
                }

                if (!separators.Contains(b))
                {
                    break;
                }

                at++;
            }

            long token = at;
            while (true)
            {
                byte b = _emulator.ReadMemory(at, 1)[0];
                if (b == 0)
                {
                    return (token, 0);
                }

                if (separators.Contains(b))
                {
                    _emulator.WriteMemory(at, [0]);
                    return (token, at + 1);
                }

                at++;
            }
        }

        private void ChangeCase(long address, bool upper)
        {
            byte[] bytes = NarrowBytes(address);
            for (int i = 0; i < bytes.Length; i++)
            {
                char c = (char)bytes[i];
                bytes[i] = (byte)(upper ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
            }

            if (bytes.Length > 0)
            {
                _emulator.WriteMemory(address, bytes);
            }

            _frame.Return(address);
        }

        private byte[] NarrowBytes(long address)
        {
            List<byte> bytes = [];
            for (long at = address; address != 0 && bytes.Count < 1 << 20; at++)
            {
                byte b = _emulator.ReadMemory(at, 1)[0];
                if (b == 0)
                {
                    break;
                }

                bytes.Add(b);
            }

            return [.. bytes];
        }

        private string WideText(long address)
        {
            System.Text.StringBuilder text = new();
            for (long at = address; address != 0 && text.Length < 1 << 20; at += 2)
            {
                char c = (char)BitConverter.ToUInt16(_emulator.ReadMemory(at, 2));
                if (c == 0)
                {
                    break;
                }

                text.Append(c);
            }

            return text.ToString();
        }
    }
}

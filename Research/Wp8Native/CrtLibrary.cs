using System.Globalization;

namespace WPR.Wp8Native
{
    /// <summary>
    /// The C runtime's character, string and number functions, implemented on the host.
    /// </summary>
    /// <remarks>
    /// These are the least glamorous functions in the image and the most damaging to fake.
    /// A stub returning zero makes <c>isdigit</c> say nothing is a digit and
    /// <c>strcmp</c> say every pair of strings is equal - so parsers reject valid input,
    /// throw, and retry forever. That is exactly the loop this file was written to fix:
    /// 3,320 std::exception constructions alongside 3,320 isdigit calls.
    ///
    /// Everything here behaves as it would in the C locale, which is what the image gets
    /// on a phone anyway.
    /// </remarks>
    public sealed class CrtLibrary
    {
        private readonly ArmEmulator _emulator;
        private readonly CallFrame _frame;

        public CrtLibrary(ArmEmulator emulator, CallFrame frame)
        {
            _emulator = emulator;
            _frame = frame;
        }

        public void RegisterInto(Dictionary<string, Action> handlers)
        {
            // --- character classification ---
            Classify(handlers, "isdigit", char.IsAsciiDigit);
            Classify(handlers, "isalpha", char.IsAsciiLetter);
            Classify(handlers, "isalnum", char.IsAsciiLetterOrDigit);
            Classify(handlers, "isxdigit", char.IsAsciiHexDigit);
            Classify(handlers, "isspace", c => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r');
            Classify(handlers, "isupper", char.IsAsciiLetterUpper);
            Classify(handlers, "islower", char.IsAsciiLetterLower);
            Classify(handlers, "iscntrl", c => c < 0x20 || c == 0x7F);
            Classify(handlers, "isprint", c => c is >= (char)0x20 and < (char)0x7F);
            Classify(handlers, "isgraph", c => c is > (char)0x20 and < (char)0x7F);
            Classify(handlers, "ispunct", c => c is > (char)0x20 and < (char)0x7F && !char.IsAsciiLetterOrDigit(c));

            handlers["tolower"] = () => _frame.Return(char.ToLowerInvariant((char)(_frame.Arg(0) & 0xFF)));
            handlers["toupper"] = () => _frame.Return(char.ToUpperInvariant((char)(_frame.Arg(0) & 0xFF)));

            // Multi-byte lead byte: never, in a single-byte locale.
            handlers["_ismbblead"] = () => _frame.Return(0);

            // --- comparison ---
            handlers["strcmp"] = () => _frame.Return(Compare(Read(0), Read(1)));
            handlers["strcoll"] = () => _frame.Return(Compare(Read(0), Read(1)));
            handlers["strncmp"] = () => _frame.Return(CompareBounded());
            handlers["wcscmp"] = () => _frame.Return(
                Compare(_emulator.ReadUtf16String(_frame.Arg(0)), _emulator.ReadUtf16String(_frame.Arg(1))));

            // --- copying ---
            handlers["strcpy"] = () => CopyString(_frame.Arg(0), Read(1));
            handlers["strcat"] = () => CopyString(_frame.Arg(0), Read(0) + Read(1));

            // The _s forms take the destination size as their second argument, which shifts
            // the source along by one: (dst, dstSize, src).
            handlers["strcpy_s"] = () => CopyStringChecked(_frame.Arg(0), (int)_frame.Arg(1), Read(2));
            handlers["strcat_s"] = () => CopyStringChecked(_frame.Arg(0), (int)_frame.Arg(1), Read(0) + Read(2));

            handlers["strncpy"] = () => CopyStringPadded(_frame.Arg(0), Read(1), (int)_frame.Arg(2));
            handlers["strncat"] = () => CopyString(_frame.Arg(0), Read(0) + Truncate(Read(1), (int)_frame.Arg(2)));

            // --- searching ---
            handlers["strchr"] = () => FindCharacter(first: true);
            handlers["strrchr"] = () => FindCharacter(first: false);
            handlers["strstr"] = () => FindSubstring();
            handlers["strpbrk"] = () => FindAnyOf();
            handlers["strcspn"] = () => SpanLength(matching: false);
            handlers["strspn"] = () => SpanLength(matching: true);

            // --- numbers ---
            handlers["atoi"] = () => _frame.Return(
                int.TryParse(Read(0).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                    ? parsed
                    : 0);

            handlers["strtoul"] = ParseUnsignedLong;
            handlers["strtod"] = ParseDouble;

            // --- misc ---
            handlers["strerror"] = () => _frame.Return(AllocateString("unknown error"));

            RegisterFormatting(handlers);
            RegisterConcurrency(handlers);
        }

        /// <summary>
        /// The printf family. The variadic arguments start one core position past the
        /// named ones, which is the only thing that differs between these.
        /// </summary>
        private void RegisterFormatting(Dictionary<string, Action> handlers)
        {
            // int sprintf(char* buffer, const char* format, ...)
            handlers["sprintf"] = () => WriteFormatted(
                buffer: _frame.Arg(0), capacity: -1, format: _frame.Arg(1), firstVariadicPosition: 2);

            // int sprintf_s(char* buffer, size_t size, const char* format, ...)
            handlers["sprintf_s"] = () => WriteFormatted(
                buffer: _frame.Arg(0), capacity: _frame.Arg(1), format: _frame.Arg(2), firstVariadicPosition: 3);

            // int _snprintf(char* buffer, size_t count, const char* format, ...)
            handlers["_snprintf"] = () => WriteFormatted(
                buffer: _frame.Arg(0), capacity: _frame.Arg(1), format: _frame.Arg(2), firstVariadicPosition: 3);
        }

        private void WriteFormatted(long buffer, long capacity, long format, int firstVariadicPosition)
        {
            string formatText = _frame.ReadNarrowString(format);
            string text = new PrintfFormatter(_emulator, _frame)
                .Format(formatText, new VarArgReader(_emulator, firstVariadicPosition));

            // Keeping the first variadic words alongside the result: when a formatted
            // string comes out wrong it is almost always an argument read from the wrong
            // place rather than a formatting bug, and these two tell them apart.
            FormattedStrings.Add(
                $"\"{formatText}\" (r2=0x{_frame.Arg(2):X8} r3=0x{_frame.Arg(3):X8}) -> \"{text}\"");

            if (buffer == 0)
            {
                _frame.Return(-1);
                return;
            }

            if (capacity >= 0 && text.Length + 1 > capacity)
            {
                // The _s forms leave an empty string and report failure rather than truncating.
                if (capacity > 0)
                {
                    _frame.WriteNarrowString(buffer, string.Empty);
                }

                _frame.Return(-1);
                return;
            }

            _frame.WriteNarrowString(buffer, text);
            _frame.Return(text.Length);
        }

        /// <summary>
        /// The Concurrency Runtime, as far as a single CPU can honour it.
        /// </summary>
        /// <remarks>
        /// Locks are the easy part: with one thread of execution nothing is ever
        /// contended, so acquiring one always succeeds immediately and a no-op is not an
        /// approximation but the correct answer. Waiting is the awkward part - an event
        /// wait that returns immediately is a lie if something else was supposed to signal
        /// it, and there is no something else. It is reported as signalled because the
        /// alternative is to block forever, and deferred work at least gets a chance to run.
        /// </remarks>
        private void RegisterConcurrency(Dictionary<string, Action> handlers)
        {
            // Concurrency::Alloc / Free - a separate allocator from the CRT heap.
            handlers["?Alloc@Concurrency@@YAPAXI@Z"] = () => _frame.Return(_emulator.AllocateHeap(_frame.Arg(0)));
            handlers["?Free@Concurrency@@YAXPAX@Z"] = () => _frame.Return(0);

            // critical_section and its scoped_lock. Constructors return `this` through the
            // general rule in HostStubs; locking and unlocking have nothing to do.
            handlers["?lock@critical_section@Concurrency@@QAAXXZ"] = () => _frame.Return(_frame.Arg(0));
            handlers["?unlock@critical_section@Concurrency@@QAAXXZ"] = () => _frame.Return(0);
            handlers["?try_lock@critical_section@Concurrency@@QAA_NXZ"] = () => _frame.Return(1);

            // event::wait returns 0 when signalled, 0xFFFFFFFF on timeout.
            handlers["?wait@event@Concurrency@@QAAII@Z"] = () => _frame.Return(0);
            handlers["?set@event@Concurrency@@QAAXXZ"] = () => _frame.Return(0);
            handlers["?reset@event@Concurrency@@QAAXXZ"] = () => _frame.Return(0);

            // Concurrency::wait(milliseconds) - a sleep, and time does not pass here.
            handlers["?wait@Concurrency@@YAXI@Z"] = () => _frame.Return(0);

            // One hardware thread, which is the truth.
            handlers["?_GetConcurrency@details@Concurrency@@YAIXZ"] = () => _frame.Return(1);
            handlers["?_Id@_CurrentScheduler@details@Concurrency@@SAIXZ"] = () => _frame.Return(1);

            // Returns unsigned& - a reference, so it owes the caller an address that stays
            // valid and readable, not a value.
            handlers["?_GetCurrentInlineDepth@_StackGuard@details@Concurrency@@CAAAIXZ"] = () =>
            {
                _inlineDepth = _inlineDepth == 0 ? _emulator.AllocateHeap(4) : _inlineDepth;
                _frame.Return(_inlineDepth);
            };

            // static void _ScheduleTask(void (*proc)(void*), void* data) - the raw-function
            // shape of the same "run this elsewhere" request the thread pool makes.
            handlers["?_ScheduleTask@_CurrentScheduler@details@Concurrency@@SAXP6AXPAX@Z0@Z"] = () =>
            {
                _emulator.QueueDeferredCall("Concurrency::ScheduledTask", _frame.Arg(0), _frame.Arg(1));
                _frame.Return(0);
            };

            RegisterTaskCollections(handlers);
            RegisterTime(handlers);
        }

        /// <summary>
        /// PPL task collections - the machinery behind <c>Concurrency::task</c>.
        /// </summary>
        /// <remarks>
        /// A task collection owns a set of chores and runs them. With one CPU there is
        /// nothing to run them on concurrently, so the collection is a stand-in object and
        /// the work it is given is queued alongside everything else.
        ///
        /// The important part is that <c>_NewCollection</c> hands back an **object**.
        /// Returning zero here is what stopped the previous run: the image took the null,
        /// called <c>_Schedule</c> through it, and died.
        /// </remarks>
        private void RegisterTaskCollections(Dictionary<string, Action> handlers)
        {
            // static _AsyncTaskCollection* _NewCollection(_CancellationTokenState*)
            handlers["?_NewCollection@_AsyncTaskCollection@details@Concurrency@@SAPAV123@PAV_CancellationTokenState@23@@Z"] =
                () => _frame.Return(_emulator.CreateShapedObject("_AsyncTaskCollection"));

            // void _TaskCollection::_Schedule(_UnrealizedChore*)
            handlers["?_Schedule@_TaskCollection@details@Concurrency@@QAAXPAV_UnrealizedChore@23@@Z"] = () =>
            {
                ScheduleChore(_frame.Arg(1));
                _frame.Return(0);
            };

            // _TaskCollectionStatus _TaskCollection::_RunAndWait(_UnrealizedChore*)
            // 0 = _NotComplete, 1 = _Completed, 2 = _Canceled.
            handlers["?_RunAndWait@_TaskCollection@details@Concurrency@@QAA?AW4_TaskCollectionStatus@23@PAV_UnrealizedChore@23@@Z"] = () =>
            {
                TaskCollectionLog.Add($"_RunAndWait(chore 0x{_frame.Arg(1):X8}) -> reported complete");
                _frame.Return(1);
            };

            handlers["?_Cancel@_TaskCollection@details@Concurrency@@QAAXXZ"] = () => _frame.Return(0);

            // void Concurrency::wait(unsigned milliseconds) - the image's only sleep, and
            // the point where it says it is willing to be interrupted. Anything queued runs
            // here; if nothing is queued this is just a return, exactly as a zero-length
            // sleep would be.
            handlers["?wait@Concurrency@@YAXI@Z"] = () =>
            {
                _frame.Return(0);
                _emulator.YieldToDeferredWork();
            };

            // A cancellation registration is another object the caller keeps and later
            // hands back, so it cannot be null either.
            handlers["?_RegisterCallback@_CancellationTokenState@details@Concurrency@@QAAPAV_CancellationTokenRegistration@23@P6AXPAX@Z0H@Z"] =
                () => _frame.Return(_emulator.CreateShapedObject("_CancellationTokenRegistration", 8));

            handlers["?_DeregisterCallback@_CancellationTokenState@details@Concurrency@@QAAXPAV_CancellationTokenRegistration@23@@Z"] =
                () => _frame.Return(0);

            // Called when a task's exception was never observed. Worth recording: it means
            // something threw inside a task and nothing looked at it.
            handlers["?_ReportUnobservedException@details@Concurrency@@YAXXZ"] = () =>
            {
                TaskCollectionLog.Add("_ReportUnobservedException - a task exception went unobserved");
                _frame.Return(0);
            };
        }

        /// <summary>
        /// Queues a ConcRT chore so it runs at the next yield point.
        /// </summary>
        /// <remarks>
        /// A chore is not dispatched through its vtable. <c>_Chore</c> declares exactly one
        /// virtual - its destructor - and its vtable here has exactly one slot to match;
        /// <c>_UnrealizedChore::_Invoke</c> is an ordinary member that calls a function
        /// pointer stored in the object, which ConcRT sets to a bridge that knows the real
        /// handle type. So the thing to call is that member, and finding it means looking at
        /// the object rather than at the class.
        ///
        /// The scan is for the first word that lands in the image's executable range. That is
        /// deliberately empirical: the layout of _Chore is not published, and guessing an
        /// offset here is exactly the mistake that had this probe calling a deleting
        /// destructor as if it were a work item.
        /// </remarks>
        private void ScheduleChore(long chore)
        {
            if (chore == 0)
            {
                TaskCollectionLog.Add("_Schedule(null) - ignored");
                return;
            }

            string words = string.Join(" ", Enumerable.Range(0, 8)
                .Select(i => $"+{i * 4:X2}={_emulator.ReadUInt32(chore + (i * 4)):X8}"));

            long invoke = 0;
            int foundAt = -1;
            for (int i = 0; i < 8; i++)
            {
                long candidate = _emulator.ReadUInt32(chore + (i * 4));
                if (_emulator.IsExecutableCode(candidate))
                {
                    invoke = candidate;
                    foundAt = i * 4;
                    break;
                }
            }

            if (invoke == 0)
            {
                TaskCollectionLog.Add($"_Schedule(chore 0x{chore:X8}) - no chore function found; {words}");
                return;
            }

            TaskCollectionLog.Add(
                $"_Schedule(chore 0x{chore:X8}) -> queued 0x{invoke:X8} from +0x{foundAt:X}; {words}");
            _emulator.QueueDeferredCall("_UnrealizedChore::_Invoke", invoke, chore);
        }

        /// <summary>
        /// The C time functions, which nothing here implemented until now.
        /// </summary>
        /// <remarks>
        /// All seven fell through to the default stub and answered zero, and zero is a
        /// legal-looking answer for every one of them: <c>time</c> returns the epoch,
        /// <c>localtime</c> returns null, and a struct tm full of zeros is 1 January 1900,
        /// which is exactly what showed up in the trace as <c>"%g" -> "1900"</c> while the
        /// image formatted a date.
        ///
        /// Time here advances but does not track the wall clock. A fixed base plus a counted
        /// tick keeps two runs of the same image comparable, which matters more for a probe
        /// than being right about the date; a game that measures elapsed time still sees it
        /// pass.
        /// </remarks>
        private void RegisterTime(Dictionary<string, Action> handlers)
        {
            handlers["time"] = () => ReturnTime(wide: false);
            handlers["_time32"] = () => ReturnTime(wide: false);
            handlers["_time64"] = () => ReturnTime(wide: true);

            // clock() counts CLOCKS_PER_SEC ticks since the process started, and MSVC's
            // CLOCKS_PER_SEC is 1000 - so this is milliseconds, and it must start near zero
            // rather than at the epoch.
            handlers["clock"] = () => _frame.Return(NextTick() * 1000 / TicksPerSecond);

            handlers["_localtime64"] = () => ReturnBrokenDownTime(Arg64Pointer(0));
            handlers["_localtime32"] = () => ReturnBrokenDownTime(Arg64Pointer(0));
            handlers["localtime"] = () => ReturnBrokenDownTime(Arg64Pointer(0));
            handlers["_gmtime64"] = () => ReturnBrokenDownTime(Arg64Pointer(0));
            handlers["_gmtime32"] = () => ReturnBrokenDownTime(Arg64Pointer(0));
            handlers["gmtime"] = () => ReturnBrokenDownTime(Arg64Pointer(0));

            // errno_t _localtime64_s(struct tm *result, const __time64_t *time) - the
            // arguments are the other way round from the unsafe version, and it fills the
            // caller's buffer rather than a static one.
            handlers["_localtime64_s"] = () => FillBrokenDownTime(_frame.Arg(0), Arg64Pointer(1));
            handlers["_gmtime64_s"] = () => FillBrokenDownTime(_frame.Arg(0), Arg64Pointer(1));

            handlers["_mktime64"] = () => _frame.Return64(SecondsFrom(_frame.Arg(0)));
            handlers["mktime"] = () => _frame.Return(SecondsFrom(_frame.Arg(0)));

            handlers["strftime"] = FormatTime;
            handlers["_strftime_l"] = FormatTime;
        }

        /// <summary>1 January 2026, so a formatted date reads as a date and not as 1900.</summary>
        private static readonly DateTime TimeBase = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        private const long TicksPerSecond = 1000;

        private long _timeTicks;

        private long NextTick() => _timeTicks += 16;

        private DateTime Now() => TimeBase.AddMilliseconds(NextTick());

        private long CurrentSeconds()
            => (long)(Now() - DateTime.UnixEpoch).TotalSeconds;

        /// <summary>
        /// time() and _time64() both take an optional pointer to write the result through,
        /// and both also return it.
        /// </summary>
        private void ReturnTime(bool wide)
        {
            long seconds = CurrentSeconds();
            long destination = _frame.Arg(0);

            if (destination != 0)
            {
                _emulator.WriteMemory(
                    destination,
                    wide ? BitConverter.GetBytes(seconds) : BitConverter.GetBytes((int)seconds));
            }

            if (wide)
            {
                _frame.Return64(seconds);
            }
            else
            {
                _frame.Return(seconds);
            }
        }

        /// <summary>Reads a time_t through a pointer argument, 64-bit or 32-bit.</summary>
        private DateTime Arg64Pointer(int index)
        {
            long pointer = _frame.Arg(index);
            if (pointer == 0)
            {
                return Now();
            }

            try
            {
                long seconds = BitConverter.ToInt64(_emulator.ReadMemory(pointer, 8));

                // A 32-bit time_t leaves the high word as whatever was next in memory, so a
                // wildly out-of-range value means this was the narrow kind.
                if (seconds is < 0 or > 4102444800L)
                {
                    seconds = BitConverter.ToInt32(_emulator.ReadMemory(pointer, 4));
                }

                return seconds <= 0 ? Now() : DateTime.UnixEpoch.AddSeconds(seconds);
            }
            catch (Exception)
            {
                return Now();
            }
        }

        /// <summary>struct tm is nine ints: sec, min, hour, mday, mon, year, wday, yday, isdst.</summary>
        private const int BrokenDownTimeSize = 36;

        private long _brokenDownTime;

        private void WriteBrokenDownTime(long destination, DateTime when)
        {
            int[] fields =
            [
                when.Second, when.Minute, when.Hour, when.Day, when.Month - 1,
                when.Year - 1900, (int)when.DayOfWeek, when.DayOfYear - 1, 0,
            ];

            for (int i = 0; i < fields.Length; i++)
            {
                _emulator.WriteUInt32(destination + (i * 4), (uint)fields[i]);
            }
        }

        /// <summary>
        /// localtime and gmtime hand back a pointer to a static buffer they own.
        /// </summary>
        private void ReturnBrokenDownTime(DateTime when)
        {
            _brokenDownTime = _brokenDownTime != 0
                ? _brokenDownTime
                : _emulator.AllocateHeap(BrokenDownTimeSize);

            WriteBrokenDownTime(_brokenDownTime, when);
            _frame.Return(_brokenDownTime);
        }

        private void FillBrokenDownTime(long destination, DateTime when)
        {
            if (destination != 0)
            {
                WriteBrokenDownTime(destination, when);
            }

            _frame.Return(0); // errno_t success
        }

        private long SecondsFrom(long brokenDown)
        {
            if (brokenDown == 0)
            {
                return CurrentSeconds();
            }

            try
            {
                int Field(int index) => (int)_emulator.ReadUInt32(brokenDown + (index * 4));

                DateTime when = new(
                    Math.Clamp(Field(5) + 1900, 1970, 9999),
                    Math.Clamp(Field(4) + 1, 1, 12),
                    1, 0, 0, 0, DateTimeKind.Utc);

                when = when.AddDays(Math.Clamp(Field(3), 1, 31) - 1)
                           .AddHours(Field(2))
                           .AddMinutes(Field(1))
                           .AddSeconds(Field(0));

                return (long)(when - DateTime.UnixEpoch).TotalSeconds;
            }
            catch (Exception)
            {
                return CurrentSeconds();
            }
        }

        /// <summary>
        /// size_t strftime(char *dest, size_t maxsize, const char *format, const struct tm *tm).
        /// </summary>
        /// <remarks>
        /// The conversions a game actually uses, which is dates and times rather than the
        /// locale-dependent corners of the specification. An unrecognised conversion is
        /// copied through verbatim, which is visible in the output rather than silent.
        /// </remarks>
        private void FormatTime()
        {
            long destination = _frame.Arg(0);
            long limit = _frame.Arg(1);
            string format = _frame.ReadNarrowString(_frame.Arg(2));
            long brokenDown = _frame.Arg(3);

            if (destination == 0 || limit <= 0)
            {
                _frame.Return(0);
                return;
            }

            DateTime when = DateTime.UnixEpoch.AddSeconds(SecondsFrom(brokenDown));
            var text = new System.Text.StringBuilder();

            for (int i = 0; i < format.Length; i++)
            {
                if (format[i] != '%' || i + 1 >= format.Length)
                {
                    text.Append(format[i]);
                    continue;
                }

                char conversion = format[++i];
                text.Append(conversion switch
                {
                    'Y' => when.Year.ToString("D4"),
                    'y' => (when.Year % 100).ToString("D2"),
                    'm' => when.Month.ToString("D2"),
                    'd' => when.Day.ToString("D2"),
                    'H' => when.Hour.ToString("D2"),
                    'I' => (when.Hour % 12 == 0 ? 12 : when.Hour % 12).ToString("D2"),
                    'M' => when.Minute.ToString("D2"),
                    'S' => when.Second.ToString("D2"),
                    'j' => when.DayOfYear.ToString("D3"),
                    'p' => when.Hour < 12 ? "AM" : "PM",
                    'A' => when.DayOfWeek.ToString(),
                    'a' => when.DayOfWeek.ToString()[..3],
                    'B' => MonthNames[when.Month - 1],
                    'b' => MonthNames[when.Month - 1][..3],
                    'x' => when.ToString("MM/dd/yy"),
                    'X' => when.ToString("HH:mm:ss"),
                    'c' => when.ToString("ddd MMM d HH:mm:ss yyyy"),
                    'Z' => "UTC",
                    '%' => "%",
                    _ => $"%{conversion}",
                });
            }

            string result = text.ToString();
            if (result.Length >= limit)
            {
                // Too long is not truncation: strftime leaves the buffer unspecified and
                // returns zero, and a caller that checks will retry with more room.
                _frame.Return(0);
                return;
            }

            _frame.WriteNarrowString(destination, result);
            _frame.Return(result.Length);
        }

        private static readonly string[] MonthNames =
        [
            "January", "February", "March", "April", "May", "June",
            "July", "August", "September", "October", "November", "December",
        ];

        /// <summary>What the image asked the task machinery to do.</summary>
        public List<string> TaskCollectionLog { get; } = new();

        private long _inlineDepth;

        /// <summary>
        /// Functions this library knowingly does not implement. Both consume varargs; the
        /// printf side of that now works (see <see cref="PrintfFormatter"/>), but scanning
        /// a string back into caller-supplied pointers is a separate job.
        /// </summary>
        public static IReadOnlyList<string> NotImplemented { get; } = ["sscanf", "strftime"];

        /// <summary>Everything the image has formatted, in order - a window into what it is doing.</summary>
        public List<string> FormattedStrings { get; } = new();

        private void Classify(Dictionary<string, Action> handlers, string name, Func<char, bool> predicate)
            => handlers[name] = () =>
            {
                long value = _frame.Arg(0);

                // EOF (-1) is a legal argument to every one of these and is never a member
                // of any class; anything outside a byte is not a character at all.
                bool member = value is >= 0 and <= 0xFF && predicate((char)value);
                _frame.Return(member ? 1 : 0);
            };

        private string Read(int argumentIndex) => _frame.ReadNarrowString(_frame.Arg(argumentIndex));

        private static string Truncate(string text, int max)
            => max <= 0 ? string.Empty : text.Length <= max ? text : text[..max];

        private static int Compare(string left, string right) => Math.Sign(string.CompareOrdinal(left, right));

        private int CompareBounded()
        {
            int count = (int)_frame.Arg(2);
            return count <= 0 ? 0 : Compare(Truncate(Read(0), count), Truncate(Read(1), count));
        }

        private void CopyString(long destination, string text)
        {
            if (destination != 0)
            {
                _frame.WriteNarrowString(destination, text);
            }

            _frame.Return(destination);
        }

        /// <summary>The _s variants return an errno rather than the destination pointer.</summary>
        private void CopyStringChecked(long destination, int destinationSize, string text)
        {
            if (destination == 0 || destinationSize <= 0)
            {
                _frame.Return(22); // EINVAL
                return;
            }

            if (text.Length + 1 > destinationSize)
            {
                _frame.Return(34); // ERANGE
                return;
            }

            _frame.WriteNarrowString(destination, text);
            _frame.Return(0);
        }

        /// <summary>
        /// strncpy is not strcpy with a limit: it pads the destination with NULs out to
        /// count, and does not terminate at all when the source is longer.
        /// </summary>
        private void CopyStringPadded(long destination, string text, int count)
        {
            if (destination == 0 || count <= 0 || count > CallFrame.SaneLengthLimit)
            {
                _frame.Return(destination);
                return;
            }

            byte[] buffer = new byte[count];
            byte[] source = System.Text.Encoding.Latin1.GetBytes(text);
            Array.Copy(source, buffer, Math.Min(source.Length, count));

            _emulator.WriteMemory(destination, buffer);
            _frame.Return(destination);
        }

        private void FindCharacter(bool first)
        {
            long start = _frame.Arg(0);
            string haystack = Read(0);
            char needle = (char)(_frame.Arg(1) & 0xFF);

            // Searching for NUL finds the terminator, which is a real position in the string.
            int index = needle == '\0'
                ? haystack.Length
                : first ? haystack.IndexOf(needle) : haystack.LastIndexOf(needle);

            _frame.Return(index < 0 ? 0 : start + index);
        }

        private void FindSubstring()
        {
            string haystack = Read(0);
            string needle = Read(1);
            int index = needle.Length == 0 ? 0 : haystack.IndexOf(needle, StringComparison.Ordinal);
            _frame.Return(index < 0 ? 0 : _frame.Arg(0) + index);
        }

        private void FindAnyOf()
        {
            string haystack = Read(0);
            int index = haystack.IndexOfAny(Read(1).ToCharArray());
            _frame.Return(index < 0 ? 0 : _frame.Arg(0) + index);
        }

        private void SpanLength(bool matching)
        {
            string haystack = Read(0);
            HashSet<char> set = [.. Read(1)];

            int length = 0;
            while (length < haystack.Length && set.Contains(haystack[length]) == matching)
            {
                length++;
            }

            _frame.Return(length);
        }

        private void ParseUnsignedLong()
        {
            string text = Read(0);
            int radix = (int)_frame.Arg(2);
            int consumed = 0;

            ulong value = ParseInteger(text, radix, ref consumed);
            ReportParseEnd(_frame.Arg(1), _frame.Arg(0), consumed);
            _frame.Return(unchecked((long)(uint)value));
        }

        private void ParseDouble()
        {
            string text = Read(0);
            int length = 0;

            while (length < text.Length && (char.IsAsciiDigit(text[length])
                || (length == 0 && text[length] is '-' or '+')
                || text[length] == '.'
                || (length > 0 && text[length] is 'e' or 'E')
                || (length > 0 && text[length - 1] is 'e' or 'E' && text[length] is '-' or '+')))
            {
                length++;
            }

            double value = double.TryParse(
                text[..length], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : 0.0;

            ReportParseEnd(_frame.Arg(1), _frame.Arg(0), length);
            _frame.ReturnDouble(value);
        }

        /// <summary>Writes the "first unconverted character" pointer that strto* owes its caller.</summary>
        private void ReportParseEnd(long endPointer, long start, int consumed)
        {
            if (endPointer != 0)
            {
                _emulator.WriteUInt32(endPointer, (uint)(start + consumed));
            }
        }

        private static ulong ParseInteger(string text, int radix, ref int consumed)
        {
            int index = 0;
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            if (radix == 0)
            {
                radix = text[index..].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10;
            }

            if (radix == 16 && text[index..].StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                index += 2;
            }

            ulong value = 0;
            int digits = 0;
            while (index < text.Length)
            {
                int digit = char.IsAsciiDigit(text[index]) ? text[index] - '0'
                    : char.IsAsciiLetter(text[index]) ? char.ToLowerInvariant(text[index]) - 'a' + 10
                    : -1;

                if (digit < 0 || digit >= radix)
                {
                    break;
                }

                value = (value * (ulong)radix) + (ulong)digit;
                index++;
                digits++;
            }

            consumed = digits == 0 ? 0 : index;
            return value;
        }

        private long AllocateString(string text)
        {
            long address = _emulator.AllocateHeap(text.Length + 1);
            if (address != 0)
            {
                _frame.WriteNarrowString(address, text);
            }

            return address;
        }
    }
}

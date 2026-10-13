using System.Text;

namespace WPR.Wp8Native
{
    /// <summary>
    /// FILETIME, SYSTEMTIME and time zones (api-ms-win-core-timezone, -sysinfo, -datetime's
    /// conversions). The time zone is the host's real one, described the way Windows describes a
    /// zone: a bias, and the standard and daylight transitions of the current rule.
    /// </summary>
    public sealed partial class Win32Core
    {
        /// <summary>Days from 0001-01-01 to 1601-01-01, the FILETIME epoch.</summary>
        private const long FileTimeEpochDays = 584388;

        private const long TicksPerMillisecond = 10_000;
        private const long TicksPerDay = 864_000_000_000;

        private readonly record struct SystemTime(int Year, int Month, int DayOfWeek, int Day, int Hour, int Minute, int Second, int Milliseconds);

        private void RegisterTime(Dictionary<string, Action> handlers)
        {
            // BOOL FileTimeToSystemTime(const FILETIME*, SYSTEMTIME*): UTC broken down, with the day
            // of the week. A FILETIME with the top bit set is ERROR_INVALID_PARAMETER.
            handlers["FileTimeToSystemTime"] = () =>
            {
                ulong fileTime = ReadFileTime(_frame.Arg(0));
                if (fileTime > long.MaxValue || _frame.Arg(1) == 0)
                {
                    Fail(ErrorInvalidParameter);
                    return;
                }

                WriteSystemTime(_frame.Arg(1), FromFileTime((long)fileTime));
                _frame.Return(1);
            };

            // BOOL SystemTimeToFileTime(const SYSTEMTIME*, FILETIME*): wDayOfWeek is ignored; any
            // field out of range is ERROR_INVALID_PARAMETER.
            handlers["SystemTimeToFileTime"] = () =>
            {
                if (_frame.Arg(1) == 0 || ToFileTime(ReadSystemTime(_frame.Arg(0))) is not { } fileTime)
                {
                    Fail(ErrorInvalidParameter);
                    return;
                }

                _emulator.WriteUInt64(_frame.Arg(1), (ulong)fileTime);
                _frame.Return(1);
            };

            // LONG CompareFileTime(const FILETIME*, const FILETIME*): -1, 0 or 1.
            handlers["CompareFileTime"] = () =>
            {
                ulong a = ReadFileTime(_frame.Arg(0)), b = ReadFileTime(_frame.Arg(1));
                _frame.Return(a < b ? 0xFFFFFFFF : a > b ? 1 : 0);
            };

            // BOOL FileTimeToLocalFileTime / LocalFileTimeToFileTime: shifted by the zone's CURRENT
            // bias, daylight saving included, whatever date the FILETIME holds - as documented.
            handlers["FileTimeToLocalFileTime"] = () => ShiftFileTime(+1);
            handlers["LocalFileTimeToFileTime"] = () => ShiftFileTime(-1);

            // void GetLocalTime(SYSTEMTIME*): the host's local wall clock.
            handlers["GetLocalTime"] = () =>
            {
                DateTime now = DateTime.Now;
                WriteSystemTime(_frame.Arg(0), FromDateTime(now));
                _frame.Return(0);
            };

            // void GetSystemTime(SYSTEMTIME*): UTC.
            handlers["GetSystemTime"] = () =>
            {
                WriteSystemTime(_frame.Arg(0), FromDateTime(DateTime.UtcNow));
                _frame.Return(0);
            };

            // DWORD GetTimeZoneInformation(TIME_ZONE_INFORMATION*) / GetDynamicTimeZoneInformation:
            // TIME_ZONE_ID_UNKNOWN (0) for a zone without daylight saving, otherwise STANDARD (1) or
            // DAYLIGHT (2) for now.
            handlers["GetTimeZoneInformation"] = () => WriteZone(_frame.Arg(0), dynamic: false);
            handlers["GetDynamicTimeZoneInformation"] = () => WriteZone(_frame.Arg(0), dynamic: true);

            // BOOL SystemTimeToTzSpecificLocalTime(const TIME_ZONE_INFORMATION* (NULL: current),
            // const SYSTEMTIME* utc, SYSTEMTIME* local), and the inverse.
            handlers["SystemTimeToTzSpecificLocalTime"] = () => ConvertWithZone(toLocal: true);
            handlers["TzSpecificLocalTimeToSystemTime"] = () => ConvertWithZone(toLocal: false);
        }

        private ulong ReadFileTime(long pointer) => pointer == 0 ? 0 : BitConverter.ToUInt64(_emulator.ReadMemory(pointer, 8));

        private void ShiftFileTime(int direction)
        {
            long input = _frame.Arg(0), output = _frame.Arg(1);
            if (input == 0 || output == 0)
            {
                Fail(ErrorInvalidParameter);
                return;
            }

            long offset = (long)TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalMinutes * 60 * 10_000_000;
            ulong value = ReadFileTime(input);
            _emulator.WriteUInt64(output, unchecked(value + (ulong)(direction * offset)));
            _frame.Return(1);
        }

        // ---------------------------------------------------------------------------
        // the calendar arithmetic, over the whole SYSTEMTIME range (1601-30827)
        // ---------------------------------------------------------------------------

        /// <summary>Days since 0001-01-01 of a proleptic Gregorian date (Hinnant's days_from_civil, shifted).</summary>
        private static long DaysFromCivil(long year, long month, long day)
        {
            year -= month <= 2 ? 1 : 0;
            long era = (year >= 0 ? year : year - 399) / 400;
            long yearOfEra = year - (era * 400);
            long dayOfYear = ((153 * (month + (month > 2 ? -3 : 9))) + 2) / 5 + day - 1;
            long dayOfEra = (yearOfEra * 365) + (yearOfEra / 4) - (yearOfEra / 100) + dayOfYear;
            return (era * 146097) + dayOfEra - 719468 + 719162;   // 719162 = days from 0001-01-01 to 1970-01-01
        }

        private static (int Year, int Month, int Day) CivilFromDays(long days)
        {
            long z = days - 719162 + 719468;
            long era = (z >= 0 ? z : z - 146096) / 146097;
            long dayOfEra = z - (era * 146097);
            long yearOfEra = (dayOfEra - (dayOfEra / 1460) + (dayOfEra / 36524) - (dayOfEra / 146096)) / 365;
            long dayOfYear = dayOfEra - ((365 * yearOfEra) + (yearOfEra / 4) - (yearOfEra / 100));
            long mp = ((5 * dayOfYear) + 2) / 153;
            long day = dayOfYear - (((153 * mp) + 2) / 5) + 1;
            long month = mp + (mp < 10 ? 3 : -9);
            return ((int)(yearOfEra + (era * 400) + (month <= 2 ? 1 : 0)), (int)month, (int)day);
        }

        private static int DaysInMonth(int year, int month) => month switch
        {
            2 => (year % 4 == 0 && (year % 100 != 0 || year % 400 == 0)) ? 29 : 28,
            4 or 6 or 9 or 11 => 30,
            _ => 31,
        };

        private static bool IsValid(SystemTime t) =>
            t.Year is >= 1601 and <= 30827 && t.Month is >= 1 and <= 12 && t.Day >= 1 && t.Day <= DaysInMonth(t.Year, t.Month) &&
            t.Hour is >= 0 and < 24 && t.Minute is >= 0 and < 60 && t.Second is >= 0 and < 60 && t.Milliseconds is >= 0 and < 1000;

        /// <summary>100ns units since 1601-01-01, or null when the SYSTEMTIME is not a valid one.</summary>
        private static long? ToFileTime(SystemTime t)
        {
            if (!IsValid(t))
            {
                return null;
            }

            long days = DaysFromCivil(t.Year, t.Month, t.Day) - FileTimeEpochDays;
            return (days * TicksPerDay) + ((((t.Hour * 60L) + t.Minute) * 60 + t.Second) * 10_000_000) + (t.Milliseconds * TicksPerMillisecond);
        }

        private static SystemTime FromFileTime(long fileTime)
        {
            long days = fileTime / TicksPerDay;
            long rest = fileTime % TicksPerDay;
            (int year, int month, int day) = CivilFromDays(days + FileTimeEpochDays);
            int dayOfWeek = (int)((days + 1) % 7);   // 1601-01-01 was a Monday
            return new SystemTime(year, month, dayOfWeek, day,
                (int)(rest / 36_000_000_000), (int)(rest / 600_000_000 % 60), (int)(rest / 10_000_000 % 60), (int)(rest / TicksPerMillisecond % 1000));
        }

        private static SystemTime FromDateTime(DateTime t) =>
            new(t.Year, t.Month, (int)t.DayOfWeek, t.Day, t.Hour, t.Minute, t.Second, t.Millisecond);

        private SystemTime ReadSystemTime(long pointer)
        {
            if (pointer == 0)
            {
                return default;
            }

            byte[] b = _emulator.ReadMemory(pointer, 16);
            int F(int i) => BitConverter.ToUInt16(b, i * 2);
            return new SystemTime(F(0), F(1), F(2), F(3), F(4), F(5), F(6), F(7));
        }

        private void WriteSystemTime(long pointer, SystemTime t)
        {
            if (pointer == 0)
            {
                return;
            }

            byte[] b = new byte[16];
            int[] fields = [t.Year, t.Month, t.DayOfWeek, t.Day, t.Hour, t.Minute, t.Second, t.Milliseconds];
            for (int i = 0; i < 8; i++)
            {
                BitConverter.GetBytes((ushort)fields[i]).CopyTo(b, i * 2);
            }

            _emulator.WriteMemory(pointer, b);
        }

        // ---------------------------------------------------------------------------
        // time zones
        // ---------------------------------------------------------------------------

        /// <summary>A TIME_ZONE_INFORMATION's content: biases in minutes, transitions as SYSTEMTIMEs.</summary>
        private sealed record ZoneRule(int Bias, string StandardName, SystemTime StandardDate, int StandardBias, string DaylightName, SystemTime DaylightDate, int DaylightBias)
        {
            public bool HasDaylight => DaylightDate.Month != 0 && StandardDate.Month != 0;
        }

        /// <summary>The host zone, as Windows would report it: its current adjustment rule.</summary>
        private static ZoneRule HostZone(out string keyName)
        {
            TimeZoneInfo zone = TimeZoneInfo.Local;
            keyName = zone.Id;
            try
            {
                if (!zone.HasIanaId)
                {
                    keyName = zone.Id;
                }
                else if (TimeZoneInfo.TryConvertIanaIdToWindowsId(zone.Id, out string? windowsId))
                {
                    keyName = windowsId;
                }
            }
            catch (Exception)
            {
                // No ICU to translate with: the IANA id is still a name for the zone.
            }

            DateTime now = DateTime.Now;
            TimeZoneInfo.AdjustmentRule? rule = zone.SupportsDaylightSavingTime
                ? zone.GetAdjustmentRules().FirstOrDefault(r => r.DateStart <= now && now <= r.DateEnd && r.DaylightDelta != TimeSpan.Zero)
                : null;

            int bias = -(int)(zone.BaseUtcOffset + (rule?.BaseUtcOffsetDelta ?? TimeSpan.Zero)).TotalMinutes;
            if (rule is null)
            {
                return new ZoneRule(bias, zone.StandardName, default, 0, zone.DaylightName, default, 0);
            }

            static SystemTime Transition(TimeZoneInfo.TransitionTime t, int year)
            {
                DateTime at = t.TimeOfDay;
                return t.IsFixedDateRule
                    ? new SystemTime(year, t.Month, 0, t.Day, at.Hour, at.Minute, at.Second, at.Millisecond)
                    : new SystemTime(0, t.Month, (int)t.DayOfWeek, t.Week, at.Hour, at.Minute, at.Second, at.Millisecond);
            }

            return new ZoneRule(
                bias,
                zone.StandardName,
                Transition(rule.DaylightTransitionEnd, now.Year),
                0,
                zone.DaylightName,
                Transition(rule.DaylightTransitionStart, now.Year),
                -(int)rule.DaylightDelta.TotalMinutes);
        }

        // TIME_ZONE_INFORMATION: Bias 0, StandardName[32] 4, StandardDate 68, StandardBias 84,
        // DaylightName[32] 88, DaylightDate 152, DaylightBias 168 (172 bytes). The dynamic form
        // adds TimeZoneKeyName[128] at 172 and DynamicDaylightTimeDisabled at 428 (432 bytes).
        private void WriteZone(long pointer, bool dynamic)
        {
            if (pointer == 0)
            {
                Fail(ErrorInvalidParameter, 0xFFFFFFFF);
                return;
            }

            ZoneRule zone = HostZone(out string keyName);
            byte[] block = new byte[dynamic ? 432 : 172];
            BitConverter.GetBytes(zone.Bias).CopyTo(block, 0);
            PutName(block, 4, zone.StandardName, 32);
            PutSystemTime(block, 68, zone.StandardDate);
            BitConverter.GetBytes(zone.StandardBias).CopyTo(block, 84);
            PutName(block, 88, zone.DaylightName, 32);
            PutSystemTime(block, 152, zone.DaylightDate);
            BitConverter.GetBytes(zone.DaylightBias).CopyTo(block, 168);
            if (dynamic)
            {
                PutName(block, 172, keyName, 128);
            }

            _emulator.WriteMemory(pointer, block);
            _frame.Return(!zone.HasDaylight ? 0 : TimeZoneInfo.Local.IsDaylightSavingTime(DateTime.Now) ? 2 : 1);
        }

        private static void PutName(byte[] block, int offset, string name, int chars)
        {
            byte[] text = Encoding.Unicode.GetBytes(name.Length >= chars ? name[..(chars - 1)] : name);
            Array.Copy(text, 0, block, offset, text.Length);
        }

        private static void PutSystemTime(byte[] block, int offset, SystemTime t)
        {
            int[] fields = [t.Year, t.Month, t.DayOfWeek, t.Day, t.Hour, t.Minute, t.Second, t.Milliseconds];
            for (int i = 0; i < 8; i++)
            {
                BitConverter.GetBytes((ushort)fields[i]).CopyTo(block, offset + (i * 2));
            }
        }

        private ZoneRule ReadZone(long pointer)
        {
            byte[] b = _emulator.ReadMemory(pointer, 172);
            SystemTime At(int offset)
            {
                int F(int i) => BitConverter.ToUInt16(b, offset + (i * 2));
                return new SystemTime(F(0), F(1), F(2), F(3), F(4), F(5), F(6), F(7));
            }

            string Name(int offset) => Encoding.Unicode.GetString(b, offset, 64).Split('\0')[0];
            return new ZoneRule(BitConverter.ToInt32(b, 0), Name(4), At(68), BitConverter.ToInt32(b, 84), Name(88), At(152), BitConverter.ToInt32(b, 168));
        }

        /// <summary>
        /// A transition SYSTEMTIME resolved to a FILETIME (in the zone's local clock) for one year:
        /// wYear 0 is "the wDay-th wDayOfWeek of wMonth" (5 = the last), anything else an absolute
        /// date that applies to that year only.
        /// </summary>
        private static long? TransitionInYear(SystemTime t, int year)
        {
            int day;
            if (t.Year != 0)
            {
                if (t.Year != year)
                {
                    return null;
                }

                day = t.Day;
            }
            else
            {
                int firstDow = (int)((DaysFromCivil(year, t.Month, 1) + 1) % 7);   // 0001-01-01 was a Monday
                day = 1 + ((t.DayOfWeek - firstDow + 7) % 7) + ((Math.Clamp(t.Day, 1, 5) - 1) * 7);
                while (day > DaysInMonth(year, t.Month))
                {
                    day -= 7;
                }
            }

            return ToFileTime(t with { Year = year, Day = day, DayOfWeek = 0 });
        }

        private const long TicksPerMinute = 600_000_000;

        /// <summary>Whether a UTC FILETIME falls in daylight time under <paramref name="zone"/>.</summary>
        private static bool InDaylight(ZoneRule zone, long utc)
        {
            if (!zone.HasDaylight)
            {
                return false;
            }

            int year = FromFileTime(utc - (zone.Bias * TicksPerMinute)).Year;
            long? start = TransitionInYear(zone.DaylightDate, year);   // local standard clock
            long? end = TransitionInYear(zone.StandardDate, year);     // local daylight clock
            if (start is null || end is null)
            {
                return false;
            }

            long startUtc = start.Value + ((zone.Bias + zone.StandardBias) * TicksPerMinute);
            long endUtc = end.Value + ((zone.Bias + zone.DaylightBias) * TicksPerMinute);
            return startUtc < endUtc
                ? utc >= startUtc && utc < endUtc
                : utc >= startUtc || utc < endUtc;   // southern hemisphere
        }

        private void ConvertWithZone(bool toLocal)
        {
            long zonePointer = _frame.Arg(0), input = _frame.Arg(1), output = _frame.Arg(2);
            ZoneRule zone = zonePointer == 0 ? HostZone(out _) : ReadZone(zonePointer);
            if (input == 0 || output == 0 || ToFileTime(ReadSystemTime(input)) is not { } time)
            {
                Fail(ErrorInvalidParameter);
                return;
            }

            long result;
            if (toLocal)
            {
                int bias = zone.Bias + (InDaylight(zone, time) ? zone.DaylightBias : zone.StandardBias);
                result = time - (bias * TicksPerMinute);
            }
            else
            {
                long asStandard = time + ((zone.Bias + zone.StandardBias) * TicksPerMinute);
                result = InDaylight(zone, asStandard)
                    ? time + ((zone.Bias + zone.DaylightBias) * TicksPerMinute)
                    : asStandard;
            }

            if (result < 0)
            {
                Fail(ErrorInvalidParameter);
                return;
            }

            WriteSystemTime(output, FromFileTime(result));
            _frame.Return(1);
        }
    }
}

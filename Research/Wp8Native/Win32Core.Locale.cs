using System.Globalization;
using System.Text;

namespace WPR.Wp8Native
{
    /// <summary>
    /// api-ms-win-core-localization, -string and -datetime: locale names and LCIDs, GetLocaleInfoEx,
    /// number/currency/date/time formatting, string comparison and character types, code-page
    /// conversion and FormatMessage.
    /// </summary>
    /// <remarks>
    /// The device locale is en-US, the one GetUserPreferredUILanguages reports. Its data is the
    /// Windows 8 NLS table spelled out below; any other locale a game names explicitly comes from
    /// the host's culture data when the host has it (ICU on Android, NLS on Windows) and is
    /// ERROR_INVALID_PARAMETER when it does not.
    /// </remarks>
    public sealed partial class Win32Core
    {
        public const string DeviceLocale = "en-US";
        private const int DeviceLcid = 0x0409;

        private const int ErrorNoUnicodeTranslation = 1113;
        private const int ErrorMessageNotFound = 317;   // ERROR_MR_MID_NOT_FOUND

        /// <summary>One locale's NLS data, everything formatting and GetLocaleInfoEx need.</summary>
        private sealed class LocaleData
        {
            public required string Name { get; init; }
            public required int Lcid { get; init; }
            public required string Language { get; init; }      // ISO 639
            public required string Language3 { get; init; }
            public required string Country { get; init; }       // ISO 3166
            public required string Country3 { get; init; }
            public required string EnglishLanguage { get; init; }
            public required string EnglishCountry { get; init; }
            public required string NativeLanguage { get; init; }
            public required string NativeCountry { get; init; }
            public required string EnglishDisplay { get; init; }
            public required string NativeDisplay { get; init; }
            public required string Parent { get; init; }
            public required string[] Days { get; init; }         // Sunday first
            public required string[] AbbrevDays { get; init; }
            public required string[] ShortestDays { get; init; }
            public required string[] Months { get; init; }
            public required string[] AbbrevMonths { get; init; }
            public required string ShortDate { get; init; }
            public required string LongDate { get; init; }
            public required string YearMonth { get; init; }
            public required string MonthDay { get; init; }
            public required string TimeFormat { get; init; }
            public required string ShortTime { get; init; }
            public required string Am { get; init; }
            public required string Pm { get; init; }
            public required string DateSeparator { get; init; }
            public required string TimeSeparator { get; init; }
            public required int FirstDayOfWeek { get; init; }    // Windows numbering: Monday = 0
            public required string Decimal { get; init; }
            public required string Thousand { get; init; }
            public required int[] Grouping { get; init; }
            public required int Digits { get; init; }
            public required int NegativeNumber { get; init; }
            public required string NegativeSign { get; init; }
            public required string PositiveSign { get; init; }
            public required string Currency { get; init; }
            public required string IntlSymbol { get; init; }
            public required string MonDecimal { get; init; }
            public required string MonThousand { get; init; }
            public required int[] MonGrouping { get; init; }
            public required int CurrencyDigits { get; init; }
            public required int PositiveCurrency { get; init; }
            public required int NegativeCurrency { get; init; }
            public required string EnglishCurrency { get; init; }
            public required string NativeCurrency { get; init; }
            public required int Measure { get; init; }           // 0 metric, 1 US
            public required string List { get; init; }
            public required int AnsiCodePage { get; init; }
            public required int OemCodePage { get; init; }
            public required int MacCodePage { get; init; }
            public required int EbcdicCodePage { get; init; }
            public required string NaN { get; init; }
            public required string PositiveInfinity { get; init; }
            public required string NegativeInfinity { get; init; }
            public required string Percent { get; init; }
            public required string PerMille { get; init; }
            public required int GeoId { get; init; }
            public CompareInfo? Compare { get; init; }
        }

        private static readonly LocaleData EnUs = new()
        {
            Name = "en-US", Lcid = 0x0409, Language = "en", Language3 = "eng", Country = "US", Country3 = "USA",
            EnglishLanguage = "English", EnglishCountry = "United States", NativeLanguage = "English", NativeCountry = "United States",
            EnglishDisplay = "English (United States)", NativeDisplay = "English (United States)", Parent = "en",
            Days = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"],
            AbbrevDays = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"],
            ShortestDays = ["Su", "Mo", "Tu", "We", "Th", "Fr", "Sa"],
            Months = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"],
            AbbrevMonths = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"],
            ShortDate = "M/d/yyyy", LongDate = "dddd, MMMM d, yyyy", YearMonth = "MMMM, yyyy", MonthDay = "MMMM d",
            TimeFormat = "h:mm:ss tt", ShortTime = "h:mm tt", Am = "AM", Pm = "PM", DateSeparator = "/", TimeSeparator = ":",
            FirstDayOfWeek = 6, Decimal = ".", Thousand = ",", Grouping = [3], Digits = 2, NegativeNumber = 1,
            NegativeSign = "-", PositiveSign = "", Currency = "$", IntlSymbol = "USD", MonDecimal = ".", MonThousand = ",",
            MonGrouping = [3], CurrencyDigits = 2, PositiveCurrency = 0, NegativeCurrency = 0,
            EnglishCurrency = "US Dollar", NativeCurrency = "US Dollar", Measure = 1, List = ",",
            AnsiCodePage = 1252, OemCodePage = 437, MacCodePage = 10000, EbcdicCodePage = 37,
            NaN = "NaN", PositiveInfinity = "Infinity", NegativeInfinity = "-Infinity", Percent = "%", PerMille = "‰",
            GeoId = 244, Compare = TryCompareInfo("en-US"),
        };

        private static CompareInfo? TryCompareInfo(string name)
        {
            try
            {
                return CultureInfo.GetCultureInfo(name).CompareInfo;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static readonly Dictionary<string, LocaleData?> Locales = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A locale by name: NULL (LOCALE_NAME_USER_DEFAULT) and "!x-sys-default-locale" are the
        /// device's, "" is the invariant locale, anything else the host's data for that name.
        /// </summary>
        private static LocaleData? FindLocale(string? name, bool allowNeutral = true)
        {
            if (name is null || name == "!x-sys-default-locale" || string.Equals(name, DeviceLocale, StringComparison.OrdinalIgnoreCase))
            {
                return EnUs;
            }

            lock (Locales)
            {
                if (Locales.TryGetValue(name, out LocaleData? cached))
                {
                    return cached;
                }

                LocaleData? data = null;
                try
                {
                    CultureInfo culture = name.Length == 0 ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(name, predefinedOnly: true);
                    if (allowNeutral || !culture.IsNeutralCulture)
                    {
                        data = FromCulture(culture);
                    }
                }
                catch (Exception)
                {
                    data = null;
                }

                Locales[name] = data;
                return data;
            }
        }

        private static LocaleData? FindLocale(int lcid) => lcid switch
        {
            0x0400 or 0x0800 or 0 or DeviceLcid => EnUs,   // LOCALE_USER_DEFAULT, LOCALE_SYSTEM_DEFAULT
            0x007F => FindLocale(string.Empty),           // LOCALE_INVARIANT
            _ => LcidToName(lcid) is { } name ? FindLocale(name) : null,
        };

        private static string? LcidToName(int lcid)
        {
            try
            {
                return CultureInfo.GetCultureInfo(lcid).Name;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int[] Groups(int[] sizes) => sizes.Length == 0 ? [0] : sizes;

        private static LocaleData FromCulture(CultureInfo culture)
        {
            RegionInfo? region = null;
            try
            {
                region = culture.IsNeutralCulture || culture.Name.Length == 0 ? null : new RegionInfo(culture.Name);
            }
            catch (Exception)
            {
                region = null;
            }

            DateTimeFormatInfo dates = culture.DateTimeFormat;
            NumberFormatInfo numbers = culture.NumberFormat;
            TextInfo text = culture.TextInfo;
            string[] Seven(string[] names) => names.Take(7).ToArray();
            string[] Twelve(string[] names) => names.Take(12).ToArray();
            return new LocaleData
            {
                Name = culture.Name,
                Lcid = culture.LCID,
                Language = culture.TwoLetterISOLanguageName,
                Language3 = culture.ThreeLetterISOLanguageName,
                Country = region?.TwoLetterISORegionName ?? string.Empty,
                Country3 = region?.ThreeLetterISORegionName ?? string.Empty,
                EnglishLanguage = (culture.IsNeutralCulture ? culture : culture.Parent).EnglishName,
                EnglishCountry = region?.EnglishName ?? string.Empty,
                NativeLanguage = (culture.IsNeutralCulture ? culture : culture.Parent).NativeName,
                NativeCountry = region?.NativeName ?? string.Empty,
                EnglishDisplay = culture.EnglishName,
                NativeDisplay = culture.NativeName,
                Parent = culture.Parent.Name,
                Days = Seven(dates.DayNames),
                AbbrevDays = Seven(dates.AbbreviatedDayNames),
                ShortestDays = Seven(dates.ShortestDayNames),
                Months = Twelve(dates.MonthNames),
                AbbrevMonths = Twelve(dates.AbbreviatedMonthNames),
                ShortDate = dates.ShortDatePattern,
                LongDate = dates.LongDatePattern,
                YearMonth = dates.YearMonthPattern,
                MonthDay = dates.MonthDayPattern,
                TimeFormat = dates.LongTimePattern,
                ShortTime = dates.ShortTimePattern,
                Am = dates.AMDesignator,
                Pm = dates.PMDesignator,
                DateSeparator = dates.DateSeparator,
                TimeSeparator = dates.TimeSeparator,
                FirstDayOfWeek = ((int)dates.FirstDayOfWeek + 6) % 7,
                Decimal = numbers.NumberDecimalSeparator,
                Thousand = numbers.NumberGroupSeparator,
                Grouping = Groups(numbers.NumberGroupSizes),
                Digits = numbers.NumberDecimalDigits,
                NegativeNumber = numbers.NumberNegativePattern,
                NegativeSign = numbers.NegativeSign,
                PositiveSign = numbers.PositiveSign == "+" ? string.Empty : numbers.PositiveSign,
                Currency = numbers.CurrencySymbol,
                IntlSymbol = region?.ISOCurrencySymbol ?? string.Empty,
                MonDecimal = numbers.CurrencyDecimalSeparator,
                MonThousand = numbers.CurrencyGroupSeparator,
                MonGrouping = Groups(numbers.CurrencyGroupSizes),
                CurrencyDigits = numbers.CurrencyDecimalDigits,
                PositiveCurrency = numbers.CurrencyPositivePattern,
                NegativeCurrency = Math.Min(numbers.CurrencyNegativePattern, 15),
                EnglishCurrency = region?.CurrencyEnglishName ?? string.Empty,
                NativeCurrency = region?.CurrencyNativeName ?? string.Empty,
                Measure = region is { IsMetric: false } ? 1 : 0,
                List = text.ListSeparator,
                AnsiCodePage = text.ANSICodePage,
                OemCodePage = text.OEMCodePage,
                MacCodePage = text.MacCodePage,
                EbcdicCodePage = text.EBCDICCodePage,
                NaN = numbers.NaNSymbol,
                PositiveInfinity = numbers.PositiveInfinitySymbol,
                NegativeInfinity = numbers.NegativeInfinitySymbol,
                Percent = numbers.PercentSymbol,
                PerMille = numbers.PerMilleSymbol,
                GeoId = region?.GeoId ?? 0,
                Compare = culture.CompareInfo,
            };
        }

        /// <summary>The locale named by a guest LPCWSTR argument, or null with ERROR_INVALID_PARAMETER set.</summary>
        private LocaleData? LocaleArgument(int argument)
        {
            long pointer = _frame.Arg(argument);
            LocaleData? data = FindLocale(pointer == 0 ? null : Wide(pointer, 85));
            if (data is null)
            {
                LastError = ErrorInvalidParameter;
            }

            return data;
        }

        private void RegisterLocale(Dictionary<string, Action> handlers)
        {
            // int GetUserDefaultLocaleName(LPWSTR, int) / GetSystemDefaultLocaleName.
            handlers["GetUserDefaultLocaleName"] = () => ReturnWide(DeviceLocale, _frame.Arg(0), _frame.Arg(1));
            handlers["GetSystemDefaultLocaleName"] = handlers["GetUserDefaultLocaleName"];

            // LCID GetUserDefaultLCID / GetSystemDefaultLCID / GetThreadLocale; LANGID for the UI.
            handlers["GetUserDefaultLCID"] = () => _frame.Return(DeviceLcid);
            handlers["GetSystemDefaultLCID"] = () => _frame.Return(DeviceLcid);
            handlers["GetThreadLocale"] = () => _frame.Return(DeviceLcid);
            handlers["GetUserDefaultUILanguage"] = () => _frame.Return(DeviceLcid);
            handlers["GetSystemDefaultUILanguage"] = () => _frame.Return(DeviceLcid);
            handlers["GetUserDefaultLangID"] = () => _frame.Return(DeviceLcid);

            // BOOL IsValidLocaleName(LPCWSTR).
            handlers["IsValidLocaleName"] = () => _frame.Return(_frame.Arg(0) != 0 && FindLocale(Wide(_frame.Arg(0), 85)) is not null ? 1 : 0);

            // LCID LocaleNameToLCID(LPCWSTR, DWORD flags): 0 with ERROR_INVALID_PARAMETER for an
            // unknown name. A locale with no LCID of its own is LOCALE_CUSTOM_UNSPECIFIED (0x1000).
            handlers["LocaleNameToLCID"] = () =>
            {
                if ((_frame.Arg(1) & ~0x08000000L) != 0)
                {
                    Fail(ErrorInvalidFlags);
                    return;
                }

                LocaleData? data = LocaleArgument(0);
                _frame.Return(data is null ? 0 : data.Name.Length == 0 ? 0x7F : data.Lcid is 0 or 0x1000 or 0xC00 ? 0x1000 : data.Lcid);
            };

            // int LCIDToLocaleName(LCID, LPWSTR, int, DWORD flags)
            handlers["LCIDToLocaleName"] = () =>
            {
                if (FindLocale((int)_frame.Arg(0)) is not { } data)
                {
                    Fail(ErrorInvalidParameter);
                    return;
                }

                ReturnWide(data.Name, _frame.Arg(1), _frame.Arg(2));
            };

            // int GetLocaleInfoEx(LPCWSTR locale, LCTYPE, LPWSTR data, int chars)
            handlers["GetLocaleInfoEx"] = () =>
            {
                if (LocaleArgument(0) is { } data)
                {
                    ReturnLocaleInfo(data, (uint)_frame.Arg(1), _frame.Arg(2), _frame.Arg(3));
                }
                else
                {
                    _frame.Return(0);
                }
            };

            // int GetLocaleInfoW(LCID, LCTYPE, LPWSTR, int)
            handlers["GetLocaleInfoW"] = () =>
            {
                if (FindLocale((int)_frame.Arg(0)) is { } data)
                {
                    ReturnLocaleInfo(data, (uint)_frame.Arg(1), _frame.Arg(2), _frame.Arg(3));
                }
                else
                {
                    Fail(ErrorInvalidParameter);
                }
            };

            // GEOID GetUserGeoID(GEOCLASS): the United States for GEOCLASS_NATION (16);
            // GEOID_NOT_AVAILABLE (-1) for anything else.
            handlers["GetUserGeoID"] = () => _frame.Return(_frame.Arg(0) == 16 ? 244 : 0xFFFFFFFF);

            // int GetGeoInfoW(GEOID, GEOTYPE, LPWSTR, int, LANGID)
            handlers["GetGeoInfoW"] = GetGeoInfo;

            handlers["GetNumberFormatEx"] = GetNumberFormat;
            handlers["GetCurrencyFormatEx"] = GetCurrencyFormat;
            handlers["GetDateFormatEx"] = GetDateFormat;
            handlers["GetTimeFormatEx"] = GetTimeFormat;

            // int CompareStringEx(LPCWSTR locale, DWORD flags, LPCWCH, int, LPCWCH, int,
            // LPNLSVERSIONINFO, void* reserved, LPARAM): CSTR_LESS_THAN (1), EQUAL (2), GREATER (3).
            handlers["CompareStringEx"] = () =>
            {
                if (_frame.Arg(6) != 0 || _frame.Arg(7) != 0 || _frame.Arg(8) != 0)
                {
                    Fail(ErrorInvalidParameter);
                    return;
                }

                if (LocaleArgument(0) is not { } data)
                {
                    _frame.Return(0);
                    return;
                }

                CompareStrings(data, _frame.Arg(1), _frame.Arg(2), _frame.SignedArg(3), _frame.Arg(4), _frame.SignedArg(5));
            };

            // int CompareStringW(LCID, DWORD, LPCWCH, int, LPCWCH, int)
            handlers["CompareStringW"] = () =>
            {
                if (FindLocale((int)_frame.Arg(0)) is not { } data)
                {
                    Fail(ErrorInvalidParameter);
                    return;
                }

                CompareStrings(data, _frame.Arg(1), _frame.Arg(2), _frame.SignedArg(3), _frame.Arg(4), _frame.SignedArg(5));
            };

            // int CompareStringOrdinal(LPCWCH, int, LPCWCH, int, BOOL ignoreCase)
            handlers["CompareStringOrdinal"] = () =>
            {
                int count1 = _frame.SignedArg(1), count2 = _frame.SignedArg(3);
                if (_frame.Arg(0) == 0 || _frame.Arg(2) == 0 || count1 < -1 || count2 < -1)
                {
                    Fail(ErrorInvalidParameter);
                    return;
                }

                int order = _frame.Arg(4) != 0
                    ? string.Compare(WideCounted(_frame.Arg(0), count1), WideCounted(_frame.Arg(2), count2), StringComparison.OrdinalIgnoreCase)
                    : string.CompareOrdinal(WideCounted(_frame.Arg(0), count1), WideCounted(_frame.Arg(2), count2));
                _frame.Return(Math.Sign(order) + 2);
            };

            // BOOL GetStringTypeExW(LCID, DWORD infoType, LPCWSTR, int, LPWORD) and GetStringTypeW
            // (no LCID).
            handlers["GetStringTypeExW"] = () => GetStringType(_frame.Arg(1), _frame.Arg(2), _frame.SignedArg(3), _frame.Arg(4));
            handlers["GetStringTypeW"] = () => GetStringType(_frame.Arg(0), _frame.Arg(1), _frame.SignedArg(2), _frame.Arg(3));

            handlers["MultiByteToWideChar"] = MultiByteToWideChar;
            handlers["WideCharToMultiByte"] = WideCharToMultiByte;
            handlers["FormatMessageW"] = () => FormatMessage(wide: true);
            handlers["FormatMessageA"] = () => FormatMessage(wide: false);
        }

        // ---------------------------------------------------------------------------
        // GetLocaleInfoEx
        // ---------------------------------------------------------------------------

        private const uint LocaleReturnNumber = 0x20000000;
        private const uint LocaleFlagsMask = 0x80000000 | 0x40000000 | LocaleReturnNumber | 0x10000000 | 0x08000000;

        /// <summary>
        /// The value of one LCTYPE: a string, or (for an I* type) a number - which LOCALE_RETURN_NUMBER
        /// asks for as a DWORD in place of the string. An unknown LCTYPE is ERROR_INVALID_FLAGS.
        /// </summary>
        private static (string? Text, int? Number) LocaleValue(LocaleData d, uint type)
        {
            static string Grouping(int[] g) => g.Length == 1 && g[0] == 0 ? "0" : string.Join(";", g) + ";0";

            if (type is >= 0x2A and <= 0x30)
            {
                return (d.Days[(type - 0x2A + 1) % 7], null);   // SDAYNAME1 is Monday
            }

            if (type is >= 0x31 and <= 0x37)
            {
                return (d.AbbrevDays[(type - 0x31 + 1) % 7], null);
            }

            if (type is >= 0x60 and <= 0x66)
            {
                return (d.ShortestDays[(type - 0x60 + 1) % 7], null);
            }

            if (type is >= 0x38 and <= 0x43)
            {
                return (d.Months[type - 0x38], null);
            }

            if (type is >= 0x44 and <= 0x4F)
            {
                return (d.AbbrevMonths[type - 0x44], null);
            }

            return type switch
            {
                0x01 => (d.Lcid.ToString("x4"), d.Lcid),                                // LOCALE_ILANGUAGE
                0x02 or 0x72 => (d.EnglishDisplay, null),                               // SLOCALIZEDDISPLAYNAME, SENGLISHDISPLAYNAME
                0x73 => (d.NativeDisplay, null),                                        // SNATIVEDISPLAYNAME
                0x6F or 0x1001 => (d.EnglishLanguage, null),                            // SLOCALIZEDLANGUAGENAME, SENGLISHLANGUAGENAME
                0x04 => (d.NativeLanguage, null),                                       // SNATIVELANGUAGENAME
                0x06 or 0x1002 => (d.EnglishCountry, null),                             // SLOCALIZEDCOUNTRYNAME, SENGLISHCOUNTRYNAME
                0x08 => (d.NativeCountry, null),                                        // SNATIVECOUNTRYNAME
                0x05 or 0x0A => (d.Country == "US" ? "1" : "0", d.Country == "US" ? 1 : 0), // ICOUNTRY, IDEFAULTCOUNTRY (dialling code)
                0x0B => (d.OemCodePage.ToString(CultureInfo.InvariantCulture), d.OemCodePage),
                0x1004 => (d.AnsiCodePage.ToString(CultureInfo.InvariantCulture), d.AnsiCodePage),
                0x1011 => (d.MacCodePage.ToString(CultureInfo.InvariantCulture), d.MacCodePage),
                0x1012 => (d.EbcdicCodePage.ToString(CultureInfo.InvariantCulture), d.EbcdicCodePage),
                0x0C => (d.List, null),
                0x0D => (d.Measure.ToString(CultureInfo.InvariantCulture), d.Measure),
                0x0E => (d.Decimal, null),
                0x0F => (d.Thousand, null),
                0x10 => (Grouping(d.Grouping), null),
                0x11 => (d.Digits.ToString(CultureInfo.InvariantCulture), d.Digits),
                0x12 => ("1", 1),                                                        // ILZERO
                0x13 => ("0123456789", null),                                            // SNATIVEDIGITS
                0x14 => (d.Currency, null),
                0x15 => (d.IntlSymbol, null),
                0x16 => (d.MonDecimal, null),
                0x17 => (d.MonThousand, null),
                0x18 => (Grouping(d.MonGrouping), null),
                0x19 or 0x1A => (d.CurrencyDigits.ToString(CultureInfo.InvariantCulture), d.CurrencyDigits),
                0x1B => (d.PositiveCurrency.ToString(CultureInfo.InvariantCulture), d.PositiveCurrency),
                0x1C => (d.NegativeCurrency.ToString(CultureInfo.InvariantCulture), d.NegativeCurrency),
                0x1D => (d.DateSeparator, null),
                0x1E => (d.TimeSeparator, null),
                0x1F => (d.ShortDate, null),
                0x20 => (d.LongDate, null),
                0x21 => (DateOrder(d.ShortDate).ToString(CultureInfo.InvariantCulture), DateOrder(d.ShortDate)),
                0x22 => (DateOrder(d.LongDate).ToString(CultureInfo.InvariantCulture), DateOrder(d.LongDate)),
                0x23 => (d.TimeFormat.Contains('H') ? "1" : "0", d.TimeFormat.Contains('H') ? 1 : 0), // ITIME
                0x24 => ("1", 1),                                                        // ICENTURY
                0x25 => (d.TimeFormat.Contains("hh") || d.TimeFormat.Contains("HH") ? "1" : "0", d.TimeFormat.Contains("hh") || d.TimeFormat.Contains("HH") ? 1 : 0),
                0x26 => (d.ShortDate.Contains("dd") ? "1" : "0", d.ShortDate.Contains("dd") ? 1 : 0),
                0x27 => (d.ShortDate.Contains("MM") ? "1" : "0", d.ShortDate.Contains("MM") ? 1 : 0),
                0x28 => (d.Am, null),
                0x29 => (d.Pm, null),
                0x50 => (d.PositiveSign, null),
                0x51 => (d.NegativeSign, null),
                0x52 or 0x53 => ("3", 3),                                                // IPOSSIGNPOSN, INEGSIGNPOSN
                0x54 or 0x56 => (d.PositiveCurrency is 0 or 2 ? "1" : "0", d.PositiveCurrency is 0 or 2 ? 1 : 0),
                0x55 or 0x57 => (d.PositiveCurrency >= 2 ? "1" : "0", d.PositiveCurrency >= 2 ? 1 : 0),
                0x59 => (d.Language, null),
                0x5A => (d.Country, null),
                0x5B => (d.GeoId.ToString(CultureInfo.InvariantCulture), d.GeoId),       // IGEOID
                0x5C => (d.Name, null),                                                  // SNAME
                0x67 => (d.Language3, null),
                0x68 => (d.Country3, null),
                0x69 => (d.NaN, null),
                0x6A => (d.PositiveInfinity, null),
                0x6B => (d.NegativeInfinity, null),
                0x6C => ("Latn;", null),                                                 // SSCRIPTS
                0x6D => (d.Parent, null),
                0x74 => ("1", 1),                                                        // INEGATIVEPERCENT
                0x75 => ("0", 0),                                                        // IPOSITIVEPERCENT
                0x76 => (d.Percent, null),
                0x77 => (d.PerMille, null),
                0x78 => (d.MonthDay, null),
                0x79 => (d.ShortTime, null),
                0x70 => ("0", 0),                                                        // IREADINGLAYOUT: left to right
                0x71 => (d.Country.Length == 0 ? "1" : "0", d.Country.Length == 0 ? 1 : 0), // INEUTRAL
                0x1003 => (d.TimeFormat, null),
                0x1005 => ("0", 0),                                                      // ITIMEMARKPOSN: suffix
                0x1006 => (d.YearMonth, null),
                0x1007 => (d.EnglishCurrency, null),
                0x1008 => (d.NativeCurrency, null),
                0x1009 => ("1", 1),                                                      // ICALENDARTYPE: Gregorian
                0x100A => (d.Measure == 1 ? "1" : "9", d.Measure == 1 ? 1 : 9),          // IPAPERSIZE: letter / A4
                0x100B => ("0", 0),                                                      // IOPTIONALCALENDAR
                0x100C => (d.FirstDayOfWeek.ToString(CultureInfo.InvariantCulture), d.FirstDayOfWeek),
                0x100D => ("0", 0),                                                      // IFIRSTWEEKOFYEAR
                0x100E or 0x100F => (string.Empty, null),                                // S(ABBREV)MONTHNAME13
                0x1010 => (d.NegativeNumber.ToString(CultureInfo.InvariantCulture), d.NegativeNumber),
                0x1014 => ("1", 1),                                                      // IDIGITSUBSTITUTION: none
                _ => (null, null),
            };
        }

        /// <summary>IDATE/ILDATE: 0 month-day-year, 1 day-month-year, 2 year-month-day.</summary>
        private static int DateOrder(string pattern)
        {
            int day = pattern.IndexOf('d'), month = pattern.IndexOf('M'), year = pattern.IndexOf('y');
            return year >= 0 && year < month ? 2 : day >= 0 && day < month ? 1 : 0;
        }

        private void ReturnLocaleInfo(LocaleData data, uint type, long buffer, long capacity)
        {
            if ((int)capacity < 0 || (capacity > 0 && buffer == 0))
            {
                Fail(ErrorInvalidParameter);
                return;
            }

            (string? text, int? number) = LocaleValue(data, type & ~LocaleFlagsMask);
            if (text is null)
            {
                Fail(ErrorInvalidFlags);
                return;
            }

            if ((type & LocaleReturnNumber) != 0)
            {
                if (number is null)
                {
                    Fail(ErrorInvalidFlags);
                    return;
                }

                // The DWORD occupies two WCHARs of the buffer.
                if (capacity == 0)
                {
                    _frame.Return(2);
                    return;
                }

                if (capacity < 2)
                {
                    Fail(ErrorInsufficientBuffer);
                    return;
                }

                _emulator.WriteUInt32(buffer, (uint)number.Value);
                _frame.Return(2);
                return;
            }

            ReturnWide(text, buffer, capacity);
        }

        // ---------------------------------------------------------------------------
        // GetGeoInfo
        // ---------------------------------------------------------------------------

        private void GetGeoInfo()
        {
            int geoId = (int)_frame.Arg(0);
            long type = _frame.Arg(1), buffer = _frame.Arg(2), capacity = _frame.Arg(3);
            string? value = null;
            if (geoId == 244)
            {
                value = type switch
                {
                    1 => "244", 2 => "39.45", 3 => "-98.908", 4 => "US", 5 => "USA", 6 => "en-US", 7 => "00000409",
                    8 => "United States", 9 => "United States of America", 12 => "840", 13 => "39070",
                    _ => null,
                };
            }
            else if (RegionByGeoId(geoId) is { } region)
            {
                value = type switch
                {
                    1 => geoId.ToString(CultureInfo.InvariantCulture), 4 => region.TwoLetterISORegionName, 5 => region.ThreeLetterISORegionName,
                    8 => region.EnglishName,
                    _ => null,
                };
            }
            else
            {
                Fail(ErrorInvalidParameter);
                return;
            }

            if (value is null)
            {
                // A fact about that region this host has no data for.
                Fail(ErrorInvalidFlags);
                return;
            }

            ReturnWide(value, buffer, capacity);
        }

        private static RegionInfo? RegionByGeoId(int geoId)
        {
            try
            {
                foreach (CultureInfo culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
                {
                    try
                    {
                        var region = new RegionInfo(culture.Name);
                        if (region.GeoId == geoId)
                        {
                            return region;
                        }
                    }
                    catch (Exception)
                    {
                        // A culture with no region: skip it.
                    }
                }
            }
            catch (Exception)
            {
                // No culture data on this host.
            }

            return null;
        }

        // ---------------------------------------------------------------------------
        // number and currency formatting
        // ---------------------------------------------------------------------------

        /// <summary>The digits of a GetNumberFormat value, rounded half away from zero to <paramref name="decimals"/> places.</summary>
        private static bool TryRound(string value, int decimals, out bool negative, out string integer, out string fraction)
        {
            negative = false;
            integer = fraction = string.Empty;
            string text = value;
            if (text.StartsWith('-'))
            {
                negative = true;
                text = text[1..];
            }

            int point = text.IndexOf('.');
            string whole = point < 0 ? text : text[..point];
            string part = point < 0 ? string.Empty : text[(point + 1)..];
            if ((whole.Length == 0 && part.Length == 0) || !whole.All(char.IsAsciiDigit) || !part.All(char.IsAsciiDigit))
            {
                return false;
            }

            if (whole.Length == 0)
            {
                whole = "0";
            }

            bool roundUp = part.Length > decimals && part[decimals] >= '5';
            char[] digits = (whole + part.PadRight(decimals, '0')[..decimals]).ToCharArray();
            if (roundUp)
            {
                int i = digits.Length - 1;
                while (i >= 0 && digits[i] == '9')
                {
                    digits[i--] = '0';
                }

                string carried = new(digits);
                if (i < 0)
                {
                    carried = "1" + carried;
                }
                else
                {
                    digits[i]++;
                    carried = new string(digits);
                }

                digits = carried.ToCharArray();
            }

            string all = new(digits);
            integer = all[..(all.Length - decimals)].TrimStart('0');
            fraction = all[(all.Length - decimals)..];
            if (integer.Length == 0)
            {
                integer = "0";
            }

            if (negative && integer == "0" && fraction.All(c => c == '0'))
            {
                negative = false;
            }

            return true;
        }

        /// <summary>Windows' Grouping word: each decimal digit a group size; a trailing 0 stops repetition.</summary>
        private static (int[] Sizes, bool Repeat) GroupingWord(long word)
        {
            if (word == 0)
            {
                return ([], false);
            }

            string digits = word.ToString(CultureInfo.InvariantCulture);
            bool repeat = !digits.EndsWith('0');
            return (digits.TrimEnd('0').Select(c => c - '0').ToArray(), repeat);
        }

        private static string Group(string integer, int[] sizes, bool repeat, string separator)
        {
            if (sizes.Length == 0 || sizes[0] == 0)
            {
                return integer;
            }

            var groups = new List<string>();
            int end = integer.Length;
            int index = 0;
            while (end > 0)
            {
                int size = index < sizes.Length ? sizes[index] : repeat ? sizes[^1] : end;
                if (size <= 0)
                {
                    size = end;
                }

                int start = Math.Max(0, end - size);
                groups.Insert(0, integer[start..end]);
                end = start;
                index++;
            }

            return string.Join(separator, groups);
        }

        private sealed record NumberShape(int Digits, bool LeadingZero, int[] Grouping, bool Repeat, string Decimal, string Thousand, int Negative, int Positive, string Currency);

        /// <summary>Reads NUMBERFMTW (currency: false) or CURRENCYFMTW, or takes the locale's own.</summary>
        private NumberShape? ReadShape(long format, LocaleData locale, bool currency)
        {
            if (format == 0)
            {
                return currency
                    ? new NumberShape(locale.CurrencyDigits, true, locale.MonGrouping, true, locale.MonDecimal, locale.MonThousand, locale.NegativeCurrency, locale.PositiveCurrency, locale.Currency)
                    : new NumberShape(locale.Digits, true, locale.Grouping, true, locale.Decimal, locale.Thousand, locale.NegativeNumber, 0, string.Empty);
            }

            uint digits = _emulator.ReadUInt32(format), leading = _emulator.ReadUInt32(format + 4), grouping = _emulator.ReadUInt32(format + 8);
            long decimalSep = _emulator.ReadUInt32(format + 12), thousandSep = _emulator.ReadUInt32(format + 16);
            uint negative = _emulator.ReadUInt32(format + 20);
            uint positive = currency ? _emulator.ReadUInt32(format + 24) : 0;
            long symbol = currency ? _emulator.ReadUInt32(format + 28) : 0;
            if (decimalSep == 0 || thousandSep == 0 || digits > 9 || leading > 1 ||
                (currency ? negative > 15 || positive > 3 || symbol == 0 : negative > 4))
            {
                return null;
            }

            (int[] sizes, bool repeat) = GroupingWord(grouping);
            return new NumberShape((int)digits, leading == 1, sizes, repeat, Wide(decimalSep, 8), Wide(thousandSep, 8), (int)negative, (int)positive, currency ? Wide(symbol, 16) : string.Empty);
        }

        private string? FormatNumber(string value, NumberShape shape, bool currency)
        {
            if (!TryRound(value, shape.Digits, out bool negative, out string integer, out string fraction))
            {
                return null;
            }

            if (integer == "0" && !shape.LeadingZero && shape.Digits > 0)
            {
                integer = string.Empty;
            }

            string n = Group(integer, shape.Grouping, shape.Repeat, shape.Thousand) + (shape.Digits > 0 ? shape.Decimal + fraction : string.Empty);
            if (!currency)
            {
                return !negative ? n : shape.Negative switch
                {
                    0 => $"({n})",
                    1 => $"-{n}",
                    2 => $"- {n}",
                    3 => $"{n}-",
                    _ => $"{n} -",
                };
            }

            string s = shape.Currency;
            if (!negative)
            {
                return shape.Positive switch
                {
                    0 => s + n,
                    1 => n + s,
                    2 => $"{s} {n}",
                    _ => $"{n} {s}",
                };
            }

            return shape.Negative switch
            {
                0 => $"({s}{n})",
                1 => $"-{s}{n}",
                2 => $"{s}-{n}",
                3 => $"{s}{n}-",
                4 => $"({n}{s})",
                5 => $"-{n}{s}",
                6 => $"{n}-{s}",
                7 => $"{n}{s}-",
                8 => $"-{n} {s}",
                9 => $"-{s} {n}",
                10 => $"{n} {s}-",
                11 => $"{s} {n}-",
                12 => $"{s} -{n}",
                13 => $"{n}- {s}",
                14 => $"({s} {n})",
                _ => $"({n} {s})",
            };
        }

        /// <summary>
        /// int GetNumberFormatEx(LPCWSTR locale, DWORD flags, LPCWSTR value, const NUMBERFMTW*,
        /// LPWSTR, int). The value is "[-]digits[.digits]"; anything else is
        /// ERROR_INVALID_PARAMETER. LOCALE_NOUSEROVERRIDE is the only flag, and only without a format.
        /// </summary>
        private void GetNumberFormat() => FormatValue(currency: false);

        /// <summary>int GetCurrencyFormatEx(LPCWSTR locale, DWORD flags, LPCWSTR value, const CURRENCYFMTW*, LPWSTR, int).</summary>
        private void GetCurrencyFormat() => FormatValue(currency: true);

        private void FormatValue(bool currency)
        {
            long flags = _frame.Arg(1), format = _frame.Arg(3);
            if ((flags & ~0x80000000L) != 0 || (flags != 0 && format != 0))
            {
                Fail(ErrorInvalidFlags);
                return;
            }

            if (LocaleArgument(0) is not { } locale)
            {
                _frame.Return(0);
                return;
            }

            NumberShape? shape = ReadShape(format, locale, currency);
            string? text = shape is null || _frame.Arg(2) == 0 ? null : FormatNumber(Wide(_frame.Arg(2), 512), shape, currency);
            if (text is null)
            {
                Fail(ErrorInvalidParameter);
                return;
            }

            ReturnWide(text, _frame.Arg(4), _frame.Arg(5));
        }

        // ---------------------------------------------------------------------------
        // date and time formatting
        // ---------------------------------------------------------------------------

        private const long DateShort = 0x1, DateLong = 0x2, DateAltCalendar = 0x4, DateYearMonth = 0x8,
            DateLtr = 0x10, DateRtl = 0x20, DateAutoLayout = 0x40, DateMonthDay = 0x80;

        /// <summary>
        /// int GetDateFormatEx(LPCWSTR locale, DWORD flags, const SYSTEMTIME* (NULL: now, local),
        /// LPCWSTR picture, LPWSTR, int, LPCWSTR calendar). A picture and a format flag together,
        /// or two format flags, are ERROR_INVALID_FLAGS; the calendar must be NULL.
        /// </summary>
        private void GetDateFormat()
        {
            long flags = _frame.Arg(1) & ~0x80000000L;
            long picture = _frame.Arg(3);
            long formats = flags & (DateShort | DateLong | DateYearMonth | DateMonthDay);
            if ((flags & ~(DateShort | DateLong | DateAltCalendar | DateYearMonth | DateLtr | DateRtl | DateAutoLayout | DateMonthDay)) != 0 ||
                (formats & (formats - 1)) != 0 || (picture != 0 && formats != 0) || (flags & DateLtr) != 0 && (flags & DateRtl) != 0)
            {
                Fail(ErrorInvalidFlags);
                return;
            }

            if (_frame.Arg(6) != 0)
            {
                Fail(ErrorInvalidParameter);
                return;
            }

            if (LocaleArgument(0) is not { } locale)
            {
                _frame.Return(0);
                return;
            }

            SystemTime time = _frame.Arg(2) == 0 ? FromDateTime(DateTime.Now) : ReadSystemTime(_frame.Arg(2));
            if (!IsValid(time))
            {
                Fail(ErrorInvalidParameter);
                return;
            }

            string pattern = picture != 0 ? Wide(picture, 512) : formats switch
            {
                DateLong => locale.LongDate,
                DateYearMonth => locale.YearMonth,
                DateMonthDay => locale.MonthDay,
                _ => locale.ShortDate,
            };

            ReturnWide(FormatDate(pattern, locale, time), _frame.Arg(4), _frame.Arg(5));
        }

        /// <summary>
        /// A Windows date picture: d dd ddd dddd, M MM MMM MMMM, y yy yyyy (yyy and longer are the
        /// full year), g/gg the era, '...' quoted text ('' a quote). Every other character is literal.
        /// </summary>
        private static string FormatDate(string picture, LocaleData locale, SystemTime t)
        {
            int dayOfWeek = (int)((DaysFromCivil(t.Year, t.Month, t.Day) + 1) % 7);
            var output = new StringBuilder();
            for (int i = 0; i < picture.Length;)
            {
                char c = picture[i];
                if (c == '\'')
                {
                    i = CopyQuoted(picture, i, output);
                    continue;
                }

                int run = 1;
                while (i + run < picture.Length && picture[i + run] == c)
                {
                    run++;
                }

                switch (c)
                {
                    case 'd':
                        output.Append(run switch
                        {
                            1 => t.Day.ToString(CultureInfo.InvariantCulture),
                            2 => t.Day.ToString("00", CultureInfo.InvariantCulture),
                            3 => locale.AbbrevDays[dayOfWeek],
                            _ => locale.Days[dayOfWeek],
                        });
                        break;
                    case 'M':
                        output.Append(run switch
                        {
                            1 => t.Month.ToString(CultureInfo.InvariantCulture),
                            2 => t.Month.ToString("00", CultureInfo.InvariantCulture),
                            3 => locale.AbbrevMonths[t.Month - 1],
                            _ => locale.Months[t.Month - 1],
                        });
                        break;
                    case 'y':
                        output.Append(run switch
                        {
                            1 => (t.Year % 100).ToString(CultureInfo.InvariantCulture),
                            2 => (t.Year % 100).ToString("00", CultureInfo.InvariantCulture),
                            _ => t.Year.ToString(CultureInfo.InvariantCulture),
                        });
                        break;
                    case 'g':
                        output.Append("A.D.");
                        break;
                    default:
                        output.Append(c, run);
                        break;
                }

                i += run;
            }

            return output.ToString();
        }

        private static int CopyQuoted(string picture, int start, StringBuilder output)
        {
            int i = start + 1;
            while (i < picture.Length)
            {
                if (picture[i] == '\'')
                {
                    if (i + 1 < picture.Length && picture[i + 1] == '\'')
                    {
                        output.Append('\'');
                        i += 2;
                        continue;
                    }

                    return i + 1;
                }

                output.Append(picture[i++]);
            }

            return i;
        }

        private const long TimeNoMinutesOrSeconds = 0x1, TimeNoSeconds = 0x2, TimeNoTimeMarker = 0x4, TimeForce24Hour = 0x8;

        /// <summary>
        /// int GetTimeFormatEx(LPCWSTR locale, DWORD flags, const SYSTEMTIME* (NULL: now, local),
        /// LPCWSTR picture, LPWSTR, int). Picture: h hh (12-hour) H HH, m mm, s ss, t tt; '...'
        /// quoted. TIME_NOSECONDS / TIME_NOMINUTESORSECONDS drop those fields with the separator
        /// before them, TIME_NOTIMEMARKER the marker with the space before it, and
        /// TIME_FORCE24HOURFORMAT turns h into H.
        /// </summary>
        private void GetTimeFormat()
        {
            long flags = _frame.Arg(1) & ~0x80000000L;
            if ((flags & ~(TimeNoMinutesOrSeconds | TimeNoSeconds | TimeNoTimeMarker | TimeForce24Hour)) != 0)
            {
                Fail(ErrorInvalidFlags);
                return;
            }

            if (LocaleArgument(0) is not { } locale)
            {
                _frame.Return(0);
                return;
            }

            SystemTime time = _frame.Arg(2) == 0 ? FromDateTime(DateTime.Now) : ReadSystemTime(_frame.Arg(2));
            if (time.Hour is < 0 or > 23 || time.Minute is < 0 or > 59 || time.Second is < 0 or > 59 || time.Milliseconds is < 0 or > 999)
            {
                Fail(ErrorInvalidParameter);
                return;
            }

            string picture = _frame.Arg(3) != 0 ? Wide(_frame.Arg(3), 512) : locale.TimeFormat;
            ReturnWide(FormatTime(picture, locale, time, flags), _frame.Arg(4), _frame.Arg(5));
        }

        private static string FormatTime(string picture, LocaleData locale, SystemTime t, long flags)
        {
            var output = new StringBuilder();
            int pendingLiteral = 0;   // where the literal text since the last field starts in output
            for (int i = 0; i < picture.Length;)
            {
                char c = picture[i];
                if (c == '\'')
                {
                    i = CopyQuoted(picture, i, output);
                    continue;
                }

                int run = 1;
                while (i + run < picture.Length && picture[i + run] == c)
                {
                    run++;
                }

                bool drop = c switch
                {
                    'm' => (flags & TimeNoMinutesOrSeconds) != 0,
                    's' => (flags & (TimeNoMinutesOrSeconds | TimeNoSeconds)) != 0,
                    't' => (flags & TimeNoTimeMarker) != 0,
                    _ => false,
                };

                if (c is 'h' or 'H' or 'm' or 's' or 't')
                {
                    if (drop)
                    {
                        output.Length = pendingLiteral;
                        i += run;
                        continue;
                    }

                    bool twentyFour = c == 'H' || (c == 'h' && (flags & TimeForce24Hour) != 0);
                    int hour12 = t.Hour % 12 == 0 ? 12 : t.Hour % 12;
                    output.Append(c switch
                    {
                        'h' or 'H' => (twentyFour ? t.Hour : hour12).ToString(run >= 2 ? "00" : "0", CultureInfo.InvariantCulture),
                        'm' => t.Minute.ToString(run >= 2 ? "00" : "0", CultureInfo.InvariantCulture),
                        's' => t.Second.ToString(run >= 2 ? "00" : "0", CultureInfo.InvariantCulture),
                        _ => run == 1 ? Marker()[..Math.Min(1, Marker().Length)] : Marker(),
                    });
                    pendingLiteral = output.Length;
                    i += run;
                    continue;
                }

                output.Append(c, run);
                i += run;
            }

            return output.ToString();

            string Marker() => t.Hour < 12 ? locale.Am : locale.Pm;
        }

        // ---------------------------------------------------------------------------
        // comparison and character types
        // ---------------------------------------------------------------------------

        private const long NormIgnoreCase = 0x1, NormIgnoreNonSpace = 0x2, NormIgnoreSymbols = 0x4, SortDigitsAsNumbers = 0x8,
            LinguisticIgnoreCase = 0x10, LinguisticIgnoreDiacritic = 0x20, SortStringSort = 0x1000,
            NormIgnoreKanaType = 0x10000, NormIgnoreWidth = 0x20000, NormLinguisticCasing = 0x8000000;

        private void CompareStrings(LocaleData locale, long flags, long first, int firstCount, long second, int secondCount)
        {
            const long allowed = NormIgnoreCase | NormIgnoreNonSpace | NormIgnoreSymbols | SortDigitsAsNumbers | LinguisticIgnoreCase |
                                 LinguisticIgnoreDiacritic | SortStringSort | NormIgnoreKanaType | NormIgnoreWidth | NormLinguisticCasing;
            if ((flags & ~allowed) != 0)
            {
                Fail(ErrorInvalidFlags);
                return;
            }

            if (first == 0 || second == 0 || firstCount < -1 || secondCount < -1)
            {
                Fail(ErrorInvalidParameter);
                return;
            }

            CompareOptions options = CompareOptions.None;
            if ((flags & (NormIgnoreCase | LinguisticIgnoreCase)) != 0) options |= CompareOptions.IgnoreCase;
            if ((flags & (NormIgnoreNonSpace | LinguisticIgnoreDiacritic)) != 0) options |= CompareOptions.IgnoreNonSpace;
            if ((flags & NormIgnoreSymbols) != 0) options |= CompareOptions.IgnoreSymbols;
            if ((flags & NormIgnoreKanaType) != 0) options |= CompareOptions.IgnoreKanaType;
            if ((flags & NormIgnoreWidth) != 0) options |= CompareOptions.IgnoreWidth;
            if ((flags & SortStringSort) != 0) options |= CompareOptions.StringSort;
            if ((flags & SortDigitsAsNumbers) != 0) options |= (CompareOptions)0x20;   // CompareOptions.NumericOrdering (.NET 10); the net8 probe build has no name for it

            string a = WideCounted(first, firstCount), b = WideCounted(second, secondCount);
            int order;
            try
            {
                order = (locale.Compare ?? CultureInfo.InvariantCulture.CompareInfo).Compare(a, b, options);
            }
            catch (Exception)
            {
                // A host without linguistic data (invariant globalisation) compares ordinally.
                order = string.Compare(a, b, (options & CompareOptions.IgnoreCase) != 0 ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }

            _frame.Return(Math.Sign(order) + 2);
        }

        private const long CType1 = 1, CType2 = 2, CType3 = 4;

        /// <summary>
        /// BOOL GetStringTypeExW(LCID, DWORD infoType, LPCWSTR, int count, LPWORD types): one WORD
        /// of CT_CTYPE1, CT_CTYPE2 or CT_CTYPE3 bits per character (a count of -1 is NUL-terminated,
        /// the NUL included). The types come from the Unicode category, which is what NLS uses.
        /// </summary>
        private void GetStringType(long infoType, long source, int count, long types)
        {
            if (infoType is not (CType1 or CType2 or CType3))
            {
                Fail(ErrorInvalidFlags);
                return;
            }

            if (source == 0 || types == 0 || count < -1 || count == 0 || source == types)
            {
                Fail(ErrorInvalidParameter);
                return;
            }

            string text = count < 0 ? Wide(source, 1 << 20) + "\0" : WideCounted(source, count);
            byte[] output = new byte[text.Length * 2];
            for (int i = 0; i < text.Length; i++)
            {
                ushort value = infoType switch
                {
                    CType1 => CharType1(text[i]),
                    CType2 => CharType2(text[i]),
                    _ => CharType3(text[i]),
                };
                BitConverter.GetBytes(value).CopyTo(output, i * 2);
            }

            _emulator.WriteMemory(types, output);
            _frame.Return(1);
        }

        private static ushort CharType1(char c)
        {
            const ushort upper = 0x1, lower = 0x2, digit = 0x4, space = 0x8, punct = 0x10, control = 0x20, blank = 0x40, xdigit = 0x80, alpha = 0x100, defined = 0x200;
            UnicodeCategory category = char.GetUnicodeCategory(c);
            if (category == UnicodeCategory.OtherNotAssigned)
            {
                return 0;
            }

            ushort bits = defined;
            if (char.IsUpper(c)) bits |= upper | alpha;
            if (char.IsLower(c)) bits |= lower | alpha;
            if (char.IsLetter(c)) bits |= alpha;
            if (category == UnicodeCategory.DecimalDigitNumber) bits |= digit;
            if (char.IsWhiteSpace(c)) bits |= space;
            if (char.IsPunctuation(c) || char.IsSymbol(c)) bits |= punct;
            if (char.IsControl(c)) bits |= control;
            if (c is ' ' or '\t' or ' ' or '　') bits |= blank;
            if (char.IsAsciiHexDigit(c)) bits |= xdigit;
            return bits;
        }

        /// <summary>CT_CTYPE2: the bidirectional class, from the character's script and category.</summary>
        private static ushort CharType2(char c)
        {
            if (c is >= '0' and <= '9') return 3;                         // C2_EUROPENUMBER
            if (c is '+' or '-') return 4;                                // C2_EUROPESEPARATOR
            if (c is '#' or '$' or '%' or '¢' or '£' or '¥' or '°' or '€') return 5; // C2_EUROPETERMINATOR
            if (c is >= '٠' and <= '٩') return 6;               // C2_ARABICNUMBER
            if (c is ',' or '.' or ':' or '/' or ' ') return 7;      // C2_COMMONSEPARATOR
            if (c is '\n' or '\r' or '\u2029' or '\u001C' or '\u001D' or '\u001E' or '\u0085') return 8; // C2_BLOCKSEPARATOR
            if (c is '\t' or '\u000B' or '\u001F') return 9;              // C2_SEGMENTSEPARATOR
            if (char.IsWhiteSpace(c)) return 10;                          // C2_WHITESPACE
            if (c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿') return 2; // C2_RIGHTTOLEFT
            if (char.IsLetter(c) || char.GetUnicodeCategory(c) is UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber) return 1; // C2_LEFTTORIGHT
            if (char.GetUnicodeCategory(c) is UnicodeCategory.OtherNotAssigned or UnicodeCategory.Control) return 0; // C2_NOTAPPLICABLE
            return 11;                                                    // C2_OTHERNEUTRAL
        }

        private static ushort CharType3(char c)
        {
            const ushort nonSpacing = 0x1, diacritic = 0x2, symbol = 0x8, katakana = 0x10, hiragana = 0x20, halfWidth = 0x40, fullWidth = 0x80, ideograph = 0x100, alpha = 0x8000, high = 0x800, low = 0x1000;
            UnicodeCategory category = char.GetUnicodeCategory(c);
            ushort bits = 0;
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark) bits |= nonSpacing | diacritic;
            if (category == UnicodeCategory.ModifierSymbol) bits |= diacritic;
            if (char.IsSymbol(c) || char.IsPunctuation(c)) bits |= symbol;
            if (c is >= '゠' and <= 'ヿ') bits |= katakana;
            if (c is >= '぀' and <= 'ゟ') bits |= hiragana;
            if (c is >= '｡' and <= 'ￜ') bits |= halfWidth;
            if (c is >= '！' and <= '｠' or >= '　' and <= 'ヿ' or >= '一' and <= '鿿') bits |= fullWidth;
            if (c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿' or >= '豈' and <= '﫿') bits |= ideograph;
            if (char.IsLetter(c)) bits |= alpha;
            if (char.IsHighSurrogate(c)) bits |= high;
            if (char.IsLowSurrogate(c)) bits |= low;
            return bits;
        }

        // ---------------------------------------------------------------------------
        // code pages
        // ---------------------------------------------------------------------------

        /// <summary>CP_ACP (0) and CP_THREAD_ACP (3) are 1252, CP_OEMCP (1) 437, CP_MACCP (2) 10000.</summary>
        private static Encoding? CodePage(long codePage, EncoderFallback encoder, DecoderFallback decoder)
        {
            int resolved = codePage switch
            {
                0 or 3 => 1252,
                1 => 437,
                2 => 10000,
                _ => (int)codePage,
            };

            try
            {
                return resolved switch
                {
                    65001 => new UTF8Encoding(false, decoder is DecoderExceptionFallback),
                    _ => Encoding.GetEncoding(resolved, encoder, decoder),
                };
            }
            catch (Exception)
            {
                return resolved == 1252 ? Encoding.Latin1 : null;
            }
        }

        private const long MbErrInvalidChars = 0x8, WcErrInvalidChars = 0x80;

        /// <summary>
        /// int MultiByteToWideChar(UINT codePage, DWORD flags, LPCCH, int bytes, LPWSTR, int chars).
        /// bytes -1 is NUL-terminated (the NUL converted too); a capacity of 0 asks for the size.
        /// MB_ERR_INVALID_CHARS turns an invalid sequence into ERROR_NO_UNICODE_TRANSLATION; UTF-8
        /// takes no other flag.
        /// </summary>
        private void MultiByteToWideChar()
        {
            long codePage = _frame.Arg(0), flags = _frame.Arg(1), source = _frame.Arg(2), destination = _frame.Arg(4);
            int bytes = _frame.SignedArg(3), room = _frame.SignedArg(5);
            if (source == 0 || bytes == 0 || bytes < -1 || room < 0 || (room > 0 && destination == 0))
            {
                Fail(ErrorInvalidParameter);
                return;
            }

            if ((codePage == 65001 && (flags & ~MbErrInvalidChars) != 0) || (flags & ~0xFL) != 0)
            {
                Fail(ErrorInvalidFlags);
                return;
            }

            bool strict = (flags & MbErrInvalidChars) != 0;
            Encoding? encoding = CodePage(codePage, EncoderFallback.ReplacementFallback, strict ? DecoderFallback.ExceptionFallback : DecoderFallback.ReplacementFallback);
            if (encoding is null)
            {
                Fail(ErrorInvalidParameter);
                return;
            }

            byte[] raw = bytes < 0 ? [.. NarrowBytes(source), 0] : _emulator.ReadMemory(source, bytes);
            char[] chars;
            try
            {
                chars = encoding.GetChars(raw);
            }
            catch (DecoderFallbackException)
            {
                Fail(ErrorNoUnicodeTranslation);
                return;
            }

            if (room == 0)
            {
                _frame.Return(chars.Length);
                return;
            }

            if (chars.Length > room)
            {
                Fail(ErrorInsufficientBuffer);
                return;
            }

            _emulator.WriteMemory(destination, Encoding.Unicode.GetBytes(chars));
            _frame.Return(chars.Length);
        }

        private byte[] NarrowBytes(long address)
        {
            var bytes = new List<byte>();
            while (bytes.Count < (1 << 24))
            {
                byte[] block = _emulator.ReadMemory(address + bytes.Count, 64);
                int end = Array.IndexOf(block, (byte)0);
                if (end >= 0)
                {
                    bytes.AddRange(block[..end]);
                    break;
                }

                bytes.AddRange(block);
            }

            return bytes.ToArray();
        }

        /// <summary>
        /// int WideCharToMultiByte(UINT codePage, DWORD flags, LPCWCH, int chars, LPSTR, int bytes,
        /// LPCCH defaultChar, LPBOOL usedDefault). For UTF-8 the last two must be NULL and only
        /// WC_ERR_INVALID_CHARS is accepted (a lone surrogate then fails with
        /// ERROR_NO_UNICODE_TRANSLATION, otherwise it becomes U+FFFD). For a code page, characters
        /// it cannot hold become the default character ('?' unless given) and *usedDefault says so;
        /// no best-fit mapping is applied, which is WC_NO_BEST_FIT_CHARS' behaviour.
        /// </summary>
        private void WideCharToMultiByte()
        {
            long codePage = _frame.Arg(0), flags = _frame.Arg(1), source = _frame.Arg(2), destination = _frame.Arg(4);
            int chars = _frame.SignedArg(3), room = _frame.SignedArg(5);
            long defaultChar = _frame.Arg(6), usedDefault = _frame.Arg(7);
            bool utf = codePage is 65001 or 65000;
            if (source == 0 || chars == 0 || chars < -1 || room < 0 || (room > 0 && destination == 0) ||
                (utf && (defaultChar != 0 || usedDefault != 0)))
            {
                Fail(ErrorInvalidParameter);
                return;
            }

            if ((utf && (flags & ~WcErrInvalidChars) != 0) || (!utf && (flags & WcErrInvalidChars) != 0))
            {
                Fail(ErrorInvalidFlags);
                return;
            }

            string text = chars < 0 ? Wide(source, 1 << 20) + "\0" : WideCounted(source, chars);
            byte[] output;
            if (utf)
            {
                try
                {
                    output = new UTF8Encoding(false, (flags & WcErrInvalidChars) != 0).GetBytes(text);
                }
                catch (EncoderFallbackException)
                {
                    Fail(ErrorNoUnicodeTranslation);
                    return;
                }
            }
            else
            {
                string replacement = defaultChar == 0 ? "?" : ((char)_emulator.ReadMemory(defaultChar, 1)[0]).ToString();
                Encoding? strict = CodePage(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
                Encoding? lenient = CodePage(codePage, new EncoderReplacementFallback(replacement), DecoderFallback.ReplacementFallback);
                if (strict is null || lenient is null)
                {
                    Fail(ErrorInvalidParameter);
                    return;
                }

                bool used = false;
                try
                {
                    output = strict.GetBytes(text);
                }
                catch (EncoderFallbackException)
                {
                    used = true;
                    output = lenient.GetBytes(text);
                }

                if (usedDefault != 0)
                {
                    _emulator.WriteUInt32(usedDefault, used ? 1u : 0u);
                }
            }

            if (room == 0)
            {
                _frame.Return(output.Length);
                return;
            }

            if (output.Length > room)
            {
                Fail(ErrorInsufficientBuffer);
                return;
            }

            _emulator.WriteMemory(destination, output);
            _frame.Return(output.Length);
        }

        // ---------------------------------------------------------------------------
        // FormatMessage
        // ---------------------------------------------------------------------------

        private const long FormatAllocate = 0x100, FormatIgnoreInserts = 0x200, FormatFromString = 0x400,
            FormatFromModule = 0x800, FormatFromSystem = 0x1000, FormatArgumentArray = 0x2000, FormatMaxWidth = 0xFF;

        /// <summary>The system message table for the codes a game meets, in en-US.</summary>
        private static readonly Dictionary<uint, string> SystemMessages = new()
        {
            [0] = "The operation completed successfully.",
            [1] = "Incorrect function.",
            [2] = "The system cannot find the file specified.",
            [3] = "The system cannot find the path specified.",
            [4] = "The system cannot open the file.",
            [5] = "Access is denied.",
            [6] = "The handle is invalid.",
            [8] = "Not enough memory resources are available to process this command.",
            [13] = "The data is invalid.",
            [14] = "Not enough memory resources are available to complete this operation.",
            [18] = "There are no more files.",
            [32] = "The process cannot access the file because it is being used by another process.",
            [33] = "The process cannot access the file because another process has locked a portion of the file.",
            [38] = "Reached the end of the file.",
            [50] = "The request is not supported.",
            [80] = "The file exists.",
            [87] = "The parameter is incorrect.",
            [112] = "There is not enough space on the disk.",
            [122] = "The data area passed to a system call is too small.",
            [123] = "The filename, directory name, or volume label syntax is incorrect.",
            [145] = "The directory is not empty.",
            [183] = "Cannot create a file when that file already exists.",
            [234] = "More data is available.",
            [258] = "The wait operation timed out.",
            [998] = "Invalid access to memory location.",
            [1004] = "Invalid flags.",
            [1460] = "This operation returned because the timeout period expired.",
            [0x80004001] = "Not implemented",
            [0x80004002] = "No such interface supported",
            [0x80004003] = "Invalid pointer",
            [0x80004004] = "Operation aborted",
            [0x80004005] = "Unspecified error",
            [0x8000FFFF] = "Catastrophic failure",
            [0x80040154] = "Class not registered",
        };

        /// <summary>A system message's text; HRESULT_FROM_WIN32 codes share the Win32 code's.</summary>
        private static string? SystemMessage(uint id)
        {
            if (SystemMessages.TryGetValue(id, out string? text))
            {
                return text + "\r\n";
            }

            if ((id & 0xFFFF0000) == 0x80070000 && SystemMessages.TryGetValue(id & 0xFFFF, out text))
            {
                return text + "\r\n";
            }

            return null;
        }

        /// <summary>
        /// DWORD FormatMessage(DWORD flags, const void* source, DWORD id, DWORD language, LPTSTR
        /// buffer, DWORD size, va_list* arguments). The message is the string given
        /// (FORMAT_MESSAGE_FROM_STRING) or the system table's; there are no module message tables
        /// here, so FROM_HMODULE alone finds nothing. Inserts %1..%99 with an optional !printf!
        /// format, %0 %n %r %t %% and friends; FORMAT_MESSAGE_ALLOCATE_BUFFER hands back a heap block.
        /// Returns the characters written without the NUL, or 0 with the last-error set.
        /// </summary>
        private void FormatMessage(bool wide)
        {
            long flags = _frame.Arg(0), source = _frame.Arg(1), buffer = _frame.Arg(4), size = _frame.Arg(5), arguments = _frame.Arg(6);
            uint id = (uint)_frame.Arg(2);

            string? message;
            if ((flags & FormatFromString) != 0)
            {
                message = source == 0 ? null : wide ? Wide(source, 1 << 16) : _frame.ReadNarrowString(source, 1 << 16);
                if (message is null)
                {
                    Fail(ErrorInvalidParameter);
                    return;
                }
            }
            else if ((flags & FormatFromSystem) != 0)
            {
                message = SystemMessage(id);
                if (message is null)
                {
                    Fail(ErrorMessageNotFound);
                    return;
                }
            }
            else
            {
                Fail((flags & FormatFromModule) != 0 ? ErrorMessageNotFound : ErrorInvalidParameter);
                return;
            }

            if (buffer == 0)
            {
                Fail(ErrorInvalidParameter);
                return;
            }

            string? text = ExpandMessage(message, flags, arguments, wide);
            if (text is null)
            {
                Fail(ErrorInvalidParameter);
                return;
            }

            byte[] encoded = wide ? Encoding.Unicode.GetBytes(text + "\0") : Ansi1252().GetBytes(text + "\0");
            int unit = wide ? 2 : 1;
            if ((flags & FormatAllocate) != 0)
            {
                long block = _emulator.AllocateHeap(Math.Max(encoded.Length, size * unit));
                _emulator.WriteMemory(block, encoded);
                _emulator.WriteUInt32(buffer, (uint)block);
            }
            else
            {
                if (size < text.Length + 1)
                {
                    Fail(ErrorInsufficientBuffer);
                    return;
                }

                _emulator.WriteMemory(buffer, encoded);
            }

            _frame.Return(text.Length);
        }

        private static Encoding Ansi1252() => CodePage(1252, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback) ?? Encoding.Latin1;

        private string? ExpandMessage(string message, long flags, long arguments, bool wide)
        {
            long width = flags & FormatMaxWidth;
            if (width == FormatMaxWidth)
            {
                // Only %n breaks lines; the message's own line breaks become nothing.
                message = message.Replace("\r\n", string.Empty).Replace("\n", string.Empty);
            }

            var output = new StringBuilder();
            for (int i = 0; i < message.Length; i++)
            {
                char c = message[i];
                if (c != '%' || i + 1 >= message.Length)
                {
                    output.Append(c);
                    continue;
                }

                char next = message[++i];
                switch (next)
                {
                    case '0':
                        return output.ToString();
                    case 'n':
                        output.Append("\r\n");
                        continue;
                    case 'r':
                        output.Append('\r');
                        continue;
                    case 't':
                        output.Append('\t');
                        continue;
                    case 'b':
                        output.Append(' ');
                        continue;
                    case '%' or '.' or '!' or ' ':
                        output.Append(next);
                        continue;
                }

                if (next is < '1' or > '9')
                {
                    output.Append('%').Append(next);
                    continue;
                }

                int start = i - 1;
                int number = next - '0';
                if (i + 1 < message.Length && char.IsAsciiDigit(message[i + 1]))
                {
                    number = (number * 10) + (message[++i] - '0');
                }

                string format = "s";
                if (i + 1 < message.Length && message[i + 1] == '!')
                {
                    int close = message.IndexOf('!', i + 2);
                    if (close < 0)
                    {
                        return null;
                    }

                    format = message[(i + 2)..close];
                    i = close;
                }

                if ((flags & FormatIgnoreInserts) != 0)
                {
                    output.Append(message, start, i - start + 1);
                    continue;
                }

                if (arguments == 0)
                {
                    return null;
                }

                long argument = (flags & FormatArgumentArray) != 0
                    ? _emulator.ReadUInt32(arguments + ((number - 1) * 4))
                    : _emulator.ReadUInt32(_emulator.ReadUInt32(arguments) + ((number - 1) * 4));
                output.Append(FormatInsert(format, argument, wide));
            }

            return output.ToString();
        }

        /// <summary>One insert's printf-style format: flags, width, precision, h/l/ll/I64/w and d i u x X o c s S p.</summary>
        private string FormatInsert(string format, long argument, bool wide)
        {
            int i = 0;
            bool left = false, zero = false, plus = false, space = false, hash = false;
            for (; i < format.Length && "-0+ #".Contains(format[i]); i++)
            {
                left |= format[i] == '-';
                zero |= format[i] == '0';
                plus |= format[i] == '+';
                space |= format[i] == ' ';
                hash |= format[i] == '#';
            }

            int width = 0;
            while (i < format.Length && char.IsAsciiDigit(format[i]))
            {
                width = (width * 10) + (format[i++] - '0');
            }

            int precision = -1;
            if (i < format.Length && format[i] == '.')
            {
                precision = 0;
                i++;
                while (i < format.Length && char.IsAsciiDigit(format[i]))
                {
                    precision = (precision * 10) + (format[i++] - '0');
                }
            }

            bool shortString = false, longString = false;
            while (i < format.Length && "hlwIL3264".Contains(format[i]))
            {
                shortString |= format[i] == 'h';
                longString |= format[i] is 'l' or 'w';
                i++;
            }

            char conversion = i < format.Length ? format[i] : 's';
            string body = conversion switch
            {
                'd' or 'i' => ((int)argument).ToString(CultureInfo.InvariantCulture),
                'u' => ((uint)argument).ToString(CultureInfo.InvariantCulture),
                'x' => (hash && argument != 0 ? "0x" : string.Empty) + ((uint)argument).ToString("x", CultureInfo.InvariantCulture),
                'X' => (hash && argument != 0 ? "0X" : string.Empty) + ((uint)argument).ToString("X", CultureInfo.InvariantCulture),
                'o' => Convert.ToString((uint)argument, 8),
                'p' => ((uint)argument).ToString("X8", CultureInfo.InvariantCulture),
                'c' or 'C' => ((char)(argument & 0xFFFF)).ToString(),
                _ => argument == 0 ? "(null)"
                    : (conversion == 'S' ? !wide : wide) && !shortString || longString
                        ? Wide(argument, 1 << 16)
                        : _frame.ReadNarrowString(argument, 1 << 16),
            };

            if (conversion is 'd' or 'i' && (int)argument >= 0)
            {
                body = (plus ? "+" : space ? " " : string.Empty) + body;
            }

            if (conversion is 's' or 'S' && precision >= 0 && body.Length > precision)
            {
                body = body[..precision];
            }

            if (body.Length < width)
            {
                body = left ? body.PadRight(width) : zero && conversion is not ('s' or 'S' or 'c' or 'C') ? body.PadLeft(width, '0') : body.PadLeft(width);
            }

            return body;
        }
    }
}

using System.Text;

namespace WPR.Wp8Native
{
    /// <summary>
    /// The imports the census still listed after the families landed (Plans/WP8-API-CENSUS.md), each
    /// to its documented contract: the "C" locale object, the stdio functions that format or scan
    /// through a FILE*, MSVCP110's C-locale collation and conversion helpers, and the last vccorlib
    /// exports (TerminateModule, Rect, Object::GetHashCode, ValueType::Equals).
    /// </summary>
    public sealed partial class HostStubs
    {
        /// <summary>MSVCR110's strings and conversions (CrtStrings.cs): fscanf uses its scanner.</summary>
        public CrtStrings Strings { get; private set; } = null!;

        /// <summary>ConcRT and PPL (ConcurrencyRuntime.cs).</summary>
        public ConcurrencyRuntime Ppl { get; private set; } = null!;

        private long _unhandledExceptionFilter;
        private long _cTypeTable, _cLowerMap, _cUpperMap, _cLocaleName, _cLocaleWideName;
        private readonly HashSet<long> _locales = new();

        private void RegisterRemainderImports()
        {
            Ppl = new ConcurrencyRuntime(_emulator, _frame, () => Msvcp);
            Ppl.RegisterInto(_handlers);
            RegisterCrtRemainder();
            RegisterLocaleObjects();
            RegisterCollation();
            RegisterVccorlibRemainder();
        }

        // ------------------------------------------------------------------------------------
        // MSVCR110
        // ------------------------------------------------------------------------------------

        private void RegisterCrtRemainder()
        {
            // void __crtSetUnhandledExceptionFilter(LPTOP_LEVEL_EXCEPTION_FILTER): the CRT's top-level
            // SEH filter. Recorded; there is no SEH dispatch here to run it. The previous one is
            // answered, as SetUnhandledExceptionFilter does underneath.
            _handlers["__crtSetUnhandledExceptionFilter"] = () =>
            {
                long previous = _unhandledExceptionFilter;
                _unhandledExceptionFilter = Arg(0);
                Return(previous);
            };

            // int vfprintf(FILE*, const char* format, va_list): the formatted length, or -1.
            _handlers["vfprintf"] = () =>
            {
                if (Arg(0) == 0 || Arg(1) == 0)
                {
                    InvalidParameter("vfprintf", 0xFFFFFFFFL, Einval);
                    return;
                }

                string text = new PrintfFormatter(_emulator, _frame).Format(Narrow(1), VarArgReader.FromList(_emulator, Arg(2)));
                Return(WriteToStream(Arg(0), text));
            };

            // int fscanf(FILE*, const char* format, ...): sscanf over the stream from its position; what
            // the conversions consumed is consumed from the stream. EOF (-1) at the end of the input.
            _handlers["fscanf"] = () =>
            {
                long stream = Arg(0);
                if (stream == 0 || Arg(1) == 0)
                {
                    InvalidParameter("fscanf", 0xFFFFFFFFL, Einval);
                    return;
                }

                const int window = 64 * 1024;
                string? input = Files.PeekStreamText(stream, window);
                if (input is null)
                {
                    Return(0xFFFFFFFFL);   // stdin, or a stream nothing opened: at its end
                    return;
                }

                int result = Strings.ScanText(input, Narrow(1), new VarArgReader(_emulator, 2), wide: false, secure: false, out int consumed);
                Files.AdvanceStream(stream, consumed, atEnd: consumed >= input.Length && input.Length < window);
                Return(unchecked((uint)result));
            };

            // int printf_s(const char* format, ...): printf, with the _s validation (a null format, or
            // %n, is an invalid parameter). stdout is the debug log here.
            _handlers["printf_s"] = () =>
            {
                string? format = Arg(0) == 0 ? null : Narrow(0);
                if (format is null || HasCountConversion(format))
                {
                    InvalidParameter("printf_s", 0xFFFFFFFFL, Einval);
                    return;
                }

                string text = new PrintfFormatter(_emulator, _frame).Format(format, new VarArgReader(_emulator, 1));
                PrintToLog(text.TrimEnd('\n'));
                Return(Encoding.Latin1.GetByteCount(text));
            };

            // int wprintf(const wchar_t* format, ...) / vwprintf: wide output to stdout; characters written.
            _handlers["wprintf"] = () => WidePrint(Arg(0) == 0 ? null : new VarArgReader(_emulator, 1), "wprintf");
            _handlers["vwprintf"] = () => WidePrint(Arg(0) == 0 ? null : VarArgReader.FromList(_emulator, Arg(1)), "vwprintf");
        }

        private void WidePrint(VarArgReader? arguments, string function)
        {
            if (arguments is null)
            {
                InvalidParameter(function, 0xFFFFFFFFL, Einval);
                return;
            }

            string text = new PrintfFormatter(_emulator, _frame).Format(Wide(0), arguments, true);
            PrintToLog(text.TrimEnd('\n'));
            Return(text.Length);
        }

        /// <summary>stdout and stderr go to the debug log; an fopen'd stream gets the bytes; anything else fails.</summary>
        private long WriteToStream(long stream, string text)
        {
            if (_iob != 0 && stream >= _iob + 0x20 && stream < _iob + 0x60)
            {
                PrintToLog(text.TrimEnd('\n'));
                return Encoding.Latin1.GetByteCount(text);
            }

            int written = Files.WriteStreamText(stream, text);
            return written < 0 ? 0xFFFFFFFFL : written;
        }

        /// <summary>A %n conversion, which the _s printf functions refuse.</summary>
        private static bool HasCountConversion(string format)
        {
            for (int i = 0; i < format.Length; i++)
            {
                if (format[i] != '%')
                {
                    continue;
                }

                int j = i + 1;
                while (j < format.Length && "-+ #0123456789.*hlLwIjzt".Contains(format[j]))
                {
                    j++;
                }

                if (j < format.Length && format[j] == 'n')
                {
                    return true;
                }

                i = j < format.Length && format[j] == '%' ? j : i;
            }

            return false;
        }

        // ------------------------------------------------------------------------------------
        // _locale_t: the "C" locale as an object
        // ------------------------------------------------------------------------------------

        // VC11 crtdefs.h, 32-bit: threadlocinfo { refcount, lc_codepage, lc_collate_cp, lc_time_cp,
        // lc_category[6] {locale, wlocale, refcount, wrefcount}, lc_clike, mb_cur_max, three lconv
        // refcounts, lconv, ctype1_refcount, ctype1, pctype, pclmap, pcumap, lc_time_curr,
        // locale_name[6] } and threadmbcinfo { refcount, mbcodepage, ismbcodepage, mbulinfo[6],
        // mbctype[257], mbcasemap[256], mblocalename }.
        private const int LocInfoSize = 0xB8, MbcInfoSize = 0x220;

        private void RegisterLocaleObjects()
        {
            // _locale_t _create_locale(int category, const char* locale) / _wcreate_locale: only "C"
            // exists here (setlocale's rule: "" is the native locale, which is "C"); any other name,
            // a category outside LC_ALL..LC_TIME or a null name answers NULL, the documented failure.
            _handlers["_create_locale"] = () => Return(SignedArg(0) is >= 0 and <= 5 && Arg(1) != 0 && Narrow(1) is "" or "C" ? NewCLocale() : 0);
            _handlers["_wcreate_locale"] = () => Return(SignedArg(0) is >= 0 and <= 5 && Arg(1) != 0 && Wide(1) is "" or "C" ? NewCLocale() : 0);

            // _locale_t _get_current_locale(): a copy of the thread's locale, which is always "C".
            _handlers["_get_current_locale"] = () => Return(NewCLocale());

            // void _free_locale(_locale_t): drops the locale's references and frees it; null is ignored.
            _handlers["_free_locale"] = () =>
            {
                long locale = Arg(0);
                if (locale != 0 && _locales.Remove(locale))
                {
                    long locinfo = _emulator.ReadUInt32(locale), mbcinfo = _emulator.ReadUInt32(locale + 4);
                    if (DropReference(locinfo))
                    {
                        _emulator.FreeHeap(locinfo);
                    }

                    if (DropReference(mbcinfo))
                    {
                        _emulator.FreeHeap(mbcinfo);
                    }

                    _emulator.FreeHeap(locale);
                }

                Return(0);
            };
        }

        private bool DropReference(long block)
        {
            if (block == 0)
            {
                return false;
            }

            long count = _emulator.ReadUInt32(block) - 1;
            _emulator.WriteUInt32(block, (uint)count);
            return count <= 0;
        }

        /// <summary>A fresh _locale_tstruct { locinfo, mbcinfo } describing the "C" locale.</summary>
        private long NewCLocale()
        {
            EnsureCTables();
            long locinfo = _emulator.AllocateHeap(LocInfoSize);
            _emulator.WriteMemory(locinfo, new byte[LocInfoSize]);
            _emulator.WriteUInt32(locinfo, 1);
            for (int category = 0; category < 6; category++)
            {
                _emulator.WriteUInt32(locinfo + 16 + (category * 16), (uint)_cLocaleName);
                _emulator.WriteUInt32(locinfo + 16 + (category * 16) + 4, (uint)_cLocaleWideName);
            }

            _emulator.WriteUInt32(locinfo + 112, 1);                              // lc_clike
            _emulator.WriteUInt32(locinfo + 116, 1);                              // mb_cur_max
            _emulator.WriteUInt32(locinfo + 132, (uint)Extras.LocaleConventions()); // lconv
            _emulator.WriteUInt32(locinfo + 144, (uint)(_cTypeTable + 2));        // pctype: index -1 is EOF
            _emulator.WriteUInt32(locinfo + 148, (uint)_cLowerMap);
            _emulator.WriteUInt32(locinfo + 152, (uint)_cUpperMap);

            long mbcinfo = _emulator.AllocateHeap(MbcInfoSize);
            _emulator.WriteMemory(mbcinfo, new byte[MbcInfoSize]);
            _emulator.WriteUInt32(mbcinfo, 1);

            long locale = _emulator.AllocateHeap(8);
            _emulator.WriteUInt32(locale, (uint)locinfo);
            _emulator.WriteUInt32(locale + 4, (uint)mbcinfo);
            _locales.Add(locale);
            return locale;
        }

        /// <summary>The "C" locale's classification table (257 entries from EOF) and case maps.</summary>
        private void EnsureCTables()
        {
            if (_cTypeTable != 0)
            {
                return;
            }

            const ushort upper = 0x1, lower = 0x2, digit = 0x4, space = 0x8, punct = 0x10, control = 0x20, blank = 0x40, hex = 0x80, alpha = 0x100;
            byte[] table = new byte[257 * 2];
            for (int c = 0; c < 128; c++)
            {
                ushort bits = 0;
                char ch = (char)c;
                if (char.IsAsciiLetterUpper(ch)) bits |= upper | alpha;
                if (char.IsAsciiLetterLower(ch)) bits |= lower | alpha;
                if (char.IsAsciiDigit(ch)) bits |= digit;
                if (c is 9 or 10 or 11 or 12 or 13 or 32) bits |= space;
                if (c is >= 33 and <= 126 && !char.IsAsciiLetterOrDigit(ch)) bits |= punct;
                if (c < 32 || c == 127) bits |= control;
                if (c is 9 or 32) bits |= blank;
                if (char.IsAsciiHexDigit(ch)) bits |= hex;
                BitConverter.TryWriteBytes(table.AsSpan((c + 1) * 2), bits);
            }

            _cTypeTable = _emulator.AllocateHeap(table.Length);
            _emulator.WriteMemory(_cTypeTable, table);
            byte[] lowerMap = Enumerable.Range(0, 256).Select(c => (byte)(c is >= 'A' and <= 'Z' ? c + 32 : c)).ToArray();
            byte[] upperMap = Enumerable.Range(0, 256).Select(c => (byte)(c is >= 'a' and <= 'z' ? c - 32 : c)).ToArray();
            _cLowerMap = _emulator.AllocateHeap(256);
            _emulator.WriteMemory(_cLowerMap, lowerMap);
            _cUpperMap = _emulator.AllocateHeap(256);
            _emulator.WriteMemory(_cUpperMap, upperMap);
            _cLocaleName = _emulator.AllocateHeap(2);
            _emulator.WriteMemory(_cLocaleName, [(byte)'C', 0]);
            _cLocaleWideName = _emulator.AllocateHeap(4);
            _emulator.WriteMemory(_cLocaleWideName, [(byte)'C', 0, 0, 0]);
        }

        // ------------------------------------------------------------------------------------
        // MSVCP110's C-locale helpers (xlocinfo.h): collation and one multibyte conversion
        // ------------------------------------------------------------------------------------

        private void RegisterCollation()
        {
            // int _Strcoll(const char* s1, const char* e1, const char* s2, const char* e2, const _Collvec*):
            // the "C" locale compares the ranges as unsigned bytes, the shorter first when one is a prefix.
            _handlers["_Strcoll"] = () => Return(unchecked((uint)CompareRanges(Arg(0), Arg(1), Arg(2), Arg(3), unit: 1)));
            _handlers["_Wcscoll"] = () => Return(unchecked((uint)CompareRanges(Arg(0), Arg(1), Arg(2), Arg(3), unit: 2)));

            // size_t _Strxfrm(char* s1, char* e1, const char* s2, const char* e2, const _Collvec*): in
            // the "C" locale the key is the string itself: copied when it fits in [s1, e1); the length
            // of the key is answered either way.
            _handlers["_Strxfrm"] = () => Return(TransformRange(Arg(0), Arg(1), Arg(2), Arg(3), unit: 1));
            _handlers["_Wcsxfrm"] = () => Return(TransformRange(Arg(0), Arg(1), Arg(2), Arg(3), unit: 2));

            // int _Mbrtowc(wchar_t* pwc, const char* s, size_t n, mbstate_t*, const _Cvtvec*): the "C"
            // locale maps each byte to the same code unit. 0 for a null/empty input or the NUL char,
            // otherwise 1 (one byte consumed).
            _handlers["_Mbrtowc"] = () =>
            {
                long pwc = Arg(0), s = Arg(1);
                if (s == 0 || Arg(2) == 0)
                {
                    Return(0);
                    return;
                }

                byte b = _emulator.ReadMemory(s, 1)[0];
                if (pwc != 0)
                {
                    _emulator.WriteMemory(pwc, BitConverter.GetBytes((ushort)b));
                }

                Return(b == 0 ? 0 : 1);
            };
        }

        private int CompareRanges(long s1, long e1, long s2, long e2, int unit)
        {
            long n1 = Math.Max(0, (e1 - s1) / unit), n2 = Math.Max(0, (e2 - s2) / unit);
            long common = Math.Min(n1, n2);
            if (common > 0)
            {
                byte[] a = _emulator.ReadMemory(s1, (int)(common * unit)), b = _emulator.ReadMemory(s2, (int)(common * unit));
                for (int i = 0; i < common; i++)
                {
                    uint x = unit == 1 ? a[i] : BitConverter.ToUInt16(a, i * 2);
                    uint y = unit == 1 ? b[i] : BitConverter.ToUInt16(b, i * 2);
                    if (x != y)
                    {
                        return x < y ? -1 : 1;
                    }
                }
            }

            return n1 == n2 ? 0 : n1 < n2 ? -1 : 1;
        }

        private long TransformRange(long s1, long e1, long s2, long e2, int unit)
        {
            long room = Math.Max(0, (e1 - s1) / unit), length = Math.Max(0, (e2 - s2) / unit);
            if (length > 0 && length <= room)
            {
                _emulator.WriteMemory(s1, _emulator.ReadMemory(s2, (int)(length * unit)));
            }

            return length;
        }

        // ------------------------------------------------------------------------------------
        // vccorlib110
        // ------------------------------------------------------------------------------------

        private void RegisterVccorlibRemainder()
        {
            // HRESULT InitializeData(int threadingModel): vccorlib's per-module start-up - the apartment
            // (RoInitialize) and its module data. The host is the apartment: S_OK.
            _handlers[$"?InitializeData@Details{Pl}YAJH@Z"] = () => Return(SOk);

            // bool TerminateModule(ModuleBase*) - DllCanUnloadNow's body: true when the WRL module has
            // no live objects (ModuleBase::GetObjectCount, vtable slot 3); a null module is true.
            _handlers["?TerminateModule@Details@Platform@@YA_NPAVModuleBase@1WRL@Microsoft@@@Z"] = () =>
            {
                long module = Arg(0);
                long back = _emulator.ReturnAddress;
                if (module == 0)
                {
                    Return(1);
                    return;
                }

                CallMethod("ModuleBase::GetObjectCount", module, 3, [], () => FinishImport(back, (Arg(0) & 0xFFFFFFFFL) == 0 ? 1 : 0));
            };

            // Rect::Rect(Point location, Size size): `this` r0; the two float pairs are HFAs, so s0-s1 and
            // s2-s3 (measured at Lumia.Imaging's call sites). An empty size (negative width) makes
            // Rect::Empty, as WPF/Silverlight's Rect; otherwise { X, Y, Width, Height }. Returns this.
            _handlers["??0Rect@Foundation@Windows@@QAA@VPoint@12@VSize@12@@Z"] = () =>
            {
                long self = Arg(0);
                float x = _frame.FloatArg(0), y = _frame.FloatArg(1), width = _frame.FloatArg(2), height = _frame.FloatArg(3);
                float[] rect = width < 0
                    ? [float.PositiveInfinity, float.PositiveInfinity, float.NegativeInfinity, float.NegativeInfinity]
                    : [x, y, width, height];
                if (self != 0)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        _emulator.WriteSingle(self + (i * 4), rect[i]);
                    }
                }

                Return(self);
            };

            // bool Rect::IntersectsWith(Rect rect): `this` r0, the other rect in s0-s3 (measured). False if
            // either is empty; otherwise the closed ranges overlap on both axes (touching edges count).
            _handlers["?IntersectsWith@Rect@Foundation@Windows@@QAA_NV123@@Z"] = () =>
            {
                long self = Arg(0);
                float x = FloatAt(self), y = FloatAt(self + 4), w = FloatAt(self + 8), h = FloatAt(self + 12);
                float ox = _frame.FloatArg(0), oy = _frame.FloatArg(1), ow = _frame.FloatArg(2), oh = _frame.FloatArg(3);
                bool intersects = !(w < 0) && !(ow < 0) &&
                    ox <= x + w && ox + ow >= x && oy <= y + h && oy + oh >= y;
                Return(intersects ? 1 : 0);
            };

            // int Object::GetHashCode(): the object's identity - its IUnknown pointer, by QueryInterface,
            // as vccorlib110 (so every interface pointer to one object hashes alike).
            _handlers[$"?GetHashCode@Object{Pl}Q$AAAHXZ"] = () =>
            {
                long self = Arg(0);
                long back = _emulator.ReturnAddress;
                if (self == 0)
                {
                    Return(0);
                    return;
                }

                long iid = Scratch(16), unknown = Scratch(4);
                WriteGuid(iid, IidUnknown);
                CallMethod("QueryInterface(IUnknown)", self, 0, [iid, unknown], () =>
                {
                    long identity = _emulator.ReadUInt32(unknown);
                    if (IsFailure(Arg(0)) || identity == 0)
                    {
                        FinishImport(back, self);
                        return;
                    }

                    CallMethod("Release", identity, 2, [], () => FinishImport(back, identity));
                });
            };

            // bool ValueType::Equals(Object^ other): two boxed values are equal when they box the same
            // type with the same value (a String by its text); a null other is never equal; anything
            // that is not a box here compares by identity.
            _handlers[$"?Equals@ValueType{Pl}Q$AAA_NP$AAVObject@2@@Z"] = () =>
            {
                long self = Arg(0), other = Arg(1);
                if (other == 0 || self == 0)
                {
                    Return(self == other ? 1 : 0);
                    return;
                }

                if (Unbox(self) is { } a && Unbox(other) is { } b)
                {
                    bool same = a.Type == b.Type && (a.Type == PropertyType.String
                        ? _strings.ReadText(BitConverter.ToUInt32(a.Raw)) == _strings.ReadText(BitConverter.ToUInt32(b.Raw))
                        : a.Raw.AsSpan().SequenceEqual(b.Raw));
                    Return(same ? 1 : 0);
                    return;
                }

                Return(self == other ? 1 : 0);
            };
        }
    }
}

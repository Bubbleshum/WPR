using System.Text;

namespace WPR.Wp8Native
{
    using Arm = UnicornEngine.Const.Arm;

    /// <summary>
    /// MSVCP110's iostreams for <c>char</c> and <c>wchar_t</c>: basic_streambuf, ios_base,
    /// basic_ios, basic_istream, basic_ostream and basic_iostream, as the ARM build of Visual
    /// C++ 2012 lays them out. The locale side is <see cref="LocaleLibrary"/>, owned by this.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before this, every stream constructor went to the generic stand-in, which writes a fake
    /// vtable at <c>this+0</c>. For a stream that word is the virtual-base-table pointer the
    /// game's own constructor had just set, so the next <c>ios</c> lookup landed at a garbage
    /// offset and the ostream sentry called through it: Angry Birds Classic and Seasons jumped to
    /// 0x4F000104 / 0x06000104 before their first frame, inside the ostream sentry.
    /// </para>
    /// <para>
    /// <b>Layouts</b>, read off the games' own inlined code rather than assumed, and the same for
    /// both element types (counts are in elements, pointers address elements):
    /// <list type="bullet">
    /// <item>basic_streambuf (0x38): vfptr; _Gfirst +4; _Pfirst +8; _IGfirst +0xC; _IPfirst +0x10;
    /// _Gnext +0x14; _Pnext +0x18; _IGnext +0x1C; _IPnext +0x20; _Gcount +0x24; _Pcount +0x28;
    /// _IGcount +0x2C; _IPcount +0x30; _Plocale +0x34. No lock member. A game's basic_stringbuf
    /// then puts _Seekhigh at +0x38 and its state at +0x3C - Classic's overflow reads +0x3C.</item>
    /// <item>ios_base / basic_ios (0x48): vfptr; (4 bytes for the empty _Iosb base); _Stdstr +8;
    /// _Mystate +0xC; _Except +0x10; _Fmtfl +0x14; _Prec +0x18 (8); _Wide +0x20 (8); _Arr +0x28;
    /// _Calls +0x2C; _Ploc +0x30 (a locale*); _Mystrbuf +0x38; _Tiestr +0x3C; _Fillch +0x40 - the
    /// ostream sentry reads the state at +0xC, the buffer at +0x38 and the tie at +0x3C.</item>
    /// <item>A stream subobject starts with its vbptr; the shared basic_ios is at
    /// <c>this + vbtable[1]</c>. basic_istream keeps _Chcount (8 bytes) at +8.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Hidden flag.</b> Constructors of classes with a virtual base take a trailing "construct
    /// the virtual bases" int: r3 for basic_istream/basic_ostream(sb, isstd), r2 for
    /// basic_iostream(sb). A game's stringstream constructs its basic_ios itself and passes 0;
    /// a game that makes a plain ostream directly passes 1, and then the vbptr is ours to set.
    /// </para>
    /// <para>
    /// <b>Virtuals live in the game.</b> basic_stringbuf's and basic_filebuf's overflow,
    /// underflow, pbackfail, seekoff/seekpos and sync are game code (vtable slots 3, 6, 4, 10, 11,
    /// 13), so everything here that runs a buffer dry calls back into the image through
    /// <see cref="ArmEmulator.CallEmulated(string,long,ReadOnlySpan{long},Action,bool)"/>, in
    /// continuation style. Those that would only ever tail-call do so, leaving lr alone so the
    /// virtual returns straight to the caller.
    /// </para>
    /// <para>
    /// <b>Struct results.</b> A member returning <c>fpos</c> or <c>locale</c> by value takes
    /// <c>this</c> in r0 and the hidden result pointer in r1 (measured on Terraria's <c>tellg</c>
    /// call site); a static one takes the result in r0.
    /// </para>
    /// </remarks>
    public sealed partial class StreamLibrary
    {
        // ios_base::iostate
        private const uint GoodBit = 0, EofBit = 1, FailBit = 2, BadBit = 4, StateMask = 0x17;

        // ios_base::fmtflags (VC11)
        private const uint SkipWs = 0x0001, UnitBuf = 0x0002, Uppercase = 0x0004, ShowBase = 0x0008,
            ShowPoint = 0x0010, ShowPos = 0x0020, Left = 0x0040, Right = 0x0080, Internal = 0x0100,
            Dec = 0x0200, Oct = 0x0400, Hex = 0x0800, Scientific = 0x1000, Fixed = 0x2000, BoolAlpha = 0x4000,
            AdjustField = Left | Right | Internal, BaseField = Dec | Oct | Hex, FloatField = Scientific | Fixed, FmtMask = 0xFFFF;

        // ios_base::event
        private const int EraseEvent = 0, ImbueEvent = 1;

        // seekdir and openmode
        private const long SeekCur = 1, OpenIn = 1, OpenOut = 2;

        // basic_streambuf fields
        private const int Gfirst = 0x04, Pfirst = 0x08, IGfirst = 0x0C, IPfirst = 0x10, Gnext = 0x14, Pnext = 0x18,
            IGnext = 0x1C, IPnext = 0x20, Gcount = 0x24, Pcount = 0x28, IGcount = 0x2C, IPcount = 0x30, Plocale = 0x34,
            StreambufSize = 0x38;

        // ios_base / basic_ios fields
        private const int Stdstr = 0x08, State = 0x0C, Except = 0x10, Flags = 0x14, Precision = 0x18, Width = 0x20,
            Arr = 0x28, Calls = 0x2C, Locale = 0x30, Buffer = 0x38, Tie = 0x3C, Fill = 0x40, IosSize = 0x48;

        // basic_streambuf vtable slots
        private const int SlotOverflow = 3, SlotPbackfail = 4, SlotShowmanyc = 5, SlotUnderflow = 6, SlotUflow = 7,
            SlotXsgetn = 8, SlotXsputn = 9, SlotSeekoff = 10, SlotSeekpos = 11, SlotSetbuf = 12, SlotSync = 13, SlotImbue = 14;

        private static readonly string[] StreambufSlots =
        [
            "dtor", "_Lock", "_Unlock", "overflow", "pbackfail", "showmanyc", "underflow", "uflow",
            "xsgetn", "xsputn", "seekoff", "seekpos", "setbuf", "sync", "imbue",
        ];

        /// <summary>One element type: its size, mangled names and EOF.</summary>
        private sealed class Kind
        {
            public Kind(bool wide)
            {
                Wide = wide;
                C = wide ? "_W" : "D";
                I = wide ? "G" : "H";
                T = $"{C}U?$char_traits@{C}@std@@";
            }

            public bool Wide { get; }

            public int Size => Wide ? 2 : 1;

            /// <summary>The element type's mangled name.</summary>
            public string C { get; }

            /// <summary>int_type: int for char, unsigned short (wint_t) for wchar_t.</summary>
            public string I { get; }

            /// <summary>The template arguments, <c>&lt;C, char_traits&lt;C&gt;&gt;</c>.</summary>
            public string T { get; }

            /// <summary>traits::eof() as it sits in r0.</summary>
            public long EofRegister => Wide ? 0xFFFF : 0xFFFFFFFF;

            public string Name => Wide ? "wchar_t" : "char";

            public long StreambufVtable { get; set; }

            /// <summary>Expands {Sb} {Ios} {Os} {Is} {IoS} {T} {C} {I} {P} {PB} in a mangled-name pattern.</summary>
            public string Mangle(string pattern) => pattern
                .Replace("{Sb}", "?$basic_streambuf@{T}@std@@")
                .Replace("{Ios}", "?$basic_ios@{T}@std@@")
                .Replace("{Os}", "?$basic_ostream@{T}@std@@")
                .Replace("{Is}", "?$basic_istream@{T}@std@@")
                .Replace("{IoS}", "?$basic_iostream@{T}@std@@")
                .Replace("{T}", T)
                .Replace("{PB}", "PB" + C)
                .Replace("{P}", "PA" + C)
                .Replace("{C}", C)
                .Replace("{I}", I);
        }

        private readonly ArmEmulator _emulator;
        private readonly CallFrame _frame;
        private readonly Kind _narrow = new(wide: false);
        private readonly Kind _wide = new(wide: true);
        private readonly Dictionary<int, long> _vbtables = new();
        private readonly Dictionary<long, int> _stdOpens = new();
        private int _xallocIndex;
        private long _deadIos;
        private string? _pendingFailure;

        public StreamLibrary(ArmEmulator emulator, CallFrame frame)
        {
            _emulator = emulator;
            _frame = frame;
            Locales = new LocaleLibrary(emulator, frame)
            {
                StreamPut = (sb, chars, done) => PutChars(_narrow, sb, chars, 0, done),
            };
            FillDataImports();
        }

        /// <summary>The locale and facet side of MSVCP110.</summary>
        public LocaleLibrary Locales { get; }

        /// <summary>What the streams did, for the report.</summary>
        public List<string> Log { get; } = new();

        private void Note(string line)
        {
            if (Log.Count < 200)
            {
                Log.Add(line);
            }
        }

        private Kind[] Kinds => [_narrow, _wide];

        /// <summary>Registers one handler per element type for a mangled-name pattern.</summary>
        private void Both(Dictionary<string, Action> handlers, string pattern, Action<Kind> body)
        {
            foreach (Kind kind in Kinds)
            {
                Kind k = kind;
                handlers[k.Mangle(pattern)] = () => body(k);
            }
        }

        public void RegisterInto(Dictionary<string, Action> handlers)
        {
            Locales.RegisterInto(handlers);
            RegisterStreambuf(handlers);
            RegisterIos(handlers);
            RegisterOstream(handlers);
            RegisterIstream(handlers);
        }

        // ------------------------------------------------------------------------------
        // Memory and calls
        // ------------------------------------------------------------------------------

        private uint Read32(long address) => _emulator.ReadUInt32(address);

        private void Write32(long address, long value) => _emulator.WriteUInt32(address, (uint)value);

        private long ReadInt64(long address) => BitConverter.ToInt64(_emulator.ReadMemory(address, 8));

        private void WriteInt64(long address, long value) => _emulator.WriteMemory(address, BitConverter.GetBytes(value));

        /// <summary>*(this->_IXxx): the value an indirect field points at.</summary>
        private long Deref(long sb, int field)
        {
            long pointer = Read32(sb + field);
            return pointer == 0 ? 0 : Read32(pointer);
        }

        private void Store(long sb, int field, long value)
        {
            long pointer = Read32(sb + field);
            if (pointer != 0)
            {
                Write32(pointer, value);
            }
        }

        /// <summary>A 64-bit argument in r2:r3 (after this in r0 and a skipped r1).</summary>
        private long Arg64() => (long)((ulong)(uint)_frame.Arg(2) | ((ulong)(uint)_frame.Arg(3) << 32));

        private long ArgPair(int low) => (long)((ulong)(uint)_frame.Arg(low) | ((ulong)(uint)_frame.Arg(low + 1) << 32));

        private int ReadChar(Kind k, long address)
        {
            byte[] b = _emulator.ReadMemory(address, k.Size);
            return k.Wide ? b[0] | (b[1] << 8) : b[0];
        }

        private int[] ReadChars(Kind k, long address, int count)
        {
            if (count <= 0)
            {
                return [];
            }

            byte[] b = _emulator.ReadMemory(address, count * k.Size);
            int[] chars = new int[count];
            for (int i = 0; i < count; i++)
            {
                chars[i] = k.Wide ? b[i * 2] | (b[(i * 2) + 1] << 8) : b[i];
            }

            return chars;
        }

        private void WriteChars(Kind k, long address, int[] chars, int index, int count)
        {
            if (count <= 0)
            {
                return;
            }

            byte[] b = new byte[count * k.Size];
            for (int i = 0; i < count; i++)
            {
                int c = chars[index + i];
                if (k.Wide)
                {
                    b[i * 2] = (byte)c;
                    b[(i * 2) + 1] = (byte)(c >> 8);
                }
                else
                {
                    b[i] = (byte)c;
                }
            }

            _emulator.WriteMemory(address, b);
        }

        /// <summary>A guest int_type in r0 as a character, or -1 for traits::eof().</summary>
        private static int CharResult(Kind k, long r0) =>
            k.Wide ? ((r0 & 0xFFFF) == 0xFFFF ? -1 : (int)(r0 & 0xFFFF)) : (unchecked((int)(uint)r0) == -1 ? -1 : (int)(r0 & 0xFF));

        /// <summary>A character (or -1) as the int_type a function returns.</summary>
        private static long CharRegister(Kind k, int c) => c == -1 ? k.EofRegister : c;

        /// <summary>Text as elements: Latin-1 bytes for char (anything wider becomes '?'), UTF-16 units for wchar_t.</summary>
        private static int[] Elements(Kind k, string text) =>
            text.Select(ch => k.Wide ? ch : ch > 0xFF ? '?' : (int)ch).ToArray();

        private long VirtualSlot(long obj, int slot) => Read32(Read32(obj) + (slot * 4));

        /// <summary>Hands the call to a virtual with the same arguments; it returns to our caller.</summary>
        private void TailCallVirtual(long obj, int slot) => _emulator.ContinueAt(VirtualSlot(obj, slot));

        private void CallGuest(long function, long[] arguments, Action<long> then)
            => _emulator.CallEmulated("iostream callback", function, arguments, onReturn: () => then(_frame.Arg(0)), recycle: true);

        /// <summary>
        /// Returns <paramref name="value"/> to <paramref name="returnTo"/>. Every handler ends here,
        /// so an <c>ios_base::failure</c> an exception mask asked for is thrown from here, at the
        /// game's call site.
        /// </summary>
        private void Finish(long value, long returnTo)
        {
            _frame.Return(value);
            _emulator.ContinueAt(returnTo);
            ThrowPendingFailure(returnTo);
        }

        private void Finish64(long value, long returnTo)
        {
            _frame.Return64(value);
            _emulator.ContinueAt(returnTo);
            ThrowPendingFailure(returnTo);
        }

        /// <summary>Finish from a handler that has not called the guest: lr is still the caller.</summary>
        private void Done(long value) => Finish(value, _emulator.ReturnAddress);

        // ------------------------------------------------------------------------------
        // basic_streambuf
        // ------------------------------------------------------------------------------

        private void RegisterStreambuf(Dictionary<string, Action> h)
        {
            Both(h, "??0{Sb}IAA@XZ", ConstructStreambuf);
            Both(h, "??1{Sb}UAA@XZ", k =>
            {
                long returnTo = _emulator.ReturnAddress;
                DestroyStreambuf(_frame.Arg(0), () => Finish(0, returnTo));
            });
            Both(h, "?_Init@{Sb}IAAXXZ", _ => { InitStreambuf(_frame.Arg(0)); Done(0); });

            // _Init(_Gf, _Gn, _Gc, _Pf, _Pn, _Pc): point the indirect fields at the caller's pointers.
            Both(h, "?_Init@{Sb}IAAXPA{P}0PAH001@Z", _ =>
            {
                long sb = _frame.Arg(0);
                Write32(sb + IGfirst, _frame.Arg(1));
                Write32(sb + IGnext, _frame.Arg(2));
                Write32(sb + IGcount, _frame.Arg(3));
                Write32(sb + IPfirst, _frame.Arg(4));
                Write32(sb + IPnext, _frame.Arg(5));
                Write32(sb + IPcount, _frame.Arg(6));
                Done(0);
            });

            Both(h, "?eback@{Sb}IBA{P}XZ", _ => Done(Deref(_frame.Arg(0), IGfirst)));
            Both(h, "?gptr@{Sb}IBA{P}XZ", _ => Done(Deref(_frame.Arg(0), IGnext)));
            Both(h, "?egptr@{Sb}IBA{P}XZ", k => Done(EndOfArea(k, _frame.Arg(0), IGnext, IGcount)));
            Both(h, "?pbase@{Sb}IBA{P}XZ", _ => Done(Deref(_frame.Arg(0), IPfirst)));
            Both(h, "?pptr@{Sb}IBA{P}XZ", _ => Done(Deref(_frame.Arg(0), IPnext)));
            Both(h, "?epptr@{Sb}IBA{P}XZ", k => Done(EndOfArea(k, _frame.Arg(0), IPnext, IPcount)));
            Both(h, "?gbump@{Sb}IAAXH@Z", k => { Advance(k, _frame.Arg(0), IGnext, IGcount, _frame.SignedArg(1)); Done(0); });
            Both(h, "?pbump@{Sb}IAAXH@Z", k => { Advance(k, _frame.Arg(0), IPnext, IPcount, _frame.SignedArg(1)); Done(0); });
            Both(h, "?setg@{Sb}IAAX{P}00@Z", k => { SetArea(k, _frame.Arg(0), IGfirst, IGnext, IGcount, _frame.Arg(1), _frame.Arg(2), _frame.Arg(3)); Done(0); });
            Both(h, "?setp@{Sb}IAAX{P}0@Z", k => { SetArea(k, _frame.Arg(0), IPfirst, IPnext, IPcount, _frame.Arg(1), _frame.Arg(1), _frame.Arg(2)); Done(0); });
            Both(h, "?setp@{Sb}IAAX{P}00@Z", k => { SetArea(k, _frame.Arg(0), IPfirst, IPnext, IPcount, _frame.Arg(1), _frame.Arg(2), _frame.Arg(3)); Done(0); });

            // _Gninc: return the next get pointer, then step it. _Gndec: step back, return it. _Pninc likewise.
            Both(h, "?_Gninc@{Sb}IAA{P}XZ", k => Done(Step(k, _frame.Arg(0), IGnext, IGcount, +1, returnOld: true)));
            Both(h, "?_Gndec@{Sb}IAA{P}XZ", k => Done(Step(k, _frame.Arg(0), IGnext, IGcount, -1, returnOld: false)));
            Both(h, "?_Pninc@{Sb}IAA{P}XZ", k => Done(Step(k, _frame.Arg(0), IPnext, IPcount, +1, returnOld: true)));
            Both(h, "?_Gnavail@{Sb}IBA_JXZ", _ => { _frame.Return64(GetAvailable(_frame.Arg(0))); });
            Both(h, "?_Pnavail@{Sb}IBA_JXZ", _ => { _frame.Return64(PutAvailable(_frame.Arg(0))); });

            // locale getloc() const: the result by hidden pointer in r1.
            Both(h, "?getloc@{Sb}QBA?AVlocale@2@XZ", _ =>
            {
                long sb = _frame.Arg(0), result = _frame.Arg(1);
                Locales.CopyLocale(Read32(sb + Plocale), result);
                Done(result);
            });

            // locale pubimbue(const locale&): the buffer's imbue (slot 14) first, then the swap.
            Both(h, "?pubimbue@{Sb}QAA?AVlocale@2@ABV32@@Z", _ =>
            {
                long sb = _frame.Arg(0), result = _frame.Arg(1), incoming = _frame.Arg(2);
                long returnTo = _emulator.ReturnAddress;
                PubImbue(sb, incoming, result, () => Finish(result, returnTo));
            });

            // The public forwarders: same arguments, so a plain tail call.
            Both(h, "?pubsetbuf@{Sb}QAAPAV12@{P}_J@Z", _ => TailCallVirtual(_frame.Arg(0), SlotSetbuf));
            Both(h, "?pubseekoff@{Sb}QAA?AV?$fpos@H@2@_JHH@Z", _ => TailCallVirtual(_frame.Arg(0), SlotSeekoff));
            Both(h, "?pubseekpos@{Sb}QAA?AV?$fpos@H@2@V32@H@Z", _ => TailCallVirtual(_frame.Arg(0), SlotSeekpos));
            Both(h, "?pubsync@{Sb}QAAHXZ", _ => TailCallVirtual(_frame.Arg(0), SlotSync));
            Both(h, "?sputn@{Sb}QAA_J{PB}_J@Z", _ => TailCallVirtual(_frame.Arg(0), SlotXsputn));
            Both(h, "?sgetn@{Sb}QAA_J{P}_J@Z", _ => TailCallVirtual(_frame.Arg(0), SlotXsgetn));

            // streamsize in_avail(): what the get area holds, else showmanyc().
            Both(h, "?in_avail@{Sb}QAA_JXZ", _ =>
            {
                long sb = _frame.Arg(0);
                int available = GetAvailable(sb);
                if (available > 0)
                {
                    _frame.Return64(available);
                    return;
                }

                TailCallVirtual(sb, SlotShowmanyc);
            });

            Both(h, "?sgetc@{Sb}QAA{I}XZ", Sgetc);
            Both(h, "?sbumpc@{Sb}QAA{I}XZ", Sbumpc);
            Both(h, "?snextc@{Sb}QAA{I}XZ", Snextc);
            Both(h, "?stossc@{Sb}QAAXXZ", k =>
            {
                long returnTo = _emulator.ReturnAddress;
                NextChar(k, _frame.Arg(0), _ => Finish(0, returnTo));
            });
            Both(h, "?sputc@{Sb}QAA{I}{C}@Z", Sputc);
            Both(h, "?sungetc@{Sb}QAA{I}XZ", k => PutBack(k, _frame.Arg(0), null));
            Both(h, "?sputbackc@{Sb}QAA{I}{C}@Z", k => PutBack(k, _frame.Arg(0), (int)(_frame.Arg(1) & (k.Wide ? 0xFFFF : 0xFF))));
            Both(h, "?_Lock@{Sb}UAAXXZ", _ => Done(0));
            Both(h, "?_Unlock@{Sb}UAAXXZ", _ => Done(0));

            // The base virtuals, exported because a game's derived vtable names them.
            Both(h, "?overflow@{Sb}MAA{I}{I}@Z", k => Done(k.EofRegister));
            Both(h, "?pbackfail@{Sb}MAA{I}{I}@Z", k => Done(k.EofRegister));
            Both(h, "?underflow@{Sb}MAA{I}XZ", k => Done(k.EofRegister));
            Both(h, "?showmanyc@{Sb}MAA_JXZ", _ => _frame.Return64(0));
            Both(h, "?uflow@{Sb}MAA{I}XZ", Uflow);
            Both(h, "?xsgetn@{Sb}MAA_J{P}_J@Z", Xsgetn);
            Both(h, "?xsputn@{Sb}MAA_J{PB}_J@Z", Xsputn);
            Both(h, "?seekoff@{Sb}MAA?AV?$fpos@H@2@_JHH@Z", _ => { WriteFpos(_frame.Arg(1), -1); Done(_frame.Arg(1)); });
            Both(h, "?seekpos@{Sb}MAA?AV?$fpos@H@2@V32@H@Z", _ => { WriteFpos(_frame.Arg(1), -1); Done(_frame.Arg(1)); });
            Both(h, "?setbuf@{Sb}MAAPAV12@{P}_J@Z", _ => Done(_frame.Arg(0)));
            Both(h, "?sync@{Sb}MAAHXZ", _ => Done(0));
            Both(h, "?imbue@{Sb}MAAXABVlocale@2@@Z", _ => Done(0));
        }

        private void InitStreambuf(long sb)
        {
            Write32(sb + IGfirst, sb + Gfirst);
            Write32(sb + IPfirst, sb + Pfirst);
            Write32(sb + IGnext, sb + Gnext);
            Write32(sb + IPnext, sb + Pnext);
            Write32(sb + IGcount, sb + Gcount);
            Write32(sb + IPcount, sb + Pcount);
            foreach (int field in new[] { Gfirst, Pfirst, Gnext, Pnext, Gcount, Pcount })
            {
                Write32(sb + field, 0);
            }
        }

        /// <summary>basic_streambuf(): the base vtable, empty areas, and <c>_Plocale = new locale</c> (the global one).</summary>
        private void ConstructStreambuf(Kind k)
        {
            long sb = _frame.Arg(0);
            InitializeStreambuf(k, sb);
            Done(sb);
        }

        private void InitializeStreambuf(Kind k, long sb)
        {
            _emulator.WriteMemory(sb, new byte[StreambufSize]);
            Write32(sb, StreambufVtable(k));
            InitStreambuf(sb);
            Write32(sb + Plocale, Locales.NewLocaleObject());
        }

        /// <summary>~basic_streambuf: <c>delete _Plocale</c>.</summary>
        private void DestroyStreambuf(long sb, Action then)
        {
            long locale = Read32(sb + Plocale);
            Write32(sb + Plocale, 0);
            Locales.DeleteLocaleObject(locale, then);
        }

        private void PubImbue(long sb, long incoming, long result, Action then)
        {
            long held = Read32(sb + Plocale);
            Locales.CopyLocale(held, result);
            CallGuest(VirtualSlot(sb, SlotImbue), [sb, incoming], _ =>
            {
                if (held == 0)
                {
                    Write32(sb + Plocale, Locales.NewLocaleObject());
                    held = Read32(sb + Plocale);
                }

                Locales.AssignLocale(held, incoming, then);
            });
        }

        /// <summary>
        /// basic_streambuf's own vtable, for the moment between this constructor and the derived
        /// one that replaces it: every slot is the base behaviour, routed back through the same
        /// import names so they cannot disagree.
        /// </summary>
        private long StreambufVtable(Kind k)
        {
            if (k.StreambufVtable != 0)
            {
                return k.StreambufVtable;
            }

            long table = _emulator.AllocateHeap(StreambufSlots.Length * 4);
            for (int i = 0; i < StreambufSlots.Length; i++)
            {
                string slot = StreambufSlots[i];
                long trap = _emulator.RegisterVtableMethod($"basic_streambuf<{k.Name}>::{slot} (base)", () => BaseVirtual(k, slot));
                Write32(table + (i * 4), ArmEmulator.ThumbEntry(trap));
            }

            k.StreambufVtable = table;
            return table;
        }

        private void BaseVirtual(Kind k, string slot)
        {
            switch (slot)
            {
                case "dtor":
                {
                    long sb = _frame.Arg(0);
                    bool free = (_frame.Arg(1) & 1) != 0;
                    long returnTo = _emulator.ReturnAddress;
                    DestroyStreambuf(sb, () =>
                    {
                        if (free)
                        {
                            _emulator.FreeHeap(sb);
                        }

                        Finish(sb, returnTo);
                    });
                    break;
                }

                case "uflow": Uflow(k); break;
                case "xsgetn": Xsgetn(k); break;
                case "xsputn": Xsputn(k); break;
                case "showmanyc": _frame.Return64(0); break;
                case "seekoff":
                case "seekpos":
                    WriteFpos(_frame.Arg(1), -1);
                    Done(_frame.Arg(1));
                    break;
                case "overflow":
                case "pbackfail":
                case "underflow": Done(k.EofRegister); break;
                case "setbuf": Done(_frame.Arg(0)); break;
                default: Done(0); break;
            }
        }

        private void SetArea(Kind k, long sb, int first, int next, int count, long firstValue, long nextValue, long lastValue)
        {
            Store(sb, first, firstValue);
            Store(sb, next, nextValue);
            Store(sb, count, (uint)((lastValue - nextValue) / k.Size));
        }

        private long EndOfArea(Kind k, long sb, int next, int count)
        {
            long pointer = Deref(sb, next);
            return pointer == 0 ? 0 : pointer + (unchecked((int)(uint)Deref(sb, count)) * (long)k.Size);
        }

        private void Advance(Kind k, long sb, int next, int count, int by)
        {
            Store(sb, count, (uint)(unchecked((int)(uint)Deref(sb, count)) - by));
            Store(sb, next, Deref(sb, next) + ((long)by * k.Size));
        }

        private long Step(Kind k, long sb, int next, int count, int by, bool returnOld)
        {
            long old = Deref(sb, next);
            Advance(k, sb, next, count, by);
            return returnOld ? old : Deref(sb, next);
        }

        private int GetAvailable(long sb)
        {
            long next = Deref(sb, IGnext);
            return next == 0 ? 0 : unchecked((int)(uint)Deref(sb, IGcount));
        }

        private int PutAvailable(long sb)
        {
            long next = Deref(sb, IPnext);
            return next == 0 ? 0 : unchecked((int)(uint)Deref(sb, IPcount));
        }

        private int Bump(Kind k, long sb)
        {
            long next = Deref(sb, IGnext);
            int c = ReadChar(k, next);
            Advance(k, sb, IGnext, IGcount, 1);
            return c;
        }

        private void Sgetc(Kind k)
        {
            long sb = _frame.Arg(0);
            if (GetAvailable(sb) > 0)
            {
                Done(ReadChar(k, Deref(sb, IGnext)));
                return;
            }

            TailCallVirtual(sb, SlotUnderflow);
        }

        private void Sbumpc(Kind k)
        {
            long sb = _frame.Arg(0);
            if (GetAvailable(sb) > 0)
            {
                Done(Bump(k, sb));
                return;
            }

            TailCallVirtual(sb, SlotUflow);
        }

        /// <summary>int_type snextc(): sbumpc, then sgetc - eof if the bump found nothing.</summary>
        private void Snextc(Kind k)
        {
            long sb = _frame.Arg(0);
            if (GetAvailable(sb) > 1)
            {
                Bump(k, sb);
                Done(ReadChar(k, Deref(sb, IGnext)));
                return;
            }

            long returnTo = _emulator.ReturnAddress;
            NextChar(k, sb, bumped =>
            {
                if (bumped == -1)
                {
                    Finish(k.EofRegister, returnTo);
                    return;
                }

                PeekChar(k, sb, c => Finish(CharRegister(k, c), returnTo));
            });
        }

        /// <summary>sgetc, continuation-style: underflow in the image when the buffer is dry.</summary>
        private void PeekChar(Kind k, long sb, Action<int> then)
        {
            if (GetAvailable(sb) > 0)
            {
                then(ReadChar(k, Deref(sb, IGnext)));
                return;
            }

            // A standard buffer of ours has nothing to read (stdin is at its end): no round trip.
            if (_hostBuffers.ContainsKey(sb))
            {
                then(-1);
                return;
            }

            CallGuest(VirtualSlot(sb, SlotUnderflow), [sb], r0 => then(CharResult(k, r0)));
        }

        /// <summary>sbumpc, continuation-style.</summary>
        private void NextChar(Kind k, long sb, Action<int> then)
        {
            if (GetAvailable(sb) > 0)
            {
                then(Bump(k, sb));
                return;
            }

            // A standard buffer of ours has nothing to read (stdin is at its end): no round trip.
            if (_hostBuffers.ContainsKey(sb))
            {
                then(-1);
                return;
            }

            CallGuest(VirtualSlot(sb, SlotUflow), [sb], r0 => then(CharResult(k, r0)));
        }

        /// <summary>The base uflow: underflow, then take the character it made available.</summary>
        private void Uflow(Kind k)
        {
            long sb = _frame.Arg(0);
            long returnTo = _emulator.ReturnAddress;
            CallGuest(VirtualSlot(sb, SlotUnderflow), [sb], r0 =>
            {
                int c = CharResult(k, r0);
                Finish(c == -1 || GetAvailable(sb) == 0 ? k.EofRegister : Bump(k, sb), returnTo);
            });
        }

        private void Sputc(Kind k)
        {
            long sb = _frame.Arg(0);
            int c = (int)(_frame.Arg(1) & (k.Wide ? 0xFFFF : 0xFF));
            if (PutAvailable(sb) > 0)
            {
                WriteChars(k, Deref(sb, IPnext), [c], 0, 1);
                Advance(k, sb, IPnext, IPcount, 1);
                Done(c);
                return;
            }

            if (IsHostOutput(sb))
            {
                HostWrite(k, sb, [c]);
                Done(c);
                return;
            }

            TailCallVirtual(sb, SlotOverflow);
        }

        /// <summary>
        /// sungetc / sputbackc(c): step back over the last character read when there is one (and,
        /// for sputbackc, it is <paramref name="c"/>); otherwise pbackfail.
        /// </summary>
        private void PutBack(Kind k, long sb, int? c)
        {
            long next = Deref(sb, IGnext);
            long first = Deref(sb, IGfirst);
            if (next != 0 && first < next && (c is null || ReadChar(k, next - k.Size) == c))
            {
                Advance(k, sb, IGnext, IGcount, -1);
                Done(ReadChar(k, next - k.Size));
                return;
            }

            // pbackfail() for sungetc, pbackfail(to_int_type(c)) for sputbackc.
            _emulator.WriteRegister(Arm.UC_ARM_REG_R1, c is null ? k.EofRegister : c.Value);
            TailCallVirtual(sb, SlotPbackfail);
        }

        /// <summary>Continuation form of <see cref="PutBack"/>.</summary>
        private void PutBackChar(Kind k, long sb, int? c, Action<int> then)
        {
            long next = Deref(sb, IGnext);
            long first = Deref(sb, IGfirst);
            if (next != 0 && first < next && (c is null || ReadChar(k, next - k.Size) == c))
            {
                Advance(k, sb, IGnext, IGcount, -1);
                then(ReadChar(k, next - k.Size));
                return;
            }

            CallGuest(VirtualSlot(sb, SlotPbackfail), [sb, c is null ? k.EofRegister : c.Value], r0 => then(CharResult(k, r0)));
        }

        /// <summary>
        /// Writes elements through the put area, calling the image's overflow for each one that
        /// does not fit - which is how a stringbuf grows. Reports how many were written.
        /// </summary>
        private void PutChars(Kind k, long sb, int[] chars, int index, Action<int> done)
        {
            if (IsHostOutput(sb))
            {
                HostWrite(k, sb, chars[index..]);
                done(chars.Length);
                return;
            }

            while (index < chars.Length)
            {
                int room = PutAvailable(sb);
                if (room <= 0)
                {
                    int at = index;
                    CallGuest(VirtualSlot(sb, SlotOverflow), [sb, chars[at]], r0 =>
                    {
                        if (CharResult(k, r0) == -1 && !(k.Wide && chars[at] == 0xFFFF))
                        {
                            done(at);
                            return;
                        }

                        PutChars(k, sb, chars, at + 1, done);
                    });
                    return;
                }

                int count = Math.Min(room, chars.Length - index);
                WriteChars(k, Deref(sb, IPnext), chars, index, count);
                Advance(k, sb, IPnext, IPcount, count);
                index += count;
            }

            done(chars.Length);
        }

        private void Xsputn(Kind k)
        {
            long sb = _frame.Arg(0);
            long source = _frame.Arg(1);
            long count = Arg64();
            long returnTo = _emulator.ReturnAddress;
            int[] chars = count <= 0 ? [] : ReadChars(k, source, (int)Math.Min(count, CallFrame.SaneLengthLimit / k.Size));
            PutChars(k, sb, chars, 0, written => Finish64(written, returnTo));
        }

        private void Xsgetn(Kind k)
        {
            long sb = _frame.Arg(0);
            long destination = _frame.Arg(1);
            long count = Arg64();
            long returnTo = _emulator.ReturnAddress;
            GetChars(k, sb, destination, count, 0, got => Finish64(got, returnTo));
        }

        private void GetChars(Kind k, long sb, long destination, long count, long done, Action<long> finished)
        {
            while (done < count)
            {
                int available = GetAvailable(sb);
                if (available <= 0)
                {
                    long at = done;
                    CallGuest(VirtualSlot(sb, SlotUflow), [sb], r0 =>
                    {
                        int c = CharResult(k, r0);
                        if (c == -1)
                        {
                            finished(at);
                            return;
                        }

                        WriteChars(k, destination + (at * k.Size), [c], 0, 1);
                        GetChars(k, sb, destination, count, at + 1, finished);
                    });
                    return;
                }

                int take = (int)Math.Min(available, count - done);
                long next = Deref(sb, IGnext);
                _emulator.WriteMemory(destination + (done * k.Size), _emulator.ReadMemory(next, take * k.Size));
                Advance(k, sb, IGnext, IGcount, take);
                done += take;
            }

            finished(done);
        }

        /// <summary>
        /// fpos&lt;_Mbstatet&gt; as VC11 lays it out: streamoff _Myoff (8), fpos_t _Fpos (8), then the
        /// conversion state (8). The offset it stands for is _Myoff + _Fpos.
        /// </summary>
        private void WriteFpos(long fpos, long offset)
        {
            if (fpos == 0)
            {
                return;
            }

            WriteInt64(fpos, offset);
            WriteInt64(fpos + 8, 0);
            WriteInt64(fpos + 16, 0);
        }

        private long FposOffset(long fpos) => ReadInt64(fpos) + ReadInt64(fpos + 8);

        /// <summary>pubsync: the buffer's sync (slot 13); -1 is failure.</summary>
        private void SyncBuffer(long sb, Action<int> then)
        {
            if (IsHostOutput(sb))
            {
                HostSync(sb);
                then(0);
                return;
            }

            CallGuest(VirtualSlot(sb, SlotSync), [sb], r0 => then(unchecked((int)(uint)r0)));
        }

        /// <summary>
        /// pubseekoff(off, way, mode) into a scratch fpos; answers the offset it stands for (-1 on failure).
        /// </summary>
        private void SeekBuffer(long sb, long offset, long way, long mode, Action<long> then)
        {
            long position = _emulator.AllocateHeap(24);
            WriteFpos(position, -1);
            CallGuest(
                VirtualSlot(sb, SlotSeekoff),
                [sb, position, offset & 0xFFFFFFFF, (offset >> 32) & 0xFFFFFFFF, way, mode],
                _ =>
                {
                    long result = FposOffset(position);
                    _emulator.FreeHeap(position);
                    then(result);
                });
        }

        /// <summary>pubseekpos(pos, mode), the fpos passed by value: r2:r3 then four stack words.</summary>
        private void SeekBufferTo(long sb, long[] fposWords, long mode, Action<long> then)
        {
            long position = _emulator.AllocateHeap(24);
            WriteFpos(position, -1);
            CallGuest(
                VirtualSlot(sb, SlotSeekpos),
                [sb, position, .. fposWords, mode],
                _ =>
                {
                    long result = FposOffset(position);
                    _emulator.FreeHeap(position);
                    then(result);
                });
        }

        // ------------------------------------------------------------------------------
        // ios_base and basic_ios
        // ------------------------------------------------------------------------------

        private void RegisterIos(Dictionary<string, Action> h)
        {
            const string B = "ios_base@std@@";

            h[$"?getloc@{B}QBA?AVlocale@2@XZ"] = () =>
            {
                long ios = _frame.Arg(0), result = _frame.Arg(1);
                Locales.CopyLocale(Read32(ios + Locale), result);
                Done(result);
            };
            h[$"?flags@{B}QBAHXZ"] = () => Done(Read32(_frame.Arg(0) + Flags));
            h[$"?flags@{B}QAAHH@Z"] = () =>
            {
                long ios = _frame.Arg(0);
                uint old = Read32(ios + Flags);
                Write32(ios + Flags, _frame.Arg(1) & FmtMask);
                Done(old);
            };
            h[$"?setf@{B}QAAHH@Z"] = () =>
            {
                long ios = _frame.Arg(0);
                uint old = Read32(ios + Flags);
                Write32(ios + Flags, old | ((uint)_frame.Arg(1) & FmtMask));
                Done(old);
            };
            h[$"?setf@{B}QAAHHH@Z"] = () =>
            {
                long ios = _frame.Arg(0);
                uint old = Read32(ios + Flags);
                uint mask = (uint)_frame.Arg(2);
                Write32(ios + Flags, (old & ~mask) | ((uint)_frame.Arg(1) & mask & FmtMask));
                Done(old);
            };
            h[$"?unsetf@{B}QAAXH@Z"] = () =>
            {
                long ios = _frame.Arg(0);
                Write32(ios + Flags, Read32(ios + Flags) & ~(uint)_frame.Arg(1));
                Done(0);
            };

            // streamsize precision()/width() const, and the setters taking a streamsize in r2:r3.
            h[$"?precision@{B}QBA_JXZ"] = () => _frame.Return64(ReadInt64(_frame.Arg(0) + Precision));
            h[$"?width@{B}QBA_JXZ"] = () => _frame.Return64(ReadInt64(_frame.Arg(0) + Width));
            h[$"?precision@{B}QAA_J_J@Z"] = () => SwapInt64(Precision);
            h[$"?width@{B}QAA_J_J@Z"] = () => SwapInt64(Width);

            h[$"?rdstate@{B}QBAHXZ"] = () => Done(Read32(_frame.Arg(0) + State));
            h[$"?good@{B}QBA_NXZ"] = () => Done(Read32(_frame.Arg(0) + State) == GoodBit ? 1 : 0);
            h[$"?eof@{B}QBA_NXZ"] = () => Done((Read32(_frame.Arg(0) + State) & EofBit) != 0 ? 1 : 0);
            h[$"?fail@{B}QBA_NXZ"] = () => Done((Read32(_frame.Arg(0) + State) & (FailBit | BadBit)) != 0 ? 1 : 0);
            h[$"?bad@{B}QBA_NXZ"] = () => Done((Read32(_frame.Arg(0) + State) & BadBit) != 0 ? 1 : 0);
            h[$"??B{B}QBAPAXXZ"] = () =>
            {
                long ios = _frame.Arg(0);
                Done((Read32(ios + State) & (FailBit | BadBit)) != 0 ? 0 : ios);
            };
            h[$"??7{B}QBA_NXZ"] = () => Done((Read32(_frame.Arg(0) + State) & (FailBit | BadBit)) != 0 ? 1 : 0);

            h[$"?exceptions@{B}QBAHXZ"] = () => Done(Read32(_frame.Arg(0) + Except));
            h[$"?exceptions@{B}QAAXH@Z"] = () => SetExceptions();
            h[$"?exceptions@{B}QAAXI@Z"] = () => SetExceptions();
            h[$"?clear@{B}QAAXH_N@Z"] = () => { ClearState(_frame.Arg(0), (uint)_frame.Arg(1), basicIos: false); Done(0); };
            h[$"?clear@{B}QAAXI@Z"] = () => { ClearState(_frame.Arg(0), (uint)_frame.Arg(1), basicIos: false); Done(0); };
            h[$"?setstate@{B}QAAXH_N@Z"] = () => { AddState(_frame.Arg(0), (uint)_frame.Arg(1), basicIos: false); Done(0); };
            h[$"?setstate@{B}QAAXI@Z"] = () => { AddState(_frame.Arg(0), (uint)_frame.Arg(1), basicIos: false); Done(0); };

            h[$"?imbue@{B}QAA?AVlocale@2@ABV32@@Z"] = () =>
            {
                long ios = _frame.Arg(0), result = _frame.Arg(1), incoming = _frame.Arg(2);
                long returnTo = _emulator.ReturnAddress;
                IosBaseImbue(ios, incoming, result, () => Finish(result, returnTo));
            };

            h[$"?_Ios_base_dtor@{B}CAXPAV12@@Z"] = () =>
            {
                long returnTo = _emulator.ReturnAddress;
                DestroyIosBase(_frame.Arg(0), () => Finish(0, returnTo));
            };
            h[$"??1{B}UAA@XZ"] = () =>
            {
                long returnTo = _emulator.ReturnAddress;
                DestroyIosBase(_frame.Arg(0), () => Finish(0, returnTo));
            };
            h[$"?_Init@{B}IAAXXZ"] = () => { InitIosBase(_frame.Arg(0)); Done(0); };

            // _Addstd: a standard stream, whose destruction is suppressed while it is open.
            h[$"?_Addstd@{B}SAXPAV12@@Z"] = () => { AddStd(_frame.Arg(0)); Done(0); };

            h[$"?xalloc@{B}SAHXZ"] = () => Done(_xallocIndex++);
            h[$"?iword@{B}QAAAAJH@Z"] = () => Done(FindArray(_frame.Arg(0), _frame.SignedArg(1)) + 8);
            h[$"?pword@{B}QAAAAPAXH@Z"] = () => Done(FindArray(_frame.Arg(0), _frame.SignedArg(1)) + 12);

            // register_callback(event_callback, int): pushed at the head of _Calls, { next, index, fn }.
            h[$"?register_callback@{B}QAAXP6AXW4event@12@AAV12@H@ZH@Z"] = () =>
            {
                long ios = _frame.Arg(0);
                long node = _emulator.AllocateHeap(12);
                Write32(node, Read32(ios + Calls));
                Write32(node + 4, _frame.Arg(2));
                Write32(node + 8, _frame.Arg(1));
                Write32(ios + Calls, node);
                Done(0);
            };

            // There is one C library under these streams, so synchronising with it is a no-op
            // that reports the previous setting, which starts true.
            bool sync = true;
            h[$"?sync_with_stdio@{B}SA_N_N@Z"] = () =>
            {
                bool old = sync;
                sync = (_frame.Arg(0) & 0xFF) != 0;
                Done(old ? 1 : 0);
            };

            // ios_base::Init: the standard streams already exist (they are built with the image's
            // imports), so the init object has nothing to do.
            h[$"??0Init@{B}QAA@XZ"] = () => Done(_frame.Arg(0));
            h[$"??1Init@{B}QAA@XZ"] = () => Done(0);

            // --- basic_ios ---
            Both(h, "??0{Ios}IAA@XZ", k =>
            {
                long ios = _frame.Arg(0);
                _emulator.WriteMemory(ios + 4, new byte[IosSize - 4]);
                Write32(ios, StreamVtable(k, "basic_ios", 0));
                Done(ios);
            });
            Both(h, "??0{Ios}QAA@PAV?$basic_streambuf@{T}@1@@Z", k =>
            {
                long ios = _frame.Arg(0);
                _emulator.WriteMemory(ios + 4, new byte[IosSize - 4]);
                Write32(ios, StreamVtable(k, "basic_ios", 0));
                InitIos(k, ios, _frame.Arg(1), standard: false);
                Done(ios);
            });
            Both(h, "??1{Ios}UAA@XZ", _ =>
            {
                long returnTo = _emulator.ReturnAddress;
                DestroyIosBase(_frame.Arg(0), () => Finish(0, returnTo));
            });
            Both(h, "?init@{Ios}IAAXPAV?$basic_streambuf@{T}@2@_N@Z", k =>
            {
                InitIos(k, _frame.Arg(0), _frame.Arg(1), standard: (_frame.Arg(2) & 0xFF) != 0);
                Done(0);
            });
            Both(h, "?clear@{Ios}QAAXH_N@Z", _ => { ClearState(_frame.Arg(0), (uint)_frame.Arg(1), basicIos: true); Done(0); });
            Both(h, "?clear@{Ios}QAAXI@Z", _ => { ClearState(_frame.Arg(0), (uint)_frame.Arg(1), basicIos: true); Done(0); });
            Both(h, "?setstate@{Ios}QAAXH_N@Z", _ => { AddState(_frame.Arg(0), (uint)_frame.Arg(1), basicIos: true); Done(0); });
            Both(h, "?setstate@{Ios}QAAXI@Z", _ => { AddState(_frame.Arg(0), (uint)_frame.Arg(1), basicIos: true); Done(0); });
            Both(h, "?rdbuf@{Ios}QBAPAV?$basic_streambuf@{T}@2@XZ", _ => Done(Read32(_frame.Arg(0) + Buffer)));

            // rdbuf(sb): swap in a new buffer and clear(goodbit) - badbit if it is null.
            Both(h, "?rdbuf@{Ios}QAAPAV?$basic_streambuf@{T}@2@PAV32@@Z", _ =>
            {
                long ios = _frame.Arg(0);
                long old = Read32(ios + Buffer);
                Write32(ios + Buffer, _frame.Arg(1));
                ClearState(ios, GoodBit, basicIos: true);
                Done(old);
            });
            Both(h, "?tie@{Ios}QBAPAV?$basic_ostream@{T}@2@XZ", _ => Done(Read32(_frame.Arg(0) + Tie)));
            Both(h, "?tie@{Ios}QAAPAV?$basic_ostream@{T}@2@PAV32@@Z", _ =>
            {
                long ios = _frame.Arg(0);
                long old = Read32(ios + Tie);
                Write32(ios + Tie, _frame.Arg(1));
                Done(old);
            });
            Both(h, "?fill@{Ios}QBA{C}XZ", k => Done(ReadFill(k, _frame.Arg(0))));
            Both(h, "?fill@{Ios}QAA{C}{C}@Z", k =>
            {
                long ios = _frame.Arg(0);
                int old = ReadFill(k, ios);
                Write32(ios + Fill, _frame.Arg(1) & (k.Wide ? 0xFFFF : 0xFF));
                Done(old);
            });

            // narrow/widen go through the stream's ctype facet; in "C" a byte is itself, and a
            // wide character without a single-byte form narrows to the default.
            Both(h, "?narrow@{Ios}QBAD{C}D@Z", k =>
            {
                int c = (int)(_frame.Arg(1) & (k.Wide ? 0xFFFF : 0xFF));
                Done(c <= 0xFF ? c : _frame.Arg(2) & 0xFF);
            });
            Both(h, "?widen@{Ios}QBA{C}D@Z", _ => Done(_frame.Arg(1) & 0xFF));

            // locale imbue(const locale&): ios_base::imbue (with its callbacks), then rdbuf()->pubimbue.
            Both(h, "?imbue@{Ios}QAA?AVlocale@2@ABV32@@Z", _ =>
            {
                long ios = _frame.Arg(0), result = _frame.Arg(1), incoming = _frame.Arg(2);
                long returnTo = _emulator.ReturnAddress;
                IosBaseImbue(ios, incoming, result, () =>
                {
                    long sb = Read32(ios + Buffer);
                    if (sb == 0)
                    {
                        Finish(result, returnTo);
                        return;
                    }

                    long discarded = _emulator.AllocateHeap(4);
                    PubImbue(sb, incoming, discarded, () => Locales.ReleaseLocimp(Read32(discarded), () =>
                    {
                        _emulator.FreeHeap(discarded);
                        Finish(result, returnTo);
                    }));
                });
            });

            Both(h, "?_Add_vtordisp1@{Ios}UAAXXZ", _ => Done(0));
            Both(h, "?_Add_vtordisp2@{Ios}UAAXXZ", _ => Done(0));
        }

        private void SwapInt64(int field)
        {
            long ios = _frame.Arg(0);
            long old = ReadInt64(ios + field);
            WriteInt64(ios + field, Arg64());
            _frame.Return64(old);
        }

        private int ReadFill(Kind k, long ios) => (int)(Read32(ios + Fill) & (k.Wide ? 0xFFFF : 0xFF));

        private void SetExceptions()
        {
            long ios = _frame.Arg(0);
            Write32(ios + Except, _frame.Arg(1) & StateMask);
            ClearState(ios, Read32(ios + State), basicIos: false);
            Done(0);
        }

        private long IosOf(long stream)
        {
            long vbtable = _emulator.ReadUInt32(stream, 0);
            long ios = vbtable == 0 ? 0 : stream + unchecked((int)_emulator.ReadUInt32(vbtable + 4, 0x7FFFFFFF));
            if (vbtable == 0 || !_emulator.Cpu.IsMapped(ios) || !_emulator.Cpu.IsMapped(ios + IosSize - 1))
            {
                Note($"stream 0x{stream:X8} has no readable vbtable (word 0x{vbtable:X8}) - treated as a failed stream");
                return _deadIos != 0 ? _deadIos : (_deadIos = DeadIos());
            }

            return ios;
        }

        /// <summary>A basic_ios in permanent badbit, for streams we cannot find the ios of.</summary>
        private long DeadIos()
        {
            long ios = _emulator.AllocateHeap(IosSize);
            _emulator.WriteMemory(ios, new byte[IosSize]);
            Write32(ios + State, BadBit);
            return ios;
        }

        /// <summary>
        /// clear(state): the new state (badbit added when basic_ios has no buffer); if it meets the
        /// exception mask, an <c>ios_base::failure</c> is thrown when the handler returns.
        /// </summary>
        private void ClearState(long ios, uint state, bool basicIos)
        {
            if (basicIos && Read32(ios + Buffer) == 0)
            {
                state |= BadBit;
            }

            state &= StateMask;
            Write32(ios + State, state);
            uint raised = state & Read32(ios + Except);
            if (raised != 0 && ios != _deadIos)
            {
                _pendingFailure = (raised & BadBit) != 0 ? "ios_base::badbit set"
                    : (raised & FailBit) != 0 ? "ios_base::failbit set"
                    : "ios_base::eofbit set";
            }
        }

        private void AddState(long ios, uint add, bool basicIos)
        {
            if (add != GoodBit)
            {
                ClearState(ios, Read32(ios + State) | add, basicIos);
            }
        }

        /// <summary>basic_ios::setstate: what every stream operation here reports through.</summary>
        private void SetState(long ios, uint add) => AddState(ios, add, basicIos: true);

        /// <summary>ios_base::_Init: the default format state, and <c>_Ploc = new locale</c>.</summary>
        private void InitIosBase(long ios)
        {
            Write32(ios + Stdstr, 0);
            Write32(ios + Except, GoodBit);
            Write32(ios + Flags, SkipWs | Dec);
            WriteInt64(ios + Precision, 6);
            WriteInt64(ios + Width, 0);
            Write32(ios + Arr, 0);
            Write32(ios + Calls, 0);
            Write32(ios + State, GoodBit);
            Write32(ios + Locale, Locales.NewLocaleObject());
        }

        /// <summary>basic_ios::init(sb, isstd).</summary>
        private void InitIos(Kind k, long ios, long sb, bool standard)
        {
            Write32(ios + Buffer, sb);
            Write32(ios + Tie, 0);
            Write32(ios + Fill, ' ');
            _ = k;
            InitIosBase(ios);
            if (sb == 0)
            {
                Write32(ios + State, BadBit);
            }

            if (standard)
            {
                AddStd(ios);
            }
        }

        private void AddStd(long ios)
        {
            Write32(ios + Stdstr, 1);
            _stdOpens[ios] = _stdOpens.GetValueOrDefault(ios) + 1;
        }

        /// <summary>
        /// ~ios_base (_Ios_base_dtor): a standard stream is left alone while it is open; otherwise
        /// the erase_event callbacks run, the iword/pword and callback lists are freed and the
        /// locale is deleted.
        /// </summary>
        private void DestroyIosBase(long ios, Action then)
        {
            if (Read32(ios + Stdstr) != 0 && _stdOpens.TryGetValue(ios, out int opens))
            {
                _stdOpens[ios] = opens - 1;
                if (opens - 1 > 0)
                {
                    then();
                    return;
                }
            }

            CallCallbacks(ios, EraseEvent, () =>
            {
                for (long node = Read32(ios + Arr); node != 0;)
                {
                    long next = Read32(node);
                    _emulator.FreeHeap(node);
                    node = next;
                }

                for (long node = Read32(ios + Calls); node != 0;)
                {
                    long next = Read32(node);
                    _emulator.FreeHeap(node);
                    node = next;
                }

                Write32(ios + Arr, 0);
                Write32(ios + Calls, 0);
                long locale = Read32(ios + Locale);
                Write32(ios + Locale, 0);
                Locales.DeleteLocaleObject(locale, then);
            });
        }

        /// <summary>_Callfns(event): every registered callback, newest first, as (event, ios_base&amp;, index).</summary>
        private void CallCallbacks(long ios, int ev, Action then)
        {
            void Next(long node)
            {
                if (node == 0)
                {
                    then();
                    return;
                }

                long function = Read32(node + 8);
                long index = Read32(node + 4);
                long following = Read32(node);
                if (function == 0)
                {
                    Next(following);
                    return;
                }

                CallGuest(function, [ev, ios, index], _ => Next(following));
            }

            Next(Read32(ios + Calls));
        }

        /// <summary>ios_base::imbue: the old locale out, the new one in, then the imbue_event callbacks.</summary>
        private void IosBaseImbue(long ios, long incoming, long result, Action then)
        {
            long held = Read32(ios + Locale);
            if (held == 0)
            {
                held = Locales.NewLocaleObject();
                Write32(ios + Locale, held);
            }

            Locales.CopyLocale(held, result);
            Locales.AssignLocale(held, incoming, () => CallCallbacks(ios, ImbueEvent, then));
        }

        /// <summary>_Findarr(index): the iword/pword node for an index, made zeroed if missing.</summary>
        private long FindArray(long ios, int index)
        {
            for (long node = Read32(ios + Arr); node != 0; node = Read32(node))
            {
                if (unchecked((int)Read32(node + 4)) == index)
                {
                    return node;
                }
            }

            long fresh = _emulator.AllocateHeap(16);
            _emulator.WriteMemory(fresh, new byte[16]);
            Write32(fresh, Read32(ios + Arr));
            Write32(fresh + 4, index);
            Write32(ios + Arr, fresh);
            return fresh;
        }

        // ------------------------------------------------------------------------------
        // ios_base::failure
        // ------------------------------------------------------------------------------

        private long _failureVtable;

        /// <summary>
        /// Throws the <c>ios_base::failure</c> a <c>clear</c> asked for, into the game, from its
        /// call site. VC11's failure is a system_error: vfptr, exception's _Mywhat (+4) and
        /// _Mydofree (+8), then the error_code (+0xC value 1 = io_errc::stream, +0x10 category).
        /// </summary>
        private void ThrowPendingFailure(long returnTo)
        {
            if (_pendingFailure is not { } what)
            {
                return;
            }

            _pendingFailure = null;
            if (_failureVtable == 0)
            {
                _failureVtable = _emulator.AllocateHeap(8);
                long dtor = _emulator.RegisterVtableMethod("ios_base::failure::dtor", () =>
                {
                    if ((_frame.Arg(1) & 1) != 0)
                    {
                        _emulator.FreeHeap(_frame.Arg(0));
                    }

                    Done(_frame.Arg(0));
                });
                long whatSlot = _emulator.RegisterVtableMethod("ios_base::failure::what", () => Done(Read32(_frame.Arg(0) + 4)));
                Write32(_failureVtable, ArmEmulator.ThumbEntry(dtor));
                Write32(_failureVtable + 4, ArmEmulator.ThumbEntry(whatSlot));
            }

            byte[] text = Encoding.ASCII.GetBytes(what + "\0");
            long message = _emulator.AllocateHeap(text.Length);
            _emulator.WriteMemory(message, text);
            long failure = _emulator.AllocateHeap(0x14);
            _emulator.WriteMemory(failure, new byte[0x14]);
            Write32(failure, _failureVtable);
            Write32(failure + 4, message);
            Write32(failure + 0xC, 1);
            Note($"throwing ios_base::failure(\"{what}\") to 0x{returnTo:X8}");
            _emulator.WriteRegister(Arm.UC_ARM_REG_LR, returnTo);
            _emulator.Stubs.ThrowIntoGuest(new ThrownException(
                failure,
                [".?AVfailure@ios_base@std@@", ".?AVsystem_error@std@@", ".?AVruntime_error@std@@", ".?AVexception@std@@"]));
        }
    }
}

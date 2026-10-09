using System.Globalization;
using System.Text;

namespace WPR.Wp8Native
{
    using Arm = UnicornEngine.Const.Arm;

    /// <summary>
    /// MSVCP110's <c>char</c> iostreams: basic_streambuf, basic_ios, basic_istream,
    /// basic_ostream and basic_iostream, as the ARM build of Visual C++ 2012 lays them out.
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
    /// <b>Layouts</b>, read off the games' own inlined code rather than assumed:
    /// <list type="bullet">
    /// <item>basic_streambuf (0x38): vfptr; _Gfirst +4; _Pfirst +8; _IGfirst +0xC; _IPfirst +0x10;
    /// _Gnext +0x14; _Pnext +0x18; _IGnext +0x1C; _IPnext +0x20; _Gcount +0x24; _Pcount +0x28;
    /// _IGcount +0x2C; _IPcount +0x30; _Plocale +0x34. No lock member. A game's basic_stringbuf
    /// then puts _Seekhigh at +0x38 and its state at +0x3C - Classic's overflow reads +0x3C.</item>
    /// <item>basic_ios (0x48): vfptr; _Mystate +0xC; _Except +0x10; _Fmtfl +0x14; _Prec +0x18 (8);
    /// _Wide +0x20 (8); _Ploc +0x30; _Mystrbuf +0x38; _Tiestr +0x3C; _Fillch +0x40 - the ostream
    /// sentry reads the state at +0xC, the buffer at +0x38 and the tie at +0x3C.</item>
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
    /// <b>Virtuals live in the game.</b> basic_stringbuf's overflow, underflow, pbackfail and
    /// seekoff/seekpos are game code (vtable slots 3, 6, 4, 10, 11), so the base implementations
    /// here - xsputn, xsgetn, uflow, and the non-virtual sputc/sbumpc/sgetc/snextc - call back into
    /// the image through <see cref="ArmEmulator.CallEmulated(string,long,ReadOnlySpan{long},Action,bool)"/>
    /// whenever the buffer they are working on runs dry. Those that would only ever tail-call do
    /// so, leaving lr alone so the virtual returns straight to the caller.
    /// </para>
    /// </remarks>
    public sealed class StreamLibrary
    {
        private const int Eof = -1;

        // ios_base::iostate
        private const uint GoodBit = 0, EofBit = 1, FailBit = 2, BadBit = 4;

        // ios_base::fmtflags (VC11)
        private const uint SkipWs = 0x0001, UnitBuf = 0x0002, Uppercase = 0x0004, ShowBase = 0x0008,
            ShowPos = 0x0020, Left = 0x0040, Internal = 0x0100, Dec = 0x0200, Oct = 0x0400, Hex = 0x0800;

        // basic_streambuf fields
        private const int IGfirst = 0x0C, IPfirst = 0x10, Gfirst = 0x04, Pfirst = 0x08, Gnext = 0x14, Pnext = 0x18,
            IGnext = 0x1C, IPnext = 0x20, Gcount = 0x24, Pcount = 0x28, IGcount = 0x2C, IPcount = 0x30, Plocale = 0x34;

        // basic_ios fields
        private const int State = 0x0C, Except = 0x10, Flags = 0x14, Precision = 0x18, Width = 0x20, Locale = 0x30,
            Buffer = 0x38, Tie = 0x3C, Fill = 0x40, IosSize = 0x48;

        // basic_streambuf<char> vtable slots
        private const int SlotOverflow = 3, SlotUnderflow = 6, SlotUflow = 7, SlotXsputn = 9, SlotSync = 13;

        private const string Sb = "basic_streambuf@DU?$char_traits@D@std@@@std@@";
        private const string Ios = "basic_ios@DU?$char_traits@D@std@@@std@@";
        private const string Os = "basic_ostream@DU?$char_traits@D@std@@@std@@";
        private const string Is = "basic_istream@DU?$char_traits@D@std@@@std@@";
        private const string IoS = "basic_iostream@DU?$char_traits@D@std@@@std@@";

        private readonly ArmEmulator _emulator;
        private readonly CallFrame _frame;
        private long _streambufVtable;
        private readonly Dictionary<int, long> _vbtables = new();

        public StreamLibrary(ArmEmulator emulator, CallFrame frame)
        {
            _emulator = emulator;
            _frame = frame;
        }

        /// <summary>What the streams did, for the report.</summary>
        public List<string> Log { get; } = new();

        private void Note(string line)
        {
            if (Log.Count < 200)
            {
                Log.Add(line);
            }
        }

        public void RegisterInto(Dictionary<string, Action> handlers)
        {
            // --- basic_streambuf ---
            handlers[$"??0?${Sb}IAA@XZ"] = ConstructStreambuf;
            handlers[$"??1?${Sb}UAA@XZ"] = () => _frame.Return(0);
            handlers[$"?_Init@?${Sb}IAAXXZ"] = () => { InitStreambuf(_frame.Arg(0)); _frame.Return(0); };
            handlers[$"?setg@?${Sb}IAAXPAD00@Z"] = () =>
            {
                SetGet(_frame.Arg(0), _frame.Arg(1), _frame.Arg(2), _frame.Arg(3));
                _frame.Return(0);
            };
            handlers[$"?_Pninc@?${Sb}IAAPADXZ"] = () =>
            {
                long sb = _frame.Arg(0);
                long next = Deref(sb, IPnext);
                Store(sb, IPnext, next + 1);
                Store(sb, IPcount, (uint)(Deref(sb, IPcount) - 1));
                _frame.Return(next);
            };
            handlers[$"?_Lock@?${Sb}UAAXXZ"] = () => _frame.Return(0);
            handlers[$"?_Unlock@?${Sb}UAAXXZ"] = () => _frame.Return(0);
            handlers[$"?showmanyc@?${Sb}MAA_JXZ"] = () => _frame.Return64(0);
            handlers[$"?sync@?${Sb}MAAHXZ"] = () => _frame.Return(0);
            handlers[$"?setbuf@?${Sb}MAAPAV12@PAD_J@Z"] = () => _frame.Return(_frame.Arg(0));
            handlers[$"?imbue@?${Sb}MAAXABVlocale@2@@Z"] = () => _frame.Return(0);
            handlers[$"?sgetc@?${Sb}QAAHXZ"] = Sgetc;
            handlers[$"?sbumpc@?${Sb}QAAHXZ"] = Sbumpc;
            handlers[$"?snextc@?${Sb}QAAHXZ"] = Snextc;
            handlers[$"?sputc@?${Sb}QAAHD@Z"] = Sputc;
            handlers[$"?sputn@?${Sb}QAA_JPBD_J@Z"] = () => TailCallVirtual(_frame.Arg(0), SlotXsputn);
            handlers[$"?xsputn@?${Sb}MAA_JPBD_J@Z"] = Xsputn;
            handlers[$"?xsgetn@?${Sb}MAA_JPAD_J@Z"] = Xsgetn;
            handlers[$"?uflow@?${Sb}MAAHXZ"] = Uflow;

            // --- basic_ios ---
            handlers[$"??0?${Ios}IAA@XZ"] = () =>
            {
                ZeroIos(_frame.Arg(0));
                _frame.Return(_frame.Arg(0));
            };
            handlers[$"??1?${Ios}UAA@XZ"] = () => _frame.Return(0);
            handlers[$"?setstate@?${Ios}QAAXH_N@Z"] = () =>
            {
                SetState(_frame.Arg(0), (uint)_frame.Arg(1));
                _frame.Return(0);
            };
            handlers[$"?widen@?${Ios}QBADD@Z"] = () => _frame.Return(_frame.Arg(1) & 0xFF);
            handlers[$"?_Add_vtordisp1@?${Ios}UAAXXZ"] = () => _frame.Return(0);
            handlers[$"?_Add_vtordisp2@?${Ios}UAAXXZ"] = () => _frame.Return(0);

            // --- constructors / destructors of the streams ---
            handlers[$"??0?${Os}QAA@PAV?$basic_streambuf@DU?$char_traits@D@std@@@1@_N@Z"] = () => ConstructStream(_frame.Arg(0), _frame.Arg(1), _frame.Arg(3), iosOffset: 0x08, isInput: false);
            handlers[$"??0?${Is}QAA@PAV?$basic_streambuf@DU?$char_traits@D@std@@@1@_N@Z"] = () => ConstructStream(_frame.Arg(0), _frame.Arg(1), _frame.Arg(3), iosOffset: 0x10, isInput: true);
            handlers[$"??0?${IoS}QAA@PAV?$basic_streambuf@DU?$char_traits@D@std@@@1@@Z"] = ConstructIostream;
            handlers[$"??1?${Os}UAA@XZ"] = () => _frame.Return(0);
            handlers[$"??1?${Is}UAA@XZ"] = () => _frame.Return(0);
            handlers[$"??1?${IoS}UAA@XZ"] = () => _frame.Return(0);
            handlers[$"?_Add_vtordisp1@?${Is}UAAXXZ"] = () => _frame.Return(0);
            handlers[$"?_Add_vtordisp2@?${Os}UAAXXZ"] = () => _frame.Return(0);

            // --- basic_ostream ---
            handlers[$"?flush@?${Os}QAAAAV12@XZ"] = () => _frame.Return(_frame.Arg(0));
            handlers[$"?_Osfx@?${Os}QAAXXZ"] = () => _frame.Return(0);
            handlers["?endl@std@@YAAAV?$basic_ostream@DU?$char_traits@D@std@@@1@AAV21@@Z"] = () =>
                WriteFormatted(_frame.Arg(0), "\n", pad: false);
            handlers[$"??6?${Os}QAAAAV01@H@Z"] = () => WriteNumber((int)_frame.Arg(1), signed: true);
            handlers[$"??6?${Os}QAAAAV01@I@Z"] = () => WriteNumber((uint)_frame.Arg(1), signed: false);
            handlers[$"??6?${Os}QAAAAV01@K@Z"] = () => WriteNumber((uint)_frame.Arg(1), signed: false);
            handlers[$"??6?${Os}QAAAAV01@G@Z"] = () => WriteNumber((ushort)_frame.Arg(1), signed: false);
            handlers[$"??6?${Os}QAAAAV01@_J@Z"] = () => WriteNumber(Arg64(), signed: true);
            handlers[$"??6?${Os}QAAAAV01@_K@Z"] = () => WriteNumber(Arg64(), signed: false);

            // operator<<(ostream& (*)(ostream&)) - endl, flush, ends: the function returns its
            // argument, which is what operator<< returns, so this is a plain tail call.
            handlers[$"??6?${Os}QAAAAV01@P6AAAV01@AAV01@@Z@Z"] = () =>
            {
                long function = _frame.Arg(1);
                _emulator.ContinueAt(function);
            };

            // operator<<(ios_base& (*)(ios_base&)) - hex, dec, ...: called on the ios, returns *this.
            handlers[$"??6?${Os}QAAAAV01@P6AAAVios_base@1@AAV21@@Z@Z"] = () =>
            {
                long self = _frame.Arg(0);
                long function = _frame.Arg(1);
                long returnTo = _emulator.ReturnAddress;
                CallGuest(function, [IosOf(self)], _ => Finish(self, returnTo));
            };

            // --- basic_istream ---
            handlers[$"?_Ipfx@?${Is}QAA_N_N@Z"] = () =>
            {
                long self = _frame.Arg(0);
                bool noSkip = (_frame.Arg(1) & 0xFF) != 0;
                long returnTo = _emulator.ReturnAddress;
                Prefix(self, noSkip, ok => Finish(ok ? 1 : 0, returnTo));
            };
            handlers[$"??5?${Is}QAAAAV01@AA_K@Z"] = () => ReadUnsigned(8);
            handlers[$"??5?${Is}QAAAAV01@AAK@Z"] = () => ReadUnsigned(4);   // unsigned long&
            handlers[$"??5?${Is}QAAAAV01@AAI@Z"] = () => ReadUnsigned(4);   // unsigned int&
            handlers[$"??5?${Is}QAAAAV01@AAG@Z"] = () => ReadUnsigned(2);   // unsigned short&

            // int_type get(): one character, unformatted. Unimplemented, it answered 0 rather
            // than EOF, and Modern Combat 4's read-to-end loop span for ever.
            handlers[$"?get@?${Is}QAAHXZ"] = () =>
            {
                long self = _frame.Arg(0);
                long returnTo = _emulator.ReturnAddress;
                long ios = IosOf(self);
                long sb = Read32(ios + Buffer);
                _emulator.WriteUInt64(self + 8, 0); // _Chcount
                Prefix(self, noSkip: true, ok =>
                {
                    if (!ok)
                    {
                        Finish(unchecked((uint)Eof), returnTo);
                        return;
                    }

                    NextChar(sb, bump: true, c =>
                    {
                        if (c == Eof)
                        {
                            SetState(ios, EofBit | FailBit);
                            Finish(unchecked((uint)Eof), returnTo);
                            return;
                        }

                        _emulator.WriteUInt64(self + 8, 1);
                        Finish(c & 0xFF, returnTo);
                    });
                });
            };
        }

        // ------------------------------------------------------------------------------
        // Data imports: std::cerr / std::cout / std::clog
        // ------------------------------------------------------------------------------

        /// <summary>
        /// A standard stream object for a data import: a well-formed ostream whose ios has no
        /// buffer and badbit set, so every insertion is a no-op exactly as the library defines it.
        /// </summary>
        public static long BuildDisabledOstream(ArmEmulator emulator)
        {
            long vbtable = emulator.AllocateHeap(8);
            emulator.WriteMemory(vbtable, [0, 0, 0, 0, 8, 0, 0, 0]);
            long stream = emulator.AllocateHeap(0x60);
            emulator.WriteMemory(stream, new byte[0x60]);
            emulator.WriteUInt32(stream, (uint)vbtable);
            long ios = stream + 8;
            emulator.WriteUInt32(ios + State, BadBit);
            emulator.WriteUInt32(ios + Flags, SkipWs | Dec);
            emulator.WriteMemory(ios + Precision, BitConverter.GetBytes(6L));
            emulator.WriteUInt32(ios + Fill, ' ');
            return stream;
        }

        // ------------------------------------------------------------------------------
        // Fields
        // ------------------------------------------------------------------------------

        private uint Read32(long address) => _emulator.ReadUInt32(address);

        private void Write32(long address, long value) => _emulator.WriteUInt32(address, (uint)value);

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

        private long Arg64() => (long)((ulong)(uint)_frame.Arg(2) | ((ulong)(uint)_frame.Arg(3) << 32));

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

        private long _deadIos;

        /// <summary>A basic_ios in permanent badbit, for streams we cannot find the ios of.</summary>
        private long DeadIos()
        {
            long ios = _emulator.AllocateHeap(IosSize);
            _emulator.WriteMemory(ios, new byte[IosSize]);
            Write32(ios + State, BadBit);
            return ios;
        }

        private void SetState(long ios, uint add)
        {
            uint state = Read32(ios + State) | add;
            if (Read32(ios + Buffer) == 0)
            {
                state |= BadBit;
            }

            Write32(ios + State, state);
        }

        private void Finish(long value, long returnTo)
        {
            _frame.Return(value);
            _emulator.ContinueAt(returnTo);
        }

        // ------------------------------------------------------------------------------
        // basic_streambuf
        // ------------------------------------------------------------------------------

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

        private void ConstructStreambuf()
        {
            long sb = _frame.Arg(0);
            _emulator.WriteMemory(sb, new byte[0x38]);
            Write32(sb, StreambufVtable());
            InitStreambuf(sb);
            long locale = _emulator.AllocateHeap(16);
            _emulator.WriteMemory(locale, new byte[16]);
            Write32(sb + Plocale, locale);
            _frame.Return(sb);
        }

        /// <summary>
        /// basic_streambuf's own vtable, for the moment between this constructor and the derived
        /// one that replaces it: every slot is the base behaviour, routed back through the same
        /// import names so they cannot disagree.
        /// </summary>
        private long StreambufVtable()
        {
            if (_streambufVtable != 0)
            {
                return _streambufVtable;
            }

            string[] slots =
            [
                "dtor", "_Lock", "_Unlock", "overflow", "pbackfail", "showmanyc", "underflow", "uflow",
                "xsgetn", "xsputn", "seekoff", "seekpos", "setbuf", "sync", "imbue",
            ];
            _streambufVtable = _emulator.AllocateHeap(slots.Length * 4);
            for (int i = 0; i < slots.Length; i++)
            {
                string slot = slots[i];
                long trap = _emulator.RegisterVtableMethod($"basic_streambuf::{slot} (base)", () => BaseVirtual(slot));
                Write32(_streambufVtable + (i * 4), ArmEmulator.ThumbEntry(trap));
            }

            return _streambufVtable;
        }

        private void BaseVirtual(string slot)
        {
            switch (slot)
            {
                case "uflow": Uflow(); break;
                case "xsgetn": Xsgetn(); break;
                case "xsputn": Xsputn(); break;
                case "showmanyc": _frame.Return64(0); break;
                case "seekoff":
                case "seekpos": _frame.Return64(-1); break;
                case "overflow":
                case "pbackfail":
                case "underflow": _frame.Return(unchecked((uint)Eof)); break;
                case "setbuf": _frame.Return(_frame.Arg(0)); break;
                default: _frame.Return(0); break;
            }
        }

        private void SetGet(long sb, long first, long next, long last)
        {
            Store(sb, IGfirst, first);
            Store(sb, IGnext, next);
            Store(sb, IGcount, (uint)(last - next));
        }

        private int GetAvailable(long sb)
        {
            long next = Deref(sb, IGnext);
            return next == 0 ? 0 : (int)Deref(sb, IGcount);
        }

        private int PutAvailable(long sb)
        {
            long next = Deref(sb, IPnext);
            return next == 0 ? 0 : (int)Deref(sb, IPcount);
        }

        private long VirtualSlot(long sb, int slot) => Read32(Read32(sb) + (slot * 4));

        /// <summary>Hands the call to a virtual with the same arguments; it returns to our caller.</summary>
        private void TailCallVirtual(long sb, int slot) => _emulator.ContinueAt(VirtualSlot(sb, slot));

        private void CallGuest(long function, long[] arguments, Action<long> then)
        {
            _emulator.CallEmulated("iostream callback", function, arguments, onReturn: () => then(_frame.Arg(0)), recycle: true);
        }

        private void Sgetc()
        {
            long sb = _frame.Arg(0);
            if (GetAvailable(sb) > 0)
            {
                _frame.Return(_emulator.ReadMemory(Deref(sb, IGnext), 1)[0]);
                return;
            }

            TailCallVirtual(sb, SlotUnderflow);
        }

        private int Bump(long sb)
        {
            long next = Deref(sb, IGnext);
            byte c = _emulator.ReadMemory(next, 1)[0];
            Store(sb, IGnext, next + 1);
            Store(sb, IGcount, (uint)(Deref(sb, IGcount) - 1));
            return c;
        }

        private void Sbumpc()
        {
            long sb = _frame.Arg(0);
            if (GetAvailable(sb) > 0)
            {
                _frame.Return(Bump(sb));
                return;
            }

            TailCallVirtual(sb, SlotUflow);
        }

        private void Snextc()
        {
            long sb = _frame.Arg(0);
            if (GetAvailable(sb) > 1)
            {
                Bump(sb);
                _frame.Return(_emulator.ReadMemory(Deref(sb, IGnext), 1)[0]);
                return;
            }

            long returnTo = _emulator.ReturnAddress;
            NextChar(sb, bump: true, bumped =>
            {
                if (bumped == Eof)
                {
                    Finish(unchecked((uint)Eof), returnTo);
                    return;
                }

                PeekChar(sb, c => Finish(c == Eof ? unchecked((uint)Eof) : c, returnTo));
            });
        }

        /// <summary>sgetc, continuation-style: underflow in the image when the buffer is dry.</summary>
        private void PeekChar(long sb, Action<int> then)
        {
            if (GetAvailable(sb) > 0)
            {
                then(_emulator.ReadMemory(Deref(sb, IGnext), 1)[0]);
                return;
            }

            CallGuest(VirtualSlot(sb, SlotUnderflow), [sb], r0 => then(unchecked((int)(uint)r0)));
        }

        /// <summary>sbumpc, continuation-style.</summary>
        private void NextChar(long sb, bool bump, Action<int> then)
        {
            if (GetAvailable(sb) > 0)
            {
                then(Bump(sb));
                return;
            }

            CallGuest(VirtualSlot(sb, SlotUflow), [sb], r0 => then(unchecked((int)(uint)r0)));
        }

        private void Uflow()
        {
            long sb = _frame.Arg(0);
            long returnTo = _emulator.ReturnAddress;
            CallGuest(VirtualSlot(sb, SlotUnderflow), [sb], r0 =>
            {
                int c = unchecked((int)(uint)r0);
                Finish(c == Eof || GetAvailable(sb) == 0 ? unchecked((uint)Eof) : Bump(sb), returnTo);
            });
        }

        private void Sputc()
        {
            long sb = _frame.Arg(0);
            byte c = (byte)_frame.Arg(1);
            if (PutAvailable(sb) > 0)
            {
                PutDirect(sb, c);
                _frame.Return(c);
                return;
            }

            TailCallVirtual(sb, SlotOverflow);
        }

        private void PutDirect(long sb, byte c)
        {
            long next = Deref(sb, IPnext);
            _emulator.WriteMemory(next, [c]);
            Store(sb, IPnext, next + 1);
            Store(sb, IPcount, (uint)(Deref(sb, IPcount) - 1));
        }

        /// <summary>
        /// Writes bytes through the put area, calling the image's overflow for each byte that
        /// does not fit - which is how a stringbuf grows. Reports how many were written.
        /// </summary>
        private void PutBytes(long sb, byte[] bytes, int index, Action<int> done)
        {
            while (index < bytes.Length)
            {
                int room = PutAvailable(sb);
                if (room <= 0)
                {
                    int at = index;
                    CallGuest(VirtualSlot(sb, SlotOverflow), [sb, bytes[at]], r0 =>
                    {
                        if (unchecked((int)(uint)r0) == Eof)
                        {
                            done(at);
                            return;
                        }

                        PutBytes(sb, bytes, at + 1, done);
                    });
                    return;
                }

                int count = Math.Min(room, bytes.Length - index);
                long next = Deref(sb, IPnext);
                _emulator.WriteMemory(next, bytes.AsSpan(index, count).ToArray());
                Store(sb, IPnext, next + count);
                Store(sb, IPcount, (uint)(room - count));
                index += count;
            }

            done(bytes.Length);
        }

        private void Xsputn()
        {
            long sb = _frame.Arg(0);
            long source = _frame.Arg(1);
            long count = Arg64();
            long returnTo = _emulator.ReturnAddress;
            byte[] bytes = count <= 0 ? [] : _emulator.ReadMemory(source, (int)Math.Min(count, CallFrame.SaneLengthLimit));
            PutBytes(sb, bytes, 0, written =>
            {
                _frame.Return64(written);
                _emulator.ContinueAt(returnTo);
            });
        }

        private void Xsgetn()
        {
            long sb = _frame.Arg(0);
            long destination = _frame.Arg(1);
            long count = Arg64();
            long returnTo = _emulator.ReturnAddress;
            GetBytes(sb, destination, count, 0, got =>
            {
                _frame.Return64(got);
                _emulator.ContinueAt(returnTo);
            });
        }

        private void GetBytes(long sb, long destination, long count, long done, Action<long> finished)
        {
            while (done < count)
            {
                int available = GetAvailable(sb);
                if (available <= 0)
                {
                    long at = done;
                    CallGuest(VirtualSlot(sb, SlotUflow), [sb], r0 =>
                    {
                        int c = unchecked((int)(uint)r0);
                        if (c == Eof)
                        {
                            finished(at);
                            return;
                        }

                        _emulator.WriteMemory(destination + at, [(byte)c]);
                        GetBytes(sb, destination, count, at + 1, finished);
                    });
                    return;
                }

                int take = (int)Math.Min(available, count - done);
                long next = Deref(sb, IGnext);
                _emulator.WriteMemory(destination + done, _emulator.ReadMemory(next, take));
                Store(sb, IGnext, next + take);
                Store(sb, IGcount, (uint)(available - take));
                done += take;
            }

            finished(done);
        }

        // ------------------------------------------------------------------------------
        // basic_ios and the stream constructors
        // ------------------------------------------------------------------------------

        private void ZeroIos(long ios)
        {
            // The vfptr is left as found: the derived constructor sets it.
            _emulator.WriteMemory(ios + 4, new byte[IosSize - 4]);
        }

        private void InitIos(long ios, long sb)
        {
            Write32(ios + Buffer, sb);
            Write32(ios + Tie, 0);
            Write32(ios + Fill, ' ');
            Write32(ios + State, sb == 0 ? BadBit : GoodBit);
            Write32(ios + Except, 0);
            Write32(ios + Flags, SkipWs | Dec);
            _emulator.WriteMemory(ios + Precision, BitConverter.GetBytes(6L));
            _emulator.WriteMemory(ios + Width, BitConverter.GetBytes(0L));
            if (Read32(ios + Locale) == 0)
            {
                long locale = _emulator.AllocateHeap(16);
                _emulator.WriteMemory(locale, new byte[16]);
                Write32(ios + Locale, locale);
            }
        }

        private long VbTable(int iosOffset)
        {
            if (!_vbtables.TryGetValue(iosOffset, out long table))
            {
                table = _emulator.AllocateHeap(8);
                _emulator.WriteMemory(table, [0, 0, 0, 0, .. BitConverter.GetBytes(iosOffset)]);
                _vbtables[iosOffset] = table;
            }

            return table;
        }

        private void ConstructStream(long self, long sb, long constructVirtualBases, int iosOffset, bool isInput)
        {
            if (constructVirtualBases != 0)
            {
                Write32(self, VbTable(iosOffset));
                ZeroIos(self + iosOffset);
            }

            if (isInput)
            {
                _emulator.WriteMemory(self + 8, BitConverter.GetBytes(0L)); // _Chcount
            }

            InitIos(IosOf(self), sb);
            Note($"{(isInput ? "istream" : "ostream")} at 0x{self:X8}, buffer 0x{sb:X8}{(constructVirtualBases != 0 ? ", most derived" : "")}");
            _frame.Return(self);
        }

        private void ConstructIostream()
        {
            long self = _frame.Arg(0);
            long sb = _frame.Arg(1);
            if (_frame.Arg(2) != 0)
            {
                // istream part [vbptr, _Chcount], ostream part [vbptr] at +0x10, ios at +0x18.
                Write32(self, VbTable(0x18));
                Write32(self + 0x10, VbTable(0x08));
                ZeroIos(self + 0x18);
            }

            _emulator.WriteMemory(self + 8, BitConverter.GetBytes(0L));
            InitIos(IosOf(self), sb);
            Note($"iostream at 0x{self:X8}, buffer 0x{sb:X8}");
            _frame.Return(self);
        }

        // ------------------------------------------------------------------------------
        // Formatted output
        // ------------------------------------------------------------------------------

        private void WriteNumber(long value, bool signed)
        {
            long self = _frame.Arg(0);
            long ios = IosOf(self);
            uint flags = Read32(ios + Flags);
            uint basefield = flags & (Dec | Oct | Hex);
            string text;
            if (basefield == Hex)
            {
                ulong bits = signed ? unchecked((ulong)value) : (ulong)value;
                text = bits.ToString((flags & Uppercase) != 0 ? "X" : "x", CultureInfo.InvariantCulture);
                if ((flags & ShowBase) != 0 && bits != 0) text = ((flags & Uppercase) != 0 ? "0X" : "0x") + text;
            }
            else if (basefield == Oct)
            {
                text = Convert.ToString(value, 8);
                if ((flags & ShowBase) != 0 && value != 0) text = "0" + text;
            }
            else
            {
                text = signed ? value.ToString(CultureInfo.InvariantCulture) : ((ulong)value).ToString(CultureInfo.InvariantCulture);
                if ((flags & ShowPos) != 0 && value >= 0) text = "+" + text;
            }

            WriteFormatted(self, text, pad: true);
        }

        /// <summary>
        /// The tail every inserter shares: sentry, padding to width with the fill character,
        /// write through the buffer, width(0), badbit on a short write, return *this.
        /// </summary>
        private void WriteFormatted(long self, string text, bool pad)
        {
            long ios = IosOf(self);
            long returnTo = _emulator.ReturnAddress;
            long sb = Read32(ios + Buffer);
            if (Read32(ios + State) != GoodBit || sb == 0)
            {
                SetState(ios, FailBit);
                Finish(self, returnTo);
                return;
            }

            if (pad)
            {
                long width = BitConverter.ToInt64(_emulator.ReadMemory(ios + Width, 8));
                char fill = (char)(Read32(ios + Fill) & 0xFF);
                if (width > text.Length)
                {
                    uint adjust = Read32(ios + Flags) & (Left | Internal | 0x0080);
                    string padding = new(fill, (int)(width - text.Length));
                    text = adjust == Left ? text + padding : padding + text;
                }

                _emulator.WriteMemory(ios + Width, BitConverter.GetBytes(0L));
            }

            byte[] bytes = Encoding.Latin1.GetBytes(text);
            PutBytes(sb, bytes, 0, written =>
            {
                if (written < bytes.Length)
                {
                    SetState(ios, BadBit);
                }

                Finish(self, returnTo);
            });
        }

        // ------------------------------------------------------------------------------
        // Formatted input
        // ------------------------------------------------------------------------------

        /// <summary>basic_istream::_Ipfx: state check, then skip leading whitespace unless asked not to.</summary>
        private void Prefix(long self, bool noSkip, Action<bool> then)
        {
            long ios = IosOf(self);
            long sb = Read32(ios + Buffer);
            if (Read32(ios + State) != GoodBit || sb == 0)
            {
                SetState(ios, FailBit);
                then(false);
                return;
            }

            if (noSkip || (Read32(ios + Flags) & SkipWs) == 0)
            {
                then(true);
                return;
            }

            void Skip()
            {
                PeekChar(sb, c =>
                {
                    if (c == Eof)
                    {
                        SetState(ios, EofBit | FailBit);
                        then(false);
                        return;
                    }

                    if (!char.IsWhiteSpace((char)c))
                    {
                        then(true);
                        return;
                    }

                    NextChar(sb, bump: true, _ => Skip());
                });
            }

            Skip();
        }

        /// <summary>operator&gt;&gt; for an unsigned integer of <paramref name="width"/> bytes.</summary>
        private void ReadUnsigned(int width)
        {
            long self = _frame.Arg(0);
            long target = _frame.Arg(1);
            long returnTo = _emulator.ReturnAddress;
            long ios = IosOf(self);
            long sb = Read32(ios + Buffer);

            Prefix(self, noSkip: false, ok =>
            {
                if (!ok)
                {
                    Finish(self, returnTo);
                    return;
                }

                uint basefield = Read32(ios + Flags) & (Dec | Oct | Hex);
                int radix = basefield == Hex ? 16 : basefield == Oct ? 8 : 10;
                ulong value = 0;
                int digits = 0;

                void Next()
                {
                    PeekChar(sb, c =>
                    {
                        int digit = c switch
                        {
                            >= '0' and <= '9' => c - '0',
                            >= 'a' and <= 'f' => c - 'a' + 10,
                            >= 'A' and <= 'F' => c - 'A' + 10,
                            _ => 99,
                        };

                        if (c == Eof || digit >= radix)
                        {
                            uint state = c == Eof ? EofBit : GoodBit;
                            if (digits == 0)
                            {
                                state |= FailBit;
                            }
                            else
                            {
                                _emulator.WriteMemory(target, BitConverter.GetBytes(value)[..width]);
                            }

                            if (state != GoodBit)
                            {
                                SetState(ios, state);
                            }

                            Finish(self, returnTo);
                            return;
                        }

                        value = unchecked((value * (ulong)radix) + (ulong)digit);
                        digits++;
                        NextChar(sb, bump: true, _ => Next());
                    });
                }

                Next();
            });
        }
    }
}

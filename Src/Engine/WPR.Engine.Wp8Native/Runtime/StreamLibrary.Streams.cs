namespace WPR.Wp8Native
{
    using static StreamNumbers;

    /// <summary>basic_ostream, basic_istream and basic_iostream for both element types.</summary>
    public sealed partial class StreamLibrary
    {
        // ------------------------------------------------------------------------------
        // Constructors and destructors
        // ------------------------------------------------------------------------------

        private void ConstructStream(Kind k, long self, long sb, long constructVirtualBases, int iosOffset, bool isInput)
        {
            if (constructVirtualBases != 0)
            {
                Write32(self, VbTable(iosOffset));
                _emulator.WriteMemory(self + iosOffset + 4, new byte[IosSize - 4]);
            }

            if (isInput)
            {
                WriteInt64(self + 8, 0); // _Chcount
            }

            long ios = IosOf(self);
            Write32(ios, StreamVtable(k, isInput ? "basic_istream" : "basic_ostream", iosOffset));
            InitIos(k, ios, sb, standard: false);
            Note($"{(isInput ? "istream" : "ostream")}<{k.Name}> at 0x{self:X8}, buffer 0x{sb:X8}{(constructVirtualBases != 0 ? ", most derived" : "")}");
            Done(self);
        }

        private void ConstructIostream(Kind k)
        {
            long self = _frame.Arg(0);
            long sb = _frame.Arg(1);
            if (_frame.Arg(2) != 0)
            {
                // istream part [vbptr, _Chcount], ostream part [vbptr] at +0x10, ios at +0x18.
                Write32(self, VbTable(0x18));
                Write32(self + 0x10, VbTable(0x08));
                _emulator.WriteMemory(self + 0x18 + 4, new byte[IosSize - 4]);
            }

            WriteInt64(self + 8, 0);
            long ios = IosOf(self);
            Write32(ios, StreamVtable(k, "basic_iostream", 0x18));
            InitIos(k, ios, sb, standard: false);
            Note($"iostream<{k.Name}> at 0x{self:X8}, buffer 0x{sb:X8}");
            Done(self);
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

        /// <summary>
        /// The vbase destructor (<c>??_D</c>) of a stream: its own destructor does nothing, then
        /// the virtual base basic_ios is destroyed.
        /// </summary>
        private void VbaseDestructor()
        {
            long returnTo = _emulator.ReturnAddress;
            DestroyIosBase(IosOf(_frame.Arg(0)), () => Finish(0, returnTo));
        }

        // ------------------------------------------------------------------------------
        // basic_ostream
        // ------------------------------------------------------------------------------

        private void RegisterOstream(Dictionary<string, Action> h)
        {
            Both(h, "??0{Os}QAA@PAV?$basic_streambuf@{T}@1@_N@Z", k => ConstructStream(k, _frame.Arg(0), _frame.Arg(1), _frame.Arg(3), iosOffset: 0x08, isInput: false));
            Both(h, "??0{Is}QAA@PAV?$basic_streambuf@{T}@1@_N@Z", k => ConstructStream(k, _frame.Arg(0), _frame.Arg(1), _frame.Arg(3), iosOffset: 0x10, isInput: true));
            Both(h, "??0{IoS}QAA@PAV?$basic_streambuf@{T}@1@@Z", ConstructIostream);
            Both(h, "??1{Os}UAA@XZ", _ => Done(0));
            Both(h, "??1{Is}UAA@XZ", _ => Done(0));
            Both(h, "??1{IoS}UAA@XZ", _ => Done(0));
            Both(h, "??_D{Os}QAAXXZ", _ => VbaseDestructor());
            Both(h, "??_D{Is}QAAXXZ", _ => VbaseDestructor());
            Both(h, "??_D{IoS}QAAXXZ", _ => VbaseDestructor());
            Both(h, "?_Add_vtordisp1@{Is}UAAXXZ", _ => Done(0));
            Both(h, "?_Add_vtordisp2@{Os}UAAXXZ", _ => Done(0));

            // basic_ostream& flush(): with a buffer, under a sentry, pubsync; -1 is badbit.
            Both(h, "?flush@{Os}QAAAAV12@XZ", k =>
            {
                long self = _frame.Arg(0);
                long returnTo = _emulator.ReturnAddress;
                FlushStream(k, self, () => Finish(self, returnTo));
            });
            Both(h, "?_Osfx@{Os}QAAXXZ", k =>
            {
                long returnTo = _emulator.ReturnAddress;
                Osfx(k, _frame.Arg(0), () => Finish(0, returnTo));
            });
            Both(h, "?osfx@{Os}QAAXXZ", k =>
            {
                long returnTo = _emulator.ReturnAddress;
                Osfx(k, _frame.Arg(0), () => Finish(0, returnTo));
            });
            Both(h, "?opfx@{Os}QAA_NXZ", k =>
            {
                long returnTo = _emulator.ReturnAddress;
                OstreamSentry(k, _frame.Arg(0), ok => Finish(ok ? 1 : 0, returnTo));
            });

            // endl: put(widen('\n')) then flush. ends: put('\0'). flush: flush().
            Both(h, "?endl@std@@YAAAV?$basic_ostream@{T}@1@AAV21@@Z", k => Unformatted(k, _frame.Arg(0), ['\n'], flush: true));
            Both(h, "?ends@std@@YAAAV?$basic_ostream@{T}@1@AAV21@@Z", k => Unformatted(k, _frame.Arg(0), [0], flush: false));
            Both(h, "?flush@std@@YAAAV?$basic_ostream@{T}@1@AAV21@@Z", k =>
            {
                long self = _frame.Arg(0);
                long returnTo = _emulator.ReturnAddress;
                FlushStream(k, self, () => Finish(self, returnTo));
            });

            Both(h, "?put@{Os}QAAAAV12@{C}@Z", k => Unformatted(k, _frame.Arg(0), [(int)(_frame.Arg(1) & (k.Wide ? 0xFFFF : 0xFF))], flush: false));

            // write(const C*, streamsize): streamsize is 64-bit, so r2:r3.
            Both(h, "?write@{Os}QAAAAV12@{PB}_J@Z", k =>
            {
                long count = Arg64();
                int[] chars = count <= 0 ? [] : ReadChars(k, _frame.Arg(1), (int)Math.Min(count, CallFrame.SaneLengthLimit / k.Size));
                Unformatted(k, _frame.Arg(0), chars, flush: false);
            });

            // pos_type tellp(): under a sentry, pubseekoff(0, cur, out) unless fail(), else -1.
            Both(h, "?tellp@{Os}QAA?AV?$fpos@H@2@XZ", k =>
            {
                long self = _frame.Arg(0), result = _frame.Arg(1);
                long returnTo = _emulator.ReturnAddress;
                long ios = IosOf(self);
                OstreamSentry(k, self, _ =>
                {
                    long sb = Read32(ios + Buffer);
                    if ((Read32(ios + State) & (FailBit | BadBit)) != 0 || sb == 0)
                    {
                        WriteFpos(result, -1);
                        Finish(result, returnTo);
                        return;
                    }

                    CallGuest(VirtualSlot(sb, SlotSeekoff), [sb, result, 0, 0, SeekCur, OpenOut], _ => Finish(result, returnTo));
                });
            });

            // seekp(off_type, seekdir) and seekp(pos_type): failbit when the buffer answers -1.
            Both(h, "?seekp@{Os}QAAAAV12@_JH@Z", k =>
            {
                long offset = Arg64(), way = _frame.Arg(4);
                SeekStream(k, _frame.Arg(0), output: true, (sb, then) => SeekBuffer(sb, offset, way, OpenOut, then));
            });
            Both(h, "?seekp@{Os}QAAAAV12@V?$fpos@H@2@@Z", k =>
            {
                long[] words = [_frame.Arg(2), _frame.Arg(3), _frame.Arg(4), _frame.Arg(5), _frame.Arg(6), _frame.Arg(7)];
                SeekStream(k, _frame.Arg(0), output: true, (sb, then) => SeekBufferTo(sb, words, OpenOut, then));
            });

            // The arithmetic inserters. short/int/long widen to long; in oct and hex the value is
            // reinterpreted as unsigned at its own width first, as VC11 does.
            Both(h, "??6{Os}QAAAAV01@F@Z", k => InsertInteger(k, unchecked((short)_frame.Arg(1)), signed: true, bits: 16));
            Both(h, "??6{Os}QAAAAV01@G@Z", k => InsertInteger(k, (ushort)_frame.Arg(1), signed: false, bits: 16));
            Both(h, "??6{Os}QAAAAV01@H@Z", k => InsertInteger(k, _frame.SignedArg(1), signed: true, bits: 32));
            Both(h, "??6{Os}QAAAAV01@I@Z", k => InsertInteger(k, (uint)_frame.Arg(1), signed: false, bits: 32));
            Both(h, "??6{Os}QAAAAV01@J@Z", k => InsertInteger(k, _frame.SignedArg(1), signed: true, bits: 32));
            Both(h, "??6{Os}QAAAAV01@K@Z", k => InsertInteger(k, (uint)_frame.Arg(1), signed: false, bits: 32));
            Both(h, "??6{Os}QAAAAV01@_J@Z", k => InsertInteger(k, Arg64(), signed: true, bits: 64));
            Both(h, "??6{Os}QAAAAV01@_K@Z", k => InsertInteger(k, Arg64(), signed: false, bits: 64));
            Both(h, "??6{Os}QAAAAV01@_N@Z", k =>
            {
                bool value = (_frame.Arg(1) & 0xFF) != 0;
                uint flags = Read32(IosOf(_frame.Arg(0)) + Flags);
                if ((flags & BoolAlpha) != 0)
                {
                    InsertField(k, _frame.Arg(0), new Formatted(value ? "true" : "false", 0));
                    return;
                }

                InsertInteger(k, value ? 1 : 0, signed: true, bits: 32);
            });

            // float in s0, double and long double in d0 (hard-float).
            Both(h, "??6{Os}QAAAAV01@M@Z", k => InsertFloat(k, _frame.FloatArg(0)));
            Both(h, "??6{Os}QAAAAV01@N@Z", k => InsertFloat(k, _frame.DoubleArg(0)));
            Both(h, "??6{Os}QAAAAV01@O@Z", k => InsertFloat(k, _frame.DoubleArg(0)));

            // const void*: VC11 writes %p, eight upper-case hex digits.
            Both(h, "??6{Os}QAAAAV01@PBX@Z", k => InsertField(k, _frame.Arg(0), new Formatted(((uint)_frame.Arg(1)).ToString("X8"), 0)));

            Both(h, "??6{Os}QAAAAV01@PAV?$basic_streambuf@{T}@1@@Z", InsertStreambuf);

            // operator<<(ostream& (*)(ostream&)) - endl, flush, ends: the function returns its
            // argument, which is what operator<< returns, so this is a plain tail call.
            Both(h, "??6{Os}QAAAAV01@P6AAAV01@AAV01@@Z@Z", _ => _emulator.ContinueAt(_frame.Arg(1)));

            // operator<<(ios_base& (*)(ios_base&)) / (basic_ios& (*)(basic_ios&)) - hex, dec, ...:
            // called on the ios, returns *this.
            Both(h, "??6{Os}QAAAAV01@P6AAAVios_base@1@AAV21@@Z@Z", _ => ApplyManipulator());
            Both(h, "??6{Os}QAAAAV01@P6AAAV?$basic_ios@{T}@1@AAV21@@Z@Z", _ => ApplyManipulator());
        }

        private void ApplyManipulator()
        {
            long self = _frame.Arg(0);
            long function = _frame.Arg(1);
            long returnTo = _emulator.ReturnAddress;
            CallGuest(function, [IosOf(self)], _ => Finish(self, returnTo));
        }

        /// <summary>
        /// The ostream sentry: if the stream is good and tied to another, flush that one first;
        /// answers good().
        /// </summary>
        private void OstreamSentry(Kind k, long self, Action<bool> then)
        {
            long ios = IosOf(self);
            long tie = Read32(ios + Tie);
            if (Read32(ios + State) == GoodBit && tie != 0 && tie != self)
            {
                FlushStream(k, tie, () => then(Read32(ios + State) == GoodBit));
                return;
            }

            then(Read32(ios + State) == GoodBit);
        }

        /// <summary>basic_ostream::flush, continuation-style.</summary>
        private void FlushStream(Kind k, long self, Action then)
        {
            long ios = IosOf(self);
            long sb = Read32(ios + Buffer);
            if (sb == 0)
            {
                then();
                return;
            }

            OstreamSentry(k, self, ok =>
            {
                if (!ok)
                {
                    then();
                    return;
                }

                SyncBuffer(sb, result =>
                {
                    if (result == -1)
                    {
                        SetState(ios, BadBit);
                    }

                    then();
                });
            });
        }

        /// <summary>_Osfx: with unitbuf set on a good stream, pubsync, badbit on -1.</summary>
        private void Osfx(Kind k, long self, Action then)
        {
            long ios = IosOf(self);
            long sb = Read32(ios + Buffer);
            _ = k;
            if (Read32(ios + State) != GoodBit || (Read32(ios + Flags) & UnitBuf) == 0 || sb == 0)
            {
                then();
                return;
            }

            SyncBuffer(sb, result =>
            {
                if (result == -1)
                {
                    SetState(ios, BadBit);
                }

                then();
            });
        }

        /// <summary>put / write / endl / ends: sentry, the elements through the buffer, badbit on a short write, _Osfx.</summary>
        private void Unformatted(Kind k, long self, int[] chars, bool flush)
        {
            long returnTo = _emulator.ReturnAddress;
            long ios = IosOf(self);
            OstreamSentry(k, self, ok =>
            {
                if (!ok)
                {
                    SetState(ios, BadBit);
                    Finish(self, returnTo);
                    return;
                }

                PutChars(k, Read32(ios + Buffer), chars, 0, written =>
                {
                    if (written < chars.Length)
                    {
                        SetState(ios, BadBit);
                    }

                    if (flush)
                    {
                        FlushStream(k, self, () => Finish(self, returnTo));
                        return;
                    }

                    Osfx(k, self, () => Finish(self, returnTo));
                });
            });
        }

        private void InsertInteger(Kind k, long value, bool signed, int bits)
        {
            long self = _frame.Arg(0);
            uint flags = Read32(IosOf(self) + Flags);
            InsertField(k, self, FormatInteger(value, signed, bits, flags));
        }

        private void InsertFloat(Kind k, double value)
        {
            long self = _frame.Arg(0);
            long ios = IosOf(self);
            InsertField(k, self, FormatFloat(value, Read32(ios + Flags), ReadInt64(ios + Precision)));
        }

        /// <summary>
        /// The tail every formatted inserter shares: sentry, padding to width() with fill(),
        /// width(0), write through the buffer (badbit on a short write), _Osfx, return *this.
        /// A sentry that fails writes nothing and changes no state, as VC11's num_put path does.
        /// </summary>
        private void InsertField(Kind k, long self, Formatted field)
        {
            long ios = IosOf(self);
            long returnTo = _emulator.ReturnAddress;
            OstreamSentry(k, self, ok =>
            {
                if (!ok)
                {
                    Finish(self, returnTo);
                    return;
                }

                string text = Pad(field, ReadInt64(ios + Width), (char)ReadFill(k, ios), Read32(ios + Flags));
                WriteInt64(ios + Width, 0);
                int[] chars = Elements(k, text);
                PutChars(k, Read32(ios + Buffer), chars, 0, written =>
                {
                    if (written < chars.Length)
                    {
                        SetState(ios, BadBit);
                    }

                    Osfx(k, self, () => Finish(self, returnTo));
                });
            });
        }

        /// <summary>
        /// operator&lt;&lt;(basic_streambuf*): copy until the source runs dry or this buffer
        /// refuses a character; badbit for a null source, failbit if nothing was copied.
        /// </summary>
        private void InsertStreambuf(Kind k)
        {
            long self = _frame.Arg(0);
            long source = _frame.Arg(1);
            long returnTo = _emulator.ReturnAddress;
            long ios = IosOf(self);
            OstreamSentry(k, self, ok =>
            {
                void End(uint state, bool copied)
                {
                    WriteInt64(ios + Width, 0);
                    SetState(ios, source == 0 ? BadBit : copied ? state : state | FailBit);
                    Osfx(k, self, () => Finish(self, returnTo));
                }

                if (!ok || source == 0)
                {
                    End(GoodBit, copied: false);
                    return;
                }

                long sb = Read32(ios + Buffer);
                void Copy(int c, bool copied)
                {
                    if (c == -1)
                    {
                        End(GoodBit, copied);
                        return;
                    }

                    PutChars(k, sb, [c], 0, written =>
                    {
                        if (written == 0)
                        {
                            End(BadBit, copied);
                            return;
                        }

                        NextAfter(k, source, next => Copy(next, copied: true));
                    });
                }

                PeekChar(k, source, c => Copy(c, copied: false));
            });
        }

        /// <summary>snextc, continuation-style.</summary>
        private void NextAfter(Kind k, long sb, Action<int> then) =>
            NextChar(k, sb, bumped =>
            {
                if (bumped == -1)
                {
                    then(-1);
                    return;
                }

                PeekChar(k, sb, then);
            });

        /// <summary>seekp/seekg: (seekg clears eofbit first), a sentry, then the seek unless fail(); failbit on -1.</summary>
        private void SeekStream(Kind k, long self, bool output, Action<long, Action<long>> seek)
        {
            long returnTo = _emulator.ReturnAddress;
            long ios = IosOf(self);
            long sb = Read32(ios + Buffer);

            void Seek()
            {
                if ((Read32(ios + State) & (FailBit | BadBit)) != 0 || sb == 0)
                {
                    Finish(self, returnTo);
                    return;
                }

                seek(sb, result =>
                {
                    if (result == -1)
                    {
                        SetState(ios, FailBit);
                    }

                    Finish(self, returnTo);
                });
            }

            if (output)
            {
                OstreamSentry(k, self, _ => Seek());
                return;
            }

            Write32(ios + State, Read32(ios + State) & ~EofBit);
            Prefix(k, self, noSkip: true, _ => Seek());
        }

        // ------------------------------------------------------------------------------
        // basic_istream
        // ------------------------------------------------------------------------------

        private void RegisterIstream(Dictionary<string, Action> h)
        {
            Both(h, "?_Ipfx@{Is}QAA_N_N@Z", k =>
            {
                long self = _frame.Arg(0);
                bool noSkip = (_frame.Arg(1) & 0xFF) != 0;
                long returnTo = _emulator.ReturnAddress;
                Prefix(k, self, noSkip, ok => Finish(ok ? 1 : 0, returnTo));
            });
            Both(h, "?ipfx@{Is}QAA_N_N@Z", k =>
            {
                long self = _frame.Arg(0);
                bool noSkip = (_frame.Arg(1) & 0xFF) != 0;
                long returnTo = _emulator.ReturnAddress;
                Prefix(k, self, noSkip, ok => Finish(ok ? 1 : 0, returnTo));
            });
            Both(h, "?_Isfx@{Is}QAAXXZ", _ => Done(0));
            Both(h, "?isfx@{Is}QAAXXZ", _ => Done(0));
            Both(h, "?gcount@{Is}QBA_JXZ", _ => _frame.Return64(ReadInt64(_frame.Arg(0) + 8)));

            // int_type get(): one element, unformatted. Unimplemented, it answered 0 rather than
            // EOF, and Modern Combat 4's read-to-end loop span for ever.
            Both(h, "?get@{Is}QAA{I}XZ", k =>
            {
                long self = _frame.Arg(0);
                long returnTo = _emulator.ReturnAddress;
                GetOne(k, self, c => Finish(CharRegister(k, c), returnTo));
            });

            // get(C&): the element stored when there is one.
            Both(h, "?get@{Is}QAAAAV12@AA{C}@Z", k =>
            {
                long self = _frame.Arg(0), target = _frame.Arg(1);
                long returnTo = _emulator.ReturnAddress;
                GetOne(k, self, c =>
                {
                    if (c != -1)
                    {
                        WriteChars(k, target, [c], 0, 1);
                    }

                    Finish(self, returnTo);
                });
            });

            // get(C*, n[, delim]) stops before the delimiter; getline(C*, n[, delim]) extracts it.
            Both(h, "?get@{Is}QAAAAV12@{P}_J@Z", k => ReadLine(k, _frame.Arg(0), _frame.Arg(1), Arg64(), '\n', extractDelimiter: false));
            Both(h, "?get@{Is}QAAAAV12@{P}_J{C}@Z", k => ReadLine(k, _frame.Arg(0), _frame.Arg(1), Arg64(), (int)(_frame.Arg(4) & (k.Wide ? 0xFFFF : 0xFF)), extractDelimiter: false));
            Both(h, "?getline@{Is}QAAAAV12@{P}_J@Z", k => ReadLine(k, _frame.Arg(0), _frame.Arg(1), Arg64(), '\n', extractDelimiter: true));
            Both(h, "?getline@{Is}QAAAAV12@{P}_J{C}@Z", k => ReadLine(k, _frame.Arg(0), _frame.Arg(1), Arg64(), (int)(_frame.Arg(4) & (k.Wide ? 0xFFFF : 0xFF)), extractDelimiter: true));

            // ignore(streamsize n, int_type delim): r2:r3, then the delimiter on the stack.
            Both(h, "?ignore@{Is}QAAAAV12@_J{I}@Z", k => Ignore(k, _frame.Arg(0), Arg64(), CharResult(k, _frame.Arg(4))));

            // int_type peek(): sgetc under a sentry; eofbit at the end.
            Both(h, "?peek@{Is}QAA{I}XZ", k =>
            {
                long self = _frame.Arg(0);
                long returnTo = _emulator.ReturnAddress;
                long ios = IosOf(self);
                WriteInt64(self + 8, 0);
                Prefix(k, self, noSkip: true, ok =>
                {
                    if (!ok)
                    {
                        Finish(k.EofRegister, returnTo);
                        return;
                    }

                    PeekChar(k, Read32(ios + Buffer), c =>
                    {
                        if (c == -1)
                        {
                            SetState(ios, EofBit);
                        }

                        Finish(CharRegister(k, c), returnTo);
                    });
                });
            });

            // putback(c) / unget(): eofbit cleared first; badbit if the buffer cannot back up.
            Both(h, "?putback@{Is}QAAAAV12@{C}@Z", k => Unread(k, _frame.Arg(0), (int)(_frame.Arg(1) & (k.Wide ? 0xFFFF : 0xFF))));
            Both(h, "?unget@{Is}QAAAAV12@XZ", k => Unread(k, _frame.Arg(0), null));

            // basic_istream& read(C*, streamsize): rdbuf()->sgetn into the buffer, the count in
            // _Chcount (+8), eof|fail on a short read.
            Both(h, "?read@{Is}QAAAAV12@{P}_J@Z", k =>
            {
                long self = _frame.Arg(0), buffer = _frame.Arg(1), count = Arg64();
                long returnTo = _emulator.ReturnAddress;
                ReadBlock(k, self, buffer, count, () => Finish(self, returnTo));
            });

            // streamsize readsome(C*, n): only what in_avail() promises; eofbit if it says -1.
            Both(h, "?readsome@{Is}QAA_J{P}_J@Z", k =>
            {
                long self = _frame.Arg(0), buffer = _frame.Arg(1), count = Arg64();
                long returnTo = _emulator.ReturnAddress;
                long ios = IosOf(self);
                WriteInt64(self + 8, 0);
                Prefix(k, self, noSkip: true, ok =>
                {
                    if (!ok)
                    {
                        SetState(ios, FailBit);
                        Finish64(0, returnTo);
                        return;
                    }

                    long sb = Read32(ios + Buffer);
                    void WithAvailable(long available)
                    {
                        if (available < 0)
                        {
                            SetState(ios, EofBit);
                            Finish64(0, returnTo);
                            return;
                        }

                        if (count <= 0 || available == 0)
                        {
                            Finish64(0, returnTo);
                            return;
                        }

                        ReadBlock(k, self, buffer, Math.Min(count, available), () => Finish64(ReadInt64(self + 8), returnTo));
                    }

                    int inBuffer = GetAvailable(sb);
                    if (inBuffer > 0)
                    {
                        WithAvailable(inBuffer);
                        return;
                    }

                    CallGuest(VirtualSlot(sb, SlotShowmanyc), [sb], low => WithAvailable((long)((ulong)(uint)low | ((ulong)(uint)_frame.Arg(1) << 32))));
                });
            });

            // int sync(): -1 with no buffer; badbit and -1 when pubsync fails.
            Both(h, "?sync@{Is}QAAHXZ", k =>
            {
                long self = _frame.Arg(0);
                long returnTo = _emulator.ReturnAddress;
                long ios = IosOf(self);
                long sb = Read32(ios + Buffer);
                Prefix(k, self, noSkip: true, _ =>
                {
                    if (sb == 0)
                    {
                        Finish(unchecked((uint)-1), returnTo);
                        return;
                    }

                    SyncBuffer(sb, result =>
                    {
                        if (result == -1)
                        {
                            SetState(ios, BadBit);
                            Finish(unchecked((uint)-1), returnTo);
                            return;
                        }

                        Finish(0, returnTo);
                    });
                });
            });

            // pos_type tellg(): rdbuf()->pubseekoff(0, cur, in) unless fail(), else pos_type(-1).
            // MSVC on ARM passes this in r0 and the hidden pos_type* in r1 - read off Terraria's own
            // call site - and returns that pointer. The caller's buffer is handed straight to the
            // game's seekoff, which fills it.
            Both(h, "?tellg@{Is}QAA?AV?$fpos@H@2@XZ", k =>
            {
                long self = _frame.Arg(0);
                long result = _frame.Arg(1);
                long returnTo = _emulator.ReturnAddress;
                long ios = IosOf(self);
                Prefix(k, self, noSkip: true, _ =>
                {
                    long sb = Read32(ios + Buffer);
                    if ((Read32(ios + State) & (FailBit | BadBit)) != 0 || sb == 0)
                    {
                        WriteFpos(result, -1);
                        Finish(result, returnTo);
                        return;
                    }

                    CallGuest(VirtualSlot(sb, SlotSeekoff), [sb, result, 0, 0, SeekCur, OpenIn], _ => Finish(result, returnTo));
                });
            });

            // seekg(off_type, seekdir): off_type is 64-bit, so r2:r3, and the direction is on the
            // stack. C++11 clears eofbit first (a stream read to its end can be rewound).
            Both(h, "?seekg@{Is}QAAAAV12@_JH@Z", k =>
            {
                long offset = Arg64(), way = _frame.Arg(4);
                SeekStream(k, _frame.Arg(0), output: false, (sb, then) => SeekBuffer(sb, offset, way, OpenIn, then));
            });

            // seekg(pos_type): the 24-byte fpos by value - r2:r3 and four stack words.
            Both(h, "?seekg@{Is}QAAAAV12@V?$fpos@H@2@@Z", k =>
            {
                long[] words = [_frame.Arg(2), _frame.Arg(3), _frame.Arg(4), _frame.Arg(5), _frame.Arg(6), _frame.Arg(7)];
                SeekStream(k, _frame.Arg(0), output: false, (sb, then) => SeekBufferTo(sb, words, OpenIn, then));
            });

            // The arithmetic extractors.
            Both(h, "??5{Is}QAAAAV01@AAF@Z", k => ExtractInteger(k, s => s.AsSigned(16), 2));
            Both(h, "??5{Is}QAAAAV01@AAG@Z", k => ExtractInteger(k, s => (long?)s.AsUnsigned(16), 2));
            Both(h, "??5{Is}QAAAAV01@AAH@Z", k => ExtractInteger(k, s => s.AsSigned(32), 4));
            Both(h, "??5{Is}QAAAAV01@AAI@Z", k => ExtractInteger(k, s => (long?)s.AsUnsigned(32), 4));
            Both(h, "??5{Is}QAAAAV01@AAJ@Z", k => ExtractInteger(k, s => s.AsSigned(32), 4));
            Both(h, "??5{Is}QAAAAV01@AAK@Z", k => ExtractInteger(k, s => (long?)s.AsUnsigned(32), 4));
            Both(h, "??5{Is}QAAAAV01@AA_J@Z", k => ExtractInteger(k, s => s.AsSigned(64), 8));
            Both(h, "??5{Is}QAAAAV01@AA_K@Z", k => ExtractInteger(k, s => s.AsUnsigned(64) is { } v ? unchecked((long)v) : null, 8));
            Both(h, "??5{Is}QAAAAV01@AAPAX@Z", k => ExtractInteger(k, s => (long?)s.AsUnsigned(32), 4, forceHex: true));
            Both(h, "??5{Is}QAAAAV01@AA_N@Z", ExtractBool);
            Both(h, "??5{Is}QAAAAV01@AAM@Z", k => ExtractFloat(k, single: true));
            Both(h, "??5{Is}QAAAAV01@AAN@Z", k => ExtractFloat(k, single: false));
            Both(h, "??5{Is}QAAAAV01@AAO@Z", k => ExtractFloat(k, single: false));
            Both(h, "??5{Is}QAAAAV01@PAV?$basic_streambuf@{T}@1@@Z", ExtractStreambuf);

            // operator>>(istream& (*)(istream&)): the function returns its argument - a tail call.
            Both(h, "??5{Is}QAAAAV01@P6AAAV01@AAV01@@Z@Z", _ => _emulator.ContinueAt(_frame.Arg(1)));
            Both(h, "??5{Is}QAAAAV01@P6AAAVios_base@1@AAV21@@Z@Z", _ => ApplyManipulator());
            Both(h, "??5{Is}QAAAAV01@P6AAAV?$basic_ios@{T}@1@AAV21@@Z@Z", _ => ApplyManipulator());

            // ws: skip whitespace under a noskip sentry; eofbit (and no failbit) at the end.
            Both(h, "?ws@std@@YAAAV?$basic_istream@{T}@1@AAV21@@Z", k =>
            {
                long self = _frame.Arg(0);
                long returnTo = _emulator.ReturnAddress;
                long ios = IosOf(self);
                Prefix(k, self, noSkip: true, ok =>
                {
                    if (!ok)
                    {
                        Finish(self, returnTo);
                        return;
                    }

                    SkipSpace(k, Read32(ios + Buffer), atEnd =>
                    {
                        if (atEnd)
                        {
                            SetState(ios, EofBit);
                        }

                        Finish(self, returnTo);
                    });
                });
            });
        }

        private bool IsSpace(Kind k, int c) =>
            c != -1 && ((k.Wide ? LocaleLibrary.WideMask(c) : LocaleLibrary.NarrowMask(c)) & 0x8) != 0;

        /// <summary>Skips whitespace in a buffer; answers whether it reached the end.</summary>
        private void SkipSpace(Kind k, long sb, Action<bool> then)
        {
            PeekChar(k, sb, c =>
            {
                if (c == -1)
                {
                    then(true);
                    return;
                }

                if (!IsSpace(k, c))
                {
                    then(false);
                    return;
                }

                NextChar(k, sb, _ => SkipSpace(k, sb, then));
            });
        }

        /// <summary>
        /// basic_istream::_Ipfx, the istream sentry: if good, flush the tie, then (unless
        /// <paramref name="noSkip"/> or skipws is off) skip whitespace - eofbit at the end; failbit
        /// unless the stream is still good.
        /// </summary>
        private void Prefix(Kind k, long self, bool noSkip, Action<bool> then)
        {
            long ios = IosOf(self);
            long sb = Read32(ios + Buffer);
            if (Read32(ios + State) != GoodBit || sb == 0)
            {
                SetState(ios, FailBit);
                then(false);
                return;
            }

            void Skip()
            {
                if (noSkip || (Read32(ios + Flags) & SkipWs) == 0)
                {
                    Conclude();
                    return;
                }

                SkipSpace(k, sb, atEnd =>
                {
                    if (atEnd)
                    {
                        SetState(ios, EofBit);
                    }

                    Conclude();
                });
            }

            void Conclude()
            {
                if (Read32(ios + State) == GoodBit)
                {
                    then(true);
                    return;
                }

                SetState(ios, FailBit);
                then(false);
            }

            long tie = Read32(ios + Tie);
            if (tie != 0)
            {
                FlushStream(k, tie, Skip);
                return;
            }

            Skip();
        }

        /// <summary>get(): one element under a noskip sentry; eof|fail at the end. _Chcount is 0 or 1.</summary>
        private void GetOne(Kind k, long self, Action<int> then)
        {
            long ios = IosOf(self);
            WriteInt64(self + 8, 0);
            Prefix(k, self, noSkip: true, ok =>
            {
                if (!ok)
                {
                    then(-1);
                    return;
                }

                NextChar(k, Read32(ios + Buffer), c =>
                {
                    if (c == -1)
                    {
                        SetState(ios, EofBit | FailBit);
                    }
                    else
                    {
                        WriteInt64(self + 8, 1);
                    }

                    then(c);
                });
            });
        }

        /// <summary>
        /// get(s, n, delim) / getline(s, n, delim): store up to n-1 elements until the delimiter
        /// (getline extracts it, get leaves it), always NUL-terminate. eofbit at the end; failbit
        /// if nothing was extracted, and for getline when n-1 elements filled the array first.
        /// </summary>
        private void ReadLine(Kind k, long self, long buffer, long size, int delimiter, bool extractDelimiter)
        {
            long returnTo = _emulator.ReturnAddress;
            long ios = IosOf(self);
            WriteInt64(self + 8, 0);
            Prefix(k, self, noSkip: true, ok =>
            {
                long count = 0;
                uint state = GoodBit;
                long sb = Read32(ios + Buffer);

                void End()
                {
                    if (size > 0)
                    {
                        WriteChars(k, buffer + (Math.Min(count, size - 1) * k.Size), [0], 0, 1);
                    }

                    WriteInt64(self + 8, count);
                    if (count == 0)
                    {
                        state |= FailBit;
                    }

                    SetState(ios, state);
                    Finish(self, returnTo);
                }

                if (!ok)
                {
                    if (size > 0)
                    {
                        WriteChars(k, buffer, [0], 0, 1);
                    }

                    Finish(self, returnTo);
                    return;
                }

                long stored = 0;
                void Next()
                {
                    PeekChar(k, sb, c =>
                    {
                        if (c == -1)
                        {
                            state |= EofBit;
                            End();
                            return;
                        }

                        if (c == delimiter)
                        {
                            if (!extractDelimiter)
                            {
                                End();
                                return;
                            }

                            NextChar(k, sb, _ =>
                            {
                                count++;
                                End();
                            });
                            return;
                        }

                        if (stored >= size - 1)
                        {
                            if (extractDelimiter)
                            {
                                state |= FailBit;
                            }

                            End();
                            return;
                        }

                        NextChar(k, sb, got =>
                        {
                            WriteChars(k, buffer + (stored * k.Size), [got], 0, 1);
                            stored++;
                            count++;
                            Next();
                        });
                    });
                }

                Next();
            });
        }

        /// <summary>
        /// ignore(n, delim): extract and discard until n elements (unlimited at the streamsize
        /// maximum) or the delimiter has been extracted; eofbit at the end.
        /// </summary>
        private void Ignore(Kind k, long self, long limit, int delimiter)
        {
            long returnTo = _emulator.ReturnAddress;
            long ios = IosOf(self);
            WriteInt64(self + 8, 0);
            Prefix(k, self, noSkip: true, ok =>
            {
                if (!ok || limit <= 0)
                {
                    Finish(self, returnTo);
                    return;
                }

                long sb = Read32(ios + Buffer);
                long count = 0;
                void Next()
                {
                    if (limit != long.MaxValue && count >= limit)
                    {
                        WriteInt64(self + 8, count);
                        Finish(self, returnTo);
                        return;
                    }

                    NextChar(k, sb, c =>
                    {
                        if (c == -1)
                        {
                            WriteInt64(self + 8, count);
                            SetState(ios, EofBit);
                            Finish(self, returnTo);
                            return;
                        }

                        count++;
                        if (c == delimiter && delimiter != -1)
                        {
                            WriteInt64(self + 8, count);
                            Finish(self, returnTo);
                            return;
                        }

                        Next();
                    });
                }

                Next();
            });
        }

        /// <summary>putback / unget: clear eofbit, a noskip sentry, then sputbackc / sungetc; badbit on eof.</summary>
        private void Unread(Kind k, long self, int? c)
        {
            long returnTo = _emulator.ReturnAddress;
            long ios = IosOf(self);
            WriteInt64(self + 8, 0);
            Write32(ios + State, Read32(ios + State) & ~EofBit);
            Prefix(k, self, noSkip: true, ok =>
            {
                if (!ok)
                {
                    Finish(self, returnTo);
                    return;
                }

                PutBackChar(k, Read32(ios + Buffer), c, result =>
                {
                    if (result == -1)
                    {
                        SetState(ios, BadBit);
                    }

                    Finish(self, returnTo);
                });
            });
        }

        /// <summary>read(s, n): xsgetn under a noskip sentry; the count in _Chcount; eof|fail if short.</summary>
        private void ReadBlock(Kind k, long self, long buffer, long count, Action then)
        {
            long ios = IosOf(self);
            long sb = Read32(ios + Buffer);
            WriteInt64(self + 8, 0);
            Prefix(k, self, noSkip: true, ok =>
            {
                if (!ok)
                {
                    then();
                    return;
                }

                CallGuest(
                    VirtualSlot(sb, SlotXsgetn),
                    [sb, buffer, count & 0xFFFFFFFF, (count >> 32) & 0xFFFFFFFF],
                    low =>
                    {
                        long got = (long)((ulong)(uint)low | ((ulong)(uint)_frame.Arg(1) << 32));
                        WriteInt64(self + 8, Math.Max(0, got));
                        if (got != count)
                        {
                            SetState(ios, EofBit | FailBit);
                        }

                        then();
                    });
            });
        }

        /// <summary>
        /// A formatted extraction: the skipping sentry, then the field fed element by element
        /// to <paramref name="scanner"/> while it accepts them; eofbit when the stream ran out.
        /// </summary>
        private void Extract(Kind k, Scanner scanner, Func<Scanner, bool> store)
        {
            long self = _frame.Arg(0);
            long returnTo = _emulator.ReturnAddress;
            long ios = IosOf(self);
            Prefix(k, self, noSkip: false, ok =>
            {
                if (!ok)
                {
                    Finish(self, returnTo);
                    return;
                }

                long sb = Read32(ios + Buffer);
                void Complete(bool atEnd)
                {
                    uint state = store(scanner) ? GoodBit : FailBit;
                    if (atEnd)
                    {
                        state |= EofBit;
                    }

                    SetState(ios, state);
                    Finish(self, returnTo);
                }

                void Next()
                {
                    PeekChar(k, sb, c =>
                    {
                        if (c == -1)
                        {
                            Complete(atEnd: true);
                            return;
                        }

                        if (c > 0x7F || !scanner.Feed(c))
                        {
                            Complete(atEnd: false);
                            return;
                        }

                        NextChar(k, sb, _ => Next());
                    });
                }

                Next();
            });
        }

        private void ExtractInteger(Kind k, Func<IntegerScanner, long?> convert, int bytes, bool forceHex = false)
        {
            long target = _frame.Arg(1);
            uint flags = forceHex ? Hex : Read32(IosOf(_frame.Arg(0)) + Flags);
            Extract(k, new IntegerScanner(flags), s =>
            {
                long? value = convert((IntegerScanner)s);
                if (value is null)
                {
                    return false;
                }

                _emulator.WriteMemory(target, BitConverter.GetBytes(value.Value)[..bytes]);
                return true;
            });
        }

        private void ExtractFloat(Kind k, bool single)
        {
            long target = _frame.Arg(1);
            Extract(k, new FloatScanner(), s =>
            {
                double? value = ((FloatScanner)s).AsDouble(single);
                if (value is null)
                {
                    return false;
                }

                _emulator.WriteMemory(target, single ? BitConverter.GetBytes((float)value.Value) : BitConverter.GetBytes(value.Value));
                return true;
            });
        }

        /// <summary>operator&gt;&gt;(bool&amp;): "false"/"true" with boolalpha, else a long that must be 0 or 1.</summary>
        private void ExtractBool(Kind k)
        {
            long target = _frame.Arg(1);
            uint flags = Read32(IosOf(_frame.Arg(0)) + Flags);
            if ((flags & BoolAlpha) != 0)
            {
                Extract(k, new NameScanner("false", "true"), s =>
                {
                    int match = ((NameScanner)s).Match;
                    if (match < 0)
                    {
                        return false;
                    }

                    _emulator.WriteMemory(target, [(byte)match]);
                    return true;
                });
                return;
            }

            Extract(k, new IntegerScanner(flags), s =>
            {
                long? value = ((IntegerScanner)s).AsSigned(32);
                if (value is not (0 or 1))
                {
                    return false;
                }

                _emulator.WriteMemory(target, [(byte)value.Value]);
                return true;
            });
        }

        /// <summary>
        /// operator&gt;&gt;(basic_streambuf*): copy until this stream runs out (eofbit) or the
        /// target refuses one; failbit for a null target or when nothing was copied.
        /// </summary>
        private void ExtractStreambuf(Kind k)
        {
            long self = _frame.Arg(0), target = _frame.Arg(1);
            long returnTo = _emulator.ReturnAddress;
            long ios = IosOf(self);
            WriteInt64(self + 8, 0);
            Prefix(k, self, noSkip: false, ok =>
            {
                if (!ok || target == 0)
                {
                    if (target == 0)
                    {
                        SetState(ios, FailBit);
                    }

                    Finish(self, returnTo);
                    return;
                }

                long sb = Read32(ios + Buffer);
                long copied = 0;
                void End(uint state)
                {
                    WriteInt64(self + 8, copied);
                    SetState(ios, copied == 0 ? state | FailBit : state);
                    Finish(self, returnTo);
                }

                void Next()
                {
                    PeekChar(k, sb, c =>
                    {
                        if (c == -1)
                        {
                            End(EofBit);
                            return;
                        }

                        PutChars(k, target, [c], 0, written =>
                        {
                            if (written == 0)
                            {
                                End(GoodBit);
                                return;
                            }

                            copied++;
                            NextChar(k, sb, _ => Next());
                        });
                    });
                }

                Next();
            });
        }
    }
}

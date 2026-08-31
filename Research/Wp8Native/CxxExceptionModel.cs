namespace WPR.Wp8Native
{
    /// <summary>What is being thrown: the object, and every type it can be caught as.</summary>
    public sealed record ThrownException(long Object, IReadOnlyList<string> CatchableTypes)
    {
        /// <summary>The most derived type, which is the one the throw was written against.</summary>
        public string TypeName => CatchableTypes.Count > 0 ? CatchableTypes[0] : "(unknown)";
    }

    /// <summary>A catch clause found during the search.</summary>
    public sealed record CatchCandidate(
        UnwoundFrame Frame,
        int TryLow,
        int TryHigh,
        string CaughtType,
        uint FuncletRva)
    {
        /// <summary>Offset from the frame where the caught object is copied, or 0.</summary>
        public int CatchObjectOffset { get; init; }

        /// <summary>Adjustment applied to the establisher frame on ARM and x64.</summary>
        public int FrameOffset { get; init; }

        public bool IsCatchAll => CaughtType == "...";

        public override string ToString()
            => $"catch({CaughtType}) at funclet 0x{FuncletRva:X8} " +
               $"in function 0x{Frame.FunctionRva:X8} (states {TryLow}-{TryHigh}) " +
               $"catchObj=+0x{CatchObjectOffset:X} frameAdj=+0x{FrameOffset:X} " +
               $"frameSp=0x{Frame.FramePointer:X8}";
    }

    /// <summary>
    /// Reads the data structures MSVC emits for C++ exception handling.
    /// </summary>
    /// <remarks>
    /// Two halves meet here. The throw site supplies a <c>ThrowInfo</c> naming every type
    /// the thrown object can be caught as - a class and all its bases, so that
    /// <c>catch (const std::exception&amp;)</c> catches a <c>std::runtime_error</c>. Each
    /// frame supplies a <c>FuncInfo</c> listing its try blocks, the range of states each
    /// covers, and the catch clauses hanging off them.
    ///
    /// The search is then: for each frame from the throw outwards, work out which state the
    /// frame's PC is in, find the try blocks covering that state, and compare each catch
    /// clause's type against the thrown object's catchable list. First match wins.
    ///
    /// Every cross-reference in these structures is an image-relative RVA rather than a
    /// pointer, which is what makes them readable at all without relocations applied.
    /// </remarks>
    public sealed class CxxExceptionModel
    {
        /// <summary>FuncInfo magic numbers MSVC has used. Anything else is not a FuncInfo.</summary>
        private static readonly uint[] KnownMagic = [0x19930520, 0x19930521, 0x19930522];

        private readonly ArmEmulator _emulator;
        private readonly long _imageBase;

        public CxxExceptionModel(ArmEmulator emulator, long imageBase)
        {
            _emulator = emulator;
            _imageBase = imageBase;
        }

        /// <summary>
        /// Reads the ThrowInfo handed to <c>_CxxThrowException</c>, listing every type the
        /// thrown object can be caught as, most derived first.
        /// </summary>
        public ThrownException ReadThrow(long exceptionObject, long throwInfo)
        {
            List<string> types = new();

            if (throwInfo != 0)
            {
                uint catchableArrayRva = _emulator.ReadUInt32(throwInfo + 12);
                if (catchableArrayRva != 0)
                {
                    long array = _imageBase + catchableArrayRva;
                    int count = (int)_emulator.ReadUInt32(array);

                    for (int i = 0; i < count && i < 64; i++)
                    {
                        uint catchableRva = _emulator.ReadUInt32(array + 4 + (i * 4));
                        if (catchableRva == 0)
                        {
                            continue;
                        }

                        uint typeRva = _emulator.ReadUInt32(_imageBase + catchableRva + 4);
                        types.Add(ReadTypeName(typeRva));
                    }
                }
            }

            return new ThrownException(exceptionObject, types);
        }

        /// <summary>
        /// Walks the frames looking for a catch clause that accepts the thrown object.
        /// </summary>
        public List<CatchCandidate> FindHandlers(IEnumerable<UnwoundFrame> frames, ThrownException thrown)
        {
            List<CatchCandidate> found = new();

            foreach (UnwoundFrame frame in frames)
            {
                if (!frame.HasHandler || frame.HandlerDataRva == 0)
                {
                    continue;
                }

                found.AddRange(SearchFrame(frame, thrown));
            }

            return found;
        }

        /// <summary>A cleanup funclet to run while unwinding, in the order it must run.</summary>
        /// <summary>
        /// One cleanup funclet to run, and the frame it belongs to.
        /// </summary>
        /// <remarks>
        /// The whole <see cref="UnwoundFrame"/> is carried, not just its frame pointer,
        /// because a funclet reaches its parent's locals through that frame's callee-saved
        /// registers - and which register it uses is the compiler's choice, not something
        /// this side can infer.
        /// </remarks>
        public sealed record CleanupAction(UnwoundFrame Frame, uint FuncletRva, string Description);

        /// <summary>
        /// Collects the cleanup funclets between the throw and a chosen catch.
        /// </summary>
        /// <remarks>
        /// Each frame's unwind map is a chain: state N names a funclet to run and the state
        /// to move to next. Walking it from the frame's current state down to the target
        /// runs the destructors for everything constructed inside the abandoned scopes.
        ///
        /// Skipping this does not merely leak. A destructor also *resets* state - clearing a
        /// pointer, releasing a handle - so code after the catch can read something the
        /// cleanup was supposed to have tidied.
        /// </remarks>
        public List<CleanupAction> CollectCleanups(IEnumerable<UnwoundFrame> frames, CatchCandidate target)
        {
            List<CleanupAction> actions = new();

            foreach (UnwoundFrame frame in frames)
            {
                if (!frame.HasHandler || frame.HandlerDataRva == 0)
                {
                    continue;
                }

                long funcInfo = _imageBase + frame.HandlerDataRva;
                if (!KnownMagic.Contains(_emulator.ReadUInt32(funcInfo, 0)))
                {
                    continue;
                }

                int maxState = (int)_emulator.ReadUInt32(funcInfo + 4, 0);
                uint unwindMapRva = _emulator.ReadUInt32(funcInfo + 8, 0);
                int ipMapCount = (int)_emulator.ReadUInt32(funcInfo + 20, 0);
                uint ipMapRva = _emulator.ReadUInt32(funcInfo + 24, 0);

                bool isCatchFrame = frame == target.Frame;
                int stopAt = isCatchFrame ? target.TryLow : -1;
                int state = FindState(CallSiteOf(frame), ipMapRva, ipMapCount);

                if (unwindMapRva != 0)
                {
                    // Bounded by maxState: a malformed chain must not loop forever.
                    for (int guard = 0; guard <= maxState && state > stopAt && state >= 0; guard++)
                    {
                        long entry = _imageBase + unwindMapRva + (state * 8);
                        int toState = (int)_emulator.ReadUInt32(entry, 0);
                        uint action = _emulator.ReadUInt32(entry + 4, 0);

                        if (action != 0)
                        {
                            actions.Add(new CleanupAction(
                                frame,
                                action,
                                $"cleanup state {state} in function 0x{frame.FunctionRva:X8}"));
                        }

                        state = toState;
                    }
                }

                if (isCatchFrame)
                {
                    break;
                }
            }

            return actions;
        }

        /// <summary>
        /// Why a frame did or did not offer a catch: a function needing only destructors
        /// run has handler data with no try blocks at all, which is the common case and
        /// looks identical from the outside to a failed search.
        /// </summary>
        public string DescribeFrame(UnwoundFrame frame)
        {
            if (!frame.HasHandler || frame.HandlerDataRva == 0)
            {
                return "no handler data";
            }

            long funcInfo = _imageBase + frame.HandlerDataRva;
            uint magic = _emulator.ReadUInt32(funcInfo, 0);
            if (!KnownMagic.Contains(magic))
            {
                return $"handler data at 0x{frame.HandlerDataRva:X8} is not a FuncInfo (magic 0x{magic:X8})";
            }

            int tryBlocks = (int)_emulator.ReadUInt32(funcInfo + 12, 0);
            int ipMapCount = (int)_emulator.ReadUInt32(funcInfo + 20, 0);
            uint ipMapRva = _emulator.ReadUInt32(funcInfo + 24, 0);
            int state = FindState(CallSiteOf(frame), ipMapRva, ipMapCount);

            if (tryBlocks == 0)
            {
                return $"FuncInfo: no try blocks, cleanup only (state {state} of {ipMapCount} ip entries)";
            }

            return $"FuncInfo: {tryBlocks} try block(s), state {state} of {ipMapCount} ip entries" +
                   DescribeTryBlocks(funcInfo, tryBlocks, state);
        }

        /// <summary>
        /// Every try block in a function, what it covers, and what it catches.
        /// </summary>
        /// <remarks>
        /// A frame that has try blocks and still offers no handler is the interesting case,
        /// and the summary above cannot tell "the PC was not inside any try" from "nothing
        /// caught this type". Printing the state ranges and the caught types next to the
        /// frame's own state separates them at a glance.
        /// </remarks>
        private string DescribeTryBlocks(long funcInfo, int tryBlocks, int state)
        {
            uint tryBlockMapRva = _emulator.ReadUInt32(funcInfo + 16, 0);
            if (tryBlockMapRva == 0 || tryBlocks > 64)
            {
                return string.Empty;
            }

            var text = new System.Text.StringBuilder();
            for (int i = 0; i < tryBlocks; i++)
            {
                long entry = _imageBase + tryBlockMapRva + (i * 20);
                int tryLow = (int)_emulator.ReadUInt32(entry, 0);
                int tryHigh = (int)_emulator.ReadUInt32(entry + 4, 0);
                int catchCount = (int)_emulator.ReadUInt32(entry + 12, 0);
                uint handlerArrayRva = _emulator.ReadUInt32(entry + 16, 0);

                string covers = state >= tryLow && state <= tryHigh ? "ACTIVE" : "not active";
                text.Append($"{Environment.NewLine}            try states {tryLow}-{tryHigh} ({covers}):");

                for (int c = 0; c < catchCount && c < 16 && handlerArrayRva != 0; c++)
                {
                    long handler = _imageBase + handlerArrayRva + (c * 16);
                    uint typeRva = _emulator.ReadUInt32(handler + 4, 0);
                    uint funcletRva = _emulator.ReadUInt32(handler + 12, 0);
                    string caught = typeRva == 0 ? "..." : ReadTypeName(typeRva);
                    text.Append($" catch({caught})@0x{funcletRva:X8}");
                }
            }

            return text.ToString();
        }

        private IEnumerable<CatchCandidate> SearchFrame(UnwoundFrame frame, ThrownException thrown)
        {
            long funcInfo = _imageBase + frame.HandlerDataRva;
            uint magic = _emulator.ReadUInt32(funcInfo, 0);
            if (!KnownMagic.Contains(magic))
            {
                yield break;
            }

            int tryBlockCount = (int)_emulator.ReadUInt32(funcInfo + 12, 0);
            uint tryBlockMapRva = _emulator.ReadUInt32(funcInfo + 16, 0);
            int ipMapCount = (int)_emulator.ReadUInt32(funcInfo + 20, 0);
            uint ipMapRva = _emulator.ReadUInt32(funcInfo + 24, 0);

            if (tryBlockCount is <= 0 or > 1024 || tryBlockMapRva == 0)
            {
                yield break;
            }

            int state = FindState(CallSiteOf(frame), ipMapRva, ipMapCount);

            for (int i = 0; i < tryBlockCount; i++)
            {
                long entry = _imageBase + tryBlockMapRva + (i * 20);
                int tryLow = (int)_emulator.ReadUInt32(entry, 0);
                int tryHigh = (int)_emulator.ReadUInt32(entry + 4, 0);
                int catchCount = (int)_emulator.ReadUInt32(entry + 12, 0);
                uint handlerArrayRva = _emulator.ReadUInt32(entry + 16, 0);

                // A try block only applies if the PC was inside it, which the state says.
                // A state of -1 means "no try block active", so nothing matches.
                if (state < tryLow || state > tryHigh || handlerArrayRva == 0)
                {
                    continue;
                }

                for (int c = 0; c < catchCount && c < 64; c++)
                {
                    // HandlerType is FOUR words on ARM32: adjectives, dispType,
                    // dispCatchObj, dispOfHandler. The fifth member, dispFrame, exists only
                    // on x64 and ARM64 - ehdata.h guards it with _M_X64 || _M_ARM64, and
                    // _M_ARM_NT is in neither.
                    //
                    // Reading it as five words is invisible on the first entry, because the
                    // first four fields still land correctly; every entry after that is one
                    // word further out of step. The tell is a catch whose funclet address is
                    // zero and whose catch-object offset holds something that looks like an
                    // RVA - that "offset" is the next entry's handler address, read one field
                    // early. That is what the report meant by "the matching catch has no
                    // funclet address", and it cost a Lua exception its handler.
                    long handler = _imageBase + handlerArrayRva + (c * 16);
                    uint typeRva = _emulator.ReadUInt32(handler + 4, 0);
                    int catchObject = (int)_emulator.ReadUInt32(handler + 8, 0);
                    uint funcletRva = _emulator.ReadUInt32(handler + 12, 0);

                    // A null type descriptor is catch(...), which accepts anything.
                    string caught = typeRva == 0 ? "..." : ReadTypeName(typeRva);

                    if (caught == "..." || thrown.CatchableTypes.Contains(caught))
                    {
                        yield return new CatchCandidate(frame, tryLow, tryHigh, caught, funcletRva)
                        {
                            CatchObjectOffset = catchObject,
                        };
                    }
                }
            }
        }

        /// <summary>
        /// The address to look a frame's EH state up at: the call, not the return.
        /// </summary>
        /// <remarks>
        /// Every address in an unwound stack is a return address - the instruction *after*
        /// the call that is still in progress. Looking the state up there asks "what scope
        /// is this function in once the call has returned", and the answer is a scope the
        /// try block has already been left.
        ///
        /// Angry Birds Rio is a clean example. The frame that should catch its LuaException
        /// has one try block covering states 7-13, with catch(std::exception) - which the
        /// exception is catchable as. Looked up at the return address the frame reports
        /// state 6, one below the range, so the try reads as not active and the search walks
        /// straight past it to a catch(...) in the C++/CX boundary wrapper above, whose
        /// entire body is __abi_FailFast. The game terminating itself was the first visible
        /// symptom of a one-byte address.
        ///
        /// Subtracting one is the same thing Windows' own unwinder does for a non-leaf
        /// frame. It only has to land inside the call instruction, not at its start, because
        /// the lookup takes the last entry at or before the address.
        /// </remarks>
        private static long CallSiteOf(UnwoundFrame frame) => frame.Address - 1;

        /// <summary>
        /// Maps a PC to the EH state it is in, using the function's IP-to-state table. The
        /// table is sorted, so the state is the one belonging to the last entry at or
        /// before the address.
        /// </summary>
        private int FindState(long address, uint ipMapRva, int count)
        {
            if (ipMapRva == 0 || count is <= 0 or > 65536)
            {
                return -1;
            }

            uint target = (uint)(address - _imageBase);
            int state = -1;

            for (int i = 0; i < count; i++)
            {
                long entry = _imageBase + ipMapRva + (i * 8);
                uint ip = _emulator.ReadUInt32(entry, 0);
                if (ip > target)
                {
                    break;
                }

                state = (int)_emulator.ReadUInt32(entry + 4, 0);
            }

            return state;
        }

        /// <summary>
        /// Reads a TypeDescriptor's decorated name, which begins 8 bytes in, past the
        /// vftable pointer and a spare word.
        /// </summary>
        private string ReadTypeName(uint typeDescriptorRva)
        {
            if (typeDescriptorRva == 0)
            {
                return "...";
            }

            long address = _imageBase + typeDescriptorRva + 8;
            System.Text.StringBuilder name = new();

            for (int i = 0; i < 512; i++)
            {
                byte character = _emulator.ReadMemory(address + i, 1)[0];
                if (character == 0)
                {
                    break;
                }

                name.Append((char)character);
            }

            return name.Length == 0 ? "(unnamed)" : name.ToString();
        }
    }
}

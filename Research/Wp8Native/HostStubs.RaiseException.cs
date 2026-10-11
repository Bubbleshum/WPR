using UnicornEngine.Const;

namespace WPR.Wp8Native
{
    /// <summary>
    /// <c>RaiseException</c>, delivered through the same handler search a guest <c>throw</c> uses
    /// where the exception is one that search understands.
    /// </summary>
    public sealed partial class HostStubs
    {
        /// <summary>The MSVC C++ exception code, 'msc' | 0xE0000000.</summary>
        private const uint CxxExceptionCode = 0xE06D7363;

        /// <summary>MS_VC_EXCEPTION: the thread-naming message to a debugger.</summary>
        private const uint SetThreadNameCode = 0x406D1388;

        private static readonly uint[] CxxMagic = [0x19930520, 0x19930521, 0x19930522];

        /// <summary>Guest thread names given through MS_VC_EXCEPTION, by thread id.</summary>
        public Dictionary<uint, string> GuestThreadNames { get; } = new();

        /// <summary>
        /// void RaiseException(DWORD code, DWORD flags, DWORD argumentCount, const ULONG_PTR* arguments).
        /// </summary>
        /// <remarks>
        /// <para>Three cases, each to what Windows would do:</para>
        /// <list type="bullet">
        /// <item>A C++ exception (code 0xE06D7363, the FuncInfo magic, the object and its ThrowInfo)
        /// is what <c>_CxxThrowException</c> raises, so it is thrown exactly as that is: the C++
        /// handler search, cleanups, and the catch funclet - or, uncaught, the end of the thread.</item>
        /// <item>MS_VC_EXCEPTION (0x406D1388) names a thread for a debugger. With none attached the
        /// raiser's own <c>__except(EXCEPTION_EXECUTE_HANDLER)</c> swallows it; returning is that
        /// outcome. The name is kept.</item>
        /// <item>Any other code is a structured exception. Only an SEH <c>__except</c> filter can
        /// handle it, and those are not run here. If no frame on the stack has a non-C++ handler it
        /// is genuinely unhandled, and Windows ends the process: a background guest thread ends, the
        /// main one stops the run with the code. If some frame does have one, returning is the
        /// only answer left; it is recorded as an undelivered throw so a failure later names it.</item>
        /// </list>
        /// </remarks>
        internal void RaiseExceptionFromImage()
        {
            uint code = (uint)Arg(0);
            uint count = Math.Min((uint)Arg(2), 15u);   // EXCEPTION_MAXIMUM_PARAMETERS
            long arguments = Arg(3);
            uint[] values = new uint[arguments == 0 ? 0 : count];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = _emulator.ReadUInt32(arguments + (i * 4));
            }

            if (code == CxxExceptionCode && values.Length >= 3 && CxxMagic.Contains(values[0]))
            {
                ThrownException? thrown = values[1] == 0 && values[2] == 0
                    ? CurrentlyHandled()
                    : _emulator.ExceptionModel.ReadThrow(values[1], values[2]);
                if (thrown is not null)
                {
                    ThrowIntoImage(thrown);
                    return;
                }
            }

            if (code == SetThreadNameCode && values.Length >= 3 && values[0] == 0x1000)
            {
                string name = _frame.ReadNarrowString(values[1], 256);
                uint thread = values[2] == 0xFFFFFFFF ? (uint)(0x1000 + _emulator.CurrentThreadId) : values[2];
                GuestThreadNames[thread] = name;
                Return(0);
                return;
            }

            if (AnyStructuredHandlerOnStack())
            {
                _undeliveredThrows.Add($"RaiseException(0x{code:X8}) - an SEH handler is on the stack and SEH filters are not run");
                Return(0);
                return;
            }

            string why = $"unhandled exception 0x{code:X8} raised at 0x{_emulator.ReturnAddress & ~1L:X8}";
            _undeliveredThrows.Add(why);
            Return(0);
            if (!_emulator.EndCurrentGuestThread(why))
            {
                _emulator.Stop($"the image raised an {why}");
            }
        }

        /// <summary>Whether any frame from the caller outwards has a language handler that is not C++'s.</summary>
        private bool AnyStructuredHandlerOnStack()
        {
            int[] coreRegisters =
            [
                Arm.UC_ARM_REG_R0, Arm.UC_ARM_REG_R1, Arm.UC_ARM_REG_R2, Arm.UC_ARM_REG_R3,
                Arm.UC_ARM_REG_R4, Arm.UC_ARM_REG_R5, Arm.UC_ARM_REG_R6, Arm.UC_ARM_REG_R7,
                Arm.UC_ARM_REG_R8, Arm.UC_ARM_REG_R9, Arm.UC_ARM_REG_R10, Arm.UC_ARM_REG_R11,
                Arm.UC_ARM_REG_R12, Arm.UC_ARM_REG_SP, Arm.UC_ARM_REG_LR, Arm.UC_ARM_REG_PC,
            ];

            try
            {
                IReadOnlyList<UnwoundFrame> frames = _emulator.Unwinder.Walk(
                    pc: _emulator.ReturnAddress & ~1L,
                    liveRegisters: coreRegisters.Select(_emulator.ReadRegister).ToArray());
                return frames.Any(f => f.HasHandler && f.HandlerDataRva != 0 &&
                                       !CxxMagic.Contains(_emulator.ReadUInt32(_emulator.ImageBase + f.HandlerDataRva, 0)));
            }
            catch (Exception)
            {
                // A stack that cannot be walked might hold a handler: do not end a thread on a guess.
                return true;
            }
        }
    }
}

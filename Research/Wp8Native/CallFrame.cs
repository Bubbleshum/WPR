using System.Text;
using UnicornEngine.Const;

namespace WPR.Wp8Native
{
    /// <summary>
    /// Reads arguments and writes return values for a trapped call, following the ARM
    /// procedure call standard: the first four arguments in r0-r3, the result in r0, and
    /// a 64-bit result in the r0:r1 pair.
    /// </summary>
    public sealed class CallFrame
    {
        private readonly ArmEmulator _emulator;

        public CallFrame(ArmEmulator emulator) => _emulator = emulator;

        /// <summary>Longer than this is a garbage length, not a real one.</summary>
        public const int SaneLengthLimit = 64 * 1024 * 1024;

        /// <summary>
        /// The n-th argument, from r0-r3 and then the stack.
        /// </summary>
        /// <remarks>
        /// AAPCS puts arguments five and up on the stack, pushed by the caller, so at the
        /// moment a trap fires sp points straight at the fifth. That holds here because a
        /// trap slot is reached by a branch, not a call: nothing has pushed a frame in
        /// between, and sp is exactly as the caller left it.
        ///
        /// Only correct for arguments that occupy one word each. A 64-bit value or a double
        /// takes two slots and an even-aligned pair, so a signature containing one has to be
        /// read deliberately rather than counted through.
        /// </remarks>
        public long Arg(int index) => index switch
        {
            0 => _emulator.ReadRegister(Arm.UC_ARM_REG_R0),
            1 => _emulator.ReadRegister(Arm.UC_ARM_REG_R1),
            2 => _emulator.ReadRegister(Arm.UC_ARM_REG_R2),
            3 => _emulator.ReadRegister(Arm.UC_ARM_REG_R3),
            < 0 => throw new ArgumentOutOfRangeException(nameof(index)),
            _ => _emulator.ReadUInt32(_emulator.ReadRegister(Arm.UC_ARM_REG_SP) + ((index - 4) * 4)),
        };

        /// <summary>
        /// The n-th argument as a signed 32-bit integer.
        /// </summary>
        /// <remarks>
        /// <see cref="Arg"/> hands back what the register holds, and a register holds no
        /// sign - so a negative int arrives as a number just under four billion. That is
        /// harmless right up until it is used as an offset or a count, at which point it is
        /// catastrophic and looks like nothing at all: fseek(file, -153652, SEEK_END) became
        /// a seek to 2^32, the next fread returned zero bytes, and the image threw
        /// "Failed to read 4 bytes from ...FONT_BASIC.pvr" - eleven frames and one unhandled
        /// exception away from anything to do with signs.
        ///
        /// Every parameter declared int, long, ptrdiff_t or ssize_t must come through here.
        /// </remarks>
        public int SignedArg(int index) => unchecked((int)(uint)Arg(index));

        public void Return(long value) => _emulator.WriteRegister(Arm.UC_ARM_REG_R0, value);

        public void Return64(long value)
        {
            _emulator.WriteRegister(Arm.UC_ARM_REG_R0, value & 0xFFFFFFFFL);
            _emulator.WriteRegister(Arm.UC_ARM_REG_R1, (value >> 32) & 0xFFFFFFFFL);
        }

        /// <summary>
        /// Returns a double. Windows on ARM is hard-float (AAPCS-VFP): the result belongs in d0.
        /// It is written to r0:r1 as well, which costs nothing and keeps any soft-float caller
        /// (there should be none) from reading garbage.
        /// </summary>
        public void ReturnDouble(double value)
        {
            long bits = BitConverter.DoubleToInt64Bits(value);
            Return64(bits);
            _emulator.Cpu.VfpWrite(0, (uint)bits);
            _emulator.Cpu.VfpWrite(1, (uint)(bits >> 32));
        }

        /// <summary>Returns a float in s0, the hard-float convention.</summary>
        public void ReturnFloat(float value) => _emulator.Cpu.VfpWrite(0, BitConverter.SingleToUInt32Bits(value));

        /// <summary>
        /// The n-th float argument. Under AAPCS-VFP floats are allocated to s0, s1, ... in
        /// order, independently of the integer arguments in r0-r3. Only correct for signatures
        /// whose float arguments are all floats (no doubles to back-fill around).
        /// </summary>
        public float FloatArg(int index) => BitConverter.UInt32BitsToSingle(_emulator.Cpu.VfpRead(index));

        /// <summary>The n-th double argument: d0, d1, ... Only for all-double signatures.</summary>
        public double DoubleArg(int index)
        {
            ulong bits = _emulator.Cpu.VfpRead(index * 2) | ((ulong)_emulator.Cpu.VfpRead((index * 2) + 1) << 32);
            return BitConverter.UInt64BitsToDouble(bits);
        }

        public void WriteInt64(long address, long value)
        {
            if (address != 0)
            {
                _emulator.WriteMemory(address, BitConverter.GetBytes(value));
            }
        }

        /// <summary>Reads a null-terminated single-byte string, capped at a sane length.</summary>
        public string ReadNarrowString(long address, int limit = 64 * 1024)
        {
            if (address == 0)
            {
                return string.Empty;
            }

            List<byte> bytes = new();
            const int chunk = 128;

            while (bytes.Count < limit)
            {
                byte[] block = _emulator.ReadMemory(address + bytes.Count, chunk);
                int terminator = Array.IndexOf(block, (byte)0);
                if (terminator >= 0)
                {
                    bytes.AddRange(block[..terminator]);
                    break;
                }

                bytes.AddRange(block);
            }

            return Encoding.Latin1.GetString(bytes.ToArray());
        }

        public void WriteNarrowString(long address, string text)
        {
            byte[] bytes = Encoding.Latin1.GetBytes(text);
            _emulator.WriteMemory(address, [.. bytes, 0]);
        }
    }
}

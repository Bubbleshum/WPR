using UnicornEngine.Const;

namespace WPR.Wp8Native
{
    /// <summary>
    /// Walks the variadic arguments of a trapped call.
    /// </summary>
    /// <remarks>
    /// Under AAPCS every argument, variadic or not, takes the next free slot in one
    /// sequence: core positions 0-3 are r0-r3, and position 4 onwards is the stack at
    /// <c>[sp]</c>, <c>[sp+4]</c>, and so on. At the moment a trap fires, sp still points
    /// where the caller left it, so the stack half needs no adjustment.
    ///
    /// The part that catches people out is floating point. A variadic <c>double</c> is not
    /// passed in a VFP register - it goes in a **pair of core registers**, and the pair
    /// must start at an even position. So <c>printf("%d %f", 1, 2.0)</c> puts the int in
    /// r2, skips r3, and passes the double in the stack slots, not in r3:[sp]. Getting
    /// that wrong reads half an argument and shifts everything after it.
    /// </remarks>
    public sealed class VarArgReader
    {
        private static readonly int[] CoreRegisters =
        [
            Arm.UC_ARM_REG_R0, Arm.UC_ARM_REG_R1, Arm.UC_ARM_REG_R2, Arm.UC_ARM_REG_R3,
        ];

        private readonly ArmEmulator _emulator;
        private readonly long _stackPointer;
        private int _position;

        /// <param name="firstVariadicPosition">
        /// The core position the variadic arguments start at, which is one past the named
        /// ones: 2 for <c>sprintf(buffer, format, ...)</c>, 3 for
        /// <c>sprintf_s(buffer, size, format, ...)</c>.
        /// </param>
        public VarArgReader(ArmEmulator emulator, int firstVariadicPosition)
        {
            _emulator = emulator;
            _stackPointer = emulator.ReadRegister(Arm.UC_ARM_REG_SP);
            _position = firstVariadicPosition;
        }

        public uint NextUInt32() => ReadAt(_position++);

        public int NextInt32() => unchecked((int)NextUInt32());

        public ulong NextUInt64()
        {
            // A 64-bit value occupies an even-aligned pair.
            if ((_position & 1) != 0)
            {
                _position++;
            }

            uint low = ReadAt(_position++);
            uint high = ReadAt(_position++);
            return ((ulong)high << 32) | low;
        }

        public long NextInt64() => unchecked((long)NextUInt64());

        public double NextDouble() => BitConverter.Int64BitsToDouble(NextInt64());

        /// <summary>A pointer argument, which is always a single word on 32-bit ARM.</summary>
        public long NextPointer() => NextUInt32();

        private uint ReadAt(int position)
            => position < CoreRegisters.Length
                ? (uint)_emulator.ReadRegister(CoreRegisters[position])
                : _emulator.ReadUInt32(_stackPointer + ((position - CoreRegisters.Length) * 4));
    }
}

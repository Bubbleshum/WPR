using System.Runtime.InteropServices;

namespace WPR.HostDirect3D
{
    /// <summary>
    /// Small pieces of machine code: a jump to a function, placed below 2 GB, and a "slot N was
    /// called" reporter for vtable slots WPR does not implement.
    /// </summary>
    /// <remarks>
    /// <para><b>Why below 2 GB.</b> SharpDX's WP8 build asks its native helper for function
    /// pointers as a 32-bit <c>int</c> (<c>new IntPtr(Interop.D3D11CreateDevice())</c>): it was
    /// built for 32-bit ARM. A 64-bit host's code lives far above that, so each such pointer is a
    /// jump placed in low memory, whose address survives the round trip through an int.</para>
    /// <para><b>Pages are written, then made executable, never written again</b> (W^X on Android, and
    /// ARM64's instruction cache is made coherent by the kernel when a page becomes executable).
    /// Thunks are therefore made in batches: <see cref="Batch"/> collects them and
    /// <see cref="Batch.Commit"/> emits one page set.</para>
    /// </remarks>
    internal static unsafe class NativeCode
    {
        private static readonly bool Arm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        /// <summary>The slot id the last unimplemented-slot reporter stored, read by its common target.</summary>
        internal static readonly int* LastSlot = (int*)NativeMemory.AllocZeroed(sizeof(int));

        internal sealed class Batch
        {
            private readonly List<(IntPtr Target, int Slot)> _items = new();

            /// <summary>Queues a thunk; its address is <see cref="Commit"/>'s result at the same index.</summary>
            public int Jump(IntPtr target) => Add(target, -1);

            /// <summary>Queues a thunk that stores <paramref name="slot"/> in <see cref="LastSlot"/> and jumps.</summary>
            public int Reporter(IntPtr target, int slot) => Add(target, slot);

            private int Add(IntPtr target, int slot)
            {
                _items.Add((target, slot));
                return _items.Count - 1;
            }

            public IntPtr[] Commit()
            {
                const int size = 48;
                int bytes = Math.Max(1, _items.Count) * size;
                byte* page = AllocateLow(bytes);
                var result = new IntPtr[_items.Count];
                for (int i = 0; i < _items.Count; i++)
                {
                    byte* at = page + (i * size);
                    (IntPtr target, int slot) = _items[i];
                    if (Arm64)
                    {
                        EmitArm64(at, target, slot);
                    }
                    else
                    {
                        EmitX64(at, target, slot);
                    }

                    result[i] = (IntPtr)at;
                }

                MakeExecutable(page, bytes);
                return result;
            }
        }

        private static void EmitX64(byte* p, IntPtr target, int slot)
        {
            int i = 0;
            if (slot >= 0)
            {
                p[i++] = 0x48; p[i++] = 0xB8; *(long*)(p + i) = (long)LastSlot; i += 8;   // mov rax, LastSlot
                p[i++] = 0xC7; p[i++] = 0x00; *(int*)(p + i) = slot; i += 4;               // mov dword [rax], slot
            }

            p[i++] = 0x48; p[i++] = 0xB8; *(long*)(p + i) = (long)target; i += 8;          // mov rax, target
            p[i++] = 0xFF; p[i++] = 0xE0;                                                   // jmp rax
        }

        private static void EmitArm64(byte* p, IntPtr target, int slot)
        {
            uint* code = (uint*)p;
            if (slot >= 0)
            {
                code[0] = 0x580000D0;                                   // ldr x16, #24 (LastSlot)
                code[1] = 0x52800000u | ((uint)(slot & 0xFFFF) << 5) | 17; // movz w17, #slot
                code[2] = 0xB9000211;                                   // str w17, [x16]
                code[3] = 0x580000B0;                                   // ldr x16, #20 (target)
                code[4] = 0xD61F0200;                                   // br x16
                code[5] = 0xD503201F;                                   // nop
                *(long*)(p + 24) = (long)LastSlot;
                *(long*)(p + 32) = (long)target;
            }
            else
            {
                code[0] = 0x58000050;                                   // ldr x16, #8
                code[1] = 0xD61F0200;                                   // br x16
                *(long*)(p + 8) = (long)target;
            }
        }

        private static byte* AllocateLow(int bytes)
        {
            nuint size = (nuint)((bytes + 0xFFFF) & ~0xFFFF);
            for (long hint = 0x10000000; hint < 0x7F000000; hint += 0x01000000)
            {
                byte* p = OperatingSystem.IsWindows()
                    ? (byte*)VirtualAlloc((IntPtr)hint, size, 0x3000 /* MEM_COMMIT | MEM_RESERVE */, 0x04 /* PAGE_READWRITE */)
                    : MapLow((IntPtr)hint, size);
                if (p != null && (long)p + (long)size < 0x7FFFFFFF)
                {
                    return p;
                }

                if (p != null)
                {
                    Release(p, size);
                }
            }

            throw new InvalidOperationException("No memory below 2 GB for code thunks.");
        }

        private static void MakeExecutable(byte* page, int bytes)
        {
            nuint size = (nuint)((bytes + 0xFFFF) & ~0xFFFF);
            if (OperatingSystem.IsWindows())
            {
                VirtualProtect((IntPtr)page, size, 0x20 /* PAGE_EXECUTE_READ */, out _);
                FlushInstructionCache(GetCurrentProcess(), (IntPtr)page, size);
            }
            else if (mprotect((IntPtr)page, size, 5 /* PROT_READ | PROT_EXEC */) != 0)
            {
                throw new InvalidOperationException("mprotect failed for code thunks.");
            }
        }

        private static byte* MapLow(IntPtr hint, nuint size)
        {
            IntPtr p = mmap(hint, size, 3 /* PROT_READ | PROT_WRITE */, 0x22 /* MAP_PRIVATE | MAP_ANONYMOUS */, -1, 0);
            return p == new IntPtr(-1) ? null : (byte*)p;
        }

        private static void Release(byte* p, nuint size)
        {
            if (OperatingSystem.IsWindows())
            {
                VirtualFree((IntPtr)p, 0, 0x8000 /* MEM_RELEASE */);
            }
            else
            {
                munmap((IntPtr)p, size);
            }
        }

        [DllImport("kernel32")] private static extern IntPtr VirtualAlloc(IntPtr address, nuint size, uint type, uint protect);
        [DllImport("kernel32")] private static extern bool VirtualProtect(IntPtr address, nuint size, uint protect, out uint old);
        [DllImport("kernel32")] private static extern bool VirtualFree(IntPtr address, nuint size, uint type);
        [DllImport("kernel32")] private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, nuint size);
        [DllImport("kernel32")] private static extern IntPtr GetCurrentProcess();
        [DllImport("libc")] private static extern IntPtr mmap(IntPtr address, nuint length, int prot, int flags, int fd, long offset);
        [DllImport("libc")] private static extern int mprotect(IntPtr address, nuint length, int prot);
        [DllImport("libc")] private static extern int munmap(IntPtr address, nuint length);
    }
}

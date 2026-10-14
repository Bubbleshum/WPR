using UnicornEngine.Const;

namespace WPR.Wp8Native
{
    /// <summary>
    /// Proves the vtable bridge by having emulated ARM code call a host-implemented WinRT
    /// property getter and checking the value that comes back.
    /// </summary>
    /// <remarks>
    /// The caller here is hand-assembled rather than the game's own code, because the game
    /// asks for CoreApplication long before it would ask for MemoryManager, and answering
    /// CoreApplication properly means calling back <em>into</em> emulated code - a separate
    /// mechanism. What is not simulated is the part that matters: these are real Thumb-2
    /// instructions, executing on the emulated CPU, against a vtable in emulated memory,
    /// dispatching into C#. It is the exact instruction sequence the Microsoft C++ compiler
    /// emits for a WinRT property getter:
    ///
    ///     ldr r2, [r0]         ; r2 = this-&gt;vtable
    ///     ldr r3, [r2, #slot*4] ; r3 = vtable[slot]
    ///     blx r3               ; call it, with this in r0 and the out-pointer in r1
    /// </remarks>
    public static class VtableProof
    {
        public static bool Run(ArmEmulator emulator, TextWriter output)
        {
            long? factory = emulator.WinRt.GetActivationFactory(WinRtRuntime.MemoryManagerClass);
            if (factory is null)
            {
                output.WriteLine("  MemoryManager is not registered - nothing to prove.");
                return false;
            }

            output.WriteLine($"  activation factory   0x{factory.Value:X8}");
            output.WriteLine($"  its vtable           0x{emulator.ReadUInt32(factory.Value):X8}");
            output.WriteLine();

            bool bytesOk = CallGetter(
                emulator, output, factory.Value,
                WinRtRuntime.SlotProcessCommittedBytes,
                "get_ProcessCommittedBytes",
                WinRtRuntime.ProcessCommittedBytes);

            bool limitOk = CallGetter(
                emulator, output, factory.Value,
                WinRtRuntime.SlotProcessCommittedLimit,
                "get_ProcessCommittedLimit",
                WinRtRuntime.ProcessCommittedLimit);

            return bytesOk && limitOk;
        }

        private static bool CallGetter(
            ArmEmulator emulator,
            TextWriter output,
            long instance,
            int slot,
            string name,
            ulong expected)
        {
            long code = emulator.AllocateCode(16);
            long resultBuffer = emulator.AllocateHeap(8);
            emulator.WriteUInt64(resultBuffer, 0xDEADBEEFDEADBEEF); // poison, so a no-op call is visible

            emulator.WriteMemory(code, EmitGetterCall(slot));
            emulator.WriteRegister(Arm.UC_ARM_REG_R0, instance);
            emulator.WriteRegister(Arm.UC_ARM_REG_R1, resultBuffer);

            // Stop when the CPU returns to the instruction after the blx.
            string? fault = emulator.Run(ArmEmulator.ThumbEntry(code), untilAddress: code + 6, instructionBudget: 100);

            ulong actual = BitConverter.ToUInt64(emulator.ReadMemory(resultBuffer, 8));
            long hresult = emulator.ReadRegister(Arm.UC_ARM_REG_R0);
            bool ok = fault is null && hresult == 0 && actual == expected;

            output.WriteLine($"  vtable slot {slot}  {name}");
            output.WriteLine($"      HRESULT          0x{hresult:X8}{(hresult == 0 ? " (S_OK)" : "")}");
            output.WriteLine($"      value returned   {actual:N0} bytes ({actual / 1024 / 1024} MB)");
            output.WriteLine($"      expected         {expected:N0} bytes");
            if (fault is not null)
            {
                output.WriteLine($"      fault            {fault}");
            }

            output.WriteLine($"      -> {(ok ? "PASS" : "FAIL")}");
            output.WriteLine();
            return ok;
        }

        /// <summary>
        /// Proves the reverse direction: the host calling a function made of emulated ARM
        /// code, and control coming back afterwards.
        /// </summary>
        /// <remarks>
        /// The image's own CreateView does run (see the trace), but it throws before
        /// returning because it asks for a class that is not implemented yet, so it cannot
        /// demonstrate the return leg. This builds a stand-in IFrameworkViewSource out of
        /// real Thumb-2 instead, so the whole round trip is observable and deterministic:
        ///
        ///     caller stub  ->  ICoreApplication::Run   (host)
        ///                  ->  CreateView              (emulated, synthesised here)
        ///                  ->  onReturn                (host)
        ///                  ->  back to the caller stub
        /// </remarks>
        public static bool RunCallbackProof(ArmEmulator emulator, TextWriter output)
        {
            long? coreApplication = emulator.WinRt.GetActivationFactory(WinRtRuntime.CoreApplicationClass);
            if (coreApplication is null)
            {
                output.WriteLine("  CoreApplication is not registered - nothing to prove.");
                return false;
            }

            // A view whose lifecycle methods are real emulated code, so that driving it
            // leaves evidence on the emulated side rather than only in the host's log.
            long markers = emulator.AllocateHeap(LifecycleMethodCount * 4);
            long expectedView = BuildEmulatedView(emulator, markers);
            long viewSource = BuildEmulatedViewSource(emulator, expectedView);
            output.WriteLine($"  synthetic view source  0x{viewSource:X8}  (its CreateView is real Thumb-2)");
            output.WriteLine($"  view it will return    0x{expectedView:X8}");
            output.WriteLine();

            long code = emulator.AllocateCode(16);
            emulator.WriteMemory(code, EmitGetterCall(WinRtRuntime.SlotCoreApplicationRun));
            emulator.WriteRegister(Arm.UC_ARM_REG_R0, coreApplication.Value);
            emulator.WriteRegister(Arm.UC_ARM_REG_R1, viewSource);

            string? fault = emulator.Run(
                ArmEmulator.ThumbEntry(code), untilAddress: code + 6, instructionBudget: 1000);

            long hresult = emulator.ReadRegister(Arm.UC_ARM_REG_R0);
            long pc = emulator.ReadRegister(Arm.UC_ARM_REG_PC);
            long? view = emulator.WinRt.FrameworkView;

            bool returned = pc == code + 6;

            // Each lifecycle method stamped its own number, so a zero here means the
            // emulated method never ran, whatever the host-side log claims.
            string[] names = ["Initialize", "SetWindow", "Load", "Run"];
            bool allRan = true;
            for (int i = 0; i < LifecycleMethodCount; i++)
            {
                if (emulator.ReadUInt32(markers + (i * 4)) != i + 1)
                {
                    allRan = false;
                }
            }

            bool ok = fault is null && returned && hresult == 0 && view == expectedView && allRan;

            output.WriteLine($"  ICoreApplication::Run");
            output.WriteLine($"      HRESULT          0x{hresult:X8}{(hresult == 0 ? " (S_OK)" : "")}");
            output.WriteLine($"      view received    0x{view ?? 0:X8}");
            output.WriteLine($"      expected         0x{expectedView:X8}");
            output.WriteLine($"      resumed caller   {(returned ? $"yes, PC = 0x{pc:X8}" : $"NO, PC = 0x{pc:X8}")}");
            output.WriteLine("      lifecycle driven on the emulated side:");
            for (int i = 0; i < LifecycleMethodCount; i++)
            {
                uint marker = emulator.ReadUInt32(markers + (i * 4));
                output.WriteLine($"          IFrameworkView::{names[i],-11} {(marker == i + 1 ? "ran" : "DID NOT RUN")}");
            }

            if (fault is not null)
            {
                output.WriteLine($"      fault            {fault}");
            }

            output.WriteLine($"      -> {(ok ? "PASS" : "FAIL")}");
            return ok;
        }

        /// <summary>
        /// Builds an IFrameworkViewSource whose CreateView is genuine emulated ARM code:
        /// it stores <paramref name="view"/> through the out-pointer and returns S_OK.
        /// </summary>
        private static long BuildEmulatedViewSource(ArmEmulator emulator, long view)
        {
            //   ldr r2, [pc, #4]   ; r2 = the literal at the end
            //   str r2, [r1]       ; *out = view
            //   movs r0, #0        ; S_OK
            //   bx lr
            //   .word view
            long code = emulator.AllocateCode(16);
            emulator.WriteMemory(code,
            [
                0x01, 0x4A,             // ldr r2, [pc, #4]
                0x0A, 0x60,             // str r2, [r1]
                0x00, 0x20,             // movs r0, #0
                0x70, 0x47,             // bx lr
            ]);
            emulator.WriteUInt32(code + 8, (uint)view);

            const int slotCreateView = 6;
            long vtable = emulator.AllocateHeap((slotCreateView + 1) * 4);
            emulator.WriteUInt32(vtable + (slotCreateView * 4), (uint)ArmEmulator.ThumbEntry(code));

            long instance = emulator.AllocateHeap(8);
            emulator.WriteUInt32(instance, (uint)vtable);
            return instance;
        }

        /// <summary>Number of IFrameworkView lifecycle methods the host drives.</summary>
        private const int LifecycleMethodCount = 4;

        /// <summary>
        /// Builds an IFrameworkView out of real Thumb-2. Each of Initialize, SetWindow,
        /// Load and Run stamps its own number into <paramref name="markers"/> and returns
        /// S_OK, so the emulated side leaves evidence that it actually ran.
        /// </summary>
        private static long BuildEmulatedView(ArmEmulator emulator, long markers)
        {
            long vtable = emulator.AllocateHeap((6 + LifecycleMethodCount) * 4);

            for (int i = 0; i < LifecycleMethodCount; i++)
            {
                //   ldr r2, [pc, #8]   ; r2 = &markers[i]
                //   movs r3, #i+1
                //   str r3, [r2]
                //   movs r0, #0        ; S_OK
                //   bx lr
                //   nop                ; pad so the literal lands 4-aligned
                //   .word &markers[i]
                long code = emulator.AllocateCode(16);
                ushort stamp = (ushort)(0x2300 | (i + 1));   // movs r3, #imm8

                emulator.WriteMemory(code,
                [
                    0x02, 0x4A,                                  // ldr r2, [pc, #8]
                    (byte)(stamp & 0xFF), (byte)(stamp >> 8),    // movs r3, #i+1
                    0x13, 0x60,                                  // str r3, [r2]
                    0x00, 0x20,                                  // movs r0, #0
                    0x70, 0x47,                                  // bx lr
                    0x00, 0xBF,                                  // nop
                ]);
                emulator.WriteUInt32(code + 12, (uint)(markers + (i * 4)));
                emulator.WriteUInt32(vtable + ((6 + i) * 4), (uint)ArmEmulator.ThumbEntry(code));
            }

            long instance = emulator.AllocateHeap(8);
            emulator.WriteUInt32(instance, (uint)vtable);
            return instance;
        }

        /// <summary>
        /// Assembles <c>ldr r2,[r0] ; ldr r3,[r2,#slot*4] ; blx r3</c> as Thumb-2.
        /// </summary>
        private static byte[] EmitGetterCall(int slot)
        {
            if (slot is < 0 or > 31)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(slot), "The 5-bit immediate in LDR (T1) only reaches slot 31.");
            }

            // LDR (immediate) T1:  0110 1 imm5 Rn Rt   with address = Rn + imm5*4
            ushort ldrVtable = (ushort)(0x6800 | (0 << 6) | (0 << 3) | 2);       // ldr r2, [r0, #0]
            ushort ldrMethod = (ushort)(0x6800 | (slot << 6) | (2 << 3) | 3);    // ldr r3, [r2, #slot*4]
            const ushort BlxR3 = 0x4798;                                         // blx r3

            return
            [
                (byte)(ldrVtable & 0xFF), (byte)(ldrVtable >> 8),
                (byte)(ldrMethod & 0xFF), (byte)(ldrMethod >> 8),
                (byte)(BlxR3 & 0xFF), (byte)(BlxR3 >> 8),
            ];
        }
    }
}

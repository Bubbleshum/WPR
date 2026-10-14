namespace WPR.Wp8Native
{
    /// <summary>
    /// The interlocked singly linked list (<c>SLIST_HEADER</c>), to its documented contract.
    /// </summary>
    /// <remarks>
    /// Job systems are built on these: a producer pushes a job, a worker flushes or pops the
    /// list. Answered with 0 they lose every job silently - a push links nothing and a flush
    /// hands back an empty list - which is how Angry Birds Go! reached a null call with five
    /// workers parked on their condition variables (Plans/RUNTIME-CONVERGENCE.md, section 1).
    ///
    /// ARM32 lays the header out as on x86 (winnt.h): an 8-byte, 8-aligned union of
    /// <c>{ SLIST_ENTRY* Next; WORD Depth; WORD Sequence; }</c>. An entry is just its Next
    /// pointer, at offset 0 of the caller's structure.
    ///
    /// No lock: guest threads are scheduled by this host one at a time on a single emulated CPU,
    /// and a stub runs to completion before any other guest thread can, so each of these is
    /// atomic exactly as the interlocked original is.
    /// </remarks>
    public sealed class SListLibrary(ArmEmulator emulator, CallFrame frame)
    {
        public void RegisterInto(Dictionary<string, Action> handlers)
        {
            // void InitializeSListHead(PSLIST_HEADER)
            handlers["InitializeSListHead"] = () =>
            {
                emulator.WriteUInt64(frame.Arg(0), 0);
                frame.Return(0);
            };

            // PSLIST_ENTRY InterlockedPushEntrySList(PSLIST_HEADER, PSLIST_ENTRY): the previous first entry.
            handlers["InterlockedPushEntrySList"] = () =>
            {
                long head = frame.Arg(0), entry = frame.Arg(1);
                long previous = emulator.ReadUInt32(head);
                emulator.WriteUInt32(entry, (uint)previous);
                Write(head, entry, Depth(head) + 1);
                frame.Return(previous);
            };

            // PSLIST_ENTRY InterlockedPushListSListEx(PSLIST_HEADER, PSLIST_ENTRY first, PSLIST_ENTRY last, ULONG count):
            // splices an already linked run onto the front.
            handlers["InterlockedPushListSListEx"] = () =>
            {
                long head = frame.Arg(0), first = frame.Arg(1), last = frame.Arg(2);
                int count = (int)frame.Arg(3);
                long previous = emulator.ReadUInt32(head);
                emulator.WriteUInt32(last, (uint)previous);
                Write(head, first, Depth(head) + count);
                frame.Return(previous);
            };

            // PSLIST_ENTRY InterlockedPopEntrySList(PSLIST_HEADER): the first entry, or NULL when empty.
            handlers["InterlockedPopEntrySList"] = () =>
            {
                long head = frame.Arg(0);
                long first = emulator.ReadUInt32(head);
                if (first != 0)
                {
                    Write(head, emulator.ReadUInt32(first), Math.Max(0, Depth(head) - 1));
                }

                frame.Return(first);
            };

            // PSLIST_ENTRY InterlockedFlushSList(PSLIST_HEADER): the whole list, leaving it empty.
            handlers["InterlockedFlushSList"] = () =>
            {
                long head = frame.Arg(0);
                long first = emulator.ReadUInt32(head);
                Write(head, 0, 0);
                frame.Return(first);
            };

            // USHORT QueryDepthSList(PSLIST_HEADER)
            handlers["QueryDepthSList"] = () => frame.Return(Depth(frame.Arg(0)));

            // PSLIST_ENTRY RtlFirstEntrySList(const SLIST_HEADER*): peek.
            handlers["RtlFirstEntrySList"] = () => frame.Return(emulator.ReadUInt32(frame.Arg(0)));
        }

        private int Depth(long head) => emulator.ReadMemory(head + 4, 2) is var b ? b[0] | (b[1] << 8) : 0;

        /// <summary>Writes Next and Depth and advances Sequence, as every successful operation does.</summary>
        private void Write(long head, long next, int depth)
        {
            byte[] old = emulator.ReadMemory(head + 6, 2);
            int sequence = (old[0] | (old[1] << 8)) + 1;
            emulator.WriteUInt32(head, (uint)next);
            emulator.WriteMemory(head + 4, [(byte)depth, (byte)(depth >> 8), (byte)sequence, (byte)(sequence >> 8)]);
        }
    }
}

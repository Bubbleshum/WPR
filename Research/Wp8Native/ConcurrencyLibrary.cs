namespace WPR.Wp8Native
{
    /// <summary>
    /// MSVCP110's <c>Concurrency::details::_Concurrent_queue_base_v4</c>, the untyped half of
    /// <c>concurrent_queue&lt;T&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <para>The queue's storage is host-side: one page per item, allocated in the guest heap,
    /// keyed by the queue object. What makes it correct for any <c>T</c> is that items are never
    /// byte-copied - they go in and out through the derived class's own virtuals, which is
    /// exactly what the library does:</para>
    /// <code>
    /// vtable: [0] _Move_item(_Page&amp;, size_t, void* src)
    ///         [1] _Copy_item(_Page&amp;, size_t, const void* src)
    ///         [2] _Assign_and_destroy_item(void* dst, _Page&amp;, size_t)
    ///         [3] scalar deleting destructor  [4] _Allocate_page()  [5] _Deallocate_page(_Page*)
    /// </code>
    /// <para>Declaration order, with the destructor after the three item methods - read off Amazing
    /// Alex's <c>concurrent_queue&lt;std::function&gt;</c> by disassembly (slot 2 computes
    /// <c>page + index*24 + 8</c> from r2/r3 and assigns into r1; slot 3 tests the delete flag).
    /// Putting the destructor first sent every push into <c>_Assign_and_destroy_item</c> with
    /// shifted arguments.</para>
    /// <code>
    /// </code>
    /// <para>Item <c>i</c> of a page lives after the page header (<c>_Next</c>, <c>_Mask</c>), so a
    /// one-item page is 8 bytes of header plus the item. Left unimplemented, <c>_Internal_empty</c>
    /// answered 0 ("not empty") and <c>_Internal_pop_if_present</c> answered 0 ("nothing"), and
    /// Amazing Alex's render loop spun on that pair 85 million times.</para>
    /// </remarks>
    public sealed class ConcurrencyLibrary
    {
        private const string Base = "_Concurrent_queue_base_v4@details@Concurrency@@";
        private const int SlotMoveItem = 0, SlotCopyItem = 1, SlotAssignAndDestroy = 2;
        private const int PageHeader = 8;

        private readonly ArmEmulator _emulator;
        private readonly CallFrame _frame;
        private readonly Dictionary<long, long> _itemSize = new();
        private readonly Dictionary<long, Queue<long>> _queues = new();

        public ConcurrencyLibrary(ArmEmulator emulator, CallFrame frame)
        {
            _emulator = emulator;
            _frame = frame;
        }

        public int Pushed { get; private set; }

        public int Popped { get; private set; }

        public void RegisterInto(Dictionary<string, Action> handlers)
        {
            // _Concurrent_queue_base_v4(size_t _Item_size)
            handlers[$"??0{Base}IAA@I@Z"] = () =>
            {
                long self = _frame.Arg(0);
                _itemSize[self] = Math.Max(1, _frame.Arg(1));
                _queues[self] = new Queue<long>();
                _frame.Return(self);
            };

            handlers[$"??1{Base}MAA@XZ"] = () =>
            {
                _queues.Remove(_frame.Arg(0));
                _itemSize.Remove(_frame.Arg(0));
                _frame.Return(0);
            };

            handlers[$"?_Internal_push@{Base}IAAXPBX@Z"] = () => Push(SlotCopyItem);
            handlers[$"?_Internal_move_push@{Base}IAAXPAX@Z"] = () => Push(SlotMoveItem);
            handlers[$"?_Internal_pop_if_present@{Base}IAA_NPAX@Z"] = PopIfPresent;
            handlers[$"?_Internal_empty@{Base}IBA_NXZ"] = () => _frame.Return(QueueOf(_frame.Arg(0)).Count == 0 ? 1 : 0);
            handlers[$"?_Internal_size@{Base}IBAIXZ"] = () => _frame.Return(QueueOf(_frame.Arg(0)).Count);
            handlers[$"?_Internal_finish_clear@{Base}IAAXXZ"] = () =>
            {
                QueueOf(_frame.Arg(0)).Clear();
                _frame.Return(0);
            };
            handlers[$"?_Internal_throw_exception@{Base}IBAXXZ"] = () => _frame.Return(0);

            // HRESULT CaptureUiThreadContext(IContextCallback**) - PhoneAppModelHost.dll. WP8's
            // ppltasks.h asks it, after CoGetApartmentType, whether the calling thread is the UI
            // thread, and task::wait()/get() then throws invalid_operation("Illegal to wait on a
            // task in a Windows Runtime STA"). Unimplemented it answered 0 - S_OK - so every
            // thread looked like the UI thread, including the ConcRT chores that run here on the
            // main guest thread and are the very code allowed to wait. Angry Birds Stella's sign-in
            // task died on that throw and its "connecting" board waited for ever. A Direct3D app
            // has no XAML UI thread to capture, so failing is the truthful answer.
            handlers["CaptureUiThreadContext"] = () =>
            {
                if (_frame.Arg(0) != 0)
                {
                    _emulator.WriteUInt32(_frame.Arg(0), 0);
                }

                _frame.Return(unchecked((int)0x8001010E)); // RPC_E_WRONG_THREAD
            };
        }

        private Queue<long> QueueOf(long self)
        {
            if (!_queues.TryGetValue(self, out Queue<long>? queue))
            {
                // A queue constructed before these stubs could see it (a static one): treat its
                // items as pointer-sized, which the virtuals make right anyway.
                queue = _queues[self] = new Queue<long>();
                _itemSize.TryAdd(self, 8);
            }

            return queue;
        }

        /// <summary>Allocates a one-item page and has the derived class copy or move the item in.</summary>
        private void Push(int slot)
        {
            long self = _frame.Arg(0);
            long source = _frame.Arg(1);
            Queue<long> queue = QueueOf(self);
            long page = _emulator.AllocateHeap(PageHeader + Math.Max(_itemSize[self], 8));
            _emulator.WriteMemory(page, new byte[PageHeader]);

            long function = VirtualAt(self, slot);
            long callerReturn = _emulator.ReturnAddress;
            if (Environment.GetEnvironmentVariable("WPR_QTRACE") is not null)
            {
                long vtable = _emulator.ReadUInt32(self);
                Console.Error.WriteLine($"[queue] push self=0x{self:X8} size={_itemSize[self]} vtable=0x{vtable:X8} slots=" +
                    string.Join(" ", Enumerable.Range(0, 8).Select(i => $"{_emulator.ReadUInt32(vtable + (i * 4), 0):X8}")) +
                    $" src=0x{source:X8} caller=0x{callerReturn:X8}");
            }
            _emulator.CallEmulated("concurrent_queue::_Copy_item", function, [self, page, 0, source], onReturn: () =>
            {
                queue.Enqueue(page);
                Pushed++;
                _frame.Return(0);
                _emulator.ContinueAt(callerReturn);
            }, recycle: true);
        }

        private void PopIfPresent()
        {
            long self = _frame.Arg(0);
            long destination = _frame.Arg(1);
            Queue<long> queue = QueueOf(self);
            if (queue.Count == 0)
            {
                _frame.Return(0);
                return;
            }

            long page = queue.Dequeue();
            long function = VirtualAt(self, SlotAssignAndDestroy);
            long callerReturn = _emulator.ReturnAddress;
            _emulator.CallEmulated("concurrent_queue::_Assign_and_destroy_item", function, [self, destination, page, 0], onReturn: () =>
            {
                Popped++;
                _frame.Return(1);
                _emulator.ContinueAt(callerReturn);
            }, recycle: true);
        }

        private long VirtualAt(long self, int slot)
        {
            long vtable = _emulator.ReadUInt32(self);
            return _emulator.ReadUInt32(vtable + (slot * 4));
        }
    }
}

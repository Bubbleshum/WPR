namespace WPR.Wp8Native
{
    /// <summary>
    /// vccorlib's event plumbing: every C++/CX <c>event</c> is a <c>void* targets</c> field that
    /// these functions maintain, and raising it walks the target array they hand back.
    /// </summary>
    /// <remarks>
    /// <para>Unimplemented, adding a handler did nothing and raising an event found no targets,
    /// so no C++/CX event ever reached anyone - Amazing Alex raises one every frame.</para>
    /// <list type="bullet">
    /// <item><c>EventSourceInitialize(void** targets)</c> / <c>EventSourceUninitialize</c></item>
    /// <item><c>EventRegistrationToken EventSourceAdd(void** targets, EventLock*, Delegate^)</c> -
    /// the 8-byte token comes back through a hidden pointer in r0, so the real arguments start at r1.</item>
    /// <item><c>EventSourceRemove(void** targets, EventLock*, EventRegistrationToken)</c> - the token by
    /// value in r2:r3.</item>
    /// <item><c>void* EventSourceGetTargetArray(void* targets, EventLock*)</c>, then
    /// <c>EventSourceGetTargetArraySize(array)</c> and <c>EventSourceGetTargetArrayEvent(array, i)</c>.</item>
    /// </list>
    /// <para>The field holds a host-made list object, and the "target array" is that same object:
    /// raising sees the handlers registered at that moment. Delegates are AddRef'd on add, because
    /// the caller releases its own reference as soon as the add returns.</para>
    /// </remarks>
    public sealed partial class WinRtRuntime
    {
        private readonly Dictionary<long, List<(long Token, long Delegate)>> _eventTargets = new();
        private long _nextEventToken = 1;

        public int EventsRaised { get; private set; }

        public void RegisterEventSources(Dictionary<string, Action> handlers)
        {
            const string Ns = "@Details@Platform@@";
            handlers[$"?EventSourceInitialize{Ns}YAXPAPAX@Z"] = () =>
            {
                if (Arg(0) != 0)
                {
                    _emulator.WriteUInt32(Arg(0), 0);
                }

                Return(0);
            };
            handlers[$"?EventSourceUninitialize{Ns}YAXPAPAX@Z"] = () =>
            {
                if (Arg(0) != 0)
                {
                    _eventTargets.Remove(_emulator.ReadUInt32(Arg(0), 0));
                    _emulator.WriteUInt32(Arg(0), 0);
                }

                Return(0);
            };
            handlers[$"?EventSourceAdd{Ns}YA?AVEventRegistrationToken@Foundation@Windows@@PAPAXPAUEventLock@12@P$AAVDelegate@2@@Z"] = EventSourceAdd;
            handlers[$"?EventSourceRemove{Ns}YAXPAPAXPAUEventLock@12@VEventRegistrationToken@Foundation@Windows@@@Z"] = () =>
            {
                long targets = Arg(0) == 0 ? 0 : _emulator.ReadUInt32(Arg(0), 0);
                long token = Arg(2) | (Arg(3) << 32);
                if (_eventTargets.TryGetValue(targets, out var list))
                {
                    list.RemoveAll(t => t.Token == token);
                }

                Return(0);
            };
            handlers[$"?EventSourceGetTargetArray{Ns}YAPAXPAXPAUEventLock@12@@Z"] = () =>
            {
                long targets = Arg(0);
                bool any = _eventTargets.TryGetValue(targets, out var list) && list.Count > 0;
                if (any)
                {
                    EventsRaised++;
                }

                Return(any ? targets : 0);
            };
            handlers[$"?EventSourceGetTargetArraySize{Ns}YAIPAX@Z"] = () =>
                Return(_eventTargets.TryGetValue(Arg(0), out var list) ? list.Count : 0);
            // Returns the delegate AddRef'd: the raiser releases it once it has invoked it, so
            // answering with a borrowed pointer freed the handler after its first raise.
            handlers[$"?EventSourceGetTargetArrayEvent{Ns}YAPAXPAXI@Z"] = () =>
            {
                long d = _eventTargets.TryGetValue(Arg(0), out var list) && Arg(1) < list.Count ? list[(int)Arg(1)].Delegate : 0;
                AddRefThenReturn(d, d);
            };
        }

        private void EventSourceAdd()
        {
            long result = Arg(0);
            long field = Arg(1);
            long handler = Arg(3);
            long targets = field == 0 ? 0 : _emulator.ReadUInt32(field, 0);
            if (targets == 0 || !_eventTargets.ContainsKey(targets))
            {
                // The list object: a COM-shaped stand-in, so anything that AddRefs or Releases the
                // "target array" it is handed lands on a harmless trap.
                targets = CreateDiscoveryObject("EventTargetArray", slotCount: 8);
                _eventTargets[targets] = [];
                if (field != 0)
                {
                    _emulator.WriteUInt32(field, (uint)targets);
                }
            }

            if (Environment.GetEnvironmentVariable("WPR_EVTRACE") is not null)
            {
                long vt = handler == 0 ? 0 : _emulator.ReadUInt32(handler, 0);
                Console.Error.WriteLine($"[event] add field=0x{field:X8} handler=0x{handler:X8} words=" +
                    string.Join(" ", Enumerable.Range(0, 10).Select(i => $"{_emulator.ReadUInt32(handler + (i * 4), 0):X8}")) +
                    $" vtable=" + string.Join(" ", Enumerable.Range(0, 5).Select(i => $"{_emulator.ReadUInt32(vt + (i * 4), 0):X8}")));
            }

            long token = _nextEventToken++;
            _eventTargets[targets].Add((token, handler));
            if (result != 0)
            {
                _emulator.WriteUInt64(result, (ulong)token);
            }

            // Keep the delegate alive: the caller releases its own reference after the add.
            AddRefThenReturn(handler, result);
        }

        /// <summary>Calls IUnknown::AddRef (slot 1) on <paramref name="target"/>, then returns <paramref name="value"/> to the import's caller.</summary>
        private void AddRefThenReturn(long target, long value)
        {
            long callerReturn = _emulator.ReturnAddress;
            long addRef = target == 0 ? 0 : _emulator.ReadUInt32(_emulator.ReadUInt32(target, 0) + 4, 0);
            if (!_emulator.IsExecutableCode(addRef))
            {
                Return(value);
                return;
            }

            _emulator.CallEmulated("Delegate::AddRef", addRef, [target], onReturn: () =>
            {
                Return(value);
                _emulator.ContinueAt(callerReturn);
            }, recycle: true);
        }
    }
}

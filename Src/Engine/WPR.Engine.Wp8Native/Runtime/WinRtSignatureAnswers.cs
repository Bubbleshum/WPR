namespace WPR.Wp8Native
{
    /// <summary>
    /// The default answer for an unimplemented member of a stand-in object, decided by the
    /// member's real signature (<see cref="WinRtSystemInterfaces"/>) rather than by guessing from
    /// what happens to be in r1 and r2.
    /// </summary>
    /// <remarks>
    /// The guessing (the rest of <c>CreateDiscoveryObject</c>'s default) is still there for
    /// interfaces the metadata does not cover - Windows.Phone.*, Xbox services, generic
    /// collections - and its rules are what this keeps where they were right:
    /// <list type="bullet">
    /// <item><c>add_X</c> keeps the handler, takes a reference on it and hands back a token;</item>
    /// <item><c>put_Completed</c> fires the completion at once, because nothing behind a
    /// stand-in is still running;</item>
    /// <item>an interface or class out-parameter gets a placeholder object, because the image
    /// dereferences it straight away.</item>
    /// </list>
    /// What the signature fixes is where the guesses were wrong: a scalar or struct out-parameter
    /// is zeroed at its real size instead of receiving a placeholder's address, an out-pointer is
    /// found by the ABI instead of by being a stack address, and a call whose layout cannot be
    /// computed falls back to the guessing (a diagnostic run stops on it by name instead).
    /// </remarks>
    public sealed partial class WinRtRuntime
    {
        private const string EventRegistrationToken = "System.Runtime.InteropServices.WindowsRuntime.EventRegistrationToken";

        private readonly Dictionary<string, int> _defaultedSlots = new(StringComparer.Ordinal);

        /// <summary>
        /// Members answered S_OK with their out-parameters zeroed or given placeholders: the
        /// WinRT counterpart of <see cref="HostStubs.DefaultedCalls"/>, and the to-do list for
        /// stand-ins.
        /// </summary>
        public IReadOnlyDictionary<string, int> DefaultedSlots => _defaultedSlots;

        private readonly Dictionary<string, int> _guessedSlots = new(StringComparer.Ordinal);

        /// <summary>
        /// Members of a stand-in class the metadata does not describe, answered by the out-parameter
        /// heuristics in <c>CreateDiscoveryObject</c> (which register looks like a stack pointer).
        /// The last shape-guessing in the runtime (the runtime convergence plan, 1e); counted so it
        /// reaches the <c>summary:</c> line, where an empty list means none of it ran.
        /// </summary>
        public IReadOnlyDictionary<string, int> GuessedSlots => _guessedSlots;

        /// <summary>The first member whose signature could not be answered, which stopped the guest.</summary>
        public string? FirstUnanswerableSlot { get; private set; }

        /// <summary>
        /// Answers a call to <paramref name="method"/> from its signature. Returns false only
        /// when it cannot, so the old shape-guessing answers instead and a player's run keeps going.
        /// Only a diagnostic run (WPR_WP8_TRAP_IMPORTS=1) stops the guest there.
        /// </summary>
        private bool AnswerBySignature(WinRtSystemInterfaces.Method method, string interfaceName, int slot, string described, bool failing)
        {
            WinRtSystemInterfaces.AbiLayout layout = WinRtSystemInterfaces.Layout(method);
            string? problem = layout.Problem ?? layout.Outs
                .Where(o => o.Shape is null)
                .Select(o => $"out-parameter type {o.Type} has no known size")
                .FirstOrDefault();

            if (problem is not null)
            {
                if (!HostStubs.TrapUnknownImports)
                {
                    _defaultedSlots[described + " (unanswerable: " + problem + ")"] = _defaultedSlots.GetValueOrDefault(described + " (unanswerable: " + problem + ")") + 1;
                    return false;
                }

                FirstUnanswerableSlot ??= $"{described} [{method.Signature}]: {problem}";
                Return(HResultNotImplemented);
                _emulator.Stop($"unanswerable WinRT call {described}: {problem}");
                return true;
            }

            // A class this runtime refuses to stand in for answers failure, with its
            // out-parameters still filled: see the `failing` remarks in CreateDiscoveryObject.
            if (failing)
            {
                WriteOuts(layout, described);
                Return(HResultFail);
                return true;
            }

            if (method.Name.StartsWith("add_", StringComparison.Ordinal) &&
                layout.Parameters.Count == 1 && layout.Result?.Type == EventRegistrationToken)
            {
                long handler = Arg(layout.Parameters[0].Word);
                WriteZeros(Arg(layout.Result.Word), 8);   // a zero token is valid; it never matches a remove
                if (interfaceName == "ICoreWindow")
                {
                    // CoreWindow events keep every handler, so they hand out real tokens.
                    _emulator.WriteUInt64(Arg(layout.Result.Word), (ulong)AddWindowHandler(slot, handler));
                }

                if (KeepAlive(handler))
                {
                    return true;
                }

                Return(HResultOk);
                return true;
            }

            if (method.Name.StartsWith("remove_", StringComparison.Ordinal))
            {
                // The 64-bit token is an even register pair: r2:r3 after `this` in r0.
                if (interfaceName == "ICoreWindow")
                {
                    RemoveWindowHandler((Arg(3) << 32) | (Arg(2) & 0xFFFFFFFFL));
                }

                Return(HResultOk);
                return true;
            }

            if (method.Name == "put_Completed" && layout.Result is null &&
                layout.Parameters.Count == 1 && layout.Parameters[0].Shape?.Kind == 'D' &&
                CompleteAsync(Arg(0), Arg(layout.Parameters[0].Word), described))
            {
                return true;
            }

            WriteOuts(layout, described);
            _defaultedSlots[described] = _defaultedSlots.GetValueOrDefault(described) + 1;
            Return(HResultOk);
            return true;
        }

        /// <summary>
        /// Fills every out-parameter: a placeholder object for an interface, class or delegate,
        /// zeros at the type's real size for everything else (the null HSTRING is the empty
        /// string), and an empty array for an out array.
        /// </summary>
        private void WriteOuts(WinRtSystemInterfaces.AbiLayout layout, string described)
        {
            foreach (WinRtSystemInterfaces.AbiArgument argument in layout.Outs)
            {
                long pointer = Arg(argument.Word);
                if (argument.IsArray)
                {
                    WriteZeros(pointer, 4);                 // UInt32* length
                    WriteZeros(Arg(argument.Word + 1), 4);  // T** data
                    continue;
                }

                if (argument.Shape!.IsObject)
                {
                    if (IsWritable(pointer, 4))
                    {
                        _emulator.WriteUInt32(pointer, (uint)GetPlaceholder(described));
                    }

                    continue;
                }

                WriteZeros(pointer, argument.Shape.Size);
            }
        }

        private void WriteZeros(long pointer, int size)
        {
            if (IsWritable(pointer, size))
            {
                _emulator.WriteMemory(pointer, new byte[size]);
            }
        }

        /// <summary>An out-pointer the image passed is written only if it is real memory: a null one is legal for some members and means "not wanted".</summary>
        private bool IsWritable(long pointer, int size) =>
            pointer != 0 && _emulator.Cpu.IsMapped(pointer) && _emulator.Cpu.IsMapped(pointer + Math.Max(size, 1) - 1);
    }
}

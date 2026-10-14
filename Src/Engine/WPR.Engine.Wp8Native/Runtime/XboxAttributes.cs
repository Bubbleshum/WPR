namespace WPR.Wp8Native
{
    /// <summary>
    /// <c>Microsoft.Xbox.Leaderboards.LeaderboardAttribute</c> and
    /// <c>ILeaderboardService::PostResultsAsync</c>, the multi-column way to post a score. Layouts
    /// are <c>Microsoft.Xbox.winmd</c>'s declaration order, as in <c>XboxRuntime.cs</c>.
    /// </summary>
    /// <remarks>
    /// <para>MONOPOLY Millionaire and Assassin's Creed Pirates (LuaMobileSDK) name this class. Before
    /// it existed the class was improvised - every property a logged trap - and PostResultsAsync
    /// completed having posted nothing, so a score sent this way never reached WPR Hub.</para>
    ///
    /// <para>The attribute objects are this runtime's (the class is activated through it), but the
    /// list handed to PostResultsAsync is the game's own <c>Platform::Collections</c> vector, so it
    /// is read through the game's methods: <c>get_Size</c>, then <c>GetAt</c> for each element,
    /// before the call returns. Elements are looked up by pointer in <see cref="_attributes"/>; one
    /// that is not ours is skipped and recorded.</para>
    /// </remarks>
    public sealed partial class WinRtRuntime
    {
        private sealed class AttributeState
        {
            public uint Id;
            public int Verb;
            public int Type;
            public string Value = string.Empty;

            public XboxLeaderboardAttribute Snapshot() => new(Id, Verb, Type, Value);
        }

        private readonly Dictionary<long, AttributeState> _attributes = new();

        /// <summary>
        /// The class's factory: <c>IActivationFactory::ActivateInstance(out)</c> and
        /// <c>ILeaderboardAttributeFactory::CreateLeaderboardAttribute(id, verb, type, value, out)</c>,
        /// both slot 6, told apart as the other Xbox factories are: ActivateInstance's first argument
        /// is the out-parameter, CreateLeaderboardAttribute's is a small attribute id.
        /// </summary>
        private long LeaderboardAttributeFactory() => CreateDiscoveryObject(
            "ILeaderboardAttributeFactory",
            slotCount: 8,
            known: new Dictionary<int, (string, Action)>
            {
                [InspectableSlots + 0] = ("CreateLeaderboardAttribute|ActivateInstance", () =>
                {
                    if (ArmEmulator.IsStackAddress(Arg(1)))
                    {
                        WriteOut(1, LeaderboardAttributeObject(new AttributeState()));
                        Return(HResultOk);
                        return;
                    }

                    // (UInt32 id, LeaderboardAggregation verb, LeaderboardValueType type, String value,
                    // LeaderboardAttribute* out): id, verb, type in r1-r3, value and out on the stack.
                    var state = new AttributeState
                    {
                        Id = (uint)Arg(1),
                        Verb = (int)Arg(2),
                        Type = (int)Arg(3),
                        Value = Arg(4) == 0 ? string.Empty : _strings.ReadText(Arg(4)),
                    };
                    WriteOut(5, LeaderboardAttributeObject(state));
                    Return(HResultOk);
                }),
            });

        /// <summary>
        /// <c>ILeaderboardAttribute</c>: Verb, AttributeId, Type and Value, each a get/put pair.
        /// </summary>
        private long LeaderboardAttributeObject(AttributeState state)
        {
            long self = CreateDiscoveryObject(
                "LeaderboardAttribute",
                slotCount: 16,
                known: new Dictionary<int, (string, Action)>
                {
                    [InspectableSlots + 0] = ("get_Verb", () => ReturnUInt32((uint)state.Verb)),
                    [InspectableSlots + 1] = ("put_Verb", () =>
                    {
                        state.Verb = (int)Arg(1);
                        Return(HResultOk);
                    }),
                    [InspectableSlots + 2] = ("get_AttributeId", () => ReturnUInt32(state.Id)),
                    [InspectableSlots + 3] = ("put_AttributeId", () =>
                    {
                        state.Id = (uint)Arg(1);
                        Return(HResultOk);
                    }),
                    [InspectableSlots + 4] = ("get_Type", () => ReturnUInt32((uint)state.Type)),
                    [InspectableSlots + 5] = ("put_Type", () =>
                    {
                        state.Type = (int)Arg(1);
                        Return(HResultOk);
                    }),
                    [InspectableSlots + 6] = ("get_Value", () => ReturnString(state.Value)),
                    [InspectableSlots + 7] = ("put_Value", () =>
                    {
                        state.Value = Arg(1) == 0 ? string.Empty : _strings.ReadText(Arg(1));
                        Return(HResultOk);
                    }),
                });

            _attributes[self] = state;
            return self;
        }

        /// <summary>The attribute behind a pointer this runtime handed out, if it is one.</summary>
        internal XboxLeaderboardAttribute? AttributeAt(long pointer) =>
            _attributes.TryGetValue(pointer, out AttributeState? state) ? state.Snapshot() : null;

        /// <summary>
        /// <c>PostResultsAsync(UInt32 leaderboardId, IReadOnlyList&lt;LeaderboardAttribute&gt; attributes,
        /// IAsyncAction* out)</c>: reads the list through its own vtable (slot 7 get_Size, slot 6
        /// GetAt), hands the columns to the host, and completes.
        /// </summary>
        private void PostResultsAsync()
        {
            uint id = (uint)Arg(1);
            long list = Arg(2);
            long outPointer = Arg(3);
            long resume = _emulator.ReturnAddress;
            long scratch = _emulator.AllocateHeap(8);
            var collected = new List<XboxLeaderboardAttribute>();
            int skipped = 0;

            void Finish()
            {
                _emulator.FreeHeap(scratch);
                XboxCalls.Add($"PostResultsAsync(id={id}, [{string.Join(", ", collected.Select(a => $"{a.AttributeId}:{a.Value} verb={a.Verb} type={a.Type}"))}]" +
                              (skipped > 0 ? $", {skipped} not ours" : string.Empty) + ")");
                try { XboxHost?.PostResults(id, collected); }
                catch (Exception ex) { XboxCalls.Add($"  host threw {ex.GetType().Name}: {ex.Message}"); }

                if (outPointer != 0)
                {
                    _emulator.WriteUInt32(outPointer, (uint)AsyncOperation("PostResultsAsync", 0));
                }

                Return(HResultOk);
            }

            long getSize = VirtualMethod(list, InspectableSlots + 1);
            long getAt = VirtualMethod(list, InspectableSlots + 0);
            if (list == 0 || getSize == 0 || getAt == 0)
            {
                Finish();
                return;
            }

            _emulator.CallEmulated("IVectorView<LeaderboardAttribute>::get_Size", getSize, [list, scratch], onReturn: () =>
            {
                uint size = (Arg(0) & 0xFFFFFFFFL) == HResultOk ? _emulator.ReadUInt32(scratch, 0) : 0u;
                size = Math.Min(size, 64u);
                uint index = 0;

                void Next()
                {
                    if (index >= size)
                    {
                        Finish();
                        _emulator.ContinueAt(resume);
                        return;
                    }

                    _emulator.WriteUInt32(scratch, 0);
                    _emulator.CallEmulated("IVectorView<LeaderboardAttribute>::GetAt", getAt, [list, index, scratch], onReturn: () =>
                    {
                        long element = (Arg(0) & 0xFFFFFFFFL) == HResultOk ? _emulator.ReadUInt32(scratch, 0) : 0;
                        if (AttributeAt(element) is { } attribute)
                        {
                            collected.Add(attribute);
                        }
                        else
                        {
                            skipped++;
                        }

                        index++;
                        Next();
                    }, recycle: true);
                }

                Next();
            }, recycle: true);
        }

        /// <summary>Method <paramref name="slot"/> of a COM object, or 0 when there is none.</summary>
        private long VirtualMethod(long pointer, int slot)
        {
            if (pointer == 0)
            {
                return 0;
            }

            long vtable = _emulator.ReadUInt32(pointer, 0);
            if (vtable == 0)
            {
                return 0;
            }

            long fn = _emulator.ReadUInt32(vtable + (slot * 4), 0);
            return _emulator.IsExecutableCode(fn) || _emulator.TrapNameAt(fn & ~1L) is not null ? fn : 0;
        }
    }
}

namespace WPR.Wp8Native
{
    /// <summary>
    /// MSVC's run-time type information: <c>__RTDynamicCast</c> and <c>__RTCastToVoid</c>,
    /// read from the RTTI structures the compiler emitted into the image.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Answered with zero by the default stub, every <c>dynamic_cast</c> failed - 36 of them on
    /// the way into Angry Birds Classic - and the code behind a failed cast is an error path.
    /// </para>
    /// <para>
    /// 32-bit layout (absolute pointers, no image-relative RVAs): the word before an object's
    /// vtable points at its <b>CompleteObjectLocator</b> {signature, offset, cdOffset,
    /// TypeDescriptor*, ClassHierarchyDescriptor*}; the hierarchy is {signature, attributes,
    /// numBaseClasses, BaseClassDescriptor**}; each base is {TypeDescriptor*, numContainedBases,
    /// PMD {mdisp, pdisp, vdisp}, attributes}. Entry 0 of the base array is the complete class
    /// itself. A base reached through a virtual base (pdisp &gt;= 0) is located through the
    /// complete object's vbtable.
    /// </para>
    /// <para>
    /// Type descriptors are compared by name as well as by address: two modules can each carry a
    /// copy of the same descriptor, and the name is what the language says identity is.
    /// </para>
    /// </remarks>
    public sealed class RttiLibrary
    {
        private readonly ArmEmulator _emulator;
        private readonly CallFrame _frame;

        public RttiLibrary(ArmEmulator emulator, CallFrame frame)
        {
            _emulator = emulator;
            _frame = frame;
        }

        /// <summary>Casts that failed, for the report.</summary>
        public List<string> Failures { get; } = new();

        public void RegisterInto(Dictionary<string, Action> handlers)
        {
            // void* __RTDynamicCast(void* in, LONG vfDelta, void* srcType, void* targetType, BOOL isRef)
            handlers["__RTDynamicCast"] = () =>
            {
                long result = DynamicCast(_frame.Arg(0), _frame.SignedArg(1), _frame.Arg(3));
                _frame.Return(result);
            };

            // void* __RTCastToVoid(void* in): the complete object.
            handlers["__RTCastToVoid"] = () =>
            {
                long input = _frame.Arg(0);
                _frame.Return(input == 0 ? 0 : CompleteObject(input, 0, out _) ?? 0);
            };
        }

        private uint Read(long address, uint fallback = 0) => _emulator.ReadUInt32(address, fallback);

        /// <summary>The complete object an interior pointer belongs to, and its locator.</summary>
        private long? CompleteObject(long input, int vfDelta, out long locator)
        {
            locator = 0;
            long subobject = input + vfDelta;
            long vtable = Read(subobject);
            if (vtable < 4)
            {
                return null;
            }

            locator = Read(vtable - 4);
            if (locator == 0)
            {
                return null;
            }

            int offset = unchecked((int)Read(locator + 4));
            int cdOffset = unchecked((int)Read(locator + 8));
            long complete = subobject - offset;
            if (cdOffset != 0)
            {
                // vtordisp adjustment: the displacement is stored just before the subobject.
                complete -= unchecked((int)Read(subobject - cdOffset));
            }

            return complete;
        }

        private long DynamicCast(long input, int vfDelta, long targetType)
        {
            if (input == 0 || targetType == 0)
            {
                return 0;
            }

            try
            {
                if (CompleteObject(input, vfDelta, out long locator) is not { } complete)
                {
                    return 0;
                }

                long hierarchy = Read(locator + 16);
                int count = (int)Read(hierarchy + 8);
                long bases = Read(hierarchy + 12);
                string targetName = TypeName(targetType);

                for (int i = 0; i < count && i < 512; i++)
                {
                    long descriptor = Read(bases + (i * 4));
                    long type = Read(descriptor);
                    if (type != targetType && TypeName(type) != targetName)
                    {
                        continue;
                    }

                    int mdisp = unchecked((int)Read(descriptor + 8));
                    int pdisp = unchecked((int)Read(descriptor + 12));
                    int vdisp = unchecked((int)Read(descriptor + 16));

                    long result = complete;
                    if (pdisp >= 0)
                    {
                        long vbtable = Read(complete + pdisp);
                        result += pdisp + unchecked((int)Read(vbtable + vdisp));
                    }

                    return result + mdisp;
                }

                if (Failures.Count < 50)
                {
                    Failures.Add($"dynamic_cast<{targetName}> of a {TypeName(Read(locator + 12))} failed");
                }
            }
            catch (CpuMemoryException)
            {
            }

            return 0;
        }

        /// <summary>A TypeDescriptor's decorated name: after the vtable pointer and the spare word.</summary>
        private string TypeName(long typeDescriptor)
            => typeDescriptor == 0 ? string.Empty : _frame.ReadNarrowString(typeDescriptor + 8, 512);
    }
}

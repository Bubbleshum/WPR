namespace WPR.Wp8Native
{
    /// <summary>
    /// IPropertyStore and PROPVARIANT: <c>MFCreatePropertiesFromMediaType</c>,
    /// <c>MFCreateMediaTypeFromProperties</c>, <c>PropVariantClear</c> and <c>PropVariantCopy</c>.
    /// </summary>
    /// <remarks>
    /// <para>A media type's properties are its attributes. On Windows the same function also builds
    /// the WinRT <c>IMediaEncodingProperties</c> family, whose property set is keyed by the attribute
    /// GUIDs; that family is not in this runtime's metadata, so only <c>IPropertyStore</c> (and
    /// <c>IPropertyStoreCapabilities</c>-free IUnknown) is offered, and any other interface is
    /// E_NOINTERFACE and recorded. In the property store an attribute is the key
    /// <c>{fmtid = attribute GUID, pid = PID_FIRST_USABLE (2)}</c>; reading back, any pid is
    /// accepted, since the fmtid alone names the attribute.</para>
    /// <para>PROPVARIANT on ARM is 16 bytes: vt at 0, three reserved words, the value at 8 (a
    /// counted BLOB or vector is cElems/cbSize at 8 and the pointer at 12). Everything the caller is
    /// handed is its own: strings, GUIDs and blobs CoTaskMemAlloc'd, interfaces AddRef'd.</para>
    /// </remarks>
    internal sealed partial class MediaFoundation
    {
        public static readonly Guid IidPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
        public const int PidFirstUsable = 2;
        private const long BadVarType = unchecked((int)0x80020008);   // DISP_E_BADVARTYPE

        // VARTYPEs handled beyond the seven attribute types.
        private const ushort VtEmpty = 0, VtI2 = 2, VtI4 = 3, VtR4 = 4, VtError = 10, VtBool = 11, VtI1 = 16, VtUi1 = 17,
            VtUi2 = 18, VtI8 = 20, VtInt = 22, VtUint = 23, VtFiletime = 64, VtBstr = 8, VtBlobPlain = 65, VtDispatch = 9;

        private static bool IsScalar(ushort vt) =>
            vt is VtI2 or VtI4 or VtR4 or VtError or VtBool or VtI1 or VtUi1 or VtUi2 or VtI8 or VtInt or VtUint or VtFiletime
                or MfValue.VtUi4 or MfValue.VtUi8 or MfValue.VtR8;

        /// <summary>Reads any PROPVARIANT this host understands; null for another VARTYPE. VT_EMPTY is a value of type 0.</summary>
        public MfValue? ReadAnyPropVariant(long pv)
        {
            if (pv == 0)
            {
                return null;
            }

            ushort vt = _com.U16(pv);
            if (vt == VtEmpty)
            {
                return new MfValue { Type = VtEmpty };
            }

            if (vt == MfValue.VtR8)
            {
                return new MfValue { Type = vt, Real = BitConverter.UInt64BitsToDouble(_com.U64(pv + 8)) };
            }

            if (IsScalar(vt))
            {
                return new MfValue { Type = vt, Integer = _com.U64(pv + 8) };
            }

            return vt switch
            {
                VtBstr => new MfValue { Type = vt, Text = _com.U32(pv + 8) == 0 ? string.Empty : _com.ReadWide(_com.U32(pv + 8)) },
                VtBlobPlain => new MfValue
                {
                    Type = vt,
                    Blob = _com.U32(pv + 8) == 0 ? [] : _e.ReadMemory(_com.U32(pv + 12), (int)Math.Min(_com.U32(pv + 8), CallFrame.SaneLengthLimit)),
                },
                VtDispatch => new MfValue { Type = vt, Unknown = _com.U32(pv + 8) },
                _ => _attributes.ReadPropVariant(pv),
            };
        }

        /// <summary>Writes a PROPVARIANT the caller owns: payloads copied, interfaces AddRef'd (queued in <paramref name="calls"/>).</summary>
        public void WriteAnyPropVariant(long pv, MfValue value, List<GuestCall> calls)
        {
            if (pv == 0)
            {
                return;
            }

            _e.WriteMemory(pv, new byte[16]);
            _e.WriteMemory(pv, BitConverter.GetBytes(value.Type));
            switch (value.Type)
            {
                case VtEmpty:
                    break;
                case var vt when IsScalar(vt) && vt != MfValue.VtR8:
                    _com.W64(pv + 8, value.Integer);
                    break;
                case VtBstr:
                    _com.W32(pv + 8, _com.AllocBstr(value.Text ?? string.Empty));
                    break;
                case VtBlobPlain:
                    long b = _com.Alloc(value.Blob!.Length);
                    _e.WriteMemory(b, value.Blob);
                    _com.W32(pv + 8, value.Blob.Length);
                    _com.W32(pv + 12, b);
                    break;
                case VtDispatch:
                    _com.W32(pv + 8, value.Unknown);
                    _com.AddRefAny(value.Unknown, calls);
                    break;
                default:
                    _attributes.WritePropVariant(pv, value, calls);
                    break;
            }
        }

        /// <summary>
        /// PropVariantClear: frees what the PROPVARIANT owns and sets it to VT_EMPTY. A VARTYPE this
        /// host does not know is DISP_E_BADVARTYPE, left untouched (it cannot know what to free).
        /// </summary>
        public long ClearPropVariant(long pv, List<GuestCall> calls)
        {
            if (pv == 0)
            {
                return MediaCom.SOk;
            }

            ushort vt = _com.U16(pv);
            switch (vt)
            {
                case VtEmpty:
                case var scalar when IsScalar(scalar):
                    break;
                case MfValue.VtLpwstr:
                case MfValue.VtClsid:
                    _com.Free(_com.U32(pv + 8));
                    break;
                case VtBstr:
                    long text = _com.U32(pv + 8);
                    if (text != 0)
                    {
                        _com.Free(text - 4);
                    }

                    break;
                case VtBlobPlain:
                case MfValue.VtBlob:
                    _com.Free(_com.U32(pv + 12));
                    break;
                case MfValue.VtUnknown:
                case VtDispatch:
                    _com.ReleaseAny(_com.U32(pv + 8), calls);
                    break;
                default:
                    return BadVarType;
            }

            _e.WriteMemory(pv, new byte[16]);
            return MediaCom.SOk;
        }

        // ------------------------------------------------------------------------------
        // The property store
        // ------------------------------------------------------------------------------

        internal sealed class PropertyStore : MediaObject
        {
            public override string Kind => "IPropertyStore";

            public readonly List<(Guid Fmtid, uint Pid, MfValue Value)> Items = new();

            public int IndexOf(Guid fmtid, uint pid) => Items.FindIndex(i => i.Fmtid == fmtid && i.Pid == pid);

            public override IEnumerable<long> Destroy() =>
                Items.Where(i => i.Value.Type is MfValue.VtUnknown or VtDispatch).Select(i => i.Value.Unknown).ToList();
        }

        private MediaInterface? _propertyStoreShape;

        // IPropertyStore: GetCount 3, GetAt 4, GetValue 5, SetValue 6, Commit 7.
        private MediaInterface PropertyStoreShape => _propertyStoreShape ??= new("IPropertyStore", [IidPropertyStore], 8, () => new()
        {
            [3] = ("GetCount", () => WithStore(p => Out32(Arg(1), p.Items.Count))),
            [4] = ("GetAt", () => WithStore(p =>
            {
                if (Arg(2) == 0)
                {
                    Return(MediaCom.EPointer);
                    return;
                }

                if (Arg(1) >= p.Items.Count)
                {
                    _e.WriteMemory(Arg(2), new byte[20]);
                    Return(MediaCom.EInvalidArg);
                    return;
                }

                (Guid fmtid, uint pid, _) = p.Items[(int)Arg(1)];
                _com.WriteGuid(Arg(2), fmtid);
                _com.W32(Arg(2) + 16, pid);
                Return(MediaCom.SOk);
            })),

            // GetValue(REFPROPERTYKEY, PROPVARIANT*): a key not in the store is S_OK with VT_EMPTY.
            [5] = ("GetValue", () => WithStore(p =>
            {
                if (Arg(1) == 0 || Arg(2) == 0)
                {
                    Return(MediaCom.EPointer);
                    return;
                }

                int index = p.IndexOf(_com.ReadGuid(Arg(1)), _com.U32(Arg(1) + 16));
                List<GuestCall> calls = new();
                WriteAnyPropVariant(Arg(2), index < 0 ? new MfValue { Type = VtEmpty } : p.Items[index].Value, calls);
                _com.Then(calls, _ => Return(MediaCom.SOk));
            })),

            // SetValue(REFPROPERTYKEY, REFPROPVARIANT): the store keeps its own copy.
            [6] = ("SetValue", () => WithStore(p =>
            {
                if (Arg(1) == 0 || Arg(2) == 0)
                {
                    Return(MediaCom.EPointer);
                    return;
                }

                if (ReadAnyPropVariant(Arg(2)) is not { } value)
                {
                    Return(BadVarType);
                    return;
                }

                Guid fmtid = _com.ReadGuid(Arg(1));
                uint pid = _com.U32(Arg(1) + 16);
                List<GuestCall> calls = new();
                int index = p.IndexOf(fmtid, pid);
                if (index >= 0)
                {
                    if (p.Items[index].Value.Type is MfValue.VtUnknown or VtDispatch)
                    {
                        _com.ReleaseAny(p.Items[index].Value.Unknown, calls);
                    }

                    p.Items.RemoveAt(index);
                }

                if (value.Type is MfValue.VtUnknown or VtDispatch)
                {
                    _com.AddRefAny(value.Unknown, calls);
                }

                if (value.Type != VtEmpty)
                {
                    if (index >= 0)
                    {
                        p.Items.Insert(index, (fmtid, pid, value));
                    }
                    else
                    {
                        p.Items.Add((fmtid, pid, value));
                    }
                }

                _com.Then(calls, _ => Return(MediaCom.SOk));
            })),

            // In memory only; there is nothing to commit to.
            [7] = ("Commit", () => WithStore(_ => Return(MediaCom.SOk))),
        });

        private void WithStore(Action<PropertyStore> act)
        {
            if (_com.Self<PropertyStore>() is { } p)
            {
                act(p);
            }
            else
            {
                Return(MediaCom.EPointer);
            }
        }

        public long CreatePropertyStore(MfAttributeStore? from, List<GuestCall> calls)
        {
            PropertyStore store = new();
            foreach (var (key, value) in from?.Items ?? [])
            {
                store.Items.Add((key, PidFirstUsable, value));
                if (value.Type == MfValue.VtUnknown)
                {
                    _com.AddRefAny(value.Unknown, calls);
                }
            }

            return _com.Expose(store, PropertyStoreShape);
        }

        /// <summary>A property value as the attribute type it maps to; null when it has none.</summary>
        private static MfValue? AsAttribute(MfValue v) => v.Type switch
        {
            MfValue.VtUi4 or MfValue.VtUi8 or MfValue.VtR8 or MfValue.VtClsid or MfValue.VtLpwstr or MfValue.VtBlob or MfValue.VtUnknown => v,
            VtI4 or VtInt or VtUint => new MfValue { Type = MfValue.VtUi4, Integer = (uint)v.Integer },
            VtUi1 or VtI1 => new MfValue { Type = MfValue.VtUi4, Integer = (uint)(v.Integer & 0xFF) },
            VtUi2 or VtI2 => new MfValue { Type = MfValue.VtUi4, Integer = (uint)(v.Integer & 0xFFFF) },
            VtBool => new MfValue { Type = MfValue.VtUi4, Integer = (v.Integer & 0xFFFF) != 0 ? 1u : 0u },
            VtI8 or VtFiletime => new MfValue { Type = MfValue.VtUi8, Integer = v.Integer },
            VtR4 => new MfValue { Type = MfValue.VtR8, Real = BitConverter.UInt32BitsToSingle((uint)v.Integer) },
            VtBstr => new MfValue { Type = MfValue.VtLpwstr, Text = v.Text },
            VtBlobPlain => new MfValue { Type = MfValue.VtBlob, Blob = v.Blob },
            VtDispatch => new MfValue { Type = MfValue.VtUnknown, Unknown = v.Unknown },
            _ => null,
        };

        /// <summary>Builds a media type from properties; MF_E_INVALIDMEDIATYPE without a major type.</summary>
        private long MediaTypeFromProperties(IEnumerable<(Guid Fmtid, MfValue Value)> properties, out long mediaType, List<GuestCall> calls)
        {
            MfMediaType type = new();
            foreach (var (fmtid, value) in properties)
            {
                if (AsAttribute(value) is not { } attribute)
                {
                    continue;
                }

                if (attribute.Type == MfValue.VtUnknown)
                {
                    _com.AddRefAny(attribute.Unknown, calls);
                }

                if (type.Attributes.Set(fmtid, attribute) is { Type: MfValue.VtUnknown } replaced)
                {
                    _com.ReleaseAny(replaced.Unknown, calls);
                }
            }

            mediaType = 0;
            if (type.Attributes.GuidOf(MtMajorType) is null)
            {
                foreach (var item in type.Attributes.Items.Where(i => i.Value.Type == MfValue.VtUnknown))
                {
                    _com.ReleaseAny(item.Value.Unknown, calls);
                }

                return MfEInvalidMediaType;
            }

            mediaType = _com.Expose(type, MediaTypeShape);
            return MediaCom.SOk;
        }

        private void RegisterProperties(Dictionary<string, Action> handlers)
        {
            // HRESULT MFCreatePropertiesFromMediaType(IMFMediaType*, REFIID riid, void** ppv).
            handlers["MFCreatePropertiesFromMediaType"] = () =>
            {
                long outPointer = Arg(2);
                if (outPointer == 0)
                {
                    Return(MediaCom.EPointer);
                    return;
                }

                _com.W32(outPointer, 0);
                MfAttributeStore? type = (_com.Lookup(Arg(0)) as MfMediaType)?.Attributes;
                if (type is null)
                {
                    if (Arg(0) != 0)
                    {
                        _com.Record("MFCreatePropertiesFromMediaType on an IMFMediaType not created here -> E_NOTIMPL");
                    }

                    Return(Arg(0) == 0 ? MediaCom.EPointer : MediaCom.ENotImpl);
                    return;
                }

                Guid iid = _com.ReadGuid(Arg(1));
                if (iid != IidPropertyStore && iid != MediaCom.IidUnknown)
                {
                    _com.Record($"MFCreatePropertiesFromMediaType({iid}): only IPropertyStore is offered here (Windows.Media.MediaProperties is not in this runtime) -> E_NOINTERFACE");
                    Return(MediaCom.ENoInterface);
                    return;
                }

                List<GuestCall> calls = new();
                long store = CreatePropertyStore(type, calls);
                _com.Then(calls, _ =>
                {
                    _com.W32(outPointer, store);
                    Return(MediaCom.SOk);
                });
            };

            // HRESULT MFCreateMediaTypeFromProperties(IUnknown* punkStream, IMFMediaType**): from any
            // IPropertyStore - one of these directly, a game's through its own methods.
            handlers["MFCreateMediaTypeFromProperties"] = () =>
            {
                long source = Arg(0), outPointer = Arg(1);
                if (outPointer == 0 || source == 0)
                {
                    _com.W32(outPointer, 0);
                    Return(MediaCom.EPointer);
                    return;
                }

                _com.W32(outPointer, 0);
                if (_com.Lookup(source) is PropertyStore mine)
                {
                    List<GuestCall> calls = new();
                    long hr = MediaTypeFromProperties(mine.Items.Select(i => (i.Fmtid, i.Value)).ToList(), out long created, calls);
                    _com.Then(calls, _ =>
                    {
                        _com.W32(outPointer, created);
                        Return(hr);
                    });
                    return;
                }

                if (_com.Lookup(source) is not null)
                {
                    Return(MediaCom.ENoInterface);
                    return;
                }

                ReadGuestPropertyStore(source, outPointer);
            };

            // HRESULT PropVariantClear(PROPVARIANT*).
            handlers["PropVariantClear"] = () =>
            {
                List<GuestCall> calls = new();
                long hr = ClearPropVariant(Arg(0), calls);
                _com.Then(calls, _ => Return(hr));
            };

            // HRESULT PropVariantCopy(PROPVARIANT* pvarDest, const PROPVARIANT* pvarSrc): a deep copy
            // over whatever pvarDest held, which is not freed (the documented contract).
            handlers["PropVariantCopy"] = () =>
            {
                if (Arg(0) == 0 || Arg(1) == 0)
                {
                    Return(MediaCom.EInvalidArg);
                    return;
                }

                if (ReadAnyPropVariant(Arg(1)) is not { } value)
                {
                    Return(BadVarType);
                    return;
                }

                List<GuestCall> calls = new();
                WriteAnyPropVariant(Arg(0), value, calls);
                _com.Then(calls, _ => Return(MediaCom.SOk));
            };
        }

        /// <summary>
        /// A game's own property store (or any object answering IPropertyStore), read through
        /// QueryInterface, GetCount, and GetAt/GetValue per property; each value is cleared after
        /// reading, as the caller of GetValue must.
        /// </summary>
        private void ReadGuestPropertyStore(long source, long outPointer)
        {
            GuestOp op = new() { Hr = MediaCom.SOk };
            long created = 0;

            IEnumerable<GuestCall> Script()
            {
                long scratch = _com.Alloc(16 + 4 + 20 + 16 + 4);
                long iidAt = scratch, countAt = scratch + 16, keyAt = scratch + 20, valueAt = scratch + 40, storeAt = scratch + 56;
                List<(Guid, MfValue)> read = new();
                List<long> owned = new();
                long store = 0;
                static bool Failed(long hr) => (hr & 0x80000000) != 0;
                GuestCall? Method(int slot, string name, params long[] args) =>
                    _com.GuestMethod(store, slot) is { } fn ? new GuestCall($"IPropertyStore::{name}", fn, [store, .. args]) : null;

                try
                {
                    if (_com.GuestMethod(source, 0) is not { } qi)
                    {
                        op.Hr = MediaCom.EPointer;
                        yield break;
                    }

                    _com.WriteGuid(iidAt, IidPropertyStore);
                    yield return new GuestCall("IUnknown::QueryInterface", qi, [source, iidAt, storeAt]);
                    store = Failed(op.Last) ? 0 : _com.U32(storeAt);
                    if (store == 0)
                    {
                        op.Hr = MediaCom.ENoInterface;
                        yield break;
                    }

                    if (Method(3, "GetCount", countAt) is { } getCount && Method(4, "GetAt", 0, keyAt) is not null && Method(5, "GetValue", keyAt, valueAt) is not null)
                    {
                        yield return getCount;
                        if (Failed(op.Last))
                        {
                            op.Hr = op.Last;
                        }
                    }
                    else
                    {
                        op.Hr = MediaCom.EPointer;
                    }

                    int count = op.Hr == MediaCom.SOk ? (int)Math.Min(_com.U32(countAt), 4096) : 0;
                    for (int i = 0; i < count && op.Hr == MediaCom.SOk; i++)
                    {
                        _e.WriteMemory(valueAt, new byte[16]);
                        yield return Method(4, "GetAt", i, keyAt)!.Value;
                        if (!Failed(op.Last))
                        {
                            yield return Method(5, "GetValue", keyAt, valueAt)!.Value;
                        }

                        if (Failed(op.Last))
                        {
                            op.Hr = op.Last;
                            break;
                        }

                        if (ReadAnyPropVariant(valueAt) is { } v && v.Type != VtEmpty)
                        {
                            read.Add((_com.ReadGuid(keyAt), v));
                        }

                        List<GuestCall> clears = new();
                        if (_com.U16(valueAt) is MfValue.VtUnknown or VtDispatch)
                        {
                            // The reference GetValue handed out is released once the media type holds its own.
                            owned.Add(_com.U32(valueAt + 8));
                            _e.WriteMemory(valueAt, new byte[16]);
                        }
                        else
                        {
                            ClearPropVariant(valueAt, clears);
                        }

                        foreach (GuestCall c in clears)
                        {
                            yield return c;
                        }
                    }

                    List<GuestCall> after = new();
                    if (op.Hr == MediaCom.SOk)
                    {
                        op.Hr = MediaTypeFromProperties(read, out created, after);
                    }

                    foreach (long o in owned)
                    {
                        _com.ReleaseAny(o, after);
                    }

                    _com.ReleaseAny(store, after);
                    foreach (GuestCall c in after)
                    {
                        yield return c;
                    }
                }
                finally
                {
                    _com.Free(scratch);
                }
            }

            _com.Run(op, Script(), () =>
            {
                _com.W32(outPointer, created);
                Return(op.Hr);
            });
        }
    }
}

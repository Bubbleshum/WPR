namespace WPR.Wp8Native
{
    /// <summary>A Media Foundation attribute value: one of the seven MF_ATTRIBUTE_TYPEs.</summary>
    internal sealed class MfValue
    {
        public const ushort VtUi4 = 19, VtUi8 = 21, VtR8 = 5, VtClsid = 72, VtLpwstr = 31, VtBlob = 0x1011, VtUnknown = 13;

        public ushort Type { get; init; }
        public ulong Integer { get; init; }
        public double Real { get; init; }
        public Guid Guid { get; init; }
        public string? Text { get; init; }
        public byte[]? Blob { get; init; }
        public long Unknown { get; init; }

        public bool SameAs(MfValue other) => Type == other.Type && Type switch
        {
            VtUi4 or VtUi8 => Integer == other.Integer,
            VtR8 => Real.Equals(other.Real),
            VtClsid => Guid == other.Guid,
            VtLpwstr => string.Equals(Text, other.Text, StringComparison.Ordinal),
            VtBlob => Blob!.AsSpan().SequenceEqual(other.Blob),
            _ => Unknown == other.Unknown,
        };
    }

    /// <summary>The attribute store behind IMFAttributes, in insertion order (GetItemByIndex is ordered).</summary>
    internal sealed class MfAttributeStore
    {
        private readonly List<KeyValuePair<Guid, MfValue>> _items = new();

        public int Count => _items.Count;

        public IReadOnlyList<KeyValuePair<Guid, MfValue>> Items => _items;

        public MfValue? Get(Guid key)
        {
            foreach (var item in _items)
            {
                if (item.Key == key)
                {
                    return item.Value;
                }
            }

            return null;
        }

        /// <summary>Sets a value; returns the value it replaced, if any.</summary>
        public MfValue? Set(Guid key, MfValue value)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Key == key)
                {
                    MfValue old = _items[i].Value;
                    _items[i] = new(key, value);
                    return old;
                }
            }

            _items.Add(new(key, value));
            return null;
        }

        public MfValue? Delete(Guid key)
        {
            int i = _items.FindIndex(p => p.Key == key);
            if (i < 0)
            {
                return null;
            }

            MfValue old = _items[i].Value;
            _items.RemoveAt(i);
            return old;
        }

        public List<MfValue> Clear()
        {
            List<MfValue> old = _items.Select(p => p.Value).ToList();
            _items.Clear();
            return old;
        }

        public uint? UInt32(Guid key) => Get(key) is { Type: MfValue.VtUi4 } v ? (uint)v.Integer : null;

        public ulong? UInt64(Guid key) => Get(key) is { Type: MfValue.VtUi8 } v ? v.Integer : null;

        public Guid? GuidOf(Guid key) => Get(key) is { Type: MfValue.VtClsid } v ? v.Guid : null;

        public long UnknownOf(Guid key) => Get(key) is { Type: MfValue.VtUnknown } v ? v.Unknown : 0;

        public bool IsSubsetOf(MfAttributeStore other) =>
            _items.All(p => other.Get(p.Key) is { } theirs && theirs.SameAs(p.Value));

        // ---- the opaque blob of MFGetAttributesAsBlob: a header, fixed 32-byte items, then data ----

        private const uint BlobMagic = 0x494D4641; // "AFMI"

        public byte[] ToBlob()
        {
            List<KeyValuePair<Guid, MfValue>> items = _items.Where(p => p.Value.Type != MfValue.VtUnknown).ToList();
            using MemoryStream data = new();
            using MemoryStream fixedPart = new();
            using BinaryWriter w = new(fixedPart);
            w.Write(BlobMagic);
            w.Write(items.Count);
            int dataStart = 8 + (items.Count * 32);
            foreach (var (key, value) in items)
            {
                w.Write(key.ToByteArray());
                w.Write((ulong)value.Type);
                switch (value.Type)
                {
                    case MfValue.VtUi4:
                    case MfValue.VtUi8:
                        w.Write(value.Integer);
                        break;
                    case MfValue.VtR8:
                        w.Write(value.Real);
                        break;
                    default:
                        byte[] bytes = value.Type switch
                        {
                            MfValue.VtClsid => value.Guid.ToByteArray(),
                            MfValue.VtLpwstr => System.Text.Encoding.Unicode.GetBytes(value.Text + "\0"),
                            _ => value.Blob!,
                        };
                        w.Write((uint)bytes.Length);
                        w.Write((uint)(dataStart + data.Length));
                        data.Write(bytes);
                        break;
                }
            }

            return [.. fixedPart.ToArray(), .. data.ToArray()];
        }

        /// <summary>Replaces the contents from a blob; false (contents untouched) if it is not one.</summary>
        public bool FromBlob(byte[] blob)
        {
            if (blob.Length < 8 || BitConverter.ToUInt32(blob, 0) != BlobMagic)
            {
                return false;
            }

            int count = BitConverter.ToInt32(blob, 4);
            if (count < 0 || 8L + (count * 32L) > blob.Length)
            {
                return false;
            }

            List<KeyValuePair<Guid, MfValue>> parsed = new();
            for (int i = 0; i < count; i++)
            {
                int at = 8 + (i * 32);
                Guid key = new(blob.AsSpan(at, 16));
                ushort type = (ushort)BitConverter.ToUInt64(blob, at + 16);
                MfValue? value;
                if (type is MfValue.VtUi4 or MfValue.VtUi8)
                {
                    value = new MfValue { Type = type, Integer = BitConverter.ToUInt64(blob, at + 24) };
                }
                else if (type == MfValue.VtR8)
                {
                    value = new MfValue { Type = type, Real = BitConverter.ToDouble(blob, at + 24) };
                }
                else
                {
                    int size = BitConverter.ToInt32(blob, at + 24), offset = BitConverter.ToInt32(blob, at + 28);
                    if (size < 0 || offset < 0 || (long)offset + size > blob.Length)
                    {
                        return false;
                    }

                    byte[] bytes = blob.AsSpan(offset, size).ToArray();
                    value = type switch
                    {
                        MfValue.VtClsid when size == 16 => new MfValue { Type = type, Guid = new Guid(bytes) },
                        MfValue.VtLpwstr => new MfValue { Type = type, Text = System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0') },
                        MfValue.VtBlob => new MfValue { Type = type, Blob = bytes },
                        _ => null,
                    };
                }

                if (value is null)
                {
                    return false;
                }

                parsed.Add(new(key, value));
            }

            _items.Clear();
            _items.AddRange(parsed);
            return true;
        }
    }

    /// <summary>Anything exposing IMFAttributes: attributes, media types, samples.</summary>
    internal abstract class MfAttributedObject : MediaObject
    {
        public MfAttributeStore Attributes { get; } = new();

        public override IEnumerable<long> Destroy() =>
            Attributes.Items.Where(p => p.Value.Type == MfValue.VtUnknown).Select(p => p.Value.Unknown).ToList();
    }

    /// <summary>The thirty methods of IMFAttributes (slots 3-32), shared by every interface deriving from it.</summary>
    internal sealed class MfAttributeMethods
    {
        public const long MfEAttributeNotFound = unchecked((int)0xC00D36E6);
        public const long MfEInvalidType = unchecked((int)0xC00D36BD);

        public const int SlotCount = 33;

        private readonly MediaCom _com;

        public MfAttributeMethods(MediaCom com) => _com = com;

        private long Arg(int i) => _com.Arg(i);

        private void Return(long v) => _com.Return(v);

        private MfAttributeStore? Store() => _com.Self<MfAttributedObject>()?.Attributes;

        /// <summary>Reads the value a REFPROPVARIANT holds; null if its type is not an attribute type.</summary>
        public MfValue? ReadPropVariant(long pv)
        {
            if (pv == 0)
            {
                return null;
            }

            ushort vt = _com.U16(pv);
            return vt switch
            {
                MfValue.VtUi4 => new MfValue { Type = vt, Integer = _com.U32(pv + 8) },
                MfValue.VtUi8 => new MfValue { Type = vt, Integer = _com.U64(pv + 8) },
                MfValue.VtR8 => new MfValue { Type = vt, Real = BitConverter.UInt64BitsToDouble(_com.U64(pv + 8)) },
                MfValue.VtClsid => new MfValue { Type = vt, Guid = _com.ReadGuid(_com.U32(pv + 8)) },
                MfValue.VtLpwstr => new MfValue { Type = vt, Text = _com.ReadWide(_com.U32(pv + 8)) },
                MfValue.VtBlob => new MfValue
                {
                    Type = vt,
                    Blob = _com.U32(pv + 8) == 0 ? [] : _com.Emulator.ReadMemory(_com.U32(pv + 12), (int)_com.U32(pv + 8)),
                },
                MfValue.VtUnknown => new MfValue { Type = vt, Unknown = _com.U32(pv + 8) },
                _ => null,
            };
        }

        /// <summary>Writes a PROPVARIANT the caller owns (strings, GUIDs and blobs CoTaskMemAlloc'd).</summary>
        public void WritePropVariant(long pv, MfValue value, List<GuestCall> calls)
        {
            if (pv == 0)
            {
                return;
            }

            _com.Emulator.WriteMemory(pv, new byte[16]);
            _com.Emulator.WriteMemory(pv, BitConverter.GetBytes(value.Type));
            switch (value.Type)
            {
                case MfValue.VtUi4:
                    _com.W32(pv + 8, (long)value.Integer);
                    break;
                case MfValue.VtUi8:
                    _com.W64(pv + 8, value.Integer);
                    break;
                case MfValue.VtR8:
                    _com.W64(pv + 8, BitConverter.DoubleToUInt64Bits(value.Real));
                    break;
                case MfValue.VtClsid:
                    long g = _com.Alloc(16);
                    _com.WriteGuid(g, value.Guid);
                    _com.W32(pv + 8, g);
                    break;
                case MfValue.VtLpwstr:
                    _com.W32(pv + 8, _com.AllocWide(value.Text ?? string.Empty));
                    break;
                case MfValue.VtBlob:
                    long b = _com.Alloc(value.Blob!.Length);
                    _com.Emulator.WriteMemory(b, value.Blob);
                    _com.W32(pv + 8, value.Blob.Length);
                    _com.W32(pv + 12, b);
                    break;
                case MfValue.VtUnknown:
                    _com.W32(pv + 8, value.Unknown);
                    _com.AddRefAny(value.Unknown, calls);
                    break;
            }
        }

        /// <summary>Stores <paramref name="value"/>, keeping references right, and returns S_OK.</summary>
        public void Put(MfAttributeStore store, Guid key, MfValue value)
        {
            List<GuestCall> calls = new();
            if (value.Type == MfValue.VtUnknown)
            {
                _com.AddRefAny(value.Unknown, calls);
            }

            if (store.Set(key, value) is { Type: MfValue.VtUnknown } old)
            {
                _com.ReleaseAny(old.Unknown, calls);
            }

            _com.Then(calls, _ => Return(MediaCom.SOk));
        }

        /// <summary>Copies every item of <paramref name="from"/> into <paramref name="to"/>, adding references.</summary>
        public void CopyInto(MfAttributeStore from, MfAttributeStore to, List<GuestCall> calls)
        {
            foreach (MfValue old in to.Clear())
            {
                if (old.Type == MfValue.VtUnknown)
                {
                    _com.ReleaseAny(old.Unknown, calls);
                }
            }

            foreach (var (key, value) in from.Items)
            {
                if (value.Type == MfValue.VtUnknown)
                {
                    _com.AddRefAny(value.Unknown, calls);
                }

                to.Set(key, value);
            }
        }

        private void Typed(ushort type, Action<MfValue> found)
        {
            MfAttributeStore? store = Store();
            if (store is null)
            {
                Return(MediaCom.EPointer);
                return;
            }

            MfValue? value = store.Get(_com.ReadGuid(Arg(1)));
            if (value is null)
            {
                Return(MfEAttributeNotFound);
            }
            else if (value.Type != type)
            {
                Return(MfEInvalidType);
            }
            else
            {
                found(value);
            }
        }

        private void Set(MfValue value)
        {
            MfAttributeStore? store = Store();
            if (store is null)
            {
                Return(MediaCom.EPointer);
                return;
            }

            Put(store, _com.ReadGuid(Arg(1)), value);
        }

        public Dictionary<int, (string, Action)> Build(Dictionary<int, (string, Action)>? more = null)
        {
            Dictionary<int, (string, Action)> m = new()
            {
                [3] = ("GetItem", () =>
                {
                    MfAttributeStore? store = Store();
                    MfValue? value = store?.Get(_com.ReadGuid(Arg(1)));
                    if (value is null)
                    {
                        Return(store is null ? MediaCom.EPointer : MfEAttributeNotFound);
                        return;
                    }

                    List<GuestCall> calls = new();
                    WritePropVariant(Arg(2), value, calls);
                    _com.Then(calls, _ => Return(MediaCom.SOk));
                }),
                [4] = ("GetItemType", () =>
                {
                    MfValue? value = Store()?.Get(_com.ReadGuid(Arg(1)));
                    if (value is null)
                    {
                        Return(MfEAttributeNotFound);
                        return;
                    }

                    _com.W32(Arg(2), value.Type);
                    Return(MediaCom.SOk);
                }),
                [5] = ("CompareItem", () =>
                {
                    MfValue? mine = Store()?.Get(_com.ReadGuid(Arg(1)));
                    MfValue? theirs = ReadPropVariant(Arg(2));
                    _com.W32(Arg(3), mine is not null && theirs is not null && mine.SameAs(theirs) ? 1 : 0);
                    Return(MediaCom.SOk);
                }),
                [6] = ("Compare", () =>
                {
                    MfAttributeStore? mine = Store();
                    MfAttributeStore? theirs = (_com.Lookup(Arg(1)) as MfAttributedObject)?.Attributes;
                    if (mine is null || Arg(3) == 0)
                    {
                        Return(MediaCom.EPointer);
                        return;
                    }

                    if (theirs is null)
                    {
                        _com.Record("IMFAttributes::Compare against an IMFAttributes not created here -> E_NOTIMPL");
                        Return(MediaCom.ENotImpl);
                        return;
                    }

                    bool? match = Arg(2) switch
                    {
                        0 => mine.IsSubsetOf(theirs),                                      // OUR_ITEMS
                        1 => theirs.IsSubsetOf(mine),                                      // THEIR_ITEMS
                        2 => mine.Count == theirs.Count && mine.IsSubsetOf(theirs),        // ALL_ITEMS
                        3 => mine.Items.All(p => theirs.Get(p.Key) is not { } t || t.SameAs(p.Value)), // INTERSECTION
                        4 => mine.Count <= theirs.Count ? mine.IsSubsetOf(theirs) : theirs.IsSubsetOf(mine), // SMALLER
                        _ => null,
                    };

                    if (match is null)
                    {
                        Return(MediaCom.EInvalidArg);
                        return;
                    }

                    _com.W32(Arg(3), match.Value ? 1 : 0);
                    Return(MediaCom.SOk);
                }),
                [7] = ("GetUINT32", () => Typed(MfValue.VtUi4, v => { _com.W32(Arg(2), (long)v.Integer); Return(MediaCom.SOk); })),
                [8] = ("GetUINT64", () => Typed(MfValue.VtUi8, v => { _com.W64(Arg(2), v.Integer); Return(MediaCom.SOk); })),
                [9] = ("GetDouble", () => Typed(MfValue.VtR8, v => { _com.W64(Arg(2), BitConverter.DoubleToUInt64Bits(v.Real)); Return(MediaCom.SOk); })),
                [10] = ("GetGUID", () => Typed(MfValue.VtClsid, v => { _com.WriteGuid(Arg(2), v.Guid); Return(MediaCom.SOk); })),
                [11] = ("GetStringLength", () => Typed(MfValue.VtLpwstr, v => { _com.W32(Arg(2), v.Text!.Length); Return(MediaCom.SOk); })),
                [12] = ("GetString", () => Typed(MfValue.VtLpwstr, v =>
                {
                    // GetString(key, LPWSTR, UINT32 cchBufSize, UINT32* pcchLength).
                    _com.W32(Arg(4), v.Text!.Length);
                    if (Arg(3) < v.Text.Length + 1)
                    {
                        Return(MediaCom.ENotSufficientBuffer);
                        return;
                    }

                    _com.Emulator.WriteMemory(Arg(2), System.Text.Encoding.Unicode.GetBytes(v.Text + "\0"));
                    Return(MediaCom.SOk);
                })),
                [13] = ("GetAllocatedString", () => Typed(MfValue.VtLpwstr, v =>
                {
                    _com.W32(Arg(2), _com.AllocWide(v.Text!));
                    _com.W32(Arg(3), v.Text!.Length);
                    Return(MediaCom.SOk);
                })),
                [14] = ("GetBlobSize", () => Typed(MfValue.VtBlob, v => { _com.W32(Arg(2), v.Blob!.Length); Return(MediaCom.SOk); })),
                [15] = ("GetBlob", () => Typed(MfValue.VtBlob, v =>
                {
                    // GetBlob(key, UINT8*, UINT32 cbBufSize, UINT32* pcbBlobSize).
                    _com.W32(Arg(4), v.Blob!.Length);
                    if (Arg(3) < v.Blob.Length)
                    {
                        Return(MediaCom.ENotSufficientBuffer);
                        return;
                    }

                    _com.Emulator.WriteMemory(Arg(2), v.Blob);
                    Return(MediaCom.SOk);
                })),
                [16] = ("GetAllocatedBlob", () => Typed(MfValue.VtBlob, v =>
                {
                    long p = _com.Alloc(v.Blob!.Length);
                    _com.Emulator.WriteMemory(p, v.Blob);
                    _com.W32(Arg(2), p);
                    _com.W32(Arg(3), v.Blob.Length);
                    Return(MediaCom.SOk);
                })),
                [17] = ("GetUnknown", () => Typed(MfValue.VtUnknown, v =>
                    _com.QueryAny(v.Unknown, _com.ReadGuid(Arg(2)), Arg(3), hr => Return(hr)))),
                [18] = ("SetItem", () =>
                {
                    MfValue? value = ReadPropVariant(Arg(2));
                    if (value is null)
                    {
                        Return(MfEInvalidType);
                        return;
                    }

                    Set(value);
                }),
                [19] = ("DeleteItem", () =>
                {
                    MfAttributeStore? store = Store();
                    List<GuestCall> calls = new();
                    if (store?.Delete(_com.ReadGuid(Arg(1))) is { Type: MfValue.VtUnknown } old)
                    {
                        _com.ReleaseAny(old.Unknown, calls);
                    }

                    _com.Then(calls, _ => Return(MediaCom.SOk));
                }),
                [20] = ("DeleteAllItems", () =>
                {
                    List<GuestCall> calls = new();
                    foreach (MfValue old in Store()?.Clear() ?? [])
                    {
                        if (old.Type == MfValue.VtUnknown)
                        {
                            _com.ReleaseAny(old.Unknown, calls);
                        }
                    }

                    _com.Then(calls, _ => Return(MediaCom.SOk));
                }),
                [21] = ("SetUINT32", () => Set(new MfValue { Type = MfValue.VtUi4, Integer = (uint)Arg(2) })),

                // SetUINT64(REFGUID, UINT64): the value is the r2:r3 pair.
                [22] = ("SetUINT64", () => Set(new MfValue { Type = MfValue.VtUi8, Integer = (ulong)(uint)Arg(2) | ((ulong)(uint)Arg(3) << 32) })),

                // SetDouble(REFGUID, double): hard-float, so the value is d0.
                [23] = ("SetDouble", () => Set(new MfValue { Type = MfValue.VtR8, Real = _com.DoubleArg(0) })),
                [24] = ("SetGUID", () =>
                {
                    if (Arg(2) == 0)
                    {
                        Return(MediaCom.EPointer);
                        return;
                    }

                    Set(new MfValue { Type = MfValue.VtClsid, Guid = _com.ReadGuid(Arg(2)) });
                }),
                [25] = ("SetString", () =>
                {
                    if (Arg(2) == 0)
                    {
                        Return(MediaCom.EPointer);
                        return;
                    }

                    Set(new MfValue { Type = MfValue.VtLpwstr, Text = _com.ReadWide(Arg(2)) });
                }),
                [26] = ("SetBlob", () => Set(new MfValue
                {
                    Type = MfValue.VtBlob,
                    Blob = Arg(3) == 0 ? [] : _com.Emulator.ReadMemory(Arg(2), (int)Math.Min(Arg(3), CallFrame.SaneLengthLimit)),
                })),
                [27] = ("SetUnknown", () => Set(new MfValue { Type = MfValue.VtUnknown, Unknown = Arg(2) })),

                // One guest thread runs at a time and every method here is atomic, so the store
                // is never seen half-written; the lock has nothing to exclude.
                [28] = ("LockStore", () => Return(MediaCom.SOk)),
                [29] = ("UnlockStore", () => Return(MediaCom.SOk)),
                [30] = ("GetCount", () =>
                {
                    _com.W32(Arg(1), Store()?.Count ?? 0);
                    Return(MediaCom.SOk);
                }),
                [31] = ("GetItemByIndex", () =>
                {
                    MfAttributeStore? store = Store();
                    if (store is null || Arg(1) >= store.Count)
                    {
                        Return(MediaCom.EInvalidArg);
                        return;
                    }

                    var item = store.Items[(int)Arg(1)];
                    _com.WriteGuid(Arg(2), item.Key);
                    List<GuestCall> calls = new();
                    WritePropVariant(Arg(3), item.Value, calls);
                    _com.Then(calls, _ => Return(MediaCom.SOk));
                }),
                [32] = ("CopyAllItems", () =>
                {
                    MfAttributeStore? mine = Store();
                    MfAttributeStore? dest = (_com.Lookup(Arg(1)) as MfAttributedObject)?.Attributes;
                    if (mine is null || Arg(1) == 0)
                    {
                        Return(MediaCom.EPointer);
                        return;
                    }

                    if (dest is null)
                    {
                        _com.Record("IMFAttributes::CopyAllItems into an IMFAttributes not created here -> E_NOTIMPL");
                        Return(MediaCom.ENotImpl);
                        return;
                    }

                    List<GuestCall> calls = new();
                    if (!ReferenceEquals(mine, dest))
                    {
                        CopyInto(mine, dest, calls);
                    }

                    _com.Then(calls, _ => Return(MediaCom.SOk));
                }),
            };

            foreach (var (slot, method) in more ?? new())
            {
                m[slot] = method;
            }

            return m;
        }
    }
}

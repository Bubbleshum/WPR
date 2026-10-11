using System.Globalization;

namespace WPR.Wp8Native
{
    /// <summary>
    /// Windows.Foundation for the system classes: completed async operations, collections, boxing
    /// (<c>PropertyValue</c>), <c>PropertySet</c>, <c>Uri</c> and <c>WwwFormUrlDecoder</c>.
    /// </summary>
    public sealed partial class WinRtRuntime
    {
        /// <summary>WinRT type signatures, as the parameterized-IID algorithm spells them.</summary>
        private static class Sig
        {
            public static string Of(Guid iid) => "{" + iid.ToString("D") + "}";

            public static string Of(string interfaceName) => Of(WinRtSystemInterfaces.Find(interfaceName)?.Iid ?? throw new KeyNotFoundException($"no WinRT metadata for {interfaceName}"));

            public static string P(string genericInterface, params string[] arguments)
                => $"pinterface({Of(genericInterface)};{string.Join(";", arguments)})";

            public static string Rc(string className, string defaultInterface) => $"rc({className};{Of(defaultInterface)})";

            public static string Rc(string className, Guid defaultInterface) => $"rc({className};{Of(defaultInterface)})";

            public static Guid Iid(string genericInterface, params string[] arguments) => ParameterizedIid(P(genericInterface, arguments));

            public const string String = "string";
            public const string Boolean = "b1";
            public const string UInt32 = "u4";
            public const string UInt64 = "u8";
            public const string Object = "cinterface(IInspectable)";

            public const string AsyncOperation = "Windows.Foundation.IAsyncOperation`1";
            public const string AsyncOperationWithProgress = "Windows.Foundation.IAsyncOperationWithProgress`2";
            public const string VectorView = "Windows.Foundation.Collections.IVectorView`1";
            public const string Vector = "Windows.Foundation.Collections.IVector`1";
            public const string Iterable = "Windows.Foundation.Collections.IIterable`1";
            public const string Iterator = "Windows.Foundation.Collections.IIterator`1";
            public const string Map = "Windows.Foundation.Collections.IMap`2";
            public const string MapView = "Windows.Foundation.Collections.IMapView`2";
            public const string ObservableMap = "Windows.Foundation.Collections.IObservableMap`2";
            public const string KeyValuePair = "Windows.Foundation.Collections.IKeyValuePair`2";
            public const string MapChangedEventArgs = "Windows.Foundation.Collections.IMapChangedEventArgs`1";
            public const string Reference = "Windows.Foundation.IReference`1";
            public const string ReferenceArray = "Windows.Foundation.IReferenceArray`1";

            public static readonly string StringObjectPair = P(KeyValuePair, String, Object);
        }

        private sealed partial class SystemClasses
        {
            /// <summary>AsyncStatus: Started 0, Completed 1, Canceled 2, Error 3.</summary>
            public const int AsyncCompleted = 1;
            public const int AsyncCanceled = 2;
            public const int AsyncError = 3;

            private uint _nextAsyncId = 1;

            public sealed class AsyncState
            {
                public int Status { get; set; } = AsyncCompleted;

                public int Error { get; set; }

                public Action<RtCall>? Result { get; init; }

                public long Completed { get; set; }

                public long Progress { get; set; }

                public uint Id { get; init; }

                public bool Closed { get; set; }
            }

            // -----------------------------------------------------------------------------------
            // Async operations. Nothing behind them is still running - the work was done before the
            // object existed - so they are born Completed (or Error), and a completion handler set on
            // one is invoked at once, as WinRT does for an operation that has already finished.
            // -----------------------------------------------------------------------------------

            private Iface AsyncInfo() => I(
                Meta("Windows.Foundation.IAsyncInfo"),
                ("get_Id", c => c.RetU32(c.State<AsyncState>().Id)),
                ("get_Status", c => c.RetU32((uint)c.State<AsyncState>().Status)),
                ("get_ErrorCode", c => c.RetI32(c.State<AsyncState>().Error)),
                ("Cancel", c => c.Ok()),   // already finished: cancelling does nothing
                ("Close", c =>
                {
                    c.State<AsyncState>().Closed = true;
                    c.Ok();
                }));

            private void PutCompleted(RtCall c)
            {
                AsyncState s = c.State<AsyncState>();
                long handler = c.P(0);
                if (s.Completed != 0)
                {
                    c.Fail(E_ILLEGAL_DELEGATE_ASSIGNMENT);
                    return;
                }

                s.Completed = handler;
                long sender = c.Self.Primary;
                if (!InvokeThenReturn([handler], h => [h, sender, s.Status], S_OK, $"{c.Self.Class.Name}.Completed"))
                {
                    c.Ok();
                }
            }

            private static void GetResults(RtCall c)
            {
                AsyncState s = c.State<AsyncState>();
                if (s.Status == AsyncError)
                {
                    c.Fail(s.Error);
                }
                else if (s.Status != AsyncCompleted || s.Closed)
                {
                    c.Fail(E_ILLEGAL_METHOD_CALL);
                }
                else if (s.Result is { } result)
                {
                    result(c);
                }
                else
                {
                    c.Ok();
                }
            }

            /// <summary>An <c>IAsyncOperation&lt;T&gt;</c>, finished, whose GetResults runs <paramref name="result"/>.</summary>
            public long Operation(string resultSignature, Action<RtCall> result)
                => AsyncObject(OperationClass(resultSignature), new AsyncState { Result = result, Id = _nextAsyncId++ });

            /// <summary>An <c>IAsyncOperation&lt;T&gt;</c> that failed with <paramref name="hresult"/>: its status is Error and GetResults returns the HRESULT.</summary>
            public long FailedOperation(string resultSignature, int hresult)
                => AsyncObject(OperationClass(resultSignature), new AsyncState { Status = AsyncError, Error = hresult, Id = _nextAsyncId++ });

            /// <summary>An <c>IAsyncAction</c>, finished, or failed with <paramref name="hresult"/>.</summary>
            public long AsyncAction(int hresult = S_OK)
            {
                ComClass cls = Class("Windows.Foundation.IAsyncAction", [
                    I(
                        Meta("Windows.Foundation.IAsyncAction"),
                        ("put_Completed", PutCompleted),
                        ("get_Completed", c => c.RetObj(c.State<AsyncState>().Completed)),
                        ("GetResults", GetResults)),
                    AsyncInfo(),
                ]);
                return AsyncObject(cls, new AsyncState
                {
                    Status = hresult == S_OK ? AsyncCompleted : AsyncError,
                    Error = hresult,
                    Id = _nextAsyncId++,
                });
            }

            /// <summary>An <c>IAsyncOperationWithProgress&lt;T, P&gt;</c>, finished: progress is never reported, which the contract allows.</summary>
            public long ProgressOperation(string resultSignature, string progressSignature, Action<RtCall> result, int hresult = S_OK)
            {
                Guid iid = Sig.Iid(Sig.AsyncOperationWithProgress, resultSignature, progressSignature);
                ComClass cls = Class($"Windows.Foundation.IAsyncOperationWithProgress<{resultSignature},{progressSignature}>", [
                    I(
                        Meta(Sig.AsyncOperationWithProgress, iid),
                        ("put_Progress", c =>
                        {
                            c.State<AsyncState>().Progress = c.P(0);
                            c.Ok();
                        }),
                        ("get_Progress", c => c.RetObj(c.State<AsyncState>().Progress)),
                        ("put_Completed", PutCompleted),
                        ("get_Completed", c => c.RetObj(c.State<AsyncState>().Completed)),
                        ("GetResults", GetResults)),
                    AsyncInfo(),
                ]);
                return AsyncObject(cls, new AsyncState
                {
                    Status = hresult == S_OK ? AsyncCompleted : AsyncError,
                    Error = hresult,
                    Result = result,
                    Id = _nextAsyncId++,
                });
            }

            private ComClass OperationClass(string resultSignature)
            {
                Guid iid = Sig.Iid(Sig.AsyncOperation, resultSignature);
                return Class($"Windows.Foundation.IAsyncOperation<{resultSignature}>", [
                    I(
                        Meta(Sig.AsyncOperation, iid),
                        ("put_Completed", PutCompleted),
                        ("get_Completed", c => c.RetObj(c.State<AsyncState>().Completed)),
                        ("GetResults", GetResults)),
                    AsyncInfo(),
                ]);
            }

            private long AsyncObject(ComClass cls, AsyncState state)
            {
                if (state.Status == AsyncError)
                {
                    Note($"{cls.Name} #{state.Id} failed 0x{state.Error:X8}");
                }

                return New(cls, state).Primary;
            }

            /// <summary>Shorthands for the common results.</summary>
            public long OperationOfObject(string resultSignature, long value) => Operation(resultSignature, c => c.RetObj(value));

            public long OperationOfBool(bool value) => Operation(Sig.Boolean, c => c.RetBool(value));

            public long OperationOfUInt32(uint value) => Operation(Sig.UInt32, c => c.RetU32(value));

            public long OperationOfString(string value) => Operation(Sig.String, c => c.RetStr(value));

            // -----------------------------------------------------------------------------------
            // Collections over host lists: IVectorView<T>/IVector<T> and their IIterable/IIterator.
            // -----------------------------------------------------------------------------------

            /// <summary>A list of objects (interface pointers) or strings, and the signature of its element type.</summary>
            public sealed class ListState
            {
                public required string Element { get; init; }

                public List<long>? Objects { get; init; }

                public List<string>? Strings { get; init; }

                public int Count => Objects?.Count ?? Strings!.Count;
            }

            private sealed class IteratorState
            {
                public required ListState List { get; init; }

                public int Index { get; set; }
            }

            private void ReturnElement(RtCall c, ListState list, int index)
            {
                if (list.Objects is { } objects)
                {
                    c.RetObj(objects[index]);
                }
                else
                {
                    c.RetStr(list.Strings![index]);
                }
            }

            /// <summary>Writes elements [start, start+n) into a guest array of words, AddRef'ing objects and making new HSTRINGs.</summary>
            private void WriteElements(ListState list, int start, int n, long data)
            {
                for (int i = 0; i < n; i++)
                {
                    long value;
                    if (list.Objects is { } objects)
                    {
                        value = objects[start + i];
                        if (Lookup(value) is { } obj)
                        {
                            obj.Refs++;
                        }
                    }
                    else
                    {
                        string text = list.Strings![start + i];
                        value = text.Length == 0 ? 0 : _rt._strings.Create(text);
                    }

                    _e.WriteUInt32(data + (i * 4), (uint)value);
                }
            }

            private int IndexOf(ListState list, long value)
                => list.Objects is { } objects ? objects.IndexOf(value) : list.Strings!.IndexOf(_rt._strings.ReadText(value));

            /// <summary>The IVectorView members over the list the state accessor finds.</summary>
            private Iface VectorViewMembers(InterfaceDef def, Func<RtCall, ListState> list) => I(
                def,
                ("GetAt", c =>
                {
                    ListState l = list(c);
                    long index = c.P(0);
                    if (index < 0 || index >= l.Count)
                    {
                        c.Fail(E_BOUNDS);
                        return;
                    }

                    ReturnElement(c, l, (int)index);
                }),
                ("get_Size", c => c.RetU32((uint)list(c).Count)),
                ("IndexOf", c =>
                {
                    int found = IndexOf(list(c), c.P(0));
                    c.OutBytes(1, BitConverter.GetBytes((uint)Math.Max(found, 0)));
                    c.RetBool(found >= 0);
                }),
                ("GetMany", c =>
                {
                    ListState l = list(c);
                    long start = c.P(0);
                    (uint capacity, long data) = c.Array(1);
                    if (start > l.Count)
                    {
                        c.Fail(E_BOUNDS);
                        return;
                    }

                    int n = (int)Math.Min(capacity, (uint)(l.Count - start));
                    WriteElements(l, (int)start, n, data);
                    c.RetU32((uint)n);
                }));

            private Iface IterableMembers(string element, Func<RtCall, ListState> list) => I(
                Meta(Sig.Iterable, Sig.Iid(Sig.Iterable, element)),
                ("First", c => c.RetObj(Iterator(list(c)))));

            /// <summary>A read-only view over objects or strings.</summary>
            public long VectorView(ListState list)
            {
                ComClass cls = Class($"Windows.Foundation.Collections.IVectorView<{list.Element}>", [
                    VectorViewMembers(Meta(Sig.VectorView, Sig.Iid(Sig.VectorView, list.Element)), c => c.State<ListState>()),
                    IterableMembers(list.Element, c => c.State<ListState>()),
                ]);
                return New(cls, list).Primary;
            }

            public long ObjectView(string element, IEnumerable<long> items) => VectorView(new ListState { Element = element, Objects = items.ToList() });

            public long StringView(IEnumerable<string> items) => VectorView(new ListState { Element = Sig.String, Strings = items.ToList() });

            public long Iterator(ListState list)
            {
                ComClass cls = Class($"Windows.Foundation.Collections.IIterator<{list.Element}>", [
                    I(
                        Meta(Sig.Iterator, Sig.Iid(Sig.Iterator, list.Element)),
                        ("get_Current", c =>
                        {
                            IteratorState s = c.State<IteratorState>();
                            if (s.Index >= s.List.Count)
                            {
                                c.Fail(E_BOUNDS);
                                return;
                            }

                            ReturnElement(c, s.List, s.Index);
                        }),
                        ("get_HasCurrent", c =>
                        {
                            IteratorState s = c.State<IteratorState>();
                            c.RetBool(s.Index < s.List.Count);
                        }),
                        ("MoveNext", c =>
                        {
                            IteratorState s = c.State<IteratorState>();
                            if (s.Index < s.List.Count)
                            {
                                s.Index++;
                            }

                            c.RetBool(s.Index < s.List.Count);
                        }),
                        ("GetMany", c =>
                        {
                            IteratorState s = c.State<IteratorState>();
                            (uint capacity, long data) = c.Array(0);
                            int n = (int)Math.Min(capacity, (uint)(s.List.Count - s.Index));
                            WriteElements(s.List, s.Index, n, data);
                            s.Index += n;
                            c.RetU32((uint)n);
                        })),
                ]);
                return New(cls, new IteratorState { List = list }).Primary;
            }

            /// <summary>A mutable <c>IVector&lt;String&gt;</c> (what FileIO.ReadLinesAsync hands back).</summary>
            public long StringVector(List<string> items)
            {
                ListState list = new() { Element = Sig.String, Strings = items };
                ComClass cls = Class("Windows.Foundation.Collections.IVector<string>", [
                    I(
                        Meta(Sig.Vector, Sig.Iid(Sig.Vector, Sig.String)),
                        ("GetAt", c =>
                        {
                            List<string> l = c.State<ListState>().Strings!;
                            if (c.P(0) >= l.Count)
                            {
                                c.Fail(E_BOUNDS);
                                return;
                            }

                            c.RetStr(l[(int)c.P(0)]);
                        }),
                        ("get_Size", c => c.RetU32((uint)c.State<ListState>().Count)),
                        ("GetView", c => c.RetObj(StringView(c.State<ListState>().Strings!))),
                        ("IndexOf", c =>
                        {
                            int found = c.State<ListState>().Strings!.IndexOf(c.Str(0));
                            c.OutBytes(1, BitConverter.GetBytes((uint)Math.Max(found, 0)));
                            c.RetBool(found >= 0);
                        }),
                        ("SetAt", c =>
                        {
                            List<string> l = c.State<ListState>().Strings!;
                            if (c.P(0) >= l.Count)
                            {
                                c.Fail(E_BOUNDS);
                                return;
                            }

                            l[(int)c.P(0)] = c.Str(1);
                            c.Ok();
                        }),
                        ("InsertAt", c =>
                        {
                            List<string> l = c.State<ListState>().Strings!;
                            if (c.P(0) > l.Count)
                            {
                                c.Fail(E_BOUNDS);
                                return;
                            }

                            l.Insert((int)c.P(0), c.Str(1));
                            c.Ok();
                        }),
                        ("RemoveAt", c =>
                        {
                            List<string> l = c.State<ListState>().Strings!;
                            if (c.P(0) >= l.Count)
                            {
                                c.Fail(E_BOUNDS);
                                return;
                            }

                            l.RemoveAt((int)c.P(0));
                            c.Ok();
                        }),
                        ("Append", c =>
                        {
                            c.State<ListState>().Strings!.Add(c.Str(0));
                            c.Ok();
                        }),
                        ("RemoveAtEnd", c =>
                        {
                            List<string> l = c.State<ListState>().Strings!;
                            if (l.Count == 0)
                            {
                                c.Fail(E_BOUNDS);
                                return;
                            }

                            l.RemoveAt(l.Count - 1);
                            c.Ok();
                        }),
                        ("Clear", c =>
                        {
                            c.State<ListState>().Strings!.Clear();
                            c.Ok();
                        }),
                        ("GetMany", c =>
                        {
                            ListState l = c.State<ListState>();
                            long start = c.P(0);
                            (uint capacity, long data) = c.Array(1);
                            if (start > l.Count)
                            {
                                c.Fail(E_BOUNDS);
                                return;
                            }

                            int n = (int)Math.Min(capacity, (uint)(l.Count - start));
                            WriteElements(l, (int)start, n, data);
                            c.RetU32((uint)n);
                        }),
                        ("ReplaceAll", c =>
                        {
                            (uint length, long data) = c.Array(0);
                            List<string> l = c.State<ListState>().Strings!;
                            l.Clear();
                            for (uint i = 0; i < length; i++)
                            {
                                l.Add(_rt._strings.ReadText(_e.ReadUInt32(data + (i * 4))));
                            }

                            c.Ok();
                        })),
                    IterableMembers(Sig.String, c => c.State<ListState>()),
                ]);
                return New(cls, list).Primary;
            }

            /// <summary>The strings in a string collection this runtime made (a vector, a view), or null for one the game made itself.</summary>
            public List<string>? StringsOf(long pointer) => StateOf<ListState>(pointer)?.Strings;

            // -----------------------------------------------------------------------------------
            // IMap<String, Object>: PropertySet, ApplicationDataCompositeValue and a settings
            // container's Values.
            // -----------------------------------------------------------------------------------

            public sealed class MapState
            {
                public MapState(StringComparer comparer) => Values = new Dictionary<string, long>(comparer);

                /// <summary>Key to IInspectable pointer (0 is a stored null).</summary>
                public Dictionary<string, long> Values { get; }

                public List<long> Handlers { get; } = new();

                /// <summary>Called after every change, before MapChanged is raised (settings persist here).</summary>
                public Action? Changed { get; set; }

                /// <summary>Validates a value on Insert; returns the failure HRESULT or S_OK.</summary>
                public Func<long, int>? Accepts { get; set; }

                public long NextToken { get; set; } = 1;

                public Dictionary<long, long> Tokens { get; } = new();
            }

            private static readonly Guid MapIid = Sig.Iid(Sig.Map, Sig.String, Sig.Object);
            private static readonly Guid MapViewIid = Sig.Iid(Sig.MapView, Sig.String, Sig.Object);
            private static readonly Guid ObservableMapIid = Sig.Iid(Sig.ObservableMap, Sig.String, Sig.Object);
            private static readonly Guid PairIterableIid = Sig.Iid(Sig.Iterable, Sig.StringObjectPair);

            /// <summary>A PropertySet-shaped class: IPropertySet, IObservableMap, IMap, IIterable of pairs.</summary>
            public ComClass MapClass(string className) => Class(className, [
                I(Meta("Windows.Foundation.Collections.IPropertySet")),
                I(
                    Meta(Sig.ObservableMap, ObservableMapIid),
                    ("add_MapChanged", c =>
                    {
                        MapState m = c.State<MapState>();
                        long token = m.NextToken++;
                        m.Tokens[token] = c.P(0);
                        m.Handlers.Add(c.P(0));
                        c.RetU64((ulong)token);
                        _rt.KeepAlive(c.P(0));   // a subscription keeps its delegate alive
                    }),
                    ("remove_MapChanged", c =>
                    {
                        MapState m = c.State<MapState>();
                        if (m.Tokens.Remove(c.P64(0), out long handler))
                        {
                            m.Handlers.Remove(handler);
                        }

                        c.Ok();
                    })),
                I(
                    Meta(Sig.Map, MapIid),
                    ("Lookup", c =>
                    {
                        if (!c.State<MapState>().Values.TryGetValue(c.Str(0), out long value))
                        {
                            c.Fail(E_BOUNDS);
                            return;
                        }

                        c.RetObj(value);
                    }),
                    ("get_Size", c => c.RetU32((uint)c.State<MapState>().Values.Count)),
                    ("HasKey", c => c.RetBool(c.State<MapState>().Values.ContainsKey(c.Str(0)))),
                    ("GetView", c => c.RetObj(MapView(new Dictionary<string, long>(c.State<MapState>().Values)))),
                    ("Insert", c =>
                    {
                        MapState m = c.State<MapState>();
                        string key = c.Str(0);
                        long value = c.P(1);
                        if (m.Accepts?.Invoke(value) is { } refused and not S_OK)
                        {
                            c.Fail(refused);
                            return;
                        }

                        bool replaced = m.Values.ContainsKey(key);
                        m.Values[key] = value;
                        if (Lookup(value) is { } obj)
                        {
                            obj.Refs++;
                        }

                        m.Changed?.Invoke();
                        c.RetBool(replaced);
                        RaiseMapChanged(c, m, replaced ? 3 : 1, key);
                    }),
                    ("Remove", c =>
                    {
                        MapState m = c.State<MapState>();
                        string key = c.Str(0);
                        if (!m.Values.Remove(key))
                        {
                            c.Fail(E_BOUNDS);
                            return;
                        }

                        m.Changed?.Invoke();
                        c.Ok();
                        RaiseMapChanged(c, m, 2, key);
                    }),
                    ("Clear", c =>
                    {
                        MapState m = c.State<MapState>();
                        m.Values.Clear();
                        m.Changed?.Invoke();
                        c.Ok();
                        RaiseMapChanged(c, m, 0, string.Empty);
                    })),
                I(
                    Meta(Sig.Iterable, PairIterableIid),
                    ("First", c => c.RetObj(Iterator(Pairs(c.State<MapState>().Values))))),
            ]);

            /// <summary>
            /// Raises MapChanged (CollectionChange: Reset 0, ItemInserted 1, ItemRemoved 2,
            /// ItemChanged 3) to every subscriber, after the change and with the member's own result
            /// already written. The stub's r0 is S_OK either way.
            /// </summary>
            private void RaiseMapChanged(RtCall c, MapState m, int change, string key)
            {
                if (m.Handlers.Count == 0)
                {
                    return;
                }

                long sender = PointerFor(c.Self, Sig.ObservableMap);
                long args = MapChangedArgs(change, key);
                InvokeThenReturn(m.Handlers.ToList(), h => [h, sender, args], S_OK, "MapChangedEventHandler");
            }

            private long MapChangedArgs(int change, string key)
            {
                ComClass cls = Class("Windows.Foundation.Collections.IMapChangedEventArgs<string>", [
                    I(
                        Meta(Sig.MapChangedEventArgs, Sig.Iid(Sig.MapChangedEventArgs, Sig.String)),
                        ("get_CollectionChange", c => c.RetU32((uint)(((int, string))c.Self.State!).Item1)),
                        ("get_Key", c => c.RetStr((((int, string))c.Self.State!).Item2))),
                ]);
                return New(cls, (change, key)).Primary;
            }

            private ListState Pairs(Dictionary<string, long> values)
                => new() { Element = Sig.StringObjectPair, Objects = values.Select(p => KeyValuePair(p.Key, p.Value)).ToList() };

            private long KeyValuePair(string key, long value)
            {
                ComClass cls = Class("Windows.Foundation.Collections.IKeyValuePair<string,Object>", [
                    I(
                        Meta(Sig.KeyValuePair, Sig.Iid(Sig.KeyValuePair, Sig.String, Sig.Object)),
                        ("get_Key", c => c.RetStr((((string, long))c.Self.State!).Item1)),
                        ("get_Value", c => c.RetObj((((string, long))c.Self.State!).Item2))),
                ]);
                return New(cls, (key, value)).Primary;
            }

            public long MapView(Dictionary<string, long> values)
            {
                ComClass cls = Class("Windows.Foundation.Collections.IMapView<string,Object>", [
                    I(
                        Meta(Sig.MapView, MapViewIid),
                        ("Lookup", c =>
                        {
                            if (!((Dictionary<string, long>)c.Self.State!).TryGetValue(c.Str(0), out long value))
                            {
                                c.Fail(E_BOUNDS);
                                return;
                            }

                            c.RetObj(value);
                        }),
                        ("get_Size", c => c.RetU32((uint)((Dictionary<string, long>)c.Self.State!).Count)),
                        ("HasKey", c => c.RetBool(((Dictionary<string, long>)c.Self.State!).ContainsKey(c.Str(0)))),
                        ("Split", c =>
                        {
                            // A view this small is not worth splitting: both halves are null, which the contract allows.
                            c.OutBytes(0, new byte[4]);
                            c.OutBytes(1, new byte[4]);
                            c.Ok();
                        })),
                    I(
                        Meta(Sig.Iterable, PairIterableIid),
                        ("First", c => c.RetObj(Iterator(Pairs((Dictionary<string, long>)c.Self.State!))))),
                ]);
                return New(cls, values).Primary;
            }

            public ComObject PropertySet(string className = "Windows.Foundation.Collections.PropertySet", StringComparer? comparer = null)
                => New(MapClass(className), new MapState(comparer ?? StringComparer.Ordinal));

            // -----------------------------------------------------------------------------------
            // PropertyValue: boxing.
            // -----------------------------------------------------------------------------------

            /// <summary>Windows.Foundation.PropertyType.</summary>
            public enum PropertyType
            {
                Empty = 0, UInt8 = 1, Int16 = 2, UInt16 = 3, Int32 = 4, UInt32 = 5, Int64 = 6, UInt64 = 7,
                Single = 8, Double = 9, Char16 = 10, Boolean = 11, String = 12, Inspectable = 13,
                DateTime = 14, TimeSpan = 15, Guid = 16, Point = 17, Size = 18, Rect = 19, OtherType = 20,
                ArrayOffset = 1024,
            }

            /// <summary>A boxed value: its PropertyType and the host value (an array type for an array).</summary>
            public sealed record Boxed(PropertyType Type, object Value)
            {
                public bool IsArray => Type > PropertyType.ArrayOffset;

                public PropertyType Element => IsArray ? Type - (int)PropertyType.ArrayOffset : Type;
            }

            private static string ScalarSignature(PropertyType type) => type switch
            {
                PropertyType.UInt8 => "u1",
                PropertyType.Int16 => "i2",
                PropertyType.UInt16 => "u2",
                PropertyType.Int32 => "i4",
                PropertyType.UInt32 => "u4",
                PropertyType.Int64 => "i8",
                PropertyType.UInt64 => "u8",
                PropertyType.Single => "f4",
                PropertyType.Double => "f8",
                PropertyType.Char16 => "c2",
                PropertyType.Boolean => "b1",
                PropertyType.String => "string",
                PropertyType.Guid => "g16",
                PropertyType.DateTime => "struct(Windows.Foundation.DateTime;i8)",
                PropertyType.TimeSpan => "struct(Windows.Foundation.TimeSpan;i8)",
                PropertyType.Point => "struct(Windows.Foundation.Point;f4;f4)",
                PropertyType.Size => "struct(Windows.Foundation.Size;f4;f4)",
                PropertyType.Rect => "struct(Windows.Foundation.Rect;f4;f4;f4;f4)",
                _ => "cinterface(IInspectable)",
            };

            /// <summary>The bytes of one scalar element as WinRT lays it out (an HSTRING is a new handle).</summary>
            private byte[] ScalarBytes(PropertyType type, object value) => type switch
            {
                PropertyType.UInt8 => [(byte)value],
                PropertyType.Int16 => BitConverter.GetBytes((short)value),
                PropertyType.UInt16 => BitConverter.GetBytes((ushort)value),
                PropertyType.Int32 => BitConverter.GetBytes((int)value),
                PropertyType.UInt32 => BitConverter.GetBytes((uint)value),
                PropertyType.Int64 or PropertyType.DateTime or PropertyType.TimeSpan => BitConverter.GetBytes((long)value),
                PropertyType.UInt64 => BitConverter.GetBytes((ulong)value),
                PropertyType.Single => BitConverter.GetBytes((float)value),
                PropertyType.Double => BitConverter.GetBytes((double)value),
                PropertyType.Char16 => BitConverter.GetBytes((char)value),
                PropertyType.Boolean => [(bool)value ? (byte)1 : (byte)0],
                PropertyType.String => BitConverter.GetBytes(((string)value).Length == 0 ? 0u : (uint)_rt._strings.Create((string)value)),
                PropertyType.Guid => ((Guid)value).ToByteArray(),
                PropertyType.Point or PropertyType.Size or PropertyType.Rect => ((float[])value).SelectMany(BitConverter.GetBytes).ToArray(),
                _ => BitConverter.GetBytes((uint)(long)value),
            };

            private static int ScalarSize(PropertyType type) => type switch
            {
                PropertyType.UInt8 or PropertyType.Boolean => 1,
                PropertyType.Int16 or PropertyType.UInt16 or PropertyType.Char16 => 2,
                PropertyType.Int64 or PropertyType.UInt64 or PropertyType.Double or PropertyType.DateTime or PropertyType.TimeSpan or PropertyType.Point or PropertyType.Size => 8,
                PropertyType.Guid or PropertyType.Rect => 16,
                _ => 4,
            };

            private static bool IsNumeric(PropertyType type) => type is >= PropertyType.UInt8 and <= PropertyType.Double;

            /// <summary>
            /// IPropertyValue's GetX on a numeric scalar: converted when the value fits, as WinRT's
            /// PropertyValue does (DISP_E_OVERFLOW when it does not, TYPE_E_TYPEMISMATCH for a
            /// non-integral float to an integer type or for a non-numeric value).
            /// </summary>
            private static (int HResult, object? Value) Coerce(Boxed boxed, PropertyType wanted)
            {
                if (boxed.Type == wanted)
                {
                    return (S_OK, boxed.Value);
                }

                if (!IsNumeric(boxed.Type) || !IsNumeric(wanted))
                {
                    return (TypeMismatch, null);
                }

                double asDouble = Convert.ToDouble(boxed.Value, CultureInfo.InvariantCulture);
                if (wanted is PropertyType.Single or PropertyType.Double)
                {
                    return (S_OK, wanted == PropertyType.Single ? (float)asDouble : asDouble);
                }

                if (boxed.Type is PropertyType.Single or PropertyType.Double && Math.Floor(asDouble) != asDouble)
                {
                    return (TypeMismatch, null);
                }

                try
                {
                    decimal exact = boxed.Type switch
                    {
                        PropertyType.Single or PropertyType.Double => (decimal)asDouble,
                        PropertyType.UInt64 => (ulong)boxed.Value,
                        _ => Convert.ToInt64(boxed.Value, CultureInfo.InvariantCulture),
                    };
                    object converted = wanted switch
                    {
                        PropertyType.UInt8 => (object)checked((byte)exact),
                        PropertyType.Int16 => checked((short)exact),
                        PropertyType.UInt16 => checked((ushort)exact),
                        PropertyType.Int32 => checked((int)exact),
                        PropertyType.UInt32 => checked((uint)exact),
                        PropertyType.Int64 => checked((long)exact),
                        _ => checked((ulong)exact),
                    };
                    return (S_OK, converted);
                }
                catch (OverflowException)
                {
                    return (Overflow, null);
                }
            }

            /// <summary>A boxed value: IPropertyValue plus IReference&lt;T&gt; (or IReferenceArray&lt;T&gt; for an array).</summary>
            public long Box(Boxed boxed)
            {
                string elementSig = ScalarSignature(boxed.Element);
                Iface reference = boxed.IsArray
                    ? I(
                        Meta(Sig.ReferenceArray, Sig.Iid(Sig.ReferenceArray, elementSig)),
                        ("get_Value", c =>
                        {
                            (uint length, long data) = ArrayToGuest(c.State<Boxed>());
                            OutArrayAt(c, 1, length, data);
                            c.Ok();
                        }))
                    : I(
                        Meta(Sig.Reference, Sig.Iid(Sig.Reference, elementSig)),
                        ("get_Value", c => c.RetBytes(ScalarBytes(c.State<Boxed>().Type, c.State<Boxed>().Value))));

                var members = new List<(string, Member)>
                {
                    ("get_Type", c => c.RetU32((uint)c.State<Boxed>().Type)),
                    ("get_IsNumericScalar", c => c.RetBool(IsNumeric(c.State<Boxed>().Type))),
                };

                foreach (PropertyType scalar in Enum.GetValues<PropertyType>().Where(t => t is > PropertyType.Empty and < PropertyType.OtherType and not PropertyType.Inspectable))
                {
                    PropertyType wanted = scalar;
                    string getter = "Get" + (wanted switch
                    {
                        PropertyType.UInt8 => "UInt8",
                        PropertyType.Char16 => "Char16",
                        _ => wanted.ToString(),
                    });
                    members.Add((getter, c =>
                    {
                        (int hr, object? value) = Coerce(c.State<Boxed>(), wanted);
                        if (hr != S_OK)
                        {
                            c.Fail(hr);
                            return;
                        }

                        c.RetBytes(ScalarBytes(wanted, value!));
                    }));

                    members.Add((getter + "Array", c =>
                    {
                        Boxed b = c.State<Boxed>();
                        if (!b.IsArray || b.Element != wanted)
                        {
                            c.Fail(TypeMismatch);
                            return;
                        }

                        (uint length, long data) = ArrayToGuest(b);
                        c.OutArray(0, length, data);
                        c.Ok();
                    }));
                }

                ComClass cls = Class($"Windows.Foundation.IReference<{(boxed.IsArray ? elementSig + "[]" : elementSig)}>", [
                    I(Meta("Windows.Foundation.IPropertyValue"), members.ToArray()),
                    reference,
                ]);
                return New(cls, boxed).Primary;
            }

            /// <summary>IReferenceArray's get_Value has no out-pointer in front of it in the metadata layout (it is the result), so place it by word.</summary>
            private void OutArrayAt(RtCall c, int firstWord, uint length, long data)
            {
                long lengthPointer = _rt.Arg(firstWord);
                long dataPointer = _rt.Arg(firstWord + 1);
                if (_rt.IsWritable(lengthPointer, 4))
                {
                    _e.WriteUInt32(lengthPointer, length);
                }

                if (_rt.IsWritable(dataPointer, 4))
                {
                    _e.WriteUInt32(dataPointer, (uint)data);
                }
            }

            private (uint Length, long Data) ArrayToGuest(Boxed b)
            {
                Array array = (Array)b.Value;
                int size = ScalarSize(b.Element);
                long data = array.Length == 0 ? 0 : _e.AllocateHeap(array.Length * size);
                for (int i = 0; i < array.Length; i++)
                {
                    _e.WriteMemory(data + (i * size), ScalarBytes(b.Element, array.GetValue(i)!));
                }

                return ((uint)array.Length, data);
            }

            /// <summary>The value behind a pointer, when it is a box this runtime made.</summary>
            public Boxed? Unbox(long pointer) => StateOf<Boxed>(pointer);

            private object ReadScalar(PropertyType type, long address) => type switch
            {
                PropertyType.UInt8 => _e.ReadMemory(address, 1)[0],
                PropertyType.Int16 => BitConverter.ToInt16(_e.ReadMemory(address, 2)),
                PropertyType.UInt16 => BitConverter.ToUInt16(_e.ReadMemory(address, 2)),
                PropertyType.Int32 => (int)_e.ReadUInt32(address),
                PropertyType.UInt32 => _e.ReadUInt32(address),
                PropertyType.Int64 or PropertyType.DateTime or PropertyType.TimeSpan => BitConverter.ToInt64(_e.ReadMemory(address, 8)),
                PropertyType.UInt64 => BitConverter.ToUInt64(_e.ReadMemory(address, 8)),
                PropertyType.Single => BitConverter.ToSingle(_e.ReadMemory(address, 4)),
                PropertyType.Double => BitConverter.ToDouble(_e.ReadMemory(address, 8)),
                PropertyType.Char16 => BitConverter.ToChar(_e.ReadMemory(address, 2)),
                PropertyType.Boolean => _e.ReadMemory(address, 1)[0] != 0,
                PropertyType.String => _rt._strings.ReadText(_e.ReadUInt32(address)),
                PropertyType.Guid => _e.ReadGuid(address),
                PropertyType.Point or PropertyType.Size => new[] { BitConverter.ToSingle(_e.ReadMemory(address, 4)), BitConverter.ToSingle(_e.ReadMemory(address + 4, 4)) },
                PropertyType.Rect => Enumerable.Range(0, 4).Select(i => BitConverter.ToSingle(_e.ReadMemory(address + (i * 4), 4))).ToArray(),
                _ => (long)_e.ReadUInt32(address),
            };

            private static Array NewArray(PropertyType element, int length) => element switch
            {
                PropertyType.UInt8 => new byte[length],
                PropertyType.Int16 => new short[length],
                PropertyType.UInt16 => new ushort[length],
                PropertyType.Int32 => new int[length],
                PropertyType.UInt32 => new uint[length],
                PropertyType.Int64 or PropertyType.DateTime or PropertyType.TimeSpan => new long[length],
                PropertyType.UInt64 => new ulong[length],
                PropertyType.Single => new float[length],
                PropertyType.Double => new double[length],
                PropertyType.Char16 => new char[length],
                PropertyType.Boolean => new bool[length],
                PropertyType.String => new string[length],
                PropertyType.Guid => new Guid[length],
                _ => new float[length][],
            };

            private float VfpSingle(int index) => BitConverter.UInt32BitsToSingle(_e.Cpu.VfpRead(index));

            private double VfpDouble(int index) => BitConverter.UInt64BitsToDouble(_e.Cpu.VfpRead(index * 2) | ((ulong)_e.Cpu.VfpRead((index * 2) + 1) << 32));

            private void RegisterPropertyValue()
            {
                var members = new List<(string, Member)>
                {
                    ("CreateEmpty", c => c.RetObj(0)),   // documented: an empty property value is null
                    ("CreateInspectable", c => c.RetObj(c.P(0))),   // documented: returns its argument
                };

                foreach (PropertyType scalar in Enum.GetValues<PropertyType>().Where(t => t is > PropertyType.Empty and < PropertyType.OtherType and not PropertyType.Inspectable))
                {
                    PropertyType type = scalar;
                    string name = "Create" + type;
                    members.Add((name, c =>
                    {
                        object value = type switch
                        {
                            // Hard-float: floats and float-only structs arrive in VFP registers.
                            PropertyType.Single => VfpSingle(0),
                            PropertyType.Double => VfpDouble(0),
                            PropertyType.Point or PropertyType.Size => new[] { VfpSingle(0), VfpSingle(1) },
                            PropertyType.Rect => new[] { VfpSingle(0), VfpSingle(1), VfpSingle(2), VfpSingle(3) },
                            PropertyType.Int64 or PropertyType.UInt64 or PropertyType.DateTime or PropertyType.TimeSpan
                                => type == PropertyType.UInt64 ? (object)(ulong)c.P64(0) : c.P64(0),
                            PropertyType.Guid => new Guid(c.PBytes(0, 4)),
                            PropertyType.String => c.Str(0),
                            PropertyType.Boolean => (c.P(0) & 0xFF) != 0,
                            PropertyType.UInt8 => (byte)c.P(0),
                            PropertyType.Int16 => (short)c.P(0),
                            PropertyType.UInt16 => (ushort)c.P(0),
                            PropertyType.Char16 => (char)c.P(0),
                            PropertyType.Int32 => (int)c.P(0),
                            _ => (uint)c.P(0),
                        };
                        c.RetObj(Box(new Boxed(type, value)));
                    }));

                    members.Add((name + "Array", c =>
                    {
                        (uint length, long data) = c.Array(0);
                        int size = ScalarSize(type);
                        Array array = NewArray(type, (int)length);
                        for (int i = 0; i < length; i++)
                        {
                            array.SetValue(ReadScalar(type, data + (i * size)), i);
                        }

                        c.RetObj(Box(new Boxed(type + (int)PropertyType.ArrayOffset, array)));
                    }));
                }

                Factory("Windows.Foundation.PropertyValue", I(Meta("Windows.Foundation.IPropertyValueStatics"), members.ToArray()));
            }

            // -----------------------------------------------------------------------------------
            // Uri and WwwFormUrlDecoder.
            // -----------------------------------------------------------------------------------

            public sealed record UriState(Uri Value, string Raw);

            /// <summary>An absolute URI as WinRT's Uri, or null when it does not parse (WinRT refuses relative ones).</summary>
            public long? NewUri(string raw)
            {
                if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out Uri? parsed))
                {
                    return null;
                }

                ComClass cls = Class("Windows.Foundation.Uri", [
                    I(
                        Meta("Windows.Foundation.IUriRuntimeClass"),
                        ("get_AbsoluteUri", c => c.RetStr(U(c).AbsoluteUri)),
                        ("get_DisplayUri", c => c.RetStr(U(c).GetComponents(UriComponents.AbsoluteUri, UriFormat.SafeUnescaped))),
                        ("get_Domain", c => c.RetStr(Domain(U(c)))),
                        ("get_Extension", c => c.RetStr(Path.GetExtension(U(c).AbsolutePath))),
                        ("get_Fragment", c => c.RetStr(U(c).Fragment)),
                        ("get_Host", c => c.RetStr(U(c).Host)),
                        ("get_Password", c => c.RetStr(U(c).UserInfo.Contains(':') ? U(c).UserInfo[(U(c).UserInfo.IndexOf(':') + 1)..] : string.Empty)),
                        ("get_Path", c => c.RetStr(U(c).AbsolutePath)),
                        ("get_Query", c => c.RetStr(U(c).Query)),
                        ("get_QueryParsed", c => c.RetObj(FormDecoder(U(c).Query))),
                        ("get_RawUri", c => c.RetStr(c.State<UriState>().Raw)),
                        ("get_SchemeName", c => c.RetStr(U(c).Scheme)),
                        ("get_UserName", c => c.RetStr(U(c).UserInfo.Split(':')[0])),
                        ("get_Port", c => c.RetI32(Math.Max(U(c).Port, 0))),
                        ("get_Suspicious", c => c.RetBool(false)),
                        ("Equals", c => c.RetBool(StateOf<UriState>(c.P(0)) is { } other && other.Value.AbsoluteUri == U(c).AbsoluteUri)),
                        ("CombineUri", c =>
                        {
                            if (!Uri.TryCreate(U(c), c.Str(0), out Uri? combined) || NewUri(combined.OriginalString) is not { } uri)
                            {
                                c.Fail(E_INVALIDARG);
                                return;
                            }

                            c.RetObj(uri);
                        })),
                    I(
                        Meta("Windows.Foundation.IUriRuntimeClassWithAbsoluteCanonicalUri"),
                        ("get_AbsoluteCanonicalUri", c => c.RetStr(U(c).AbsoluteUri)),
                        ("get_DisplayIri", c => c.RetStr(U(c).ToString()))),
                ]);
                return New(cls, new UriState(parsed, raw)).Primary;
            }

            private static Uri U(RtCall c) => c.State<UriState>().Value;

            /// <summary>The registrable domain: the last two labels of a host name, nothing for an IP address.</summary>
            private static string Domain(Uri uri)
            {
                if (uri.HostNameType != UriHostNameType.Dns)
                {
                    return string.Empty;
                }

                string[] labels = uri.Host.Split('.');
                return labels.Length <= 2 ? uri.Host : string.Join('.', labels[^2..]);
            }

            /// <summary>The absolute URI text of a Uri this runtime made, or null.</summary>
            public string? UriText(long pointer) => StateOf<UriState>(pointer)?.Value.OriginalString;

            private void RegisterUri()
            {
                Factory(
                    "Windows.Foundation.Uri",
                    I(
                        Meta("Windows.Foundation.IUriRuntimeClassFactory"),
                        ("CreateUri", c =>
                        {
                            if (NewUri(c.Str(0)) is { } uri)
                            {
                                c.RetObj(uri);
                            }
                            else
                            {
                                c.Fail(E_INVALIDARG);
                            }
                        }),
                        ("CreateWithRelativeUri", c =>
                        {
                            if (Uri.TryCreate(c.Str(0), UriKind.Absolute, out Uri? baseUri) &&
                                Uri.TryCreate(baseUri, c.Str(1), out Uri? combined) &&
                                NewUri(combined.OriginalString) is { } uri)
                            {
                                c.RetObj(uri);
                            }
                            else
                            {
                                c.Fail(E_INVALIDARG);
                            }
                        })),
                    I(
                        Meta("Windows.Foundation.IUriEscapeStatics"),
                        ("UnescapeComponent", c => c.RetStr(Uri.UnescapeDataString(c.Str(0)))),
                        ("EscapeComponent", c => c.RetStr(Uri.EscapeDataString(c.Str(0))))));

                Factory(
                    "Windows.Foundation.WwwFormUrlDecoder",
                    I(
                        Meta("Windows.Foundation.IWwwFormUrlDecoderRuntimeClassFactory"),
                        ("CreateWwwFormUrlDecoder", c => c.RetObj(FormDecoder(c.Str(0))))));
            }

            private static readonly string EntrySignature = Sig.Of("Windows.Foundation.IWwwFormUrlDecoderEntry");

            public long FormDecoder(string query)
            {
                var entries = new List<(string Name, string Value)>();
                foreach (string part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int equals = part.IndexOf('=');
                    string name = equals < 0 ? part : part[..equals];
                    string value = equals < 0 ? string.Empty : part[(equals + 1)..];
                    entries.Add((Uri.UnescapeDataString(name.Replace('+', ' ')), Uri.UnescapeDataString(value.Replace('+', ' '))));
                }

                ComClass entryClass = Class("Windows.Foundation.WwwFormUrlDecoderEntry", [
                    I(
                        Meta("Windows.Foundation.IWwwFormUrlDecoderEntry"),
                        ("get_Name", c => c.RetStr((((string, string))c.Self.State!).Item1)),
                        ("get_Value", c => c.RetStr((((string, string))c.Self.State!).Item2))),
                ]);

                ListState list = new() { Element = EntrySignature, Objects = entries.Select(e => New(entryClass, e).Primary).ToList() };
                ComClass cls = Class("Windows.Foundation.WwwFormUrlDecoder", [
                    I(
                        Meta("Windows.Foundation.IWwwFormUrlDecoderRuntimeClass"),
                        ("GetFirstValueByName", c =>
                        {
                            string name = c.Str(0);
                            foreach ((string n, string v) in c.State<DecoderState>().Entries)
                            {
                                if (n == name)
                                {
                                    c.RetStr(v);
                                    return;
                                }
                            }

                            c.Fail(E_INVALIDARG);
                        })),
                    VectorViewMembers(Meta(Sig.VectorView, Sig.Iid(Sig.VectorView, EntrySignature)), c => c.State<DecoderState>().List),
                    IterableMembers(EntrySignature, c => c.State<DecoderState>().List),
                ]);
                return New(cls, new DecoderState(entries, list)).Primary;
            }

            private sealed record DecoderState(List<(string Name, string Value)> Entries, ListState List);

            private void RegisterFoundation()
            {
                RegisterPropertyValue();
                RegisterUri();
                Factory(
                    "Windows.Foundation.Collections.PropertySet",
                    I(ActivationFactory, ("ActivateInstance", c => c.RetObj(PropertySet().Primary))));
            }
        }
    }
}

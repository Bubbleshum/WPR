namespace WPR.Wp8Native
{
    /// <summary>
    /// Vtable layouts of the WinRT system interfaces, from Windows' own metadata
    /// (<see cref="Table"/>, generated into <c>WinRtSystemInterfaces.g.cs</c> by
    /// <c>Tools/WinRtSlotGen</c>).
    /// </summary>
    /// <remarks>
    /// Before this, every slot on a stand-in object was either a hand-written constant confirmed by
    /// watching a game call it, or <c>slotN</c> in the log. The metadata is the fact the
    /// observations were approximating: slot = 6 (IInspectable) + declaration index. It names what
    /// an unimplemented call was (<c>ICoreWindow::add_KeyDown</c>, not <c>ICoreWindow::slot34</c>)
    /// and carries its signature, which is what deciding how to answer it properly needs.
    ///
    /// Interfaces outside the generated namespaces (Windows.Phone.*, Xbox services, the game's
    /// own) are not here; those still log by slot number.
    /// </remarks>
    public static partial class WinRtSystemInterfaces
    {
        public const int InspectableSlots = 6;

        /// <summary>One method: its name, its ABI-relevant signature text, and its slot.</summary>
        public sealed record Method(string Name, string Signature, int Slot);

        /// <summary>One interface, its methods in vtable order.</summary>
        public sealed record Interface(string FullName, Guid Iid, IReadOnlyList<Method> Methods)
        {
            public string ShortName => FullName[(FullName.LastIndexOf('.') + 1)..];

            public Method? At(int slot) =>
                slot >= InspectableSlots && slot - InspectableSlots < Methods.Count ? Methods[slot - InspectableSlots] : null;

            public Method? Find(string name) => Methods.FirstOrDefault(m => m.Name == name);
        }

        private static readonly Lazy<Index> Indexed = new(() => new Index());

        /// <summary>
        /// The short labels the runtime gives its stand-ins that really are these system interfaces.
        /// </summary>
        /// <remarks>
        /// An explicit list, not a short-name lookup: the runtime also labels stand-ins for the
        /// Windows Phone and Xbox Live interfaces with bare names, and two of them collide with
        /// unrelated desktop ones. Matching by short name answered the Xbox <c>IUser</c> with
        /// <c>Windows.System.IUser</c>'s signatures and the phone's <c>IMemoryManagerStatics</c> with
        /// <c>Windows.System</c>'s - caught by the slot-drift check on the first run.
        /// </remarks>
        private static readonly Dictionary<string, string> Aliases = new[]
        {
            "Windows.UI.Core.ICoreWindow", "Windows.UI.Core.ICoreWindowStatic", "Windows.UI.Core.ICoreDispatcher",
            "Windows.UI.Core.IPointerEventArgs", "Windows.UI.Input.IPointerPoint", "Windows.Devices.Input.IPointerDevice",
            "Windows.ApplicationModel.Core.ICoreApplication", "Windows.ApplicationModel.Core.ICoreApplicationView",
            "Windows.Storage.IApplicationData", "Windows.Storage.IApplicationDataStatics", "Windows.Storage.IStorageItem",
            "Windows.Graphics.Display.IDisplayPropertiesStatics", "Windows.System.Threading.IThreadPoolStatics",
            "Windows.Foundation.IAsyncAction",
            "Windows.ApplicationModel.Store.ILicenseInformation", "Windows.ApplicationModel.Store.IListingInformation",
            "Windows.Devices.Sensors.IAccelerometer", "Windows.Devices.Sensors.IAccelerometerStatics",
            "Windows.Devices.Sensors.IAccelerometerReading", "Windows.Devices.Sensors.IAccelerometerReadingChangedEventArgs",
            "Windows.Devices.Sensors.IGyrometer", "Windows.Devices.Sensors.IGyrometerStatics",
            "Windows.Devices.Sensors.IGyrometerReading", "Windows.Devices.Sensors.IGyrometerReadingChangedEventArgs",
        }.ToDictionary(n => n[(n.LastIndexOf('.') + 1)..], StringComparer.Ordinal);

        /// <summary>The interface by full name (<c>Windows.UI.Core.ICoreWindow</c>), or by one of the runtime's <see cref="Aliases"/>.</summary>
        public static Interface? Find(string name)
        {
            // Stand-ins are often labelled with a decoration: "IIterator<folders>", "IStorageItem<Local>".
            int angle = name.IndexOf('<');
            string key = angle > 0 ? name[..angle] : name;
            return Indexed.Value.ByFullName.GetValueOrDefault(key) ??
                (Aliases.TryGetValue(key, out string? full) ? Indexed.Value.ByFullName.GetValueOrDefault(full) : null);
        }

        /// <summary>The interface with this IID.</summary>
        public static Interface? Find(Guid iid) => Indexed.Value.ByIid.GetValueOrDefault(iid);

        /// <summary>
        /// <c>ICoreWindow::add_KeyDown</c> when the metadata knows the slot, <c>ICoreWindow::slot34</c>
        /// when it does not.
        /// </summary>
        public static string Describe(string interfaceName, int slot) =>
            Find(interfaceName)?.At(slot) is { } method ? $"{interfaceName}::{method.Name}" : $"{interfaceName}::slot{slot}";

        /// <summary>The method name at a slot, or <c>slotN</c>.</summary>
        public static string NameAt(string interfaceName, int slot) =>
            Find(interfaceName)?.At(slot)?.Name ?? $"slot{slot}";

        /// <summary>The slot of a named method. Throws when the metadata has no such member.</summary>
        public static int Slot(string interfaceName, string method) =>
            (Find(interfaceName) ?? throw new KeyNotFoundException($"no WinRT metadata for {interfaceName}"))
            .Find(method)?.Slot ?? throw new KeyNotFoundException($"{interfaceName} has no member {method}");

        /// <summary>
        /// Checks hand-written slot numbers against the metadata. Each entry is
        /// (interface, method, the slot the code uses); every disagreement is returned.
        /// </summary>
        /// <remarks>
        /// The constants stay constants because <c>switch</c> needs them. This is what stops one
        /// drifting from the interface it claims to describe without anybody noticing.
        /// </remarks>
        public static List<string> Verify(IEnumerable<(string Interface, string Method, int Slot)> claims)
        {
            List<string> wrong = new();
            foreach ((string iface, string method, int slot) in claims)
            {
                int actual;
                try
                {
                    actual = Slot(iface, method);
                }
                catch (KeyNotFoundException ex)
                {
                    wrong.Add(ex.Message);
                    continue;
                }

                if (actual != slot)
                {
                    wrong.Add($"{iface}::{method} is slot {actual}, code uses {slot}");
                }
            }

            return wrong;
        }

        public static int Count => Indexed.Value.ByFullName.Count;

        private sealed class Index
        {
            public readonly Dictionary<string, Interface> ByFullName = new(StringComparer.Ordinal);
            public readonly Dictionary<Guid, Interface> ByIid = new();

            public Index()
            {
                foreach ((string name, string iid, string[] methods) in Table)
                {
                    Interface layout = new(
                        name,
                        Guid.Parse(iid),
                        methods.Select((m, i) => new Method(m[..m.IndexOf('(')], m, InspectableSlots + i)).ToArray());

                    ByFullName[name] = layout;
                    ByIid[layout.Iid] = layout;
                }
            }
        }
    }
}

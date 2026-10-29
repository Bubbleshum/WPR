using System.Xml.Linq;

namespace WPR.Wp8Native
{
    /// <summary>
    /// The package's own WinRT components: the runtime classes WMAppManifest.xml registers under
    /// <c>ActivatableClasses/InProcessServer</c>, each served by a DLL in the package. Activating one
    /// is what the CLR did for the managed page (<c>new LuaMobileSDKBridge(callback)</c>): load that
    /// DLL - by its path in the package, so a same-named DLL elsewhere in it (Spark2's
    /// <c>Spark2Projects\_MobileSDK\LuaMobileSDK.dll</c> beside the root <c>LuaMobileSDK.dll</c>) is
    /// never taken for it - run its DllMain, and ask its <c>DllGetActivationFactory</c> for the class.
    /// </summary>
    public sealed partial class WinRtRuntime
    {
        private Dictionary<string, string>? _packageClasses;
        private readonly Dictionary<string, long> _packageFactories = new(StringComparer.Ordinal);

        /// <summary>
        /// Runtime class -> the package-relative DLL that serves it, from WMAppManifest.xml. Empty
        /// when the package has no readable manifest.
        /// </summary>
        public IReadOnlyDictionary<string, string> PackageClasses => _packageClasses ??= ReadPackageClasses(_emulator.PackageRoot);

        /// <summary>The <c>ActivatableClasses</c> of the manifest at <paramref name="packageRoot"/>.</summary>
        public static Dictionary<string, string> ReadPackageClasses(string packageRoot)
        {
            Dictionary<string, string> classes = new(StringComparer.Ordinal);
            try
            {
                string path = Path.Combine(packageRoot, "WMAppManifest.xml");
                if (!File.Exists(path))
                {
                    return classes;
                }

                foreach (XElement server in XDocument.Load(path).Descendants().Where(e => e.Name.LocalName == "InProcessServer"))
                {
                    string? dll = server.Elements().FirstOrDefault(e => e.Name.LocalName == "Path")?.Value.Trim();
                    if (string.IsNullOrEmpty(dll))
                    {
                        continue;
                    }

                    foreach (XElement activatable in server.Elements().Where(e => e.Name.LocalName == "ActivatableClass"))
                    {
                        string? id = activatable.Attribute("ActivatableClassId")?.Value.Trim();
                        if (!string.IsNullOrEmpty(id))
                        {
                            classes.TryAdd(id, dll);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // An unreadable manifest registers nothing; activation then says "not registered".
            }

            return classes;
        }

        /// <summary>
        /// Whether activating <paramref name="className"/> should load the package DLL that registers
        /// it rather than take a host factory: the package registers it, the host has no factory for
        /// it yet, and it is not a class the host implements itself (Microsoft.Xbox, which packages
        /// register for their own Microsoft.Xbox.dll).
        /// </summary>
        public bool ServedByPackage(string className) =>
            !_factories.ContainsKey(className) &&
            !className.StartsWith("Microsoft.Xbox", StringComparison.Ordinal) &&
            PackageClasses.ContainsKey(className);

        /// <summary>
        /// A package class's factory as the interface <paramref name="iid"/> (null: the factory as
        /// DllGetActivationFactory gave it), for the image's own activations (C++/CX <c>ref new</c>,
        /// RoGetActivationFactory). 0 when the class cannot be activated from its DLL, in which case
        /// the caller falls back to the host's answer. Only legal from inside a trap handler.
        /// </summary>
        public void ActivatePackageFactoryAs(string className, Guid? iid, Action<long> then)
        {
            ActivatePackageFactory(className, factory =>
            {
                if (factory == 0)
                {
                    then(0);
                    return;
                }

                // Always through QueryInterface, which hands the caller a reference of its own:
                // the cached factory's reference is the cache's, and a caller that releases what it
                // was given (ComPtr does) freed the cache's copy (Tiny Death Star's second
                // ref new cocos2d::DirectXRender got a pointer into reused memory).
                QueryInterface(factory, iid ?? IidActivationFactory, then);
            });
        }

        private static readonly Guid IidActivationFactory = new("00000035-0000-0000-c000-000000000046");

        /// <summary>
        /// The activation factory of a runtime class the package registers, loading its DLL first.
        /// Only legal from inside a trap handler (it continues in emulated code); <paramref name="then"/>
        /// gets the factory, or 0 when the class is not registered, its DLL does not load, or it
        /// has no factory for the class.
        /// </summary>
        public void ActivatePackageFactory(string className, Action<long> then)
        {
            if (_packageFactories.TryGetValue(className, out long known))
            {
                then(known);
                return;
            }

            if (!PackageClasses.TryGetValue(className, out string? dll))
            {
                Log($"{className}: not registered in WMAppManifest.xml");
                then(0);
                return;
            }

            // ".\" makes it a path: the loader then takes exactly the package-root file, never a
            // loaded module that merely shares its base name.
            string relative = dll.Contains('\\') || dll.Contains('/') ? dll : @".\" + dll;
            _emulator.LoadLibraryFromGuest(relative, allowSystem: false, (handle, error) =>
            {
                if (handle == 0)
                {
                    Log($"{className}: {dll} did not load (error {error})");
                    then(0);
                    return;
                }

                long? getFactory = _emulator.ProcedureAddress(handle, "DllGetActivationFactory", out _);
                if (getFactory is null)
                {
                    Log($"{className}: {dll} exports no DllGetActivationFactory");
                    then(0);
                    return;
                }

                long outFactory = _emulator.AllocateHeap(8);
                _emulator.WriteUInt32(outFactory, 0);
                long classId = _strings.Create(className);
                Call($"{dll}!DllGetActivationFactory", getFactory.Value, [classId, outFactory], [], hr =>
                {
                    long factory = hr == 0 ? _emulator.ReadUInt32(outFactory) : 0;
                    Log($"{dll}!DllGetActivationFactory({className}) = 0x{hr:X8}, factory 0x{factory:X8}");
                    if (factory != 0)
                    {
                        _packageFactories[className] = factory;
                    }

                    then(factory);
                });
            });
        }
    }
}

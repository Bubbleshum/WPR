namespace WPR.Wp8Native
{
    public sealed partial class WinRtRuntime
    {
        /// <summary>
        /// The managed object a XAML title's page passes to its component as an argument (see
        /// <see cref="XamlHostObject"/>): the C# class implementing one of the component's own
        /// interfaces, which the engine then calls back into.
        /// </summary>
        /// <remarks>
        /// SEAM. The real Spark2 objects (<c>SparkApp.CSharpFunctions</c>,
        /// <c>LuaMobileSDK.ICallback</c>) are written in <c>SparkHostObjects.cs</c>; once that is
        /// merged, the cases below become one-line calls to it. Until then every interface gets
        /// <see cref="DefaultAnsweringObject"/>.
        /// </remarks>
        private long HostObjectFor(string interfaceName)
        {
            if (_hostObjects.TryGetValue(interfaceName, out long existing))
            {
                return existing;
            }

            long created = interfaceName switch
            {
                // "SparkApp.CSharpFunctions" => CreateCSharpFunctions(...),
                // "LuaMobileSDK.ICallback" => CreateLuaCallback(...),
                _ => DefaultAnsweringObject(interfaceName),
            };

            Log($"host object {interfaceName} = 0x{created:X8}");
            return _hostObjects[interfaceName] = created;
        }

        private readonly Dictionary<string, long> _hostObjects = new(StringComparer.Ordinal);

        /// <summary>
        /// A placeholder implementation of a component interface: every method succeeds and
        /// answers its type's default through the trailing out pointer (0, false, 0.0, the empty
        /// HSTRING). Its calls are counted in <see cref="CallbacksRaised"/> so the log says what
        /// the engine asked of the C# side.
        /// </summary>
        private long DefaultAnsweringObject(string interfaceName)
        {
            WinmdReader.InterfaceLayout? layout = _metadata?.Interface(interfaceName);
            if (layout is null)
            {
                Log($"host object {interfaceName}: not in the component's metadata; a discovery object stands in");
                return CreateDiscoveryObject(interfaceName, slotCount: 16);
            }

            var known = new Dictionary<int, (string, Action)>();
            foreach (WinmdReader.MethodLayout method in layout.Methods)
            {
                // Out pointer: after "this" and every argument that takes a core register.
                int resultArg = 1 + method.ParameterTypes.Count(t => t is not ("Single" or "Double"));
                string name = $"{interfaceName.Split('.')[^1]}::{method.Name}";
                known[method.Slot] = (method.Name, () =>
                {
                    int count = CallbacksRaised[name] = CallbacksRaised.GetValueOrDefault(name) + 1;
                    if (count <= 3)
                    {
                        Log($"{name} called (placeholder answers {method.ReturnType} default)");
                    }

                    if (method.ReturnType != "Void" && Arg(resultArg) is long result && result != 0)
                    {
                        _emulator.WriteMemory(result, new byte[method.ReturnType switch
                        {
                            "Boolean" or "Byte" => 1,
                            "Double" or "Int64" or "UInt64" => 8,
                            _ => 4,
                        }]);
                    }

                    Return(HResultOk);
                });
            }

            return CreateDiscoveryObject(interfaceName, slotCount: layout.Methods.Count, known: known);
        }
    }
}

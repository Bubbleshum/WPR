namespace WPR.Wp8Native
{
    /// <summary>WS2_32: see <see cref="WinsockLibrary"/>.</summary>
    public sealed partial class HostStubs
    {
        /// <summary>Real sockets, and the log of every network target the image reached for.</summary>
        public WinsockLibrary Winsock { get; private set; } = null!;

        private void RegisterWinsock()
        {
            // _sync is created later in the constructor; the library asks for it only when called.
            Winsock = new WinsockLibrary(_emulator, _frame, () => _sync, _handlers);
            Winsock.RegisterInto(_handlers);
        }

        /// <summary>
        /// WS2_32 imports by ordinal ("WS2_32.dll!#23" is socket). Ordinals are only unique per DLL,
        /// so this runs before the by-name table; an ordinal this library does not implement falls
        /// through and is recorded as defaulted under its number.
        /// </summary>
        private bool TryWinsockOrdinal(string fullName)
        {
            if (!fullName.StartsWith("WS2_32", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string ordinal = fullName[(fullName.IndexOf('!') + 1)..];
            if (!WinsockLibrary.OrdinalNames.TryGetValue(ordinal, out string? name) ||
                !_handlers.TryGetValue(name, out Action? handler))
            {
                return false;
            }

            handler();
            return true;
        }

        /// <summary>
        /// <see cref="IsImplemented(string)"/> for a DLL-qualified import: needed for ordinals, which
        /// mean different functions in different DLLs.
        /// </summary>
        public bool IsImplemented(string dll, string function) =>
            function.StartsWith('#') && dll.StartsWith("WS2_32", StringComparison.OrdinalIgnoreCase)
                ? WinsockLibrary.OrdinalNames.TryGetValue(function, out string? name) && _handlers.ContainsKey(name)
                : IsImplemented(function);
    }
}

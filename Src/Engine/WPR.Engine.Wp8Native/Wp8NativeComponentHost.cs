extern alias wprwinrt;

using wprwinrt::WPR.WinRT;
using WinRtSize = wprwinrt::Windows.Foundation.Size;
using WinRtPoint = wprwinrt::Windows.Foundation.Point;
using WinRtToken = wprwinrt::System.Runtime.InteropServices.WindowsRuntime.EventRegistrationToken;

namespace WPR.Wp8Native
{
    /// <summary>
    /// Answers <see cref="WinRtComponentBridge"/> for a title running its own managed page: each
    /// call goes to the guest thread as a <see cref="BridgeCall"/> (Runtime/ComponentBridge.cs) and
    /// the caller waits for it, as a call into a WinRT component on the phone was synchronous.
    /// This class only translates: managed values to the runtime's argument kinds, and the 8 bytes
    /// the component answered back to the type the stub declared.
    /// </summary>
    public sealed class Wp8NativeComponentHost : IWinRtComponentHost
    {
        /// <summary>How long one call may take; a loading component can be slow, a hung one should not hang the page for ever.</summary>
        private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

        private readonly Wp8NativeSession _session;
        private bool _providerSet;

        public Wp8NativeComponentHost(Wp8NativeSession session) => _session = session;

        public long Activate(string className, object?[] arguments)
        {
            BridgeCall call = Run(new BridgeCall(BridgeCallKind.Activate, className, ".ctor", 0, Translate(arguments)));
            return (long)call.ResultBits;
        }

        public object? Invoke(long target, string className, string member, Type returnType, object?[] arguments)
        {
            BridgeCall call = Run(new BridgeCall(BridgeCallKind.Invoke, className, member, target, Translate(arguments)));
            CopyOutParameters(call.Arguments, arguments);
            return call.Error is null ? Result(call, returnType) : null;
        }

        /// <summary>
        /// Out parameters, which the stub passes as null and reads back from the same slot
        /// (<c>WinRtComponentBridge.OutValue</c>): what the runtime read from the component's
        /// out slots. An object comes back as a pointer for the bridge to wrap.
        /// </summary>
        private static void CopyOutParameters(object?[] answered, object?[] arguments)
        {
            for (int i = 0; i < arguments.Length && i < answered.Length; i++)
            {
                if (arguments[i] is null && answered[i] is { } value)
                {
                    arguments[i] = value is GuestRef guest ? new GuestPointer(guest.Pointer) : value;
                }
            }
        }

        /// <summary>
        /// The page handed a grid (or, <paramref name="drawingSurface"/>, a <c>DrawingSurface</c> on a
        /// page that is <paramref name="landscape"/> or not) its content provider: connect it, and the
        /// frames begin.
        /// </summary>
        public bool SetContentProvider(object? provider, bool drawingSurface = false, bool landscape = false)
        {
            if (provider is null && _providerSet)
            {
                // The page is letting go of the surface (MainPage.Closing does this before it
                // disposes the component): stop drawing, or the next frame calls a closed component.
                Run(new BridgeCall(BridgeCallKind.SetProvider, "", "SetContentProvider(null)", 0, [drawingSurface, landscape]));
                _providerSet = false;
                return true;
            }

            if (provider is null || !WinRtComponentBridge.TryGetPointer(provider, out long pointer))
            {
                return false;
            }

            _providerSet = true;

            Run(new BridgeCall(BridgeCallKind.SetProvider, "",
                drawingSurface ? "SetContentProvider" : "SetBackgroundContentProvider", pointer, [drawingSurface, landscape]));
            return true;
        }

        /// <summary>The page handed a grid its manipulation handler: give it the host that raises the grid's pointer events.</summary>
        public bool SetManipulationHandler(object? handler)
        {
            if (handler is null || !WinRtComponentBridge.TryGetPointer(handler, out long pointer))
            {
                return false;
            }

            Run(new BridgeCall(BridgeCallKind.Invoke, "", "SetManipulationHost", pointer, [XamlShell.ManipulationHost]));
            return true;
        }

        private BridgeCall Run(BridgeCall call)
        {
            // The page can be built before the guest thread has made its runtime.
            _session.RuntimeReady.Wait(CallTimeout);
            if (_session.Runtime is not { } runtime)
            {
                call.Error = "the guest is not running";
                return call;
            }

            // Guest objects handed to the page's own objects arrive as the bridge's wrappers.
            runtime.WrapGuestObject ??= (pointer, type) => WinRtComponentBridge.Wrap(pointer, type);
            runtime.RunBridgeCall(call, CallTimeout);
            return call;
        }

        /// <summary>Managed arguments as the runtime marshals them.</summary>
        private static object?[] Translate(object?[] arguments)
        {
            var translated = new object?[arguments.Length];
            for (int i = 0; i < arguments.Length; i++)
            {
                translated[i] = arguments[i] switch
                {
                    null => null,
                    WinRtSize size => new SizeF((float)size.Width, (float)size.Height),
                    WinRtPoint point => new SizeF((float)point.X, (float)point.Y),
                    WinRtToken token => (long)WinRtComponentBridge.TokenValue(token),
                    Enum value => Convert.ToInt32(value),
                    Delegate handler => handler,
                    string or bool or int or uint or long or float or double => arguments[i],
                    object other when WinRtComponentBridge.TryGetPointer(other, out long pointer) => new GuestRef(pointer),
                    // The page's own object implementing one of the component's interfaces.
                    object other => new HostManaged(other),
                };
            }

            return translated;
        }

        /// <summary>What the component answered, as the declared type; an object goes back as a pointer for the bridge to wrap.</summary>
        private static object? Result(BridgeCall call, Type type)
        {
            ulong bits = call.ResultBits;
            if (type == typeof(void))
            {
                return null;
            }

            if (type == typeof(string))
            {
                return call.ResultText ?? "";
            }

            if (type == typeof(bool))
            {
                return (bits & 0xFF) != 0;
            }

            if (type == typeof(float))
            {
                return BitConverter.UInt32BitsToSingle((uint)bits);
            }

            if (type == typeof(double))
            {
                return BitConverter.UInt64BitsToDouble(bits);
            }

            if (type == typeof(WinRtSize))
            {
                return new WinRtSize(BitConverter.UInt32BitsToSingle((uint)bits), BitConverter.UInt32BitsToSingle((uint)(bits >> 32)));
            }

            if (type == typeof(WinRtPoint))
            {
                return new WinRtPoint(BitConverter.UInt32BitsToSingle((uint)bits), BitConverter.UInt32BitsToSingle((uint)(bits >> 32)));
            }

            if (type == typeof(WinRtToken) || type == typeof(long) || type == typeof(ulong))
            {
                return bits;
            }

            if (type.IsPrimitive || type.IsEnum)
            {
                return (long)(int)(uint)bits;
            }

            return type.IsValueType ? null : new GuestPointer((long)(uint)bits);
        }
    }
}

using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using WPR.WinRT;

namespace System.Runtime.InteropServices.WindowsRuntime
{
    /// <summary>
    /// Shim for <c>System.Runtime.InteropServices.WindowsRuntime.EventRegistrationToken</c>, which
    /// WP8 kept in mscorlib (ApplicationPatcher rescopes it here; .NET 5 removed it). A WinRT
    /// event's add_ method returns one, its remove_ method takes it back.
    /// </summary>
    public struct EventRegistrationToken : IEquatable<EventRegistrationToken>
    {
        private readonly ulong _value;

        internal EventRegistrationToken(ulong value) => _value = value;

        internal ulong Value => _value;

        public static bool operator ==(EventRegistrationToken left, EventRegistrationToken right) => left._value == right._value;

        public static bool operator !=(EventRegistrationToken left, EventRegistrationToken right) => left._value != right._value;

        public bool Equals(EventRegistrationToken other) => _value == other._value;

        public override bool Equals(object? obj) => obj is EventRegistrationToken other && Equals(other);

        public override int GetHashCode() => _value.GetHashCode();
    }

    /// <summary>
    /// Shim for <c>System.Runtime.InteropServices.WindowsRuntime.EventRegistrationTokenTable`1</c>:
    /// the handler list behind a WinRT event, keyed by token.
    /// </summary>
    public sealed class EventRegistrationTokenTable<T> where T : class
    {
        private static long _next;
        private readonly Dictionary<ulong, T> _handlers = new();
        private readonly object _lock = new();

        public T? InvocationList
        {
            get
            {
                lock (_lock)
                {
                    Delegate? all = null;
                    foreach (T handler in _handlers.Values)
                    {
                        all = Delegate.Combine(all, handler as Delegate);
                    }
                    return all as T;
                }
            }
            set
            {
                lock (_lock)
                {
                    _handlers.Clear();
                    if (value != null) _handlers[(ulong)Interlocked.Increment(ref _next)] = value;
                }
            }
        }

        public EventRegistrationToken AddEventHandler(T? handler)
        {
            if (handler is null) return default;
            var token = new EventRegistrationToken((ulong)Interlocked.Increment(ref _next));
            lock (_lock) _handlers[token.Value] = handler;
            return token;
        }

        public void RemoveEventHandler(EventRegistrationToken token)
        {
            lock (_lock) _handlers.Remove(token.Value);
        }

        public void RemoveEventHandler(T? handler)
        {
            if (handler is null) return;
            lock (_lock)
            {
                foreach (var pair in _handlers)
                {
                    if (Equals(pair.Value, handler))
                    {
                        _handlers.Remove(pair.Key);
                        return;
                    }
                }
            }
        }

        public static EventRegistrationTokenTable<T> GetOrCreateEventRegistrationTokenTable(ref EventRegistrationTokenTable<T>? refEventTable)
        {
            if (refEventTable is null) Interlocked.CompareExchange(ref refEventTable, new EventRegistrationTokenTable<T>(), null);
            return refEventTable!;
        }
    }

    /// <summary>
    /// Shim for <c>System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeMarshal</c> (mscorlib
    /// on WP8, rescoped here). C# compiles <c>XboxLIVEService.SignedOut += h</c> on a WinRT event
    /// into AddEventHandler(add_SignedOut, remove_SignedOut, h), and <c>-=</c> into
    /// RemoveEventHandler(remove_SignedOut, h); this class remembers which token each handler got.
    /// </summary>
    public static class WindowsRuntimeMarshal
    {
        // Keyed by the remove method and its target, which together name one event on one object.
        private static readonly Dictionary<(MethodInfo Method, object? Target), List<(Delegate Handler, EventRegistrationToken Token)>> _tokens = new();
        private static readonly object _lock = new();

        public static void AddEventHandler<T>(Func<T, EventRegistrationToken> addMethod, Action<EventRegistrationToken> removeMethod, T handler)
        {
            ArgumentNullException.ThrowIfNull(addMethod);
            ArgumentNullException.ThrowIfNull(removeMethod);
            if (handler is not Delegate d) return;

            EventRegistrationToken token = addMethod(handler);
            lock (_lock)
            {
                var key = (removeMethod.Method, removeMethod.Target);
                if (!_tokens.TryGetValue(key, out var list)) _tokens[key] = list = new();
                list.Add((d, token));
            }
        }

        public static void RemoveEventHandler<T>(Action<EventRegistrationToken> removeMethod, T handler)
        {
            ArgumentNullException.ThrowIfNull(removeMethod);
            if (handler is not Delegate d) return;

            EventRegistrationToken? token = null;
            lock (_lock)
            {
                if (_tokens.TryGetValue((removeMethod.Method, removeMethod.Target), out var list))
                {
                    int index = list.FindLastIndex(entry => entry.Handler.Equals(d));
                    if (index >= 0)
                    {
                        token = list[index].Token;
                        list.RemoveAt(index);
                    }
                }
            }
            if (token is { } t) removeMethod(t);
        }

        public static void RemoveAllEventHandlers(Action<EventRegistrationToken> removeMethod)
        {
            ArgumentNullException.ThrowIfNull(removeMethod);
            List<(Delegate Handler, EventRegistrationToken Token)>? list;
            lock (_lock)
            {
                if (!_tokens.Remove((removeMethod.Method, removeMethod.Target), out list)) return;
            }
            foreach (var entry in list) removeMethod(entry.Token);
        }

        /// <summary>
        /// An HSTRING for <paramref name="s"/>. WPR has no WinRT string runtime, so it is the string
        /// as a native UTF-16 buffer, which is all a managed caller can do with it (pass it on to a
        /// native API WPR does not host). Freed by <see cref="FreeHString"/>.
        /// </summary>
        public static IntPtr StringToHString(string s) => s == null ? IntPtr.Zero : Marshal.StringToHGlobalUni(s);

        public static void FreeHString(IntPtr ptr)
        {
            if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
        }

        public static string PtrToStringHString(IntPtr ptr) => ptr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(ptr) ?? string.Empty;
    }

    /// <summary>
    /// Shim for <c>System.Runtime.InteropServices.WindowsRuntime.AsyncInfo</c>: wrap a task-returning
    /// function as a WinRT async object (What In The World? uses it).
    /// </summary>
    public static class AsyncInfo
    {
        public static IAsyncAction Run(Func<CancellationToken, Task> taskProvider)
        {
            ArgumentNullException.ThrowIfNull(taskProvider);
            var cancel = new CancellationTokenSource();
            return new TaskAsyncAction(taskProvider(cancel.Token), cancel);
        }

        public static IAsyncOperation<TResult> Run<TResult>(Func<CancellationToken, Task<TResult>> taskProvider)
        {
            ArgumentNullException.ThrowIfNull(taskProvider);
            var cancel = new CancellationTokenSource();
            return new TaskAsyncOperation<TResult>(taskProvider(cancel.Token), cancel);
        }
    }
}

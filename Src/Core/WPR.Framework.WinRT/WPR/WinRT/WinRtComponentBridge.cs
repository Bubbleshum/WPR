using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;

namespace WPR.WinRT
{
    /// <summary>
    /// Where a game's managed code reaches the native WinRT component it ships. WinmdStubber, in
    /// bridged mode, writes the component's metadata as a managed assembly whose every member calls
    /// this class: a runtime class's constructor <see cref="Activate"/>, every method, property and
    /// event accessor <see cref="Invoke"/>. The WP8 native runtime registers the
    /// <see cref="Host"/> that runs the calls on the component; with none registered they answer
    /// defaults, as the old do-nothing stubs did.
    /// </summary>
    /// <remarks>
    /// Objects cross as guest pointers. A managed object standing for a guest one is a stub
    /// instance (a runtime class, made without running its constructor) or a
    /// <see cref="DispatchProxy"/> (an interface); either way <see cref="TryGetPointer"/> finds its
    /// pointer, and the same pointer always comes back as the same managed object.
    /// </remarks>
    public static class WinRtComponentBridge
    {
        /// <summary>The runtime serving the calls, for this launch. Cleared at teardown.</summary>
        public static IWinRtComponentHost? Host { get; set; }

        private static readonly ConditionalWeakTable<object, StrongBox<long>> Pointers = new();
        private static readonly Dictionary<long, WeakReference<object>> Objects = new();

        /// <summary>A runtime class's constructor: activates the guest object behind <paramref name="self"/>.</summary>
        public static void Activate(object self, string className, object?[] arguments)
        {
            if (Host is not { } host)
            {
                return;
            }

            long pointer = host.Activate(className, arguments);
            Remember(self, pointer);
        }

        /// <summary>
        /// A member call on <paramref name="self"/> (null for a static member of
        /// <paramref name="className"/>), answered as <paramref name="returnType"/>.
        /// </summary>
        public static object? Invoke(object? self, string className, string member, Type returnType, object?[] arguments)
        {
            if (Host is not { } host)
            {
                return Default(returnType);
            }

            long target = 0;
            if (self is not null && !TryGetPointer(self, out target))
            {
                return Default(returnType);
            }

            object? result = host.Invoke(target, className, member, returnType, arguments);
            if (result is GuestPointer guest)
            {
                return Wrap(guest.Value, returnType);
            }

            return Coerce(result, returnType);
        }

        /// <summary>
        /// An out parameter's value after <see cref="Invoke"/>: the host writes it into the same slot
        /// of the argument array the stub passed (null going in). Called by bridged stubs, one per
        /// out parameter; nothing written is the type's default, as an untouched out slot was.
        /// </summary>
        public static T OutValue<T>(object?[] arguments, int index)
        {
            object? value = index < arguments.Length ? arguments[index] : null;
            if (value is GuestPointer guest)
            {
                value = Wrap(guest.Value, typeof(T));
            }

            return Coerce(value, typeof(T)) is T typed ? typed : default!;
        }

        /// <summary>A host answer as the exact type the stub unboxes: numbers become the declared primitive or enum.</summary>
        private static object? Coerce(object? value, Type type)
        {
            if (value is null || type == typeof(void))
            {
                return Default(type);
            }

            if (type.IsInstanceOfType(value))
            {
                return value;
            }

            try
            {
                if (type == typeof(EventRegistrationToken))
                {
                    return new EventRegistrationToken(Convert.ToUInt64(value));
                }

                if (type.IsEnum)
                {
                    return Enum.ToObject(type, Convert.ToInt64(value));
                }

                if (type.IsPrimitive)
                {
                    return Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            catch (Exception)
            {
                // Falls through to the default: a wrong answer is no worse than none.
            }

            return Default(type);
        }

        /// <summary>The guest pointer a managed object stands for.</summary>
        public static bool TryGetPointer(object value, out long pointer)
        {
            if (Pointers.TryGetValue(value, out StrongBox<long>? box))
            {
                pointer = box.Value;
                return true;
            }

            pointer = 0;
            return false;
        }

        /// <summary>
        /// The managed object for a guest pointer, as <paramref name="type"/>: the one made before for
        /// that pointer, else a stub instance (a class) or a proxy (an interface).
        /// </summary>
        public static object? Wrap(long pointer, Type type)
        {
            if (pointer == 0)
            {
                return null;
            }

            lock (Objects)
            {
                if (Objects.TryGetValue(pointer, out WeakReference<object>? known) &&
                    known.TryGetTarget(out object? existing) && type.IsInstanceOfType(existing))
                {
                    return existing;
                }
            }

            object made;
            if (type.IsInterface)
            {
                made = GuestInterfaceProxy.Create(type);
            }
            else if (type.IsClass && !type.IsAbstract && type != typeof(object))
            {
                made = RuntimeHelpers.GetUninitializedObject(type);
            }
            else
            {
                made = new GuestObject();
            }

            Remember(made, pointer);
            return made;
        }

        /// <summary>The token an event's add accessor hands back, from the guest's 64-bit value.</summary>
        public static EventRegistrationToken Token(ulong value) => new(value);

        /// <summary>The guest's 64-bit value of a token a remove accessor is given.</summary>
        public static ulong TokenValue(EventRegistrationToken token) => token.Value;

        private static void Remember(object value, long pointer)
        {
            if (pointer == 0)
            {
                return;
            }

            Pointers.AddOrUpdate(value, new StrongBox<long>(pointer));
            lock (Objects)
            {
                Objects[pointer] = new WeakReference<object>(value);
            }
        }

        private static object? Default(Type type) =>
            type == typeof(void) || !type.IsValueType ? null : Activator.CreateInstance(type);

        /// <summary>Forgets every guest object: the launch is over.</summary>
        public static void Reset()
        {
            Host = null;
            lock (Objects)
            {
                Objects.Clear();
            }
        }
    }

    /// <summary>A guest object handed back where the metadata names no managed type to give it.</summary>
    public sealed class GuestObject
    {
    }

    /// <summary>A guest pointer, as a host answers an object-valued member; the bridge wraps it.</summary>
    public readonly record struct GuestPointer(long Value);

    /// <summary>The runtime behind <see cref="WinRtComponentBridge"/>: the WP8 native runtime.</summary>
    public interface IWinRtComponentHost
    {
        /// <summary>Activates <paramref name="className"/> (its factory's ActivateInstance, or CreateInstance with arguments); the guest pointer.</summary>
        long Activate(string className, object?[] arguments);

        /// <summary>
        /// Calls <paramref name="member"/> on <paramref name="target"/> (0: a static of
        /// <paramref name="className"/>). Answers a value, null, or a <see cref="GuestPointer"/>.
        /// </summary>
        object? Invoke(long target, string className, string member, Type returnType, object?[] arguments);
    }

    /// <summary>
    /// An interface the guest implements, as a managed object: every call goes to the bridge by the
    /// method's name. What <c>CreateContentProvider</c> hands back is one of these.
    /// </summary>
    public class GuestInterfaceProxy : DispatchProxy
    {
        private Type _interface = typeof(object);

        internal static object Create(Type interfaceType)
        {
            MethodInfo create = typeof(DispatchProxy)
                .GetMethod(nameof(DispatchProxy.Create), 2, Type.EmptyTypes)!
                .MakeGenericMethod(interfaceType, typeof(GuestInterfaceProxy));
            var proxy = (GuestInterfaceProxy)create.Invoke(null, null)!;
            proxy._interface = interfaceType;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
            {
                return null;
            }

            return WinRtComponentBridge.Invoke(this, _interface.FullName ?? _interface.Name, targetMethod.Name,
                targetMethod.ReturnType, args ?? Array.Empty<object?>());
        }
    }
}

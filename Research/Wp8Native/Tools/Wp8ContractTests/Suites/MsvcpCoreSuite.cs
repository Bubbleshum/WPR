using System.Collections;
using System.Reflection;
using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// MSVCP110 outside iostreams/locale (<see cref="MsvcpCore"/>): the _X* throwers and the objects they
/// throw, the error maps and categories, _Lockit, _Container_base12, _Yarn, exception_ptr, and the
/// timed/at-thread-exit additions to the thread layer.
/// </summary>
public sealed class MsvcpCoreSuite : IContractSuite
{
    public string Name => "msvcp core";

    private const int LR = 10;

    public void Run(Harness h)
    {
        ArmEmulator e = h.E;
        HostStubs s = h.S;
        void Check(string what, bool ok, string detail = "") => h.Check(what, ok, detail);
        long Call(string fn, params long[] a) => h.Call(fn, a);

        // Calls a host vtable method the way a guest virtual call reaches it.
        IDictionary traps = (IDictionary)typeof(ArmEmulator).GetField("_traps", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(e)!;
        long Virtual(long self, int slot, params long[] args)
        {
            long function = e.ReadUInt32(e.ReadUInt32(self) + (slot * 4));
            object? entry = traps[function & ~1L];
            if (entry is null || (function & 1) == 0) return -1;
            var handler = (Action)entry.GetType().GetProperty("Handler")!.GetValue(entry)!;
            int[] registers = [Harness.R0, Harness.R1, Harness.R2, Harness.R3];
            e.WriteRegister(registers[0], self);
            for (int i = 0; i < args.Length; i++) e.WriteRegister(registers[i + 1], args[i]);
            handler();
            return e.ReadRegister(Harness.R0) & 0xFFFFFFFFL;
        }

        // A throw from a host helper with no guest frames above it: nothing catches it, which is
        // what lets the thrown model be inspected.
        ThrownException? Throws(string fn, params long[] a)
        {
            e.WriteRegister(LR, 0);
            Call(fn, a);
            return s.Thrown;
        }

        string What(long self) => h.ReadNarrow(Virtual(self, 1), 512);

        // ---- the _X* throwers ----
        (string Function, string[] Types)[] messageThrowers =
        [
            ("?_Xlength_error@std@@YAXPBD@Z", [".?AVlength_error@std@@", ".?AVlogic_error@std@@", ".?AVexception@std@@"]),
            ("?_Xout_of_range@std@@YAXPBD@Z", [".?AVout_of_range@std@@", ".?AVlogic_error@std@@", ".?AVexception@std@@"]),
            ("?_Xinvalid_argument@std@@YAXPBD@Z", [".?AVinvalid_argument@std@@", ".?AVlogic_error@std@@", ".?AVexception@std@@"]),
            ("?_Xoverflow_error@std@@YAXPBD@Z", [".?AVoverflow_error@std@@", ".?AVruntime_error@std@@", ".?AVexception@std@@"]),
            ("?_Xruntime_error@std@@YAXPBD@Z", [".?AVruntime_error@std@@", ".?AVexception@std@@"]),
        ];
        foreach ((string function, string[] types) in messageThrowers)
        {
            string message = "message for " + types[0];
            long text = h.Narrow(message);
            ThrownException? thrown = Throws(function, text);
            Check($"{function} throws {types[0]} catchable as its bases",
                thrown is not null && thrown.CatchableTypes.SequenceEqual(types), thrown is null ? "nothing thrown" : string.Join(",", thrown.CatchableTypes));
            if (thrown is null) continue;
            long self = thrown.Object;
            Check($"{types[0]}: what() through the vtable returns the message", What(self) == message, What(self));
            Check($"{types[0]}: the message is the exception's own copy (_Mydofree)",
                e.ReadUInt32(self + 4) != text && e.ReadMemory(self + 8, 1)[0] == 1);
        }

        ThrownException? lengthError = Throws("?_Xlength_error@std@@YAXPBD@Z", h.Narrow("vector<T> too long"));
        long le = lengthError!.Object;

        // RTTI on the thrown object: dynamic_cast to a base, by name, and a failed cast to a stranger.
        long TypeDescriptor(string name)
        {
            long td = h.Buffer(8 + name.Length + 1);
            e.WriteMemory(td + 8, System.Text.Encoding.ASCII.GetBytes(name + "\0"));
            return td;
        }

        Check("dynamic_cast<std::logic_error*> of a thrown length_error succeeds",
            Call("__RTDynamicCast", le, 0, 0, TypeDescriptor(".?AVlogic_error@std@@"), 0) == le);
        Check("dynamic_cast<std::runtime_error*> of a thrown length_error fails",
            Call("__RTDynamicCast", le, 0, 0, TypeDescriptor(".?AVruntime_error@std@@"), 0) == 0);
        long locator = e.ReadUInt32(e.ReadUInt32(le) - 4);
        Check("the vtable's locator names the most derived type (typeid)",
            h.ReadNarrow(e.ReadUInt32(locator + 12) + 8) == ".?AVlength_error@std@@");

        long deleted = Virtual(le, 0, 1);
        Check("the scalar deleting destructor returns this and drops the message", deleted == le);

        ThrownException? badAlloc = Throws("?_Xbad_alloc@std@@YAXXZ");
        Check("_Xbad_alloc throws bad_alloc", badAlloc?.CatchableTypes.SequenceEqual([".?AVbad_alloc@std@@", ".?AVexception@std@@"]) == true);
        Check("bad_alloc::what() is \"bad allocation\", not owned", badAlloc is not null && What(badAlloc.Object) == "bad allocation" && e.ReadMemory(badAlloc.Object + 8, 1)[0] == 0);

        ThrownException? badCall = Throws("?_Xbad_function_call@std@@YAXXZ");
        Check("_Xbad_function_call throws bad_function_call", badCall?.CatchableTypes.SequenceEqual([".?AVbad_function_call@std@@", ".?AVexception@std@@"]) == true);
        Check("bad_function_call::what() is \"bad function call\"", badCall is not null && What(badCall.Object) == "bad function call");

        ThrownException? regex = Throws("?_Xregex_error@std@@YAXW4error_type@regex_constants@1@@Z", 4);
        Check("_Xregex_error throws regex_error : runtime_error",
            regex?.CatchableTypes.SequenceEqual([".?AVregex_error@std@@", ".?AVruntime_error@std@@", ".?AVexception@std@@"]) == true);
        Check("regex_error carries its code and _Stringify's message",
            regex is not null && e.ReadUInt32(regex.Object + 12) == 4 && What(regex.Object).StartsWith("regex_error(error_brack)"), regex is null ? "" : What(regex.Object));

        ThrownException? cppError = Throws("?_Throw_Cpp_error@std@@YAXH@Z", 1);
        Check("_Throw_Cpp_error throws system_error : runtime_error",
            cppError?.CatchableTypes.SequenceEqual([".?AVsystem_error@std@@", ".?AVruntime_error@std@@", ".?AVexception@std@@"]) == true);
        long category = cppError is null ? 0 : e.ReadUInt32(cppError.Object + 16);
        Check("_Throw_Cpp_error(_INVALID_ARGUMENT): what() and error_code(EINVAL, generic_category())",
            cppError is not null && What(cppError.Object) == "invalid argument" && e.ReadUInt32(cppError.Object + 12) == 22 && category == s.Msvcp.GenericCategory);
        Check("generic_category().name() is \"generic\"", h.ReadNarrow(Virtual(category, 1)) == "generic");
        long str = h.Buffer(24);
        Virtual(category, 2, str, 2);
        Check("generic_category().message(ENOENT) is a std::string \"no such file or directory\"",
            h.ReadNarrow(e.ReadUInt32(str)) == "no such file or directory" && e.ReadUInt32(str + 16) == 25, $"size={e.ReadUInt32(str + 16)}");
        Virtual(category, 2, str, 5);
        Check("a message under 16 characters is stored in _Buf, _Myres 15",
            h.ReadNarrow(str) == "io error" && e.ReadUInt32(str + 16) == 8 && e.ReadUInt32(str + 20) == 15);
        long condition = h.Buffer(8);
        Virtual(category, 3, condition, 22);
        Check("default_error_condition(v) is (v, generic)", e.ReadUInt32(condition) == 22 && e.ReadUInt32(condition + 4) == category);
        long code = h.Buffer(8);
        e.WriteUInt32(code, 22); e.WriteUInt32(code + 4, (uint)category);
        Check("equivalent(error_code, int): same category and value", Virtual(category, 4, code, 22) == 1 && Virtual(category, 4, code, 23) == 0);
        Check("equivalent(int, error_condition): through either slot", Virtual(category, 5, 22, condition) == 1 && Virtual(category, 4, 23, condition) == 0);
        long system = s.Msvcp.SystemCategory;
        Check("system_category().name() is \"system\"", h.ReadNarrow(Virtual(system, 1)) == "system");
        Virtual(system, 3, condition, 2);
        Check("system_category maps an errno-valued code to the generic condition", e.ReadUInt32(condition + 4) == category);

        ThrownException? cError = Throws("?_Throw_C_error@std@@YAXH@Z", 3);
        Check("_Throw_C_error(_Thrd_busy) throws device_or_resource_busy", cError is not null && e.ReadUInt32(cError.Object + 12) == 16 && What(cError.Object) == "device or resource busy");
        ThrownException? before = s.Thrown;
        Call("?_Throw_C_error@std@@YAXH@Z", 0);
        Check("_Throw_C_error of a non-error code throws nothing", ReferenceEquals(before, s.Thrown));

        long futureCode = h.Buffer(8);
        e.WriteUInt32(futureCode, 3); e.WriteUInt32(futureCode + 4, 0x12345678);
        ThrownException? future = Throws("?_Throw_future_error@std@@YAXABVerror_code@1@@Z", futureCode);
        Check("_Throw_future_error throws future_error : logic_error",
            future?.CatchableTypes.SequenceEqual([".?AVfuture_error@std@@", ".?AVlogic_error@std@@", ".?AVexception@std@@"]) == true);
        Check("future_error keeps the code and category and what() describes it",
            future is not null && e.ReadUInt32(future.Object + 12) == 3 && e.ReadUInt32(future.Object + 16) == 0x12345678 && What(future.Object) == "promise already satisfied");
        ThrownException? lockError = Throws("?_Throw_lock_error@threads@stdext@@YAXXZ");
        Check("stdext::threads::_Throw_lock_error throws lock_error : runtime_error",
            lockError?.TypeName == ".?AVlock_error@threads@stdext@@" && What(lockError.Object) == "lock error");

        // ---- error maps ----
        Check("_Syserror_map(ENOENT)", h.ReadNarrow(Call("?_Syserror_map@std@@YAPBDH@Z", 2)) == "no such file or directory");
        Check("_Syserror_map(EWOULDBLOCK=140)", h.ReadNarrow(Call("?_Syserror_map@std@@YAPBDH@Z", 140)) == "operation would block");
        Check("_Syserror_map of an unknown code is NULL", Call("?_Syserror_map@std@@YAPBDH@Z", 9999) == 0);
        Check("_Syserror_map returns the same static string each time", Call("?_Syserror_map@std@@YAPBDH@Z", 2) == Call("?_Syserror_map@std@@YAPBDH@Z", 2));
        Check("_Winerror_map(ERROR_ACCESS_DENIED)", h.ReadNarrow(Call("?_Winerror_map@std@@YAPBDH@Z", 5)) == "permission denied");
        Check("_Winerror_map(WSAETIMEDOUT)", h.ReadNarrow(Call("?_Winerror_map@std@@YAPBDH@Z", 10060)) == "timed out");
        Check("_Winerror_map of an unknown code is NULL", Call("?_Winerror_map@std@@YAPBDH@Z", 0x7FFF) == 0);
        Check("_Future_error_map(broken_promise)", h.ReadNarrow(Call("?_Future_error_map@std@@YAPBDH@Z", 1)) == "broken promise");
        Check("_Future_error_map of an unknown code is NULL", Call("?_Future_error_map@std@@YAPBDH@Z", 0) == 0);
        Check("_Get_future_error_what(no_state)", h.ReadNarrow(Call("?_Get_future_error_what@std@@YAPBDH@Z", 4)) == "no state");

        // ---- _Lockit ----
        long lockit = h.Buffer(4), lockit2 = h.Buffer(4);
        Check("_Lockit(3) returns this and records its kind", Call("??0_Lockit@std@@QAA@H@Z", lockit, 3) == lockit && e.ReadUInt32(lockit) == 3);
        Check("_Lockit takes the lock", s.Msvcp.LockitState(3) == (e.CurrentThreadId, 1));
        Call("??0_Lockit@std@@QAA@H@Z", lockit2, 3);
        Check("_Lockit is recursive on its own thread", s.Msvcp.LockitState(3).Count == 2);
        Call("??1_Lockit@std@@QAA@XZ", lockit2);
        Call("??1_Lockit@std@@QAA@XZ", lockit);
        Check("~_Lockit releases it", s.Msvcp.LockitState(3).Count == 0);

        // ---- _Container_base12 ----
        long container = h.Buffer(4), proxy = h.Buffer(8);
        long[] iterators = [h.Buffer(8), h.Buffer(8), h.Buffer(8)];
        e.WriteUInt32(container, 0xDEAD);
        Check("_Container_base12() zeroes _Myproxy", Call("??0_Container_base12@std@@QAA@XZ", container) == container && e.ReadUInt32(container) == 0);
        e.WriteUInt32(container, (uint)proxy);
        e.WriteUInt32(proxy, (uint)container);
        e.WriteUInt32(proxy + 4, (uint)iterators[0]);
        for (int i = 0; i < 3; i++)
        {
            e.WriteUInt32(iterators[i], (uint)proxy);
            e.WriteUInt32(iterators[i] + 4, i < 2 ? (uint)iterators[i + 1] : 0);
        }

        Call("?_Orphan_all@_Container_base12@std@@QAAXXZ", container);
        Check("_Orphan_all detaches every iterator and empties the list",
            iterators.All(i => e.ReadUInt32(i) == 0) && e.ReadUInt32(proxy + 4) == 0 && e.ReadUInt32(container) == proxy);

        long other = h.Buffer(4), otherProxy = h.Buffer(8);
        e.WriteUInt32(other, (uint)otherProxy);
        e.WriteUInt32(otherProxy, (uint)other);
        Call("?_Swap_all@_Container_base12@std@@QAAXAAU12@@Z", container, other);
        Check("_Swap_all exchanges proxies and re-points each at its new owner",
            e.ReadUInt32(container) == otherProxy && e.ReadUInt32(other) == proxy &&
            e.ReadUInt32(otherProxy) == container && e.ReadUInt32(proxy) == other);
        e.WriteUInt32(otherProxy + 4, (uint)iterators[0]);
        e.WriteUInt32(iterators[0], (uint)otherProxy);
        e.WriteUInt32(iterators[0] + 4, 0);
        Call("??1_Container_base12@std@@QAA@XZ", container);
        Check("~_Container_base12 orphans its iterators", e.ReadUInt32(iterators[0]) == 0 && e.ReadUInt32(otherProxy + 4) == 0);

        // ---- _Yarn<char> ----
        long yarn = h.Buffer(8);
        Check("an empty _Yarn's c_str() is \"\" at _Nul", Call("?c_str@?$_Yarn@D@std@@QBAPBDXZ", yarn) == yarn + 4);
        long source = h.Narrow("C");
        Check("_Yarn = \"C\" returns *this", Call("??4?$_Yarn@D@std@@QAAAAV01@PBD@Z", yarn, source) == yarn);
        long copied = Call("?c_str@?$_Yarn@D@std@@QBAPBDXZ", yarn);
        Check("and holds its own copy", copied != source && h.ReadNarrow(copied) == "C");
        Call("??4?$_Yarn@D@std@@QAAAAV01@PBD@Z", yarn, 0);
        Check("_Yarn = NULL empties it", e.ReadUInt32(yarn) == 0 && h.ReadNarrow(Call("?c_str@?$_Yarn@D@std@@QBAPBDXZ", yarn)) == "");

        // ---- exception_ptr ----
        long ptr = h.Buffer(8), ptr2 = h.Buffer(8);
        e.WriteMemory(ptr, [1, 2, 3, 4, 5, 6, 7, 8]);
        Call("?__ExceptionPtrCreate@@YAXPAX@Z", ptr);
        Check("__ExceptionPtrCreate makes a null pointer", e.ReadUInt32(ptr) == 0 && Call("?__ExceptionPtrToBool@@YA_NPBX@Z", ptr) == 0);
        Call("?__ExceptionPtrCurrentException@@YAXPAX@Z", ptr);
        Check("current_exception() outside a catch is null", e.ReadUInt32(ptr) == 0);

        // make_exception_ptr(obj) for a simple 8-byte class: a ThrowInfo built the way MSVC emits one.
        long image = e.ImageBase;
        long td = TypeDescriptor(".?AVsimple_error@@");
        long catchableType = h.Buffer(28);
        e.WriteUInt32(catchableType + 4, (uint)(td - image));
        e.WriteUInt32(catchableType + 12, 0xFFFFFFFF);
        e.WriteUInt32(catchableType + 20, 8);
        long catchableArray = h.Buffer(8);
        e.WriteUInt32(catchableArray, 1);
        e.WriteUInt32(catchableArray + 4, (uint)(catchableType - image));
        long throwInfo = h.Buffer(16);
        e.WriteUInt32(throwInfo + 12, (uint)(catchableArray - image));
        long original = h.Buffer(8);
        e.WriteUInt32(original, 0x11111111); e.WriteUInt32(original + 4, 0x22222222);
        int live = s.Msvcp.LiveExceptionPointers;
        Call("?__ExceptionPtrCopyException@@YAXPAXPBX1@Z", ptr, original, throwInfo);
        Check("__ExceptionPtrCopyException captures a non-null pointer", Call("?__ExceptionPtrToBool@@YA_NPBX@Z", ptr) == 1 && s.Msvcp.LiveExceptionPointers == live + 1);
        Call("?__ExceptionPtrCopy@@YAXPAXPBX@Z", ptr2, ptr);
        Check("__ExceptionPtrCopy shares the capture (compare equal)", Call("?__ExceptionPtrCompare@@YA_NPBX0@Z", ptr, ptr2) == 1);
        ThrownException? rethrown = Throws("?__ExceptionPtrRethrow@@YAXPBX@Z", ptr);
        Check("rethrow_exception throws a copy of the object, catchable as its ThrowInfo says",
            rethrown is not null && rethrown.Object != original && e.ReadUInt32(rethrown.Object) == 0x11111111 &&
            e.ReadUInt32(rethrown.Object + 4) == 0x22222222 && rethrown.CatchableTypes.SequenceEqual([".?AVsimple_error@@"]) && rethrown.ThrowInfo == throwInfo);
        ThrownException? viaFuture = Throws("?_Rethrow_future_exception@std@@YAXVexception_ptr@1@@Z", e.ReadUInt32(ptr), e.ReadUInt32(ptr + 4));
        Check("_Rethrow_future_exception rethrows a pointer passed by value", viaFuture is not null && viaFuture.Object == rethrown?.Object);
        ThrownException? viaReference = Throws("?_Rethrow_future_exception@std@@YAXVexception_ptr@1@@Z", ptr2);
        Check("... and one passed by reference to a temporary", viaReference is not null && viaReference.Object == rethrown?.Object);
        Call("?__ExceptionPtrDestroy@@YAXPAX@Z", ptr2);
        Check("destroying one copy keeps the capture alive", s.Msvcp.LiveExceptionPointers == live + 1);
        Call("?__ExceptionPtrCreate@@YAXPAX@Z", ptr2);
        Call("?__ExceptionPtrSwap@@YAXPAX0@Z", ptr, ptr2);
        Check("__ExceptionPtrSwap exchanges them", e.ReadUInt32(ptr) == 0 && Call("?__ExceptionPtrToBool@@YA_NPBX@Z", ptr2) == 1);
        Call("?__ExceptionPtrAssign@@YAXPAXPBX@Z", ptr2, ptr);
        Check("assigning null over the last copy releases the capture", s.Msvcp.LiveExceptionPointers == live && e.ReadUInt32(ptr2) == 0);
        ThrownException? nullRethrow = Throws("?__ExceptionPtrRethrow@@YAXPBX@Z", ptr);
        Check("rethrowing a null exception_ptr throws bad_exception", nullRethrow?.TypeName == ".?AVbad_exception@std@@");

        // ---- odds and ends ----
        long r1 = Call("?_Random_device@std@@YAIXZ"), r2 = Call("?_Random_device@std@@YAIXZ"), r3 = Call("?_Random_device@std@@YAIXZ");
        Check("_Random_device yields varying words", !(r1 == r2 && r2 == r3));
        Check("set_new_handler returns the previous handler",
            Call("?set_new_handler@std@@YAP6AXXZP6AXXZ@Z", 0x401001) == 0 && Call("?set_new_handler@std@@YAP6AXXZP6AXXZ@Z", 0) == 0x401001);
        Check("_BADOFF is the 64-bit -1", BitConverter.ToInt64(MsvcpCore.DataConstants["?_BADOFF@std@@3_JB"]) == -1);
        Check("_Nan is a quiet NaN and _Inf is +inf",
            double.IsNaN(BitConverter.ToDouble(MsvcpCore.DataConstants["_Nan"])) && double.IsPositiveInfinity(BitConverter.ToDouble(MsvcpCore.DataConstants["_Inf"])));
        Check("std::placeholders::_1 is a data import", MsvcpCore.DataConstants.ContainsKey("?_1@placeholders@std@@3V?$_Ph@$00@2@A"));
        foreach (string name in new[] { "?_Xlength_error@std@@YAXPBD@Z", "?_Syserror_map@std@@YAPBDH@Z", "??0_Lockit@std@@QAA@H@Z", "?_Orphan_all@_Container_base12@std@@QAAXXZ", "_Mtx_timedlock", "?__ExceptionPtrCreate@@YAXPAX@Z" })
        {
            Check($"{name} is implemented", s.IsImplemented(name));
        }

        // ---- timed waits and at-thread-exit ----
        long mtxHandle = h.Buffer(4), cndHandle = h.Buffer(4);
        Call("_Mtx_init", mtxHandle, 4);
        Call("_Cnd_init", cndHandle);
        long mtx = e.ReadUInt32(mtxHandle), cnd = e.ReadUInt32(cndHandle);
        long past = h.Buffer(12);
        long seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5;
        e.WriteMemory(past, [.. BitConverter.GetBytes(seconds), .. BitConverter.GetBytes(0)]);
        Check("_Mtx_timedlock on a free mutex succeeds", Call("_Mtx_timedlock", mtx, past) == 0);
        Check("_Cnd_timedwait past its deadline with no signal times out", Call("_Cnd_timedwait", cnd, mtx, past) == 2);
        Check("and holds the mutex again", Call("_Mtx_current_owns", mtx) == 1);
        Call("_Cnd_signal", cnd);
        Call("_Mtx_unlock", mtx);

        long flag = h.Buffer(4);
        Call("_Mtx_lock", mtx);
        Call("_Cnd_register_at_thread_exit", cnd, mtx, flag);
        Check("a registration does nothing before the thread exits", e.ReadUInt32(flag) == 0);
        Call("_Cnd_do_broadcast_at_thread_exit");
        Check("at thread exit: *p = 1 and the mutex is released", e.ReadUInt32(flag) == 1 && Call("_Mtx_current_owns", mtx) == 0);
        e.WriteUInt32(flag, 0);
        Call("_Cnd_register_at_thread_exit", cnd, mtx, flag);
        Call("_Cnd_unregister_at_thread_exit", mtx);
        Call("_Cnd_do_broadcast_at_thread_exit");
        Check("_Cnd_unregister_at_thread_exit cancels it", e.ReadUInt32(flag) == 0);
    }
}

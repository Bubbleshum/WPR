using System.Text;
using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// ConcRT and PPL (<see cref="ConcurrencyRuntime"/>): cancellation tokens and registrations, the
/// unstructured and structured task collections (inline runs, stealing, worker threads, blocking
/// waits, cancellation), the scheduler queries, contexts, beacons and ConcRT's exceptions. Chores and
/// callbacks are host traps standing in for guest code, run on the emulator so thread switches and
/// guest calls happen exactly as they would for a game.
/// </summary>
public sealed class ConcurrencyRuntimeSuite : IContractSuite
{
    public string Name => "concurrency runtime";

    private const int R0 = Harness.R0, R1 = Harness.R1, R2 = Harness.R2, R3 = Harness.R3, SP = Harness.SP, LR = 10, R12 = 78;
    private const string D = "@details@Concurrency@@";

    public void Run(Harness h)
    {
        ArmEmulator e = h.E;
        HostStubs s = h.S;
        void Check(string what, bool ok, string detail = "") => h.Check(what, ok, detail);
        long Reg(int r) => e.ReadRegister(r) & 0xFFFFFFFFL;
        long U32(long a) => e.ReadUInt32(a);

        // ---- a guest call runner: dispatch an import with lr = a stop trap, then run whatever it started ----
        bool returned = false;
        long sentinel = ArmEmulator.ThumbEntry(e.RegisterVtableMethod("ppl test: returned", () =>
        {
            returned = true;
            e.Stop("returned to the contract test");
        }));
        string? lastFault = null;
        long Run(string fn, params long[] args)
        {
            int[] registers = [R0, R1, R2, R3];
            for (int i = 0; i < args.Length && i < 4; i++) e.WriteRegister(registers[i], args[i]);
            e.WriteRegister(SP, h.Stack);
            e.WriteRegister(LR, sentinel);
            e.WriteRegister(R12, sentinel);
            s.Dispatch("MSVCR110.dll!" + fn);
            long next = Reg(R12);
            lastFault = null;
            if ((next & ~1L) != (sentinel & ~1L))
            {
                returned = false;
                string? fault = e.Run(next, 50_000_000);
                if (!returned)
                {
                    lastFault = fault ?? e.StopReason ?? "the budget ran out";
                }
            }

            return Reg(R0);
        }

        // A nested import call from inside a trap: the trap's own registers survive it.
        long Nested(string fn, params long[] args)
        {
            long sp = Reg(SP), r12 = Reg(R12), lr = Reg(LR);
            long result = h.Call(fn, args);
            e.WriteRegister(SP, sp);
            e.WriteRegister(R12, r12);
            e.WriteRegister(LR, lr);
            return result;
        }

        // ---- chores: { vptr, m_pFunction, _M_pTaskCollection, _M_pChoreFunction, runtime-owned } ----
        var ran = new List<(long Chore, int Thread)>();
        var deleted = new List<long>();
        var bodies = new Dictionary<long, Action>();
        long choreVtable = e.AllocateHeap(4);
        e.WriteUInt32(choreVtable, (uint)ArmEmulator.ThumbEntry(e.RegisterVtableMethod("test chore::~", () =>
        {
            deleted.Add(Reg(R0));
            e.WriteRegister(R0, Reg(R0));
        })));
        long choreFunction = ArmEmulator.ThumbEntry(e.RegisterVtableMethod("test chore body", () =>
        {
            long chore = Reg(R0);
            ran.Add((chore, e.CurrentThreadId));
            if (bodies.TryGetValue(chore, out Action? body)) body();
            e.WriteRegister(R0, 0);
        }));
        long NewChore(bool runtimeOwned = false)
        {
            long chore = h.Buffer(0x20);
            e.WriteUInt32(chore, (uint)choreVtable);
            e.WriteUInt32(chore + 4, (uint)choreFunction);
            e.WriteMemory(chore + 0x10, [runtimeOwned ? (byte)1 : (byte)0]);
            return chore;
        }

        int Runs(long chore) => ran.Count(r => r.Chore == chore);

        // ================================================================== exceptions
        long op = h.Buffer(16);
        Check("invalid_operation(const char*) returns this", h.Call($"??0invalid_operation@Concurrency@@QAA@PBD@Z", op, h.Narrow("boom")) == op);
        Check("... with a vtable whose what() is the message", h.ReadNarrow(h.CallMethod(op, 1)) == "boom");
        Check("... owning a copy of it (_Mydofree)", e.ReadMemory(op + 8, 1)[0] == 1);
        long canceled = h.Buffer(16);
        h.Call("??0task_canceled@Concurrency@@QAA@XZ", canceled);
        Check("task_canceled(): what() is \"Unknown exception\"", h.ReadNarrow(h.CallMethod(canceled, 1)) == "Unknown exception");
        long missing = h.Buffer(16);
        h.Call("??0missing_wait@Concurrency@@QAA@XZ", missing);
        Check("missing_wait() builds a std::exception", U32(missing) != 0 && U32(missing + 4) == 0);
        e.WriteRegister(LR, 0);
        h.Call("?cancel_current_task@Concurrency@@YAXXZ");
        Check("cancel_current_task throws task_canceled : std::exception",
            s.Thrown is { } thrown && thrown.CatchableTypes.SequenceEqual([".?AVtask_canceled@Concurrency@@", ".?AVexception@std@@"]),
            s.Thrown is null ? "nothing thrown" : string.Join(",", s.Thrown.CatchableTypes));

        // ================================================================== scheduler queries
        long id1 = h.Call($"?_GetNextAsyncId{D}YAIXZ"), id2 = h.Call($"?_GetNextAsyncId{D}YAIXZ");
        Check("_GetNextAsyncId counts up from 1", id1 == 1 && id2 == 2, $"{id1},{id2}");
        Check("one virtual processor", h.Call($"?_GetNumberOfVirtualProcessors@_CurrentScheduler{D}SAIXZ") == 1 && h.Call($"?_GetConcurrency{D}YAIXZ") == 1);
        long depth = h.Call($"?_GetCurrentInlineDepth@_StackGuard{D}CAAAIXZ");
        Check("_GetCurrentInlineDepth: a stable per-thread cell", depth != 0 && h.Call($"?_GetCurrentInlineDepth@_StackGuard{D}CAAAIXZ") == depth && U32(depth) == 0);
        h.Call($"?_Trace_ppl_function@Concurrency@@YAXABU_GUID@@EW4ConcRT_EventType@1@@Z", h.Buffer(16), 4, 1);
        long contextResult = h.Buffer(4);
        Check("_CurrentContext returns its hidden result pointer", h.Call($"?_CurrentContext@_Context{D}SA?AV123@XZ", contextResult) == contextResult);
        long context = U32(contextResult);
        Check("... holding one context per thread", context != 0 && h.Call($"?_CurrentContext@_Context{D}SA?AV123@XZ", h.Buffer(4)) != 0 && U32(contextResult) == context);
        Check("the running context is not synchronously blocked", h.Call($"?_IsSynchronouslyBlocked@_Context{D}QBA_NXZ", contextResult) == 0);
        Check("_SpinOnce: keep spinning", Run($"?_SpinOnce@?$_SpinWait@$00{D}QAA_NXZ", h.Buffer(16)) == 1);
        Run($"?_Oversubscribe@_Context{D}SAX_N@Z", 1);
        Check("_Oversubscribe(true) returns", lastFault is null, lastFault ?? "");
        Check("is_task_cancellation_requested outside any task is false", h.Call("?is_task_cancellation_requested@Concurrency@@YA_NXZ") == 0);

        // ================================================================== tokens
        long token = h.Call($"?_NewTokenState@_CancellationTokenState{D}SAPAV123@XZ");
        Check("_NewTokenState: refcount 1, not canceled", token != 0 && U32(token + 4) == 1 && U32(token + 8) == 0);
        var callbacks = new List<long>();
        long callback = ArmEmulator.ThumbEntry(e.RegisterVtableMethod("test cancellation callback", () =>
        {
            callbacks.Add(Reg(R0));
            e.WriteRegister(R0, 0);
        }));
        const string Register3 = $"?_RegisterCallback@_CancellationTokenState{D}QAAPAV_CancellationTokenRegistration@23@P6AXPAX@Z0H@Z";
        const string Register1 = $"?_RegisterCallback@_CancellationTokenState{D}QAAXPAV_CancellationTokenRegistration@23@@Z";
        const string Deregister = $"?_DeregisterCallback@_CancellationTokenState{D}QAAXPAV_CancellationTokenRegistration@23@@Z";
        const string CancelToken = $"?_Cancel@_CancellationTokenState{D}QAAXXZ";
        long registration = Run(Register3, token, callback, 0xDA7A, 1);
        Check("_RegisterCallback(proc, data, 1): a registration, one reference each for caller and token",
            registration != 0 && U32(registration + 4) == 2 && U32(registration + 8) == 0 && U32(registration + 0x10) == token);
        Check("... and nothing called yet", callbacks.Count == 0);
        Run(CancelToken, token);
        Check("_Cancel runs the callback with its data", callbacks.SequenceEqual([0xDA7AL]), string.Join(",", callbacks) + " " + lastFault);
        Check("... sets the flag the inline _IsCanceled reads", U32(token + 8) == 1);
        Check("... marks the registration called and drops the token's reference", U32(registration + 8) == 3 && U32(registration + 4) == 1);
        Run(CancelToken, token);
        Check("a second _Cancel calls nothing again", callbacks.Count == 1);
        long late = Run(Register3, token, callback, 0x1A7E, 1);
        Check("registering on a canceled token calls it at once", late != 0 && callbacks.Count == 2 && callbacks[1] == 0x1A7E && U32(late + 4) == 1);

        long token2 = h.Call($"?_NewTokenState@_CancellationTokenState{D}SAPAV123@XZ");
        long gone = Run(Register3, token2, callback, 0x60, 1);
        Run(Deregister, token2, gone);
        Check("_DeregisterCallback unlinks it and drops the token's reference", U32(gone + 4) == 1 && U32(gone + 8) == 2, lastFault ?? "");
        Run(CancelToken, token2);
        Check("... so cancelling no longer calls it", callbacks.Count == 2);

        // A registration the game defines: { vptr [~, _Destroy, _Exec], refcount, state, ?, token, ... }.
        var executed = new List<long>();
        var destroyed = new List<long>();
        long registrationVtable = e.AllocateHeap(12);
        e.WriteUInt32(registrationVtable, (uint)ArmEmulator.ThumbEntry(e.RegisterVtableMethod("test registration::~", () => e.WriteRegister(R0, Reg(R0)))));
        e.WriteUInt32(registrationVtable + 4, (uint)ArmEmulator.ThumbEntry(e.RegisterVtableMethod("test registration::_Destroy", () => destroyed.Add(Reg(R0)))));
        e.WriteUInt32(registrationVtable + 8, (uint)ArmEmulator.ThumbEntry(e.RegisterVtableMethod("test registration::_Exec", () => executed.Add(Reg(R0)))));
        long own = h.Buffer(0x20);
        e.WriteUInt32(own, (uint)registrationVtable);
        e.WriteUInt32(own + 4, 1);
        e.WriteUInt32(own + 8, 3);
        long token3 = h.Call($"?_NewTokenState@_CancellationTokenState{D}SAPAV123@XZ");
        Run(Register1, token3, own);
        Check("_RegisterCallback(registration): CLEAR, a reference, the token at +0x10",
            U32(own + 8) == 0 && U32(own + 4) == 2 && U32(own + 0x10) == token3);
        Run(CancelToken, token3);
        Check("cancelling calls its _Exec (slot 2) with the registration", executed.SequenceEqual([own]), lastFault ?? "");
        Check("... then CALLED and the token's reference released", U32(own + 8) == 3 && U32(own + 4) == 1);
        long own2 = h.Buffer(0x20);
        e.WriteUInt32(own2, (uint)registrationVtable);
        e.WriteUInt32(own2 + 4, 0);
        long token4 = h.Call($"?_NewTokenState@_CancellationTokenState{D}SAPAV123@XZ");
        Run(Register1, token4, own2);
        Run(Deregister, token4, own2);
        Check("a release to zero calls _Destroy (slot 1)", destroyed.SequenceEqual([own2]), lastFault ?? "");

        // ================================================================== the unstructured collection
        const string NewCollection = $"?_NewCollection@_AsyncTaskCollection{D}SAPAV123@PAV_CancellationTokenState@23@@Z";
        const string Schedule = $"?_Schedule@_TaskCollection{D}QAAXPAV_UnrealizedChore@23@@Z";
        const string RunAndWait = $"?_RunAndWait@_TaskCollection{D}QAA?AW4_TaskCollectionStatus@23@PAV_UnrealizedChore@23@@Z";
        const string CancelCollection = $"?_Cancel@_TaskCollection{D}QAAXXZ";
        long async = h.Call(NewCollection, 2);
        Check("_NewCollection: refcount 1 and a _Destroy slot", async != 0 && U32(async + 4) == 1 && U32(U32(async) + 4) != 0);
        long tc = async + 8;
        long a = NewChore(runtimeOwned: true);
        h.Call(Schedule, tc, a);
        Check("_Schedule records the collection in the chore", U32(a + 8) == tc);
        Check("... and does not run it there and then", Runs(a) == 0);
        long status = Run(RunAndWait, tc, 0);
        Check("_RunAndWait(NULL) runs the scheduled chore here and answers _Completed", status == 1 && Runs(a) == 1 && ran[^1].Thread == 1, lastFault ?? "");
        Check("... and deletes a chore the runtime owns", deleted.Contains(a));
        long b = NewChore();
        status = Run(RunAndWait, tc, b);
        Check("_RunAndWait(chore) runs it inline", status == 1 && Runs(b) == 1 && !deleted.Contains(b), lastFault ?? "");

        long async2 = h.Call(NewCollection, 2);
        long c = NewChore();
        h.Call(Schedule, async2 + 8, c);
        h.Call(CancelCollection, async2 + 8);
        status = Run(RunAndWait, async2 + 8, 0);
        Check("a canceled collection does not run its chores and waits _Canceled", status == 2 && Runs(c) == 0, lastFault ?? "");

        long token5 = h.Call($"?_NewTokenState@_CancellationTokenState{D}SAPAV123@XZ");
        long async3 = h.Call(NewCollection, token5);
        long d = NewChore();
        h.Call(Schedule, async3 + 8, d);
        Run(CancelToken, token5);
        status = Run(RunAndWait, async3 + 8, 0);
        Check("cancelling the collection's token cancels it", status == 2 && Runs(d) == 0, lastFault ?? "");

        // inside a chore: is_task_cancellation_requested and a beacon see the collection's cancel.
        long async4 = h.Call(NewCollection, 2);
        long g = NewChore();
        long beacon = h.Buffer(4);
        long seenBefore = -1, seenAfter = -1, signals = -1, confirmed = -1;
        bodies[g] = () =>
        {
            seenBefore = Nested("?is_task_cancellation_requested@Concurrency@@YA_NXZ");
            Nested($"??0_Cancellation_beacon{D}QAA@XZ", beacon);
            Nested(CancelCollection, async4 + 8);
            seenAfter = Nested("?is_task_cancellation_requested@Concurrency@@YA_NXZ");
            signals = U32(U32(beacon));
            confirmed = Nested($"?_Confirm_cancel@_Cancellation_beacon{D}QAA_NXZ", beacon);
            Nested($"??1_Cancellation_beacon{D}QAA@XZ", beacon);
        };
        status = Run(RunAndWait, async4 + 8, g);
        Check("is_task_cancellation_requested inside a task: false, then true once its collection is canceled",
            seenBefore == 0 && seenAfter == 1, $"{seenBefore},{seenAfter} {lastFault}");
        Check("the beacon is raised by the cancel, and _Confirm_cancel confirms it", signals == 1 && confirmed == 1, $"{signals},{confirmed}");
        Check("... and the wait answers _Canceled", status == 2);
        long lone = h.Buffer(4);
        h.Call($"??0_Cancellation_beacon{D}QAA@XZ", lone);
        e.WriteUInt32(U32(lone), 1);
        Check("a beacon raised outside any canceling task: _Confirm_cancel lowers it, false",
            h.Call($"?_Confirm_cancel@_Cancellation_beacon{D}QAA_NXZ", lone) == 0 && U32(U32(lone)) == 0);

        // ================================================================== workers and blocking waits
        int startedBefore = s.Ppl.WorkersStarted;
        long async5 = h.Call(NewCollection, 2);
        long w1 = NewChore();
        h.Call(Schedule, async5 + 8, w1);
        Check("queued work has a worker to take it", s.Ppl.WorkersStarted > 0, $"{startedBefore} -> {s.Ppl.WorkersStarted}");
        Run($"?_Yield@_Context{D}SAXXZ");
        Check("a yield lets the worker run the chore on its own thread", Runs(w1) == 1 && ran.Last(r => r.Chore == w1).Thread != 1, lastFault ?? "");
        Check("... and the main thread carries on afterwards", lastFault is null && e.CurrentThreadId == 1, $"{lastFault} thread {e.CurrentThreadId}");

        bool release = false;
        long w2 = NewChore();
        bodies[w2] = () => e.BlockCurrentThread(() => release, why: "test chore waiting");
        h.Call(Schedule, async5 + 8, w2);
        Run($"?_Yield@_Context{D}SAXXZ");
        Check("a worker's chore can block, giving the CPU back", Runs(w2) == 1 && e.CurrentThreadId == 1, lastFault ?? "");
        release = true;
        status = Run(RunAndWait, async5 + 8, 0);
        Check("_RunAndWait blocks until a chore running on another thread has finished", status == 1 && lastFault is null && e.CurrentThreadId == 1, lastFault ?? "");

        // An exception that leaves a chore is caught by the runtime (MSVCR110's chore wrapper):
        // the collection is canceled and the worker goes on to its next chore. ppltasks rethrows
        // into that wrapper on purpose; Halo: Spartan Assault's first save does exactly that.
        long async6 = h.Call(NewCollection, 2), async7 = h.Call(NewCollection, 2);
        long thrower = NewChore(), after = NewChore();
        int raisedBefore = s.Ppl.ChoresRaised;
        bodies[thrower] = () =>
        {
            long what = h.Buffer(12);
            s.ThrowIntoGuest(new ThrownException(what, [".?AVruntime_error@std@@", ".?AVexception@std@@"]));
        };
        h.Call(Schedule, async6 + 8, thrower);
        h.Call(Schedule, async7 + 8, after);
        Run($"?_Yield@_Context{D}SAXXZ");
        int throwerThread = ran.LastOrDefault(r => r.Chore == thrower).Thread;
        Check("a chore that throws is caught by the runtime, not left to end its thread",
            Runs(thrower) == 1 && s.Ppl.ChoresRaised == raisedBefore + 1, $"raised {s.Ppl.ChoresRaised - raisedBefore} {lastFault}");
        Check("... and the same worker goes on to the next chore", Runs(after) == 1 && ran.Last(r => r.Chore == after).Thread == throwerThread && throwerThread != 1,
            $"{throwerThread} / {ran.LastOrDefault(r => r.Chore == after).Thread} {lastFault}");
        Check("... while the thrower's collection is canceled", Run(RunAndWait, async6 + 8, 0) == 2 && Run(RunAndWait, async7 + 8, 0) == 1, lastFault ?? "");

        var tasks = new List<long>();
        long proc = ArmEmulator.ThumbEntry(e.RegisterVtableMethod("test scheduled task", () => tasks.Add(Reg(R0))));
        h.Call($"?_ScheduleTask@_CurrentScheduler{D}SAXP6AXPAX@Z0@Z", proc, 0x7A5C);
        Run($"?wait@Concurrency@@YAXI@Z", 0);
        Check("_ScheduleTask(proc, data) runs proc(data) on a worker", tasks.SequenceEqual([0x7A5CL]), $"{string.Join(",", tasks)} {lastFault}");

        // ================================================================== the structured collection
        const string StructuredCtor = $"??0_StructuredTaskCollection{D}QAA@PAV_CancellationTokenState@12@@Z";
        const string StructuredSchedule = $"?_Schedule@_StructuredTaskCollection{D}QAAXPAV_UnrealizedChore@23@@Z";
        const string StructuredWait = $"?_RunAndWait@_StructuredTaskCollection{D}QAA?AW4_TaskCollectionStatus@23@PAV_UnrealizedChore@23@@Z";
        long group = h.Buffer(0x40);
        Check("_StructuredTaskCollection(token) returns this", h.Call(StructuredCtor, group, 2) == group);
        long s1 = NewChore(), s2 = NewChore(), s3 = NewChore();
        h.Call(StructuredSchedule, group, s1);
        h.Call(StructuredSchedule, group, s2);
        status = Run(StructuredWait, group, s3);
        Check("structured run_and_wait: the given chore and every scheduled one ran, _Completed",
            status == 1 && Runs(s1) == 1 && Runs(s2) == 1 && Runs(s3) == 1, lastFault ?? "");
        h.Call($"?_CleanupToken@_StructuredTaskCollection{D}AAAXXZ", group);
        long group2 = h.Buffer(0x40);
        h.Call(StructuredCtor, group2, 2);
        long s4 = NewChore();
        h.Call(StructuredSchedule, group2, s4);
        Run($"?_Abort@_StructuredTaskCollection{D}AAAXXZ", group2);
        Run($"?_Yield@_Context{D}SAXXZ");
        Check("_Abort cancels: a queued chore never runs", Runs(s4) == 0 && lastFault is null, lastFault ?? "");
        long group3 = h.Buffer(0x40);
        h.Call(StructuredCtor, group3, 2);
        long s5 = NewChore();
        h.Call(StructuredSchedule, group3, s5);
        h.Call($"?_CheckTaskCollection@_UnrealizedChore{D}IAAXXZ", s5);
        Run($"?_Yield@_Context{D}SAXXZ");
        Check("_CheckTaskCollection withdraws a chore destroyed while still queued", Runs(s5) == 0 && Run(StructuredWait, group3, 0) == 1);

        // ================================================================== allocator
        long block = h.Call("?Alloc@Concurrency@@YAPAXI@Z", 40);
        Check("Concurrency::Alloc returns a block", block != 0);
        h.Call("?Free@Concurrency@@YAXPAX@Z", block);
        Check("everything here is implemented", new[]
        {
            "??0task_canceled@Concurrency@@QAA@XZ", "??0invalid_operation@Concurrency@@QAA@PBD@Z", "??0invalid_operation@Concurrency@@QAA@XZ",
            "??0missing_wait@Concurrency@@QAA@XZ", CancelToken, $"?_NewTokenState@_CancellationTokenState{D}SAPAV123@XZ", Register1, Register3,
            Deregister, NewCollection, Schedule, RunAndWait, CancelCollection, StructuredCtor, StructuredSchedule, StructuredWait,
            $"?_CleanupToken@_StructuredTaskCollection{D}AAAXXZ", $"?_Abort@_StructuredTaskCollection{D}AAAXXZ",
            $"?_CheckTaskCollection@_UnrealizedChore{D}IAAXXZ", $"?_Oversubscribe@_Context{D}SAX_N@Z", $"?_Yield@_Context{D}SAXXZ",
            $"?_CurrentContext@_Context{D}SA?AV123@XZ", $"?_IsSynchronouslyBlocked@_Context{D}QBA_NXZ", $"?_UnderlyingYield{D}YAXXZ",
            $"?_SpinOnce@?$_SpinWait@$00{D}QAA_NXZ", $"??0_Cancellation_beacon{D}QAA@XZ", $"??1_Cancellation_beacon{D}QAA@XZ",
            $"?_Confirm_cancel@_Cancellation_beacon{D}QAA_NXZ", $"?_GetNumberOfVirtualProcessors@_CurrentScheduler{D}SAIXZ",
            $"?_Trace_ppl_function@Concurrency@@YAXABU_GUID@@EW4ConcRT_EventType@1@@Z", $"?_GetNextAsyncId{D}YAIXZ",
            "?cancel_current_task@Concurrency@@YAXXZ", "?is_task_cancellation_requested@Concurrency@@YA_NXZ",
        }.All(s.IsImplemented));
    }
}

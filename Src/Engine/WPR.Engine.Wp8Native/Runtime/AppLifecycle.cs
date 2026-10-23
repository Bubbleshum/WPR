using System.Collections.Concurrent;

namespace WPR.Wp8Native
{
    /// <summary>
    /// The app lifecycle as a WP8 title sees it: the host going to the background or coming back is
    /// raised to the guest as <c>CoreWindow.Activated</c>, <c>CoreWindow.VisibilityChanged</c>,
    /// <c>CoreApplication.Suspending</c> and <c>CoreApplication.Resuming</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>Order, per the Windows Runtime app model.</b> Leaving: the window is deactivated
    /// (<c>Activated</c> with <c>CoreWindowActivationState.Deactivated</c>), becomes invisible
    /// (<c>VisibilityChanged</c>, false), and then the app is suspended (<c>Suspending</c>). Coming
    /// back: <c>Resuming</c> (only if the app was suspended), <c>VisibilityChanged</c> true, then
    /// <c>Activated</c> with <c>CodeActivated</c>.</para>
    /// <para><b>A Direct3D/XAML title also gets its managed shell's half</b> (XamlInterop.cs). On WP8
    /// the Silverlight page ran <c>Application_Deactivated</c> and <c>OnNavigatedFrom</c>, and the
    /// <c>DrawingSurfaceBackgroundGrid</c> left the screen: it called
    /// <c>IDrawingSurfaceBackgroundContentProviderNative::Disconnect</c> and drew nothing more. Coming
    /// back with the instance preserved, <c>Application_Activated</c> and <c>OnNavigatedTo</c> ran,
    /// and the grid called <c>Connect</c> again (with a new device: the old one was lost) before the
    /// next <c>PrepareResources</c>/<c>Draw</c>. Here that is, leaving: <c>Activated(Deactivated)</c>,
    /// <c>VisibilityChanged(false)</c>, the shell's <see cref="XamlShell.Deactivated"/> calls,
    /// <c>Disconnect</c>, then <c>Suspending</c> last as ever; and coming back: <c>Resuming</c>,
    /// <c>VisibilityChanged(true)</c>, the shell's <see cref="XamlShell.Activated"/> calls,
    /// <c>Connect</c> (the same device: the host's is never lost), then <c>Activated(CodeActivated)</c>.
    /// While disconnected the frame loop draws nothing and takes no input, but keeps turning, paced by
    /// the host (<c>IdleTurn</c>), so a deferral and the return can be delivered.</para>
    /// <para><b>Suspending deferrals are honoured.</b> A handler that takes
    /// <c>args.SuspendingOperation.GetDeferral()</c> keeps the app "suspending" until it calls
    /// <c>Complete()</c> or the deadline passes, as on the device; a return to the foreground in the
    /// meantime is held until then, because the platform never resumes an app that has not finished
    /// suspending.</para>
    /// <para>A transition comes in from the host's thread through <see cref="InjectVisibility"/> and
    /// goes out on the guest's, at the next turn round its main loop (an exe title's
    /// <c>ProcessEvents</c>, a XAML component's frame), the same way a Back press does.</para>
    /// <para>The guest is not frozen while "suspended": the host's own loop already stops ticking it
    /// (Android) or keeps it in lockstep with an inactive window (desktop).</para>
    /// </remarks>
    public sealed partial class WinRtRuntime
    {
        /// <summary>ICoreWindow slots, by the documented member order (verified in the constructor).</summary>
        private const int SlotWindowVisible = InspectableSlots + 11;
        private const int SlotAddWindowActivated = InspectableSlots + 18;
        private const int SlotRemoveWindowActivated = InspectableSlots + 19;
        private const int SlotAddVisibilityChanged = InspectableSlots + 50;
        private const int SlotRemoveVisibilityChanged = InspectableSlots + 51;

        /// <summary>Windows.UI.Core.CoreWindowActivationState.</summary>
        private const uint CodeActivated = 0, Deactivated = 1;

        private readonly ConcurrentQueue<bool> _visibilityRequests = new();
        private readonly List<string> _appLifecycleLog = new();
        private bool _suspended;
        private bool _suspending;
        private DateTime _suspendDeadline;
        private int _suspendDeferrals;

        /// <summary>Whether the window is visible, as <c>CoreWindow.Visible</c> answers.</summary>
        public bool WindowVisible { get; private set; } = true;

        /// <summary>Whether the app has been suspended (Suspending raised and every deferral done) and not resumed.</summary>
        public bool IsSuspended => _suspended;

        /// <summary>How long a Suspending deferral may hold the app: the Windows Runtime's 5 seconds.</summary>
        public TimeSpan SuspendingDeadline { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>Every lifecycle event raised to the guest, and what came of it, in order.</summary>
        public IReadOnlyList<string> AppLifecycleLog => _appLifecycleLog;

        /// <summary>Queues the host going to the background (false) or coming back (true). Safe from any thread.</summary>
        public void InjectVisibility(bool visible) => _visibilityRequests.Enqueue(visible);

        /// <summary><c>add_Activated</c>/<c>add_VisibilityChanged</c>: kept by slot, token = slot, so a remove can find it.</summary>
        private Action AddLifecycleWindowHandler(int slot) => () =>
        {
            long handler = Arg(1);
            _windowHandlers[slot] = handler;
            if (IsWritable(Arg(2), 8))
            {
                _emulator.WriteUInt64(Arg(2), (ulong)slot);
            }

            AddRefThenReturn(handler, HResultOk);
        };

        /// <summary><c>remove_X(EventRegistrationToken)</c>: the 64-bit token arrives in r2:r3.</summary>
        private Action RemoveLifecycleWindowHandler(int slot) => () =>
        {
            long token = (Arg(3) << 32) | (Arg(2) & 0xFFFFFFFFL);
            if (token == slot)
            {
                _windowHandlers.Remove(slot);
            }

            Return(HResultOk);
        };

        /// <summary>
        /// Raises the next queued lifecycle transition, or returns false if there is none (or a
        /// suspension is still waiting on a deferral). As with <see cref="DeliverBack"/>, true means it
        /// has taken over the return path and will run <paramref name="continueWith"/> itself.
        /// </summary>
        private bool DeliverLifecycle(Action continueWith)
        {
            if (_suspending)
            {
                bool done = _suspendDeferrals <= 0;
                if (!done && DateTime.UtcNow < _suspendDeadline)
                {
                    return false;
                }

                _suspending = false;
                _suspended = true;
                _appLifecycleLog.Add(done
                    ? "suspended: every deferral completed"
                    : $"suspended: deadline passed with {_suspendDeferrals} deferral(s) outstanding");
            }

            bool visible;
            do
            {
                if (!_visibilityRequests.TryDequeue(out visible))
                {
                    return false;
                }
            }
            while (visible == WindowVisible);

            // Each step runs and then calls its continuation: a guest delegate, or (for a XAML
            // component) the page's own calls and the grid's Disconnect/Connect.
            var steps = new List<(string Name, Action<Action> Run)>();
            void Raise(string name, long handler, long[] arguments) => steps.Add((name, next =>
            {
                long invoke = _emulator.ReadUInt32(_emulator.ReadUInt32(handler, 0) + (SlotDelegateInvoke * 4), 0);
                if (!_emulator.IsExecutableCode(invoke))
                {
                    _appLifecycleLog.Add($"   {name}: handler 0x{handler:X8} has no Invoke");
                    next();
                    return;
                }

                _appLifecycleLog.Add($"   {name}");
                _emulator.CallEmulated(name, invoke, arguments, onReturn: () =>
                {
                    if ((Arg(0) & 0xFFFFFFFFL) != HResultOk)
                    {
                        _appLifecycleLog.Add($"   {name} returned 0x{Arg(0):X8}");
                    }

                    next();
                }, recycle: true);
            }));

            void Window(int slot, string name, long args)
            {
                if (_windowHandlers.TryGetValue(slot, out long handler) && handler != 0)
                {
                    Raise($"CoreWindow::{name}", handler, [handler, _coreWindow, args]);
                }
            }

            // A Direct3D/XAML title's managed shell: the page's Application_Deactivated/Activated
            // calls on the component (XamlShell.Deactivated/Activated), and the
            // DrawingSurfaceBackgroundGrid disconnecting and reconnecting the content provider.
            void Page(string name, IReadOnlyList<XamlShellStep> calls) => steps.Add((name, next =>
            {
                _appLifecycleLog.Add($"   {name} ({calls.Count} call(s))");
                RunSteps(calls, 0, next);
            }));

            bool xaml = HostsXamlComponent;
            WindowVisible = visible;
            Action finished;
            if (!visible)
            {
                Window(SlotAddWindowActivated, "Activated(Deactivated)", Classes.WindowActivatedEventArgs(Deactivated));
                Window(SlotAddVisibilityChanged, "VisibilityChanged(false)", Classes.VisibilityChangedEventArgs(false));
                if (xaml)
                {
                    Page("XamlShell::Application_Deactivated", _shell!.Deactivated);
                    steps.Add(("DrawingSurfaceBackgroundGrid::Disconnect", DisconnectProvider));
                }

                if (_suspendingHandlers.Count > 0)
                {
                    _suspendDeadline = DateTime.UtcNow + SuspendingDeadline;
                    _suspendDeferrals = 0;
                    long args = Classes.SuspendingEventArgs(_suspendDeadline);
                    foreach (long handler in _suspendingHandlers.Values)
                    {
                        Raise("CoreApplication::Suspending", handler, [handler, 0, args]);
                    }
                }

                finished = () =>
                {
                    if (_suspendDeferrals > 0)
                    {
                        _suspending = true;
                        _appLifecycleLog.Add($"suspending: waiting on {_suspendDeferrals} deferral(s)");
                    }
                    else
                    {
                        _suspended = true;
                        _appLifecycleLog.Add("suspended");
                    }
                };
            }
            else
            {
                if (_suspended)
                {
                    foreach (long handler in _resumingHandlers.Values)
                    {
                        Raise("CoreApplication::Resuming", handler, [handler, 0, 0]);
                    }
                }

                _suspended = false;
                Window(SlotAddVisibilityChanged, "VisibilityChanged(true)", Classes.VisibilityChangedEventArgs(true));
                if (xaml && _providerDisconnected)
                {
                    Page("XamlShell::Application_Activated", _shell!.Activated);
                    // OnNavigatedTo runs again on the way back, setting the flag check_NavigatedTo reads.
                    _mandreelNavigatedTo = true;
                    steps.Add(("DrawingSurfaceBackgroundGrid::Connect", ReconnectProvider));
                }

                Window(SlotAddWindowActivated, "Activated(CodeActivated)", Classes.WindowActivatedEventArgs(CodeActivated));
                finished = () => _appLifecycleLog.Add("resumed");
            }

            _appLifecycleLog.Add($"{(visible ? "foreground" : "background")}: {steps.Count} handler(s)");
            int next = 0;
            void RaiseNext()
            {
                if (next < steps.Count)
                {
                    steps[next++].Run(RaiseNext);
                    return;
                }

                finished();
                continueWith();
            }

            if (steps.Count == 0)
            {
                finished();
                return false;
            }

            RaiseNext();
            return true;
        }

        private sealed partial class SystemClasses
        {
            private sealed class HandledState
            {
                public uint Value;
                public bool Handled;
            }

            private sealed class DeferralState
            {
                public bool Completed;
            }

            private static readonly InterfaceDef CoreWindowEventArgs = Meta("Windows.UI.Core.ICoreWindowEventArgs");

            private (string, Member) GetHandled => ("get_Handled", c => c.RetBool(c.State<HandledState>().Handled));

            private (string, Member) PutHandled => ("put_Handled", c =>
            {
                c.State<HandledState>().Handled = (c.P(0) & 0xFF) != 0;
                c.Ok();
            });

            /// <summary>Windows.UI.Core.VisibilityChangedEventArgs.</summary>
            public long VisibilityChangedEventArgs(bool visible)
            {
                ComClass cls = Class("Windows.UI.Core.VisibilityChangedEventArgs", [
                    I(Meta("Windows.UI.Core.IVisibilityChangedEventArgs"), ("get_Visible", c => c.RetBool(c.State<HandledState>().Value != 0))),
                    I(CoreWindowEventArgs, GetHandled, PutHandled),
                ]);
                return New(cls, new HandledState { Value = visible ? 1u : 0u }).Primary;
            }

            /// <summary>Windows.UI.Core.WindowActivatedEventArgs.</summary>
            public long WindowActivatedEventArgs(uint state)
            {
                ComClass cls = Class("Windows.UI.Core.WindowActivatedEventArgs", [
                    I(Meta("Windows.UI.Core.IWindowActivatedEventArgs"), ("get_WindowActivationState", c => c.RetU32(c.State<HandledState>().Value))),
                    I(CoreWindowEventArgs, GetHandled, PutHandled),
                ]);
                return New(cls, new HandledState { Value = state }).Primary;
            }

            /// <summary>
            /// Windows.ApplicationModel.SuspendingEventArgs, its SuspendingOperation and the deferrals
            /// that operation hands out: each GetDeferral holds the suspension until its Complete.
            /// </summary>
            public long SuspendingEventArgs(DateTime deadline)
            {
                ComClass deferral = Class("Windows.ApplicationModel.SuspendingDeferral", [
                    I(Meta("Windows.ApplicationModel.ISuspendingDeferral"), ("Complete", c =>
                    {
                        DeferralState state = c.State<DeferralState>();
                        if (!state.Completed)
                        {
                            state.Completed = true;
                            _rt._suspendDeferrals--;
                            _rt._appLifecycleLog.Add($"   SuspendingDeferral.Complete ({_rt._suspendDeferrals} outstanding)");
                        }

                        c.Ok();
                    })),
                ]);

                ComClass operation = Class("Windows.ApplicationModel.SuspendingOperation", [
                    I(
                        Meta("Windows.ApplicationModel.ISuspendingOperation"),
                        ("GetDeferral", c =>
                        {
                            _rt._suspendDeferrals++;
                            c.RetObj(New(deferral, new DeferralState()).Primary);
                        }),
                        ("get_Deadline", c => c.RetI64(((DateTime)c.Self.State!).ToFileTimeUtc()))),
                ]);

                long op = New(operation, deadline).Primary;
                ComClass args = Class("Windows.ApplicationModel.SuspendingEventArgs", [
                    I(Meta("Windows.ApplicationModel.ISuspendingEventArgs"), ("get_SuspendingOperation", c => c.RetObj((long)c.Self.State!))),
                ]);
                return New(args, op).Primary;
            }
        }
    }
}

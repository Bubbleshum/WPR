namespace WPR.Wp8Native
{
    /// <summary>
    /// What a XAML title's page does with the hardware Back button, from its
    /// <c>OnBackKeyPress</c>/<c>BackKeyPress</c> handler.
    /// </summary>
    /// <param name="QuitIf">
    /// A boolean method of the component asked first. When it answers <paramref name="QuitWhen"/>
    /// the page lets the press through and the app closes; otherwise it cancels the press and runs
    /// <paramref name="Otherwise"/>.
    /// </param>
    /// <param name="Otherwise">What the page does when it keeps the press for the game.</param>
    /// <param name="BeforeQuit">What the app does on its way out (<c>Application_Closing</c>).</param>
    /// <param name="QuitWhen">
    /// The answer that means "close": true for Alex's <c>IsSafeToQuit</c>, false for a method that
    /// answers <c>e.Cancel</c> (Modern Combat 4's <c>OnBackButtonPressed</c>).
    /// </param>
    public sealed record XamlBackKey(
        string QuitIf,
        IReadOnlyList<XamlShellStep> Otherwise,
        IReadOnlyList<XamlShellStep> BeforeQuit,
        bool QuitWhen = true);

    /// <summary>
    /// The hardware Back button: <c>Windows.Phone.UI.Input.HardwareButtons.BackPressed</c> for an
    /// exe title, the page's back-key handler (<see cref="XamlShell.BackKey"/>) for a XAML one.
    /// </summary>
    /// <remarks>
    /// <para>A press comes in from the host's thread through <see cref="InjectBack"/> and goes out
    /// on the emulator's, at the next turn round the main loop, ahead of any pointer input.</para>
    /// <para>On WP8 a press nobody handles closes the app. That is the host's job, so the answer
    /// is reported through <see cref="BackPressDelivered"/>: false means "the game let it through,
    /// close it".</para>
    /// </remarks>
    public sealed partial class WinRtRuntime
    {
        private int _backRequests;
        private long _backHandler;
        private long _backArgs;
        private bool _backHandled;

        /// <summary>Raised on the emulator's thread once a press has been delivered: true if the game kept it.</summary>
        public event Action<bool>? BackPressDelivered;

        /// <summary>What happened to each press, for the log.</summary>
        public List<string> BackLog { get; } = new();

        /// <summary>Queues a Back press. Safe from any thread.</summary>
        public void InjectBack() => Interlocked.Increment(ref _backRequests);

        /// <summary>IHardwareButtonsStatics: add_BackPressed, remove_BackPressed.</summary>
        private long CreateHardwareButtons() => CreateDiscoveryObject(
            "IHardwareButtonsStatics",
            slotCount: 8,
            known: new Dictionary<int, (string, Action)>
            {
                [InspectableSlots + 0] = ("add_BackPressed", () =>
                {
                    long handler = Arg(1);
                    BackLog.Add($"BackPressed subscribed, handler 0x{handler:X8}");
                    _backHandler = handler;
                    if (Arg(2) != 0)
                    {
                        _emulator.WriteUInt64(Arg(2), 0xBAC0);
                    }

                    // Ours to keep: the caller releases its reference once the add returns.
                    AddRefThenReturn(handler, HResultOk);
                }),
                [InspectableSlots + 1] = ("remove_BackPressed", () =>
                {
                    BackLog.Add("BackPressed unsubscribed");
                    _backHandler = 0;
                    Return(HResultOk);
                }),
            });

        /// <summary>IBackPressedEventArgs: get_Handled, put_Handled.</summary>
        private long BackPressedArgs()
        {
            if (_backArgs == 0)
            {
                _backArgs = CreateDiscoveryObject(
                    "IBackPressedEventArgs",
                    slotCount: 8,
                    known: new Dictionary<int, (string, Action)>
                    {
                        [InspectableSlots + 0] = ("get_Handled", () =>
                        {
                            if (Arg(1) != 0)
                            {
                                _emulator.WriteMemory(Arg(1), [(byte)(_backHandled ? 1 : 0)]);
                            }

                            Return(HResultOk);
                        }),
                        [InspectableSlots + 1] = ("put_Handled", () =>
                        {
                            _backHandled = (Arg(1) & 0xFF) != 0;
                            Return(HResultOk);
                        }),
                    });
            }

            return _backArgs;
        }

        /// <summary>
        /// Delivers a pending Back press, or returns false if there is none. Like
        /// <see cref="DeliverInput(Action, long)"/>, true means it has taken over the return path
        /// and will run <paramref name="continueWith"/> itself.
        /// </summary>
        private bool DeliverBack(Action continueWith)
        {
            if (Volatile.Read(ref _backRequests) == 0)
            {
                return false;
            }

            Interlocked.Decrement(ref _backRequests);

            void Report(bool handled, string how)
            {
                BackLog.Add($"Back: {how} -> {(handled ? "kept by the game" : "let through, closing")}");
                BackPressDelivered?.Invoke(handled);
            }

            if (_shell?.BackKey is { } page)
            {
                InvokeMember(page.QuitIf, [], quit =>
                {
                    if (((quit & 0xFF) != 0) == page.QuitWhen)
                    {
                        RunSteps(page.BeforeQuit, 0, () =>
                        {
                            Report(false, $"{page.QuitIf}() = {page.QuitWhen}");
                            continueWith();
                        });
                        return;
                    }

                    RunSteps(page.Otherwise, 0, () =>
                    {
                        Report(true, $"{page.QuitIf}() = {!page.QuitWhen}");
                        continueWith();
                    });
                });
                return true;
            }

            long handler = _backHandler;
            long invoke = handler == 0 ? 0 : _emulator.ReadUInt32(_emulator.ReadUInt32(handler, 0) + (SlotDelegateInvoke * 4), 0);
            if (!_emulator.IsExecutableCode(invoke))
            {
                Report(false, handler == 0 ? "nobody subscribed" : $"handler 0x{handler:X8} has no Invoke");
                return false;
            }

            _backHandled = false;
            _emulator.CallEmulated("HardwareButtons::BackPressed", invoke, [handler, 0, BackPressedArgs()], onReturn: () =>
            {
                Report(_backHandled, "BackPressed handler");
                continueWith();
            });
            return true;
        }
    }
}

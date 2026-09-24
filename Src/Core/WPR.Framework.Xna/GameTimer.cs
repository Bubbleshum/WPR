using System;
using System.Collections.Generic;

namespace Microsoft.Xna.Framework
{
    /// <summary>
    /// Shim for <c>Microsoft.Xna.Framework.GameTimerEventArgs</c>.
    /// </summary>
    public class GameTimerEventArgs : EventArgs
    {
        public GameTimerEventArgs()
            : this(TimeSpan.Zero, TimeSpan.Zero)
        {
        }

        /// <remarks>
        /// Parameter order is XNA's: <c>totalTime</c> first. Exactly one title in the installed
        /// library constructs this itself (the rest only read the properties off the instance
        /// the timer hands them), and both parameters are <see cref="TimeSpan"/> — so a swapped
        /// order would not be a compile error, a load error or a verification error anywhere.
        /// It would be a silent timing bug in that one game. Do not "tidy" the order.
        /// </remarks>
        public GameTimerEventArgs(TimeSpan totalTime, TimeSpan elapsedTime)
        {
            TotalTime = totalTime;
            ElapsedTime = elapsedTime;
        }

        /// <summary>Time since the previous update.</summary>
        public TimeSpan ElapsedTime { get; private set; }

        /// <summary>Time since the timer was started.</summary>
        public TimeSpan TotalTime { get; private set; }
    }

    /// <summary>
    /// Shim for <c>Microsoft.Xna.Framework.GameTimer</c> — the game loop of a WP7.1
    /// Silverlight/XNA <em>mixed-mode</em> application.
    ///
    /// <para>On the phone this is driven by the Silverlight compositor: an app with no
    /// <see cref="Game"/> at all creates a <c>GameTimer</c> in its <c>PhoneApplicationPage</c>,
    /// subscribes <c>Update</c>/<c>Draw</c>, and calls <see cref="Start"/> from
    /// <c>OnNavigatedTo</c>. Under WPR there IS a real <see cref="Game"/> underneath —
    /// <c>MixedModeGame</c> — so this type does not own a clock of its own. It registers itself
    /// while started and the host pumps it from <c>Game.Update</c> / <c>Game.Draw</c>.</para>
    ///
    /// <para><b>Why the host drives it rather than a timer of our own:</b> a second clock would
    /// race the graphics device. <c>Draw</c> here issues real XNA draw calls against the
    /// backbuffer, which are only valid between the host's <c>BeginDraw</c>/<c>EndDraw</c>;
    /// firing it off a <see cref="System.Threading.Timer"/> would put draw calls on a pool
    /// thread in the middle of a present.</para>
    /// </summary>
    public class GameTimer : IDisposable
    {
        // Every started, undisposed timer in the process. A mixed-mode page typically owns
        // exactly one, but nothing stops a title creating several (a menu timer and a game
        // timer, started and stopped as it navigates), so this is a list rather than a slot.
        private static readonly List<GameTimer> _Running = new List<GameTimer>();
        private static readonly object _Sync = new object();

        private TimeSpan _updateInterval = TimeSpan.FromTicks(333333); // XNA default: 30 Hz
        private TimeSpan _totalTime;
        private TimeSpan _sinceLastUpdate;
        private bool _disposed;

        /// <summary>Raised once per update, before <see cref="FrameAction"/> and <see cref="Draw"/>.</summary>
        public event EventHandler<GameTimerEventArgs> Update;

        /// <summary>
        /// Raised once per frame between <see cref="Update"/> and <see cref="Draw"/>. WP7 titles
        /// use it to sample input at a point where the update has run but nothing has been drawn
        /// yet; 12 of the 13 mixed-mode titles in the library subscribe to it.
        /// </summary>
        public event EventHandler<EventArgs> FrameAction;

        /// <summary>Raised once per rendered frame, inside the host's draw pass.</summary>
        public event EventHandler<GameTimerEventArgs> Draw;

        /// <summary>
        /// Requested time between <see cref="Update"/> events.
        /// </summary>
        /// <remarks>
        /// <b>Honoured by pacing the HOST, not by skipping ticks here.</b> Every mixed-mode
        /// title sets this (13 of 13), almost always to 33 ms. The host reads
        /// <see cref="ShortestInterval"/> and sets its own <c>TargetElapsedTime</c> to match, so
        /// a timer fires on every tick and the tick rate is the requested rate — which is what
        /// WP7 did, where these timers were driven by the compositor and the compositor ran at
        /// 30 Hz.
        ///
        /// <para><b>Do not reintroduce an accumulator that withholds updates.</b> The first
        /// version of this class did exactly that — a timer asking for 33 ms updated on every
        /// other 16 ms host tick — and it silently broke touch input. The touch drain
        /// (<c>TouchPanel.Update</c>, via the app's own FrameworkDispatcher pump) runs every
        /// tick and ages a finger from Pressed to Moved after one tick. With the page updating
        /// on alternate ticks, the aging always landed on the tick the page skipped, so
        /// <c>GetState</c> reported Moved from the very first poll and no tap was ever seen.
        /// Measured on Cut the Rope: the menu rendered perfectly and ignored every tap, which is
        /// indistinguishable from the game being broken. Skipping a tick is not free — it
        /// desynchronises the page from the input pipeline.</para>
        /// </remarks>
        public TimeSpan UpdateInterval
        {
            get { return _updateInterval; }
            set
            {
                // XNA rejects a non-positive interval, and a game that derives one from a frame
                // rate can arrive at zero — which would make the accumulator below meaningless.
                _updateInterval = value <= TimeSpan.Zero
                    ? TimeSpan.FromTicks(333333)
                    : value;
            }
        }

        public bool IsRunning { get; private set; }

        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException("GameTimer");
            if (IsRunning) return;

            IsRunning = true;
            _sinceLastUpdate = TimeSpan.Zero;
            lock (_Sync)
            {
                if (!_Running.Contains(this)) _Running.Add(this);
            }
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            lock (_Sync)
            {
                _Running.Remove(this);
            }
        }

        /// <summary>
        /// Drops the accumulated elapsed time so the next update reports a fresh interval rather
        /// than everything that passed while the app was away. The counterpart of XNA's
        /// <c>Game.ResetElapsedTime</c>.
        /// </summary>
        public void ResetElapsedTime()
        {
            _sinceLastUpdate = TimeSpan.Zero;
        }

        public void Dispose()
        {
            Stop();
            _disposed = true;
            Update = null;
            Draw = null;
            FrameAction = null;
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Host hook: advance every running timer, raising <see cref="Update"/> then
        /// <see cref="FrameAction"/>. Called from <c>Game.Update</c>.
        /// </summary>
        internal static void PumpUpdate(TimeSpan elapsed)
        {
            GameTimer[] timers = Snapshot();
            for (int i = 0; i < timers.Length; i++)
            {
                timers[i].RaiseUpdate(elapsed);
            }
        }

        /// <summary>
        /// Host hook: raise <see cref="Draw"/> on every running timer. Called from
        /// <c>Game.Draw</c>, i.e. inside the host's begin/end draw pass.
        /// </summary>
        internal static void PumpDraw(TimeSpan elapsed)
        {
            GameTimer[] timers = Snapshot();
            for (int i = 0; i < timers.Length; i++)
            {
                GameTimer t = timers[i];
                if (!t.IsRunning) continue;
                try
                {
                    EventHandler<GameTimerEventArgs> handler = t.Draw;
                    if (handler != null)
                    {
                        handler(t, new GameTimerEventArgs(t._totalTime, elapsed));
                    }
                }
                catch (Exception ex)
                {
                    // Matches Game.Tick's own policy for a throwing Draw: report and keep the
                    // loop alive. A page that throws every frame is the Feed Me Oil shape — the
                    // game renders nothing but stays responsive enough to diagnose.
                    System.Diagnostics.Trace.WriteLine("[wpr-mixed] GameTimer.Draw threw: " + ex);
                }
            }
        }

        /// <summary>
        /// Stops and forgets every timer. Called at teardown, because this list is static: a
        /// timer a game never stopped would otherwise survive into the next launch, holding the
        /// previous game's page alive and — through it — that launch's assembly load context.
        /// </summary>
        internal static void ResetForNewLaunch()
        {
            GameTimer[] timers = Snapshot();
            for (int i = 0; i < timers.Length; i++)
            {
                timers[i].IsRunning = false;
            }
            lock (_Sync)
            {
                _Running.Clear();
            }
        }

        /// <summary>
        /// True when some running timer has a <see cref="Draw"/> handler — i.e. the current page
        /// renders itself with XNA.
        /// </summary>
        /// <remarks>
        /// <para>This is how the mixed-mode host tells a page that draws from one that does not,
        /// and the distinction is real rather than defensive: a mixed-mode title is NOT uniformly
        /// XNA. Carcassonne's <c>IngamePage</c> owns a <c>GameTimer</c> and a
        /// <c>UIElementRenderer</c> and paints its own board, while its <c>MainMenu</c> is plain
        /// Silverlight that on the phone was drawn by the compositor — which WPR does not have, so
        /// the host must composite that page itself.</para>
        ///
        /// <para><b>Subscribers, not merely running timers.</b> The WP7 app template has the App
        /// create a <c>GameTimer</c> purely to pump <c>FrameworkDispatcher</c>, with only a
        /// <c>FrameAction</c> handler; it runs for the whole life of the process. Testing
        /// "is any timer running" would therefore answer true on every page of every title and
        /// the host would never composite anything.</para>
        /// </remarks>
        internal static bool AnyDrawSubscriber
        {
            get
            {
                GameTimer[] timers = Snapshot();
                for (int i = 0; i < timers.Length; i++)
                {
                    if (timers[i].IsRunning && timers[i].Draw != null) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// The shortest <see cref="UpdateInterval"/> among running timers, or
        /// <see cref="TimeSpan.Zero"/> when none are running. The host paces its loop to this.
        /// </summary>
        /// <remarks>
        /// The shortest rather than an average or the page's: an app typically runs two timers —
        /// one the page uses for its game loop, and one the App creates purely to pump
        /// <c>FrameworkDispatcher</c> — and every one of them must fire at least as often as it
        /// asked. Taking the minimum satisfies all of them; anything slower would starve one.
        /// </remarks>
        internal static TimeSpan ShortestInterval
        {
            get
            {
                TimeSpan shortest = TimeSpan.Zero;
                GameTimer[] timers = Snapshot();
                for (int i = 0; i < timers.Length; i++)
                {
                    if (!timers[i].IsRunning) continue;
                    TimeSpan iv = timers[i]._updateInterval;
                    if (shortest == TimeSpan.Zero || iv < shortest) shortest = iv;
                }
                return shortest;
            }
        }

        private static GameTimer[] Snapshot()
        {
            // Copy before raising: a handler is entitled to Stop() its own timer, or to start
            // another, and either one mutates the list we would be walking.
            lock (_Sync)
            {
                return _Running.ToArray();
            }
        }

        private void RaiseUpdate(TimeSpan elapsed)
        {
            if (!IsRunning) return;

            _totalTime += elapsed;

            // Every tick, deliberately — see UpdateInterval. The requested rate is applied by
            // pacing the host loop, not by dropping updates here.
            TimeSpan step = elapsed + _sinceLastUpdate;
            _sinceLastUpdate = TimeSpan.Zero;

            try
            {
                EventHandler<GameTimerEventArgs> update = Update;
                if (update != null)
                {
                    update(this, new GameTimerEventArgs(_totalTime, step));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("[wpr-mixed] GameTimer.Update threw: " + ex);

                /* This — not Game.Tick's catch — is where a mixed-mode title's update exceptions
                 * actually land, so it is where the WP7 quit idiom has to be recognised: a
                 * Silverlight game exits by throwing a private exception and leaving
                 * Application.UnhandledException's e.Handled false. Report it and let Game.Tick
                 * act on the verdict; a title with no reporter is swallowed exactly as before. */
                WPR.Xna.Rhi.GameUnhandledException.Report(ex);
            }

            try
            {
                EventHandler<EventArgs> frame = FrameAction;
                if (frame != null)
                {
                    frame(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("[wpr-mixed] GameTimer.FrameAction threw: " + ex);

                // Same contract as the Update handler above: a quit can be raised from either.
                WPR.Xna.Rhi.GameUnhandledException.Report(ex);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Controls.MediaElement</c>.
    ///
    /// <para><b>This deliberately plays nothing, and reports that it finished.</b> WP7 titles
    /// ship their cutscenes as WMV (Cut the Rope carries <c>intro.wmv</c> and <c>outro.wmv</c>),
    /// and WPR's only video decoder is Theorafile, which reads Ogg Theora. Rather than fail the
    /// open — which strands a game that is waiting to be told the movie ended — this runs the
    /// real state machine at maximum speed: <c>Opening</c>, then <c>Playing</c>, then
    /// <c>Stopped</c>, one host frame apart, raising the events a title expects at each step.
    /// The visible result is that a cutscene is skipped and the game proceeds.</para>
    ///
    /// <para><b>The one-frame delay is the whole design, not laziness.</b> Cut the Rope's
    /// <c>GamePage.PlayMovie</c> calls <c>Play()</c> and only THEN sets its own
    /// <c>Playing = true</c>. A state change raised synchronously inside <c>Play()</c> would run
    /// its <c>CurrentStateChanged</c> handler while that flag was still false, the handler's
    /// <c>if (Playing)</c> arm would not run, and <c>PlayingEnded</c> would never be set — so
    /// <c>MovieMgrDelegate.moviePlaybackFinished</c> would never fire and the game would sit on
    /// the cutscene for ever. Deferring to the next pump puts the transition after the caller
    /// has finished, which is what a real asynchronous media pipeline does.</para>
    /// </summary>
    public class MediaElement : FrameworkElement
    {
        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.Register(nameof(Source), typeof(Uri), typeof(MediaElement),
                new PropertyMetadata((object?)null, OnSourceChanged));

        // Every element with a transition owing. Static because the host pumps them all from one
        // place; a page normally has exactly one MediaElement, but nothing requires that.
        private static readonly List<MediaElement> _Pending = new List<MediaElement>();
        private static readonly object _Sync = new object();

        private MediaElementState _currentState = MediaElementState.Closed;
        private double _volume = 0.85;      // Silverlight's default
        private double _balance;
        private bool _isMuted;
        private bool _autoPlay = true;      // Silverlight's default
        private TimeSpan _position;

        public event RoutedEventHandler? CurrentStateChanged;
        public event RoutedEventHandler? MediaOpened;
        public event RoutedEventHandler? MediaEnded;
        public event EventHandler<ExceptionRoutedEventArgs>? MediaFailed;
        public event TimelineMarkerRoutedEventHandler? MarkerReached;
        public event RoutedEventHandler? BufferingProgressChanged;
        public event RoutedEventHandler? DownloadProgressChanged;

        public Uri? Source
        {
            get => (Uri?)GetValue(SourceProperty);
            set => SetValue(SourceProperty, value);
        }

        public MediaElementState CurrentState => _currentState;

        /// <summary>
        /// Always <see cref="Duration.Automatic"/> — nothing is ever opened, so no real duration
        /// is ever known. A title comparing against it gets "unknown" rather than a confident
        /// zero, which is the honest answer and the one that keeps a progress calculation from
        /// dividing by it.
        /// </summary>
        public Duration NaturalDuration => Duration.Automatic;

        /// <summary>Always empty; see <see cref="TimelineMarkerCollection"/>.</summary>
        public TimelineMarkerCollection Markers { get; } = new TimelineMarkerCollection();

        public bool AutoPlay
        {
            get => _autoPlay;
            set => _autoPlay = value;
        }

        public bool IsMuted
        {
            get => _isMuted;
            set => _isMuted = value;
        }

        public double Volume
        {
            get => _volume;
            set => _volume = value < 0 ? 0 : (value > 1 ? 1 : value);
        }

        public double Balance
        {
            get => _balance;
            set => _balance = value;
        }

        public TimeSpan Position
        {
            get => _position;
            set => _position = value;
        }

        public double BufferingProgress => 1.0;

        public double DownloadProgress => 1.0;

        public bool CanPause => true;

        public bool CanSeek => false;

        public void Play()
        {
            // Opening rather than Playing: a title that polls CurrentState on the same tick
            // should not be told a file it never opened is already on screen.
            SetState(MediaElementState.Opening);
            SchedulePump();
        }

        public void Pause()
        {
            if (_currentState == MediaElementState.Playing)
            {
                SetState(MediaElementState.Paused);
            }
        }

        public void Stop()
        {
            CancelPump();
            _position = TimeSpan.Zero;
            SetState(MediaElementState.Stopped);
        }

        /// <summary>
        /// Silverlight's stream-backed source. Accepted and dropped: see the class remarks.
        /// The stream is NOT disposed — ownership stays with the caller, matching Silverlight,
        /// and a title that plays the same stream twice would otherwise get an ObjectDisposed.
        /// </summary>
        public void SetSource(Stream stream)
        {
            SetState(MediaElementState.Closed);
        }

        public void SetSource(object mediaStreamSource)
        {
            SetState(MediaElementState.Closed);
        }

        /// <summary>
        /// Host hook: advance every element with a transition owing. Called once per frame from
        /// the mixed-mode host's <c>Update</c>, before the page's own update runs.
        /// </summary>
        internal static void PumpPending()
        {
            MediaElement[] pending;
            lock (_Sync)
            {
                if (_Pending.Count == 0) return;
                pending = _Pending.ToArray();
            }

            for (int i = 0; i < pending.Length; i++)
            {
                pending[i].Advance();
            }
        }

        /// <summary>
        /// Clears the pending list at teardown. Static state, so an element left mid-transition
        /// by a game that exited during a cutscene would otherwise hold that page — and its
        /// assembly load context — alive into the next launch.
        /// </summary>
        internal static void ResetForNewLaunch()
        {
            lock (_Sync)
            {
                _Pending.Clear();
            }
        }

        private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not MediaElement me) return;

            // Silverlight opens on assignment and, when AutoPlay is set, starts straight away.
            // Cut the Rope relies on neither (it assigns Source then calls Play explicitly), but
            // a title that sets Source alone and waits for MediaEnded needs this arm.
            if (e.NewValue == null)
            {
                me.SetState(MediaElementState.Closed);
                return;
            }

            if (me._autoPlay)
            {
                me.Play();
            }
            else
            {
                me.SetState(MediaElementState.Opening);
            }
        }

        private void SchedulePump()
        {
            lock (_Sync)
            {
                if (!_Pending.Contains(this)) _Pending.Add(this);
            }
        }

        private void CancelPump()
        {
            lock (_Sync)
            {
                _Pending.Remove(this);
            }
        }

        private void Advance()
        {
            switch (_currentState)
            {
                case MediaElementState.Opening:
                    // "Opened" first: a title that starts its own soundtrack from MediaOpened
                    // expects it before anything reports playback.
                    SetState(MediaElementState.Playing);
                    Raise(MediaOpened);
                    break;

                case MediaElementState.Playing:
                    CancelPump();
                    SetState(MediaElementState.Stopped);
                    Raise(MediaEnded);
                    break;

                default:
                    // Paused, Stopped, Closed — nothing owing. Stop pumping so a paused element
                    // does not sit in the list for the rest of the launch.
                    CancelPump();
                    break;
            }
        }

        private void SetState(MediaElementState state)
        {
            if (_currentState == state) return;
            _currentState = state;
            Raise(CurrentStateChanged);
        }

        private void Raise(RoutedEventHandler? handler)
        {
            if (handler == null) return;
            try
            {
                handler(this, new RoutedEventArgs());
            }
            catch (Exception ex)
            {
                // A media callback is not worth taking the launch down for, and these run from
                // inside the host's Update. Report and continue, as the game loop does.
                System.Diagnostics.Trace.WriteLine("[wpr-mixed] MediaElement event handler threw: " + ex);
            }
        }
    }

    /// <summary>
    /// Shim for <c>System.Windows.Media.TimelineMarkerRoutedEventHandler</c>.
    /// </summary>
    public delegate void TimelineMarkerRoutedEventHandler(object sender, TimelineMarkerRoutedEventArgs e);

    /// <summary>
    /// Shim for <c>System.Windows.Media.TimelineMarkerRoutedEventArgs</c>.
    /// </summary>
    public class TimelineMarkerRoutedEventArgs : RoutedEventArgs
    {
        public TimelineMarker? Marker { get; set; }
    }
}

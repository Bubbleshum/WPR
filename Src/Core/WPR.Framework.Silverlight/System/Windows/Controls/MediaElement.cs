using System;
using System.Collections.Generic;
using System.IO;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Controls.MediaElement</c>.
    ///
    /// <para><b>Plays the movie when the platform can.</b> WP7 titles ship their cutscenes as WMV
    /// or MP4 (Cut the Rope carries <c>intro.wmv</c> and <c>outro.wmv</c>). When the platform
    /// registered a video decoder (<c>WPR.Engine.Audio.VideoDecoderBackend</c>) and the file
    /// opens, <see cref="WPR.Xna.Media.WprMoviePlayer"/> plays it: the picture is painted into
    /// this element's slot by <c>SoftwareVisualRasteriser</c>, the sound goes out through a
    /// dynamic sound effect, and <c>MediaEnded</c> fires when it really ends.</para>
    ///
    /// <para><b>Otherwise it plays nothing, and reports that it finished.</b> Rather than fail
    /// the open — which strands a game that is waiting to be told the movie ended — it runs the
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
        private WPR.Xna.Media.WprMoviePlayer? _movie;
        private bool _playRequested;
        private int _convertedVersion = -1;

        /// <summary>The current movie frame as ARGB ints (the WriteableBitmap layout the
        /// rasteriser blits), or null when nothing is showing.</summary>
        internal int[]? FramePixels { get; private set; }
        internal int FrameWidth { get; private set; }
        internal int FrameHeight { get; private set; }
        internal int FrameVersion { get; private set; }

        /// <summary>How the picture fills the element. Silverlight's default is Uniform.</summary>
        public Stretch Stretch { get; set; } = Stretch.Uniform;

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
        /// The movie's length while one is open; otherwise <see cref="Duration.Automatic"/>. A
        /// title comparing against it then gets "unknown" rather than a confident zero, which
        /// keeps a progress calculation from dividing by it.
        /// </summary>
        public Duration NaturalDuration =>
            _movie != null && _movie.Duration > TimeSpan.Zero ? new Duration(_movie.Duration) : Duration.Automatic;

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
            set { _isMuted = value; ApplyVolume(); }
        }

        public double Volume
        {
            get => _volume;
            set { _volume = value < 0 ? 0 : (value > 1 ? 1 : value); ApplyVolume(); }
        }

        public double Balance
        {
            get => _balance;
            set => _balance = value;
        }

        public TimeSpan Position
        {
            get => _movie != null ? _movie.Position : _position;
            set => _position = value;
        }

        public double BufferingProgress => 1.0;

        public double DownloadProgress => 1.0;

        public bool CanPause => true;

        public bool CanSeek => false;

        /// <summary>
        /// Asks for playback; the state changes follow on the next host frames, never inside this
        /// call. Silverlight's media pipeline is asynchronous and titles are written against that:
        /// Sid Meier's Pirates! calls <c>Play()</c> and only then sets the <c>inPage</c> flag its
        /// <c>CurrentStateChanged</c> handler requires before it lifts the black cover over the
        /// movie, so an <c>Opening</c> raised in here left the screen black for the whole film.
        /// </summary>
        public void Play()
        {
            _playRequested = true;
            SchedulePump();
        }

        public void Pause()
        {
            if (_currentState == MediaElementState.Playing)
            {
                _movie?.Pause();
                SetState(MediaElementState.Paused);
            }
        }

        public void Stop()
        {
            _playRequested = false;
            CancelPump();
            CloseMovie();
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
            MediaElement[] pending;
            lock (_Sync)
            {
                pending = _Pending.ToArray();
                _Pending.Clear();
            }
            // A movie left open holds decoder threads and a sound instance.
            foreach (MediaElement me in pending)
            {
                try { me.CloseMovie(); } catch (Exception) { }
            }
        }

        private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not MediaElement me) return;

            // A new source replaces whatever was playing. Quietly: the state machine below raises
            // the transitions the new source owes, and an extra Closed here would read to a title
            // like Pirates! as "the movie finished".
            if (me._movie != null)
            {
                me.CloseMovie();
                me._currentState = MediaElementState.Closed;
            }

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
                case MediaElementState.Closed:
                case MediaElementState.Stopped:
                    if (!_playRequested)
                    {
                        CancelPump();
                        break;
                    }
                    // Opening rather than Playing: a title that polls CurrentState on this tick
                    // should not be told a file it has only just started to open is on screen.
                    SetState(MediaElementState.Opening);
                    TryOpenMovie();
                    break;

                case MediaElementState.Opening:
                    if (_movie == null) TryOpenMovie();
                    _playRequested = false;
                    // "Opened" first: a title that starts its own soundtrack from MediaOpened
                    // expects it before anything reports playback.
                    SetState(MediaElementState.Playing);
                    Raise(MediaOpened);
                    _movie?.Play();
                    break;

                case MediaElementState.Paused:
                    if (!_playRequested)
                    {
                        CancelPump();
                        break;
                    }
                    _playRequested = false;
                    if (_movie != null)
                    {
                        _movie.Play();
                        SetState(MediaElementState.Playing);
                    }
                    else
                    {
                        // Nothing open to resume (the movie could not be played): finish, as
                        // the fallback state machine always has.
                        SetState(MediaElementState.Stopped);
                        Raise(MediaEnded);
                        CancelPump();
                    }
                    break;

                case MediaElementState.Playing:
                    _playRequested = false;
                    if (_movie != null)
                    {
                        _movie.Update();
                        RefreshFrame();
                        if (!_movie.Ended) break;
                        System.Diagnostics.Trace.WriteLine($"[wpr-video] MediaElement: {_movie.Name} ended at {_movie.Position.TotalSeconds:0.0} s");
                        CloseMovie();
                    }
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

        private void TryOpenMovie()
        {
            CloseMovie();
            string? path = ResolveSource(Source);
            if (path == null) return;
            _movie = WPR.Xna.Media.WprMoviePlayer.TryOpen(path);
            ApplyVolume();
        }

        private static string? ResolveSource(Uri? source)
        {
            if (source == null) return null;
            try
            {
                if (source.IsAbsoluteUri)
                {
                    return source.IsFile && System.IO.File.Exists(source.LocalPath) ? source.LocalPath : null;
                }
                string root = HostContext.CurrentInstallFolder ?? string.Empty;
                if (root.Length == 0) return null;
                string rel = source.OriginalString.Replace('\\', '/').TrimStart('/');
                string full = System.IO.Path.Combine(root, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                return System.IO.File.Exists(full) ? full : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Converts the newest RGBA frame into the ARGB ints the rasteriser blits.</summary>
        private void RefreshFrame()
        {
            if (_movie == null || _movie.FrameVersion == _convertedVersion) return;
            _convertedVersion = _movie.FrameVersion;
            int w = _movie.Width, h = _movie.Height;
            int[] px = FramePixels != null && FramePixels.Length == w * h ? FramePixels : new int[w * h];
            byte[] rgba = _movie.Frame;
            for (int i = 0, j = 0; i < px.Length; i++, j += 4)
            {
                px[i] = (rgba[j + 3] << 24) | (rgba[j] << 16) | (rgba[j + 1] << 8) | rgba[j + 2];
            }
            bool resized = w != FrameWidth || h != FrameHeight;
            FramePixels = px;
            FrameWidth = w;
            FrameHeight = h;
            FrameVersion++;
            if (resized) Relayout();
        }

        // What this element was last measured against and arranged into, so it can lay itself out
        // again when its natural size changes (see Relayout).
        private Size _lastAvailable;
        private Rect _lastSlot;
        private bool _laidOut;

        protected override Size MeasureCore(Size availableSize)
        {
            _lastAvailable = availableSize;
            return base.MeasureCore(availableSize);
        }

        protected override Rect ResolveArrangeRect(Rect slot)
        {
            _lastSlot = slot;
            _laidOut = true;
            return base.ResolveArrangeRect(slot);
        }

        /// <summary>
        /// Re-measures and re-arranges this element in the slot its parent last gave it, now that
        /// the movie's size is known (or gone). The mixed-mode host runs a layout pass only on
        /// navigation, deliberately — a pass raises <c>LayoutUpdated</c>, which titles react to —
        /// so a size that changes later has to be applied here. The parent keeps its own layout,
        /// which matches Silverlight for any element the parent does not size to its content.
        /// </summary>
        private void Relayout()
        {
            InvalidateMeasure();
            if (!_laidOut) return;
            Measure(_lastAvailable);
            Arrange(_lastSlot);
        }

        /// <summary>
        /// Silverlight sizes a MediaElement from the video's natural size, fitted to the space by
        /// <see cref="Stretch"/>. Without it an element aligned rather than stretched (Pirates!
        /// puts its movie at <c>VerticalAlignment="Top"</c>) measures to nothing and the film
        /// plays into a zero-height slot.
        /// </summary>
        internal override Size WprMeasure(Size availableSize)
        {
            if (FrameWidth <= 0 || FrameHeight <= 0) return Size.Empty;
            double nw = FrameWidth, nh = FrameHeight;
            if (Stretch == Stretch.None) return new Size(nw, nh);

            bool wInf = double.IsInfinity(availableSize.Width);
            bool hInf = double.IsInfinity(availableSize.Height);
            if (wInf && hInf) return new Size(nw, nh);

            double sx = wInf ? double.NaN : availableSize.Width / nw;
            double sy = hInf ? double.NaN : availableSize.Height / nh;
            switch (Stretch)
            {
                case Stretch.Fill:
                    return new Size(wInf ? nw : availableSize.Width, hInf ? nh : availableSize.Height);
                case Stretch.UniformToFill:
                {
                    double k = wInf ? sy : hInf ? sx : Math.Max(sx, sy);
                    return new Size(Math.Min(nw * k, wInf ? double.MaxValue : availableSize.Width),
                                    Math.Min(nh * k, hInf ? double.MaxValue : availableSize.Height));
                }
                default:
                {
                    double k = wInf ? sy : hInf ? sx : Math.Min(sx, sy);
                    return new Size(nw * k, nh * k);
                }
            }
        }

        private void CloseMovie()
        {
            if (_movie == null) return;
            _movie.Dispose();
            _movie = null;
            _convertedVersion = -1;
            FramePixels = null;
            FrameVersion++;
            if (FrameWidth != 0 || FrameHeight != 0)
            {
                FrameWidth = FrameHeight = 0;
                Relayout();
            }
        }

        private void ApplyVolume()
        {
            if (_movie != null) _movie.Volume = _isMuted ? 0f : (float)_volume;
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

namespace System.Device.Location
{
    /// <summary>
    /// Shim for <c>System.Device.Location.GeoCoordinateWatcher</c>.
    ///
    /// WPR has no location provider, so this never produces a fix. The members below exist
    /// because games read them before doing anything location-shaped — Crimson Dragon:
    /// Side Story (DracoWP.GPS) and Kinectimals (Location.Locator_WinPhone) both gate on
    /// <see cref="Permission"/> / <see cref="Status"/> — and an absent member is a
    /// MissingMethodException at JIT rather than a graceful "no GPS here".
    /// </summary>
    public class GeoCoordinateWatcher : IDisposable
    {
        private GeoPositionAccuracy _Accuracy;
        private double _Threshold;
        private bool _Disposed;

        public event EventHandler<GeoLocationChangedEventArgs> LocationChanged;
        public event EventHandler<GeoPositionChangedEventArgs<GeoCoordinate>> PositionChanged;
        public event EventHandler<GeoPositionStatusChangedEventArgs> StatusChanged;

        public GeoCoordinateWatcher(GeoPositionAccuracy accuracy)
        {
            _Accuracy = accuracy;
        }

        public double MovementThreshold
        {
            get => _Threshold;
            set {
                if (value < 0.0 || Double.IsNaN(value))
                {
                    throw new ArgumentOutOfRangeException("Threshold value is set to negative!");
                }

                _Threshold = value;
            }
        }

        /// <summary>
        /// Reported as <see cref="GeoPositionPermission.Denied"/> rather than Granted on purpose.
        /// A game that sees Denied takes its "user said no to location" path, which is a designed,
        /// tested branch that degrades cleanly. Granted would promise a fix we can never deliver
        /// and typically parks the game on a "acquiring location…" screen forever.
        /// </summary>
        public GeoPositionPermission Permission => GeoPositionPermission.Denied;

        /// <summary>
        /// Always <see cref="GeoPositionStatus.Disabled"/> — the same value real WP7 reports when
        /// the location service is off or access was denied, which is exactly our situation.
        /// </summary>
        public GeoPositionStatus Status => GeoPositionStatus.Disabled;

        /// <summary>
        /// An empty position with <see cref="DateTimeOffset.MinValue"/> — never a fix. Callers
        /// that check <see cref="Status"/> first (the documented pattern) won't read this.
        /// </summary>
        public GeoPosition<GeoCoordinate> Position { get; } = new GeoPosition<GeoCoordinate>();

        public void Start()
        {

        }

        public void Start(bool suppressPermissionPrompt)
        {
            Start();
        }

        public void Stop()
        {

        }

        public void Dispose()
        {
            if (_Disposed) return;
            _Disposed = true;
            Stop();
            GC.SuppressFinalize(this);
        }
    }
}
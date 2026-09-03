using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WPR.Wp8Native;

namespace WPR.Wp8Native.Desktop
{
    /// <summary>
    /// A window that shows what the emulated image presents, and hands it the mouse as a
    /// touch screen.
    /// </summary>
    /// <remarks>
    /// Two threads, kept apart on purpose. The emulator owns one: Unicorn is not reentrant,
    /// every hook and stub runs on it, and nothing here touches the CPU from anywhere else.
    /// The window owns the other. They meet at exactly two points, each a copy under a lock:
    /// a frame comes across as a private RGBA buffer, and a pointer event goes back through
    /// <see cref="WinRtRuntime.InjectPointer"/>, which queues it for the image's next turn
    /// round its own main loop.
    /// <para>
    /// The point of this over the console probe is not the picture - the probe already writes
    /// one - it is that a person can tap wherever they like. Every scripted run so far has
    /// tapped the centre of the screen, and a title screen waiting for a touch on a button
    /// somewhere else would look exactly like one waiting for nothing.
    /// </para>
    /// </remarks>
    internal sealed class GameWindow : Form
    {
        /// <summary>Long enough to be "for ever" at this emulator's speed: half a day.</summary>
        private const long Budget = 1_000_000_000_000L;

        private readonly ArmEmulator _emulator;
        private readonly string _title;
        private readonly Thread _cpu;
        private readonly object _gate = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly System.Windows.Forms.Timer _status;

        private byte[]? _latest;
        private int _frameWidth;
        private int _frameHeight;
        private Bitmap? _surface;
        private bool _paintQueued;
        private long _framesShown;
        private long _framesAtLastTick;
        private bool _pointerDown;
        private PointF _lastPointer = new(-1, -1);

        /// <summary>Why the emulator stopped, once it has.</summary>
        public string Outcome { get; private set; } = "still running";

        public GameWindow(ArmEmulator emulator, string title)
        {
            _emulator = emulator;
            _title = title;

            Text = title;
            DoubleBuffered = true;
            BackColor = Color.Black;
            StartPosition = FormStartPosition.CenterScreen;

            // The image composes in landscape at the raster size; open at 1.5x so it is
            // legible on a modern display, and let the frame stretch with the window after.
            ClientSize = new Size(FrameCapture.Width * 3 / 2, FrameCapture.Height * 3 / 2);
            MinimumSize = new Size(FrameCapture.Width / 2, FrameCapture.Height / 2);

            _status = new System.Windows.Forms.Timer { Interval = 500 };
            _status.Tick += (_, _) => UpdateTitle();
            _status.Start();

            emulator.Direct3D.FramePresented += OnFramePresented;

            _cpu = new Thread(RunEmulator)
            {
                Name = "emulated ARM",
                IsBackground = true,
            };
            _cpu.Start();
        }

        // ------------------------------------------------------------------------------
        // The emulator's side
        // ------------------------------------------------------------------------------

        private void RunEmulator()
        {
            string? fault;
            try
            {
                fault = _emulator.RunEntryPoint(Budget);
            }
            catch (Exception ex)
            {
                fault = $"host exception: {ex.GetType().Name}: {ex.Message}";
            }

            Outcome = fault ?? _emulator.StopReason ?? "budget exhausted";
            if (IsHandleCreated)
            {
                try
                {
                    BeginInvoke(UpdateTitle);
                }
                catch (InvalidOperationException)
                {
                    // The window went away first. Nothing to tell.
                }
            }
        }

        /// <summary>
        /// Takes a frame from the emulator's thread. Copies and gets out; the paint happens on
        /// the window's own thread, whenever it next gets to it.
        /// </summary>
        private void OnFramePresented(byte[] rgba, int width, int height)
        {
            lock (_gate)
            {
                if (_latest is null || _latest.Length != rgba.Length)
                {
                    _latest = new byte[rgba.Length];
                }

                Buffer.BlockCopy(rgba, 0, _latest, 0, rgba.Length);
                _frameWidth = width;
                _frameHeight = height;

                // One queued repaint at a time. The emulator can present faster than a window
                // repaints, and a queue of stale invalidations is only ever thrown away.
                if (_paintQueued || !IsHandleCreated)
                {
                    return;
                }

                _paintQueued = true;
            }

            try
            {
                BeginInvoke(Invalidate);
            }
            catch (InvalidOperationException)
            {
                lock (_gate)
                {
                    _paintQueued = false;
                }
            }
        }

        // ------------------------------------------------------------------------------
        // The window's side
        // ------------------------------------------------------------------------------

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            lock (_gate)
            {
                _paintQueued = false;
                if (_latest is null)
                {
                    return;
                }

                if (_surface is null || _surface.Width != _frameWidth || _surface.Height != _frameHeight)
                {
                    _surface?.Dispose();
                    _surface = new Bitmap(_frameWidth, _frameHeight, PixelFormat.Format32bppRgb);
                }

                Blit(_latest, _surface);
                _framesShown++;
            }

            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            e.Graphics.DrawImage(_surface, ClientRectangle);
        }

        /// <summary>
        /// RGBA in, BGRA out - GDI's 32-bit formats are little-endian ARGB, which is B, G, R,
        /// A in memory. Getting this backwards is the red-bird-comes-out-blue mistake all over
        /// again, one layer up.
        /// </summary>
        private static void Blit(byte[] rgba, Bitmap surface)
        {
            BitmapData data = surface.LockBits(
                new Rectangle(0, 0, surface.Width, surface.Height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppRgb);

            try
            {
                byte[] row = new byte[surface.Width * 4];
                for (int y = 0; y < surface.Height; y++)
                {
                    int source = y * surface.Width * 4;
                    for (int x = 0; x < surface.Width; x++)
                    {
                        int at = x * 4;
                        row[at + 0] = rgba[source + at + 2];
                        row[at + 1] = rgba[source + at + 1];
                        row[at + 2] = rgba[source + at + 0];
                        row[at + 3] = 255;
                    }

                    Marshal.Copy(row, 0, data.Scan0 + (y * data.Stride), row.Length);
                }
            }
            finally
            {
                surface.UnlockBits(data);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // The frame covers the client area; painting black underneath it first is a flicker.
            if (_latest is null)
            {
                base.OnPaintBackground(e);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Invalidate();
        }

        private void UpdateTitle()
        {
            long frames = _emulator.Direct3D.PresentCount;
            double seconds = _clock.Elapsed.TotalSeconds;
            double fps = seconds > 0 ? (frames - _framesAtLastTick) / 0.5 : 0;
            _framesAtLastTick = frames;

            string state = _cpu.IsAlive ? $"{fps,4:0} fps" : Outcome;

            // Say what the last click did. "The mouse does nothing" and "the mouse was delivered
            // to the wrong place" look identical on screen; the title bar can tell them apart,
            // because it shows the tap the image was actually handed and that it returned.
            int taps = 0;
            string? last = null;
            foreach (string line in _emulator.InputDelivered)
            {
                if (line.StartsWith("PointerPressed", StringComparison.Ordinal))
                {
                    taps++;
                    last = line;
                }
            }

            string tapText = last is null
                ? "no taps yet"
                : $"{taps} tap(s), last {last[..Math.Min(last.IndexOf(" ->", StringComparison.Ordinal) is var cut and > 0 ? cut : last.Length, last.Length)]}";

            Text = $"{_title}  -  frame {frames:N0}  -  {state}  -  {tapText}";
        }

        // ------------------------------------------------------------------------------
        // The mouse is the touch screen
        // ------------------------------------------------------------------------------

        private PointF ToRaster(Point client) => new(
            client.X * (float)FrameCapture.Width / Math.Max(1, ClientSize.Width),
            client.Y * (float)FrameCapture.Height / Math.Max(1, ClientSize.Height));

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            _pointerDown = true;
            _lastPointer = ToRaster(e.Location);
            _emulator.WinRt.InjectPointer(WinRtRuntime.PointerKind.Pressed, _lastPointer.X, _lastPointer.Y);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_pointerDown)
            {
                return;
            }

            // One move per turn of the image's main loop is all that can be delivered, so
            // there is no point queueing a move for every pixel the mouse crosses. A raster
            // pixel of travel is the useful granularity.
            PointF at = ToRaster(e.Location);
            if (Math.Abs(at.X - _lastPointer.X) < 1f && Math.Abs(at.Y - _lastPointer.Y) < 1f)
            {
                return;
            }

            _lastPointer = at;
            _emulator.WinRt.InjectPointer(WinRtRuntime.PointerKind.Moved, at.X, at.Y);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !_pointerDown)
            {
                return;
            }

            _pointerDown = false;
            PointF at = ToRaster(e.Location);
            _emulator.WinRt.InjectPointer(WinRtRuntime.PointerKind.Released, at.X, at.Y);
        }

        // ------------------------------------------------------------------------------

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _status.Stop();
            _emulator.Direct3D.FramePresented -= OnFramePresented;

            if (_cpu.IsAlive)
            {
                _emulator.Stop("window closed");
                _cpu.Join(TimeSpan.FromSeconds(3));
            }

            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _status.Dispose();
                _surface?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

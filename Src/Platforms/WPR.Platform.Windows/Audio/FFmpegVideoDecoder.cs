using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using WPR.Engine.Audio;

namespace WPR.Platform.Windows.Audio
{
    /// <summary>
    /// Windows <see cref="IVideoDecoder"/>: the bundled <c>ffmpeg.exe</c>, run as two pipes per
    /// movie — one producing RGBA frames at a constant rate, one producing 16-bit stereo PCM —
    /// each drained by a reader thread into a small bounded queue.
    ///
    /// <para>ffmpeg decodes WP7's WMV (WMV3/VC-1) and MP4 (H.264/MPEG-4) directly, so the desktop
    /// needs no install-time step. Two processes rather than one because a single pipe can carry
    /// only one stream, and interleaving them would need a container parser.</para>
    ///
    /// <para>Probing reads ffmpeg's own banner (<c>ffmpeg -i</c> with no output), since the head
    /// ships no ffprobe. A size or rate it cannot parse fails the open, and the caller then skips
    /// the movie exactly as before this existed.</para>
    /// </summary>
    public sealed class FFmpegVideoDecoder : IVideoDecoder
    {
        private static readonly string FfmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");

        private static readonly Regex DurationRx = new(@"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);
        private static readonly Regex VideoRx = new(@"Stream #\d+:\d+.*?: Video: (?!.*attached pic).*?, (\d{2,5})x(\d{2,5})", RegexOptions.Compiled);
        private static readonly Regex FpsRx = new(@"Stream #\d+:\d+.*?: Video: .*?([\d.]+) (?:fps|tbr)", RegexOptions.Compiled);
        private static readonly Regex AudioRx = new(@"Stream #\d+:\d+.*?: Audio: ", RegexOptions.Compiled);

        private readonly string _ffmpeg;

        public FFmpegVideoDecoder(string? ffmpegPath = null) => _ffmpeg = ffmpegPath ?? FfmpegPath;

        public string Name => "ffmpeg.exe";

        public bool IsAvailable => File.Exists(_ffmpeg);

        public IVideoStream? Open(string path)
        {
            try
            {
                if (!IsAvailable || !File.Exists(path)) return null;

                string banner = Probe(path);
                Match v = VideoRx.Match(banner);
                if (!v.Success) return null;
                int w = int.Parse(v.Groups[1].Value, CultureInfo.InvariantCulture);
                int h = int.Parse(v.Groups[2].Value, CultureInfo.InvariantCulture);

                // Keep frames to a sane upload size; WP7 movies are WVGA, iPhone ports up to 960x640.
                if (w > 1280)
                {
                    h = (int) Math.Round(h * 1280.0 / w);
                    w = 1280;
                }
                w &= ~1;
                h &= ~1;
                if (w <= 0 || h <= 0) return null;

                double fps = 30;
                Match f = FpsRx.Match(banner);
                if (f.Success && double.TryParse(f.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && parsed >= 5 && parsed <= 120)
                {
                    fps = parsed;
                }

                TimeSpan duration = TimeSpan.Zero;
                Match d = DurationRx.Match(banner);
                if (d.Success)
                {
                    duration = new TimeSpan(0, int.Parse(d.Groups[1].Value), int.Parse(d.Groups[2].Value), 0)
                        + TimeSpan.FromSeconds(double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture));
                }

                bool hasAudio = AudioRx.IsMatch(banner);
                return new Stream(_ffmpeg, path, w, h, fps, duration, hasAudio);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[wpr-video] ffmpeg could not open " + path + ": " + ex.Message);
                return null;
            }
        }

        private string Probe(string path)
        {
            ProcessStartInfo start = NewStart();
            start.ArgumentList.Add("-hide_banner");
            start.ArgumentList.Add("-nostdin");
            start.ArgumentList.Add("-i");
            start.ArgumentList.Add(path);
            using Process p = Process.Start(start)!;
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            return err;
        }

        private ProcessStartInfo NewStart() => new(_ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
        };

        private sealed class Stream : IVideoStream
        {
            private const int MaxQueuedFrames = 6;
            private const int AudioRate = 44100;
            private const int AudioChannelsOut = 2;
            private const int MaxQueuedAudioBytes = AudioRate * AudioChannelsOut * 2 * 2; // 2 s

            private readonly double _fps;
            private readonly int _frameBytes;
            private readonly Process? _video;
            private readonly Process? _audio;
            private readonly Thread _videoThread;
            private readonly Thread? _audioThread;

            private readonly object _gate = new();
            private readonly Queue<byte[]> _frames = new();
            private readonly Stack<byte[]> _spare = new();
            private long _framesDequeued;
            private bool _videoDone;

            private readonly Queue<byte[]> _pcm = new();
            private int _pcmQueued;
            private int _pcmHeadOffset;
            private bool _audioDone;
            private volatile bool _disposed;

            public int Width { get; }
            public int Height { get; }
            public TimeSpan Duration { get; }
            public int AudioChannels { get; }
            public int AudioSampleRate { get; }

            public Stream(string ffmpeg, string path, int w, int h, double fps, TimeSpan duration, bool hasAudio)
            {
                Width = w;
                Height = h;
                Duration = duration;
                _fps = fps;
                _frameBytes = w * h * 4;

                _video = Start(ffmpeg, "-hide_banner", "-nostdin", "-loglevel", "error", "-i", path,
                    "-map", "0:v:0", "-an", "-vf", $"scale={w}:{h}",
                    "-r", fps.ToString("0.###", CultureInfo.InvariantCulture),
                    "-pix_fmt", "rgba", "-f", "rawvideo", "pipe:1");
                _videoThread = new Thread(ReadVideo) { IsBackground = true, Name = "WPR video frames" };
                _videoThread.Start();

                if (hasAudio)
                {
                    _audio = Start(ffmpeg, "-hide_banner", "-nostdin", "-loglevel", "error", "-i", path,
                        "-map", "0:a:0", "-vn", "-ac", "2", "-ar", AudioRate.ToString(CultureInfo.InvariantCulture),
                        "-f", "s16le", "pipe:1");
                    AudioChannels = AudioChannelsOut;
                    AudioSampleRate = AudioRate;
                    _audioThread = new Thread(ReadAudioPipe) { IsBackground = true, Name = "WPR video audio" };
                    _audioThread.Start();
                }
                else
                {
                    _audioDone = true;
                }

                Trace.WriteLine($"[wpr-video] ffmpeg: {Path.GetFileName(path)} {w}x{h} @ {fps:0.##} fps, {duration.TotalSeconds:0.0} s, audio={(hasAudio ? "yes" : "no")}");
            }

            private static Process Start(string ffmpeg, params string[] args)
            {
                ProcessStartInfo start = new(ffmpeg)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (string a in args) start.ArgumentList.Add(a);
                Process p = Process.Start(start)!;
                // Drain stderr so a chatty decode can never block on a full pipe.
                p.ErrorDataReceived += (_, _) => { };
                p.BeginErrorReadLine();
                return p;
            }

            private void ReadVideo()
            {
                try
                {
                    System.IO.Stream src = _video!.StandardOutput.BaseStream;
                    while (!_disposed)
                    {
                        byte[] frame;
                        lock (_gate)
                        {
                            while (_frames.Count >= MaxQueuedFrames && !_disposed) Monitor.Wait(_gate, 100);
                            if (_disposed) return;
                            frame = _spare.Count > 0 ? _spare.Pop() : new byte[_frameBytes];
                        }
                        if (!ReadExactly(src, frame)) break;
                        lock (_gate)
                        {
                            _frames.Enqueue(frame);
                        }
                    }
                }
                catch (Exception ex) when (!_disposed)
                {
                    Trace.WriteLine("[wpr-video] frame pipe failed: " + ex.Message);
                }
                finally
                {
                    lock (_gate) _videoDone = true;
                }
            }

            private void ReadAudioPipe()
            {
                try
                {
                    System.IO.Stream src = _audio!.StandardOutput.BaseStream;
                    while (!_disposed)
                    {
                        lock (_gate)
                        {
                            while (_pcmQueued >= MaxQueuedAudioBytes && !_disposed) Monitor.Wait(_gate, 100);
                        }
                        byte[] chunk = new byte[16384];
                        int n = src.Read(chunk, 0, chunk.Length);
                        if (n <= 0) break;
                        if (n < chunk.Length) Array.Resize(ref chunk, n);
                        lock (_gate)
                        {
                            _pcm.Enqueue(chunk);
                            _pcmQueued += n;
                        }
                    }
                }
                catch (Exception ex) when (!_disposed)
                {
                    Trace.WriteLine("[wpr-video] audio pipe failed: " + ex.Message);
                }
                finally
                {
                    lock (_gate) _audioDone = true;
                }
            }

            private static bool ReadExactly(System.IO.Stream s, byte[] buf)
            {
                int read = 0;
                while (read < buf.Length)
                {
                    int n = s.Read(buf, read, buf.Length - read);
                    if (n <= 0) return false;
                    read += n;
                }
                return true;
            }

            public bool VideoEnded
            {
                get { lock (_gate) return _videoDone && _frames.Count == 0; }
            }

            public bool TryGetFrame(TimeSpan position, byte[] rgba)
            {
                lock (_gate)
                {
                    // Frame i is due at i / fps. Skip any that are already late, keep the newest due.
                    byte[]? latest = null;
                    while (_frames.Count > 0 && _framesDequeued / _fps <= position.TotalSeconds)
                    {
                        if (latest != null) _spare.Push(latest);
                        latest = _frames.Dequeue();
                        _framesDequeued++;
                    }
                    if (latest == null) return false;
                    Buffer.BlockCopy(latest, 0, rgba, 0, Math.Min(rgba.Length, latest.Length));
                    _spare.Push(latest);
                    Monitor.PulseAll(_gate);
                    return true;
                }
            }

            public int ReadAudio(byte[] buffer, int offset, int count)
            {
                lock (_gate)
                {
                    if (_pcm.Count == 0) return _audioDone ? -1 : 0;
                    int written = 0;
                    while (written < count && _pcm.Count > 0)
                    {
                        byte[] head = _pcm.Peek();
                        int n = Math.Min(count - written, head.Length - _pcmHeadOffset);
                        Buffer.BlockCopy(head, _pcmHeadOffset, buffer, offset + written, n);
                        written += n;
                        _pcmHeadOffset += n;
                        if (_pcmHeadOffset == head.Length)
                        {
                            _pcm.Dequeue();
                            _pcmHeadOffset = 0;
                        }
                    }
                    _pcmQueued -= written;
                    Monitor.PulseAll(_gate);
                    return written;
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                lock (_gate) Monitor.PulseAll(_gate);
                Kill(_video);
                Kill(_audio);
                _videoThread.Join(1000);
                _audioThread?.Join(1000);
                _video?.Dispose();
                _audio?.Dispose();
            }

            private static void Kill(Process? p)
            {
                try
                {
                    if (p != null && !p.HasExited) p.Kill();
                }
                catch (Exception)
                {
                }
            }
        }
    }
}

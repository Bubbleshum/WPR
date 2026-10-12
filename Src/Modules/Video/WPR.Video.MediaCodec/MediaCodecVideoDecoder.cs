using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Android.Graphics;
using Android.Media;
using WPR.Engine.Audio;
using Trace = System.Diagnostics.Trace;

namespace WPR.Video.MediaCodec
{
    /// <summary>
    /// Android <see cref="IVideoDecoder"/>. Reads the sidecars the installer wrote beside a movie
    /// (<see cref="VideoSidecars"/>): the picture through <c>MediaExtractor</c> + <c>MediaCodec</c>,
    /// the sound through NVorbis. An MP4 without sidecars (installed before they existed) is tried
    /// as is, since MediaCodec reads WP7's H.264 MP4s; a WMV without them cannot play, and a
    /// repatch writes them.
    ///
    /// <para><c>[wpr-video]</c> lines go through <c>Trace</c>, which reaches the session log.</para>
    /// </summary>
    public sealed class MediaCodecVideoDecoder : IVideoDecoder
    {
        public string Name => "MediaCodec";

        public bool IsAvailable => true;

        public IVideoStream? Open(string path)
        {
            string video = VideoSidecars.VideoPath(path);
            string? audio = VideoSidecars.AudioPath(path);
            if (!File.Exists(video))
            {
                string ext = System.IO.Path.GetExtension(path);
                if (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase))
                {
                    video = path;
                    audio = null;
                }
                else
                {
                    Trace.WriteLine("[wpr-video] no sidecar for " + path + " - repatch the game to prepare its movies");
                    return null;
                }
            }
            if (audio != null && !File.Exists(audio)) audio = null;

            try
            {
                return MediaCodecStream.Open(video, audio);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[wpr-video] MediaCodec could not open " + video + ": " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }
    }

    internal sealed class MediaCodecStream : IVideoStream
    {
        private const int MaxQueuedFrames = 4;
        private const long DequeueTimeoutUs = 10_000;

        private readonly MediaExtractor _extractor;
        private readonly Android.Media.MediaCodec _codec;
        private readonly Thread _thread;
        private readonly int _frameBytes;

        private readonly object _gate = new();
        private readonly Queue<(long Us, byte[] Rgba)> _frames = new();
        private readonly Stack<byte[]> _spare = new();
        private bool _videoDone;
        private volatile bool _disposed;

        private NVorbis.VorbisReader? _vorbis;
        private float[] _floats = Array.Empty<float>();
        private bool _audioDone;

        public int Width { get; }
        public int Height { get; }
        public TimeSpan Duration { get; }
        public int AudioChannels { get; }
        public int AudioSampleRate { get; }

        private MediaCodecStream(MediaExtractor extractor, Android.Media.MediaCodec codec, int w, int h, TimeSpan duration, NVorbis.VorbisReader? vorbis)
        {
            _extractor = extractor;
            _codec = codec;
            Width = w;
            Height = h;
            Duration = duration;
            _frameBytes = w * h * 4;
            _vorbis = vorbis;
            if (vorbis != null)
            {
                AudioChannels = Math.Min(2, vorbis.Channels);
                AudioSampleRate = vorbis.SampleRate;
            }
            else
            {
                _audioDone = true;
            }
            _thread = new Thread(DecodeLoop) { IsBackground = true, Name = "WPR MediaCodec video" };
            _thread.Start();
        }

        public static MediaCodecStream? Open(string videoPath, string? audioPath)
        {
            var extractor = new MediaExtractor();
            try
            {
                extractor.SetDataSource(videoPath);
                int track = -1;
                MediaFormat? format = null;
                for (int i = 0; i < extractor.TrackCount; i++)
                {
                    MediaFormat f = extractor.GetTrackFormat(i);
                    string? mime = f.GetString(MediaFormat.KeyMime);
                    if (mime != null && mime.StartsWith("video/", StringComparison.Ordinal))
                    {
                        track = i;
                        format = f;
                        break;
                    }
                    f.Dispose();
                }
                if (track < 0 || format == null)
                {
                    extractor.Release();
                    extractor.Dispose();
                    return null;
                }

                extractor.SelectTrack(track);
                string mimeType = format.GetString(MediaFormat.KeyMime)!;
                int w = format.GetInteger(MediaFormat.KeyWidth);
                int h = format.GetInteger(MediaFormat.KeyHeight);
                TimeSpan duration = format.ContainsKey(MediaFormat.KeyDuration)
                    ? TimeSpan.FromTicks(format.GetLong(MediaFormat.KeyDuration) * 10)
                    : TimeSpan.Zero;
                // Ask for the flexible YUV layout, which GetOutputImage can always describe.
                format.SetInteger(MediaFormat.KeyColorFormat, (int) MediaCodecCapabilities.Formatyuv420flexible);

                Android.Media.MediaCodec codec = Android.Media.MediaCodec.CreateDecoderByType(mimeType);
                codec.Configure(format, null, null, MediaCodecConfigFlags.None);
                codec.Start();
                format.Dispose();

                NVorbis.VorbisReader? vorbis = null;
                if (audioPath != null)
                {
                    try { vorbis = new NVorbis.VorbisReader(audioPath); }
                    catch (Exception ex) { Trace.WriteLine("[wpr-video] no sound from " + audioPath + ": " + ex.Message); }
                }

                Trace.WriteLine($"[wpr-video] MediaCodec: {System.IO.Path.GetFileName(videoPath)} {mimeType} {w}x{h}, {duration.TotalSeconds:0.0} s, audio={(vorbis != null ? "yes" : "no")}");
                return new MediaCodecStream(extractor, codec, w, h, duration, vorbis);
            }
            catch
            {
                try { extractor.Release(); } catch { }
                extractor.Dispose();
                throw;
            }
        }

        private void DecodeLoop()
        {
            bool inputDone = false;
            using var info = new Android.Media.MediaCodec.BufferInfo();
            try
            {
                while (!_disposed)
                {
                    if (!inputDone)
                    {
                        int inIndex = _codec.DequeueInputBuffer(DequeueTimeoutUs);
                        if (inIndex >= 0)
                        {
                            using Java.Nio.ByteBuffer? input = _codec.GetInputBuffer(inIndex);
                            int size = input == null ? -1 : _extractor.ReadSampleData(input, 0);
                            if (size < 0)
                            {
                                _codec.QueueInputBuffer(inIndex, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                                inputDone = true;
                            }
                            else
                            {
                                _codec.QueueInputBuffer(inIndex, 0, size, _extractor.SampleTime, MediaCodecBufferFlags.None);
                                _extractor.Advance();
                            }
                        }
                    }

                    lock (_gate)
                    {
                        while (_frames.Count >= MaxQueuedFrames && !_disposed) Monitor.Wait(_gate, 50);
                    }
                    if (_disposed) break;

                    int outIndex = _codec.DequeueOutputBuffer(info, DequeueTimeoutUs);
                    if (outIndex < 0) continue;   // try again later, format changed, buffers changed

                    bool end = (info.Flags & MediaCodecBufferFlags.EndOfStream) != 0;
                    if (info.Size > 0)
                    {
                        byte[] rgba;
                        lock (_gate) rgba = _spare.Count > 0 ? _spare.Pop() : new byte[_frameBytes];
                        bool ok;
                        // Close() hands the image back to the codec; Dispose() only drops the JNI
                        // reference. Both, every frame.
                        using (Image? image = _codec.GetOutputImage(outIndex))
                        {
                            try
                            {
                                ok = image != null && Convert(image, rgba);
                            }
                            finally
                            {
                                image?.Close();
                            }
                        }
                        if (ok)
                        {
                            lock (_gate) _frames.Enqueue((info.PresentationTimeUs, rgba));
                        }
                    }
                    _codec.ReleaseOutputBuffer(outIndex, false);
                    if (end) break;
                }
            }
            catch (Exception ex) when (!_disposed)
            {
                Trace.WriteLine("[wpr-video] MediaCodec decode failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                lock (_gate) _videoDone = true;
            }
        }

        /// <summary>
        /// YUV_420_888 to RGBA (BT.601, video range — what WP7's encoders wrote). Each plane is
        /// read through its direct buffer address, and every Java object touched is disposed
        /// here: a plane or buffer wrapper owns a JNI reference, and leaking one per frame is the
        /// accelerometer bug of 2026-09-05 over again.
        /// </summary>
        private unsafe bool Convert(Image image, byte[] rgba)
        {
            if (image.Format != ImageFormatType.Yuv420888) return false;
            Image.Plane[]? planes = image.GetPlanes();
            if (planes == null || planes.Length < 3) return false;
            try
            {
                Rect crop = image.CropRect!;
                int cw = Math.Min(Width, crop.Width());
                int ch = Math.Min(Height, crop.Height());
                int left = crop.Left, top = crop.Top;
                crop.Dispose();

                using Java.Nio.ByteBuffer yBuf = planes[0].Buffer!;
                using Java.Nio.ByteBuffer uBuf = planes[1].Buffer!;
                using Java.Nio.ByteBuffer vBuf = planes[2].Buffer!;
                byte* y = (byte*) yBuf.GetDirectBufferAddress().ToPointer();
                byte* u = (byte*) uBuf.GetDirectBufferAddress().ToPointer();
                byte* v = (byte*) vBuf.GetDirectBufferAddress().ToPointer();
                if (y == null || u == null || v == null) return false;

                int yRow = planes[0].RowStride, yPix = planes[0].PixelStride;
                int uRow = planes[1].RowStride, uPix = planes[1].PixelStride;
                int vRow = planes[2].RowStride, vPix = planes[2].PixelStride;

                fixed (byte* dst0 = rgba)
                {
                    for (int row = 0; row < ch; row++)
                    {
                        byte* yr = y + (top + row) * yRow + left * yPix;
                        int cr = (top + row) >> 1;
                        byte* ur = u + cr * uRow + (left >> 1) * uPix;
                        byte* vr = v + cr * vRow + (left >> 1) * vPix;
                        byte* d = dst0 + row * Width * 4;
                        for (int col = 0; col < cw; col++)
                        {
                            int c = yr[col * yPix] - 16;
                            int du = ur[(col >> 1) * uPix] - 128;
                            int ev = vr[(col >> 1) * vPix] - 128;
                            int k = 298 * c + 128;
                            int r = (k + 409 * ev) >> 8;
                            int g = (k - 100 * du - 208 * ev) >> 8;
                            int b = (k + 516 * du) >> 8;
                            d[0] = (byte) (r < 0 ? 0 : r > 255 ? 255 : r);
                            d[1] = (byte) (g < 0 ? 0 : g > 255 ? 255 : g);
                            d[2] = (byte) (b < 0 ? 0 : b > 255 ? 255 : b);
                            d[3] = 255;
                            d += 4;
                        }
                    }
                }
                return true;
            }
            finally
            {
                foreach (Image.Plane p in planes) p.Dispose();
            }
        }

        public bool VideoEnded
        {
            get { lock (_gate) return _videoDone && _frames.Count == 0; }
        }

        public bool TryGetFrame(TimeSpan position, byte[] rgba)
        {
            long nowUs = position.Ticks / 10;
            lock (_gate)
            {
                byte[]? latest = null;
                while (_frames.Count > 0 && _frames.Peek().Us <= nowUs)
                {
                    if (latest != null) _spare.Push(latest);
                    latest = _frames.Dequeue().Rgba;
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
            if (_audioDone || _vorbis == null) return -1;
            int channelsIn = _vorbis.Channels;
            int frames = count / (AudioChannels * 2);
            if (frames <= 0) return 0;
            int need = frames * channelsIn;
            if (_floats.Length < need) _floats = new float[need];
            int got;
            try
            {
                got = _vorbis.ReadSamples(_floats, 0, need);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[wpr-video] audio decode failed: " + ex.Message);
                got = 0;
            }
            int gotFrames = got / channelsIn;
            if (gotFrames <= 0)
            {
                _audioDone = true;
                return -1;
            }
            int o = offset;
            for (int f = 0; f < gotFrames; f++)
            {
                for (int c = 0; c < AudioChannels; c++)
                {
                    float s = _floats[f * channelsIn + Math.Min(c, channelsIn - 1)];
                    short v = (short) Math.Clamp((int) (s * 32767f), short.MinValue, short.MaxValue);
                    buffer[o++] = (byte) v;
                    buffer[o++] = (byte) (v >> 8);
                }
            }
            return o - offset;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_gate) Monitor.PulseAll(_gate);
            _thread.Join(2000);
            try { _codec.Stop(); } catch { }
            try { _codec.Release(); } catch { }
            _codec.Dispose();
            try { _extractor.Release(); } catch { }
            _extractor.Dispose();
            _vorbis?.Dispose();
            _vorbis = null;
        }
    }
}

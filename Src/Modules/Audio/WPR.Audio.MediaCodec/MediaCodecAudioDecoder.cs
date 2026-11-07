using System;
using System.Buffers.Binary;
using System.IO;
using Android.Media;
using WPR.Engine.Audio;
using Trace = System.Diagnostics.Trace;

namespace WPR.Audio.MediaCodec
{
    /// <summary>
    /// Android <see cref="IAudioDecoder"/>: <c>MediaExtractor</c> + <c>MediaCodec</c>, the codecs the
    /// device already has, decoding a whole source to interleaved 16-bit PCM in the game process.
    /// </summary>
    /// <remarks>
    /// <para>What it is for: WP8 titles that hand the Media Foundation media engine compressed audio -
    /// Assassin's Creed Pirates' title music, Doodle God Blitz, FIFA, the GameMaker and Unity runners
    /// (all MP3). Without a decoder the runtime answers MF_MEDIA_ENGINE_ERR_SRC_NOT_SUPPORTED, as a
    /// codec-less phone would, and the game plays silence. The desktop's equivalent is ffmpeg.exe.</para>
    /// <para>The bytes go through a temporary file rather than a <c>MediaDataSource</c>: that keeps the
    /// extractor on its own well-trodden path, and a source is decoded once, as it is loaded.</para>
    /// <para>A container with a video stream is reported (<see cref="DecodedPcm.HasVideo"/>) and its
    /// picture never decoded - the runtime refuses such sources on purpose; see CLAUDE.md "WP8 media".</para>
    /// <para><c>[wpr-adec]</c> lines go through <c>Trace</c>, which reaches the session log.</para>
    /// </remarks>
    public sealed class MediaCodecAudioDecoder : IAudioDecoder
    {
        private const long DequeueTimeoutUs = 10_000;

        /// <summary>A source that has not finished by then is abandoned, so a wedged codec cannot hang a load.</summary>
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

        public string Name => "MediaCodec";

        public bool IsAvailable => true;

        public bool CanDecode(string mimeType) => mimeType switch
        {
            "audio/mpeg" or "audio/mp3" or "audio/mpeg3" or "audio/x-mpeg" or "audio/x-mp3" => true,
            "audio/mp4" or "audio/x-m4a" or "audio/m4a" or "audio/aac" or "audio/aacp" or "audio/x-aac" or "audio/3gpp" => true,
            "audio/ogg" or "audio/vorbis" or "audio/flac" or "audio/x-flac" or "audio/amr" => true,
            // No WMA: Android ships no WMA decoder.
            _ => false,
        };

        public DecodedPcm? Decode(byte[] data, string? nameHint)
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wpr-adec-" + Guid.NewGuid().ToString("N") + Extension(nameHint));
            try
            {
                File.WriteAllBytes(path, data);
                return DecodeFile(path, nameHint);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[wpr-adec] " + (nameHint ?? "source") + ": " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        /// <summary>The hint's extension helps the extractor sniff; content still decides.</summary>
        private static string Extension(string? nameHint)
        {
            string ext = nameHint is null ? "" : System.IO.Path.GetExtension(nameHint);
            return ext.Length is > 1 and <= 6 ? ext : ".bin";
        }

        private static DecodedPcm? DecodeFile(string path, string? nameHint)
        {
            using var extractor = new MediaExtractor();
            Android.Media.MediaCodec? codec = null;
            try
            {
                extractor.SetDataSource(path);
                int track = -1;
                bool hasVideo = false;
                MediaFormat? format = null;
                for (int i = 0; i < extractor.TrackCount; i++)
                {
                    MediaFormat f = extractor.GetTrackFormat(i);
                    string? mime = f.GetString(MediaFormat.KeyMime);
                    if (mime != null && mime.StartsWith("video/", StringComparison.Ordinal))
                    {
                        hasVideo = true;
                    }

                    if (track < 0 && mime != null && mime.StartsWith("audio/", StringComparison.Ordinal))
                    {
                        track = i;
                        format = f;
                        continue;
                    }

                    f.Dispose();
                }

                if (track < 0 || format is null)
                {
                    Trace.WriteLine("[wpr-adec] " + (nameHint ?? "source") + ": no audio track");
                    return null;
                }

                string audioMime = format.GetString(MediaFormat.KeyMime)!;
                int rate = format.GetInteger(MediaFormat.KeySampleRate);
                int channels = format.GetInteger(MediaFormat.KeyChannelCount);
                extractor.SelectTrack(track);

                codec = Android.Media.MediaCodec.CreateDecoderByType(audioMime);
                codec.Configure(format, null, null, MediaCodecConfigFlags.None);
                codec.Start();

                // 16-bit unless the output format says otherwise (KEY_PCM_ENCODING, API 24+).
                int encoding = 2;   // AudioFormat.ENCODING_PCM_16BIT

                // One buffer, sized up front from the track's duration, that every output
                // buffer is appended to. A MemoryStream doubled its way up (up to twice the track), then
                // ToArray and Finish's sample arrays copied it three more times: a 3-minute
                // stereo MP3 is ~32 MB of PCM, so its decode peaked well over 100 MB on the
                // large-object heap. Now the peak is this buffer plus one exact-length copy.
                long durationUs = format.ContainsKey(MediaFormat.KeyDuration) ? format.GetLong(MediaFormat.KeyDuration) : 0;
                byte[] pcm = new byte[EstimateBytes(durationUs, rate, channels)];
                int pcmLength = 0;
                byte[] chunk = Array.Empty<byte>();
                using var info = new Android.Media.MediaCodec.BufferInfo();
                bool inputDone = false;
                DateTime giveUp = DateTime.UtcNow + Deadline;
                while (DateTime.UtcNow < giveUp)
                {
                    if (!inputDone)
                    {
                        int inIndex = codec.DequeueInputBuffer(DequeueTimeoutUs);
                        if (inIndex >= 0)
                        {
                            using Java.Nio.ByteBuffer? input = codec.GetInputBuffer(inIndex);
                            int size = input is null ? -1 : extractor.ReadSampleData(input, 0);
                            if (size < 0)
                            {
                                codec.QueueInputBuffer(inIndex, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                                inputDone = true;
                            }
                            else
                            {
                                codec.QueueInputBuffer(inIndex, 0, size, extractor.SampleTime, MediaCodecBufferFlags.None);
                                extractor.Advance();
                            }
                        }
                    }

                    int outIndex = codec.DequeueOutputBuffer(info, DequeueTimeoutUs);
                    if (outIndex == (int)MediaCodecInfoState.OutputFormatChanged)
                    {
                        using MediaFormat changed = codec.OutputFormat;
                        rate = changed.GetInteger(MediaFormat.KeySampleRate);
                        channels = changed.GetInteger(MediaFormat.KeyChannelCount);
                        if (changed.ContainsKey(MediaFormat.KeyPcmEncoding))
                        {
                            encoding = changed.GetInteger(MediaFormat.KeyPcmEncoding);
                        }

                        continue;
                    }

                    if (outIndex < 0)
                    {
                        continue;   // try again later
                    }

                    bool end = (info.Flags & MediaCodecBufferFlags.EndOfStream) != 0;
                    if (info.Size > 0)
                    {
                        using Java.Nio.ByteBuffer? output = codec.GetOutputBuffer(outIndex);
                        if (output != null)
                        {
                            if (pcmLength + info.Size > pcm.Length)
                            {
                                // The duration was short or missing (or the output is float):
                                // grow by half again, not double.
                                long grown = Math.Max((long)pcmLength + info.Size, pcm.Length + (pcm.Length / 2L));
                                Array.Resize(ref pcm, (int)Math.Min(grown, Array.MaxLength));
                            }

                            // Through a small reused chunk, never straight into pcm: the binding
                            // marshals the WHOLE managed array across JNI on every Get, so a
                            // direct read would copy the growing track once per output buffer.
                            if (chunk.Length < info.Size)
                            {
                                chunk = new byte[info.Size];
                            }

                            output.Position(info.Offset);
                            output.Get(chunk, 0, info.Size);
                            Buffer.BlockCopy(chunk, 0, pcm, pcmLength, info.Size);
                            pcmLength += info.Size;
                        }
                    }

                    codec.ReleaseOutputBuffer(outIndex, false);
                    if (end)
                    {
                        return Finish(pcm, pcmLength, rate, channels, encoding, hasVideo, nameHint);
                    }
                }

                Trace.WriteLine("[wpr-adec] " + (nameHint ?? "source") + ": gave up after " + Deadline.TotalSeconds + " s");
                return null;
            }
            finally
            {
                if (codec != null)
                {
                    try { codec.Stop(); } catch { }
                    codec.Release();
                    codec.Dispose();
                }

                extractor.Release();
            }
        }

        /// <summary>
        /// Bytes for <paramref name="durationUs"/> of 16-bit PCM, plus a little, as the decode
        /// buffer's first size. 1 MB when the container gives no duration; never more than 256 MB
        /// up front (a longer track grows into it).
        /// </summary>
        private static int EstimateBytes(long durationUs, int rate, int channels)
        {
            if (durationUs <= 0 || rate <= 0 || channels <= 0)
            {
                return 1024 * 1024;
            }

            double bytes = (durationUs / 1_000_000.0 * rate * channels * 2) + (64 * 1024);
            return (int)Math.Clamp(bytes, 64 * 1024, 256.0 * 1024 * 1024);
        }

        /// <summary>
        /// To 16-bit, at most two channels (the runtime's voices are mono or stereo), converted in
        /// place: each output sample is written at or before the position its input is read
        /// from, so the decode buffer is reused and only the exact-length result is new.
        /// </summary>
        private static DecodedPcm? Finish(byte[] raw, int length, int rate, int channels, int encoding, bool hasVideo, string? nameHint)
        {
            if (rate <= 0 || channels <= 0)
            {
                return null;
            }

            bool isFloat = encoding == 4;   // AudioFormat.ENCODING_PCM_FLOAT; anything else is read as 16-bit
            int bytesPerSample = isFloat ? 4 : 2;
            int keep = Math.Min(channels, 2);
            int frames = length / bytesPerSample / channels;
            int written = frames * keep * 2;

            if (isFloat || channels > 2)
            {
                Span<byte> pcm = raw.AsSpan(0, length);
                int at = 0;
                for (int f = 0; f < frames; f++)
                {
                    for (int c = 0; c < keep; c++)
                    {
                        int from = ((f * channels) + c) * bytesPerSample;
                        short s = isFloat
                            ? (short)Math.Clamp((int)MathF.Round(BinaryPrimitives.ReadSingleLittleEndian(pcm.Slice(from)) * 32767f), short.MinValue, short.MaxValue)
                            : BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(from));
                        BinaryPrimitives.WriteInt16LittleEndian(pcm.Slice(at), s);
                        at += 2;
                    }
                }
            }

            // 16-bit mono or stereo is already the output layout: nothing to convert.
            byte[] pcm16 = written == raw.Length ? raw : raw.AsSpan(0, written).ToArray();

            Trace.WriteLine($"[wpr-adec] {nameHint ?? "source"}: {rate} Hz x{keep}, {frames} frames" + (hasVideo ? " (video stream not decoded)" : ""));
            return new DecodedPcm(rate, keep, pcm16, hasVideo);
        }
    }
}

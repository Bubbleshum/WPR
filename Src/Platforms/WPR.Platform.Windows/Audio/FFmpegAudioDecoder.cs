using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WPR.Engine.Audio;

namespace WPR.Platform.Windows.Audio
{
    /// <summary>
    /// Windows <see cref="IAudioDecoder"/>: the <c>ffmpeg.exe</c> this head already ships for
    /// install-time transcoding (<see cref="FFMpegCoreAudioTranscoder"/>), run once per source to
    /// decode it to 16-bit PCM WAVE on its stdout.
    ///
    /// <para>The input goes through a temporary file rather than stdin because MP4/M4A put their
    /// index (<c>moov</c>) wherever the muxer left it - often at the end - and ffmpeg needs to seek
    /// to it. <c>aformat=channel_layouts=mono|stereo</c> downmixes anything wider, since the
    /// runtime's voices are one or two channels. A video stream is reported, not decoded; cover art
    /// in an MP3 (<c>(attached pic)</c>) is not video.</para>
    ///
    /// <para>Desktop only, like the transcoder: an APK has no executable to spawn, and ffmpeg-kit
    /// cannot run in the game process (see the <c>:transcode</c> service). An Android decoder would
    /// be MediaExtractor + MediaCodec.</para>
    /// </summary>
    public sealed class FFmpegAudioDecoder : IAudioDecoder
    {
        private static readonly string FfmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");

        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

        private static readonly Regex VideoStream = new(@"Stream #\d+:\d+.*: Video: (?!.*attached pic)", RegexOptions.Compiled);

        /// <param name="ffmpegPath">Where ffmpeg.exe is; by default beside this assembly.</param>
        public FFmpegAudioDecoder(string? ffmpegPath = null) => _ffmpeg = ffmpegPath ?? FfmpegPath;

        private readonly string _ffmpeg;

        public string Name => "ffmpeg.exe";

        public bool IsAvailable => File.Exists(_ffmpeg);

        public bool CanDecode(string mimeType) => mimeType switch
        {
            "audio/mpeg" or "audio/mp3" or "audio/mpeg3" or "audio/x-mpeg" or "audio/x-mp3" => true,
            "audio/mp4" or "audio/x-m4a" or "audio/m4a" or "audio/aac" or "audio/aacp" or "audio/x-aac" or "audio/3gpp" => true,
            "audio/x-ms-wma" or "audio/wma" or "audio/ogg" or "audio/vorbis" or "audio/flac" or "audio/x-flac" or "audio/amr" => true,
            _ => false,
        };

        public DecodedPcm? Decode(byte[] data, string? nameHint)
        {
            if (!IsAvailable || data.Length == 0)
            {
                return null;
            }

            string input = Path.Combine(Path.GetTempPath(), $"wpr-decode-{Guid.NewGuid():N}.bin");
            try
            {
                File.WriteAllBytes(input, data);
                ProcessStartInfo start = new(_ffmpeg)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = false,
                };
                foreach (string arg in new[]
                         {
                             "-hide_banner", "-nostdin", "-i", input, "-map", "0:a:0", "-vn",
                             "-af", "aformat=channel_layouts=mono|stereo", "-acodec", "pcm_s16le", "-f", "wav", "pipe:1",
                         })
                {
                    start.ArgumentList.Add(arg);
                }

                using Process process = Process.Start(start)!;
                Task<string> errors = process.StandardError.ReadToEndAsync();
                using MemoryStream wav = new();
                Task copy = process.StandardOutput.BaseStream.CopyToAsync(wav);
                if (!process.WaitForExit(Timeout))
                {
                    process.Kill(entireProcessTree: true);
                    return null;
                }

                copy.Wait(Timeout);
                bool hasVideo = VideoStream.IsMatch(errors.Result);
                return process.ExitCode == 0 ? ParseWave(wav.ToArray(), hasVideo) : null;
            }
            catch (Exception)
            {
                // A decoder costs the source, never the game: the media engine reports it unplayable.
                return null;
            }
            finally
            {
                try
                {
                    File.Delete(input);
                }
                catch (IOException)
                {
                }
            }
        }

        /// <summary>A piped WAVE: its sizes are placeholders, so the data runs to the end.</summary>
        private static DecodedPcm? ParseWave(byte[] wav, bool hasVideo)
        {
            if (wav.Length < 12 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
            {
                return null;
            }

            int channels = 0, rate = 0, at = 12;
            while (at + 8 <= wav.Length)
            {
                string id = Encoding.ASCII.GetString(wav, at, 4);
                int size = BitConverter.ToInt32(wav, at + 4);
                if (id == "fmt " && at + 16 <= wav.Length)
                {
                    channels = BitConverter.ToUInt16(wav, at + 10);
                    rate = BitConverter.ToInt32(wav, at + 12);
                }
                else if (id == "data")
                {
                    int length = (wav.Length - (at + 8)) / Math.Max(1, channels * 2) * channels * 2;
                    if (channels is not (1 or 2) || rate <= 0 || length <= 0)
                    {
                        return null;
                    }

                    return new DecodedPcm(rate, channels, wav.AsSpan(at + 8, length).ToArray(), hasVideo);
                }

                if (size < 0 || at + 8L + size > wav.Length)
                {
                    return null;
                }

                at += 8 + size + (size & 1);
            }

            return null;
        }
    }
}

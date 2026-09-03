using System.Security.Cryptography;
using System.Text;

namespace WPR.Wp8Native
{
    /// <summary>
    /// Recovers script text from emulated memory, after the image has decrypted it.
    /// </summary>
    /// <remarks>
    /// Everything under <c>assets/data/scripts/</c> in this title is encrypted - the first
    /// bytes of <c>gamelogic.lua</c> are <c>8e 27 da da</c>, where Lua source would be ASCII
    /// and compiled Lua would be <c>1b 4c 75 61</c>. So the logic that decides when the
    /// loading screen ends cannot be read off disk at all.
    /// <para>
    /// It can be read out of the heap, because the image has to decrypt it to run it. This
    /// walks the used part of the heap looking for two things: the Lua bytecode signature,
    /// and long runs of printable text that read like source. Neither needs to know anything
    /// about the encryption, which is the point - recovering the cipher would be a much larger
    /// job for a strictly worse result.
    /// </para>
    /// <para>
    /// The catch is timing. A buffer decrypted, compiled and freed is only there until the
    /// allocator hands its memory to someone else, so <c>WPR_DUMPLUA=dir:frame</c> takes the
    /// snapshot at a chosen frame rather than at the end of the run.
    /// </para>
    /// </remarks>
    public sealed class ScriptDumper
    {
        /// <summary>ESC "Lua" - the precompiled-chunk signature, any version.</summary>
        private static readonly byte[] BytecodeSignature = [0x1B, 0x4C, 0x75, 0x61];

        /// <summary>How much of a bytecode chunk to keep, having no length to read.</summary>
        private const int BytecodeWindow = 256 * 1024;

        /// <summary>Shortest run of text worth calling a script.</summary>
        private const int MinimumSourceRun = 400;

        /// <summary>Read the heap in pieces; it can be hundreds of megabytes.</summary>
        private const int ChunkSize = 4 * 1024 * 1024;

        /// <summary>Overlap so a signature straddling a chunk boundary is still found.</summary>
        private const int Overlap = 64 * 1024;

        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public List<string> Log { get; } = new();

        public int Written { get; private set; }

        /// <summary>Where to write, and at which frame, from <c>WPR_DUMPLUA=dir[:frame]</c>.</summary>
        public static (string Directory, int Frame, int Every)? Requested { get; } = Parse();

        private static (string, int, int)? Parse()
        {
            string? value = Environment.GetEnvironmentVariable("WPR_DUMPLUA");
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            // Split on the last colon, so a Windows drive letter survives. The frame may
            // carry a "+every" stride, because one snapshot catches almost nothing: a script
            // is decrypted, compiled and freed, and the buffer survives only until the
            // allocator reuses it. Scanning repeatedly through the load is what turns six
            // chunks into a library.
            int colon = value.LastIndexOf(':');
            string tail = colon > 1 ? value[(colon + 1)..] : string.Empty;
            string[] parts = tail.Split('+');

            if (parts.Length >= 1 && int.TryParse(parts[0], out int frame))
            {
                int every = parts.Length == 2 && int.TryParse(parts[1], out int e) && e > 0 ? e : 0;
                return (value[..colon], frame, every);
            }

            return (value, 0, 0);
        }

        /// <summary>How many chunks were caught on their way to being freed.</summary>
        public int CaughtAtFree { get; private set; }

        /// <summary>
        /// Takes one heap block that is about to be freed, if it is a Lua chunk.
        /// </summary>
        /// <remarks>
        /// This is the hook the heap scan could never be. A script is decrypted into a buffer,
        /// handed to lua_load, and the buffer is released - through this probe's own free stub,
        /// because the CRT is ours. So the moment of release is the one moment every chunk is
        /// guaranteed to be complete, contiguous, and still there, and the allocator knows its
        /// exact size, so there is no 256 KB window and no adjacent-heap noise in what is
        /// written. A scan at any fixed time saw five scripts; this sees the ones that were
        /// compiled and gone between scans, which is all the others.
        /// </remarks>
        public void Capture(ArmEmulator emulator, long address, long size, string directory)
        {
            if (size < 12 || size > 16 * 1024 * 1024)
            {
                return;
            }

            byte[] body;
            try
            {
                body = emulator.ReadMemory(address, (int)size);
            }
            catch (Exception)
            {
                return;
            }

            if (body[0] != 0x1B || body[1] != 0x4C || body[2] != 0x75 || body[3] != 0x61 ||
                body[4] is not (0x51 or 0x52 or 0x53) || body[5] != 0x00)
            {
                return;
            }

            Directory.CreateDirectory(directory);
            CaughtAtFree++;
            Write(body, $"freed-{address:X8}.luac", directory, $"{size:N0} bytes, caught at free");
        }

        /// <summary>
        /// Walks the used heap and writes out everything that looks like a script.
        /// </summary>
        public void Scan(ArmEmulator emulator, string directory)
        {
            Directory.CreateDirectory(directory);

            long used = Math.Max(emulator.HeapUsed, 0);
            Log.Add($"scanning {used / (1024 * 1024)} MB of heap from 0x{ArmEmulator.HeapBaseAddress:X8}");

            for (long offset = 0; offset < used; offset += ChunkSize - Overlap)
            {
                int length = (int)Math.Min(ChunkSize, used - offset);
                if (length <= 0)
                {
                    break;
                }

                byte[] chunk;
                try
                {
                    chunk = emulator.ReadMemory(ArmEmulator.HeapBaseAddress + offset, length);
                }
                catch (Exception)
                {
                    continue;
                }

                ScanForBytecode(chunk, ArmEmulator.HeapBaseAddress + offset, directory);
                ScanForSource(chunk, ArmEmulator.HeapBaseAddress + offset, directory);
            }

            Log.Add($"{Written} script(s) written to {directory}");
        }

        private void ScanForBytecode(byte[] chunk, long baseAddress, string directory)
        {
            for (int i = 0; i + BytecodeSignature.Length + 2 < chunk.Length; i++)
            {
                if (chunk[i] != BytecodeSignature[0] ||
                    chunk[i + 1] != BytecodeSignature[1] ||
                    chunk[i + 2] != BytecodeSignature[2] ||
                    chunk[i + 3] != BytecodeSignature[3])
                {
                    continue;
                }

                // Four bytes of signature turn up in ordinary data often enough to matter.
                // The version byte and the format byte after it are what make it a chunk:
                // 0x51/0x52/0x53 for Lua 5.1 to 5.3, and format 0 for the official one.
                byte version = chunk[i + 4];
                if (version is not (0x51 or 0x52 or 0x53) || chunk[i + 5] != 0x00)
                {
                    continue;
                }

                int length = Math.Min(BytecodeWindow, chunk.Length - i);
                byte[] body = chunk[i..(i + length)];
                Write(body, $"bytecode-{baseAddress + i:X8}.luac", directory,
                    $"Lua 5.{version - 0x50} chunk");
            }
        }

        private void ScanForSource(byte[] chunk, long baseAddress, string directory)
        {
            int start = -1;

            for (int i = 0; i <= chunk.Length; i++)
            {
                bool printable = i < chunk.Length && IsText(chunk[i]);

                if (printable)
                {
                    if (start < 0)
                    {
                        start = i;
                    }

                    continue;
                }

                if (start >= 0 && i - start >= MinimumSourceRun)
                {
                    byte[] body = chunk[start..i];
                    string text = Encoding.Latin1.GetString(body);
                    if (LooksLikeScript(text))
                    {
                        Write(body, $"source-{baseAddress + start:X8}.lua", directory,
                            $"{body.Length} bytes of text");
                    }
                }

                start = -1;
            }
        }

        /// <summary>
        /// Printable ASCII plus the whitespace source is allowed to contain.
        /// </summary>
        private static bool IsText(byte value)
            => value is >= 0x20 and <= 0x7E || value is 0x09 or 0x0A or 0x0D;

        /// <summary>
        /// Whether a run of text reads like Lua rather than like a blob of names.
        /// </summary>
        /// <remarks>
        /// Two keywords rather than one, because a texture atlas is a long run of printable
        /// text too and the word "end" turns up inside plenty of identifiers. Requiring
        /// <c>function</c> plus a second structural keyword is enough to separate them without
        /// needing a parser.
        /// </remarks>
        private static bool LooksLikeScript(string text)
        {
            if (!text.Contains("function", StringComparison.Ordinal))
            {
                return false;
            }

            int marks = 0;
            foreach (string keyword in (string[])["local ", "\nend", " then", "return ", "elseif"])
            {
                if (text.Contains(keyword, StringComparison.Ordinal))
                {
                    marks++;
                }
            }

            return marks >= 2;
        }

        private void Write(byte[] body, string name, string directory, string note)
        {
            // The same buffer turns up in several overlapping chunks, and a script the image
            // keeps resident turns up on every scan. Hash rather than address, so identical
            // content found twice is written once.
            string hash = Convert.ToHexString(MD5.HashData(body))[..12];
            if (!_seen.Add(hash))
            {
                return;
            }

            // The hash goes in the name as well as the dedupe. Scanning repeatedly finds
            // different scripts at the *same* address as the allocator recycles it, and a
            // name built from the address alone silently overwrote them: 58 chunks written,
            // seven files on disk.
            name = $"{Path.GetFileNameWithoutExtension(name)}-{hash}{Path.GetExtension(name)}";

            try
            {
                File.WriteAllBytes(Path.Combine(directory, name), body);
                Written++;
                if (Log.Count < 40)
                {
                    Log.Add($"{name}  {note}");
                }
            }
            catch (Exception ex)
            {
                Log.Add($"{name} failed: {ex.Message}");
            }
        }
    }
}

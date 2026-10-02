using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace WPR.Common
{
    /// <summary>
    /// WPR's own log file: every <see cref="Log"/> line and every <see cref="Trace"/> line, timestamped,
    /// in a small rolling file per process. What a manual report sends as "the last 20 minutes".
    /// </summary>
    /// <remarks>
    /// <para><b>Why it exists.</b> <see cref="Log"/> writes to stdout, which the desktop WinExe discards,
    /// and the per-game <c>wpr_game_debug.log</c> exists in Debug builds only. So a Release build kept
    /// no record of anything, and "send a report" had nothing to send.</para>
    ///
    /// <para><b>One file per process</b> (<c>wpr-launcher.log</c>, <c>wpr-game.log</c>), because
    /// Android runs the launcher and the <c>:game</c> process side by side and two processes appending
    /// to one file interleave torn lines. <see cref="ReadRecent"/> merges them by timestamp.</para>
    ///
    /// <para><b>Bounded</b>: when a file passes <see cref="MaxBytes"/> its older half is dropped, so it
    /// always holds the recent past and never grows without limit. Every write is best effort and
    /// never throws; a log must not be the thing that breaks a launch.</para>
    /// </remarks>
    public static class SessionLog
    {
        public const string FolderName = "Logs";

        private const long MaxBytes = 4 * 1024 * 1024;
        private const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

        /// <summary>U+2028: stands in for a newline inside one entry, so every entry stays one physical line.</summary>
        private const char LineSeparator = (char)0x2028;

        private static readonly object Gate = new object();
        private static string? _path;
        private static StreamWriter? _writer;
        private static SessionLogListener? _listener;

        /// <summary>The folder the log files live in, or null before <see cref="Start"/>.</summary>
        public static string? Directory { get; private set; }

        /// <summary>
        /// Start logging this process to <c>&lt;directory&gt;/wpr-&lt;processTag&gt;.log</c>, and capture
        /// <see cref="Trace"/> output there too. Idempotent; a second call with a different folder
        /// moves the log.
        /// </summary>
        public static void Start(string directory, string processTag)
        {
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, $"wpr-{processTag}.log");

                lock (Gate)
                {
                    if (_path == path && _writer != null) return;

                    _writer?.Dispose();
                    Directory = directory;
                    _path = path;
                    _writer = OpenWriter(path);
                }

                if (_listener == null)
                {
                    _listener = new SessionLogListener();
                    Trace.Listeners.Add(_listener);
                }

                Write("session", $"--- {processTag} process started, pid {Environment.ProcessId}, {Environment.OSVersion} ---");
            }
            catch (Exception)
            {
                // No log is better than no WPR.
            }
        }

        /// <summary>Append one line. Never throws.</summary>
        public static void Write(string category, string message)
        {
            if (_writer == null) return;
            try
            {
                string stamp = DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture);
                // One physical line per entry, so the time filter below never splits an entry.
                string text = message.Replace("\r\n", "\n").Replace('\n', LineSeparator);

                lock (Gate)
                {
                    if (_writer == null) return;
                    _writer.Write(stamp);
                    _writer.Write(" [");
                    _writer.Write(category);
                    _writer.Write("] ");
                    _writer.WriteLine(text);

                    if (_writer.BaseStream.Length > MaxBytes) Trim();
                }
            }
            catch (Exception)
            {
                // Disk full, file deleted underneath us: drop the line.
            }
        }

        /// <summary>
        /// Push this process's buffered lines to disk now. For a process about to be killed, whose
        /// last lines another process is going to read (the "stop and send report" button). Never throws.
        /// </summary>
        public static void Flush()
        {
            try { lock (Gate) { _writer?.Flush(); } }
            catch (Exception) { /* best effort */ }
        }

        /// <summary>
        /// Every line from the last <paramref name="window"/>, across all processes' files, oldest
        /// first, as text. Entries that were multi-line are restored. Empty when there is nothing.
        /// </summary>
        public static string ReadRecent(TimeSpan window)
        {
            string? directory = Directory;
            if (directory == null || !System.IO.Directory.Exists(directory)) return "";

            DateTime since = DateTime.Now - window;
            List<(DateTime At, int File, int Order, string Line)> lines = new List<(DateTime, int, int, string)>();

            lock (Gate) { _writer?.Flush(); }

            string[] files = System.IO.Directory.GetFiles(directory, "wpr-*.log");
            for (int f = 0; f < files.Length; f++)
            {
                string tag = Path.GetFileNameWithoutExtension(files[f]).Substring(4);
                try
                {
                    using FileStream fs = new FileStream(files[f], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using StreamReader reader = new StreamReader(fs, Encoding.UTF8);
                    int order = 0;
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Length < TimeFormat.Length
                            || !DateTime.TryParseExact(line.Substring(0, TimeFormat.Length), TimeFormat,
                                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime at)
                            || at < since)
                        {
                            continue;
                        }
                        string body = line.Substring(TimeFormat.Length).Replace(LineSeparator, '\n');
                        lines.Add((at, f, order++, $"{line.Substring(0, TimeFormat.Length)} {tag}{body}"));
                    }
                }
                catch (IOException)
                {
                    // Being rotated by the other process right now; the rest still counts.
                }
            }

            return string.Join("\n", lines.OrderBy(l => l.At).ThenBy(l => l.File).ThenBy(l => l.Order).Select(l => l.Line));
        }

        private static StreamWriter OpenWriter(string path)
        {
            FileStream fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
        }

        /// <summary>Keep the newer half. Called under <see cref="Gate"/>.</summary>
        private static void Trim()
        {
            if (_path == null || _writer == null) return;
            _writer.Dispose();
            _writer = null;

            byte[] all = File.ReadAllBytes(_path);
            int start = all.Length / 2;
            while (start < all.Length && all[start] != (byte)'\n') start++;
            File.WriteAllBytes(_path, all.AsSpan(Math.Min(start + 1, all.Length)).ToArray());

            _writer = OpenWriter(_path);
        }

        /// <summary>Routes <see cref="Trace"/> into the session log.</summary>
        private sealed class SessionLogListener : TraceListener
        {
            private readonly StringBuilder _partial = new StringBuilder();

            public override void Write(string? message)
            {
                lock (_partial) _partial.Append(message);
            }

            public override void WriteLine(string? message)
            {
                string text;
                lock (_partial)
                {
                    _partial.Append(message);
                    text = _partial.ToString();
                    _partial.Clear();
                }
                SessionLog.Write("trace", text);
            }
        }
    }
}

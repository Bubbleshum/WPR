using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// This app's own logcat for a recent window, for "send a report".
    /// </summary>
    /// <remarks>
    /// <para>Logcat is where the things WPR's session log cannot hold end up: native crashes
    /// (tombstone summaries, <c>DEBUG</c> lines), FNA3D and SDL output, and the runtime's own
    /// messages. It covers the <c>:game</c> process too, because both processes share the app's uid.</para>
    ///
    /// <para><b>No permission is involved</b>: since Android 4.1 an app may read only its own uid's
    /// log lines, and that is all <c>logcat -d</c> returns when an app runs it, so this cannot see any
    /// other app. Capped, and every failure answers "(logcat unavailable)".</para>
    /// </remarks>
    internal static class OwnLogcat
    {
        private const int MaxChars = 8_000_000;

        public static string Read(TimeSpan window)
        {
            try
            {
                // logcat's -T takes 'MM-dd HH:mm:ss.mmm' in the device's local time.
                string since = (DateTime.Now - window).ToString("MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                Java.Lang.Process process = Java.Lang.Runtime.GetRuntime()!.Exec(new[] { "logcat", "-d", "-v", "threadtime", "-T", since })!;

                StringBuilder text = new StringBuilder();
                using (StreamReader reader = new StreamReader(process.InputStream!, Encoding.UTF8))
                {
                    char[] buffer = new char[16384];
                    int read;
                    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        text.Append(buffer, 0, read);
                        if (text.Length > MaxChars) text.Remove(0, text.Length - MaxChars);
                    }
                }
                process.WaitFor();
                return text.Length == 0 ? "(no logcat lines in that window)" : text.ToString();
            }
            catch (Exception ex)
            {
                return $"(logcat unavailable: {ex.Message})";
            }
        }
    }
}

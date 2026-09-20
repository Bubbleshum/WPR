#nullable enable
using System;
using System.IO;

namespace WPR.Engine.Graphics
{
    /// <summary>
    /// A crash-loop breaker for the graphics driver: remembers, across process deaths, whether the
    /// last launch that asked for a given driver ever got a frame onto the screen.
    ///
    /// <para><b>Why this is not a capability cache.</b> The obvious design is "probe what the
    /// device supports, cache the answer". That is not what this is, because the failures worth
    /// defending against are not reported. A driver that declines at
    /// <c>FNA3D_PrepareWindowAttributes</c> is already handled — the ladder in
    /// <c>SDL2_FNAPlatform.PrepareWindowAttributesWithFallback</c> simply tries the next one. What
    /// the ladder cannot rescue is a driver that <i>prepares</i> and then dies: an error logged
    /// from inside <c>FNA3D_CreateDevice</c> arrives as a managed throw through native frames that
    /// are now half-initialised, so retrying in-process is unsafe — and the common case on a bad
    /// mobile driver is worse still, a <c>SIGSEGV</c> that takes the process with it and can be
    /// neither caught nor logged. Nothing can be learned within such a launch. It can only be
    /// learned across one, which is why this is a file and not a probe.</para>
    ///
    /// <para><b>The window is deliberately tiny.</b> <see cref="MarkAttempting"/> is called
    /// immediately before the driver is first touched and <see cref="MarkWorking"/> at the first
    /// presented frame, so "pending" describes device creation plus one frame — not a play
    /// session. That is what makes a single strike safe: for a user to be mistaken for a crash
    /// they would have to force-close inside that window. If a real device ever argues otherwise,
    /// the knob is to require N consecutive pendings rather than one.</para>
    ///
    /// <para><b>What it cannot see.</b> A driver that initialises, presents, and dies a minute
    /// later while drawing the actual scene is marked working at frame one and never demoted. That
    /// class of device is what the user-facing graphics setting exists for; the two mechanisms are
    /// complementary and neither replaces the other.</para>
    ///
    /// <para><b>Unconfigured is disabled.</b> A platform that never calls <see cref="Configure"/>
    /// — the Windows head, which declares no driver at all — gets <see cref="Verdict"/> of null and
    /// no file is ever written. Absent means "this platform does not use it", never an error, the
    /// same rule every other capability follows.</para>
    /// </summary>
    public static class GraphicsDriverProbe
    {
        /// <summary>
        /// Sits beside <see cref="GraphicsDriverPreference.OverrideFileName"/> in the same
        /// directory, so a support request can read both with one <c>adb shell cat</c>.
        /// </summary>
        public const string ProbeFileName = "graphics_probe.txt";

        private const string VerdictPending = "pending";
        private const string VerdictWorking = "ok";

        private static readonly object _gate = new object();
        private static string? _directory;
        private static string? _deviceKey;

        /* Volatile and read outside the lock because MarkWorking is called from SwapBuffers, i.e.
         * once per frame for the life of the game. After the first frame this field is the entire
         * cost of the feature. */
        private static volatile bool _markedWorking;

        /// <summary>
        /// Points the probe at its storage and at the identity of the device it describes. Called
        /// by the head, which is the only thing that can name a device.
        /// </summary>
        /// <param name="directory">Where <see cref="ProbeFileName"/> lives — normally the same
        /// directory the head hands to <c>caps.GraphicsDriver</c>. Null disables the probe.</param>
        /// <param name="deviceKey">An opaque identity for this device and its system image
        /// (<c>Build.FINGERPRINT</c> on Android). A verdict recorded against a different key is
        /// ignored, so an OS or driver update re-tries the preferred driver instead of inheriting
        /// a condemnation earned by software that is no longer installed.</param>
        public static void Configure(string? directory, string? deviceKey)
        {
            lock (_gate)
            {
                _directory = string.IsNullOrEmpty(directory) ? null : directory;
                _deviceKey = deviceKey;
                _markedWorking = false;
            }
        }

        /// <summary>
        /// The driver that failed to get a frame onto the screen last time, or null if there is no
        /// such verdict — no probe file, an unreadable one, one recorded against a different
        /// device key, or one that reached <see cref="MarkWorking"/>.
        ///
        /// <para>This names a driver to <b>avoid</b>, not one to use. Choosing the replacement is
        /// the head's business, because only it knows what else its platform has.</para>
        /// </summary>
        public static string? FailedDriver
        {
            get
            {
                string? directory;
                string? deviceKey;
                lock (_gate)
                {
                    directory = _directory;
                    deviceKey = _deviceKey;
                }

                ProbeRecord? record = Read(directory);
                if (record == null || !record.Value.IsPending)
                {
                    return null;
                }

                /* A verdict earned on a different system image says nothing about this one. */
                if (!string.Equals(record.Value.Device, deviceKey, StringComparison.Ordinal))
                {
                    return null;
                }

                return string.IsNullOrEmpty(record.Value.Requested) ? null : record.Value.Requested;
            }
        }

        /// <summary>A one-line description of the stored verdict, for diagnostics screens.</summary>
        public static string Describe()
        {
            string? directory;
            lock (_gate)
            {
                directory = _directory;
            }

            if (directory == null)
            {
                return "(not used on this platform)";
            }

            ProbeRecord? record = Read(directory);
            if (record == null)
            {
                return "(none recorded)";
            }

            string actual = string.IsNullOrEmpty(record.Value.Actual) ? "?" : record.Value.Actual!;
            string requested = string.IsNullOrEmpty(record.Value.Requested)
                ? "(automatic)"
                : record.Value.Requested!;

            return record.Value.IsPending
                ? "last launch asked for " + requested + " and never presented a frame"
                : "last launch ran on " + actual + " (asked for " + requested + ")";
        }

        /// <summary>
        /// Records that a launch is about to touch <paramref name="requestedDriver"/>. Call this
        /// immediately before anything reaches the driver — the very first call into it builds a
        /// throwaway device to test the waters, so a crash is reachable well before
        /// <c>FNA3D_CreateDevice</c>.
        /// </summary>
        /// <param name="requestedDriver">The forced driver name, or null for automatic
        /// selection.</param>
        public static void MarkAttempting(string? requestedDriver)
        {
            string? directory;
            string? deviceKey;
            lock (_gate)
            {
                directory = _directory;
                deviceKey = _deviceKey;
                _markedWorking = false;
            }

            Write(directory, deviceKey, requestedDriver, actual: null, pending: true);
        }

        /// <summary>
        /// Records that a frame reached the screen, which retires the pending mark. Safe and cheap
        /// to call on every frame: only the first call after a <see cref="MarkAttempting"/> writes
        /// anything.
        /// </summary>
        /// <param name="actualDriver">The driver that actually served the frame, which is not
        /// always the one requested — a declined driver falls through the ladder silently, and a
        /// verdict that recorded only the request would hide which one really ran.</param>
        public static void MarkWorking(string? actualDriver)
        {
            /* Unlocked fast path: this runs once per presented frame and after the first one there
             * is nothing left to do. The lock below still settles the race between two threads
             * arriving at the first frame together. */
            if (_markedWorking)
            {
                return;
            }

            string? directory;
            string? deviceKey;
            lock (_gate)
            {
                if (_markedWorking)
                {
                    return;
                }

                _markedWorking = true;
                directory = _directory;
                deviceKey = _deviceKey;
            }

            ProbeRecord? record = Read(directory);
            string? requested = record?.Requested;

            Write(directory, deviceKey, requested, actualDriver, pending: false);
        }

        /// <summary>
        /// Forgets any stored verdict. Call this when the user makes an explicit driver choice: the
        /// choice outranks the probe anyway, and leaving a stale condemnation behind would let it
        /// reassert itself the moment they set the driver back to the default.
        /// </summary>
        public static void Clear()
        {
            string? directory;
            lock (_gate)
            {
                directory = _directory;
                _markedWorking = false;
            }

            if (directory == null)
            {
                return;
            }

            try
            {
                string path = Path.Combine(directory, ProbeFileName);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                /* Diagnostics only; a verdict we failed to delete is not worth failing a launch
                 * over, and the explicit setting beats it regardless. */
            }
        }

        private readonly struct ProbeRecord
        {
            internal ProbeRecord(string? device, string? requested, string? actual, bool isPending)
            {
                Device = device;
                Requested = requested;
                Actual = actual;
                IsPending = isPending;
            }

            internal string? Device { get; }
            internal string? Requested { get; }
            internal string? Actual { get; }
            internal bool IsPending { get; }
        }

        /* Line-based rather than JSON on purpose. This project has no package references at all and
         * GraphicsDriverPreference already reads its override file with a bare File.ReadAllText, so
         * a serializer would be the only dependency in the assembly. It also keeps the file
         * readable with `adb shell cat`, which is how it will actually be inspected. */
        private static ProbeRecord? Read(string? directory)
        {
            if (directory == null)
            {
                return null;
            }

            try
            {
                string path = Path.Combine(directory, ProbeFileName);
                if (!File.Exists(path))
                {
                    return null;
                }

                string? device = null;
                string? requested = null;
                string? actual = null;
                string? verdict = null;

                foreach (string line in File.ReadAllLines(path))
                {
                    int split = line.IndexOf('=');
                    if (split <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, split).Trim();
                    string value = line.Substring(split + 1).Trim();

                    if (key == "device") device = value;
                    else if (key == "requested") requested = value;
                    else if (key == "actual") actual = value;
                    else if (key == "verdict") verdict = value;
                }

                if (verdict == null)
                {
                    return null;
                }

                return new ProbeRecord(
                    device,
                    Blank(requested),
                    Blank(actual),
                    string.Equals(verdict, VerdictPending, StringComparison.Ordinal));
            }
            catch (Exception)
            {
                /* A torn or unreadable file reads as "no verdict", so the worst a half-written one
                 * can do is let the preferred driver be tried again. That is the safe direction:
                 * this file may only ever cost a device its first choice, never its only one. */
                return null;
            }
        }

        private static void Write(
            string? directory,
            string? deviceKey,
            string? requested,
            string? actual,
            bool pending)
        {
            if (directory == null)
            {
                return;
            }

            try
            {
                string contents =
                    "device=" + (deviceKey ?? "") + "\n" +
                    "requested=" + (requested ?? "") + "\n" +
                    "actual=" + (actual ?? "") + "\n" +
                    "verdict=" + (pending ? VerdictPending : VerdictWorking) + "\n";

                /* Temp-then-replace so a process that dies mid-write cannot leave a file that
                 * parses into a verdict nobody reached. */
                string path = Path.Combine(directory, ProbeFileName);
                string temp = path + ".tmp";
                File.WriteAllText(temp, contents);
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception)
            {
                /* Never fail a launch over the breadcrumb. Losing a write costs one repeated crash
                 * on the next launch, which is exactly the situation without this file at all. */
            }
        }

        private static string? Blank(string? value) =>
            string.IsNullOrEmpty(value) ? null : value;
    }
}

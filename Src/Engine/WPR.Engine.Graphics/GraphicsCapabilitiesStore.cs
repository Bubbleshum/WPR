#nullable enable
using System;
using System.Globalization;
using System.IO;

namespace WPR.Engine.Graphics
{
    /// <summary>
    /// Carries the measured <see cref="IGraphicsCapabilities"/> from the process that has a device
    /// to the process that wants to display them.
    ///
    /// <para><b>Why this is a file and not a static.</b> Capabilities can only be measured where a
    /// graphics device exists, which on Android is the <c>:game</c> process — and the diagnostics
    /// screen that wants to show them is <c>GameInfoActivity</c>, in the launcher. The two never
    /// share memory, and <c>GameActivity.OnDestroy</c> kills <c>:game</c> outright, so there is no
    /// handover to catch. The same reasoning, and the same directory, as
    /// <see cref="GraphicsDriverProbe"/>.</para>
    ///
    /// <para><b>Deliberately not in config.json.</b> <c>Configuration.Save()</c> serialises the
    /// whole object, so a launcher holding a copy loaded before the game wrote would silently
    /// clobber this on its next unrelated save.</para>
    ///
    /// <para>What is stored is the LAST measurement on this device, not a permanent truth. It is
    /// rewritten every launch that reaches its first frame, so after a driver change it describes
    /// the new driver.</para>
    /// </summary>
    public static class GraphicsCapabilitiesStore
    {
        /// <summary>Sits beside the probe file so one <c>adb shell cat</c> gets the whole story.</summary>
        public const string CapabilitiesFileName = "graphics_capabilities.txt";

        private static readonly object _gate = new object();
        private static string? _directory;
        private static IGraphicsCapabilities? _current;

        /// <summary>
        /// Points the store at its storage. Called by the head alongside
        /// <see cref="GraphicsDriverProbe.Configure"/>. Null disables it, which is what a platform
        /// that declares no driver gets.
        /// </summary>
        public static void Configure(string? directory)
        {
            lock (_gate)
            {
                _directory = string.IsNullOrEmpty(directory) ? null : directory;
                _current = null;
            }
        }

        /// <summary>
        /// What this launch measured, if this process is the one that measured it. Null in the
        /// launcher, which has no device — use <see cref="ReadLastMeasured"/> there.
        /// </summary>
        public static IGraphicsCapabilities? Current
        {
            get { lock (_gate) { return _current; } }
        }

        /// <summary>
        /// Records a measurement and writes it through. Called once per launch, at the first
        /// presented frame, by the backend that owns the device handle.
        /// </summary>
        public static void Publish(GraphicsCapabilities capabilities)
        {
            if (capabilities == null)
            {
                return;
            }

            string? directory;
            lock (_gate)
            {
                _current = capabilities;
                directory = _directory;
            }

            if (directory == null)
            {
                return;
            }

            try
            {
                string path = Path.Combine(directory, CapabilitiesFileName);
                string temp = path + ".tmp";
                File.WriteAllText(temp, capabilities.Serialise());
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception)
            {
                /* Diagnostics only — never fail a launch over them. */
            }
        }

        /// <summary>
        /// The last measurement written on this device, or null if nothing has ever measured (no
        /// game has been run since install) or the file is unreadable.
        /// </summary>
        public static IGraphicsCapabilities? ReadLastMeasured()
        {
            string? directory;
            lock (_gate)
            {
                directory = _directory;
            }

            if (directory == null)
            {
                return null;
            }

            try
            {
                string path = Path.Combine(directory, CapabilitiesFileName);
                if (!File.Exists(path))
                {
                    return null;
                }

                string? backend = null;
                bool? offThread = null;
                bool dxt1 = false, s3tc = false, bc7 = false;
                bool instancing = false, noOverwrite = false, srgb = false;
                int texSlots = 0, vtexSlots = 0;
                bool sawAnything = false;

                foreach (string line in File.ReadAllLines(path))
                {
                    int split = line.IndexOf('=');
                    if (split <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, split).Trim();
                    string value = line.Substring(split + 1).Trim();
                    sawAnything = true;

                    switch (key)
                    {
                        case "backend": backend = value.Length == 0 ? null : value; break;
                        case "offthread": offThread = Tri(value); break;
                        case "dxt1": dxt1 = value == "1"; break;
                        case "s3tc": s3tc = value == "1"; break;
                        case "bc7": bc7 = value == "1"; break;
                        case "instancing": instancing = value == "1"; break;
                        case "nooverwrite": noOverwrite = value == "1"; break;
                        case "srgbtargets": srgb = value == "1"; break;
                        case "texslots": texSlots = Int(value); break;
                        case "vtexslots": vtexSlots = Int(value); break;
                    }
                }

                if (!sawAnything)
                {
                    return null;
                }

                return new GraphicsCapabilities(
                    backend, offThread, dxt1, s3tc, bc7,
                    instancing, noOverwrite, srgb, texSlots, vtexSlots);
            }
            catch (Exception)
            {
                /* A torn file reads as "never measured", which is the safe direction: the screen
                 * says nothing rather than something wrong. */
                return null;
            }
        }

        private static bool? Tri(string value) =>
            value == "1" ? true : value == "0" ? (bool?) false : null;

        private static int Int(string value) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : 0;
    }
}

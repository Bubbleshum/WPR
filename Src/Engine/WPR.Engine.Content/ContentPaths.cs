#nullable enable
using System;
using System.IO;
using WPR.Common;

namespace WPR.Engine.Content
{
    /// <summary>
    /// Turns a path a WP7 game asked for into one this platform can actually open.
    ///
    /// <para><b>This is the whole of the logic.</b> The shims in the framework
    /// (<c>File2</c>, <c>Directory2</c>, <c>XmlReader2</c>, <c>XDocument2</c>, <c>XElement2</c>,
    /// the <c>NormalizedPath*</c> stream subclasses) exist only because
    /// <c>ApplicationPatcher.MemberPatches</c> has to name a type that the game's rewritten IL can
    /// bind - they must live beside the other patch targets. Every one of them is a one-line call
    /// into here, so there is exactly one implementation of "what does this path mean".</para>
    ///
    /// <para><b>Read vs write is a real distinction, not a tidiness one.</b>
    /// <see cref="Resolve"/> may redirect to a file that already exists elsewhere;
    /// <see cref="Normalize"/> only fixes separators. A create/write call must use
    /// <see cref="Normalize"/>, or writing a new file could silently land on an existing one in
    /// the install folder instead of where the game asked.</para>
    /// </summary>
    public static class ContentPaths
    {
        private static readonly object _gate = new object();
        private static ContentPathRules? _declared;

        /// <summary>
        /// The rules in force: what a platform declared, or the running filesystem's own if no
        /// platform has. See <see cref="ContentPathRules.FromRunningPlatform"/> for why there is a
        /// measured fallback rather than a no-op one.
        /// </summary>
        public static ContentPathRules Rules
        {
            get
            {
                lock (_gate)
                {
                    return _declared ??= ContentPathRules.FromRunningPlatform();
                }
            }
        }

        /// <summary>Records the platform's declaration. Called by the composition root.</summary>
        public static void Declare(ContentPathRules rules)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            lock (_gate)
            {
                _declared = rules;
            }
        }

        /// <summary>
        /// Separator translation only. Use for anything that CREATES or WRITES, and for directory
        /// operations - see the note on the class about why this is not <see cref="Resolve"/>.
        /// </summary>
        public static string? Normalize(string? path)
        {
            if (string.IsNullOrEmpty(path) || Rules.WindowsSeparatorsAreNative)
            {
                return path;
            }

            return path!.IndexOf('\\') < 0 ? path : path!.Replace('\\', '/');
        }

        /// <summary>
        /// The path to actually open for a READ: separators translated, then - if the platform
        /// asked for it - a relative path that does not resolve against the working directory is
        /// retried against the running game's install folder.
        ///
        /// <para>An absolute path, or a relative one that already resolves, is returned unchanged,
        /// so this can only turn a failure into a success.</para>
        /// </summary>
        public static string? Resolve(string? path)
        {
            string? normalized = Normalize(path);
            if (string.IsNullOrEmpty(normalized) || !Rules.ProbeInstallFolderForRelativePaths)
            {
                return normalized;
            }

            try
            {
                if (Path.IsPathRooted(normalized) || File.Exists(normalized))
                {
                    return normalized;
                }

                string? installFolder = WprHostEnvironment.CurrentInstallFolder;
                if (string.IsNullOrEmpty(installFolder))
                {
                    return normalized;
                }

                string candidate = Path.Combine(installFolder!, normalized!);
                return File.Exists(candidate) ? candidate : normalized;
            }
            catch (Exception)
            {
                /* A malformed path (illegal characters, too long) throws from IsPathRooted or
                 * Combine. Hand back what the game asked for and let the real BCL call produce
                 * the exception the game expects, rather than failing here with a different one. */
                return normalized;
            }
        }

        /// <summary>One line for the platform summary in the launch log.</summary>
        public static string Describe() => "content=(" + Rules + ")";
    }
}

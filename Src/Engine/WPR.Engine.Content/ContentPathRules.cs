#nullable enable
using System.IO;

namespace WPR.Engine.Content
{
    /// <summary>
    /// What a platform tells the engine about how its filesystem treats a WP7 game's paths.
    ///
    /// <para><b>These are facts about the device, not instructions.</b> A head says
    /// "backslash is not a separator here"; it does not say "rewrite backslashes". Deciding what
    /// to do about that is <see cref="ContentPaths"/>' job, which is why the rules are declared
    /// once at composition and the logic lives in one place instead of being spread across a
    /// dozen shims. Same split as <see cref="WPR.Engine.Graphics.GraphicsDriver"/> and
    /// <c>GraphicsDriverPreference</c>.</para>
    ///
    /// <para><b>Why any of this is needed.</b> Every WP7 title was built and tested on Windows,
    /// where <c>\</c> and <c>/</c> are interchangeable, so hardcoded paths like
    /// <c>Content\Credits.xml</c> are common - 7 of the 26 installed titles carry at least one and
    /// one carries 89. On Android <c>\</c> is an ordinary, legal filename character, so that names
    /// a single file in the install root rather than <c>Credits.xml</c> inside <c>Content</c>. The
    /// open fails, games routinely swallow the exception, and the symptom surfaces somewhere
    /// unrelated. Battlewagon is the reference case: one failed
    /// <c>XmlReader.Create("Content\Credits.xml")</c> left a null field and
    /// <c>TitleScene.Update</c> then threw a NullReferenceException on EVERY frame - 5,593 in one
    /// run - so its menu never built while the background animated happily.</para>
    /// </summary>
    public sealed class ContentPathRules
    {
        /// <summary>
        /// True when the platform already accepts <c>\</c> as a directory separator, i.e. a
        /// Windows-style path opens as-is. Windows: true. Android and any other Unix host: false,
        /// because there <c>\</c> is a legal character in a filename.
        /// </summary>
        public bool WindowsSeparatorsAreNative { get; init; }

        /// <summary>
        /// True when a relative path that does not resolve against the current working directory
        /// should be retried against the running game's install folder.
        ///
        /// <para>WP7 titles read data files with a bare relative path because on real hardware the
        /// working directory WAS the install root. That still holds for XNA games under WPR (the
        /// launch path chdir's there), but a Silverlight app runs in-process and the working
        /// directory is the host exe's, so the file is not where the game expects. This is what
        /// made <c>XElement.Load("XboxLIVESettings.xml")</c> work, and generalising it was the
        /// point of moving this logic into the engine.</para>
        /// </summary>
        public bool ProbeInstallFolderForRelativePaths { get; init; }

        /// <summary>
        /// What the engine assumes when no platform has declared anything - measured from the
        /// running filesystem rather than guessed, so a host that composes no platform at all
        /// (the bare <c>FnaGameHost</c> console harness) still behaves correctly.
        ///
        /// <para>The install-folder probe is deliberately OFF in this default: it is a hosting
        /// policy rather than a property of the filesystem, so it is something a platform opts
        /// into rather than something inferred.</para>
        /// </summary>
        public static ContentPathRules FromRunningPlatform() => new ContentPathRules
        {
            WindowsSeparatorsAreNative = Path.DirectorySeparatorChar == '\\',
            ProbeInstallFolderForRelativePaths = false,
        };

        /// <summary>One line for the platform summary in the launch log.</summary>
        public override string ToString() =>
            "separators=" + (WindowsSeparatorsAreNative ? "native" : "translated")
            + " installProbe=" + (ProbeInstallFolderForRelativePaths ? "on" : "off");
    }
}

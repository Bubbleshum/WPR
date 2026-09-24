using System;
using System.Diagnostics;
using System.Linq;
using WPR.Engine.Launchers;

namespace Microsoft.Phone.Tasks
{
    /// <summary>
    /// Shim for <c>Microsoft.Phone.Tasks.ShareTaskBase</c>.
    /// </summary>
    /// <remarks>
    /// <para><see cref="Show"/> hands the task's text to <see cref="LauncherBackend.Share"/> —
    /// Android's system chooser. A platform that declares no share sheet (the desktop head, for
    /// now) leaves this a no-op, which is still a real WP7 outcome: a device with no share targets
    /// configured showed the user nothing either.</para>
    ///
    /// <para>These tasks are fire-and-forget: they report no result and raise no completion event,
    /// so a caller cannot tell whether anything was shared and nothing waits on one. That is also
    /// why <see cref="Show"/> never throws — a game calls it from a button handler.</para>
    /// </remarks>
    public abstract class ShareTaskBase
    {
        public virtual void Show()
        {
            if (!Describe(out string? subject, out string? text) || string.IsNullOrWhiteSpace(text))
            {
                Trace.WriteLine("[wpr-launch] " + GetType().Name + ".Show with nothing to share - ignored");
                return;
            }

            IShareSheet? sheet = LauncherBackend.Share;
            if (sheet == null)
            {
                Trace.WriteLine("[wpr-launch] " + GetType().Name + ".Show - no share sheet on this platform");
                return;
            }

            try
            {
                sheet.TryShare(string.IsNullOrWhiteSpace(subject) ? null : subject, text);
            }
            catch (Exception ex)
            {
                // TryShare is not supposed to throw; this is the belt to that brace.
                Trace.WriteLine("[wpr-launch] share sheet threw: " + ex);
            }
        }

        /// <summary>
        /// What this task shares: an optional subject and the body. False when there is nothing.
        /// </summary>
        internal abstract bool Describe(out string? subject, out string? text);
    }

    /// <summary>
    /// Shim for <c>Microsoft.Phone.Tasks.ShareLinkTask</c> — WP7's "share this link" chooser.
    /// </summary>
    /// <remarks>
    /// <para>Shares <see cref="Message"/> followed by the link, with <see cref="Title"/> as the
    /// subject — the shape WP7's own share targets presented, and what an email or chat app shows
    /// sensibly.</para>
    ///
    /// <para><b>The link goes through the same rewrite as <see cref="WebBrowserTask"/></b>, so Cut
    /// the Rope's <c>vnd.youtube:&lt;id&gt;</c> is shared as a normal youtube.com URL — a WP7-only
    /// scheme is useless to whoever receives it. A link <c>WebBrowserTask</c> would refuse to OPEN
    /// is still shared, verbatim: here it is only text in a message the player chooses to send,
    /// not something WPR hands to the OS to act on.</para>
    /// </remarks>
    public class ShareLinkTask : ShareTaskBase
    {
        public string? Title { get; set; }

        public string? Message { get; set; }

        public Uri? LinkUri { get; set; }

        internal override bool Describe(out string? subject, out string? text)
        {
            subject = Title;

            string? link = null;
            if (LinkUri != null)
            {
                link = WebBrowserTask.Normalise(LinkUri.OriginalString)?.AbsoluteUri ?? LinkUri.OriginalString;
            }

            text = string.Join(" ", new[] { Message?.Trim(), link }.Where(s => !string.IsNullOrEmpty(s)));
            return text.Length != 0;
        }
    }

    /// <summary>
    /// Shim for <c>Microsoft.Phone.Tasks.ShareStatusTask</c>.
    /// </summary>
    public class ShareStatusTask : ShareTaskBase
    {
        public string? Status { get; set; }

        internal override bool Describe(out string? subject, out string? text)
        {
            subject = null;
            text = Status?.Trim();
            return !string.IsNullOrEmpty(text);
        }
    }
}

using System;
using Android.Content;
using WPR.Engine.Launchers;
using Trace = System.Diagnostics.Trace;

namespace WPR.Launchers.AndroidIntent
{
    /// <summary>
    /// Opens a URI with an <c>ACTION_VIEW</c> intent, so Android picks the handler: the browser,
    /// or an app that has claimed the link (a youtube.com URL goes to the YouTube app).
    /// </summary>
    /// <remarks>
    /// <para><b>Hold the application context, never an activity.</b> This is composed in both the
    /// launcher and the <c>:game</c> process, and is process-lifetime; an activity reference would
    /// pin it. That is also why the intent carries <c>NEW_TASK</c>, which Android requires when
    /// starting an activity from a non-activity context. The game's activity is simply paused
    /// underneath and resumes when the player comes back, exactly like WP7's task switch.</para>
    /// </remarks>
    public sealed class AndroidIntentUriLauncher : IUriLauncher
    {
        private readonly Context _context;

        public AndroidIntentUriLauncher(Context context)
        {
            _context = (context ?? throw new ArgumentNullException(nameof(context))).ApplicationContext ?? context;
        }

        public bool TryOpen(Uri uri)
        {
            try
            {
                var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(uri.AbsoluteUri));
                intent.AddFlags(ActivityFlags.NewTask);
                _context.StartActivity(intent);
                Trace.WriteLine("[wpr-launch] opened " + uri.AbsoluteUri);
                return true;
            }
            catch (Exception ex)
            {
                // ActivityNotFoundException when nothing on the device handles the link.
                Trace.WriteLine($"[wpr-launch] could not open {uri.AbsoluteUri}: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }
}

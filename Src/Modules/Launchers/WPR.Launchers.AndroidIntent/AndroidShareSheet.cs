using System;
using Android.Content;
using WPR.Engine.Launchers;
using Trace = System.Diagnostics.Trace;

namespace WPR.Launchers.AndroidIntent
{
    /// <summary>
    /// Shares text through Android's system chooser: an <c>ACTION_SEND</c> of <c>text/plain</c>,
    /// wrapped in <c>Intent.createChooser</c> so the player always gets the picker rather than a
    /// remembered default.
    /// </summary>
    /// <remarks>
    /// Holds the application context and adds <c>NEW_TASK</c>, for the reasons given on
    /// <see cref="AndroidIntentUriLauncher"/>. The flag has to go on the CHOOSER intent — that is
    /// the one actually started from a non-activity context.
    /// </remarks>
    public sealed class AndroidShareSheet : IShareSheet
    {
        private readonly Context _context;

        public AndroidShareSheet(Context context)
        {
            _context = (context ?? throw new ArgumentNullException(nameof(context))).ApplicationContext ?? context;
        }

        public bool TryShare(string? subject, string text)
        {
            try
            {
                var send = new Intent(Intent.ActionSend);
                send.SetType("text/plain");
                send.PutExtra(Intent.ExtraText, text);
                if (!string.IsNullOrEmpty(subject))
                {
                    send.PutExtra(Intent.ExtraSubject, subject);
                }

                Intent? chooser = Intent.CreateChooser(send, subject);
                if (chooser == null) return false;
                chooser.AddFlags(ActivityFlags.NewTask);
                _context.StartActivity(chooser);

                Trace.WriteLine("[wpr-launch] share sheet shown (" + text.Length + " chars)");
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[wpr-launch] could not show share sheet: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }
}

using System;
using System.Diagnostics;

namespace WPR.Shell
{
    public class ApplicationLaunchRequestArgs : EventArgs
    {
        internal ApplicationLaunchRequestArgs(Models.Application app, bool diagnostic)
        {
            this.Target = app;
            this.Diagnostic = diagnostic;
        }

        public Models.Application Target { get; set; }

        /// <summary>"Play with logging": see <see cref="DiagnosticRun"/>.</summary>
        public bool Diagnostic { get; }
    }

    public static class ApplicationLaunchRequest
    {
        public static EventHandler<ApplicationLaunchRequestArgs>? Incoming;

        public static void Ask(Models.Application app, bool diagnostic = false)
        {
            try
            {
                Incoming?.Invoke(null, new ApplicationLaunchRequestArgs(app, diagnostic));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ex] ApplicationLaunchRequest (Ask) error: " + ex.Message);
                WPR.Common.Log.Error(WPR.Common.LogCategory.AppList, $"ApplicationLaunchRequest failed: {ex}");
            }
        }
    }
}

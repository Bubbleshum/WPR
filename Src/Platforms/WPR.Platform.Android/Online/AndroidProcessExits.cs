using System;
using Android.App;
using Android.Content;
using WPR.Engine.Online;

namespace WPR.Platform.Android.Online
{
    /// <summary>
    /// <see cref="CompatibilityProblems.ProcessExitLookup"/> over Android's own record of why each
    /// of this app's processes ended (<c>ActivityManager.getHistoricalProcessExitReasons</c>, API
    /// 30+). Below 30 there is no record and every unexplained death stays a native crash.
    /// </summary>
    /// <remarks>
    /// Read it on a phone with <c>adb shell dumpsys activity exit-info com.wpr.android</c>.
    /// Measured on the S24, 2026-10-23: an APK install is <c>reason=16 (PACKAGE UPDATED)
    /// description=stop com.wpr.android due to installPackageLI</c> for the launcher AND a running
    /// <c>:game</c>; a game ending normally is <c>reason=2 (SIGNALED) status=9</c> (GameActivity's
    /// own <c>KillProcess</c>, after the run file is gone); a real crash is
    /// <c>reason=5 (APP CRASH(NATIVE))</c>.
    /// </remarks>
    public sealed class AndroidProcessExits
    {
        private readonly Context _context;

        public AndroidProcessExits(Context context) => _context = context.ApplicationContext ?? context;

        /// <summary>Clock slack between the run file's start time and the system's records.</summary>
        private static readonly TimeSpan Slack = TimeSpan.FromSeconds(5);

        public ProcessExit? Lookup(int pid, DateTimeOffset after)
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(30)) return null;
            if (_context.GetSystemService(Context.ActivityService) is not ActivityManager am) return null;

            long since = (after - Slack).ToUnixTimeMilliseconds();
            ApplicationExitInfo? best = null;
            var records = am.GetHistoricalProcessExitReasons(_context.PackageName, pid < 0 ? 0 : pid, pid > 0 ? 1 : 16);
            foreach (ApplicationExitInfo info in records)
            {
                if (info.Timestamp < since) continue;               // an older process with a reused pid
                if (pid <= 0 && info.ProcessName?.EndsWith(":game", StringComparison.Ordinal) != true) continue;
                if (best == null || info.Timestamp > best.Timestamp) best = info;
            }
            return best == null ? null : Describe(best);
        }

        private static ProcessExit Describe(ApplicationExitInfo info)
        {
            int reason = (int)info.Reason;
            (string name, bool crash) = reason switch
            {
                1 => ("exit-self", true),
                2 => ("signaled-" + info.Status, true),
                3 => ("low-memory", true),
                4 => ("crash", true),
                5 => ("crash-native", true),
                6 => ("anr", true),
                7 => ("initialization-failure", true),
                8 => ("permission-change", false),
                9 => ("excessive-resource-usage", true),
                10 => ("user-requested", false),  // force stop (Settings, am force-stop)
                11 => ("user-stopped", false),    // the user stopped multi-user
                12 => ("dependency-died", true),
                13 => ("other", true),
                14 => ("freezer", true),
                15 => ("package-state-change", false), // disabled
                16 => ("package-updated", false),      // an APK install
                _ => ("unknown-" + reason, true),
            };
            return new ProcessExit { Reason = name, Description = info.Description, IsCrash = crash };
        }
    }
}

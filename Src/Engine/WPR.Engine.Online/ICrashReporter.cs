using System.Threading.Tasks;

namespace WPR.Engine.Online
{
    /// <summary>
    /// Automatic crash reports to WPR Hub, for players who switched on server logging.
    /// </summary>
    /// <remarks>
    /// <para><b><see cref="Report"/> only writes to disk.</b> It is called from places a network
    /// call cannot survive: an unhandled-exception handler whose process is about to die, and on
    /// Android a <c>:game</c> process that <c>GameActivity.OnDestroy</c> kills outright. So a
    /// report is persisted synchronously, and <see cref="FlushAsync"/>, run by the launcher, sends
    /// it. One code path on both heads, and no report is lost to the crash it describes.</para>
    ///
    /// <para><b>Nothing is recorded without consent.</b> With server logging off
    /// <see cref="Report"/> is a no-op and <see cref="FlushAsync"/> discards anything queued,
    /// including game-package uploads in flight.</para>
    /// </remarks>
    public interface ICrashReporter
    {
        /// <summary>Queue a report. Synchronous, must not throw, must not touch the network.</summary>
        void Report(CrashReport report);

        /// <summary>
        /// Queue one run's per-problem compatibility reports
        /// (<see cref="CompatibilityProblems.BuildProblemReports"/>). An implementation drops the
        /// ones it has already sent for that title and WPR version and caps the rest at
        /// <see cref="CompatibilityProblems.MaxReportsPerRun"/>
        /// (<see cref="CompatibilityProblems.SelectForSending"/>). Returns how many were queued.
        /// Same rules as <see cref="Report"/>: synchronous, never throws, no network.
        /// </summary>
        int ReportCompatibility(System.Collections.Generic.IReadOnlyList<CrashReport> reports)
        {
            var picked = CompatibilityProblems.SelectForSending(reports, _ => false);
            foreach (CrashReport r in picked) Report(r);
            return picked.Count;
        }

        /// <summary>
        /// Send queued reports and upload any game package the hub asked for. Never throws; a
        /// failure leaves the item queued for the next flush.
        /// </summary>
        Task FlushAsync();
    }
}

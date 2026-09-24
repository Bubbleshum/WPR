using System;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.IApplicationService</c> — the interface an object implements
    /// to be listed in <c>&lt;Application.ApplicationLifetimeObjects&gt;</c> and be told when
    /// the app starts and stops.
    /// </summary>
    /// <remarks>
    /// WP7's own <c>PhoneApplicationService</c> is the usual occupant of that collection, but
    /// an app may add its own: Galactic Reign declares a <c>GamerServicesApplicationService</c>
    /// in its App.xaml, and without the interface the XAML reader could not resolve the type
    /// and the App failed to construct.
    ///
    /// <para><b>WPR does not call these.</b> The lifetime collection is held and its entries are
    /// constructed, which is what the app needs to build; driving Start/Stop would mean deciding
    /// when an app has "started" for objects with no other contract, and nothing yet needs it.
    /// An implementation that expects <c>StartService</c> gets an object that exists and is
    /// never started, which is the same position it is in on a page that is never navigated to.</para>
    /// </remarks>
    public interface IApplicationService
    {
        void StartService(ApplicationServiceContext context);

        void StopService();
    }

    /// <summary>
    /// Shim for <c>System.Windows.ApplicationServiceContext</c> — what an
    /// <see cref="IApplicationService"/> is handed when it starts.
    /// </summary>
    /// <remarks>
    /// Carries the Silverlight plugin's init parameters, which on a phone were always empty;
    /// see <see cref="StartupEventArgs"/>.
    /// </remarks>
    public class ApplicationServiceContext
    {
        public System.Collections.Generic.IDictionary<string, string> ApplicationInitParams { get; } =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Shim for <c>System.Windows.IApplicationLifetimeAware</c> — the finer-grained companion to
    /// <see cref="IApplicationService"/>, for an object that wants the four lifecycle edges.
    /// </summary>
    /// <remarks>Held and not called, for the same reason.</remarks>
    public interface IApplicationLifetimeAware
    {
        void Starting();
        void Started();
        void Exiting();
        void Exited();
    }
}

using System;

namespace System.Windows.Navigation
{
    /// <summary>
    /// Shim for <c>System.Windows.Navigation.UriMapperBase</c>.
    ///
    /// On Windows Phone this type ships in <c>Microsoft.Phone.dll</c>, so user assemblies
    /// reference it as <c>[Microsoft.Phone]System.Windows.Navigation.UriMapperBase</c>. It
    /// lives here in <c>WPR.SilverlightCompability</c> (kept in its original namespace) so
    /// that <see cref="WPR.SilverlightCompability.Frame"/>'s <c>UriMapper</c> property can be
    /// typed against it without SL taking a dependency on the Microsoft.Phone assembly (which
    /// references SL). The ApplicationPatcher retargets the user's typeref assembly scope to
    /// <c>WPR.SilverlightCompability</c> (namespace unchanged). It is the abstract base
    /// assigned to a frame's <c>UriMapper</c> property; the navigation framework calls
    /// <see cref="MapUri"/> to translate a requested URI before the page is resolved.
    /// </summary>
    public abstract class UriMapperBase
    {
        /// <summary>
        /// Translate <paramref name="uri"/> into the URI that should actually be navigated
        /// to. Implementations return the original URI unchanged when no rewrite applies.
        /// </summary>
        public abstract Uri MapUri(Uri uri);
    }
}

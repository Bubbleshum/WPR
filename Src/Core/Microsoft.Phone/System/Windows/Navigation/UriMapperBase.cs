using System;

namespace System.Windows.Navigation
{
    /// <summary>
    /// Shim for <c>System.Windows.Navigation.UriMapperBase</c>.
    ///
    /// On Windows Phone this type ships in <c>Microsoft.Phone.dll</c> (not the Silverlight
    /// core), which is why user assemblies reference it from the <c>Microsoft.Phone</c>
    /// assembly — so it lives here in our facade rather than being redirected by the
    /// patcher. It is the abstract base assigned to a frame's <c>UriMapper</c> property;
    /// the navigation framework calls <see cref="MapUri"/> to translate a requested URI
    /// before the page is resolved.
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

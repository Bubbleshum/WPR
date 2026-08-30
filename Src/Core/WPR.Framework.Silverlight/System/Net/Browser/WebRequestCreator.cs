using System;
using System.Net;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Net.Browser.WebRequestCreator</c>.
    ///
    /// In Silverlight this picked which HTTP stack a request went through: <c>BrowserHttp</c>
    /// routed via the browser (cookies, same-origin policy, a restricted header/verb set) and
    /// <c>ClientHttp</c> used the CLR stack. Off the browser there is only one stack, so both
    /// properties hand back the same creator and the distinction disappears — which is the
    /// correct behaviour here, not a compromise: WPR is the "client" case in both.
    ///
    /// Games reach this through <c>WebRequest.RegisterPrefix("http://", WebRequestCreator.ClientHttp)</c>
    /// while wiring up their networking, typically from a licence or trial-mode check during
    /// startup. Crimson Dragon: Side Story does exactly that in
    /// <c>Microsoft.Phone.Marketplace.HttpRequest..ctor</c>, so an unresolvable type here is a
    /// TypeLoadException before the game ever draws a frame.
    /// </summary>
    public static class WebRequestCreator
    {
        private static readonly ClrWebRequestCreator _Creator = new ClrWebRequestCreator();

        /// <summary>
        /// The browser HTTP stack in Silverlight. WPR has no browser, so this is the CLR stack.
        /// Requests that relied on browser cookie handling won't carry ambient cookies.
        /// </summary>
        public static IWebRequestCreate BrowserHttp => _Creator;

        /// <summary>The client HTTP stack — the CLR one, which is what we have.</summary>
        public static IWebRequestCreate ClientHttp => _Creator;

        private sealed class ClrWebRequestCreator : IWebRequestCreate
        {
            public WebRequest Create(Uri uri) => WebRequest.Create(uri);
        }
    }
}

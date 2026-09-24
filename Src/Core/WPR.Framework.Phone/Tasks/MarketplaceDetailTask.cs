using System;

namespace Microsoft.Phone.Tasks
{
    /// <summary>
    /// Shim for <c>Microsoft.Phone.Tasks.MarketplaceDetailTask</c> — opens the Marketplace page
    /// for an app, which is how a WP7 trial build sent you off to buy the full game.
    /// </summary>
    /// <remarks>
    /// <para><b><see cref="Show"/> does nothing, and that is the right answer</b> — the
    /// Marketplace it would open has not existed for years. What matters is that the properties
    /// exist so the call can be COMPILED.</para>
    ///
    /// <para><b><see cref="ContentIdentifier"/> was missing, and it cost every button on a
    /// menu.</b> Cut the Rope: Experiments' <c>MenuController.onButtonPressed</c> is one method
    /// handling every button, and one of its arms builds this task. A missing member is resolved
    /// when the method is COMPILED, not when that arm runs, so the whole method threw
    /// MissingMethodException on the first press — and Play, Album and Options were all dead
    /// together. The menu drew, taps arrived at the right pixel, the button dispatched, and
    /// nothing happened.</para>
    ///
    /// <para>That shape is worth recognising: <b>a menu where no button does anything, while
    /// touch traces look perfect, is one unresolved member in the handler rather than an input
    /// problem</b>. The per-game log names it — the <c>[wpr-fce]</c> stack points straight at the
    /// game's own button handler.</para>
    ///
    /// <para>Not to be confused with the game being in trial mode: <c>Guide.IsTrialMode</c> is
    /// false here and was answering correctly throughout.</para>
    /// </remarks>
    public class MarketplaceDetailTask
    {
        /// <summary>
        /// The product to show. Null means "this app", which is what a trial build passes when it
        /// offers to sell you itself.
        /// </summary>
        public string? ContentIdentifier { get; set; }

        public MarketplaceContentType ContentType { get; set; }

        public void Show()
        {
            // Deliberately empty — see the class remarks.
        }
    }
}

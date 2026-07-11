using System.Globalization;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Markup.XmlLanguage</c>. Wraps an IETF language tag
    /// (e.g. "en-US"). Silverlight exposes no public constructor — instances come from the
    /// static <see cref="GetLanguage"/> factory, which the WP app template's
    /// <c>InitializeLanguage()</c> uses to set <c>RootFrame.Language</c>.
    /// </summary>
    public class XmlLanguage
    {
        private XmlLanguage(string ietfLanguageTag)
        {
            IetfLanguageTag = ietfLanguageTag ?? string.Empty;
        }

        /// <summary>The IETF language tag this instance represents (lower-cased, per SL).</summary>
        public string IetfLanguageTag { get; }

        /// <summary>
        /// Returns an <see cref="XmlLanguage"/> for the given IETF tag. Silverlight caches and
        /// lower-cases the tag; we mirror the lower-casing so equality by tag behaves the same.
        /// </summary>
        public static XmlLanguage GetLanguage(string ietfLanguageTag)
            => new XmlLanguage((ietfLanguageTag ?? string.Empty).ToLowerInvariant());

        /// <summary>
        /// The .NET culture matching this language, or <see cref="CultureInfo.InvariantCulture"/>
        /// when the tag is empty or unrecognized (SL throws for unknown tags; we degrade instead).
        /// </summary>
        public CultureInfo GetEquivalentCulture()
        {
            if (string.IsNullOrEmpty(IetfLanguageTag))
                return CultureInfo.InvariantCulture;
            try { return CultureInfo.GetCultureInfo(IetfLanguageTag); }
            catch { return CultureInfo.InvariantCulture; }
        }

        public override string ToString() => IetfLanguageTag;
    }
}

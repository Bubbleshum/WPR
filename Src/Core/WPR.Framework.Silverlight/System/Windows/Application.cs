using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using WPR.SilverlightCompability;

namespace WPR.WindowsCompability
{
    /// <summary>A retrieved resource stream and its content type.</summary>
    public class StreamResourceInfo
    {
        public Stream? Stream { get; set; }
        public string? ContentType { get; set; }
    }

    public class Application
    {
        private static Application? _Current;
        public event EventHandler<ApplicationUnhandledExceptionEventArgs>? UnhandledException;

        public string? ProductId
        {
            get
            {
                return productId;
            }

            set
            {
                productId = value;
            }
        }

        public UIElement? RootVisual { get; set; }

        /// <summary>Silverlight host. Singleton on the Application instance.</summary>
        public SilverlightHost Host { get; } = new();

        /// <summary>
        /// Silverlight's per-application service container; XAML populates it from
        /// <c>&lt;Application.ApplicationLifetimeObjects&gt;</c>. Typically holds a
        /// <c>PhoneApplicationService</c> on WP, possibly with other lifetime objects.
        /// </summary>
        public IList<object> ApplicationLifetimeObjects { get; } = new List<object>();

        private ResourceDictionary _Resources;
        private string? productId;

        /// <summary>
        /// Public so user-code App classes (e.g. <c>MyApp.App : Application</c>) can call this
        /// via implicit <c>: base()</c> across the assembly boundary. The newest-constructed
        /// instance becomes <see cref="Current"/>, matching real Silverlight behaviour where
        /// the user's App class registers itself as the singleton.
        /// </summary>
        public Application()
        {
            _Resources = new ResourceDictionary();

            // Apply the user's chosen accent color (from desktop settings) before
            // PhoneTheme runs — the theme builds PhoneAccentBrush/Color and the
            // text-accent style from this value, so it has to be set first.
            // Parsing failures fall back to the WP7 default (Cyan).
            string? hex = WPR.Common.Configuration.Current?.AccentColor;
            if (!string.IsNullOrWhiteSpace(hex) && TryParseAccentColor(hex, out var c))
                WPR.SilverlightCompability.PhoneTheme.AccentColorOverride = c;

            // Seed the WP7 default-theme resources up-front so user XAML's
            // {StaticResource PhoneForegroundBrush} etc. find a value. App.xaml
            // entries land in _Resources later via WireFields/LoadComponent and
            // win over these defaults (PhoneTheme.Apply only adds missing keys).
            WPR.SilverlightCompability.PhoneTheme.Apply(_Resources);
            _Current = this;

            // Bridge for the SilverlightCompability XAML reader's StaticResource
            // resolver — it can't reference us directly (would be a circular
            // project ref), so we register a callback that exposes our Resources.
            WPR.SilverlightCompability.XamlReader.ApplicationResourceLookup = key =>
            {
                if (_Current is Application app && app._Resources.TryGetValue(key, out var v))
                    return v;
                return null;
            };
        }

        public static Application Current
        {
            get
            {
                if (_Current == null)
                {
                    _Current = new Application();
                }
                return _Current;
            }
        }

        /// <summary>Drops the cached singleton so a future Boot starts clean. Used at app shutdown.</summary>
        public static void ResetCurrent()
        {
            _Current = null;
        }

        /// <summary>
        /// Parse "#AARRGGBB" or "#RRGGBB" hex into a <see cref="WPR.SilverlightCompability.Color"/>.
        /// Returns false (with <paramref name="color"/> default) on malformed input rather
        /// than throwing — the caller falls back to the WP7 default accent.
        /// </summary>
        private static bool TryParseAccentColor(string hex, out WPR.SilverlightCompability.Color color)
        {
            color = default;
            string s = hex.Trim();
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
            if (s.Length != 6 && s.Length != 8) return false;
            if (!uint.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out uint v))
                return false;
            byte a, r, g, b;
            if (s.Length == 8)
            {
                a = (byte)((v >> 24) & 0xFF);
                r = (byte)((v >> 16) & 0xFF);
                g = (byte)((v >> 8) & 0xFF);
                b = (byte)(v & 0xFF);
            }
            else // 6 hex digits → assume opaque
            {
                a = 0xFF;
                r = (byte)((v >> 16) & 0xFF);
                g = (byte)((v >> 8) & 0xFF);
                b = (byte)(v & 0xFF);
            }
            color = WPR.SilverlightCompability.Color.FromArgb(a, r, g, b);
            return true;
        }

        public ResourceDictionary Resources
        {
            get
            {
                return _Resources;
            }
        }

        /// <summary>
        /// Loads a XAML resource by URI and merges it into an existing component instance.
        /// Mirrors <c>System.Windows.Application.LoadComponent</c>: the component's type is
        /// expected to match the XAML root's <c>x:Class</c>; attributes and children apply
        /// to the supplied <paramref name="component"/>, and <c>x:Name</c>'d elements are
        /// reflected onto matching fields on the component.
        /// </summary>
        public static void LoadComponent(object component, Uri resourceLocator)
        {
            if (component == null) throw new ArgumentNullException(nameof(component));
            if (resourceLocator == null) throw new ArgumentNullException(nameof(resourceLocator));

            StreamResourceInfo? info = GetResourceStream(resourceLocator, component.GetType().Assembly);
            if (info?.Stream == null)
                throw new InvalidOperationException(
                    $"XAML resource '{resourceLocator}' not found in assembly '{component.GetType().Assembly.GetName().Name}'.");

            string xaml;
            using (var reader = new StreamReader(info.Stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                xaml = reader.ReadToEnd();

            XamlReader.LoadComponent(component, xaml);
        }

        /// <summary>
        /// Locates an embedded resource (XAML, image, etc.) by relative or absolute URI.
        /// Searches the assembly's manifest resources for a matching name.
        /// </summary>
        public static StreamResourceInfo? GetResourceStream(Uri uri) =>
            GetResourceStream(uri, Assembly.GetCallingAssembly());

        public static StreamResourceInfo? GetResourceStream(Uri uri, Assembly assembly)
        {
            if (uri == null || assembly == null) return null;

            string path = uri.OriginalString.TrimStart('/');

            // Silverlight pack URI: "AssemblyName;component/Path/To.xaml" → strip the prefix.
            int compIdx = path.IndexOf(";component/", StringComparison.OrdinalIgnoreCase);
            if (compIdx >= 0)
                path = path.Substring(compIdx + ";component/".Length);

            string normalized = path.Replace('\\', '/');
            string suffix = "/" + normalized;

            // Pass 1: top-level manifest resources matched by name.
            string[] names = assembly.GetManifestResourceNames();
            string? exact = null;
            string? suffixMatch = null;
            foreach (string n in names)
            {
                string asPath = n.Replace('\\', '/');
                if (asPath.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    exact = n;
                    break;
                }
                if (asPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                    asPath.EndsWith(normalized.Replace('/', '.'), StringComparison.OrdinalIgnoreCase))
                {
                    suffixMatch ??= n;
                }
            }

            string? match = exact ?? suffixMatch;
            if (match != null)
            {
                Stream? s = assembly.GetManifestResourceStream(match);
                if (s != null) return MakeInfo(s, path);
            }

            // Pass 2: WPF/Silverlight bundles XAML (and other build-time resources) inside a
            // <AssemblyName>.g.resources file — a System.Resources.ResourceReader bundle. Look
            // through every *.g.resources resource for an entry matching our key.
            foreach (string n in names)
            {
                if (!n.EndsWith(".g.resources", StringComparison.OrdinalIgnoreCase)) continue;

                Stream? bundleStream = assembly.GetManifestResourceStream(n);
                if (bundleStream == null) continue;

                Stream? inner = ResourceBundleReader.FindEntry(bundleStream, normalized);
                if (inner != null) return MakeInfo(inner, path);
            }

            return null;
        }

        private static StreamResourceInfo MakeInfo(Stream s, string path) => new()
        {
            Stream = s,
            ContentType = path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                ? "application/xaml+xml"
                : null,
        };
    }
}
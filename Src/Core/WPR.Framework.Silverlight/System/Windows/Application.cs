using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
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

        /// <summary>
        /// Silverlight's application-started event, raised once after the App is constructed.
        /// </summary>
        /// <remarks>
        /// Distinct from WP7's own <c>PhoneApplicationService.Launching</c>, and a game may use
        /// either — Little Acorns uses this one, and does its whole first-run setup in the
        /// handler, so without the event it started with nothing initialised.
        /// </remarks>
        public event StartupEventHandler? Startup;

        /// <summary>
        /// Exit is raised on the way down. Paired with <see cref="Startup"/> because a title
        /// that saves from one usually loads from the other.
        /// </summary>
        public event EventHandler? Exit;

        /// <summary>
        /// Host hook: raise <see cref="Startup"/>. Called once by the mixed-mode host after the
        /// App subclass exists and before the first navigation — the point at which Silverlight
        /// raised it, and before the page whose handler may depend on it is built.
        /// </summary>
        internal void RaiseStartup()
        {
            Startup?.Invoke(this, new StartupEventArgs());
        }

        /// <summary>Host hook: raise <see cref="Exit"/>.</summary>
        internal void RaiseExit()
        {
            Exit?.Invoke(this, EventArgs.Empty);
        }

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
        /// <remarks>
        /// <b>Non-generic <see cref="System.Collections.IList"/> on purpose</b> — that is
        /// Silverlight's signature, and the return type is part of the method signature a game's
        /// IL binds. This was <c>IList&lt;object&gt;</c> until 2026-09-21, which no WPR code
        /// noticed (nothing but doc comments referenced it) and which every game that touches
        /// this property failed on: Cut the Rope's generated <c>App.InitializeXnaApplication</c>
        /// calls <c>get_ApplicationLifetimeObjects()</c> expecting <c>IList</c> and died with
        /// MissingMethodException before its App ctor had finished. <c>List&lt;object&gt;</c>
        /// implements both, so the backing store is unchanged.
        /// </remarks>
        public System.Collections.IList ApplicationLifetimeObjects { get; } = new List<object>();

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

            // Bridge so an exception escaping the game loop reaches this app's
            // UnhandledException handler, which is how a WP7 Silverlight title quits: it throws a
            // private exception, does its save-on-close work in the handler, and leaves Handled
            // false so the shell terminates it. Registered here rather than the other way round
            // for the same reason as the XamlReader bridge below — WPR.Framework.Xna cannot
            // reference this assembly. Cleared by ResetCurrent.
            WPR.Xna.Rhi.GameUnhandledException.Reporter = RaiseUnhandledException;

            // Bridge for the SilverlightCompability XAML reader's StaticResource
            // resolver — it can't reference us directly (would be a circular
            // project ref), so we register a callback that exposes our Resources.
            WPR.SilverlightCompability.XamlReader.ApplicationResourceLookup = key =>
            {
                // Deep, so an app-level StaticResource finds a key that lives in one of App.xaml's
                // merged theme dictionaries rather than inline.
                if (_Current is Application app && app._Resources.TryGetDeep(key, out var v))
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

        /// <summary>
        /// Host hook: offer an exception that escaped the game loop to this app's
        /// <see cref="UnhandledException"/> handler, and report back whether the game claimed it.
        ///
        /// <para>Returns true when the game set <c>Handled</c>, or when it has no handler at all —
        /// only a title that explicitly opts into the unhandled-exception contract and then
        /// declines to handle something is asking to be terminated. That is the WP7 quit idiom,
        /// and keeping the no-handler case non-fatal is what stops this changing behaviour for
        /// titles that merely throw and carry on.</para>
        /// </summary>
        private bool RaiseUnhandledException(Exception exception)
        {
            EventHandler<ApplicationUnhandledExceptionEventArgs>? handler = UnhandledException;
            if (handler == null)
            {
                return true;
            }

            ApplicationUnhandledExceptionEventArgs args =
                new ApplicationUnhandledExceptionEventArgs(exception, false);
            handler(this, args);
            if (args.Handled)
            {
                return true;
            }

            /* Unhandled. WP7 would terminate here — but taking that literally would change
             * behaviour for every Silverlight title, because the stock WP7 project template
             * subscribes this event with a body that does nothing but `if (Debugger.IsAttached)
             * Debugger.Break();`. Measured: ALL TWELVE Silverlight titles installed here carry
             * that subscription, so a single stray NullReferenceException out of Update would
             * start closing games that currently limp along and stay diagnosable.
             *
             * So terminate only for an exception the GAME ITSELF DEFINED. That is exactly the
             * shape of the quit idiom — you cannot signal "the user chose to exit" with an NRE,
             * so titles declare a private type for it (Cut the Rope's QuitException) — while
             * ordinary bugs surface as BCL exceptions and stay non-fatal, as they are today. */
            return !IsDefinedByTheGame(exception.GetType());
        }

        /// <summary>
        /// True when this type comes from a game assembly rather than the BCL or WPR's own shims.
        ///
        /// <para>Game code is loaded into its own <see cref="AssemblyLoadContext"/> by
        /// <c>ApplicationLaunch</c>, which is a far more reliable test than matching namespaces or
        /// assembly names. Anything uncertain answers false, so the fallback is always the
        /// long-standing "swallow and keep running".</para>
        /// </summary>
        private static bool IsDefinedByTheGame(Type exceptionType)
        {
            try
            {
                Assembly declaring = exceptionType.Assembly;
                AssemblyLoadContext? context = AssemblyLoadContext.GetLoadContext(declaring);
                return context != null && context != AssemblyLoadContext.Default;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Drops the cached singleton so a future Boot starts clean. Used at app shutdown.</summary>
        public static void ResetCurrent()
        {
            // Must clear with the singleton: these are statics in WPR.Framework.Xna, which is NOT
            // in the game's ALC, so on the desktop head a stale delegate would outlive this launch
            // and keep the previous game's Application (and its assemblies) alive — and a pending
            // termination would exit the NEXT game on its first tick.
            WPR.Xna.Rhi.GameUnhandledException.Reset();
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
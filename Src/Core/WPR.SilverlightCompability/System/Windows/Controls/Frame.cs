using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Text;
using System.Xml.Linq;

namespace WPR.SilverlightCompability
{
    public class Frame : FrameworkElement
    {
        protected readonly Dictionary<string, Type> _pageRegistry =
            new(StringComparer.OrdinalIgnoreCase);

        protected readonly Stack<JournalEntry> _backStack = new();
        protected readonly Stack<JournalEntry> _forwardStack = new();

        protected PhoneApplicationPage? _currentPage;
        protected Uri? _currentUri;

        public Frame()
        {
            NavigationService = new NavigationService(this);
        }

        public NavigationService NavigationService { get; }

        /// <summary>
        /// Optional URI rewriter applied to every <see cref="Navigate(Uri)"/> target, mirroring
        /// WP's <c>Frame.UriMapper</c>. The WP app template assigns one in
        /// <c>InitializePhoneApplication</c> (e.g. AC Pirates' <c>AssociationUriMapper</c>), so
        /// the property must exist and be honoured for the app's navigation to resolve the same
        /// pages it would on-device. Lives in the <c>Microsoft.Phone</c> facade because that's
        /// the assembly WP user code references it from.
        /// </summary>
        public System.Windows.Navigation.UriMapperBase? UriMapper { get; set; }

        public Uri? Source
        {
            get => _currentUri;
            set { if (value != null) Navigate(value); }
        }

        public Uri? CurrentSource => _currentUri;

        public object? Content => _currentPage;

        public bool CanGoBack => _backStack.Count > 0;
        public bool CanGoForward => _forwardStack.Count > 0;

        public IEnumerable<JournalEntry> BackStack => _backStack.ToArray();
        public IEnumerable<JournalEntry> ForwardStack => _forwardStack.ToArray();

        // Use the WP-spec delegate types (not generic EventHandler<>) so IL emitted by user
        // assemblies — which expects e.g. add_Navigated(NavigatedEventHandler) — binds correctly.
        public event NavigatingCancelEventHandler? Navigating;
        public event NavigatedEventHandler? Navigated;
        public event NavigationFailedEventHandler? NavigationFailed;
        public event NavigationStoppedEventHandler? NavigationStopped;

        public void RegisterPage(string name, Type pageType)
        {
            if (!typeof(PhoneApplicationPage).IsAssignableFrom(pageType))
                throw new ArgumentException(
                    $"{pageType.FullName} is not a PhoneApplicationPage", nameof(pageType));
            _pageRegistry[name] = pageType;
        }

        public bool Navigate(Uri source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            // Apply the app-supplied UriMapper the same way WP does before resolving a page.
            // A mapper returns the original URI when no rule matches, so this is a no-op for
            // normal launches and only rewrites friendly/deep-link URIs. If the guest's mapper
            // throws — typically because its body touches a WP API we don't shim yet (AC Pirates'
            // AssociationUriMapper calls System.Net.HttpUtility, which isn't provided) — fall
            // back to the original target so a special-case rewrite can't break a normal launch.
            if (UriMapper != null)
            {
                try
                {
                    Uri mapped = UriMapper.MapUri(source);
                    if (mapped != null) source = mapped;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[UriMapper] {UriMapper.GetType().Name}.MapUri('{source}') " +
                                      $"threw {ex.GetType().Name}: {ex.Message} — using unmapped URI.");
                }
            }

            var navigatingArgs = new NavigatingCancelEventArgs(source, NavigationMode.New);
            Navigating?.Invoke(this, navigatingArgs);
            if (navigatingArgs.Cancel) return false;

            PhoneApplicationPage page;
            try
            {
                page = ResolvePage(source);
            }
            catch (Exception ex)
            {
                var failedArgs = new NavigationFailedEventArgs(source, ex);
                NavigationFailed?.Invoke(this, failedArgs);
                if (!failedArgs.Handled) throw;
                return false;
            }

            DoNavigate(page, source, NavigationMode.New, pushCurrentToBack: true);
            return true;
        }

        public void GoBack()
        {
            if (!CanGoBack)
                throw new InvalidOperationException("Back stack is empty.");

            var entry = _backStack.Pop();

            if (_currentUri != null)
                _forwardStack.Push(new JournalEntry(_currentUri));

            var page = ResolvePage(entry.Source);
            DoNavigate(page, entry.Source, NavigationMode.Back, pushCurrentToBack: false);
        }

        public void GoForward()
        {
            if (!CanGoForward)
                throw new InvalidOperationException("Forward stack is empty.");

            var entry = _forwardStack.Pop();

            if (_currentUri != null)
                _backStack.Push(new JournalEntry(_currentUri));

            var page = ResolvePage(entry.Source);
            DoNavigate(page, entry.Source, NavigationMode.Forward, pushCurrentToBack: false);
        }

        public void StopLoading()
        {
            NavigationStopped?.Invoke(this, new NavigationEventArgs(_currentPage, _currentUri, NavigationMode.New));
        }

        /// <summary>
        /// Removes the most recent entry from the back stack and returns it, or <c>null</c> when
        /// the stack is empty. Matches WP semantics (returns null rather than throwing) — the
        /// standard app template's <c>ClearBackStackAfterReset</c> drains the stack with
        /// <c>while (RootFrame.RemoveBackEntry() != null) { }</c>.
        /// </summary>
        public JournalEntry? RemoveBackEntry()
        {
            return _backStack.Count > 0 ? _backStack.Pop() : null;
        }

        protected PhoneApplicationPage ResolvePage(Uri source)
        {
            string key = source.OriginalString.Trim();
            if (key.StartsWith("/", StringComparison.Ordinal))
                key = key.Substring(1);
            if (key.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                key = key.Substring(0, key.Length - 5);

            // Explicit registration wins.
            if (_pageRegistry.TryGetValue(key, out var registered))
                return (PhoneApplicationPage)Activator.CreateInstance(registered)!;

            // Standard WP behaviour: navigate by XAML resource URI. Locate the embedded XAML
            // resource in the user assembly, read its x:Class to find the page type, instantiate.
            PhoneApplicationPage? page = TryResolveFromXamlResource(source);
            if (page != null) return page;

            throw new InvalidOperationException($"No page registered for navigation key '{key}' (Uri: {source})");
        }

        /// <summary>
        /// Locate the XAML resource at <paramref name="source"/> inside the user assembly,
        /// parse the root element's <c>x:Class</c> attribute, and instantiate that type.
        /// Returns null if any step fails so the caller can fall back to a clearer error.
        /// </summary>
        private static PhoneApplicationPage? TryResolveFromXamlResource(Uri source)
        {
            Assembly? userAsm = HostContext.UserAssembly;
            if (userAsm == null) return null;

            string xaml = ReadEmbeddedXaml(source, userAsm);
            if (xaml == null) return null;

            string? className = TryReadClassAttribute(xaml);
            if (string.IsNullOrEmpty(className)) return null;

            Type? pageType = userAsm.GetType(className, throwOnError: false)
                             ?? Type.GetType(className!, throwOnError: false);
            if (pageType == null) return null;
            if (!typeof(PhoneApplicationPage).IsAssignableFrom(pageType)) return null;

            try
            {
                return (PhoneApplicationPage?)Activator.CreateInstance(pageType);
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                // Surface a more informative crash so we can see WHICH call inside the user's
                // ctor blew up. The default trace just says "at MainPage..ctor()" with no
                // intermediate frames, which is useless for diagnosis.
                var inner = tie.InnerException;
                var sb = new StringBuilder();
                sb.AppendLine($"Activator.CreateInstance({pageType.FullName}) failed.");
                int depth = 0;
                for (Exception? e = inner; e != null; e = e.InnerException, depth++)
                {
                    sb.AppendLine($"--- Inner #{depth} ---");
                    sb.AppendLine($"Type: {e.GetType().FullName}");
                    sb.AppendLine($"HResult: 0x{e.HResult:X8}");
                    sb.AppendLine($"Message: {e.Message}");
                    sb.AppendLine($"Source: {e.Source}");
                    sb.AppendLine($"TargetSite: {e.TargetSite?.DeclaringType?.FullName}.{e.TargetSite?.Name} " +
                                  $"(Module: {e.TargetSite?.Module?.Name})");
                    sb.AppendLine($"StackTrace:\n{e.StackTrace}");
                }
                throw new InvalidOperationException(sb.ToString(), inner);
            }
        }

        private static string? ReadEmbeddedXaml(Uri source, Assembly userAsm)
        {
            string path = source.OriginalString.TrimStart('/');
            int compIdx = path.IndexOf(";component/", StringComparison.OrdinalIgnoreCase);
            if (compIdx >= 0) path = path.Substring(compIdx + ";component/".Length);
            string normalized = path.Replace('\\', '/');
            string suffix = "/" + normalized;
            string dottedSuffix = "." + normalized.Replace('/', '.');

            // Pass 1: top-level manifest resources matched by name.
            string[] manifestNames = userAsm.GetManifestResourceNames();
            string? match = null;
            foreach (string n in manifestNames)
            {
                string asPath = n.Replace('\\', '/');
                if (asPath.Equals(normalized, StringComparison.OrdinalIgnoreCase)) { match = n; break; }
                if (asPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                    asPath.EndsWith(dottedSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    match ??= n;
                }
            }

            if (match != null)
            {
                using Stream? s = userAsm.GetManifestResourceStream(match);
                if (s != null)
                {
                    using var reader = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    return reader.ReadToEnd();
                }
            }

            // Pass 2: Silverlight/WPF stash XAML inside <AsmName>.g.resources as inner entries
            // keyed by lowercase relative path (e.g. "mainpage.xaml"). Open the .g.resources file
            // with a ResourceReader and look up the entry.
            foreach (string n in manifestNames)
            {
                if (!n.EndsWith(".g.resources", StringComparison.OrdinalIgnoreCase)) continue;

                using Stream? s = userAsm.GetManifestResourceStream(n);
                if (s == null) continue;
                try
                {
                    using var rr = new ResourceReader(s);
                    foreach (DictionaryEntry e in rr)
                    {
                        if (e.Key is not string key) continue;
                        string keyPath = key.Replace('\\', '/');
                        bool isMatch =
                            keyPath.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                            keyPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                            keyPath.Equals(Path.GetFileName(normalized), StringComparison.OrdinalIgnoreCase);
                        if (!isMatch) continue;

                        // Read the inner blob using the typed accessor — gives us the raw stream/bytes
                        // for arbitrary types stored in the .resources container.
                        rr.GetResourceData(key, out _, out byte[] data);
                        return DecodeResourceBlob(data);
                    }
                }
                catch
                {
                    // Bad/incompatible .resources — skip and try the next.
                }
            }

            return null;
        }

        /// <summary>
        /// .NET's resource container wraps payloads with a small type prefix; for stream-typed
        /// resources we get a 7-bit-encoded length followed by the bytes. Try a few common
        /// shapes (raw text, ResourceTypeCode.Stream, ResourceTypeCode.Byte[]) and return the
        /// XAML text if any decodes to readable XML.
        /// </summary>
        private static string? DecodeResourceBlob(byte[] data)
        {
            if (data == null || data.Length == 0) return null;

            // Strategy: scan for the first '<' and try to decode as UTF-8 from there. Silverlight
            // .g.resources entries are stored as MemoryStream-wrapped XAML; the leading bytes are
            // type metadata + 7-bit-encoded length. The XAML content starts with '<'.
            int start = -1;
            for (int i = 0; i < Math.Min(data.Length, 32); i++)
            {
                if (data[i] == (byte)'<') { start = i; break; }
            }
            if (start < 0) return null;

            try
            {
                return Encoding.UTF8.GetString(data, start, data.Length - start);
            }
            catch
            {
                return null;
            }
        }

        private static string? TryReadClassAttribute(string xaml)
        {
            try
            {
                var doc = XDocument.Parse(xaml);
                var attr = doc.Root?.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"));
                return attr?.Value;
            }
            catch { return null; }
        }

        protected void DoNavigate(PhoneApplicationPage newPage, Uri newUri, NavigationMode mode, bool pushCurrentToBack)
        {
            var oldPage = _currentPage;
            var oldUri = _currentUri;

            if (oldPage != null)
                oldPage.OnNavigatingFrom(new NavigatingCancelEventArgs(newUri, mode));

            if (pushCurrentToBack && oldUri != null)
            {
                _backStack.Push(new JournalEntry(oldUri));
                _forwardStack.Clear();
            }

            if (oldPage != null)
            {
                oldPage.OnNavigatedFrom(new NavigationEventArgs(oldPage, oldUri, mode));
                oldPage.NavigationService = null;
            }

            _currentPage = newPage;
            _currentUri = newUri;
            newPage.NavigationService = NavigationService;

            // If OnNavigatedTo throws (e.g., the page's override calls a method
            // our shims don't yet expose — Minesweeper's HelpOptionsPage hit
            // MissingMethodException on Control.set_IsEnabled), don't leave the
            // frame half-committed: every subsequent tap on the still-visible
            // old page would otherwise see `_currentPage` pointing at the
            // broken new page and crash all over again. Roll the page pointer
            // back to the previous state and rethrow so the host handler logs
            // the original exception.
            try
            {
                newPage.OnNavigatedTo(new NavigationEventArgs(newPage, newUri, mode));
            }
            catch
            {
                _currentPage = oldPage;
                _currentUri = oldUri;
                newPage.NavigationService = null;
                if (oldPage != null) oldPage.NavigationService = NavigationService;
                if (pushCurrentToBack && oldUri != null && _backStack.Count > 0)
                {
                    // Undo the back-stack push we did earlier so the journal
                    // doesn't list a page we never actually displayed.
                    _backStack.Pop();
                }
                throw;
            }
            Navigated?.Invoke(this, new NavigationEventArgs(newPage, newUri, mode));

            // Fire Loaded on the new page's tree. Real SL fires this once the
            // element has entered the visual tree and the first layout pass has
            // run; for our virtual visual tree we approximate by raising right
            // after navigation completes. Games rely on this to kick off any
            // background work and dismiss splash overlays — without it
            // navigation-gating logic that checks for an open splash popup will
            // block every subsequent tap (Minesweeper's MainPage is the canonical
            // case — see RaiseLoadedTree's remarks).
            FrameworkElement.RaiseLoadedTree(newPage);
        }
    }
}

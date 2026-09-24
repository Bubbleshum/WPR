using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

// Namespace deliberately kept as WPR.WindowsCompability. Originally that was so
// FrameworkElement.Resources could return this type without a circular project reference;
// since 2026-08-30 it is also what let the whole WPR.WindowsCompability project be dissolved
// into this one without touching a single NewNamespace string in ApplicationPatcher.
//
// NOTE: the [TypeForwardedTo] that used to live in the WPR.WindowsCompability assembly is
// GONE, because that assembly is gone. Patched user IL naming it no longer resolves at all —
// ApplicationPatcher.Version 18 is the tripwire that forces such installs to be repatched.
namespace WPR.WindowsCompability
{
    /// <summary>
    /// XAML resource bag. Originally <c>System.Windows.ResourceDictionary</c>; the
    /// patcher rewrites user-IL refs to land here. <see cref="System.Collections.IDictionary"/>
    /// shape so our XAML loader can populate by <c>x:Key</c>.
    /// </summary>
    public class ResourceDictionary : Dictionary<string, object?>
    {
        /// <summary>
        /// Other dictionaries merged into this one — the standard way an app splits its resources
        /// across files and pulls them together in App.xaml.
        /// </summary>
        /// <remarks>
        /// <b>Read-only on purpose</b>, which is what makes the XAML reader treat it as a
        /// collection and append to it. Declared writable (or not at all) it is taken for a
        /// single-valued property, and an App.xaml that merges more than one file fails with
        /// "had N child elements" — taking the whole application down at construction, not just
        /// its resources. Carcassonne merges six.
        ///
        /// <para>Lookups DO fall through to the merged dictionaries — see
        /// <see cref="TryGetDeep"/>. Anything reading a resource should go through that rather than
        /// the base <c>Dictionary.TryGetValue</c>, which only sees this file's own keys.</para>
        /// </remarks>
        public List<ResourceDictionary> MergedDictionaries { get; } = new List<ResourceDictionary>();

        /// <summary>Guards a dictionary that merges itself, directly or in a cycle.</summary>
        private const int MaxMergeDepth = 16;

        private Uri? _source;

        /// <summary>
        /// The XAML file this dictionary's contents come from — Silverlight's
        /// <c>&lt;ResourceDictionary Source="themes/generic.xaml"/&gt;</c>. Assigning it loads and
        /// merges that file immediately.
        /// </summary>
        /// <remarks>
        /// <para><b>Without this, every merged dictionary in an app is silently empty.</b> The XAML
        /// reader has nowhere to put an unknown member, so it logs "Skipping unknown member
        /// 'Source'" and carries on with a dictionary that parsed perfectly and contains nothing.
        /// The failure then surfaces wherever the app first reads a style — for Carcassonne, a
        /// <c>NullReferenceException</c> inside <c>App..ctor</c>, five files from the cause.</para>
        ///
        /// <para>Splitting resources across theme files and merging them in App.xaml is the normal
        /// Silverlight idiom, not an unusual one: Carcassonne merges five.</para>
        ///
        /// <para><b>A file that cannot be found is skipped, never thrown.</b> Resource loading is
        /// best-effort everywhere else in this shim, and an app that would have lost one style
        /// should not instead fail to construct.</para>
        /// </remarks>
        public Uri? Source
        {
            get => _source;
            set
            {
                _source = value;
                if (value != null) LoadFromSource(value);
            }
        }

        private void LoadFromSource(Uri source)
        {
            try
            {
                Assembly? asm = WPR.SilverlightCompability.HostContext.UserAssembly;
                if (asm == null) return;

                StreamResourceInfo? info = Application.GetResourceStream(source, asm);
                if (info?.Stream == null)
                {
                    Debug.WriteLine($"[ResourceDictionary] Source '{source}' not found; skipped.");
                    return;
                }

                string xaml;
                using (var reader = new StreamReader(info.Stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                    xaml = reader.ReadToEnd();

                // Populates THIS instance: the file's root element is itself a ResourceDictionary,
                // so its keyed children land in us and its own MergedDictionaries nest as usual.
                WPR.SilverlightCompability.XamlReader.LoadComponent(this, xaml);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ResourceDictionary] Source '{source}' failed to load: {ex.Message}");
            }
        }

        /// <summary>
        /// Looks <paramref name="key"/> up in this dictionary and then, on a miss, in each merged
        /// dictionary — depth first, last merge winning, which is Silverlight's own precedence.
        /// </summary>
        public bool TryGetDeep(string key, out object? value) => TryGetDeep(key, 0, out value);

        private bool TryGetDeep(string key, int depth, out object? value)
        {
            value = null;
            if (key == null || depth > MaxMergeDepth) return false;

            if (base.TryGetValue(key, out value)) return true;

            // Reverse order: a later merge overrides an earlier one, so it is searched first.
            for (int i = MergedDictionaries.Count - 1; i >= 0; i -= 1)
            {
                ResourceDictionary merged = MergedDictionaries[i];
                if (merged != null && merged.TryGetDeep(key, depth + 1, out value)) return true;
            }

            value = null;
            return false;
        }

        public bool Contains(object obj)
        {
            return obj is string s && TryGetDeep(s, out _);
        }

        public object? this[object obj]
        {
            get
            {
                if (obj is string s && TryGetDeep(s, out var v)) return v;
                return null;
            }
            set => base[(obj as string)!] = value;
        }

        /// <summary>
        /// Silverlight's object-keyed <c>Add</c>. The generic <c>Dictionary</c> base only offers
        /// <c>Add(string, object)</c>, so a game adding a resource by object key — which is the
        /// signature its IL names — got a MissingMethodException.
        /// </summary>
        /// <remarks>
        /// A non-string key is stored under its <c>ToString()</c> rather than rejected. Silverlight
        /// keys are <c>x:Key</c> strings in practice, and the alternative (throwing) would turn a
        /// resource a game never reads into a failed startup.
        /// </remarks>
        public void Add(object key, object? value)
        {
            if (key == null) return;
            base[key as string ?? key.ToString()!] = value;
        }

        /// <summary>Silverlight's object-keyed <c>Remove</c>, for symmetry with <see cref="Add"/>.</summary>
        public bool Remove(object key)
        {
            if (key == null) return false;
            return base.Remove(key as string ?? key.ToString()!);
        }
    }
}

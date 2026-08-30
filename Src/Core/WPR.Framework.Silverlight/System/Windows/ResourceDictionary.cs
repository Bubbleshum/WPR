using System.Collections.Generic;

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
        public bool Contains(object obj)
        {
            return base.ContainsKey((obj as string)!);
        }

        public object? this[object obj]
        {
            get
            {
                if (obj is string s && base.TryGetValue(s, out var v)) return v;
                return null;
            }
            set => base[(obj as string)!] = value;
        }
    }
}

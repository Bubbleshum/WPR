using System.Collections.Generic;

// Namespace deliberately kept as WPR.WindowsCompability so that:
//   - existing patched user IL whose typerefs say
//     `WPR.WindowsCompability.ResourceDictionary, WPR.WindowsCompability`
//     still works (via [TypeForwardedTo] in the WPR.WindowsCompability assembly),
//   - and FrameworkElement.Resources here can return the type without SLC needing
//     a project reference back to WindowsCompability (which would be circular).
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

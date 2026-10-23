using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml.Linq;
using WPR.WindowsCompability;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// A control's default style: the implicit style for its <c>DefaultStyleKey</c> in
    /// <c>themes/generic.xaml</c> of the assembly that defines that type, as Silverlight finds it.
    /// </summary>
    /// <remarks>
    /// Only its <c>Template</c> is used (see <see cref="Control.EnsureTemplateApplied"/>): the
    /// template is built so the control's own <c>OnApplyTemplate</c> finds its named parts. The
    /// other setters are not applied, because the renderers draw these controls themselves and a
    /// theme's margins or brushes would move what already looks right. Files are cached per
    /// assembly weakly, so a game's assemblies are not kept alive after it ends.
    /// </remarks>
    internal static class DefaultStyles
    {
        private const string XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

        private sealed class Entry { public XDocument? Document; }

        private static readonly ConditionalWeakTable<Assembly, Entry> Generic = new();

        /// <summary>The Template the default style for <paramref name="key"/> sets, or null.</summary>
        public static ControlTemplate? FindTemplate(Type key)
        {
            for (Style? style = FindStyle(key); style != null; style = style.BasedOn)
            {
                foreach (Setter setter in style.Setters)
                {
                    if (setter.Value is ControlTemplate template
                        && setter.Property is string name
                        && (name == "Template" || name.EndsWith(".Template", StringComparison.Ordinal)))
                        return template;
                }
            }
            return null;
        }

        // Only the one element is loaded: the reader keys a dictionary by x:Key, and generic.xaml
        // styles have none (Silverlight keys them by TargetType), so loading the whole file would
        // keep nothing. It also keeps a style for some other control from failing this one.
        private static Style? FindStyle(Type key)
        {
            XDocument? document = GenericOf(key.Assembly);
            if (document?.Root == null) return null;
            foreach (XElement element in document.Root.Elements())
            {
                if (element.Name.LocalName != "Style" || element.Attribute(XName.Get("Key", XamlNs)) != null) continue;
                if (!Names(element, element.Attribute("TargetType")?.Value, key)) continue;
                try { return XamlReader.LoadElement(element) as Style; }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine(
                        $"[wpr-template] default style for {key.Name} did not load ({ex.GetType().Name}: {ex.Message})");
                    return null;
                }
            }
            return null;
        }

        /// <summary>Whether a TargetType such as <c>controls:Pivot</c> names <paramref name="key"/>.</summary>
        private static bool Names(XElement scope, string? targetType, Type key)
        {
            if (string.IsNullOrEmpty(targetType)) return false;
            int colon = targetType.IndexOf(':');
            string local = colon < 0 ? targetType : targetType.Substring(colon + 1);
            if (local != key.Name) return false;
            if (colon < 0) return true;
            string xmlns = scope.GetNamespaceOfPrefix(targetType.Substring(0, colon))?.NamespaceName ?? "";
            const string clr = "clr-namespace:";
            if (!xmlns.StartsWith(clr, StringComparison.Ordinal)) return false;
            string ns = xmlns.Substring(clr.Length);
            int semi = ns.IndexOf(';');
            if (semi >= 0) ns = ns.Substring(0, semi);
            return ns == key.Namespace;
        }

        private static XDocument? GenericOf(Assembly assembly)
        {
            lock (Generic)
            {
                if (Generic.TryGetValue(assembly, out Entry? cached)) return cached.Document;
                var entry = new Entry { Document = Load(assembly) };
                Generic.Add(assembly, entry);
                return entry.Document;
            }
        }

        private static XDocument? Load(Assembly assembly)
        {
            string name = assembly.GetName().Name ?? "";
            try
            {
                StreamResourceInfo? info = Application.GetResourceStream(
                    new Uri("/" + name + ";component/themes/generic.xaml", UriKind.Relative), assembly);
                if (info?.Stream == null) return null;
                using var reader = new StreamReader(info.Stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                XDocument document = XDocument.Parse(reader.ReadToEnd());
                System.Diagnostics.Trace.WriteLine($"[wpr-template] {name}: themes/generic.xaml read");
                return document;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[wpr-template] {name}: themes/generic.xaml did not load ({ex.GetType().Name}: {ex.Message})");
                return null;
            }
        }
    }
}

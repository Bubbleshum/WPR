using System;
using System.Xml.Linq;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Controls.ControlTemplate</c>.</summary>
    /// <remarks>
    /// <para>Captures its deferred XAML exactly as <see cref="DataTemplate"/> does, and
    /// <b>WPR does not apply it</b> — the renderers draw a control's own content rather than a
    /// template's visual tree, and changing that is a much larger piece of work than this type.
    /// </para>
    ///
    /// <para><b>It exists because the TYPE has to resolve even when the template is never used.</b>
    /// A style that sets <c>Template</c> writes
    /// <c>&lt;Setter Property="Template"&gt;&lt;ControlTemplate&gt;…</c>, and the XAML reader
    /// resolves types by name: an unknown one throws out of the element declaring it and takes the
    /// whole style — often the whole resource dictionary — with it. Galactic Reign's MenuPage
    /// failed to construct for exactly this reason.</para>
    ///
    /// <para><see cref="LoadContent"/> is provided so a game that materialises a template itself
    /// gets something back rather than a null it will dereference.</para>
    /// </remarks>
    [ContentProperty(nameof(VisualTree))]
    public class ControlTemplate
    {
        /// <summary>The type this template is written for, from <c>TargetType</c>.</summary>
        public Type? TargetType { get; set; }

        /// <summary>The XML element that becomes the root of every materialised instance.</summary>
        public XElement? VisualTree { get; set; }

        public UIElement? LoadContent()
        {
            if (VisualTree == null) return null;

            object root = XamlReader.LoadElement(VisualTree);
            return root as UIElement;
        }
    }
}

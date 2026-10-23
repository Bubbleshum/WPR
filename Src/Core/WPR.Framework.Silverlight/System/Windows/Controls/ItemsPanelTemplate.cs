using System.Xml.Linq;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Controls.ItemsPanelTemplate</c>.</summary>
    /// <remarks>
    /// Captured deferred, like <see cref="ControlTemplate"/>, and not applied: WPR's
    /// <see cref="ItemsControl"/> is its own panel (a <see cref="StackPanel"/>), so an items panel
    /// a game names is stored and never used. It exists so the type resolves; Arkadium's
    /// <c>UniformGrid</c> could not be constructed without it.
    /// </remarks>
    [ContentProperty(nameof(VisualTree))]
    public class ItemsPanelTemplate
    {
        /// <summary>The XML element that becomes the panel's root.</summary>
        public XElement? VisualTree { get; set; }

        /// <summary>Builds the panel the template describes.</summary>
        public UIElement? LoadContent() =>
            VisualTree == null ? null : XamlReader.LoadElement(VisualTree) as UIElement;
    }
}

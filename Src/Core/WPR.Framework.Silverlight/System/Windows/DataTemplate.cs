using System;
using System.Xml.Linq;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// XAML-defined factory for instantiating UI per-data-item. The template captures a
    /// deferred XML root at parse time and re-runs it through <see cref="XamlReader"/>
    /// each time <see cref="LoadContent"/> is called.
    ///
    /// 1.5 caveat: only template instantiation is supported. No DataTemplateKey, no
    /// implicit selection by data type, no nested DataTemplates inside other templates.
    /// </summary>
    public class DataTemplate
    {
        /// <summary>The XML element that becomes the root of every materialized instance.</summary>
        public XElement? VisualTreeRoot { get; set; }

        public UIElement? LoadContent()
        {
            if (VisualTreeRoot == null) return null;
            object root = XamlReader.LoadElement(VisualTreeRoot);
            if (root is UIElement el) return el;
            throw new XamlParseException(
                $"DataTemplate root must be a UIElement (got '{root.GetType().Name}').");
        }
    }
}

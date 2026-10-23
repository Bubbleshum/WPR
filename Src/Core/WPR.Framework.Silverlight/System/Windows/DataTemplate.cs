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

        /// <summary>The page or control whose XAML declared this template; it answers the event handlers the template names.</summary>
        internal object? HandlerHost { get; set; }

        /// <summary>The elements this template was declared inside; their resources stay in reach.</summary>
        internal FrameworkElement[]? Scopes { get; set; }

        /// <remarks>Returns <see cref="DependencyObject"/>, Silverlight's signature: game IL binds
        /// it by return type (YouTube's item templates), and a <c>UIElement</c> return was a
        /// MissingMethodException that took the app down at start-up.</remarks>
        public DependencyObject? LoadContent()
        {
            if (VisualTreeRoot == null) return null;
            object root = XamlReader.LoadElement(VisualTreeRoot, HandlerHost, Scopes);
            if (root is DependencyObject d) return d;
            throw new XamlParseException(
                $"DataTemplate root must be a DependencyObject (got '{root.GetType().Name}').");
        }
    }
}

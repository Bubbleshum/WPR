using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using NetXml = System.Xml;

namespace Windows.Data.Xml.Dom
{
    /// <summary>
    /// Shim for <c>Windows.Data.Xml.Dom.IXmlNode</c>. WPR's DOM wraps .NET's; the members are the
    /// ones titles were measured to use (toast templates, mostly) plus their obvious neighbours.
    /// </summary>
    public interface IXmlNode
    {
        string NodeName { get; }

        object? NodeValue { get; }

        string InnerText { get; }

        IXmlNode? ParentNode { get; }

        IXmlNode? FirstChild { get; }

        XmlNodeList ChildNodes { get; }

        XmlDocument? OwnerDocument { get; }

        IXmlNode AppendChild(IXmlNode newChild);

        IXmlNode RemoveChild(IXmlNode child);

        IXmlNode? SelectSingleNode(string xpath);

        XmlNodeList SelectNodes(string xpath);

        string GetXml();
    }

    /// <summary>The shared implementation of WPR's nodes.</summary>
    public class XmlNodeBase : IXmlNode
    {
        internal XmlNodeBase(NetXml.XmlNode node) => Node = node;

        internal NetXml.XmlNode Node { get; }

        internal static IXmlNode? Wrap(NetXml.XmlNode? node) => node switch
        {
            null => null,
            NetXml.XmlDocument d => new XmlDocument(d),
            NetXml.XmlElement e => new XmlElement(e),
            NetXml.XmlText t => new XmlText(t),
            _ => new XmlNodeBase(node),
        };

        public string NodeName => Node.Name;

        public object? NodeValue => Node.Value;

        public void put_NodeValue(object? value) => Node.Value = value?.ToString();

        public string InnerText => Node.InnerText;

        public void put_InnerText(string value) => Node.InnerText = value;

        public IXmlNode? ParentNode => Wrap(Node.ParentNode);

        public IXmlNode? FirstChild => Wrap(Node.FirstChild);

        public IXmlNode? LastChild => Wrap(Node.LastChild);

        public XmlNodeList ChildNodes => new(Node.ChildNodes);

        public XmlDocument? OwnerDocument => Node.OwnerDocument is { } d ? new XmlDocument(d) : null;

        public IXmlNode AppendChild(IXmlNode newChild)
        {
            Node.AppendChild(((XmlNodeBase)newChild).Node);
            return newChild;
        }

        public IXmlNode RemoveChild(IXmlNode child)
        {
            Node.RemoveChild(((XmlNodeBase)child).Node);
            return child;
        }

        public IXmlNode? SelectSingleNode(string xpath) => Wrap(Node.SelectSingleNode(xpath));

        public XmlNodeList SelectNodes(string xpath) => new(Node.SelectNodes(xpath)!);

        public string GetXml() => Node.OuterXml;

        public override bool Equals(object? obj) => obj is XmlNodeBase other && ReferenceEquals(other.Node, Node);

        public override int GetHashCode() => Node.GetHashCode();
    }

    /// <summary>Shim for <c>Windows.Data.Xml.Dom.XmlDocument</c>.</summary>
    public sealed class XmlDocument : XmlNodeBase
    {
        public XmlDocument() : this(new NetXml.XmlDocument())
        {
        }

        internal XmlDocument(NetXml.XmlDocument document) : base(document)
        {
        }

        private NetXml.XmlDocument Document => (NetXml.XmlDocument)Node;

        public XmlElement? DocumentElement => Document.DocumentElement is { } e ? new XmlElement(e) : null;

        public void LoadXml(string xml) => Document.LoadXml(xml);

        public XmlElement CreateElement(string tagName) => new(Document.CreateElement(tagName));

        public XmlText CreateTextNode(string data) => new(Document.CreateTextNode(data));

        public XmlNodeList GetElementsByTagName(string tagName) => new(Document.GetElementsByTagName(tagName));

        public XmlElement? GetElementById(string elementId) => Document.GetElementById(elementId) is { } e ? new XmlElement(e) : null;
    }

    /// <summary>Shim for <c>Windows.Data.Xml.Dom.XmlElement</c>.</summary>
    public sealed class XmlElement : XmlNodeBase
    {
        internal XmlElement(NetXml.XmlElement element) : base(element)
        {
        }

        private NetXml.XmlElement Element => (NetXml.XmlElement)Node;

        public string TagName => Element.Name;

        public string GetAttribute(string attributeName) => Element.GetAttribute(attributeName);

        public void SetAttribute(string attributeName, string attributeValue) => Element.SetAttribute(attributeName, attributeValue);

        public void RemoveAttribute(string attributeName) => Element.RemoveAttribute(attributeName);

        public XmlNodeList GetElementsByTagName(string tagName) => new(Element.GetElementsByTagName(tagName));
    }

    /// <summary>Shim for <c>Windows.Data.Xml.Dom.XmlText</c>.</summary>
    public sealed class XmlText : XmlNodeBase
    {
        internal XmlText(NetXml.XmlText text) : base(text)
        {
        }

        public string Data => Node.Value ?? string.Empty;

        public void put_Data(string value) => Node.Value = value;
    }

    /// <summary>Shim for <c>Windows.Data.Xml.Dom.XmlNodeList</c> (an <c>IVectorView&lt;IXmlNode&gt;</c>).</summary>
    public sealed class XmlNodeList : IReadOnlyList<IXmlNode>
    {
        private readonly NetXml.XmlNodeList _nodes;

        internal XmlNodeList(NetXml.XmlNodeList nodes) => _nodes = nodes;

        public uint Length => (uint)_nodes.Count;

        public int Count => _nodes.Count;

        public IXmlNode this[int index] => XmlNodeBase.Wrap(_nodes[index])!;

        public IEnumerator<IXmlNode> GetEnumerator()
        {
            for (int i = 0; i < _nodes.Count; i++) yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

namespace Windows.UI.Notifications
{
    using Windows.Data.Xml.Dom;

    /// <summary>Shim for <c>Windows.UI.Notifications.ToastTemplateType</c>.</summary>
    public enum ToastTemplateType
    {
        ToastImageAndText01 = 0,
        ToastImageAndText02 = 1,
        ToastImageAndText03 = 2,
        ToastImageAndText04 = 3,
        ToastText01 = 4,
        ToastText02 = 5,
        ToastText03 = 6,
        ToastText04 = 7,
    }

    /// <summary>Shim for <c>Windows.UI.Notifications.ToastNotification</c>.</summary>
    public sealed class ToastNotification
    {
        public ToastNotification(XmlDocument content) => Content = content;

        public XmlDocument Content { get; }

        public DateTimeOffset? ExpirationTime { get; private set; }

        public void put_ExpirationTime(DateTimeOffset? value) => ExpirationTime = value;
    }

    /// <summary>
    /// Shim for <c>Windows.UI.Notifications.ToastNotifier</c>. A WP8.1 toast is shown only while
    /// the app is NOT in front, so a title in front sees nothing either way; WPR logs it.
    /// </summary>
    public sealed class ToastNotifier
    {
        internal ToastNotifier()
        {
        }

        public void Show(ToastNotification notification) =>
            Trace.WriteLine("[wpr-winrt] toast: " + notification.Content.InnerText);

        public void Hide(ToastNotification notification)
        {
        }
    }

    /// <summary>Shim for <c>Windows.UI.Notifications.ToastNotificationManager</c>.</summary>
    public static class ToastNotificationManager
    {
        public static ToastNotifier CreateToastNotifier() => new();

        public static ToastNotifier CreateToastNotifier(string applicationId) => new();

        /// <summary>The template's XML, as Windows hands it out: text and image slots to fill in.</summary>
        public static XmlDocument GetTemplateContent(ToastTemplateType type)
        {
            int texts = type switch
            {
                ToastTemplateType.ToastText01 or ToastTemplateType.ToastImageAndText01 => 1,
                ToastTemplateType.ToastText02 or ToastTemplateType.ToastImageAndText02
                    or ToastTemplateType.ToastText03 or ToastTemplateType.ToastImageAndText03 => 2,
                _ => 3,
            };
            bool image = type <= ToastTemplateType.ToastImageAndText04;
            var xml = new global::System.Text.StringBuilder($"<toast><visual><binding template=\"{type}\">");
            if (image) xml.Append("<image id=\"1\" src=\"\"/>");
            for (int i = 1; i <= texts; i++) xml.Append($"<text id=\"{i}\"></text>");
            xml.Append("</binding></visual></toast>");

            var document = new XmlDocument();
            document.LoadXml(xml.ToString());
            return document;
        }
    }
}

using System;

namespace WPR.SilverlightCompability
{
    public class XamlParseException : Exception
    {
        public XamlParseException(string message) : base(message) { }
        public XamlParseException(string message, Exception inner) : base(message, inner) { }
    }
}

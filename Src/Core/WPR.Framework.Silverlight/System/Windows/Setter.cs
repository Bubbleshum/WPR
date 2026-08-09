using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Setter</c>. Property is the string
    /// name of a DependencyProperty/CLR property on the target type; Value is
    /// the value to assign (raw string from XAML or pre-converted object).</summary>
    public class Setter
    {
        public object? Property { get; set; }
        public object? Value { get; set; }
        public Setter() { }
        public Setter(object property, object? value) { Property = property; Value = value; }
    }
}

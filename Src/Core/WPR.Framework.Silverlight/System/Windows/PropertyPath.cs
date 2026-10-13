using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.PropertyPath</c>.
    /// Stores the dotted path; we don't resolve it (Bindings carry their own Path string).</summary>
    public class PropertyPath
    {
        public string Path { get; }
        public PropertyPath(string path) { Path = path; }
        public PropertyPath(object _) { Path = string.Empty; }

        /// <summary>
        /// Silverlight's parameterised form, <c>new PropertyPath("(0).(1)", dp0, dp1)</c>, and
        /// what the C# compiler emits for <c>new PropertyPath("(UIElement.Opacity)")</c> against
        /// the real assembly (an empty <c>params</c> array). Each <c>(n)</c> naming a
        /// <see cref="DependencyProperty"/> parameter is rewritten to <c>(Owner.Name)</c>, which
        /// is the form the storyboard path resolver reads.
        /// </summary>
        /// <remarks>Missing until 2026-10-13, so every storyboard Flowerz builds in code
        /// (bloom, fade, score) threw MissingMethodException from its Loaded handler.</remarks>
        public PropertyPath(string path, params object[] pathParameters)
        {
            string p = path ?? string.Empty;
            if (pathParameters != null)
            {
                for (int i = 0; i < pathParameters.Length; i++)
                {
                    if (pathParameters[i] is DependencyProperty dp)
                        p = p.Replace("(" + i + ")", "(" + dp.OwnerType.Name + "." + dp.Name + ")");
                }
            }
            Path = p;
        }
        public override string ToString() => Path;
    }
}

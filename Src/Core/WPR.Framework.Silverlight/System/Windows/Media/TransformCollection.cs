using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.TransformCollection</c>.</summary>
    /// <remarks>
    /// Must derive from <see cref="PresentationFrameworkCollection{T}"/>, as Silverlight's does:
    /// game code compiles <c>group.Children[0]</c> to a callvirt on
    /// <c>PresentationFrameworkCollection&lt;Transform&gt;::get_Item</c>. This used to be a
    /// <c>List&lt;Transform&gt;</c>, and the runtime ran that call against a List's field
    /// layout — an NRE with no mention of the collection (Flappy Bird's
    /// <c>UIElementRendererHelper</c>, black screen). Same trap as <c>UIElementCollection</c>.
    /// </remarks>
    public class TransformCollection : PresentationFrameworkCollection<Transform> { }
}

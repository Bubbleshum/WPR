using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Controls.ItemCollection</c>.
    /// In SL this is the runtime collection backing ItemsControl.Items.
    /// Inherits from <see cref="PresentationFrameworkCollection{T}"/> because
    /// user / toolkit IL emits <c>callvirt PresentationFrameworkCollection&lt;object&gt;::IndexOf</c>
    /// against it — if we used <c>List&lt;object&gt;</c> as the base, that virtual call
    /// would land in the wrong slot (or fail outright on a missing override).
    /// </summary>
    public class ItemCollection : PresentationFrameworkCollection<object> { }
}

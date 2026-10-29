using System;
using System.Collections.Generic;

namespace System.Windows.Input
{
    public class TouchDevice
    {
        public int Id { get; set; }
        public UIElement? DirectlyOver { get; set; }
    }
}

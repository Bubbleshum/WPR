using System;
using System.Collections.Generic;

namespace System.Windows.Input
{
    public class TouchPoint
    {
        public TouchAction Action { get; set; }
        public TouchDevice? TouchDevice { get; set; }
        public Point Position { get; set; }
        public Size Size { get; set; }
    }
}

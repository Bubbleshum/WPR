using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.FontWeights</c>. Standard named weights.</summary>
    public static class FontWeights
    {
        public static FontWeight Thin       => new FontWeight(100);
        public static FontWeight ExtraLight => new FontWeight(200);
        public static FontWeight Light      => new FontWeight(300);
        public static FontWeight Normal     => new FontWeight(400);
        public static FontWeight Medium     => new FontWeight(500);
        public static FontWeight SemiBold   => new FontWeight(600);
        public static FontWeight Bold       => new FontWeight(700);
        public static FontWeight ExtraBold  => new FontWeight(800);
        public static FontWeight Black      => new FontWeight(900);
    }
}

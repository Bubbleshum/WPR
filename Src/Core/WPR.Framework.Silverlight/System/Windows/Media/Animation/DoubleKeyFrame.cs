using System;
using System.Collections;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.Animation.DoubleKeyFrame</c>.</summary>
    public abstract class DoubleKeyFrame : DependencyObject
    {
        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(nameof(Value), typeof(double), typeof(DoubleKeyFrame),
                new PropertyMetadata((object)0.0));

        public static readonly DependencyProperty KeyTimeProperty =
            DependencyProperty.Register(nameof(KeyTime), typeof(KeyTime), typeof(DoubleKeyFrame),
                new PropertyMetadata(KeyTime.Uniform));

        public double Value
        {
            get => (double)GetValue(ValueProperty)!;
            set => SetValue(ValueProperty, value);
        }

        public KeyTime KeyTime
        {
            get => (KeyTime)GetValue(KeyTimeProperty)!;
            set => SetValue(KeyTimeProperty, value);
        }
    }
}

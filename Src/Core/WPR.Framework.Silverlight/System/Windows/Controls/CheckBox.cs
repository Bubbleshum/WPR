using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Controls.CheckBox</c>.</summary>
    public class CheckBox : ContentControl
    {
        public static readonly DependencyProperty IsCheckedProperty =
            DependencyProperty.Register(nameof(IsChecked), typeof(bool?), typeof(CheckBox),
                new PropertyMetadata((object?)false));

        public bool? IsChecked
        {
            get => (bool?)GetValue(IsCheckedProperty);
            set => SetValue(IsCheckedProperty, value);
        }

#pragma warning disable CS0067
        public event RoutedEventHandler? Checked;
        public event RoutedEventHandler? Unchecked;
        public event RoutedEventHandler? Indeterminate;
#pragma warning restore CS0067
    }
}

using System;

namespace System.Windows.Controls
{
    /// <summary>
    /// Shim for <c>System.Windows.Controls.PasswordBox</c>: a text field whose characters are
    /// shown as <see cref="PasswordChar"/>. Like <see cref="TextBox"/> it keeps its value and
    /// raises its change event, but takes no typing of its own here.
    ///
    /// Asphalt 7: Heat names one in its start page's XAML (a hidden field behind its own
    /// keyboard), so the page cannot be built without it.
    /// </summary>
    public class PasswordBox : Control
    {
        public static readonly DependencyProperty PasswordProperty =
            DependencyProperty.Register(nameof(Password), typeof(string), typeof(PasswordBox),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty PasswordCharProperty =
            DependencyProperty.Register(nameof(PasswordChar), typeof(char), typeof(PasswordBox),
                new PropertyMetadata('●'));

        public static readonly DependencyProperty MaxLengthProperty =
            DependencyProperty.Register(nameof(MaxLength), typeof(int), typeof(PasswordBox),
                new PropertyMetadata(0));

        public string Password
        {
            get => (string)GetValue(PasswordProperty)!;
            set
            {
                string old = Password;
                SetValue(PasswordProperty, value ?? string.Empty);
                if (!string.Equals(old, value ?? string.Empty, StringComparison.Ordinal))
                    PasswordChanged?.Invoke(this, new RoutedEventArgs());
            }
        }

        public char PasswordChar
        {
            get => (char)GetValue(PasswordCharProperty)!;
            set => SetValue(PasswordCharProperty, value);
        }

        public int MaxLength
        {
            get => (int)GetValue(MaxLengthProperty)!;
            set => SetValue(MaxLengthProperty, value);
        }

        public event RoutedEventHandler? PasswordChanged;

        public void SelectAll()
        {
        }
    }
}

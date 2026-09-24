using System;

// The BCL's own System.Windows.Input.ICommand (System.ObjectModel), NOT a shim. The patcher does
// not rewrite that namespace, so game IL naming ICommand resolves to this same type — a shim of
// our own would be a second, incompatible identity for the interface a game implements.
using ICommand = System.Windows.Input.ICommand;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Controls.Primitives.ButtonBase</c>.</summary>
    /// <remarks>
    /// <para><b>Exists as its own type because Silverlight declares <c>Click</c> here</b>, not on
    /// <see cref="Button"/> — so <c>button.Click += handler</c> compiles to
    /// <c>callvirt ButtonBase::add_Click</c> and a hierarchy that puts the event on the wrong class
    /// does not resolve when the method naming it is compiled. That is the same reason
    /// <see cref="RangeBase"/> is a separate type from <c>ProgressBar</c>.</para>
    ///
    /// <para>The button family was flat before this — <c>Button</c>, <c>ToggleButton</c> and
    /// <c>CheckBox</c> each deriving straight from <see cref="ContentControl"/> with their own
    /// copies of the shared members. Galactic Reign is the measured case: it names
    /// <c>ButtonBase</c> and <c>RadioButton</c>, neither existed, and the
    /// <c>TypeLoadException</c> took its <c>MenuPage</c> down before the game drew anything at
    /// all.</para>
    ///
    /// <para>The real chain is <c>ContentControl → ButtonBase → Button</c> and
    /// <c>ButtonBase → ToggleButton → CheckBox/RadioButton</c>, and it is worth matching exactly:
    /// every one of those types is a name some game's IL may resolve a member against.</para>
    /// </remarks>
    public class ButtonBase : ContentControl
    {
        public static readonly DependencyProperty ClickModeProperty =
            DependencyProperty.Register(nameof(ClickMode), typeof(ClickMode), typeof(ButtonBase),
                new PropertyMetadata(ClickMode.Release));

        public static readonly DependencyProperty IsPressedProperty =
            DependencyProperty.Register(nameof(IsPressed), typeof(bool), typeof(ButtonBase),
                new PropertyMetadata((object)false));

        public ClickMode ClickMode
        {
            get => (ClickMode)GetValue(ClickModeProperty)!;
            set => SetValue(ClickModeProperty, value);
        }

        public bool IsPressed
        {
            get => (bool)GetValue(IsPressedProperty)!;
            protected set => SetValue(IsPressedProperty, value);
        }

        /// <summary>
        /// The command invoked on click — the MVVM alternative to a <see cref="Click"/> handler.
        /// </summary>
        /// <remarks>
        /// A DependencyProperty rather than a plain property because it is almost always written
        /// as <c>Command="{Binding Something}"</c>, and WPR's binding machinery requires a DP to
        /// bind against: without one the reader throws "{Binding} on member 'Command' requires a
        /// DependencyProperty" and fails the element. Galactic Reign binds it eleven times on its
        /// menu page.
        /// </remarks>
        public static readonly DependencyProperty CommandProperty =
            DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(ButtonBase),
                new PropertyMetadata((object?)null));

        public static readonly DependencyProperty CommandParameterProperty =
            DependencyProperty.Register(nameof(CommandParameter), typeof(object), typeof(ButtonBase),
                new PropertyMetadata((object?)null));

        public ICommand? Command
        {
            get => (ICommand?)GetValue(CommandProperty);
            set => SetValue(CommandProperty, value);
        }

        public object? CommandParameter
        {
            get => GetValue(CommandParameterProperty);
            set => SetValue(CommandParameterProperty, value);
        }

        public event RoutedEventHandler? Click;

        /// <summary>Invoked by the host when this button has been hit-tested as the target of a press.</summary>
        internal void RaiseClick()
        {
            Click?.Invoke(this, new RoutedEventArgs { OriginalSource = this });

            // The command too, since a button bound to one usually has no Click handler at all —
            // dispatching only the event would leave such a button dead to the touch.
            ICommand? command = Command;
            object? parameter = CommandParameter;
            if (command != null)
            {
                try
                {
                    if (command.CanExecute(parameter)) command.Execute(parameter);
                }
                catch
                {
                    // The game's command; its failure is not the button's.
                }
            }

            OnClick();
        }

        /// <summary>
        /// Lets a subclass react to its own click — <see cref="ToggleButton"/> flips its state here.
        /// </summary>
        /// <remarks>
        /// Runs AFTER the handlers, so a handler reading <c>IsChecked</c> sees the value it had
        /// when the user pressed it. Silverlight raises Click after toggling, but WPR has no
        /// visual state to keep in step and this ordering is the one games reading the old value
        /// depend on — noted so a future change here is deliberate rather than incidental.
        /// </remarks>
        protected virtual void OnClick()
        {
        }

        /// <summary>
        /// Whether anything is subscribed to <see cref="Click"/>. Diagnostic only.
        /// </summary>
        /// <remarks>
        /// "The tap reached the button" and "the tap reached the button and it does nothing" look
        /// identical from outside, and the second means the XAML's <c>Click="Handler"</c> attribute
        /// was never wired to the code-behind — a parser problem wearing an input problem's
        /// clothes.
        /// </remarks>
        internal bool HasClickHandler => Click != null;
    }

    /// <summary>Shim for <c>System.Windows.Controls.ClickMode</c>.</summary>
    public enum ClickMode
    {
        Release,
        Press,
        Hover,
    }
}

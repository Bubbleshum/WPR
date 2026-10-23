using System;
using System.Collections;
using System.Collections.Generic;
using WPR.SilverlightCompability;

namespace Microsoft.Phone.Shell
{
    /// <summary>Shim for <c>Microsoft.Phone.Shell.ApplicationBar</c>: the WP7 app bar along the bottom of a page.</summary>
    /// <remarks>
    /// <para><b>Stored, not drawn.</b> It exists so a page that declares one can be constructed:
    /// until patcher version 47 the type was missing, and every such page (Minesweeper's and
    /// Sudoku's game boards among them) failed with TypeLoadException in its constructor, so the
    /// navigation to it did nothing. Drawing the bar and routing taps to its buttons is still to do;
    /// <see cref="ApplicationBarMenuItem"/> has the click hook for it.</para>
    /// <para>Sizes are the phone's: 72 px tall, 30 px minimised.</para>
    /// </remarks>
    [ContentProperty("Buttons")]
    public class ApplicationBar : IApplicationBar
    {
        private readonly List<object> _buttons = new();
        private readonly List<object> _menuItems = new();

        public bool IsVisible { get; set; } = true;
        public double Opacity { get; set; } = 1.0;
        public bool IsMenuEnabled { get; set; } = true;
        public Color BackgroundColor { get; set; }
        public Color ForegroundColor { get; set; }
        public ApplicationBarMode Mode { get; set; }
        public double DefaultSize => 72;
        public double MiniSize => 30;
        public IList Buttons => _buttons;
        public IList MenuItems => _menuItems;

#pragma warning disable CS0067 // raised when the menu opens, which needs a drawn bar
        public event EventHandler<ApplicationBarStateChangedEventArgs>? StateChanged;
#pragma warning restore CS0067
    }
}

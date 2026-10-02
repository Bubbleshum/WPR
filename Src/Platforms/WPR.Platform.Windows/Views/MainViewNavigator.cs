using System;
using WPR.Platform.Windows.Pages;
using WPR.Platform.Windows.ViewModels;
using Avalonia;
using Avalonia.Controls;

namespace WPR.Platform.Windows.Views
{
    public class MainViewNavigator
    {
        /// <summary>
        /// One page per sidebar tab, in the order the tabs appear in MainWindowDesktop.axaml:
        /// playing and progress (PLAY, TROPHIES), then online (HUB, MESSAGES), then setup
        /// (CONTROLS, SETTINGS), then ABOUT. Change the two together. Pages other than PLAY are
        /// built the first time their tab is opened.
        /// </summary>
        private static readonly Func<UserControl>[] PageFactories =
        {
            () => new ApplicationListingPage(),
            () => new AchievementsPage(),
            () => new HubPage(),
            () => new MessagesPage(),
            () => new ControlsPage(),
            () => new SettingsPage(),
            () => new AboutPage(),
        };

        private int _CurrentIndex = -1;
        private readonly UserControl?[] _Pages = new UserControl?[PageFactories.Length];

        public void SetupNavigation(TabControl control, TransitioningContentControl contentControl)
        {
            _CurrentIndex = 0;
            SetPageContent(contentControl, PageAt(0));

            control.SelectionChanged += (obj, args) =>
            {
                int index = control.SelectedIndex;
                if (_CurrentIndex == index || index < 0 || index >= PageFactories.Length) return;

                _CurrentIndex = index;
                SetPageContent(contentControl, PageAt(index));
            };
        }

        private UserControl PageAt(int index) => _Pages[index] ??= PageFactories[index]();

        private static void SetPageContent(TransitioningContentControl contentControl, UserControl page)
        {
            // Workaround: TransitioningContentControl may not paint the first page on Android.
            contentControl.Content = page;
            contentControl.Content = null;
            contentControl.Content = page;
        }
    }
}

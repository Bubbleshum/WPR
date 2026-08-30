using System;
using WPR.Platform.Windows.Pages;
using WPR.Platform.Windows.ViewModels;
using Avalonia;
using Avalonia.Controls;

namespace WPR.Platform.Windows.Views
{
    public class MainViewNavigator
    {
        private int _CurrentIndex = -1;
        private UserControl[] _Pages = new UserControl[5];

        public void SetupNavigation(TabControl control, TransitioningContentControl contentControl)
        {
            _CurrentIndex = 0;

            _Pages[0] = new ApplicationListingPage();

            SetPageContent(contentControl, _Pages[0]);

            control.SelectionChanged += (obj, args) =>
            {
                if (_CurrentIndex != control.SelectedIndex)
                {
                    _CurrentIndex = control.SelectedIndex;

                    if (_Pages[_CurrentIndex] == null)
                    {
                        switch (_CurrentIndex)
                        {
                            case 1:
                                _Pages[1] = new SettingsPage();
                                break;

                            case 2:
                                _Pages[2] = new ControlsPage();
                                break;

                            case 3:
                                _Pages[3] = new AboutPage();
                                break;

                            case 4:
                                _Pages[4] = new AchievementsPage();
                                break;
                        }
                    }

                    SetPageContent(contentControl, _Pages[_CurrentIndex]);
                }
            };
        }

        private static void SetPageContent(TransitioningContentControl contentControl, UserControl page)
        {
            // Workaround: TransitioningContentControl may not paint the first page on Android.
            contentControl.Content = page;
            contentControl.Content = null;
            contentControl.Content = page;
        }
    }
}

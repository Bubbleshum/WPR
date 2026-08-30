using Avalonia.Controls;
using WPR.Platform.Windows.ViewModels;

namespace WPR.Platform.Windows.Pages
{
    public partial class AchievementsPage : UserControl
    {
        public AchievementsPage()
        {
            InitializeComponent();
            DataContext = new AchievementsPageViewModel();

            // The navigator caches this page, so the VM's constructor load only runs
            // once. Reload each time the page is shown so games/achievements added
            // since (e.g. a freshly catalogued install) appear without restarting WPR.
            AttachedToVisualTree += (_, _) =>
            {
                if (DataContext is AchievementsPageViewModel vm)
                {
                    _ = vm.LoadAsync();
                }
            };
        }
    }
}

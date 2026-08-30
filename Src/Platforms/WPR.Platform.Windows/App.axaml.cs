using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

using WPR.Platform.Windows.ViewModels;
using WPR.Platform.Windows.Views;

namespace WPR.Platform.Windows
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);

            // Every view now lives in this assembly alongside the locator, so nothing
            // external needs registering. Kept as the hook a future head would use for
            // views it owns: Type.GetType(string) only searches the calling assembly
            // and corelib.
            ViewLocator.RegisterViewAssembly(typeof(App).Assembly);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            RequestedThemeVariant = ThemeVariant.Dark;

            // Only the classic desktop lifetime exists here. The single-view branch
            // that used to sit alongside this moved to the Android head.
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new MainWindowDesktop
                {
                    DataContext = new MainWindowViewModel(),
                };
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}

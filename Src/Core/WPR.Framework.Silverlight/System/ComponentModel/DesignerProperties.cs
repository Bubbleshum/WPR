using System;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.ComponentModel.DesignerProperties</c>. WP Toolkit controls
    /// (Panorama, Pivot, etc.) gate design-time-only init paths on
    /// <see cref="IsInDesignTool"/>. We're never in a designer, so the static
    /// getter returns <c>false</c> — same as runtime in Silverlight.
    /// </summary>
    public static class DesignerProperties
    {
        public static readonly DependencyProperty IsInDesignModeProperty =
            DependencyProperty.RegisterAttached("IsInDesignMode", typeof(bool), typeof(DesignerProperties),
                new PropertyMetadata((object)false));

        public static bool IsInDesignTool
        {
            get => false;
            set { /* setter exists in SL; setting it has no effect off-designer */ }
        }

        public static bool GetIsInDesignMode(DependencyObject obj) => false;
        public static void SetIsInDesignMode(DependencyObject obj, bool value)
            => obj?.SetValue(IsInDesignModeProperty, value);
    }
}

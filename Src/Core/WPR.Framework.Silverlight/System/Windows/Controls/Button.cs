using System;

namespace WPR.SilverlightCompability
{
    /// <remarks>
    /// <c>Click</c>, <c>RaiseClick</c> and <c>ClickMode</c> live on <see cref="ButtonBase"/>,
    /// which is where Silverlight declares them and therefore what game IL names.
    /// </remarks>
    public class Button : ButtonBase
    {

        /// <summary>
        /// Pad the Content's natural size out to WP7's button minimum
        /// (~3 DIP border + 10 DIP horizontal padding + 72 DIP min tap-target
        /// height). The renderer paints the chrome around this, so without a
        /// roomy DesiredSize the parent (typically a StackPanel) would arrange
        /// the button at bare-text height and the border/padding would clip
        /// against the text. Mirrors the default Style's BorderThickness /
        /// Padding / MinHeight which our XAML reader doesn't fully apply.
        /// </summary>
        protected override Size MeasureOverride(Size availableSize)
        {
            const double horizontalPad = 26;   // 3 DIP border + 10 DIP padding × 2
            const double verticalPad = 16;     // border + padding (top + bottom)
            const double minHeight = 72;       // WP7 button min tap-target

            Size baseSize = base.MeasureOverride(availableSize);
            double w = baseSize.Width + horizontalPad;
            double h = Math.Max(baseSize.Height + verticalPad, minHeight);

            // Don't exceed the available width — let StackPanel's HorizontalAlignment=Stretch
            // size us instead so the buttons span the page consistently.
            if (!double.IsInfinity(availableSize.Width) && w > availableSize.Width)
                w = availableSize.Width;

            return new Size(w, h);
        }
    }
}

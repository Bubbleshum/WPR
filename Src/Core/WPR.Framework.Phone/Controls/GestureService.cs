using System;
using WPR.SilverlightCompability;

namespace Microsoft.Phone.Controls
{
    /// <summary>
    /// Attached-property host for <see cref="GestureListener"/>. Real WP defines
    /// this in <c>Microsoft.Phone.Controls.Toolkit.dll</c>; user XAML's
    /// <c>&lt;toolkit:GestureService.GestureListener&gt;</c> attached property
    /// sets the listener and our pointer pipeline can find it via
    /// <see cref="GetGestureListener"/>.
    /// </summary>
    public static class GestureService
    {
        public static readonly DependencyProperty GestureListenerProperty =
            DependencyProperty.RegisterAttached(
                "GestureListener", typeof(GestureListener), typeof(GestureService),
                new PropertyMetadata((object?)null));

        public static GestureListener? GetGestureListener(DependencyObject obj)
            => (GestureListener?)obj?.GetValue(GestureListenerProperty);

        public static void SetGestureListener(DependencyObject obj, GestureListener value)
            => obj?.SetValue(GestureListenerProperty, value);
    }
}

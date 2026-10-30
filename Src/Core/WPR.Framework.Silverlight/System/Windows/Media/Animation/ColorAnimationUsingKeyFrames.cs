namespace System.Windows.Media.Animation
{
    /// <summary>Shim for <c>System.Windows.Media.Animation.ColorAnimationUsingKeyFrames</c>.</summary>
    /// <remarks>
    /// Built and stored, not animated, as <see cref="ColorAnimation"/> is: a storyboard holding one
    /// runs its other children and its Completed. Microsoft Bingo builds one in code for its
    /// results screen; without the type the whole method failed to compile.
    /// </remarks>
    [ContentProperty(nameof(KeyFrames))]
    public class ColorAnimationUsingKeyFrames : Timeline
    {
        public ColorKeyFrameCollection KeyFrames { get; } = new ColorKeyFrameCollection();
    }

    /// <summary>Shim for <c>System.Windows.Media.Animation.ColorKeyFrameCollection</c>.</summary>
    public class ColorKeyFrameCollection : PresentationFrameworkCollection<ColorKeyFrame> { }

    /// <summary>Shim for <c>System.Windows.Media.Animation.ColorKeyFrame</c>.</summary>
    public abstract class ColorKeyFrame : DependencyObject
    {
        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(nameof(Value), typeof(Color), typeof(ColorKeyFrame),
                new PropertyMetadata(default(Color)));

        public static readonly DependencyProperty KeyTimeProperty =
            DependencyProperty.Register(nameof(KeyTime), typeof(KeyTime), typeof(ColorKeyFrame),
                new PropertyMetadata(KeyTime.Uniform));

        public Color Value
        {
            get => (Color)GetValue(ValueProperty)!;
            set => SetValue(ValueProperty, value);
        }

        public KeyTime KeyTime
        {
            get => (KeyTime)GetValue(KeyTimeProperty)!;
            set => SetValue(KeyTimeProperty, value);
        }
    }

    /// <summary>Shim for <c>System.Windows.Media.Animation.DiscreteColorKeyFrame</c>.</summary>
    public class DiscreteColorKeyFrame : ColorKeyFrame { }

    /// <summary>Shim for <c>System.Windows.Media.Animation.LinearColorKeyFrame</c>.</summary>
    public class LinearColorKeyFrame : ColorKeyFrame { }

    /// <summary>Shim for <c>System.Windows.Media.Animation.EasingColorKeyFrame</c>.</summary>
    public class EasingColorKeyFrame : ColorKeyFrame
    {
        public IEasingFunction? EasingFunction { get; set; }
    }
}

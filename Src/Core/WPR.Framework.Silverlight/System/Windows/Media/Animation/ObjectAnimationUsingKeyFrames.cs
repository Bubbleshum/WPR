using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.Animation.ObjectKeyFrame</c>.</summary>
    /// <remarks>
    /// The object-valued counterpart of <see cref="DoubleKeyFrame"/>. Silverlight games use it to
    /// animate things that are not numbers — most often <c>Visibility</c>, to show and hide a
    /// panel from a storyboard rather than from code.
    /// </remarks>
    public abstract class ObjectKeyFrame : DependencyObject
    {
        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(nameof(Value), typeof(object), typeof(ObjectKeyFrame),
                new PropertyMetadata((object?)null));

        public static readonly DependencyProperty KeyTimeProperty =
            DependencyProperty.Register(nameof(KeyTime), typeof(KeyTime), typeof(ObjectKeyFrame),
                new PropertyMetadata(KeyTime.Uniform));

        public object? Value
        {
            get => GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public KeyTime KeyTime
        {
            get => (KeyTime)GetValue(KeyTimeProperty)!;
            set => SetValue(KeyTimeProperty, value);
        }
    }

    /// <summary>Shim for <c>System.Windows.Media.Animation.DiscreteObjectKeyFrame</c>.</summary>
    /// <remarks>
    /// The only concrete object key frame Silverlight has — an object cannot be interpolated, so
    /// every value change is a jump.
    /// </remarks>
    public class DiscreteObjectKeyFrame : ObjectKeyFrame { }

    /// <summary>Shim for <c>System.Windows.Media.Animation.ObjectKeyFrameCollection</c>.</summary>
    public class ObjectKeyFrameCollection : List<ObjectKeyFrame> { }

    /// <summary>
    /// Shim for <c>System.Windows.Media.Animation.ObjectAnimationUsingKeyFrames</c>.
    /// </summary>
    /// <remarks>
    /// Holds its key frames and animates nothing, matching <see cref="DoubleAnimationUsingKeyFrames"/>
    /// — WPR runs no timelines. It exists so a storyboard that carries one can be parsed and
    /// constructed; a game whose XAML declares it otherwise fails at page construction, which
    /// costs the whole page rather than one animation.
    ///
    /// <para>The <see cref="ContentPropertyAttribute"/> is load-bearing for that: key frames are
    /// written as bare <c>&lt;DiscreteObjectKeyFrame&gt;</c> children, and without it the XAML
    /// reader throws on the first one — so the page this type exists to rescue fails anyway.
    /// <see cref="DoubleAnimationUsingKeyFrames"/> already declared it; this one did not.</para>
    /// </remarks>
    [ContentProperty("KeyFrames")]
    public class ObjectAnimationUsingKeyFrames : Timeline
    {
        public ObjectKeyFrameCollection KeyFrames { get; } = new ObjectKeyFrameCollection();
    }
}

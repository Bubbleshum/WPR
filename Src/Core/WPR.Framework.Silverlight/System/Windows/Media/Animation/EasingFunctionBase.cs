namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Media.Animation.EasingFunctionBase</c> — the base every
    /// built-in easing function derives from.
    /// </summary>
    /// <remarks>
    /// The existing easing shims (<see cref="QuarticEase"/>, <see cref="ExponentialEase"/>)
    /// implement <see cref="IEasingFunction"/> directly and do not derive from this, which is
    /// fine for XAML but not for IL: a game that declares a field or parameter of type
    /// <c>EasingFunctionBase</c> — Carcassonne does — binds the base by name, so it has to exist
    /// and the functions have to be assignable to it. New easing shims should derive from here.
    /// </remarks>
    public abstract class EasingFunctionBase : DependencyObject, IEasingFunction
    {
        public EasingMode EasingMode { get; set; } = EasingMode.EaseOut;

        /// <summary>
        /// Maps linear progress (0..1) to eased progress, as Silverlight's
        /// <c>EasingFunctionBase.Ease</c> does.
        /// </summary>
        /// <remarks>
        /// <para>Declared here rather than on <see cref="IEasingFunction"/> on purpose: that
        /// interface is public and a game is entitled to implement it, so adding a member would
        /// break any type that does. Callers test for this base instead and fall back to linear,
        /// which is also what the two older shims that implement the interface directly
        /// (<see cref="QuarticEase"/>, <see cref="ExponentialEase"/>) get.</para>
        ///
        /// <para>The <see cref="EasingMode"/> wrapping is Silverlight's own: EaseIn applies the
        /// curve directly, EaseOut mirrors it, EaseInOut runs it in each half.</para>
        /// </remarks>
        public double Ease(double normalizedTime)
        {
            double t = normalizedTime < 0 ? 0 : normalizedTime > 1 ? 1 : normalizedTime;
            switch (EasingMode)
            {
                case EasingMode.EaseIn: return EaseInCore(t);
                case EasingMode.EaseOut: return 1.0 - EaseInCore(1.0 - t);
                default:
                    return t < 0.5
                        ? EaseInCore(t * 2.0) * 0.5
                        : (1.0 - EaseInCore((1.0 - t) * 2.0)) * 0.5 + 0.5;
            }
        }

        /// <summary>The curve itself, expressed as "ease in". Default is linear.</summary>
        protected virtual double EaseInCore(double t) => t;
    }

    /// <summary>Shim for <c>System.Windows.Media.Animation.QuinticEase</c>.</summary>
    public class QuinticEase : EasingFunctionBase
    {
        protected override double EaseInCore(double t) => t * t * t * t * t;
    }

    /// <summary>Shim for <c>System.Windows.Media.Animation.QuadraticEase</c>.</summary>
    public class QuadraticEase : EasingFunctionBase
    {
        protected override double EaseInCore(double t) => t * t;
    }

    /// <summary>Shim for <c>System.Windows.Media.Animation.CubicEase</c>.</summary>
    public class CubicEase : EasingFunctionBase
    {
        protected override double EaseInCore(double t) => t * t * t;
    }

    /// <summary>Shim for <c>System.Windows.Media.Animation.CircleEase</c>.</summary>
    public class CircleEase : EasingFunctionBase
    {
        protected override double EaseInCore(double t) => 1.0 - System.Math.Sqrt(1.0 - t * t);
    }

    /// <summary>Shim for <c>System.Windows.Media.Animation.SineEase</c>.</summary>
    public class SineEase : EasingFunctionBase
    {
        protected override double EaseInCore(double t) => 1.0 - System.Math.Sin(System.Math.PI * 0.5 * (1.0 - t));
    }

    /// <summary>Shim for <c>System.Windows.Media.Animation.BackEase</c>.</summary>
    public class BackEase : EasingFunctionBase
    {
        public double Amplitude { get; set; } = 1.0;
    }

    /// <summary>Shim for <c>System.Windows.Media.Animation.BounceEase</c>.</summary>
    public class BounceEase : EasingFunctionBase
    {
        public int Bounces { get; set; } = 3;
        public double Bounciness { get; set; } = 2.0;
    }

    /// <summary>Shim for <c>System.Windows.Media.Animation.ElasticEase</c>.</summary>
    public class ElasticEase : EasingFunctionBase
    {
        public int Oscillations { get; set; } = 3;
        public double Springiness { get; set; } = 3.0;
    }

    /// <summary>Shim for <c>System.Windows.Media.Animation.PowerEase</c>.</summary>
    public class PowerEase : EasingFunctionBase
    {
        public double Power { get; set; } = 2.0;
    }
}

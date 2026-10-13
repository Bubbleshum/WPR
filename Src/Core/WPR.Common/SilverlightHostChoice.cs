using System;

namespace WPR.Common
{
    /// <summary>
    /// Which host runs a pure Silverlight title on the desktop: the XNA host every other game
    /// uses (<c>MixedModeGame</c>, CPU-rasterised page, <c>TouchPanel</c> input), or the old
    /// Avalonia host (<c>SilverlightLauncher</c>). Stage C of
    /// <c>Plans/SILVERLIGHT-XNA-CONVERGENCE.md</c>; the Avalonia path is kept for one release and
    /// then deleted in Stage D.
    /// </summary>
    /// <remarks>
    /// <para>Android has no Avalonia host, so it never asks: every Silverlight title there goes
    /// to the XNA host whatever this says.</para>
    /// <para>Order, first answer wins: <c>WPR_SILVERLIGHT_AVALONIA</c> (<c>1</c>/<c>true</c> =
    /// Avalonia, <c>0</c>/<c>false</c> = XNA), then <c>Configuration.SilverlightAvaloniaHost</c>
    /// in config.json, then <see cref="DefaultUsesAvalonia"/>.</para>
    /// </remarks>
    public static class SilverlightHostChoice
    {
        /// <summary>The environment variable that overrides everything else, for A/B runs.</summary>
        public const string EnvironmentVariable = "WPR_SILVERLIGHT_AVALONIA";

        /// <summary>
        /// The default when nothing is set. False: the XNA host, measured to reach the same
        /// screens as the Avalonia host for Flowerz and Minesweeper (see the plan's Stage C).
        /// </summary>
        public const bool DefaultUsesAvalonia = false;

        /// <summary>True when a pure Silverlight title should go to the Avalonia host.</summary>
        public static bool UseAvaloniaHost
        {
            get
            {
                string? env = Environment.GetEnvironmentVariable(EnvironmentVariable);
                if (!string.IsNullOrWhiteSpace(env))
                {
                    env = env.Trim();
                    if (env == "1" || env.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                    if (env == "0" || env.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
                }

                return Configuration.Current?.SilverlightAvaloniaHost ?? DefaultUsesAvalonia;
            }
        }
    }
}

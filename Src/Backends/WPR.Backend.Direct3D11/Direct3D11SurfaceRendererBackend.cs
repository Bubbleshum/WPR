using System;
using WPR.SilverlightCompability;

namespace WPR.Backend.Direct3D11
{
    /// <summary>
    /// Direct3D 11 implementation of <see cref="ISurfaceRendererBackend"/> — the concrete half of
    /// the Stage-5e seam. Registered by the launcher into
    /// <see cref="SilverlightBackend.SurfaceRenderer"/>; see
    /// <c>WPR.Platform.Windows.ServicesSetup</c>.
    ///
    /// <para>Both factories swallow construction failures and return <see langword="null"/>. That
    /// is deliberate and preserves the exact behaviour of the <c>try { … } catch { /* fall through
    /// */ }</c> ladder that used to sit inline in <c>DrawingSurfaceBackgroundGrid.LookupRenderer</c>:
    /// creating a D3D11 device fails on machines with no hardware adapter, and a background that
    /// cannot be drawn must degrade to the pure-Avalonia splash rather than take the app down.
    /// Device creation happens lazily on the first frame inside
    /// <see cref="D3D11SurfaceRenderer"/>, so in practice these ctors rarely throw — the guard is
    /// here because the old code had it, not because a specific failure is expected.</para>
    /// </summary>
    public sealed class Direct3D11SurfaceRendererBackend : ISurfaceRendererBackend
    {
        public IBackgroundRenderer? CreateImageSplashRenderer(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath)) return null;
            try { return new D3D11ImageSplashRenderer(imagePath); }
            catch { return null; }
        }

        public IBackgroundRenderer? CreateTestPatternRenderer()
        {
            try { return new D3D11TestPatternRenderer(); }
            catch { return null; }
        }
    }
}

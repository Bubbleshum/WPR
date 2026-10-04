using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input.Touch;
using WPR.Xna.Rhi;

namespace WPR.Backend.FNA
{
    /// <summary>
    /// FNA implementation of <see cref="IPlatformBackend"/> — spine relocation, step 1
    /// (2026-09-01). A thin forward onto FNA's own <c>FNAPlatform</c> static delegate table,
    /// which is itself bound to <c>SDL2_FNAPlatform</c> at type-init.
    ///
    /// <para>Deliberately thin: this step changes <b>who calls</b> the platform, not what the
    /// platform does. <c>Game</c> and <c>GraphicsDeviceManager</c> now go through
    /// <c>XnaBackend.Platform</c> instead of naming <c>FNAPlatform</c> directly, which is the
    /// prerequisite for those types leaving this assembly in step 2 — they can't move while they
    /// name a type that stays. Behaviour is byte-for-byte the same, so a regression here is a
    /// wiring mistake, not a semantics one.</para>
    ///
    /// <para>The <see cref="IGameLoopHost"/> indirection exists because FNA's delegates take
    /// <c>Game</c>, and the seam cannot name <c>Game</c> without the framework referencing FNA.
    /// <c>Game</c> implements the interface; these methods cast back. The cast is safe by
    /// construction — this backend and that <c>Game</c> ship in the same assembly today, and in
    /// step 2 the cast disappears entirely along with FNAPlatform's dependency on the concrete
    /// type.</para>
    /// </summary>
    public sealed class FnaPlatformBackend : IPlatformBackend
    {
        /// <summary>
        /// FNA's platform delegates are typed against the concrete <c>Game</c>. Everything above
        /// the seam holds an <see cref="IGameLoopHost"/>, so this is the one place that converts.
        /// A host that is not FNA's <c>Game</c> cannot reach these delegates at all, which is why
        /// this throws rather than no-oping: it would mean the loop and the platform had been
        /// composed from two different backends.
        /// </summary>
        private static Game ToGame(IGameLoopHost host) =>
            host as Game ?? throw new ArgumentException(
                "FnaPlatformBackend can only drive FNA's Game. Got: " +
                (host?.GetType().FullName ?? "null"),
                nameof(host));

        private readonly bool _markDriverAttempt;
        private readonly string? _requestedDriver;
        private bool _driverAttemptMarked;

        /// <summary>A backend that does not touch the graphics driver crash breadcrumb.</summary>
        public FnaPlatformBackend()
        {
        }

        /// <summary>
        /// A backend that drops the driver crash breadcrumb
        /// (<see cref="WPR.Engine.Graphics.GraphicsDriverProbe.MarkAttempting"/>) on the first
        /// <see cref="CreateWindow"/>, for <paramref name="requestedDriver"/> (null = automatic).
        /// </summary>
        public FnaPlatformBackend(string? requestedDriver)
        {
            _markDriverAttempt = true;
            _requestedDriver = requestedDriver;
        }

        public GameWindow CreateWindow()
        {
            // The breadcrumb goes down HERE, at the first contact with the graphics driver:
            // FNAPlatform.CreateWindow calls FNA3D_PrepareWindowAttributes, which builds a
            // throwaway device to test the waters, so a hostile driver can take the process down
            // from this call on. Marking any earlier, as FnaGameHost.RunAsync did, also counted
            // launches that ended before the driver was touched at all: ApplicationLaunch refuses a
            // Silverlight UI app or a native WinRT app before the game is constructed, that launch
            // presented no frame, and since the strike became sticky (issue #51) one tap on such a
            // title condemned Vulkan on the phone for good. Reported on a Galaxy S24 on
            // 2026-10-04: an unsupported Silverlight XAP moved every later game to OpenGL.
            if (_markDriverAttempt && !_driverAttemptMarked)
            {
                _driverAttemptMarked = true;
                WPR.Engine.Graphics.GraphicsDriverProbe.MarkAttempting(_requestedDriver);
            }
            return FNAPlatform.CreateWindow();
        }

        public void DisposeWindow(GameWindow window) => FNAPlatform.DisposeWindow(window);

        public void ScaleForWindow(IntPtr window, bool invert, ref int w, ref int h) =>
            FNAPlatform.ScaleForWindow(window, invert, ref w, ref h);

        public bool SupportsOrientationChanges() => FNAPlatform.SupportsOrientationChanges();

        public GraphicsAdapter RegisterGame(IGameLoopHost game) =>
            FNAPlatform.RegisterGame(ToGame(game));

        public void UnregisterGame(IGameLoopHost game) =>
            FNAPlatform.UnregisterGame(ToGame(game));

        public void PollEvents(
            IGameLoopHost game,
            ref GraphicsAdapter currentAdapter,
            bool[] textInputControlDown,
            ref bool textInputSuppress
        ) => FNAPlatform.PollEvents(
            ToGame(game), ref currentAdapter, textInputControlDown, ref textInputSuppress);

        public bool NeedsPlatformMainLoop() => FNAPlatform.NeedsPlatformMainLoop();

        public void RunPlatformMainLoop(IGameLoopHost game) =>
            FNAPlatform.RunPlatformMainLoop(ToGame(game));

        public void OnIsMouseVisibleChanged(bool visible) =>
            FNAPlatform.OnIsMouseVisibleChanged(visible);

        public TouchPanelCapabilities GetTouchCapabilities() => FNAPlatform.GetTouchCapabilities();

        public int TextInputControlCharacterCount => FNAPlatform.TextInputCharacters.Length;

        public void ShowRuntimeError(string title, string message) =>
            FNAPlatform.ShowRuntimeError(title, message);
    }
}

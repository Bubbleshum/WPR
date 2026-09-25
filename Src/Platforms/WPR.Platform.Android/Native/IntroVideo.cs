using System;

using Android.App;
using Android.OS;
using Android.Views;
using Android.Widget;

using WPR.Common;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// The WPR intro: a short square clip played once over the Start screen while the launcher
    /// finishes waking up.
    /// </summary>
    /// <remarks>
    /// <para><b>An overlay on the Start screen, not a splash activity.</b> A separate activity
    /// would have to carry the <c>MainLauncher</c> intent filter, which moves the app's entry
    /// point — and <c>GameShortcutActivity</c>, the widget shortcuts and the
    /// <c>startActivityForResult</c> contract with <c>GameActivity</c> are all written against
    /// <c>MainActivity</c> being that entry point. An overlay changes nothing structural: the
    /// real page is built, laid out and initialising underneath the whole time, so the clip costs
    /// no startup time it is not already spending.</para>
    ///
    /// <para><b>Once per PROCESS, not once per activity.</b> <c>MainActivity</c> is
    /// <c>SingleTask</c> and is re-created on every return from a game, from settings, from the
    /// picker — an activity-scoped flag would replay the intro each time, which is the difference
    /// between an intro and an obstacle. A static survives that and dies with the process, so it
    /// plays on a cold start and never again.</para>
    ///
    /// <para><b>It can always be dismissed, four ways.</b> Tap, completion, playback error, and a
    /// hard timeout. The last one is the important one: if the codec refuses the file or the
    /// media server is wedged, <c>Completion</c> never fires and an intro with no exit is an app
    /// that cannot be opened. Nothing here is allowed to be the reason WPR does not start — every
    /// path removes the view, and every failure removes it immediately.</para>
    ///
    /// <para><b>Added to the decor view rather than the content view</b>, because
    /// <see cref="WpTheme.ApplySystemBars"/> pads the content view clear of the system bars; a
    /// child of it would be letterboxed by that padding. The decor view is above it, so the clip
    /// is genuinely full-bleed.</para>
    /// </remarks>
    internal static class IntroVideo
    {
        /// <summary>Longest the overlay may live, however playback behaves. See the remarks.</summary>
        private static readonly TimeSpan HardTimeout = TimeSpan.FromSeconds(12);

        private static bool _Played;

        /// <summary>
        /// Plays the intro over <paramref name="activity"/>, or does nothing if it has already
        /// played in this process.
        /// </summary>
        public static void PlayOnce(Activity activity)
        {
            if (_Played) return;
            _Played = true;

            try
            {
                Show(activity);
            }
            catch (Exception ex)
            {
                // Decoration. A launcher that fails to open because of its own intro would be a
                // poor trade, so anything unexpected here is logged and dropped.
                Log.Warn(LogCategory.Common, $"intro video skipped: {ex.Message}");
            }
        }

        private static void Show(Activity activity)
        {
            if (activity.Window?.DecorView is not ViewGroup decor) return;

            var overlay = new FrameLayout(activity)
            {
                LayoutParameters = new ViewGroup.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent,
                    ViewGroup.LayoutParams.MatchParent),
                Clickable = true,   // swallow taps rather than letting them reach the tiles
                Focusable = true,
            };
            overlay.SetBackgroundColor(global::Android.Graphics.Color.Black);

            // MatchParent both ways: VideoView measures itself to the video's aspect inside its
            // bounds, so a 1:1 clip letterboxes centred on the black rather than stretching.
            var video = new VideoView(activity)
            {
                LayoutParameters = new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent,
                    ViewGroup.LayoutParams.MatchParent)
                {
                    Gravity = GravityFlags.Center,
                },
            };

            // VideoView is a SurfaceView, and a SurfaceView draws in its OWN layer BEHIND the
            // window, showing through a transparent hole punched where it sits. The black
            // background above is opaque and covers that hole, so without this the clip decodes
            // and "renders" — logcat even reports MEDIA_INFO_VIDEO_RENDERING_START — onto a black
            // rectangle. Ordering the surface on top is the fix; it does not affect touch, so the
            // tap-to-skip handler on the parent still receives taps through it.
            video.SetZOrderOnTop(true);

            overlay.AddView(video);
            decor.AddView(overlay);

            bool dismissed = false;
            void Dismiss()
            {
                if (dismissed) return;
                dismissed = true;

                try { video.StopPlayback(); } catch (Exception) { /* already torn down */ }
                decor.RemoveView(overlay);
            }

            overlay.Click += (_, _) => Dismiss();
            video.Completion += (_, _) => Dismiss();
            video.Error += (_, e) =>
            {
                // Returning handled stops the framework popping its own "Can't play this video"
                // dialog over the launcher.
                e.Handled = true;
                Dismiss();
            };

            // Belt and braces: Completion never arrives if playback never starts.
            new Handler(Looper.MainLooper!).PostDelayed(Dismiss, (long)HardTimeout.TotalMilliseconds);

            video.SetVideoURI(global::Android.Net.Uri.Parse(
                $"android.resource://{activity.PackageName}/{Resource.Raw.wpr_intro_square}"));
            video.Start();
        }
    }
}

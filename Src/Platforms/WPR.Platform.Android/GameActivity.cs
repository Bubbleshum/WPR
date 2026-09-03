using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;

using Org.Libsdl.App;
using Newtonsoft.Json;
using WPR.Common;

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Android.Content.PM;
using Android.Runtime;

namespace WPR.Platform.Android
{
    /// <summary>
    /// Hosts one WP7/XNA game run.
    ///
    /// <para><b>Runs in its own OS process</b> (<c>Process = ":game"</c>). The game is hosted
    /// in-process by FNA, and a process that has run one game cannot cleanly run another:
    /// FNA leaves non-background audio/render threads alive, SDL's native layer does not
    /// survive a re-init, and <c>ApplicationLaunch</c> deliberately retains the whole game
    /// object graph (<c>RetainGameGraphToAvoidGameFinalizers</c>) so third-party finalizers
    /// never run. The desktop head handles this with <c>Environment.Exit(0)</c> at the end of
    /// <c>Main</c>; on Android the equivalent is to give the game its own process and kill it
    /// on the way out, which leaves the launcher process untouched. This is the
    /// "host each game in its OWN PROCESS" end-state called out in ApplicationLaunch.cs.</para>
    ///
    /// <para>Nothing may be shared with the launcher through statics — the intent extra is
    /// JSON precisely so it crosses the process boundary. Anything the game path needs from
    /// the launcher's start-up has to be redone in <see cref="OnCreate"/>.</para>
    /// </summary>
    [Activity(Label = "Game activity", Theme = "@style/MyTheme.NoActionBar", ScreenOrientation = ScreenOrientation.Landscape, ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize, Process = ":game")]
    [Register("com.wpr.android.GameActivity")]
    public class GameActivity : SDLActivity
    {
        public static string TargetApplicationDataName = "TargetApplication";
        public static string ErrorDataName = "Error";

        private static Models.Application? TargetLaunchApplication;
        private static GameActivity CurrentActivity;

        /// <summary>
        /// The running host, so <see cref="OnDestroy"/> can ask the game to exit. Static because
        /// <see cref="SDLMain"/> is invoked from native code with no instance context.
        /// </summary>
        private static WPR.Engine.GameLoop.IGameHost? RunningHost;

        /// <summary>Set once the game loop has unwound and <see cref="SDLMain"/> is done.</summary>
        private static readonly ManualResetEventSlim GameLoopFinished = new ManualResetEventSlim(false);

        /// <summary>
        /// How long <see cref="OnDestroy"/> waits for the game to unwind before killing the
        /// process. It only has to cover <c>PhoneApplicationService.HandleApplicationExit()</c> —
        /// the WP7 "app is closing, save your state" hook, which ApplicationLaunch runs FIRST when
        /// the loop exits, ahead of Game.Dispose / audio teardown / the ALC unload. Everything
        /// after that hook only releases memory the OS is about to reclaim anyway, so waiting for
        /// it is pointless and (at the ~17 s that teardown has been measured to take) would blow
        /// straight through Android's activity-destroy watchdog into an ANR — which is exactly
        /// what made the game impossible to quit.
        /// </summary>
        private const int GameExitGraceMs = 3000;

        [DllImport("main")]
        private static extern void SetMain(System.Action main);

        public override void LoadLibraries()
        {
            base.LoadLibraries();
            SetMain(SDLMain);
        }

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            if (Configuration.Current == null)
            {
                Configuration.Current = new Configuration(GetExternalFilesDir(null)!.AbsolutePath);
            }

            base.OnCreate(savedInstanceState);

            CurrentActivity = this;

            // This is a fresh process, so none of MainActivity.OnCreate's process-wide setup has
            // run here. Games call Guide.ShowMessageBox / Guide.ShowInputBox mid-game (save
            // prompts, trial nags); those funcs are installed by ServicesSetup and dispatch onto
            // MessageBoxUtils.MainActivity, so without these two lines the first in-game dialog
            // NREs on a null activity.
            MessageBoxUtils.MainActivity = this;
            ServicesSetup.Start();

            string ?targetApplication = Intent!.GetStringExtra(TargetApplicationDataName);

            if (targetApplication == null)
            {
                return;
            }

            TargetLaunchApplication = JsonConvert.DeserializeObject<Models.Application>(targetApplication);
        }

        public override void OnWindowFocusChanged(bool hasFocus)
        {
            base.OnWindowFocusChanged(hasFocus);
        }

        /// <summary>
        /// Silences the song while the app is not in the foreground.
        ///
        /// <para>Sound effects need no help — they play through FAudio, and SDL pauses its audio
        /// device as part of this same lifecycle. The song is the exception: since the Android head
        /// swapped FAudio's <c>XNA_Song</c> for a platform <c>MediaPlayer</c>, the music is ours
        /// alone and nothing else will stop it, so it carried on playing over the home screen and
        /// other apps. API 36 mutes background playback itself, which hides this on a current
        /// emulator image but not on real devices.</para>
        ///
        /// <para><c>OnPause</c> rather than <c>OnStop</c>: the game is no longer interactive as soon
        /// as it is obscured, and that is the point at which WP7 considered an app deactivated.</para>
        /// </summary>
        protected override void OnPause()
        {
            // base FIRST. SDLActivity.OnPause drives the native pause and the EGL/surface teardown;
            // anything of ours that runs ahead of it and blocks or throws leaves SDL's lifecycle
            // half-applied. The music pause gains nothing from being first — the player is ours and
            // stays reachable either way — and the media lock CAN be held by the game thread across
            // a synchronous MediaPlayer.Prepare, so going first risks stalling the UI thread inside
            // a lifecycle callback.
            base.OnPause();
            SuspendMusicSafely();
        }

        /// <summary>
        /// Undoes <see cref="OnPause"/>'s suspend. Only a song this activity actually paused is
        /// resumed — a game that stopped or paused its own music keeps that state.
        /// </summary>
        protected override void OnResume()
        {
            base.OnResume();

            try
            {
                WPR.Audio.AndroidMediaPlayer.AndroidMediaPlayerBackend.Current?.RestoreFromForeground();
            }
            catch (Exception ex)
            {
                // Never let background music take down a lifecycle callback: an exception escaping
                // OnResume is an unhandled crash of the :game process.
                Common.Log.Warn(Common.LogCategory.AppList,
                    $"[wpr-media] foreground restore threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void SuspendMusicSafely()
        {
            try
            {
                WPR.Audio.AndroidMediaPlayer.AndroidMediaPlayerBackend.Current?.SuspendForBackground();
            }
            catch (Exception ex)
            {
                Common.Log.Warn(Common.LogCategory.AppList,
                    $"[wpr-media] background suspend threw: {ex.GetType().Name}: {ex.Message}");
            }
        }


        // Activity.OnBackPressed is deprecated from API 33 in favour of OnBackInvokedCallback,
        // which requires android:enableOnBackInvokedCallback in the manifest. We do not opt in,
        // so the system still dispatches KEYCODE_BACK and this override is still the hook.
#pragma warning disable CS0672
        /// <summary>
        /// Routes the hardware/system Back button into the game instead of closing it.
        ///
        /// <para>On WP7 Back is a game input, not "quit": titles walk back through their own
        /// screens with it and exit only at the root — and that exit unwinds the game loop, which
        /// takes <see cref="FinishIfNeeded"/>'s path out anyway. So a press becomes one frame of
        /// <c>GamePad.Buttons.Back</c>, exactly what Esc produces on the desktop head.</para>
        ///
        /// <para>Deliberately does NOT call <c>base</c>: <c>SDLActivity.onBackPressed</c> falls
        /// through to <c>Activity.onBackPressed</c> and finishes us. SDL's other route to the same
        /// place — <c>manualBackButton</c> → <c>superOnBackPressed</c>, which would bypass this
        /// override entirely — is shut off by the <c>SDL_ANDROID_TRAP_BACK_BUTTON</c> hint that
        /// <c>SDL2_FNAPlatform.ProgramInit</c> sets on Android.</para>
        ///
        /// <para>Before the game loop exists there is nothing to go back in, so Back leaves —
        /// that is the way out of a game that is still loading. Once it is running, a title that
        /// ignores Back would otherwise be inescapable; holding Back quits, see
        /// <see cref="OnKeyLongPress"/>.</para>
        /// </summary>
        public override void OnBackPressed()
        {
            WPR.Engine.GameLoop.IGameHost? host = RunningHost;

            if (host == null)
            {
                Finish();
                return;
            }

            host.PressBackButton();
        }
#pragma warning restore CS0672

        /// <summary>
        /// Hold Back to force-quit the game.
        ///
        /// <para><see cref="OnBackPressed"/> hands short presses to the game, so a title that
        /// never acts on <c>Buttons.Back</c> has no other way out; this is it. Android's default
        /// <c>Activity.OnKeyDown</c> starts tracking KEYCODE_BACK for us, and consuming the long
        /// press cancels the key-up that would otherwise also run OnBackPressed.</para>
        ///
        /// <para>Only reachable where Back is a real key press — three-button navigation or a
        /// hardware key. Gesture navigation has no long-press Back.</para>
        /// </summary>
        public override bool OnKeyLongPress(Keycode keyCode, KeyEvent? e)
        {
            if (keyCode == Keycode.Back)
            {
                Common.Log.Info(Common.LogCategory.AppList, "Back held — force-quitting the game.");
                Finish();
                return true;
            }

            return base.OnKeyLongPress(keyCode, e);
        }

        /// <summary>
        /// Ends the process this activity owns.
        ///
        /// <para>Deliberately does NOT call <c>base.OnDestroy()</c>. SDLActivity's implementation
        /// posts SDL_QUIT and then blocks the UI thread on <c>mSDLThread.join()</c> with no
        /// timeout — an unbounded wait on WPR's full teardown, which is the ANR that made Back
        /// impossible to escape from. Skipping the base call is safe only because the process
        /// never returns from this method: <c>KillProcess</c> below takes it down, so Android
        /// never observes the missing super call.</para>
        /// </summary>
        protected override void OnDestroy()
        {
            // Ask the game to exit and give it a bounded window to run its own exit/save hook.
            // Already-finished runs (game called Game.Exit itself) fall straight through.
            try { RunningHost?.RequestExit(); }
            catch { /* best-effort: the loop may already be gone */ }

            try { GameLoopFinished.Wait(GameExitGraceMs); }
            catch { /* wait must never be the reason we fail to exit */ }

            global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
        }

        public static void SDLMain()
        {
            // Should not be possible
            if (TargetLaunchApplication == null)
            {
                Common.Log.Error(Common.LogCategory.AppList, "Empty target application to launch!");
                return;
            }

            try
            {
                // The FNA3D driver is no longer chosen here: AndroidPlatform declares it as a
                // capability and FnaGameHost applies it before the game creates its device (the
                // force hint is read exactly once, inside FNA3D_PrepareWindowAttributes). The
                // declaration happens in ServicesSetup.Start(), which the :game process runs on
                // its way here.

                var host = new WPR.Backend.FNA.FnaGameHost(TargetLaunchApplication!, orientation =>
                {
                    CurrentActivity.RunOnUiThread(() =>
                    {
                        if (orientation == Microsoft.Xna.Framework.DisplayOrientation.Portrait)
                        {
                            CurrentActivity.RequestedOrientation = ScreenOrientation.Portrait;
                        }
                        else
                        {
                            CurrentActivity.RequestedOrientation = ScreenOrientation.Landscape;
                        }
                    });
                });

                RunningHost = host;
                host.RunAsync().Wait();
            }
            catch (Exception ex)
            {
                Intent errorIntent = new Intent();
                errorIntent.PutExtra(ErrorDataName, ex.ToString());

                CurrentActivity.SetResult(Result.FirstUser, errorIntent);
                FinishIfNeeded();
                return;
            }
            finally
            {
                // Unblocks OnDestroy's bounded wait. In the finally so a throwing game still
                // releases it rather than making every quit pay the full grace period.
                GameLoopFinished.Set();
            }

            CurrentActivity.SetResult(Result.Ok);
            FinishIfNeeded();
        }

        /// <summary>
        /// Finishes the activity once the game loop has ended.
        ///
        /// <para>Without this a game that exits on its own — WP7 titles quit themselves from their
        /// own menus, and the hardware Back button reaches them as <c>GamePad.Buttons.Back</c> —
        /// left a dead activity on screen: the loop had unwound but nothing tore the activity down.
        /// <c>Finish()</c> is called directly rather than posted, because the UI thread may already
        /// be inside <see cref="OnDestroy"/> and a posted action would never run.</para>
        /// </summary>
        private static void FinishIfNeeded()
        {
            try
            {
                if (CurrentActivity != null && !CurrentActivity.IsFinishing)
                {
                    CurrentActivity.Finish();
                }
            }
            catch { /* activity may already be gone */ }
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WPR.Backend.FNA;
using WPR.Common;
using WPR.Models;

namespace GameRun
{
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            AllocListener.StartIfAsked();
            if (Environment.GetEnvironmentVariable("GAMERUN_NOFOCUS") == "1")
            {
                Microsoft.Xna.Framework.WprActivationGuard.SuppressFocusActivation(TimeSpan.FromDays(1));
            }
            string productId = args.Length > 0 ? args[0] : null;
            if (productId == null)
            {
                Console.Error.WriteLine("usage: gamerun <productId-or-name-substring> [--exit-after <seconds>]");
                return;
            }

            // Optional: ask the host to exit after N seconds, so a smoke run exercises the whole
            // teardown path (Game.Exit -> Run returns -> ordered teardown -> ALC unload) rather
            // than being killed mid-frame.
            int exitAfter = 0;
            // Optional: after N seconds, flip Game.IsActive false then true on the game thread -
            // what a focus loss/gain does (a Guide message box on Android, an alt-tab here).
            int focusBlip = 0;
            string script = null, outDir = null;
            for (int i = 1; i + 1 < args.Length; i++)
            {
                if (args[i] == "--script") script = args[i + 1];
                if (args[i] == "--out") outDir = args[i + 1];
                if (args[i] == "--exit-after") int.TryParse(args[i + 1], out exitAfter);
                if (args[i] == "--focus-blip") int.TryParse(args[i + 1], out focusBlip);
            }

            // WPR_DATA_ROOT points the run at a scratch data root (its own applications.db,
            // AppData and IsolatedStore), so a repatched scratch copy can be run without touching
            // the user's installs or saves.
            Configuration.Current = new Configuration(
                Environment.GetEnvironmentVariable("WPR_DATA_ROOT") is { Length: > 0 } dataRoot
                    ? dataRoot
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WPR"));

            // gamerun --install <xap>: one XAP through the real install pipeline, then exit. The
            // desktop head's --reinstall-all would also repatch every installed game.
            if (productId == "--install" && args.Length > 1)
            {
                using var xap = new FileStream(args[1], FileMode.Open, FileAccess.Read, FileShare.Read);
                var err = WPR.ApplicationInstaller.Install(xap, _ => { }, _ => new AlwaysTrue(), System.Threading.CancellationToken.None, packageSource: args[1])
                    .GetAwaiter().GetResult();
                Console.Error.WriteLine($"[gamerun] install {Path.GetFileName(args[1])} -> {err}");
                Environment.Exit(err == WPR.ApplicationInstallError.None ? 0 : 1);
            }

            using var ctx = new ApplicationContext();
            var apps = ctx.Applications.AsNoTracking().ToList();
            var app = apps.FirstOrDefault(a =>
                         string.Equals(a.ProductId, productId, StringComparison.OrdinalIgnoreCase))
                   ?? apps.FirstOrDefault(a =>
                         a.Name != null && a.Name.IndexOf(productId, StringComparison.OrdinalIgnoreCase) >= 0);

            if (app == null)
            {
                Console.Error.WriteLine("no such game. installed:");
                foreach (var a in apps) Console.Error.WriteLine($"  {a.ProductId}  {a.Name}");
                return;
            }

            Console.Error.WriteLine($"[gamerun] launching {app.Name} ({app.ProductId}) patched=v{app.PatchedVersion}");
            Console.Error.WriteLine($"[gamerun] FNA3D_FORCE_DRIVER={Environment.GetEnvironmentVariable("FNA3D_FORCE_DRIVER")}");

            WPR.Xna.Rhi.XnaBackend.SetAchievements(new WPR.Database.Achievements.EfAchievementStore());
            Console.Error.WriteLine("[gamerun] achievement store registered");

            // What a crash report would carry to WPR Hub, printed instead of queued (the real
            // reporter is composed by a head, which this harness has none of).
            if (Environment.GetEnvironmentVariable("GAMERUN_HUB") == "1" &&
                WPR.Shell.HubSetup.TryCreate("windows", null, null) is { } hub)
            {
                WPR.Engine.Online.OnlineBackend.Set(hub.Services);
                Console.Error.WriteLine("[gamerun] WPR Hub composed: " + Configuration.Current.HubUri);
            }
            WPR.Engine.Online.OnlineBackend.SetCrashReporter(new PrintingCrashReporter());

            // The Windows head's run-time audio decoder (WP8 media engine MP3/AAC), so a WP8 title
            // here hears what it hears in the app. Set WPR_FFMPEG to the head's ffmpeg.exe; without
            // it the media engine fails compressed sources, as on a phone without the codec.
            string ffmpeg = Environment.GetEnvironmentVariable("WPR_FFMPEG");
            if (!string.IsNullOrEmpty(ffmpeg) && File.Exists(ffmpeg))
            {
                WPR.Engine.Audio.AudioDecoderBackend.SetDecoder(new WPR.Platform.Windows.Audio.FFmpegAudioDecoder(ffmpeg));
                Console.Error.WriteLine("[gamerun] audio decoder: " + ffmpeg);
                WPR.Engine.Audio.VideoDecoderBackend.SetDecoder(new WPR.Platform.Windows.Audio.FFmpegVideoDecoder(ffmpeg));
            }

            var host = new FnaGameHost(app);
            if (script != null)
            {
                // Before RunAsync: the host wraps its input backend with the synthetic-touch
                // injector only when a keyboard-emulation host is registered at that point.
                var s = new Script(script, outDir ?? Path.Combine(Path.GetTempPath(), "gamerun-shots"), () => host.PressBackButton());
                WPR.Xna.Rhi.XnaBackend.SetKeyboardEmulation(s);
                WPR.Engine.Sensors.SensorBackend.SetAccelerometer(s);
                Console.Error.WriteLine("[gamerun] script registered");
            }
            // The harness window sits on a desktop someone may be using, and the launch turns the
            // real mouse into a finger (TouchPanel.MouseAsTouch). A scripted run must only see its
            // own taps, so turn that off once the launch has set it.
            // Default for scripted runs: move the window off-screen so a stray real click cannot
            // land in it (MouseAsTouch would turn it into a tap). Frames are read back from the
            // device, so an off-screen window shoots the same pictures.
            if (script != null && Environment.GetEnvironmentVariable("GAMERUN_ONSCREEN") != "1")
            {
                Task.Delay(TimeSpan.FromSeconds(0.5)).ContinueWith(_ => Microsoft.Xna.Framework.WprGameThread.Post(() =>
                {
                    try
                    {
                        IntPtr handle = WPR.ApplicationLaunch.CurrentGame.Window.Handle;
                        SDL2.SDL.SDL_SetWindowPosition(handle, -4000, -4000);
                        Console.Error.WriteLine("[gamerun] window moved off-screen");
                    }
                    catch (Exception ex) { Console.Error.WriteLine("[gamerun] could not move window: " + ex.Message); }
                }));
            }

            // (Opt-in: switching it off also stops TouchPanel's update, and with it the script.)
            if (script != null && Environment.GetEnvironmentVariable("GAMERUN_NO_REAL_MOUSE") == "1")
            {
                Task.Delay(TimeSpan.FromSeconds(1)).ContinueWith(_ => Microsoft.Xna.Framework.WprGameThread.Post(() =>
                {
                    Microsoft.Xna.Framework.Input.Touch.TouchPanel.MouseAsTouch = false;
                    Console.Error.WriteLine("[gamerun] real mouse no longer reaches the game");
                }));
            }

            if (exitAfter > 0)
            {
                Task.Delay(TimeSpan.FromSeconds(exitAfter)).ContinueWith(_ =>
                {
                    Console.Error.WriteLine($"[gamerun] {exitAfter}s elapsed - RequestExit()");
                    host.RequestExit();
                });
            }

            if (focusBlip > 0)
            {
                Task.Delay(TimeSpan.FromSeconds(focusBlip)).ContinueWith(_ =>
                {
                    Microsoft.Xna.Framework.WprGameThread.Post(() =>
                    {
                        var g = WPR.ApplicationLaunch.CurrentGame;
                        var prop = typeof(Microsoft.Xna.Framework.Game).GetProperty("IsActive");
                        Console.Error.WriteLine("[gamerun] focus blip: IsActive=false");
                        prop.SetValue(g, false);
                        Console.Error.WriteLine("[gamerun] focus blip: IsActive=true");
                        prop.SetValue(g, true);
                    });
                });
            }

            host.RunAsync().GetAwaiter().GetResult();
            Console.Error.WriteLine($"[gamerun] game loop returned, host state={host.State}");
            Environment.Exit(0);
        }
    }

    internal sealed class PrintingCrashReporter : WPR.Engine.Online.ICrashReporter
    {
        public void Report(WPR.Engine.Online.CrashReport r)
        {
            Console.Error.WriteLine($"[crash-report] {r.Source} {r.RuntimePath} title={r.TitleId} type={r.ExceptionType}");
            Console.Error.WriteLine($"[crash-report] message: {r.ExceptionMessage}");
            foreach (string line in (r.StackTrace ?? "").Split('\n').Take(40))
            {
                Console.Error.WriteLine("[crash-report]   " + line);
            }
        }

        public System.Threading.Tasks.Task FlushAsync() => System.Threading.Tasks.Task.CompletedTask;
    }

    internal sealed class AlwaysTrue : IObservable<bool>
    {
        public IDisposable Subscribe(IObserver<bool> observer)
        {
            observer.OnNext(true);
            observer.OnCompleted();
            return new Nothing();
        }

        private sealed class Nothing : IDisposable
        {
            public void Dispose() { }
        }
    }
}

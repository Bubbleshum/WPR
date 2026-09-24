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
            for (int i = 1; i + 1 < args.Length; i++)
            {
                if (args[i] == "--exit-after") int.TryParse(args[i + 1], out exitAfter);
            }

            Configuration.Current = new Configuration(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WPR"));

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

            var host = new FnaGameHost(app);
            if (exitAfter > 0)
            {
                Task.Delay(TimeSpan.FromSeconds(exitAfter)).ContinueWith(_ =>
                {
                    Console.Error.WriteLine($"[gamerun] {exitAfter}s elapsed - RequestExit()");
                    host.RequestExit();
                });
            }

            host.RunAsync().GetAwaiter().GetResult();
            Console.Error.WriteLine($"[gamerun] game loop returned, host state={host.State}");
            Environment.Exit(0);
        }
    }
}

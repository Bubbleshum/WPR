using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using WPR;
using WPR.Common;
using WPR.Models;

namespace WPR.UI.Desktop
{
    /// <summary>
    /// Headless, opt-in library maintenance driven from <see cref="Program"/> when the
    /// process is launched with <c>--repatch-installed</c> or <c>--reinstall-all</c>.
    /// It re-runs the current <see cref="ApplicationPatcher"/> over every installed game
    /// (via <see cref="ApplicationInstaller.RepatchAsync"/>) and — with
    /// <c>--reinstall-all</c> — fresh-installs every library XAP that isn't installed yet
    /// (via <see cref="ApplicationInstaller.Install"/>). No UI; prints progress to stdout
    /// and returns so the caller can exit.
    ///
    /// Motivation: a patcher change that touches many already-installed games would
    /// otherwise require clicking "reinstall" per game in the UI — impractical for a large
    /// library. Reuses the exact install pipeline (and its dependency closure) rather than
    /// reimplementing it.
    /// </summary>
    internal static class BatchReinstall
    {
        public static async Task RunAsync(bool includeNew)
        {
            var token = CancellationToken.None;

            // ---- 1) Repatch every installed game -------------------------------------
            var installed = ApplicationContext.Current.Applications!.ToList();
            Console.WriteLine($"[batch] Repatching {installed.Count} installed game(s) with the current patcher...");

            int repOk = 0, repFail = 0, n = 0;
            foreach (Application app in installed)
            {
                n++;
                string label = $"({n}/{installed.Count}) {app.Name}";
                try
                {
                    ApplicationInstallError err = await ApplicationInstaller.RepatchAsync(app, _ => { }, token);
                    if (err == ApplicationInstallError.None)
                    {
                        repOk++;
                        Console.WriteLine($"[batch] repatch OK    {label}");
                    }
                    else
                    {
                        repFail++;
                        Console.WriteLine($"[batch] repatch FAIL  {label}  -> {err}");
                    }
                }
                catch (Exception ex)
                {
                    repFail++;
                    Console.WriteLine($"[batch] repatch ERROR {label}  -> {ex.Message}");
                }
            }
            Console.WriteLine($"[batch] Repatch complete: {repOk} ok, {repFail} failed.");

            if (!includeNew)
            {
                Console.WriteLine("[batch] --repatch-installed: skipping fresh installs. Done.");
                return;
            }

            // ---- 2) Fresh-install every library XAP not already installed ------------
            var installedIds = new System.Collections.Generic.HashSet<string>(
                installed.Select(a => Norm(a.ProductId)), StringComparer.OrdinalIgnoreCase);

            using var scanner = new LibraryScanner { Path = Configuration.Current!.GameLibraryPath };
            var discovered = scanner.ScanOnce().ToList();
            var toInstall = discovered
                .Where(d => d.Preview != null && !installedIds.Contains(Norm(d.Preview.ProductId)))
                .ToList();

            Console.WriteLine(
                $"[batch] Library '{Configuration.Current.GameLibraryPath}': {discovered.Count} XAP(s) readable, " +
                $"{toInstall.Count} not yet installed.");

            int insOk = 0, insFail = 0, m = 0;
            foreach (DiscoveredApplication d in toInstall)
            {
                m++;
                string name = string.IsNullOrEmpty(d.Preview?.Name) ? Path.GetFileName(d.XapFilePath) : d.Preview!.Name;
                string label = $"({m}/{toInstall.Count}) {name}";
                try
                {
                    using var fs = new FileStream(d.XapFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    ApplicationInstallError err = await ApplicationInstaller.Install(
                        fs,
                        _ => { },
                        _ => new AlwaysTrue(),   // auto-confirm "overwrite existing" (shouldn't fire — these aren't installed)
                        token);

                    if (err == ApplicationInstallError.None)
                    {
                        insOk++;
                        Console.WriteLine($"[batch] install OK    {label}");
                    }
                    else
                    {
                        insFail++;
                        Console.WriteLine($"[batch] install FAIL  {label}  -> {err}");
                    }
                }
                catch (Exception ex)
                {
                    insFail++;
                    Console.WriteLine($"[batch] install ERROR {label}  -> {ex.Message}");
                }
            }

            Console.WriteLine($"[batch] Install complete: {insOk} ok, {insFail} failed.");
            Console.WriteLine($"[batch] ALL DONE. Repatch {repOk}/{installed.Count}; Install {insOk}/{toInstall.Count}.");
        }

        // ProductIds are stored trimmed of braces in the DB but a preview may carry either
        // form; normalize both sides before comparing.
        private static string Norm(string? productId) =>
            (productId ?? "").Trim().Trim('{', '}').ToLowerInvariant();

        // Minimal IObservable<bool> that yields true once and completes. ApplicationInstaller
        // awaits the delete-confirmation observable (System.Reactive awaiter -> last value);
        // this avoids taking a direct System.Reactive dependency here.
        private sealed class AlwaysTrue : IObservable<bool>
        {
            public IDisposable Subscribe(IObserver<bool> observer)
            {
                observer.OnNext(true);
                observer.OnCompleted();
                return Noop.Instance;
            }

            private sealed class Noop : IDisposable
            {
                public static readonly Noop Instance = new Noop();
                public void Dispose() { }
            }
        }
    }
}

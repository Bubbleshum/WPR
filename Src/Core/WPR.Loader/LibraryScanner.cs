using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using WPR.Common;

namespace WPR
{
    public class DiscoveredApplication
    {
        public string XapFilePath { get; set; } = "";
        public ApplicationPreview Preview { get; set; } = new ApplicationPreview();
    }

    public class LibraryScanner : IDisposable
    {
        private FileSystemWatcher? _Watcher;
        private string? _Path;

        public event EventHandler<DiscoveredApplication>? Discovered;
        public event EventHandler<string>? Removed;

        public string? Path
        {
            get => _Path;
            set
            {
                if (string.Equals(_Path, value, StringComparison.OrdinalIgnoreCase)) return;
                _Path = value;
                Restart();
            }
        }

        public IEnumerable<DiscoveredApplication> ScanOnce()
        {
            if (string.IsNullOrEmpty(_Path) || !Directory.Exists(_Path)) yield break;

            string[] files;
            try
            {
                files = Directory.GetFiles(_Path, "*.xap", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppInstall, $"LibraryScanner: failed to enumerate '{_Path}': {ex.Message}");
                yield break;
            }

            foreach (string file in files)
            {
                DiscoveredApplication? entry = TryRead(file);
                if (entry != null) yield return entry;
            }
        }

        private static DiscoveredApplication? TryRead(string filePath)
        {
            try
            {
                using FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                ApplicationPreview? preview = ApplicationInstaller.ReadPreview(fs);
                if (preview == null) return null;
                return new DiscoveredApplication { XapFilePath = filePath, Preview = preview };
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppInstall, $"LibraryScanner: failed to read XAP preview from '{filePath}': {ex.Message}");
                return null;
            }
        }

        private void Restart()
        {
            FileSystemWatcher? old = _Watcher;
            _Watcher = null;
            old?.Dispose();

            if (string.IsNullOrEmpty(_Path) || !Directory.Exists(_Path)) return;

            try
            {
                FileSystemWatcher watcher = new FileSystemWatcher(_Path, "*.xap")
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                watcher.Created += OnCreated;
                watcher.Deleted += OnDeleted;
                watcher.Renamed += OnRenamed;
                watcher.EnableRaisingEvents = true;
                _Watcher = watcher;
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppInstall, $"LibraryScanner: failed to watch '{_Path}': {ex.Message}");
            }
        }

        private async void OnCreated(object sender, FileSystemEventArgs e)
        {
            // The file may still be in the process of being copied/written when the
            // notification fires. Retry a few times until ZipArchive can read it.
            for (int attempt = 0; attempt < 6; attempt++)
            {
                DiscoveredApplication? entry = TryRead(e.FullPath);
                if (entry != null)
                {
                    Discovered?.Invoke(this, entry);
                    return;
                }
                await Task.Delay(500);
            }
        }

        private void OnDeleted(object sender, FileSystemEventArgs e)
        {
            Removed?.Invoke(this, e.FullPath);
        }

        private async void OnRenamed(object sender, RenamedEventArgs e)
        {
            Removed?.Invoke(this, e.OldFullPath);
            for (int attempt = 0; attempt < 6; attempt++)
            {
                DiscoveredApplication? entry = TryRead(e.FullPath);
                if (entry != null)
                {
                    Discovered?.Invoke(this, entry);
                    return;
                }
                await Task.Delay(500);
            }
        }

        public void Dispose()
        {
            FileSystemWatcher? watcher = _Watcher;
            _Watcher = null;
            watcher?.Dispose();
        }
    }
}

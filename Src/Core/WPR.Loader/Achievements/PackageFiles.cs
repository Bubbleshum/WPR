using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace WPR.Achievements
{
    /// <summary>
    /// The files of a game package, wherever they are: an install folder or the XAP itself. The
    /// catalogue generator reads through this so an install and a read-only library scan run the
    /// same code. Paths use forward slashes and are relative to the package root.
    /// </summary>
    public interface IPackageFiles : IDisposable
    {
        IReadOnlyList<string> Paths { get; }

        long Length(string path);

        byte[] ReadAll(string path);
    }

    /// <summary>An extracted package (an install folder).</summary>
    public sealed class DirectoryPackageFiles : IPackageFiles
    {
        private readonly string _root;
        private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

        public DirectoryPackageFiles(string root)
        {
            _root = Path.GetFullPath(root);
            if (Directory.Exists(_root))
            {
                foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    // The patcher's pristine sidecars and WPR's own files are not the package.
                    if (file.EndsWith(".original", StringComparison.OrdinalIgnoreCase)) continue;
                    if (file.EndsWith(".native_original", StringComparison.OrdinalIgnoreCase)) continue;
                    string relative = Path.GetRelativePath(_root, file).Replace('\\', '/');
                    _files[relative] = file;
                }
            }
            Paths = _files.Keys.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public IReadOnlyList<string> Paths { get; }

        public long Length(string path) => _files.TryGetValue(path, out string? full) ? new FileInfo(full).Length : 0;

        /// <summary>
        /// A patched or stubbed module is read from its pristine sidecar when there is one: the
        /// generator wants what the package shipped (the game's own references, which the patcher
        /// retargets; the native module an Xbox SPA sits in, which the stubber replaces).
        /// </summary>
        public byte[] ReadAll(string path)
        {
            string full = _files[path];
            // WinmdStubber keeps a native module it replaced with a managed stub as .native_original.
            foreach (string sidecar in new[] { full + ".native_original", full + ".original" })
            {
                if (File.Exists(sidecar)) return File.ReadAllBytes(sidecar);
            }
            return File.ReadAllBytes(full);
        }

        public void Dispose() { }
    }

    /// <summary>A package read straight from its XAP, nothing extracted.</summary>
    public sealed class ZipPackageFiles : IPackageFiles
    {
        private readonly ZipArchive _zip;
        private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

        public ZipPackageFiles(Stream stream)
        {
            _zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            foreach (ZipArchiveEntry entry in _zip.Entries)
            {
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
                _entries[Uri.UnescapeDataString(entry.FullName.Replace('\\', '/'))] = entry;
            }
            Paths = _entries.Keys.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public IReadOnlyList<string> Paths { get; }

        public long Length(string path) => _entries.TryGetValue(path, out ZipArchiveEntry? e) ? e.Length : 0;

        public byte[] ReadAll(string path)
        {
            using Stream s = _entries[path].Open();
            using MemoryStream m = new();
            s.CopyTo(m);
            return m.ToArray();
        }

        public void Dispose() => _zip.Dispose();
    }
}

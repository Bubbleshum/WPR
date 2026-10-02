using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace WPR.Engine.Online
{
    /// <summary>
    /// What was installed: the package's SHA-256 and size, and where it came from. The installer
    /// writes it into the install folder as <see cref="FileName"/>.
    /// </summary>
    /// <remarks>
    /// <para>The hash identifies the exact build that crashed, so the hub can group reports by
    /// build and ask for a package it has never seen. It has to be taken at install, from the
    /// package itself: the install folder holds patched assemblies, and nothing in it hashes to
    /// the original.</para>
    ///
    /// <para>Games installed before this existed have no record. Their reports still go out, just
    /// without a hash, so the hub can never ask for their package. Reinstalling writes one.</para>
    /// </remarks>
    public sealed class GamePackageRecord
    {
        public const string FileName = "wpr-package.json";

        /// <summary>Lowercase hex.</summary>
        public string Sha256 { get; set; } = "";

        public long Size { get; set; }

        /// <summary>The package's own file name, e.g. <c>Angry Birds.xap</c>.</summary>
        public string? PackageFileName { get; set; }

        /// <summary>
        /// Head-specific: a filesystem path on the desktop, a <c>content://</c> URI with a
        /// persisted read grant on Android. Null when the head could not keep one.
        /// </summary>
        public string? Source { get; set; }

        public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };

        /// <summary>
        /// Hashes a seekable package stream and puts its position back where it was. Null for a
        /// forward-only stream, which cannot be read twice.
        /// </summary>
        public static GamePackageRecord? Measure(Stream package)
        {
            if (!package.CanSeek) return null;

            long position = package.Position;
            try
            {
                package.Position = 0;
                byte[] hash = SHA256.HashData(package);
                return new GamePackageRecord
                {
                    Sha256 = Convert.ToHexString(hash).ToLowerInvariant(),
                    Size = package.Length,
                    PackageFileName = package is FileStream fs ? Path.GetFileName(fs.Name) : null,
                };
            }
            finally
            {
                package.Position = position;
            }
        }

        public static GamePackageRecord? TryLoad(string? installFolder)
        {
            if (string.IsNullOrEmpty(installFolder)) return null;
            try
            {
                string path = Path.Combine(installFolder, FileName);
                if (!File.Exists(path)) return null;
                GamePackageRecord? record = JsonSerializer.Deserialize<GamePackageRecord>(File.ReadAllText(path));
                return record is { Sha256.Length: 64 } ? record : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Save(string installFolder)
        {
            File.WriteAllText(Path.Combine(installFolder, FileName), JsonSerializer.Serialize(this, Json));
        }
    }
}

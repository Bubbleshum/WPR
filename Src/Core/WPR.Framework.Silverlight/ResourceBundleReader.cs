using System;
using System.Collections;
using System.IO;
using System.Resources;
using System.Text;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Reads entries from a <c>System.Resources.ResourceReader</c>-format stream — the
    /// format used by WPF/Silverlight's <c>&lt;Assembly&gt;.g.resources</c> bundle
    /// where compiled XAML and other build-time resources are stored.
    /// </summary>
    public static class ResourceBundleReader
    {
        /// <summary>
        /// Search a resource bundle for an entry whose key matches <paramref name="targetPath"/>
        /// (case-insensitive, with forward-slash separator). Returns a fresh seekable copy of
        /// the entry's bytes, or null if no matching entry exists.
        /// </summary>
        public static Stream? FindEntry(Stream bundleStream, string targetPath)
        {
            if (bundleStream == null) throw new ArgumentNullException(nameof(bundleStream));
            if (targetPath == null) throw new ArgumentNullException(nameof(targetPath));

            string normTarget = targetPath.Replace('\\', '/').ToLowerInvariant();
            using var reader = new ResourceReader(bundleStream);
            foreach (DictionaryEntry entry in reader)
            {
                string key = (entry.Key?.ToString() ?? "").Replace('\\', '/').ToLowerInvariant();
                if (key == normTarget || key.EndsWith("/" + normTarget, StringComparison.Ordinal))
                {
                    return CopyValueToStream(entry.Value);
                }
            }
            return null;
        }

        private static Stream? CopyValueToStream(object? value)
        {
            switch (value)
            {
                case byte[] bytes:
                    return new MemoryStream(bytes, writable: false);
                case Stream s:
                    {
                        var ms = new MemoryStream();
                        s.CopyTo(ms);
                        ms.Position = 0;
                        return ms;
                    }
                case string str:
                    return new MemoryStream(Encoding.UTF8.GetBytes(str), writable: false);
                default:
                    return null;
            }
        }
    }
}

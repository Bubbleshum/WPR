// MonoGame - Copyright (C) The MonoGame Team
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.IO;

namespace MonoGame.Utilities
{
    internal static class FileHelpers
    {
        public static readonly char ForwardSlash = '/';
        public static readonly string ForwardSlashString = new string(ForwardSlash, 1);
        public static readonly char BackwardSlash = '\\';

#if WINRT
        public static readonly char NotSeparator = ForwardSlash;
        public static readonly char Separator = BackwardSlash;
#else
        public static readonly char NotSeparator = Path.DirectorySeparatorChar == BackwardSlash ? ForwardSlash : BackwardSlash;
        public static readonly char Separator = Path.DirectorySeparatorChar;
#endif

        public static string NormalizeFilePathSeparators(string name)
        {
            return name.Replace(NotSeparator, Separator);
        }

        /// <summary>
        /// Combines the filePath and relativeFile based on relativeFile being a file in the same location as filePath.
        /// Relative directory operators (..) are also resolved
        /// </summary>
        /// <example>"A\B\C.txt","D.txt" becomes "A\B\D.txt"</example>
        /// <example>"A\B\C.txt","..\D.txt" becomes "A\D.txt"</example>
        /// <example>"A\B.txt","..\..\C.txt" becomes "..\C.txt" — see remarks</example>
        /// <remarks>
        /// A reference that walks above <paramref name="filePath"/>'s own root KEEPS its
        /// leading <c>..</c> segments instead of being clamped at that root. Asset names
        /// arriving here are relative to a ContentManager's RootDirectory, so a surviving
        /// leading <c>..</c> is how content legitimately addresses a sibling of that
        /// directory; clamping silently rewrites the reference to name a file that does
        /// not exist.
        ///
        /// Fable: Coin Golf is the reference case. Its ContentManager roots at
        /// <c>Content/data</c>, and every level piece under <c>Content/data/pieces/</c>
        /// names its textures <c>..\..\&lt;texture&gt;</c> — that is <c>Content/&lt;texture&gt;</c>,
        /// one level above the root, which is exactly where all 113 of those XNBs ship.
        /// Clamping turned every one of them into <c>Content/data/&lt;texture&gt;</c>, so
        /// they all failed to load and each level drew as a black screen. The failure is
        /// invisible from inside the game because ContentReader.ReadExternalReference
        /// treats a missing referenced asset as optional and hands back default(T).
        ///
        /// This replaces a <see cref="Uri"/>-based implementation inherited from MonoGame.
        /// Uri clamps at the root by design (RFC 3986 remove_dot_segments), and it also
        /// reads <c>#</c> and <c>?</c> in an asset name as a fragment/query delimiter —
        /// both are ordinary filename characters in XNB content, as are the spaces this
        /// game's texture names carry.
        /// </remarks>
        /// <param name="filePath">Path to the file we are starting from</param>
        /// <param name="relativeFile">Relative location of another file to resolve the path to</param>
        public static string ResolveRelativePath(string filePath, string relativeFile)
        {
            filePath = filePath.Replace(BackwardSlash, ForwardSlash);
            relativeFile = relativeFile.Replace(BackwardSlash, ForwardSlash);

            // Whether the result keeps a leading slash is decided by filePath alone. That
            // is what the Uri implementation did: it prepended one when absent so the
            // "file://" URI was well formed, then stripped it back off the result.
            bool rooted = filePath.StartsWith(ForwardSlashString, StringComparison.Ordinal);

            string combined;
            if (relativeFile.StartsWith(ForwardSlashString, StringComparison.Ordinal))
            {
                // An absolute reference replaces the base path entirely.
                combined = relativeFile;
            }
            else
            {
                int lastSlash = filePath.LastIndexOf(ForwardSlash);
                string baseDirectory = lastSlash < 0
                    ? string.Empty
                    : filePath.Substring(0, lastSlash + 1);
                combined = baseDirectory + relativeFile;
            }

            var segments = new List<string>();
            foreach (string segment in combined.Split(ForwardSlash))
            {
                // Empty segments come from "//" runs and from a leading/trailing slash. The
                // Uri implementation collapsed those too (it pre-sanitized "//" by hand).
                if (segment.Length == 0 || segment == ".")
                {
                    continue;
                }

                // ".." collapses against a real preceding segment. With nothing to collapse
                // against — either at the start, or behind an earlier ".." — it is kept.
                if (segment == ".."
                    && segments.Count > 0
                    && !string.Equals(segments[segments.Count - 1], "..", StringComparison.Ordinal))
                {
                    segments.RemoveAt(segments.Count - 1);
                    continue;
                }

                segments.Add(segment);
            }

            string resolved = string.Join(ForwardSlashString, segments);
            if (rooted)
            {
                resolved = ForwardSlashString + resolved;
            }

            return NormalizeFilePathSeparators(resolved);
        }
    }
}

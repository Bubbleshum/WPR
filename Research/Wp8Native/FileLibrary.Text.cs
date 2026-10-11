using System.Text;

namespace WPR.Wp8Native
{
    /// <summary>
    /// Formatted text over a C stdio stream, for the CRT functions that format or scan through a
    /// FILE* without being stdio themselves (vfprintf, fscanf: HostStubs.Remainder.cs). Narrow text is
    /// one byte per character, as the "C" locale's.
    /// </summary>
    public sealed partial class FileLibrary
    {
        /// <summary>Whether <paramref name="file"/> is a FILE* fopen handed out.</summary>
        internal bool IsStdioStream(long file) => Find(file) is not null;

        /// <summary>Writes at the stream's position; the bytes written, or -1 for a stream it does not know.</summary>
        internal int WriteStreamText(long file, string text)
        {
            OpenFile? open = Find(file);
            if (open is null)
            {
                return -1;
            }

            byte[] bytes = Encoding.Latin1.GetBytes(text);
            open.Stream.Position = open.FilePointer;
            open.Stream.Write(bytes);
            open.FilePointer += bytes.Length;
            return bytes.Length;
        }

        /// <summary>
        /// Up to <paramref name="max"/> characters from the stream's position, without consuming them;
        /// null for a stream it does not know.
        /// </summary>
        internal string? PeekStreamText(long file, int max)
        {
            OpenFile? open = Find(file);
            if (open is null)
            {
                return null;
            }

            byte[] buffer = new byte[max];
            open.Stream.Position = open.FilePointer;
            int read = open.Stream.Read(buffer, 0, max);
            return Encoding.Latin1.GetString(buffer, 0, read);
        }

        /// <summary>Consumes <paramref name="count"/> characters; <paramref name="atEnd"/> sets the end-of-file flag.</summary>
        internal void AdvanceStream(long file, int count, bool atEnd)
        {
            if (Find(file) is { } open)
            {
                open.FilePointer += count;
                open.AtEof |= atEnd;
            }
        }
    }
}

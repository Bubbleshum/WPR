using System.Collections.Generic;
using System.IO;
using WPR.Engine.Content;

namespace WPR.WindowsCompability
{
    /// <summary>
    /// Shim for <c>System.IO.File</c>, installed by <c>ApplicationPatcher.MemberPatches</c>.
    ///
    /// <para><b>Deliberately logic-free.</b> Every member is one call into
    /// <see cref="ContentPaths"/> plus the real <see cref="File"/> call. The rules that decide
    /// what a path means are declared by the platform and applied by the engine; this type exists
    /// only because the patcher has to name something a game's rewritten IL can bind, and that
    /// has to sit beside the other patch targets.</para>
    ///
    /// <para><b>Reads use Resolve, writes use Normalize</b> - see the note on
    /// <see cref="ContentPaths"/>. Resolve may redirect to a file that already exists in the
    /// install folder, which is right for a read and wrong for a create.</para>
    /// </summary>
    public static class File2
    {
        public static FileStream Open(string path, FileMode mode) =>
            File.Open(ContentPaths.Resolve(path)!, mode);

        public static FileStream Open(string path, FileMode mode, FileAccess access) =>
            File.Open(ContentPaths.Resolve(path)!, mode, access);

        public static FileStream Open(string path, FileMode mode, FileAccess access, FileShare share) =>
            File.Open(ContentPaths.Resolve(path)!, mode, access, share);

        public static bool Exists(string? path) =>
            File.Exists(ContentPaths.Resolve(path));

        public static StreamReader OpenText(string path) =>
            File.OpenText(ContentPaths.Resolve(path)!);

        public static FileStream OpenRead(string path) =>
            File.OpenRead(ContentPaths.Resolve(path)!);

        public static string ReadAllText(string path) =>
            File.ReadAllText(ContentPaths.Resolve(path)!);

        public static byte[] ReadAllBytes(string path) =>
            File.ReadAllBytes(ContentPaths.Resolve(path)!);

        public static string[] ReadAllLines(string path) =>
            File.ReadAllLines(ContentPaths.Resolve(path)!);

        public static IEnumerable<string> ReadLines(string path) =>
            File.ReadLines(ContentPaths.Resolve(path)!);

        public static void Copy(string sourceFileName, string destFileName) =>
            File.Copy(ContentPaths.Resolve(sourceFileName)!, ContentPaths.Normalize(destFileName)!);

        public static void Copy(string sourceFileName, string destFileName, bool overwrite) =>
            File.Copy(ContentPaths.Resolve(sourceFileName)!, ContentPaths.Normalize(destFileName)!, overwrite);

        public static void Move(string sourceFileName, string destFileName) =>
            File.Move(ContentPaths.Resolve(sourceFileName)!, ContentPaths.Normalize(destFileName)!);

        // Creating or writing: separators only. Resolving could silently land a brand-new file on
        // an existing one in the install folder instead of where the game asked for it.
        public static FileStream OpenWrite(string path) =>
            File.OpenWrite(ContentPaths.Normalize(path)!);

        public static FileStream Create(string path) =>
            File.Create(ContentPaths.Normalize(path)!);

        public static StreamWriter CreateText(string path) =>
            File.CreateText(ContentPaths.Normalize(path)!);

        public static StreamWriter AppendText(string path) =>
            File.AppendText(ContentPaths.Normalize(path)!);

        public static void WriteAllText(string path, string? contents) =>
            File.WriteAllText(ContentPaths.Normalize(path)!, contents);

        public static void WriteAllBytes(string path, byte[] bytes) =>
            File.WriteAllBytes(ContentPaths.Normalize(path)!, bytes);

        public static void WriteAllLines(string path, string[] contents) =>
            File.WriteAllLines(ContentPaths.Normalize(path)!, contents);

        public static void AppendAllText(string path, string? contents) =>
            File.AppendAllText(ContentPaths.Normalize(path)!, contents);

        public static void Delete(string path) =>
            File.Delete(ContentPaths.Normalize(path)!);
    }
}

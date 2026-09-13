using System.Xml.Linq;
using WPR.Engine.Content;

namespace WPR.WindowsCompability
{
    /// <summary>
    /// Redirect target for <c>System.Xml.Linq.XElement.Load(string)</c>, wired up in
    /// <c>ApplicationPatcher.MemberPatches</c>. Lives here alongside the other BCL-method
    /// redirects (<see cref="Path2"/>, <see cref="GC2"/>, <see cref="Type2"/>) rather than in a
    /// project of its own — it was the sole contents of WPR.StandardCompability, which was
    /// removed on 2026-08-30.
    /// </summary>
    public class XElement2
    {
        public static XElement Load(string path) => XElement.Load(Resolve(path));

        public static XElement Load(string path, LoadOptions options) =>
            XElement.Load(Resolve(path), options);

        /// <summary>
        /// The path this shim will actually open.
        ///
        /// <para>Both halves of what used to be written out here - normalising Windows
        /// separators, and falling back to the game's install folder for a relative path the
        /// working directory cannot satisfy - now live in
        /// <see cref="ContentPaths.Resolve"/>, driven by rules the platform declares. This shim
        /// was where that logic was first written; generalising it to every path-taking BCL
        /// member is what moved it into the engine.</para>
        /// </summary>
        private static string Resolve(string path) => ContentPaths.Resolve(path)!;
    }
}

using System;

namespace Microsoft.Xna.Framework.Media.PhoneExtensions
{
    /// <summary>
    /// Shim for WP8's <c>Microsoft.Xna.Framework.Media.PhoneExtensions.MediaLibraryExtensions</c>.
    /// Without it the blanket <c>Microsoft.Xna.*</c> rescope sent the type to FNA, which defines no
    /// XNA API, and YouTube's view model died with an unhandled TypeLoadException at start-up.
    /// </summary>
    public static class MediaLibraryExtensions
    {
        /// <summary>
        /// The file path of the picture a Photos-hub share or edit launch handed the app, by the
        /// token in its query string.
        /// </summary>
        /// <remarks>
        /// WPR has no photo gallery (<see cref="MediaLibrary.Pictures"/> is empty) and never
        /// launches an app with a media token, so no token names a picture; it throws, as the phone
        /// does for a token it did not issue. YouTube calls it only when such a launch reaches it.
        /// </remarks>
        public static string GetPathFromToken(this MediaLibrary library, string token)
        {
            if (library == null) throw new ArgumentNullException(nameof(library));
            if (token == null) throw new ArgumentNullException(nameof(token));
            throw new ArgumentException("No picture in the media library has this token.", nameof(token));
        }
    }
}

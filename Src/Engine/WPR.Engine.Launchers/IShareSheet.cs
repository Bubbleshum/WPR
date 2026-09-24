namespace WPR.Engine.Launchers
{
    /// <summary>
    /// Offers text to the host OS's share UI — on Android the system chooser, where the player
    /// picks a messaging app, email, a social app and so on.
    /// </summary>
    /// <remarks>
    /// <para>A separate contract from <see cref="IUriLauncher"/>, not a second member on it: one
    /// device per slot. Opening a link and sharing one are different OS facilities, and a
    /// platform can have either without the other (the desktop head opens links but declares no
    /// share sheet).</para>
    ///
    /// <para>Plain text only, because that is all WP7's share tasks carry: <c>ShareLinkTask</c> is
    /// a title, a message and a link; <c>ShareStatusTask</c> is a status string. How those fold
    /// into one body is the shim's decision.</para>
    /// </remarks>
    public interface IShareSheet
    {
        /// <summary>
        /// Shows the share UI for <paramref name="text"/>. Must not throw.
        /// </summary>
        /// <param name="subject">A subject line for targets that have one (email), or null.</param>
        /// <param name="text">The body to share. Never empty.</param>
        /// <returns>True if the OS accepted the request.</returns>
        bool TryShare(string? subject, string text);
    }
}

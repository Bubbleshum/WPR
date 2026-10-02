using WPR.Engine.Online;

namespace WPR.Online.Hub;

/// <summary>
/// Everything the module needs from the head. The three <c>Func</c>s are read LIVE, on every
/// operation, so a settings change applies at once without recomposing the platform - the shape
/// <c>VibrationBackend.IsEnabled</c> uses, and the one CLAUDE.md recommends over storing a value.
/// </summary>
public sealed class HubSettings
{
    /// <summary>The hub's base URL, or null when none is configured - in which case nothing is ever sent.</summary>
    public required Func<Uri?> BaseUri { get; init; }

    /// <summary>The device token from sign-in, or null when signed out.</summary>
    public required Func<string?> AccessToken { get; init; }

    /// <summary>
    /// The player's server-logging opt-in: automatic crash reports, and uploading an unknown game
    /// package without asking. Signed in, the hub goes by the account setting instead.
    /// </summary>
    public required Func<bool> ServerLogging { get; init; }

    /// <summary>
    /// The player chose to appear offline to friends. Presence still beats (so messages and counts
    /// keep working), but as <see cref="Client.PresenceStatuses.AppearOffline"/>. Null = never.
    /// </summary>
    public Func<bool>? AppearOffline { get; init; }

    /// <summary>A folder this module owns. Shared by every process of the app.</summary>
    public required string DataDirectory { get; init; }

    public required string WprVersion { get; init; }

    /// <summary><c>windows</c> or <c>android</c>, as the hub spells them.</summary>
    public required string Platform { get; init; }

    /// <summary>The local database's queue of unlocks and play sessions awaiting upload. Null disables progress sync.</summary>
    public IOnlineLocalStore? Local { get; init; }

    /// <summary>
    /// Before the account's unlocks are restored into the local database: make sure these games'
    /// achievement catalogues are there, installed or not (hub title ids). Null skips it, and a
    /// restored game then lists only the achievements the player has earned.
    /// </summary>
    public Func<IReadOnlyCollection<string>, Task>? PrepareTitles { get; init; }

    /// <summary>
    /// The head already has tile art for this game (hub title id). With <see cref="SaveTitleArt"/>,
    /// a restore downloads the hub's icon for each game the account has played that lacks it.
    /// </summary>
    public Func<string, bool>? HasTitleArt { get; init; }

    /// <summary>Store a game's tile art (PNG bytes from the hub) for a game not installed here.</summary>
    public Action<string, byte[]>? SaveTitleArt { get; init; }

    /// <summary>A restore marked this many achievements earned. For repainting a gamerscore or a list.</summary>
    public Action<int>? ProgressRestored { get; init; }

    /// <summary>How to get a game's original package back, for uploads. Null disables uploads.</summary>
    public IGamePackageSource? Packages { get; init; }

    /// <summary>Called when the hub rejects the stored token (the device was signed out on the website).</summary>
    public Action? SignedOut { get; init; }

    /// <summary>Persist a completed sign-in: the device token and the username it belongs to.</summary>
    public Action<string, string>? SignedIn { get; init; }

    /// <summary>
    /// The account's gamerpic, saved as a local PNG (null = the account has none). The flag is true
    /// when the player just picked it, and false when it arrived with a sign-in or refresh - the
    /// head should then leave a picture the player chose locally alone.
    /// </summary>
    public Action<string?, bool>? GamerpicChanged { get; init; }

    /// <summary>
    /// The app's own recent log, for crash reports when the game left no per-game log (every
    /// Release build). Given a window, returns the lines within it.
    /// </summary>
    public Func<TimeSpan, string>? RecentLog { get; init; }

    /// <summary>Diagnostics sink. Lines are prefixed <c>[wpr-online]</c>.</summary>
    public Action<string>? Log { get; init; }
}

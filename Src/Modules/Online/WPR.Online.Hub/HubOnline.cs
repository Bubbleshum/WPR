using WPR.Engine.Online;

namespace WPR.Online.Hub;

/// <summary>
/// The module's plug: every service over one connection (one HttpClient), ready for
/// <c>caps.Online(online.Services)</c>.
/// </summary>
public sealed class HubOnline
{
    public HubOnline(HubSettings settings)
    {
        var connection = new HubConnection(settings);
        Leaderboards = new HubLeaderboards(connection);
        Crashes = new HubCrashReporter(connection);
        Account = new HubAccountService(connection);
        Progress = new HubProgressSync(connection);
        Presence = new HubPresence(connection);
        Social = new HubSocialService(connection);
        Services = new OnlineServices
        {
            Leaderboards = Leaderboards,
            Crashes = Crashes,
            Progress = Progress,
            Presence = Presence,
            Local = settings.Local,
        };
    }

    public HubLeaderboards Leaderboards { get; }

    public HubCrashReporter Crashes { get; }

    /// <summary>Sign-in and gamerpics, for the settings pages. Not an engine contract: nothing in a game needs it.</summary>
    public HubAccountService Account { get; }

    public HubProgressSync Progress { get; }

    public HubPresence Presence { get; }

    /// <summary>Friends and games, for the WPR Hub page. Not an engine contract either.</summary>
    public HubSocialService Social { get; }

    /// <summary>What the platform declares.</summary>
    public OnlineServices Services { get; }
}

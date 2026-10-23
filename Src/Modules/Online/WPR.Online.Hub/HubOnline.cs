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
        Progress = new HubProgressSync(connection);
        Account = new HubAccountService(connection, Progress);
        Presence = new HubPresence(connection);
        Social = new HubSocialService(connection);
        GameServices = new HubGameServices(connection);
        Invites = new HubGameInvites(Social);
        MessageAlerts = new HubMessageAlerts(connection, Social, settings.DataDirectory);
        Push = new HubPush(connection);
        TitleDetails = new HubTitleDetails(connection);
        Presence.BeatReceived = MessageAlerts.OnBeat;
        Services = new OnlineServices
        {
            Leaderboards = Leaderboards,
            Crashes = Crashes,
            Progress = Progress,
            Presence = Presence,
            Local = settings.Local,
            GameServices = GameServices,
            Invites = Invites,
            TitleDetails = TitleDetails,
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

    /// <summary>Where a game's retired online services live on the hub (its lobby and HTTP calls).</summary>
    public HubGameServices GameServices { get; }

    /// <summary>New incoming messages and invites, noticed from the heartbeat, for notifications.</summary>
    public HubMessageAlerts MessageAlerts { get; }

    /// <summary>Push notifications (Firebase): settings and this device's token.</summary>
    public HubPush Push { get; }

    /// <summary>Installed games' names and icons, for the hub's title pages.</summary>
    public HubTitleDetails TitleDetails { get; }

    /// <summary>Game invites sent as hub messages.</summary>
    public HubGameInvites Invites { get; }

    /// <summary>What the platform declares.</summary>
    public OnlineServices Services { get; }
}

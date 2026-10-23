using System.Reflection;
using Microsoft.Xna.Framework.GamerServices;
using WPR.Common;
using WPR.Engine.Notifications;
using WPR.Engine.Online;
using WPR.Wp8Native;
using WPR.XboxLive;
using WPR.Xna.Achievements;
using WPR.Xna.Rhi;

namespace Wp8ContractTests.Suites;

/// <summary>
/// Microsoft.Xbox achievements and leaderboards, from the guest's vtable slot to what WPR does with
/// them: the WinRT surface (XboxRuntime.cs, XboxData.cs, XboxAttributes.cs) against a recording
/// <see cref="IXboxLiveHost"/>, the board list read from the title's SPA (XboxTitleConfig), and the
/// real <see cref="Wp8NativeXboxHost"/> against an in-memory achievement store, a recording toast,
/// progress sync and leaderboard service.
/// </summary>
public sealed class XboxLiveSuite : IContractSuite
{
    public string Name => "xbox live";

    private const int Slot6 = 6;

    /// <summary>A word in the emulated stack region: what the factories test to tell an
    /// out-parameter (ActivateInstance) from an argument.</summary>
    private const long StackOut = 0x50000000L + 0x100000L;

    private Harness _h = null!;

    private ArmEmulator E => _h.E;

    public void Run(Harness h)
    {
        _h = h;
        GuestSurface(h);
        TitleConfig(h);
        Host(h);
    }

    // -------------------------------------------------------------------------------------------
    // The WinRT surface against a recording host
    // -------------------------------------------------------------------------------------------

    private sealed class RecordingHost : IXboxLiveHost
    {
        public List<uint> Unlocked { get; } = new();
        public List<(uint Id, int Verb, long Value)> Posts { get; } = new();
        public List<(uint Id, IReadOnlyList<XboxLeaderboardAttribute> Columns)> MultiPosts { get; } = new();
        public List<(uint Id, uint Skip, uint Max, bool Title)> Reads { get; } = new();

        public string Gamertag => "Tester";

        public IReadOnlyList<XboxAchievement> GetAchievements() =>
        [
            new XboxAchievement(3, "Three", "third", 10, false, true, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)),
            new XboxAchievement(9, "Nine", "ninth", 25, true, false, null),
        ];

        public void UnlockAchievement(uint achievementId) => Unlocked.Add(achievementId);

        public void PostResult(uint leaderboardId, int aggregation, long value) => Posts.Add((leaderboardId, aggregation, value));

        public void PostResults(uint leaderboardId, IReadOnlyList<XboxLeaderboardAttribute> attributes) => MultiPosts.Add((leaderboardId, attributes));

        public List<uint> Known { get; } = new();

        public IReadOnlyList<uint> KnownLeaderboards() => Known;

        public XboxLeaderboardPage? ReadLeaderboard(uint leaderboardId, uint skip, uint max, bool titleView)
        {
            Reads.Add((leaderboardId, skip, max, titleView));
            return new XboxLeaderboardPage(
                [new XboxLeaderboardRow(1, "rival", 9000, false), new XboxLeaderboardRow(2, "Tester", 4200, true)], 7);
        }
    }

    private void GuestSurface(Harness h)
    {
        var host = new RecordingHost();
        E.WinRt.XboxHost = host;

        // ---- achievements, through IUser ----
        long userFactory = E.WinRt.GetActivationFactory("Microsoft.Xbox.User")!.Value;
        long user = Activate(userFactory);
        h.Check("Microsoft.Xbox.User activates", user != 0);

        long op = h.Buffer(8);
        h.Check("UnlockAchievementAsync(17) answers S_OK with an operation",
            h.CallMethod(user, Slot6 + 6, 17, op) == 0 && h.U32(op) != 0);
        h.Check("...and reaches the host with the id", host.Unlocked.SequenceEqual([17u]), string.Join(",", host.Unlocked));

        long collection = Results(Out5(user, Slot6 + 5, 0, 100, 0, 0));
        h.Check("GetAchievementsAsync returns the host's list: TotalRecords 2", collection != 0 && Scalar(collection, Slot6 + 1) == 2);
        long items = Results(collection, Slot6 + 0, direct: true);
        h.Check("...two items", Scalar(items, Slot6 + 1) == 2);
        long first = At(items, 0);
        h.Check("...the first is id 3, earned, 10G, named",
            Scalar(first, Slot6 + 0) == 3 && Bool(first, Slot6 + 14) && Scalar(first, Slot6 + 6) == 10 && Text(first, Slot6 + 8) == "Three");
        long second = At(items, 1);
        h.Check("...the second is id 9, secret, not earned, with a locked description",
            Scalar(second, Slot6 + 0) == 9 && Bool(second, Slot6 + 3) && !Bool(second, Slot6 + 14) && Text(second, Slot6 + 7) == "Secret achievement");
        long earnedOnly = Results(Out5(user, Slot6 + 5, 0, 100, 1, 0));
        h.Check("unlockedOnly=true leaves the earned one", Scalar(earnedOnly, Slot6 + 1) == 1);

        // ---- leaderboards ----
        long serviceFactory = E.WinRt.GetActivationFactory("Microsoft.Xbox.Leaderboards.LeaderboardService")!.Value;
        long service = Activate(serviceFactory);
        h.Check("LeaderboardService activates", service != 0);

        long postOp = h.Buffer(8);
        long big = 0x1_2345_6789L;
        h.Check("PostResultAsync(5, Max, 0x123456789): the Int64 is read off the stack",
            h.CallMethod(service, Slot6 + 3, 5, 3, 0, big & 0xFFFFFFFF, big >> 32, postOp) == 0 && h.U32(postOp) != 0 &&
            host.Posts.LastOrDefault() == (5u, 3, big), host.Posts.Count == 0 ? "none" : host.Posts.Last().ToString());

        long boardsOp = h.Buffer(8);
        h.CallMethod(service, Slot6 + 0, boardsOp);
        long boards = Results(h.U32(boardsOp));
        long boardItems = Results(boards, Slot6 + 0, direct: true);
        int expected = E.WinRt.TitleConfig.Boards.Count;
        h.Check("GetLeaderboardsAsync lists the boards the title's SPA defines",
            expected > 0 && Scalar(boardItems, Slot6 + 1) == expected, $"spa={E.WinRt.TitleConfig.Source} boards={expected}");
        if (expected > 0)
        {
            long meta = At(boardItems, 0);
            XboxTitleConfig.Board b = E.WinRt.TitleConfig.Boards[0];
            h.Check("...each with its id and en-US display name",
                Scalar(meta, Slot6 + 0) == b.Id && Text(meta, Slot6 + 1) == b.DisplayName && b.DisplayName.Length > 0,
                $"{Scalar(meta, Slot6 + 0)} '{Text(meta, Slot6 + 1)}'");
        }

        // A board the title posted to that its configuration does not name is listed after the
        // defined ones; one it does name is not listed twice.
        uint firstDefined = expected > 0 ? E.WinRt.TitleConfig.Boards[0].Id : 1;
        host.Known.AddRange([firstDefined, 9999u]);
        long boardsOp2 = h.Buffer(8);
        h.CallMethod(service, Slot6 + 0, boardsOp2);
        long boardItems2 = Results(Results(h.U32(boardsOp2)), Slot6 + 0, direct: true);
        long listed = Scalar(boardItems2, Slot6 + 1);
        h.Check("...plus any other board the title has posted to, once",
            listed == expected + 1 && Scalar(At(boardItems2, (uint)(listed - 1)), Slot6 + 0) == 9999,
            $"listed={listed} expected={expected + 1}");
        host.Known.Clear();

        long readOut = h.Buffer(8);
        h.CallMethod(service, Slot6 + 1, 0, 10, 1, 1, 0, 0, readOut);
        long board = Results(h.U32(readOut));
        h.Check("GetLeaderboardAsync(skip 0, max 10, id 1, title view) reaches the host",
            host.Reads.LastOrDefault() == (1u, 0u, 10u, true));
        long rows = Results(board, Slot6 + 2, direct: true);
        h.Check("...the board carries its rows and total", Scalar(rows, Slot6 + 1) == 2 && Scalar(board, Slot6 + 3) == 7);
        long me = Results(board, Slot6 + 1, direct: true);
        h.Check("...the user row is the player's, rating as text",
            Text(me, Slot6 + 0) == "Tester" && Scalar(me, Slot6 + 1) == 2 && Text(me, Slot6 + 2) == "4200");

        // ---- LeaderboardAttribute + PostResultsAsync ----
        long attributeFactory = E.WinRt.GetActivationFactory("Microsoft.Xbox.Leaderboards.LeaderboardAttribute")!.Value;
        long a1 = Ret5(attributeFactory, Slot6, 0, 3, 2, HS("4200"));
        long a2 = Ret5(attributeFactory, Slot6, 7, 1, 1, HS("12"));
        h.Check("CreateLeaderboardAttribute builds an attribute with what it was given",
            a1 != 0 && Scalar(a1, Slot6 + 2) == 0 && Scalar(a1, Slot6 + 0) == 3 && Scalar(a1, Slot6 + 4) == 2 && Text(a1, Slot6 + 6) == "4200");
        long a3 = Activate(attributeFactory);
        h.CallMethod(a3, Slot6 + 3, 9);
        h.CallMethod(a3, Slot6 + 7, HS("x"));
        h.Check("ActivateInstance and the setters", a3 != 0 && Scalar(a3, Slot6 + 2) == 9 && Text(a3, Slot6 + 6) == "x");

        MethodInfo vector = typeof(WinRtRuntime).GetMethod("ObjectVectorView", BindingFlags.NonPublic | BindingFlags.Instance)!;
        long list = (long)vector.Invoke(E.WinRt, ["IVectorView<LeaderboardAttribute>", new long[] { a1, a2 }])!;
        long postResults = E.ReadUInt32(E.ReadUInt32(service) + ((Slot6 + 4) * 4));
        long resultsOut = h.Buffer(8);
        var guest = new ModuleLoaderSuite.Guest(E);
        long hr = guest.Invoke(postResults, service, 11, list, resultsOut);
        h.Check("PostResultsAsync reads the vector through its own methods and completes",
            hr == 0 && guest.Returned && h.U32(resultsOut) != 0, $"hr=0x{hr:X} returned={guest.Returned}");
        h.Check("...the host gets board 11 with both columns",
            host.MultiPosts.Count == 1 && host.MultiPosts[0].Id == 11 &&
            host.MultiPosts[0].Columns.Select(c => (c.AttributeId, c.Verb, c.Value)).SequenceEqual([(0u, 3, "4200"), (7u, 1, "12")]),
            string.Join("; ", host.MultiPosts.SelectMany(p => p.Columns)));
        h.Check("the rating is the lowest-numbered numeric column",
            XboxLeaderboardAttribute.PickRating(host.MultiPosts[0].Columns)?.AttributeId == 0);
        h.Check("every call is in the run summary's Xbox list",
            E.WinRt.XboxCalls.Any(c => c.StartsWith("UnlockAchievementAsync(17)", StringComparison.Ordinal)) &&
            E.WinRt.XboxCalls.Any(c => c.StartsWith("PostResultsAsync(id=11", StringComparison.Ordinal)) &&
            E.WinRt.XboxCalls.Any(c => c.StartsWith("GetLeaderboardsAsync ->", StringComparison.Ordinal)));
    }

    // -------------------------------------------------------------------------------------------
    // The SPA reader
    // -------------------------------------------------------------------------------------------

    private void TitleConfig(Harness h)
    {
        const string Xml = """
            <?xml version="1.0" encoding="utf-16"?>
            <GameConfigProject xmlns="http://www.xboxlive.com/xlast">
              <LocalizedStrings>
                <LocalizedString id="84"><Translation locale="en-US">Total Score</Translation></LocalizedString>
                <LocalizedString id="100"><Translation locale="en-US">Score</Translation></LocalizedString>
                <LocalizedString id="67"><Translation locale="en-US">Best Time</Translation></LocalizedString>
              </LocalizedStrings>
              <StatsViews nextViewId="3">
                <StatsView stringId="84" friendlyName="TOTALSCORE" id="1" viewType="Leaderboard">
                  <Columns>
                    <Field stringId="32518" attributeId="65535" friendlyName="RANK_COLUMN"><Property id="0x10008001"><Aggregation type="Last" /></Property></Field>
                    <Field stringId="32516" attributeId="65533" friendlyName="GAMERNAME_COLUMN"><Property id="0x40008002"><Aggregation type="Last" /></Property></Field>
                    <Field stringId="100" attributeId="65534" friendlyName="SCORE_COLUMN"><Property id="0x20000001"><Aggregation type="Max" /></Property></Field>
                  </Columns>
                </StatsView>
                <StatsView stringId="99" friendlyName="LAP" id="4" viewType="Leaderboard">
                  <Columns>
                    <Field stringId="67" attributeId="65534" friendlyName="TIME_COLUMN"><Property id="0x20000002"><Aggregation type="Min" /></Property></Field>
                  </Columns>
                </StatsView>
              </StatsViews>
            </GameConfigProject>
            """;
        IReadOnlyList<XboxTitleConfig.Board> boards = XboxTitleConfig.ParseBoards(Xml);
        h.Check("a gameconfig's StatsViews become boards",
            boards.Count == 2 && boards[0] == new XboxTitleConfig.Board(1, "TOTALSCORE", "Total Score", "Score", "Max"),
            string.Join("; ", boards));
        h.Check("...a board with no localized name falls back to its friendly name, and keeps its aggregation",
            boards[1] == new XboxTitleConfig.Board(4, "LAP", "LAP", "Best Time", "Min"), boards.Count > 1 ? boards[1].ToString() : "");

        byte[] module = SpaModule(Xml);
        h.Check("FindGameConfig digs the document out of a module", XboxTitleConfig.FindGameConfig(module)?.Contains("TOTALSCORE") == true);
        h.Check("...and finds nothing in one without an XDBF table", XboxTitleConfig.FindGameConfig(Gzip(Xml)) is null);
        h.Check("a missing package folder is an empty configuration", XboxTitleConfig.Load(Path.Combine(h.Sandbox, "nope")).Boards.Count == 0);

        ServiceBoards(h, Xml);
    }

    /// <summary>A module as an SPA carries its gameconfig: XDBF somewhere, then a gzipped UTF-16 document with a BOM.</summary>
    private static byte[] SpaModule(string xml) => [.. "MZ padding XDBF"u8.ToArray(), .. new byte[37], .. Gzip(xml), .. new byte[64]];

    private static byte[] Gzip(string xml)
    {
        using var gz = new MemoryStream();
        using (var z = new System.IO.Compression.GZipStream(gz, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write([.. System.Text.Encoding.Unicode.GetPreamble(), .. System.Text.Encoding.Unicode.GetBytes(xml)]);
        }

        return gz.ToArray();
    }

    /// <summary>
    /// Titles whose boards lived on the service: their SPA has no StatsViews, and the boards come
    /// from XboxTitleConfig.ServiceBoards by the gameconfig's title id.
    /// </summary>
    private static void ServiceBoards(Harness h, string withViews)
    {
        // As an SPA carries it: GameConfigProject inside XboxLiveSubmissionProject, no boards.
        static string Spa(string titleId) => $"""
            <?xml version="1.0" encoding="utf-16"?>
            <XboxLiveSubmissionProject Version="2.0.61025.01" xmlns="http://www.xboxlive.com/xlast">
              <GameConfigProject name="t" titleId="{titleId}">
                <StatsViewMasters />
                <StatsViews nextViewId="1" />
              </GameConfigProject>
            </XboxLiveSubmissionProject>
            """;

        h.Check("the title id is read from a GameConfigProject nested in the submission project",
            XboxTitleConfig.ParseTitleId(Spa("0x314C1392")) == 0x314C1392u);
        h.Check("...and from one that is the root, in decimal too",
            XboxTitleConfig.ParseTitleId("""<GameConfigProject titleId="826020754" />""") == 826020754u &&
            XboxTitleConfig.ParseTitleId("""<GameConfigProject />""") is null);

        XboxTitleConfig seasons = XboxTitleConfig.FromGameConfig(Spa("0x314C1392"), "xbl.dll");
        h.Check("Angry Birds Seasons (no StatsViews) gets its 21 boards from the table",
            seasons.Boards.Count == 21 && seasons.TitleId == 0x314C1392u && seasons.Source?.Contains("WPR's table") == true,
            $"{seasons.Boards.Count} from {seasons.Source}");
        h.Check("...TOTALSCORE is 0 and episode 19 is 304, the ids the game posts a level win to",
            seasons.Find(0)?.FriendlyName == "TOTALSCORE" && seasons.Find(304)?.FriendlyName == "TOTALSCOREEPISODE19" &&
            seasons.Boards.All(b => b.Aggregation == "Max"));

        XboxTitleConfig starWars = XboxTitleConfig.FromGameConfig(Spa("0x314C138A"), "xbl.dll");
        h.Check("Angry Birds Star Wars gets 9 boards, TOTALSCORE at 32",
            starWars.Boards.Count == 9 && starWars.Find(32)?.FriendlyName == "TOTALSCORE", $"{starWars.Boards.Count}");

        XboxTitleConfig starWars2 = XboxTitleConfig.FromGameConfig(Spa("0x314C1396"), "xbl.dll");
        // Its menu labels a chapter button with string.sub(name, 1, -19) on the Bird Side (the
        // even positions of leaderboardOrder) and -18 on the Pork Side.
        // Lua: string.sub(s, 1, -n) keeps the first len - n + 1 characters.
        static string Sub(string text, int n) => text.Length >= n - 1 ? text[..(text.Length - n + 1)] : string.Empty;
        h.Check("Angry Birds Star Wars II gets 17 boards, TOTALSCORE at 0 named as it is drawn",
            starWars2.Boards.Count == 17 && starWars2.Find(0)?.DisplayName == "Total Score");
        h.Check("...and its chapter names survive the game cutting the side off them",
            Sub(starWars2.Find(16)!.DisplayName, 19) == "Naboo Invasion" && Sub(starWars2.Find(32)!.DisplayName, 18) == "Naboo Invasion" &&
            Sub(starWars2.Find(240)!.DisplayName, 19) == "Revenge of the Pork" && Sub(starWars2.Find(256)!.DisplayName, 18) == "Revenge of the Pork",
            $"'{Sub(starWars2.Find(16)!.DisplayName, 19)}' '{Sub(starWars2.Find(32)!.DisplayName, 18)}'");

        h.Check("every table lists each id once",
            XboxTitleConfig.ServiceBoards.Values.All(list => list.Select(b => b.Id).Distinct().Count() == list.Count));
        h.Check("a title the table does not know keeps an empty list",
            XboxTitleConfig.FromGameConfig(Spa("0x12345678"), "xbl.dll").Boards.Count == 0);
        h.Check("boards the SPA defines win over the table",
            XboxTitleConfig.FromGameConfig(withViews.Replace("<GameConfigProject ", """<GameConfigProject titleId="0x314C1392" """), "x").Boards.Count == 2);

        // What WPR Hub is told each board is called.
        h.Check("a display name goes to the hub as it is",
            starWars2.HubName(0) == "Total Score" && starWars2.HubName(16) == "Naboo Invasion - Light Side Best");
        h.Check("...an internal identifier is made readable",
            seasons.HubName(0) == "Total Score" && seasons.HubName(304) == "Total Score, Episode 19" &&
            starWars.HubName(64) == "Hoth 1" && starWars.HubName(48) == "IAP 1" && starWars.HubName(128) == "Deathstar 2",
            $"'{seasons.HubName(304)}' '{starWars.HubName(64)}' '{starWars.HubName(48)}' '{starWars.HubName(128)}'");
        h.Check("...and an unknown board has none", seasons.HubName(9999) is null);
    }

    // -------------------------------------------------------------------------------------------
    // Wp8NativeXboxHost: the store, the toast, the hub
    // -------------------------------------------------------------------------------------------

    private sealed class MemoryStore : IAchievementStore
    {
        public List<Achievement> Rows { get; } = new();
        public int Saves;

        public Task<IReadOnlyList<Achievement>> GetForProductAsync(string productId) =>
            Task.FromResult<IReadOnlyList<Achievement>>(Rows.Where(r => r.OwnProductId == productId).ToList());

        public Task<IReadOnlyList<Achievement>> GetByKeyAsync(string productId, string achievementKey) =>
            Task.FromResult<IReadOnlyList<Achievement>>(Rows.Where(r => r.OwnProductId == productId && r.Key == achievementKey).ToList());

        public Task<int> CountForProductAsync(string productId) => Task.FromResult(Rows.Count(r => r.OwnProductId == productId));

        public Task<AchievementTotals> GetEarnedTotalsAsync() =>
            Task.FromResult(new AchievementTotals(Rows.Count(r => r.IsEarned), Rows.Where(r => r.IsEarned).Sum(r => r.GamerScore)));

        public Task<Achievement> AddAsync(Achievement achievement)
        {
            Rows.Add(achievement);
            return Task.FromResult(achievement);
        }

        public Task SaveChangesAsync()
        {
            Interlocked.Increment(ref Saves);
            return Task.CompletedTask;
        }
    }

    private sealed class Toasts : INotificationManager
    {
        public List<string> Shown { get; } = new();
        public string? LaunchActionId => null;
        public event EventHandler<NotificationActivatedEventArgs>? NotificationActivated { add { } remove { } }
        public event EventHandler<NotificationDismissedEventArgs>? NotificationDismissed { add { } remove { } }
        public Task Initialize() => Task.CompletedTask;

        public Task ShowNotification(Notification notification, DateTimeOffset? expirationTime = null)
        {
            lock (Shown)
            {
                Shown.Add(notification.Body ?? string.Empty);
            }

            return Task.CompletedTask;
        }

        public Task ScheduleNotification(Notification notification, DateTimeOffset deliveryTime, DateTimeOffset? expirationTime = null) =>
            ShowNotification(notification, expirationTime);

        public void Dispose()
        {
        }
    }

    private sealed class Progress : IProgressSync
    {
        public int Unlocks;
        public void Unlocked() => Interlocked.Increment(ref Unlocks);
        public Task FlushAsync() => Task.CompletedTask;
    }

    private sealed class Boards : ILeaderboardService
    {
        public List<(string Board, long Score, IReadOnlyDictionary<string, string>? Columns)> Submitted { get; } = new();
        public List<string> Ensured { get; } = new();
        public List<(string Board, LeaderboardView View)> ReadsDone { get; } = new();
        public bool CanRead => true;

        public Dictionary<string, string?> Names { get; } = new();

        public void Submit(string titleId, string? titleName, string boardKey, long score, IReadOnlyDictionary<string, string>? columns = null,
            string? boardName = null)
        {
            Submitted.Add((boardKey, score, columns));
            Names[boardKey] = boardName;
        }

        public void EnsureBoard(string titleId, string? titleName, string boardKey, string? boardName = null)
        {
            Ensured.Add(boardKey);
            Names[boardKey] = boardName;
        }

        public Task<LeaderboardPage?> ReadAsync(string titleId, string boardKey, LeaderboardView view, int offset, int limit, CancellationToken ct = default)
        {
            ReadsDone.Add((boardKey, view));
            return Task.FromResult<LeaderboardPage?>(new LeaderboardPage([new LeaderboardRow(1, "rival", 77, false, null)], 1));
        }

        public Task FlushAsync() => Task.CompletedTask;
    }

    private void Host(Harness h)
    {
        const string Product = "00000000-0000-0000-0000-0000000000a8";
        IAchievementStore? previousStore = XnaBackend.Achievements;
        INotificationManager? previousToasts = NotificationBackend.Manager;
        ILeaderboardService? previousBoards = OnlineBackend.Leaderboards;
        IProgressSync? previousProgress = OnlineBackend.Progress;
        Configuration? previousConfig = Configuration.Current;
        string? previousProduct = WprHostEnvironment.CurrentProductId;
        string root = Path.Combine(h.Sandbox, "xbox-host-data");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "config.json"), "{}");

        var store = new MemoryStore();
        store.Rows.Add(new Achievement
        {
            OwnProductId = Product, Key = "4", Name = "Catalogued", Description = "from the catalogue", HowToEarn = string.Empty,
            _IconPath = string.Empty, GamerScore = 15, DisplayBeforeEarned = true,
        });
        var toasts = new Toasts();
        var progress = new Progress();
        var boards = new Boards();
        try
        {
            Configuration.Current = new Configuration(root);
            XnaBackend.SetAchievements(store);
            NotificationBackend.SetManager(toasts);
            OnlineBackend.Set(new OnlineServices { Leaderboards = boards, Progress = progress });
            WprHostEnvironment.CurrentProductId = Product;

            // The install folder carries an SPA naming board 1.
            string package = Path.Combine(h.Sandbox, "xbox-host-package");
            Directory.CreateDirectory(package);
            File.WriteAllBytes(Path.Combine(package, "xbl.dll"), SpaModule("""
                <?xml version="1.0" encoding="utf-16"?>
                <GameConfigProject xmlns="http://www.xboxlive.com/xlast">
                  <LocalizedStrings><LocalizedString id="84"><Translation locale="en-US">Total Score</Translation></LocalizedString></LocalizedStrings>
                  <StatsViews><StatsView stringId="84" friendlyName="TOTALSCORE" id="1" viewType="Leaderboard" /></StatsViews>
                </GameConfigProject>
                """));

            var host = new Wp8NativeXboxHost(Product, "Test Title", package);
            h.Check("GetAchievements reads the catalogue", host.GetAchievements() is [{ Id: 4, Name: "Catalogued", Gamerscore: 15, IsEarned: false }]);

            host.UnlockAchievement(4);
            h.Check("an unlock flips the catalogue row, toasts it by name and asks for an upload",
                Eventually(() => store.Rows[0].IsEarned && progress.Unlocks == 1 && toasts.Shown.Contains("15G - Catalogued")),
                $"earned={store.Rows[0].IsEarned} uploads={progress.Unlocks} toasts=[{string.Join(", ", toasts.Shown)}]");
            h.Check("...stays awaiting upload until the hub answers", !store.Rows[0].EarnedOnline);
            h.Check("...and the game's next read says earned", host.GetAchievements().Single(a => a.Id == 4).IsEarned);

            host.UnlockAchievement(4);
            Thread.Sleep(300);
            h.Check("unlocking it again is quiet: no second toast", toasts.Shown.Count == 1, string.Join(", ", toasts.Shown));

            host.UnlockAchievement(31);
            h.Check("an id with no catalogue row gets one, is awarded and toasted",
                Eventually(() => store.Rows.Any(r => r.Key == "31" && r.IsEarned) && toasts.Shown.Count == 2),
                $"rows={string.Join(",", store.Rows.Select(r => r.Key + (r.IsEarned ? "*" : "")))} toasts={toasts.Shown.Count}");

            host.PostResult(1, 3, 500);
            h.Check("Max posts go to BestScore<id>:0 as they come", boards.Submitted.LastOrDefault() is ("BestScore1:0", 500, null));
            h.Check("...named for the hub as the title's SPA names the board", boards.Names.GetValueOrDefault("BestScore1:0") == "Total Score",
                boards.Names.GetValueOrDefault("BestScore1:0") ?? "null");
            host.PostResult(2, 0, 30);
            host.PostResult(2, 0, 12);
            h.Check("Add posts are summed and the total sent", boards.Submitted.TakeLast(2).Select(s => s.Score).SequenceEqual([30L, 42L]));
            h.Check("...a board the SPA does not describe goes without a name", boards.Names.TryGetValue("BestScore2:0", out string? none) && none is null);
            int before = boards.Submitted.Count;
            host.PostResult(3, 3, 0);
            host.PostResult(5, 2, 0);
            h.Check("nothing scored (0 to a Max or Min board) is not sent", boards.Submitted.Count == before);
            host.PostResult(5, 2, 61);
            h.Check("Min posts go to a lower-is-better BestTime board", boards.Submitted.LastOrDefault().Board == "BestTime5:0");

            host.PostResults(6, [new XboxLeaderboardAttribute(2, 3, 1, "7"), new XboxLeaderboardAttribute(0, 3, 2, "900")]);
            h.Check("PostResults sends the rating and keeps the other columns",
                boards.Submitted.LastOrDefault() is ("BestScore6:0", 900, { } cols) && cols["Attribute2"] == "7");

            // A new run: the aggregation and the running total come back from disk.
            var again = new Wp8NativeXboxHost(Product, "Test Title");
            again.ReadLeaderboard(5, 0, 10, true);
            h.Check("a Min board read in a later run, before any post, still names BestTime",
                boards.ReadsDone.LastOrDefault() == ("BestTime5:0", LeaderboardView.Top), boards.ReadsDone.LastOrDefault().ToString());
            host.ReadLeaderboard(1, 0, 10, true);
            h.Check("a read registers the board with its name", boards.Ensured.LastOrDefault() == "BestScore1:0" && boards.Names["BestScore1:0"] == "Total Score");
            again.PostResult(1, 3, 600);
            h.Check("a host given no package sends no name", boards.Names["BestScore1:0"] is null);
            again.PostResult(2, 0, 8);
            h.Check("an Add board's total carries over runs", boards.Submitted.LastOrDefault() is ("BestScore2:0", 50, null));
            h.Check("the boards posted to are the known boards", again.KnownLeaderboards().SequenceEqual([1u, 2u, 3u, 5u, 6u]),
                string.Join(",", again.KnownLeaderboards()));
            XboxLeaderboardPage? page = again.ReadLeaderboard(1, 0, 10, false);
            h.Check("a friends read asks the hub's friends view and maps the rows",
                boards.ReadsDone.LastOrDefault() == ("BestScore1:0", LeaderboardView.Friends) && page is { TotalRecords: 1, Rows: [{ Gamertag: "rival", Rating: 77 }] });
        }
        finally
        {
            if (previousStore is not null)
            {
                XnaBackend.SetAchievements(previousStore);
            }

            NotificationBackend.SetManager(previousToasts);
            OnlineBackend.Set(new OnlineServices { Leaderboards = previousBoards, Progress = previousProgress });
            Configuration.Current = previousConfig!;
            WprHostEnvironment.CurrentProductId = previousProduct!;
        }
    }

    private static bool Eventually(Func<bool> condition)
    {
        for (int i = 0; i < 100; i++)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return condition();
    }

    // -------------------------------------------------------------------------------------------
    // Calling WinRT objects the way C++/CX does
    // -------------------------------------------------------------------------------------------

    /// <summary>A factory's slot 6 with an out-parameter on the stack: ActivateInstance.</summary>
    private long Activate(long factory)
    {
        E.WriteUInt32(StackOut, 0);
        _h.CallMethod(factory, Slot6, StackOut);
        return E.ReadUInt32(StackOut);
    }

    /// <summary>A method of four arguments (self excluded) whose out-parameter is the fifth, on the stack.</summary>
    private long Out5(long self, int slot, long a, long b, long c, long d)
    {
        long result = _h.Buffer(8);
        _h.CallMethod(self, slot, a, b, c, d, result);
        return _h.U32(result);
    }

    private long Ret5(long self, int slot, long a, long b, long c, long d) => Out5(self, slot, a, b, c, d);

    /// <summary>GetResults (slot 8) of an async operation, or a getter's object when <paramref name="direct"/>.</summary>
    private long Results(long operation, int slot = 8, bool direct = false)
    {
        if (operation == 0)
        {
            return 0;
        }

        long result = _h.Buffer(8);
        _h.CallMethod(operation, direct ? slot : 8, result);
        return _h.U32(result);
    }

    private long At(long view, uint index)
    {
        long result = _h.Buffer(8);
        _h.CallMethod(view, Slot6, index, result);
        return _h.U32(result);
    }

    private long Scalar(long self, int slot)
    {
        if (self == 0)
        {
            return -1;
        }

        long result = _h.Buffer(8);
        _h.CallMethod(self, slot, result);
        return _h.U32(result);
    }

    private bool Bool(long self, int slot)
    {
        long result = _h.Buffer(8);
        _h.CallMethod(self, slot, result);
        return E.ReadMemory(result, 1)[0] != 0;
    }

    private string Text(long self, int slot)
    {
        if (self == 0)
        {
            return string.Empty;
        }

        long result = _h.Buffer(8);
        _h.CallMethod(self, slot, result);
        long hstring = _h.U32(result);
        if (hstring == 0)
        {
            return string.Empty;
        }

        long length = _h.Buffer(8);
        long chars = _h.Call("WindowsGetStringRawBuffer", hstring, length);
        return System.Text.Encoding.Unicode.GetString(E.ReadMemory(chars, (int)_h.U32(length) * 2));
    }

    private long HS(string text)
    {
        long result = _h.Buffer(8);
        _h.Call("WindowsCreateString", _h.Wide(text), text.Length, result);
        return _h.U32(result);
    }
}

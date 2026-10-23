using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Windows.Foundation;
using WPR.WinRT;
using WPR.XboxLive;

namespace Microsoft.Xbox.Leaderboards
{
    /// <summary>
    /// Shim for <c>Microsoft.Xbox.Leaderboards.LeaderboardService</c>: boards on WPR Hub, through
    /// the same host (and the same board-key and aggregation rules) as a WP8 native title.
    /// </summary>
    /// <remarks>
    /// GetLeaderboardsAsync lists the boards this title has posted to. Unlike the native runtime it
    /// does not read the title's SPA, so a title that lists boards before ever posting sees none.
    /// </remarks>
    public sealed class LeaderboardService
    {
        public LeaderboardService()
        {
        }

        public LeaderboardService(uint titleId)
        {
        }

        public IAsyncOperation<LeaderboardMetadataCollection> GetLeaderboardsAsync()
        {
            if (XboxHub.Host is not { } host)
            {
                return WinRtAsync.FromResult(new LeaderboardMetadataCollection(Array.Empty<LeaderboardMetadata>()));
            }

            return XboxHub.Run("GetLeaderboardsAsync", () =>
            {
                IReadOnlyList<uint> ids = host.KnownLeaderboards();
                XboxHub.Log($"GetLeaderboardsAsync -> {ids.Count} board(s) posted to before: {string.Join(", ", ids)}");
                return new LeaderboardMetadataCollection(ids.Select(id => new LeaderboardMetadata(id)).ToArray());
            });
        }

        public IAsyncOperation<Leaderboard> GetLeaderboardAsync(uint skipItems, uint maxItems, uint leaderboardId, bool titleView, IReadOnlyList<uint>? attributes, Leaderboard? previousPage)
        {
            if (XboxHub.Host is not { } host)
            {
                return WinRtAsync.FromResult(new Leaderboard(leaderboardId, null));
            }

            return XboxHub.Run($"GetLeaderboardAsync({leaderboardId}, skip {skipItems}, max {maxItems}, titleView={titleView})", () =>
            {
                XboxLeaderboardPage? page = host.ReadLeaderboard(leaderboardId, skipItems, maxItems, titleView);
                XboxHub.Log($"GetLeaderboardAsync({leaderboardId}) -> {page?.Rows.Count ?? 0} row(s) of {page?.TotalRecords ?? 0}");
                return new Leaderboard(leaderboardId, page);
            });
        }

        public IAsyncOperation<Leaderboard> GetSystemLeaderboardAsync(uint skipItems, uint maxItems, string leaderboardName, Leaderboard? previousPage) =>
            WinRtAsync.FromResult(new Leaderboard(0, null));

        public IAsyncAction PostResultAsync(uint leaderboardId, LeaderboardAggregation verb, long scoreValue)
        {
            XboxHub.Log($"PostResultAsync({leaderboardId}, {verb}, {scoreValue})");
            if (XboxHub.Host is not { } host) return WinRtAsync.Completed();
            return XboxHub.Run($"PostResultAsync({leaderboardId})", () => host.PostResult(leaderboardId, (int)verb, scoreValue));
        }

        public IAsyncAction PostResultsAsync(uint leaderboardId, IReadOnlyList<LeaderboardAttribute> attributes)
        {
            List<XboxLeaderboardAttribute> columns = (attributes ?? Array.Empty<LeaderboardAttribute>())
                .Where(a => a != null)
                .Select(a => new XboxLeaderboardAttribute(a.AttributeId, (int)a.Verb, (int)a.Type, a.Value))
                .ToList();
            XboxHub.Log($"PostResultsAsync({leaderboardId}, {string.Join(", ", columns.Select(c => $"{c.AttributeId}={c.Value}"))})");
            if (XboxHub.Host is not { } host) return WinRtAsync.Completed();
            return XboxHub.Run($"PostResultsAsync({leaderboardId})", () => host.PostResults(leaderboardId, columns));
        }
    }

    /// <summary>Shim for <c>Microsoft.Xbox.Leaderboards.Leaderboard</c>: one page of a board.</summary>
    public sealed class Leaderboard
    {
        internal Leaderboard(uint leaderboardId, XboxLeaderboardPage? page)
        {
            IReadOnlyList<XboxLeaderboardRow> rows = page?.Rows ?? Array.Empty<XboxLeaderboardRow>();
            Metadata = new LeaderboardMetadata(leaderboardId);
            UserList = rows.Select(r => new LeaderboardRow(r)).ToArray();
            // Always a row, as the native runtime answers: titles read the player's rank without a null check.
            UserRow = UserList.FirstOrDefault(r => r.IsMe) ?? new LeaderboardRow(new XboxLeaderboardRow(0, XboxHub.Gamertag, 0, true));
            TotalRecords = page?.TotalRecords ?? 0;
        }

        public LeaderboardMetadata Metadata { get; }

        public LeaderboardRow UserRow { get; }

        public IReadOnlyList<LeaderboardRow> UserList { get; }

        public uint TotalRecords { get; }
    }

    /// <summary>Shim for <c>Microsoft.Xbox.Leaderboards.LeaderboardMetadata</c>.</summary>
    public sealed class LeaderboardMetadata
    {
        internal LeaderboardMetadata(uint leaderboardId) => LeaderboardId = leaderboardId;

        public uint LeaderboardId { get; }

        public string LeaderboardName => LeaderboardId.ToString(CultureInfo.InvariantCulture);

        public string RatingColumnName => "Score";
    }

    /// <summary>Shim for <c>Microsoft.Xbox.Leaderboards.LeaderboardMetadataCollection</c>.</summary>
    public sealed class LeaderboardMetadataCollection
    {
        internal LeaderboardMetadataCollection(IReadOnlyList<LeaderboardMetadata> items) => Items = items;

        public IReadOnlyList<LeaderboardMetadata> Items { get; }
    }

    public interface ILeaderboardRow
    {
        string Gamertag { get; }
        uint Rank { get; }
        string Rating { get; }
        ulong Xuid { get; }
        IReadOnlyList<LeaderboardAttribute> Attributes { get; }
    }

    /// <summary>
    /// Shim for <c>Microsoft.Xbox.Leaderboards.LeaderboardRow</c>. Attributes is empty, as the
    /// native runtime answers: WPR Hub keeps the rating, not the other columns, per row.
    /// </summary>
    public sealed class LeaderboardRow : ILeaderboardRow
    {
        internal LeaderboardRow(XboxLeaderboardRow row)
        {
            Gamertag = row.Gamertag;
            Rank = row.Rank;
            Rating = row.Rating.ToString(CultureInfo.InvariantCulture);
            IsMe = row.IsMe;
            Xuid = XboxHub.XuidOf(row.Gamertag, row.IsMe);
        }

        internal bool IsMe { get; }

        public string Gamertag { get; }

        public uint Rank { get; }

        public string Rating { get; }

        public ulong Xuid { get; }

        public IReadOnlyList<LeaderboardAttribute> Attributes => Array.Empty<LeaderboardAttribute>();
    }

    /// <summary>Shim for <c>Microsoft.Xbox.Leaderboards.LeaderboardRowCollection</c>.</summary>
    public sealed class LeaderboardRowCollection
    {
        internal LeaderboardRowCollection()
        {
        }

        public IReadOnlyList<LeaderboardRow> Items => Array.Empty<LeaderboardRow>();
    }

    /// <summary>Shim for <c>Microsoft.Xbox.Leaderboards.LeaderboardAttribute</c>: one column a title posts.</summary>
    public sealed class LeaderboardAttribute
    {
        public LeaderboardAttribute()
        {
        }

        public LeaderboardAttribute(uint AttributeId, LeaderboardAggregation verb, LeaderboardValueType type, string value)
        {
            this.AttributeId = AttributeId;
            Verb = verb;
            Type = type;
            Value = value ?? string.Empty;
        }

        public LeaderboardAggregation Verb { get; private set; }

        public void put_Verb(LeaderboardAggregation verb) => Verb = verb;

        public uint AttributeId { get; private set; }

        public void put_AttributeId(uint value) => AttributeId = value;

        public LeaderboardValueType Type { get; private set; }

        public void put_Type(LeaderboardValueType type) => Type = type;

        public string Value { get; private set; } = string.Empty;

        public void put_Value(string value) => Value = value ?? string.Empty;
    }
}

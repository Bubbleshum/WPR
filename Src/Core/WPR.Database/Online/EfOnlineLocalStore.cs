using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Data.Sqlite;

using WPR.Common;
using WPR.Engine.Online;

namespace WPR.Database.Online
{
    /// <summary>
    /// <see cref="IOnlineLocalStore"/> over the two shipped SQLite files: unlocks live in
    /// <c>achievements.db</c>'s <c>Achievements</c> table, and play sessions in a
    /// <c>PlaySessions</c> table this class adds to the same file.
    /// </summary>
    /// <remarks>
    /// <para><b>"Awaiting upload" is <c>IsEarned = 1 AND EarnedOnline = 0</c></b>, and uploaded is
    /// <c>EarnedOnline = 1</c>. That column is XNA's own ("earned while connected to the service"),
    /// already in every shipped database, so there is no schema change and games that read
    /// <c>Achievement.EarnedOnline</c> get the truthful answer. The award path used to set it true
    /// unconditionally, so a one-time reset (<see cref="EnsureSchema"/>) turns every unlock earned
    /// before this existed back into "awaiting upload": none of them was ever sent.</para>
    ///
    /// <para><b>Raw SQL on its own connections, not <c>AchievementContext</c>.</b> That context is a
    /// process-wide tracked singleton the game thread uses during an award, and a DbContext is not
    /// thread-safe; the uploads run on the thread pool. Writing only <c>EarnedOnline</c> is safe
    /// against the tracked copy, because EF writes only the properties it saw change.</para>
    ///
    /// <para><b>No migrations</b>, matching the rest of the repo (none ever run). The table is
    /// <c>CREATE TABLE IF NOT EXISTS</c>, which makes the first use in either Android process
    /// create it, and every later one a no-op.</para>
    /// </remarks>
    public sealed class EfOnlineLocalStore : IOnlineLocalStore
    {
        private const string ResetMarker = "achievements-earned-online-reset-v1";
        private const string SqliteDate = "yyyy-MM-dd HH:mm:ss.FFFFFFF";

        private readonly SemaphoreSlim _schemaGate = new SemaphoreSlim(1, 1);
        private bool _schemaReady;

        private static string AchievementsDb => Configuration.Current!.DataPath("Database/achievements.db");
        private static string ApplicationsDb => Configuration.Current!.DataPath("Database/applications.db");

        public async Task<IReadOnlyList<PendingUnlock>> GetPendingUnlocksAsync(int max)
        {
            await EnsureSchema().ConfigureAwait(false);
            Dictionary<string, string> names = GameNames();

            List<PendingUnlock> result = new List<PendingUnlock>();
            using SqliteConnection db = Open(AchievementsDb);
            using SqliteCommand cmd = db.CreateCommand();
            cmd.CommandText = "SELECT Id, OwnProductId, Key, EarnedDateTime FROM Achievements " +
                              "WHERE IsEarned = 1 AND EarnedOnline = 0 AND Key <> '' AND OwnProductId <> '' " +
                              "ORDER BY EarnedDateTime LIMIT $max";
            cmd.Parameters.AddWithValue("$max", max);
            using SqliteDataReader r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await r.ReadAsync().ConfigureAwait(false))
            {
                string product = r.GetString(1);
                names.TryGetValue(Normalize(product), out string? name);
                result.Add(new PendingUnlock(r.GetInt64(0), product, r.GetString(2), ParseLocal(r.GetString(3)), name));
            }
            return result;
        }

        public async Task MarkUnlocksUploadedAsync(IReadOnlyList<PendingUnlock> unlocks)
        {
            if (unlocks.Count == 0) return;
            using SqliteConnection db = Open(AchievementsDb);
            using SqliteTransaction tx = db.BeginTransaction();
            using SqliteCommand cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE Achievements SET EarnedOnline = 1 WHERE Id = $id";
            SqliteParameter id = cmd.Parameters.Add("$id", SqliteType.Integer);
            foreach (PendingUnlock u in unlocks)
            {
                id.Value = u.LocalId;
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            tx.Commit();
        }

        public async Task SavePlaySessionAsync(PlaySessionRecord s)
        {
            await EnsureSchema().ConfigureAwait(false);
            using SqliteConnection db = Open(AchievementsDb);
            using SqliteCommand cmd = db.CreateCommand();
            // Seconds only grow: a late checkpoint racing the final save must not shrink it.
            cmd.CommandText =
                "INSERT INTO PlaySessions (SessionId, TitleId, TitleName, StartedAt, LastSeenAt, EndedAt, Seconds, UploadedAt) " +
                "VALUES ($id, $title, $name, $start, $seen, $end, $secs, NULL) " +
                "ON CONFLICT(SessionId) DO UPDATE SET LastSeenAt = excluded.LastSeenAt, " +
                "EndedAt = COALESCE(excluded.EndedAt, PlaySessions.EndedAt), " +
                "Seconds = MAX(PlaySessions.Seconds, excluded.Seconds), UploadedAt = NULL";
            cmd.Parameters.AddWithValue("$id", s.SessionId.ToString());
            cmd.Parameters.AddWithValue("$title", s.TitleId);
            cmd.Parameters.AddWithValue("$name", (object?)s.TitleName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$start", s.StartedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$seen", s.LastSeenAt.ToString("O"));
            cmd.Parameters.AddWithValue("$end", s.EndedAt is { } e ? e.ToString("O") : DBNull.Value);
            cmd.Parameters.AddWithValue("$secs", s.Seconds);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<PlaySessionRecord>> GetPendingPlaySessionsAsync(int max)
        {
            await EnsureSchema().ConfigureAwait(false);
            List<PlaySessionRecord> result = new List<PlaySessionRecord>();
            using SqliteConnection db = Open(AchievementsDb);
            using SqliteCommand cmd = db.CreateCommand();
            cmd.CommandText = "SELECT SessionId, TitleId, TitleName, StartedAt, LastSeenAt, EndedAt, Seconds FROM PlaySessions " +
                              "WHERE UploadedAt IS NULL ORDER BY StartedAt LIMIT $max";
            cmd.Parameters.AddWithValue("$max", max);
            using SqliteDataReader r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await r.ReadAsync().ConfigureAwait(false))
            {
                result.Add(new PlaySessionRecord(
                    Guid.Parse(r.GetString(0)), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                    DateTimeOffset.Parse(r.GetString(3), CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture),
                    r.IsDBNull(5) ? null : DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture),
                    r.GetInt64(6)));
            }
            return result;
        }

        public async Task MarkPlaySessionsUploadedAsync(IReadOnlyList<Guid> sessionIds)
        {
            if (sessionIds.Count == 0) return;
            using SqliteConnection db = Open(AchievementsDb);
            using SqliteTransaction tx = db.BeginTransaction();
            using SqliteCommand cmd = db.CreateCommand();
            cmd.Transaction = tx;
            // Only an ENDED session is final. A running one is re-sent at the next flush.
            cmd.CommandText = "UPDATE PlaySessions SET UploadedAt = $now WHERE SessionId = $id AND EndedAt IS NOT NULL";
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            SqliteParameter id = cmd.Parameters.Add("$id", SqliteType.Text);
            foreach (Guid g in sessionIds)
            {
                id.Value = g.ToString();
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            tx.Commit();
        }

        public async Task<long> GetPlaytimeSecondsAsync(string titleId, DateTimeOffset before)
        {
            await EnsureSchema().ConfigureAwait(false);
            long total = 0;
            using SqliteConnection db = Open(AchievementsDb);
            using SqliteCommand cmd = db.CreateCommand();
            cmd.CommandText = "SELECT StartedAt, Seconds FROM PlaySessions WHERE TitleId = $t COLLATE NOCASE";
            cmd.Parameters.AddWithValue("$t", titleId);
            using SqliteDataReader r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await r.ReadAsync().ConfigureAwait(false))
            {
                DateTimeOffset started = DateTimeOffset.Parse(r.GetString(0), CultureInfo.InvariantCulture);
                if (started >= before) continue;
                // A session that spans the unlock counts only up to it.
                total += Math.Min(r.GetInt64(1), (long)(before - started).TotalSeconds);
            }
            return total;
        }

        public async Task<int> RestoreUnlocksAsync(IReadOnlyList<RestoredUnlock> unlocks)
        {
            if (unlocks.Count == 0) return 0;
            await EnsureSchema().ConfigureAwait(false);

            // A game installed here keeps the product id spelling its install row uses; anything
            // else takes the catalogue's (lower case, no braces), so a later install finds it.
            Dictionary<string, string> installed = InstalledProductIds();

            int earned = 0;
            using SqliteConnection db = Open(AchievementsDb);
            using SqliteTransaction tx = db.BeginTransaction();

            using SqliteCommand find = db.CreateCommand();
            find.Transaction = tx;
            // Product ids are matched loosely: the hub spells them upper case without braces, the
            // catalogues lower case, and an install row may carry braces.
            find.CommandText = "SELECT Id, IsEarned, EarnedOnline FROM Achievements " +
                               "WHERE REPLACE(REPLACE(OwnProductId, '{', ''), '}', '') = $p COLLATE NOCASE AND Key = $k LIMIT 1";
            SqliteParameter findProduct = find.Parameters.Add("$p", SqliteType.Text);
            SqliteParameter findKey = find.Parameters.Add("$k", SqliteType.Text);

            using SqliteCommand earn = db.CreateCommand();
            earn.Transaction = tx;
            earn.CommandText = "UPDATE Achievements SET IsEarned = 1, EarnedOnline = 1, EarnedDateTime = $at WHERE Id = $id";
            SqliteParameter earnAt = earn.Parameters.Add("$at", SqliteType.Text);
            SqliteParameter earnId = earn.Parameters.Add("$id", SqliteType.Integer);

            using SqliteCommand confirm = db.CreateCommand();
            confirm.Transaction = tx;
            // Earned here and also on the hub (another device got there first): nothing to send.
            confirm.CommandText = "UPDATE Achievements SET EarnedOnline = 1 WHERE Id = $id";
            SqliteParameter confirmId = confirm.Parameters.Add("$id", SqliteType.Integer);

            using SqliteCommand insert = db.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText =
                "INSERT INTO Achievements (_IconPath, Description, DisplayBeforeEarned, EarnedDateTime, EarnedOnline, " +
                "GamerScore, HowToEarn, IsEarned, Key, Name, OwnProductId) " +
                "VALUES ('', '', 1, $at, 1, $score, '', 1, $k, $name, $p)";
            SqliteParameter insAt = insert.Parameters.Add("$at", SqliteType.Text);
            SqliteParameter insScore = insert.Parameters.Add("$score", SqliteType.Integer);
            SqliteParameter insKey = insert.Parameters.Add("$k", SqliteType.Text);
            SqliteParameter insName = insert.Parameters.Add("$name", SqliteType.Text);
            SqliteParameter insProduct = insert.Parameters.Add("$p", SqliteType.Text);

            foreach (RestoredUnlock u in unlocks)
            {
                if (string.IsNullOrEmpty(u.TitleId) || string.IsNullOrEmpty(u.Key)) continue;
                string product = Normalize(u.TitleId);
                // EF reads EarnedDateTime back as local time with no offset; write it the same way.
                string at = u.UnlockedAt.LocalDateTime.ToString(SqliteDate, CultureInfo.InvariantCulture);

                findProduct.Value = product;
                findKey.Value = u.Key;
                long? id = null;
                bool isEarned = false, isOnline = false;
                using (SqliteDataReader r = await find.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    if (await r.ReadAsync().ConfigureAwait(false))
                    {
                        id = r.GetInt64(0);
                        isEarned = r.GetInt64(1) != 0;
                        isOnline = r.GetInt64(2) != 0;
                    }
                }

                if (id is { } existing)
                {
                    if (!isEarned)
                    {
                        earnAt.Value = at;
                        earnId.Value = existing;
                        await earn.ExecuteNonQueryAsync().ConfigureAwait(false);
                        earned++;
                    }
                    else if (!isOnline)
                    {
                        confirmId.Value = existing;
                        await confirm.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                    continue;
                }

                insAt.Value = at;
                insScore.Value = Math.Max(0, u.Points);
                insKey.Value = u.Key;
                insName.Value = string.IsNullOrWhiteSpace(u.Name) ? u.Key : u.Name;
                insProduct.Value = installed.TryGetValue(product, out string? local) ? local : product.ToLowerInvariant();
                await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
                earned++;
            }

            tx.Commit();
            return earned;
        }

        public async Task<string?> GetMetaAsync(string key)
        {
            await EnsureSchema().ConfigureAwait(false);
            using SqliteConnection db = Open(AchievementsDb);
            using SqliteCommand cmd = db.CreateCommand();
            cmd.CommandText = "SELECT Value FROM WprOnlineMeta WHERE Key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            object? value = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return value is string s ? s : null;
        }

        public async Task SetMetaAsync(string key, string? value)
        {
            await EnsureSchema().ConfigureAwait(false);
            using SqliteConnection db = Open(AchievementsDb);
            using SqliteCommand cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO WprOnlineMeta (Key, Value) VALUES ($k, $v) " +
                              "ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // ------------------------------------------------------------------ plumbing

        private async Task EnsureSchema()
        {
            if (_schemaReady) return;
            await _schemaGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_schemaReady) return;
                using SqliteConnection db = Open(AchievementsDb);
                using SqliteCommand cmd = db.CreateCommand();
                cmd.CommandText =
                    "CREATE TABLE IF NOT EXISTS PlaySessions (" +
                    " SessionId TEXT NOT NULL PRIMARY KEY, TitleId TEXT NOT NULL, TitleName TEXT NULL," +
                    " StartedAt TEXT NOT NULL, LastSeenAt TEXT NOT NULL, EndedAt TEXT NULL," +
                    " Seconds INTEGER NOT NULL, UploadedAt TEXT NULL);" +
                    "CREATE INDEX IF NOT EXISTS IX_PlaySessions_Pending ON PlaySessions (UploadedAt);" +
                    "CREATE TABLE IF NOT EXISTS WprOnlineMeta (Key TEXT NOT NULL PRIMARY KEY, Value TEXT NULL);";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                // Before this existed the award path set EarnedOnline = 1 on every unlock, and nothing
                // was ever sent. Once per database: make all of them "awaiting upload".
                using SqliteTransaction tx = db.BeginTransaction();
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT COUNT(*) FROM WprOnlineMeta WHERE Key = $k";
                cmd.Parameters.AddWithValue("$k", ResetMarker);
                if (Convert.ToInt64(await cmd.ExecuteScalarAsync().ConfigureAwait(false)) == 0)
                {
                    cmd.CommandText = "UPDATE Achievements SET EarnedOnline = 0 WHERE IsEarned = 1;" +
                                      "INSERT INTO WprOnlineMeta (Key, Value) VALUES ($k, $v);";
                    cmd.Parameters.AddWithValue("$v", DateTimeOffset.UtcNow.ToString("O"));
                    int reset = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    Log.Info(LogCategory.GamerServices, $"[wpr-online] {Math.Max(0, reset - 1)} earlier unlock(s) marked awaiting upload");
                }
                tx.Commit();
                _schemaReady = true;
            }
            finally
            {
                _schemaGate.Release();
            }
        }

        private static SqliteConnection Open(string path)
        {
            // A short busy timeout: the other Android process may be writing the same file.
            SqliteConnection db = new SqliteConnection($"Data Source={path};Default Timeout=10");
            db.Open();
            return db;
        }

        /// <summary>Game names by product id, from applications.db, for the hub's title rows.</summary>
        private static Dictionary<string, string> GameNames()
        {
            Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using SqliteConnection db = Open(ApplicationsDb);
                using SqliteCommand cmd = db.CreateCommand();
                cmd.CommandText = "SELECT ProductId, Name FROM Applications";
                using SqliteDataReader r = cmd.ExecuteReader();
                while (r.Read()) names[Normalize(r.GetString(0))] = r.GetString(1);
            }
            catch (Exception)
            {
                // Names are a nicety; the unlocks go without them.
            }
            return names;
        }

        /// <summary>Installed product ids, keyed loosely, mapped to the spelling achievements.db uses for them.</summary>
        private static Dictionary<string, string> InstalledProductIds()
        {
            Dictionary<string, string> ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using SqliteConnection db = Open(ApplicationsDb);
                using SqliteCommand cmd = db.CreateCommand();
                cmd.CommandText = "SELECT ProductId FROM Applications";
                using SqliteDataReader r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (r.IsDBNull(0)) continue;
                    string id = Normalize(r.GetString(0));
                    ids[id] = id; // the seeder trims braces the same way
                }
            }
            catch (Exception)
            {
                // No install table yet (a brand-new device): every restored game is uninstalled.
            }
            return ids;
        }

        private static string Normalize(string productId) => productId.Trim().Trim('{', '}');

        /// <summary>EF stores <c>DateTime.Now</c> as local-time text with no offset.</summary>
        private static DateTimeOffset ParseLocal(string text)
        {
            if (DateTime.TryParseExact(text, SqliteDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime at)
                || DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out at))
            {
                // Seeded rows carry DateTime.MinValue; the hub refuses anything before 2010.
                if (at.Year < 2010) at = DateTime.Now;
                return new DateTimeOffset(at);
            }
            return DateTimeOffset.Now;
        }
    }
}

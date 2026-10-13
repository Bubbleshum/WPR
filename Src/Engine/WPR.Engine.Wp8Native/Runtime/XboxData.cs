namespace WPR.Wp8Native
{
    /// <summary>
    /// The Xbox objects that carry data: achievements, leaderboards, and the collections that
    /// hold them. Layouts are <c>Microsoft.Xbox.winmd</c>'s declaration order, as in
    /// <c>XboxRuntime.cs</c>.
    /// </summary>
    public sealed partial class WinRtRuntime
    {
        /// <summary>Every achievement and leaderboard call the image made, for the post-mortem.</summary>
        public List<string> XboxCalls { get; } = [];

        /// <summary>
        /// <c>IAchievementCollection</c>: get_Items, get_TotalRecords. On Xbox Live
        /// <c>GetAchievementsAsync</c> returned the title's whole list, locked ones included, and
        /// the image draws its achievements screen from it.
        /// </summary>
        private long AchievementCollection(uint skip, uint max, bool unlockedOnly)
        {
            IReadOnlyList<XboxAchievement> all = [];
            try { all = XboxHost?.GetAchievements() ?? []; }
            catch (Exception ex) { XboxCalls.Add($"  host threw {ex.GetType().Name}: {ex.Message}"); }

            List<XboxAchievement> matching = all.Where(a => !unlockedOnly || a.IsEarned).ToList();
            long[] items = matching
                .Skip((int)Math.Min(skip, int.MaxValue))
                .Take(max == 0 ? int.MaxValue : (int)Math.Min(max, int.MaxValue))
                .Select((a, i) => AchievementObject(a, (uint)(skip + i)))
                .ToArray();

            XboxCalls.Add($"GetAchievementsAsync(skip={skip}, max={max}, unlockedOnly={unlockedOnly}) -> {items.Length} of {matching.Count}");
            uint total = (uint)matching.Count;
            return CreateDiscoveryObject(
                "AchievementCollection",
                slotCount: 8,
                known: new Dictionary<int, (string, Action)>
                {
                    [InspectableSlots + 0] = ("get_Items", () => ReturnObject(ObjectVectorView("IVectorView<Achievement>", items))),
                    [InspectableSlots + 1] = ("get_TotalRecords", () => ReturnUInt32(total)),
                });
        }

        /// <summary><c>Microsoft.Xbox.IAchievement</c>.</summary>
        private long AchievementObject(XboxAchievement a, uint sequence) => CreateDiscoveryObject(
            $"Achievement<{a.Id}>",
            slotCount: 16,
            known: new Dictionary<int, (string, Action)>
            {
                [InspectableSlots + 0] = ("get_Id", () => ReturnUInt32(a.Id)),
                [InspectableSlots + 1] = ("get_TitleId", () => ReturnUInt32(1)),
                [InspectableSlots + 2] = ("get_Description", () => ReturnString(a.Description)),
                [InspectableSlots + 3] = ("get_IsSecret", () => ReturnBoolean(a.IsSecret)),

                // Windows.Foundation.DateTime: 100 ns ticks since 1601, i.e. a FILETIME.
                [InspectableSlots + 4] = ("get_TimeUnlocked", () =>
                {
                    if (Arg(1) != 0)
                    {
                        long ticks = a.Unlocked is { } when ? when.ToUniversalTime().ToFileTimeUtc() : 0;
                        _emulator.WriteUInt64(Arg(1), (ulong)ticks);
                    }

                    Return(HResultOk);
                }),
                [InspectableSlots + 5] = ("get_UnlockedOnline", () => ReturnBoolean(a.IsEarned)),
                [InspectableSlots + 6] = ("get_Gamerscore", () => ReturnUInt32((uint)a.Gamerscore)),
                [InspectableSlots + 7] = ("get_LockedDescription", () =>
                    ReturnString(a.IsSecret && !a.IsEarned ? "Secret achievement" : a.Description)),
                [InspectableSlots + 8] = ("get_Name", () => ReturnString(a.Name)),
                [InspectableSlots + 9] = ("get_ImageId", () => ReturnUInt32(0)),
                [InspectableSlots + 10] = ("get_Platform", () => ReturnUInt32(0)),
                [InspectableSlots + 11] = ("get_Sequence", () => ReturnUInt32(sequence)),
                [InspectableSlots + 12] = ("get_Flags", () => ReturnUInt32(0)),
                [InspectableSlots + 13] = ("get_Type", () => ReturnUInt32(0)),
                [InspectableSlots + 14] = ("get_IsEarned", () => ReturnBoolean(a.IsEarned)),
                [InspectableSlots + 15] = ("get_PictureUrl", () => ReturnString(string.Empty)),
            });

        /// <summary>
        /// <c>ILeaderboard</c>: get_Metadata, get_UserRow, get_UserList, get_TotalRecords.
        /// </summary>
        /// <remarks>
        /// Replaces an empty two-member "collection" that sat here before, whose slot 7 -
        /// get_UserRow - answered get_TotalRecords: the image was handed a zero where it expected
        /// a row. A page with no rows is still a whole leaderboard object.
        /// </remarks>
        private long Leaderboard(uint leaderboardId, XboxLeaderboardPage? page)
        {
            IReadOnlyList<XboxLeaderboardRow> rows = page?.Rows ?? [];
            long[] items = rows.Select(LeaderboardRowObject).ToArray();
            XboxLeaderboardRow me = rows.FirstOrDefault(r => r.IsMe) ?? new XboxLeaderboardRow(0, PlayerGamertag, 0, true);
            long userRow = LeaderboardRowObject(me);
            long metadata = LeaderboardMetadata(leaderboardId);
            uint total = page?.TotalRecords ?? 0;

            return CreateDiscoveryObject(
                $"Leaderboard<{leaderboardId}>",
                slotCount: 8,
                known: new Dictionary<int, (string, Action)>
                {
                    [InspectableSlots + 0] = ("get_Metadata", () => ReturnObject(metadata)),
                    [InspectableSlots + 1] = ("get_UserRow", () => ReturnObject(userRow)),
                    [InspectableSlots + 2] = ("get_UserList", () => ReturnObject(ObjectVectorView("IVectorView<LeaderboardRow>", items))),
                    [InspectableSlots + 3] = ("get_TotalRecords", () => ReturnUInt32(total)),
                });
        }

        /// <summary><c>ILeaderboardMetadata</c>: LeaderboardId, LeaderboardName, RatingColumnName.</summary>
        private long LeaderboardMetadata(uint leaderboardId) => CreateDiscoveryObject(
            "LeaderboardMetadata",
            slotCount: 8,
            known: new Dictionary<int, (string, Action)>
            {
                [InspectableSlots + 0] = ("get_LeaderboardId", () => ReturnUInt32(leaderboardId)),
                [InspectableSlots + 1] = ("get_LeaderboardName", () => ReturnString(leaderboardId.ToString())),
                [InspectableSlots + 2] = ("get_RatingColumnName", () => ReturnString("Score")),
            });

        /// <summary><c>ILeaderboardRow</c>: Gamertag, Rank, Rating (a string), Xuid, Attributes.</summary>
        private long LeaderboardRowObject(XboxLeaderboardRow row) => CreateDiscoveryObject(
            $"LeaderboardRow<{row.Rank}>",
            slotCount: 8,
            known: new Dictionary<int, (string, Action)>
            {
                [InspectableSlots + 0] = ("get_Gamertag", () => ReturnString(row.Gamertag)),
                [InspectableSlots + 1] = ("get_Rank", () => ReturnUInt32(row.Rank)),
                [InspectableSlots + 2] = ("get_Rating", () => ReturnString(row.Rating.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                [InspectableSlots + 3] = ("get_Xuid", () =>
                {
                    if (Arg(1) != 0)
                    {
                        // The player's own XUID for their row, so a game that finds itself by
                        // XUID does; anyone else gets a stable one derived from their name.
                        ulong xuid = row.IsMe ? PlayerXuid : PlayerXuid + 1 + (uint)StableHash(row.Gamertag);
                        _emulator.WriteUInt64(Arg(1), xuid);
                    }

                    Return(HResultOk);
                }),
                [InspectableSlots + 4] = ("get_Attributes", () => ReturnObject(EmptyVectorView())),
            });

        private static int StableHash(string text)
        {
            unchecked
            {
                int hash = 17;
                foreach (char c in text)
                {
                    hash = (hash * 31) + c;
                }

                return hash & 0x7FFFFFFF;
            }
        }

        /// <summary>A boolean through the out-parameter in r1, wherever it points.</summary>
        private void ReturnBoolean(bool value)
        {
            if (Arg(1) != 0)
            {
                _emulator.WriteBoolean(Arg(1), value);
            }

            Return(HResultOk);
        }

        /// <summary>
        /// A read-only view over interface pointers, answering as <c>IVectorView&lt;T&gt;</c> and
        /// <c>IIterable&lt;T&gt;</c> - see <see cref="EmptyVectorView"/> for why one object is both
        /// and how slot 6 tells GetAt from First.
        /// </summary>
        private long ObjectVectorView(string name, long[] items) => CreateDiscoveryObject(
            name,
            slotCount: 12,
            known: new Dictionary<int, (string, Action)>
            {
                [InspectableSlots + 0] = ("GetAt|First", () =>
                {
                    if (ArmEmulator.IsStackAddress(Arg(1)))
                    {
                        WriteOut(1, ObjectIterator(name, items));
                        Return(HResultOk);
                        return;
                    }

                    long index = Arg(1);
                    if (index < 0 || index >= items.Length)
                    {
                        WriteOut(2, 0);
                        Return(HResultBounds);
                        return;
                    }

                    WriteOut(2, items[index]);
                    Return(HResultOk);
                }),
                [InspectableSlots + 1] = ("get_Size", () => ReturnUInt32((uint)items.Length)),

                // IndexOf(T value, UINT32* index, boolean* found)
                [InspectableSlots + 2] = ("IndexOf", () =>
                {
                    int at = Array.IndexOf(items, Arg(1));
                    if (Arg(2) != 0)
                    {
                        _emulator.WriteUInt32(Arg(2), (uint)Math.Max(at, 0));
                    }

                    if (Arg(3) != 0)
                    {
                        _emulator.WriteBoolean(Arg(3), at >= 0);
                    }

                    Return(HResultOk);
                }),

                // GetMany(UINT32 startIndex, UINT32 capacity, T* items, UINT32* actual)
                [InspectableSlots + 3] = ("GetMany", () =>
                {
                    long start = Arg(1), capacity = Arg(2), buffer = Arg(3);
                    uint copied = 0;
                    for (long i = start; i < items.Length && copied < capacity && buffer != 0; i++, copied++)
                    {
                        _emulator.WriteUInt32(buffer + (copied * 4), (uint)items[i]);
                    }

                    if (Arg(4) != 0)
                    {
                        _emulator.WriteUInt32(Arg(4), copied);
                    }

                    Return(HResultOk);
                }),
            });

        /// <summary><c>IIterator&lt;T&gt;</c>: get_Current, get_HasCurrent, MoveNext, GetMany.</summary>
        private long ObjectIterator(string name, long[] items)
        {
            int[] position = [0];
            return CreateDiscoveryObject(
                $"IIterator<{name}>",
                slotCount: 12,
                known: new Dictionary<int, (string, Action)>
                {
                    [InspectableSlots + 0] = ("get_Current", () =>
                    {
                        if (position[0] >= items.Length)
                        {
                            WriteOut(1, 0);
                            Return(HResultBounds);
                            return;
                        }

                        WriteOut(1, items[position[0]]);
                        Return(HResultOk);
                    }),
                    [InspectableSlots + 1] = ("get_HasCurrent", () => ReturnBoolean(position[0] < items.Length)),
                    [InspectableSlots + 2] = ("MoveNext", () =>
                    {
                        position[0]++;
                        ReturnBoolean(position[0] < items.Length);
                    }),

                    // GetMany(UINT32 capacity, T* items, UINT32* actual)
                    [InspectableSlots + 3] = ("GetMany", () =>
                    {
                        long capacity = Arg(1), buffer = Arg(2);
                        uint copied = 0;
                        while (position[0] < items.Length && copied < capacity && buffer != 0)
                        {
                            _emulator.WriteUInt32(buffer + (copied * 4), (uint)items[position[0]]);
                            position[0]++;
                            copied++;
                        }

                        if (Arg(3) != 0)
                        {
                            _emulator.WriteUInt32(Arg(3), copied);
                        }

                        Return(HResultOk);
                    }),
                });
        }
    }
}

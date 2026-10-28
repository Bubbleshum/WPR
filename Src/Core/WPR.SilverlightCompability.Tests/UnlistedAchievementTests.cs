using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Xna.Framework.GamerServices;
using WPR.Xna.Achievements;
using WPR.Xna.Rhi;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// An award for a key the title's catalogue does not list (WPR.Xna.Achievements.UnlistedAchievements):
/// the row is created and earned through the ordinary award path.
/// </summary>
[Collection("Achievements")]
public sealed class UnlistedAchievementTests : IDisposable
{
    private const string Product = "aaaaaaaa-1111-2222-3333-444444444444";

    /// <summary>The store in memory, handing out its own objects as EF hands out tracked entities.</summary>
    private sealed class MemoryStore : IAchievementStore
    {
        public readonly List<Achievement> Rows = new();
        public int Saves;

        public Task<IReadOnlyList<Achievement>> GetForProductAsync(string productId) =>
            Task.FromResult<IReadOnlyList<Achievement>>(Rows.Where(a => a.OwnProductId == productId).ToList());
        public Task<IReadOnlyList<Achievement>> GetByKeyAsync(string productId, string key) =>
            Task.FromResult<IReadOnlyList<Achievement>>(Rows.Where(a => a.OwnProductId == productId && a.Key == key).ToList());
        public Task<int> CountForProductAsync(string productId) => Task.FromResult(Rows.Count(a => a.OwnProductId == productId));
        public Task<AchievementTotals> GetEarnedTotalsAsync() =>
            Task.FromResult(new AchievementTotals(Rows.Count(a => a.IsEarned), Rows.Where(a => a.IsEarned).Sum(a => a.GamerScore)));
        public Task<Achievement> AddAsync(Achievement achievement) { Rows.Add(achievement); return Task.FromResult(achievement); }
        public Task SaveChangesAsync() { Saves++; return Task.CompletedTask; }
    }

    private static readonly FieldInfo StoreField =
        typeof(XnaBackend).GetField("_achievements", BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly object? _previousStore = StoreField.GetValue(null);
    private readonly string? _previousProduct = WPR.Common.WprHostEnvironment.CurrentProductId;
    private readonly MemoryStore _store = new();

    public UnlistedAchievementTests()
    {
        XnaBackend.SetAchievements(_store);
        WPR.Common.WprHostEnvironment.CurrentProductId = Product;
        UnlistedAchievements.Allowed = true;
        AchievementToast.Reset();
    }

    public void Dispose()
    {
        StoreField.SetValue(null, _previousStore);
        WPR.Common.WprHostEnvironment.CurrentProductId = _previousProduct;
        UnlistedAchievements.Allowed = true;
        AchievementToast.Reset();
    }

    private static List<AchievementToastRequest> Toasts()
    {
        List<AchievementToastRequest> shown = new();
        while (AchievementToast.TryTake(out AchievementToastRequest? r)) shown.Add(r!);
        return shown;
    }

    // AwardAchievement returns before the award is stored (EndAwardAchievement does not wait), so
    // wait on what Begin hands back.
    private static void Award(string key) =>
        Assert.True(((Task)Gamer.SignedInGamers[0].BeginAwardAchievement(key, null!, null!)).Wait(TimeSpan.FromSeconds(10)));

    [Fact]
    public void AnUnlistedKey_GetsARow_EarnedLocallyFirst_AndToasts()
    {
        Award("ACH_FIRST_GOLD_STAR");

        Achievement row = Assert.Single(_store.Rows);
        Assert.Equal(Product, row.OwnProductId);
        Assert.Equal("ACH_FIRST_GOLD_STAR", row.Key);
        Assert.Equal("First Gold Star", row.Name);
        Assert.Equal(0, row.GamerScore);
        Assert.True(row.IsEarned);
        Assert.False(row.EarnedOnline);
        Assert.NotEqual(DateTime.MinValue, row.EarnedDateTime);
        Assert.True(_store.Saves > 0);

        AchievementToastRequest toast = Assert.Single(Toasts());
        Assert.Equal("First Gold Star", toast.Name);
        Assert.Null(toast.IconPath);
    }

    [Fact]
    public void AnUnlistedKey_AwardedAgain_IsNeitherDuplicatedNorToastedAgain()
    {
        Award("Collector");
        Toasts();
        Award("Collector");

        Assert.Single(_store.Rows);
        Assert.Empty(Toasts());
    }

    [Fact]
    public void AListedKey_FlipsItsCatalogueRow_AndCreatesNothing()
    {
        _store.Rows.Add(new Achievement { OwnProductId = Product, Key = "k1", Name = "Catalogue Name", GamerScore = 20, _IconPath = "" });

        Award("k1");

        Achievement row = Assert.Single(_store.Rows);
        Assert.True(row.IsEarned);
        Assert.Equal("Catalogue Name", Assert.Single(Toasts()).Name);
    }

    [Fact]
    public void ACuratedCatalogue_GetsNoRowForAnUnlistedKey()
    {
        _store.Rows.Add(new Achievement { OwnProductId = Product, Key = "listed", Name = "Listed" });
        UnlistedAchievements.Allowed = false;

        Award("NOT_IN_THE_CATALOGUE");

        Assert.Single(_store.Rows);
        Assert.False(_store.Rows[0].IsEarned);
        Assert.Empty(Toasts());
    }

    [Fact]
    public void ACreatedRow_IsListedByGetAchievements()
    {
        Award("TourOfDuty");

        AchievementCollection listed = Gamer.SignedInGamers[0].GetAchievements();
        Achievement a = Assert.Single(listed);
        Assert.Equal("Tour Of Duty", a.Name);
        Assert.True(a.IsEarned);
    }

    [Fact]
    public void AnAchievementWithNoIcon_StillHasAPicture()
    {
        using Stream picture = UnlistedAchievements.NewRow(Product, "x").GetPicture();
        byte[] head = new byte[8];
        Assert.Equal(8, picture.Read(head, 0, 8));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, head);
    }

    [Theory]
    [InlineData("ACHIEVEMENT_COLLECT_STARS", "Collect Stars")]
    [InlineData("GET_BLUE_BIRD", "Get Blue Bird")]
    [InlineData("TourOfDuty", "Tour Of Duty")]
    [InlineData("12", "Achievement 12")]
    [InlineData("ach-speed.demon", "Speed Demon")]
    public void Readable_NamesAKey(string key, string expected) =>
        Assert.Equal(expected, UnlistedAchievements.Readable(key));
}

[CollectionDefinition("Achievements", DisableParallelization = true)]
public sealed class AchievementsCollection;

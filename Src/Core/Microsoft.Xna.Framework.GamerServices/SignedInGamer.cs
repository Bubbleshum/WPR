using WPR.WindowsCompability;
using WPR.Common;

using Microsoft.EntityFrameworkCore;

using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Threading;
using System;
using System.Diagnostics;

namespace Microsoft.Xna.Framework.GamerServices
{
    public sealed class SignedInGamer : Gamer
    {
        private const int DelaySignedInMillis = 2000;
        private static bool FirstSignInSessionDone = false;

        /// <summary>
        /// Serialises invocation of SignedIn handlers. Games like Tentacles register
        /// the same callback twice from <c>Game1.Initialize</c>; without this gate
        /// the two <see cref="Task.Delay"/> continuations fire on parallel threadpool
        /// threads and both call into <see cref="Gamer.GetProfile"/>, which races on
        /// the shared <see cref="AchievementContext"/> DbContext singleton and trips
        /// EF Core's ConcurrencyDetector. Serialising the handler invocations keeps
        /// the user-visible semantics (handlers fire ~2s after the first <c>+=</c>)
        /// without requiring a per-call DbContext rewrite.
        /// </summary>
        private static readonly SemaphoreSlim _SignInGate = new SemaphoreSlim(1, 1);

        private PlayerIndex _PlayerIndex;

        private GamerPrivileges _GamerPrivileges = new GamerPrivileges();

        private GamerPresence _GamerPresence = new GamerPresence();

        public event EventHandler<EventArgs> AvatarChanged;

        public static void Reset()
        {
            FirstSignInSessionDone = false;
        }

        public static event EventHandler<SignedInEventArgs> SignedIn
        {
            add
            {
#if DEBUG
                Trace.WriteLine($"[wpr-trace] SignedInGamer.SignedIn += handler (FirstSignInSessionDone={FirstSignInSessionDone}, value={(value == null ? "null" : "set")})");
#endif
                if (value == null) return;

                // NEVER fire synchronously from inside `+=`. Game ctors commonly do
                // `SignedIn += handler` partway through the ctor, so a synchronous
                // invoke runs the handler against a half-constructed `this` — Assassin's
                // Creed XNAGame.a NREs that way. Real XNA never fires SignedIn during
                // subscription either; we always defer to the threadpool.
                //
                // First subscriber in a session: 2s delay to give the ctor and
                // Initialize() time to settle. Late subscribers: no delay, they're
                // simulating "you missed the initial sign-in, here it is now".
                int delayMs = FirstSignInSessionDone ? 0 : DelaySignedInMillis;
#if DEBUG
                Trace.WriteLine($"[wpr-trace] SignedInGamer.SignedIn: scheduling Task.Delay({delayMs}ms) → serialised invoke");
#endif
                // Both halves of the work go on a Task.Run so we never block the caller
                // of `+=`. The semaphore (acquired AFTER the delay) ensures that if N
                // handlers register, they each get their delay in parallel but their
                // synchronous Invoke runs serially — preventing the GetProfile→DbContext
                // race described on _SignInGate above.
                _ = Task.Run(async () =>
                {
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs).ConfigureAwait(false);
                    }
                    await _SignInGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
#if DEBUG
                        Trace.WriteLine("[wpr-trace] SignedInGamer.SignedIn: gate acquired, firing handler");
#endif
                        FirstSignInSessionDone = true;
                        //TODO: handle multiple signed in gamers
                        value.Invoke(null, new SignedInEventArgs(_SignedInGamers[0]));
#if DEBUG
                        Trace.WriteLine("[wpr-trace] SignedInGamer.SignedIn: handler returned normally");
#endif
                    }
                    catch (Exception ex)
                    {
#if DEBUG
                        Trace.WriteLine("[wpr-ex] SignedInGamer.SignedIn handler threw: " + ex);
#endif
                        Debug.WriteLine("[ex] SignedInGamer exception: " + ex.Message);
                    }
                    finally
                    {
                        _SignInGate.Release();
                    }
                });
            }
            remove
            {
            }
        }

        public static event EventHandler<SignedOutEventArgs> SignedOut;

        internal SignedInGamer()
        {
        }

        public IAsyncResult BeginGetAchievements(AsyncCallback? callback, Object? asyncState)
        {
            return Task.Run(async () =>
            {
                List<Achievement> achievementStored = await AchievementContext.Current!.Achievements!
                    .Where(x => x.OwnProductId == Application.Current.ProductId)
                    .ToListAsync();

                Trace.WriteLine($"[wpr-trace] BeginGetAchievements: {achievementStored.Count} rows for {Application.Current.ProductId}");

                if (achievementStored.Count == 0)
                {
                    // Scraper can throw — Kinectimals (5a3f9c59...) hits 403 because
                    // its mapping in ProductIdUrl.json is missing/stale, and the raw
                    // HttpRequestException was previously rethrown synchronously
                    // through EndGetAchievements on every Game.Update tick, locking
                    // the splash. Degrade to an empty collection on any failure so
                    // the game can advance past sign-in.
                    AchievementCollection collection;
                    try { collection = await TrueAchievements.Scraper.QueryAchievements(Application.Current!.ProductId!); }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[wpr-trace] BeginGetAchievements: scraper failed, returning empty collection: {ex.Message}");
                        collection = new AchievementCollection();
                    }

                    if (collection.Count != 0)
                    {
                        await AchievementContext.Current!.Achievements!.AddRangeAsync(collection.ToArray());
                        await AchievementContext.Current!.SaveChangesAsync();
                    }

                    if (callback != null)
                    {
                        var compSource = new TaskCompletionSource<AchievementCollection>(asyncState);
                        compSource.SetResult(collection);

                        // Run the game's completion callback on the GAME thread, not this
                        // thread-pool continuation. WP7 titles build their in-game achievement
                        // UI inside this callback and call Texture2D.FromStream(GraphicsDevice, …)
                        // per row; FNA's FNA3D/GL resource calls are thread-affine, so doing that
                        // off-thread fails. See the populated branch below for the full rationale.
                        WprGameThread.Post(() => callback(compSource.Task));
                    }

                    return collection;
                }

                AchievementCollection coll = new AchievementCollection();
                foreach (Achievement achiQueried in achievementStored)
                {
                    coll.Add(achiQueried);
                }

                var completeSource = new TaskCompletionSource<AchievementCollection>(asyncState);
                completeSource.SetResult(coll);

                if (callback != null)
                {
                    // Marshal the game's completion callback onto the GAME thread rather than
                    // invoking it here on this Task.Run thread-pool thread. Games run their
                    // BeginGetAchievements callback to build the in-game achievement screen, and
                    // that build calls Texture2D.FromStream(GraphicsDevice, GetPicture()) once per
                    // achievement (Bejeweled LIVE's GetAchievementsCallback, Assassin's Creed, …).
                    // FNA creates/uploads those textures via FNA3D on the thread that owns the
                    // graphics context, so an off-thread FromStream throws or corrupts — the game
                    // swallows it in a try/catch and the in-game list comes up EMPTY. WprGameThread
                    // drains queued actions at the top of Game.Tick (Game.cs), so the callback runs
                    // on the render thread at the start of the next frame; completeSource is already
                    // completed, so the game's EndGetAchievements(result) returns immediately.
                    WprGameThread.Post(() => callback(completeSource.Task));
                }

                return coll;
            });
        }

        public AchievementCollection EndGetAchievements(IAsyncResult result)
        {
            Task<AchievementCollection>? collectResult = result as Task<AchievementCollection>;
            return collectResult!.GetAwaiter().GetResult();
        }

        public AchievementCollection GetAchievements() => this.EndGetAchievements(this.BeginGetAchievements(null, null));

        public IAsyncResult BeginAwardAchievement(string achievementKey, AsyncCallback callback,
            object state)
        {
            return Task.Run(async () =>
            {
                string productId = Application.Current.ProductId;
                List<Achievement> achievements = await AchievementContext.Current!.Achievements!
                    .Where(x => (x.OwnProductId == productId) && (x.Key == achievementKey))
                    .ToListAsync();

                if (achievements.Count > 1)
                {
                    Log.Warn(LogCategory.GamerServices, $"More then two achievements with key {achievementKey} exists!");
                }

                if (achievements.Count == 0)
                {
                    /* Diagnostic: AwardAchievement fired but we have no matching row
                     * to flip. Two common reasons:
                     *   1) The install-time XnaAchievementSeeder couldn't reach
                     *      TrueAchievements (no internet) or that game has no
                     *      mapping in Database/TrueAchievements/ProductIdUrl.json,
                     *      so the catalogue was never seeded.
                     *   2) The scraper persisted the achievement under its
                     *      TrueAchievements DISPLAY NAME, but the game is
                     *      calling AwardAchievement with the INTERNAL KEY (the
                     *      game's own constant). The display→internal map lives
                     *      in Database/TrueAchievements/AchievementsNameToKey.json
                     *      and may be missing an entry for this product.
                     * Either way: log enough to debug. The user gets no notification
                     * (there's nothing to look up an icon/name for), but the call
                     * doesn't crash either.
                     */
                    int rowsForProduct = await AchievementContext.Current.Achievements!
                        .CountAsync(x => x.OwnProductId == productId);
                    Log.Warn(LogCategory.GamerServices,
                        $"AwardAchievement: no DB row for product '{productId}' key '{achievementKey}'. " +
                        $"{rowsForProduct} achievement(s) seeded for this product. " +
                        "Check that the game's internal key matches the seeded Key column " +
                        "(see Database/TrueAchievements/AchievementsNameToKey.json).");
                }

                if (achievements.Count != 0)
                {
                    foreach (Achievement achievement in achievements)
                    {
                        if (achievement.IsEarned)
                        {
                            continue;
                        }

                        achievement.IsEarned = true;
                        achievement.EarnedOnline = true;
                        achievement.EarnedDateTime = DateTime.Now;
                    }

                    try
                    {
                        // The desktop toast momentarily steals the game window's focus, which
                        // SDL reports as FOCUS_LOST/GAINED → FNA flips Game.IsActive →
                        // OnDeactivated/OnActivated mid-tick. Some WP7 ports throw in those
                        // overrides (Fruit Ninja 2013 surfaces a bogus "memory error" and exits).
                        // Tell FNA to ignore the focus blip for a short window around the toast.
                        WprActivationGuard.SuppressFocusActivation(TimeSpan.FromSeconds(8));

                        await NativeUI.NotificationManager.ShowNotification(new DesktopNotifications.Notification()
                        {
                            Title = Properties.Resources.AchievementUnlocked,
                            Body = $"{achievements[0].GamerScore}G - {achievements[0].Name}",
                            ImagePath = Configuration.Current!.DataPath(achievements[0]._IconPath),
                            SoundUri = "AchievementUnlocked"
                        }, DateTime.Now + TimeSpan.FromDays(1));
                    } catch (Exception ex)
                    {
                        Log.Error(LogCategory.GamerServices, $"Fail to display Achievement notification with exception:\n {ex}");
                    }
                }

                await AchievementContext.Current!.SaveChangesAsync();

                if (callback != null)
                {
                    TaskCompletionSource source = new TaskCompletionSource(state);
                    source.SetResult();

                    callback(source.Task);
                }

                return Task.CompletedTask;
            });
        }

        public void EndAwardAchievement(IAsyncResult result)
        {
        }

        public void AwardAchievement(string achievementKey) => EndAwardAchievement(BeginAwardAchievement(achievementKey, null, null));

        private static readonly FriendCollection EmptyFriends = new FriendCollection();
        private static readonly GameDefaults DefaultGameDefaults = new GameDefaults();
        private static readonly AvatarDescription DefaultAvatar = AvatarDescription.CreateRandom();

        public FriendCollection GetFriends()
        {
            return EmptyFriends;
        }

        public bool IsFriend(Gamer gamer)
        {
            return false;
        }

        public AvatarDescription Avatar
        {
            get
            {
                return DefaultAvatar;
            }
        }

        public GameDefaults GameDefaults
        {
            get
            {
                return DefaultGameDefaults;
            }
        }

        public bool IsGuest => false;

        public bool IsSignedInToLive
        {
            get
            {
                return true;
            }
        }

        public int PartySize
        {
            get
            {
                return 0;
            }
        }

        public PlayerIndex PlayerIndex
        {
            get => _PlayerIndex;
            set => _PlayerIndex = value;
        }

        public GamerPresence Presence
        {
            get => _GamerPresence;
            set => _GamerPresence = value;
        }

        public GamerPrivileges Privileges
        {            
            get => _GamerPrivileges;
            set => _GamerPrivileges = value;
        }
    }
   
}

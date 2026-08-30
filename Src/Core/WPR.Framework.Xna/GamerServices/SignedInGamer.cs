using WPR.Common;


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
        /// the shared achievement store and trips EF Core's ConcurrencyDetector.
        /// Serialising the handler invocations keeps the user-visible semantics (handlers
        /// fire ~2s after the first <c>+=</c>) without requiring a per-call rewrite.
        ///
        /// <para>Still required after Stage 5e: the store moved behind
        /// <c>WPR.Xna.Achievements.IAchievementStore</c>, but WPR.Database backs it with the
        /// same single DbContext, so the race this gate prevents is unchanged.</para>
        /// </summary>
        private static readonly SemaphoreSlim _SignInGate = new SemaphoreSlim(1, 1);

        private PlayerIndex _PlayerIndex;

        private GamerPrivileges _GamerPrivileges = new GamerPrivileges();

        private GamerPresence _GamerPresence = new GamerPresence();

        public event EventHandler<EventArgs> AvatarChanged;

        public static void Reset()
        {
            FirstSignInSessionDone = false;

            // Drop subscribers left behind by an exited game. SignedOut is a field-like STATIC event,
            // so a game handler stays reachable forever and pins the game's collectible
            // AssemblyLoadContext (games do not unsubscribe before exiting). SignedIn does not need
            // this — its custom `add` invokes the handler instead of storing it.
            SignedOut = null;
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
                // Stage 5e: reads go through the registered store (WPR.Database) instead of a
                // DbContext owned by this assembly. No store registered behaves exactly like an
                // unseeded product — an empty collection, and the game still advances past sign-in.
                var store = WPR.Xna.Rhi.XnaBackend.Achievements;
                List<Achievement> achievementStored = store == null
                    ? new List<Achievement>()
                    : (await store.GetForProductAsync(WprHostEnvironment.CurrentProductId)).ToList();

                Trace.WriteLine($"[wpr-trace] BeginGetAchievements: {achievementStored.Count} rows for {WprHostEnvironment.CurrentProductId}");

                if (achievementStored.Count == 0)
                {
                    // No rows for this product: the game has no hardcoded achievement catalogue
                    // (Database/Achievements/<productId>/achievements.json), which XnaAchievementSeeder
                    // seeds at install time. Return an empty collection — the game still advances past
                    // sign-in, it just shows no achievements.
                    //
                    // This used to fall back to a live TrueAchievements web scrape. That scraper was
                    // removed (2026-08-07): the hardcoded catalogue is the source of truth, and the
                    // scrape was network-dependent and unreliable (e.g. Kinectimals 5a3f9c59… returned
                    // 403 on a stale ProductIdUrl.json mapping, and the rethrown HttpRequestException
                    // locked the splash on every Game.Update tick). To give a game achievements, add
                    // it to the catalogue rather than fetching at runtime.
                    AchievementCollection collection = new AchievementCollection();
                    Trace.WriteLine($"[wpr-trace] BeginGetAchievements: no catalogue rows for {WprHostEnvironment.CurrentProductId}, returning empty collection");

                    if (callback != null)
                    {
                        var compSource = new TaskCompletionSource<AchievementCollection>(asyncState);
                        compSource.SetResult(collection);

                        // Run the game's completion callback on the GAME thread, not this
                        // thread-pool continuation. WP7 titles build their in-game achievement
                        // UI inside this callback and call Texture2D.FromStream(GraphicsDevice, …)
                        // per row; FNA's FNA3D/GL resource calls are thread-affine, so doing that
                        // off-thread fails. See the populated branch below for the full rationale.
                        WPR.Xna.Rhi.XnaBackend.PostToGameThread(() => callback(compSource.Task));
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
                    WPR.Xna.Rhi.XnaBackend.PostToGameThread(() => callback(completeSource.Task));
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
                string productId = WprHostEnvironment.CurrentProductId;
                // Stage 5e: award path through the registered store. If no store is registered
                // there is nothing to flip and nothing to persist, so fall through with an empty
                // list — the diagnostics below then report zero rows, which is accurate.
                var store = WPR.Xna.Rhi.XnaBackend.Achievements;
                List<Achievement> achievements = store == null
                    ? new List<Achievement>()
                    : (await store.GetByKeyAsync(productId, achievementKey)).ToList();

                if (achievements.Count > 1)
                {
                    Log.Warn(LogCategory.GamerServices, $"More then two achievements with key {achievementKey} exists!");
                }

                if (achievements.Count == 0)
                {
                    /* Diagnostic: AwardAchievement fired but we have no matching row
                     * to flip. Two common reasons:
                     *   1) This game has no hardcoded catalogue under
                     *      Database/Achievements/<productId>/achievements.json, so
                     *      XnaAchievementSeeder seeded nothing for it at install.
                     *   2) The catalogue IS seeded, but its Key column doesn't match
                     *      the INTERNAL KEY the game passes to AwardAchievement (the
                     *      game's own constant) — e.g. the catalogue was authored with
                     *      display names instead of the game's keys.
                     * Either way: log enough to debug. The user gets no notification
                     * (there's nothing to look up an icon/name for), but the call
                     * doesn't crash either.
                     */
                    int rowsForProduct = store == null
                        ? 0
                        : await store.CountForProductAsync(productId);
                    Log.Warn(LogCategory.GamerServices,
                        $"AwardAchievement: no DB row for product '{productId}' key '{achievementKey}'. " +
                        $"{rowsForProduct} achievement(s) seeded for this product. " +
                        "Check that the game's internal key matches the seeded Key column " +
                        "in Database/Achievements/<productId>/achievements.json.");
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
                        WPR.Xna.Rhi.XnaBackend.SuppressFocusActivation(TimeSpan.FromSeconds(8));

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

                if (store != null)
                {
                    await store.SaveChangesAsync();
                }

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

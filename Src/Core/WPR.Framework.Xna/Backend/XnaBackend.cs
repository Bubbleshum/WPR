using System;

namespace WPR.Xna.Rhi
{
	/// <summary>
	/// The injection point between the WPR-owned XNA runtime and a rendering backend — Stage 5c
	/// (Plans/STAGE5C-SCOPE.md). The XNA type system (WPR.Framework.Xna) references only this holder
	/// and <see cref="IGraphicsBackend"/>; a backend (<c>WPR.Backend.FNA</c>) calls
	/// <see cref="SetGraphics"/> at host startup, before the game constructs its first
	/// <c>GraphicsDevice</c>, and <see cref="Clear"/> on teardown.
	///
	/// <para>This mirrors FNA's existing <c>FNAPlatform</c> static-delegate-table pattern, inverted:
	/// there FNA <em>chooses</em> the SDL backend at load; here the backend <em>pushes</em> its
	/// implementation up into the framework. The holder lives in the default load context (not a
	/// game's collectible ALC) and is stateless per-game, but <see cref="Clear"/> on teardown is
	/// still required hygiene — see ADR Risk #1 (a backend registry that outlives a game is a static
	/// that must not pin native/ALC state).</para>
	///
	/// <para>5c-0 establishes only the graphics slot; the audio/video/input backends get their own
	/// slots here when 5c-3/5c-5 move those subsystems, following the same pattern.</para>
	/// </summary>
	public static class XnaBackend
	{
		private static IGraphicsBackend _graphics;
		private static IInputBackend _input;
		private static IStorageBackend _storage;
		private static IPlatformBackend _platform;
		private static IKeyboardEmulationHost _keyboardEmulation;
		private static WPR.Xna.Achievements.IAchievementStore _achievements;
		private static Func<string> _titleLocation;
		private static Action<string> _logInfo;
		private static Action<string> _logWarn;
		private static Action<int, int> _backBufferSize;
		private static Action<Action> _gameThreadPost;
		private static Action<TimeSpan> _suppressFocusActivation;

		/// <summary>True once a backend has registered a graphics implementation.</summary>

		/// <summary>The active graphics RHI. Throws if no backend has registered one yet.</summary>
		public static IGraphicsBackend Graphics =>
			_graphics ?? throw new InvalidOperationException(
				"No IGraphicsBackend has been registered. The rendering backend " +
				"(e.g. WPR.Backend.FNA) must call XnaBackend.SetGraphics(...) before the XNA " +
				"runtime creates a GraphicsDevice.");

		/// <summary>Registers the graphics backend. Called once by the host at startup.</summary>
		public static void SetGraphics(IGraphicsBackend backend) =>
			_graphics = backend ?? throw new ArgumentNullException(nameof(backend));
		public static bool HasGraphics => _graphics != null;

		/* The audio / XACT / media slots left this class on 2026-09-01. The whole audio subsystem —
		 * contracts, registry and composition — now lives in WPR.Engine.Audio, and the framework's
		 * audio types read AudioBackendRegistry.Sound / .Xact / .Media directly. */

		/// <summary>True once a backend has registered an input implementation.</summary>
		public static bool HasInput => _input != null;

		/// <summary>The active input backend. Throws if no backend has registered one yet.</summary>
		public static IInputBackend Input =>
			_input ?? throw new InvalidOperationException(
				"No IInputBackend has been registered. The platform backend " +
				"(e.g. WPR.Backend.FNA) must call XnaBackend.SetInput(...) before the XNA " +
				"runtime polls a device.");

		/// <summary>Registers the input backend. Called once by the host at startup.</summary>
		public static void SetInput(IInputBackend backend) =>
			_input = backend ?? throw new ArgumentNullException(nameof(backend));

		/// <summary>True once a backend has registered a storage implementation.</summary>
		public static bool HasStorage => _storage != null;

		/// <summary>The active storage backend. Throws if no backend has registered one yet.</summary>
		public static IStorageBackend Storage =>
			_storage ?? throw new InvalidOperationException(
				"No IStorageBackend has been registered. The platform backend " +
				"(e.g. WPR.Backend.FNA) must call XnaBackend.SetStorage(...) before the XNA " +
				"runtime touches StorageDevice.");

		/// <summary>Registers the storage backend. Called once by the host at startup.</summary>
		public static void SetStorage(IStorageBackend backend) =>
			_storage = backend ?? throw new ArgumentNullException(nameof(backend));

		/// <summary>True once a host has registered an achievement store.</summary>
		public static bool HasAchievements => _achievements != null;

		/// <summary>
		/// The achievement persistence backend (Stage 5e), supplied by <c>WPR.Database</c> and
		/// registered by the launcher. Unlike the graphics/audio slots this one returns null rather
		/// than throwing when unset: a game must still reach sign-in and play without achievements,
		/// which is the same degradation the "no catalogue rows for this product" path already had.
		/// GamerServices null-checks it.
		///
		/// <para>Deliberately NOT reset in <see cref="Clear"/>, which only clears the per-game
		/// delegate hooks. The store is registered once by the launcher at startup, not per game,
		/// so clearing it on teardown would silently leave the SECOND game launched without
		/// achievements. It holds no game-ALC state to pin — WPR.Database loads in the default
		/// context — so there is nothing for teardown to release.</para>
		/// </summary>
		public static WPR.Xna.Achievements.IAchievementStore? Achievements => _achievements;

		/// <summary>Registers the achievement store. Called once by the host at startup.</summary>
		public static void SetAchievements(WPR.Xna.Achievements.IAchievementStore backend) =>
			_achievements = backend ?? throw new ArgumentNullException(nameof(backend));

		/// <summary>True once a head has registered a keyboard-tilt emulator.</summary>
		public static bool HasKeyboardEmulation => _keyboardEmulation != null;

		/// <summary>
		/// The desktop keyboard-tilt emulator's head-side policy, or null where there is none
		/// (Android, which has a real accelerometer). Null rather than throwing, exactly like
		/// <see cref="Achievements"/>: absent means the feature is unavailable, not broken.
		///
		/// <para>Deliberately NOT reset in <see cref="Clear"/>. It is registered once by the
		/// launcher in <c>ServicesSetup.Start()</c>, not per game, so clearing it on teardown
		/// would silently leave the SECOND game launched without tilt emulation — the same trap
		/// documented on <see cref="Achievements"/> and on the audio module registry. The
		/// per-launch state it drives lives in the components the backend attaches, and those die
		/// with the game.</para>
		/// </summary>
		public static IKeyboardEmulationHost? KeyboardEmulation => _keyboardEmulation;

		/// <summary>Registers the keyboard-tilt emulator. Called once by the head at startup.</summary>
		public static void SetKeyboardEmulation(IKeyboardEmulationHost host) =>
			_keyboardEmulation = host ?? throw new ArgumentNullException(nameof(host));

		/// <summary>True once a backend has registered a platform implementation.</summary>
		public static bool HasPlatform => _platform != null;

		/// <summary>
		/// The windowing / event-pump backend. Throws if unset, like the graphics and audio slots:
		/// a game with no window is not a degraded experience, it is a broken launch, so failing
		/// with this message beats a NullReferenceException from inside the loop.
		/// </summary>
		public static IPlatformBackend Platform =>
			_platform ?? throw new InvalidOperationException(
				"No IPlatformBackend has been registered. The platform backend " +
				"(e.g. WPR.Backend.FNA) must call XnaBackend.SetPlatform(...) before the XNA " +
				"runtime creates its window.");

		/// <summary>Registers the platform backend. Called once by the host at startup.</summary>
		public static void SetPlatform(IPlatformBackend backend) =>
			_platform = backend ?? throw new ArgumentNullException(nameof(backend));

		/// <summary>
		/// The title's content root — where a game's loose assets live. Backs the relative-URI branch
		/// of <c>Song.FromUri</c> today and the content pipeline's asset rooting in 5c-4.
		///
		/// <para>Deliberately a hook rather than something the framework computes: the answer depends
		/// on how the host launched the title (per-game install folder vs. host exe directory — see
		/// the <c>silverlight-cwd-install-folder</c> case), which is squarely the backend's knowledge.
		/// The fallback below is only reached if a caller asks after <see cref="Clear"/>.</para>
		/// </summary>
		public static string TitleLocation => _titleLocation?.Invoke() ?? AppContext.BaseDirectory;

		/// <summary>Registers the title-location provider. Called once by the host at startup.</summary>
		public static void SetTitleLocation(Func<string> hook) => _titleLocation = hook;

		/// <summary>Diagnostic log sink for the XNA runtime (e.g. PipelineCache), routed to the
		/// backend's logger. Set by the host; no-op if unset. Replaces direct FNALoggerEXT calls.</summary>
		public static void SetLogInfo(Action<string> log) => _logInfo = log;

		/// <summary>Emits an info diagnostic through the registered sink (no-op if none).</summary>
		public static void LogInfo(string message) => _logInfo?.Invoke(message);

		/// <summary>Warning-level counterpart of <see cref="SetLogInfo"/>. Separate sink because the
		/// host routes the two to different places (the content pipeline's "asset loaded as a
		/// different type than requested" notice is a real diagnostic, not chatter).</summary>
		public static void SetLogWarn(Action<string> log) => _logWarn = log;

		/// <summary>Emits a warning diagnostic through the registered sink (no-op if none).</summary>
		public static void LogWarn(string message) => _logWarn?.Invoke(message);

		/// <summary>Registers the game-thread marshaller. Called once by the host at startup.</summary>
		public static void SetGameThreadPost(Action<Action> post) => _gameThreadPost = post;

		/// <summary>
		/// Runs <paramref name="action"/> on the game thread at the start of the next frame.
		///
		/// <para>GamerServices needs this because WP7 titles build achievement UI inside the
		/// EndGetAchievements callback and call <c>Texture2D.FromStream(GraphicsDevice, …)</c> per
		/// row; graphics resource calls are thread-affine, so running the callback on a thread-pool
		/// continuation fails. The backend owns the game loop, so only it can marshal.</para>
		///
		/// <para>With no backend registered the action runs INLINE rather than being dropped. That
		/// is the honest fallback: a host with no game loop has no next frame to wait for, and
		/// silently discarding a completion callback would hang the caller instead.</para>
		/// </summary>
		public static void PostToGameThread(Action action)
		{
			if (action == null) return;
			Action post = _gameThreadPost != null ? null : action;
			_gameThreadPost?.Invoke(action);
			post?.Invoke();
		}

		/// <summary>Registers the focus-activation suppressor. Called once by the host at startup.</summary>
		public static void SetSuppressFocusActivation(Action<TimeSpan> suppress) =>
			_suppressFocusActivation = suppress;

		/// <summary>
		/// Asks the host to ignore focus changes for <paramref name="window"/>. Used around an
		/// achievement toast: the OS focus blip would otherwise drive the game's
		/// OnDeactivated/OnActivated mid-tick, and some WP7 ports throw there (Fruit Ninja 2013
		/// surfaces a bogus "memory error" and exits). No-op if no backend registered.
		/// </summary>
		public static void SuppressFocusActivation(TimeSpan window) =>
			_suppressFocusActivation?.Invoke(window);

		/// <summary>Hook to push the backbuffer size to the platform input devices (mouse/touch
		/// faux-backbuffer scaling). The concrete Mouse/TouchPanel devices live in the backend, so
		/// GraphicsDevice notifies them through here instead of referencing them directly.</summary>
		public static void SetBackBufferSizeHook(Action<int, int> hook) => _backBufferSize = hook;

		/// <summary>Called by GraphicsDevice on device create/reset with the current backbuffer size.</summary>
		public static void NotifyBackBufferSize(int width, int height) => _backBufferSize?.Invoke(width, height);

		/// <summary>
		/// Called by the host on teardown. Clears the per-launch HOOKS but deliberately KEEPS the
		/// backend registrations.
		///
		/// <para><b>Why the backends are not nulled here.</b> XNA resources are finalizable, and their
		/// finalizers release native handles through these backends
		/// (<c>~SoundEffectInstance</c> → <c>Stop</c> → <c>DestroyVoice</c>,
		/// <c>~Texture2D</c> → <c>AddDisposeTexture</c>, …). Those finalizers run on the GC's schedule —
		/// during the host's ALC-unload collection loop and again at any later collection, i.e. AFTER
		/// this method. If the accessors threw "no backend registered" at that point, the exception
		/// would escape a finalizer and take the process down with no diagnostics — which is exactly
		/// the crash-on-close this replaced. Keeping the registration is safe: a backend holds no
		/// reference to the collectible game ALC (only native handles, which it releases in its own
		/// teardown, after which its operations become no-ops), and each launch registers a fresh one.
		/// The per-launch hooks below DO reference host state, so they are cleared.</para>
		/// </summary>
		public static void Clear()
		{
			_logInfo = null;
			_logWarn = null;
			_backBufferSize = null;
			_titleLocation = null;
			_gameThreadPost = null;
			_suppressFocusActivation = null;
		}
	}
}

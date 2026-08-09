using System;

namespace WPR.Xna.Rhi
{
	/// <summary>
	/// The injection point between the WPR-owned XNA runtime and a rendering backend — Stage 5c
	/// (docs/STAGE5C-SCOPE.md). The XNA type system (WPR.Framework.Xna) references only this holder
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
		private static IAudioBackend _audio;
		private static IXactBackend _xact;
		private static IMediaBackend _media;
		private static IInputBackend _input;
		private static IStorageBackend _storage;
		private static Func<string> _titleLocation;
		private static Action<string> _logInfo;
		private static Action<string> _logWarn;
		private static Action<int, int> _backBufferSize;

		/// <summary>True once a backend has registered a graphics implementation.</summary>
		public static bool HasGraphics => _graphics != null;

		/// <summary>True once a backend has registered an audio implementation.</summary>
		public static bool HasAudio => _audio != null;

		/// <summary>The active graphics RHI. Throws if no backend has registered one yet.</summary>
		public static IGraphicsBackend Graphics =>
			_graphics ?? throw new InvalidOperationException(
				"No IGraphicsBackend has been registered. The rendering backend " +
				"(e.g. WPR.Backend.FNA) must call XnaBackend.SetGraphics(...) before the XNA " +
				"runtime creates a GraphicsDevice.");

		/// <summary>The active audio backend. Throws if no backend has registered one yet.</summary>
		public static IAudioBackend Audio =>
			_audio ?? throw new InvalidOperationException(
				"No IAudioBackend has been registered. The audio backend " +
				"(e.g. WPR.Backend.FNA) must call XnaBackend.SetAudio(...) before the XNA " +
				"runtime opens an audio device.");

		/// <summary>Registers the graphics backend. Called once by the host at startup.</summary>
		public static void SetGraphics(IGraphicsBackend backend) =>
			_graphics = backend ?? throw new ArgumentNullException(nameof(backend));

		/// <summary>Registers the audio backend. Called once by the host at startup.</summary>
		public static void SetAudio(IAudioBackend backend) =>
			_audio = backend ?? throw new ArgumentNullException(nameof(backend));

		/// <summary>True once a backend has registered an XACT implementation.</summary>
		public static bool HasXact => _xact != null;

		/// <summary>The active XACT backend. Throws if no backend has registered one yet.</summary>
		public static IXactBackend Xact =>
			_xact ?? throw new InvalidOperationException(
				"No IXactBackend has been registered. The audio backend " +
				"(e.g. WPR.Backend.FNA) must call XnaBackend.SetXact(...) before the XNA " +
				"runtime creates an AudioEngine.");

		/// <summary>Registers the XACT backend. Called once by the host at startup.</summary>
		public static void SetXact(IXactBackend backend) =>
			_xact = backend ?? throw new ArgumentNullException(nameof(backend));

		/// <summary>True once a backend has registered a media implementation.</summary>
		public static bool HasMedia => _media != null;

		/// <summary>The active media backend (song playback + video decode). Throws if no backend
		/// has registered one yet.</summary>
		public static IMediaBackend Media =>
			_media ?? throw new InvalidOperationException(
				"No IMediaBackend has been registered. The media backend " +
				"(e.g. WPR.Backend.FNA) must call XnaBackend.SetMedia(...) before the XNA " +
				"runtime plays a Song or opens a Video.");

		/// <summary>Registers the media backend. Called once by the host at startup.</summary>
		public static void SetMedia(IMediaBackend backend) =>
			_media = backend ?? throw new ArgumentNullException(nameof(backend));

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
		}
	}
}

#region License
/* FNA - XNA4 Reimplementation for Desktop Platforms
 * Copyright 2009-2022 Ethan Lee and the MonoGame Team
 *
 * Released under the Microsoft Public License.
 * See LICENSE for details.
 */
#endregion

#region Using Statements
using System;
using System.Collections.Generic;
using System.Diagnostics;

using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input.Touch;
#endregion

namespace Microsoft.Xna.Framework
{
	public class Game : IDisposable
	{
		#region Public Properties

		public GameComponentCollection Components
		{
			get;
			private set;
		}

		private ContentManager INTERNAL_content;
		public ContentManager Content
		{
			get
			{
				return INTERNAL_content;
			}
			set
			{
				if (value == null)
				{
					//RnD
					//throw new ArgumentNullException();
					value = default;
				}
				INTERNAL_content = value;
			}
		}

		public GraphicsDevice GraphicsDevice
		{
			get
			{
				if (graphicsDeviceService == null)
				{
					graphicsDeviceService = (IGraphicsDeviceService)
						Services.GetService(typeof(IGraphicsDeviceService));

					if (graphicsDeviceService == null)
					{
						throw new InvalidOperationException(
							"No Graphics Device Service"
						);
					}
				}
				return graphicsDeviceService.GraphicsDevice;
			}
		}

		private TimeSpan INTERNAL_inactiveSleepTime;
		public TimeSpan InactiveSleepTime
		{
			get
			{
				return INTERNAL_inactiveSleepTime;
			}
			set
			{
				if (value < TimeSpan.Zero)
				{
					throw new ArgumentOutOfRangeException(
						"The time must be positive.",
						default(Exception)
					);
				}
				if (INTERNAL_inactiveSleepTime != value)
				{
					INTERNAL_inactiveSleepTime = value;
				}
			}
		}

		// Initialised to true so the BeforeLoop `IsActive = true` set is a no-op and
		// the IsActive setter does NOT raise `Activated` during startup. On WP7 the
		// game window is always active from launch (single-app fullscreen), so
		// IsActive being `true` from the very first frame is correct. Firing the
		// `Activated` event itself too early — before the first Update tick — crashes
		// games like Asphalt 5 whose handler looks up state that isn't populated until
		// after their first Update tick (KeyNotFoundException on a state-machine
		// dictionary). The initial `Activated` fire is therefore deferred to the end
		// of the first `Tick` (see `_wprInitialActivatedFired` below). Subsequent
		// background/foreground transitions are handled by the SDL FOCUS_LOST/GAINED
		// branches in `SDL2_FNAPlatform.PollEvents`.
		private bool INTERNAL_isActive = true;
		public bool IsActive
		{
			get
			{
				return INTERNAL_isActive;
			}
			internal set
			{
				if (INTERNAL_isActive != value)
				{
					INTERNAL_isActive = value;
					if (INTERNAL_isActive)
					{
						OnActivated(this, EventArgs.Empty);
					}
					else
					{
						OnDeactivated(this, EventArgs.Empty);
					}
				}
			}
		}

		public bool IsFixedTimeStep
		{
			get;
			set;
		}

		private bool INTERNAL_isMouseVisible;
		public bool IsMouseVisible
		{
			get
			{
				return INTERNAL_isMouseVisible;
			}
			set
			{
				if (INTERNAL_isMouseVisible != value)
				{
					INTERNAL_isMouseVisible = value;
					FNAPlatform.OnIsMouseVisibleChanged(value);
				}
			}
		}

		public LaunchParameters LaunchParameters
		{
			get;
			private set;
		}

		private TimeSpan INTERNAL_targetElapsedTime;
		public TimeSpan TargetElapsedTime
		{
			get
			{
				return INTERNAL_targetElapsedTime;
			}
			set
			{
				if (value <= TimeSpan.Zero)
				{
					throw new ArgumentOutOfRangeException(
						"The time must be positive and non-zero.",
						default(Exception)
					);
				}

				INTERNAL_targetElapsedTime = value;
			}
		}

		public GameServiceContainer Services
		{
			get;
			private set;
		}

		public GameWindow Window
		{
			get;
			private set;
		}

		#endregion

		#region Internal Variables

		internal bool RunApplication;

		#endregion

		#region Private Variables

		/* You will notice that these lists have some locks on them in the code.
		 * Technically this is not accurate to XNA4, as they just happily crash
		 * whenever there's an Add/Remove happening mid-copy.
		 *
		 * But do you really think I want to get reports about that crap?
		 * -flibit
		 */
		private List<IUpdateable> updateableComponents;
		private List<IUpdateable> currentlyUpdatingComponents;
		private List<IDrawable> drawableComponents;
		private List<IDrawable> currentlyDrawingComponents;

		private IGraphicsDeviceService graphicsDeviceService;
		private IGraphicsDeviceManager graphicsDeviceManager;
		private GraphicsAdapter currentAdapter;
		private bool hasInitialized;
		private bool suppressDraw;
		private bool isDisposed;

		private readonly GameTime gameTime;
		private Stopwatch gameTimer;
		private TimeSpan accumulatedElapsedTime;
		private long previousTicks = 0;
		private int updateFrameLag;
		private bool forceElapsedTimeToZero = false;

		// must be a power of 2 so we can do a bitmask optimization when checking worst case
		private const int PREVIOUS_SLEEP_TIME_COUNT = 128;
		private const int SLEEP_TIME_MASK = PREVIOUS_SLEEP_TIME_COUNT - 1;
		private TimeSpan[] previousSleepTimes = new TimeSpan[PREVIOUS_SLEEP_TIME_COUNT];
		private int sleepTimeIndex = 0;
		private TimeSpan worstCaseSleepPrecision = TimeSpan.FromMilliseconds(1);

		private static readonly TimeSpan MaxElapsedTime = TimeSpan.FromMilliseconds(500);

		private bool[] textInputControlDown;
		private bool textInputSuppress;

		#endregion

		#region Events

		public event EventHandler<EventArgs> Activated;
		public event EventHandler<EventArgs> Deactivated;
		public event EventHandler<EventArgs> Disposed;
		public event EventHandler<EventArgs> Exiting;

		#endregion

		#region Public Constructor

		public Game()
		{
			//RnD
			AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

			LaunchParameters = new LaunchParameters();
			Components = new GameComponentCollection();
			Services = new GameServiceContainer();
			Content = new ContentManager(Services);

			updateableComponents = new List<IUpdateable>();
			currentlyUpdatingComponents = new List<IUpdateable>();
			drawableComponents = new List<IDrawable>();
			currentlyDrawingComponents = new List<IDrawable>();

			IsMouseVisible = false;
			IsFixedTimeStep = true;
			TargetElapsedTime = TimeSpan.FromTicks(166667); // 60fps
			InactiveSleepTime = TimeSpan.FromSeconds(0.02);
			for (int i = 0; i < previousSleepTimes.Length; i += 1)
			{
				previousSleepTimes[i] = TimeSpan.FromMilliseconds(1);
			}

			textInputControlDown = new bool[FNAPlatform.TextInputCharacters.Length];

			hasInitialized = false;
			suppressDraw = false;
			isDisposed = false;

			gameTime = new GameTime();

			Window = FNAPlatform.CreateWindow();
			Mouse.WindowHandle = Window.Handle;
			TouchPanel.WindowHandle = Window.Handle;

			FrameworkDispatcher.Update();

			// Ready to run the loop!
			RunApplication = true;
		}

		#endregion

		#region Destructor

		~Game()
		{
			Dispose(false);
		}

		#endregion

		#region IDisposable Implementation

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
			if (Disposed != null)
			{
				Disposed(this, EventArgs.Empty);
			}
		}

		protected virtual void Dispose(bool disposing)
		{
			if (!isDisposed)
			{
				if (disposing)
				{
					// Dispose loaded game components.
					for (int i = 0; i < Components.Count; i += 1)
					{
						IDisposable disposable = Components[i] as IDisposable;
						if (disposable != null)
						{
							disposable.Dispose();
						}
					}

					if (Content != null)
					{
						Content.Dispose();
					}

					if (graphicsDeviceService != null)
					{
						// FIXME: Does XNA4 require the GDM to be disposable? -flibit
						(graphicsDeviceService as IDisposable).Dispose();
					}

					if (Window != null)
					{
						FNAPlatform.DisposeWindow(Window);
					}

					ContentTypeReaderManager.ClearTypeCreators();
				}

				//RnD
				AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;

				isDisposed = true;
			}
		}

		[DebuggerNonUserCode]
		private void AssertNotDisposed()
		{
			if (isDisposed)
			{
				string name = GetType().Name;
				throw new ObjectDisposedException(
					name,
					string.Format(
						"The {0} object was used after being Disposed.",
						name
					)
				);
			}
		}

		#endregion

		#region Public Methods

		public void Exit()
		{
			WprDebugTrace.WriteLine("[wpr-trace] Game.Exit called. Stack: " + new System.Diagnostics.StackTrace(1, false));
			RunApplication = false;
			suppressDraw = true;
		}

		public void ResetElapsedTime()
		{
			/* This only matters the next tick, and ONLY when
			 * IsFixedTimeStep is false!
			 * For fixed timestep, this is totally ignored.
			 * -flibit
			 */
			if (!IsFixedTimeStep)
			{
				forceElapsedTimeToZero = true;
			}
		}

		public void SuppressDraw()
		{
			suppressDraw = true;
		}

		public void RunOneFrame()
		{
			if (!hasInitialized)
			{
				DoInitialize();
				gameTimer = Stopwatch.StartNew();
				hasInitialized = true;
			}

			Tick();
		}

		public void Run()
		{
			AssertNotDisposed();

			WprDebugTrace.WriteLine("[wpr-trace] Game.Run: enter");

			if (!hasInitialized)
			{
				WprDebugTrace.WriteLine("[wpr-trace] Game.Run: DoInitialize starting");
				try
				{
					DoInitialize();
				}
				catch (Exception ex)
				{
					WprDebugTrace.WriteLine("[wpr-ex] Game.Run - DoInitialize threw: " + ex);
					throw;
				}
				WprDebugTrace.WriteLine("[wpr-trace] Game.Run: DoInitialize done");
				hasInitialized = true;
			}

			try
			{
				BeginRun();
			}
			catch (Exception ex)
            {
                WprDebugTrace.WriteLine("[wpr-ex] Game - BeginRun ex: " + ex);
            }

			try
			{
				BeforeLoop();
			}
			catch (Exception ex)
			{
				WprDebugTrace.WriteLine("[wpr-ex] Game - BeforeLoop ex: " + ex);
			}

			try
			{
				gameTimer = Stopwatch.StartNew();
			}
			catch (Exception ex)
			{
                WprDebugTrace.WriteLine("[wpr-ex] Game - StartNow ex: " + ex);
            }

			WprDebugTrace.WriteLine("[wpr-trace] Game.Run: entering RunLoop");
			try
			{
				RunLoop();
			}
			catch (Exception ex)
			{
                WprDebugTrace.WriteLine("[wpr-ex] Game - RunLoop ex: " + ex);
				throw;
            }
			WprDebugTrace.WriteLine("[wpr-trace] Game.Run: RunLoop returned");

			try
			{
				EndRun();
			}
			catch (Exception ex)
            {
                WprDebugTrace.WriteLine("[wpr-ex] Game - EndRun ex: " + ex);
            }

			try
			{
				AfterLoop();
			}
			catch (Exception ex)
            {
                WprDebugTrace.WriteLine("[wpr-ex] Game - AfterLoop ex: " + ex);
            }
		}

		// Counts the first few ticks so wpr-trace stage logs show that the game is actually
		// reaching the main loop. After this threshold we stop logging per-tick to avoid
		// flooding the debug log.
		private int _wprTraceTickCount;

		// Always increments — used by the heartbeat probe (which keeps firing at intervals
		// well past the per-tick trace cap above).
		private long _wprTicksTotal;

		// Reflection probes for the loading-state heartbeat. Cached after first lookup so
		// the per-tick cost is just a field read; null until the user assembly has loaded
		// (which is always by the time Tick fires, but the type discovery is deferred
		// because the user-ALC's types aren't visible from this assembly at static-init).
		private bool _wprHeartbeatTypesScanned;
		private System.Reflection.FieldInfo _wprAppLoadingProgressField;
		private System.Reflection.FieldInfo _wprAppIsLoadingField;
		private System.Reflection.FieldInfo _wprAppSceneToLoadField;
		private System.Reflection.FieldInfo _wprAppLoadIsCompleteField;
		private System.Reflection.FieldInfo _wprAppHasDrawBeenCalledField;
		private System.Reflection.FieldInfo _wprAppLoadedLevelNameField;     // PressPlay.FFWD.Application._loadedLevelName
		private System.Reflection.FieldInfo _wprPreloaderStateField;
		private Type _wprScreenManagerType;
		private System.Reflection.FieldInfo _wprScreenManagerStaticField;     // Application.screenManager
		private System.Reflection.FieldInfo _wprScreensListField;             // ScreenManager.screens
		// Tentacles-specific: LevelHandler.isLoaded gates LemmyTravelScreen.HandleInput; if it
		// stays false, no input → user can never tap to start the level (transition screen hangs).
		private Type _wprLevelHandlerType;
		private System.Reflection.FieldInfo _wprLevelHandlerIsLoadedField;    // static
		private System.Reflection.PropertyInfo _wprLevelHandlerInstanceProp;  // get_Instance
		private System.Reflection.FieldInfo _wprLevelHandlerStateField;
		private System.Reflection.FieldInfo _wprLevelHandlerCurrentLevelField;
		private object _wprPreloaderInstance;
		private long _wprLastHeartbeatTick = -1;

		// Periodic (~once / 2s) dump of FFWD's loading-state. Best-effort, swallows all
		// reflection failures — heartbeat is purely diagnostic and must not affect game
		// behaviour. Emits only for FFWD-based titles (Tentacles etc.); other XNA games
		// won't have these types so the heartbeat silently becomes a no-op.
		[Conditional("DEBUG")]
		private void WprEmitGameStateHeartbeat()
		{
			_wprTicksTotal++;
			// Tick #30 onwards we run this; before that, the verbose per-tick trace covers it.
			if (_wprTicksTotal < 30) return;
			// Emit at ~2-second intervals (60 ticks @ 30fps) — bucket flips when we cross a boundary.
			long bucket = _wprTicksTotal / 60;
			if (bucket == _wprLastHeartbeatTick) return;
			_wprLastHeartbeatTick = bucket;

			try
			{
				if (!_wprHeartbeatTypesScanned)
				{
					_wprHeartbeatTypesScanned = true;
					Type appType = null;
					Type preloaderType = null;
					Type levelHandlerType = null;
					Type screenManagerType = null;
					foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
					{
						try
						{
							if (appType == null) appType = asm.GetType("PressPlay.FFWD.Application", false);
							if (preloaderType == null) preloaderType = asm.GetType("PressPlay.Tentacles.Scripts.PreloaderScreen", false);
							if (levelHandlerType == null) levelHandlerType = asm.GetType("PressPlay.Tentacles.Scripts.LevelHandler", false);
							if (screenManagerType == null) screenManagerType = asm.GetType("PressPlay.FFWD.ScreenManager.ScreenManager", false);
						}
						catch { /* asm may be a dynamic / reflection-only context */ }
						if (appType != null && preloaderType != null && levelHandlerType != null && screenManagerType != null) break;
					}
					const System.Reflection.BindingFlags bf =
						System.Reflection.BindingFlags.Static
						| System.Reflection.BindingFlags.Instance
						| System.Reflection.BindingFlags.Public
						| System.Reflection.BindingFlags.NonPublic;
					if (appType != null)
					{
						_wprAppLoadingProgressField = appType.GetField("_loadingProgess", bf);
						_wprAppIsLoadingField = appType.GetField("isLoadingAssetBeforeSceneInitialize", bf);
						_wprAppSceneToLoadField = appType.GetField("sceneToLoad", bf);
						_wprAppLoadIsCompleteField = appType.GetField("loadIsComplete", bf);
						_wprAppHasDrawBeenCalledField = appType.GetField("hasDrawBeenCalled", bf);
						_wprAppLoadedLevelNameField = appType.GetField("_loadedLevelName", bf);
						_wprScreenManagerStaticField = appType.GetField("screenManager", bf);
					}
					if (preloaderType != null)
					{
						_wprPreloaderStateField = preloaderType.GetField("_state", bf);
					}
					if (screenManagerType != null)
					{
						_wprScreenManagerType = screenManagerType;
						_wprScreensListField = screenManagerType.GetField("screens", bf);
					}
					if (levelHandlerType != null)
					{
						_wprLevelHandlerType = levelHandlerType;
						_wprLevelHandlerIsLoadedField = levelHandlerType.GetField("isLoaded", bf);
						_wprLevelHandlerInstanceProp = levelHandlerType.GetProperty("Instance", bf);
						_wprLevelHandlerStateField = levelHandlerType.GetField("_state", bf);
						_wprLevelHandlerCurrentLevelField = levelHandlerType.GetField("_currentLevel", bf);
					}
				}

				if (_wprAppLoadingProgressField == null) return;

				object loadingProgress = _wprAppLoadingProgressField.GetValue(null);
				object isLoading = _wprAppIsLoadingField?.GetValue(null);
				object sceneToLoad = _wprAppSceneToLoadField?.GetValue(null);
				object loadIsComplete = _wprAppLoadIsCompleteField?.GetValue(null);
				object hasDrawBeenCalled = _wprAppHasDrawBeenCalledField?.GetValue(null);
				object loadedLevelName = _wprAppLoadedLevelNameField?.GetValue(null);

				// Walk the screen manager's screens list each heartbeat so we know which
				// screen is currently active AND we can re-probe a fresh PreloaderScreen /
				// LoadingScreen2 each time (instances cycle as the user moves through menus).
				string topScreens = "<no SM>";
				object preloaderInstance = null;
				object loadingScreen2Instance = null;
				if (_wprScreenManagerStaticField != null && _wprScreensListField != null)
				{
					try
					{
						object sm = _wprScreenManagerStaticField.GetValue(null);
						if (sm != null && _wprScreensListField.GetValue(sm) is System.Collections.IEnumerable screens)
						{
							var names = new System.Collections.Generic.List<string>();
							foreach (object screen in screens)
							{
								if (screen == null) continue;
								string n = screen.GetType().Name;
								names.Add(n);
								if (n == "PreloaderScreen") preloaderInstance = screen;
								else if (n == "LoadingScreen2") loadingScreen2Instance = screen;
							}
							topScreens = "[" + string.Join(",", names) + "]";
						}
					}
					catch (Exception ex) { topScreens = "<sm read failed: " + ex.Message + ">"; }
				}

				string preloaderState = preloaderInstance == null
					? "<not in screens>"
					: SafeRead(_wprPreloaderStateField, preloaderInstance);

				string loadingScreen2State = "<no LoadingScreen2>";
				if (loadingScreen2Instance != null)
				{
					var stateField = loadingScreen2Instance.GetType().GetField("_state",
						System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
					loadingScreen2State = SafeRead(stateField, loadingScreen2Instance);
				}

				// LevelHandler diagnostic — critical for understanding why LemmyTravelScreen
				// ignores taps. HandleInput requires isLoaded=true AND Instance.state==2 AND
				// IsLevelPurchasedOrPartOfTrial(currentLevel) returning non-null true.
				string lhStatus = "<no LevelHandler type>";
				if (_wprLevelHandlerType != null)
				{
					try
					{
						object isLoaded = _wprLevelHandlerIsLoadedField?.GetValue(null);
						object inst = _wprLevelHandlerInstanceProp?.GetValue(null);
						object lhState = inst != null ? _wprLevelHandlerStateField?.GetValue(inst) : null;
						object curLevel = inst != null ? _wprLevelHandlerCurrentLevelField?.GetValue(inst) : null;
						string curLevelDesc = curLevel == null ? "<null>" :
							(curLevel.GetType().GetField("worldsIndex")?.GetValue(curLevel) + "/" +
							 curLevel.GetType().GetField("levelsIndex")?.GetValue(curLevel) + " sceneName=" +
							 curLevel.GetType().GetField("sceneName")?.GetValue(curLevel));
						lhStatus = "isLoaded=" + isLoaded + " state=" + lhState + " currentLevel=" + curLevelDesc;
					}
					catch (Exception ex) { lhStatus = "<lh read failed: " + ex.Message + ">"; }
				}

				WprDebugTrace.WriteLine(
					"[wpr-heartbeat] tick=" + _wprTicksTotal +
					" loadingProgress=" + loadingProgress +
					" isLoading=" + isLoading +
					" sceneToLoad=" + (sceneToLoad ?? "<null>") +
					" loadedLevel=" + (loadedLevelName ?? "<null>") +
					" loadIsComplete=" + loadIsComplete +
					" hasDrawBeenCalled=" + hasDrawBeenCalled +
					" Preloader._state=" + preloaderState +
					" LoadingScreen2._state=" + loadingScreen2State +
					" LevelHandler:" + lhStatus +
					" screens=" + topScreens);
			}
			catch (Exception ex)
			{
				// Once: report and disable further attempts.
				WprDebugTrace.WriteLine("[wpr-heartbeat] disabled after exception: " + ex.GetType().FullName + ": " + ex.Message);
				_wprAppLoadingProgressField = null;
			}
		}

		private static string SafeRead(System.Reflection.FieldInfo f, object target)
		{
			if (f == null) return "<no field>";
			try { return (f.GetValue(target)?.ToString()) ?? "<null>"; }
			catch (Exception ex) { return "<read failed: " + ex.Message + ">"; }
		}

		// Fired once at the end of the first Tick to give XNA games a real `Activated`
		// event. INTERNAL_isActive is initialised true (see field comment above) so the
		// BeforeLoop `IsActive = true` set is a no-op and Activated does NOT raise during
		// startup — but many WP7 ports flip a private "we're in the foreground" flag only
		// from their Activated handler (e.g. Sonic 4 Episode I's AppMain.isForeground).
		// Without an explicit fire that flag stays false forever and the game self-pauses
		// every Update. We defer the fire until after the first Update completes so
		// games like Asphalt 5, whose Activated handler reads a dictionary populated by
		// the first Update tick, don't crash with KeyNotFoundException. Subsequent
		// foreground/background transitions are handled by the SDL FOCUS_LOST/GAINED
		// branches in SDL2_FNAPlatform (the FOCUS_LOST branch fires Deactivated on
		// window minimise/background on both desktop and Android).
		private bool _wprInitialActivatedFired;

		public void Tick()
		{
			/* NOTE: This code is very sensitive and can break very badly,
			 * even with what looks like a safe change. Be sure to test
			 * any change fully in both the fixed and variable timestep
			 * modes across multiple devices and platforms.
			 */

			// WPR: run any work shims have queued for the game thread (e.g. a
			// MediaPlayerLauncher video completing -> title reactivation -> LoadLevel).
			WprGameThread.DrainPending();

			bool wprTraceThisTick = _wprTraceTickCount < 30;
			int wprTraceIndex = _wprTraceTickCount;
			if (wprTraceThisTick)
			{
				WprDebugTrace.WriteLine($"[wpr-trace] Game.Tick #{wprTraceIndex} (IsFixedTimeStep={IsFixedTimeStep}, suppressDraw={suppressDraw})");
				_wprTraceTickCount++;
			}

			// Heartbeat after the verbose first-30-ticks window: every 60 ticks (~2s at 30fps)
			// dump the loading-state of FFWD-using games. Without this we have zero visibility
			// after the initial second, which is exactly when splash-screen hangs become
			// diagnosable (e.g. Tentacles getting stuck because Application.loadingProgress
			// never reaches 1.0f). Best-effort: any reflection failure is swallowed.
			WprEmitGameStateHeartbeat();

			AdvanceElapsedTime();

			if (IsFixedTimeStep)
			{
				/* If we are in fixed timestep, we want to wait until the next frame,
				 * but we don't want to oversleep. Requesting repeated 1ms sleeps and
				 * seeing how long we actually slept for lets us estimate the worst case
				 * sleep precision so we don't oversleep the next frame.
				 */
				while (accumulatedElapsedTime + worstCaseSleepPrecision < TargetElapsedTime)
				{
					System.Threading.Thread.Sleep(1);
					TimeSpan timeAdvancedSinceSleeping = AdvanceElapsedTime();
					UpdateEstimatedSleepPrecision(timeAdvancedSinceSleeping);
				}

				/* Now that we have slept into the sleep precision threshold, we need to wait
				 * for just a little bit longer until the target elapsed time has been reached.
				 * SpinWait(1) works by pausing the thread for very short intervals, so it is
				 * an efficient and time-accurate way to wait out the rest of the time.
				 */
				while (accumulatedElapsedTime < TargetElapsedTime)
				{
					System.Threading.Thread.SpinWait(1);
					AdvanceElapsedTime();
				}
			}

			// Now that we are going to perform an update, let's poll events.
			FNAPlatform.PollEvents(
				this,
				ref currentAdapter,
				textInputControlDown,
				ref textInputSuppress
			);

			// NOTE: do NOT call FrameworkDispatcher.Update() here. Stock FNA's
			// Game.Update (the virtual at the bottom of this file) already calls
			// FrameworkDispatcher.Update() at its end — that gets the per-tick pump
			// WP7 games expect. Adding a second pump here makes TouchPanel.Update run
			// twice per Tick, and the second invocation immediately promotes
			// touches[0] from Pressed to Moved (because between the two pumps,
			// touches.CopyTo(prevTouches, 0) makes prev=Pressed). The game's
			// TouchPanel.GetState() then only ever returns Moved — never Pressed —
			// so touch dispatchers that switch on State==Pressed silently swallow
			// every tap. Asphalt 5 splash → main menu transition was the surfaced
			// case (h2.b "press" path never recorded a finger because the game
			// dispatched op 2 (move) instead of op 0 (press), so h2.d's release
			// guard `if (i > 0)` failed and `be.ey.fm` never flipped).

			// Do not allow any update to take longer than our maximum.
			if (accumulatedElapsedTime > MaxElapsedTime)
			{
				accumulatedElapsedTime = MaxElapsedTime;
			}

			if (IsFixedTimeStep)
			{
				gameTime.ElapsedGameTime = TargetElapsedTime;
				int stepCount = 0;

				// Perform as many full fixed length time steps as we can.
				while (accumulatedElapsedTime >= TargetElapsedTime)
				{
					gameTime.TotalGameTime += TargetElapsedTime;
					accumulatedElapsedTime -= TargetElapsedTime;
					stepCount += 1;

					AssertNotDisposed();

					try
					{
						if (wprTraceThisTick)
						{
							WprDebugTrace.WriteLine($"[wpr-trace] Game.Update #{wprTraceIndex} (fixed) starting; RunApplication={RunApplication}");
						}
						Update(gameTime);
						if (wprTraceThisTick)
						{
							WprDebugTrace.WriteLine($"[wpr-trace] Game.Update #{wprTraceIndex} (fixed) done; RunApplication={RunApplication}");
						}
					}
					catch (Exception ex)
					{
						WprDebugTrace.WriteLine("[wpr-ex] Game.Update (fixed timestep) threw: " + ex);
					}
				}

				// Every update after the first accumulates lag
				updateFrameLag += Math.Max(0, stepCount - 1);

				/* If we think we are running slowly, wait
				 * until the lag clears before resetting it
				 */
				if (gameTime.IsRunningSlowly)
				{
					if (updateFrameLag == 0)
					{
						gameTime.IsRunningSlowly = false;
					}
				}
				else if (updateFrameLag >= 5)
				{
					/* If we lag more than 5 frames,
					 * start thinking we are running slowly.
					 */
					gameTime.IsRunningSlowly = true;
				}

				/* Every time we just do one update and one draw,
				 * then we are not running slowly, so decrease the lag.
				 */
				if (stepCount == 1 && updateFrameLag > 0)
				{
					updateFrameLag -= 1;
				}

				/* Draw needs to know the total elapsed time
				 * that occured for the fixed length updates.
				 */
				gameTime.ElapsedGameTime = TimeSpan.FromTicks(TargetElapsedTime.Ticks * stepCount);
			}
			else
			{
				// Perform a single variable length update.
				if (forceElapsedTimeToZero)
				{
					/* When ResetElapsedTime is called,
					 * Elapsed is forced to zero and
					 * Total is ignored entirely.
					 * -flibit
					 */
					gameTime.ElapsedGameTime = TimeSpan.Zero;
					forceElapsedTimeToZero = false;
				}
				else
				{
					gameTime.ElapsedGameTime = accumulatedElapsedTime;
					gameTime.TotalGameTime += gameTime.ElapsedGameTime;
				}

				accumulatedElapsedTime = TimeSpan.Zero;
				AssertNotDisposed();

                // Plan A
                //Update(gameTime);
                // Plan B
                try
                {
                    if (wprTraceThisTick)
                    {
                        WprDebugTrace.WriteLine($"[wpr-trace] Game.Update #{wprTraceIndex} starting (elapsed={gameTime.ElapsedGameTime.TotalMilliseconds:F2}ms)");
                    }
                    Update(gameTime);
                }
                catch (Exception ex2)
                {
                    WprDebugTrace.WriteLine("[wpr-ex] Game.Update (variable timestep) threw: " + ex2);
					Exit();
                }
            }

			// Draw unless the update suppressed it.
			if (suppressDraw)
			{
				suppressDraw = false;
			}
			else
			{
				/* Draw/EndDraw should not be called if BeginDraw returns false.
				 * http://stackoverflow.com/questions/4054936/manual-control-over-when-to-redraw-the-screen/4057180#4057180
				 * http://stackoverflow.com/questions/4235439/xna-3-1-to-4-0-requires-constant-redraw-or-will-display-a-purple-screen
				 */
				bool beginOk = false;
				try
				{
					beginOk = BeginDraw();
				}
				catch (Exception ex)
				{
					WprDebugTrace.WriteLine("[wpr-ex] Game.BeginDraw threw: " + ex);
				}
				if (wprTraceThisTick)
				{
					WprDebugTrace.WriteLine($"[wpr-trace] Game.Tick #{wprTraceIndex}: BeginDraw -> {beginOk}");
				}
				if (beginOk)
				{
					// Preemptively clear any SpriteBatch left in Begin state from a
					// prior tick whose Draw threw mid-batch. Without this, every frame
					// after a mid-batch NRE wastes itself on InvalidOperationException
					// at the next Begin() — that's exactly what produced the PvZ flicker
					// (NRE frame leaves batch wedged → next frame all-or-nothing).
					try
					{
						int preReset = Microsoft.Xna.Framework.Graphics.SpriteBatch.WprForceEndAll();
						if (preReset > 0)
						{
							WprDebugTrace.WriteLine($"[wpr-trace] Game.Draw #{wprTraceIndex} pre-clean: reset {preReset} stale SpriteBatch(es) from prior tick");
						}
					}
					catch { }

					try
					{
						if (wprTraceThisTick)
						{
							WprDebugTrace.WriteLine($"[wpr-trace] Game.Draw #{wprTraceIndex} starting");
						}
						Draw(gameTime);
						if (wprTraceThisTick)
						{
							WprDebugTrace.WriteLine($"[wpr-trace] Game.Draw #{wprTraceIndex} done");
						}
					}
					catch (Exception ex)
					{
						WprDebugTrace.WriteLine("[wpr-ex] Game.Draw threw: " + ex);
						try
						{
							int reset = Microsoft.Xna.Framework.Graphics.SpriteBatch.WprForceEndAll();
							if (reset > 0)
							{
								WprDebugTrace.WriteLine($"[wpr-trace] Game.Draw recovery: force-ended {reset} SpriteBatch(es) left in Begin state");
							}
						}
						catch (Exception recoveryEx)
						{
							WprDebugTrace.WriteLine("[wpr-ex] Game.Draw SpriteBatch recovery threw: " + recoveryEx);
						}
					}

					try
                    {
                        EndDraw();
                        if (wprTraceThisTick)
                        {
                            WprDebugTrace.WriteLine($"[wpr-trace] Game.EndDraw #{wprTraceIndex} done");
                        }
                    }
                    catch (Exception ex)
                    {
                        WprDebugTrace.WriteLine("[wpr-ex] Game.EndDraw threw: " + ex);
                    }

                }
			}

			if (!_wprInitialActivatedFired)
			{
				_wprInitialActivatedFired = true;
				try
				{
					WprDebugTrace.WriteLine("[wpr-trace] Game.Tick: firing initial OnActivated post-first-tick; subscribers=" + (Activated?.GetInvocationList().Length ?? 0));
					OnActivated(this, EventArgs.Empty);
				}
				catch (Exception ex)
				{
					WprDebugTrace.WriteLine("[wpr-ex] Game - initial OnActivated threw: " + ex);
				}
			}
		}

		#endregion

		#region Internal Methods

		internal void RedrawWindow()
		{
			/* Draw/EndDraw should not be called if BeginDraw returns false.
			 * http://stackoverflow.com/questions/4054936/manual-control-over-when-to-redraw-the-screen/4057180#4057180
			 * http://stackoverflow.com/questions/4235439/xna-3-1-to-4-0-requires-constant-redraw-or-will-display-a-purple-screen
			 *
			 * Additionally, if we haven't even started yet, be quiet until we have!
			 * -flibit
			 */
			if (gameTime.TotalGameTime != TimeSpan.Zero && BeginDraw())
			{
				Draw(new GameTime(gameTime.TotalGameTime, TimeSpan.Zero));
				EndDraw();
			}
		}

		#endregion

		#region Protected Methods

		protected virtual bool BeginDraw()
		{
			if (graphicsDeviceManager != null)
			{
				return graphicsDeviceManager.BeginDraw();
			}
			return true;
		}

		protected virtual void EndDraw()
		{
			if (graphicsDeviceManager != null)
			{
				graphicsDeviceManager.EndDraw();
			}
		}

		protected virtual void BeginRun()
		{
		}

		protected virtual void EndRun()
		{
		}

		protected virtual void LoadContent()
		{
		}

		protected virtual void UnloadContent()
		{
		}

		protected virtual void Initialize()
		{
			/* According to the information given on MSDN, all GameComponents
			 * in Components at the time Initialize() is called are initialized:
			 *
			 * http://msdn.microsoft.com/en-us/library/microsoft.xna.framework.game.initialize.aspx
			 *
			 * Note, however, that we are NOT using a foreach. It's actually
			 * possible to add something during initialization, and those must
			 * also be initialized. There may be a safer way to account for it,
			 * considering it may be possible to _remove_ components as well,
			 * but for now, let's worry about initializing everything we get.
			 * -flibit
			 */
			for (int i = 0; i < Components.Count; i += 1)
			{
				Components[i].Initialize();
			}

			/* This seems like a condition that warrants a major
			 * exception more than a silent failure, but for some
			 * reason it's okay... but only sort of. You can get
			 * away with initializing just before base.Initialize(),
			 * but everything gets super broken on the IManager side
			 * (IService doesn't seem to matter anywhere else).
			 */
			graphicsDeviceService = (IGraphicsDeviceService)
				Services.GetService(typeof(IGraphicsDeviceService));
			if (graphicsDeviceService != null)
			{
				graphicsDeviceService.DeviceDisposing += (o, e) =>
				{
					try
					{
						UnloadContent();
					}
					catch (Exception ex)
					{
						Debug.WriteLine("[ex] UnloadContent ex. : " + ex.Message);
					}
				};
				if (graphicsDeviceService.GraphicsDevice != null)
				{
					LoadContent();
				}
				else
				{
					graphicsDeviceService.DeviceCreated += (o, e) => LoadContent();
				}
			}
		}

		protected virtual void Draw(GameTime gameTime)
		{
			lock (drawableComponents)
			{
				for (int i = 0; i < drawableComponents.Count; i += 1)
				{
					currentlyDrawingComponents.Add(drawableComponents[i]);
				}
			}
			foreach (IDrawable drawable in currentlyDrawingComponents)
			{
				if (drawable.Visible)
				{
					drawable.Draw(gameTime);
				}
			}
			currentlyDrawingComponents.Clear();
		}

		protected virtual void Update(GameTime gameTime)
		{
			try
			{
				lock (updateableComponents)
				{
					for (int i = 0; i < updateableComponents.Count; i += 1)
					{
						currentlyUpdatingComponents.Add(updateableComponents[i]);
					}
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine("[ex] Game - Update - Lock: " + ex.Message);
			}

			try
			{
				foreach (IUpdateable updateable in currentlyUpdatingComponents)
				{
					if (updateable.Enabled)
					{
						try
						{
							updateable.Update(gameTime);
						}
						catch (Exception ex)
						{
							Debug.WriteLine("[ex] ComponentsUpdate ex.: " + ex.Message);
							throw;
						}
					}
				}
			}
			catch (Exception ex)
            {
                Debug.WriteLine("[ex] Game - Update components: " + ex.Message);
            }

			try
			{
				currentlyUpdatingComponents.Clear();
			}
			catch (Exception ex)
            {
                Debug.WriteLine("[ex] Game - Components clear: " + ex.Message);
            }

			try
			{
				FrameworkDispatcher.Update();
			}
			catch (Exception ex)
            {
                Debug.WriteLine("[ex] Game - FrameworkDispatcher Update : " + ex.Message);
            }
		}

		protected virtual void OnExiting(object sender, EventArgs args)
		{
			if (Exiting != null)
			{
				Exiting(this, args);
			}
		}

		protected virtual void OnActivated(object sender, EventArgs args)
		{
			AssertNotDisposed();
			if (Activated != null)
			{
				Activated(this, args);
			}
		}

		protected virtual void OnDeactivated(object sender, EventArgs args)
		{
			AssertNotDisposed();
			if (Deactivated != null)
			{
				Deactivated(this, args);
			}
		}

		protected virtual bool ShowMissingRequirementMessage(Exception exception)
		{
			if (exception is NoAudioHardwareException)
			{
				FNAPlatform.ShowRuntimeError(
					Window.Title,
					"Could not find a suitable audio device. " +
					" Verify that a sound card is\ninstalled," +
					" and check the driver properties to make" +
					" sure it is not disabled."
				);
				return true;
			}
			if (exception is NoSuitableGraphicsDeviceException)
			{
				FNAPlatform.ShowRuntimeError(
					Window.Title,
					"Could not find a suitable graphics device." +
					" More information:\n\n" + exception.Message
				);
				return true;
			}
			return false;
		}

		#endregion

		#region Private Methods

		private void DoInitialize()
		{
			AssertNotDisposed();

			/* If this is late, you can still create it yourself.
			 * In fact, you can even go as far as creating the
			 * _manager_ before base.Initialize(), but Begin/EndDraw
			 * will not get called. Just... please, make the service
			 * before calling Run().
			 */
			graphicsDeviceManager = (IGraphicsDeviceManager)
				Services.GetService(typeof(IGraphicsDeviceManager));
			if (graphicsDeviceManager != null)
			{
				graphicsDeviceManager.CreateDevice();
			}

			try
			{
				Initialize();
			}
			catch (Exception ex)
			{
				Debug.WriteLine("[ex] Game init ex. : " + ex.Message);
#if __ANDROID__
				throw;
#endif
			}

			/* We need to do this after virtual Initialize(...) is called.
			 * 1. Categorize components into IUpdateable and IDrawable lists.
			 * 2. Subscribe to Added/Removed events to keep the categorized
			 * lists synced and to Initialize future components as they are
			 * added.
			 */
			updateableComponents.Clear();
			drawableComponents.Clear();
			for (int i = 0; i < Components.Count; i += 1)
			{
				CategorizeComponent(Components[i]);
			}
			Components.ComponentAdded += OnComponentAdded;
			Components.ComponentRemoved += OnComponentRemoved;
		}

		private void CategorizeComponent(IGameComponent component)
		{
			IUpdateable updateable = component as IUpdateable;
			if (updateable != null)
			{
				lock (updateableComponents)
				{
					SortUpdateable(updateable);
				}
				updateable.UpdateOrderChanged += OnUpdateOrderChanged;
			}

			IDrawable drawable = component as IDrawable;
			if (drawable != null)
			{
				lock (drawableComponents)
				{
					SortDrawable(drawable);
				}
				drawable.DrawOrderChanged += OnDrawOrderChanged;
			}
		}

		private void SortUpdateable(IUpdateable updateable)
		{
			for (int i = 0; i < updateableComponents.Count; i += 1)
			{
				if (updateable.UpdateOrder < updateableComponents[i].UpdateOrder)
				{
					updateableComponents.Insert(i, updateable);
					return;
				}
			}
			updateableComponents.Add(updateable);
		}

		private void SortDrawable(IDrawable drawable)
		{
			for (int i = 0; i < drawableComponents.Count; i += 1)
			{
				if (drawable.DrawOrder < drawableComponents[i].DrawOrder)
				{
					drawableComponents.Insert(i, drawable);
					return;
				}
			}
			drawableComponents.Add(drawable);
		}

		private void BeforeLoop()
		{
			currentAdapter = FNAPlatform.RegisterGame(this);
			IsActive = true;

			// Perform initial check for a touch device
			TouchPanel.TouchDeviceExists = FNAPlatform.GetTouchCapabilities().IsConnected;
		}

		private void AfterLoop()
		{
			FNAPlatform.UnregisterGame(this);
		}

		private void RunLoop()
		{
			/* Some platforms (i.e. Emscripten) don't support
			 * indefinite while loops, so instead we have to
			 * surrender control to the platform's main loop.
			 * -caleb
			 */
			if (FNAPlatform.NeedsPlatformMainLoop())
			{
				/* This breaks control flow and jumps
				 * directly into the platform main loop.
				 * Nothing below this call will be executed.
				 */
				FNAPlatform.RunPlatformMainLoop(this);
			}

			while (RunApplication)
			{
				Tick();
			}

			// OnExiting fires the user game's Exiting event handler chain. WP7 games often
			// gather metrics or save state here and frequently NRE when the user closes
			// during early boot — e.g. Tentacles' MetricsSender.CreateTearDownExtendedKeys
			// dereferences GlobalManager.Instance.currentProfile, which is null until the
			// preloader finishes. Catch + log so the host (WPR.UI.Desktop) doesn't show its
			// "unexpected error" dialog every time the user closes a game that's still
			// initialising. The process is exiting anyway.
			try
			{
				OnExiting(this, EventArgs.Empty);
			}
			catch (Exception ex)
			{
				Debug.WriteLine("[ex] OnExiting handler threw (swallowed on shutdown path): " + ex);
			}
		}

		private TimeSpan AdvanceElapsedTime()
		{
			long currentTicks = gameTimer.Elapsed.Ticks;
			TimeSpan timeAdvanced = TimeSpan.FromTicks(currentTicks - previousTicks);
			accumulatedElapsedTime += timeAdvanced;
			previousTicks = currentTicks;
			return timeAdvanced;
		}

		/* To calculate the sleep precision of the OS, we take the worst case
		 * time spent sleeping over the results of previous requests to sleep 1ms.
		 */
		private void UpdateEstimatedSleepPrecision(TimeSpan timeSpentSleeping)
		{
			/* It is unlikely that the scheduler will actually be more imprecise than
			 * 4ms and we don't want to get wrecked by a single long sleep so we cap this
			 * value at 4ms for sanity.
			 */
			TimeSpan upperTimeBound = TimeSpan.FromMilliseconds(4);

			if (timeSpentSleeping > upperTimeBound)
			{
				timeSpentSleeping = upperTimeBound;
			}

			/* We know the previous worst case - it's saved in worstCaseSleepPrecision.
			 * We also know the current index. So the only way the worst case changes
			 * is if we either 1) just got a new worst case, or 2) the worst case was
			 * the oldest entry on the list.
			 */
			if (timeSpentSleeping >= worstCaseSleepPrecision)
			{
				worstCaseSleepPrecision = timeSpentSleeping;
			}
			else if (previousSleepTimes[sleepTimeIndex] == worstCaseSleepPrecision)
			{
				TimeSpan maxSleepTime = TimeSpan.MinValue;
				for (int i = 0; i < previousSleepTimes.Length; i += 1)
				{
					if (previousSleepTimes[i] > maxSleepTime)
					{
						maxSleepTime = previousSleepTimes[i];
					}
				}
				worstCaseSleepPrecision = maxSleepTime;
			}

			previousSleepTimes[sleepTimeIndex] = timeSpentSleeping;
			sleepTimeIndex = (sleepTimeIndex + 1) & SLEEP_TIME_MASK;
		}

		#endregion

		#region Private Event Handlers

		private void OnComponentAdded(
			object sender,
			GameComponentCollectionEventArgs e
		) {
			/* Since we only subscribe to ComponentAdded after the graphics
			 * devices are set up, it is safe to just blindly call Initialize.
			 */
			e.GameComponent.Initialize();
			CategorizeComponent(e.GameComponent);
		}

		private void OnComponentRemoved(
			object sender,
			GameComponentCollectionEventArgs e
		) {
			IUpdateable updateable = e.GameComponent as IUpdateable;
			if (updateable != null)
			{
				lock (updateableComponents)
				{
					updateableComponents.Remove(updateable);
				}
				updateable.UpdateOrderChanged -= OnUpdateOrderChanged;
			}

			IDrawable drawable = e.GameComponent as IDrawable;
			if (drawable != null)
			{
				lock (drawableComponents)
				{
					drawableComponents.Remove(drawable);
				}
				drawable.DrawOrderChanged -= OnDrawOrderChanged;
			}
		}

		private void OnUpdateOrderChanged(object sender, EventArgs e)
		{
			// FIXME: Is there a better way to re-sort one item? -flibit
			IUpdateable updateable = sender as IUpdateable;
			lock (updateableComponents)
			{
				updateableComponents.Remove(updateable);
				SortUpdateable(updateable);
			}
		}

		private void OnDrawOrderChanged(object sender, EventArgs e)
		{
			// FIXME: Is there a better way to re-sort one item? -flibit
			IDrawable drawable = sender as IDrawable;
			lock (drawableComponents)
			{
				drawableComponents.Remove(drawable);
				SortDrawable(drawable);
			}
		}

		private void OnUnhandledException(
			object sender,
			UnhandledExceptionEventArgs args
		) 
		{
			ShowMissingRequirementMessage(args.ExceptionObject as Exception);
		}

		#endregion
	}
}

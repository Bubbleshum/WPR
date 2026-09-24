using System;
using System.Reflection;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using WPR.SilverlightCompability;

namespace WPR
{
    /// <summary>
    /// Hosts a WP7.1 Silverlight/XNA <em>mixed-mode</em> application as an ordinary
    /// <see cref="Game"/>.
    ///
    /// <para>A mixed-mode title has no <c>Game</c> subclass of its own. Its App.xaml declares a
    /// <see cref="SharedGraphicsDeviceManager"/>, its <c>PhoneApplicationPage</c> owns a
    /// <see cref="GameTimer"/>, and the phone's Silverlight compositor drives that timer's
    /// <c>Update</c>/<c>Draw</c> events. WPR has no Silverlight compositor, so this class
    /// supplies the missing half: it IS the <c>Game</c>, and it pumps the timers the page
    /// registers.</para>
    ///
    /// <para><b>How much Silverlight a mixed-mode title renders is a SPECTRUM, not a property of
    /// the category.</b> An earlier version of this comment asserted that nothing Silverlight is
    /// ever rendered — that every such title carries the same page-XAML comment, <i>"No XAML
    /// content is required as the page is rendered entirely with the XNA Framework"</i>. That is
    /// true of the smallest members and false of the largest. Measured over each title's own main
    /// assembly (the bundled toolkit DLLs inflate the count several-fold, so do not count those):
    /// Little Acorns names 16 Silverlight types and needs no rendering at all; Cut the Rope 21;
    /// Pirates 32, whose main menu <em>is</em> a rasterised Silverlight tree; Galactic Reign 117;
    /// and <b>Carcassonne 139, which is more Silverlight-dependent than Flowerz — a title WPR
    /// classifies as a pure Silverlight app and hosts on the Avalonia renderer instead.</b> There
    /// is no threshold separating the two groups.</para>
    ///
    /// <para>So this host does need a visual tree and a renderer, and it has both: layout runs
    /// per navigation (see <c>OnFrameNavigated</c>) and <c>UIElementRenderer</c> rasterises the
    /// tree on the CPU through <c>SoftwareVisualRasteriser</c>. What it still needs no part of is
    /// <b>Avalonia</b> — which is what lets it run on the Android head, where there is no
    /// Silverlight host at all, and which is why the rasteriser exists as a second renderer
    /// beside <c>SilverlightRenderer</c> rather than reusing it.</para>
    ///
    /// <para>The renderer is deliberately composited in whichever direction the game asks for:
    /// Rabbids draws the overlay <em>before</em> its scene, making it the background, while
    /// Pirates draws it as the whole screen. Do not assume either.</para>
    ///
    /// <para>See <c>Plans/SILVERLIGHT-XNA-CONVERGENCE.md</c> for the full measurement and for why
    /// this host is expected to absorb the pure-Silverlight titles as well.</para>
    /// </summary>
    internal sealed class MixedModeGame : Game
    {
        // WP7's screen, matching GameWindow.ClientBounds and SilverlightHost.Content — a page
        // laid out against anything else would place its controls where the game does not expect.
        private const int PhoneScreenWidth = 480;
        private const int PhoneScreenHeight = 800;

        private readonly string _installFolder;
        private readonly Assembly _userAssembly;
        private readonly string _entryPointTypeName;
        private readonly Action<object> _onAppCreated;
        private readonly Action<string> _trace;

        private readonly GraphicsDeviceManager _graphics;
        private bool _booted;
        private bool _backWasDown;
        private UIElement? _layoutRoot;
        private bool _landscape;

        // Compositing state for a page the game does not draw itself — see CompositeSilverlightPage.
        private UIElementRenderer? _pageRenderer;
        private UIElement? _pageRendererRoot;
        private int _pageRendererWidth;
        private int _pageRendererHeight;
        private SpriteBatch? _pageBatch;
        private bool _compositeFailed;

        // Silverlight touch routing state — see PumpSilverlightTouch.
        private bool _touchDown;
        private float _lastTouchX;
        private float _lastTouchY;
        private bool _touchFailed;

        /// <param name="onAppCreated">
        /// Raised once the game's <c>Application</c> subclass exists and before the first
        /// navigation — where <c>ApplicationLaunch</c> fires the WP7 cold-start lifecycle.
        /// </param>
        public MixedModeGame(
            string installFolder,
            Assembly userAssembly,
            string entryPointTypeName,
            Action<object> onAppCreated,
            Action<string> trace)
        {
            _installFolder = installFolder ?? throw new ArgumentNullException(nameof(installFolder));
            _userAssembly = userAssembly ?? throw new ArgumentNullException(nameof(userAssembly));
            _entryPointTypeName = entryPointTypeName ?? throw new ArgumentNullException(nameof(entryPointTypeName));
            _onAppCreated = onAppCreated ?? (_ => { });
            _trace = trace ?? (_ => { });

            // Built here, in the ctor, because Game.Run creates the device from whatever
            // IGraphicsDeviceManager is registered by the time DoInitialize runs — the same
            // contract an ordinary XNA game's ctor satisfies.
            _graphics = new GraphicsDeviceManager(this);

            // Bind the facade before ANY game code can read it. The App.xaml the boot below
            // parses will construct a second SharedGraphicsDeviceManager of its own (it is
            // declared as an ApplicationLifetimeObjects entry); that one loses the race for
            // Current deliberately and is left unreferenced, because this one is the instance
            // attached to a real device.
            SharedGraphicsDeviceManager.Current.Attach(_graphics);

            // WP7's screen. A mixed-mode page declares its orientation in XAML and renders to
            // the phone surface either way; portrait is the default and what the two Cut the
            // Rope titles use. A game that wants the other one sets PreferredBackBuffer* itself
            // (4 of the 13 do), which reaches the real manager through the facade.
            SharedGraphicsDeviceManager.Current.PreferredBackBufferWidth = 480;
            SharedGraphicsDeviceManager.Current.PreferredBackBufferHeight = 800;

            // The pump is this class's to place — see Update.
            SuppressFrameworkDispatcherUpdate = true;
        }

        /// <summary>
        /// The booted app's root frame, or null if the app never built one. Nothing here renders
        /// it — it is what the hardware Back key is routed into, and what a caller reporting on
        /// the boot looks at.
        /// </summary>
        public PhoneApplicationFrame? RootFrame { get; private set; }

        protected override void LoadContent()
        {
            base.LoadContent();

            // LoadContent runs again on a device reset (Game re-invokes it from DeviceCreated),
            // and booting an app twice would re-run its whole init against a live game.
            if (_booted) return;
            _booted = true;

            SharedGraphicsDeviceManager.Current.RaiseDeviceCreated();

            _trace($"[wpr-mixed] booting mixed-mode app: {_entryPointTypeName}");

            SilverlightAppHost.HostResult result = SilverlightAppHost.BootInContext(
                _installFolder,
                _userAssembly,
                _entryPointTypeName,
                _onAppCreated);

            RootFrame = result.RootFrame;
            _layoutRoot = (UIElement?)(result.RootVisual ?? result.RootFrame);

            // EVERY navigation needs the same treatment as the first one, not just the start
            // page. A mixed-mode title is not one page: Sid Meier's Pirates! goes LogoPage ->
            // MediaElementPage -> GamePage, and GamePage builds its UIElementRenderer from
            // LayoutUpdated exactly as LogoPage does. With only the boot pass, every page after
            // the first had ActualWidth/ActualHeight of zero, never saw LayoutUpdated, and left
            // that renderer null — a NullReferenceException out of the game's own draw, once a
            // frame, on the page where the game actually is.
            if (RootFrame != null) RootFrame.Navigated += OnFrameNavigated;

            // The page the app navigated to decides which way round the screen is. Half the
            // mixed-mode library is landscape, and until this ran they were all given a portrait
            // surface — see ApplyPageOrientation.
            bool landscape = ApplyPageOrientation(CurrentPage(result));
            double layoutWidth = landscape ? PhoneScreenHeight : PhoneScreenWidth;
            double layoutHeight = landscape ? PhoneScreenWidth : PhoneScreenHeight;

            // One layout pass over whatever was navigated to, which is what raises LayoutUpdated.
            // Nothing else will: this host never composites the Silverlight tree, so there is no
            // renderer driving layout the way Silverlight's compositor did. Four of the titles
            // that overlay phone controls on their scene build their UIElementRenderer from that
            // event and dereference it in their draw, so without this they NullReference once a
            // frame. Also what gives every element a non-zero ActualWidth/ActualHeight.
            //
            // Laid out the way round the page asked for: a landscape page measured against a
            // portrait screen arranges every control into the wrong half of it, and a game that
            // sizes its overlay from ActualWidth gets a texture of the wrong shape.
            //
            // After navigation, so the page exists; see RunLayoutPass for why it is not per-frame.
            try
            {
                FrameworkElement.RunLayoutPass(
                    (UIElement)(result.RootVisual ?? result.RootFrame),
                    layoutWidth,
                    layoutHeight);
                _trace($"[wpr-mixed] layout pass run at {layoutWidth}x{layoutHeight}; LayoutUpdated raised");
            }
            catch (Exception ex)
            {
                _trace("[wpr-mixed] layout pass threw: " + ex);
            }

            // A start page that never navigated means the page's OnNavigatedTo never ran, and
            // that is where a mixed-mode title creates its SpriteBatch, loads its content and
            // calls GameTimer.Start. Nothing would draw and the cause would not be obvious from
            // inside the game, so say so.
            if (result.RootFrame == null)
            {
                _trace("[wpr-mixed] WARNING: the app built no PhoneApplicationFrame — its page was " +
                       "never navigated to, so no GameTimer will have started.");
            }
            else
            {
                _trace($"[wpr-mixed] booted; start page = {result.StartPageUri?.ToString() ?? "(none declared)"}");
            }
        }

        protected override void Update(GameTime gameTime)
        {
            // Back first, so a navigation it causes is what the rest of this tick — and this
            // frame's draw — operates on, rather than leaving one frame of the outgoing page.
            PumpHardwareBackKey();

            // Silverlight input, for a page this host is drawing itself. Before the page's own
            // update, so a tap and the frame that reacts to it are the same frame.
            PumpSilverlightTouch();

            // A flick keeps moving after the finger leaves, and nothing in the Silverlight tree
            // has a clock of its own to move it.
            if (!GameTimer.AnyDrawSubscriber)
            {
                try { SilverlightTouchRouter.TickInertia(gameTime.ElapsedGameTime.TotalSeconds); }
                catch { /* inertia must never take the frame down */ }
            }

            // Deferred media transitions first: a page's Update may read CurrentState, and on
            // the phone the media pipeline runs independently of the game loop rather than
            // behind it.
            MediaElement.PumpPending();

            // Silverlight storyboards. This is the only frame loop a mixed-mode title has, and
            // several of them gate navigation on an animation finishing rather than on a timer —
            // Sid Meier's Pirates! leaves its logo page only from a Completed handler, so with no
            // clock it sat there for ever with a clean log. Before the app's own update, so a
            // page that reads an animated property this tick sees this tick's value.
            AnimationClock.Tick(gameTime.ElapsedGameTime);

            // EXACTLY ONE FrameworkDispatcher.Update per tick, and the app gets first refusal.
            //
            // A mixed-mode app has no Game, so the WP7 template makes the app pump: its
            // App.InitializeXnaApplication creates a GameTimer whose FrameAction does nothing
            // but call FrameworkDispatcher.Update. That fires inside PumpUpdate below, after the
            // page's own OnUpdate has polled TouchPanel — which is the ordering WP7 has and the
            // one these games were written against.
            //
            // Pumping again afterwards is not merely redundant, it breaks input: the second pump
            // copies touches into prevTouches, so a finger that was Pressed is immediately aged
            // to Moved and GetState never once reports Pressed. A menu that dispatches on
            // State == Pressed then ignores every tap while rendering perfectly — Cut the Rope
            // on Android, measured, and the same shape Game.Tick's comment records for
            // Asphalt 5. Hence SuppressFrameworkDispatcherUpdate below, set in the ctor.
            int pumpsBefore = FrameworkDispatcher.PumpCount;

            MatchHostRateToTimers();

            GameTimer.PumpUpdate(gameTime.ElapsedGameTime);

            // Components only — the pump is suppressed.
            base.Update(gameTime);

            // Fallback for an app that has no pump of its own. Nothing requires a mixed-mode
            // title to use the template's FrameAction trick, and without a pump there is no
            // audio, no media and no touch at all. Self-correcting: the moment the app starts
            // pumping, this stops.
            if (FrameworkDispatcher.PumpCount == pumpsBefore)
            {
                try
                {
                    FrameworkDispatcher.Update();
                }
                catch (Exception ex)
                {
                    _trace("[wpr-mixed] FrameworkDispatcher.Update threw: " + ex);
                }
            }
        }

        /// <summary>
        /// Reads the orientation off the page that was navigated to and, for a landscape one,
        /// turns the backbuffer on its side. Returns true when the page is landscape.
        /// </summary>
        /// <remarks>
        /// <para><b>Five of the ten mixed-mode titles are landscape</b> — Big Buck Hunter Pro,
        /// Flight Control Rocket, Galactic Reign, Sid Meier's Pirates! and The Game of Life, each
        /// declaring <c>Orientation="Landscape"</c> and a design size of 800x480. Until this ran
        /// the host handed every one of them the portrait 480x800 surface, so a game laid out
        /// across 800 points had 480 of them to draw into and the rest fell off the side. Flight
        /// Control Rocket is the visible case: its menu rendered with the title and one whole
        /// column of buttons off-screen.</para>
        ///
        /// <para><b>Only the backbuffer turns round. <c>Window.ClientBounds</c> stays 480x800.</b>
        /// That asymmetry looks like a bug and is the WP7 contract: the window IS the screen, and
        /// the screen is a panel that never rotates, so a landscape title gets an 800x480
        /// backbuffer and 800x480 touch coordinates while the window keeps reporting 480x800.
        /// Games were written against exactly that — see the long note on
        /// <c>GameWindow.ClientBounds</c>, and do not "fix" the two to agree.</para>
        ///
        /// <para><b>Orientation is read, not guessed.</b> The page carries both
        /// <c>Orientation</c> (what it is now) and <c>SupportedOrientations</c> (what it will
        /// accept); a page is landscape when either says so, because a title that supports only
        /// landscape has said all it needs to even if its current value was left at the default.
        /// Anything else stays portrait, which is both WP7's default and what the other five
        /// want.</para>
        /// </remarks>
        private static PhoneApplicationPage? CurrentPage(SilverlightAppHost.HostResult result)
            => (result.RootFrame?.Content as PhoneApplicationPage)
               ?? result.RootVisual as PhoneApplicationPage;

        /// <summary>
        /// Gives a newly navigated-to page the same orientation and layout treatment the start
        /// page got at boot.
        /// </summary>
        /// <remarks>
        /// Both halves matter, and the layout half is the one that bites: a page that never sees
        /// <c>LayoutUpdated</c> never builds the <c>UIElementRenderer</c> these titles draw
        /// through. Orientation is re-read because each WP7 page declares its own — a portrait
        /// page reached from a landscape one turns the surface back.
        /// </remarks>
        private void OnFrameNavigated(object sender, NavigationEventArgs e)
        {
            try
            {
                PhoneApplicationPage? page = RootFrame?.Content as PhoneApplicationPage;
                if (page == null) return;

                bool landscape = ApplyPageOrientation(page);
                RunLayoutPassFor(page, landscape);
            }
            catch (Exception ex)
            {
                // Navigation is the game's, and a layout fault must not take it down.
                _trace("[wpr-mixed] post-navigation layout threw: " + ex);
            }
        }

        private void RunLayoutPassFor(object page, bool landscape)
        {
            double layoutWidth = landscape ? PhoneScreenHeight : PhoneScreenWidth;
            double layoutHeight = landscape ? PhoneScreenWidth : PhoneScreenHeight;

            // Lay out from the same root as the boot pass: LayOut recurses into a Frame's
            // content, so this reaches the new page while also refreshing anything hosting it.
            UIElement? root = _layoutRoot ?? page as UIElement;
            if (root == null) return;

            FrameworkElement.RunLayoutPass(root, layoutWidth, layoutHeight);
            _trace($"[wpr-mixed] {page.GetType().Name}: layout pass run at {layoutWidth}x{layoutHeight}; " +
                   "LayoutUpdated raised");
        }

        private bool ApplyPageOrientation(PhoneApplicationPage? page)
        {
            if (page == null)
            {
                _trace("[wpr-mixed] no page to read orientation from; staying portrait");
                return false;
            }

            bool landscape =
                (page.Orientation & PageOrientation.Landscape) == PageOrientation.Landscape ||
                page.SupportedOrientations == SupportedPageOrientation.Landscape;

            // Only touch the device when the way round actually changes. This runs on every
            // navigation now, and ApplyChanges is a device reset — re-issuing it per page would
            // drop and rebuild the backbuffer for nothing.
            if (landscape == _landscape)
            {
                _trace($"[wpr-mixed] page {page.GetType().Name} is " +
                       (landscape ? "LANDSCAPE" : "portrait") +
                       $" (Orientation={page.Orientation}, Supported={page.SupportedOrientations}); " +
                       "backbuffer unchanged");
                return landscape;
            }

            _landscape = landscape;
            SharedGraphicsDeviceManager.Current.PreferredBackBufferWidth =
                landscape ? PhoneScreenHeight : PhoneScreenWidth;
            SharedGraphicsDeviceManager.Current.PreferredBackBufferHeight =
                landscape ? PhoneScreenWidth : PhoneScreenHeight;
            SharedGraphicsDeviceManager.Current.ApplyChanges();

            _trace($"[wpr-mixed] page {page.GetType().Name} is " +
                   (landscape ? "LANDSCAPE" : "portrait") +
                   $" (Orientation={page.Orientation}, Supported={page.SupportedOrientations}); " +
                   $"backbuffer now {(landscape ? PhoneScreenHeight : PhoneScreenWidth)}x" +
                   $"{(landscape ? PhoneScreenWidth : PhoneScreenHeight)}");
            return landscape;
        }

        /// <summary>
        /// Paces the host loop at the rate the app's timers asked for.
        /// </summary>
        /// <remarks>
        /// This is how <see cref="GameTimer.UpdateInterval"/> is honoured — every timer then
        /// fires on every tick, so the app's FrameworkDispatcher pump (and with it the touch
        /// drain) stays in lockstep with the page's update. Withholding updates from a timer
        /// instead is what broke input; see the long note on <c>GameTimer.UpdateInterval</c>.
        ///
        /// <para>Re-read every frame rather than captured at boot, because a title is entitled
        /// to change the interval as it goes (a menu at 30 Hz, gameplay at 60), and because the
        /// app's two timers do not both exist until its page has been navigated to. Assignment
        /// is skipped when nothing changed — <c>TargetElapsedTime</c>'s setter has a clamp and a
        /// trace on it, and this runs 30-60 times a second.</para>
        /// </remarks>
        /// <summary>
        /// Routes a hardware Back press into the Silverlight frame, which is where a mixed-mode
        /// title actually handles it, and exits the game when nothing consumed it.
        /// </summary>
        /// <remarks>
        /// <para><b>This is the one place WPR's two Back paths meet, and they were separated on
        /// purpose.</b> The XNA path is a level-sampled <c>GamePad.Buttons.Back</c> held for
        /// exactly one frame; the Silverlight path is a routed event a page can cancel before
        /// <c>GoBack()</c> runs. The note on <c>PhoneHardwareButtons</c> records that they have
        /// disjoint hosts and so need no common interface — true of every title until mixed mode,
        /// which is driven by <c>IGameHost</c> (so it gets the gamepad press) <i>and</i> has real
        /// Silverlight pages (so that is where its Back handler lives). Nothing called
        /// <c>HandleBackKey</c> here, so Back did nothing at all in these games.</para>
        ///
        /// <para><b>Reading the pad rather than draining <c>WprPhoneBackButton</c> is what makes
        /// one implementation cover both heads.</b> Android queues the press there because the
        /// system Back key never reaches SDL; the desktop gets Esc, <c>SDLK_AC_BACK</c> and the
        /// rebindable key straight off the SDL event queue. Both converge on
        /// <c>Buttons.Back</c> inside <c>PollEvents</c>, which runs at the top of the tick — so by
        /// the time this sees it, either origin looks identical. It is also non-destructive: the
        /// press stays visible to a game that polls the pad itself.</para>
        ///
        /// <para><b>Measured before it was written: of the ten mixed-mode titles, all ten
        /// override <c>OnBackKeyPress</c> and NOT ONE acts on <c>Buttons.Back</c>.</b> Two read
        /// it — Flight Control Rocket's <c>GameFCArcade.Update</c> discards the value
        /// (<c>_ = buttons.Back;</c>) and drives itself off its own <c>g_bHandleBackButton</c>
        /// instead, and Big Buck Hunter Pro's read is inside Microsoft's own
        /// <c>PurchaseRequestComponent</c>. So there is no title here that would act twice; if
        /// one ever appears, this is the note that says so and the edge below is where to gate
        /// it.</para>
        ///
        /// <para><b>Edge-triggered, although the press already lasts one frame.</b> A real
        /// controller's Select/View maps to the same button and is held for as long as the player
        /// holds it — <c>SDL2_FNAPlatform.GetGamePadState</c> ORs the two sources together — and
        /// a held pad button must not walk the whole back-stack.</para>
        ///
        /// <para><b>An unconsumed Back exits, which is WP7's own behaviour at the root of the
        /// back-stack.</b> That differs from the pure-Silverlight desktop host, which drops it
        /// (its bezel is a clickable control and the window has a close box). Here the press
        /// comes from a phone's hardware button or from Esc, the window is a game's, and a title
        /// that ignored Back would otherwise be inescapable — the case
        /// <c>GameActivity.OnKeyLongPress</c> exists to rescue.</para>
        /// </remarks>
        private void PumpHardwareBackKey()
        {
            bool down;
            try
            {
                down = GamePad.GetState(PlayerIndex.One).Buttons.Back == ButtonState.Pressed;
            }
            catch (Exception)
            {
                // No input backend yet, or a pad enumeration that failed. Back is not worth
                // taking a launch down for.
                return;
            }

            bool pressed = down && !_backWasDown;
            _backWasDown = down;
            if (!pressed) return;

            PhoneApplicationFrame? frame = RootFrame;
            if (frame == null)
            {
                // The app never built a frame, so there is no page to ask and nothing to go back
                // to. Leaving is the only sensible answer, and it is the way out of a title that
                // is still loading.
                _trace("[wpr-mixed] Back pressed with no root frame — exiting.");
                Exit();
                return;
            }

            bool handled;
            try
            {
                handled = frame.HandleBackKey();
            }
            catch (Exception ex)
            {
                // A throwing handler must not be read as "nothing consumed it" and quit the game
                // out from under the player.
                _trace("[wpr-mixed] Back handler threw: " + ex);
                return;
            }

            if (!handled)
            {
                _trace("[wpr-mixed] Back not handled and the back-stack is empty — exiting.");
                Exit();
            }
        }

        private void MatchHostRateToTimers()
        {
            TimeSpan requested = GameTimer.ShortestInterval;
            if (requested <= TimeSpan.Zero) return;
            if (requested == TargetElapsedTime) return;

            // Game.TargetElapsedTime floors this at one 60 Hz frame, so a title asking for
            // something faster is clamped there rather than spinning.
            TargetElapsedTime = requested;
            _trace($"[wpr-mixed] host tick paced to {requested.TotalMilliseconds:F1} ms " +
                   $"(shortest GameTimer.UpdateInterval); actual {TargetElapsedTime.TotalMilliseconds:F1} ms");
        }

        protected override void Draw(GameTime gameTime)
        {
            base.Draw(gameTime);

            // Inside the host's BeginDraw/EndDraw, which is the only point at which the page's
            // Draw handler may touch the device. See GameTimer's class remarks.
            GameTimer.PumpDraw(gameTime.ElapsedGameTime);

            // A page that registered no Draw handler is not drawing itself — on the phone the
            // Silverlight compositor drew it. Nothing else will here, so do it.
            if (!GameTimer.AnyDrawSubscriber) CompositeSilverlightPage();
        }

        /// <summary>
        /// Feeds <c>TouchPanel</c> samples into the current Silverlight page as routed input.
        /// </summary>
        /// <remarks>
        /// <para><b>Only for a page this host draws</b> — the same
        /// <c>GameTimer.AnyDrawSubscriber</c> test that decides compositing. A page with its own
        /// Draw handler reads <c>TouchPanel</c> itself, and also routing those samples into its
        /// visual tree would deliver every tap twice: once to the game and once to whatever
        /// Silverlight element happened to be underneath, which on a game board is not nothing.
        /// Pages the game draws keep exactly the input behaviour they had.</para>
        ///
        /// <para><b>No coordinate conversion, and that is a fact rather than an omission.</b>
        /// <c>TouchPanel</c> reports in backbuffer pixels, the page is laid out to the backbuffer
        /// (see <c>RunLayoutPassFor</c>), and compositing blits it 1:1 over the whole screen — so
        /// touch space and page space are the same space. If any of those three stops being true,
        /// this needs a scale and the symptom will be taps landing at an offset.</para>
        ///
        /// <para>Reading the touch state does not consume it: the game's own
        /// <c>TouchPanel.GetState()</c> still sees the same frame.</para>
        /// </remarks>
        private void PumpSilverlightTouch()
        {
            if (GameTimer.AnyDrawSubscriber) return;

            UIElement? root = _layoutRoot;
            if (root == null) return;

            try
            {
                TouchCollection touches = TouchPanel.GetState();

                // One finger. WP7 menus are single-touch, and the router models one interaction;
                // taking the first sample keeps which finger is "the" finger stable for the life
                // of a press.
                if (touches.Count == 0)
                {
                    // A release can arrive as the absence of any sample rather than as a Released
                    // one — the drain clears the slot on the same tick some platforms report the
                    // lift. Close the interaction at the last known position so a tap is not lost.
                    if (_touchDown)
                    {
                        _touchDown = false;
                        SilverlightTouchRouter.Release(_lastTouchX, _lastTouchY);
                    }
                    return;
                }

                TouchLocation touch = touches[0];
                _lastTouchX = touch.Position.X;
                _lastTouchY = touch.Position.Y;


                switch (touch.State)
                {
                    case TouchLocationState.Pressed:
                        _touchDown = true;
                        SilverlightTouchRouter.Press(root, _lastTouchX, _lastTouchY);
                        break;

                    case TouchLocationState.Moved:
                        // A press whose Pressed sample we never saw (the finger was already down
                        // when this page appeared) still has to open an interaction, or its
                        // release would be dropped.
                        if (!_touchDown)
                        {
                            _touchDown = true;
                            SilverlightTouchRouter.Press(root, _lastTouchX, _lastTouchY);
                        }
                        else
                        {
                            SilverlightTouchRouter.Move(_lastTouchX, _lastTouchY);
                        }
                        break;

                    case TouchLocationState.Released:
                        if (_touchDown)
                        {
                            _touchDown = false;
                            SilverlightTouchRouter.Release(_lastTouchX, _lastTouchY);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                // Input must never take the frame down; a game handler that throws is the game's.
                if (!_touchFailed)
                {
                    _touchFailed = true;
                    _trace("[wpr-mixed] Silverlight touch routing failed (reported once): " + ex);
                }
            }
        }

        /// <summary>
        /// Rasterises the current Silverlight page and blits it over the whole screen.
        /// </summary>
        /// <remarks>
        /// <para><b>Only for a page the game does not draw itself</b> — see
        /// <c>GameTimer.AnyDrawSubscriber</c>. A mixed-mode title is not uniformly XNA:
        /// Carcassonne paints its board from <c>IngamePage</c>'s own <c>GameTimer</c> and
        /// <c>UIElementRenderer</c>, but its <c>MainMenu</c> is plain Silverlight with no XNA in
        /// it at all. Until this existed that menu was a black screen with a clean log —
        /// everything constructed, navigated and laid out, and nothing ever asked to draw it.</para>
        ///
        /// <para><b>Full-screen and opaque-cleared, unlike the overlay a game composites
        /// itself.</b> Here the page IS the frame, so there is nothing underneath to preserve; the
        /// rasteriser deliberately paints no page backdrop (it is normally composited over a
        /// game's scene), so without the clear a page would show whatever the previous frame
        /// left.</para>
        ///
        /// <para>The renderer is rebuilt only when the page or the backbuffer size changes.
        /// <c>UIElementRenderer.Render</c> is itself gated on a signature of the tree, so a static
        /// page costs a walk of a few nodes and no GPU traffic.</para>
        /// </remarks>
        private void CompositeSilverlightPage()
        {
            UIElement? root = _layoutRoot;
            if (root == null) return;

            try
            {
                int width = GraphicsDevice.PresentationParameters.BackBufferWidth;
                int height = GraphicsDevice.PresentationParameters.BackBufferHeight;
                if (width <= 0 || height <= 0) return;

                if (_pageRenderer == null || !ReferenceEquals(_pageRendererRoot, root) ||
                    _pageRendererWidth != width || _pageRendererHeight != height)
                {
                    _pageRenderer?.Dispose();
                    _pageRenderer = new UIElementRenderer(root, width, height);
                    _pageRendererRoot = root;
                    _pageRendererWidth = width;
                    _pageRendererHeight = height;
                }

                _pageRenderer.Render();

                Texture2D? texture = _pageRenderer.Texture;
                if (texture == null) return;

                _pageBatch ??= new SpriteBatch(GraphicsDevice);

                // Qualified: both frameworks in scope here define Color and Rectangle.
                GraphicsDevice.Clear(Microsoft.Xna.Framework.Color.Black);
                _pageBatch.Begin();
                _pageBatch.Draw(
                    texture,
                    new Microsoft.Xna.Framework.Rectangle(0, 0, width, height),
                    Microsoft.Xna.Framework.Color.White);
                _pageBatch.End();
            }
            catch (Exception ex)
            {
                // Never let compositing take the frame down; a page that cannot be drawn is a
                // black screen, which is what it already was.
                if (!_compositeFailed)
                {
                    _compositeFailed = true;
                    _trace("[wpr-mixed] Silverlight page compositing failed (reported once): " + ex);
                }
            }
        }

        // Deliberately no OnExiting override. The WP7 Deactivated/Closing pair — which is where
        // these titles save (Cut the Rope's Preferences._savePreferences hangs off Closing) — is
        // already fired by ApplicationLaunch's own teardown, on the path every game shares.
        // Firing it here as well would deliver Closing twice and have a title write its
        // preferences, recycle the service, and then write them again against an empty
        // subscriber list.
    }
}

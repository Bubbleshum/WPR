# WPR open work

Built 2026-10-16 from every `TODO` / `FIXME` / `HACK` / `XXX` / "not implemented" /
`throw new NotImplementedException` in WPR's own code, plus the open items CLAUDE.md records.
Each item was checked against the code on that date; line numbers are as of commit `combine`
at that time. Ranked by impact within each group.

**Not on this list, on purpose:**

- The ~190 `FIXME -flibit` / `TODO` lines in code that came from upstream FNA or MonoGame
  (`WPR.Framework.Xna`'s Graphics, Effect, Content, Design, Media/Xiph, the math types, LZX,
  and `FNA.Platform/src`). They are upstream's notes about upstream's code, not WPR work.
- Upstream FNA `NotImplementedException`s on APIs WP7 never had: `GraphicsAdapter`
  (`DeviceId`, `VendorId`, `Revision`, `SubSystemId`, `MonitorHandle`),
  `StorageDevice.DeleteContainer`, `GameWindow.IsBorderlessEXT`, `BoundingFrustum.Intersects`
  with a ray, the Effect render-state switches.
- Won't-fix by design: the Xbox-only avatar API (`AvatarRenderer`, `AvatarAnimation`,
  `AvatarExpression` throw; WP7 had no avatars); `FriendGamer` members throw, but no instance
  can exist because `SignedInGamer.GetFriends()` is always empty (leaderboard friends go
  through `LeaderboardGamer` instead); `SignedInGamer.SignedIn` "handle multiple signed in
  gamers" (a phone has one); `MarketplaceDetailTask`/`MarketplaceReviewTask` are empty
  (no Marketplace); `CameraCaptureTask`/`PhotoChooserTask` answer Cancel; no Vulkan
  validation layer is shipped; Android Back is the hardware button only.

## WP8 native runtime

1. **Asphalt Overdrive** (WP8 Direct3D/XAML) stops: no `XamlShells` description, and its title
   shows as `@AppResLib.dll,-100`. Being handled by the game-format agent (2026-10-16).
2. **WP8 movies on Android**: install and repatch now write sidecars for native titles (merged
   2026-10-16); WP8 titles installed before that need a repatch to get them.
3. **`Obscured`/`Unobscured` (lock screen, calls) are never raised.** MC4 pauses its game only on
   these, so it comes back from the background unpaused.
   `Src/Engine/WPR.Engine.Wp8Native/Runtime/XamlShells.cs:59`
4. **Guest-to-guest copies go through a host buffer**: ~40% of the ~30 s post-PLAY load in
   AC Pirates is `wprcpu_read`/`wprcpu_write`. Needs a native copy in `wprcpu` (rebuild of
   `wprcpu.dll` and the Android `.so`). `Src/Engine/WPR.Engine.Wp8Native/Runtime/ArmEmulator.cs:637`
5. **AC Pirates' leaderboard rows are not drawn**: each row's `get_Attributes` answers an empty
   view; the game probably asks `GetLeaderboardAsync` for attribute ids.
   `Src/Engine/WPR.Engine.Wp8Native/Runtime/XboxData.cs:179`
6. **MC4's Min board**: SPA puts Min on an XP column; if MC4 ever posts, XP lands on a lower-wins
   board. Never observed posting. `Src/Engine/WPR.Engine.Wp8Native/Wp8NativeXboxHost.cs:301`
7. **Angry Birds Seasons / Star Wars** show "Sorry, we're not sure what happened" on the
   leaderboard screen until a score has been posted (their boards are defined on the service,
   not in the SPA).
8. Not reached live, so unverified: MONOPOLY's `Score` (Sum) board, Classic's unlocks, Halo
   gameplay (needs two fingers; the harness injects one).
9. **The "no thread is runnable" fix is not verified on Android** (2026-10-21, commit `7a006328`).
   Halo's stop never reproduced on the desktop, so it is fixed from the report and a desktop trace
   only. Check the WPR Hub group stops growing for Halo, Fruit Ninja 2013 and Jetpack Joyride on
   the next build. Then run all three on A1/A2 and the S24.
10. **The Mandreel message thread is a host stand-in** for the game's own managed page
    (`MainPage.Alpha.Beta`, `Runtime/MandreelMessages.cs`). It is kept by decision, against the
    rule that a game's shipped libraries run as shipped. It goes once the managed page can run.
11. **Jetpack Joyride's world art draws only on OpenGL** (D3 and D4). On D3D11 and Vulkan a run
    shows only the HUD and the backdrop text.
12. **Mandreel titles draw into a 480-wide strip** at the left of a 1280x768 desktop window,
    rotated.

## Silverlight shims

1. **`BitmapImage` hides `BitmapSource`'s decoder** with an empty `SetSource` and
   `PixelWidth`/`PixelHeight` = 4. Game IL binds the `BitmapSource` members so games are not
   affected, but WPR code (and reflection) holding a `BitmapImage` gets the stubs. Quick win:
   delete the three members. `Src/Core/WPR.Framework.Silverlight/System/Windows/Media/Imaging/BitmapImage.cs:67`
2. Rasteriser shapes: `StrokeDashArray` phase restarts per figure; miters past the limit are
   bevelled rather than clipped. `Src/Core/WPR.Framework.Silverlight/SoftwareVisualRasteriser.Shapes.cs`
3. **Launchers not wired**: `EmailComposeTask.Show` (needs `mailto:` in the allowed schemes) and
   `BingMapsTask.Show` are empty. `Src/Core/WPR.Framework.Phone/Tasks/EmailComposeTask.cs:15`,
   `Src/Core/WPR.Framework.Phone/Tasks/BingMapsTask.cs:32`
4. **`Microsoft.Devices.Sensors.Gyroscope` has no shim** (no installed WP7 title uses it; WP8
   native titles have one through WinRT).

## XNA framework

1. **`EffectMaterialReader` gives up on an unsupported parameter type** by returning `default`,
   so the whole material is lost rather than that one parameter.
   `Src/Core/WPR.Framework.Xna/Content/ContentReaders/EffectMaterialReader.cs:104`
2. **Texture readback on GLES returns zeros** for `GetData` at mip level > 0, non-32-bit formats
   and cube maps (a CPU mirror was deliberately not built; build it when a title needs its
   pixels back). `Src/Core/WPR.Framework.Xna/Graphics/TextureReadback.cs`
3. **XNA's read-only enforcement of bound state objects is not implemented** (binding is), by
   choice until a title needs it.
4. Controller rumble for WP7's `VibrateController`: a `Controller` slot beside `Device` in
   `VibrationBackend`, filled by a `WPR.Vibration.Gamepad` module. Designed, not built.

## Native graphics (FNA3D binaries)

1. **Windows `FNA3D.dll` cannot be rebuilt here**, so forced Vulkan / OpenGL on the desktop lack
   the per-call `passLock` fix (Plants vs. Zombies hangs on D2), the optional-feature checks, the
   GL worker contexts and the ES capability check that the Android `libFNA3D.so` carry.
2. **Forcing D3D11 by name is rejected**: a named driver returning zero window flags is read as a
   decline, and D3D11 needs none. Automatic still picks D3D11.
   `Src/Backends/FNA.Platform/src/FNAPlatform/SDL2_FNAPlatform.cs:487`
3. GL worker contexts: a worker updating a texture the device context already has bound may not
   be seen until it is re-bound (not seen in practice).
4. Mali (G57-class) is unverified for every fix made blind for it (#51).

## Android head

1. **The achievements page does not ask WPR Hub for title names** (the desktop does, through
   `WPR.Shell.HubTitleNames`), so games without a catalogue entry show a product id.
2. **WP8 titles' display name from `AppResLib.dll` string resources is not resolved** (both
   heads): the manifest's `@AppResLib.dll,-100` is skipped in `PackageManifests.IsResourceString`
   and the catalogue name or XAP file name is used instead, so Minesweeper and Asphalt Overdrive
   can show a raw resource reference. `Src/Core/WPR.Loader/PackageManifests.cs:403`
3. `AndroidNotificationManager.ScheduleNotification` throws `NotImplementedException`. Nothing
   calls it today. Quick win: log and return. `Src/Modules/Notifications/WPR.Notifications.AndroidChannel/AndroidNotificationManager.cs:130`
4. A bundled Unity port APK is offered for install rather than used when no port is installed.
5. Android **Debug** build fails `MSB4096 … TrimmerRootAssembly … "RootMode"` (Release builds).
6. Pinned shortcuts keep the accent they were pinned with (deliberate; noted for completeness).

## Windows head

1. **No share sheet**: `ShareLinkTask`/`ShareStatusTask` log "no share sheet on this platform";
   the WinRT `DataTransferManager` needs a window handle.
   `Src/Platforms/WPR.Platform.Windows/WindowsPlatform.cs:76`
2. **No update check UI**: `WPR.Shell.AppUpdates` is used only by the Android head.
3. **No vibration switch** in the desktop settings page (only pad rumble would be affected).
4. No Start Menu / desktop game shortcuts (Android has pinned shortcuts).
5. **Avalonia 12 migration** (desktop is on 11.3.9): four migrations behind the version number,
   listed on `AvaloniaVersion` in `WPR.Platform.Windows.csproj`.

## Loader / patcher

1. **`FileInfo`/`DirectoryInfo` paths are not separator-normalised** (both sealed; needs a
   call-site rewrite like `RedirectIsolatedStorageOpens`; 3 uses in the installed library).
   `Src/Core/WPR.Loader/ApplicationPatcher.cs:1890`
2. **GameMaker achievement catalogues are not extracted** (the empty catalogue only affects
   WPR's own list). `Src/Core/WPR.Loader/GameMakerAchievementExtractor.cs:35`
3. GameMaker export launch is unverified: no GameMaker title or runtime is installed here, and
   WPR never installs a runtime itself.
4. Unity WP titles: the launcher rail exists, no per-game port does.

## Online / hub

1. **Install receipts are sent as `manual` crash reports** to get an upload offer; a dedicated
   anonymous "offer" endpoint on the hub would be cleaner.
2. The `compat-reports-as-crashes` hub branch must be deployed, or per-problem reports count as
   crashes on the compatibility pages. Refusal and install-problem reports were not sent end to
   end; the Android `HandleGameResult` recovery path was not run on a device.
3. Hub sort heuristic: `BestScoreLifeTime` boards defined nowhere rank lower-is-better until the
   definitions repo covers them.

## Tooling

1. The harness's synthetic tap reports `Released` for more than one frame (Cut the Rope's "any
   touch skips" skips its intro in the harness only).
2. The harness cannot inject a second finger (Halo gameplay).
3. `scratchpad/gamerun`: `Script.cs`, `matrix-run.ps1` and `matrix-grid.ps1` are local only;
   committing `Program.cs` alone breaks the harness elsewhere.

## Known game problems not caused by WPR's own TODOs

- Galactic Reign: stack overflow in its own `ScreenManager.DoNavigation` → our inline
  `Dispatcher.BeginInvoke`, ~2 in 9 launches, either driver.
- Sid Meier's Pirates!: NRE in its own `ISEUpdateCommandBuffer.Update` after the opening movie.

## Quick wins (a few lines each, not done in the TODO sweep)

- Delete `BitmapImage`'s hiding `SetSource`/`get_PixelWidth`/`get_PixelHeight`.
- `AndroidNotificationManager.ScheduleNotification`: log and return `Task.CompletedTask`.

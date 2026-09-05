# WPR.Platform.Android

The Android head. Builds `com.wpr.android` (assembly `WPR.Platform.Android`) for
`net8.0-android34.0`, minimum API 21.

## Shell: native Android, not Avalonia

The launcher UI — Start, games, achievements, settings, about — is plain
`android.app.Activity` with XML layouts under `Resources/layout`. It replaced an
Avalonia `SingleView` shell that shared its pages with the desktop head through a
`WPR.UI` project (since dissolved — those pages now live in the Windows head).
Going native is what makes list momentum, the app bar, tile press feedback,
system back and the document picker behave like the platform instead of
approximating it.

The look is Windows Phone 7/8 "dark": pure black, Segoe-ish light type
(`sans-serif-light`), square tiles in the user's accent colour, and an app bar of
ring-outlined glyph buttons. `Native/WpTheme.cs` owns every accented surface, so
changing the accent in Settings is one config write plus a repaint.

| Activity | Role |
| --- | --- |
| `MainActivity` | Start screen — tile grid, live counts, launcher entry point |
| `Native/GamesActivity` | installed games; app bar add / achievements / refresh |
| `Native/AchievementsActivity` | roll-up per game, or one game's achievement list |
| `Native/SettingsActivity` | gamertag, accent colour |
| `Native/AboutActivity` | version and credits |
| `Native/GameShortcutActivity` | what a pinned home-screen game shortcut starts; resolves a ProductId and hands off to `GameLauncher`, in its own task |
| `GameActivity` | hosts one game run under SDL in its own `:game` process |

`GameActivity` owns the hardware Back button. WP7 treats Back as a game input, so a
press is forwarded to the running game as one frame of `GamePad.Buttons.Back` — the
same edge Esc produces on the Windows head — rather than finishing the activity.
Two things make that work: `OnBackPressed` does **not** call base (SDLActivity's
would finish us), and `SDL2_FNAPlatform.ProgramInit` sets
`SDL_ANDROID_TRAP_BACK_BUTTON` on Android so SDL's own native back path
(`manualBackButton` → `superOnBackPressed`, which bypasses the override) stays out
of it. Games exit at their root screen the way they do on WP7, which unwinds the
loop into `FinishIfNeeded`; a game that ignores Back entirely is escaped by
**holding** Back (`OnKeyLongPress`), which only exists on three-button navigation
and hardware keys — gesture navigation has no long-press Back.

Avalonia is still on the reference graph, for two reasons. `MessageBoxUtils` and
`ServicesSetup` (this head's own copies, at the project root next to
`ApplicationLaunchRequest` and `LocaleUtils`) are typed against
`MessageBox.Avalonia`'s button and icon enums — the vocabulary
`Guide.ShowMessageBoxFunc` speaks — even though the Android implementation renders
with `AlertDialog`. And the `Avalonia.Android` package supplies the AndroidX
AppCompat resources that `MyTheme.NoActionBar` (used by `GameActivity` and the
splash) parents onto — don't drop it without re-parenting those styles. Nothing in
the launcher process initialises Avalonia itself.

Those four root files plus `Properties/Resources.resx` and
`System/Windows/MessageBox.cs` are **duplicated verbatim** in
`Src/Platforms/WPR.Platform.Windows/` (modulo namespace and the `#if __ANDROID__`
branch each side kept). They used to be one shared `WPR.UI` project; when it was
dissolved the pieces both heads needed were copied into both. Change one, change
the other.

## Games arrive one .xap at a time

There is no library scan on Android. `WPR.LibraryScanner`, which the desktop head
uses to watch a configured folder, is never constructed here: scoped storage
means walking shared storage needs broad, user-hostile permissions, and guessing
at folders is worse UX than asking. `Native/XapInstallFlow.cs` opens the system
document picker (`ACTION_OPEN_DOCUMENT`, `*/*` because no provider maps the
`.xap` extension to a MIME type), copies the chosen document into cache — the
installer needs a seekable stream, and a `content://` stream is forward-only —
then runs the normal `ApplicationInstaller.Install` pipeline.

## Build

Requires the .NET 8 SDK with the `android` workload installed **for the 8.0
band** (the repo-root `global.json` pins it), the Android SDK with
`platforms/android-34`, and a JDK. See the root `CLAUDE.md` for the full
toolchain notes and the CLI recipe.

```
dotnet build Src/Platforms/WPR.Platform.Android/WPR.Platform.Android.csproj -c Debug
```

Output: `bin/Debug/net8.0-android34.0/com.wpr.android-Signed.apk`.

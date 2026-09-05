<div align="center">

![WPR — Windows Phone Runner](Images/Wpr_logo.png)

# WPR — Windows Phone Runner

**Play your old Windows Phone games again, on a PC or an Android phone.**

`0.1.*` · [Compatibility list](https://bubbleshum.github.io/WPR/) · [MIT licensed](LICENSE)

</div>

---

## What is this?

Windows Phone is gone, and the games that were on it went with it. Titles like
*Fruit Ninja*, *Pac-Man CE DX*, *ilomilo* and hundreds of others were only ever
released for a phone you can't buy any more, and they've never been re-released
anywhere else.

WPR brings them back. Point it at a game file you already own, click **Install**,
and it appears in your library ready to play — with its Xbox achievements
intact.

It isn't an emulator in the usual sense. It doesn't pretend to be a phone;
instead it takes the game's original program files and rewires the parts that ask
for Windows Phone features so they talk to modern replacements instead. The game
then runs directly on your machine, at your screen's resolution and speed.

> **This is early software.** It's a hobby project, it's still being actively
> rebuilt underneath, and plenty of games don't work yet. If you want something
> polished and finished, this isn't it — yet.

## What it looks like

### On Windows

Your library, with box art, achievements and one-click launching:

![The WPR desktop library](Images/screenshot-windows-library.png)

Achievements are tracked per game and saved between sessions:

![Achievement tracking on the desktop](Images/screenshot-windows-achievements.png)

Many phone games are steered by tilting the handset. On a PC you bind those tilts
to keys, with a live preview so you can check which way is which:

![Keyboard tilt controls](Images/screenshot-windows-controls.png)

### On Android

The Android app keeps the Windows Phone look on purpose — the tile Start screen
will be familiar if you ever owned one:

<p align="center">
  <img src="Images/screenshot-android-start.png" width="30%" alt="The Android start screen" />
  <img src="Images/screenshot-android-games.png" width="30%" alt="The Android games list" />
  <img src="Images/screenshot-android-achievements.png" width="30%" alt="Achievements on Android" />
</p>

## What's new in 0.1.02

- **Play touch games with the keyboard.** Bind a key to a tap or a swipe, drawn on
  a to-scale phone outline rather than typed as coordinates. Per game, from the
  Controls button on a game's page. The Back key is rebindable too.
- **Games save again.** Progress, settings and high scores could silently fail to
  persist while the game looked perfectly healthy. Worth retrying anything you'd
  written off as "loses progress".
- **Three games that hung or went blank now work** — Game Room: Pitfall! freezing
  on its second splash, Guitar Hero 5's black screen on Android, and Angry Birds'
  credits page becoming impossible to leave.
- **Installing a second game in one session no longer hangs on Android.**
- **Achievement unlock toasts appear on Android,** and secret achievements no
  longer spoil themselves on Windows.

> **Repatch your games after updating** — there's a button on each game's page.
> Games patched by an older version won't launch until you do.

Full notes for this release are in
[`Docs/ReleaseNotes/0.1.02.md`](Docs/ReleaseNotes/0.1.02.md); earlier ones sit
beside it, and older changes are on the
[Update History wiki page](https://github.com/Bubbleshum/WPR/wiki/Update-History).

> 🚧 **Coming in 0.1.03** — games can vibrate your phone on Android, with a single
> on/off switch in Settings covering every game; and tilt now works in portrait
> games like Doodle Jump that never responded to it at all. It is on `main` but
> **not yet released**, so it is not on the Releases page and not in 0.1.02. See
> [`Docs/ReleaseNotes/0.1.03.md`](Docs/ReleaseNotes/0.1.03.md) for what is queued.

## Features

| | |
| --- | --- |
| 🎮 **Runs the original games** | Unmodified Windows Phone 7/8 game files — no patched or repacked copies needed |
| 🏆 **Xbox achievements** | 277 games ship with full achievement catalogues; unlocks are saved between sessions, with a pop-up when you earn one |
| 🖥️ **Windows and Android** | Runs on a desktop PC and on an Android phone or tablet |
| 🗂️ **A real library** | Box art, publisher, search, and install / repatch / uninstall per game |
| 👤 **A gamer profile** | Set a gamertag, a gamer picture and an accent colour — games that ask for them get real answers instead of blanks |
| ⌨️ **Tilt on a keyboard** | Bind the four tilt directions to keys, with adjustable strength, a live preview and an optional in-game overlay |
| 🕹️ **Several game types** | XNA games (the main path), Silverlight games (experimental), GameMaker exports, and a rail for launching rebuilt native ports |
| 📝 **Diagnostics that help** | A per-game log written on every launch, so problems can actually be reported |
| 📦 **Nothing else to install** | The Windows installer bundles the .NET runtime and every native library the games need |

## Getting WPR

### Download

If a release has been published, the
[Releases page](https://github.com/Bubbleshum/WPR/releases) has:

- `WPR-Setup-<version>.exe` — Windows installer, 64-bit. Everything is bundled;
  you don't need to install anything else first.
- `WPR-<version>.apk` — Android, for sideloading.

You'll need **Windows 10 (version 1809) or newer**, or **Android 5.0 or newer**.

### Build it yourself

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) —
that's it for the desktop app. Then:

```bash
git clone https://github.com/Bubbleshum/WPR.git
cd WPR
dotnet build Src/Platforms/WPR.Platform.Windows/WPR.Platform.Windows.csproj -c Debug
```

The app appears at
`Src/Platforms/WPR.Platform.Windows/bin/Debug/net8.0-windows10.0.17763.0/WPR.Platform.Windows.exe`.

If you'd rather have a packaged build, there's a script that does it in one go:

```powershell
.\build-desktop.ps1 -Configuration Debug -Run
```

Working in an IDE? Open `Src/WPR.Windows.slnf` rather than the full solution — it
leaves out the Android-only projects, which need extra tooling.

Building the **Android** app needs more setup (the .NET Android workload, a JDK
and the Android SDK). That, and everything else about building, is in
[Docs/BUILDING.md](Docs/BUILDING.md).

### Adding a game

WPR doesn't come with any games — you supply your own. On Windows, point it at a
folder of game files and it lists what it finds; on Android, use **add game** and
pick the file. Either way, **Install** unpacks and prepares the game once, and
after that it's in your library.

## Which games work?

There's a searchable, sortable
**[compatibility list](https://bubbleshum.github.io/WPR/)** with box art, showing
what's known to run and what isn't.

Broadly: XNA games are the best-supported and most work. Silverlight games are
experimental and hit-and-miss. Unity games can't be run directly at all — they
need a one-off rebuild first, and only a couple exist. Later Windows Phone 8 apps
written in C++ aren't supported and won't be.

## Things to know

- **It's early, and it's mid-rebuild.** The `main` branch isn't guaranteed to
  build or run cleanly at any given moment — a large internal reorganisation is
  still in progress.
- **When a game fails, it's usually a missing piece rather than a broken app.**
  Each game leans on a slightly different set of phone features, and the ones it
  needs may not be reimplemented yet.
- **Android trails the desktop.** It builds and runs, but far fewer games have
  been tried there.
- **If you update WPR and a game suddenly stops working,** reinstall that game
  from inside WPR. Games are prepared once when installed, so a change to how
  that preparation works doesn't reach a game that's already set up.
- **No support is offered.** This is a spare-time project, shared as-is.

## Documentation

| Where | What's in it |
| --- | --- |
| [Docs/](Docs/README.md) | Technical reference — [how it works](Docs/ARCHITECTURE.md), [building](Docs/BUILDING.md), [releasing](Docs/RELEASING.md) |
| [Plans/](Plans/README.md) | Design work in progress — the architecture migration, stage scopes, feasibility studies, TODO lists |
| [CLAUDE.md](CLAUDE.md) | Working conventions and gotchas for anyone editing the code |

## Credits

This is a fork of the original [WPR](https://github.com/8212369/WPR), heavily
rebuilt for modern .NET.

- [mediaexplorer74/WPR](https://github.com/mediaexplorer74/WPR) — the fork this
  one is based on; foundational Avalonia port work, Android groundwork, and the
  long-running R&D that made everything downstream possible
- [Tyler Jaacks](https://github.com/TylerJaacks) — .NET 5/6 → .NET 8 upgrade
- [Hector47](https://github.com/Hector47) — online services groundwork
- [fallaciousreasoning](https://github.com/fallaciousreasoning) - fixing the android build

Related forks worth a look:
[TylerJaacks/WPR](https://github.com/TylerJaacks/WPR) (branches `net8_upgrade`,
`dotnet_upgrade`), [Hector47/WPR](https://github.com/Hector47/WPR) (GameServices
ideas), and
[yangzhongke/Windows-Phone-Emulator](https://github.com/yangzhongke/Windows-Phone-Emulator)
(Silverlight-era reference implementations of the Windows Phone controls).

## Licence

[MIT](LICENSE). Provided as-is, with no warranty and no support.

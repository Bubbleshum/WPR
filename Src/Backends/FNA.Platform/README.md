# FNA.Platform — the FNA backend's platform layer

A vendored, WPR-modified fork of [FNA](https://github.com/FNA-XNA/FNA). It builds to
**`FNA.dll`** via `FNA.Core.csproj`.

It used to live at `Src/ThirdParty/fna`. It moved here after Stage 5c because that name had
stopped being true: most of the XNA type system is now WPR-owned, and what is left is either
WPR-authored or a native binding — none of it is third-party code we merely consume.
`Src/ThirdParty` still exists for genuinely-vendored dependencies (Icons.Avalonia,
assembly-store-reader).

## Three names, on purpose

| Thing | Name | Why |
|---|---|---|
| Directory | `FNA.Platform` | What it *is*: the FNA backend's platform layer |
| Project | `FNA.Core.csproj` | Upstream's .NET Core project file, kept for diffability |
| **Assembly** | **`FNA`** | **Load-bearing — do not rename** |

The assembly name is depended on by name in three places, and changing it breaks all of them
silently or at runtime:
- `WPR.Framework.Xna`'s `[InternalsVisibleTo("FNA")]`, which is how the spine here still writes
  into the moved XNA types' internals (`Keyboard.keys`, `Mouse.INTERNAL_*`, `TouchPanel`'s touch
  event entry points, `MediaPlayer.PumpUpdate`, …).
- `FNADllMap`, which registers the native `DllImport` resolver **keyed on the declaring
  assembly** — this is why the backend adapters call into the bindings here rather than
  re-declaring the P/Invokes on their own side.
- `ApplicationPatcher`'s `FNARef`, which rescopes games' remaining XNA typerefs here.

## What is actually left in `src/`

After Stage 5c (see `Docs/STAGE5C-SCOPE.md`) this is down to the **spine** plus glue:

- **Game loop + components** — `Game`, `GameComponent`, `DrawableGameComponent`,
  `GameServiceContainer`
- **Window + device selection** — `GameWindow`, `FNAPlatform/FNAWindow`,
  `GraphicsDeviceManager`, `GraphicsDeviceInformation`, `PreparingDeviceSettingsEventArgs`
- **Platform layer** — `FNAPlatform/FNAPlatform` (the delegate table),
  `FNAPlatform/SDL2_FNAPlatform` (the SDL event pump), `TitleLocation`, `FNALoggerEXT`
- **Native binding** — `Graphics/FNA3D.cs`
- **WPR-authored** — `WprActivationGuard`, `WprGameThread`
- **Build glue** — `Utilities/{FNADllMap,AssemblyHelper,XamarinHelper}`, `Properties/AssemblyInfo`

Everything else — Graphics, Audio, XACT, Media, Content, Input, Storage — is in
`Src/Core/WPR.Framework.Xna` and reaches the platform through the seams in `WPR.Xna.Rhi`,
which `Src/Backends/WPR.Backend.FNA` implements against this assembly.

**Direction of travel:** only *pull* operations cross a seam. Event delivery still flows from
`SDL2_FNAPlatform`'s pump straight into the moved types' internals via the IVT above. Keep it
that way — it is what let the spine keep compiling unchanged through every 5c sub-stage.

## `lib/`

Vendored copies of SDL2-CS, FAudio, FNA3D and Theorafile. Upstream ships these as git
submodules; here they are **committed as plain files** (there is deliberately no `.gitmodules`
— it described submodules that no longer exist and only caused confusion).

Only three files are compiled — the C# bindings:

    lib/SDL2-CS/src/SDL2.cs
    lib/FAudio/csharp/FAudio.cs
    lib/Theorafile/csharp/Theorafile.cs

`FAudio.cs` in particular carries local WPR edits, so treat it as forked, not vendored.

The native C/C++ sources are kept for the ability to rebuild a native (patching FAudio for a
WP7 quirk, adding an Android ABI), but **nothing in this repo builds them**. The shipped
binaries are prebuilt: Android `.so`s under `Src/UI/WPR.UI.Android/Libraries/<abi>/`, desktop
`.dll`s beside `Src/UI/WPR.UI.Desktop`. `lib/FNA3D` contributes no compile units at all — its
C# binding is the checked-in `src/Graphics/FNA3D.cs`.

## Building

Needs `-p:Platform=x64`:

```bash
dotnet build Src/Backends/FNA.Platform/FNA.Core.csproj -c Debug -p:Platform=x64 -maxcpucount:1 -nodeReuse:false --nologo
```

`Directory.Build.props` redirects this project's intermediates to `obj_core/` so they do not
collide with upstream's `obj/`.

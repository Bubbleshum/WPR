# SDL2 headers (vendored)

Headers only, from the official release:

- **Version:** 2.24.0
- **Source:** https://github.com/libsdl-org/SDL/releases/download/release-2.24.0/SDL2-2.24.0.tar.gz
- **sha256:** `91e4c34b1768f92d399b078e171448c6af18cafda743987ed2064a28954d6d97`
- **Taken from the tarball:** `include/` and `LICENSE.txt`. Nothing else.
- **Licence:** zlib (see LICENSE.txt).

## Why only headers

These exist so **FNA3D can be built from the vendored source** in `../FNA3D`. FNA3D
includes `<SDL.h>`, `<SDL_syswm.h>` and `<SDL_vulkan.h>`, but nothing here builds SDL
itself — we link against the prebuilt libraries already committed at
`Src/Platforms/WPR.Platform.Android/Libraries/<abi>/libSDL2.so` and
`Src/Platforms/WPR.Platform.Windows/SDL2.dll`.

## Why 2.24.0 specifically

`SDL2.dll` in the Windows head reports file version 2.24.0. The Android `.so` is
stripped of its revision string, but was vendored alongside it. SDL keeps ABI
compatibility across the 2.x series, so matching the known version exactly is the
safe choice rather than tracking latest.

**If the prebuilt SDL binaries are ever updated, update these headers to match.**

## Feeding them to FNA3D's build

FNA3D's `CMakeLists.txt` honours pre-defined `SDL2_INCLUDE_DIRS` / `SDL2_LIBRARIES`
and skips its own `find_package` when both are set - which is what makes a
cross-compile against the committed `.so` work without an SDL2 CMake package:

```
-DSDL2_INCLUDE_DIRS=<repo>/Src/Backends/FNA.Platform/lib/SDL2/include
-DSDL2_LIBRARIES=<repo>/Src/Platforms/WPR.Platform.Android/Libraries/arm64-v8a/libSDL2.so
```

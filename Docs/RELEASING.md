# Publishing a release

[`.github/workflows/release.yml`](../.github/workflows/release.yml) builds both
distributables and attaches them to a GitHub Release. It is **manual dispatch
only** — Actions → *Release* → *Run workflow*.

## Inputs

| Input | Meaning |
| --- | --- |
| `version` | `MAJOR.MINOR.PATCH`, e.g. `0.1.0` (no leading `v`). Becomes the tag `v0.1.0`, the installer version, and the APK `versionName`. A malformed value fails fast, before ~20 minutes of runner time is spent. |
| `android_version_code` | Integer `versionCode`. Blank uses the workflow run number. Must increase between releases or Android refuses the upgrade. |
| `publish_release` | Off = build only, grab the workflow artifacts (useful for a dry run). |
| `prerelease` | Marks the GitHub Release as a pre-release. |

## What it produces

| Artifact | What |
| --- | --- |
| `WPR-Setup-<version>.exe` | Windows x64 — a self-contained publish wrapped in an Inno Setup installer, so users need no .NET install |
| `WPR-<version>.apk` | Android — API 21+, arm64-v8a / x86_64 |

The Windows leg publishes self-contained `win-x64`, stages the pre-made database,
then compiles [`Packaging/windows/WPR.iss`](../Packaging/windows/WPR.iss). The
Android leg runs on Linux with the .NET Android workload and API 34 platform
installed on the runner.


## Where the version number lives

**One place: `<WprVersion>` in [`Src/Directory.Build.props`](../Src/Directory.Build.props).**
It is currently `0.1.0`. Everything else derives from it:

| Consumer | How |
| --- | --- |
| Windows exe | `$(Version)` + `$(InformationalVersion)` (`<WprVersion>-<WprVersionSuffix>`) |
| Windows UI | `AppVersion.Display` reads `InformationalVersion` back at runtime — the window title and About page are **not** hardcoded |
| Android APK | `$(ApplicationDisplayVersion)` → `android:versionName` |
| Release build | `release.yml` overrides `-p:Version` / `-p:ApplicationDisplayVersion` from the `version` input |

So a release does **not** require editing any file — type the version into the workflow. Bump
`WprVersion` anyway so local builds report what you are working towards.

> Two traps this replaced, both of which had already bitten:
> * the version was hardcoded in two XAML files and had drifted — the About page said `0.0.20`
>   while the window title still said `0.0.18`;
> * `AndroidManifest.xml` carried `android:versionName="0.0.18"`, which **silently overrode**
>   `-p:ApplicationDisplayVersion`. Every release APK would have shipped `0.0.18` no matter what
>   was typed in. Verified: passing `0.0.99` produced `versionCode=99` but `versionName=0.0.18`.
>   Do not re-add `versionName` or `versionCode` to that manifest.

## Release notes are generated

The `Generate release notes` step builds the body from `git log` between the previous `v*` tag
and `HEAD` (all history when there is no previous tag), grouped into **Features / Fixes /
Performance / Chores** by [Conventional Commit](https://www.conventionalcommits.org) prefix.

This repo only partly uses those prefixes, so anything unrecognised is collected under **Other
changes** rather than dropped. Two clean-ups keep the output readable, both driven by what the
real history contains:

* placeholder subjects (a dozen commits are literally `.`) are filtered out;
* subjects are capped at 140 characters — one commit has an entire multi-paragraph body as its
  subject, having omitted the blank line after the summary, and would otherwise swallow the notes;
* `Other changes` is capped at 20 entries with an "…and N more" line.

The `release` job checks out with `fetch-depth: 0` — the default shallow fetch would produce an
empty changelog — and checkout runs **before** `download-artifact`, because checkout cleans the
workspace and would otherwise delete the built artifacts.

To get better notes, write commit subjects as `feat: …`, `fix: …` or `chore: …`.

### Curated highlights (optional, recommended for big releases)

A generated commit list is fine for a patch but says little useful about a large release. If
`Docs/ReleaseNotes/<version>.md` exists, the workflow prepends it as a **Highlights** section and
puts the generated list under **All changes** beneath it. If it does not exist, you just get the
generated list — nothing breaks.

So for a significant release: write `Docs/ReleaseNotes/0.2.0.md` before dispatching. See
[`Docs/ReleaseNotes/0.1.0.md`](ReleaseNotes/0.1.0.md) for the shape.

#### Write them as you go, not at the end

The notes file for the *next* release is created as soon as the first thing lands for it and is
added to as work merges — [`Docs/ReleaseNotes/0.1.03.md`](ReleaseNotes/0.1.03.md) is the current
one. Two reasons: reconstructing user-facing notes from `git log` weeks later loses the "why", and
a file on `main` describing what is coming is something people can be pointed at when they ask
whether a fix has shipped.

Such a file carries a **"Coming in `<version>` — not released yet" banner**, and that banner
**must be deleted before dispatching the workflow** — the file is prepended verbatim, so leaving
it in tells readers of the published release that it has not happened. `README.md` carries a
matching banner under its "What's new" section, which has to come down at the same time. Each
in-progress file repeats the full checklist as an HTML comment at the top, along with the other
things to re-check — the "Upgrading?" line in particular, which goes stale the moment anything
bumps `ApplicationPatcher.Version`.


## Android signing

Without a keystore, .NET Android signs with a **debug key generated fresh on each
runner** — so every release gets a different signature and users get *"App not
installed"* when updating over a previous version. The workflow emits a warning in
the run log when the secrets are absent.

To fix that, create a key once:

```bash
keytool -genkeypair -v -keystore wpr.keystore -alias wpr -keyalg RSA -keysize 2048 -validity 10000
```

Then add four repository secrets (Settings → Secrets and variables → Actions):

| Secret | Value |
| --- | --- |
| `ANDROID_KEYSTORE_BASE64` | the keystore file as base64 — `base64 -w0 wpr.keystore` |
| `ANDROID_KEYSTORE_PASSWORD` | store password |
| `ANDROID_KEY_ALIAS` | `wpr`, or whatever alias you used |
| `ANDROID_KEY_PASSWORD` | key password |

The workflow picks them up automatically.

> **Keep the keystore file backed up.** Losing it means never being able to ship
> an in-place upgrade again — every future release would have to be installed as
> a fresh app.

For a locally-built release APK, the equivalent is `AndroidKeyStore=true` plus
`AndroidSigningKeyStore` / `AndroidSigningKeyAlias` / `AndroidSigningStorePass` /
`AndroidSigningKeyPass`.

<#
.SYNOPSIS
    Builds the WPR Android app (WPR.Platform.Android) and drops the signed APK into Artifacts\.

.DESCRIPTION
    Windows counterpart of build-android.sh. Wraps the CLI recipe documented in
    CLAUDE.md, and auto-detects the tooling that recipe hardcodes:

      * ANDROID_HOME  - picks the first SDK root that actually has platforms\android-34.
                        The system SDK at "Program Files (x86)\Android\android-sdk" only
                        ships API 35/36, so the user-local %LOCALAPPDATA%\Android\Sdk is
                        normally the one that works.
      * JAVA_HOME     - Android Studio's bundled JBR, else an installed JDK 17.
      * dotnet        - the system install; repo global.json pins the SDK 8.0 band, which is
                        what supplies the net8.0-android34.0 ref packs.

    net8.0-android* maps to API 34 only (see CLAUDE.md) - do not bump the TFM without
    also moving to .NET 10 + Avalonia 12.

.PARAMETER Configuration
    Release (default) or Debug. A plain Release `build` stops short of packaging, so the
    script adds -t:SignAndroidPackage to still get an installable APK. It is signed with
    the local debug keystore unless the project is given a real one (AndroidKeyStore=true
    + AndroidSigningKeyStore/Alias/Pass) - fine for sideloading, not for Play upload.

.PARAMETER TargetFramework
    Default net8.0-android34.0.

.PARAMETER OutputDir
    Where to copy the APK. Default: Artifacts\android\<Configuration>. Split by
    configuration because the APK filename is derived from the package name, so a
    Debug and a Release build would otherwise overwrite each other.

.PARAMETER Clean
    Delete the project's bin/obj for this configuration before building.

.PARAMETER Install
    Run `adb install -r` on the produced APK (needs a connected device/emulator).

.EXAMPLE
    .\build-android.ps1
.EXAMPLE
    .\build-android.ps1 -Install
.EXAMPLE
    .\build-android.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$TargetFramework = 'net8.0-android34.0',
    [string]$OutputDir,
    [switch]$Clean,
    [switch]$Install
)

$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project = Join-Path $Root 'Src\Platforms\WPR.Platform.Android\WPR.Platform.Android.csproj'

if (-not (Test-Path $Project)) {
    throw "Project not found: $Project"
}

# Passed as an MSBuild *global* property so it reaches every transitive ProjectReference,
# mirroring what Rider/VS do from the .sln. Src\Directory.Build.props defines SolutionDir
# too, but Src\Backends\FNA.Platform\Directory.Build.props shadows it (nearest-wins, and it
# doesn't import the parent), so FNA.Core.csproj would fail to resolve
# $(SolutionDir)Core\WPR.Framework.Xna and cascade into CS0246 on every XNA type.
# Forward slashes deliberately: a trailing backslash would escape the closing quote.
$SolutionDir = ((Join-Path $Root 'Src') -replace '\\', '/') + '/'

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $Root "Artifacts\android\$Configuration"
}

# API level implied by the TFM, e.g. net8.0-android34.0 -> 34
$apiLevel = '34'
if ($TargetFramework -match 'android(\d+)') { $apiLevel = $Matches[1] }

# --- locate the Android SDK ---------------------------------------------------
$sdkCandidates = @(
    $env:ANDROID_HOME
    $env:ANDROID_SDK_ROOT
    (Join-Path $env:LOCALAPPDATA 'Android\Sdk')
    (Join-Path ${env:ProgramFiles(x86)} 'Android\android-sdk')
    (Join-Path $env:ProgramFiles 'Android\android-sdk')
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

$AndroidHome = $null
foreach ($candidate in $sdkCandidates) {
    if (Test-Path (Join-Path $candidate "platforms\android-$apiLevel")) {
        $AndroidHome = $candidate
        break
    }
}
if ($null -eq $AndroidHome) {
    $tried = $sdkCandidates -join "`n  "
    throw "No Android SDK with platforms\android-$apiLevel found. Tried:`n  $tried`nInstall API $apiLevel via Android Studio's SDK Manager, or set ANDROID_HOME."
}

# --- locate a JDK -------------------------------------------------------------
$jdkCandidates = @(
    $env:JAVA_HOME
    (Join-Path $env:ProgramFiles 'Android\Android Studio\jbr')
    (Join-Path $env:ProgramFiles 'Microsoft\jdk-17.0.13.11-hotspot')
    (Join-Path $env:ProgramFiles 'Eclipse Adoptium\jdk-17.0.13.11-hotspot')
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

$JavaHome = $null
foreach ($candidate in $jdkCandidates) {
    if (Test-Path (Join-Path $candidate 'bin\java.exe')) {
        $JavaHome = $candidate
        break
    }
}
if ($null -eq $JavaHome) {
    # Last resort: any Microsoft/Adoptium JDK 17+ under Program Files.
    $found = Get-ChildItem -Path (Join-Path $env:ProgramFiles '*\jdk-1*') -Directory -ErrorAction SilentlyContinue |
             Where-Object { Test-Path (Join-Path $_.FullName 'bin\java.exe') } |
             Select-Object -First 1
    if ($null -ne $found) { $JavaHome = $found.FullName }
}
if ($null -eq $JavaHome) {
    throw 'No JDK found. Install Android Studio (bundled JBR) or a JDK 17, or set JAVA_HOME.'
}

# --- locate dotnet ------------------------------------------------------------
$Dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (-not (Test-Path $Dotnet)) {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $cmd) { throw 'dotnet not found. Install the .NET 8 SDK or add dotnet to PATH.' }
    $Dotnet = $cmd.Source
}

$env:ANDROID_HOME     = $AndroidHome
$env:ANDROID_SDK_ROOT = $AndroidHome
$env:JAVA_HOME        = $JavaHome

Push-Location $Root
try {
    $sdkVersion = & $Dotnet --version
    Write-Host "SDK           : $sdkVersion  ($Dotnet)" -ForegroundColor DarkGray
    if ($sdkVersion -notlike '8.*') {
        Write-Warning "Resolved SDK is $sdkVersion, not 8.x. net8.0-android$apiLevel.0 ref packs only ship with the .NET 8 android workload - expect CS0234 on Android.* namespaces."
    }
    Write-Host "ANDROID_HOME  : $AndroidHome"
    Write-Host "JAVA_HOME     : $JavaHome"
    Write-Host "Configuration : $Configuration"
    Write-Host "Framework     : $TargetFramework"
    Write-Host "Output        : $OutputDir"
    Write-Host ''

    if ($Clean) {
        foreach ($dir in @('bin', 'obj')) {
            $path = Join-Path $Root "Src\Platforms\WPR.Platform.Android\$dir\$Configuration"
            if (Test-Path $path) {
                Write-Host "Cleaning $path ..." -ForegroundColor DarkGray
                Remove-Item -Recurse -Force $path
            }
        }
    }

    $buildArgs = @(
        $Project
        '-c', $Configuration
        '-f', $TargetFramework
        '-maxcpucount:1'
        '-nodeReuse:false'
        '--nologo'
        "-p:SolutionDir=$SolutionDir"
        # Src\Directory.Build.targets drops the net8.0-android TFM when it cannot
        # find the workload install marker. This script IS the android build, so
        # force the leg on and let MSBuild raise the real workload error if absent.
        '-p:IncludeAndroidTargets=true'
        "-p:AndroidSdkDirectory=$AndroidHome"
        # Embed assemblies so an adb-installed APK runs without VS fast-deploy staging.
        '-p:EmbedAssembliesIntoApk=true'
        '-p:AndroidEnableAssemblyCompression=false'
    )
    if ($Configuration -eq 'Release') {
        # A plain Release `build` stops short of packaging/signing.
        $buildArgs += '-t:SignAndroidPackage'
    }

    Write-Host 'Building...' -ForegroundColor Cyan
    & $Dotnet build @buildArgs

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code $LASTEXITCODE"
    }

    $binDir = Join-Path $Root "Src\Platforms\WPR.Platform.Android\bin\$Configuration\$TargetFramework"
    $apk = Get-ChildItem -Path $binDir -Filter '*-Signed.apk' -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $apk) {
        $apk = Get-ChildItem -Path $binDir -Filter '*.apk' -ErrorAction SilentlyContinue |
               Sort-Object LastWriteTime -Descending | Select-Object -First 1
    }
    if ($null -eq $apk) {
        throw "Build reported success but no .apk was found in $binDir"
    }

    if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null }
    $dest = Join-Path $OutputDir $apk.Name
    Copy-Item -Path $apk.FullName -Destination $dest -Force

    $sizeMb = [math]::Round($apk.Length / 1MB, 1)
    Write-Host ''
    Write-Host 'BUILD OK' -ForegroundColor Green
    Write-Host "  apk  : $dest ($sizeMb MB)"

    if ($Install) {
        $adb = Join-Path $AndroidHome 'platform-tools\adb.exe'
        if (-not (Test-Path $adb)) { throw "adb not found at $adb" }
        Write-Host ''
        Write-Host 'Installing to connected device...' -ForegroundColor Cyan
        & $adb install -r $dest
        if ($LASTEXITCODE -ne 0) { throw "adb install failed with code $LASTEXITCODE" }
        Write-Host 'Installed.' -ForegroundColor Green
    }
}
finally {
    Pop-Location
}

<#
.SYNOPSIS
    Builds the WPR desktop app (WPR.UI.Desktop) into a runnable folder under Artifacts\.

.DESCRIPTION
    Wraps the CLI build recipe documented in CLAUDE.md:
      * explicit TFM so the broken Android leg is never touched
      * -maxcpucount:1 -nodeReuse:false to dodge the MSBuild CS0006 race
      * uses the system .NET at "C:\Program Files\dotnet" (repo global.json pins 8.0.421)

    By default it runs `dotnet publish` into Artifacts\desktop\<Configuration>, then
    copies Src\Database\** alongside the exe. That copy is needed because the csproj's
    "Copy pre-made database" target is AfterTargets="Build" and writes to $(OutputPath),
    so it never reaches the publish folder on its own.

.PARAMETER Configuration
    Release (default) or Debug. Release drops the Avalonia.Diagnostics package
    (the csproj conditions it to Debug only).

.PARAMETER OutputDir
    Where to place the publish output. Default: Artifacts\desktop\<Configuration>.

.PARAMETER SelfContained
    Bundle the .NET runtime (win-x64). Much larger output, runs without .NET installed.

.PARAMETER NoPublish
    Only `dotnet build`; leaves output in Src\UI\WPR.UI.Desktop\bin\<Configuration>\<tfm>.

.PARAMETER Clean
    Delete the output directory before building.

.PARAMETER Run
    Launch the produced exe when the build succeeds.

.EXAMPLE
    .\build-desktop.ps1
.EXAMPLE
    .\build-desktop.ps1 -Configuration Debug -Run
.EXAMPLE
    .\build-desktop.ps1 -SelfContained -Run
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputDir,
    [switch]$SelfContained,
    [switch]$NoPublish,
    [switch]$Clean,
    [switch]$Run
)

$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project = Join-Path $Root 'Src\UI\WPR.UI.Desktop\WPR.UI.Desktop.csproj'
$DatabaseDir = Join-Path $Root 'Src\Database'
$Tfm = 'net8.0-windows10.0.17763.0'

# Passed as an MSBuild *global* property so it reaches every transitive
# ProjectReference, mirroring what Rider/VS do from the .sln. Src\Directory.Build.props
# defines SolutionDir too, but Src\Backends\FNA.Platform\Directory.Build.props shadows
# it (nearest-wins, and it doesn't import the parent), so FNA.Core.csproj would fail to
# resolve $(SolutionDir)Core\WPR.Framework.Xna and cascade into CS0246 on every XNA type.
# Forward slashes deliberately: a trailing backslash would escape the closing quote.
$SolutionDir = ((Join-Path $Root 'Src') -replace '\\', '/') + '/'

if (-not (Test-Path $Project)) {
    throw "Project not found: $Project"
}

# --- locate dotnet ------------------------------------------------------------
# Prefer the system install: the user-local C:\Users\<u>\.dotnet is documented as
# broken (missing android workload manifest) in CLAUDE.md.
$Dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (-not (Test-Path $Dotnet)) {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $cmd) { throw 'dotnet not found. Install the .NET 8 SDK or add dotnet to PATH.' }
    $Dotnet = $cmd.Source
}

Push-Location $Root
try {
    $sdkVersion = & $Dotnet --version
    Write-Host "SDK           : $sdkVersion  ($Dotnet)" -ForegroundColor DarkGray
    if ($sdkVersion -notlike '8.*') {
        Write-Warning "global.json pins 8.0.421 but the resolved SDK is $sdkVersion. Build may behave unexpectedly."
    }

    if ($NoPublish) {
        $OutputDir = Join-Path $Root "Src\UI\WPR.UI.Desktop\bin\$Configuration\$Tfm"
    }
    elseif ([string]::IsNullOrWhiteSpace($OutputDir)) {
        # Self-contained gets its own folder: `dotnet publish -o` does not clear the
        # target, so publishing both modes into one directory leaves the framework-
        # dependent leftovers behind and the installer would package a hybrid payload.
        $suffix = ''
        if ($SelfContained) { $suffix = '-selfcontained' }
        $OutputDir = Join-Path $Root "Artifacts\desktop\$Configuration$suffix"
    }

    Write-Host "Configuration : $Configuration"
    Write-Host "Framework     : $Tfm"
    Write-Host "Output        : $OutputDir"
    Write-Host ''

    if ($Clean -and (Test-Path $OutputDir)) {
        Write-Host "Cleaning $OutputDir ..." -ForegroundColor DarkGray
        Remove-Item -Recurse -Force $OutputDir
    }

    # Shared MSBuild flags. -maxcpucount:1/-nodeReuse:false avoid the parallel-build
    # "metadata file not found" (CS0006) race this repo hits under default settings.
    $common = @(
        '-c', $Configuration
        '-f', $Tfm
        '-maxcpucount:1'
        '-nodeReuse:false'
        '--nologo'
        "-p:SolutionDir=$SolutionDir"
    )

    if ($NoPublish) {
        Write-Host 'Building...' -ForegroundColor Cyan
        & $Dotnet build $Project @common
    }
    else {
        $publishArgs = $common + @('-o', $OutputDir)
        if ($SelfContained) {
            $publishArgs += @('-r', 'win-x64', '--self-contained', 'true')
        }
        else {
            $publishArgs += @('--self-contained', 'false')
        }
        Write-Host 'Publishing...' -ForegroundColor Cyan
        & $Dotnet publish $Project @publishArgs
    }

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code $LASTEXITCODE"
    }

    # --- stage the pre-made SQLite databases + achievement catalogues ---------
    # The csproj target only handles the build output dir, not the publish dir.
    if (-not $NoPublish) {
        if (Test-Path $DatabaseDir) {
            $dbTarget = Join-Path $OutputDir 'Database'
            Write-Host ''
            Write-Host "Staging Database\ -> $dbTarget" -ForegroundColor Cyan
            if (-not (Test-Path $dbTarget)) { New-Item -ItemType Directory -Path $dbTarget -Force | Out-Null }
            Copy-Item -Path (Join-Path $DatabaseDir '*') -Destination $dbTarget -Recurse -Force
        }
        else {
            Write-Warning "Src\Database not found - skipping database staging."
        }
    }

    $exe = Join-Path $OutputDir 'WPR.UI.Desktop.exe'
    if (-not (Test-Path $exe)) {
        throw "Build reported success but $exe is missing."
    }

    $sizeMb = [math]::Round((Get-ChildItem -Recurse $OutputDir | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
    Write-Host ''
    Write-Host 'BUILD OK' -ForegroundColor Green
    Write-Host "  exe    : $exe"
    Write-Host "  folder : $OutputDir ($sizeMb MB)"

    if ($Run) {
        Write-Host ''
        Write-Host 'Launching...' -ForegroundColor Cyan
        Start-Process -FilePath $exe -WorkingDirectory $OutputDir
    }
}
finally {
    Pop-Location
}

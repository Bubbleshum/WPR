<#
.SYNOPSIS
    Builds and runs the WP8 native probe from Windows.

.DESCRIPTION
    Runs natively when a win-x64 unicorn.dll is present, and falls back to building for
    linux-x64 and running under WSL when it is not. Both produce identical output - the
    rasterised frames from the two are byte-for-byte the same file.

    UnicornEngine.Unicorn 2.1.3 ships natives for linux-x64, linux-arm64, linux-ppc64le and
    osx-x64 - there is no win-x64 unicorn.dll in the package, and nothing on this machine can
    build one (no cmake, no MSVC). Until that DLL is supplied, WSL is the only way to get a
    CPU. Everything else in the probe is portable: the only P/Invoke is the Unicorn binding.

    Drop a win-x64 unicorn.dll (Unicorn 2.x) into this directory and this script will use it
    without being asked, which also means Rider can attach a debugger to it.

.PARAMETER Game
    The unpacked WP8 executable. Defaults to $env:WPR_GAME.

.PARAMETER Budget
    Instruction budget. 3000000000 gets Angry Birds Rio a few hundred frames in.

.PARAMETER Screenshot
    Where to write a PNG of one presented frame.

.PARAMETER Frame
    Which presented frame to photograph. Ignored without -Screenshot.

.PARAMETER Trace
    Comma-separated addresses to log the register file at, e.g. 0x00534E3A,0x00462494.

.PARAMETER Watch
    address:length to log every write into, e.g. 0x60003A0C:16.

.EXAMPLE
    .\run.ps1 -Game C:\wp8\abrio\AngryBirdsRio.exe -Screenshot .\frame.png -Frame 200
#>
[CmdletBinding()]
param(
    [string] $Game = $env:WPR_GAME,
    [long]   $Budget = 3000000000,
    [string] $Screenshot,
    [int]    $Frame = 1,
    [string] $Trace,
    [string] $Watch,
    [int]    $Files = 20,
    [switch] $Desktop
)

$ErrorActionPreference = 'Stop'
$probe = $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($Game)) {
    Write-Host "No game given. Pass -Game <path to the unpacked .exe>, or set WPR_GAME." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "It wants the executable inside an unpacked WP8 XAP - the one next to its"
    Write-Host "assets/ folder, e.g. AngryBirdsRio.exe. A XAP is a zip; unpack it anywhere"
    Write-Host "durable and point at the .exe."
    exit 1
}

if (-not (Test-Path -LiteralPath $Game)) {
    throw "No such file: $Game"
}

$Game = (Resolve-Path -LiteralPath $Game).Path

# ---------------------------------------------------------------------------
# Native Windows, if a unicorn.dll is available
# ---------------------------------------------------------------------------

$nativeDll = Join-Path $probe 'unicorn.dll'
$native = Test-Path -LiteralPath $nativeDll

# ---------------------------------------------------------------------------
# The windowed host. Windows-only by nature, so it needs the native unicorn.dll and there
# is no WSL fallback for it.
# ---------------------------------------------------------------------------

if ($Desktop) {
    $dll = Join-Path $probe 'unicorn.dll'
    if (-not (Test-Path -LiteralPath $dll)) {
        throw "-Desktop needs a win-x64 unicorn.dll in $probe; the console probe can fall back to WSL, a window cannot."
    }

    $project = Join-Path $probe 'Desktop\WPR.Wp8Desktop.csproj'
    dotnet build $project -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }

    $exe = Join-Path $probe 'Desktop\bin\Release\net8.0-windows\WPR.Wp8Desktop.exe'
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $exe $Game
    }
    finally {
        $ErrorActionPreference = $previous
    }

    exit $LASTEXITCODE
}

$environment = @{}
if ($Screenshot) { $environment['WPR_SCREENSHOT'] = "" }   # filled in per mode below
if ($Trace)      { $environment['WPR_TRACE'] = $Trace }
if ($Watch)      { $environment['WPR_WATCH_ADDR'] = $Watch }
if ($Files -ne 20) { $environment['WPR_FILES'] = "$Files" }

if ($native) {
    Write-Host "unicorn.dll found - running natively." -ForegroundColor Green

    $output = Join-Path $probe 'bin\Release\net8.0-win'
    dotnet publish (Join-Path $probe 'WPR.Wp8Probe.csproj') -c Release -r win-x64 `
        --self-contained false -o $output --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }

    Copy-Item -LiteralPath $nativeDll -Destination $output -Force

    if ($Screenshot) { $environment['WPR_SCREENSHOT'] = "$Screenshot`:$Frame" }
    foreach ($key in $environment.Keys) { Set-Item -Path "env:$key" -Value $environment[$key] }

    # Windows PowerShell 5.1 turns anything a native command writes to stderr into a
    # terminating error while ErrorActionPreference is Stop - so a probe that reports a fault
    # on stderr, which is exactly what this one does, looks like the script itself crashed.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & (Join-Path $output 'WPR.Wp8Probe.exe') $Game $Budget
    }
    finally {
        $ErrorActionPreference = $previous
    }

    exit $LASTEXITCODE
}

# ---------------------------------------------------------------------------
# WSL, which is where the only Unicorn native this package ships can run
# ---------------------------------------------------------------------------

Write-Host "No unicorn.dll here - building for linux-x64 and running under WSL." -ForegroundColor Cyan

$publish = Join-Path $probe 'bin\Release\publish-linux'
dotnet publish (Join-Path $probe 'WPR.Wp8Probe.csproj') -c Release -r linux-x64 `
    --self-contained true -o $publish --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

function ConvertTo-WslPath([string] $windowsPath) {
    # Done here rather than by shelling out to wslpath, because wsl.exe eats the backslashes
    # in an argument before wslpath ever sees them, so a rooted path arrives as one run of
    # letters. [char]92 is the separator, spelled that way to survive the same hazard.
    $full = [System.IO.Path]::GetFullPath($windowsPath)
    $drive = $full.Substring(0, 1).ToLowerInvariant()
    $rest = $full.Substring(2).Replace([char]92, '/')
    return "/mnt/$drive$rest"
}

$publishWsl = ConvertTo-WslPath $publish
$gameWsl = ConvertTo-WslPath $Game

# A fixed staging directory inside WSL rather than running from /mnt/c: the launcher has to
# be executable, and a Windows filesystem mount will not reliably carry that bit.
$stage = '~/.wpr-wp8probe'

$shotWsl = ''
$prefix = ''
if ($Screenshot) {
    $shotWsl = '/tmp/wpr-frame.png'
    $prefix += "WPR_SCREENSHOT='$shotWsl`:$Frame' "
}

foreach ($key in $environment.Keys) {
    if ($key -eq 'WPR_SCREENSHOT') { continue }
    $prefix += "$key='$($environment[$key])' "
}

$command = "mkdir -p $stage && cp -rf '$publishWsl/.' $stage/ && chmod +x $stage/WPR.Wp8Probe && " +
           "$prefix$stage/WPR.Wp8Probe '$gameWsl' $Budget"

wsl -e bash -lc $command
$code = $LASTEXITCODE

if ($Screenshot -and $code -eq 0) {
    $shotDirectory = Split-Path -Parent ([System.IO.Path]::GetFullPath($Screenshot))
    if (-not (Test-Path -LiteralPath $shotDirectory)) {
        New-Item -ItemType Directory -Path $shotDirectory -Force | Out-Null
    }

    $destination = ConvertTo-WslPath $Screenshot
    wsl -e bash -lc "cp -f '$shotWsl' '$destination' 2>/dev/null"
    if (Test-Path -LiteralPath $Screenshot) {
        Write-Host "Screenshot: $Screenshot" -ForegroundColor Green
    }
}

exit $code

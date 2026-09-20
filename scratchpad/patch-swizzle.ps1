#requires -Version 5.1
<#
Corrects XNAToVK_SurfaceSwizzle in a prebuilt FNA3D binary.

  entry 1  SurfaceFormat.Bgr565    {B,G,R,ONE} -> IDENTITY
  entry 3  SurfaceFormat.Bgra4444  IDENTITY    -> {G,R,A,B}

The table is located by anchoring on entries 12..17, which are a 96-byte run of
non-zero swizzles (Alpha8 / Single / Vector2 / Vector4 / HalfSingle / HalfVector2)
and therefore unique in the image. Every entry except the two being changed is
verified against the vendored source before anything is written, and the file is
left untouched unless exactly one table is found.

Idempotent: re-running over an already-corrected binary reports "already correct".
#>
param([Parameter(Mandatory)][string[]]$Path, [switch]$WhatIfOnly)

# NB: PowerShell variable names are case-insensitive, so a scan loop using $i would
# silently clobber a constant named $I. Hence $vkI, and $scan/$k for loop counters.
$vkI=0; $Z=1; $O=2; $R=3; $G=4; $B=5; $A=6
$IDENT = @($vkI,$vkI,$vkI,$vkI)
$names = @('Color','Bgr565','Bgra5551','Bgra4444','Dxt1','Dxt3','Dxt5','NormalizedByte2',
           'NormalizedByte4','Rgba1010102','Rg32','Rgba64','Alpha8','Single','Vector2','Vector4',
           'HalfSingle','HalfVector2','HalfVector4','HdrBlendable','ColorBgraEXT','ColorSrgbEXT',
           'Dxt5SrgbEXT','Bc7EXT','Bc7SrgbEXT')

# The 23 entries that must match the vendored source exactly. $null = "don't check"
# (entries 1 and 3, the ones this script owns).
$expect = @(
  @($vkI,$vkI,$vkI,$vkI), $null,        @($vkI,$vkI,$vkI,$vkI), $null,
  @($vkI,$vkI,$vkI,$vkI), @($vkI,$vkI,$vkI,$vkI), @($vkI,$vkI,$vkI,$vkI), @($R,$G,$O,$O),
  @($vkI,$vkI,$vkI,$vkI), @($vkI,$vkI,$vkI,$vkI), @($R,$G,$O,$O), @($vkI,$vkI,$vkI,$vkI),
  @($Z,$Z,$Z,$R), @($R,$O,$O,$O), @($R,$G,$O,$O), @($vkI,$vkI,$vkI,$vkI),
  @($R,$O,$O,$O), @($R,$G,$O,$O), @($vkI,$vkI,$vkI,$vkI), @($vkI,$vkI,$vkI,$vkI),
  @($vkI,$vkI,$vkI,$vkI), @($vkI,$vkI,$vkI,$vkI), @($vkI,$vkI,$vkI,$vkI), @($vkI,$vkI,$vkI,$vkI), @($vkI,$vkI,$vkI,$vkI))

$FIX_565  = @($vkI,$vkI,$vkI,$vkI)   # VK_FORMAT_R5G6B5_UNORM_PACK16 already matches XNA Bgr565
$FIX_4444 = @($G,$R,$A,$B)   # VK_FORMAT_B4G4R4A4_UNORM_PACK16 holding XNA Bgra4444
$BAD_565  = @($B,$G,$R,$O)

function ToBytes([int[]]$e) { $o = New-Object byte[] 16; for ($i=0; $i -lt 4; $i++) { [BitConverter]::GetBytes([uint32]$e[$i]).CopyTo($o, $i*4) }; $o }
function Entry([byte[]]$b, [int]$off, [int]$idx) { @(0..3 | ForEach-Object { [int][BitConverter]::ToUInt32($b, $off + $idx*16 + $_*4) }) }
function Same($a, $b) { -not (Compare-Object $a $b) }

# Anchor = entries 12..17 laid out end to end.
$anchor = @()
12..17 | ForEach-Object { $anchor += ToBytes $expect[$_] }

foreach ($p in $Path) {
    $leaf = Split-Path $p -Leaf
    $rel  = $p -replace [regex]::Escape((Get-Location).Path + '\'), ''
    if (-not (Test-Path $p)) { Write-Output "SKIP     $rel  (not found)"; continue }
    $bytes = [IO.File]::ReadAllBytes($p)

    # locate anchor
    $hits = @()
    for ($scan = 0; $scan -le $bytes.Length - $anchor.Length; $scan++) {
        if ($bytes[$scan] -ne $anchor[0]) { continue }
        $ok = $true
        for ($k = 1; $k -lt $anchor.Length; $k++) { if ($bytes[$scan+$k] -ne $anchor[$k]) { $ok = $false; break } }
        if ($ok) { $hits += ($scan - 12*16) }
    }
    if ($hits.Count -ne 1) { Write-Output "ABORT    $rel  (expected 1 table, found $($hits.Count))"; continue }
    $off = $hits[0]
    if ($off -lt 0 -or $off + 25*16 -gt $bytes.Length) { Write-Output "ABORT    $rel  (table out of bounds)"; continue }

    # verify every entry this script does not own
    $bad = @()
    for ($e = 0; $e -lt 25; $e++) {
        if ($null -eq $expect[$e]) { continue }
        if (-not (Same (Entry $bytes $off $e) $expect[$e])) { $bad += "$e/$($names[$e])" }
    }
    if ($bad.Count) { Write-Output "ABORT    $rel  (unexpected entries: $($bad -join ', '))"; continue }

    $cur565  = Entry $bytes $off 1
    $cur4444 = Entry $bytes $off 3
    $changes = @()
    if (Same $cur565 $BAD_565)   { $changes += 'Bgr565 {B,G,R,ONE}->IDENTITY' }
    elseif (-not (Same $cur565 $FIX_565)) { Write-Output "ABORT    $rel  (Bgr565 is neither the known-bad nor the fixed value)"; continue }
    if (Same $cur4444 $IDENT) { $changes += 'Bgra4444 IDENTITY->{G,R,A,B}' }
    elseif (-not (Same $cur4444 $FIX_4444)) { Write-Output "ABORT    $rel  (Bgra4444 is neither IDENTITY nor the fixed value)"; continue }

    if (-not $changes.Count) { Write-Output ("OK       {0}  table @ 0x{1:X}  already correct" -f $rel, $off); continue }
    if ($WhatIfOnly)        { Write-Output ("WOULD    {0}  table @ 0x{1:X}  {2}" -f $rel, $off, ($changes -join '; ')); continue }

    (ToBytes $FIX_565).CopyTo($bytes,  $off + 1*16)
    (ToBytes $FIX_4444).CopyTo($bytes, $off + 3*16)
    [IO.File]::WriteAllBytes($p, $bytes)

    # read back from disk and confirm
    $v = [IO.File]::ReadAllBytes($p)
    $ok565  = Same (Entry $v $off 1) $FIX_565
    $ok4444 = Same (Entry $v $off 3) $FIX_4444
    Write-Output ("PATCHED  {0}  table @ 0x{1:X}  {2}  [verify 565={3} 4444={4}]" -f $rel, $off, ($changes -join '; '), $ok565, $ok4444)
}

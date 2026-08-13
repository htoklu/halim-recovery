<#
.SYNOPSIS
  Offline recovery regression test. No admin rights required.
.DESCRIPTION
  Uses the VHD fixtures produced by New-DebugImages.ps1 (testlab\vhd\debug_*.vhd):
  extracts each partition to a raw image, runs quick-scan recovery on the image and
  verifies every recovered file against the recorded SHA-256 manifest.
#>
param([string[]]$FileSystems = @('NTFS', 'FAT32', 'exFAT'))

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$workDir = Join-Path $PSScriptRoot 'vhd'
$resultsDir = Join-Path $PSScriptRoot 'results'
$cli = "$root\src\HalimRecovery.Cli\bin\Release\net9.0-windows\HalimRecoveryCli.exe"
if (-not (Test-Path $cli)) { dotnet build "$root\src\HalimRecovery.Cli\HalimRecovery.Cli.csproj" -c Release -v q --nologo | Out-Null }

$summary = @()
foreach ($fs in $FileSystems) {
    $vhd = Join-Path $workDir "debug_$fs.vhd"
    $manifestPath = Join-Path $resultsDir "debug_manifest_$fs.json"
    if (-not (Test-Path $vhd) -or -not (Test-Path $manifestPath)) {
        Write-Host "SKIP $fs (fixture missing - run New-DebugImages.ps1 as admin first)" -ForegroundColor Yellow
        continue
    }

    # Extract partition (MBR partition 1) from the fixed VHD
    $img = Join-Path $workDir "part_$fs.img"
    $in = [System.IO.File]::OpenRead($vhd)
    $mbr = New-Object byte[] 512
    $in.Read($mbr, 0, 512) | Out-Null
    $lba = [BitConverter]::ToUInt32($mbr, 454)
    $sectors = [BitConverter]::ToUInt32($mbr, 458)
    $out = [System.IO.File]::Create($img)
    $in.Position = [int64]$lba * 512
    $buf = New-Object byte[] 4MB
    $remaining = [int64]$sectors * 512
    while ($remaining -gt 0) {
        $n = $in.Read($buf, 0, [Math]::Min($buf.Length, $remaining))
        if ($n -le 0) { break }
        $out.Write($buf, 0, $n)
        $remaining -= $n
    }
    $out.Close(); $in.Close()

    # Recover from the image and verify hashes
    $dest = Join-Path $resultsDir "offline_${fs}_recovery"
    if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
    & $cli recover $img --dest $dest --mode quick --all | Out-Null

    $manifest = Get-Content $manifestPath | ConvertFrom-Json
    $recovered = if (Test-Path $dest) { Get-ChildItem -Recurse -File $dest | Where-Object { $_.Name -notlike 'recovery-report*' } } else { @() }
    $exact = 0; $partial = 0; $failed = 0
    foreach ($p in $manifest.PSObject.Properties) {
        $leaf = Split-Path $p.Name -Leaf
        $m = $recovered | Where-Object { $_.Name -eq $leaf } | Select-Object -First 1
        if (-not $m) { $failed++; Write-Host "  FAILED  $fs $($p.Name)" -ForegroundColor Red }
        elseif ((Get-FileHash -Algorithm SHA256 $m.FullName).Hash.ToLower() -eq $p.Value) { $exact++ }
        else { $partial++; Write-Host "  PARTIAL $fs $($p.Name)" -ForegroundColor Yellow }
    }
    $total = ($manifest.PSObject.Properties | Measure-Object).Count
    $color = if ($failed -eq 0 -and $partial -eq 0) { 'Green' } else { 'Yellow' }
    Write-Host "$fs quick-scan offline: $exact/$total exact, $partial partial, $failed failed" -ForegroundColor $color
    $summary += [pscustomobject]@{ FileSystem = $fs; Total = $total; Exact = $exact; Partial = $partial; Failed = $failed }
    Remove-Item $img -Force
}
$summary | Format-Table -AutoSize
if ($summary | Where-Object { $_.Failed -gt 0 }) { exit 1 } else { exit 0 }

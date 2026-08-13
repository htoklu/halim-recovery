<#
.SYNOPSIS
  HALIM RECOVERY - Controlled recovery test laboratory.
.DESCRIPTION
  For each requested filesystem (NTFS, FAT32, exFAT):
    1. Creates a virtual disk (VHD), formats it
    2. Writes structurally valid test files (JPG/PNG/GIF/PDF/DOCX/XLSX/PPTX/ZIP/MP4/MP3/WAV/TXT)
    3. Records SHA-256 of every file
    4. Permanently deletes the files
    5. Detaches/re-attaches the VHD (flushes all filesystem caches)
    6. Runs Halim Recovery (quick scan; optionally deep scan)
    7. Compares recovered files against original hashes
  Classification: EXACT (hash match) / PARTIAL (recovered but hash differs) / FAILED (not recovered)
.NOTES
  Requires Administrator (raw disk + VHD operations). Only virtual disks are touched.
#>
#Requires -RunAsAdministrator
param(
    [string[]]$FileSystems = @('NTFS', 'FAT32', 'exFAT'),
    [switch]$SkipDeepScan,
    [int]$VhdSizeMB = 256
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$workDir = Join-Path $PSScriptRoot 'vhd'
$resultsDir = Join-Path $PSScriptRoot 'results'
New-Item -ItemType Directory -Force -Path $workDir, $resultsDir | Out-Null
Start-Transcript -Path (Join-Path $resultsDir 'lab-run.log') -Force | Out-Null
trap { Write-Host "FATAL: $_" -ForegroundColor Red; Stop-Transcript | Out-Null; exit 1 }

Write-Host "Building CLI (Release)..." -ForegroundColor Cyan
dotnet build "$root\src\HalimRecovery.Cli\HalimRecovery.Cli.csproj" -c Release -v q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "CLI build failed" }
$cli = "$root\src\HalimRecovery.Cli\bin\Release\net9.0-windows\HalimRecoveryCli.exe"

function Invoke-DiskPart([string]$script) {
    $tmp = [System.IO.Path]::GetTempFileName()
    Set-Content -Path $tmp -Value $script -Encoding ASCII
    $out = diskpart /s $tmp 2>&1 | Out-String
    Remove-Item $tmp -Force
    if ($LASTEXITCODE -ne 0) { throw "diskpart failed:`n$out" }
    return $out
}

function Get-TestVolumeLetter {
    $vol = Get-Volume -FileSystemLabel 'HALIMTEST' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($vol -and $vol.DriveLetter) { return $vol.DriveLetter }
    # Assign a letter if the volume mounted without one.
    $part = Get-Partition | Where-Object { ($_ | Get-Volume -ErrorAction SilentlyContinue).FileSystemLabel -eq 'HALIMTEST' } | Select-Object -First 1
    if ($part) {
        $free = [char[]](90..81) | Where-Object { -not (Test-Path "$($_):\") } | Select-Object -First 1
        $part | Set-Partition -NewDriveLetter $free
        return $free
    }
    throw "HALIMTEST volume not found after attach."
}

$benchmark = @()

foreach ($fs in $FileSystems) {
    Write-Host "`n===== $fs TEST =====" -ForegroundColor Yellow
    $vhd = Join-Path $workDir "test_$fs.vhd"
    if (Test-Path $vhd) { Invoke-DiskPart "select vdisk file=`"$vhd`"`ndetach vdisk" 2>$null | Out-Null; Remove-Item $vhd -Force }

    $fsArg = $fs.ToLower()
    Invoke-DiskPart @"
create vdisk file="$vhd" maximum=$VhdSizeMB type=expandable
select vdisk file="$vhd"
attach vdisk
create partition primary
format fs=$fsArg quick label=HALIMTEST
assign
"@ | Out-Null
    Start-Sleep -Seconds 2
    $letter = Get-TestVolumeLetter
    Write-Host "VHD attached as $letter`:"

    # 1-3: generate test files + manifest of SHA-256 hashes
    $manifestPath = Join-Path $resultsDir "manifest_$fs.json"
    & $cli gen-testfiles "$letter`:\TestData" --manifest $manifestPath | Out-Null
    $manifest = Get-Content $manifestPath | ConvertFrom-Json
    $fileCount = ($manifest.PSObject.Properties | Measure-Object).Count
    Write-Host "Wrote $fileCount test files, hashes recorded."

    # CRITICAL: flush file DATA to disk before deleting. Without this the lazy writer
    # may never physically write the data, and there is nothing on disk to recover.
    Write-VolumeCache -DriveLetter $letter
    Start-Sleep -Seconds 2

    # 4: permanent delete (direct delete, Recycle Bin is not involved)
    Remove-Item -Recurse -Force "$letter`:\TestData"

    # Flush the METADATA updates (deletion markers) to disk as well.
    Write-VolumeCache -DriveLetter $letter
    Start-Sleep -Seconds 2

    # 5: detach + attach for a completely fresh, cache-free view of the volume
    Invoke-DiskPart "select vdisk file=`"$vhd`"`ndetach vdisk" | Out-Null
    Start-Sleep -Seconds 1
    Invoke-DiskPart "select vdisk file=`"$vhd`"`nattach vdisk" | Out-Null
    Start-Sleep -Seconds 2
    $letter = Get-TestVolumeLetter
    Write-Host "Re-attached as $letter`:"

    # 6: quick-scan recovery
    $dest = Join-Path $resultsDir "recovered_${fs}_quick"
    if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
    $cliOut = & $cli recover "$letter" --dest $dest --mode quick --all 2>&1
    $cliOut | ForEach-Object { Write-Host "  CLI: $_" }
    if ($LASTEXITCODE -ne 0) { Write-Host "Quick recovery exited with code $LASTEXITCODE" -ForegroundColor Red }

    # 7: hash comparison (match by file name anywhere under dest)
    $recoveredFiles = if (Test-Path $dest) { Get-ChildItem -Recurse -File $dest | Where-Object { $_.Name -notlike 'recovery-report*' } } else { @() }
    $exact = 0; $partial = 0; $failed = 0; $rows = @()
    foreach ($prop in $manifest.PSObject.Properties) {
        $leaf = Split-Path $prop.Name -Leaf
        $match = $recoveredFiles | Where-Object { $_.Name -eq $leaf } | Select-Object -First 1
        if (-not $match) { $failed++; $rows += "FAILED  $($prop.Name)"; continue }
        $hash = (Get-FileHash -Algorithm SHA256 $match.FullName).Hash.ToLower()
        if ($hash -eq $prop.Value) { $exact++; $rows += "EXACT   $($prop.Name)" }
        else { $partial++; $rows += "PARTIAL $($prop.Name)" }
    }
    Write-Host "QUICK SCAN [$fs]: $exact exact, $partial partial, $failed failed (of $fileCount)" -ForegroundColor Green
    $rows | ForEach-Object { Write-Host "  $_" }
    $benchmark += [pscustomobject]@{ FileSystem = $fs; Mode = 'QuickScan'; Total = $fileCount; Exact = $exact; Partial = $partial; Failed = $failed }

    # Optional deep-scan pass (carving; names are not preserved, match by hash)
    if (-not $SkipDeepScan) {
        $destDeep = Join-Path $resultsDir "recovered_${fs}_deep"
        if (Test-Path $destDeep) { Remove-Item -Recurse -Force $destDeep }
        $cliOut = & $cli recover "$letter" --dest $destDeep --mode deep --all 2>&1
        $cliOut | ForEach-Object { Write-Host "  CLI: $_" }
        $deepFiles = if (Test-Path $destDeep) { Get-ChildItem -Recurse -File $destDeep | Where-Object { $_.Name -notlike 'recovery-report*' } } else { @() }
        $deepHashes = @{}
        foreach ($f in $deepFiles) { $deepHashes[(Get-FileHash -Algorithm SHA256 $f.FullName).Hash.ToLower()] = $f.Name }
        $dExact = 0; $dMissed = 0
        foreach ($prop in $manifest.PSObject.Properties) {
            if ($deepHashes.ContainsKey($prop.Value)) { $dExact++ } else { $dMissed++ }
        }
        Write-Host "DEEP SCAN [$fs]: $dExact exact-hash matches of $fileCount originals ($($deepFiles.Count) files carved)" -ForegroundColor Green
        $benchmark += [pscustomobject]@{ FileSystem = $fs; Mode = 'DeepScan'; Total = $fileCount; Exact = $dExact; Partial = ($deepFiles.Count - $dExact); Failed = $dMissed }
    }

    # cleanup
    Invoke-DiskPart "select vdisk file=`"$vhd`"`ndetach vdisk" | Out-Null
    Remove-Item $vhd -Force
}

# Benchmark dashboard
$benchmark | Format-Table -AutoSize
$benchmark | ConvertTo-Json | Set-Content (Join-Path $resultsDir 'benchmark.json')
$md = "# Halim Recovery - Benchmark Results`n`nDate: $(Get-Date -Format 'yyyy-MM-dd HH:mm')`n`n| Filesystem | Mode | Total | Exact | Partial | Failed |`n|---|---|---|---|---|---|`n"
foreach ($b in $benchmark) { $md += "| $($b.FileSystem) | $($b.Mode) | $($b.Total) | $($b.Exact) | $($b.Partial) | $($b.Failed) |`n" }
$md | Set-Content (Join-Path $resultsDir 'benchmark.md')
Write-Host "`nBenchmark written to testlab\results\benchmark.md" -ForegroundColor Cyan
Stop-Transcript | Out-Null

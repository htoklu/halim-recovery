<#
.SYNOPSIS
  Creates persistent VHD fixtures (NTFS/FAT32/exFAT) containing DELETED test files,
  for offline parser debugging and regression testing without admin rights.
  The VHDs stay in testlab\vhd\ after this script finishes (they are gitignored).
#>
#Requires -RunAsAdministrator
param([int]$VhdSizeMB = 128)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$workDir = Join-Path $PSScriptRoot 'vhd'
$resultsDir = Join-Path $PSScriptRoot 'results'
New-Item -ItemType Directory -Force -Path $workDir, $resultsDir | Out-Null
Start-Transcript -Path (Join-Path $resultsDir 'debug-images.log') -Force | Out-Null
trap { Write-Host "FATAL: $_" -ForegroundColor Red; Stop-Transcript | Out-Null; exit 1 }

$cli = "$root\src\HalimRecovery.Cli\bin\Release\net9.0-windows\HalimRecoveryCli.exe"
if (-not (Test-Path $cli)) {
    dotnet build "$root\src\HalimRecovery.Cli\HalimRecovery.Cli.csproj" -c Release -v q --nologo | Out-Null
}

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
    throw "HALIMTEST volume not found."
}

foreach ($fs in @('NTFS', 'FAT32', 'exFAT')) {
    Write-Host "Creating $fs debug image..."
    $vhd = Join-Path $workDir "debug_$fs.vhd"
    if (Test-Path $vhd) { try { Invoke-DiskPart "select vdisk file=`"$vhd`"`ndetach vdisk" | Out-Null } catch {}; Remove-Item $vhd -Force }

    Invoke-DiskPart @"
create vdisk file="$vhd" maximum=$VhdSizeMB type=fixed
select vdisk file="$vhd"
attach vdisk
create partition primary
format fs=$($fs.ToLower()) quick label=HALIMTEST
assign
"@ | Out-Null
    Start-Sleep -Seconds 2
    $letter = Get-TestVolumeLetter

    & $cli gen-testfiles "$letter`:\TestData" --manifest (Join-Path $resultsDir "debug_manifest_$fs.json") | Out-Null
    Write-VolumeCache -DriveLetter $letter
    Start-Sleep -Seconds 2
    Remove-Item -Recurse -Force "$letter`:\TestData"
    Write-VolumeCache -DriveLetter $letter
    Start-Sleep -Seconds 2
    Invoke-DiskPart "select vdisk file=`"$vhd`"`ndetach vdisk" | Out-Null
    Write-Host "$fs debug image ready: $vhd"
}

Write-Host "All debug images created."
Stop-Transcript | Out-Null

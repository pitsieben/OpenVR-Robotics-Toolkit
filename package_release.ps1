<#
  Builds everything and packs a ready-to-use release ZIP:
    dist\OpenVR-Robotics-Toolkit-<Version>.zip

  Usage (PowerShell, from the repo folder):
    .\package_release.ps1                    # version = today's date, e.g. 2026.11.17
    .\package_release.ps1 -Version 1.2.0
    .\package_release.ps1 -SkipBuild         # just pack the files that are already built

  The ZIP contains only compiled files, scripts, the template and docs (no source code, never a token).
  Users extract it, run copy_gha_to_grasshopper.bat and follow README.md.
#>
param(
    [string]$Version = (Get-Date -Format "yyyy.MM.dd"),
    [switch]$SkipBuild
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
Set-Location $root

function Step($msg) { Write-Host "`n== $msg" -ForegroundColor Cyan }

if (-not $SkipBuild) {
    Step "Building Grasshopper plugin"
    dotnet build -c Release "ViveGrasshopperRecorder\ViveGrasshopperRecorder.csproj"
    if ($LASTEXITCODE) { throw "Plugin build failed." }
    Copy-Item "ViveGrasshopperRecorder\bin\Release\net7.0-windows\ViveTcpRecorderV2.gha" "ViveTcpRecorderV2.gha" -Force

    $apps = @(
        @{ Name = "ViveTcpPublisher";  Proj = "ViveTcpPublisher\ViveTcpPublisher.csproj" },
        @{ Name = "ClayViewer";        Proj = "ClayViewer\ClayViewer.csproj" },
        @{ Name = "ViveExchangeSetup"; Proj = "ViveExchangeSetup\ViveExchangeSetup.csproj" }
    )
    foreach ($a in $apps) {
        Step "Publishing $($a.Name)"
        # publish into dist\_build so a running copy of the program can't lock the output
        $out = Join-Path $root "dist\_build\$($a.Name)"
        if (Test-Path $out) { Remove-Item $out -Recurse -Force }
        dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $out $a.Proj
        if ($LASTEXITCODE) { throw "$($a.Name) publish failed." }
        # refresh the repo copy too, unless the program is running
        $dest = Join-Path $root "$($a.Name)\publish-win-x64"
        try { Get-ChildItem $out -File | Where-Object { $_.Extension -ne ".pdb" } | Copy-Item -Destination $dest -Force -ErrorAction Stop }
        catch { Write-Host "  ($($a.Name) is running: repo copy in $dest not refreshed, the ZIP still gets the new build)" -ForegroundColor Yellow }
    }

    Step "Clay Viewer self-test"
    & "dist\_build\ClayViewer\ClayViewer.exe" --selftest | Select-Object -Last 3
}

# ---------------------------------------------------------------- stage
$name  = "OpenVR-Robotics-Toolkit-$Version"
$dist  = Join-Path $root "dist"
$stage = Join-Path $dist $name
Step "Staging $name"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

# relative path -> required?
$files = [ordered]@{
    "ViveTcpRecorderV2.gha"                                    = $true
    "copy_gha_to_grasshopper.bat"                              = $true
    "README.md"                                                = $true
    "Grasshopper\ViveTracking_Template.gh"                     = $true
    "ViveTcpPublisher\publish-win-x64\ViveTcpPublisher.exe"    = $true
    "ViveTcpPublisher\publish-win-x64\Launch_ViveTcpPublisher.bat" = $true
    "ViveTcpPublisher\publish-win-x64\ViveTcpPublisher_logo.png"   = $false
    "ViveTcpPublisher\README.md"                               = $false
    "ViveTcpPublisher\THIRD_PARTY_NOTICES.md"                  = $false
    "ClayViewer\publish-win-x64\ClayViewer.exe"                = $true
    "ClayViewer\publish-win-x64\cimgui.dll"                    = $true
    "ClayViewer\publish-win-x64\glfw3.dll"                     = $true
    "ViveExchangeSetup\publish-win-x64\ViveExchangeSetup.exe"  = $true
    "docs\PLANE_EXCHANGE.md"                                   = $true
    "docs\images\viewer_overview.png"                          = $false
    "docs\images\viewer_take.png"                              = $false
    "set_exchange_token.ps1"                                   = $true
    "enable_headless_trackers.ps1"                             = $true
    "restore_steamvr_settings.ps1"                             = $true
    "dummy_sender.py"                                          = $true
    "run_dummy_sender.bat"                                     = $true
    "run_two_dummy_senders.bat"                                = $true
}
# any extra native dlls the publisher needs (e.g. openvr_api.dll)
Get-ChildItem "ViveTcpPublisher\publish-win-x64\*.dll" -ErrorAction SilentlyContinue | ForEach-Object {
    $files["ViveTcpPublisher\publish-win-x64\$($_.Name)"] = $true
}

$missing = @()
foreach ($rel in $files.Keys) {
    $src = Join-Path $root $rel
    if ($rel -match '^(ViveTcpPublisher|ClayViewer|ViveExchangeSetup)\\publish-win-x64\\(.+)$') {
        $fresh = Join-Path $root "dist\_build\$($Matches[1])\$($Matches[2])"
        if (Test-Path $fresh) { $src = $fresh }
    }
    if (-not (Test-Path $src)) {
        if ($files[$rel]) { $missing += $rel } else { Write-Host "  (optional, skipped) $rel" -ForegroundColor DarkGray }
        continue
    }
    $dst = Join-Path $stage $rel
    New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
    Copy-Item $src $dst -Force
}
if ($missing) { throw "Missing required files (run without -SkipBuild):`n  " + ($missing -join "`n  ") }

# safety: never ship a token
$leak = Get-ChildItem $stage -Recurse -Force | Where-Object { $_.Name -in "token.dat", "token.txt" -or $_.FullName -match "\\ViveExchange\\" }
if ($leak) { throw "Token file found in the package: $($leak.FullName -join ', ')" }

$commit = (git rev-parse --short HEAD 2>$null)
$dirty  = if (git status --porcelain 2>$null) { " (+ uncommitted changes)" } else { "" }
@"
OpenVR Robotics Toolkit $Version
Built:  $(Get-Date -Format "yyyy-MM-dd HH:mm")
Commit: $commit$dirty

Install:
  1. Right-click the ZIP > Properties > Unblock, then extract it.
  2. Close Rhino and double-click copy_gha_to_grasshopper.bat.
  3. Follow README.md, Part A.
"@ | Set-Content (Join-Path $stage "VERSION.txt") -Encoding utf8

# ---------------------------------------------------------------- zip
$zip = Join-Path $dist "$name.zip"
Step "Zipping"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "`nDone: $zip ($mb MB)" -ForegroundColor Green

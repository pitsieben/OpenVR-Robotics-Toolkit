# Script to configure SteamVR to track HTC Vive Trackers WITHOUT an HMD (Headset)
# This enables the virtual "null" driver and disables the headset requirement.

$ErrorActionPreference = "Stop"

$steamvrNullSettings = "C:\Program Files (x86)\Steam\steamapps\common\SteamVR\drivers\null\resources\settings\default.vrsettings"
$steamvrUserSettings = "C:\Program Files (x86)\Steam\config\steamvr.vrsettings"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  SteamVR Headless Tracker Configuration Tool" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Check if SteamVR is running
$vrProcesses = Get-Process -Name "vrmonitor", "vrserver", "vrcompositor" -ErrorAction SilentlyContinue
if ($vrProcesses) {
    Write-Host "`n[!] SteamVR is currently running." -ForegroundColor Yellow
    Write-Host "Closing SteamVR processes to safely apply settings..." -ForegroundColor Yellow
    $vrProcesses | Stop-Process -Force
    Start-Sleep -Seconds 2
    Write-Host "[+] SteamVR closed." -ForegroundColor Green
}

# 2. Configure null driver default.vrsettings
if (Test-Path $steamvrNullSettings) {
    # Backup
    $nullBak = "$steamvrNullSettings.bak"
    if (!(Test-Path $nullBak)) {
        Copy-Item $steamvrNullSettings $nullBak
        Write-Host "[+] Created backup: $nullBak" -ForegroundColor DarkGray
    }

    $nullJson = Get-Content -Raw $steamvrNullSettings | ConvertFrom-Json
    $nullJson.driver_null.enable = $true
    $nullJson | ConvertTo-Json -Depth 10 | Set-Content $steamvrNullSettings -Encoding UTF8
    Write-Host "[+] Enabled null driver in: $steamvrNullSettings" -ForegroundColor Green
} else {
    Write-Warning "Could not find null driver settings at: $steamvrNullSettings"
}

# 3. Configure user steamvr.vrsettings
if (Test-Path $steamvrUserSettings) {
    # Backup
    $userBak = "$steamvrUserSettings.bak"
    if (!(Test-Path $userBak)) {
        Copy-Item $steamvrUserSettings $userBak
        Write-Host "[+] Created backup: $userBak" -ForegroundColor DarkGray
    }

    $userJson = Get-Content -Raw $steamvrUserSettings | ConvertFrom-Json
    if (-not $userJson.steamvr) {
        $userJson | Add-Member -MemberType NoteProperty -Name "steamvr" -Value ([PSCustomObject]@{})
    }

    $userJson.steamvr | Add-Member -MemberType NoteProperty -Name "requireHmd" -Value $false -Force
    $userJson.steamvr | Add-Member -MemberType NoteProperty -Name "forcedDriver" -Value "null" -Force
    $userJson.steamvr | Add-Member -MemberType NoteProperty -Name "activateMultipleDrivers" -Value $true -Force

    $userJson | ConvertTo-Json -Depth 10 | Set-Content $steamvrUserSettings -Encoding UTF8
    Write-Host "[+] Configured steamvr settings in: $steamvrUserSettings" -ForegroundColor Green
} else {
    Write-Warning "Could not find user steamvr.vrsettings at: $steamvrUserSettings"
}

Write-Host "`n==========================================================" -ForegroundColor Cyan
Write-Host " SteamVR is now configured for tracker-only tracking!" -ForegroundColor Green
Write-Host " Requirements to track without a headset:" -ForegroundColor White
Write-Host "  1. Plug in the USB wireless dongle for each Vive Tracker." -ForegroundColor Gray
Write-Host "  2. Power on your Base Stations (Lighthouses)." -ForegroundColor Gray
Write-Host "  3. Start SteamVR." -ForegroundColor Gray
Write-Host "  4. Turn on your Vive Trackers (LED turns solid green)." -ForegroundColor Gray
Write-Host "==========================================================" -ForegroundColor Cyan

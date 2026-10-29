# Script to restore SteamVR settings back to normal (requiring an HMD)

$ErrorActionPreference = "Stop"

$steamvrNullSettings = "C:\Program Files (x86)\Steam\steamapps\common\SteamVR\drivers\null\resources\settings\default.vrsettings"
$steamvrUserSettings = "C:\Program Files (x86)\Steam\config\steamvr.vrsettings"

Write-Host "Restoring original SteamVR settings..." -ForegroundColor Cyan

# 1. Close SteamVR if running
$vrProcesses = Get-Process -Name "vrmonitor", "vrserver", "vrcompositor" -ErrorAction SilentlyContinue
if ($vrProcesses) {
    Write-Host "Closing SteamVR..." -ForegroundColor Yellow
    $vrProcesses | Stop-Process -Force
    Start-Sleep -Seconds 2
}

# 2. Restore or reset null driver
if (Test-Path "$steamvrNullSettings.bak") {
    Copy-Item "$steamvrNullSettings.bak" $steamvrNullSettings -Force
    Write-Host "[+] Restored $steamvrNullSettings from backup" -ForegroundColor Green
} elseif (Test-Path $steamvrNullSettings) {
    $nullJson = Get-Content -Raw $steamvrNullSettings | ConvertFrom-Json
    $nullJson.driver_null.enable = $false
    $nullJson | ConvertTo-Json -Depth 10 | Set-Content $steamvrNullSettings -Encoding UTF8
    Write-Host "[+] Disabled null driver in $steamvrNullSettings" -ForegroundColor Green
}

# 3. Restore user settings
if (Test-Path "$steamvrUserSettings.bak") {
    Copy-Item "$steamvrUserSettings.bak" $steamvrUserSettings -Force
    Write-Host "[+] Restored $steamvrUserSettings from backup" -ForegroundColor Green
} elseif (Test-Path $steamvrUserSettings) {
    $userJson = Get-Content -Raw $steamvrUserSettings | ConvertFrom-Json
    if ($userJson.steamvr) {
        $userJson.steamvr.requireHmd = $true
        $userJson.steamvr.forcedDriver = ""
    }
    $userJson | ConvertTo-Json -Depth 10 | Set-Content $steamvrUserSettings -Encoding UTF8
    Write-Host "[+] Reverted requireHmd in $steamvrUserSettings" -ForegroundColor Green
}

Write-Host "`nSteamVR settings successfully restored to default." -ForegroundColor Green

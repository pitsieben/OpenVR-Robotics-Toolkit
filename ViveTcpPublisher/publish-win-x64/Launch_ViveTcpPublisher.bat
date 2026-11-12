@echo off
title Vive TCP Publisher
cd /d "%~dp0"

:menu
cls
echo ============================================================
echo   VIVE TCP PUBLISHER
echo ============================================================
echo.
echo   This tool streams HTC Vive tracker poses over TCP so
echo   Grasshopper can receive them in real-time.
echo.
echo   Make sure SteamVR is running before starting!
echo.
echo   [1] List connected trackers
echo   [2] Start publisher  (auto-select tracker, port 7000)
echo   [3] Start publisher  (choose serial + port manually)
echo   [4] Exit
echo.
set /p choice="  Choose an option (1-4): "

if "%choice%"=="1" goto list
if "%choice%"=="2" goto autostart
if "%choice%"=="3" goto manual
if "%choice%"=="4" exit
goto menu

:list
cls
echo ============================================================
echo   Connected Trackers:
echo ============================================================
echo.
ViveTcpPublisher.exe --list
echo.
pause
goto menu

:autostart
cls
echo ============================================================
echo   Starting publisher on 127.0.0.1:7000 ...
echo   Press Ctrl+C to stop.
echo ============================================================
echo.
ViveTcpPublisher.exe
echo.
echo Publisher stopped (exit code %ERRORLEVEL%).
pause
goto menu

:manual
cls
echo ============================================================
echo   Manual Setup
echo ============================================================
echo.
echo   First, list your trackers to find the serial number:
ViveTcpPublisher.exe --list
echo.
set /p serial="  Enter tracker serial (leave blank for auto): "
set /p port="  Enter port [default 7000]: "
if "%port%"=="" set port=7000
echo.
echo   Starting: ViveTcpPublisher.exe --serial %serial% --port %port%
echo   Press Ctrl+C to stop.
echo.
if "%serial%"=="" (
    ViveTcpPublisher.exe --port %port%
) else (
    ViveTcpPublisher.exe --serial %serial% --port %port%
)
echo.
echo Publisher stopped (exit code %ERRORLEVEL%).
pause
goto menu

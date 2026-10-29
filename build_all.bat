@echo off
setlocal
echo ============================================================
echo   Build HTC Vive Tracking Suite (Release)
echo ============================================================
echo.

echo [1/2] Building Grasshopper plugin (ViveTcpRecorderV2.gha)...
dotnet build -c Release "%~dp0ViveGrasshopperRecorder\ViveGrasshopperRecorder.csproj"
if errorlevel 1 (
    echo [ERROR] Failed to build Grasshopper plugin.
    pause
    exit /b 1
)

echo.
echo [2/2] Publishing SteamVR Publisher (ViveTcpPublisher.exe)...
dotnet publish -c Release -r win-x64 --self-contained false -o "%~dp0ViveTcpPublisher\publish-win-x64" "%~dp0ViveTcpPublisher\ViveTcpPublisher.csproj"
if errorlevel 1 (
    echo [ERROR] Failed to publish ViveTcpPublisher.
    pause
    exit /b 1
)

echo.
echo ============================================================
echo   Build completed successfully!
echo   Run copy_gha_to_grasshopper.bat to install the GHA plugin.
echo ============================================================
echo.
pause

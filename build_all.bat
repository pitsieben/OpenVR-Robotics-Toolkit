@echo off
setlocal
echo ============================================================
echo   Build HTC Vive Tracking Suite (Release)
echo ============================================================
echo.

echo [1/4] Building Grasshopper plugin (ViveTcpRecorderV2.gha)...
dotnet build -c Release "%~dp0ViveGrasshopperRecorder\ViveGrasshopperRecorder.csproj"
if errorlevel 1 (
    echo [ERROR] Failed to build Grasshopper plugin.
    pause
    exit /b 1
)

echo.
echo [2/4] Publishing SteamVR Publisher (ViveTcpPublisher.exe)...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "%~dp0ViveTcpPublisher\publish-win-x64" "%~dp0ViveTcpPublisher\ViveTcpPublisher.csproj"
if errorlevel 1 (
    echo [ERROR] Failed to publish ViveTcpPublisher.
    pause
    exit /b 1
)

echo.
echo [3/4] Publishing Clay Viewer (ClayViewer.exe)...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "%~dp0ClayViewer\publish-win-x64" "%~dp0ClayViewer\ClayViewer.csproj"
if errorlevel 1 (
    echo [ERROR] Failed to publish ClayViewer.
    pause
    exit /b 1
)

echo.
echo [4/4] Publishing token and invite app (ViveExchangeSetup.exe)...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "%~dp0ViveExchangeSetup\publish-win-x64" "%~dp0ViveExchangeSetup\ViveExchangeSetup.csproj"
if errorlevel 1 (
    echo [ERROR] Failed to publish ViveExchangeSetup.
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

@echo off
echo ============================================================
echo  Deploy ViveTcpRecorderV2.gha to Grasshopper Libraries
echo ============================================================

tasklist /FI "IMAGENAME eq Rhino.exe" 2>NUL | find /I /N "Rhino.exe">NUL
if "%ERRORLEVEL%"=="0" (
    echo [WARNING] Rhino is currently running!
    echo Please save your work and close Rhino so the file can be overwritten.
    echo.
    pause
)

set SOURCE="%~dp0ViveTcpRecorderV2.gha"
if not exist %SOURCE% set SOURCE="%~dp0ViveGrasshopperRecorder\bin\Release\net7.0-windows\ViveTcpRecorderV2.gha"
set DEST="%APPDATA%\Grasshopper\Libraries\ViveTcpRecorderV2.gha"

echo Copying %SOURCE%
echo to %DEST% ...

copy /Y %SOURCE% %DEST%
if errorlevel 1 (
    echo.
    echo [ERROR] Failed to copy GHA. Make sure Rhino is completely closed.
) else (
    echo.
    echo [SUCCESS] ViveTcpRecorderV2.gha updated successfully in Grasshopper Libraries!
)

echo.
pause

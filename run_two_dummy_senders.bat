@echo off
title Launching 2 Dummy Vive Trackers...
echo Starting Tracker 1 on port 7000 (Circle motion)...
start "Vive Tracker 1 (Port 7000)" python "%~dp0dummy_sender.py" --port 7000 --mode circle --id LHR-TRACKER-1

echo Starting Tracker 2 on port 7001 (Spiral motion)...
start "Vive Tracker 2 (Port 7001)" python "%~dp0dummy_sender.py" --port 7001 --mode spiral --id LHR-TRACKER-2

echo.
echo Both dummy trackers are running!
echo In Grasshopper:
echo   - Component 1: set IP to "127.0.0.1:7000"
echo   - Component 2: set IP to "127.0.0.1:7001"
echo.
pause

@echo off
title ModernUO - rebuild
cd /d "%~dp0"

tasklist /FI "IMAGENAME eq ModernUO.exe" /NH | findstr /I "ModernUO.exe" >nul
if not errorlevel 1 (
    echo ModernUO is still running.
    echo Close the server first: type "shutdown" in its window, then run this again.
    pause
    exit /b 1
)

echo Building...
dotnet build
if errorlevel 1 (
    echo.
    echo BUILD FAILED. The server was not started. See the errors above.
    pause
    exit /b 1
)

echo.
echo Build succeeded. Starting the server...
call "%~dp0start-server.bat"

@echo off
setlocal
title MVNCP viewer task

rem ============================================================
rem  Run this on the PC you CONTROL FROM.
rem  Registers bin\mvncp-viewer.exe to start at logon
rem  (Task Scheduler).
rem
rem  EDIT THE LINE BELOW: address of the PC running the server.
rem  Format: host  or  host:port
rem ============================================================

set "ADDR=192.168.1.10:5900"

set "APP=%~dp0bin\mvncp-viewer.exe"
set "TASK=mvncp-viewer"

if not exist "%APP%" (
    echo [ERROR] Not found: "%APP%"
    echo Build first:  powershell -ExecutionPolicy Bypass -File "%~dp0build.ps1"
    echo.
    pause
    exit /b 1
)

net session >nul 2>&1
if errorlevel 1 (
    echo Administrator rights are required to create a logon task.
    echo Windows will show a UAC prompt now.
    echo If you cancel it, nothing will be changed.
    echo.
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo Registering viewer task "%TASK%"
echo   exe:     "%APP%"
echo   address: %ADDR%
echo   trigger: at logon, current user, minimized
echo.

schtasks /Create /TN "%TASK%" /TR "\"%APP%\" %ADDR% /min" /SC ONLOGON /RL LIMITED /F
if errorlevel 1 (
    echo.
    echo [ERROR] Could not create the task.
    pause
    exit /b 1
)

echo.
set /p START=Start it now? [y/N] 
if /I "%START%"=="y" schtasks /Run /TN "%TASK%"

echo.
echo Done.
echo   start:  schtasks /Run /TN "%TASK%"
echo   stop:   schtasks /End /TN "%TASK%"
echo   delete: schtasks /Delete /TN "%TASK%" /F
echo.
pause

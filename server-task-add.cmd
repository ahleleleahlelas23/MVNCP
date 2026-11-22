@echo off
setlocal
title MVNCP server task

rem ============================================================
rem  Run this on the PC that will be CONTROLLED.
rem  Registers bin\mvncp-server.exe to start at logon
rem  (Task Scheduler) and opens the firewall for it on
rem  private and domain networks.
rem
rem  Task Scheduler needs administrator rights, so this file
rem  will ask for elevation (UAC) once.
rem ============================================================

set "APP=%~dp0bin\mvncp-server.exe"
set "TASK=mvncp-server"
set "RULE=MVNCP Server"

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

echo Registering server task "%TASK%"
echo   exe:     "%APP%"
echo   trigger: at logon, current user
echo.

schtasks /Create /TN "%TASK%" /TR "\"%APP%\"" /SC ONLOGON /RL LIMITED /F
if errorlevel 1 (
    echo.
    echo [ERROR] Could not create the task.
    pause
    exit /b 1
)

echo.
echo Adding firewall rule "%RULE%" for private and domain networks...
netsh advfirewall firewall delete rule name="%RULE%" >nul 2>&1
netsh advfirewall firewall add rule name="%RULE%" dir=in action=allow program="%APP%" enable=yes profile=private,domain
if errorlevel 1 echo [WARN] Firewall rule was not added. Allow mvncp-server.exe manually if connections fail.

echo.
set /p START=Start it now? [y/N] 
if /I "%START%"=="y" schtasks /Run /TN "%TASK%"

echo.
echo Done.
echo   start:  schtasks /Run /TN "%TASK%"
echo   stop:   schtasks /End /TN "%TASK%"
echo   delete: schtasks /Delete /TN "%TASK%" /F
echo   firewall rule: netsh advfirewall firewall delete rule name="%RULE%"
echo.
pause

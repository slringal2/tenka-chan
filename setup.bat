@echo off
setlocal DisableDelayedExpansion
chcp 65001 >nul
title Tenka-chan - Setup
set "TENKA_PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%TENKA_PS%" goto no_powershell
if not exist "%~dp0scripts\tenka-windows.ps1" goto no_files
"%TENKA_PS%" -NoLogo -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0scripts\tenka-windows.ps1" -Mode Setup
set "TENKA_EXIT=%ERRORLEVEL%"
echo.
if not "%TENKA_EXIT%"=="0" echo Setup/start did not complete. Please read the message and log above.
echo Press any key to close this window.
pause >nul
exit /b %TENKA_EXIT%
:no_files
echo Please extract ALL files from the ZIP first.
echo Put this BAT in the tenka-chan folder next to src, scripts and global.json.
echo Missing: scripts\tenka-windows.ps1
pause
exit /b 1
:no_powershell
echo Windows PowerShell could not be found. This launcher requires Windows.
pause
exit /b 1

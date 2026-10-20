@echo off
setlocal DisableDelayedExpansion
chcp 65001 >nul
title Tenka-chan - Startup Verification
set "TENKA_PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%TENKA_PS%" goto no_powershell
if not exist "%~dp0scripts\tenka-windows.ps1" goto no_files
echo Checking the real Tenka startup path without opening a browser.
echo The previously configured workspace is used. No setup or download runs.
echo An already-running server is never stopped by this check.
echo.
"%TENKA_PS%" -NoLogo -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0scripts\tenka-windows.ps1" -Mode Start -VerifyStartup
set "TENKA_EXIT=%ERRORLEVEL%"
echo.
if "%TENKA_EXIT%"=="0" (
    echo Tenka startup and MCP connection verification completed.
) else (
    echo Verification failed. Read the message and log above.
)
echo Press any key to close this window.
pause >nul
exit /b %TENKA_EXIT%
:no_files
echo Missing scripts\tenka-windows.ps1. Extract the full repository first.
pause
exit /b 1
:no_powershell
echo Windows PowerShell 5.1 was not found.
pause
exit /b 1

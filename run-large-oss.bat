@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\prepare-large-oss.ps1"
set "RESULT=%ERRORLEVEL%"
echo.
pause
exit /b %RESULT%

@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\check-tenka.ps1"
set "TENKA_CHECK_EXIT=%ERRORLEVEL%"
echo.
pause
exit /b %TENKA_CHECK_EXIT%

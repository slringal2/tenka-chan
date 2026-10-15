@echo off
setlocal
if not exist "%~dp0samples\Tenka.Sample.RaidLab\run-lab.bat" (
  >&2 echo RaidLab files are missing. Update the full source checkout first.
  pause
  exit /b 2
)
call "%~dp0samples\Tenka.Sample.RaidLab\run-lab.bat"
exit /b %ERRORLEVEL%

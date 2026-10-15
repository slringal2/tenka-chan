@echo off
setlocal
set "ADAPTER=%~dp0src\TenkaChan.Mcp\bin\Release\net10.0\TenkaChan.Mcp.dll"
if not exist "%ADAPTER%" (
  >&2 echo Tenka MCP adapter is not built. Run setup.bat first, then start.bat.
  exit /b 2
)
dotnet "%ADAPTER%" %*
exit /b %ERRORLEVEL%

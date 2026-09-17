@echo off
setlocal
cd /d "%~dp0"
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
set "SDK=dotnet"
if exist "%~dp0.tools\dotnet\dotnet.exe" set "SDK=%~dp0.tools\dotnet\dotnet.exe"
"%SDK%" run --project tests\Notlar.Tests -c Release -- %*
exit /b %errorlevel%

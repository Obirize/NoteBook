@echo off
setlocal
cd /d "%~dp0"
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
set "SDK=dotnet"
if exist "%~dp0.tools\dotnet\dotnet.exe" set "SDK=%~dp0.tools\dotnet\dotnet.exe"
set "VERSIONARG="
if defined NOTLAR_VERSION set "VERSIONARG=-p:Version=%NOTLAR_VERSION%"
"%SDK%" publish src\Notlar\Notlar.csproj -c Release -r win-x64 --self-contained true -o app -p:DebugType=None -p:DebugSymbols=false %VERSIONARG%
if errorlevel 1 exit /b 1
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /win32icon:"%~dp0notes.ico" /out:"%~dp0Not Defteri.exe" /r:System.Windows.Forms.dll "%~dp0Launcher.cs"
if errorlevel 1 exit /b 1
echo Not Defteri.exe hazir.

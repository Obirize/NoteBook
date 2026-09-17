@echo off
setlocal
cd /d "%~dp0"
call "%~dp0build.cmd"
if errorlevel 1 exit /b 1
set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (
  echo Inno Setup 6 bulunamadi. https://jrsoftware.org/isdl.php adresinden kurun.
  exit /b 1
)
"%ISCC%" setup\Notlar.iss
if errorlevel 1 exit /b 1
echo Kurulum dosyasi dist\ klasorunde hazir.

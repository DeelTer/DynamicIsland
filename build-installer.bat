@echo off
setlocal
set "PROJECT=%~dp0"
set "PUBLISH=%TEMP%\Island-publish-%RANDOM%-%RANDOM%"
set "ISCC="

where dotnet >nul 2>&1 || (echo .NET 9 SDK is required to build the installer.& exit /b 1)
where ISCC.exe >nul 2>&1 && set "ISCC=ISCC.exe"
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 7\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 7\ISCC.exe"
if not defined ISCC if exist "%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe"
if not defined ISCC (
  echo Inno Setup 7 is required. Install it from https://jrsoftware.org/isdl.php
  exit /b 1
)
if not exist "%PROJECT%assets\MicrosoftEdgeWebView2Setup.exe" (
  echo WebView2 bootstrapper is missing from assets.
  exit /b 1
)
tasklist /FI "IMAGENAME eq Island.exe" /NH | findstr /I /B /C:"Island.exe " >nul
if not errorlevel 1 (
  echo Close Island before building its installer.
  exit /b 1
)
if not exist "%PROJECT%release" mkdir "%PROJECT%release"

dotnet publish "%PROJECT%Island.csproj" -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -p:CopyOutputSymbolsToPublishDirectory=false -o "%PUBLISH%"
if errorlevel 1 goto failed

"%ISCC%" -dAppSource="%PUBLISH%" -dWebView2Bootstrapper="%PROJECT%assets\MicrosoftEdgeWebView2Setup.exe" "%PROJECT%scripts\build-installer.iss"
if errorlevel 1 goto failed
rmdir /s /q "%PUBLISH%"
echo Installer created: "%PROJECT%release\Island-Setup.exe"
exit /b 0

:failed
if exist "%PUBLISH%" rmdir /s /q "%PUBLISH%"
exit /b 1
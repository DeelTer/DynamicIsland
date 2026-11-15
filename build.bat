@echo off
setlocal
set "INSTALL=%LOCALAPPDATA%\Programs\Island"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo Install the .NET SDK before building Island.
    goto failed
)

tasklist /FI "IMAGENAME eq Island.exe" /NH | findstr /I /B /C:"Island.exe " >nul
if not errorlevel 1 (
    echo Close Island through its tray menu, then run build.bat again.
    goto failed
)

dotnet publish "%~dp0Island.csproj" -c Release -r win-x64 --self-contained true -o "%INSTALL%"
if errorlevel 1 goto failed

start "" /D "%INSTALL%" "%INSTALL%\Island.exe"
if errorlevel 1 goto failed
exit /b 0

:failed
pause
exit /b 1

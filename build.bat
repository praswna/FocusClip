@echo off
setlocal

cd /d "%~dp0"

rem Deploy OUTSIDE Dropbox. A single-file exe inside a synced folder can be
rem partially synced / locked at launch and run corrupt (icons + saving break).
set "DEPLOY=%LOCALAPPDATA%\FocusClip\app"

echo === Killing running FocusClip.exe ===
taskkill /IM FocusClip.exe /F >nul 2>&1

echo === Cleaning build intermediates ===
if exist "bin" rmdir /s /q "bin"
if exist "obj" rmdir /s /q "obj"

echo === Publishing single EXE to %DEPLOY% ===
rem Publish-only props are passed here (not in csproj) to keep plain build fast.
dotnet publish FocusClip.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishReadyToRun=true -o "%DEPLOY%"

if errorlevel 1 (
    echo.
    echo === BUILD FAILED ===
    pause
    exit /b 1
)

rem Guard: publish can fail without a caught errorlevel (e.g. "No .NET SDKs were found"
rem from the dotnet host). Never report OK / launch unless the exe actually exists.
if not exist "%DEPLOY%\FocusClip.exe" (
    echo.
    echo === BUILD FAILED: %DEPLOY%\FocusClip.exe was not produced ===
    echo Check that the .NET 8 SDK is installed:  dotnet --list-sdks
    pause
    exit /b 1
)

echo === Removing build intermediates ===
dotnet build-server shutdown >nul 2>&1
if exist "bin" rmdir /s /q "bin"
if exist "obj" rmdir /s /q "obj"
if exist "%DEPLOY%\*.pdb" del /q "%DEPLOY%\*.pdb"

echo.
echo === BUILD OK: %DEPLOY%\FocusClip.exe ===

echo === Refreshing icon cache (no Explorer restart) ===
"%SystemRoot%\System32\ie4uinit.exe" -show >nul 2>&1

echo === Launching FocusClip ===
start "" "%DEPLOY%\FocusClip.exe"

echo.
pause
endlocal

@echo off
setlocal

cd /d "%~dp0"

echo === Checking .NET SDK ===
where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo.
    echo === ERROR: 'dotnet' not found on PATH ===
    echo Install the .NET 8 SDK ^(the SDK, not just the runtime^):
    echo   https://aka.ms/dotnet/download
    pause
    exit /b 1
)
rem The runtime alone answers 'where dotnet' but ships no SDK, so publish/build
rem fails with "No .NET SDKs were found." Detect that up front.
set "FC_SDK="
for /f "delims=" %%v in ('dotnet --list-sdks 2^>nul') do set "FC_SDK=1"
if not defined FC_SDK (
    echo.
    echo === ERROR: .NET runtime is installed but no SDK ===
    echo Install the .NET 8 SDK ^(x64^):
    echo   https://aka.ms/dotnet/download
    pause
    exit /b 1
)

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

if %errorlevel% neq 0 (
    echo.
    echo === BUILD FAILED ===
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

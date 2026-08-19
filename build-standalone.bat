@echo off
setlocal

cd /d "%~dp0"

echo === Checking .NET SDK ===
where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo.
    echo === ERROR: 'dotnet' not found on PATH ===
    echo Install the .NET 10 SDK ^(the SDK, not just the runtime^):
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
    echo Install the .NET 10 SDK ^(x64^):
    echo   https://aka.ms/dotnet/download
    pause
    exit /b 1
)

rem Self-contained build: .NET 10 Runtime is bundled INSIDE one exe (~170 MB).
rem No runtime install required on the target machine.
rem Output name differs from build.bat (FocusClip.exe) so both versions coexist.
set "DEPLOY=%LOCALAPPDATA%\FocusClip\app"
set "FC_TMP=%TEMP%\fc-standalone"

echo === Cleaning build intermediates ===
if exist "bin" rmdir /s /q "bin"
if exist "obj" rmdir /s /q "obj"
if exist "%FC_TMP%" rmdir /s /q "%FC_TMP%"

echo === Publishing self-contained single EXE (this takes a while) ===
dotnet publish FocusClip.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%FC_TMP%"

if %errorlevel% neq 0 (
    echo.
    echo === BUILD FAILED ===
    pause
    exit /b 1
)

echo === Deploying as FocusClip-Standalone.exe ===
if not exist "%FC_TMP%\FocusClip.exe" (
    echo.
    echo === BUILD FAILED: publish produced no exe ===
    pause
    exit /b 1
)
if not exist "%DEPLOY%" mkdir "%DEPLOY%"
move /y "%FC_TMP%\FocusClip.exe" "%DEPLOY%\FocusClip-Standalone.exe" >nul

echo === Cleaning up ===
dotnet build-server shutdown >nul 2>&1
if exist "bin" rmdir /s /q "bin"
if exist "obj" rmdir /s /q "obj"
if exist "%FC_TMP%" rmdir /s /q "%FC_TMP%"

echo.
echo === BUILD OK (self-contained): %DEPLOY%\FocusClip-Standalone.exe ===
rem Does not kill/launch the daily FocusClip.exe; this exe is for distribution.
explorer "%DEPLOY%"

echo.
pause
endlocal

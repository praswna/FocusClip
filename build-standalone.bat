@echo off
setlocal

cd /d "%~dp0"

rem Self-contained build: .NET 8 Runtime is bundled INSIDE one exe (~170 MB).
rem No runtime install required on the target machine.
rem Output name differs from build.bat (FocusClip.exe) so both versions coexist.
set "DEPLOY=%LOCALAPPDATA%\FocusClip\app"
set "TMP=%TEMP%\fc-standalone"

echo === Cleaning build intermediates ===
if exist "bin" rmdir /s /q "bin"
if exist "obj" rmdir /s /q "obj"
if exist "%TMP%" rmdir /s /q "%TMP%"

echo === Publishing self-contained single EXE (this takes a while) ===
dotnet publish FocusClip.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%TMP%"

if errorlevel 1 (
    echo.
    echo === BUILD FAILED ===
    pause
    exit /b 1
)

echo === Deploying as FocusClip-Standalone.exe ===
if not exist "%DEPLOY%" mkdir "%DEPLOY%"
move /y "%TMP%\FocusClip.exe" "%DEPLOY%\FocusClip-Standalone.exe" >nul

echo === Cleaning up ===
dotnet build-server shutdown >nul 2>&1
if exist "bin" rmdir /s /q "bin"
if exist "obj" rmdir /s /q "obj"
if exist "%TMP%" rmdir /s /q "%TMP%"

echo.
echo === BUILD OK (self-contained): %DEPLOY%\FocusClip-Standalone.exe ===
rem Does not kill/launch the daily FocusClip.exe; this exe is for distribution.
explorer "%DEPLOY%"

echo.
pause
endlocal

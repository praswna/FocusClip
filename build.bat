@echo off
setlocal

cd /d "%~dp0"

echo === Killing running FocusClip.exe ===
taskkill /IM FocusClip.exe /F >nul 2>&1

echo === Cleaning old outputs ===
rem Keep publish\ (overwrite in place) so the exe path stays stable for the icon cache.
if exist "bin" rmdir /s /q "bin"
if exist "obj" rmdir /s /q "obj"

echo === dotnet publish (single EXE -> publish\) ===
dotnet publish FocusClip.csproj -c Release -o publish

if errorlevel 1 (
    echo.
    echo === BUILD FAILED ===
    pause
    exit /b 1
)

echo === Removing build intermediates (keep only publish\FocusClip.exe) ===
dotnet build-server shutdown >nul 2>&1
if exist "bin" rmdir /s /q "bin"
if exist "obj" rmdir /s /q "obj"
if exist "publish\*.pdb" del /q "publish\*.pdb"

echo.
echo === BUILD OK: publish\FocusClip.exe ===

echo === Refreshing icon cache (no Explorer restart) ===
ie4uinit.exe -show

echo === Launching FocusClip ===
start "" "%~dp0publish\FocusClip.exe"

echo.
pause
endlocal

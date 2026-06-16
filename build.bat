@echo off
setlocal

cd /d "%~dp0"

echo === Killing running FocusClip.exe ===
taskkill /IM FocusClip.exe /F >nul 2>&1

echo === Cleaning old outputs ===
if exist "bin" rmdir /s /q "bin"
if exist "obj" rmdir /s /q "obj"
if exist "publish" rmdir /s /q "publish"

echo === dotnet publish (single EXE -> publish\) ===
dotnet publish FocusClip.csproj -c Release -o publish

if errorlevel 1 (
    echo.
    echo === BUILD FAILED ===
    exit /b 1
)

echo === Removing build intermediates (keep only publish\FocusClip.exe) ===
dotnet build-server shutdown >nul 2>&1
if exist "bin" rmdir /s /q "bin"
if exist "obj" rmdir /s /q "obj"
if exist "publish\*.pdb" del /q "publish\*.pdb"

echo.
echo === BUILD OK: publish\FocusClip.exe ===
endlocal

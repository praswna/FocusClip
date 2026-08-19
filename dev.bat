@echo off
setlocal
rem === 개발 반복용 빠른 빌드 ===
rem build.bat 과 달리 bin/obj 를 지우지 않는다 → 증분 빌드(보통 2~5초).
rem Debug 구성, ReadyToRun/단일EXE/RID 없음. 배포 산출물이 필요할 때만 build.bat 사용.

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

echo === Killing running FocusClip.exe ===
taskkill /IM FocusClip.exe /F >nul 2>&1
rem 종료 후 DLL 잠금이 풀릴 시간을 잠깐 준다(증분 빌드 파일잠금 회피).
ping -n 2 127.0.0.1 >nul

echo === dotnet build (Debug, incremental) ===
dotnet build FocusClip.csproj -c Debug

if %errorlevel% neq 0 (
    echo.
    echo === BUILD FAILED ===
    pause
    exit /b 1
)

echo === Launching FocusClip (Debug) ===
start "" "%~dp0bin\Debug\net10.0-windows\FocusClip.exe"

endlocal

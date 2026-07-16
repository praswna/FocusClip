@echo off
setlocal
rem === 개발 반복용 빠른 빌드 ===
rem build.bat 과 달리 bin/obj 를 지우지 않는다 → 증분 빌드(보통 2~5초).
rem Debug 구성, ReadyToRun/단일EXE/RID 없음. 배포 산출물이 필요할 때만 build.bat 사용.

cd /d "%~dp0"

echo === Killing running FocusClip.exe ===
taskkill /IM FocusClip.exe /F >nul 2>&1
rem 종료 후 DLL 잠금이 풀릴 시간을 잠깐 준다(증분 빌드 파일잠금 회피).
ping -n 2 127.0.0.1 >nul

echo === dotnet build (Debug, incremental) ===
dotnet build FocusClip.csproj -c Debug

if errorlevel 1 (
    echo.
    echo === BUILD FAILED ===
    pause
    exit /b 1
)

rem Guard: build can fail without a caught errorlevel (e.g. "No .NET SDKs were found").
if not exist "%~dp0bin\Debug\net8.0-windows\FocusClip.exe" (
    echo.
    echo === BUILD FAILED: exe was not produced ===
    echo Check that the .NET 8 SDK is installed:  dotnet --list-sdks
    pause
    exit /b 1
)

echo === Launching FocusClip (Debug) ===
start "" "%~dp0bin\Debug\net8.0-windows\FocusClip.exe"

endlocal

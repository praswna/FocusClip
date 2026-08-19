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

rem WPF 는 Microsoft.WindowsDesktop.App 공유 프레임워크가 있어야 실행된다.
rem SDK 만 있고 이게 없으면 빌드는 되지만 실행 시
rem "You must install .NET Desktop Runtime" 대화상자가 뜬다. 이 스크립트는 끝에서
rem exe 를 실행하므로 미리 확인한다.
set "FC_DRT="
for /f "delims=" %%v in ('dotnet --list-runtimes 2^>nul ^| findstr /C:"Microsoft.WindowsDesktop.App"') do set "FC_DRT=1"
if not defined FC_DRT (
    echo.
    echo === ERROR: .NET Desktop Runtime not found ===
    echo WPF needs the Microsoft.WindowsDesktop.App shared framework.
    echo Install the .NET 10 Desktop Runtime ^(x64^):
    echo   https://dotnet.microsoft.com/download/dotnet/10.0
    echo.
    echo ^(Or use build-standalone.bat - it bundles the runtime into the exe.^)
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

# FocusClip

FocusManager(AHK)와 Clipboard-Manager(PyQt6)를 단일 네이티브 앱으로 통합한 Windows 유틸리티.  
C# / .NET 8 / WPF로 작성되었으며 단일 EXE로 배포된다(.NET 8 Runtime 필요).

## 주요 기능

| 기능 | 설명 |
|------|------|
| **런처 도크** | CapsLock 누르면 등록 앱 목록 팝업. 숫자키(고정 개수만큼, 최대 1~9)로 즉시 전환 |
| **클립보드 히스토리** | 텍스트/이미지 최대 30개 보관, 재시작 후에도 유지, 팝업에서 선택 후 자동 붙여넣기, 드래그앤드롭으로 다른 앱에 직접 드롭. 전체/텍스트/이미지 필터 |
| **경로 팝업** | 복사된 파일 경로·URL을 별도 팝업에서 함축 표시. 클릭=경로 붙여넣기, 드래그=텍스트로 드롭, ↗=로컬/URL 열기, 전체/로컬/URL 필터 |
| **항상 위 토글** | 사이드바에서 우클릭 → 대상 창을 TopMost 전환 |
| **텍스트 편집기** | 클립 텍스트를 편집 후 재붙여넣기 |
| **이미지 어노테이션** | 클립 이미지에 텍스트/도형 주석 추가 후 저장 |
| **토스트 알림** | 새 클립 캡처 시, 드래그 드롭 실패 시 우하단 알림 |
| **시스템 트레이** | 트레이 상주, 우클릭 메뉴로 설정/종료 |
| **자동 시작** | 설정에서 Windows 시작 시 자동 실행 등록/해제 |

## 단축키

| 키 | 동작 |
|----|------|
| `CapsLock` | 런처 도크 열기/닫기 |
| `1` ~ `9` | 도크 표시 중 — 고정 구간 앱 활성화(없으면 실행). 고정 개수만큼 동작 |
| `Esc` | 도크/팝업 닫기 |
| 그 외 키 | 도크 자동 닫기(키는 대상 앱에 그대로 전달) |

기본 단축키(CapsLock)는 설정 창에서 변경 가능.

## 빌드 및 실행

```
dotnet build -c Release
dotnet run
```

단일 EXE 배포:

```
dotnet publish FocusClip.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishReadyToRun=true -o publish
```

출력: `publish\FocusClip.exe` (.NET 8 Runtime 필요)

## 요구 사항

- Windows 10/11 x64
- .NET 8 Runtime (또는 self-contained 배포 사용)

## 설정 파일

| 파일 | 내용 |
|------|------|
| `%APPDATA%\FocusClip\config.json` | 앱 목록·단축키 설정. 직접 편집하거나 설정 창으로 관리 |
| `%APPDATA%\FocusClip\clips.json` | 클립보드 히스토리(텍스트/경로/이미지 참조). 재시작 후에도 유지 |
| `%MyPictures%\ClipboardSaver\` | 이미지 클립 PNG 파일 저장 위치 |

## 프로젝트 구조

```
FocusClip/
├── Models/          AppConfig, AppEntry, ClipItem
├── Services/        ClipboardService, HotkeyService, WindowManager
│                    ConfigService, IconService, PasteService, StartupService
├── Views/           LauncherDock, Sidebar, ClipboardPopup, PathPopup
│                    SettingsWindow, TextEditWindow, ImageAnnotateWindow, Toast
├── Interop/         NativeMethods (Win32 P/Invoke)
├── Themes/          Dark.xaml
└── App.xaml
```

## 원본 앱과의 대응

| FocusClip | 기존 |
|-----------|------|
| `HotkeyService` (LL 키보드 후크) | FocusManager AHK 스크립트 |
| `ClipboardService` (WM_CLIPBOARDUPDATE) | Clipboard-Manager 폴링 스레드 |
| `WindowManager.ActivateOrRun` | FM `ActivateOrRun` 함수 |
| `LauncherDock` | FM 팝업 UI |
| `ClipboardPopup` | CM 팝업 UI |

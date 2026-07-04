using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FocusClip.Models;

/// <summary>도크·사이드바 아이콘 우클릭 시 수행할 동작.</summary>
public enum DockRightClickAction
{
    AlwaysOnTop, // 항상 위 토글(기존 기본 동작, 주황 표시등)
    Close,       // 대상 창 닫기(WM_CLOSE)
    Minimize,    // 대상 창 최소화
    None,        // 아무 동작 안 함
}

/// <summary>앱 전역 설정 + 등록 앱 목록. %APPDATA%\FocusClip\config.json 에 저장.</summary>
public class AppConfig
{
    public int HotkeyVk { get; set; } = 0x14;     // CapsLock
    public int PinnedCount { get; set; } = 4;     // 앞쪽 N개 = 사이드바 고정 + 숫자키 대상
    public bool StartupRegistered { get; set; } = false; // 첫 실행 시 자동시작 1회 등록 완료 여부
    public bool SidebarEnabled { get; set; } = true; // 왼쪽 고정 사이드바 표시 여부(기본 ON)
    public string FileManagerPath { get; set; } = ""; // 폴더 열기에 쓸 파일 관리자 exe(예: Q-Dir). 비우면 기본 탐색기.
    public string FileManagerArgs { get; set; } = "\"%path%\""; // 파일 관리자 실행 인자 템플릿. %path%가 폴더 경로로 치환됨.
    // 저장 정책은 핀 기반(미고정=메모리, 고정=파일)으로 고정 — 별도 설정 없음. (옛 MemoryOnly 필드는 제거됨)

    // 아이콘 우클릭 동작(설정창에서 변경). 문자열로 직렬화해 enum 순서 변경에 강하게.
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DockRightClickAction RightClickAction { get; set; } = DockRightClickAction.AlwaysOnTop;

    public List<AppEntry> Apps { get; set; } = new();
}

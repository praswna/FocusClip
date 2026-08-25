using System.Collections.Generic;
using System.Text.Json;
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
    public bool SidebarAutoHide { get; set; } = true; // 일정 시간 뒤 사이드바 자동 숨김(기본 ON). 끄면 상시 표시.
    public int SidebarHideDelayMs { get; set; } = 3000; // 자동 숨김까지의 대기 시간(ms). 마우스가 벗어난 시점부터 잰다.
    public string FileManagerPath { get; set; } = ""; // 폴더 열기에 쓸 파일 관리자 exe(예: Q-Dir). 비우면 기본 탐색기.
    public string FileManagerArgs { get; set; } = "\"%path%\""; // 파일 관리자 실행 인자 템플릿. %path%가 폴더 경로로 치환됨.
    // 저장 정책은 핀 기반(미고정=메모리, 고정=파일)으로 고정 — 별도 설정 없음. (옛 MemoryOnly 필드는 제거됨)

    // 아이콘 우클릭 동작(설정창에서 변경). 문자열로 직렬화해 enum 순서 변경에 강하게.
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DockRightClickAction RightClickAction { get; set; } = DockRightClickAction.AlwaysOnTop;

    public List<AppEntry> Apps { get; set; } = new();

    /// <summary>깊은 복사본. 설정창이 「취소」로 되돌릴 스냅샷을 뜨는 데 쓴다.
    /// JSON 왕복이라 설정 항목이 늘어도 따라온다(AppEntry 의 UI 전용 속성은 JsonIgnore 라 빠진다).</summary>
    public AppConfig Clone() => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this))!;

    /// <summary>스냅샷의 값으로 되돌린다. 인스턴스를 교체하지 않고 값만 덮어써
    /// 이 객체를 들고 있는 쪽(App 등)의 참조가 그대로 유효하게 둔다.
    /// 리플렉션이라 설정 항목이 늘어도 되돌리기에서 빠지지 않는다.</summary>
    public void CopyFrom(AppConfig other)
    {
        foreach (var p in typeof(AppConfig).GetProperties())
            if (p.CanRead && p.CanWrite) p.SetValue(this, p.GetValue(other));
    }
}

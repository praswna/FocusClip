using System.Collections.Generic;

namespace FocusClip.Models;

/// <summary>앱 전역 설정 + 등록 앱 목록. %APPDATA%\FocusClip\config.json 에 저장.</summary>
public class AppConfig
{
    public int HotkeyVk { get; set; } = 0x14;     // CapsLock
    public int PinnedCount { get; set; } = 4;     // 앞쪽 N개 = 사이드바 고정 + 숫자키 대상
    public bool StartupRegistered { get; set; } = false; // 첫 실행 시 자동시작 1회 등록 완료 여부
    public List<AppEntry> Apps { get; set; } = new();
}

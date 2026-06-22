using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace FocusClip.Models;

/// <summary>등록된 앱 1개. (FocusManager 의 TargetApps 항목에 대응)</summary>
public class AppEntry : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string ProcessName { get; set; } = "";   // 예: "chrome.exe" (창 매칭용, FM의 ahk_exe 값)
    public string ExePath { get; set; } = "";        // 실행 파일 경로(낡으면 실행 중 프로세스에서 self-heal)

    // ── UI 바인딩용(설정 직렬화 제외) ──
    private ImageSource? _icon;
    [System.Text.Json.Serialization.JsonIgnore]
    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; OnPropertyChanged(); }
    }

    private bool _isActive;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsActive
    {
        get => _isActive;
        set { _isActive = value; OnPropertyChanged(); }
    }

    private bool _isTopMost;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsTopMost
    {
        get => _isTopMost;
        set { _isTopMost = value; OnPropertyChanged(); }
    }

    // 도크 숫자키 라벨("1"~"9"). 고정구간 앱만 채워지고, 그 외엔 ""(미표시). 도크가 인덱스/PinnedCount 기준으로 갱신.
    private string _hotkeyLabel = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string HotkeyLabel
    {
        get => _hotkeyLabel;
        set { _hotkeyLabel = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

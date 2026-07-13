using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FocusClip.Models;

/// <summary>프롬프트 그룹 1개 = 독립 팝업 창 하나. 이름·항목과 함께 팝업 핀 상태/위치를 영속한다
/// — 핀해서 옮겨둔 그룹 팝업은 다음 실행에서도 같은 자리에 복원된다.</summary>
public class PromptGroup : INotifyPropertyChanged
{
    private string _name = "";
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public ObservableCollection<PromptItem> Items { get; } = new();

    /// <summary>팝업 핀 상태(영속). 핀된 그룹은 오버레이를 열 때 저장된 위치에 다시 표시된다.</summary>
    public bool Pinned { get; set; }

    /// <summary>핀된 팝업의 저장 위치(DIP). NaN이면 저장된 위치 없음 → 기본 배치.</summary>
    public double PinX { get; set; } = double.NaN;
    public double PinY { get; set; } = double.NaN;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

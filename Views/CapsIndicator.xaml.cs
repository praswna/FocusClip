using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FocusClip.Interop;

namespace FocusClip.Views;

/// <summary>
/// CapsLock 대소문자 상태(A/a) 표시 + 클릭 시 전환. 다른 팝업과 동일하게 오버레이와 함께 표시/숨김되는
/// 무활성·Topmost 툴윈도우. 도크 왼쪽에 같은 세로크기로 정렬된다.
/// </summary>
public partial class CapsIndicator : Window
{
    /// <summary>클릭으로 대소문자 전환을 요청(App이 실제 CapsLock 토글 수행).</summary>
    public event Action? ToggleRequested;

    private bool _caps;

    public CapsIndicator()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.MakeNoActivateToolWindow(new WindowInteropHelper(this).Handle);
    }

    private void Apply()
    {
        Glyph.Text = _caps ? "A" : "a";
        Glyph.Foreground = _caps ? (Brush)FindResource("AccentBrush") : Brushes.White;
    }

    /// <summary>도크(anchor) 왼쪽에, 도크와 같은 세로크기(정사각)로 정렬해 표시한다.</summary>
    public void ShowAligned(Window anchor, bool caps)
    {
        _caps = caps;
        Apply();

        double size = anchor.ActualHeight > 0 ? anchor.ActualHeight : 40;
        Width = size;
        Height = size;          // 도크와 같은 세로크기
        Glyph.FontSize = Math.Max(12, size * 0.45);

        Show();
        UpdateLayout();

        var wa = ScreenUtil.WorkAreaDip(anchor);
        double left = anchor.Left - Width - 6;   // 도크 바로 왼쪽
        double top = anchor.Top;                  // 도크와 상단 정렬
        Left = Math.Max(wa.Left, Math.Min(left, wa.Right - Width));
        Top = Math.Max(wa.Top, Math.Min(top, wa.Bottom - Height));
    }

    private void Root_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _caps = !_caps;     // 즉시 반영(예측) — 실제 토글은 App이 합성 키로 수행
        Apply();
        ToggleRequested?.Invoke();
    }
}

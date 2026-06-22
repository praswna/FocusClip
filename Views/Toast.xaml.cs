using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FocusClip.Interop;

namespace FocusClip.Views;

/// <summary>클립 캡처 시 화면 우하단에 잠깐 뜨는 알림. (CM의 ToastManager 대응)</summary>
public partial class Toast : Window
{
    private readonly DispatcherTimer _timer;

    public Toast()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        _timer.Tick += (_, _) => { _timer.Stop(); Hide(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.MakeNoActivateToolWindow(new WindowInteropHelper(this).Handle);
    }

    public void ShowToast(string text)
    {
        // 개행이 있으면 NoWrap이라도 줄이 늘어 토스트 높이/위치가 흔들리므로 한 줄로 정리.
        BodyText.Text = string.IsNullOrEmpty(text) ? "" : text.ReplaceLineEndings(" ");
        BodyText.Visibility = Visibility.Visible;
        BodyImage.Visibility = Visibility.Collapsed;
        BodyImage.Source = null;
        ShowAndPosition();
    }

    /// <summary>이미지 클립 토스트: 텍스트 대신 썸네일을 보여준다. image가 null이면 텍스트로 폴백.</summary>
    public void ShowToast(System.Windows.Media.ImageSource? image)
    {
        if (image == null) { ShowToast("🖼 이미지"); return; }
        BodyText.Visibility = Visibility.Collapsed;
        BodyImage.Source = image;
        BodyImage.Visibility = Visibility.Visible;
        ShowAndPosition();
    }

    private void ShowAndPosition()
    {
        Show();
        UpdateLayout(); // 이미지 토스트는 높이가 달라지므로 배치 전 레이아웃 확정
        // 커서가 있는 모니터의 우하단에 표시(멀티모니터). 커서 조회 실패 시 현재 창 모니터로 폴백.
        var wa = NativeMethods.GetCursorPos(out var p)
            ? ScreenUtil.WorkAreaDipFromPhysical(p.X, p.Y, this)
            : ScreenUtil.WorkAreaDip(this);
        Left = wa.Right - ActualWidth - 16;
        Top = wa.Bottom - ActualHeight - 16;
        _timer.Stop();
        _timer.Start();
    }
}

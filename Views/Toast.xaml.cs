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
        BodyText.Text = text;
        Show();
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 16;
        Top = wa.Bottom - ActualHeight - 16;
        _timer.Stop();
        _timer.Start();
    }
}

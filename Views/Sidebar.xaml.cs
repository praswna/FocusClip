using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using FocusClip.Interop;
using FocusClip.Models;

namespace FocusClip.Views;

/// <summary>화면 왼쪽 가장자리에 항상 표시되는 고정 앱 사이드바 (FM의 사이드바 대응).</summary>
public partial class Sidebar : Window
{
    public event Action<AppEntry>? AppActivated;
    public event Action<AppEntry>? AppRightClicked;

    private bool _userMovedY; // 사용자가 핸들로 세로 위치를 옮겼는지(이후 자동 센터링 안 함)
    private IntPtr _hwnd;
    private IntPtr _winEventHook;
    private NativeMethods.WinEventDelegate? _winEventProc; // GC 방지용 참조 유지

    public Sidebar()
    {
        InitializeComponent();
        Loaded += (_, _) => PositionLeftCenter();
    }

    // WPF Topmost=True 만으로는 다른 앱이 새로 뜨면서 자기 창을 topmost 로 올리면 밀린다.
    // 포그라운드 창이 바뀌는 순간(다른 앱 실행/전환)에만 HWND_TOPMOST 를 다시 걸어 최상단 보장.
    private void OnForegroundChanged(IntPtr hook, uint ev, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == _hwnd) return; // 사이드바 자신이면 무시
        ReassertTopmost();
    }

    private void ReassertTopmost()
    {
        if (_hwnd == IntPtr.Zero || !IsVisible) return;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    // ── FM의 ::: 핸들: 세로(Y)만 이동 ──
    private void MoveHandle_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var wa = SystemParameters.WorkArea;
        double top = Top + e.VerticalChange;
        Top = Math.Max(wa.Top, Math.Min(wa.Bottom - ActualHeight, top));
        Left = wa.Left + 2; // X 고정(FM과 동일)
        _userMovedY = true;
    }

    public void SetApps(IEnumerable<AppEntry> apps) => AppList.ItemsSource = apps;

    private void AppButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is AppEntry app)
            AppActivated?.Invoke(app);
    }

    private void AppButton_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.Tag is AppEntry app)
            AppRightClicked?.Invoke(app);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.MakeNoActivateToolWindow(_hwnd);
        ReassertTopmost();

        // 포그라운드 변경 이벤트만 구독(폴링 없음). SKIPOWNPROCESS 로 자기 프로세스 이벤트는 제외.
        _winEventProc = OnForegroundChanged;
        _winEventHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_winEventHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
        base.OnClosed(e);
    }

    public void PositionLeftCenter()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + 2;
        if (_userMovedY)
        {
            // 사용자가 옮긴 세로 위치 유지(화면 밖으로 나가지 않게만 보정)
            Top = Math.Max(wa.Top, Math.Min(wa.Bottom - ActualHeight, Top));
        }
        else
        {
            Top = wa.Top + (wa.Height - ActualHeight) / 2;
        }
    }
}

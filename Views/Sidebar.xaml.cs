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

    public Sidebar()
    {
        InitializeComponent();
        Loaded += (_, _) => PositionLeftCenter();
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
        NativeMethods.MakeNoActivateToolWindow(new WindowInteropHelper(this).Handle);
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

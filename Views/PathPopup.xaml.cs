using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using FocusClip.Interop;
using FocusClip.Models;

namespace FocusClip.Views;

/// <summary>복사된 파일 경로 전용 팝업. 함축 표시(이름+축약 경로), 클릭 시 전체 경로 붙여넣기.</summary>
public partial class PathPopup : Window
{
    public event Action<ClipItem>? PathSelected;
    public event Action<ClipItem>? PathDeleteRequested;
    public event Action<ClipItem>? PathOpenRequested; // 로컬 경로/URL 열기

    /// <summary>팝업 핀(자동 닫힘 해제). true면 외부 클릭/앱 활성화에도 닫지 않음.</summary>
    public bool Pinned { get; private set; }

    private enum PathFilter { All, Local, Url }
    private PathFilter _filter = PathFilter.All;
    private ICollectionView? _view;

    public PathPopup()
    {
        InitializeComponent();
    }

    public void SetItems(IEnumerable<ClipItem> items)
    {
        _view = CollectionViewSource.GetDefaultView(items);
        _view.Filter = o => o is ClipItem c && _filter switch
        {
            PathFilter.Local => !c.IsUrl,
            PathFilter.Url => c.IsUrl,
            _ => true,
        };
        PathList.ItemsSource = _view;
    }

    // ── 로컬/URL 필터 토글 ──
    private void Filter_All_Checked(object sender, RoutedEventArgs e) => ApplyFilter(PathFilter.All);
    private void Filter_Local_Checked(object sender, RoutedEventArgs e) => ApplyFilter(PathFilter.Local);
    private void Filter_Url_Checked(object sender, RoutedEventArgs e) => ApplyFilter(PathFilter.Url);

    private void ApplyFilter(PathFilter f)
    {
        _filter = f;
        _view?.Refresh();
    }

    private void PinToggle_Changed(object sender, RoutedEventArgs e)
        => Pinned = PinToggle.IsChecked == true;

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ClipItem item)
            PathSelected?.Invoke(item);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // 카드 선택(붙여넣기)으로 전파 방지
        if (sender is FrameworkElement fe && fe.Tag is ClipItem item)
            PathDeleteRequested?.Invoke(item);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // 카드 선택(붙여넣기)으로 전파 방지
        if (sender is FrameworkElement fe && fe.Tag is ClipItem item)
            PathOpenRequested?.Invoke(item);
    }

    // ── 스크롤: 휠로 직접 스크롤(무활성 창에서도 확실히 동작) ──
    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.MakeNoActivateToolWindow(hwnd);
        HwndSource.FromHwnd(hwnd)?.AddHook(WheelHook);
    }

    private IntPtr WheelHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_MOUSEWHEEL = 0x020A;
        if (msg == WM_MOUSEWHEEL)
        {
            int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - delta);
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 기준 창(도크) 바로 아래에 배치. 아래 공간이 부족하면 위로 뒤집어 배치.
    /// avoid(클립 팝업)가 같은 쪽으로 뒤집혀 겹치면 그 위/아래로 비켜 쌓는다(가장자리 겹침 방지).
    /// </summary>
    public void ShowBelow(Window anchor, Window? avoid = null)
    {
        Show();
        var wa = SystemParameters.WorkArea;
        double left = Math.Min(anchor.Left, wa.Right - ActualWidth);
        Left = Math.Max(wa.Left, left);

        double top = anchor.Top + anchor.ActualHeight + 6;
        // 아래쪽 공간이 부족하면 도크 위로 뒤집어 배치
        if (top + ActualHeight > wa.Bottom)
            top = anchor.Top - ActualHeight - 6;

        // 클립 팝업과 세로로 겹치면 클립 팝업 기준으로 비켜 배치.
        if (avoid != null && avoid.IsVisible
            && top < avoid.Top + avoid.ActualHeight && top + ActualHeight > avoid.Top)
        {
            top = avoid.Top >= anchor.Top + anchor.ActualHeight
                ? avoid.Top + avoid.ActualHeight + 6   // 클립이 도크 아래 → 경로는 클립 아래
                : avoid.Top - ActualHeight - 6;        // 클립이 도크 위  → 경로는 클립 위
        }
        Top = Math.Max(wa.Top, Math.Min(top, wa.Bottom - ActualHeight));
    }
}

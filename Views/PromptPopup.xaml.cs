using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FocusClip.Interop;
using FocusClip.Models;

namespace FocusClip.Views;

/// <summary>자주 쓰는 프롬프트 보관함 팝업. 사용자가 직접 추가(+)/편집(✎)/삭제(✕)하며,
/// 클릭 시 직전 창에 본문 붙여넣기, 드래그 시 텍스트로 드롭. 클립보드 팝업 오른쪽에 표시된다.</summary>
public partial class PromptPopup : Window
{
    public event Action<PromptItem>? PromptSelected;
    public event Action<PromptItem>? PromptEditRequested;
    public event Action<PromptItem>? PromptDeleteRequested;
    public event Action? PromptAddRequested;
    public event Action? PinChanged;   // 핀 토글 변경(앱이 단독 핀 팝업 정리에 사용)
    public event Action? DragFailed;   // 드롭 미지원 앱에 드롭 시도 시

    /// <summary>팝업 핀(자동 닫힘 해제). true면 외부 클릭/앱 활성화에도 닫지 않음.</summary>
    public bool Pinned { get; private set; }

    public PromptPopup()
    {
        InitializeComponent();
    }

    public void SetItems(IEnumerable<PromptItem> items) => PromptList.ItemsSource = items;

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        PromptAddRequested?.Invoke();
    }

    private void PinToggle_Changed(object sender, RoutedEventArgs e)
    {
        Pinned = PinToggle.IsChecked == true;
        MoveHandle.Visibility = Pinned ? Visibility.Visible : Visibility.Collapsed; // 핀 시 이동 핸들 표시
        PinChanged?.Invoke();
    }

    // ── 이동 핸들: 핀된 팝업을 드래그로 옮긴다(작업영역 안으로 클램프) ──
    private void MoveHandle_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var wa = ScreenUtil.WorkAreaDip(this);
        Left = Math.Max(wa.Left, Math.Min(wa.Right - ActualWidth, Left + e.HorizontalChange));
        Top = Math.Max(wa.Top, Math.Min(wa.Bottom - ActualHeight, Top + e.VerticalChange));
    }

    // ── 카드 드래그: 프롬프트 본문을 OS 드래그로 끌어내 에디터 등에 텍스트로 드롭 ──
    private Point _cardDragStart;
    private PromptItem? _cardDragItem;
    private bool _cardDragHappened;

    private void Card_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _cardDragHappened = false;
        if (IsFromButton(e.OriginalSource)) { _cardDragItem = null; return; }
        _cardDragStart = e.GetPosition(null);
        _cardDragItem = (sender as FrameworkElement)?.Tag as PromptItem;
    }

    private void Card_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_cardDragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - _cardDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _cardDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _cardDragItem;
        _cardDragItem = null;
        _cardDragHappened = true;
        try
        {
            var data = PathPopup.BuildTextData(item.Text); // 경로 팝업과 동일: CF_TEXT(ANSI) + CF_UNICODETEXT
            bool cancelled = false;
            QueryContinueDragEventHandler qcd = (_, qe) => { if (qe.EscapePressed) cancelled = true; };
            var src = (UIElement)sender;
            src.QueryContinueDrag += qcd;
            var effect = DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
            src.QueryContinueDrag -= qcd;
            if (effect == DragDropEffects.None && !cancelled) DragFailed?.Invoke();
        }
        catch { }
    }

    private static bool IsFromButton(object src)
    {
        var d = src as DependencyObject;
        while (d != null)
        {
            if (d is Button) return true;
            d = (d is Visual or System.Windows.Media.Media3D.Visual3D)
                ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (_cardDragHappened) { _cardDragHappened = false; return; }
        if (sender is FrameworkElement fe && fe.Tag is PromptItem item)
            PromptSelected?.Invoke(item);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // 카드 선택(붙여넣기)으로 전파 방지
        if (sender is FrameworkElement fe && fe.Tag is PromptItem item)
            PromptEditRequested?.Invoke(item);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // 카드 선택(붙여넣기)으로 전파 방지
        if (sender is FrameworkElement fe && fe.Tag is PromptItem item)
            PromptDeleteRequested?.Invoke(item);
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
    /// 기준 창(클립 팝업 또는 도크) 오른쪽에 배치. alignBottom이면 아래 모서리를 맞춰 위로 자라게 한다
    /// — 도크 위에 뜬 클립 팝업 옆에 붙을 때 이 팝업이 더 길어도 도크·경로 팝업을 덮지 않는다.
    /// avoid(경로 팝업)가 보이면 그 오른쪽 끝까지 비켜 배치. 오른쪽 공간이 부족하면 왼쪽으로 뒤집는다.
    /// </summary>
    public void ShowRightOf(Window anchor, bool alignBottom = false, Window? avoid = null)
    {
        bool wasVisible = IsVisible;
        Show();
        if (Pinned && wasVisible) return; // 핀+이미 표시 중이면 사용자가 옮긴 위치 유지(재배치 안 함)
        var wa = ScreenUtil.WorkAreaDip(anchor); // 기준 창이 놓인 모니터 기준(멀티모니터)

        bool avoidOn = avoid != null && avoid.IsVisible;
        double rightEdge = anchor.Left + anchor.ActualWidth;                    // 비켜야 할 오른쪽 끝
        if (avoidOn) rightEdge = Math.Max(rightEdge, avoid!.Left + avoid.ActualWidth);
        double leftEdge = anchor.Left;                                          // 왼쪽 뒤집기 기준
        if (avoidOn) leftEdge = Math.Min(leftEdge, avoid!.Left);

        double left = rightEdge + 6;
        // 오른쪽 공간이 부족하면 왼쪽으로 뒤집어 배치
        if (left + ActualWidth > wa.Right)
            left = leftEdge - ActualWidth - 6;
        Left = Math.Max(wa.Left, Math.Min(left, wa.Right - ActualWidth));

        // 상단 정렬(기본) 또는 아래 모서리 정렬(위로 자람)
        double top = alignBottom ? anchor.Top + anchor.ActualHeight - ActualHeight : anchor.Top;
        Top = Math.Max(wa.Top, Math.Min(top, wa.Bottom - ActualHeight));
    }
}

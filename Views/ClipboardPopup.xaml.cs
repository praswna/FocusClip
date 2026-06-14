using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FocusClip.Interop;
using FocusClip.Models;

namespace FocusClip.Views;

/// <summary>클립보드 항목 카드 목록 팝업. (CM의 ContentPopup 대응)</summary>
public partial class ClipboardPopup : Window
{
    public event Action<ClipItem>? ClipSelected;
    public event Action<ClipItem>? ClipDeleteRequested;
    public event Action<ClipItem>? ClipPinToggled;   // C2: 카드 핀 토글
    public event Action<ClipItem>? ClipEditRequested; // C4/C5: 카드 우클릭 편집

    /// <summary>C1: 팝업 핀(자동 닫힘 해제). true면 앱 활성화/클립 선택에도 닫지 않음.</summary>
    public bool Pinned { get; private set; }

    public ClipboardPopup()
    {
        InitializeComponent();
    }

    public void SetItems(IEnumerable<ClipItem> items) => ClipList.ItemsSource = items;

    // ── 카드 드래그: 내용을 OS 드래그로 끌어내 다른 앱/탐색기에 드롭 (CM mouseMoveEvent 대응) ──
    private Point _cardDragStart;
    private ClipItem? _cardDragItem;
    private bool _cardDragHappened;

    private void Card_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _cardDragHappened = false;
        if (IsFromButton(e.OriginalSource)) { _cardDragItem = null; return; } // 버튼 누름은 드래그 아님
        _cardDragStart = e.GetPosition(null);
        _cardDragItem = (sender as FrameworkElement)?.Tag as ClipItem;
    }

    private void Card_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_cardDragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - _cardDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _cardDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _cardDragItem;
        _cardDragItem = null;
        _cardDragHappened = true; // 뒤따르는 Card_Click(복사+붙여넣기) 억제
        try
        {
            var data = new DataObject();
            if (item.IsImage)
            {
                if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
                    data.SetFileDropList(new StringCollection { item.FilePath });
                if (item.FullImage != null) data.SetImage(item.FullImage);
            }
            else
            {
                data.SetText(item.Text);
            }
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
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
        if (_cardDragHappened) { _cardDragHappened = false; return; } // 드래그였으면 복사 안 함
        if (sender is FrameworkElement fe && fe.Tag is ClipItem item)
            ClipSelected?.Invoke(item);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // 카드 선택(복사)으로 전파 방지
        if (sender is FrameworkElement fe && fe.Tag is ClipItem item)
            ClipEditRequested?.Invoke(item);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // 카드 선택(복사)으로 전파되지 않게
        if (sender is FrameworkElement fe && fe.Tag is ClipItem item)
            ClipDeleteRequested?.Invoke(item);
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.Tag is ClipItem item)
            ClipPinToggled?.Invoke(item);
    }

    private void PinToggle_Changed(object sender, RoutedEventArgs e)
        => Pinned = PinToggle.IsChecked == true;

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
        // 폴백: 무활성 창에 WM_MOUSEWHEEL 이 와도 라우팅이 안 되는 경우 직접 스크롤 전달
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

    /// <summary>지정한 기준 창(런처 도크) 바로 위에 배치한다(사용자 요청: CM 팝업을 도크 위로).</summary>
    public void ShowAbove(Window anchor)
    {
        Show();
        var wa = SystemParameters.WorkArea;
        double left = Math.Min(anchor.Left, wa.Right - ActualWidth);
        Left = Math.Max(wa.Left, left);
        double top = anchor.Top - ActualHeight - 6;
        Top = Math.Max(wa.Top, top);
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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

    private enum ClipFilter { All, Text, Image }
    private ClipFilter _filter = ClipFilter.All;
    private ICollectionView? _view;

    public ClipboardPopup()
    {
        InitializeComponent();
    }

    public void SetItems(IEnumerable<ClipItem> items)
    {
        _view = CollectionViewSource.GetDefaultView(items);
        _view.Filter = o => o is ClipItem c && _filter switch
        {
            ClipFilter.Text => !c.IsImage,
            ClipFilter.Image => c.IsImage,
            _ => true,
        };
        ClipList.ItemsSource = _view;
    }

    // ── 텍스트/이미지 필터 토글 ──
    private void Filter_All_Checked(object sender, RoutedEventArgs e) => ApplyFilter(ClipFilter.All);
    private void Filter_Text_Checked(object sender, RoutedEventArgs e) => ApplyFilter(ClipFilter.Text);
    private void Filter_Image_Checked(object sender, RoutedEventArgs e) => ApplyFilter(ClipFilter.Image);

    private void ApplyFilter(ClipFilter f)
    {
        _filter = f;
        _view?.Refresh();
    }

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
                // 저장된 PNG 파일이 있으면 파일 드롭만 제공한다.
                // SetImage 는 원본 비트맵을 드래그 중 DIB(CF_DIB)로 동기 직렬화하므로
                // 큰 캡처일수록 매우 느리다. 대상(탐색기·편집기·채팅 등)은 파일 드롭이면
                // 충분하므로, 비트맵은 파일이 아직 저장되지 않은 경우의 폴백으로만 첨부한다.
                if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
                    data.SetFileDropList(new StringCollection { item.FilePath });
                else if (item.FullImage != null)
                    data.SetImage(item.FullImage);
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
        // 위쪽 공간이 부족하면(클램프 시 도크와 겹침) 도크 아래로 뒤집어 배치
        if (top < wa.Top)
            top = anchor.Top + anchor.ActualHeight + 6;
        Top = Math.Max(wa.Top, Math.Min(top, wa.Bottom - ActualHeight));
    }
}

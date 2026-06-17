using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FocusClip.Interop;
using FocusClip.Models;
using FocusClip.Services;

namespace FocusClip.Views;

/// <summary>클립보드 항목 카드 목록 팝업. 클릭=붙여넣기, 드래그=다른 앱에 직접 드롭. (CM의 ContentPopup 대응)</summary>
public partial class ClipboardPopup : Window
{
    public event Action<ClipItem>? ClipSelected;
    public event Action<ClipItem>? ClipDeleteRequested;
    public event Action<ClipItem>? ClipPinToggled;   // C2: 카드 핀 토글
    public event Action<ClipItem>? ClipEditRequested; // C4/C5: 카드 우클릭 편집
    public event Action<ClipItem>? ClipOpenRequested; // 저장된 본문 파일 위치 열기
    public event Action? OpenFolderRequested;         // 헤더 파일 수 클릭 → 저장 폴더 열기
    public event Action? DragFailed;                  // P001: 드롭 미지원 앱에 드롭 시도 시

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
            DataObject data;
            if (item.IsImage)
            {
                // 저장된 PNG 파일을 그대로 파일 드롭으로 넘긴다 — 우리 쪽 복사·인코딩 없음(드래그 즉시 시작).
                // Copy 만 허용하므로 원본은 이동되지 않는다. 파일이 아직 없으면(방금 캡처) 비트맵 폴백.
                data = new DataObject();
                if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
                    data.SetFileDropList(new StringCollection { item.FilePath });
                else if (item.FullImage != null)
                    data.SetImage(item.FullImage);
            }
            else
            {
                // SetText 로 CF_TEXT(ANSI 변환 포함)+CF_UNICODETEXT 등록 → 주소창 등 다양한 대상 호환.
                data = PathPopup.BuildTextData(item.Text);
            }
            bool cancelled = false; // Esc 취소는 DragFailed 로 보지 않는다.
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

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // 카드 선택(복사)으로 전파 방지
        if (sender is FrameworkElement fe && fe.Tag is ClipItem item)
            ClipOpenRequested?.Invoke(item);
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

    /// <summary>저장 폴더(media)의 파일 수를 헤더에 표시. 팝업이 보관 개수(최근 20)만 보여주는 동안 실제 보관된 파일 총량을 안내.
    /// 본문 삭제가 완전 수동이라 폴더가 무한정 커질 수 있으므로, 디렉터리 열거는 백그라운드에서 하고 UI는 블로킹하지 않는다.</summary>
    private void UpdateFolderCount()
    {
        FolderCount.Text = "📁 …"; // 즉시 표시 — 팝업 오픈을 막지 않는다
        string dir = ClipboardService.SaveDir;
        System.Threading.Tasks.Task.Run(() =>
        {
            int n = 0;
            try { if (System.IO.Directory.Exists(dir)) n = System.IO.Directory.EnumerateFiles(dir).Count(); }
            catch { }
            Dispatcher.BeginInvoke(() => FolderCount.Text = $"📁 {n}");
        });
    }

    private void FolderCount_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenFolderRequested?.Invoke();
    }

    /// <summary>지정한 기준 창(런처 도크) 바로 위에 배치한다(사용자 요청: CM 팝업을 도크 위로). 위쪽 공간이 부족하면 도크 아래로 뒤집는다.</summary>
    public void ShowAbove(Window anchor)
    {
        UpdateFolderCount();
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

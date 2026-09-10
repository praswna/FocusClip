using System;
using System.Collections.Generic;
using System.ComponentModel;
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

/// <summary>복사된 파일 경로 전용 팝업. 함축 표시(이름+축약 경로), 클릭 동작은 설정에서 열기/복사 선택, 드래그 시 텍스트로 드롭.</summary>
public partial class PathPopup : Window
{
    public event Action<ClipItem>? PathSelected;      // 📋 → 클립보드 복사 + 직전 창에 붙여넣기
    public event Action<ClipItem>? PathDeleteRequested;
    public event Action<ClipItem>? PathOpenRequested; // 카드 클릭 → 로컬 경로/URL 열기(기본 동작)
    public event Action<ClipItem>? PathPinToggled;    // 카드 고정핀 토글
    public event Action? PinChanged;                  // 핀 토글 변경(앱이 단독 핀 팝업 정리에 사용)
    public event Action? CollapseChanged;             // 접기/펼치기 후 팝업 묶음 재배치
    public event Action? DragFailed;                  // P001: 드롭 미지원 앱에 드롭 시도 시

    /// <summary>팝업 핀(자동 닫힘 해제). true면 외부 클릭/앱 활성화에도 닫지 않음.</summary>
    public bool Pinned { get; private set; }

    private enum PathFilter { All, Local, Url }
    private PathFilter _filter = PathFilter.All;
    private ICollectionView? _view;
    private readonly double _expandedMinHeight;

    public PathPopup()
    {
        InitializeComponent();
        _expandedMinHeight = MinHeight;
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) Expand(); };
    }

    public void SetItems(IEnumerable<ClipItem> items)
    {
        _view = CollectionViewSource.GetDefaultView(items);
        // 보관(★)된 경로는 오른쪽 고정 팝업(PinnedPopup)이 압축 카드로 맡는다 — 여기는 미고정 경로만.
        _view.Filter = o => o is ClipItem c && !c.Pinned && _filter switch
        {
            PathFilter.Local => !c.IsUrl,
            PathFilter.Url => c.IsUrl,
            _ => true,
        };
        PathList.ItemsSource = _view;
    }

    /// <summary>핀 토글 후 목록을 다시 거른다(항목의 Pinned 변경은 컬렉션 변경이 아니라 뷰가 스스로 알아채지 못한다).</summary>
    public void RefreshItems() => _view?.Refresh();


    private void Collapse_Click(object sender, RoutedEventArgs e)
    {
        bool collapse = Scroller.Visibility == Visibility.Visible;
        SetCollapsed(collapse);
        CollapseChanged?.Invoke();
    }

    public void Expand() => SetCollapsed(false);

    private void SetCollapsed(bool collapse)
    {
        Scroller.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
        MinHeight = collapse ? 0 : _expandedMinHeight;
        CollapseButton.Content = collapse ? "▾" : "▴";
        CollapseButton.ToolTip = collapse ? "펼치기" : "접기";
        PopupSizing.Refresh(this);
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
    {
        Pinned = PinToggle.IsChecked == true;
        MoveHandle.Visibility = Pinned ? Visibility.Visible : Visibility.Collapsed; // 핀 시 이동 핸들 표시
        PinChanged?.Invoke();
    }

    // ── 이동 핸들: 핀된 팝업을 드래그로 옮긴다(작업영역 안으로 클램프) ──
    private void MoveHandle_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var wa = ScreenUtil.WorkAreaDip(this);
        Left = System.Math.Max(wa.Left, System.Math.Min(wa.Right - ActualWidth, Left + e.HorizontalChange));
        Top = System.Math.Max(wa.Top, System.Math.Min(wa.Bottom - ActualHeight, Top + e.VerticalChange));
    }

    // ── 카드 드래그: 경로 항목을 OS 드래그로 끌어내 탐색기·에디터 등에 드롭 ──
    private Point _cardDragStart;
    private ClipItem? _cardDragItem;
    private bool _cardDragHappened;

    private void Card_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _cardDragHappened = false;
        if (IsFromButton(e.OriginalSource)) { _cardDragItem = null; return; }
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
        _cardDragHappened = true;
        try
        {
            var data = BuildTextData(item.Text);
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

    // SetData(DataFormats.Text, ...) 대신 SetText 를 써야 한다.
    // SetData 는 ANSI 변환 없이 raw 유니코드 바이트를 CF_TEXT 에 넣어 한글이 한자로 깨짐.
    internal static DataObject BuildTextData(string text)
    {
        var data = new DataObject();
        data.SetText(text); // CF_TEXT(ANSI 변환) + CF_UNICODETEXT 양쪽 등록
        return data;
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

    /// <summary>카드 클릭 = 앱에서 설정된 열기/복사 동작. 붙여넣기는 📋 버튼으로 분리했다.</summary>
    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (_cardDragHappened) { _cardDragHappened = false; return; }
        if (sender is FrameworkElement fe && fe.Tag is ClipItem item)
            PathOpenRequested?.Invoke(item);
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // 카드 선택(열기)으로 전파 방지
        if (sender is FrameworkElement fe && fe.Tag is ClipItem item)
            PathSelected?.Invoke(item);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // 카드 선택(열기)으로 전파 방지
        if (sender is FrameworkElement fe && fe.Tag is ClipItem item)
            PathDeleteRequested?.Invoke(item);
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // 카드 선택(열기)으로 전파 방지
        if (sender is FrameworkElement fe && fe.Tag is ClipItem item)
            PathPinToggled?.Invoke(item);
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

    /// <summary>배치 전에 표시하고, 고정 열 그리드에 넣어도 되는지 알린다(위치는 PopupPlacement 가 한꺼번에 계산).
    /// 핀(📌)으로 사용자가 직접 옮겨 둔 창이면 false — 그 자리를 그대로 지킨다.</summary>
    public bool ShowForLayout()
    {
        bool wasVisible = IsVisible;
        Show();
        return !(Pinned && wasVisible);
    }
}

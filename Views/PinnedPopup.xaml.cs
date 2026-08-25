using System;
using System.Collections.Generic;
using System.Collections.Specialized;
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

/// <summary>고정(📌)한 항목만 모아 관리하는 압축 팝업. 클립보드·경로 팝업이 각각 하나씩 쓰며,
/// 기본 팝업 오른쪽에 뜬다. 기본 팝업은 미고정 항목만 보여주므로 고정 카드가 목록을 잡아먹지 않는다.
/// 카드는 썸네일+두 줄 텍스트의 한 줄짜리 압축 형태고, 동작(클립 클릭=붙여넣기·경로 클릭=열기, 드래그=드롭)은 기본 팝업과 같다.</summary>
public partial class PinnedPopup : Window
{
    public event Action<ClipItem>? ItemSelected;      // 클립 카드 클릭·경로 카드 📋 → 붙여넣기
    public event Action<ClipItem>? UnpinRequested;    // 📌 → 고정 해제(기본 팝업으로 돌아감)
    public event Action<ClipItem>? DeleteRequested;   // ✕ → 목록에서 제거
    public event Action<ClipItem>? OpenRequested;     // 클립 📂 → 저장 위치 열기 / 경로 카드 클릭 → 경로·URL 열기
    public event Action<ClipItem>? EditRequested;     // ✎ → 텍스트·이미지 편집(클립 전용)
    public event Action<ClipItem>? PromoteRequested;  // 🔖 → 프롬프트 보관함으로(텍스트 클립 전용)
    public event Action? PinChanged;                  // 팝업 핀 토글 변경(앱이 단독 핀 팝업 정리에 사용)
    public event Action? DragFailed;                  // 드롭 미지원 앱에 드롭 시도 시

    /// <summary>팝업 핀(자동 닫힘 해제). true면 외부 클릭/앱 활성화에도 닫지 않음.</summary>
    public bool Pinned { get; private set; }

    private CollectionViewSource? _cvs;

    public PinnedPopup()
    {
        InitializeComponent();
    }

    /// <summary>헤더 제목("고정 클립" / "고정 경로").</summary>
    public string Header
    {
        get => HeaderText.Text;
        set => HeaderText.Text = value;
    }

    /// <summary>원본 컬렉션(Items 또는 Paths)에서 고정 항목만 걸러 보여준다.
    /// 기본 뷰(GetDefaultView)는 기본 팝업이 자기 필터로 쓰고 있으므로, 전용 뷰를 따로 만든다(필터 충돌 방지).</summary>
    public void SetItems(IEnumerable<ClipItem> items)
    {
        _cvs = new CollectionViewSource { Source = items };
        var view = _cvs.View;
        if (view == null) return;
        view.Filter = o => o is ClipItem c && c.Pinned;
        PinList.ItemsSource = view;
        UpdateCount();
    }

    /// <summary>목록을 다시 거른다. 항목의 Pinned 변경은 컬렉션 변경이 아니라 뷰가 스스로 알아채지 못하므로,
    /// 핀 토글 후 앱이 기본 팝업과 함께 호출해 준다.</summary>
    public void RefreshItems()
    {
        _cvs?.View?.Refresh();
        UpdateCount();
    }

    /// <summary>현재 보이는(고정된) 항목 수.</summary>
    public int Count
    {
        get
        {
            var view = _cvs?.View;
            if (view == null) return 0;
            int n = 0;
            foreach (var _ in view) n++;   // ICollectionView 는 필터된 항목만 열거한다
            return n;
        }
    }

    private void UpdateCount()
    {
        int n = Count;
        CountText.Text = n > 0 ? n.ToString() : "";
    }

    // ── 카드 드래그: 내용을 OS 드래그로 끌어내 다른 앱/탐색기에 드롭(기본 팝업과 동일 규칙) ──
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
                // 저장된 PNG를 그대로 파일 드롭으로(우리 쪽 복사·인코딩 없음). 아직 파일이 없으면 비트맵 폴백.
                data = new DataObject();
                if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
                    data.SetFileDropList(new StringCollection { item.FilePath });
                else if (item.FullImage != null)
                    data.SetImage(item.FullImage);
            }
            else
            {
                data = PathPopup.BuildTextData(item.Text); // CF_TEXT(ANSI) + CF_UNICODETEXT
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

    /// <summary>카드 클릭 — 클립은 붙여넣기, 경로는 열기(기본 팝업과 같은 규칙).</summary>
    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (_cardDragHappened) { _cardDragHappened = false; return; } // 드래그였으면 복사 안 함
        if (sender is not FrameworkElement fe || fe.Tag is not ClipItem item) return;
        if (item.IsPath) OpenRequested?.Invoke(item);
        else ItemSelected?.Invoke(item);
    }

    /// <summary>경로 카드의 📋 — 클릭이 '열기'로 갔으므로 붙여넣기는 이 버튼으로.</summary>
    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // 카드 선택(열기)으로 전파 방지
        if (sender is FrameworkElement fe && fe.Tag is ClipItem item)
            ItemSelected?.Invoke(item);
    }

    private void Unpin_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // 카드 선택(붙여넣기)으로 전파 방지
        if (sender is FrameworkElement fe && fe.Tag is ClipItem item)
            UnpinRequested?.Invoke(item);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.Tag is ClipItem item)
            EditRequested?.Invoke(item);
    }

    private void Promote_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.Tag is ClipItem item)
            PromoteRequested?.Invoke(item);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.Tag is ClipItem item)
            OpenRequested?.Invoke(item);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.Tag is ClipItem item)
            DeleteRequested?.Invoke(item);
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

    /// <summary>기본 팝업이 놓이는(비어서 안 떠 있으면 놓였을) 자리 오른쪽에 배치.
    /// 창이 아니라 자리(Rect)를 받는 덕에, 기본 팝업이 비어도 고정 팝업은 늘 같은 열·같은 높이에 뜬다.
    /// stackAvoid 와 세로로 겹치면 같은 열에서 위/아래로 비켜 쌓는다.</summary>
    public void ShowRightOf(Rect anchorSlot, Window monitorRef, bool alignBottom = false, Window? stackAvoid = null)
    {
        bool wasVisible = IsVisible;
        Show();
        UpdateCount();
        if (Pinned && wasVisible) return; // 핀+이미 표시 중이면 사용자가 옮긴 위치 유지(재배치 안 함)
        PopupPlacement.PlaceRightOf(this, anchorSlot, monitorRef, alignBottom, stackAvoid);
    }
}

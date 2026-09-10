using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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

/// <summary>
/// 클립·경로·프롬프트를 탭으로 묶은 단일 팝업. 예전에는 목록마다 창이 따로 떠서(클립·경로·고정 2종·프롬프트)
/// 도크 주변 배치가 내용에 따라 흔들렸는데, 창 하나로 합쳐 그 문제를 없앴다.
/// 창 높이는 고정이라 탭을 바꿔도 크기·위치가 그대로다. 고정(★)한 항목은 각 탭 목록 맨 위에 압축 카드로 나온다.
/// </summary>
public partial class MainPopup : Window
{
    // 클립 카드(고정 카드 포함) 동작
    public event Action<ClipItem>? ClipSelected;         // 카드 클릭 / 경로 📋 → 클립보드 + 직전 창에 붙여넣기
    public event Action<ClipItem>? ClipEditRequested;    // ✎ 텍스트·이미지 편집
    public event Action<ClipItem>? ClipPromoteRequested; // 🔖 프롬프트 보관함으로
    public event Action<ClipItem>? ClipOpenRequested;    // 📂 저장된 본문 파일 위치 열기
    public event Action<ClipItem>? PathPrimaryRequested; // 경로 카드 클릭 → 설정된 열기/복사 동작
    public event Action<ClipItem>? PinToggleRequested;   // ★ 보관 고정/해제(클립·경로 공통)
    public event Action<ClipItem>? ItemDeleteRequested;  // ✕ 목록에서 제거(클립·경로 공통)

    // 프롬프트 동작
    public event Action<PromptItem>? PromptSelected;
    public event Action<PromptItem>? PromptEditRequested;
    public event Action<PromptItem>? PromptDeleteRequested;
    public event Action? PromptAddRequested;

    public event Action? OpenFolderRequested; // 헤더 파일 수 클릭 → 저장 폴더 열기
    public event Action? PinChanged;          // 창 고정(📌) 토글 변경
    public event Action? CollapseChanged;     // 접기/펼치기로 창 높이가 바뀜 → 앱이 다시 배치
    public event Action? DragFailed;          // P001: 드롭 미지원 앱에 드롭 시도 시

    /// <summary>창 고정(자동 닫힘 해제). true면 외부 클릭/CapsLock 에도 닫지 않는다.</summary>
    public bool Pinned { get; private set; }

    /// <summary>펼친 상태의 창 높이(고정). 탭을 바꿔도 창 크기가 변하지 않게 한다.</summary>
    private const double ExpandedHeight = 460;

    /// <summary>도크와 팝업 사이 간격(px, DIP).</summary>
    private const double Gap = 6;

    private enum ClipFilter { All, Text, Image }
    private enum PathFilter { All, Local, Url }

    private ClipFilter _clipFilter = ClipFilter.All;
    private PathFilter _pathFilter = PathFilter.All;

    private ICollectionView? _clipView;            // 미고정 클립(기본 뷰 + 필터)
    private ICollectionView? _pathView;            // 미고정 경로(기본 뷰 + 필터)
    private CollectionViewSource? _pinnedClips;    // 고정 클립 — 기본 뷰는 위가 쓰므로 전용 뷰를 따로 둔다
    private CollectionViewSource? _pinnedPaths;    // 고정 경로

    public MainPopup()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) Expand(); };
    }

    /// <summary>세 목록을 한 번에 연결한다. 고정(★) 항목은 각 탭 맨 위 압축 카드로, 나머지는 아래 목록으로 갈린다.</summary>
    public void SetItems(IEnumerable<ClipItem> clips, IEnumerable<ClipItem> paths, IEnumerable<PromptItem> prompts)
    {
        _clipView = CollectionViewSource.GetDefaultView(clips);
        _clipView.Filter = o => o is ClipItem c && !c.Pinned && _clipFilter switch
        {
            ClipFilter.Text => !c.IsImage,
            ClipFilter.Image => c.IsImage,
            _ => true,
        };
        ClipList.ItemsSource = _clipView;

        _pinnedClips = new CollectionViewSource { Source = clips };
        if (_pinnedClips.View != null) _pinnedClips.View.Filter = o => o is ClipItem c && c.Pinned;
        PinnedClipList.ItemsSource = _pinnedClips.View;

        _pathView = CollectionViewSource.GetDefaultView(paths);
        _pathView.Filter = o => o is ClipItem c && !c.Pinned && _pathFilter switch
        {
            PathFilter.Local => !c.IsUrl,
            PathFilter.Url => c.IsUrl,
            _ => true,
        };
        PathList.ItemsSource = _pathView;

        _pinnedPaths = new CollectionViewSource { Source = paths };
        if (_pinnedPaths.View != null) _pinnedPaths.View.Filter = o => o is ClipItem c && c.Pinned;
        PinnedPathList.ItemsSource = _pinnedPaths.View;

        PromptList.ItemsSource = prompts;
    }

    /// <summary>목록을 다시 거른다. 항목의 Pinned 변경은 컬렉션 변경이 아니라 뷰가 스스로 알아채지 못한다.</summary>
    public void RefreshItems()
    {
        _clipView?.Refresh();
        _pathView?.Refresh();
        _pinnedClips?.View?.Refresh();
        _pinnedPaths?.View?.Refresh();
    }

    /// <summary>도크 바로 위에 왼쪽을 맞춰 표시(위가 좁으면 도크 아래로). 높이가 고정이라 매번 같은 자리에 뜬다.</summary>
    public void ShowAboveDock(Window dock)
    {
        bool wasVisible = IsVisible;
        Show();
        UpdateFolderCount();
        if (Pinned && wasVisible) return; // 핀으로 직접 옮겨 둔 자리는 건드리지 않는다

        var wa = ScreenUtil.WorkAreaDip(dock); // 도크가 놓인 모니터 기준(멀티모니터)
        Left = Clamp(dock.Left, wa.Left, wa.Right - ActualWidth);
        double top = dock.Top - ActualHeight - Gap;
        if (top < wa.Top) top = dock.Top + dock.ActualHeight + Gap;
        Top = Clamp(top, wa.Top, wa.Bottom - ActualHeight);
    }

    private static double Clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(v, hi));

    // ── 탭 전환 ──
    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        // XAML 파싱 중 IsChecked="True" 로 먼저 불릴 수 있다(그 시점엔 탭 패널이 아직 null).
        if (TabClip == null || TabPath == null || ClipTab == null || PathTab == null
            || PromptTab == null || FolderCount == null) return;

        bool clip = TabClip.IsChecked == true;
        bool path = TabPath.IsChecked == true;
        ClipTab.Visibility = clip ? Visibility.Visible : Visibility.Collapsed;
        PathTab.Visibility = path ? Visibility.Visible : Visibility.Collapsed;
        PromptTab.Visibility = !clip && !path ? Visibility.Visible : Visibility.Collapsed;
        FolderCount.Visibility = clip ? Visibility.Visible : Visibility.Collapsed; // 저장 폴더 수는 클립 탭에서만
    }

    // ── 필터 ──
    private void ClipFilter_All(object sender, RoutedEventArgs e) => ApplyClipFilter(ClipFilter.All);
    private void ClipFilter_Text(object sender, RoutedEventArgs e) => ApplyClipFilter(ClipFilter.Text);
    private void ClipFilter_Image(object sender, RoutedEventArgs e) => ApplyClipFilter(ClipFilter.Image);

    private void ApplyClipFilter(ClipFilter f)
    {
        _clipFilter = f;
        _clipView?.Refresh();
    }

    private void PathFilter_All(object sender, RoutedEventArgs e) => ApplyPathFilter(PathFilter.All);
    private void PathFilter_Local(object sender, RoutedEventArgs e) => ApplyPathFilter(PathFilter.Local);
    private void PathFilter_Url(object sender, RoutedEventArgs e) => ApplyPathFilter(PathFilter.Url);

    private void ApplyPathFilter(PathFilter f)
    {
        _pathFilter = f;
        _pathView?.Refresh();
    }

    // ── 접기/펼치기 ──
    private void Collapse_Click(object sender, RoutedEventArgs e)
    {
        SetCollapsed(TabArea.Visibility == Visibility.Visible);
        CollapseChanged?.Invoke();
    }

    public void Expand() => SetCollapsed(false);

    private void SetCollapsed(bool collapse)
    {
        TabArea.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
        // 접으면 헤더만 남는 높이로 줄이고(SizeToContent), 펼치면 고정 높이로 되돌린다.
        if (collapse)
        {
            SizeToContent = SizeToContent.Height;
        }
        else
        {
            SizeToContent = SizeToContent.Manual;
            Height = ExpandedHeight;
        }
        CollapseButton.Content = collapse ? "▾" : "▴";
        CollapseButton.ToolTip = collapse ? "펼치기" : "접기";
    }

    // ── 창 고정(📌) + 이동 핸들 ──
    private void PinToggle_Changed(object sender, RoutedEventArgs e)
    {
        Pinned = PinToggle.IsChecked == true;
        MoveHandle.Visibility = Pinned ? Visibility.Visible : Visibility.Collapsed; // 핀 시 이동 핸들 표시
        PinChanged?.Invoke();
    }

    private void MoveHandle_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var wa = ScreenUtil.WorkAreaDip(this);
        Left = Math.Max(wa.Left, Math.Min(wa.Right - ActualWidth, Left + e.HorizontalChange));
        Top = Math.Max(wa.Top, Math.Min(wa.Bottom - ActualHeight, Top + e.VerticalChange));
    }

    // ── 카드 드래그: 내용을 OS 드래그로 끌어내 다른 앱/탐색기에 드롭 ──
    private Point _dragStart;
    private object? _dragItem;
    private bool _dragHappened;

    private void Card_Down(object sender, MouseButtonEventArgs e)
    {
        _dragHappened = false;
        if (IsFromButton(e.OriginalSource)) { _dragItem = null; return; } // 버튼 누름은 드래그 아님
        _dragStart = e.GetPosition(null);
        _dragItem = (sender as FrameworkElement)?.Tag;
    }

    private void Card_Move(object sender, MouseEventArgs e)
    {
        if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        object item = _dragItem;
        _dragItem = null;
        _dragHappened = true; // 뒤따르는 카드 클릭(붙여넣기/열기) 억제
        try
        {
            var data = BuildDragData(item);
            if (data == null) return;
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

    private static DataObject? BuildDragData(object item) => item switch
    {
        ClipItem c when c.IsImage => ImageDragData(c),
        ClipItem c => BuildTextData(c.Text),
        PromptItem p => BuildTextData(p.Text),
        _ => null,
    };

    /// <summary>저장된 PNG 를 그대로 파일 드롭으로 넘긴다(우리 쪽 복사·인코딩 없음).
    /// Copy 만 허용하므로 원본은 이동되지 않는다. 파일이 아직 없으면(방금 캡처) 비트맵 폴백.</summary>
    private static DataObject ImageDragData(ClipItem item)
    {
        var data = new DataObject();
        if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
            data.SetFileDropList(new StringCollection { item.FilePath });
        else if (ClipboardService.LoadImage(item) is { } image)
            data.SetImage(image);
        return data;
    }

    // SetData(DataFormats.Text, ...) 대신 SetText 를 써야 한다.
    // SetData 는 ANSI 변환 없이 raw 유니코드 바이트를 CF_TEXT 에 넣어 한글이 한자로 깨짐.
    private static DataObject BuildTextData(string text)
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

    /// <summary>드래그였으면 뒤따르는 클릭을 한 번 삼킨다.</summary>
    private bool ConsumeDrag()
    {
        if (!_dragHappened) return false;
        _dragHappened = false;
        return true;
    }

    // ── 카드 클릭 ──
    private void ClipCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (ConsumeDrag()) return;
        if (sender is FrameworkElement fe && fe.Tag is ClipItem item) ClipSelected?.Invoke(item);
    }

    private void PathCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (ConsumeDrag()) return;
        if (sender is FrameworkElement fe && fe.Tag is ClipItem item) PathPrimaryRequested?.Invoke(item);
    }

    /// <summary>고정 카드 클릭 — 클립은 붙여넣기, 경로는 열기(아래 목록 카드와 같은 규칙).</summary>
    private void PinCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (ConsumeDrag()) return;
        if (sender is not FrameworkElement fe || fe.Tag is not ClipItem item) return;
        if (item.IsPath) PathPrimaryRequested?.Invoke(item);
        else ClipSelected?.Invoke(item);
    }

    private void PromptCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (ConsumeDrag()) return;
        if (sender is FrameworkElement fe && fe.Tag is PromptItem item) PromptSelected?.Invoke(item);
    }

    // ── 카드 버튼(e.Handled 로 카드 클릭 전파를 막는다) ──
    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.Tag is ClipItem item) PinToggleRequested?.Invoke(item);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.Tag is ClipItem item) ClipEditRequested?.Invoke(item);
    }

    private void Promote_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.Tag is ClipItem item) ClipPromoteRequested?.Invoke(item);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.Tag is ClipItem item) ClipOpenRequested?.Invoke(item);
    }

    /// <summary>경로 카드의 📋 — 카드 클릭이 '열기'라 붙여넣기는 이 버튼으로.</summary>
    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.Tag is ClipItem item) ClipSelected?.Invoke(item);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.Tag is ClipItem item) ItemDeleteRequested?.Invoke(item);
    }

    private void PromptEdit_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.Tag is PromptItem item) PromptEditRequested?.Invoke(item);
    }

    private void PromptDelete_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.Tag is PromptItem item) PromptDeleteRequested?.Invoke(item);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        PromptAddRequested?.Invoke();
    }

    private void FolderCount_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenFolderRequested?.Invoke();
    }

    // ── 스크롤: 휠로 직접 스크롤(무활성 창에서도 확실히 동작) ──
    private ScrollViewer ActiveScroller =>
        PathTab.Visibility == Visibility.Visible ? PathScroller
        : PromptTab.Visibility == Visibility.Visible ? PromptScroller
        : ClipScroller;

    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
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
            var sv = ActiveScroller;
            sv.ScrollToVerticalOffset(sv.VerticalOffset - delta);
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>저장 폴더(media)의 파일 수를 헤더에 표시. 본문 삭제가 완전 수동이라 폴더가 무한정 커질 수 있으므로,
    /// 디렉터리 열거는 백그라운드에서 하고 UI는 블로킹하지 않는다.</summary>
    private void UpdateFolderCount()
    {
        FolderCount.Text = "저장 …"; // 즉시 표시 — 팝업 오픈을 막지 않는다
        string dir = ClipboardService.SaveDir; // Screenshots 루트(텍스트·이미지 공용)
        Task.Run(() =>
        {
            int n = 0;
            // FocusClip 이 저장한 본문(clip_*.txt / clip_*.png)만 센다 — OS 스크린샷·타 파일 제외, 비재귀.
            try
            {
                if (Directory.Exists(dir))
                    n = Directory.EnumerateFiles(dir, "clip_*.*")
                        .Count(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                                 || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
            }
            catch { }
            Dispatcher.BeginInvoke(() => FolderCount.Text = $"저장 {n}");
        });
    }
}

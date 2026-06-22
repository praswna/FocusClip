using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FocusClip.Interop;
using FocusClip.Models;

namespace FocusClip.Views;

public partial class LauncherDock : Window
{
    /// <summary>아이콘 클릭으로 앱 활성화 요청.</summary>
    public event Action<AppEntry>? AppActivated;
    /// <summary>아이콘 우클릭으로 항상-위 토글 요청.</summary>
    public event Action<AppEntry>? AppRightClicked;
    /// <summary>F1: 드래그로 순서가 바뀜 → 저장/사이드바 갱신 요청.</summary>
    public event Action? AppsReordered;
    /// <summary>F1: 도크 밖으로 드롭 → 앱 제거 요청.</summary>
    public event Action<AppEntry>? AppRemoveRequested;
    /// <summary>드래그가 구분선을 넘어 고정 개수가 바뀜 → 새 PinnedCount 전달.</summary>
    public event Action<int>? PinnedCountChanged;
    /// <summary>[+] 버튼 클릭 → 설정 열기 요청(FM의 + 버튼).</summary>
    public event Action? AddRequested;
    /// <summary>[✕] 버튼 클릭 → 프로그램 종료 요청.</summary>
    public event Action? ExitRequested;

    private const double SepGapPx = 8; // 구분선 좌우 여백(경계 컨테이너 좌측 여백)

    private ObservableCollection<AppEntry>? _apps; // 순서 변경 대상(공유 컬렉션)
    private Point _dragStart;
    private AppEntry? _dragItem;
    private bool _justDragged;
    private int _pinnedCount = 4; // 고정구간 경계(구분선 위치)
    private FrameworkElement? _paddedContainer; // 구분선 여백을 적용한 경계 컨테이너(이동 시 리셋용)

    public LauncherDock()
    {
        InitializeComponent();
        // 구분선은 아이콘 개수/크기 변화 시에만 재계산(LayoutUpdated 매 패스 호출 회피 — 성능).
        AppList.SizeChanged += (_, _) => UpdateSeparator();
    }

    public void SetApps(IEnumerable<AppEntry> apps)
    {
        _apps = apps as ObservableCollection<AppEntry>;
        AppList.ItemsSource = apps;
        // 추가/제거/순서변경 시 숫자키 라벨 재계산(설정 창이 같은 컬렉션을 직접 수정함).
        if (_apps != null) _apps.CollectionChanged += (_, _) => UpdateHotkeyLabels();
        UpdateHotkeyLabels();
    }

    /// <summary>고정구간 개수 설정 → 구분선 위치 + 숫자키 라벨 갱신(F2 변경 즉시 반영).</summary>
    public void SetPinnedCount(int n)
    {
        _pinnedCount = n;
        UpdateSeparator();
        UpdateHotkeyLabels();
    }

    /// <summary>고정구간(앞 PinnedCount개, 최대 9) 앱에 숫자키 라벨 "1"~"9"를 부여하고 나머지는 비운다.</summary>
    private void UpdateHotkeyLabels()
    {
        if (_apps == null) return;
        for (int i = 0; i < _apps.Count; i++)
            _apps[i].HotkeyLabel = (i < _pinnedCount && i < 9) ? (i + 1).ToString() : "";
    }

    /// <summary>고정 N개 뒤에 세로 구분선 배치(FM sepLine). 0<N<앱수 일 때만 표시.</summary>
    private void UpdateSeparator()
    {
        if (SepLine == null || AppList?.Items == null) return;

        // 이전 경계 컨테이너의 여백 리셋(경계가 이동했을 수 있음).
        if (_paddedContainer != null) { _paddedContainer.Margin = new Thickness(0); _paddedContainer = null; }

        int count = AppList.Items.Count;
        if (_pinnedCount <= 0 || _pinnedCount >= count)
        {
            SepLine.Visibility = Visibility.Collapsed;
            return;
        }
        if (AppList.ItemContainerGenerator.ContainerFromIndex(_pinnedCount) is not FrameworkElement container
            || SepLine.Parent is not Visual gridVisual)
        {
            SepLine.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            // 경계(첫 미고정) 컨테이너에 좌측 여백 → 틈을 벌리고 선을 그 중앙에 배치.
            container.Margin = new Thickness(SepGapPx, 0, 0, 0);
            _paddedContainer = container;

            Point p = container.TransformToAncestor(gridVisual).Transform(new Point(0, 0));
            double x = Math.Max(0, p.X - SepGapPx / 2 - 1); // 벌린 틈의 중앙
            SepLine.Margin = new Thickness(x, 4, 0, 4);
            SepLine.Visibility = Visibility.Visible;
        }
        catch { SepLine.Visibility = Visibility.Collapsed; }
    }

    private void AppButton_Click(object sender, RoutedEventArgs e)
    {
        if (_justDragged) { _justDragged = false; return; } // 드래그 직후 클릭 무시
        if (sender is FrameworkElement fe && fe.Tag is AppEntry app)
            AppActivated?.Invoke(app);
    }

    private void AppButton_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.Tag is AppEntry app)
            AppRightClicked?.Invoke(app);
    }

    // ── F1: 드래그 시작 ──
    private void AppButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _justDragged = false; // 새 누름마다 초기화 — 직전 드래그(제거 등)로 남은 플래그가 다음 클릭을 먹지 않게
        _dragStart = e.GetPosition(null);
        _dragItem = (sender as FrameworkElement)?.Tag as AppEntry;
    }

    private void AppButton_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_apps == null || _dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var dragged = _dragItem;
        _justDragged = true;
        var effect = DragDrop.DoDragDrop((DependencyObject)sender, dragged, DragDropEffects.Move);
        _dragItem = null;

        // 드롭 대상이 없었고(None) 커서가 도크 밖이면 제거.
        if (effect == DragDropEffects.None && IsCursorOutsideWindow())
            AppRemoveRequested?.Invoke(dragged);
    }

    private void AppButton_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(AppEntry)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void AppButton_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.Move;
        if (_apps == null || e.Data.GetData(typeof(AppEntry)) is not AppEntry dragged) return;
        if (sender is FrameworkElement fe && fe.Tag is AppEntry target && !ReferenceEquals(dragged, target))
        {
            int from = _apps.IndexOf(dragged), to = _apps.IndexOf(target);
            if (from >= 0 && to >= 0)
            {
                // 이동 전 인덱스로 고정구간(구분선 왼쪽) 횡단 여부 판정.
                int p = _pinnedCount;
                bool fromPinned = from < p;
                bool toPinned = to < p;

                _apps.Move(from, to);

                if (!fromPinned && toPinned)            // 미고정 → 고정: 영역 +1
                    _pinnedCount = Math.Min(_pinnedCount + 1, Math.Min(9, _apps.Count));
                else if (fromPinned && !toPinned)       // 고정 → 미고정: 영역 -1
                    _pinnedCount = Math.Max(1, _pinnedCount - 1);

                if (_pinnedCount != p)
                {
                    PinnedCountChanged?.Invoke(_pinnedCount);
                    UpdateSeparator();
                }
                UpdateHotkeyLabels(); // 순서/고정구간 변동 → 번호 재부여
                AppsReordered?.Invoke();
            }
        }
    }

    private void AddButton_Click(object sender, RoutedEventArgs e) => AddRequested?.Invoke();

    private void ExitButton_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();

    // 도크 배경에 드롭 = 순서 유지(제거 방지). Move 로 처리해 None 이 되지 않게 한다.
    private void Surface_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(AppEntry)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void Surface_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.Move;
    }

    private bool IsCursorOutsideWindow()
    {
        if (!NativeMethods.GetCursorPos(out var p)) return false;
        double sx = 1.0, sy = 1.0;
        try { var dpi = VisualTreeHelper.GetDpi(this); sx = dpi.DpiScaleX; sy = dpi.DpiScaleY; }
        catch { }
        double cx = p.X / sx, cy = p.Y / sy;
        return cx < Left || cx > Left + ActualWidth || cy < Top || cy > Top + ActualHeight;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.MakeNoActivateToolWindow(hwnd);
    }

    /// <summary>커서 근처에 표시(포커스 비탈취). 화면 밖으로 나가지 않도록 클램프.</summary>
    public void ShowAtCursor()
    {
        Show(); // 먼저 표시해야 ActualWidth/Height 와 DPI 가 확정됨
        if (NativeMethods.GetCursorPos(out var p))
        {
            double sx = 1.0, sy = 1.0;
            try { var dpi = VisualTreeHelper.GetDpi(this); sx = dpi.DpiScaleX; sy = dpi.DpiScaleY; }
            catch { }
            double cx = p.X / sx, cy = p.Y / sy;

            var wa = ScreenUtil.WorkAreaDipFromPhysical(p.X, p.Y, this); // 커서가 있는 모니터 기준
            double left = Math.Min(cx, wa.Right - ActualWidth);
            double top = cy - ActualHeight; // 커서 위쪽에 표시(FM과 동일)
            Left = Math.Max(wa.Left, left);
            Top = Math.Max(wa.Top, top);
        }
        UpdateSeparator(); // 표시 시점에 1회 구분선 위치 확정
    }
}

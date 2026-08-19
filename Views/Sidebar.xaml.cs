using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FocusClip.Interop;
using FocusClip.Models;
using FocusClip.Services;

namespace FocusClip.Views;

/// <summary>
/// 화면 왼쪽 가장자리의 고정 앱 사이드바 (FM의 사이드바 대응).
/// 자동 숨김이 켜져 있으면 마우스가 벗어나고 일정 시간 뒤 화면 밖으로 미끄러져 사라지고,
/// 다시 왼쪽 가장자리에 마우스를 대면 미끄러져 나온다(작업표시줄 자동 숨김과 동일한 감각).
/// </summary>
public partial class Sidebar : Window
{
    public event Action<AppEntry>? AppActivated;
    public event Action<AppEntry>? AppRightClicked;

    private const double SlideMs = 140;   // 미끄러지는 시간
    private const double EdgeGap = 2;     // 펼쳐졌을 때 화면 왼쪽 끝과의 간격(DIP)
    private const double BandPadDip = 12; // 가장자리 감지 세로 구간 여유(DIP)

    private bool _userMovedY; // 사용자가 핸들로 세로 위치를 옮겼는지(이후 자동 센터링 안 함)
    private IntPtr _hwnd;
    private IntPtr _winEventHook;
    private NativeMethods.WinEventDelegate? _winEventProc; // GC 방지용 참조 유지

    private readonly DispatcherTimer _hideTimer;
    private readonly EdgeHoverService _edge = new();
    private bool _hidden;   // 화면 밖으로 물러난 상태
    private bool _sliding;  // 애니메이션 진행 중

    /// <summary>자동 숨김 사용 여부. false 면 기존처럼 상시 표시.</summary>
    public bool AutoHide { get; private set; }

    public Sidebar()
    {
        InitializeComponent();

        // Interval 은 ApplyAutoHide 에서 설정값으로 덮어쓴다. 0 인 채로 Start 되면
        // 매 틱마다 발사되므로 기본값을 반드시 둔다.
        _hideTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(3000),
        };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); HideToEdge(); };

        _edge.EdgeEntered += ShowFromEdge;

        // 창 위로 마우스가 오면 숨김 보류, 벗어나면 다시 카운트 시작.
        MouseEnter += (_, _) => _hideTimer.Stop();
        MouseLeave += (_, _) => RestartHideTimer();

        Loaded += (_, _) => { PositionLeftCenter(); RestartHideTimer(); };
    }

    /// <summary>설정 변경 반영. 자동 숨김을 끄면 즉시 다시 펼쳐 상시 표시로 돌아간다.</summary>
    public void ApplyAutoHide(bool enabled, int delayMs)
    {
        AutoHide = enabled;
        _hideTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(500, Math.Min(60000, delayMs)));

        if (!enabled)
        {
            _hideTimer.Stop();
            _edge.Uninstall();
            if (_hidden) ShowFromEdge();
        }
        else
        {
            RestartHideTimer();
        }
    }

    private void RestartHideTimer()
    {
        _hideTimer.Stop();
        if (AutoHide && !_hidden && IsVisible) _hideTimer.Start();
    }

    /// <summary>설정에서 사이드바를 켤 때. 이미 자동 숨김으로 물러나 있으면 그대로 둔다
    /// (여기서 Show 하면 설정을 건드릴 때마다 혼자 튀어나온다).</summary>
    public void ShowSidebar()
    {
        if (_hidden) return;
        Show();
        PositionLeftCenter();
        RestartHideTimer();
    }

    /// <summary>설정에서 사이드바를 끌 때. 자동 숨김 상태도 함께 초기화해 다시 켜면 펼쳐진 채로 시작한다.</summary>
    public void HideSidebar()
    {
        _hideTimer.Stop();
        _edge.Uninstall();
        _hidden = false;
        Hide();
    }

    // ── 자동 숨김 ──

    /// <summary>펼쳐졌을 때의 X. 화면 밖으로 물러났을 때의 X 는 창 너비만큼 더 왼쪽.</summary>
    private double ShownLeft => WorkArea.Left + EdgeGap;
    private double HiddenLeft => WorkArea.Left - ActualWidth - 4;

    private static Rect WorkArea => SystemParameters.WorkArea;

    private void HideToEdge()
    {
        if (!AutoHide || _hidden || !IsVisible || _sliding) return;
        // 마우스가 창 위에 있으면 숨기지 않는다(타이머와 MouseLeave 가 어긋난 경우 방어).
        if (IsMouseOver) { RestartHideTimer(); return; }

        _hidden = true;
        SlideTo(HiddenLeft, () =>
        {
            // 실제로 창을 감춰야 다른 앱의 Alt+Tab/최대화 영역에 영향을 주지 않는다.
            Hide();
            UpdateEdgeBand();
            _edge.Install();
        });
    }

    private void ShowFromEdge()
    {
        if (!_hidden) return;
        _hidden = false;
        _edge.Uninstall();

        // 먼저 화면 밖 위치로 옮긴 뒤 보여야 최종 위치에서 한 프레임 번쩍이지 않는다.
        SetLeftDirect(HiddenLeft);
        Show();
        ReassertTopmost();
        SlideTo(ShownLeft, RestartHideTimer);
    }

    /// <summary>가장자리 감지 영역을 현재 사이드바 위치에 맞춘다(물리 px).</summary>
    private void UpdateEdgeBand()
    {
        double sx = 1.0, sy = 1.0;
        try { var dpi = VisualTreeHelper.GetDpi(this); sx = dpi.DpiScaleX; sy = dpi.DpiScaleY; }
        catch { /* 비주얼이 트리에 없으면 1배로 폴백 */ }

        var wa = WorkArea;
        _edge.EdgeXPx = (int)Math.Round(wa.Left * sx);
        _edge.BandTopPx = (int)Math.Round((Top - BandPadDip) * sy);
        _edge.BandBottomPx = (int)Math.Round((Top + ActualHeight + BandPadDip) * sy);
    }

    /// <summary>Left 를 애니메이션으로 옮긴다. 끝나면 애니메이션을 걷어내 코드가 다시 Left 를 제어할 수 있게 한다.</summary>
    private void SlideTo(double targetLeft, Action? done = null)
    {
        _sliding = true;
        var anim = new DoubleAnimation(targetLeft, TimeSpan.FromMilliseconds(SlideMs))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Completed += (_, _) =>
        {
            SetLeftDirect(targetLeft);
            _sliding = false;
            done?.Invoke();
        };
        BeginAnimation(LeftProperty, anim);
    }

    /// <summary>애니메이션이 붙어 있으면 Left 대입이 먹지 않으므로 먼저 떼어낸다.</summary>
    private void SetLeftDirect(double left)
    {
        BeginAnimation(LeftProperty, null);
        Left = left;
    }

    // WPF Topmost=True 만으로는 다른 앱이 새로 뜨면서 자기 창을 topmost 로 올리면 밀린다.
    // 포그라운드 창이 바뀌는 순간(다른 앱 실행/전환)에만 HWND_TOPMOST 를 다시 걸어 최상단 보장.
    private void OnForegroundChanged(IntPtr hook, uint ev, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == _hwnd) return; // 사이드바 자신이면 무시
        ReassertTopmost();
    }

    private void ReassertTopmost()
    {
        if (_hwnd == IntPtr.Zero || !IsVisible) return;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    // ── FM의 ::: 핸들: 세로(Y)만 이동 ──
    private void MoveHandle_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var wa = SystemParameters.WorkArea;
        double top = Top + e.VerticalChange;
        Top = Math.Max(wa.Top, Math.Min(wa.Bottom - ActualHeight, top));
        SetLeftDirect(ShownLeft); // X 고정(FM과 동일)
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
        _hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.MakeNoActivateToolWindow(_hwnd);
        ReassertTopmost();

        // 포그라운드 변경 이벤트만 구독(폴링 없음). SKIPOWNPROCESS 로 자기 프로세스 이벤트는 제외.
        _winEventProc = OnForegroundChanged;
        _winEventHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    protected override void OnClosed(EventArgs e)
    {
        _hideTimer.Stop();
        _edge.Dispose();
        if (_winEventHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
        base.OnClosed(e);
    }

    public void PositionLeftCenter()
    {
        var wa = SystemParameters.WorkArea;
        // 숨은 상태에서 호출되면(설정 변경 등) 화면 밖 위치를 유지한다 — 여기서 끌어오면 혼자 튀어나온다.
        SetLeftDirect(_hidden ? HiddenLeft : ShownLeft);
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

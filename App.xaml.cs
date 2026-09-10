using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using FocusClip.Interop;
using FocusClip.Models;
using FocusClip.Services;
using FocusClip.Views;
using WinForms = System.Windows.Forms;

namespace FocusClip;

/// <summary>
/// 트레이 상주 진입점. FocusManager(런처)와 Clipboard-Manager(클립보드)를 하나로 합친 앱.
/// 단일 CapsLock 후크가 런처 도크 + 클립보드 팝업을 함께 토글한다.
/// </summary>
public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private const string ShowEventName = "FocusClip_ShowDock";
    private EventWaitHandle? _showEvent;

    private HotkeyService? _hotkey;
    private WinForms.NotifyIcon? _tray;
    private LauncherDock? _dock;
    private Sidebar? _sidebar;
    private SettingsWindow? _settings;
    private MainPopup? _popup;            // 클립·경로·프롬프트를 탭으로 묶은 단일 팝업
    private Toast? _toast;
    private DispatcherTimer? _refreshTimer;
    private DispatcherTimer? _outsidePoll;   // 오토클로즈: 외부 클릭 감지
    private int _activeRefreshRunning;
    private DateTime _overlayShownAt;
    private IntPtr _prevForeground;
    private bool _editDialogOpen; // 모달 편집기 중복 오픈 방지(더블클릭 → BeginInvoke 큐잉으로 중첩 모달이 열리는 것 차단)

    private readonly ConfigService _configSvc = new();
    private readonly IconService _icons = new();
    private readonly WindowManager _windows = new();
    private readonly ClipboardService _clipboard = new();
    private readonly PromptService _prompts = new();
    private readonly ObservableCollection<AppEntry> _apps = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, "FocusClip_SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            // 이미 실행 중 → 기존 인스턴스에 "도크 표시" 신호를 보내고 종료.
            // (단일 인스턴스라 두 번째 실행이 조용히 사라지면 "안 떠요"로 보이므로 피드백을 준다.)
            try { if (EventWaitHandle.TryOpenExisting(ShowEventName, out var ev)) { ev.Set(); ev.Dispose(); } } catch { }
            _mutex.Dispose(); _mutex = null; Shutdown(); return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 두 번째 실행이 보내는 신호를 받아 도크를 띄우는 대기 스레드.
        StartShowSignalListener();

        _configSvc.Load();

        // 첫 실행 시 자동시작 1회 자동 등록 (이후 사용자가 설정에서 끄면 재등록 안 함)
        if (!_configSvc.Config.StartupRegistered)
        {
            try { StartupService.SetEnabled(true); } catch { /* 레지스트리 권한 문제 등 무시 */ }
            _configSvc.Config.StartupRegistered = true;
            _configSvc.Save();
        }
        else
        {
            // 자동시작이 켜져 있으면 Run 키 경로를 현재 exe로 자가 보정 — 배포 위치가 바뀌어도
            // (예: Dropbox\publish → %LOCALAPPDATA%\app) 시작프로그램이 옛 경로를 가리키지 않게.
            try { if (StartupService.IsEnabled()) StartupService.SetEnabled(true); } catch { }
        }

        foreach (var a in _configSvc.Config.Apps) _apps.Add(a);

        SetupTray();
        SetupDock();
        SetupSidebar();
        SetupClipboard();
        // 후크 등록(SetWindowsHookEx)은 일부 보안SW/세션 제한 환경에서 실패하며 예외를 던진다.
        // 잡지 않으면 시작 즉시 미처리 예외로 크래시하므로, 토스트로 알리고 트레이 상주는 유지한다.
        try { SetupHotkey(); }
        catch (Exception ex) { _toast?.ShowToast("단축키 등록 실패 — 트레이 메뉴로 종료/설정 가능"); System.Diagnostics.Debug.WriteLine(ex); }

        LoadIconsAsync();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _refreshTimer.Tick += (_, _) => RefreshActiveStates();
        _refreshTimer.Start();
        RefreshActiveStates();

        _outsidePoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _outsidePoll.Tick += (_, _) => OutsidePollTick();
    }

    // ── 초기화 ──
    private void SetupTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("설정", null, (_, _) => Dispatcher.BeginInvoke(OpenSettings));
        // 프롬프트 팝업은 비어 있으면 표시되지 않으므로, 첫 항목(또는 전부 삭제 후)을 만들 수 있는 상시 진입로.
        menu.Items.Add("프롬프트 추가", null, (_, _) => Dispatcher.BeginInvoke(OnPromptAdd));
        menu.Items.Add("저장 폴더 열기", null, (_, _) => OpenSaveFolder());
        menu.Items.Add("종료", null, (_, _) => ExitApp());
        _tray = new WinForms.NotifyIcon
        {
            Text = "FocusClip",
            Icon = LoadAppIcon(),
            Visible = true,
            ContextMenuStrip = menu,
        };
    }

    private void SetupDock()
    {
        _dock = new LauncherDock();
        _dock.SetApps(_apps);
        _dock.AppActivated += OnAppActivated;
        _dock.AppRightClicked += OnAppRightClicked;
        _dock.AppsReordered += OnAppsChanged;
        _dock.AppRemoveRequested += app => { _apps.Remove(app); OnAppsChanged(); };
        // 드래그가 구분선을 넘으면 고정 개수 갱신(저장은 뒤따르는 AppsReordered→OnAppsChanged가 처리).
        _dock.PinnedCountChanged += n => _configSvc.Config.PinnedCount = n;
        _dock.CapsToggleRequested += () => NativeMethods.ToggleCapsLock(); // [A/a] → 대소문자 전환(후크는 무시)
        _dock.ImeToggleRequested += () => NativeMethods.ToggleIme();       // [한/영] → 현재 입력 언어 전환
        _dock.AddRequested += () => Dispatcher.BeginInvoke(OpenSettings); // [+] → 설정 열기
        _dock.ExitRequested += () => Dispatcher.BeginInvoke(ExitApp);      // [✕] → 프로그램 종료
        _dock.SetPinnedCount(_configSvc.Config.PinnedCount);
    }

    /// <summary>F1: 도크 순서 변경/제거 후 config 저장 + 사이드바 갱신.</summary>
    private void OnAppsChanged()
    {
        // 앱 제거로 고정 개수가 앱 수를 초과하면 클램프.
        if (_configSvc.Config.PinnedCount > _apps.Count)
            _configSvc.Config.PinnedCount = Math.Max(1, _apps.Count);
        _configSvc.Config.Apps = _apps.ToList();
        _configSvc.Save();
        RebuildSidebar();
        _dock?.SetPinnedCount(_configSvc.Config.PinnedCount);
    }

    private void SetupSidebar()
    {
        int n = Math.Min(_configSvc.Config.PinnedCount, _apps.Count);
        _sidebar = new Sidebar();
        _sidebar.SetApps(_apps.Take(n).ToList());
        _sidebar.AppActivated += app => _windows.ActivateOrRun(app); // 클릭해도 사이드바는 그대로(자동 숨김 타이머가 처리)
        _sidebar.AppRightClicked += OnAppRightClicked;
        if (_configSvc.Config.SidebarEnabled) _sidebar.Show(); // 설정에서 끄면 표시 안 함
        _sidebar.ApplyAutoHide(_configSvc.Config.SidebarAutoHide, _configSvc.Config.SidebarHideDelayMs);
    }

    private void SetupClipboard()
    {
        _toast = new Toast();
        _clipboard.LoadHistory();// 고정해 둔 클립만 복원(미고정은 메모리 전용) — Start() 전에
        _clipboard.Start();
        _clipboard.ItemAdded += item => Dispatcher.BeginInvoke(() =>
        {
            if (item.IsImage) _toast?.ShowToast(item.Thumb);            // 썸네일 미리보기
            else _toast?.ShowToast(item.IsPath ? item.PathName : item.Snippet);
        });
        _prompts.Load(); // 프롬프트 보관함 복원(전량 영구 저장)

        // 클립·경로·프롬프트를 탭으로 묶은 단일 팝업. 고정(★)한 항목은 각 탭 목록 맨 위에 압축 카드로 나온다.
        _popup = new MainPopup();
        _popup.SetItems(_clipboard.Items, _clipboard.Paths, _prompts.Prompts);
        _popup.ClipSelected += item => Dispatcher.BeginInvoke(() => OnClipSelected(item));
        _popup.ClipEditRequested += item => Dispatcher.BeginInvoke(() => OnClipEdit(item));
        _popup.ClipPromoteRequested += item => Dispatcher.BeginInvoke(() => OnClipPromote(item));
        _popup.ClipOpenRequested += item => Dispatcher.BeginInvoke(() => OnClipOpen(item));
        _popup.PathPrimaryRequested += item => Dispatcher.BeginInvoke(() => OnPathPrimary(item));
        _popup.PinToggleRequested += item => Dispatcher.BeginInvoke(() => OnPinToggled(item));
        _popup.ItemDeleteRequested += item => Dispatcher.BeginInvoke(() => OnClipRemove(item));
        _popup.PromptSelected += p => Dispatcher.BeginInvoke(() => OnPromptSelected(p));
        _popup.PromptEditRequested += p => Dispatcher.BeginInvoke(() => OnPromptEdit(p));
        _popup.PromptDeleteRequested += p => Dispatcher.BeginInvoke(() => OnPromptRemove(p));
        _popup.PromptAddRequested += () => Dispatcher.BeginInvoke(OnPromptAdd);
        _popup.OpenFolderRequested += () => Dispatcher.BeginInvoke(OpenSaveFolder);
        _popup.PinChanged += () => Dispatcher.BeginInvoke(OnPopupPinChanged);
        _popup.CollapseChanged += () => Dispatcher.BeginInvoke(LayoutPopups); // 높이가 바뀌었으니 다시 배치
        _popup.DragFailed += () => Dispatcher.BeginInvoke(() => _toast?.ShowToast("드롭 미지원 앱")); // P001
    }

    // ── 카드 고정/삭제: 항목이 고정 카드와 일반 목록 사이를 오가므로 재필터가 필요하다 ──
    /// <summary>카드 보관(★) 토글 → 탭 목록을 다시 거른다.</summary>
    private void OnPinToggled(ClipItem item)
    {
        _clipboard.TogglePin(item);
        _popup?.RefreshItems();
    }

    /// <summary>카드 삭제(목록에서만 제거 — 본문 파일은 보존) → 목록 갱신.</summary>
    private void OnClipRemove(ClipItem item)
    {
        int index = _clipboard.RemoveForUndo(item);
        if (index < 0) return;
        _popup?.RefreshItems();
        string label = item.IsImage ? "이미지" : item.IsPath ? item.PathName : item.Snippet;
        _toast?.ShowUndo(label,
            undo: () =>
            {
                _clipboard.RestoreRemoved(item, index);
                _popup?.RefreshItems();
            },
            commit: () => ClipboardService.FinalizeRemove(item));
    }

    private void OnPromptRemove(PromptItem item)
    {
        int index = _prompts.RemoveForUndo(item);
        if (index < 0) return;
        _toast?.ShowUndo(item.DisplayTitle,
            undo: () => _prompts.RestoreRemoved(item, index),
            commit: () => { });
    }

    /// <summary>팝업 헤더의 고정핀(📌) 상태 — 켜져 있으면 오토클로즈/CapsLock 에도 닫지 않는다.</summary>
    private bool PopupPinned => _popup?.Pinned ?? false;

    /// <summary>핀 해제 시: 도크가 숨겨진 채 핀으로만 떠 있던 팝업이라면 닫는다
    /// (CapsLock 으로 안 닫히므로 핀 해제가 닫는 수단).</summary>
    private void OnPopupPinChanged()
    {
        if (!PopupPinned && !(_dock?.IsVisible ?? false)) _popup?.Hide();
    }

    /// <summary>현재 CapsLock 토글 상태(켜짐=대문자).</summary>
    private static bool CapsOn() => (NativeMethods.GetKeyState(NativeMethods.VK_CAPITAL) & 0x0001) != 0;

    private void SetupHotkey()
    {
        _hotkey = new HotkeyService { HotkeyVk = _configSvc.Config.HotkeyVk, MaxNumber = _configSvc.Config.PinnedCount };
        _hotkey.HotkeyPressed += () => Dispatcher.BeginInvoke(ToggleOverlay);
        _hotkey.NumberPressed += n => Dispatcher.BeginInvoke(() => ActivatePinned(n));
        _hotkey.EscapePressed += () => Dispatcher.BeginInvoke(() => HideOverlay(force: true));
        _hotkey.DismissRequested += () => Dispatcher.BeginInvoke(() => HideOverlay(force: false)); // 다른 키 → 오토클로즈
        _hotkey.Install();
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        var info = GetResourceStream(new Uri("app_icon.ico", UriKind.Relative));
        return new System.Drawing.Icon(info.Stream);
    }

    private void LoadIconsAsync()
    {
        var apps = _apps.ToArray();
        Task.Run(() =>
        {
            foreach (var app in apps)
            {
                try // 견고화: 한 앱의 아이콘 로드 실패가 전체 로드를 중단시키지 않게 한다.
                {
                    var img = _icons.GetIcon(app);
                    if (img != null) Dispatcher.Invoke(() => app.Icon = img);
                }
                catch { /* 개별 아이콘 실패는 무시하고 계속 */ }
            }
            // self-heal 된 exe 경로 저장. 단, 앱이 0개면 저장하지 않는다 — 로드 실패로 비어있는 상태를
            // 그대로 영속화해 정상 설정을 덮어쓰는 사고를 막는다(앱이 있을 때만 self-heal 의미가 있음).
            try { if (apps.Length > 0) Dispatcher.Invoke(() => _configSvc.Save()); }
            catch { }
        });
    }

    /// <summary>두 번째 실행이 보내는 신호(EventWaitHandle)를 백그라운드에서 대기하다가, 신호가 오면 도크를 띄운다.</summary>
    private void StartShowSignalListener()
    {
        try { _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName); }
        catch { return; }
        var t = new Thread(() =>
        {
            while (true)
            {
                // WaitOne(이벤트 Dispose 시)·BeginInvoke(Dispatcher 종료 시) 모두 예외를 던질 수 있다.
                // raw 스레드라 미처리 예외는 프로세스를 죽이므로, 루프 전체를 try로 감싸 안전 종료한다.
                try
                {
                    if (!_showEvent.WaitOne()) break;
                    Dispatcher.BeginInvoke(() => { if (!(_dock?.IsVisible ?? false)) ShowOverlay(); });
                }
                catch { break; }
            }
        }) { IsBackground = true, Name = "FocusClipShowSignal" };
        t.Start();
    }

    // ── 오버레이(도크 + 클립 팝업) 토글 ──
    private void ToggleOverlay()
    {
        // 도크 표시 여부로 토글. force:false 라서 핀된 팝업은 CapsLock 으로 닫히지 않고 유지된다(사용자 요청).
        // 도크가 숨겨진 상태에서 보이는 팝업은 '핀된 것'뿐이므로 도크 기준 판정이 안전하다.
        if (_dock?.IsVisible ?? false) HideOverlay(force: false);
        else ShowOverlay();
    }

    private void ShowOverlay()
    {
        _prevForeground = NativeMethods.GetForegroundWindow(); // 붙여넣기 대상 기억
        RefreshActiveStates(); // 비동기 — 표시를 막지 않고, 활성 표시(IsActive)는 도크 표시 직후 한 틱 내 갱신
        _dock!.ShowAtCursor();
        _dock.SetCaps(CapsOn()); // 도크 안 [A/a] 버튼에 현재 대소문자 상태 반영
        LayoutPopups();
        _hotkey!.CaptureExtraKeys = true;
        _overlayShownAt = DateTime.Now;
        _outsidePoll?.Start(); // 오토클로즈 외부클릭 감지 시작
    }

    /// <summary>팝업을 도크 바로 위에 배치한다. 창이 하나뿐이고 높이도 고정이라 늘 같은 자리에 뜬다
    /// (예전에는 목록마다 창이 따로 떠서 내용에 따라 배치가 흔들렸다).</summary>
    private void LayoutPopups()
    {
        var dock = _dock;
        if (dock == null || _popup == null || !dock.IsVisible) return;
        // 표시 직전 백그라운드 재검사(세션 중 수동 삭제 반영) — 경로 존재 여부, 클립 본문 크기·유실 여부.
        _clipboard.RefreshPathExistsAll();
        _clipboard.RefreshBodyStateAll();
        _popup.ShowAboveDock(dock);
    }

    /// <summary>오버레이 숨김. force=true(CapsLock·Esc)면 팝업 핀도 무시하고 닫는다(C1).</summary>
    private void HideOverlay(bool force = false)
    {
        _dock?.Hide();
        if (force || !PopupPinned) _popup?.Hide();
        if (_hotkey != null) _hotkey.CaptureExtraKeys = false;
        _outsidePoll?.Stop();
    }

    // ── 오토클로즈: 외부(도크·팝업 밖) 클릭 시 닫기 (CM _poll_focus 대응) ──
    private void OutsidePollTick()
    {
        try
        {
            bool dockVisible = _dock?.IsVisible ?? false;
            bool popupOpen = (_popup?.IsVisible ?? false) && !PopupPinned;
            // 닫을 대상은 도크 + 핀이 안 걸린 채 떠 있는 팝업. 둘 다 없으면 폴링을 멈춘다.
            if (!dockVisible && !popupOpen) { _outsidePoll?.Stop(); return; }
            if ((DateTime.Now - _overlayShownAt).TotalMilliseconds < 200) return;            // 표시 직후 유예

            bool clicked = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0
                        || (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RBUTTON) & 0x8000) != 0;
            if (!clicked) return;

            bool outsideDock = !dockVisible || _dock == null || CursorOutside(_dock);
            bool outsidePopup = _popup == null || !_popup.IsVisible || CursorOutside(_popup);
            // 대소문자 버튼은 도크 안에 있으므로 outsideDock 판정에 이미 포함된다.
            if (outsideDock && outsidePopup) HideOverlay(force: false);
        }
        catch { /* 30ms 타이머는 절대 앱을 죽이지 않게 */ }
    }

    private void OutsidePollMaybeStart()
    {
        if (_outsidePoll == null) return;
        bool need = (_dock?.IsVisible ?? false)
                 || ((_popup?.IsVisible ?? false) && !PopupPinned);
        if (need) { _overlayShownAt = DateTime.Now; _outsidePoll.Start(); }
    }

    private static bool CursorOutside(Window w)
    {
        if (!NativeMethods.GetCursorPos(out var p)) return false;
        double sx = 1.0, sy = 1.0;
        try { var dpi = VisualTreeHelper.GetDpi(w); sx = dpi.DpiScaleX; sy = dpi.DpiScaleY; } catch { }
        double cx = p.X / sx, cy = p.Y / sy;
        return cx < w.Left || cx > w.Left + w.ActualWidth || cy < w.Top || cy > w.Top + w.ActualHeight;
    }

    // ── 런처 활성화 ──
    private void OnAppActivated(AppEntry app)
    {
        _windows.ActivateOrRun(app);
        HideOverlay();
    }

    private void OnAppRightClicked(AppEntry app)
    {
        // 우클릭 동작은 설정값에 따른다(도크·사이드바 공통). 오버레이는 유지.
        switch (_configSvc.Config.RightClickAction)
        {
            case DockRightClickAction.AlwaysOnTop:
                app.IsTopMost = _windows.ToggleTopMost(app); // 항상-위 토글(주황 표시등)
                break;
            case DockRightClickAction.Close:
                _windows.CloseWindow(app);
                break;
            case DockRightClickAction.Minimize:
                _windows.MinimizeWindow(app);
                break;
            case DockRightClickAction.None:
                break;
        }
    }

    private void ActivatePinned(int n)
    {
        int idx = n - 1;
        if (idx >= 0 && idx < _configSvc.Config.PinnedCount && idx < _apps.Count)
            OnAppActivated(_apps[idx]);
    }

    // ── 클립 선택 → 클립보드 설정 + 직전 창에 붙여넣기 ──
    private void OnClipSelected(ClipItem item)
    {
        HideOverlay();
        try
        {
            _clipboard.MarkInternalCopy();
            if (item.IsImage)
            {
                var img = ClipboardService.LoadImage(item);
                if (img != null) Clipboard.SetImage(img);
            }
            else
            {
                Clipboard.SetText(item.Text);
            }
        }
        catch { }
        PasteToPrevious();
    }

    /// <summary>직전 창에 포커스를 되돌린 뒤 Ctrl+V 합성(포커스 안정화까지 120ms 지연).</summary>
    private void PasteToPrevious()
    {
        if (_prevForeground != IntPtr.Zero) NativeMethods.SetForegroundWindow(_prevForeground);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        t.Tick += (_, _) => { t.Stop(); PasteService.SendCtrlV(); };
        t.Start();
    }

    // ── 프롬프트 선택 → 본문을 클립보드에 넣고 직전 창에 붙여넣기 ──
    private void OnPromptSelected(PromptItem p)
    {
        HideOverlay();
        try
        {
            _clipboard.MarkInternalCopy(); // 붙여넣은 프롬프트가 새 클립으로 재수집되지 않게
            Clipboard.SetText(p.Text);
        }
        catch { }
        PasteToPrevious();
    }

    // ── 프롬프트 추가/편집(모달 편집창) ──
    /// <summary>핀 상태로 떠 있는 팝업이 하나라도 있는지(모달 종료 후 오버레이 복구 판단용).</summary>
    private bool AnyPinnedPopupVisible() => (_popup?.IsVisible ?? false) && PopupPinned;

    /// <summary>모달 편집창을 확실히 맨 앞으로 올린다. 도크·팝업이 포커스를 뺏지 않는(no-activate) 창이라
    /// Activate() 만으로는 포그라운드 잠금에 막혀 편집창이 다른 앱 뒤에서 열릴 수 있다 — 그러면 모달이라
    /// 화면상 '버튼이 안 먹는' 것처럼 보인다. 설정 창과 같은 방식으로 우회한다.</summary>
    private static void BringToFront(Window w)
    {
        w.Activate();
        var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        if (h != IntPtr.Zero) NativeMethods.ForceForeground(h);
        w.Topmost = false;
    }

    /// <summary>프롬프트 편집 모달 공통 진입로(추가/편집/승격). 오토클로즈 정지 → 오버레이 강제 숨김 →
    /// 편집창을 맨 앞으로 올려 표시 → 저장 시 onSave.
    /// 종료 후 핀 팝업이 있었으면 오버레이 복구, 아니면 폴 재개(OnClipEdit과 동일 규칙).</summary>
    private void ShowPromptDialog(string title, string text, Action<string, string> onSave)
    {
        if (_editDialogOpen) return; // 더블클릭으로 큐잉된 중복 호출 무시
        _editDialogOpen = true;
        bool wasPinned = false; // 모달 종료 후 복구 대상
        // 여는 준비까지 try 안에서 — 예외가 새면 _editDialogOpen 이 굳어 이후 편집이 영영 먹통이 된다.
        try
        {
            wasPinned = AnyPinnedPopupVisible();
            _outsidePoll?.Stop();      // 모달 편집기 동안 오토클로즈 정지
            HideOverlay(force: true);  // 팝업/도크는 Topmost라 안 닫으면 편집기가 그 뒤로 깔림

            var dlg = new PromptEditWindow(title, text) { Topmost = true };
            dlg.Loaded += (_, _) => BringToFront(dlg);
            if (dlg.ShowDialog() == true)
                onSave(dlg.ResultTitle, dlg.ResultText);
        }
        finally
        {
            _editDialogOpen = false;
            // 모달 동안 닫힌 오버레이를 핀 상태였으면 복구, 아니면 폴 재개
            if (wasPinned && !(_dock?.IsVisible ?? false))
                Dispatcher.BeginInvoke(ShowOverlay);
            else
                OutsidePollMaybeStart();
        }
    }

    private void OnPromptAdd() => ShowPromptDialog("", "", (t, x) => _prompts.Add(t, x));

    private void OnPromptEdit(PromptItem p) => ShowPromptDialog(p.Title, p.Text, (t, x) => _prompts.Update(p, t, x));

    // ── 클립 → 프롬프트 승격: 편집창을 본문 미리 채워 열고, 저장 시 보관함에 추가 ──
    private void OnClipPromote(ClipItem item)
    {
        if (item.IsImage) return; // 프롬프트는 텍스트 전용
        ShowPromptDialog("", item.Text, (t, x) => _prompts.Add(t, x));
    }

    // ── 경로/URL 열기(셸 실행: 로컬은 탐색기/기본앱, URL은 기본 브라우저) ──
    // 경로 카드의 주 동작(클릭)이므로 클립 붙여넣기와 같게 force:false — 사용자가 고정핀을 건 팝업은 남긴다.
    private void OnPathOpen(ClipItem item)
    {
        HideOverlay();
        if (!item.CheckPathExists()) // 클릭 시점 최신 확인(이동/삭제된 로컬 경로)
        {
            _toast?.ShowToast("경로를 찾을 수 없음");
            return;
        }
        try
        {
            if (Directory.Exists(item.Text)) OpenFolderWith(item.Text);        // 폴더면 지정 파일 관리자로
            else Process.Start(new ProcessStartInfo(item.Text) { UseShellExecute = true }); // 파일/URL은 기본 앱
        }
        catch { _toast?.ShowToast("열 수 없음"); }
    }

    private void OnPathPrimary(ClipItem item)
    {
        if (_configSvc.Config.PathClickAction == PathCardClickAction.Open)
        {
            OnPathOpen(item);
            return;
        }

        HideOverlay();
        try
        {
            _clipboard.MarkInternalCopy();
            Clipboard.SetText(item.Text);
            _toast?.ShowToast("경로 복사됨");
        }
        catch { _toast?.ShowToast("복사할 수 없음"); }
    }

    /// <summary>폴더를 설정된 파일 관리자(예: Q-Dir)로 연다. 미설정/경로 없음이면 기본 탐색기로 폴백.</summary>
    private void OpenFolderWith(string folder)
        => FolderLauncher.OpenFolder(folder, _configSvc.Config.FileManagerPath, _configSvc.Config.FileManagerArgs);

    // ── 클립 편집(C4 텍스트 / C5 이미지 주석) ──
    private void OnClipEdit(ClipItem item)
    {
        if (_editDialogOpen) return; // 더블클릭으로 큐잉된 중복 호출 무시
        _editDialogOpen = true;
        bool wasPinned = false; // 모달 종료 후 복구 대상(클립뿐 아니라 경로·프롬프트 핀 팝업도)
        // 여는 준비까지 try 안에서 한다 — 여기서 예외가 새면 _editDialogOpen 이 true 로 굳어
        // 이후 수정 버튼이 영영 먹통이 된다.
        try
        {
            wasPinned = AnyPinnedPopupVisible();
            _outsidePoll?.Stop();      // 모달 편집기 동안 오토클로즈 정지
            HideOverlay(force: true);  // 팝업/도크는 Topmost라 안 닫으면 편집기가 그 뒤로 깔림

            if (item.IsImage)
            {
                var src = ClipboardService.LoadImage(item);
                // 본문 PNG 는 수동 삭제될 수 있다. 조용히 돌아가면 '버튼이 안 먹는' 것처럼 보이므로 알린다.
                if (src == null) { _toast?.ShowToast("원본 이미지를 찾을 수 없음"); return; }
                var dlg = new ImageAnnotateWindow(src) { Topmost = true };
                dlg.Loaded += (_, _) => BringToFront(dlg);
                if (dlg.ShowDialog() == true && dlg.Result != null)
                    _clipboard.AddEditedImage(dlg.Result); // CM: 이미지 편집은 항상 새 클립
            }
            else
            {
                var dlg = new TextEditWindow(item.Text) { Topmost = true };
                dlg.Loaded += (_, _) => BringToFront(dlg);
                if (dlg.ShowDialog() == true)
                {
                    if (dlg.SaveMode == TextEditWindow.Mode.New)
                        _clipboard.AddEditedText(dlg.ResultText);     // 새 클립
                    else
                        _clipboard.ReplaceText(item, dlg.ResultText); // 원본 덮어쓰기
                }
            }
        }
        catch { }
        finally
        {
            _editDialogOpen = false;
            // 편집 대화상자가 떠 있는 동안 닫힌 오버레이를 핀 상태였으면 복구, 아니면 폴 재개
            if (wasPinned && !(_dock?.IsVisible ?? false))
                Dispatcher.BeginInvoke(ShowOverlay);
            else
                OutsidePollMaybeStart();
        }
    }

    /// <summary>클립 카드의 저장된 본문 파일 위치를 탐색기로 연다. 파일이 있으면 그 파일을 선택, 없으면 저장 폴더만.</summary>
    private void OnClipOpen(ClipItem item)
    {
        HideOverlay(force: true);
        try
        {
            if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
            {
                string fm = _configSvc.Config.FileManagerPath;
                if (!string.IsNullOrWhiteSpace(fm) && File.Exists(fm))
                    OpenFolderWith(Path.GetDirectoryName(item.FilePath)!); // 커스텀 관리자는 폴더만(파일 선택 인자 규약이 제각각)
                else
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FilePath}\"") { UseShellExecute = true });
            }
            else
            {
                // 저장 전(메모리 전용)이거나 파일이 사라진 경우: 그 클립이 저장될 폴더를 연다
                // — 이미지는 Screenshots, 텍스트/경로는 media.
                string dir = item.IsImage ? ClipboardService.ImageDir : ClipboardService.SaveDir;
                Directory.CreateDirectory(dir);
                OpenFolderWith(dir);
            }
        }
        catch { _toast?.ShowToast("열 수 없음"); }
    }

    /// <summary>클립 본문(이미지 PNG·텍스트 TXT) 저장 폴더를 탐색기로 연다. 없으면 먼저 생성.</summary>
    private void OpenSaveFolder()
    {
        try
        {
            Directory.CreateDirectory(ClipboardService.SaveDir);
            OpenFolderWith(ClipboardService.SaveDir);
        }
        catch { _toast?.ShowToast("폴더를 열 수 없음"); }
    }

    // ── 설정 ──
    private void OpenSettings()
    {
        // 도크/팝업은 Topmost라 그대로 두면 설정창이 그 뒤로 가려진다 → 오버레이를 닫고 설정창을 앞으로.
        HideOverlay(force: true);

        if (_settings == null)
        {
            _settings = new SettingsWindow(_configSvc, _icons, _apps,
                onChanged: () =>
                {
                    RebuildSidebar();
                    RefreshActiveStates();
                },
                onHotkeyChanged: vk => { if (_hotkey != null) _hotkey.HotkeyVk = vk; });
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }
        else if (_settings.WindowState == WindowState.Minimized)
        {
            _settings.WindowState = WindowState.Normal;
        }

        _settings.Activate();
        // 포커스 비탈취 no-activate 도크에서 호출되므로, 포그라운드 잠금을 우회해 확실히 맨 앞으로 끌어올린다.
        var h = new System.Windows.Interop.WindowInteropHelper(_settings).Handle;
        if (h != IntPtr.Zero) NativeMethods.ForceForeground(h);
    }

    private void RebuildSidebar()
    {
        // 후크 숫자키 상한을 고정 개수에 동기화(설정 변경·드래그 양쪽에서 호출되는 단일 지점).
        if (_hotkey != null) _hotkey.MaxNumber = _configSvc.Config.PinnedCount;
        if (_sidebar is not null)
        {
            if (_configSvc.Config.SidebarEnabled)
            {
                int n = Math.Min(_configSvc.Config.PinnedCount, _apps.Count);
                _sidebar.SetApps(_apps.Take(n).ToList());
                _sidebar.ShowSidebar();       // 설정에서 켜면 표시(자동 숨김 중이면 물러난 상태 유지)
                _sidebar.ApplyAutoHide(_configSvc.Config.SidebarAutoHide, _configSvc.Config.SidebarHideDelayMs);
            }
            else
            {
                _sidebar.HideSidebar();       // 설정에서 끄면 숨김
            }
        }
        _dock?.SetPinnedCount(_configSvc.Config.PinnedCount); // 구분선 위치 갱신(F2)
    }

    private void RefreshActiveStates()
    {
        if (System.Threading.Interlocked.Exchange(ref _activeRefreshRunning, 1) != 0) return;
        // 시스템 전체 프로세스 열거(GetProcesses)는 1.5초마다 반복되는 무거운 작업이라 백그라운드에서 수행하고,
        // IsActive(바인딩 속성) 갱신만 UI 스레드로 마샬한다 — UI 스레드의 주기적 멈춤을 없앤다.
        // GetProcesses()가 돌려준 Process 객체(파이널라이저 대상)는 finally에서 즉시 Dispose.
        Task.Run(() =>
        {
            try
            {
                HashSet<string> running;
                var procs = Process.GetProcesses();
                try
                {
                    running = new HashSet<string>(
                        procs.Select(p => { try { return p.ProcessName; } catch { return ""; } }),
                        StringComparer.OrdinalIgnoreCase);
                }
                finally { foreach (var p in procs) p.Dispose(); }
                Dispatcher.BeginInvoke(() =>
                {
                    foreach (var app in _apps)
                    {
                        string n = app.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                            ? app.ProcessName[..^4] : app.ProcessName;
                        app.IsActive = !string.IsNullOrEmpty(n) && running.Contains(n);
                    }
                });
            }
            catch { }
            finally { System.Threading.Interlocked.Exchange(ref _activeRefreshRunning, 0); }
        });
    }

    private void ExitApp()
    {
        _hotkey?.Dispose();
        _clipboard.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkey?.Dispose();
        _clipboard.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        try { _configSvc.Save(); } catch { }
        try { _showEvent?.Dispose(); } catch { }
        if (_mutex is not null && _ownsMutex)
        {
            try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
        }
        base.OnExit(e);
    }
}

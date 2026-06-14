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
using System.Windows.Media.Imaging;
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

    private HotkeyService? _hotkey;
    private WinForms.NotifyIcon? _tray;
    private LauncherDock? _dock;
    private Sidebar? _sidebar;
    private SettingsWindow? _settings;
    private ClipboardPopup? _clipPopup;
    private PathPopup? _pathPopup;
    private Toast? _toast;
    private DispatcherTimer? _refreshTimer;
    private DispatcherTimer? _outsidePoll;   // 오토클로즈: 외부 클릭 감지
    private DateTime _overlayShownAt;
    private IntPtr _prevForeground;

    private readonly ConfigService _configSvc = new();
    private readonly IconService _icons = new();
    private readonly WindowManager _windows = new();
    private readonly ClipboardService _clipboard = new();
    private readonly ObservableCollection<AppEntry> _apps = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, "FocusClip_SingleInstance", out _ownsMutex);
        if (!_ownsMutex) { _mutex.Dispose(); _mutex = null; Shutdown(); return; }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _configSvc.Load();

        // 첫 실행 시 자동시작 1회 자동 등록 (이후 사용자가 설정에서 끄면 재등록 안 함)
        if (!_configSvc.Config.StartupRegistered)
        {
            try { StartupService.SetEnabled(true); } catch { /* 레지스트리 권한 문제 등 무시 */ }
            _configSvc.Config.StartupRegistered = true;
            _configSvc.Save();
        }

        foreach (var a in _configSvc.Config.Apps) _apps.Add(a);

        SetupTray();
        SetupDock();
        SetupSidebar();
        SetupClipboard();
        SetupHotkey();

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
        _dock.AddRequested += () => Dispatcher.BeginInvoke(OpenSettings); // [+] → 설정 열기
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
        _sidebar.AppActivated += app => _windows.ActivateOrRun(app); // 항상 표시 → 숨기지 않음
        _sidebar.AppRightClicked += OnAppRightClicked;
        _sidebar.Show();
    }

    private void SetupClipboard()
    {
        _toast = new Toast();
        _clipboard.Start();
        _clipboard.ItemAdded += item => Dispatcher.BeginInvoke(() =>
            _toast?.ShowToast(item.IsImage ? "🖼 이미지" : item.Snippet));
        _clipPopup = new ClipboardPopup();
        _clipPopup.SetItems(_clipboard.Items);
        _clipPopup.ClipSelected += item => Dispatcher.BeginInvoke(() => OnClipSelected(item));
        _clipPopup.ClipDeleteRequested += item => Dispatcher.BeginInvoke(() => _clipboard.Remove(item));
        _clipPopup.ClipPinToggled += item => Dispatcher.BeginInvoke(() => _clipboard.TogglePin(item));
        _clipPopup.ClipEditRequested += item => Dispatcher.BeginInvoke(() => OnClipEdit(item));

        _pathPopup = new PathPopup();
        _pathPopup.SetItems(_clipboard.Paths);
        _pathPopup.PathSelected += item => Dispatcher.BeginInvoke(() => OnClipSelected(item)); // 전체 경로 붙여넣기
        _pathPopup.PathDeleteRequested += item => Dispatcher.BeginInvoke(() => _clipboard.Remove(item));
    }

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
                var img = _icons.GetIcon(app);
                if (img != null) Dispatcher.Invoke(() => app.Icon = img);
            }
            Dispatcher.Invoke(() => _configSvc.Save()); // self-heal 된 경로 저장
        });
    }

    // ── 오버레이(도크 + 클립 팝업) 토글 ──
    private void ToggleOverlay()
    {
        if ((_dock?.IsVisible ?? false) || (_clipPopup?.IsVisible ?? false) || (_pathPopup?.IsVisible ?? false)) HideOverlay(force: true);
        else ShowOverlay();
    }

    private void ShowOverlay()
    {
        _prevForeground = NativeMethods.GetForegroundWindow(); // 붙여넣기 대상 기억
        RefreshActiveStates();
        _dock!.ShowAtCursor();
        if (_clipboard.Items.Count > 0) _clipPopup!.ShowAbove(_dock); // 클립이 있을 때만 도크 위에
        if (_clipboard.Paths.Count > 0) _pathPopup!.ShowBelow(_dock); // 경로가 있을 때만 도크 아래에
        _hotkey!.CaptureExtraKeys = true;
        _overlayShownAt = DateTime.Now;
        _outsidePoll?.Start(); // 오토클로즈 외부클릭 감지 시작
    }

    /// <summary>오버레이 숨김. force=true(CapsLock·Esc)면 팝업 핀도 무시하고 닫는다(C1).</summary>
    private void HideOverlay(bool force = false)
    {
        _dock?.Hide();
        if (force || !(_clipPopup?.Pinned ?? false)) _clipPopup?.Hide();
        _pathPopup?.Hide();
        if (_hotkey != null) _hotkey.CaptureExtraKeys = false;
        _outsidePoll?.Stop();
    }

    // ── 오토클로즈: 외부(도크·팝업 밖) 클릭 시 닫기 (CM _poll_focus 대응) ──
    private void OutsidePollTick()
    {
        try
        {
            bool dockVisible = _dock?.IsVisible ?? false;
            bool popupVisible = _clipPopup?.IsVisible ?? false;
            bool pathVisible = _pathPopup?.IsVisible ?? false;
            bool pinned = _clipPopup?.Pinned ?? false;
            if (!dockVisible && (!popupVisible || pinned) && !pathVisible) { _outsidePoll?.Stop(); return; } // 닫을 대상 없음
            if ((DateTime.Now - _overlayShownAt).TotalMilliseconds < 200) return;            // 표시 직후 유예

            bool clicked = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0
                        || (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RBUTTON) & 0x8000) != 0;
            if (!clicked) return;

            bool outsideDock = !dockVisible || _dock == null || CursorOutside(_dock);
            bool outsidePopup = !popupVisible || _clipPopup == null || CursorOutside(_clipPopup);
            bool outsidePath = !pathVisible || _pathPopup == null || CursorOutside(_pathPopup);
            if (outsideDock && outsidePopup && outsidePath) HideOverlay(force: false);
        }
        catch { /* 30ms 타이머는 절대 앱을 죽이지 않게 */ }
    }

    private void OutsidePollMaybeStart()
    {
        if (_outsidePoll == null) return;
        bool need = (_dock?.IsVisible ?? false)
                 || ((_clipPopup?.IsVisible ?? false) && !(_clipPopup?.Pinned ?? false));
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
        app.IsTopMost = _windows.ToggleTopMost(app); // 항상-위 토글(주황 표시등) — 오버레이는 유지
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
                var img = item.FullImage ?? LoadImage(item.FilePath); // C7: 저장 전이면 메모리 원본 사용
                if (img != null) Clipboard.SetImage(img);
            }
            else
            {
                Clipboard.SetText(item.Text);
            }
        }
        catch { }

        if (_prevForeground != IntPtr.Zero) NativeMethods.SetForegroundWindow(_prevForeground);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        t.Tick += (_, _) => { t.Stop(); PasteService.SendCtrlV(); };
        t.Start();
    }

    // ── 클립 편집(C4 텍스트 / C5 이미지 주석) ──
    private void OnClipEdit(ClipItem item)
    {
        bool wasPinned = _clipPopup?.Pinned ?? false;
        _outsidePoll?.Stop(); // 모달 편집기 동안 오토클로즈 정지
        HideOverlay(force: true); // 팝업/도크는 Topmost라 안 닫으면 편집기가 그 뒤로 깔림
        try
        {
            if (item.IsImage)
            {
                var src = item.FullImage ?? LoadImage(item.FilePath);
                if (src == null) return;
                var dlg = new ImageAnnotateWindow(src) { Topmost = true };
                dlg.Loaded += (_, _) => { dlg.Activate(); dlg.Topmost = false; };
                if (dlg.ShowDialog() == true && dlg.Result != null)
                    _clipboard.AddEditedImage(dlg.Result); // CM: 이미지 편집은 항상 새 클립
            }
            else
            {
                var dlg = new TextEditWindow(item.Text) { Topmost = true };
                dlg.Loaded += (_, _) => { dlg.Activate(); dlg.Topmost = false; };
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
            // 편집 대화상자가 떠 있는 동안 닫힌 오버레이를 핀 상태였으면 복구, 아니면 폴 재개
            if (wasPinned && !(_clipPopup?.IsVisible ?? false))
                Dispatcher.BeginInvoke(ShowOverlay);
            else
                OutsidePollMaybeStart();
        }
    }

    private static BitmapSource? LoadImage(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.UriSource = new Uri(path);
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch { return null; }
    }

    // ── 설정 ──
    private void OpenSettings()
    {
        if (_settings != null) { _settings.Activate(); return; }
        _settings = new SettingsWindow(_configSvc, _icons, _apps,
            onChanged: () => { RebuildSidebar(); RefreshActiveStates(); },
            onHotkeyChanged: vk => { if (_hotkey != null) _hotkey.HotkeyVk = vk; });
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }

    private void RebuildSidebar()
    {
        // 후크 숫자키 상한을 고정 개수에 동기화(설정 변경·드래그 양쪽에서 호출되는 단일 지점).
        if (_hotkey != null) _hotkey.MaxNumber = _configSvc.Config.PinnedCount;
        if (_sidebar is null) return;
        int n = Math.Min(_configSvc.Config.PinnedCount, _apps.Count);
        _sidebar.SetApps(_apps.Take(n).ToList());
        _sidebar.PositionLeftCenter();
        _dock?.SetPinnedCount(_configSvc.Config.PinnedCount); // 구분선 위치 갱신(F2)
    }

    private void RefreshActiveStates()
    {
        HashSet<string> running;
        try
        {
            running = new HashSet<string>(
                Process.GetProcesses().Select(p => { try { return p.ProcessName; } catch { return ""; } }),
                StringComparer.OrdinalIgnoreCase);
        }
        catch { return; }

        foreach (var app in _apps)
        {
            string n = app.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? app.ProcessName[..^4] : app.ProcessName;
            app.IsActive = !string.IsNullOrEmpty(n) && running.Contains(n);
        }
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
        if (_mutex is not null && _ownsMutex)
        {
            try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
        }
        base.OnExit(e);
    }
}

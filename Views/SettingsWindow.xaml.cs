using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FocusClip.Models;
using FocusClip.Services;

namespace FocusClip.Views;

/// <summary>앱 등록/제거 + 시작프로그램/단축키/고정개수 설정. (FM의 OpenSettingsGui 대응)</summary>
public partial class SettingsWindow : Window
{
    private readonly ConfigService _cfg;
    private readonly IconService _icons;
    private readonly ObservableCollection<AppEntry> _registered;
    private readonly Action _onChanged;
    private readonly Action<int>? _onHotkeyChanged;
    private readonly ObservableCollection<AppEntry> _running = new();
    private bool _capturingHotkey;
    private bool _suppressPinned;
    private bool _suppressHideDelay;
    private bool _suppressRc;
    private bool _suppressFm;
    private Point _dragStart;
    private bool _dragging;

    // 「취소」로 되돌릴 스냅샷. 창을 여는 순간의 상태를 떠 둔다.
    // 앱 목록은 Clone() 이 아니라 인스턴스 그대로 담는다 — 아이콘 등 UI 전용 속성이
    // JsonIgnore 라 복제본에는 없기 때문에, 되돌릴 때 원래 인스턴스를 그대로 복원해야 한다.
    private readonly AppConfig _snapConfig;
    private readonly List<AppEntry> _snapApps;
    private readonly bool _snapStartup;
    private bool _dirty;          // 저장할 변경이 있는지(창 닫기 시 확인 여부 판단)
    private bool _closingHandled; // 저장/취소 버튼이 이미 처리함 → OnClosing 에서 다시 묻지 않게

    // 등록 목록 안에서의 순서 변경 드래그. 실행 중 목록에서 넘어오는 '추가' 드래그와
    // 구분해야 해서 전용 데이터 포맷을 쓴다.
    private const string ReorderFormat = "FocusClip.ReorderApps";
    private Point _regDragStart;
    private bool _regDragging;

    public SettingsWindow(ConfigService cfg, IconService icons,
        ObservableCollection<AppEntry> registered, Action onChanged,
        Action<int>? onHotkeyChanged = null)
    {
        InitializeComponent();
        _cfg = cfg;
        _icons = icons;
        _registered = registered;
        _onChanged = onChanged;
        _onHotkeyChanged = onHotkeyChanged;

        RegisteredList.ItemsSource = _registered;
        RunningList.ItemsSource = _running;

        // 변경은 즉시 화면에 반영하되(미리보기) 디스크 저장은 「저장」까지 미룬다.
        // 「취소」는 이 스냅샷으로 되돌린다.
        _snapConfig = _cfg.Config.Clone();
        _snapApps = _registered.ToList();
        _snapStartup = StartupService.IsEnabled();

        StartupCheck.IsChecked = _snapStartup;
        StartupCheck.Checked += (_, _) => { StartupService.SetEnabled(true); _dirty = true; };
        StartupCheck.Unchecked += (_, _) => { StartupService.SetEnabled(false); _dirty = true; };

        SidebarCheck.IsChecked = _cfg.Config.SidebarEnabled;
        SidebarCheck.Checked += (_, _) => { _cfg.Config.SidebarEnabled = true; Touch(); };
        SidebarCheck.Unchecked += (_, _) => { _cfg.Config.SidebarEnabled = false; Touch(); };

        SidebarAutoHideCheck.IsChecked = _cfg.Config.SidebarAutoHide;
        SidebarAutoHideCheck.Checked += (_, _) => { _cfg.Config.SidebarAutoHide = true; Touch(); };
        SidebarAutoHideCheck.Unchecked += (_, _) => { _cfg.Config.SidebarAutoHide = false; Touch(); };

        // 설정은 ms 로 저장하고 입력은 초 단위로 받는다(1~60초).
        _suppressHideDelay = true;
        SidebarHideDelayBox.Text = (_cfg.Config.SidebarHideDelayMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture);
        _suppressHideDelay = false;

        HotkeyButton.Content = VkName(_cfg.Config.HotkeyVk);
        _suppressPinned = true;
        PinnedBox.Text = _cfg.Config.PinnedCount.ToString();
        _suppressPinned = false;

        _suppressRc = true;
        (_cfg.Config.RightClickAction switch
        {
            DockRightClickAction.Close => RcClose,
            DockRightClickAction.Minimize => RcMin,
            DockRightClickAction.None => RcNone,
            _ => RcTop,
        }).IsChecked = true;
        _suppressRc = false;

        _suppressFm = true;
        FileManagerBox.Text = _cfg.Config.FileManagerPath;
        FileManagerArgsBox.Text = _cfg.Config.FileManagerArgs;
        _suppressFm = false;

        PreviewKeyDown += SettingsWindow_PreviewKeyDown;

        RefreshRunning();
    }

    // ── C3: 단축키 변경 ──
    private void Hotkey_Click(object sender, RoutedEventArgs e)
    {
        _capturingHotkey = true;
        HotkeyButton.Content = "키를 누르세요…";
        HotkeyHint.Text = "(Esc=취소)";
        HotkeyButton.Focus();
    }

    private void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingHotkey) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) // 취소
        {
            _capturingHotkey = false;
            HotkeyButton.Content = VkName(_cfg.Config.HotkeyVk);
            HotkeyHint.Text = "";
            return;
        }

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        _cfg.Config.HotkeyVk = vk;
        _dirty = true;
        _onHotkeyChanged?.Invoke(vk); // 미리보기 — 디스크 저장은 「저장」에서

        _capturingHotkey = false;
        HotkeyButton.Content = VkName(vk);
        HotkeyHint.Text = "적용됨";
    }

    private static string VkName(int vk) => vk switch
    {
        0x14 => "CapsLock",
        0x09 => "Tab",
        0x20 => "Space",
        0x12 => "Alt",
        0x11 => "Ctrl",
        0x10 => "Shift",
        0x5B => "Win",
        0xC0 => "` (백틱)",
        0x13 => "Pause",
        0x91 => "ScrollLock",
        0x2D => "Insert",
        0x24 => "Home",
        0x23 => "End",
        >= 0x70 and <= 0x7B => "F" + (vk - 0x6F),       // F1~F12
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),    // A~Z
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),    // 0~9
        _ => $"VK 0x{vk:X2}",
    };

    // ── 폴더 열기 프로그램(파일 관리자) ──
    private void FileManagerBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppressFm) return;
        _cfg.Config.FileManagerPath = FileManagerBox.Text.Trim();
        _dirty = true;
    }

    private void FileManagerArgsBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppressFm) return;
        _cfg.Config.FileManagerArgs = FileManagerArgsBox.Text;
        _dirty = true;
    }

    private void BrowseFileManager_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "폴더를 열 프로그램 선택",
            Filter = "실행 파일 (*.exe)|*.exe|모든 파일 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true) FileManagerBox.Text = dlg.FileName; // TextChanged가 반영
    }

    private void ClearFileManager_Click(object sender, RoutedEventArgs e) => FileManagerBox.Text = "";

    // ── 아이콘 우클릭 동작 ──
    private void RightClick_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressRc) return;
        if (sender is FrameworkElement fe && fe.Tag is string s
            && Enum.TryParse<DockRightClickAction>(s, out var act))
        {
            _cfg.Config.RightClickAction = act;
            _dirty = true;
        }
    }

    // ── F2: 고정 개수 ──
    private void PinnedBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        => e.Handled = !e.Text.All(char.IsDigit);

    private void PinnedBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppressPinned) return;
        if (!int.TryParse(PinnedBox.Text, out int n)) return;
        int max = Math.Max(1, _registered.Count);
        int clamped = Math.Max(1, Math.Min(max, n));
        if (clamped != n)
        {
            _suppressPinned = true;
            PinnedBox.Text = clamped.ToString();
            PinnedBox.CaretIndex = PinnedBox.Text.Length;
            _suppressPinned = false;
        }
        _cfg.Config.PinnedCount = clamped;
        Touch();
    }

    private void HideDelayBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        => e.Handled = !e.Text.All(c => char.IsDigit(c) || c == '.');

    private void HideDelayBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppressHideDelay) return;
        if (!double.TryParse(SidebarHideDelayBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double sec))
            return; // 입력 도중("." 만 친 상태 등)에는 손대지 않는다
        // 자동 숨김 자체를 끄는 건 체크박스 몫이므로 여기서 0 은 허용하지 않는다.
        double clamped = Math.Max(1, Math.Min(60, sec));
        _cfg.Config.SidebarHideDelayMs = (int)Math.Round(clamped * 1000);
        Touch();
    }

    private void RefreshRunning()
    {
        _running.Clear();
        var reg = new HashSet<string>(_registered.Select(a => a.ProcessName), StringComparer.OrdinalIgnoreCase);
        foreach (var rp in ScanRunning(reg))
        {
            rp.Icon = _icons.IconForPath(rp.ExePath, 20);
            _running.Add(rp);
        }
    }

    private static List<AppEntry> ScanRunning(HashSet<string> exclude)
    {
        var list = new List<AppEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero || string.IsNullOrEmpty(p.MainWindowTitle)) continue;
                string proc = p.ProcessName + ".exe";
                if (exclude.Contains(proc) || seen.Contains(proc)) continue;
                string? path = p.MainModule?.FileName;
                if (string.IsNullOrEmpty(path)) continue;
                if (path.StartsWith(winDir, StringComparison.OrdinalIgnoreCase)) continue;
                seen.Add(proc);
                list.Add(new AppEntry { Name = p.ProcessName, ProcessName = proc, ExePath = path });
            }
            catch { /* 권한/비트수 차이 → 건너뜀 */ }
        }
        return list.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        foreach (var sel in RunningList.SelectedItems.Cast<AppEntry>().ToList())
        {
            sel.Icon = _icons.GetIcon(sel); // 도크/사이드바용 32px 아이콘으로 교체
            _registered.Add(sel);
            _running.Remove(sel);
        }
        Persist();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var sel in RegisteredList.SelectedItems.Cast<AppEntry>().ToList())
            _registered.Remove(sel);
        Persist();
    }

    /// <summary>앱 목록 변경을 화면에 반영한다. 디스크 저장은 「저장」에서 한 번에 한다.</summary>
    private void Persist()
    {
        Touch();
        RefreshRunning();
    }

    // ── 드래그로 앱 추가 (RunningList → RegisteredList) ──
    private void RunningList_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragging = false;
    }

    private void RunningList_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragging) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var items = RunningList.SelectedItems.Cast<AppEntry>().ToList();
        if (items.Count == 0) return;
        _dragging = true;
        DragDrop.DoDragDrop(RunningList, items, DragDropEffects.Move);
        _dragging = false;
    }

    private void RegisteredList_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(ReorderFormat))
        {
            e.Effects = DragDropEffects.Move;
            ShowInsertMark(DropIndexAt(e.GetPosition(RegisteredList)));
        }
        else
        {
            e.Effects = e.Data.GetDataPresent(typeof(List<AppEntry>))
                ? DragDropEffects.Move : DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void RegisteredList_DragLeave(object sender, DragEventArgs e) => ShowInsertMark(-1);

    private void RegisteredList_Drop(object sender, DragEventArgs e)
    {
        ShowInsertMark(-1);

        // 등록 목록 안에서 끌었다 → 순서 변경
        if (e.Data.GetDataPresent(ReorderFormat))
        {
            if (e.Data.GetData(ReorderFormat) is List<AppEntry> moving && moving.Count > 0)
            {
                ReorderTo(moving, DropIndexAt(e.GetPosition(RegisteredList)));
                Touch(); // 실행 중 목록은 바뀌지 않으므로 RefreshRunning 은 하지 않는다
            }
            return;
        }

        // 실행 중 목록에서 끌어왔다 → 추가
        if (!e.Data.GetDataPresent(typeof(List<AppEntry>))) return;
        foreach (var sel in (List<AppEntry>)e.Data.GetData(typeof(List<AppEntry>)))
        {
            sel.Icon = _icons.GetIcon(sel);
            _registered.Add(sel);
            _running.Remove(sel);
        }
        Persist();
    }

    // ── 드래그로 순서 바꾸기 (RegisteredList 안에서) ──

    private void RegisteredList_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _regDragStart = e.GetPosition(null);
        _regDragging = false;
    }

    private void RegisteredList_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _regDragging) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _regDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _regDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var items = RegisteredList.SelectedItems.Cast<AppEntry>().ToList();
        if (items.Count == 0) return;

        _regDragging = true;
        var data = new DataObject(ReorderFormat, items);
        DragDrop.DoDragDrop(RegisteredList, data, DragDropEffects.Move);
        _regDragging = false;
        ShowInsertMark(-1);
    }

    /// <summary>커서 Y 위치에 해당하는 삽입 인덱스. 항목의 위쪽 절반이면 그 앞, 아래쪽 절반이면 그 뒤.</summary>
    private int DropIndexAt(Point pt)
    {
        for (int i = 0; i < _registered.Count; i++)
        {
            if (RegisteredList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem lbi) continue;
            if (lbi.ActualHeight <= 0) continue;
            double top = lbi.TranslatePoint(new Point(0, 0), RegisteredList).Y;
            if (pt.Y < top + lbi.ActualHeight / 2) return i;
        }
        return _registered.Count; // 마지막 항목보다 아래 → 맨 끝
    }

    /// <summary>삽입 위치를 항목 테두리로 표시한다. index=-1 이면 모두 지운다.</summary>
    private void ShowInsertMark(int index)
    {
        for (int i = 0; i < _registered.Count; i++)
        {
            if (RegisteredList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem lbi) continue;
            // 맨 끝에 놓는 경우만 마지막 항목의 '아래' 선으로 표시한다.
            double top = index == i ? 2 : 0;
            double bottom = index == _registered.Count && i == _registered.Count - 1 ? 2 : 0;
            lbi.BorderThickness = new Thickness(0, top, 0, bottom);
        }
    }

    /// <summary>선택 항목들을 targetIndex 자리로 옮긴다(서로의 상대 순서는 유지).</summary>
    private void ReorderTo(List<AppEntry> moving, int targetIndex)
    {
        // 현재 순서대로 정렬해 두어야 여러 개를 옮겨도 자기들끼리의 순서가 보존된다.
        var ordered = moving.Where(_registered.Contains)
                            .OrderBy(_registered.IndexOf)
                            .ToList();
        if (ordered.Count == 0) return;

        // 옮길 항목 중 목표 위치보다 앞에 있던 것들은 제거되면서 인덱스를 당기므로 그만큼 보정한다.
        int insert = targetIndex - ordered.Count(a => _registered.IndexOf(a) < targetIndex);

        foreach (var a in ordered) _registered.Remove(a);

        insert = Math.Max(0, Math.Min(_registered.Count, insert));
        foreach (var a in ordered) _registered.Insert(insert++, a);

        RegisteredList.SelectedItems.Clear();
        foreach (var a in ordered) RegisteredList.SelectedItems.Add(a);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRunning();

    // ── 저장 / 취소 ──

    /// <summary>변경을 화면에 즉시 반영(미리보기)하고 저장 대상으로 표시한다.</summary>
    private void Touch()
    {
        _dirty = true;
        _onChanged();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveChanges();
        _closingHandled = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        RevertChanges();
        _closingHandled = true;
        Close();
    }

    private void SaveChanges()
    {
        _cfg.Config.Apps = _registered.ToList();
        _cfg.Save();
        _dirty = false;
    }

    /// <summary>창을 열던 시점의 상태로 되돌리고 화면에 반영한다.</summary>
    private void RevertChanges()
    {
        if (!_dirty) return;

        _cfg.Config.CopyFrom(_snapConfig);
        // CopyFrom 이 넣은 Apps 는 JSON 복제본이라 아이콘이 없다 → 원래 인스턴스로 되돌린다.
        _cfg.Config.Apps = _snapApps.ToList();

        _registered.Clear();
        foreach (var a in _snapApps) _registered.Add(a);

        if (StartupService.IsEnabled() != _snapStartup) StartupService.SetEnabled(_snapStartup);

        _onHotkeyChanged?.Invoke(_cfg.Config.HotkeyVk); // 단축키도 되돌려 다시 걸어야 한다
        _onChanged();
        _dirty = false;
    }

    /// <summary>제목표시줄 X 로 닫을 때. 저장/취소 버튼이 이미 처리했으면 그냥 닫는다.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closingHandled && _dirty)
        {
            var r = MessageBox.Show(this, "변경사항을 저장할까요?", "FocusClip 설정",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (r == MessageBoxResult.Yes) SaveChanges(); else RevertChanges();
        }
        base.OnClosing(e);
    }

    // ── Config(데이터) 폴더 열기 ── 설정된 파일 관리자(Q-Dir 등)가 있으면 그것으로, 없으면 기본 탐색기.
    private void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = ConfigService.Dir;
            System.IO.Directory.CreateDirectory(dir);
            FolderLauncher.OpenFolder(dir, _cfg.Config.FileManagerPath, _cfg.Config.FileManagerArgs);
        }
        catch { }
    }

    // ── Ko-fi 후원 링크 ──
    private void Kofi_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://ko-fi.com/praswna") { UseShellExecute = true });
        }
        catch { /* 브라우저 열기 실패는 무시 */ }
    }
}

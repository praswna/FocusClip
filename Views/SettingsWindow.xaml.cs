using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
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
    private bool _suppressRc;
    private bool _suppressFm;

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

        StartupCheck.IsChecked = StartupService.IsEnabled();
        StartupCheck.Checked += (_, _) => StartupService.SetEnabled(true);
        StartupCheck.Unchecked += (_, _) => StartupService.SetEnabled(false);

        SidebarCheck.IsChecked = _cfg.Config.SidebarEnabled;
        SidebarCheck.Checked += (_, _) => { _cfg.Config.SidebarEnabled = true; _cfg.Save(); _onChanged(); };
        SidebarCheck.Unchecked += (_, _) => { _cfg.Config.SidebarEnabled = false; _cfg.Save(); _onChanged(); };

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
        _cfg.Save();
        _onHotkeyChanged?.Invoke(vk);

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
        _cfg.Save();
    }

    private void FileManagerArgsBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppressFm) return;
        _cfg.Config.FileManagerArgs = FileManagerArgsBox.Text;
        _cfg.Save();
    }

    private void BrowseFileManager_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "폴더를 열 프로그램 선택",
            Filter = "실행 파일 (*.exe)|*.exe|모든 파일 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true) FileManagerBox.Text = dlg.FileName; // TextChanged가 저장
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
            _cfg.Save();
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
        _cfg.Save();
        _onChanged();
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

    private void Persist()
    {
        _cfg.Config.Apps = _registered.ToList();
        _cfg.Save();
        _onChanged();
        RefreshRunning();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRunning();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

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

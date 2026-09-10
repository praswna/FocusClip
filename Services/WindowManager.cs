using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using FocusClip.Interop;
using FocusClip.Models;

namespace FocusClip.Services;

/// <summary>
/// 등록 앱을 활성화/실행하고, 항상-위(TopMost) 토글을 처리한다.
/// (FM의 ActivateOrRun/ActivateWindow/AppIcon_RightClick 대응)
/// </summary>
public sealed class WindowManager
{
    public void ActivateOrRun(AppEntry app)
    {
        IntPtr h = FindMainWindow(app);
        if (h != IntPtr.Zero) { NativeMethods.ForceForeground(h); return; }
        Run(app);
    }

    /// <summary>열려 있는 탐색기 폴더 창 중 Z-order 맨 아래(가장 오래 안 본) 창을 앞으로 올린다.
    /// 앞으로 올린 창이 맨 위로 가므로, 연타하면 열린 창 전체를 한 바퀴 순환한다
    /// — 별도로 순서를 기억할 필요가 없다. 열린 폴더 창이 없으면 false.</summary>
    public static bool FocusNextExplorerWindow()
    {
        IntPtr bottom = IntPtr.Zero;
        try
        {
            // EnumWindows 는 Z-order 위→아래 순으로 돈다 → 마지막으로 만난 폴더 창이 맨 아래 창.
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero) return true;
                if (IsFolderWindow(hwnd)) bottom = hwnd;
                return true;
            }, IntPtr.Zero);
        }
        catch { }

        if (bottom == IntPtr.Zero) return false;
        NativeMethods.ForceForeground(bottom);
        return true;
    }

    /// <summary>탐색기 폴더 창인지(클래스명으로 판정). 폴더 창은 전부 explorer.exe 소유라
    /// 실행 파일명만으로는 바탕화면(Progman)·작업표시줄과 구분되지 않는다.</summary>
    private static bool IsFolderWindow(IntPtr hwnd)
    {
        var name = new StringBuilder(64);
        if (NativeMethods.GetClassName(hwnd, name, name.Capacity) <= 0) return false;
        string cls = name.ToString();
        return cls == "CabinetWClass"    // 일반 폴더 창
            || cls == "ExploreWClass";   // 탐색 창(트리 보기)
    }

    /// <summary>윈도우 기본 위치로 탐색기를 새로 연다(인자 없이 실행 → '파일 탐색기 열기' 설정을 따름).</summary>
    public static void OpenDefaultExplorer()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true }); }
        catch { }
    }

    /// <summary>대상 창의 항상-위 상태를 토글하고 새 상태를 반환(창이 없으면 false).</summary>
    public bool ToggleTopMost(AppEntry app)
    {
        IntPtr h = FindMainWindow(app);
        if (h == IntPtr.Zero) return false;
        long ex = NativeMethods.GetWindowLongPtr(h, NativeMethods.GWL_EXSTYLE).ToInt64();
        bool isTop = (ex & NativeMethods.WS_EX_TOPMOST) != 0;
        NativeMethods.SetWindowPos(h, isTop ? NativeMethods.HWND_NOTOPMOST : NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        return !isTop;
    }

    /// <summary>대상 창을 닫는다(WM_CLOSE — 앱에 정상 종료 요청). 창이 없으면 false.</summary>
    public bool CloseWindow(AppEntry app)
    {
        IntPtr h = FindMainWindow(app);
        if (h == IntPtr.Zero) return false;
        NativeMethods.PostMessage(h, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    /// <summary>대상 창을 최소화한다. 창이 없으면 false.</summary>
    public bool MinimizeWindow(AppEntry app)
    {
        IntPtr h = FindMainWindow(app);
        if (h == IntPtr.Zero) return false;
        NativeMethods.ShowWindow(h, NativeMethods.SW_MINIMIZE);
        return true;
    }

    private static IntPtr FindMainWindow(AppEntry app)
    {
        string nameNoExe = app.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? app.ProcessName[..^4] : app.ProcessName;
        if (string.IsNullOrEmpty(nameNoExe)) return IntPtr.Zero;

        // 대상 실행 파일명을 공유하는 모든 프로세스 ID 수집.
        // 앱 활성화/항상위 토글마다 호출되므로, GetProcessesByName()가 돌려준 Process 객체(핸들 보유)는
        // finally에서 전부 Dispose 해 핸들 누적을 막는다.
        var pids = new HashSet<uint>();
        try
        {
            var procs = Process.GetProcessesByName(nameNoExe);
            try { foreach (var p in procs) pids.Add((uint)p.Id); }
            finally { foreach (var p in procs) p.Dispose(); }
        }
        catch { }
        if (pids.Count == 0) return IntPtr.Zero;

        // Process.MainWindowHandle 은 Chromium/Electron/WebView2 계열(예: ChatGPT 데스크톱)에서
        // 보이는 창을 소유한 프로세스가 핸들을 노출하지 않아 0 을 돌려주는 경우가 있다. 그래서
        // AHK 의 ahk_exe 매칭처럼 시스템의 모든 최상위 창을 직접 열거해, 위 프로세스가 소유한
        // "보이는 · 소유자 없는 · 제목 있는" 창을 Z-order 상단부터 찾는다(가장 앞의 실제 창).
        IntPtr found = IntPtr.Zero;
        IntPtr fallback = IntPtr.Zero;   // 제목 없는 후보(제목 있는 창이 하나도 없을 때만 사용)
        try
        {
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hwnd)) return true;                              // 숨김/트레이 창 제외
                if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero) return true; // 소유된 보조 창 제외
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                if (!pids.Contains(pid)) return true;
                if (NativeMethods.GetWindowTextLength(hwnd) > 0) { found = hwnd; return false; }    // 제목 있는 창 → 즉시 채택
                if (fallback == IntPtr.Zero) fallback = hwnd;                                       // 제목 없으면 후보로만 보관
                return true;
            }, IntPtr.Zero);
        }
        catch { }

        return found != IntPtr.Zero ? found : fallback;
    }

    private static void Run(AppEntry app)
    {
        string? path = IconService.ResolvePath(app) ?? (string.IsNullOrEmpty(app.ExePath) ? null : app.ExePath);
        if (string.IsNullOrEmpty(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { }
    }
}

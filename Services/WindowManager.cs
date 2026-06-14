using System;
using System.Diagnostics;
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

    private static IntPtr FindMainWindow(AppEntry app)
    {
        string nameNoExe = app.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? app.ProcessName[..^4] : app.ProcessName;
        if (string.IsNullOrEmpty(nameNoExe)) return IntPtr.Zero;
        try
        {
            foreach (var p in Process.GetProcessesByName(nameNoExe))
            {
                IntPtr h = p.MainWindowHandle;
                if (h != IntPtr.Zero) return h;
            }
        }
        catch { }
        return IntPtr.Zero;
    }

    private static void Run(AppEntry app)
    {
        string? path = IconService.ResolvePath(app) ?? (string.IsNullOrEmpty(app.ExePath) ? null : app.ExePath);
        if (string.IsNullOrEmpty(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { }
    }
}

using System;
using Microsoft.Win32;

namespace FocusClip.Services;

/// <summary>윈도우 시작 시 자동 실행(HKCU Run 키) 등록/해제.</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FocusClip";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) != null;
    }

    public static void SetEnabled(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (k is null) return;
        if (on)
            k.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else
            k.DeleteValue(ValueName, false);
    }
}

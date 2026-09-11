using System;
using FocusClip.Interop;

namespace FocusClip.Services;

/// <summary>현재 포그라운드 창에 Ctrl+V 를 합성해 붙여넣는다. (CM의 _send_paste 대응)</summary>
public static class PasteService
{
    public static void SendCtrlV()
    {
        NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_V, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_V, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>현재 포그라운드 창에 Ctrl+Tab 을 합성한다 — 브라우저·에디터 등에서 다음 탭으로 이동.
    /// 앱마다 별도 API 없이도 통하는 공통 관례라, 특정 프로그램(크롬 등)에 국한하지 않는다.</summary>
    public static void SendCtrlTab()
    {
        NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_TAB, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_TAB, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}

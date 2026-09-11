using System;
using FocusClip.Interop;

namespace FocusClip.Services;

/// <summary>현재 포그라운드 창에 Ctrl+V/Ctrl+Tab 을 합성한다. (CM의 _send_paste 대응)
/// 합성 입력에는 전부 NativeMethods.CAPS_SYNTH_TAG 표식을 붙인다 — 안 붙이면 도크가 떠 있는 동안
/// 저수준 키보드 후크가 이 합성 키 입력을 '사용자가 실제로 누른 키'로 오인해, Ctrl+Tab 의 Tab 이
/// 숫자·Esc·백틱이 아닌 다른 키로 잡혀 오토클로즈(DismissRequested)를 걸고 도크를 닫아버린다.</summary>
public static class PasteService
{
    public static void SendCtrlV()
    {
        UIntPtr tag = (UIntPtr)(ulong)NativeMethods.CAPS_SYNTH_TAG;
        NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, 0, tag);
        NativeMethods.keybd_event(NativeMethods.VK_V, 0, 0, tag);
        NativeMethods.keybd_event(NativeMethods.VK_V, 0, NativeMethods.KEYEVENTF_KEYUP, tag);
        NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, NativeMethods.KEYEVENTF_KEYUP, tag);
    }

    /// <summary>현재 포그라운드 창에 Ctrl+Tab 을 합성한다 — 브라우저·에디터 등에서 다음 탭으로 이동.
    /// 앱마다 별도 API 없이도 통하는 공통 관례라, 특정 프로그램(크롬 등)에 국한하지 않는다.</summary>
    public static void SendCtrlTab()
    {
        UIntPtr tag = (UIntPtr)(ulong)NativeMethods.CAPS_SYNTH_TAG;
        NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, 0, tag);
        NativeMethods.keybd_event(NativeMethods.VK_TAB, 0, 0, tag);
        NativeMethods.keybd_event(NativeMethods.VK_TAB, 0, NativeMethods.KEYEVENTF_KEYUP, tag);
        NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, NativeMethods.KEYEVENTF_KEYUP, tag);
    }
}

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
}

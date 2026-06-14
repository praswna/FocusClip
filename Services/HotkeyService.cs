using System;
using System.Runtime.InteropServices;
using FocusClip.Interop;

namespace FocusClip.Services;

/// <summary>
/// 전역 단축키(기본 CapsLock)를 로우레벨 키보드 후크로 즉시 감지한다.
/// FocusManager(AHK)와 Clipboard-Manager(폴링)의 단축키 처리를 하나로 대체 —
/// 폴링 지연/프로세스 간 충돌/깜빡임 없음. 단축키는 통과(pass-through),
/// 도크가 떠 있을 때의 숫자키(1~4)·Esc 는 가로채서 소비한다(FM의 PopupIsVisible 동작).
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private bool _keyDown;

    public int HotkeyVk { get; set; } = NativeMethods.VK_CAPITAL;

    /// <summary>도크가 떠 있을 때 true → 숫자키/Esc 를 가로채 소비한다.</summary>
    public bool CaptureExtraKeys { get; set; }

    public event Action? HotkeyPressed;       // 단축키 토글
    public event Action<int>? NumberPressed;  // 1~4 (도크 표시 중)
    public event Action? EscapePressed;       // Esc (도크 표시 중)
    public event Action? DismissRequested;    // 도크 표시 중 그 외 키 입력 → 오토클로즈(키는 통과)

    public HotkeyService()
    {
        _proc = HookCallback; // 델리게이트 GC 방지
    }

    public void Install()
    {
        if (_hookId != IntPtr.Zero) return;
        IntPtr hMod = NativeMethods.GetModuleHandle(null);
        _hookId = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, hMod, 0);
        if (_hookId == IntPtr.Zero)
            throw new InvalidOperationException($"SetWindowsHookEx 실패 (err={Marshal.GetLastWin32Error()})");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            bool down = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
            bool up = msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP;

            if (data.vkCode == (uint)HotkeyVk)
            {
                if (down && !_keyDown) { _keyDown = true; Raise(HotkeyPressed); }
                else if (up) { _keyDown = false; }
            }
            else if (CaptureExtraKeys && down)
            {
                uint vk = data.vkCode;
                if (vk >= 0x31 && vk <= 0x34) // 1~4
                {
                    int n = (int)(vk - 0x30);
                    try { NumberPressed?.Invoke(n); } catch { }
                    return (IntPtr)1; // 소비(대상 앱으로 흘러가지 않게)
                }
                if (vk == 0x1B) // Esc
                {
                    Raise(EscapePressed);
                    return (IntPtr)1;
                }
                // 그 외 키: 수식키가 아니면 오토클로즈 요청(키는 소비하지 않고 대상 앱에 전달 — CM 동일)
                if (!IsModifier(vk))
                    Raise(DismissRequested);
            }
        }
        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static void Raise(Action? e)
    {
        try { e?.Invoke(); } catch { /* 후크는 절대 죽지 않게 */ }
    }

    /// <summary>Shift/Ctrl/Alt/Win 류 수식키 여부(단독으로 눌려도 오토클로즈 안 함).</summary>
    private static bool IsModifier(uint vk) => vk switch
    {
        0x10 or 0x11 or 0x12 => true,            // Shift, Ctrl, Alt
        >= 0xA0 and <= 0xA5 => true,             // L/R Shift/Ctrl/Alt
        0x5B or 0x5C => true,                     // L/R Win
        _ => false,
    };

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }
}

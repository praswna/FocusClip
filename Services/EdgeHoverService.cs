using System;
using System.Runtime.InteropServices;
using FocusClip.Interop;

namespace FocusClip.Services;

/// <summary>
/// 화면 왼쪽 가장자리에 마우스가 닿는 순간을 로우레벨 마우스 후크로 감지한다.
/// 자동 숨김된 사이드바를 다시 불러내는 트리거 전용.
///
/// 가장자리에 얇은 감지용 창을 띄우는 방식도 가능하지만, 그 창이 화면 끝 클릭을
/// 가로채 버린다(최대화된 창의 왼쪽 스크롤바 등 화면 끝을 겨냥하는 조작이 막힘).
/// 후크는 입력을 소비하지 않으므로 다른 앱 조작을 전혀 방해하지 않는다.
/// 폴링도 없다 — HotkeyService 의 키보드 후크와 같은 방식.
///
/// 후크 콜백은 마우스가 움직일 때마다 시스템 전역에서 호출되므로 반드시 싸야 한다.
/// 그래서 사이드바가 숨어 있는 동안에만 Install 하고, 다시 보이면 Uninstall 한다.
/// </summary>
public sealed class EdgeHoverService : IDisposable
{
    private readonly NativeMethods.LowLevelMouseProc _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private bool _inside; // 가장자리 안에 있는 동안 이벤트가 연속 발사되지 않게

    /// <summary>가장자리로 인정할 폭(물리 px). EdgeXPx 부터 이 폭만큼이 감지 영역.</summary>
    public int ThresholdPx { get; set; } = 2;

    /// <summary>감지 기준이 되는 화면 왼쪽 끝 X(물리 px).</summary>
    public int EdgeXPx { get; set; }

    /// <summary>감지할 세로 구간(물리 px). 사이드바가 놓인 높이 범위에서만 반응하도록 제한.</summary>
    public int BandTopPx { get; set; } = int.MinValue;
    public int BandBottomPx { get; set; } = int.MaxValue;

    /// <summary>커서가 감지 영역 밖에서 안으로 들어온 순간 1회 발생. 후크를 설치한 스레드(UI)에서 호출된다.</summary>
    public event Action? EdgeEntered;

    public EdgeHoverService()
    {
        _proc = HookCallback; // 델리게이트 GC 방지
    }

    public bool IsInstalled => _hookId != IntPtr.Zero;

    /// <summary>후크를 설치한다. 실패해도 예외를 던지지 않는다 —
    /// 가장자리 복귀가 안 될 뿐 사이드바 자체는 동작해야 하므로 호출부가 계속 진행할 수 있어야 한다.</summary>
    public bool Install()
    {
        if (_hookId != IntPtr.Zero) return true;
        _inside = false;
        IntPtr hMod = NativeMethods.GetModuleHandle(null);
        _hookId = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, hMod, 0);
        return _hookId != IntPtr.Zero;
    }

    public void Uninstall()
    {
        if (_hookId == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
        _inside = false;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt32() == NativeMethods.WM_MOUSEMOVE)
        {
            int x = Marshal.ReadInt32(lParam, NativeMethods.MSLLHOOKSTRUCT_X_OFFSET);
            int y = Marshal.ReadInt32(lParam, NativeMethods.MSLLHOOKSTRUCT_Y_OFFSET);

            bool hit = x >= EdgeXPx && x < EdgeXPx + ThresholdPx
                       && y >= BandTopPx && y <= BandBottomPx;

            if (hit)
            {
                if (!_inside)
                {
                    _inside = true;
                    EdgeEntered?.Invoke();
                }
            }
            else
            {
                _inside = false;
            }
        }
        // 항상 통과 — 마우스 입력을 절대 소비하지 않는다.
        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}

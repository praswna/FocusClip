using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WinFormsScreen = System.Windows.Forms.Screen;

namespace FocusClip.Interop;

/// <summary>
/// 멀티모니터 작업영역 도우미. <see cref="SystemParameters.WorkArea"/> 는 주 모니터만 반환하므로,
/// 커서/창이 실제로 놓인 모니터의 작업영역을 구해 보조 모니터에서도 도크·팝업이 뜨게 한다.
/// (앱은 System-DPI-aware 라 가상 데스크톱 전체가 단일 배율 → GetDpi 한 번으로 DIP 변환 가능.)
/// </summary>
internal static class ScreenUtil
{
    /// <summary>지정한 창이 놓인 모니터의 작업영역을 DIP 단위로 반환.</summary>
    public static Rect WorkAreaDip(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        // FromHandle 은 MONITOR_DEFAULTTONEAREST 동작 → 잘못된/0 핸들이면 주 모니터를 돌려줌(안전).
        var screen = WinFormsScreen.FromHandle(hwnd);
        return ToDip(screen.WorkingArea, window);
    }

    /// <summary>물리 좌표(px) 점이 속한 모니터의 작업영역을 DIP 단위로 반환.</summary>
    public static Rect WorkAreaDipFromPhysical(int px, int py, Visual dpiSource)
    {
        var screen = WinFormsScreen.FromPoint(new System.Drawing.Point(px, py));
        return ToDip(screen.WorkingArea, dpiSource);
    }

    private static Rect ToDip(System.Drawing.Rectangle r, Visual dpiSource)
    {
        double sx = 1.0, sy = 1.0;
        try { var dpi = VisualTreeHelper.GetDpi(dpiSource); sx = dpi.DpiScaleX; sy = dpi.DpiScaleY; }
        catch { /* 비주얼이 아직 트리에 없으면 1배로 폴백 */ }
        return new Rect(r.Left / sx, r.Top / sy, r.Width / sx, r.Height / sy);
    }
}

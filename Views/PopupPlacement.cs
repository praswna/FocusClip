using System;
using System.Windows;
using FocusClip.Interop;

namespace FocusClip.Views;

/// <summary>도크 주변 팝업 배치 공통 계산. 프롬프트·고정(핀) 팝업이 같은 규칙으로 오른쪽 열에 쌓이도록
/// 한 곳에 모았다(창마다 복사해 두면 규칙이 갈라진다).</summary>
internal static class PopupPlacement
{
    /// <summary>팝업 사이 간격(px, DIP).</summary>
    private const double Gap = 6;

    /// <summary>
    /// <paramref name="win"/>을 <paramref name="anchor"/> 오른쪽에 배치한다.
    /// <paramref name="alignBottom"/>이면 아래 모서리를 맞춰 위로 자라게 한다 — 도크 위에 뜬 클립 팝업 옆에
    /// 붙을 때 이 팝업이 더 길어도 도크·경로 팝업을 덮지 않는다.
    /// <paramref name="pushAvoid"/>에 있는(보이는) 창들의 오른쪽 끝 바깥으로 비켜 놓고,
    /// <paramref name="stackAvoid"/>와 세로로 겹치면 같은 열에서 위/아래로 비켜 쌓는다.
    /// 오른쪽 공간이 부족하면 기준 창들의 왼쪽으로 뒤집는다.
    /// </summary>
    public static void PlaceRightOf(Window win, Window anchor, bool alignBottom = false,
                                    Window? stackAvoid = null, params Window?[] pushAvoid)
    {
        var wa = ScreenUtil.WorkAreaDip(anchor); // 기준 창이 놓인 모니터 기준(멀티모니터)

        double rightEdge = anchor.Left + anchor.ActualWidth; // 비켜야 할 오른쪽 끝
        double leftEdge = anchor.Left;                       // 왼쪽 뒤집기 기준
        foreach (var a in pushAvoid)
        {
            if (a == null || !a.IsVisible) continue;
            rightEdge = Math.Max(rightEdge, a.Left + a.ActualWidth);
            leftEdge = Math.Min(leftEdge, a.Left);
        }

        double left = rightEdge + Gap;
        // 오른쪽 공간이 부족하면 왼쪽으로 뒤집어 배치
        if (left + win.ActualWidth > wa.Right)
            left = leftEdge - win.ActualWidth - Gap;
        win.Left = Math.Max(wa.Left, Math.Min(left, wa.Right - win.ActualWidth));

        // 상단 정렬(기본) 또는 아래 모서리 정렬(위로 자람)
        double top = alignBottom ? anchor.Top + anchor.ActualHeight - win.ActualHeight : anchor.Top;

        // 같은 열에 이미 놓인 창과 겹치면(도크가 화면 끝이라 팝업들이 한쪽으로 몰린 경우) 그 위/아래로 비켜 쌓는다.
        if (stackAvoid != null && stackAvoid.IsVisible
            && win.Left < stackAvoid.Left + stackAvoid.ActualWidth && win.Left + win.ActualWidth > stackAvoid.Left
            && top < stackAvoid.Top + stackAvoid.ActualHeight && top + win.ActualHeight > stackAvoid.Top)
        {
            top = top >= stackAvoid.Top
                ? stackAvoid.Top + stackAvoid.ActualHeight + Gap  // 아래로
                : stackAvoid.Top - win.ActualHeight - Gap;        // 위로
        }
        win.Top = Math.Max(wa.Top, Math.Min(top, wa.Bottom - win.ActualHeight));
    }
}

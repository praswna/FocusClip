using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using FocusClip.Interop;

namespace FocusClip.Views;

/// <summary>도크 주변 팝업을 고정 열 그리드에 배치한다. 0열은 도크 왼쪽에 맞추고, 1·2열은 (팝업 폭 + 간격)만큼
/// 일정하게 떨어진다. 열 자리는 '어떤 팝업이 지금 떠 있는지'와 무관하므로, 클립·경로 목록이 비거나 차도
/// 나머지 팝업이 좌우로 밀려다니지 않는다. 오른쪽 공간이 모자라면 그리드 전체가 한 번에 왼쪽으로 자라고,
/// 그래도 넘치면 그리드를 통째로 밀어 넣는다 — 팝업이 제각각 빈 자리를 찾아 흩어지지 않게.</summary>
internal static class PopupPlacement
{
    /// <summary>팝업 사이 간격(px, DIP). 도크·팝업 배치가 전부 이 값을 쓴다.</summary>
    private const double Gap = 6;

    /// <summary>세로 구역. Above 는 아래 모서리를 도크 윗변에, Below 는 위 모서리를 도크 아랫변에 맞춘다.</summary>
    public enum Band { Above, Below }

    /// <summary>그리드 한 칸 — 몇 번째 열, 어느 구역. Window 가 null 이면 열 자리만 잡아 두고 배치는 건너뛴다
    /// (팝업이 비었거나, 사용자가 핀으로 직접 옮겨 둔 경우). 열 개수는 이 칸 목록이 정하므로
    /// 지금 몇 개가 떠 있든 그리드가 쓰는 폭은 늘 같다.</summary>
    public readonly record struct Cell(Window? Window, int Column, Band Band);

    /// <summary>보이는 팝업들을 도크 기준 고정 열 그리드에 배치한다.</summary>
    public static void Layout(Window dock, IEnumerable<Cell> cells)
    {
        var all = cells.ToList();
        var placed = all.Where(c => c.Window is { IsVisible: true })
                        .Select(c => (Win: c.Window!, Col: c.Column, Zone: c.Band))
                        .ToList();
        if (placed.Count == 0) return;

        var wa = ScreenUtil.WorkAreaDip(dock); // 도크가 놓인 모니터 기준(멀티모니터)
        double dockBottom = dock.Top + dock.ActualHeight;

        // 열 간격은 가장 넓은 팝업 기준 — 폭이 모두 같으면 열이 정확히 맞물리고, 달라도 겹치지 않는다.
        double colWidth = placed.Max(p => Width(p.Win));
        double step = colWidth + Gap;

        // 자라는 방향은 그리드 전체가 한 번에, 그것도 '빈 열까지 포함한' 폭으로 정한다 — 팝업마다 따로 뒤집으면
        // 배치가 제각각이 되고, 떠 있는 것만 세면 항목이 생길 때마다 방향이 뒤집혀 열이 통째로 옮겨 다닌다.
        double dx = dock.Left + all.Max(c => c.Column) * step + colWidth > wa.Right ? -step : step;
        // 화면 밖으로 나가는 만큼만 그리드를 통째로 민다(실제로 떠 있는 열 기준 — 공연히 도크에서 떼어놓지 않게).
        double origin = FitOrigin(dock.Left, dx, placed.Max(p => p.Col), colWidth, wa);

        // 세로도 구역마다 한 번씩만 정해, 같은 구역의 팝업이 모두 같은 기준선에 나란히 서게 한다.
        double aboveHeight = BandHeight(placed, Band.Above);
        double belowHeight = BandHeight(placed, Band.Below);
        bool aboveFits = dock.Top - Gap - aboveHeight >= wa.Top;
        bool belowFits = dockBottom + Gap + belowHeight <= wa.Bottom;

        double aboveLine, belowLine; // 구역 기준선
        bool aboveUp, belowUp;       // true면 기준선이 아래 모서리(위로 자람)
        if (!aboveFits && belowFits)
        {
            // 도크 위가 좁다 → 위 구역을 통째로 도크 아래로 내리고, 아래 구역은 그 밑에 붙인다.
            aboveLine = dockBottom + Gap;
            belowLine = aboveLine + (aboveHeight > 0 ? aboveHeight + Gap : 0);
            aboveUp = belowUp = false;
        }
        else if (!belowFits && aboveFits)
        {
            // 도크 아래가 좁다 → 아래 구역을 통째로 도크 위로 올리고, 위 구역은 그 위에 붙인다.
            belowLine = dock.Top - Gap;
            aboveLine = belowLine - (belowHeight > 0 ? belowHeight + Gap : 0);
            aboveUp = belowUp = true;
        }
        else
        {
            aboveLine = dock.Top - Gap;
            belowLine = dockBottom + Gap;
            aboveUp = true;
            belowUp = false;
        }

        foreach (var (win, column, zone) in placed)
        {
            double line = zone == Band.Above ? aboveLine : belowLine;
            bool up = zone == Band.Above ? aboveUp : belowUp;
            double height = Height(win);
            win.Left = Clamp(origin + column * dx, wa.Left, wa.Right - Width(win));
            win.Top = Clamp(up ? line - height : line, wa.Top, wa.Bottom - height);
        }
    }

    /// <summary>열이 전부 작업영역 안에 들어가도록 그리드 전체를 같은 양만큼 민다(열 간격은 그대로 유지).</summary>
    private static double FitOrigin(double origin, double dx, int lastColumn, double colWidth, Rect wa)
    {
        double last = origin + lastColumn * dx;
        double gridLeft = Math.Min(origin, last);
        double gridRight = Math.Max(origin, last) + colWidth;
        if (gridRight > wa.Right) { double over = gridRight - wa.Right; origin -= over; gridLeft -= over; }
        if (gridLeft < wa.Left) origin += wa.Left - gridLeft;
        return origin;
    }

    /// <summary>한 구역에서 가장 높은 팝업의 높이(구역이 비었으면 0).</summary>
    private static double BandHeight(IEnumerable<(Window Win, int Col, Band Zone)> placed, Band band)
        => placed.Where(p => p.Zone == band).Select(p => Height(p.Win)).DefaultIfEmpty(0).Max();

    /// <summary>아직 측정 전(ActualWidth=0)이면 XAML 에 선언한 폭으로 대신한다.</summary>
    private static double Width(Window w)
    {
        if (w.ActualWidth > 0) return w.ActualWidth;
        return double.IsNaN(w.Width) ? w.MinWidth : w.Width;
    }

    /// <summary>SizeToContent 라 Height 가 NaN 일 수 있어, 측정 전에는 MinHeight 로 폴백한다.</summary>
    private static double Height(Window w)
    {
        if (w.ActualHeight > 0) return w.ActualHeight;
        return double.IsNaN(w.Height) ? w.MinHeight : w.Height;
    }

    /// <summary>Math.Clamp 와 달리 lo &gt; hi(창이 작업영역보다 큰 경우)에도 던지지 않고 lo 를 준다.</summary>
    private static double Clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(v, hi));
}

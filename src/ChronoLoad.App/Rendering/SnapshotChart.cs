using System.Windows;
using System.Windows.Media;
using ChronoLoad.App.Services;
using ChronoLoad.Core.Layout;
using ChronoLoad.Core.Metrics;

namespace ChronoLoad.App.Rendering;

/// <summary>
/// 스냅샷 창의 차트 (§9.6). 얼어붙은 배열의 <b>임의 구간</b>을 그린다.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ChartSurface"/>를 쓰지 않는 이유는 필요한 것이 다르기 때문이다. 저쪽은 늘
/// <b>가장 최근</b> N 점을 그리고 스크럽선을 얹는다. 여기는 확대·스크롤로 정한 구간을 그리고
/// 구간 선택 밴드를 얹는다. 라이브 차트에 조망 구간 개념을 집어넣어 봐야 라이브 쪽에는
/// 쓸 일이 없고, 그 위험만 나눠 갖는다.
/// </para>
/// <para>
/// 세로 축은 확대하지 않는다. 퍼센트는 0~100 고정이라 <b>같은 자리의 같은 높이가 늘 같은 값</b>
/// 이어야 하고, 세로로 늘이는 순간 그 규칙이 무너진다.
/// </para>
/// </remarks>
public sealed class SnapshotChart : FrameworkElement
{
    private readonly StreamGeometry _fill = new();
    private readonly StreamGeometry _line = new();
    private readonly StreamGeometry _secondLine = new();

    public SnapshotMetric? Metric { get; set; }

    /// <summary>같은 축에 겹쳐 그리는 둘째 계열(네트워크 송신, 디스크 쓰기).</summary>
    public SnapshotMetric? Secondary { get; set; }

    public long[] Timestamps { get; set; } = [];

    /// <summary>그릴 구간 — <see cref="ViewStart"/> 이상 <c>ViewStart + ViewCount</c> 미만.</summary>
    public int ViewStart { get; set; }

    public int ViewCount { get; set; }

    /// <summary>선택 구간(전체 배열 인덱스). 없으면 null.</summary>
    public int? SelectionStart { get; set; }

    public int? SelectionEnd { get; set; }

    /// <summary>
    /// 점 하나만 집었을 때의 자리. 구간이 아니라 <b>순간</b>이므로 밴드가 아니라 선으로 긋고,
    /// 각 계열의 값을 점으로 찍는다 — 메인 창의 스크럽(§8.7)과 같은 읽기다.
    /// </summary>
    public int? ScrubIndex { get; set; }

    public ThemePalette Palette { get; set; } = ThemePalette.Dark;
    public Color Accent { get; set; } = Colors.DodgerBlue;

    /// <summary>축 상한. 창이 보이는 구간에서 정해 넘긴다(홈통 라벨과 같은 수여야 한다).</summary>
    public double FixedMax { get; set; }

    /// <summary>세로 격자를 그릴 자리(0~1 비율). 창이 시각에서 계산해 넘긴다 — 전 차트 공통이다.</summary>
    public IReadOnlyList<double> TimeTicks { get; set; } = [];

    public long GapThresholdTicks { get; set; }

    /// <summary>이번에 쓴 축 상한. 홈통 라벨이 읽어 간다.</summary>
    public double AxisMax { get; private set; } = 100;

    public void Invalidate() => InvalidateVisual();

    /// <summary>X 좌표 → 전체 배열 인덱스. 드래그가 쓴다.</summary>
    public int IndexAt(double x)
    {
        if (ViewCount <= 1 || ActualWidth < 1) return ViewStart;
        double ratio = Math.Clamp(x / ActualWidth, 0, 1);
        return ViewStart + (int)Math.Round(ratio * (ViewCount - 1));
    }

    /// <summary>전체 배열 인덱스 → X 좌표.</summary>
    public double XOf(int index) =>
        ViewCount <= 1 ? 0 : ActualWidth * Math.Clamp(index - ViewStart, 0, ViewCount - 1) / (ViewCount - 1);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;

        // 빈 구간에서도 드래그가 잡히도록 먼저 깐다.
        dc.DrawRectangle(new SolidColorBrush(Palette.Surface), null, new Rect(0, 0, w, h));

        if (Metric is null || ViewCount < 2) return;
        int from = Math.Max(0, ViewStart);
        int to = Math.Min(Metric.Count, ViewStart + ViewCount);
        if (to - from < 2) return;

        // 상한은 창이 정해 넘긴다. 홈통 라벨과 같은 수를 써야 그림과 숫자가 어긋나지 않는다.
        AxisMax = FixedMax > 0 ? FixedMax : 1;
        DrawGrid(dc, w, h);
        DrawSelection(dc, w, h);

        double Y(float v) => h - Math.Clamp(v / AxisMax, 0, 1) * (h - 2) - 1;

        FillArea(dc, w, h, from, to, Y);
        DrawSeries(dc, _line, Metric, from, to, w, Y, Pen(Accent, 1.5, 0xFF));
        if (Secondary is not null)
            DrawSeries(dc, _secondLine, Secondary, from, to, w, Y, Pen(Accent, 1.2, 0x88));

        DrawGapSeams(dc, w, h, from, to);
        DrawScrub(dc, h, from, to, Y);
    }


    private void DrawGrid(DrawingContext dc, double w, double h)
    {
        var minor = Pen(Palette.Line, 1, 0x88);
        var mid = Pen(Palette.Line, 1, 0xDD);

        foreach (double fraction in (double[])[0.25, 0.5, 0.75])
        {
            double y = Math.Round(h * fraction) + 0.5;
            dc.DrawLine(fraction == 0.5 ? mid : minor, new Point(0, y), new Point(w, y));
        }

        // 세로 격자는 전 차트가 같은 x 에 긋는다. 어긋나면 "같은 순간을 가로질러 본다"가 무너진다.
        foreach (double fraction in TimeTicks)
        {
            double x = Math.Round(w * fraction) + 0.5;
            dc.DrawLine(minor, new Point(x, 0), new Point(x, h));
        }
    }

    private void DrawSelection(DrawingContext dc, double w, double h)
    {
        if (SelectionStart is not { } a || SelectionEnd is not { } b) return;

        double x1 = XOf(Math.Min(a, b)), x2 = XOf(Math.Max(a, b));
        if (x2 - x1 < 1) x2 = x1 + 1;

        var band = new SolidColorBrush(Color.FromArgb(0x1A, Palette.Gpu.R, Palette.Gpu.G, Palette.Gpu.B));
        band.Freeze();
        dc.DrawRectangle(band, null, new Rect(x1, 0, x2 - x1, h));

        var edge = Pen(Palette.Gpu, 1.2, 0xFF);
        dc.DrawLine(edge, new Point(Math.Round(x1) + 0.5, 0), new Point(Math.Round(x1) + 0.5, h));
        dc.DrawLine(edge, new Point(Math.Round(x2) + 0.5, 0), new Point(Math.Round(x2) + 0.5, h));
    }

    private void FillArea(DrawingContext dc, double w, double h, int from, int to, Func<float, double> y)
    {
        _fill.Clear();
        using (var ctx = _fill.Open())
        {
            bool open = false;
            for (int i = from; i < to; i++)
            {
                float v = Metric!.Values[i];
                double x = XOf(i);
                if (float.IsNaN(v) || (open && BreaksBefore(i)))
                {
                    if (open) { ctx.LineTo(new Point(x, h), false, false); open = false; }
                    if (float.IsNaN(v)) continue;
                }

                if (!open)
                {
                    ctx.BeginFigure(new Point(x, h), isFilled: true, isClosed: true);
                    ctx.LineTo(new Point(x, y(v)), false, true);
                    open = true;
                }
                else ctx.LineTo(new Point(x, y(v)), true, true);
            }
            if (open) ctx.LineTo(new Point(XOf(to - 1), h), false, false);
        }

        var brush = new LinearGradientBrush(
            Color.FromArgb(0x47, Accent.R, Accent.G, Accent.B),
            Color.FromArgb(0x05, Accent.R, Accent.G, Accent.B),
            new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        dc.DrawGeometry(brush, null, _fill);
    }

    private void DrawSeries(DrawingContext dc, StreamGeometry geometry, SnapshotMetric metric,
                            int from, int to, double w, Func<float, double> y, Pen pen)
    {
        geometry.Clear();
        using (var ctx = geometry.Open())
        {
            bool open = false;
            for (int i = from; i < to; i++)
            {
                float v = metric.Values[i];
                if (float.IsNaN(v)) { open = false; continue; }
                if (BreaksBefore(i)) open = false;

                var point = new Point(XOf(i), y(v));
                if (!open) { ctx.BeginFigure(point, false, false); open = true; }
                else ctx.LineTo(point, true, true);
            }
        }
        dc.DrawGeometry(null, pen, geometry);
    }

    private void DrawScrub(DrawingContext dc, double h, int from, int to, Func<float, double> y)
    {
        if (ScrubIndex is not { } index || index < from || index >= to) return;

        double x = Math.Round(XOf(index)) + 0.5;
        dc.DrawLine(Dashed(Palette.Fg, 1, 0x9C), new Point(x, 0), new Point(x, h));

        Dot(dc, x, Metric!, index, y, Accent, 1);
        if (Secondary is not null) Dot(dc, x, Secondary, index, y, Accent, 0.75);
    }

    /// <summary>계열 색으로 찍는다 — 어느 값을 읽고 있는지가 점 자체로 드러나야 한다.</summary>
    private void Dot(DrawingContext dc, double x, SnapshotMetric metric, int index,
                     Func<float, double> y, Color color, double opacity)
    {
        float v = metric.Values[index];
        if (float.IsNaN(v)) return;

        var fill = new SolidColorBrush(color) { Opacity = opacity };
        fill.Freeze();
        dc.DrawEllipse(fill, Pen(Palette.Surface, 1.5, 0xFF), new Point(x, y(v)), 3, 3);
    }

    /// <summary>수집이 끊긴 자리(§13). 폭이 없으므로 두 줄로 긋고 선도 잇지 않는다.</summary>
    private void DrawGapSeams(DrawingContext dc, double w, double h, int from, int to)
    {
        if (GapThresholdTicks <= 0 || Timestamps.Length < 2) return;
        var pen = Pen(Palette.Faint, 1, 0xB0);

        for (int i = Math.Max(1, from); i < to; i++)
        {
            if (!BreaksBefore(i)) continue;
            double mid = (XOf(i - 1) + XOf(i)) / 2;
            dc.DrawLine(pen, new Point(Math.Round(mid - 1.5) + 0.5, 0), new Point(Math.Round(mid - 1.5) + 0.5, h));
            dc.DrawLine(pen, new Point(Math.Round(mid + 1.5) + 0.5, 0), new Point(Math.Round(mid + 1.5) + 0.5, h));
        }
    }

    private bool BreaksBefore(int index)
    {
        if (GapThresholdTicks <= 0 || index <= 0 || index >= Timestamps.Length) return false;
        long delta = Timestamps[index] - Timestamps[index - 1];
        return delta > GapThresholdTicks || delta < 0;
    }

    private static Pen Dashed(Color color, double thickness, byte alpha)
    {
        var pen = Pen(color, thickness, alpha);
        var dashed = pen.Clone();
        dashed.DashStyle = new DashStyle([4, 3], 0);
        dashed.Freeze();
        return dashed;
    }

    private static Pen Pen(Color color, double thickness, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }
}

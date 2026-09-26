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
    private readonly StreamGeometry _band = new();

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

    /// <summary>
    /// 둘째 계열을 첫째 <b>위에 쌓는다</b>(메인 창의 GPU 메모리와 같은 규칙, §8.3).
    /// </summary>
    /// <remarks>
    /// 메모리는 파형이 아니라 점유량이다. 전용과 공유를 따로 그으면 "합쳐서 얼마나 잡고 있나"를
    /// 눈으로 더해야 하는데, 그것이 바로 보려던 값이다. 전용을 바닥에 깔고 공유를 그 위에 얹으면
    /// 띠의 윗변이 곧 합계다.
    /// </remarks>
    public bool Stacked { get; set; }

    /// <summary>전용 VRAM 용량선을 그을 자리(바이트). 0 이면 긋지 않는다.</summary>
    public double CapacityLine { get; set; }

    /// <summary>외장 GPU 인가. 외장에서만 공유 메모리가 경고색이 된다.</summary>
    public bool Discrete { get; set; }

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

        if (Stacked && Secondary is not null) DrawStacked(dc, w, h, from, to, Y);
        else
        {
            FillArea(dc, w, h, from, to, Y);
            DrawSeries(dc, _line, Metric, from, to, w, Y, Pen(Accent, 1.5, 0xFF));
            if (Secondary is not null)
                DrawSeries(dc, _secondLine, Secondary, from, to, w, Y, Pen(Accent, 1.2, 0x88));
        }

        DrawGapSeams(dc, w, h, from, to);
        DrawScrub(dc, h, from, to, Y);
    }


    /// <summary>
    /// 전용을 바닥에 깔고 공유를 그 위에 얹는다. 용량선은 넘친 구간이 있으면 경고색이다.
    /// </summary>
    /// <remarks>
    /// 공유 띠는 색이 아니라 <b>해치 패턴</b>으로도 구분한다 — 색약 사용자를 배려한 것으로,
    /// 메인 창과 같은 처리다. 외장에서 공유가 쓰인다는 것 자체가 전용 밖으로 나갔다는 뜻이라
    /// 양과 무관하게 눈에 띄는 색을 쓴다. 내장은 공유가 정상 경로이므로 평소 색 그대로다.
    /// </remarks>
    private void DrawStacked(DrawingContext dc, double w, double h, int from, int to, Func<float, double> y)
    {
        var shared = Secondary!;
        float Sum(int i) => Finite(Metric!.Values[i]) + Finite(shared.Values[i]);

        // 전용 — 뒤쪽·저채도.
        FillArea(dc, w, h, from, to, y);
        DrawSeries(dc, _line, Metric!, from, to, w, y, Pen(Accent, 1, 0x77));

        var hatchColor = Discrete ? Palette.Warn : Accent;
        _band.Clear();
        using (var ctx = _band.Open())
        {
            int start = from;
            while (start < to)
            {
                int end = start + 1;
                while (end < to && !BreaksBefore(end)) end++;

                if (end - start >= 2)
                {
                    ctx.BeginFigure(new Point(XOf(start), y(Sum(start))), true, true);
                    for (int i = start + 1; i < end; i++) ctx.LineTo(new Point(XOf(i), y(Sum(i))), true, true);
                    for (int i = end - 1; i >= start; i--)
                        ctx.LineTo(new Point(XOf(i), y(Finite(Metric!.Values[i]))), true, true);
                }
                start = end;
            }
        }
        dc.DrawGeometry(Hatch(hatchColor, 0x99), null, _band);

        // 합계선 — 띠의 윗변이 곧 "합쳐서 얼마나 잡고 있나"다.
        _secondLine.Clear();
        using (var ctx = _secondLine.Open())
        {
            bool open = false;
            for (int i = from; i < to; i++)
            {
                if (BreaksBefore(i)) open = false;
                var point = new Point(XOf(i), y(Sum(i)));
                if (!open) { ctx.BeginFigure(point, false, false); open = true; }
                else ctx.LineTo(point, true, true);
            }
        }
        dc.DrawGeometry(null, Pen(hatchColor, 1, 0x88), _secondLine);

        if (CapacityLine <= 0 || !Discrete) return;

        // 전용 용량선 — 넘으면 시스템 RAM 스필오버다. 여기서는 "지금 넘쳤나"가 아니라
        // <b>보고 있는 구간에 넘긴 적이 있나</b>로 판정한다. 흐름이 멈춘 창이라 그것이 읽을 값이다.
        bool spilled = false;
        for (int i = from; i < to && !spilled; i++) spilled = Sum(i) > CapacityLine;

        double capY = Math.Max(y((float)CapacityLine), 1);
        dc.DrawLine(Dashed(spilled ? Palette.Warn : Accent, 1.3, spilled ? (byte)0xFF : (byte)0x99),
                    new Point(0, Math.Round(capY) + 0.5), new Point(w, Math.Round(capY) + 0.5));
    }

    private static float Finite(float v) => float.IsNaN(v) ? 0 : v;

    /// <summary>해치 브러시. 메인 창(<see cref="ChartSurface"/>)과 같은 각·간격이다.</summary>
    private static Brush Hatch(Color color, byte alpha)
    {
        var lines = new GeometryGroup();
        lines.Children.Add(new LineGeometry(new Point(-2, 6), new Point(6, -2)));
        lines.Children.Add(new LineGeometry(new Point(1, 9), new Point(9, 1)));

        var stroke = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        stroke.Freeze();
        var pen = new Pen(stroke, 1.6);
        pen.Freeze();

        var brush = new DrawingBrush(new GeometryDrawing(null, pen, lines))
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 8, 8),
            ViewportUnits = BrushMappingMode.Absolute,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 8, 8),
        };
        brush.Freeze();
        return brush;
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

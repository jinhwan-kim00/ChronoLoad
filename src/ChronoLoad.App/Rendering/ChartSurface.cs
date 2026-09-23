using System.Windows;
using System.Windows.Media;
using ChronoLoad.App.Services;
using ChronoLoad.Core.Layout;
using ChronoLoad.Core.Metrics;

namespace ChronoLoad.App.Rendering;

public enum ChartMode
{
    /// <summary>0 기준 영역 + 상단 라인. CPU·메모리.</summary>
    Area,

    /// <summary>중앙선 기준 미러. 위 = 수신/읽기, 아래 = 송신/쓰기. 네트워크·디스크.</summary>
    Mirror,

    /// <summary>접힌 카드의 18px 스트립. 축 눈금도 격자도 없다.</summary>
    Sparkline,

    /// <summary>
    /// GPU 조합 차트. 좌축 0~100% 사용률(실선 + Compute 점선), 우축 바이트 누적 메모리
    /// (전용 솔리드 + 공유 해치), 그리고 전용 VRAM 용량선. 설계서 §8.3.
    /// </summary>
    GpuCombo,
}

public enum ScaleMode
{
    /// <summary><see cref="ChartSurface.FixedMax"/>를 그대로 쓴다. 0~100%, 총 메모리 등.</summary>
    Fixed,

    /// <summary>표시 창의 최댓값에 맞춘다. 스파크라인 전용.</summary>
    PeakRelative,

    /// <summary>1/10/100Mbps 같은 단계에 스냅한다. 축이 매 프레임 출렁이지 않게.</summary>
    StepSnap,
}

/// <summary>
/// 자체 차트 렌더러. 차트 라이브러리를 쓰지 않는 이유는 설계서 §8.1에 있다 —
/// 상시 구동 위젯에서 라이브러리의 자체 렌더 루프가 "저부하" 목표와 정면으로 충돌한다.
/// WPF는 리테인드 모드라 <see cref="UIElement.InvalidateVisual"/>을 부르지 않으면 렌더 비용이 0이다.
/// </summary>
/// <remarks>
/// 정상 루프에서 할당이 생기지 않도록 값 버퍼·<see cref="StreamGeometry"/>·<see cref="Pen"/>을 모두 재사용한다.
/// </remarks>
public sealed class ChartSurface : FrameworkElement
{
    private static readonly Dictionary<(uint Argb, int Thickness10, bool Dashed), Pen> PenCache = [];
    private static readonly Dictionary<(uint Argb, int Alpha), LinearGradientBrush> FillCache = [];

    // WPF 는 리테인드 모드다. DrawGeometry 는 도형을 복사하지 않고 <b>참조만</b> 잡으므로,
    // 한 프레임 안에서 같은 StreamGeometry 를 Clear 하고 다시 쓰면 앞서 그린 것까지 바뀐다.
    // 그래서 프레임 안에서는 도형마다 새것을 꺼내 쓰고, 프레임 사이에만 재사용한다.
    private readonly List<StreamGeometry> _pool = [];
    private int _poolIndex;

    private float[] _primary = [];
    private float[] _secondary = [];
    private float[] _dedicated = [];
    private float[] _shared = [];
    private int _primaryCount;
    private int _secondaryCount;
    private int _memoryCount;

    public MetricSeries? Series { get; set; }

    /// <summary>미러 차트의 아래쪽, 또는 영역 차트에 얹는 보조 라인(커밋 차지 등).</summary>
    public MetricSeries? SecondarySeries { get; set; }

    public ChartMode Mode { get; set; } = ChartMode.Area;
    public ScaleMode Scale { get; set; } = ScaleMode.Fixed;

    /// <summary><see cref="ScaleMode.Fixed"/>일 때의 축 상한.</summary>
    public double FixedMax { get; set; } = 100;

    /// <summary><see cref="ScaleMode.StepSnap"/>일 때 쓸 단계들. 오름차순.</summary>
    public double[]? Steps { get; set; }

    public Color Accent { get; set; } = Colors.DodgerBlue;
    public ThemePalette Palette { get; set; } = ThemePalette.Dark;

    /// <summary>표시 창의 포인트 수. 250ms × 240 = 60초.</summary>
    public int WindowPoints { get; set; } = 240;

    /// <summary>리셋 이후 누적 평균. NaN이면 그리지 않는다.</summary>
    public double AverageValue { get; set; } = double.NaN;

    /// <summary>리셋 마커 위치(표시 창 안의 샘플 인덱스).</summary>
    public IReadOnlyList<int> ResetMarkers { get; set; } = [];

    /// <summary>스크럽 위치. null이면 그리지 않는다. 전 카드가 같은 값을 받는다.</summary>
    public int? ScrubIndex { get; set; }

    /// <summary>직전 렌더에서 스크럽선이 놓인 X 좌표.</summary>
    public double ScrubX { get; private set; }

    /// <summary>
    /// 표시 창 인덱스 → X 좌표. 오버레이 패널이 렌더를 기다리지 않고 위치를 잡을 수 있게 한다.
    /// </summary>
    public double XForIndex(int index)
    {
        int count = _primaryCount > 1 ? _primaryCount : WindowPoints;
        return count <= 1 ? 0 : ActualWidth * Math.Clamp(index, 0, count - 1) / (count - 1);
    }

    public bool ShowGrid { get; set; } = true;

    /// <summary>조합 차트의 전용 메모리(우축 누적 하단).</summary>
    public MetricSeries? MemoryDedicated { get; set; }

    /// <summary>조합 차트의 공유 메모리(우축 누적 상단, 해치).</summary>
    public MetricSeries? MemoryShared { get; set; }

    /// <summary>전용 VRAM 용량. 0이면 용량선을 그리지 않는다.</summary>
    public double DedicatedCapacity { get; set; }

    /// <summary>공유 메모리 한도. 내장 GPU 의 고정 축 상한이다. 0이면 축을 고정하지 않는다.</summary>
    public double SharedCapacity { get; set; }

    /// <summary>외장 GPU만 용량선과 스필오버 경고를 갖는다. 내장은 공유 메모리를 쓰는 게 정상이다.</summary>
    public bool IsDiscrete { get; set; }

    /// <summary>공유 메모리가 용량선을 넘어 시스템 RAM으로 흘러넘치는 중인지.</summary>
    public bool IsSpilling { get; private set; }

    /// <summary>이번 프레임에 실제로 쓰인 축 상한. 카드 푸터의 스케일 힌트가 읽어 간다.</summary>
    public double LastAxisMax { get; private set; }

    public void Invalidate() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4 || Series is null) return;

        // FrameworkElement 는 그린 픽셀 위에서만 히트 테스트된다. 투명 사각형을 먼저 깔아야
        // 차트 빈 구간에서도 커서 이벤트가 잡힌다(스크럽이 데이터 있는 곳에서만 되면 곤란하다).
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        _poolIndex = 0;
        int points = Math.Max(2, Math.Min(WindowPoints, Series.Capacity));
        EnsureBuffer(ref _primary, points);
        _primaryCount = Series.CopyLatest(_primary.AsSpan(0, points));
        if (_primaryCount == 0) return;

        _secondaryCount = 0;
        if (SecondarySeries is not null)
        {
            EnsureBuffer(ref _secondary, points);
            _secondaryCount = SecondarySeries.CopyLatest(_secondary.AsSpan(0, points));
        }

        _memoryCount = 0;
        if (Mode == ChartMode.GpuCombo && MemoryDedicated is not null)
        {
            EnsureBuffer(ref _dedicated, points);
            EnsureBuffer(ref _shared, points);
            _memoryCount = MemoryDedicated.CopyLatest(_dedicated.AsSpan(0, points));
            if (MemoryShared is not null) MemoryShared.CopyLatest(_shared.AsSpan(0, points));
            else Array.Clear(_shared, 0, points);
        }

        double max = ResolveMax();
        LastAxisMax = max;

        switch (Mode)
        {
            case ChartMode.Sparkline: RenderSparkline(dc, w, h, max); break;
            case ChartMode.Mirror: RenderMirror(dc, w, h, max); break;
            case ChartMode.GpuCombo: RenderGpuCombo(dc, w, h); break;
            default: RenderArea(dc, w, h, max); break;
        }

        RenderResetMarkers(dc, w, h, points);
        RenderScrub(dc, w, h, max);
    }

    /// <summary>
    /// 스크럽선과 각 계열의 값 지점. 점은 <b>계열 색</b>으로 찍어 어느 값을 읽고 있는지 분명히 한다.
    /// </summary>
    private void RenderScrub(DrawingContext dc, double w, double h, double max)
    {
        if (ScrubIndex is not { } index || _primaryCount < 2) return;

        int clamped = Math.Clamp(index, 0, _primaryCount - 1);
        double x = Math.Round(X(clamped, w)) + 0.5;
        ScrubX = x;

        dc.DrawLine(Pen(Palette.Fg, 1, 0x8C, dashed: true), new Point(x, 0), new Point(x, h));

        switch (Mode)
        {
            case ChartMode.Area:
            {
                double top = 2, bottom = h;
                Dot(dc, x, bottom - (Value(_primary, clamped) / max) * (bottom - top) * 0.98, Accent);
                break;
            }

            case ChartMode.Mirror:
            {
                double mid = Math.Round(h / 2) + 0.5, half = mid - 3;
                Dot(dc, mid - (Value(_primary, clamped) / max) * half, x, Accent, swap: true);
                if (_secondaryCount > 0)
                    Dot(dc, mid + (Value(_secondary, clamped) / max) * half, x, Accent, swap: true);
                break;
            }

            case ChartMode.GpuCombo:
            {
                Dot(dc, x, h - (Value(_primary, clamped) / 100) * h * 0.92 - 2, Accent);
                if (_memoryCount > clamped)
                {
                    float total = Value(_dedicated, clamped) + Value(_shared, clamped);
                    Dot(dc, x, h - (total / LastAxisMax) * h * 0.96 - 2,
                        IsSpilling ? Palette.Warn : Accent);
                }
                break;
            }
        }
    }

    private static void Dot(DrawingContext dc, double x, double y, Color color, bool swap = false)
    {
        var brush = FlatBrush(color, 0xFF);
        dc.DrawEllipse(brush, null, swap ? new Point(y, x) : new Point(x, y), 3, 3);
    }

    private double ResolveMax()
    {
        switch (Scale)
        {
            case ScaleMode.Fixed:
                return FixedMax > 0 ? FixedMax : 1;

            case ScaleMode.PeakRelative:
            {
                double peak = Peak(_primary, _primaryCount);
                return peak > 0 ? peak * 1.15 : 1;
            }

            default:
            {
                double peak = Math.Max(Peak(_primary, _primaryCount), Peak(_secondary, _secondaryCount));
                if (Steps is { Length: > 0 })
                    foreach (double step in Steps)
                        if (step >= peak * 1.05) return step;

                return peak > 0 ? peak * 1.1 : 1;
            }
        }
    }

    private static double Peak(float[] buffer, int count)
    {
        double peak = 0;
        for (int i = 0; i < count; i++)
            if (!float.IsNaN(buffer[i]) && buffer[i] > peak) peak = buffer[i];
        return peak;
    }

    // ── 영역 차트 ────────────────────────────────────────────────
    private void RenderArea(DrawingContext dc, double w, double h, double max)
    {
        double top = 2, bottom = h;
        double Y(float v) => bottom - (v / max) * (bottom - top) * 0.98;

        if (ShowGrid) DrawGrid(dc, w, h);

        // 채움 — 아래쪽으로 사라지는 그라데이션. 부피감만 주고 격자를 가리지 않는다.
        var fill = Next();
        using (var ctx = fill.Open())
        {
            bool open = false;
            for (int i = 0; i < _primaryCount; i++)
            {
                float v = _primary[i];
                double x = X(i, w);
                if (float.IsNaN(v))
                {
                    if (open) { ctx.LineTo(new Point(x, bottom), false, false); open = false; }
                    continue;
                }

                if (!open)
                {
                    ctx.BeginFigure(new Point(x, bottom), isFilled: true, isClosed: true);
                    ctx.LineTo(new Point(x, Y(v)), false, true);
                    open = true;
                }
                else
                {
                    ctx.LineTo(new Point(x, Y(v)), true, true);
                }
            }

            if (open) ctx.LineTo(new Point(X(_primaryCount - 1, w), bottom), false, false);
        }
        dc.DrawGeometry(FillBrush(Accent, 0x47, h), null, fill);

        if (!double.IsNaN(AverageValue) && AverageValue > 0)
            DrawHorizontal(dc, w, Y((float)AverageValue), Pen(Accent, 1, 0x88, dashed: true));

        // 보조 라인(커밋 차지 등)은 주 계열보다 얇고 흐리게
        if (_secondaryCount > 0)
            DrawPolyline(dc, Next(), _secondary, _secondaryCount, w, Y, Pen(Accent, 1, 0x66));

        DrawPolyline(dc, Next(), _primary, _primaryCount, w, Y, Pen(Accent, 1.5, 0xFF));
    }

    // ── 미러 차트 ────────────────────────────────────────────────
    private void RenderMirror(DrawingContext dc, double w, double h, double max)
    {
        double mid = Math.Round(h / 2) + 0.5;
        double half = mid - 3;
        double Up(float v) => mid - (v / max) * half;
        double Down(float v) => mid + (v / max) * half;

        if (ShowGrid) DrawGrid(dc, w, h, skipMiddle: true);
        DrawHorizontal(dc, w, mid, Pen(Palette.Line, 1, 0xFF));

        FillUnder(dc, Next(), _primary, _primaryCount, w, Up, mid, 0x47);
        DrawPolyline(dc, Next(), _primary, _primaryCount, w, Up, Pen(Accent, 1.4, 0xFF));

        if (_secondaryCount > 0)
        {
            FillUnder(dc, Next(), _secondary, _secondaryCount, w, Down, mid, 0x33);
            DrawPolyline(dc, Next(), _secondary, _secondaryCount, w, Down, Pen(Accent, 1.4, 0xCC));
        }
    }

    // ── GPU 조합 차트 ────────────────────────────────────────────
    private void RenderGpuCombo(DrawingContext dc, double w, double h)
    {
        double peak = 0;
        for (int i = 0; i < _memoryCount; i++)
        {
            double total = Value(_dedicated, i) + Value(_shared, i);
            if (total > peak) peak = total;
        }

        // 축 정책은 그리기가 아니라 판단이라 Core 에 있다(§8.3). 화면 없이 테스트하기 위해서이고,
        // 외장 GPU 가 없는 기기에서도 외장 규칙을 검증할 수 있어야 하기 때문이다.
        double axisMax = GpuMemoryAxis.Max(IsDiscrete, DedicatedCapacity, SharedCapacity, peak);

        LastAxisMax = axisMax;
        IsSpilling = IsDiscrete && DedicatedCapacity > 0 && _memoryCount > 0
                     && Value(_dedicated, _memoryCount - 1) + Value(_shared, _memoryCount - 1)
                        > DedicatedCapacity;

        double yMem(float v) => h - (v / axisMax) * h * 0.96 - 2;
        double yUtil(float v) => h - (v / 100) * h * 0.92 - 2;

        if (ShowGrid) DrawGrid(dc, w, h);

        if (_memoryCount > 1)
        {
            // 전용 — 뒤쪽·저채도. 사용률 라인보다 시각 위계가 낮아야 한다.
            FillUnder(dc, Next(), _dedicated, _memoryCount, w, v => yMem(v), h, 0x33);
            DrawPolyline(dc, Next(), _dedicated, _memoryCount, w, v => yMem(v), Pen(Accent, 1, 0x77));

            // 공유 — 전용 위에 적층. 색이 아니라 <b>해치 패턴</b>으로도 구분해 색약 사용자를 배려한다.
            var band = Next();
            using (var ctx = band.Open())
            {
                ctx.BeginFigure(new Point(X(0, w), yMem(Value(_dedicated, 0) + Value(_shared, 0))), true, true);
                for (int i = 1; i < _memoryCount; i++)
                    ctx.LineTo(new Point(X(i, w), yMem(Value(_dedicated, i) + Value(_shared, i))), true, true);
                for (int i = _memoryCount - 1; i >= 0; i--)
                    ctx.LineTo(new Point(X(i, w), yMem(Value(_dedicated, i))), true, true);
            }

            // 외장에서 공유 메모리가 쓰인다는 것 자체가 전용 VRAM 밖으로 나갔다는 뜻이다.
            // 전용을 다 채우기 전부터 넘어가는 경우가 많아 "용량선을 넘었을 때"만 경고색을
            // 쓰면 정작 성능이 떨어지는 구간을 놓친다. 그래서 양과 무관하게 눈에 띄는 색으로
            // 두고, 얼마나 넘어갔는지는 띠의 높이가 말하게 한다.
            // 내장은 공유가 정상 경로이므로 평소 색 그대로다 — 상시 경고는 경고를 무의미하게 만든다.
            var hatchColor = IsDiscrete ? Palette.Warn : Accent;
            dc.DrawGeometry(Hatch(hatchColor, 0x99), null, band);
            DrawPolyline(dc, Next(), Sum(_dedicated, _shared, _memoryCount), _memoryCount, w,
                v => yMem(v), Pen(hatchColor, 1, 0x88));
        }

        // 전용 VRAM 용량선 — 넘으면 시스템 RAM 스필오버. 성능 급락의 가장 흔한 원인이다.
        // 넘치지 않는 동안에는 축 상한이 곧 이 용량이라 선이 위쪽 끝에 붙는다. 카드가 눌려
        // 차트가 낮아지면 1.3px 파선의 절반이 잘려 나가므로, 최소 1px 는 안으로 들여 긋는다.
        if (IsDiscrete && DedicatedCapacity > 0)
            DrawHorizontal(dc, w, Math.Max(yMem((float)DedicatedCapacity), 1),
                Pen(IsSpilling ? Palette.Warn : Accent, 1.3, IsSpilling ? (byte)0xFF : (byte)0x99, dashed: true));

        // Compute 엔진 — AI 워크로드인지 렌더링인지 가른다.
        if (_secondaryCount > 1)
            DrawPolyline(dc, Next(), _secondary, _secondaryCount, w, v => yUtil(v), Pen(Accent, 1.2, 0x70), dashed: true);

        // 사용률 — 누적 영역 위를 지나도 묻히지 않도록 같은 경로를 배경색으로 먼저 긋는다.
        DrawPolyline(dc, Next(), _primary, _primaryCount, w, v => yUtil(v), Pen(Palette.Surface, 4, 0xBF));
        DrawPolyline(dc, Next(), _primary, _primaryCount, w, v => yUtil(v), Pen(Accent, 2, 0xFF));
    }

    private static float Value(float[] buffer, int index) =>
        index < buffer.Length && !float.IsNaN(buffer[index]) ? buffer[index] : 0;

    private float[] Sum(float[] a, float[] b, int count)
    {
        EnsureBuffer(ref _sumBuffer, count);
        for (int i = 0; i < count; i++) _sumBuffer[i] = Value(a, i) + Value(b, i);
        return _sumBuffer;
    }

    private float[] _sumBuffer = [];

    // ── 스파크라인 ───────────────────────────────────────────────
    private void RenderSparkline(DrawingContext dc, double w, double h, double max)
    {
        // 18px 스트립은 "최근 창의 형태"만 전달하면 된다. 절대 축을 쓰면 값이 낮을 때
        // 바닥에 붙어 아무 정보도 주지 못하므로 상대 스케일이 기본이다.
        double Y(float v) => h - 2 - (v / max) * (h - 4);
        DrawPolyline(dc, Next(), _primary, _primaryCount, w, Y, Pen(Accent, 1.2, 0xD0));
    }

    // ── 공통 ────────────────────────────────────────────────────
    private double X(int index, double w) =>
        _primaryCount <= 1 ? w : w * index / (_primaryCount - 1);

    private void DrawGrid(DrawingContext dc, double w, double h, bool skipMiddle = false)
    {
        var pen = Pen(Palette.Line, 1, 0x99);
        var guidelines = new GuidelineSet();

        for (int i = 1; i < 3; i++)
        {
            if (skipMiddle && i == 1) continue;
            guidelines.GuidelinesY.Add(Math.Round(h * i / 3) + 0.5);
        }

        dc.PushGuidelineSet(guidelines);
        for (int i = 1; i < 3; i++)
        {
            if (skipMiddle && i == 1) continue;
            double y = Math.Round(h * i / 3) + 0.5;
            dc.DrawLine(pen, new Point(0, y), new Point(w, y));
        }
        dc.Pop();
    }

    private void RenderResetMarkers(DrawingContext dc, double w, double h, int points)
    {
        if (ResetMarkers.Count == 0) return;
        var pen = Pen(Palette.Fg, 1, 0x73, dashed: true);

        foreach (int index in ResetMarkers)
        {
            if (index < 0 || index >= points) continue;
            double x = Math.Round(w * index / Math.Max(1, points - 1)) + 0.5;
            dc.DrawLine(pen, new Point(x, 0), new Point(x, h));
        }
    }

    private static void DrawHorizontal(DrawingContext dc, double w, double y, Pen pen)
    {
        double snapped = Math.Round(y) + 0.5;
        dc.DrawLine(pen, new Point(0, snapped), new Point(w, snapped));
    }

    private StreamGeometry Next()
    {
        if (_poolIndex >= _pool.Count) _pool.Add(new StreamGeometry());
        var geometry = _pool[_poolIndex++];
        geometry.Clear();
        return geometry;
    }

    private void DrawPolyline(DrawingContext dc, StreamGeometry geometry, float[] buffer, int count,
        double w, Func<float, double> y, Pen pen, bool dashed = false)
    {
        if (count < 2) return;

        geometry.Clear();
        using (var ctx = geometry.Open())
        {
            bool open = false;
            for (int i = 0; i < count; i++)
            {
                float v = buffer[i];
                if (float.IsNaN(v)) { open = false; continue; }   // 공백 구간은 선을 끊는다

                var point = new Point(X(i, w), y(v));
                if (!open) { ctx.BeginFigure(point, false, false); open = true; }
                else ctx.LineTo(point, true, true);
            }
        }

        dc.DrawGeometry(null, pen, geometry);
    }

    private void FillUnder(DrawingContext dc, StreamGeometry geometry, float[] buffer, int count,
        double w, Func<float, double> y, double baseline, byte alpha)
    {
        if (count < 2) return;

        geometry.Clear();
        using (var ctx = geometry.Open())
        {
            bool open = false;
            for (int i = 0; i < count; i++)
            {
                float v = buffer[i];
                double x = X(i, w);
                if (float.IsNaN(v))
                {
                    if (open) { ctx.LineTo(new Point(x, baseline), false, false); open = false; }
                    continue;
                }

                if (!open)
                {
                    ctx.BeginFigure(new Point(x, baseline), true, true);
                    ctx.LineTo(new Point(x, y(v)), false, true);
                    open = true;
                }
                else ctx.LineTo(new Point(x, y(v)), true, true);
            }

            if (open) ctx.LineTo(new Point(X(count - 1, w), baseline), false, false);
        }

        dc.DrawGeometry(FlatBrush(Accent, alpha), null, geometry);
    }

    // ── 캐시 ────────────────────────────────────────────────────
    private static void EnsureBuffer(ref float[] buffer, int size)
    {
        if (buffer.Length < size) buffer = new float[size];
    }

    private static Pen Pen(Color color, double thickness, byte alpha, bool dashed = false)
    {
        var key = (Argb: (uint)(alpha << 24 | color.R << 16 | color.G << 8 | color.B),
                   Thickness10: (int)(thickness * 10), Dashed: dashed);

        if (PenCache.TryGetValue(key, out var cached)) return cached;

        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        var pen = new System.Windows.Media.Pen(brush, thickness);
        if (dashed) pen.DashStyle = new DashStyle([3, 3], 0);
        pen.Freeze();

        PenCache[key] = pen;
        return pen;
    }

    private static readonly Dictionary<(uint Argb, byte Alpha), Brush> HatchCache = [];

    /// <summary>
    /// 대각 해치. 전용/공유 메모리를 <b>색만이 아니라 채움 패턴으로도</b> 구분해
    /// 색을 구별하기 어려운 사용자도 두 계열을 가를 수 있게 한다.
    /// </summary>
    private static Brush Hatch(Color color, byte alpha)
    {
        var key = ((uint)(color.R << 16 | color.G << 8 | color.B), alpha);
        if (HatchCache.TryGetValue(key, out var cached)) return cached;

        var lines = new GeometryGroup();
        lines.Children.Add(new LineGeometry(new Point(-2, 6), new Point(6, -2)));
        lines.Children.Add(new LineGeometry(new Point(1, 9), new Point(9, 1)));

        var stroke = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        stroke.Freeze();
        var pen = new System.Windows.Media.Pen(stroke, 1.6);
        pen.Freeze();

        var brush = new DrawingBrush(new GeometryDrawing(null, pen, lines))
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 6, 6),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        brush.Freeze();

        HatchCache[key] = brush;
        return brush;
    }

    private static Brush FlatBrush(Color color, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 그라데이션을 도형 경계가 아니라 <b>컨트롤 높이</b>에 고정한다.
    /// 경계 상대로 두면 값이 낮을 때 그라데이션이 납작하게 눌려 색이 진해 보인다.
    /// </summary>
    private static Brush FillBrush(Color color, byte alpha, double height)
    {
        int bucket = (int)Math.Round(height);
        var key = ((uint)(color.R << 16 | color.G << 8 | color.B), alpha | (bucket << 8));
        if (FillCache.TryGetValue(key, out var cached)) return cached;

        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, Math.Max(1, height)),
            GradientStops =
            [
                new GradientStop(Color.FromArgb(alpha, color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
            ],
        };
        brush.Freeze();

        FillCache[key] = brush;
        return brush;
    }
}

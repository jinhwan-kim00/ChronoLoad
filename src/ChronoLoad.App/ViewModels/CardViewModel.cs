using System.Windows.Media;
using ChronoLoad.App.Rendering;
using ChronoLoad.App.Services;
using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Formatting;
using ChronoLoad.Core.Layout;
using ChronoLoad.Core.Metrics;

namespace ChronoLoad.App.ViewModels;

/// <summary>GPU 카드 헤더의 메모리 요약.</summary>
/// <param name="Text">예 <c>12/22G</c>. 사용량과 용량을 한 덩어리로 읽는다.</param>
/// <param name="Ratio">용량 대비 사용 비율. 넘치면 1 을 넘는다 — 자르는 것은 그리는 쪽이 한다.</param>
/// <param name="Over">용량을 넘겼는가. 미터의 색이 이 값을 따른다.</param>
public readonly record struct GpuMemorySummary(string Text, double Ratio, bool Over);

/// <summary>카드 하나가 무엇을 어떻게 그릴지. 값은 갖지 않고 <see cref="MetricRegistry"/>를 그때그때 읽는다.</summary>
public sealed class CardViewModel
{
    public required DeviceHandle Device { get; init; }
    public required MetricSeries Primary { get; init; }
    public required int PrimarySlot { get; init; }

    public MetricSeries? Secondary { get; init; }
    public int SecondarySlot { get; init; } = -1;

    /// <summary>GPU 조합 차트의 전용·공유 메모리.</summary>
    public MetricSeries? MemoryDedicated { get; init; }
    public MetricSeries? MemoryShared { get; init; }

    /// <summary>
    /// 논리 코어별 사용률. 오버레이의 코어 미니 바가 읽는다 (§5.1).
    /// 상시 화면에는 나오지 않는다 — 코어 수만큼 늘어나는 것을 카드에 두면 텍스트 예산이 무너진다.
    /// </summary>
    public IReadOnlyList<MetricSeries> Cores { get; init; } = [];

    /// <summary>
    /// 코어별 효율 등급. <see cref="Cores"/> 와 같은 순서이며, 구분할 것이 없으면 빈 배열이다.
    /// 값이 클수록 성능 지향 — 제조사가 정하는 값이라 "1이면 P코어"로 고정해 읽지 않는다.
    /// </summary>
    public IReadOnlyList<byte> CoreClasses { get; init; } = [];

    /// <summary>전용 VRAM 용량. 0이면 용량선을 그리지 않는다.</summary>
    public double DedicatedCapacity { get; init; }

    /// <summary>공유 메모리 한도. 내장 GPU 의 고정 축 상한이다. 0이면 축을 고정하지 않는다.</summary>
    public double SharedCapacity { get; init; }

    public bool IsDiscrete { get; init; }

    /// <summary>NPU는 메모리 누적 없이 사용률만 본다 — 연산 전용 가속기에 VRAM 용량선은 의미가 없다.</summary>
    public bool IsNpu { get; init; }

    /// <summary>헤더 아이콘. 색이 지표 종류를, 아이콘이 장치의 정체를 말한다.</summary>
    public required Geometry Icon { get; init; }

    public ChartMode Mode { get; init; } = ChartMode.Area;
    public ScaleMode Scale { get; init; } = ScaleMode.Fixed;
    public double FixedMax { get; init; } = 100;
    public double[]? Steps { get; init; }

    public MetricUnit DisplayUnit { get; init; } = MetricUnit.Percent;

    /// <summary>원시값 → 표시값 배율. 메모리는 바이트를 총량 대비 퍼센트로 바꾼다.</summary>
    public double DisplayFactor { get; init; } = 1;

    public double Weight { get; init; } = 1;
    public int Priority { get; init; }

    /// <summary>GPU·디스크·네트워크처럼 같은 종류가 여럿일 때의 인덱스 배지.</summary>
    public string? Badge { get; init; }

    /// <summary>내장 GPU는 외곽선 배지. 외장은 채운 배지.</summary>
    public bool BadgeOutlined { get; init; }

    /// <summary>미러 차트에서 보조 계열은 "반대 방향"이라 푸터에 ▼ 대신 ↑ 를 쓴다.</summary>
    public bool SecondaryIsOpposite { get; init; }

    public bool UserCollapsed { get; set; }
    public bool AutoCollapsed { get; set; }
    public bool Collapsed => UserCollapsed || AutoCollapsed;

    public string Key => Device.Key;
    public string ShortName => Device.Info.ShortName;

    /// <summary>
    /// 장치가 저전력 대기 중인가. 온도·전력·클럭이 비는 이유가 <b>고장이 아니라 의도</b>임을
    /// 화면에 밝히기 위해 쓴다 (§6.3).
    /// </summary>
    public bool IsStandby => Device.Availability == DeviceAvailability.Standby;
    public string FullName => Device.Info.FullName;

    public Color Accent(ThemePalette palette) => palette.AccentFor(Device.Info.Class, Device.Key);
    public Color IconColor(ThemePalette palette) => palette.IconColorFor(Device.Info.Icon, Accent(palette));

    public FormattedValue HeaderValue(int? scrubIndex = null)
    {
        float value = Read(Primary, scrubIndex);
        return float.IsNaN(value)
            ? new FormattedValue("—", string.Empty)
            : MetricFormatter.Format(DisplayUnit, value * DisplayFactor);
    }

    /// <summary>"얼마나 찼는가"를 셀 때의 분모. 외장은 전용 VRAM, 내장은 공유 한도. 모르면 0.</summary>
    public double MemoryCapacity =>
        GpuMemoryAxis.CapacityReference(IsDiscrete, DedicatedCapacity, SharedCapacity);

    /// <summary>
    /// 헤더에 사용률과 나란히 붙는 메모리 요약과, 접힌 카드의 미터가 쓸 비율.
    /// </summary>
    /// <remarks>
    /// <b>큰 숫자는 사용률로 되돌렸다.</b> 메모리 용량으로 바꿔보니 카드를 접었을 때
    /// "지금 바쁜가"가 사라졌다 — 접힌 카드가 답해야 할 질문이 바로 그것이다.
    /// 둘 다 필요하므로 사용률은 큰 숫자로, 메모리는 작은 글씨와 미터로 함께 보여준다.
    /// </remarks>
    public GpuMemorySummary? MemoryHeadline(int? scrubIndex = null)
    {
        if (MemoryDedicated is null) return null;

        double capacity = MemoryCapacity;
        if (capacity <= 0) return null;

        float dedicated = Read(MemoryDedicated, scrubIndex);
        float shared = Read(MemoryShared, scrubIndex);

        // 한쪽만 없는 것은 0 으로 세지만, 둘 다 없으면 값이 없는 것이다.
        if (float.IsNaN(dedicated) && float.IsNaN(shared)) return null;

        double used = (float.IsNaN(dedicated) ? 0 : dedicated) + (float.IsNaN(shared) ? 0 : shared);

        // 헤더에는 쓴 양만 적는다. 전체 크기는 카드마다 바뀌지 않는 값이라 매 갱신마다
        // 눈에 들어올 이유가 없고, 하단 스케일 힌트에 이미 자리가 있다.
        // 비율은 미터가 말하므로 숫자로 또 말할 필요도 없다.
        var usedText = MetricFormatter.Format(MetricUnit.Bytes, used);

        return new GpuMemorySummary($"{usedText.Value}{usedText.Unit}", used / capacity, used > capacity);
    }

    private static float Read(MetricSeries? series, int? scrubIndex) =>
        series is null ? float.NaN
        : scrubIndex is { } index ? SampleAt(series, index)
        : series.Latest;

    /// <summary>
    /// 스크럽 시점의 코어별 사용률. 값이 없는 코어는 NaN 이다. 코어가 없으면 빈 배열.
    /// </summary>
    /// <remarks>
    /// <b>오버레이의 다른 행과 같은 시점을 읽어야 한다.</b> 코어만 "지금" 값을 쓰면 과거를
    /// 고정해 놓고 보는 패널 안에서 한 줄만 현재가 되어, 같이 놓인 총 사용률과 아귀가 맞지 않는다.
    /// 코어별 시계열을 들고 있는 이유가 이것이다 — 최신 값만 두면 이 줄을 못 맞춘다.
    /// </remarks>
    public float[] CoreUsage(int index)
    {
        if (Cores.Count == 0) return [];

        var values = new float[Cores.Count];
        for (int i = 0; i < values.Length; i++) values[i] = SampleAt(Cores[i], index);
        return values;
    }

    /// <summary>표시 창 안의 인덱스로 값을 읽는다. 창은 항상 최근 <paramref name="window"/>개다.</summary>
    public static float SampleAt(MetricSeries series, int index, int window = 240)
    {
        int absolute = series.AbsoluteIndexOf(index, window);
        return absolute < 0 ? float.NaN : series[absolute];
    }

    /// <summary>
    /// 오버레이 한 줄. 포커스 카드는 전체 패널(첫 줄이 전체 장치명), 나머지는 요약 칩이다.
    /// </summary>
    public IReadOnlyList<(string Label, string Value)> OverlayRows(int index, bool full)
    {
        var rows = new List<(string, string)>();
        string Fmt(MetricSeries? s, MetricUnit unit, double factor = 1)
        {
            if (s is null) return "—";
            float v = SampleAt(s, index);
            return float.IsNaN(v) ? "—" : MetricFormatter.Format(unit, v * factor).ToString();
        }

        rows.Add((string.Empty, Fmt(Primary, DisplayUnit, DisplayFactor)));

        if (!full)
        {
            // 요약 칩도 계열이 여럿이면 각각을 보여준다(UX §04: 이더넷 "↓ ↑ 속도",
            // 디스크 "읽기 / 쓰기", GPU "사용률, 전용+공유"). 한쪽만 내면 미러 차트가
            // "읽기만 있고 쓰기는 없는" 것처럼 읽히고, GPU 는 사용률만으로 메모리를 알 수 없다.
            if (Secondary is not null && SecondaryIsOpposite)
                rows.Add(("↑", Fmt(Secondary, DisplayUnit, DisplayFactor)));
            else if (MemoryHeadline(index) is { } memory)
                rows.Add((string.Empty, memory.Text));

            return rows;
        }

        // 왜 값이 비는지를 먼저 말한다. 아래의 "—" 들이 고장으로 읽히면 안 된다.
        if (IsStandby) rows.Add(("상태", "저전력 대기 — 깨우지 않음"));

        if (MemoryDedicated is not null)
        {
            rows.Add(("전용", Fmt(MemoryDedicated, MetricUnit.Bytes)));
            rows.Add(("공유", Fmt(MemoryShared, MetricUnit.Bytes)));
        }

        if (Secondary is not null && SecondaryIsOpposite)
            rows.Add(("↑", Fmt(Secondary, DisplayUnit, DisplayFactor)));
        else if (Secondary is not null && MemoryDedicated is not null)
            rows.Add(("Compute", Fmt(Secondary, MetricUnit.Percent)));

        // 온도·전력·클럭은 벤더 네이티브 경로가 붙은 어댑터에만 값이 있다(설계서 §5.4 계층 B).
        if (Temperature is not null) rows.Add(("온도", Fmt(Temperature, MetricUnit.Celsius)));
        if (Power is not null) rows.Add(("전력", Fmt(Power, MetricUnit.Watt)));
        if (Clock is not null) rows.Add(("클럭", Fmt(Clock, MetricUnit.Megahertz)));

        return rows;
    }

    /// <summary>벤더 경로가 채우는 계열. 없으면 null이고 오버레이에서 빠진다.</summary>
    public MetricSeries? Temperature { get; init; }
    public MetricSeries? Power { get; init; }
    public MetricSeries? Clock { get; init; }

    /// <summary>리셋 이후 구간 통계. 세 값은 같은 단위로 맞춘다.</summary>
    public string FooterText(MetricRegistry registry)
    {
        var stats = registry.StatsSnapshot(PrimarySlot);
        if (stats.IsEmpty) return string.Empty;

        double third = SecondaryIsOpposite && SecondarySlot >= 0
            ? registry.StatsSnapshot(SecondarySlot).Max
            : stats.Min;

        var (values, unit) = MetricFormatter.FormatGroup(DisplayUnit,
            stats.Mean * DisplayFactor, stats.Max * DisplayFactor, third * DisplayFactor);

        string thirdSymbol = SecondaryIsOpposite ? "↑" : "▼";
        string suffix = unit.Length > 0 ? $"  {unit}" : string.Empty;
        string line = $"~ {values[0]}   ▲ {values[1]}   {thirdSymbol} {values[2]}{suffix}";

        // GPU 카드만 현재 VRAM 을 하나 더 허용한다. 스필오버 중이면 +공유를 붙인다.
        if (MemoryDedicated is not null)
        {
            float dedicated = MemoryDedicated.Latest;
            float shared = MemoryShared?.Latest ?? 0;
            if (!float.IsNaN(dedicated))
            {
                string memory = MetricFormatter.Format(MetricUnit.Bytes, dedicated).Value;
                if (!float.IsNaN(shared) && shared > 0.05 * 1024 * 1024 * 1024)
                    memory += "+" + MetricFormatter.Format(MetricUnit.Bytes, shared).Value;
                line += $"   ▪ {memory}";
            }
        }

        return line;
    }

    /// <summary>축을 퍼센트가 아닌 다른 단위로 안내해야 하는 카드(메모리의 총 용량 등).</summary>
    public MetricUnit? ScaleHintUnit { get; init; }

    /// <summary>축 상한 힌트. 차트가 실제로 쓴 값을 받아 표시한다.</summary>
    /// <remarks>
    /// <b>GPU 메모리만 축 상한이 아니라 용량을 적는다.</b> 이 자리가 답해야 할 것은
    /// "전체가 얼마인가"이고, GPU 메모리에서 그것은 축 상한이 아니라 용량이다.
    /// 둘은 넘치는 동안에만 갈라지는데, 그때는 용량선이 차트 안에 파선으로 보이므로
    /// 이 숫자가 그 선을 가리키는 것으로 읽힌다 — 오히려 축 상한을 적는 쪽이 짝이 없다.
    /// </remarks>
    public string ScaleText(double axisMax)
    {
        if (MemoryDedicated is not null)
            return MetricFormatter.Format(MetricUnit.Bytes,
                MemoryCapacity > 0 ? MemoryCapacity : axisMax).ToString();
        if (ScaleHintUnit is { } hint) return MetricFormatter.Format(hint, axisMax).ToString();
        return DisplayUnit == MetricUnit.Percent ? "100%" : MetricFormatter.Format(DisplayUnit, axisMax).ToString();
    }

    public double AverageForChart(MetricRegistry registry)
    {
        var stats = registry.StatsSnapshot(PrimarySlot);
        return stats.IsEmpty ? double.NaN : stats.Mean;
    }
}

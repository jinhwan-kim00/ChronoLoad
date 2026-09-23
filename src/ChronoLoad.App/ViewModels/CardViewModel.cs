using System.Windows.Media;
using ChronoLoad.App.Rendering;
using ChronoLoad.App.Services;
using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Formatting;
using ChronoLoad.Core.Layout;
using ChronoLoad.Core.Metrics;

namespace ChronoLoad.App.ViewModels;

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

    /// <summary>전용 VRAM 용량. 0이면 용량선을 그리지 않는다.</summary>
    public double DedicatedCapacity { get; init; }

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
        float value = scrubIndex is { } index ? SampleAt(Primary, index) : Primary.Latest;
        return float.IsNaN(value)
            ? new FormattedValue("—", string.Empty)
            : MetricFormatter.Format(DisplayUnit, value * DisplayFactor);
    }

    /// <summary>표시 창 안의 인덱스로 값을 읽는다. 창은 항상 최근 <paramref name="window"/>개다.</summary>
    public static float SampleAt(MetricSeries series, int index, int window = 240)
    {
        int count = series.Count;
        if (count == 0) return float.NaN;

        // 표시 창이 아직 다 차지 않았으면 왼쪽이 비어 있다. 인덱스를 실제 샘플로 옮긴다.
        int offset = Math.Min(window, count) - window;
        int actual = index + offset;
        return actual < 0 || actual >= count ? float.NaN : series[actual];
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
        if (!full) return rows;

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
    public string ScaleText(double axisMax)
    {
        if (MemoryDedicated is not null) return MetricFormatter.Format(MetricUnit.Bytes, axisMax).ToString();
        if (ScaleHintUnit is { } hint) return MetricFormatter.Format(hint, axisMax).ToString();
        return DisplayUnit == MetricUnit.Percent ? "100%" : MetricFormatter.Format(DisplayUnit, axisMax).ToString();
    }

    public double AverageForChart(MetricRegistry registry)
    {
        var stats = registry.StatsSnapshot(PrimarySlot);
        return stats.IsEmpty ? double.NaN : stats.Mean;
    }
}

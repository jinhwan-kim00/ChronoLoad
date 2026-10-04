using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Metrics;
using ModelContextProtocol.Server;

namespace ChronoLoad.Mcp;

/// <summary>
/// 구간 마커와 구간 통계 (§10.2). 리셋을 순서대로 걸지 않고도 <b>사후에</b> 여러 구간을 잰다.
/// </summary>
/// <remarks>
/// <para>
/// 실사용 보고: 벤치 단계 시각을 이력의 오프셋에 손으로 맞춰야 했다. 리셋은 구간 하나만 들고 있고,
/// 다음 구간을 재려면 앞 구간을 잃는다. 마커는 시각에 이름만 붙이고, 통계는 링의 표본으로 그때그때 센다.
/// </para>
/// <para>
/// 링(15분)을 벗어난 구간은 셀 수 없다 — 근사로 메우지 않고 <c>truncated</c> 로 알린다.
/// 마커는 앱 메모리에만 있어 앱을 다시 켜면 사라진다. 링도 같이 사라지므로 잃는 것은 없다.
/// </para>
/// <para>
/// <c>mark</c> 는 <c>reset_stats</c> 와 함께 상태를 바꾸는 둘뿐인 툴이다. 바꾸는 것은 MCP 쪽 메모리의
/// 이름표뿐이고 시스템도, 화면 통계도 건드리지 않는다.
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class IntervalTools(McpContext ctx)
{
    private static readonly double[] Quantiles = [0.50, 0.95, 0.99];

    [McpServerTool(Name = "mark")]
    [Description("지금 시각에 이름을 붙인다(벤치 단계 시작·끝 등). 같은 이름을 다시 쓰면 옮긴다. 나중에 get_interval_stats·compare_intervals 에서 이름으로 구간을 지정한다. 링 버퍼(15분) 안의 구간만 잴 수 있다.")]
    public object Mark(
        [Description("마커 이름. 예: idle, load-8calls, cooldown.")] string label,
        [Description("메모. 선택.")] string? note = null)
    {
        if (string.IsNullOrWhiteSpace(label))
            return ChronoLoadTools.Error("invalid_label", "이름이 비어 있다.");

        long now = DateTime.UtcNow.Ticks;
        var (mark, moved) = ctx.Markers.Set(label.Trim(), now, note);

        return new
        {
            header = MetricReader.Header(ctx),
            label = mark.Label,
            at = McpJsonHelpers.Iso(mark.UtcTicks),
            note = mark.Note,
            // 같은 이름이 있었으면 그 시각. 실수로 덮어썼는지 알 수 있게 돌려준다.
            movedFrom = moved is { } previous ? McpJsonHelpers.Iso(previous) : null,
            bufferStartsAt = BufferStart() is { } start ? McpJsonHelpers.Iso(start) : null,
        };
    }

    [McpServerTool(Name = "list_marks")]
    [Description("찍어 둔 마커 목록. 시각순. inBuffer 가 false 면 링 버퍼에서 밀려나 그 시각으로 시작하는 구간은 잘린다.")]
    public object ListMarks()
    {
        long? start = BufferStart();
        return new
        {
            header = MetricReader.Header(ctx),
            bufferStartsAt = start is { } s ? McpJsonHelpers.Iso(s) : null,
            marks = ctx.Markers.All().Select(m => new
            {
                label = m.Label,
                at = McpJsonHelpers.Iso(m.UtcTicks),
                note = m.Note,
                inBuffer = start is { } b && m.UtcTicks >= b,
            }).ToArray(),
        };
    }

    [McpServerTool(Name = "get_interval_stats")]
    [Description("두 시점 사이의 통계(평균·최소·최대·p50·p95·p99·표준편차, 백분율 지표는 포화 비율). from·to 는 마커 이름이나 ISO-8601 시각이고 to 를 생략하면 지금이다. 링 버퍼의 실측 표본으로 정확히 센다. 리셋 구간과 무관하다. coverage 는 읽어 본 실측 중 값을 얻은 비율 — 1 보다 작으면 통계는 구간 일부만의 것이다.")]
    public object GetIntervalStats(
        [Description("시작 — 마커 이름 또는 ISO-8601 시각.")] string from,
        [Description("끝 — 마커 이름 또는 ISO-8601 시각. 생략하면 지금.")] string? to = null,
        [Description("지표 이름. 생략하면 전 지표.")] string? metric = null,
        [Description("장치 키. 생략하면 전 장치.")] string? deviceKey = null,
        [Description("포화로 칠 문턱(%). 백분율 지표에만 쓰인다.")]
        double saturationThreshold = MetricReader.DefaultSaturationThreshold)
    {
        if (Resolve(from) is not { } start) return UnknownPoint(from);
        long end = DateTime.UtcNow.Ticks;
        if (to is not null)
        {
            if (Resolve(to) is not { } resolved) return UnknownPoint(to);
            end = resolved;
        }
        if (end <= start) return ChronoLoadTools.Error("empty_interval", "끝이 시작보다 앞이거나 같다.");

        if (!TryKind(metric, out var kind, out var error)) return error!;

        var blocks = new List<IntervalBlock>();
        int omitted = 0;
        bool truncated = false;
        foreach (var (device, k) in Targets(kind, deviceKey))
        {
            // 지표를 콕 집어 물었으면 표본이 없어도 빈 칸으로 답한다 — 빈 목록만 주면 "그런 지표가 없다"와
            // "있는데 구간 안에 실측이 없다(깨진 카운터라 전부 측정 불가 등)"를 가르지 못한다.
            // 전 지표를 물을 때는 응답이 커지므로 빼고 수만 센다.
            var block = Block(device, k, start, end, saturationThreshold, keepEmpty: kind is not null, out bool cut);
            truncated |= cut;
            if (block is null) { omitted++; continue; }
            blocks.Add(block);
        }

        if (blocks.Count == 0 && omitted == 0) return ChronoLoadTools.Error("no_match", "조건에 맞는 지표가 없다.");

        return new
        {
            header = MetricReader.Header(ctx),
            interval = Describe(from, to, start, end, truncated),
            stats = blocks,
            // 구간 안에 실측 표본이 하나도 없어 뺀 지표 수.
            omittedEmpty = omitted,
        };
    }

    [McpServerTool(Name = "compare_intervals")]
    [Description("마커 여러 개를 시각순으로 이어 만든 구간들을 지표별로 한 표에 놓는다. marks 가 [a, b, c] 면 a→b, b→c 두 구간이고, untilNow 가 true 면 c→지금도 더한다. 칸마다 평균·p95·최대·표본 수, 백분율 지표는 포화 비율. 응답이 크므로 metric·deviceKey 로 좁히기를 권한다.")]
    public object CompareIntervals(
        [Description("마커 이름 목록(2개 이상, 또는 untilNow 와 함께 1개 이상). 시각순으로 정렬해 쓴다.")] string[] marks,
        [Description("마지막 마커부터 지금까지를 구간으로 더한다.")] bool untilNow = false,
        [Description("지표 이름. 생략하면 전 지표.")] string? metric = null,
        [Description("장치 키. 생략하면 전 장치.")] string? deviceKey = null,
        [Description("포화로 칠 문턱(%). 백분율 지표에만 쓰인다.")]
        double saturationThreshold = MetricReader.DefaultSaturationThreshold)
    {
        var points = new List<(string Label, long Ticks)>();
        foreach (string label in marks ?? [])
        {
            if (ctx.Markers.Find(label) is not { } m) return UnknownPoint(label);
            points.Add((m.Label, m.UtcTicks));
        }
        points.Sort((a, b) => a.Ticks.CompareTo(b.Ticks));
        if (untilNow && points.Count > 0) points.Add(("now", DateTime.UtcNow.Ticks));

        if (points.Count < 2)
            return ChronoLoadTools.Error("too_few_marks", "구간을 만들려면 마커가 둘 이상(또는 untilNow 와 함께 하나 이상) 필요하다.");

        if (!TryKind(metric, out var kind, out var error)) return error!;

        var segments = new List<(string Name, long From, long To)>();
        for (int i = 1; i < points.Count; i++)
            segments.Add(($"{points[i - 1].Label}→{points[i].Label}", points[i - 1].Ticks, points[i].Ticks));

        var rows = new List<object>();
        var truncatedSegments = new bool[segments.Count];
        foreach (var (device, k) in Targets(kind, deviceKey))
        {
            bool percent = k.Unit() == MetricUnit.Percent;
            var cells = new object?[segments.Count];
            bool any = false;
            for (int s = 0; s < segments.Count; s++)
            {
                var w = WindowStatistics.Compute(ctx.Registry, device.SlotOf(k), segments[s].From, segments[s].To,
                    Quantiles, percent ? saturationThreshold : null);
                truncatedSegments[s] |= w.StartsBeforeBuffer;

                // 읽어 본 적도 없는 칸만 null 이다. 읽어 봤는데 값을 하나도 못 얻은 칸(깨진 카운터 등)은
                // n 0 · coverage 0 으로 낸다 — null 이면 "그 구간엔 이 지표가 없었다"로 읽힌다.
                if (w.Attempts == 0) { cells[s] = null; continue; }

                // 전 지표를 물을 때 값이 하나도 없는 행은 빼서 응답을 줄인다. 콕 집어 물었으면 남긴다.
                any |= w.Count > 0 || kind is not null;
                cells[s] = new
                {
                    n = w.Count,
                    coverage = McpJsonHelpers.Finite(w.Coverage),
                    avg = McpJsonHelpers.Finite(w.Mean),
                    p95 = McpJsonHelpers.Finite(w.Quantiles[1]),
                    max = McpJsonHelpers.Finite(w.Max),
                    saturatedFraction = percent ? McpJsonHelpers.Finite(w.FractionAtOrAbove) : null,
                };
            }

            if (!any) continue;
            rows.Add(new
            {
                metric = k.ToString(),
                deviceKey = device.Key,
                unit = McpJsonHelpers.UnitName(k.Unit()),
                segments = cells,
            });
        }

        return new
        {
            header = MetricReader.Header(ctx),
            saturationThreshold,
            segments = segments.Select((s, i) => new
            {
                name = s.Name,
                from = McpJsonHelpers.Iso(s.From),
                to = McpJsonHelpers.Iso(s.To),
                seconds = Math.Round(TimeSpan.FromTicks(s.To - s.From).TotalSeconds, 3),
                truncated = truncatedSegments[i],
            }).ToArray(),
            // 행마다 segments 배열의 i 번째 칸이 위 segments[i] 구간이다. 읽어 본 적도 없는 칸은 null,
            // 읽었는데 값을 못 얻은 칸은 n 0 · coverage 0.
            rows,
        };
    }

    // ── 공통 ────────────────────────────────────────────────────

    /// <param name="keepEmpty">표본이 없어도 빈 칸(값은 전부 null)을 돌려준다. 아니면 null.</param>
    private IntervalBlock? Block(DeviceHandle device, MetricKind kind, long from, long to,
        double threshold, bool keepEmpty, out bool truncated)
    {
        bool percent = kind.Unit() == MetricUnit.Percent;
        var w = WindowStatistics.Compute(ctx.Registry, device.SlotOf(kind), from, to,
            Quantiles, percent ? threshold : null);
        truncated = w.StartsBeforeBuffer;
        if (w.Count == 0 && !keepEmpty) return null;

        return new IntervalBlock(
            kind.ToString(), device.Key, McpJsonHelpers.UnitName(kind.Unit()), w.Count,
            McpJsonHelpers.Finite(w.Mean), McpJsonHelpers.Finite(w.Min), McpJsonHelpers.Finite(w.Max),
            McpJsonHelpers.Finite(w.Quantiles[0]), McpJsonHelpers.Finite(w.Quantiles[1]),
            McpJsonHelpers.Finite(w.Quantiles[2]), McpJsonHelpers.Finite(w.StdDev))
        {
            Coverage = McpJsonHelpers.Finite(w.Coverage),
            SaturationThreshold = percent ? threshold : null,
            SaturatedFraction = percent ? McpJsonHelpers.Finite(w.FractionAtOrAbove) : null,
        };
    }

    private IEnumerable<(DeviceHandle Device, MetricKind Kind)> Targets(MetricKind? kind, string? deviceKey)
    {
        foreach (var device in ctx.Registry.ActiveDevices.OrderBy(d => d.Info.Class).ThenBy(d => d.Index))
        {
            if (deviceKey is not null &&
                !string.Equals(device.Key, deviceKey, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var k in device.Kinds.OrderBy(x => x))
                if (kind is not { } want || k == want) yield return (device, k);
        }
    }

    private static bool TryKind(string? metric, out MetricKind? kind, out object? error)
    {
        kind = null;
        error = null;
        if (string.IsNullOrWhiteSpace(metric)) return true;
        if (Enum.TryParse<MetricKind>(metric, ignoreCase: true, out var parsed)) { kind = parsed; return true; }

        error = ChronoLoadTools.Error("unknown_metric", $"'{metric}' 은 알 수 없는 지표다.");
        return false;
    }

    /// <summary>마커 이름을 먼저 보고, 없으면 ISO-8601 시각으로 읽는다. 오프셋이 없으면 로컬 시각이다.</summary>
    private long? Resolve(string point)
    {
        if (ctx.Markers.Find(point) is { } mark) return mark.UtcTicks;
        if (point.Equals("now", StringComparison.OrdinalIgnoreCase)) return DateTime.UtcNow.Ticks;

        return DateTimeOffset.TryParse(point, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at)
            ? at.UtcTicks
            : null;
    }

    private object UnknownPoint(string point) => ChronoLoadTools.Error("unknown_point",
        $"'{point}' 은 마커 이름도 ISO-8601 시각도 아니다. list_marks 로 마커를 확인한다.");

    private object Describe(string from, string? to, long start, long end, bool truncated) => new
    {
        from = McpJsonHelpers.Iso(start),
        to = McpJsonHelpers.Iso(end),
        fromLabel = ctx.Markers.Find(from)?.Label,
        toLabel = to is null ? null : ctx.Markers.Find(to)?.Label,
        seconds = Math.Round(TimeSpan.FromTicks(end - start).TotalSeconds, 3),
        // 시작이 링 버퍼보다 앞이라 앞부분은 셀 수 없었다. 통계는 남은 표본만의 것이다.
        truncated,
        bufferStartsAt = BufferStart() is { } s ? McpJsonHelpers.Iso(s) : null,
    };

    private long? BufferStart()
    {
        long frames = ctx.Registry.Frames;
        if (frames == 0) return null;
        return ctx.Registry.TimestampAtFrame(frames - Math.Min(frames, ctx.Registry.SeriesCapacity));
    }
}

/// <summary>구간 통계 한 칸. 리셋 통계(<see cref="StatsBlock"/>)와 같은 정의이고 기준점 필드만 없다.</summary>
public sealed record IntervalBlock(
    [property: JsonPropertyName("metric")] string Metric,
    [property: JsonPropertyName("deviceKey")] string DeviceKey,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("sampleCount")] long SampleCount,
    [property: JsonPropertyName("avg")] double? Avg,
    [property: JsonPropertyName("min")] double? Min,
    [property: JsonPropertyName("max")] double? Max,
    [property: JsonPropertyName("p50")] double? P50,
    [property: JsonPropertyName("p95")] double? P95,
    [property: JsonPropertyName("p99")] double? P99,
    [property: JsonPropertyName("stdDev")] double? StdDev)
{
    /// <summary>읽어 본 실측 중 값을 얻은 비율(0~1). <see cref="StatsBlock.Coverage"/> 와 같다.</summary>
    [JsonPropertyName("coverage")]
    public double? Coverage { get; init; }

    [JsonPropertyName("saturationThreshold")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? SaturationThreshold { get; init; }

    [JsonIgnore]
    public double? SaturatedFraction { get; init; }

    /// <summary>문턱이 있으면 표본이 없어도 <c>null</c> 로 낸다(<see cref="StatsBlock"/> 와 같다).</summary>
    [JsonPropertyName("saturatedFraction")]
    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private JsonElement? SaturatedFractionWire => McpJsonHelpers.AlongWith(SaturationThreshold, SaturatedFraction);
}

/// <summary>
/// 이름 붙은 시각들. MCP 스레드 여럿이 동시에 부를 수 있어 잠근다. 앱 메모리에만 있다.
/// </summary>
public sealed class MarkerBook
{
    /// <summary>들고 있을 최대 개수. 넘치면 가장 오래된 것부터 버린다 — 어차피 링 밖으로 밀려난 것들이다.</summary>
    public const int Capacity = 200;

    public sealed record Marker(string Label, long UtcTicks, string? Note);

    private readonly Lock _gate = new();
    private readonly List<Marker> _marks = [];

    /// <summary>이름을 붙인다. 같은 이름이 있으면 옮기고 이전 시각을 돌려준다.</summary>
    public (Marker Mark, long? MovedFrom) Set(string label, long utcTicks, string? note)
    {
        lock (_gate)
        {
            int at = _marks.FindIndex(m => string.Equals(m.Label, label, StringComparison.OrdinalIgnoreCase));
            long? moved = at >= 0 ? _marks[at].UtcTicks : null;
            if (at >= 0) _marks.RemoveAt(at);

            var mark = new Marker(label, utcTicks, note);
            _marks.Add(mark);
            _marks.Sort((a, b) => a.UtcTicks.CompareTo(b.UtcTicks));
            while (_marks.Count > Capacity) _marks.RemoveAt(0);
            return (mark, moved);
        }
    }

    public Marker? Find(string label)
    {
        lock (_gate)
            return _marks.FirstOrDefault(m => string.Equals(m.Label, label, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<Marker> All()
    {
        lock (_gate) return [.. _marks];
    }
}

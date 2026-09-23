using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Metrics;

namespace ChronoLoad.Mcp;

/// <summary>
/// 레지스트리에서 툴이 쓸 값을 꺼내는 공통 경로.
/// </summary>
/// <remarks>
/// 툴마다 슬롯 조회와 NaN 처리를 따로 쓰면 같은 지표가 툴에 따라 다르게 보인다.
/// "값이 없다"를 0 으로 바꾸는 실수도 그렇게 생긴다 — 여기 한 곳에서만 판단한다.
/// </remarks>
internal static class MetricReader
{
    /// <summary>
    /// 마지막 실측이 이보다 오래됐으면 <c>stale</c> 로 표시한다.
    /// Slow 티어가 4틱(1초)에 한 번이므로 그 서너 배를 넘으면 정상 갱신이 아니다.
    /// </summary>
    private const int StaleAfterSamples = 16;

    public static double? Latest(McpContext ctx, DeviceHandle device, MetricKind kind)
    {
        int slot = device.SlotOf(kind);
        if (slot < 0) return null;

        var series = ctx.Registry.Series(slot);
        if (series is null || series.Count == 0) return null;

        return McpJsonHelpers.Finite(series.Latest);
    }

    public static ByteValue? LatestBytes(McpContext ctx, DeviceHandle device, MetricKind kind) =>
        Latest(ctx, device, kind) is { } value ? ByteValue.From(value) : null;

    /// <summary>
    /// 이 장치의 지표 중 하나라도 한참 갱신되지 않았는가. 장치 단위로 판단하는 이유는
    /// 에이전트가 "이 GPU 값이 지금 것인가"를 묻지 슬롯 단위로 묻지 않기 때문이다.
    /// </summary>
    /// <remarks>
    /// <b>한 번도 측정된 적 없는 지표는 세지 않는다.</b> 그것은 늦은 값이 아니라 없는 값이고,
    /// 응답에서 이미 <c>null</c> 로 그렇게 말하고 있다. 여기에 섞으면 값이 멀쩡히 들어오는
    /// 어댑터가 영영 <c>stale</c> 로 보인다 — 온도 센서를 0개로 돌려주는 내장 GPU 에서
    /// 실제로 그랬고, 에이전트는 멀쩡한 사용률까지 의심하게 된다.
    /// </remarks>
    public static bool IsStale(McpContext ctx, DeviceHandle device)
    {
        foreach (var kind in device.Kinds)
        {
            int slot = device.SlotOf(kind);
            var series = ctx.Registry.Series(slot);
            if (series is null || series.Count == 0 || !series.HasMeasurement) continue;

            if (series.SamplesSinceMeasurement > StaleAfterSamples) return true;
        }

        return false;
    }

    public static ResponseHeader Header(McpContext ctx, bool stale = false) =>
        new(McpJsonHelpers.Iso(ctx.Now), stale, ctx.Registry.Revision);

    /// <summary>키 또는 인덱스로 장치를 찾는다. 키가 우선이다 (§10.3).</summary>
    public static DeviceHandle? Find(McpContext ctx, DeviceClass klass, string? key, int? index)
    {
        var devices = Devices(ctx, klass);

        if (!string.IsNullOrWhiteSpace(key))
            return devices.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));

        if (index is { } i) return devices.FirstOrDefault(d => d.Index == i);
        return null;
    }

    public static IReadOnlyList<DeviceHandle> Devices(McpContext ctx, DeviceClass klass) =>
        ctx.Registry.ActiveDevices.Where(d => d.Info.Class == klass).OrderBy(d => d.Index).ToArray();

    /// <summary>NPU 는 WDDM 어댑터로 열거되지만 GPU 가 아니다. 아이콘으로 가른다.</summary>
    public static bool IsNpu(DeviceHandle device) => device.Info.Icon == IconKind.Npu;

    public static StatsBlock Stats(McpContext ctx, DeviceHandle device, MetricKind kind)
    {
        int slot = device.SlotOf(kind);
        var snapshot = ctx.Registry.StatsSnapshot(slot, McpContext.Scope);
        long now = DateTime.UtcNow.Ticks;

        return new StatsBlock(
            Metric: kind.ToString(),
            DeviceKey: device.Key,
            Unit: McpJsonHelpers.UnitName(kind.Unit()),
            ResetAt: McpJsonHelpers.Iso(snapshot.ResetTimestampUtcTicks),
            ElapsedSeconds: Math.Round(snapshot.Elapsed(now).TotalSeconds, 3),
            SampleCount: snapshot.Count,
            Avg: McpJsonHelpers.Finite(snapshot.Mean),
            Min: snapshot.IsEmpty ? null : McpJsonHelpers.Finite(snapshot.Min),
            Max: snapshot.IsEmpty ? null : McpJsonHelpers.Finite(snapshot.Max),
            P95: McpJsonHelpers.Finite(ctx.Registry.Quantile(slot, 0.95, McpContext.Scope)),
            StdDev: McpJsonHelpers.Finite(snapshot.StdDev));
    }

    /// <summary>
    /// 시계열을 min-max 데시메이션으로 줄인다. 평균으로 줄이면 순간 스파이크가 사라지는데,
    /// GPU 워크로드 분석에서 사라지면 안 되는 것이 바로 그 스파이크다.
    /// </summary>
    /// <remarks>
    /// <b>값이 없는 샘플은 <c>null</c> 로 내보낸다.</b> 시리즈는 측정되지 않은 구간을 <c>NaN</c> 으로
    /// 들고 있는데, JSON 에는 <c>NaN</c> 을 쓸 수 없어 직렬화가 통째로 예외를 던진다 — 슬롯은
    /// 등록됐지만 한 번도 측정되지 않은 지표(PDH 만 붙은 어댑터의 온도 등)에서는 툴 호출 자체가
    /// 실패했다. 0 으로 바꾸는 것은 더 나쁘다. §10.4 의 "값이 없으면 null" 을 여기서도 지킨다.
    /// </remarks>
    public static (double?[] Values, int Span) History(
        McpContext ctx, int slot, int windowSamples, int maxPoints)
    {
        var series = ctx.Registry.Series(slot);
        if (series is null || series.Count == 0) return ([], 0);

        int take = Math.Min(windowSamples, series.Count);
        var window = new float[take];
        int copied = series.CopyLatest(window);
        if (copied <= 0) return ([], 0);

        var source = window.AsSpan(0, copied);
        if (copied <= maxPoints)
            return (Project(source), 1);

        var reduced = new float[maxPoints];
        int produced = MetricSeries.Decimate(source, reduced);
        return (Project(reduced.AsSpan(0, produced)),
                (int)Math.Ceiling((double)copied / Math.Max(1, produced)));

        static double?[] Project(ReadOnlySpan<float> values)
        {
            var result = new double?[values.Length];
            for (int i = 0; i < values.Length; i++) result[i] = McpJsonHelpers.Finite(values[i]);
            return result;
        }
    }
}

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
        new(McpJsonHelpers.Iso(ctx.Now), stale, ctx.Registry.ConfigurationRevision);

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

    /// <summary>
    /// 포화로 치는 사용률 문턱의 기본값. 버스트형 부하에서는 평균이 포화를 가린다 —
    /// 평균 62% 인데 100% 구간이 반복되는 경우를 이 비율이 드러낸다.
    /// </summary>
    public const double DefaultSaturationThreshold = 90;

    private static readonly double[] StatQuantiles = [0.50, 0.95, 0.99];

    public static StatsBlock Stats(McpContext ctx, DeviceHandle device, MetricKind kind,
        double saturationThreshold = DefaultSaturationThreshold)
    {
        int slot = device.SlotOf(kind);
        var snapshot = ctx.Registry.StatsSnapshot(slot, McpContext.Scope);
        long now = DateTime.UtcNow.Ticks;

        // 포화 비율은 백분율 지표에만 뜻이 있다. 바이트·온도에 90 을 대면 아무 의미 없는 숫자가 나온다.
        bool percent = kind.Unit() == MetricUnit.Percent;
        var summary = ctx.Registry.Summarize(slot, McpContext.Scope, StatQuantiles,
            percent ? saturationThreshold : null);

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
            P50: McpJsonHelpers.Finite(summary.Quantiles[0]),
            P95: McpJsonHelpers.Finite(summary.Quantiles[1]),
            P99: McpJsonHelpers.Finite(summary.Quantiles[2]),
            QuantilesExact: snapshot.IsEmpty ? null : summary.Exact,
            StdDev: McpJsonHelpers.Finite(snapshot.StdDev))
        {
            Coverage = McpJsonHelpers.Finite(snapshot.Coverage),
            SaturationThreshold = percent ? saturationThreshold : null,
            SaturatedFraction = percent ? McpJsonHelpers.Finite(summary.FractionAtOrAbove) : null,
        };
    }

    /// <summary>
    /// 최근 <paramref name="window"/> 동안의 <b>실측</b> 표본을 시각과 함께 떠낸다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>실측이 아닌 칸은 버린다.</b> 1초에 한 번 읽는 지표(PDH 엔진 사용률, 벤더 온도·전력)는
    /// 250ms 틱마다 직전 값이 다시 기록된다 — 시간 축을 맞추려는 것이지 새 값이 아니다.
    /// 그대로 내보내면 같은 값이 네 번씩 나와 에이전트가 250ms 해상도로 착각한다.
    /// 시각을 함께 주므로 표본 간격이 곧 그 지표의 실제 주기다.
    /// </para>
    /// <para>
    /// <b>줄여야 하면 시간으로 등분한다.</b> 칸마다 평균·최소·최대를 준다. 예전의 min-max 데시메이션은
    /// 칸당 0~2점을 내 요청한 점 수와 맞지 않았고, 점이 언제인지도 알 수 없었다. 등분하면 점 수가
    /// 요청과 정확히 같고 칸 폭이 일정하다. 최대를 함께 주므로 스파이크는 여전히 사라지지 않는다.
    /// </para>
    /// <para>
    /// 값이 없는 칸은 <c>null</c> 이다(§10.4). 0 으로 채우면 "쉬고 있었다"로 읽힌다.
    /// </para>
    /// </remarks>
    public static HistoryWindow History(McpContext ctx, int slot, TimeSpan window, int maxPoints)
    {
        var registry = ctx.Registry;
        var series = registry.Series(slot);
        if (series is null || series.Count == 0) return HistoryWindow.Empty;

        float[] values = [];
        bool[] measured = [];
        long[] stamps = [];
        int n = 0;

        // 값과 시각은 따로 떠낸다. 그 사이에 프레임이 하나 밀리면 한 칸씩 어긋나므로,
        // 프레임 수가 그대로인 것을 확인하고 쓴다.
        for (int attempt = 0; ; attempt++)
        {
            long before = registry.Frames;
            int take = Math.Min(registry.PointsWithin(window), series.Count);
            if (take <= 0) return HistoryWindow.Empty;

            values = new float[take];
            measured = new bool[take];
            stamps = new long[take];
            n = series.CopyLatest(values, measured);
            int stamped = registry.CopyTimestamps(stamps.AsSpan(0, n));

            if (registry.Frames == before && stamped == n) break;
            if (attempt == 2) return HistoryWindow.Empty;
        }

        long start = stamps[0], end = stamps[n - 1];

        var ticks = new List<long>(n);
        var samples = new List<float>(n);
        for (int i = 0; i < n; i++)
        {
            if (!measured[i] || !float.IsFinite(values[i])) continue;
            ticks.Add(stamps[i]);
            samples.Add(values[i]);
        }

        double? period = MedianIntervalMs(ticks);

        if (samples.Count <= maxPoints)
        {
            var offsets = new long[samples.Count];
            var raw = new double?[samples.Count];
            var counts = new int[samples.Count];
            for (int i = 0; i < samples.Count; i++)
            {
                offsets[i] = (ticks[i] - start) / TimeSpan.TicksPerMillisecond;
                raw[i] = McpJsonHelpers.Finite(samples[i]);
                counts[i] = 1;
            }

            return new HistoryWindow(start, end, "raw", null, period, samples.Count,
                offsets, raw, raw, raw, counts);
        }

        // 칸 폭은 창 전체를 maxPoints 로 나눈 것. 끝 표본이 마지막 칸에 들도록 올림한다.
        long width = Math.Max(1, (end - start + maxPoints) / maxPoints);
        var bucketOffsets = new long[maxPoints];
        var avg = new double?[maxPoints];
        var min = new double?[maxPoints];
        var max = new double?[maxPoints];
        var count = new int[maxPoints];
        var sum = new double[maxPoints];

        for (int b = 0; b < maxPoints; b++) bucketOffsets[b] = b * width / TimeSpan.TicksPerMillisecond;

        for (int i = 0; i < samples.Count; i++)
        {
            int b = (int)Math.Min(maxPoints - 1, (ticks[i] - start) / width);
            double v = samples[i];
            sum[b] += v;
            count[b]++;
            min[b] = min[b] is { } lo ? Math.Min(lo, v) : v;
            max[b] = max[b] is { } hi ? Math.Max(hi, v) : v;
        }

        for (int b = 0; b < maxPoints; b++)
        {
            if (count[b] == 0) continue;
            avg[b] = McpJsonHelpers.Finite(sum[b] / count[b]);
            min[b] = McpJsonHelpers.Finite(min[b]!.Value);
            max[b] = McpJsonHelpers.Finite(max[b]!.Value);
        }

        return new HistoryWindow(start, end, "bucketed", width / (double)TimeSpan.TicksPerMillisecond,
            period, samples.Count, bucketOffsets, avg, min, max, count);
    }

    /// <summary>
    /// 이웃한 실측 표본 사이 간격의 중앙값(ms). 지표가 실제로 몇 ms 마다 새 값을 내는지다.
    /// 평균이 아니라 중앙값인 것은 절전 복귀 같은 긴 공백 하나에 끌려가지 않기 위해서다.
    /// </summary>
    private static double? MedianIntervalMs(List<long> ticks)
    {
        if (ticks.Count < 2) return null;

        var gaps = new long[ticks.Count - 1];
        for (int i = 1; i < ticks.Count; i++) gaps[i - 1] = ticks[i] - ticks[i - 1];
        Array.Sort(gaps);

        return Math.Round(gaps[gaps.Length / 2] / (double)TimeSpan.TicksPerMillisecond, 1);
    }
}

/// <summary>
/// <see cref="MetricReader.History"/> 의 결과. 배열은 모두 같은 길이이고 같은 인덱스가 같은 점이다.
/// </summary>
/// <param name="Mode"><c>raw</c> 면 점 하나가 실측 하나, <c>bucketed</c> 면 시간 등분 칸 하나.</param>
/// <param name="BucketMs">칸 폭(ms). raw 면 null.</param>
/// <param name="MeasuredPeriodMs">실측 표본 간격의 중앙값. 이 지표의 실제 갱신 주기다.</param>
/// <param name="OffsetsMs">각 점의 시각. <paramref name="StartUtcTicks"/> 로부터의 ms. 칸이면 칸의 시작.</param>
internal sealed record HistoryWindow(
    long StartUtcTicks,
    long EndUtcTicks,
    string Mode,
    double? BucketMs,
    double? MeasuredPeriodMs,
    int MeasuredCount,
    long[] OffsetsMs,
    double?[] Avg,
    double?[] Min,
    double?[] Max,
    int[] Samples)
{
    public static HistoryWindow Empty { get; } = new(0, 0, "raw", null, null, 0, [], [], [], [], []);
}

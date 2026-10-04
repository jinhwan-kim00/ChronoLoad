namespace ChronoLoad.Core.Metrics;

/// <summary>
/// 시각으로 자른 구간 하나의 통계. 링 버퍼 안의 실측 표본만으로 <b>정확히</b> 구한다 (§7.3 · §10.2).
/// </summary>
/// <param name="Count">구간 안의 실측 표본 수. 0 이면 나머지는 NaN 이다.</param>
/// <param name="FirstUtcTicks">구간 안 첫 실측 표본의 시각. 없으면 null.</param>
/// <param name="LastUtcTicks">구간 안 마지막 실측 표본의 시각. 없으면 null.</param>
/// <param name="StartsBeforeBuffer">
/// 요청한 시작이 링에 남은 가장 오래된 프레임보다 앞이다. 그 앞부분은 이미 밀려나 통계에 없다.
/// </param>
/// <param name="Attempts">구간 안에서 읽어 본 실측 수 — 값을 얻지 못한 것(NaN)도 센다. <see cref="StatsAccumulator.Attempts"/> 와 같다.</param>
public sealed record WindowStats(
    long Count,
    double Mean,
    double Min,
    double Max,
    double StdDev,
    double[] Quantiles,
    double FractionAtOrAbove,
    long? FirstUtcTicks,
    long? LastUtcTicks,
    bool StartsBeforeBuffer,
    long Attempts = 0)
{
    /// <summary>읽어 본 실측 중 값을 얻은 비율(0~1). 읽어 본 적이 없으면 NaN. <see cref="StatsAccumulator.Coverage"/> 와 같다.</summary>
    public double Coverage => Attempts == 0 ? double.NaN : (double)Count / Attempts;
}

public static class WindowStatistics
{
    /// <summary>
    /// <paramref name="fromUtcTicks"/> 이상 <paramref name="toUtcTicks"/> 이하에 커밋된 실측 표본의 통계.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 리셋 구간(§7.3)과 달리 누산기가 없다 — 표본을 링에서 떠내 그 자리에서 센다. 그래서 구간이
    /// 링(15분)을 벗어나면 벗어난 만큼은 셀 수 없고, 그 사실을 <see cref="WindowStats.StartsBeforeBuffer"/> 로 알린다.
    /// 근사로 메우지 않는다 — 사후에 여러 구간을 비교하려는 기능이라 구간마다 기준이 다르면 비교가 무너진다.
    /// </para>
    /// <para>
    /// 유지값(Slow 티어가 매 틱 다시 쓰는 직전 값)은 빼고 실측만 센다. 통계 누산기와 같은 규칙이다.
    /// 분위수는 최근접 순위, 표준편차는 표본(n−1) 기준이다 — 리셋 구간 통계와 같은 정의다.
    /// </para>
    /// </remarks>
    public static WindowStats Compute(
        MetricRegistry registry, int slot, long fromUtcTicks, long toUtcTicks,
        ReadOnlySpan<double> quantiles, double? threshold = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var none = new WindowStats(0, double.NaN, double.NaN, double.NaN, double.NaN,
            Enumerable.Repeat(double.NaN, quantiles.Length).ToArray(), double.NaN, null, null, false);

        var series = registry.Series(slot);
        if (series is null || series.Count == 0 || toUtcTicks < fromUtcTicks) return none;

        float[] values = [];
        bool[] measured = [];
        long[] stamps = [];
        int n = 0;

        // 값과 시각을 따로 떠내므로 그 사이에 프레임이 밀리면 한 칸 어긋난다. 프레임 수가 그대로인 것을 확인한다.
        for (int attempt = 0; ; attempt++)
        {
            long before = registry.Frames;
            int take = series.Count;
            values = new float[take];
            measured = new bool[take];
            stamps = new long[take];
            n = series.CopyLatest(values, measured);
            int stamped = registry.CopyTimestamps(stamps.AsSpan(0, n));

            if (registry.Frames == before && stamped == n) break;
            if (attempt == 2) return none;
        }

        if (n == 0) return none;

        // 링에 남은 가장 오래된 프레임. 시리즈가 늦게 시작했으면 그 앞은 원래 없던 것이지 밀려난 것이 아니다.
        long oldestFrame = registry.Frames - Math.Min(registry.Frames, registry.SeriesCapacity);
        long? oldestStamp = registry.TimestampAtFrame(oldestFrame);
        bool truncated = series.Written > series.Count && oldestStamp is { } o && fromUtcTicks < o;

        var samples = new List<float>();
        long? first = null, last = null;
        long attempts = 0;
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            if (stamps[i] < fromUtcTicks || stamps[i] > toUtcTicks) continue;
            if (!measured[i]) continue;
            attempts++;
            if (!float.IsFinite(values[i])) continue;

            samples.Add(values[i]);
            sum += values[i];
            first ??= stamps[i];
            last = stamps[i];
        }

        if (samples.Count == 0) return none with { StartsBeforeBuffer = truncated, Attempts = attempts };

        double mean = sum / samples.Count;
        double m2 = 0;
        foreach (float v in samples) m2 += (v - mean) * (v - mean);
        double stdDev = samples.Count < 2 ? double.NaN : Math.Sqrt(m2 / (samples.Count - 1));

        double fraction = double.NaN;
        if (threshold is { } t)
        {
            int above = 0;
            foreach (float v in samples) if (v >= t) above++;
            fraction = (double)above / samples.Count;
        }

        samples.Sort();
        var q = new double[quantiles.Length];
        for (int i = 0; i < quantiles.Length; i++)
        {
            long rank = Math.Max(1, (long)Math.Ceiling(Math.Clamp(quantiles[i], 0, 1) * samples.Count));
            q[i] = samples[(int)rank - 1];
        }

        return new WindowStats(samples.Count, mean, samples[0], samples[^1], stdDev, q, fraction, first, last, truncated, attempts);
    }
}

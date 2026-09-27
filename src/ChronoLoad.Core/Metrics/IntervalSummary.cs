namespace ChronoLoad.Core.Metrics;

/// <summary>
/// 리셋 이후 구간의 분포 요약. <see cref="MetricRegistry.Summarize"/> 가 만든다.
/// </summary>
/// <param name="Quantiles">요청한 순서대로의 분위수. 표본이 없으면 NaN.</param>
/// <param name="FractionAtOrAbove">문턱 이상이었던 표본의 비율(0~1). 문턱을 주지 않았으면 NaN.</param>
/// <param name="Exact">
/// 구간의 표본을 링에서 직접 정렬했으면 true. 구간이 링보다 길어 스케치로 근사했으면 false —
/// 그때의 오차는 상대 ±<see cref="PercentileTracker.RelativeAccuracy"/> 이내다.
/// </param>
public readonly record struct IntervalSummary(double[] Quantiles, double FractionAtOrAbove, bool Exact)
{
    public static IntervalSummary Empty(int quantiles)
    {
        var nan = new double[quantiles];
        Array.Fill(nan, double.NaN);
        return new IntervalSummary(nan, double.NaN, Exact: false);
    }
}

namespace ChronoLoad.Core.Metrics;

/// <summary>
/// 리셋 시점 기준 구간 통계. <b>O(1) 갱신, 고정 메모리</b>라 24시간을 누적해도 비용이 늘지 않는다.
/// 표준편차는 Welford 온라인 알고리즘으로 구해 큰 평균 위에 작은 분산이 얹힌 경우에도 정확하다.
/// </summary>
/// <remarks>NaN은 "이번 주기에 값 없음"을 뜻하므로 집계에서 제외한다.</remarks>
public struct StatsAccumulator
{
    public long Count;
    public double Sum;
    private double _mean;
    private double _m2;
    public float Min;
    public float Max;

    /// <summary>마지막 리셋 시각(UTC ticks). 경과 시간 표시에 쓴다.</summary>
    public long ResetTimestampUtcTicks;

    /// <summary>
    /// 리셋 시점의 프레임 번호(§7.4). 지금 프레임과의 차가 링 길이 안이면 구간의 표본이
    /// 링에 다 남아 있어 분위수를 정확히 구할 수 있다. 레지스트리가 채운다.
    /// </summary>
    public long ResetFrame;

    public static StatsAccumulator Create(long nowUtcTicks)
    {
        var s = default(StatsAccumulator);
        s.Reset(nowUtcTicks);
        return s;
    }

    public void Reset(long nowUtcTicks)
    {
        Count = 0;
        Sum = 0;
        _mean = 0;
        _m2 = 0;
        Min = float.NaN;
        Max = float.NaN;
        ResetTimestampUtcTicks = nowUtcTicks;
    }

    public void Add(float value)
    {
        if (float.IsNaN(value)) return;

        Count++;
        Sum += value;

        double delta = value - _mean;
        _mean += delta / Count;
        _m2 += delta * (value - _mean);

        if (float.IsNaN(Min) || value < Min) Min = value;
        if (float.IsNaN(Max) || value > Max) Max = value;
    }

    public readonly bool IsEmpty => Count == 0;

    public readonly double Mean => Count == 0 ? double.NaN : _mean;

    /// <summary>표본 분산(n−1). 샘플이 2개 미만이면 NaN.</summary>
    public readonly double Variance => Count < 2 ? double.NaN : _m2 / (Count - 1);

    public readonly double StdDev
    {
        get
        {
            double v = Variance;
            return double.IsNaN(v) ? double.NaN : Math.Sqrt(v);
        }
    }

    public readonly TimeSpan Elapsed(long nowUtcTicks) =>
        TimeSpan.FromTicks(Math.Max(0, nowUtcTicks - ResetTimestampUtcTicks));
}

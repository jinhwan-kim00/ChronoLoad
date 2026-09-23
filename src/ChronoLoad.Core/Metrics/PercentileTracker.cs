namespace ChronoLoad.Core.Metrics;

/// <summary>
/// 고정 버킷 히스토그램으로 분위수를 근사한다. 정렬이 없어 O(1) 갱신·고정 메모리이므로
/// 구간이 아무리 길어져도 비용이 늘지 않는다.
/// </summary>
/// <remarks>
/// 정확한 p95를 내려면 표본을 모두 들고 있어야 하는데, 24시간 누적을 목표로 하는 이 앱에서는
/// 성립하지 않는다. 버킷 해상도만큼의 오차(백분율 계열 ±0.5%p)를 받아들이는 편이 맞다.
/// </remarks>
public sealed class PercentileTracker
{
    private readonly long[] _buckets;
    private readonly double _min;
    private readonly double _max;
    private readonly bool _logarithmic;
    private long _count;
    private long _below;   // 하한 미만
    private long _above;   // 상한 초과

    private PercentileTracker(int bucketCount, double min, double max, bool logarithmic)
    {
        _buckets = new long[bucketCount];
        _min = min;
        _max = max;
        _logarithmic = logarithmic;
    }

    /// <summary>0~100% 계열. 1% 단위 101개 버킷.</summary>
    public static PercentileTracker ForPercent() => new(101, 0, 100, logarithmic: false);

    /// <summary>바이트·B/s 계열. 1 B ~ 16 TB를 64개 로그 버킷으로.</summary>
    public static PercentileTracker ForBytes() => new(64, 1, Math.Pow(2, 44), logarithmic: true);

    public static PercentileTracker ForRange(double min, double max, int buckets = 64) =>
        new(buckets, min, max, logarithmic: false);

    public long Count => _count;

    public void Reset()
    {
        Array.Clear(_buckets);
        _count = 0;
        _below = 0;
        _above = 0;
    }

    public void Add(double value)
    {
        if (double.IsNaN(value)) return;
        _count++;

        if (value < _min) { _below++; return; }
        if (value > _max) { _above++; return; }

        _buckets[BucketOf(value)]++;
    }

    private int BucketOf(double value)
    {
        double t = _logarithmic
            ? (Math.Log2(Math.Max(value, _min)) - Math.Log2(_min)) / (Math.Log2(_max) - Math.Log2(_min))
            : (value - _min) / (_max - _min);

        int idx = (int)(t * (_buckets.Length - 1) + 0.5);
        return Math.Clamp(idx, 0, _buckets.Length - 1);
    }

    private double ValueOf(int bucket)
    {
        double t = (double)bucket / (_buckets.Length - 1);
        return _logarithmic
            ? Math.Pow(2, Math.Log2(_min) + t * (Math.Log2(_max) - Math.Log2(_min)))
            : _min + t * (_max - _min);
    }

    /// <summary><paramref name="q"/>는 0~1. 표본이 없으면 NaN.</summary>
    public double Quantile(double q)
    {
        if (_count == 0) return double.NaN;
        q = Math.Clamp(q, 0, 1);

        long target = (long)Math.Ceiling(q * _count);
        if (target <= 0) target = 1;

        long cumulative = _below;
        if (cumulative >= target) return _min;

        for (int i = 0; i < _buckets.Length; i++)
        {
            cumulative += _buckets[i];
            if (cumulative >= target) return ValueOf(i);
        }

        return _max;
    }
}

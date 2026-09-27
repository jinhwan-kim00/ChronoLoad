namespace ChronoLoad.Core.Metrics;

/// <summary>
/// 상대 오차가 보장되는 로그 버킷 스케치로 분위수를 근사한다(DDSketch 방식).
/// 정렬이 없어 O(1) 갱신이고, 메모리는 <b>실제로 관측된 값의 폭</b>에만 비례한다.
/// </summary>
/// <remarks>
/// <para>
/// 정확한 분위수는 표본을 모두 들고 있어야 나온다. 링 버퍼 안에 구간이 다 들어 있으면
/// <see cref="MetricRegistry"/> 가 이 스케치 대신 표본을 직접 정렬한다 — 이것은 구간이
/// 링(15분)보다 길어졌을 때의 경로다. 24시간을 누적해도 비용이 늘지 않아야 한다.
/// </para>
/// <para>
/// <b>왜 고정 칸 히스토그램이 아닌가.</b> 바이트 계열을 1 B ~ 16 TB 64칸으로 나누면 칸 하나가
/// ×1.62 배라, 14.6 GB 가 12.35 GB 로 답해졌다. 전력을 0~1000 W 64칸으로 나누면 칸 폭이 15.9 W 라
/// 22.5 W 유휴 전력의 p95 가 15.9 W 로 나왔다 — 최소보다 작은 p95 다. 서로 다른 지표가 같은 칸
/// 경계에 떨어져 같은 숫자를 내기도 했다. 단위마다 범위를 맞추는 한 이 문제는 되풀이된다.
/// 로그 버킷은 값의 크기와 무관하게 <b>상대 오차 <see cref="RelativeAccuracy"/> 이내</b>를 보장한다.
/// </para>
/// <para>
/// 답은 늘 관측된 최솟값·최댓값 안으로 자른다. 버킷 대표값이 범위 밖으로 나가면 그것은
/// 근사의 흔적일 뿐 어떤 표본과도 맞지 않는 숫자다.
/// </para>
/// </remarks>
public sealed class PercentileTracker
{
    /// <summary>분위수 답의 최대 상대 오차. 95 W 는 94.5~95.5 W 안에 든다.</summary>
    public const double RelativeAccuracy = 0.005;

    /// <summary>이보다 작은 크기는 0 으로 센다. 사용률 0.000001% 와 0% 를 가를 이유가 없다.</summary>
    private const double ZeroThreshold = 1e-6;

    private static readonly double Gamma = (1 + RelativeAccuracy) / (1 - RelativeAccuracy);
    private static readonly double LogGamma = Math.Log(Gamma);

    // 양수·음수를 따로 센다. 음수는 드물지만(온도 센서 초기값 등) 부호를 잃으면 순서가 뒤집힌다.
    private readonly Store _positive = new();
    private readonly Store _negative = new();
    private long _zero;
    private long _count;
    private double _min = double.NaN;
    private double _max = double.NaN;

    public long Count => _count;

    public void Reset()
    {
        _positive.Clear();
        _negative.Clear();
        _zero = 0;
        _count = 0;
        _min = double.NaN;
        _max = double.NaN;
    }

    public void Add(double value)
    {
        if (!double.IsFinite(value)) return;
        _count++;

        if (double.IsNaN(_min) || value < _min) _min = value;
        if (double.IsNaN(_max) || value > _max) _max = value;

        if (value > ZeroThreshold) _positive.Add(IndexOf(value));
        else if (value < -ZeroThreshold) _negative.Add(IndexOf(-value));
        else _zero++;
    }

    private static int IndexOf(double magnitude) => (int)Math.Ceiling(Math.Log(magnitude) / LogGamma);

    /// <summary>버킷 (γ^(i−1), γ^i] 의 대표값. 양 끝 어느 쪽에 대해서도 상대 오차가 α 이내다.</summary>
    private static double ValueOf(int index) => 2 * Math.Pow(Gamma, index) / (Gamma + 1);

    /// <summary><paramref name="q"/>는 0~1. 표본이 없으면 NaN.</summary>
    /// <remarks>순위는 최근접 순위(nearest-rank) 정의를 쓴다 — 정확 경로와 같은 정의다.</remarks>
    public double Quantile(double q)
    {
        if (_count == 0) return double.NaN;
        q = Math.Clamp(q, 0, 1);

        long rank = Math.Max(1, (long)Math.Ceiling(q * _count));

        // 양 끝 순위는 버킷이 아니라 실제 최솟값·최댓값이다. 근사할 이유가 없다.
        if (rank == 1) return _min;
        if (rank == _count) return _max;

        double value = ValueAtRank(rank);
        return Math.Clamp(value, _min, _max);
    }

    private double ValueAtRank(long rank)
    {
        // 작은 값부터: 음수(크기가 큰 것부터) → 0 → 양수(작은 것부터).
        long cumulative = 0;

        for (int i = _negative.MaxIndex; i >= _negative.MinIndex && _negative.Any; i--)
        {
            cumulative += _negative[i];
            if (cumulative >= rank) return -ValueOf(i);
        }

        cumulative += _zero;
        if (cumulative >= rank) return 0;

        for (int i = _positive.MinIndex; i <= _positive.MaxIndex && _positive.Any; i++)
        {
            cumulative += _positive[i];
            if (cumulative >= rank) return ValueOf(i);
        }

        return _max;
    }

    /// <summary>
    /// <paramref name="threshold"/> 이상이었던 표본의 비율(0~1). 표본이 없으면 NaN.
    /// </summary>
    /// <remarks>
    /// 문턱이 걸친 버킷 하나만큼은 근사다 — 그 버킷은 대표값이 문턱 이상이면 센다.
    /// 문턱이 최솟값 이하·최댓값 초과이면 정확히 1·0 이다.
    /// </remarks>
    public double FractionAtOrAbove(double threshold)
    {
        if (_count == 0) return double.NaN;
        if (threshold <= _min) return 1;
        if (threshold > _max) return 0;

        long above = 0;
        for (int i = _positive.MinIndex; i <= _positive.MaxIndex && _positive.Any; i++)
            if (ValueOf(i) >= threshold) above += _positive[i];

        if (threshold <= 0)
        {
            above += _zero;
            for (int i = _negative.MinIndex; i <= _negative.MaxIndex && _negative.Any; i++)
                if (-ValueOf(i) >= threshold) above += _negative[i];
        }

        return (double)above / _count;
    }

    /// <summary>
    /// 관측된 인덱스 폭만큼만 자라는 조밀 저장소. 사용률 0.5~100% 는 530칸,
    /// 메모리 14~16 GB 는 14칸이면 된다 — 쓰지 않는 자릿수를 위해 자리를 잡지 않는다.
    /// </summary>
    private sealed class Store
    {
        private int[] _counts = [];
        private int _offset;

        public bool Any => _counts.Length > 0;
        public int MinIndex => _offset;
        public int MaxIndex => _offset + _counts.Length - 1;

        public long this[int index] => _counts[index - _offset];

        public void Add(int index)
        {
            if (_counts.Length == 0)
            {
                _counts = new int[8];
                _offset = index - 4;
            }
            else if (index < _offset || index > MaxIndex)
            {
                int min = Math.Min(index, _offset), max = Math.Max(index, MaxIndex);
                // 앞뒤로 여유를 두고 늘린다. 한 칸씩 늘리면 흔들리는 값마다 배열을 다시 잡는다.
                int length = max - min + 1;
                int slack = Math.Max(8, length / 4);
                var grown = new int[length + 2 * slack];
                int newOffset = min - slack;
                Array.Copy(_counts, 0, grown, _offset - newOffset, _counts.Length);
                _counts = grown;
                _offset = newOffset;
            }

            _counts[index - _offset]++;
        }

        public void Clear()
        {
            _counts = [];
            _offset = 0;
        }
    }
}

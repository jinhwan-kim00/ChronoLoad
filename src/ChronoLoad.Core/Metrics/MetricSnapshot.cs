using ChronoLoad.Core.Devices;

namespace ChronoLoad.Core.Metrics;

/// <summary>구간 통계. 표본이 하나도 없으면 <see cref="Count"/>가 0 이고 나머지는 NaN 이다.</summary>
/// <remarks>
/// <b>실측 표본만 센다.</b> Slow 티어 지표는 Fast 틱마다 직전 값이 다시 기록되는데(§7.2),
/// 그 반복을 세면 표본 수가 주기 비율만큼 부풀고 편차 0 인 반복이 표준편차를 끌어내린다.
/// </remarks>
public readonly record struct RangeStats(int Count, double Mean, double Min, double Max)
{
    public static readonly RangeStats Empty = new(0, double.NaN, double.NaN, double.NaN);

    /// <summary>값이 있는가. 거짓이면 화면에 <c>0</c> 이 아니라 <c>—</c> 를 적는다.</summary>
    public bool HasValue => Count > 0;
}

/// <summary>
/// 얼어붙은 지표 하나. 값과 실측 여부를 그대로 떠 온 배열이며 <b>다시 쓰이지 않는다</b>.
/// </summary>
public sealed class SnapshotMetric
{
    internal SnapshotMetric(DeviceInfo device, MetricKind kind, float[] values, bool[] measured)
    {
        Device = device;
        Kind = kind;
        Values = values;
        Measured = measured;
    }

    /// <summary>
    /// 장치 정보는 <b>값으로</b> 들고 있는다. 스냅샷을 열어 둔 채 eGPU 를 빼면 라이브 핸들은
    /// 회수되는데(§5.7), 창은 그 장치의 이름을 계속 말해야 한다 — 과거를 보여주다 갑자기
    /// 현재를 모른다고 하는 물건이 되면 안 된다.
    /// </summary>
    public DeviceInfo Device { get; }

    public MetricKind Kind { get; }
    public MetricUnit Unit => Kind.Unit();

    /// <summary>시간 순(오래된 것 먼저). 값이 없는 칸은 NaN.</summary>
    public float[] Values { get; }

    /// <summary>같은 길이. 거짓이면 직전 값이 유지된 칸이라 통계에서 뺀다.</summary>
    public bool[] Measured { get; }

    public int Count => Values.Length;

    /// <summary><paramref name="from"/> 이상 <paramref name="to"/> 미만 구간의 실측 통계.</summary>
    public RangeStats StatsOf(int from, int to)
    {
        from = Math.Max(0, from);
        to = Math.Min(Count, to);
        if (to <= from) return RangeStats.Empty;

        int n = 0;
        double sum = 0, min = double.MaxValue, max = double.MinValue;
        for (int i = from; i < to; i++)
        {
            if (!Measured[i]) continue;
            float v = Values[i];
            if (float.IsNaN(v)) continue;

            n++;
            sum += v;
            if (v < min) min = v;
            if (v > max) max = v;
        }

        return n == 0 ? RangeStats.Empty : new RangeStats(n, sum / n, min, max);
    }
}

/// <summary>
/// 지금 링 버퍼에 있는 것을 통째로 떠낸 것 (§9.6). <b>새 데이터가 들어오지 않는다.</b>
/// </summary>
/// <remarks>
/// <para>
/// 메인 창은 곁눈질용이라 화면이 계속 흐른다. 스크럽 고정(§8.7)이 한 순간을 붙들지만
/// <b>구간</b>을 재려면 흐름 자체를 멈춰야 한다.
/// </para>
/// <para>
/// 설계서에는 <see cref="MetricSeries"/>를 복제한다고 적었으나, 전용 차트를 두면서
/// <b>평범한 배열</b>이 더 단순해졌다 — 임의 구간을 그리고, 구간 통계를 내고, CSV 로
/// 내보내는 일이 전부 배열 인덱스 하나로 끝난다. 링 버퍼의 랩어라운드는 떠내는 순간 사라진다.
/// </para>
/// </remarks>
public sealed class MetricSnapshot
{
    private MetricSnapshot(long[] timestamps, IReadOnlyList<SnapshotMetric> metrics, DateTime capturedUtc)
    {
        Timestamps = timestamps;
        Metrics = metrics;
        CapturedUtc = capturedUtc;
    }

    /// <summary>각 점의 커밋 시각(UTC ticks). 값 배열과 길이가 같다(§7.4).</summary>
    public long[] Timestamps { get; }

    public IReadOnlyList<SnapshotMetric> Metrics { get; }
    public DateTime CapturedUtc { get; }

    public int Count => Timestamps.Length;
    public bool IsEmpty => Count == 0;

    /// <summary>가장 오래된 점의 시각(로컬). 창 제목에 쓴다.</summary>
    public DateTime StartedLocal =>
        Count == 0 ? CapturedUtc.ToLocalTime() : new DateTime(Timestamps[0], DateTimeKind.Utc).ToLocalTime();

    public DateTime EndedLocal =>
        Count == 0 ? CapturedUtc.ToLocalTime() : new DateTime(Timestamps[^1], DateTimeKind.Utc).ToLocalTime();

    public TimeSpan Span => Count < 2 ? TimeSpan.Zero : TimeSpan.FromTicks(Timestamps[^1] - Timestamps[0]);

    /// <summary>
    /// 레지스트리에서 지금 가진 것을 전부 떠낸다. 살아 있는 장치의 슬롯만 담는다 —
    /// 회수 대기 중인 장치까지 담으면 창에 유령 카드가 생긴다.
    /// </summary>
    /// <param name="maxPoints">이보다 많으면 최신 쪽만. 0 이하면 제한하지 않는다.</param>
    public static MetricSnapshot Capture(MetricRegistry registry, int maxPoints = 0)
    {
        ArgumentNullException.ThrowIfNull(registry);

        int available = (int)Math.Min(registry.Frames, registry.SeriesCapacity);
        int count = maxPoints > 0 ? Math.Min(available, maxPoints) : available;
        var now = DateTime.UtcNow;
        if (count <= 0) return new MetricSnapshot([], [], now);

        var stamps = new long[count];
        int copied = registry.CopyTimestamps(stamps);
        if (copied < count)
        {
            // 떠내는 사이에 밀려났다. 가진 만큼으로 줄인다 — 길이가 어긋난 채로 두면
            // 값과 시각이 한 칸씩 어긋나 그림과 숫자가 다른 곳을 가리킨다.
            count = copied;
            stamps = stamps[..count];
        }
        if (count == 0) return new MetricSnapshot([], [], now);

        var metrics = new List<SnapshotMetric>();
        foreach (var device in registry.ActiveDevices)
        {
            foreach (var kind in device.Kinds.OrderBy(k => (int)k))
            {
                int slot = device.SlotOf(kind);
                if (registry.Series(slot) is not { } series) continue;

                var values = new float[count];
                var measured = new bool[count];
                int n = series.CopyLatest(values, measured);
                if (n < count)
                {
                    // 늦게 등록된 장치는 시리즈가 짧다. 앞쪽을 NaN 으로 비워 두면
                    // 끝이 맞으므로(§7.4) 시각과 그대로 대응한다.
                    Array.Copy(values, 0, values, count - n, n);
                    Array.Copy(measured, 0, measured, count - n, n);
                    for (int i = 0; i < count - n; i++) { values[i] = float.NaN; measured[i] = false; }
                }

                metrics.Add(new SnapshotMetric(device.Info, kind, values, measured));
            }
        }

        return new MetricSnapshot(stamps, metrics, now);
    }

    /// <summary>
    /// <paramref name="utcTicks"/> 이상인 첫 인덱스. 없으면 <see cref="Count"/>.
    /// 시각은 오름차순이므로 이진 탐색이다.
    /// </summary>
    public int IndexAtOrAfter(long utcTicks)
    {
        int lo = 0, hi = Count - 1, found = Count;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (Timestamps[mid] >= utcTicks) { found = mid; hi = mid - 1; }
            else lo = mid + 1;
        }
        return found;
    }
}

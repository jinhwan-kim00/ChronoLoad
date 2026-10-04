using ChronoLoad.Core.Devices;

namespace ChronoLoad.Core.Metrics;

/// <summary>장치가 지금 값을 낼 수 있는 상태인지.</summary>
public enum DeviceAvailability : byte
{
    Active = 0,

    /// <summary>
    /// 저전력 대기(D3). 값이 비는 것은 고장이 아니라 <b>의도적으로 깨우지 않기 때문</b>이다 (§6.3).
    /// 이걸 표시하지 않으면 "센서가 고장났나"와 구분할 수 없다.
    /// </summary>
    Standby = 1,
}

/// <summary>등록된 장치 하나와 그 지표 슬롯. 프로바이더는 초기화 때 이걸 받아두고 슬롯 번호로만 쓴다.</summary>
public sealed class DeviceHandle
{
    private readonly Dictionary<MetricKind, int> _slots;

    internal DeviceHandle(DeviceInfo info, byte index, Dictionary<MetricKind, int> slots)
    {
        Info = info;
        Index = index;
        _slots = slots;
    }

    public DeviceInfo Info { get; internal set; }
    public string Key => Info.Key;

    /// <summary>
    /// 지금 이 장치를 측정할 수 있는 상태인가. 프로바이더가 갱신한다.
    /// </summary>
    /// <remarks>
    /// <b>장치 집합 변경과는 다른 축이다.</b> 여기가 바뀌어도 <c>Revision</c> 은 올리지 않는다 —
    /// 카드를 다시 짓거나 에이전트에게 "구성이 바뀌었다"고 알릴 일이 아니다.
    /// 값이 비는 이유를 설명하기 위한 표시일 뿐이다.
    /// </remarks>
    public DeviceAvailability Availability { get; set; } = DeviceAvailability.Active;

    /// <summary>같은 <see cref="DeviceClass"/> 안에서의 인덱스. <see cref="MetricId"/>에 쓰인다.</summary>
    public byte Index { get; }

    public IReadOnlyCollection<MetricKind> Kinds => _slots.Keys;

    /// <summary>등록되지 않은 종류면 −1.</summary>
    public int SlotOf(MetricKind kind) => _slots.TryGetValue(kind, out int s) ? s : -1;

    /// <summary>
    /// 같은 종류의 값을 여러 개 쓰는 장치의 부가 슬롯. 지금은 CPU 코어별 사용률이 유일하다.
    /// </summary>
    /// <remarks>
    /// <see cref="MetricKind"/> 하나에 슬롯 하나라는 규칙을 깨지 않으려고 따로 둔다 — 코어를
    /// 지표 종류로 세면 <c>CpuCore0..63</c> 같은 열거가 생기고, 그 순간 MCP 의 지표 목록과
    /// 카드의 축 선택이 전부 코어 수에 휘둘린다. 채널은 <b>오버레이에서만</b> 읽힌다.
    /// </remarks>
    public IReadOnlyList<int> Channels { get; private set; } = [];

    internal void SetChannels(int[] channels) => Channels = channels;

    internal IEnumerable<int> Slots => _slots.Values.Concat(Channels);

    /// <summary>제거 예정 시각(UTC ticks). 살아 있으면 null.</summary>
    internal long? RetiredAtUtcTicks { get; set; }
}

/// <summary>
/// 장치별 지표 시리즈와 통계의 소유자. 장치 집합은 런타임에 바뀌므로
/// (§5.7 핫플러그) 추가·제거·재연결을 모두 이 안에서 처리한다.
/// </summary>
/// <remarks>
/// 슬롯 번호는 프로바이더가 샘플마다 쓰는 배열 인덱스다. 딕셔너리 조회 없이 쓰기 위해 존재하며,
/// 장치가 제거되면 유예 기간 뒤에 회수되어 재사용된다.
/// </remarks>
public sealed class MetricRegistry
{
    /// <summary>제거된 장치의 시리즈·통계를 붙잡아 두는 기본 유예. 껐다 켠 Wi-Fi가 통계를 잃지 않게 한다.</summary>
    public static readonly TimeSpan DefaultRetentionGrace = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly List<MetricSeries?> _series = [];
    private readonly List<int> _freeSlots = [];
    private readonly Dictionary<string, DeviceHandle> _devices = [];
    private readonly Dictionary<MetricId, int> _slotByMetric = [];

    // 통계는 스코프마다 한 벌씩. 시계열은 공유하고 "언제부터 세는가"만 나뉜다.
    private readonly StatsAccumulator[][] _stats =
        [new StatsAccumulator[16], new StatsAccumulator[16]];

    // 분위수는 평균·표준편차와 달리 누적식으로 계산할 수 없다. 구간이 링 안에 다 들어 있으면
    // 링의 표본을 직접 정렬하고(정확), 넘치면 상대 오차 스케치로 근사한다 — 구간이 24시간이 되어도
    // 비용과 메모리가 늘지 않는다.
    private readonly List<PercentileTracker?>[] _percentiles = [[], []];
    private int _revision;
    private int _configurationRevision;

    // 시간 축(§7.4). 값 링과 같은 길이지만 슬롯당이 아니라 프레임당 하나다 —
    // 모든 슬롯이 한 틱에 함께 커밋되므로 시각도 한 번만 있으면 된다(3600 × 8B = 28.8KB).
    private readonly long[] _stamps;
    private long _frames;

    public MetricRegistry(int seriesCapacity = 3600)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(seriesCapacity, 2);
        _capacity = seriesCapacity;
        _stamps = new long[seriesCapacity];
    }

    /// <summary>시리즈 하나가 보관하는 샘플 수. 250ms × 3600 = 15분.</summary>
    public int SeriesCapacity => _capacity;

    /// <summary>할당된 슬롯 총수(회수 대기 중인 것 포함). 샘플 버퍼 크기가 된다.</summary>
    public int SlotCount
    {
        get { lock (_gate) return _series.Count; }
    }

    /// <summary>
    /// 장치 집합이 바뀔 때마다 증가한다. MCP 응답의 <c>devicesRevision</c>이자,
    /// 샘플 엔진이 버퍼를 다시 잡아야 하는지 판단하는 신호다.
    /// </summary>
    public int Revision => Volatile.Read(ref _revision);

    /// <summary>
    /// 장치 <b>구성</b>이 바뀔 때만 증가한다 — 장치가 들고 나거나, 채널 수나 고정 정보가 바뀔 때.
    /// MCP 응답의 <c>devicesRevision</c> 이다(§10.3).
    /// </summary>
    /// <remarks>
    /// <see cref="Revision"/> 은 이름만 바뀌어도 오른다. 화면이 카드 이름을 갈아끼우는 신호라 그래야 한다 —
    /// Wi-Fi 링크 속도가 카드 이름에 들어 있다. 하지만 그 속도는 몇 초마다 바뀌어서, 그것을 MCP 에 그대로 내면
    /// 30초 검증 재열거 때마다 오르는 셈이 되어 "이 값만 비교하면 구성이 그대로인지 안다"가 거짓이 된다.
    /// </remarks>
    public int ConfigurationRevision => Volatile.Read(ref _configurationRevision);

    /// <summary>현재 살아 있는 장치들.</summary>
    public IReadOnlyList<DeviceHandle> ActiveDevices
    {
        get
        {
            lock (_gate)
                return _devices.Values.Where(d => d.RetiredAtUtcTicks is null).ToArray();
        }
    }

    public DeviceHandle? Find(string key)
    {
        lock (_gate) return _devices.GetValueOrDefault(key);
    }

    /// <summary>
    /// 장치를 등록한다. 같은 키가 유예 기간 안에 돌아오면 <b>기존 시리즈와 통계를 그대로 이어받는다</b> —
    /// Wi-Fi를 껐다 켜거나 드라이버가 재시작됐다고 벤치마크 구간이 날아가면 안 된다.
    /// </summary>
    public DeviceHandle Register(DeviceInfo info, ReadOnlySpan<MetricKind> kinds)
    {
        lock (_gate)
        {
            if (_devices.TryGetValue(info.Key, out var existing))
            {
                // 재열거는 장치가 안 바뀌어도 30초마다 돈다(§5.7). 같은 장치를 같은 내용으로
                // 다시 등록하는 것은 "장치 구성 변경"이 아니다 — 여기서 무조건 올리면
                // devicesRevision 이 30초마다 증가해서, 그 값만 비교하면 구성이 그대로인지
                // 알 수 있다는 §10.3 의 약속이 깨진다. 실제로 그렇게 깨져 있었다.
                bool returned = existing.RetiredAtUtcTicks is not null;
                bool described = existing.Info.HasSameDescription(info);
                bool configured = existing.Info.HasSameConfiguration(info);

                existing.RetiredAtUtcTicks = null;
                existing.Info = info;          // 이름·아이콘은 갱신, 데이터는 유지

                if (returned || !described) Volatile.Write(ref _revision, _revision + 1);
                if (returned || !configured) BumpConfiguration();
                return existing;
            }

            byte index = NextIndexFor(info.Class);
            var slots = new Dictionary<MetricKind, int>(kinds.Length);

            foreach (var kind in kinds)
            {
                if (kind.Class() != info.Class)
                    throw new ArgumentException(
                        $"{kind} 은(는) {kind.Class()} 지표인데 {info.Class} 장치에 등록하려 했다.", nameof(kinds));

                int slot = AllocateSlot();
                slots[kind] = slot;
                _slotByMetric[new MetricId(kind, index)] = slot;
            }

            var handle = new DeviceHandle(info, index, slots);
            _devices.Add(info.Key, handle);
            Volatile.Write(ref _revision, _revision + 1);
            BumpConfiguration();
            return handle;
        }
    }

    /// <summary>
    /// 장치에 채널 슬롯을 붙인다. 같은 수로 다시 부르면 아무것도 하지 않는다.
    /// </summary>
    /// <param name="kind">값의 종류. 단위가 같아야 하므로 코어 사용률은 <see cref="MetricKind.CpuTotal"/> 이다.</param>
    /// <remarks>
    /// 장치 등록과 분리한 이유는 채널을 붙이는 쪽이 장치를 등록한 쪽과 다르기 때문이다 —
    /// 총 사용률은 Fast 티어, 코어별은 Slow 티어라 프로바이더가 나뉜다(§6.1).
    /// </remarks>
    public IReadOnlyList<int> RegisterChannels(DeviceHandle device, MetricKind kind, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        lock (_gate)
        {
            if (device.Channels.Count == count) return device.Channels;

            foreach (int old in device.Channels) ReleaseSlot(old);

            var slots = new int[count];
            for (int i = 0; i < count; i++) slots[i] = AllocateSlot();

            device.SetChannels(slots);

            // 슬롯 수가 바뀌면 샘플 엔진이 버퍼를 다시 잡아야 한다.
            Volatile.Write(ref _revision, _revision + 1);
            BumpConfiguration();
            return slots;
        }
    }

    /// <summary>
    /// 장치가 사라졌다고 표시한다. 시리즈와 통계는 남겨두고 <see cref="PurgeRetired"/>에서 회수한다.
    /// </summary>
    public void Retire(string key, long nowUtcTicks)
    {
        lock (_gate)
        {
            if (_devices.TryGetValue(key, out var handle) && handle.RetiredAtUtcTicks is null)
            {
                handle.RetiredAtUtcTicks = nowUtcTicks;
                Volatile.Write(ref _revision, _revision + 1);
                BumpConfiguration();
            }
        }
    }

    // _gate 안에서만 부른다.
    private void BumpConfiguration() => Volatile.Write(ref _configurationRevision, _configurationRevision + 1);

    /// <summary>유예가 지난 장치의 슬롯을 회수한다. 회수한 장치 수를 돌려준다.</summary>
    public int PurgeRetired(long nowUtcTicks, TimeSpan? grace = null)
    {
        long graceTicks = (grace ?? DefaultRetentionGrace).Ticks;
        int purged = 0;

        lock (_gate)
        {
            foreach (var key in _devices.Keys.ToArray())
            {
                var handle = _devices[key];
                if (handle.RetiredAtUtcTicks is not { } at) continue;
                if (nowUtcTicks - at < graceTicks) continue;

                foreach (var kind in handle.Kinds.ToArray())
                {
                    int slot = handle.SlotOf(kind);
                    ReleaseSlot(slot);
                    _slotByMetric.Remove(new MetricId(kind, handle.Index));
                }

                // 채널도 같이 회수한다. 빠뜨리면 장치가 빠질 때마다 슬롯이 코어 수만큼 샌다.
                foreach (int slot in handle.Channels) ReleaseSlot(slot);
                handle.SetChannels([]);

                _devices.Remove(key);
                purged++;
            }

            // 구성 리비전은 올리지 않는다. 장치가 빠진 것은 은퇴 때 이미 알렸고, 회수는 슬롯 정리일 뿐이다.
            if (purged > 0) Volatile.Write(ref _revision, _revision + 1);
        }

        return purged;
    }

    public MetricSeries? Series(int slot)
    {
        lock (_gate) return (uint)slot < (uint)_series.Count ? _series[slot] : null;
    }

    public MetricSeries? Series(MetricId id)
    {
        lock (_gate)
            return _slotByMetric.TryGetValue(id, out int slot) ? _series[slot] : null;
    }

    /// <summary>
    /// 해당 스코프의 구간 통계 스냅샷. 스코프를 생략하면 화면 기준이다 —
    /// MCP 경로는 <see cref="StatsScope.Mcp"/>를 <b>명시적으로</b> 넘겨야 한다.
    /// </summary>
    /// <summary>
    /// 리셋 이후 구간의 분위수 하나. 표본이 없으면 NaN.
    /// </summary>
    /// <remarks>여러 분위수를 함께 볼 때는 <see cref="Summarize"/> 가 한 번에 구한다.</remarks>
    public double Quantile(int slot, double q, StatsScope scope = StatsScope.Ui)
    {
        var summary = Summarize(slot, scope, [q]);
        return summary.Quantiles.Length == 0 ? double.NaN : summary.Quantiles[0];
    }

    /// <summary>
    /// 리셋 이후 구간의 분위수와 문턱 이상 비율.
    /// </summary>
    /// <param name="quantiles">구할 분위수(0~1). 최근접 순위 정의다.</param>
    /// <param name="threshold">이 값 이상이었던 표본의 비율을 함께 구한다. null 이면 구하지 않는다.</param>
    /// <remarks>
    /// <para>
    /// <b>구간이 링 안에 다 들어 있으면 정확하다.</b> 리셋 이후 프레임이 링 길이(기본 15분)를
    /// 넘지 않았다면 링에 그 구간의 실측 표본이 전부 남아 있으므로 정렬해서 읽는다.
    /// "유휴 → 리셋 → 부하 → 통계" 처럼 몇 분 단위로 재는 흐름은 전부 여기로 온다.
    /// </para>
    /// <para>
    /// 넘치면 스케치로 근사한다(상대 오차 ±<see cref="PercentileTracker.RelativeAccuracy"/>, 관측 범위 안으로 자름).
    /// 어느 쪽이었는지는 <see cref="IntervalSummary.Exact"/> 로 알린다.
    /// </para>
    /// </remarks>
    public IntervalSummary Summarize(int slot, StatsScope scope, ReadOnlySpan<double> quantiles, double? threshold = null)
    {
        float[]? values = null;
        bool[]? measured = null;
        long expected;
        var sketchQuantiles = new double[quantiles.Length];
        double sketchFraction = double.NaN;

        lock (_gate)
        {
            var channel = _stats[(int)scope];
            if ((uint)slot >= (uint)_series.Count || _series[slot] is not { } series ||
                (uint)slot >= (uint)channel.Length || channel[slot].Count == 0)
                return IntervalSummary.Empty(quantiles.Length);

            expected = channel[slot].Count;
            // 리셋 이후 이 시리즈에 기록된 칸 수. 프레임 수와 다를 수 있다 — 장치가 등록된 뒤
            // 샘플 엔진이 버퍼를 다시 잡기까지 몇 프레임은 새 슬롯에 쓰지 않는다. 그 몇 칸 때문에
            // "프레임 수 ≤ 링 칸 수" 로만 판정하면 재기동 뒤 첫 구간이 내내 근사로 떨어졌다
            // (실사용 보고: 165초 구간인데 GPU·디스크만 quantilesExact=false). 시리즈가 리셋보다 늦게
            // 시작했다면 시리즈 전체가 그 구간이다.
            long span = Math.Min(_frames - channel[slot].ResetFrame, series.Written);

            if (span > 0 && span <= series.Count)
            {
                // 값만 락 안에서 떠낸다. 정렬은 락 밖에서 — 샘플 스레드를 붙잡지 않는다.
                values = new float[span];
                measured = new bool[span];
                series.CopyLatest(values, measured);
            }
            else
            {
                var tracker = PercentileOf(slot, (int)scope);
                for (int i = 0; i < quantiles.Length; i++)
                    sketchQuantiles[i] = tracker?.Quantile(quantiles[i]) ?? double.NaN;
                if (threshold is { } t) sketchFraction = tracker?.FractionAtOrAbove(t) ?? double.NaN;
            }
        }

        if (values is not null)
        {
            var samples = new List<float>(values.Length);
            for (int i = 0; i < values.Length; i++)
                if (measured![i] && float.IsFinite(values[i])) samples.Add(values[i]);

            // 통계 누산기와 표본 수가 맞아야 같은 구간이다. 어긋나면(리셋 직후의 경계 등)
            // 정확하다고 주장하지 않고 스케치로 돌아간다.
            if (samples.Count == expected)
            {
                samples.Sort();
                var exact = new double[quantiles.Length];
                for (int i = 0; i < quantiles.Length; i++)
                {
                    long rank = Math.Max(1, (long)Math.Ceiling(Math.Clamp(quantiles[i], 0, 1) * samples.Count));
                    exact[i] = samples[(int)rank - 1];
                }

                double fraction = double.NaN;
                if (threshold is { } t)
                {
                    int above = 0;
                    foreach (float v in samples) if (v >= t) above++;
                    fraction = (double)above / samples.Count;
                }

                return new IntervalSummary(exact, fraction, Exact: true);
            }

            lock (_gate)
            {
                var tracker = PercentileOf(slot, (int)scope);
                for (int i = 0; i < quantiles.Length; i++)
                    sketchQuantiles[i] = tracker?.Quantile(quantiles[i]) ?? double.NaN;
                if (threshold is { } t) sketchFraction = tracker?.FractionAtOrAbove(t) ?? double.NaN;
            }
        }

        return new IntervalSummary(sketchQuantiles, sketchFraction, Exact: false);
    }

    /// <summary>슬롯의 분위수 스케치. 처음 쓸 때 만든다.</summary>
    private PercentileTracker? PercentileOf(int slot, int scope)
    {
        var channel = _percentiles[scope];
        while (channel.Count <= slot) channel.Add(null);

        if (channel[slot] is { } existing) return existing;
        if (_series[slot] is null) return null;

        return channel[slot] = new PercentileTracker();
    }

    public StatsAccumulator StatsSnapshot(int slot, StatsScope scope = StatsScope.Ui)
    {
        lock (_gate)
        {
            var channel = _stats[(int)scope];
            return (uint)slot < (uint)channel.Length ? channel[slot] : default;
        }
    }

    /// <summary>
    /// 한 스코프의 전 지표 통계를 리셋한다. 접힌 카드와 표시하지 않는 지표까지 전부 포함하되,
    /// <b>다른 스코프는 건드리지 않는다</b>.
    /// </summary>
    public void ResetAllStats(StatsScope scope, long nowUtcTicks)
    {
        lock (_gate)
        {
            var channel = _stats[(int)scope];
            for (int i = 0; i < _series.Count; i++)
                if (_series[i] is not null)
                {
                    channel[i].Reset(nowUtcTicks);
                    channel[i].ResetFrame = _frames;
                    PercentileOf(i, (int)scope)?.Reset();
                }
        }
    }

    public void ResetStats(int slot, StatsScope scope, long nowUtcTicks)
    {
        lock (_gate)
        {
            var channel = _stats[(int)scope];
            if ((uint)slot >= (uint)channel.Length) return;

            channel[slot].Reset(nowUtcTicks);
            channel[slot].ResetFrame = _frames;
            PercentileOf(slot, (int)scope)?.Reset();
        }
    }

    /// <summary>샘플 엔진이 락 없이 쓰기 위해 잡아두는 스냅샷.</summary>
    internal (MetricSeries?[] Series, int Count) SnapshotSeries()
    {
        lock (_gate) return (_series.ToArray(), _series.Count);
    }

    /// <summary>
    /// 외부 소스가 한 프레임을 밀어 넣는다. 리플레이·데모·골든 이미지 렌더 테스트처럼
    /// 센서 없이 파이프라인을 구동해야 하는 경우에 쓴다.
    /// </summary>
    public void PushFrame(ReadOnlySpan<float> values) => PushFrame(values, DateTime.UtcNow.Ticks);

    /// <inheritdoc cref="PushFrame(ReadOnlySpan{float})"/>
    /// <param name="nowUtcTicks">이 프레임의 커밋 시각. 시간 축(§7.4)에 그대로 들어간다.</param>
    public void PushFrame(ReadOnlySpan<float> values, long nowUtcTicks) =>
        CommitAll(values, ReadOnlySpan<bool>.Empty, nowUtcTicks);

    /// <summary>모든 슬롯을 실측값으로 커밋한다(테스트·단순 경로용).</summary>
    internal void CommitAll(ReadOnlySpan<float> values) =>
        CommitAll(values, ReadOnlySpan<bool>.Empty, DateTime.UtcNow.Ticks);

    /// <inheritdoc cref="CommitAll(ReadOnlySpan{float}, ReadOnlySpan{bool}, long)"/>
    internal void CommitAll(ReadOnlySpan<float> values, ReadOnlySpan<bool> measured) =>
        CommitAll(values, measured, DateTime.UtcNow.Ticks);

    /// <summary>
    /// 한 틱의 값을 커밋한다. 시리즈에는 <b>전부</b> 기록해 시간 축을 맞추고,
    /// 통계에는 <paramref name="measured"/>가 true인 슬롯만 반영한다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Slow 티어 지표는 Fast 틱마다 직전 값이 다시 기록된다. 그 반복을 통계에 넣으면
    /// 샘플 수가 주기 비율만큼 부풀고, 편차가 0인 반복값이 표준편차를 끌어내려 실제보다 안정적으로 보인다.
    /// </para>
    /// <para>
    /// <b>이 메서드가 시간 축을 한 칸 밀어내는 유일한 곳이다</b>(§7.4). 슬롯 하나만 따로 기록하는
    /// 경로를 두면 시리즈마다 프레임 수가 어긋나 인덱스↔시각 대응이 조용히 깨진다.
    /// </para>
    /// </remarks>
    internal void CommitAll(ReadOnlySpan<float> values, ReadOnlySpan<bool> measured, long nowUtcTicks)
    {
        lock (_gate)
        {
            int n = Math.Min(values.Length, _series.Count);
            for (int i = 0; i < n; i++)
            {
                var series = _series[i];
                if (series is null) continue;

                bool isMeasured = measured.IsEmpty || (i < measured.Length && measured[i]);
                series.Write(values[i], isMeasured);
                if (!isMeasured) continue;

                // 모든 스코프가 같은 표본을 받는다. 다른 것은 리셋 시점뿐이다.
                for (int scope = 0; scope < StatsScopes.Count; scope++) PercentileOf(i, scope)?.Add(values[i]);

                for (int scope = 0; scope < StatsScopes.Count; scope++)
                    _stats[scope][i].Add(values[i]);
            }

            // 값을 다 쓴 뒤에 시각을 공개한다. 프레임 수가 먼저 늘면 소비자가
            // 아직 채우지 않은 칸을 읽는다.
            _stamps[(int)(_frames % _capacity)] = nowUtcTicks;
            Volatile.Write(ref _frames, _frames + 1);
        }
    }

    // ── 시간 축 (§7.4) ────────────────────────────────────────────────
    //
    // 시리즈는 값만 담고 차트는 점 간격이 일정하다고 가정해 그린다. 그런데 적응형
    // 백오프(§6.3)가 Fast 주기를 배수로 늘리므로 실제 간격은 균일하지 않다. 프레임마다
    // 커밋 시각을 남겨 두면 "이 점이 언제인가"를 되물을 수 있다.
    //
    // 인덱스↔프레임 대응은 **시리즈가 첫 기록 이후 매 프레임 한 번씩 기록된다**는
    // 불변식에 기댄다. CommitAll 이 유일한 기록 경로이고 슬롯은 회수돼도 목록에서
    // 빠지지 않으므로(ReleaseSlot 은 null 로만 둔다) 살아 있는 슬롯이 프레임을
    // 건너뛰는 일은 없다. 늦게 등록된 시리즈는 Count 가 작을 뿐 끝이 같다.

    /// <summary>커밋된 프레임 총수. 시리즈 인덱스와 시각을 잇는 좌표계다.</summary>
    public long Frames => Volatile.Read(ref _frames);

    /// <summary>가장 최근 프레임의 커밋 시각(UTC ticks). 아직 한 프레임도 없으면 null.</summary>
    public long? LatestTimestamp => TimestampAtFrame(Volatile.Read(ref _frames) - 1);

    /// <summary>
    /// 프레임 번호의 커밋 시각(UTC ticks). 아직 오지 않았거나 링에서 밀려났으면 null.
    /// </summary>
    public long? TimestampAtFrame(long frame)
    {
        long frames = Volatile.Read(ref _frames);
        if (frame < 0 || frame >= frames || frames - frame > _capacity) return null;
        return _stamps[(int)(frame % _capacity)];
    }

    /// <summary>
    /// 시리즈의 <paramref name="index"/>번째 샘플(0 = 가장 오래된 유효 샘플)이 커밋된 프레임.
    /// 범위를 벗어나면 −1.
    /// </summary>
    public long FrameAt(MetricSeries series, int index)
    {
        ArgumentNullException.ThrowIfNull(series);
        int count = series.Count;
        if ((uint)index >= (uint)count) return -1;
        return Volatile.Read(ref _frames) - count + index;
    }

    /// <summary>
    /// 시리즈의 <paramref name="index"/>번째 샘플이 커밋된 시각(UTC ticks). 없으면 null.
    /// </summary>
    public long? TimestampAt(MetricSeries series, int index)
    {
        long frame = FrameAt(series, index);
        return frame < 0 ? null : TimestampAtFrame(frame);
    }

    /// <summary>
    /// 가장 최근 <paramref name="destination"/>.Length 개 프레임의 커밋 시각을
    /// 시간 순(오래된 것 먼저)으로 복사하고 실제 복사한 개수를 돌려준다.
    /// </summary>
    /// <remarks>
    /// <see cref="MetricSeries.CopyLatest(Span{float})"/>와 같은 규약이다. 값과 시각을
    /// 같은 길이로 떠내면 인덱스가 그대로 대응한다 — 스냅샷 복제와 CSV 내보내기가 이것을 쓴다.
    /// </remarks>
    public int CopyTimestamps(Span<long> destination)
    {
        if (destination.IsEmpty) return 0;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            long before = Volatile.Read(ref _frames);
            int available = (int)Math.Min(before, _capacity);
            int n = Math.Min(available, destination.Length);
            if (n == 0) return 0;

            long start = before - n;
            for (int i = 0; i < n; i++)
                destination[i] = _stamps[(int)((start + i) % _capacity)];

            // 생산자가 우리가 읽은 가장 오래된 칸을 아직 덮어쓰지 않았으면 일관된 스냅샷이다.
            if (Volatile.Read(ref _frames) - start <= _capacity) return n;
        }

        destination[0] = _stamps[(int)((Volatile.Read(ref _frames) - 1) % _capacity)];
        return 1;
    }

    /// <summary>
    /// 가장 최근 프레임에서 <paramref name="span"/>만큼 거슬러 올라갈 때 들어오는 프레임 수.
    /// 시간 폭(§9.4)을 점 개수로 환산한다.
    /// </summary>
    /// <remarks>
    /// 점 개수로 세면 적응형 백오프(§6.3) 구간에서 "60초"가 60초가 아니게 된다.
    /// 시각으로 잘라야 화면이 늘 같은 길이의 시간을 보여 준다.
    /// <para>
    /// 절전으로 끊긴 구간(§13)이 있으면 그 이전 샘플은 자연히 빠진다 — 복귀 직후에는
    /// 점이 몇 개뿐이겠지만, <b>그것이 실제로 가진 전부</b>다.
    /// </para>
    /// </remarks>
    public int PointsWithin(TimeSpan span)
    {
        long frames = Volatile.Read(ref _frames);
        int available = (int)Math.Min(frames, _capacity);
        if (available == 0) return 0;
        if (span <= TimeSpan.Zero) return 1;

        long Stamp(int i) => _stamps[(int)((frames - available + i) % _capacity)];

        long cutoff = Stamp(available - 1) - span.Ticks;
        int lo = 0, hi = available - 1, first = available - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (Stamp(mid) >= cutoff) { first = mid; hi = mid - 1; }
            else lo = mid + 1;
        }

        return available - first;
    }

    /// <summary>슬롯 하나를 비우고 재사용 대기열에 넣는다. 시리즈·통계·분위수를 모두 지운다.</summary>
    private void ReleaseSlot(int slot)
    {
        if ((uint)slot >= (uint)_series.Count) return;

        _series[slot] = null;
        for (int scope = 0; scope < StatsScopes.Count; scope++)
        {
            _stats[scope][slot] = default;
            if (slot < _percentiles[scope].Count) _percentiles[scope][slot] = null;
        }

        _freeSlots.Add(slot);
    }

    private int AllocateSlot()
    {
        int slot;
        if (_freeSlots.Count > 0)
        {
            slot = _freeSlots[^1];
            _freeSlots.RemoveAt(_freeSlots.Count - 1);
            _series[slot] = new MetricSeries(_capacity);
        }
        else
        {
            slot = _series.Count;
            _series.Add(new MetricSeries(_capacity));
        }

        long now = DateTime.UtcNow.Ticks;
        for (int scope = 0; scope < StatsScopes.Count; scope++)
        {
            if (slot >= _stats[scope].Length)
            {
                var grown = _stats[scope];
                Array.Resize(ref grown, Math.Max(grown.Length * 2, slot + 1));
                _stats[scope] = grown;
            }

            _stats[scope][slot] = StatsAccumulator.Create(now);
            _stats[scope][slot].ResetFrame = _frames;
        }

        return slot;
    }

    private byte NextIndexFor(DeviceClass cls)
    {
        var used = _devices.Values.Where(d => d.Info.Class == cls).Select(d => d.Index).ToHashSet();
        for (int i = 0; i < byte.MaxValue; i++)
            if (!used.Contains((byte)i)) return (byte)i;

        throw new InvalidOperationException($"{cls} 장치 인덱스가 고갈됐다.");
    }
}

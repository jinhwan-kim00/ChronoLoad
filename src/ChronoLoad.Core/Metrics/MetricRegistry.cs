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

    internal IEnumerable<int> Slots => _slots.Values;

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

    // 분위수는 평균·표준편차와 달리 누적식으로 계산할 수 없다. 표본을 다 들고 있을 수도 없으므로
    // 고정 버킷 히스토그램으로 근사한다 — 구간이 24시간이 되어도 비용과 메모리가 늘지 않는다.
    private readonly List<PercentileTracker?>[] _percentiles = [[], []];
    private int _revision;

    public MetricRegistry(int seriesCapacity = 3600)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(seriesCapacity, 2);
        _capacity = seriesCapacity;
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

                existing.RetiredAtUtcTicks = null;
                existing.Info = info;          // 이름·아이콘은 갱신, 데이터는 유지

                if (returned || !described) Volatile.Write(ref _revision, _revision + 1);
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
            return handle;
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
            }
        }
    }

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
                    _series[slot] = null;
                    for (int scope = 0; scope < StatsScopes.Count; scope++)
                    {
                        _stats[scope][slot] = default;
                        if (slot < _percentiles[scope].Count) _percentiles[scope][slot] = null;
                    }
                    _freeSlots.Add(slot);
                    _slotByMetric.Remove(new MetricId(kind, handle.Index));
                }

                _devices.Remove(key);
                purged++;
            }

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
    /// 리셋 이후 구간의 분위수. 표본이 없으면 NaN.
    /// </summary>
    /// <remarks>
    /// 히스토그램 근사라 버킷 해상도만큼의 오차가 있다(백분율 계열 ±0.5%p).
    /// 24시간을 누적해도 메모리가 고정이라는 성질과의 교환이다.
    /// </remarks>
    public double Quantile(int slot, double q, StatsScope scope = StatsScope.Ui)
    {
        lock (_gate)
        {
            var tracker = PercentileOf(slot, (int)scope);
            return tracker is null || tracker.Count == 0 ? double.NaN : tracker.Quantile(q);
        }
    }

    /// <summary>
    /// 슬롯의 분위수 추적기. 지표 단위에 맞는 버킷 배치를 골라 처음 쓸 때 만든다 —
    /// 사용률(0~100)과 바이트(수십 GiB)를 같은 눈금으로 재면 둘 다 쓸모없어진다.
    /// </summary>
    private PercentileTracker? PercentileOf(int slot, int scope)
    {
        var channel = _percentiles[scope];
        while (channel.Count <= slot) channel.Add(null);

        if (channel[slot] is { } existing) return existing;
        if (_series[slot] is null) return null;

        var kind = _slotByMetric.FirstOrDefault(p => p.Value == slot).Key.Kind;
        var tracker = kind.Unit() switch
        {
            MetricUnit.Percent => PercentileTracker.ForPercent(),
            MetricUnit.Bytes or MetricUnit.ByteRate or MetricUnit.BitRate => PercentileTracker.ForBytes(),
            MetricUnit.Celsius => PercentileTracker.ForRange(0, 150),
            MetricUnit.Watt => PercentileTracker.ForRange(0, 1000),
            MetricUnit.Megahertz => PercentileTracker.ForRange(0, 10_000),
            MetricUnit.Milliseconds => PercentileTracker.ForRange(0, 1000),
            _ => PercentileTracker.ForRange(0, 1_000_000),
        };

        channel[slot] = tracker;
        return tracker;
    }

    public StatsAccumulator StatsSnapshot(int slot, StatsScope scope = StatsScope.Ui)
    {
        lock (_gate)
        {
            var channel = _stats[(int)scope];
            return (uint)slot < (uint)channel.Length ? channel[slot] : default;
        }
    }

    /// <summary>슬롯 하나에 값을 커밋한다(시리즈 기록 + 전 스코프 통계 누적). 샘플 엔진 전용.</summary>
    internal void Commit(int slot, float value, bool measured = true)
    {
        var series = _series[slot];
        if (series is null) return;      // 회수된 슬롯
        series.Write(value, measured);
        if (!measured) return;
        for (int scope = 0; scope < StatsScopes.Count; scope++)
        {
            _stats[scope][slot].Add(value);
            PercentileOf(slot, scope)?.Add(value);
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
    public void PushFrame(ReadOnlySpan<float> values) => CommitAll(values, ReadOnlySpan<bool>.Empty);

    /// <summary>모든 슬롯을 실측값으로 커밋한다(테스트·단순 경로용).</summary>
    internal void CommitAll(ReadOnlySpan<float> values) => CommitAll(values, ReadOnlySpan<bool>.Empty);

    /// <summary>
    /// 한 틱의 값을 커밋한다. 시리즈에는 <b>전부</b> 기록해 시간 축을 맞추고,
    /// 통계에는 <paramref name="measured"/>가 true인 슬롯만 반영한다.
    /// </summary>
    /// <remarks>
    /// Slow 티어 지표는 Fast 틱마다 직전 값이 다시 기록된다. 그 반복을 통계에 넣으면
    /// 샘플 수가 주기 비율만큼 부풀고, 편차가 0인 반복값이 표준편차를 끌어내려 실제보다 안정적으로 보인다.
    /// </remarks>
    internal void CommitAll(ReadOnlySpan<float> values, ReadOnlySpan<bool> measured)
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
        }
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

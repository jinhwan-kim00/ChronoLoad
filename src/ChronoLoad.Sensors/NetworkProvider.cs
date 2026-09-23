using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Metrics;
using ChronoLoad.Core.Sensors;
using ChronoLoad.Sensors.Native;

namespace ChronoLoad.Sensors;

/// <summary>
/// 네트워크 인터페이스별 수신·송신 처리량.
/// </summary>
/// <remarks>
/// <para>
/// 인터페이스 하나당 프로바이더 하나가 아니라 <b>한 프로바이더가 전체 인터페이스를 맡는다</b>.
/// <c>GetIfTable2</c>가 호출 한 번으로 전 인터페이스를 돌려주기 때문에, 장치마다 프로바이더를 두면
/// 같은 테이블을 인터페이스 수만큼 다시 읽게 된다.
/// </para>
/// <para>
/// <b>VPN·터널은 카드로 만들지 않는다.</b> 터널을 지나는 바이트는 하위 물리 NIC에도 그대로 계상되므로
/// 같은 트래픽이 두 카드에 나타나고, "지금 회선을 얼마나 쓰고 있나"에 틀린 답을 주게 된다(설계서 §5.3).
/// </para>
/// </remarks>
public sealed class NetworkProvider : ISensorProvider
{
    /// <summary>이 시간 동안 누적 옥텟이 한 바이트도 변하지 않으면 숨긴다. 가상 어댑터 대부분이 여기서 걸러진다.</summary>
    public static readonly TimeSpan IdleHideAfter = TimeSpan.FromMinutes(5);

    private readonly List<IfTable.Counters> _counters = [];
    private readonly Dictionary<ulong, Interface> _interfaces = [];
    private MetricRegistry? _registry;
    // 장치 이벤트 스레드가 쓰고 샘플링 스레드가 읽는다.
    private volatile bool _enumeratePending;
    private bool _showTunnels;

    public NetworkProvider(bool showTunnels = false) => _showTunnels = showTunnels;

    public string Id => "net";
    public SensorTier Tier => SensorTier.Fast;
    public bool IsAvailable { get; private set; }

    /// <summary>설정에서 켤 수 있지만 기본은 꺼짐. 켜면 이중 계상 주의가 함께 표시된다.</summary>
    public bool ShowTunnels
    {
        get => _showTunnels;
        set { _showTunnels = value; if (_registry is not null) Enumerate(_registry); }
    }

    public ValueTask InitializeAsync(MetricRegistry registry, CancellationToken cancellationToken)
    {
        _registry = registry;
        Enumerate(registry);
        IsAvailable = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>장치 집합을 다시 읽는다. M5의 <c>DeviceWatcher</c>가 인터페이스 변경 때 호출한다.</summary>
    public void Enumerate(MetricRegistry registry)
    {
        var seen = new HashSet<ulong>();

        foreach (var row in IfTable.Enumerate())
        {
            if (!ShouldShow(row)) continue;
            seen.Add(row.InterfaceLuid);

            if (_interfaces.TryGetValue(row.InterfaceLuid, out var existing))
            {
                existing.Handle = registry.Register(BuildInfo(row), [MetricKind.NetRx, MetricKind.NetTx]);
                continue;
            }

            var handle = registry.Register(BuildInfo(row), [MetricKind.NetRx, MetricKind.NetTx]);
            _interfaces[row.InterfaceLuid] = new Interface
            {
                Luid = row.InterfaceLuid,
                Handle = handle,
                RxSlot = handle.SlotOf(MetricKind.NetRx),
                TxSlot = handle.SlotOf(MetricKind.NetTx),
                LastChangeUtcTicks = DateTime.UtcNow.Ticks,
            };
        }

        // 사라진 인터페이스는 폐기하지 않고 은퇴시킨다 — 유예 안에 돌아오면 통계가 이어진다.
        foreach (var luid in _interfaces.Keys.ToArray())
        {
            if (seen.Contains(luid)) continue;
            registry.Retire(_interfaces[luid].Handle.Key, DateTime.UtcNow.Ticks);
            _interfaces.Remove(luid);
        }
    }

    private bool ShouldShow(in MibIfRow2 row)
    {
        if (row.Type == IfType.SoftwareLoopback) return false;
        if (row.OperStatus != IfOperStatus.Up) return false;
        if (row.IsNotMediaConnected) return false;

        // NDIS 필터 계층 인스턴스는 같은 물리 NIC 를 필터 수만큼 중복으로 노출한다.
        // 실기기에서 Realtek 2.5GbE 하나가 WFP·QoS·802.3 세 벌로 잡히는 것을 확인했다.
        if (row.IsFilterInterface) return false;

        if (IsTunnel(row)) return _showTunnels;

        // WAN Miniport 같은 가상 어댑터도 Type 은 Ethernet 이다. 하드웨어 플래그로 가른다.
        return row.IsHardwareInterface;
    }

    private static bool IsTunnel(in MibIfRow2 row) =>
        row.Type is IfType.Tunnel or IfType.Ppp || row.IsEndPointInterface;

    private static DeviceInfo BuildInfo(in MibIfRow2 row)
    {
        var icon = row.Type switch
        {
            IfType.Ieee80211 => IconKind.NetWiFi,
            IfType.Ethernet => IconKind.NetEthernet,
            IfType.WwanPp or IfType.WwanPp2 => IconKind.NetCellular,
            IfType.Tunnel or IfType.Ppp => IconKind.NetTunnel,
            _ => IconKind.NetGeneric,
        };

        string speed = FormatLinkSpeed(row.ReceiveLinkSpeed);
        string alias = string.IsNullOrWhiteSpace(row.Alias) ? row.Description : row.Alias;

        return new DeviceInfo(
            Key: $"net:{row.InterfaceGuid:B}",
            Class: DeviceClass.Network,
            ShortName: speed.Length > 0 ? $"{alias} {speed}" : alias,
            FullName: $"{row.Description} · {alias}{(speed.Length > 0 ? " · " + speed : "")} · 연결됨",
            Icon: icon)
        {
            Extra = new Dictionary<string, string>
            {
                ["interfaceIndex"] = row.InterfaceIndex.ToString(),
                ["linkSpeedBitsPerSecond"] = row.ReceiveLinkSpeed.ToString(),
                ["type"] = row.Type.ToString(),
            },
        };
    }

    /// <summary>
    /// 협상된 링크 속도. 카드 이름에 붙어 같은 종류의 인터페이스를 구분하는 데 쓰인다.
    /// </summary>
    /// <remarks>
    /// <b><c>bps</c> 까지 적는다.</b> <c>G</c> 만 붙이면 Wi-Fi 에서 <c>2.4G</c> 가 나오는데,
    /// 이것은 2.4Gbps 링크 속도인데도 2.4GHz 밴드로 읽힌다. 실제로 그렇게 읽혔다 —
    /// 그 기기의 실제 밴드는 5GHz 였다.
    /// </remarks>
    private static string FormatLinkSpeed(ulong bitsPerSecond) => bitsPerSecond switch
    {
        0 or ulong.MaxValue => string.Empty,
        >= 1_000_000_000 => $"{bitsPerSecond / 1e9:0.#}Gbps",
        >= 1_000_000 => $"{bitsPerSecond / 1e6:0}Mbps",
        _ => $"{bitsPerSecond / 1e3:0}Kbps",
    };

    /// <summary>Wi-Fi 라디오 on/off·어댑터 착탈 시 다음 샘플에서 다시 열거하도록 표시한다.</summary>
    public void RequestEnumerate() => _enumeratePending = true;

    public void Sample(in SampleWriter writer)
    {
        if (!IsAvailable) return;

        if (_enumeratePending && _registry is not null)
        {
            _enumeratePending = false;
            Enumerate(_registry);
        }

        IfTable.ReadCounters(_counters);
        long now = writer.TimestampUtcTicks;

        foreach (var counter in _counters)
        {
            if (!_interfaces.TryGetValue(counter.Luid, out var state)) continue;

            if (counter.OperStatus != IfOperStatus.Up)
            {
                // 링크가 내려간 것은 제거가 아니다. 카드는 남기고 값만 공백으로 둔다.
                writer.WriteUnavailable(state.RxSlot);
                writer.WriteUnavailable(state.TxSlot);
                state.HasBaseline = false;
                continue;
            }

            if (!state.HasBaseline)
            {
                state.Snapshot(counter, now);
                writer.WriteUnavailable(state.RxSlot);
                writer.WriteUnavailable(state.TxSlot);
                continue;
            }

            double elapsed = Math.Max(1e-6, (now - state.LastSampleUtcTicks) / (double)TimeSpan.TicksPerSecond);
            float rx = (float)(Delta(state.LastIn, counter.InOctets) / elapsed);
            float tx = (float)(Delta(state.LastOut, counter.OutOctets) / elapsed);

            writer.Write(state.RxSlot, rx);
            writer.Write(state.TxSlot, tx);

            if (counter.InOctets != state.LastIn || counter.OutOctets != state.LastOut)
                state.LastChangeUtcTicks = now;

            state.Snapshot(counter, now);
        }
    }

    /// <summary>
    /// 32bit 카운터를 쓰는 드라이버가 남아 있어 감소가 관측될 수 있다.
    /// 랩어라운드를 음수 처리량으로 흘리면 차트가 뒤집히므로 0으로 접는다.
    /// </summary>
    private static double Delta(ulong previous, ulong current) =>
        current >= previous ? current - previous : 0;

    /// <summary>5분간 한 바이트도 오가지 않은 인터페이스. 카드에서 감춘다.</summary>
    public IEnumerable<string> IdleInterfaceKeys(long nowUtcTicks) =>
        _interfaces.Values
            .Where(i => nowUtcTicks - i.LastChangeUtcTicks > IdleHideAfter.Ticks)
            .Select(i => i.Handle.Key);

    public void Dispose()
    {
        _interfaces.Clear();
        IsAvailable = false;
    }

    private sealed class Interface
    {
        public ulong Luid { get; init; }
        public required DeviceHandle Handle { get; set; }
        public int RxSlot { get; init; }
        public int TxSlot { get; init; }
        public ulong LastIn { get; private set; }
        public ulong LastOut { get; private set; }
        public long LastSampleUtcTicks { get; private set; }
        public long LastChangeUtcTicks { get; set; }
        public bool HasBaseline { get; set; }

        public void Snapshot(in IfTable.Counters counters, long nowUtcTicks)
        {
            LastIn = counters.InOctets;
            LastOut = counters.OutOctets;
            LastSampleUtcTicks = nowUtcTicks;
            HasBaseline = true;
        }
    }
}

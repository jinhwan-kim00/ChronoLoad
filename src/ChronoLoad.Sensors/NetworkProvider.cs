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
/// <b>카운터는 샘플링 스레드가 아니라 전용 스레드가 읽는다.</b> 감시 대상이 CPU 를 다 쓰면 인터페이스
/// 카운터 호출이 막힌다 — 전 코어를 채운 실측에서 이 프로바이더 한 번이 평균 52ms, 최대 1.2초 걸렸고
/// 그동안 CPU·GPU·디스크까지 모든 틱이 멈췄다(나머지 프로바이더는 같은 조건에서 2ms 안쪽).
/// 읽기 스레드는 <b>자기가 읽은 시각</b>으로 처리량을 계산해 두고, 샘플러는 새 값이 있을 때만 가져간다.
/// 막히면 네트워크 값만 늦게 오고, 늦게 와도 처리량은 실제 간격으로 나눈 값이다.
/// </para>
/// <para>
/// <b>VPN·터널은 카드로 만들지 않는다.</b> 터널을 지나는 바이트는 하위 물리 NIC에도 그대로 계상되므로
/// 같은 트래픽이 두 카드에 나타나고, "지금 회선을 얼마나 쓰고 있나"에 틀린 답을 주게 된다(설계서 §5.3).
/// </para>
/// </remarks>
public sealed class NetworkProvider : ISensorProvider
{
    private readonly Dictionary<ulong, Interface> _interfaces = [];

    /// <summary>
    /// 샘플러가 틱마다 다음 읽기를 청한다. 읽기 스레드가 제 타이머를 두지 않으므로 창을 최소화해 주기가
    /// 1초로 늘면 읽기도 1초로 준다 — 따로 250ms 로 돌면 아무도 안 보는 동안 네 배로 깨어난다.
    /// </summary>
    private readonly AutoResetEvent _demand = new(true);

    // 읽기 스레드가 쓰고 샘플러가 가져간다.
    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, Reading> _latest = [];
    private volatile ulong[] _watched = [];
    private readonly CancellationTokenSource _stop = new();
    private Thread? _reader;

    /// <summary>
    /// 읽기 스레드가 구한 한 번의 결과. <paramref name="Up"/> 이 거짓이면 링크가 내려간 것이다.
    /// 처리량이 NaN 이면 기준선을 잡은 첫 읽기라 아직 모른다.
    /// </summary>
    private readonly record struct Reading(float Rx, float Tx, bool Up, long Sequence);
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

        _reader = new Thread(ReadLoop)
        {
            Name = "ChronoLoad network counters",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _reader.Start();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 샘플러가 청할 때마다 감시하는 인터페이스의 카운터를 읽어 처리량을 구해 둔다. 시각은 <b>읽은 순간</b>의 것을 쓴다 —
    /// 호출이 막혔다 풀렸을 때 틱 시각으로 나누면 막힌 시간만큼 처리량이 부풀거나 줄어든다.
    /// </summary>
    private void ReadLoop()
    {
        var baselines = new Dictionary<ulong, (ulong In, ulong Out, long Stamp)>();
        nint buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(IfTable.EntrySize);
        long sequence = 0;
        var token = _stop.Token;
        WaitHandle[] wake = [token.WaitHandle, _demand];

        try
        {
            // 막혀 있던 동안 쌓인 요청은 AutoResetEvent 가 하나로 접는다 — 풀리면 한 번만 읽는다.
            while (WaitHandle.WaitAny(wake) == 1)
            {
                var watched = _watched;

                foreach (ulong luid in watched)
                {
                    if (!IfTable.ReadEntry(luid, buffer, out var counters)) continue;
                    long stamp = System.Diagnostics.Stopwatch.GetTimestamp();
                    sequence++;

                    Reading reading;
                    if (counters.OperStatus != IfOperStatus.Up)
                    {
                        baselines.Remove(luid);
                        reading = new Reading(float.NaN, float.NaN, Up: false, sequence);
                    }
                    else if (baselines.TryGetValue(luid, out var previous) && stamp > previous.Stamp)
                    {
                        double seconds = (stamp - previous.Stamp) / (double)System.Diagnostics.Stopwatch.Frequency;
                        reading = new Reading(
                            (float)(Delta(previous.In, counters.InOctets) / seconds),
                            (float)(Delta(previous.Out, counters.OutOctets) / seconds),
                            Up: true, sequence);
                    }
                    else
                    {
                        reading = new Reading(float.NaN, float.NaN, Up: true, sequence);
                    }

                    baselines[luid] = (counters.InOctets, counters.OutOctets, stamp);
                    lock (_gate) _latest[luid] = reading;
                }
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
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
            };
        }

        // 사라진 인터페이스는 폐기하지 않고 은퇴시킨다 — 유예 안에 돌아오면 통계가 이어진다.
        foreach (var luid in _interfaces.Keys.ToArray())
        {
            if (seen.Contains(luid)) continue;
            registry.Retire(_interfaces[luid].Handle.Key, DateTime.UtcNow.Ticks);
            _interfaces.Remove(luid);
        }

        // 읽기 스레드는 이 목록만 본다. 배열을 통째로 갈아 끼워 도중에 바뀌는 일이 없게 한다.
        _watched = [.. _interfaces.Keys];
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
            // Wi-Fi 는 링크 속도를 몇 초마다 재협상한다. 이름에도 들어가지만 구성 변경은 아니다(§10.3).
            LiveExtraKeys = ["linkSpeedBitsPerSecond"],
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

        foreach (var state in _interfaces.Values)
        {
            Reading reading;
            lock (_gate)
                if (!_latest.TryGetValue(state.Luid, out reading)) continue;

            // 새 결과가 없으면 쓰지 않는다 — 직전 값이 유지되고 실측으로 세지 않는다.
            if (reading.Sequence == state.ConsumedSequence) continue;
            state.ConsumedSequence = reading.Sequence;

            if (!reading.Up || float.IsNaN(reading.Rx))
            {
                // 링크가 내려갔거나 기준선뿐이다. 링크가 내려간 것은 제거가 아니므로 카드는 남기고 값만 비운다.
                writer.WriteUnavailable(state.RxSlot);
                writer.WriteUnavailable(state.TxSlot);
                continue;
            }

            writer.Write(state.RxSlot, reading.Rx);
            writer.Write(state.TxSlot, reading.Tx);
        }

        // 다음 틱에 쓸 값을 지금 읽어 두게 한다. 값은 한 주기 늦지만 간격은 읽은 시각으로 나눈 실제 간격이다.
        _demand.Set();
    }

    /// <summary>
    /// 32bit 카운터를 쓰는 드라이버가 남아 있어 감소가 관측될 수 있다.
    /// 랩어라운드를 음수 처리량으로 흘리면 차트가 뒤집히므로 0으로 접는다.
    /// </summary>
    private static double Delta(ulong previous, ulong current) =>
        current >= previous ? current - previous : 0;

    public void Dispose()
    {
        _stop.Cancel();
        // 읽기가 막혀 2초 안에 못 끝나면 핸들을 남겨 둔다 — 풀린 스레드가 닫힌 핸들을 건드리지 않게.
        if (_reader is null || _reader.Join(TimeSpan.FromSeconds(2)))
        {
            _stop.Dispose();
            _demand.Dispose();
        }
        _interfaces.Clear();
        IsAvailable = false;
    }

    private sealed class Interface
    {
        public ulong Luid { get; init; }
        public required DeviceHandle Handle { get; set; }
        public int RxSlot { get; init; }
        public int TxSlot { get; init; }

        /// <summary>샘플러가 마지막으로 가져간 읽기 번호. 같으면 새 결과가 없는 것이다.</summary>
        public long ConsumedSequence { get; set; }
    }
}

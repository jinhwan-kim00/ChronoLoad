using System.Diagnostics;
using ChronoLoad.Sensors.Native;

namespace ChronoLoad.Sensors;

/// <summary>프로세스 한 줄. 어댑터 키별로 GPU 사용률과 메모리를 분해해 담는다.</summary>
public sealed record ProcessSample(
    int Pid,
    string Name,
    double CpuPercent,
    long WorkingSetBytes,
    double DiskBytesPerSecond,
    IReadOnlyDictionary<string, double> GpuByAdapter,
    IReadOnlyDictionary<string, long> GpuMemoryByAdapter,
    IReadOnlyDictionary<string, double> GpuByEngine,
    int ParentPid);

/// <summary>
/// 프로세스 표 수집기 (§5.6). <b>MCP 전용이며 요청이 있을 때만 돈다.</b>
/// </summary>
/// <remarks>
/// <para>
/// 아무도 묻지 않는 동안 전 프로세스를 훑는 것은 "감시 대상에 부하를 주지 않는다"는
/// 이 앱의 전제를 깬다. 그래서 <see cref="KeepAlive"/> 로 관심을 표시한 뒤
/// <see cref="IdleTimeout"/> 동안 조용하면 스스로 멈춘다.
/// </para>
/// <para>
/// CPU 사용률은 커널이 주는 <b>누적</b> 시간이라 두 스냅샷의 차를 경과 시간으로 나눠 구한다.
/// 첫 수집에서는 기준선만 잡고 0 을 보고한다 — 거기서 절대값을 쓰면
/// "부팅 이후 평균"이 나와 지금 무엇이 바쁜지와 무관한 숫자가 된다.
/// </para>
/// </remarks>
public sealed class ProcessProvider : IDisposable
{
    /// <summary>마지막 요청 후 이만큼 조용하면 수집을 멈춘다.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>수집 주기. 프로세스 표는 초 단위로 충분하다.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();
    private readonly Dictionary<int, Baseline> _baselines = [];
    private readonly int _cores = Environment.ProcessorCount;

    private PdhQuery? _query;
    private PdhCounterArray? _engine;
    private PdhCounterArray? _processMemory;

    private IReadOnlyList<ProcessSample> _snapshot = [];
    private DateTimeOffset? _sampledAt;
    private long _lastRequestTicks;
    private long _lastCollectTimestamp;
    private Timer? _timer;
    private bool _pdhOpened;

    public DateTimeOffset? SampledAt { get { lock (_gate) return _sampledAt; } }

    /// <summary>지금 수집이 돌고 있는가. <c>describe_capabilities</c> 가 보고한다.</summary>
    public bool IsCollecting => _timer is not null;

    public IReadOnlyList<ProcessSample> Snapshot() { lock (_gate) return _snapshot; }

    /// <summary>
    /// 표가 필요하다고 알린다. 꺼져 있었다면 켜고 곧바로 한 번 수집한다 —
    /// 처음 물은 에이전트가 빈 배열을 받고 "프로세스가 없다"고 읽으면 안 된다.
    /// </summary>
    public void KeepAlive()
    {
        Volatile.Write(ref _lastRequestTicks, DateTime.UtcNow.Ticks);

        lock (_gate)
        {
            if (_timer is not null) return;

            OpenCounters();
            Collect();                       // 기준선
            _timer = new Timer(_ => Tick(), null, Period, Period);
            SensorLog.Write("프로세스 수집 시작 (MCP 요청)");
        }
    }

    private void Tick()
    {
        var idle = DateTime.UtcNow - new DateTime(Volatile.Read(ref _lastRequestTicks), DateTimeKind.Utc);
        if (idle > IdleTimeout)
        {
            Stop("요청 없음");
            return;
        }

        lock (_gate) Collect();
    }

    private void Stop(string reason)
    {
        lock (_gate)
        {
            if (_timer is null) return;

            _timer.Dispose();
            _timer = null;
            _baselines.Clear();
            SensorLog.Write($"프로세스 수집 중단 ({reason})");
        }
    }

    private void OpenCounters()
    {
        if (_pdhOpened) return;
        _pdhOpened = true;

        _query = PdhQuery.TryOpen();
        if (_query is null) return;

        // pid_ 로 시작하는 인스턴스만 프로세스별 분해가 가능하다.
        _engine = _query.TryAddArray(@"\GPU Engine(*)\Utilization Percentage");
        _processMemory = _query.TryAddArray(@"\GPU Process Memory(*)\Dedicated Usage");
        _query.Collect();
    }

    private void Collect()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = _lastCollectTimestamp == 0
            ? Period.TotalSeconds
            : Math.Max(1e-3, Stopwatch.GetElapsedTime(_lastCollectTimestamp, now).TotalSeconds);
        _lastCollectTimestamp = now;

        var rows = NtProcess.Enumerate();
        if (rows.Count == 0) return;

        var (gpuByPid, memoryByPid, engineByPid) = ReadGpuPerProcess();

        var samples = new List<ProcessSample>(rows.Count);
        var seen = new HashSet<int>(rows.Count);

        foreach (var row in rows)
        {
            // PID 0(Idle)은 프로세스가 아니라 "아무것도 안 한 시간"이다. CPU 로 정렬하면 늘 1위를
            // 차지하면서 아무 정보도 주지 않는다. 그 값은 CpuTotal 지표로 이미 알 수 있다.
            if (row.Pid == 0) continue;

            seen.Add(row.Pid);

            long cpu100Ns = row.KernelTime100Ns + row.UserTime100Ns;
            long io = row.ReadBytes + row.WriteBytes;

            double cpuPercent = 0, diskRate = 0;
            if (_baselines.TryGetValue(row.Pid, out var previous))
            {
                // 100ns 단위 → 초. 전 코어 합이 분모다.
                double cpuSeconds = (cpu100Ns - previous.Cpu100Ns) / 1e7;
                cpuPercent = Math.Clamp(cpuSeconds / elapsed / _cores * 100, 0, 100);
                diskRate = Math.Max(0, (io - previous.IoBytes) / elapsed);
            }

            _baselines[row.Pid] = new Baseline(cpu100Ns, io);

            samples.Add(new ProcessSample(
                row.Pid,
                row.Name,
                Math.Round(cpuPercent, 2),
                row.WorkingSetBytes,
                Math.Round(diskRate, 1),
                gpuByPid.GetValueOrDefault(row.Pid) ?? new Dictionary<string, double>(),
                memoryByPid.GetValueOrDefault(row.Pid) ?? new Dictionary<string, long>(),
                engineByPid.GetValueOrDefault(row.Pid) ?? new Dictionary<string, double>(),
                row.ParentPid));
        }

        // 끝난 프로세스의 기준선은 버린다. 안 그러면 PID 가 재사용될 때 엉뚱한 차분이 나온다.
        foreach (int pid in _baselines.Keys.Where(p => !seen.Contains(p)).ToArray())
            _baselines.Remove(pid);

        _snapshot = samples;
        _sampledAt = DateTimeOffset.Now;
    }

    /// <summary>
    /// <c>GPU Engine(pid_1234_luid_0x..._phys_0_eng_0_engtype_3D)</c> 형태의 인스턴스명을
    /// PID · 어댑터 LUID · 엔진 종류로 쪼갠다.
    /// </summary>
    private (Dictionary<int, Dictionary<string, double>>,
             Dictionary<int, Dictionary<string, long>>,
             Dictionary<int, Dictionary<string, double>>) ReadGpuPerProcess()
    {
        Dictionary<int, Dictionary<string, double>> byAdapter = [];
        Dictionary<int, Dictionary<string, long>> byMemory = [];
        Dictionary<int, Dictionary<string, double>> byEngine = [];

        if (_query is null || !_query.Collect()) return (byAdapter, byMemory, byEngine);

        _engine?.Read((instance, value) =>
        {
            if (!TryParse(instance, out int pid, out string luid, out string engineType)) return;
            if (value <= 0) return;

            // 어댑터별로는 엔진 중 최댓값을 쓴다. 엔진 사용률을 더하면 200% 가 나온다 —
            // 3D 와 Copy 가 동시에 도는 것은 두 배로 바쁜 것이 아니다.
            var adapters = byAdapter.TryGetValue(pid, out var a) ? a : byAdapter[pid] = [];
            adapters[luid] = Math.Max(adapters.GetValueOrDefault(luid), value);

            var engines = byEngine.TryGetValue(pid, out var e) ? e : byEngine[pid] = [];
            engines[engineType] = engines.GetValueOrDefault(engineType) + value;
        });

        _processMemory?.Read((instance, value) =>
        {
            if (!TryParse(instance, out int pid, out string luid, out _)) return;
            if (value <= 0) return;

            var memory = byMemory.TryGetValue(pid, out var m) ? m : byMemory[pid] = [];
            memory[luid] = memory.GetValueOrDefault(luid) + (long)value;
        });

        return (byAdapter, byMemory, byEngine);
    }

    private static bool TryParse(string instance, out int pid, out string luid, out string engineType)
    {
        pid = 0;
        luid = string.Empty;
        engineType = string.Empty;

        if (!instance.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) return false;

        var parts = instance.Split('_');
        if (parts.Length < 2 || !int.TryParse(parts[1], out pid)) return false;

        // 어댑터 키는 레지스트리의 장치 키와 같은 형식으로 맞춘다 — 에이전트가 프로세스의
        // GPU 사용을 get_gpu_status 의 어댑터와 곧바로 이을 수 있어야 한다.
        int luidAt = Array.FindIndex(parts, p => p.Equals("luid", StringComparison.OrdinalIgnoreCase));
        if (luidAt >= 0 && luidAt + 2 < parts.Length)
            luid = $"gpu:luid_{parts[luidAt + 1]}_{parts[luidAt + 2]}";

        int typeAt = Array.FindIndex(parts, p => p.Equals("engtype", StringComparison.OrdinalIgnoreCase));
        if (typeAt >= 0 && typeAt + 1 < parts.Length)
            engineType = string.Join('_', parts.Skip(typeAt + 1));

        return true;
    }

    public void Dispose()
    {
        Stop("종료");
        _engine?.Dispose();
        _processMemory?.Dispose();
        _query?.Dispose();
        _query = null;
    }

    private readonly record struct Baseline(long Cpu100Ns, long IoBytes);
}

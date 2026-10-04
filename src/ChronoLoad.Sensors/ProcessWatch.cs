using System.Runtime.InteropServices;
using ChronoLoad.Core.Metrics;

namespace ChronoLoad.Sensors;

/// <summary>
/// 지정한 프로세스 몇 개의 GPU 엔진·CPU·워킹셋을 1초마다 기록한다 (§5.6 · §10.2 <c>watch_process</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>추가 쿼리가 없다.</b> 엔진 사용률은 <see cref="GpuProvider"/> 가 어댑터 사용률을 구하려고 1초마다 읽는
/// <c>GPU Engine(*)</c> 와일드카드에 이미 PID 별로 들어 있다. 그 결과를 넘겨받아 감시 중인 PID 몫만 떼어 둔다.
/// CPU 와 워킹셋은 열어 둔 핸들로 <c>GetProcessTimes</c>·<c>K32GetProcessMemoryInfo</c> 를 부른다 —
/// 전 프로세스를 훑는 <c>list_processes</c> 수집기(§5.6)를 1초로 돌리는 것보다 훨씬 싸다.
/// </para>
/// <para>
/// 엔진 값은 PDH 의 것이라 1초 해상도이고, 긴 작업이 끝날 때 몰아서 계상되는 성질(§5.4)을 그대로 갖는다.
/// 어댑터 전체의 하드웨어 카운터(<c>GpuRenderCompute</c>)와 함께 읽는다.
/// </para>
/// <para>
/// 프로세스가 끝나도 기록은 남는다 — 끝난 뒤에 "그동안 무엇을 했나"를 묻는 일이 많다.
/// 지우는 것은 <see cref="Unwatch"/> 이거나, 자리가 모자라 가장 오래 끝나 있던 것을 밀어낼 때다.
/// </para>
/// </remarks>
public sealed class ProcessWatch : IDisposable
{
    /// <summary>동시에 감시할 수 있는 프로세스 수. 핸들을 들고 있고 1초마다 부르므로 넉넉히 두지 않는다.</summary>
    public const int MaxProcesses = 8;

    /// <summary>프로세스당 보관하는 점 수. 1초 × 900 = 15분 — 지표 링과 같은 길이다.</summary>
    public const int Capacity = 900;

    /// <summary>감시를 한 번에 걸 수 있는 최대 시간. 잊고 둔 감시가 영영 도는 것을 막는다.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<int, Watched> _watched = [];
    private readonly int _cores = Environment.ProcessorCount;
    private int _active;

    /// <summary>기록 중인 프로세스가 하나라도 있는가. 샘플링 스레드가 PID 를 파싱할지 가르는 데 쓴다.</summary>
    public bool Any => Volatile.Read(ref _active) > 0;

    public sealed record WatchInfo(int Pid, string Name, long StartedUtcTicks, long UntilUtcTicks, bool Ended, int Points);

    public sealed record History(
        int Pid, string Name, long StartedUtcTicks, long UntilUtcTicks, bool Ended,
        long[] Stamps, float[] CpuPercent, float[] WorkingSetBytes,
        IReadOnlyDictionary<(string AdapterKey, MetricKind Family), float[]> Engines);

    /// <summary>감시를 건다. 이미 있으면 기한만 늘린다. 실패하면 이유를 돌려준다.</summary>
    public (WatchInfo? Info, string? Error) Watch(int pid, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) duration = TimeSpan.FromMinutes(10);
        if (duration > MaxDuration) duration = MaxDuration;
        long now = DateTime.UtcNow.Ticks;

        lock (_gate)
        {
            if (_watched.TryGetValue(pid, out var existing) && !existing.Ended)
            {
                existing.Until = now + duration.Ticks;
                return (existing.Info, null);
            }

            if (_watched.Count >= MaxProcesses && !EvictOldestEnded())
                return (null, $"이미 {MaxProcesses}개를 감시 중이다. unwatch_process 로 하나를 푼다.");

            nint handle = ProcessNative.OpenProcess(ProcessNative.QueryLimitedInformation, false, pid);
            if (handle == 0)
                return (null, $"PID {pid} 를 열 수 없다(없거나 권한이 없다).");

            if (existing is not null) { existing.Dispose(); _watched.Remove(pid); }

            var watched = new Watched(pid, handle, ProcessNative.ImageName(handle) ?? $"pid {pid}", now, now + duration.Ticks);
            _watched[pid] = watched;
            Volatile.Write(ref _active, _watched.Values.Count(w => !w.Ended));
            return (watched.Info, null);
        }
    }

    public bool Unwatch(int pid)
    {
        lock (_gate)
        {
            if (!_watched.Remove(pid, out var watched)) return false;
            watched.Dispose();
            Volatile.Write(ref _active, _watched.Values.Count(w => !w.Ended));
            return true;
        }
    }

    public IReadOnlyList<WatchInfo> List()
    {
        lock (_gate) return _watched.Values.OrderBy(w => w.Started).Select(w => w.Info).ToArray();
    }

    public History? Get(int pid)
    {
        lock (_gate)
        {
            if (!_watched.TryGetValue(pid, out var w)) return null;
            return new History(w.Pid, w.Name, w.Started, w.Until, w.Ended,
                [.. w.Stamps], [.. w.Cpu], [.. w.WorkingSet],
                w.Engines.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
        }
    }

    // ── 샘플링 스레드 ────────────────────────────────────────────

    /// <summary>이번 엔진 읽기를 시작한다. 직전 틱의 엔진 누적을 비운다.</summary>
    internal void BeginEngines()
    {
        lock (_gate)
            foreach (var w in _watched.Values)
            {
                w.Pending.Clear();
                w.PendingUnknown.Clear();
            }
    }

    /// <summary>엔진 인스턴스 하나. 인스턴스명이 <c>pid_N_…</c> 로 시작할 때만 PID 를 읽는다.</summary>
    internal void AddEngine(string instance, string adapterKey, string engineType, double value)
    {
        if (ParsePid(instance) is not { } pid) return;

        lock (_gate)
        {
            if (!_watched.TryGetValue(pid, out var w) || w.Ended) return;
            var key = (adapterKey, engineType);
            w.Pending[key] = w.Pending.GetValueOrDefault(key) + value;
        }
    }

    /// <summary>
    /// 값을 쓰지 않은 엔진 인스턴스(<see cref="EngineCounterGuard"/> — 상한을 넘은 값, 깨진 카운터). 그 프로세스의 그 계열은 이번 점에서 측정 불가다 —
    /// 남은 인스턴스가 이미 100 이 아니라면.
    /// </summary>
    internal void AddUnknownEngine(string instance, string adapterKey, string engineType)
    {
        if (ParsePid(instance) is not { } pid) return;

        lock (_gate)
        {
            if (!_watched.TryGetValue(pid, out var w) || w.Ended) return;
            w.PendingUnknown.Add((adapterKey, engineType));
        }
    }

    private static int? ParsePid(string instance)
    {
        if (!instance.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) return null;
        int end = instance.IndexOf('_', 4);
        return end >= 0 && int.TryParse(instance.AsSpan(4, end - 4), out int pid) ? pid : null;
    }

    /// <summary>한 점을 적는다. 엔진은 계열 안 최댓값, CPU 는 직전 점과의 차분이다.</summary>
    internal void Commit(long nowUtcTicks)
    {
        lock (_gate)
        {
            foreach (var w in _watched.Values)
            {
                if (w.Ended) continue;

                if (nowUtcTicks > w.Until || !ProcessNative.IsAlive(w.Handle))
                {
                    w.Ended = true;
                    continue;
                }

                float cpu = float.NaN;
                if (ProcessNative.CpuTime100Ns(w.Handle) is { } cpuTime)
                {
                    if (w.LastCpu100Ns is { } lastCpu && nowUtcTicks > w.LastStamp)
                        cpu = (float)Math.Clamp((cpuTime - lastCpu) / (double)(nowUtcTicks - w.LastStamp) / _cores * 100, 0, 100);
                    w.LastCpu100Ns = cpuTime;
                }
                w.LastStamp = nowUtcTicks;

                float workingSet = ProcessNative.WorkingSet(w.Handle) is { } ws ? ws : float.NaN;

                // 엔진 종류 → 계열. 계열 안에서는 최댓값 — 어댑터 사용률과 같은 정의다(§5.4).
                var families = new Dictionary<(string, MetricKind), float>();
                foreach (var ((adapter, engineType), sum) in w.Pending)
                {
                    if (GpuEngineFamilies.Classify(engineType) is not { } family) continue;
                    var key = (adapter, family);
                    families[key] = Math.Max(families.GetValueOrDefault(key), (float)Math.Clamp(sum, 0, 100));
                }

                // 쓰지 않은 인스턴스가 섞인 계열은 남은 값이 하한일 뿐이다. 100 에 닿지 않았으면 NaN(측정 불가)이다.
                foreach (var (adapter, engineType) in w.PendingUnknown)
                {
                    if (GpuEngineFamilies.Classify(engineType) is not { } family) continue;
                    var key = (adapter, family);
                    if (!families.TryGetValue(key, out float known) || known < 100) families[key] = float.NaN;
                }

                w.Append(nowUtcTicks, cpu, workingSet, families);
            }

            Volatile.Write(ref _active, _watched.Values.Count(w => !w.Ended));
        }
    }

    private bool EvictOldestEnded()
    {
        var oldest = _watched.Values.Where(w => w.Ended).OrderBy(w => w.Started).FirstOrDefault();
        if (oldest is null) return false;
        _watched.Remove(oldest.Pid);
        oldest.Dispose();
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var w in _watched.Values) w.Dispose();
            _watched.Clear();
            Volatile.Write(ref _active, 0);
        }
    }

    private sealed class Watched(int pid, nint handle, string name, long started, long until) : IDisposable
    {
        public int Pid { get; } = pid;
        public nint Handle { get; private set; } = handle;
        public string Name { get; } = name;
        public long Started { get; } = started;
        public long Until { get; set; } = until;
        public bool Ended { get; set; }

        public long? LastCpu100Ns { get; set; }
        public long LastStamp { get; set; }

        public Dictionary<(string Adapter, string EngineType), double> Pending { get; } = [];
        public HashSet<(string Adapter, string EngineType)> PendingUnknown { get; } = [];

        public List<long> Stamps { get; } = [];
        public List<float> Cpu { get; } = [];
        public List<float> WorkingSet { get; } = [];
        public Dictionary<(string, MetricKind), List<float>> Engines { get; } = [];

        public WatchInfo Info => new(Pid, Name, Started, Until, Ended, Stamps.Count);

        public void Append(long stamp, float cpu, float workingSet, Dictionary<(string, MetricKind), float> families)
        {
            // 처음 보는 어댑터·계열은 앞쪽을 NaN(값 없음)으로 채워 길이를 맞춘다. 0 은 "안 썼다"라는 측정값이다.
            foreach (var key in families.Keys)
                if (!Engines.ContainsKey(key))
                    Engines[key] = [.. Enumerable.Repeat(float.NaN, Stamps.Count)];

            Stamps.Add(stamp);
            Cpu.Add(cpu);
            WorkingSet.Add(workingSet);
            // 이번 틱에 인스턴스가 없던 엔진은 그 프로세스가 그 엔진에 제출한 작업이 없다는 뜻이다 — 0 이다.
            foreach (var (key, series) in Engines) series.Add(families.GetValueOrDefault(key));

            if (Stamps.Count > Capacity)
            {
                Stamps.RemoveAt(0);
                Cpu.RemoveAt(0);
                WorkingSet.RemoveAt(0);
                foreach (var series in Engines.Values) series.RemoveAt(0);
            }
        }

        public void Dispose()
        {
            if (Handle != 0) { ProcessNative.CloseHandle(Handle); Handle = 0; }
        }
    }
}

internal static partial class ProcessNative
{
    public const uint QueryLimitedInformation = 0x1000;
    private const uint StillActive = 259;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint handle, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint handle, out uint code);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(nint handle, out MemoryCounters counters, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(nint handle, uint flags, [Out] char[] name, ref uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        public uint Cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    public static bool IsAlive(nint handle) => GetExitCodeProcess(handle, out uint code) && code == StillActive;

    /// <summary>커널+사용자 CPU 시간 누계(100ns).</summary>
    public static long? CpuTime100Ns(nint handle) =>
        GetProcessTimes(handle, out _, out _, out long kernel, out long user) ? kernel + user : null;

    public static long? WorkingSet(nint handle) =>
        GetProcessMemoryInfo(handle, out var counters, (uint)Marshal.SizeOf<MemoryCounters>())
            ? (long)counters.WorkingSetSize
            : null;

    public static string? ImageName(nint handle)
    {
        var buffer = new char[1024];
        uint size = (uint)buffer.Length;
        return QueryFullProcessImageName(handle, 0, buffer, ref size)
            ? Path.GetFileName(new string(buffer, 0, (int)size))
            : null;
    }
}

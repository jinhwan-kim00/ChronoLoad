using System.Diagnostics;
using ChronoLoad.Core.Metrics;
using ChronoLoad.Core.Sampling;
using ChronoLoad.Sensors;

// 콘솔 하네스. UI 없이 샘플 파이프라인이 실제 값을 내는지 확인한다.
//
//   dotnet run --project tools/ChronoLoad.Harness -- [초] [옵션]
//
// 옵션은 전부 "문제가 어디서 나는지 가르기" 위한 것이다. 네이티브 계층은 조용히 실패하거나
// 프로세스째 죽기 때문에, 계층을 하나씩 떼어낼 수 있어야 원인을 좁힐 수 있다.
//
//   --no-vendor     계층 B(NVML·IGCL·Level Zero)를 열지 않는다. PDH 만으로 동작
//   --no-watcher    장치 착탈 감시를 끈다
//   --power-probe   전원 상태만 관찰한다. 센서도 벤더 SDK 도 열지 않으므로 장치를 깨우지 않는다
//   --igcl-probe    IGCL 원시 텔레메트리를 1초마다 찍는다. --igcl-version=N 으로 구조체 버전을 고정한다
//   --l0-probe      Level Zero PCIe 누적 통계(송수신 바이트)를 1초마다 찍는다
//   --watch=이름    그 이름의 프로세스를 watch_process 처럼 1초마다 기록하고 끝에 요약을 찍는다

// dotnet run 이 인식하지 못한 옵션을 앱으로 넘기는 경우가 있어, 숫자로 읽히는 첫 인자를 쓴다.
int seconds = 10;
foreach (var arg in args)
    if (int.TryParse(arg, out int parsed) && parsed > 0) { seconds = parsed; break; }

// 진단 줄에도 경과 시간을 박는다. 전원 전이·장치 착탈이 "언제" 일어났는지가 곧 검증 내용이다.
var clock = Stopwatch.StartNew();
string Stamp() => $"{clock.Elapsed.TotalSeconds,6:0.00}s";

SensorLog.Sink = m => Console.Error.WriteLine($"  ! {Stamp()} {m}");

// 전원 상태만 들여다보는 모드. 센서도 벤더 SDK 도 열지 않으므로 장치를 절대 깨우지 않는다 —
// "조회 자체가 깨우는가"를 가리려면 이렇게 순수한 관찰자가 필요하다.
// MCP 서버를 실제로 세운다. 앱 없이 툴 응답을 확인하기 위한 경로다.
if (args.Contains("--mcp"))
{
    var mcpRegistry = new MetricRegistry(seriesCapacity: 3600);
    var mcpOptions = new SamplingOptions();
    await using var mcpEngine = new SampleEngine(mcpRegistry, mcpOptions);

    await mcpEngine.AddProviderAsync(new CpuProvider());
    await mcpEngine.AddProviderAsync(new MemoryProvider());
    await mcpEngine.AddProviderAsync(new NetworkProvider());
    await mcpEngine.AddProviderAsync(new DiskProvider());
    var mcpGpu = new GpuProvider();
    await mcpEngine.AddProviderAsync(mcpGpu);

    using var mcpProcesses = new ProcessProvider();
    mcpEngine.Start();

    var context = new ChronoLoad.Mcp.McpContext(mcpRegistry, mcpEngine)
    {
        TelemetryLayers = () => mcpGpu.TelemetryLayers,
        EngineBreakdown = mcpGpu.EngineBreakdown,
        LimitReasons = mcpGpu.LimitReasons,
        ProcessWatch = mcpGpu.Watch,
        Processes = new ChronoLoad.Mcp.SensorProcessSource(mcpProcesses),
        SamplePeriod = mcpOptions.FastPeriod,
    };

    await using var host = await ChronoLoad.Mcp.McpHost.StartAsync(context);
    if (host is null) { Console.Error.WriteLine("MCP 서버를 세우지 못했다."); return; }

    var tokenFile = ChronoLoad.Mcp.McpTokenFile.TryRead();
    Console.WriteLine($"MCP 서버 http://127.0.0.1:{host.Port}/mcp");
    Console.WriteLine($"토큰 파일 {ChronoLoad.Mcp.McpTokenFile.Path}");
    Console.WriteLine($"토큰 {tokenFile?.Token}");
    Console.WriteLine($"{seconds}초 동안 유지한다.");

    await Task.Delay(TimeSpan.FromSeconds(seconds));
    return;
}

// 프로세스 수집 검증. MCP 툴이 쓰는 표를 그대로 찍는다.
if (args.Contains("--processes"))
{
    using var processes = new ProcessProvider();
    processes.KeepAlive();
    await Task.Delay(2500);
    processes.KeepAlive();

    var rows = processes.Snapshot()
        .OrderByDescending(r => r.CpuPercent)
        .ThenByDescending(r => r.WorkingSetBytes)
        .Take(12)
        .ToArray();

    Console.WriteLine($"프로세스 {processes.Snapshot().Count}개 · 수집 {processes.SampledAt:HH:mm:ss}");
    Console.WriteLine($"{"PID",7} │ {"이름",-28} │ {"CPU",6} │ {"메모리",10} │ {"디스크",11} │ GPU");
    foreach (var r in rows)
    {
        string gpu = r.GpuByAdapter.Count == 0
            ? "—"
            : string.Join(" ", r.GpuByAdapter.Select(kv => $"{kv.Key}:{kv.Value:0.0}%"));
        Console.WriteLine($"{r.Pid,7} │ {Trim(r.Name, 28),-28} │ {r.CpuPercent,5:0.0}% │ " +
                          $"{r.WorkingSetBytes / 1048576.0,8:0.0} MB │ {r.DiskBytesPerSecond / 1048576.0,8:0.00} MB/s │ {gpu}");
    }

    var busiest = processes.Snapshot().Where(r => r.GpuByAdapter.Count > 0)
        .OrderByDescending(r => r.GpuByAdapter.Values.Max()).Take(5).ToArray();
    Console.WriteLine();
    Console.WriteLine("GPU 사용 상위");
    foreach (var r in busiest)
        Console.WriteLine($"  {Trim(r.Name, 26),-26} 어댑터 {string.Join(", ", r.GpuByAdapter.Select(kv => $"{kv.Key}={kv.Value:0.0}%"))}" +
                          $" · 엔진 {string.Join(", ", r.GpuByEngine.Select(kv => $"{kv.Key}={kv.Value:0.0}%"))}");
    return;

    static string Trim(string v, int n) => v.Length <= n ? v : v[..(n - 1)] + "…";
}

// IGCL 원시값. 드라이버가 어떤 항목을 지원하는지, 유휴·부하에서 무엇이 움직이는지를 본다.
if (args.Contains("--igcl-probe"))
{
    using var igcl = ChronoLoad.Sensors.Vendor.IgclTelemetry.TryCreate();
    if (igcl is null) { Console.Error.WriteLine("IGCL 을 열 수 없다."); return; }

    // 구조체 버전을 고정해 본다. 버전 1 에만 있는 항목(gpuEffectiveClock 등)을 확인할 때 쓴다.
    byte? forcedVersion = args.FirstOrDefault(a => a.StartsWith("--igcl-version=")) is { } v
        ? byte.Parse(v["--igcl-version=".Length..])
        : null;

    for (int i = 0; i < seconds; i++)
    {
        for (int d = 0; d < igcl.DeviceCount; d++)
            Console.WriteLine($"{i,3}s [{d}] " + string.Join(" ",
                igcl.Probe(d, forcedVersion).Where(p => !double.IsNaN(p.Value)).Select(p => $"{p.Name}={p.Value:0.###}")));
        await Task.Delay(1000);
    }
    return;
}

// NVML PCIe 처리량 호출이 얼마나 걸리는가. 드라이버가 호출 안에서 구간을 재므로 블로킹일 수 있다.
if (args.Contains("--nvml-pcie-probe"))
{
    var nvml = ChronoLoad.Sensors.Vendor.NvmlTelemetry.TryCreate();
    if (nvml is null) { Console.Error.WriteLine("NVML 을 열 수 없다."); return; }

    // 연달아 부를 때의 호출 비용(CPU·벽시계). 샘플링 틱처럼 띄엄띄엄 부르면 훨씬 느리다(평균 3.8ms) —
    // 드라이버가 값을 새로 갱신하느라 기다린다. 이 숫자만 보고 매 틱 부르면 안 된다.
    {
        var self = Process.GetCurrentProcess();
        var cpu0 = self.TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        for (int n = 0; n < 200; n++) nvml.ProbePcieCounters(0);
        self.Refresh();
        Console.WriteLine($"누적 필드 200회: CPU {(self.TotalProcessorTime - cpu0).TotalMilliseconds / 200:0.000} ms/회 · " +
                          $"벽시계 {wall.Elapsed.TotalMilliseconds / 200:0.000} ms/회");
    }

    ulong lastRx = 0, lastTx = 0;
    long lastStamp = 0;
    for (int i = 0; i < seconds; i++)
    {
        var c = nvml.ProbePcieCounters(0);
        string rate = lastStamp > 0 && c.StampUs > lastStamp
            ? $" → rx {(c.Rx - lastRx) / ((c.StampUs - lastStamp) / 1e6) / 1e6:0.0} MB/s · tx {(c.Tx - lastTx) / ((c.StampUs - lastStamp) / 1e6) / 1e6:0.0} MB/s"
            : "";
        Console.WriteLine($"{i,3}s 누적 rc={c.RxRc}/{c.TxRc} rx={c.Rx} tx={c.Tx} · {c.Milliseconds:0.00} ms{rate}");
        (lastRx, lastTx, lastStamp) = (c.Rx, c.Tx, c.StampUs);

        var (rx, tx, ms) = nvml.ProbePcie(0);
        Console.WriteLine($"     순간 rx={rx} KB/s tx={tx} KB/s · 두 호출 {ms:0.0} ms");
        await Task.Delay(1000);
    }
    return;
}

// Level Zero 의 PCIe 누적 통계. IGCL 에는 송수신 카운터가 없어 이쪽이 지원하는지 가른다.
// IGCL 과 한 프로세스에서 함께 열지 않는다 — 같은 로더를 두 주인이 잡는다(§5.4).
if (args.Contains("--l0-probe"))
{
    using var l0 = ChronoLoad.Sensors.Vendor.LevelZeroTelemetry.TryCreate();
    if (l0 is null) { Console.Error.WriteLine("Level Zero 를 열 수 없다."); return; }

    for (int i = 0; i < seconds; i++)
    {
        for (int d = 0; d < l0.DeviceCount; d++)
            Console.WriteLine($"{i,3}s [{d}] " + string.Join(" ",
                l0.ProbePci(d).Select(p => $"{p.Name}={p.Value:0.###}")));
        await Task.Delay(1000);
    }
    return;
}

if (args.Contains("--power-probe"))
{
    var probe = new ChronoLoad.Sensors.Native.DevicePowerProbe();
    probe.Refresh();

    var watched = probe.Addresses.ToArray();

    Console.WriteLine($"전원 상태 관찰 — {seconds}초, 벤더 SDK 를 열지 않는다");
    for (int i = 0; i < seconds; i++)
    {
        Console.WriteLine($"{i,4}s │ " + string.Join(" │ ",
            watched.Select(w => $"{w} {probe.Query(w)}")));
        await Task.Delay(1000);
    }
    return;
}

var registry = new MetricRegistry(seriesCapacity: 3600);
var options = new SamplingOptions();
await using var engine = new SampleEngine(registry, options);

await engine.AddProviderAsync(new CpuProvider());
// 코어별은 CPU 장치에 채널로 얹히므로 CpuProvider 뒤여야 한다.
await engine.AddProviderAsync(new CoreProvider());
await engine.AddProviderAsync(new MemoryProvider());
await engine.AddProviderAsync(new NetworkProvider());
await engine.AddProviderAsync(new DiskProvider());
bool useVendor = !args.Contains("--no-vendor");
if (!useVendor) Console.Error.WriteLine("  ! 벤더 텔레메트리 꺼짐 (--no-vendor)");
var gpuProvider = new GpuProvider
{
    UseVendorTelemetry = useVendor,
    TraceVendorCalls = args.Contains("--trace-vendor"),
};
await engine.AddProviderAsync(gpuProvider);

// --watch=이름 : 그 이름의 프로세스를 찾아 watch_process 와 같은 기록을 건다(끝에 요약을 찍는다).
string? watchName = args.FirstOrDefault(a => a.StartsWith("--watch="))?["--watch=".Length..];
int? watchedPid = null;
if (watchName is not null)
    _ = Task.Run(async () =>
    {
        for (int attempt = 0; attempt < 20 && watchedPid is null; attempt++)
        {
            var found = Process.GetProcessesByName(watchName).FirstOrDefault();
            if (found is not null && gpuProvider.Watch.Watch(found.Id, TimeSpan.FromMinutes(10)).Info is not null)
            {
                watchedPid = found.Id;
                Console.Error.WriteLine($"  ! {Stamp()} 감시 시작: {watchName} (PID {found.Id})");
            }
            else await Task.Delay(500);
        }
    });

// §5.7 핫플러그. 장치 착탈을 감지해 재열거를 요청한다.

// 장치 감시는 네이티브 콜백을 쓰므로, 문제를 가를 때 꺼볼 수 있어야 한다.
bool watchDevices = !args.Contains("--no-watcher");

using var watcher = new DeviceWatcher();
watcher.Changed += reason =>
{
    Console.Error.WriteLine($"  ~ {Stamp()} 장치 변경 감지 [{reason}] — 재열거 요청");
    engine.RequestRescan();
};
if (watchDevices) watcher.Start();
else Console.Error.WriteLine("  ! 장치 감시 꺼짐 (--no-watcher)");

// 장치 집합이 실제로 바뀐 순간만 찍는다. 재열거해도 결과가 같으면 조용하다.
var deviceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
engine.DevicesChanged += _ =>
{
    var current = registry.ActiveDevices.ToDictionary(d => d.Key, d => d.Info.ShortName);

    foreach (var (key, name) in current)
    {
        if (!deviceKeys.Add(key)) continue;
        Console.Error.WriteLine($"  + {Stamp()} 장치 추가: {name}");

        // GPU 는 첫 목록을 찍은 뒤에 등록된다. 부가 정보(HAGS·벤더 ID 등)를 여기서 찍는다.
        if (registry.Find(key) is { Info.Class: ChronoLoad.Core.Devices.DeviceClass.Gpu } gpu)
            Console.Error.WriteLine("      " + string.Join(" · ", gpu.Info.Extra.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    foreach (string key in deviceKeys.Where(k => !current.ContainsKey(k)).ToArray())
    {
        deviceKeys.Remove(key);
        Console.Error.WriteLine($"  - {Stamp()} 장치 제거: {key}");
    }
};

Console.WriteLine("ChronoLoad 샘플 파이프라인 하네스");
Console.WriteLine($"  샘플 주기   {options.FastPeriod.TotalMilliseconds:0}ms");
Console.WriteLine($"  버퍼 용량   {registry.SeriesCapacity} 포인트 " +
                  $"({registry.SeriesCapacity * options.FastPeriod.TotalSeconds / 60:0.#}분)");
Console.WriteLine($"  슬롯 수     {registry.SlotCount}");
Console.WriteLine();

foreach (var device in registry.ActiveDevices)
{
    Console.WriteLine($"  [{device.Info.Class}] {device.Info.ShortName}");
    Console.WriteLine($"      {device.Info.FullName}");
    foreach (var (key, value) in device.Info.Extra)
        Console.WriteLine($"      {key} = {value}");
}
Console.WriteLine();

// 장치는 실행 중에도 늘어난다(비율 카운터가 두 번 수집된 뒤에야 잡히는 디스크 등).
// 레지스트리 리비전이 바뀌면 목록을 다시 잡는다.
int knownRevision = -1;
(DeviceHandle Device, MetricKind Kind, int Slot)[] slots = [];

void RefreshSlots()
{
    knownRevision = registry.Revision;
    slots = registry.ActiveDevices
        .SelectMany(d => d.Kinds.Select(k => (Device: d, Kind: k, Slot: d.SlotOf(k))))
        .OrderBy(x => x.Slot)
        .ToArray();
}

RefreshSlots();

Console.WriteLine($"{"경과",6} │ {string.Join(" │ ", slots.Select(x => $"{x.Kind,-14}"))}");
Console.WriteLine(new string('─', 8 + slots.Length * 17));

var sw = Stopwatch.StartNew();
long lastPrint = 0;
long ticksSeen = 0;

engine.Committed += c =>
{
    ticksSeen = c.TickIndex;
    if (sw.ElapsedMilliseconds - lastPrint < 1000) return;
    lastPrint = sw.ElapsedMilliseconds;
    if (registry.Revision != knownRevision) RefreshSlots();

    var cells = slots.Select(x =>
    {
        var series = registry.Series(new MetricId(x.Kind, x.Device.Index));
        return $"{Format(x.Kind, series?.Latest ?? float.NaN),14}";
    });

    Console.WriteLine($"{sw.Elapsed.TotalSeconds,5:0.0}s │ {string.Join(" │ ", cells)}");
};

engine.Start();
await Task.Delay(TimeSpan.FromSeconds(seconds));

Console.WriteLine();
Console.WriteLine($"틱 {ticksSeen}회 · 기대치 약 {seconds / options.FastPeriod.TotalSeconds:0}회 " +
                  $"· 샘플 소요 평균 {engine.MeanSampleDuration.TotalMicroseconds:0}µs " +
                  $"· 마지막 {engine.LastSampleDuration.TotalMicroseconds:0}µs");
if (engine.DisabledProviders.Count > 0)
    Console.WriteLine($"비활성 프로바이더: {string.Join(", ", engine.DisabledProviders)}");

// 시간 축(§7.4). 프레임 수만으로는 축이 맞는지 알 수 없다 — 벽시계와 대조해야 한다.
{
    long frames = registry.Frames;
    var stamps = new long[Math.Min(frames, registry.SeriesCapacity)];
    int copied = registry.CopyTimestamps(stamps);
    Console.WriteLine();
    if (copied < 2) Console.WriteLine($"시간 축 프레임 {frames}개 — 간격을 재기엔 짧다");
    else
    {
        var gaps = new long[copied - 1];
        for (int i = 1; i < copied; i++) gaps[i - 1] = stamps[i] - stamps[i - 1];

        // 틱이 주기보다 길어지면 PeriodicTimer 가 다음 틱을 곧바로 돌린다. 그 짧은 틱에서는 PDH 비율
        // 카운터의 수집 간격이 0 에 가까워 사용률이 0 으로 나온다 — 화면의 값이 잠깐씩 0 으로 튀는 모양이다.
        int shortGaps = gaps.Count(g => g < TimeSpan.TicksPerMillisecond * 50);
        int longGaps = gaps.Count(g => g > TimeSpan.TicksPerMillisecond * 400);
        int cpuZeros = 0;
        if (registry.ActiveDevices.FirstOrDefault(d => d.SlotOf(MetricKind.CpuTotal) >= 0) is { } cpuDevice
            && registry.Series(cpuDevice.SlotOf(MetricKind.CpuTotal)) is { } cpuSeries)
            for (int i = 0; i < cpuSeries.Count; i++)
                if (cpuSeries.IsMeasured(i) && cpuSeries[i] == 0) cpuZeros++;
        Console.WriteLine($"짧은 틱(<50ms) {shortGaps}회 · 긴 틱(>400ms) {longGaps}회 · CPU 사용률 0 {cpuZeros}회");

        Array.Sort(gaps);
        var span = TimeSpan.FromTicks(stamps[copied - 1] - stamps[0]);
        Console.WriteLine(
            $"시간 축 프레임 {frames}개 · 구간 {span.TotalSeconds:0.00}s " +
            $"· 간격 중앙 {TimeSpan.FromTicks(gaps[gaps.Length / 2]).TotalMilliseconds:0.0}ms " +
            $"(기대 {options.FastPeriod.TotalMilliseconds:0}ms) " +
            $"· 최대 {TimeSpan.FromTicks(gaps[^1]).TotalMilliseconds:0.0}ms");
    }
}

RefreshSlots();
Console.WriteLine();
Console.WriteLine("장치");
foreach (var device in registry.ActiveDevices.OrderBy(d => d.Info.Class).ThenBy(d => d.Index))
    Console.WriteLine($"  [{device.Info.Class,-7}] {device.Info.ShortName,-24} {device.Info.FullName}");

// 코어별 사용률은 채널 슬롯이라 위 표에 나오지 않는다(오버레이 전용). 값이 실제로 들어오는지는
// UI 없이도 봐야 하므로 여기서 따로 찍는다 — 전부 NaN 이면 카운터 경로가 죽은 것이다.
foreach (var device in registry.ActiveDevices.Where(d => d.Channels.Count > 0))
{
    Console.WriteLine();
    Console.WriteLine($"{device.Info.ShortName} 채널 {device.Channels.Count}개 (코어별 사용률)");
    for (int i = 0; i < device.Channels.Count; i++)
    {
        float latest = registry.Series(device.Channels[i])?.Latest ?? float.NaN;
        Console.Write($"  {i,2}:{(float.IsNaN(latest) ? "   —" : $"{latest,4:0}")}%");
        if (i % 8 == 7) Console.WriteLine();
    }
    if (device.Channels.Count % 8 != 0) Console.WriteLine();
}

Console.WriteLine();
Console.WriteLine("리셋 이후 구간 통계");
Console.WriteLine($"{"지표",-16} │ {"샘플",6} │ {"평균",12} │ {"최소",12} │ {"최대",12} │ {"표준편차",10}");
Console.WriteLine(new string('─', 84));

foreach (var (device, kind, slot) in slots)
{
    var st = registry.StatsSnapshot(slot);
    Console.Write($"{device.Index} ");
    Console.WriteLine(
        $"{kind,-16} │ {st.Count,6} │ {Format(kind, (float)st.Mean),12} │ " +
        $"{Format(kind, st.Min),12} │ {Format(kind, st.Max),12} │ {Format(kind, (float)st.StdDev),12}");
}

if (watchedPid is { } pid && gpuProvider.Watch.Get(pid) is { } record)
{
    Console.WriteLine();
    Console.WriteLine($"프로세스 감시 — {record.Name} (PID {pid}) · 점 {record.Stamps.Length}개 · 끝남 {record.Ended}");
    static string Summary(float[] v)
    {
        var finite = v.Where(float.IsFinite).ToArray();
        return finite.Length == 0 ? "—" : $"평균 {finite.Average(x => (double)x):0.0} · 최대 {finite.Max():0.0} · {finite.Length}점";
    }
    Console.WriteLine($"  CPU %          {Summary(record.CpuPercent)}");
    Console.WriteLine($"  워킹셋 MB      {Summary(record.WorkingSetBytes.Select(b => b / 1048576f).ToArray())}");
    foreach (var ((adapter, family), values) in record.Engines)
        Console.WriteLine($"  {family,-14} {Summary(values)}  ({adapter})");
}

Console.WriteLine();
double dutyCycle = engine.MeanSampleDuration.TotalSeconds / options.FastPeriod.TotalSeconds;
Console.WriteLine($"샘플링 듀티 사이클 {dutyCycle:P2} (코어 1개 기준) — 목표 §12 는 평균 1% 미만");

static string Format(MetricKind kind, float value)
{
    if (float.IsNaN(value)) return "—";
    return kind.Unit() switch
    {
        MetricUnit.Percent => $"{value:0.0} %",
        MetricUnit.Bytes => $"{value / (1024.0 * 1024 * 1024):0.00} GiB",
        MetricUnit.ByteRate => $"{value / 1e6:0.0} MB/s",
        MetricUnit.BitRate => $"{value * 8 / 1e6:0.0} Mbps",
        MetricUnit.Celsius => $"{value:0} °C",
        MetricUnit.Watt => $"{value:0.0} W",
        MetricUnit.Megahertz => $"{value:0} MHz",
        _ => $"{value:0.##}",
    };
}

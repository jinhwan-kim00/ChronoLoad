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
await engine.AddProviderAsync(new GpuProvider
{
    UseVendorTelemetry = useVendor,
    TraceVendorCalls = args.Contains("--trace-vendor"),
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
        if (deviceKeys.Add(key))
            Console.Error.WriteLine($"  + {Stamp()} 장치 추가: {name}");

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

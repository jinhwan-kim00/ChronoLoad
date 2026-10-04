using System.ComponentModel;
using System.Text.Json.Serialization;
using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Metrics;
using ModelContextProtocol.Server;

namespace ChronoLoad.Mcp;

/// <summary>
/// ChronoLoad 가 MCP 로 노출하는 툴 (§10.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>읽기 전용이 원칙이다.</b> 상태를 바꾸는 툴은 <c>reset_stats</c> 하나뿐이고,
/// 프로세스 종료·우선순위 변경 같은 것은 제공하지 않는다 — 모니터가 시스템을 바꿀 이유가 없고,
/// 에이전트에게 그런 손잡이를 쥐여주면 사고의 폭이 관측 도구의 가치보다 커진다.
/// </para>
/// <para>
/// 모든 응답은 <c>sampledAt</c> · <c>stale</c> · <c>devicesRevision</c> 을 포함한다 (§10.3-10.4).
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class ChronoLoadTools(McpContext ctx)
{
    [McpServerTool(Name = "get_system_snapshot")]
    [Description("CPU·메모리와 GPU·디스크·네트워크 배열을 한 번에 돌려준다. 시스템 전체를 훑는 첫 호출용.")]
    public object GetSystemSnapshot()
    {
        var system = MetricReader.Devices(ctx, DeviceClass.System);
        var cpu = system.FirstOrDefault(d => d.SlotOf(MetricKind.CpuTotal) >= 0);
        var memory = system.FirstOrDefault(d => d.SlotOf(MetricKind.MemUsed) >= 0);

        return new
        {
            header = MetricReader.Header(ctx),
            host = new
            {
                machine = Environment.MachineName,
                logicalCores = Environment.ProcessorCount,
                os = Environment.OSVersion.VersionString,
                samplePeriodMs = ctx.SamplePeriod.TotalMilliseconds,
            },
            cpu = cpu is null ? null : new
            {
                device = McpJsonHelpers.Ref(cpu),
                utilizationPercent = MetricReader.Latest(ctx, cpu, MetricKind.CpuTotal),
            },
            memory = memory is null ? null : new
            {
                device = McpJsonHelpers.Ref(memory),
                used = MetricReader.LatestBytes(ctx, memory, MetricKind.MemUsed),
                committed = MetricReader.LatestBytes(ctx, memory, MetricKind.MemCommit),
            },
            gpus = MetricReader.Devices(ctx, DeviceClass.Gpu).Select(GpuSummary).ToArray(),
            disks = MetricReader.Devices(ctx, DeviceClass.Disk).Select(DiskSummary).ToArray(),
            networks = MetricReader.Devices(ctx, DeviceClass.Network).Select(NetworkSummary).ToArray(),
        };
    }

    [McpServerTool(Name = "get_gpu_status")]
    [Description("GPU·NPU 어댑터 상태. adapterKey 나 adapterIndex 를 생략하면 전 어댑터를 돌려준다. 전력 한도(powerLimitWatts·powerLimitPercent)와 클럭 제한 사유(throttling·limitReasons)를 포함한다 — 제한에 걸려 있던 시간 비율은 GpuThrottlePower·GpuThrottleThermal·GpuThrottleOther 의 구간 평균이다. aiSignals 는 이 어댑터에서 AI 작업이 어느 지표에 나타나는지 알려 준다(예: HAGS 가 켜진 NVIDIA 는 CUDA 가 Compute 가 아니라 3D 로 잡힌다).")]
    public object GetGpuStatus(
        [Description("어댑터 키(LUID). 인덱스보다 안정적이므로 재조회에는 이쪽을 쓴다.")] string? adapterKey = null,
        [Description("어댑터 인덱스. 장치 구성이 바뀌면 달라질 수 있다.")] int? adapterIndex = null,
        [Description("true 면 엔진 계열(3D·Compute·Copy·Video) 사용률, 엔진 종류별 원값, 부가 정보까지 포함한다.")] bool verbose = false)
    {
        var devices = Target(DeviceClass.Gpu, adapterKey, adapterIndex, out object? error);
        if (error is not null) return error;

        return new
        {
            header = MetricReader.Header(ctx, devices.Any(d => MetricReader.IsStale(ctx, d))),
            adapters = devices.Select(d => GpuDetail(d, verbose)).ToArray(),
        };
    }

    [McpServerTool(Name = "get_disk_status")]
    [Description("물리 디스크별 읽기·쓰기 처리량과 활성 비율. 매체(SSD/HDD)와 버스를 함께 준다.")]
    public object GetDiskStatus(
        [Description("디스크 키(시리얼).")] string? diskKey = null,
        [Description("디스크 인덱스.")] int? diskIndex = null)
    {
        var devices = Target(DeviceClass.Disk, diskKey, diskIndex, out object? error);
        if (error is not null) return error;

        return new
        {
            header = MetricReader.Header(ctx, devices.Any(d => MetricReader.IsStale(ctx, d))),
            disks = devices.Select(d => new
            {
                device = McpJsonHelpers.Ref(d),
                medium = d.Info.Icon switch
                {
                    IconKind.DiskSsd => "ssd",
                    IconKind.DiskHdd => "hdd",
                    _ => "unknown",
                },
                read = RateBytes(d, MetricKind.DiskRead),
                write = RateBytes(d, MetricKind.DiskWrite),
                activePercent = MetricReader.Latest(ctx, d, MetricKind.DiskActive),
                queueLength = MetricReader.Latest(ctx, d, MetricKind.DiskQueue),
                latencyMs = MetricReader.Latest(ctx, d, MetricKind.DiskLatency),
                info = d.Info.Extra,
            }).ToArray(),
        };
    }

    [McpServerTool(Name = "get_network_interfaces")]
    [Description("네트워크 인터페이스별 수신·송신 속도와 링크 정보. 터널은 기본적으로 제외된다.")]
    public object GetNetworkInterfaces(
        [Description("true 면 VPN 등 터널 인터페이스도 포함한다. 물리 인터페이스와 트래픽이 중복 계산될 수 있다.")]
        bool includeTunnels = false)
    {
        var devices = MetricReader.Devices(ctx, DeviceClass.Network)
            .Where(d => includeTunnels || d.Info.Icon != IconKind.NetTunnel)
            .ToArray();

        return new
        {
            header = MetricReader.Header(ctx, devices.Any(d => MetricReader.IsStale(ctx, d))),
            // 앱이 터널을 수집하지 않도록 설정돼 있으면 includeTunnels 를 켜도 나오지 않는다.
            // 요청과 결과가 다를 수 있다는 것을 숨기지 않는다.
            tunnelsIncluded = includeTunnels,
            interfaces = devices.Select(d => new
            {
                device = McpJsonHelpers.Ref(d),
                kind = d.Info.Icon switch
                {
                    IconKind.NetEthernet => "ethernet",
                    IconKind.NetWiFi => "wifi",
                    IconKind.NetCellular => "cellular",
                    IconKind.NetTunnel => "tunnel",
                    _ => "unknown",
                },
                receive = RateBytes(d, MetricKind.NetRx),
                transmit = RateBytes(d, MetricKind.NetTx),
                info = d.Info.Extra,
            }).ToArray(),
        };
    }

    [McpServerTool(Name = "get_metric_history")]
    [Description("한 지표의 최근 시계열. 실측 표본만 시각과 함께 준다. maxPoints 보다 많으면 시간으로 등분해 칸마다 평균·최소·최대를 준다(스파이크는 max 에 남는다).")]
    public object GetMetricHistory(
        [Description("지표 이름. 예: CpuTotal, GpuUtil, GpuCompute, Gpu3D, GpuCopy, GpuVideo, GpuMemBusy, GpuTemp, DiskRead, NetRx. AI 부하가 어느 지표에 실리는지는 get_gpu_status 의 aiSignals 를 본다.")] string metric,
        [Description("장치 키. 시스템 지표(CpuTotal 등)는 생략한다.")] string? deviceKey = null,
        [Description("조회 구간(초). 최대 900.")] int windowSeconds = 60,
        [Description("최대 점 개수. 실측 표본이 이보다 많으면 정확히 이 개수의 칸으로 등분한다. 최대 500.")] int maxPoints = 200)
    {
        if (!Enum.TryParse<MetricKind>(metric, ignoreCase: true, out var kind))
            return Error("unknown_metric", $"'{metric}' 은 알 수 없는 지표다. MetricKind 이름을 쓴다.");

        var device = MetricReader.Find(ctx, kind.Class(), deviceKey, null)
                     ?? MetricReader.Devices(ctx, kind.Class()).FirstOrDefault(d => d.SlotOf(kind) >= 0);

        if (device is null) return Error("device_not_found", $"{kind} 를 가진 장치가 없다.");

        int slot = device.SlotOf(kind);
        if (slot < 0) return Error("metric_not_available", $"{device.Info.ShortName} 은 {kind} 를 제공하지 않는다.");

        windowSeconds = Math.Clamp(windowSeconds, 1, 900);
        maxPoints = Math.Clamp(maxPoints, 2, 500);

        var history = MetricReader.History(ctx, slot, TimeSpan.FromSeconds(windowSeconds), maxPoints);
        bool any = history.OffsetsMs.Length > 0;

        return new
        {
            header = MetricReader.Header(ctx, MetricReader.IsStale(ctx, device)),
            metric = kind.ToString(),
            device = McpJsonHelpers.Ref(device),
            unit = McpJsonHelpers.UnitName(kind.Unit()),
            // 점의 시각 = startAt + offsetsMs[i]. 칸이면 칸의 시작 시각이다.
            startAt = any ? McpJsonHelpers.Iso(history.StartUtcTicks) : null,
            endAt = any ? McpJsonHelpers.Iso(history.EndUtcTicks) : null,
            mode = history.Mode,
            bucketMs = history.BucketMs,
            // 샘플 엔진의 틱 주기와 이 지표가 실제로 새 값을 내는 주기는 다르다.
            // 1초에 한 번 읽는 지표는 여기가 1000 근처다.
            samplePeriodMs = ctx.Engine.EffectiveFastPeriod.TotalMilliseconds,
            measuredPeriodMs = history.MeasuredPeriodMs,
            measuredSamples = history.MeasuredCount,
            pointCount = history.OffsetsMs.Length,
            offsetsMs = history.OffsetsMs,
            avg = history.Avg,
            min = history.Min,
            max = history.Max,
            samples = history.Samples,
        };
    }

    [McpServerTool(Name = "get_stats_since_reset")]
    [Description("MCP 스코프의 리셋 이후 누적 통계. 화면의 리셋 버튼과는 독립된 구간이다. p50·p95·p99 는 구간이 15분 안이면 정확값(quantilesExact=true), 백분율 지표는 문턱 이상 비율(saturatedFraction)을 함께 준다. coverage 는 읽어 본 실측 중 값을 얻은 비율 — 1 보다 작으면 통계는 구간 일부만의 것이다.")]
    public object GetStatsSinceReset(
        [Description("지표 이름. 생략하면 전 지표.")] string? metric = null,
        [Description("장치 키. 생략하면 전 장치.")] string? deviceKey = null,
        [Description("포화로 칠 사용률 문턱(%). 백분율 지표의 saturatedFraction 에 쓰인다.")]
        double saturationThreshold = MetricReader.DefaultSaturationThreshold)
    {
        MetricKind? kind = null;
        if (!string.IsNullOrWhiteSpace(metric))
        {
            if (!Enum.TryParse<MetricKind>(metric, ignoreCase: true, out var parsed))
                return Error("unknown_metric", $"'{metric}' 은 알 수 없는 지표다.");
            kind = parsed;
        }

        var blocks = new List<StatsBlock>();
        foreach (var device in ctx.Registry.ActiveDevices.OrderBy(d => d.Info.Class).ThenBy(d => d.Index))
        {
            if (deviceKey is not null &&
                !string.Equals(device.Key, deviceKey, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var k in device.Kinds.OrderBy(x => x))
            {
                if (kind is { } want && k != want) continue;
                blocks.Add(MetricReader.Stats(ctx, device, k, saturationThreshold));
            }
        }

        if (blocks.Count == 0) return Error("no_match", "조건에 맞는 지표가 없다.");

        return new { header = MetricReader.Header(ctx), scope = "mcp", stats = blocks };
    }

    [McpServerTool(Name = "reset_stats")]
    [Description("MCP 스코프의 통계 기준점을 지금으로 옮긴다(전 지표). 화면 통계는 건드리지 않는다. 직전 구간을 돌려주며, metric·deviceKey 로 돌려받을 범위를 좁히거나 includePrevious=false 로 생략할 수 있다.")]
    public object ResetStats(
        [Description("실수 방지를 위한 확인. true 여야 실행된다.")] bool confirm = false,
        [Description("false 면 직전 구간 통계를 돌려주지 않는다. 리셋만 필요할 때 응답을 줄인다.")] bool includePrevious = true,
        [Description("직전 구간을 이 지표만 돌려준다. 리셋 자체는 전 지표에 적용된다.")] string? metric = null,
        [Description("직전 구간을 이 장치만 돌려준다. 리셋 자체는 전 장치에 적용된다.")] string? deviceKey = null)
    {
        if (!confirm)
            return Error("confirmation_required",
                "리셋은 되돌릴 수 없다. 직전 구간 통계가 필요하면 먼저 get_stats_since_reset 을 부른다. confirm: true 로 다시 호출한다.");

        MetricKind? want = null;
        if (!string.IsNullOrWhiteSpace(metric))
        {
            if (!Enum.TryParse<MetricKind>(metric, ignoreCase: true, out var parsed))
                return Error("unknown_metric", $"'{metric}' 은 알 수 없는 지표다.");
            want = parsed;
        }

        // 지우기 전에 찍어둔다. 리셋의 목적은 새 구간을 시작하는 것이지 과거를 잃는 것이 아니다.
        // 걸러도 리셋은 전부에 건다 — 지표마다 기준점이 다르면 구간끼리 비교할 수 없게 된다.
        var previous = new List<StatsBlock>();
        int omittedEmpty = 0;
        if (includePrevious)
        {
            foreach (var device in ctx.Registry.ActiveDevices.OrderBy(d => d.Info.Class).ThenBy(d => d.Index))
            {
                if (deviceKey is not null &&
                    !string.Equals(device.Key, deviceKey, StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var kind in device.Kinds.OrderBy(x => x))
                {
                    if (want is { } w && kind != w) continue;

                    var block = MetricReader.Stats(ctx, device, kind);
                    // 한 번도 측정되지 않은 지표(PDH 만 붙은 어댑터의 온도 등)는 빈 블록뿐이다.
                    if (block.SampleCount == 0) { omittedEmpty++; continue; }
                    previous.Add(block);
                }
            }
        }

        long now = DateTime.UtcNow.Ticks;
        ctx.Registry.ResetAllStats(McpContext.Scope, now);

        return new
        {
            header = MetricReader.Header(ctx),
            scope = "mcp",
            resetAt = McpJsonHelpers.Iso(now),
            note = "화면(UI) 통계는 그대로다. 두 채널은 §10 에 따라 분리되어 있다.",
            previousInterval = includePrevious ? previous : null,
            // 표본이 하나도 없어 뺀 블록 수. 0 이 아니면 그만큼의 지표가 이 구간에 값을 내지 않았다.
            omittedEmpty = includePrevious ? omittedEmpty : (int?)null,
        };
    }

    [McpServerTool(Name = "describe_capabilities")]
    [Description("무엇을 관측할 수 있는지. 어댑터별 센서 계층, 장치 목록, 샘플 주기, devicesRevision.")]
    public object DescribeCapabilities()
    {
        var layers = ctx.TelemetryLayers?.Invoke() ?? new Dictionary<string, string>();

        return new
        {
            header = MetricReader.Header(ctx),
            sampling = new
            {
                periodMs = ctx.SamplePeriod.TotalMilliseconds,
                bufferPoints = ctx.Registry.SeriesCapacity,
                bufferSeconds = Math.Round(ctx.Registry.SeriesCapacity * ctx.SamplePeriod.TotalSeconds, 1),

                // 지금 실제로 도는 주기. 창이 최소화됐거나 기기가 버거우면 설계 주기보다 느리다 (§6.3).
                // 이게 없으면 에이전트는 값이 성긴 이유를 알 수 없고 "장치가 조용하다"로 오해한다.
                effectivePeriodMs = ctx.Engine.EffectiveFastPeriod.TotalMilliseconds,
                pace = ctx.Engine.EffectivePace.ToString().ToLowerInvariant(),
            },
            // 계층 A(PDH)는 어디서나 되고, 계층 B(벤더 SDK)가 붙은 어댑터만 온도·전력·클럭이 나온다.
            // 값이 비는 이유를 에이전트가 스스로 알 수 있어야 한다.
            // 키는 장치 키다. 같은 모델 두 장이면 이름이 겹친다.
            // "PDH · 대기 중" 은 장치가 절전이라 벤더 경로를 아직 열지 않은 상태 — 깨어나면 바뀐다.
            telemetryLayers = layers,
            processes = ctx.Processes is null ? "unavailable" : "on_demand",
            devices = ctx.Registry.ActiveDevices
                .OrderBy(d => d.Info.Class).ThenBy(d => d.Index)
                .Select(d => new
                {
                    device = McpJsonHelpers.Ref(d),
                    deviceClass = d.Info.Class.ToString().ToLowerInvariant(),
                    icon = d.Info.Icon.ToString(),
                    vendor = d.Info.Vendor,
                    metrics = d.Kinds.OrderBy(k => k).Select(k => k.ToString()).ToArray(),
                    // GPU·NPU 만. AI 작업이 이 어댑터의 어느 지표에 실리는지 — 제조사마다 다르다.
                    aiSignals = d.Info.Class == DeviceClass.Gpu ? AiSignals.For(d, ctx.EngineBreakdown?.Invoke(d.Key)?.Broken) : null,
                    info = d.Info.Extra,
                }).ToArray(),
        };
    }

    // ── 공통 ────────────────────────────────────────────────────

    /// <summary>
    /// 오류는 MCP 규약대로 구조화해서 돌려준다. 문자열로 던지면 에이전트가
    /// "값이 0" 과 "값을 못 구했다" 를 구분하지 못한다.
    /// </summary>
    internal static object Error(string code, string hint) => new { error = code, hint };

    private IReadOnlyList<DeviceHandle> Target(
        DeviceClass klass, string? key, int? index, out object? error)
    {
        error = null;

        if (key is null && index is null) return MetricReader.Devices(ctx, klass);

        var found = MetricReader.Find(ctx, klass, key, index);
        if (found is not null) return [found];

        error = Error("device_not_found",
            $"{klass} 에서 {(key is not null ? $"key '{key}'" : $"index {index}")} 를 찾지 못했다. " +
            "describe_capabilities 로 현재 장치 목록을 확인한다.");
        return [];
    }

    private object RateBytes(DeviceHandle device, MetricKind kind)
    {
        double? value = MetricReader.Latest(ctx, device, kind);
        return new
        {
            bytesPerSecond = value,
            text = value is null ? "—" : ByteValue.From(value.Value).Text + "/s",
        };
    }

    private object GpuSummary(DeviceHandle d) => new
    {
        device = McpJsonHelpers.Ref(d),
        kind = MetricReader.IsNpu(d) ? "npu" : "gpu",
        utilizationPercent = MetricReader.Latest(ctx, d, MetricKind.GpuUtil),
        dedicatedMemory = MetricReader.LatestBytes(ctx, d, MetricKind.GpuDedicated),
        temperatureCelsius = MetricReader.Latest(ctx, d, MetricKind.GpuTemp),
    };

    private object GpuDetail(DeviceHandle d, bool verbose)
    {
        double? dedicated = MetricReader.Latest(ctx, d, MetricKind.GpuDedicated);
        double? capacity = ParseCapacity(d);
        var engines = ctx.EngineBreakdown?.Invoke(d.Key);
        var broken = engines?.Broken is { Count: > 0 } b ? b : null;

        return new
        {
            device = McpJsonHelpers.Ref(d),
            kind = MetricReader.IsNpu(d) ? "npu" : "gpu",
            vendor = d.Info.Vendor,
            utilizationPercent = MetricReader.Latest(ctx, d, MetricKind.GpuUtil),
            computePercent = MetricReader.Latest(ctx, d, MetricKind.GpuCompute),
            // 메모리 컨트롤러가 VRAM 을 읽고 쓴 시간 비율(NVML 만). 대역폭에 묶인 AI 부하를 가른다.
            memoryBusyPercent = MetricReader.Latest(ctx, d, MetricKind.GpuMemBusy),
            dedicatedMemory = MetricReader.LatestBytes(ctx, d, MetricKind.GpuDedicated),
            sharedMemory = MetricReader.LatestBytes(ctx, d, MetricKind.GpuShared),
            // 전용 VRAM 을 넘겨 공유 메모리로 흘러나가는 중인가. GPU 워크로드가 느려지는
            // 가장 흔한 원인이라 별도 플래그로 세운다.
            vramExceeded = dedicated is not null && capacity is not null && dedicated > capacity * 0.98,
            temperatureCelsius = MetricReader.Latest(ctx, d, MetricKind.GpuTemp),
            powerWatts = MetricReader.Latest(ctx, d, MetricKind.GpuPower),
            // 드라이버가 강제하는 전력 한도(NVML). 전력이 여기에 붙어 있으면 클럭이 깎인다.
            powerLimitWatts = MetricReader.Latest(ctx, d, MetricKind.GpuPowerLimit),
            powerLimitPercent = PowerLimitPercent(d),
            // 지금 클럭을 막고 있는 것. throttling 은 성능을 깎는 사유(전력·온도·전압 등)가 하나라도 섰는가,
            // limitReasons 는 서 있는 사유 전부다 — idle·lowUtilization 은 할 일이 없어 클럭이 낮은 것뿐이다.
            throttling = ctx.LimitReasons?.Invoke(d.Key) is { } reasons
                ? (reasons & GpuLimitReasonsExtensions.Bottlenecks) != 0
                : (bool?)null,
            limitReasons = ctx.LimitReasons?.Invoke(d.Key)?.Names(),
            clockMegahertz = MetricReader.Latest(ctx, d, MetricKind.GpuClock),
            telemetryLayer = ctx.TelemetryLayers?.Invoke().GetValueOrDefault(d.Key) ?? "PDH",

            // 저전력 대기 중이면 온도·전력·클럭이 비는데, 이는 고장이 아니라 의도다 —
            // 잠든 장치를 깨워 가며 값을 읽지 않는다 (§6.3). 이 필드가 없으면
            // 에이전트는 null 만 보고 "센서가 죽었다"고 판단한다.
            availability = d.Availability.ToString().ToLowerInvariant(),
            stale = MetricReader.IsStale(ctx, d),
            // AI 작업이 어느 지표에 실리는가. 같은 추론이 GPU 마다 다른 엔진으로 잡힌다(§5.4).
            aiSignals = AiSignals.For(d, broken),
            // 드라이버가 누적값을 거꾸로 돌려 PDH 카운터가 깨진 엔진 종류. 그 계열은 0 이 아니라 null(측정 불가)이다.
            // verbose 가 아니어도 낸다 — computePercent 가 null 인 이유가 여기 있다.
            brokenEngineCounters = broken,
            engines = verbose ? EngineFamilies(d) : null,
            // 엔진 종류(engtype)별 원값. 그룹 안은 합, 사용률 정의는 그룹끼리 최댓값이다. 이번 값을 못 쓴 종류는 null(측정 불가).
            engineTypes = verbose ? engines?.Types : null,
            info = verbose ? d.Info.Extra : null,
        };
    }

    /// <summary>전력 ÷ 한도(%). 둘 중 하나라도 없으면 null.</summary>
    private double? PowerLimitPercent(DeviceHandle d) =>
        MetricReader.Latest(ctx, d, MetricKind.GpuPower) is { } watts
        && MetricReader.Latest(ctx, d, MetricKind.GpuPowerLimit) is { } limit && limit > 0
            ? Math.Round(watts / limit * 100, 1)
            : null;

    /// <summary>
    /// 엔진 계열별 사용률(%). 시계열·구간 통계가 있는 지표와 같은 값이다 —
    /// 여기서 본 계열 이름으로 <c>get_metric_history</c> 를 부르면 그대로 이력이 나온다.
    /// </summary>
    private Dictionary<string, double?> EngineFamilies(DeviceHandle d)
    {
        var result = new Dictionary<string, double?>();
        foreach (var (name, kind) in (ReadOnlySpan<(string, MetricKind)>)
                 [("3D", MetricKind.Gpu3D), ("Compute", MetricKind.GpuCompute),
                  ("Copy", MetricKind.GpuCopy), ("Video", MetricKind.GpuVideo)])
        {
            if (d.SlotOf(kind) >= 0) result[name] = MetricReader.Latest(ctx, d, kind);
        }
        return result;
    }

    private object DiskSummary(DeviceHandle d) => new
    {
        device = McpJsonHelpers.Ref(d),
        medium = d.Info.Icon == IconKind.DiskSsd ? "ssd" : d.Info.Icon == IconKind.DiskHdd ? "hdd" : "unknown",
        activePercent = MetricReader.Latest(ctx, d, MetricKind.DiskActive),
        read = RateBytes(d, MetricKind.DiskRead),
        write = RateBytes(d, MetricKind.DiskWrite),
    };

    private object NetworkSummary(DeviceHandle d) => new
    {
        device = McpJsonHelpers.Ref(d),
        receive = RateBytes(d, MetricKind.NetRx),
        transmit = RateBytes(d, MetricKind.NetTx),
    };

    /// <summary>어댑터 전용 메모리 용량. 프로바이더가 부가 정보에 넣어둔 값을 쓴다.</summary>
    private static double? ParseCapacity(DeviceHandle d)
    {
        foreach (var (key, value) in d.Info.Extra)
        {
            if (!key.Contains("dedicated", StringComparison.OrdinalIgnoreCase) &&
                !key.Contains("vram", StringComparison.OrdinalIgnoreCase)) continue;

            if (double.TryParse(value, out double parsed)) return parsed;
        }

        return null;
    }
}

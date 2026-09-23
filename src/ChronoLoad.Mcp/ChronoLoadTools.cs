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
    [Description("GPU·NPU 어댑터 상태. adapterKey 나 adapterIndex 를 생략하면 전 어댑터를 돌려준다.")]
    public object GetGpuStatus(
        [Description("어댑터 키(LUID). 인덱스보다 안정적이므로 재조회에는 이쪽을 쓴다.")] string? adapterKey = null,
        [Description("어댑터 인덱스. 장치 구성이 바뀌면 달라질 수 있다.")] int? adapterIndex = null,
        [Description("true 면 엔진 그룹 분해와 부가 정보까지 포함한다.")] bool verbose = false)
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
    [Description("한 지표의 최근 시계열. 스파이크가 사라지지 않도록 min-max 데시메이션으로 줄인다.")]
    public object GetMetricHistory(
        [Description("지표 이름. 예: CpuTotal, GpuUtil, GpuTemp, DiskRead, NetRx.")] string metric,
        [Description("장치 키. 시스템 지표(CpuTotal 등)는 생략한다.")] string? deviceKey = null,
        [Description("조회 구간(초). 최대 900.")] int windowSeconds = 60,
        [Description("최대 점 개수. 최대 500.")] int maxPoints = 200)
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

        int windowSamples = (int)Math.Ceiling(windowSeconds / ctx.SamplePeriod.TotalSeconds);
        var (values, span) = MetricReader.History(ctx, slot, windowSamples, maxPoints);

        return new
        {
            header = MetricReader.Header(ctx, MetricReader.IsStale(ctx, device)),
            metric = kind.ToString(),
            device = McpJsonHelpers.Ref(device),
            unit = McpJsonHelpers.UnitName(kind.Unit()),
            // 점 하나가 몇 번의 샘플을 대표하는지. 이게 없으면 시간 축을 복원할 수 없다.
            samplesPerPoint = span,
            samplePeriodMs = ctx.SamplePeriod.TotalMilliseconds,
            pointCount = values.Length,
            values,
        };
    }

    [McpServerTool(Name = "get_stats_since_reset")]
    [Description("MCP 스코프의 리셋 이후 누적 통계. 화면의 리셋 버튼과는 독립된 구간이다.")]
    public object GetStatsSinceReset(
        [Description("지표 이름. 생략하면 전 지표.")] string? metric = null,
        [Description("장치 키. 생략하면 전 장치.")] string? deviceKey = null)
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
                blocks.Add(MetricReader.Stats(ctx, device, k));
            }
        }

        if (blocks.Count == 0) return Error("no_match", "조건에 맞는 지표가 없다.");

        return new { header = MetricReader.Header(ctx), scope = "mcp", stats = blocks };
    }

    [McpServerTool(Name = "reset_stats")]
    [Description("MCP 스코프의 통계 기준점을 지금으로 옮긴다. 화면 통계는 건드리지 않는다. 직전 구간을 반환한다.")]
    public object ResetStats(
        [Description("실수 방지를 위한 확인. true 여야 실행된다.")] bool confirm = false)
    {
        if (!confirm)
            return Error("confirmation_required",
                "리셋은 되돌릴 수 없다. 직전 구간 통계가 필요하면 먼저 get_stats_since_reset 을 부른다. confirm: true 로 다시 호출한다.");

        // 지우기 전에 찍어둔다. 리셋의 목적은 새 구간을 시작하는 것이지 과거를 잃는 것이 아니다.
        var previous = new List<StatsBlock>();
        foreach (var device in ctx.Registry.ActiveDevices.OrderBy(d => d.Info.Class).ThenBy(d => d.Index))
        foreach (var kind in device.Kinds.OrderBy(x => x))
            previous.Add(MetricReader.Stats(ctx, device, kind));

        long now = DateTime.UtcNow.Ticks;
        ctx.Registry.ResetAllStats(McpContext.Scope, now);

        return new
        {
            header = MetricReader.Header(ctx),
            scope = "mcp",
            resetAt = McpJsonHelpers.Iso(now),
            note = "화면(UI) 통계는 그대로다. 두 채널은 §10 에 따라 분리되어 있다.",
            previousInterval = previous,
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

        return new
        {
            device = McpJsonHelpers.Ref(d),
            kind = MetricReader.IsNpu(d) ? "npu" : "gpu",
            vendor = d.Info.Vendor,
            utilizationPercent = MetricReader.Latest(ctx, d, MetricKind.GpuUtil),
            computePercent = MetricReader.Latest(ctx, d, MetricKind.GpuCompute),
            dedicatedMemory = MetricReader.LatestBytes(ctx, d, MetricKind.GpuDedicated),
            sharedMemory = MetricReader.LatestBytes(ctx, d, MetricKind.GpuShared),
            // 전용 VRAM 을 넘겨 공유 메모리로 흘러나가는 중인가. GPU 워크로드가 느려지는
            // 가장 흔한 원인이라 별도 플래그로 세운다.
            vramExceeded = dedicated is not null && capacity is not null && dedicated > capacity * 0.98,
            temperatureCelsius = MetricReader.Latest(ctx, d, MetricKind.GpuTemp),
            powerWatts = MetricReader.Latest(ctx, d, MetricKind.GpuPower),
            clockMegahertz = MetricReader.Latest(ctx, d, MetricKind.GpuClock),
            telemetryLayer = ctx.TelemetryLayers?.Invoke().GetValueOrDefault(d.Info.ShortName) ?? "PDH",

            // 저전력 대기 중이면 온도·전력·클럭이 비는데, 이는 고장이 아니라 의도다 —
            // 잠든 장치를 깨워 가며 값을 읽지 않는다 (§6.3). 이 필드가 없으면
            // 에이전트는 null 만 보고 "센서가 죽었다"고 판단한다.
            availability = d.Availability.ToString().ToLowerInvariant(),
            stale = MetricReader.IsStale(ctx, d),
            info = verbose ? d.Info.Extra : null,
        };
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

using System.Text.Json;
using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Metrics;
using ChronoLoad.Core.Sampling;
using ChronoLoad.Mcp;

namespace ChronoLoad.Core.Tests;

/// <summary>
/// MCP 툴이 돌려주는 내용의 계약 (§10). 값 자체보다 <b>에이전트가 오해할 수 있는 지점</b>을 본다.
/// </summary>
public class McpToolTests
{
    private static (McpContext Ctx, MetricRegistry Registry, DeviceHandle Gpu) Build()
    {
        var registry = new MetricRegistry(seriesCapacity: 256);
        var engine = new SampleEngine(registry);

        registry.Register(new DeviceInfo("cpu", DeviceClass.System, "CPU", "테스트 CPU", IconKind.Cpu),
            [MetricKind.CpuTotal]);

        var gpu = registry.Register(
            new DeviceInfo("gpu:luid_1", DeviceClass.Gpu, "RTX 9999", "테스트 GPU · 외장", IconKind.GpuNvidia),
            [MetricKind.GpuUtil, MetricKind.GpuDedicated, MetricKind.GpuTemp]);

        var ctx = new McpContext(registry, engine)
        {
            TelemetryLayers = () => new Dictionary<string, string> { ["RTX 9999"] = "NVML" },
            SamplePeriod = TimeSpan.FromMilliseconds(250),
        };

        return (ctx, registry, gpu);
    }

    private static JsonElement Json(object value) =>
        JsonSerializer.SerializeToElement(value);

    [Fact]
    public void Every_response_carries_the_devices_revision()
    {
        var (ctx, registry, _) = Build();
        var tools = new ChronoLoadTools(ctx);

        int before = Json(tools.GetSystemSnapshot()).GetProperty("header")
            .GetProperty("devicesRevision").GetInt32();

        registry.Register(new DeviceInfo("gpu:luid_2", DeviceClass.Gpu, "두 번째", "두 번째 GPU",
            IconKind.GpuAmd), [MetricKind.GpuUtil]);

        int after = Json(tools.GetSystemSnapshot()).GetProperty("header")
            .GetProperty("devicesRevision").GetInt32();

        // 에이전트는 이 값만 비교해 "내가 알던 구성이 그대로인가"를 판단한다 (§10.3).
        Assert.True(after > before);
    }

    [Fact]
    public void Missing_values_are_null_not_zero()
    {
        var (ctx, _, _) = Build();

        // 아직 아무 값도 커밋하지 않았다. 0 으로 채워 내보내면 에이전트가
        // "GPU 가 놀고 있다" 고 읽는다 — 사실은 "모른다" 다.
        var adapter = Json(new ChronoLoadTools(ctx).GetGpuStatus())
            .GetProperty("adapters")[0];

        Assert.Equal(JsonValueKind.Null, adapter.GetProperty("utilizationPercent").ValueKind);
        Assert.Equal(JsonValueKind.Null, adapter.GetProperty("temperatureCelsius").ValueKind);
    }

    [Fact]
    public void Reset_is_refused_without_confirmation_and_keeps_the_interval()
    {
        var (ctx, registry, gpu) = Build();
        var tools = new ChronoLoadTools(ctx);

        registry.PushFrame([10f, 50f, 0f, 60f]);
        registry.PushFrame([20f, 70f, 0f, 62f]);

        var refused = Json(tools.ResetStats());
        Assert.Equal("confirmation_required", refused.GetProperty("error").GetString());

        // 거절됐으므로 통계는 그대로여야 한다.
        long before = registry.StatsSnapshot(gpu.SlotOf(MetricKind.GpuUtil), StatsScope.Mcp).Count;
        Assert.Equal(2, before);

        var done = Json(tools.ResetStats(confirm: true));

        // 리셋의 목적은 새 구간을 시작하는 것이지 과거를 잃는 것이 아니다.
        Assert.True(done.GetProperty("previousInterval").GetArrayLength() > 0);
        Assert.Equal(0, registry.StatsSnapshot(gpu.SlotOf(MetricKind.GpuUtil), StatsScope.Mcp).Count);
    }

    [Fact]
    public void Mcp_reset_does_not_touch_the_screen_statistics()
    {
        var (ctx, registry, gpu) = Build();
        int slot = gpu.SlotOf(MetricKind.GpuUtil);

        registry.PushFrame([10f, 50f, 0f, 60f]);
        registry.PushFrame([20f, 70f, 0f, 62f]);

        new ChronoLoadTools(ctx).ResetStats(confirm: true);

        // 두 채널은 분리되어 있다 (R16). 에이전트의 리셋이 사용자의 측정 구간을 지우면 안 된다.
        Assert.Equal(0, registry.StatsSnapshot(slot, StatsScope.Mcp).Count);
        Assert.Equal(2, registry.StatsSnapshot(slot, StatsScope.Ui).Count);
    }

    [Fact]
    public void History_keeps_the_spike_when_it_decimates()
    {
        var (ctx, registry, gpu) = Build();
        int slot = gpu.SlotOf(MetricKind.GpuUtil);

        // 평평한 구간 한가운데 스파이크 하나. 평균으로 줄이면 사라지는 모양이다.
        for (int i = 0; i < 120; i++)
            registry.PushFrame([0f, i == 60 ? 100f : 5f, 0f, 0f]);

        var result = Json(new ChronoLoadTools(ctx).GetMetricHistory(
            "GpuUtil", deviceKey: gpu.Key, windowSeconds: 60, maxPoints: 10));

        var values = result.GetProperty("values").EnumerateArray().Select(v => v.GetDouble()).ToArray();

        Assert.True(values.Length <= 10);
        Assert.Contains(values, v => v >= 99);      // 스파이크가 살아남아야 한다
        Assert.Equal("percent", result.GetProperty("unit").GetString());
        Assert.Equal(slot, gpu.SlotOf(MetricKind.GpuUtil));
    }

    [Fact]
    public void Unknown_metric_and_device_produce_structured_errors()
    {
        var (ctx, _, _) = Build();
        var tools = new ChronoLoadTools(ctx);

        Assert.Equal("unknown_metric",
            Json(tools.GetMetricHistory("Nonsense")).GetProperty("error").GetString());

        Assert.Equal("device_not_found",
            Json(tools.GetGpuStatus(adapterKey: "gpu:없음")).GetProperty("error").GetString());
    }

    [Fact]
    public void Device_entries_expose_a_stable_key_alongside_the_index()
    {
        var (ctx, _, gpu) = Build();

        var device = Json(new ChronoLoadTools(ctx).GetGpuStatus())
            .GetProperty("adapters")[0].GetProperty("device");

        // 인덱스는 순서가 바뀔 수 있으므로 재조회에는 키를 쓴다 (§10.3).
        Assert.Equal(gpu.Key, device.GetProperty("key").GetString());
        Assert.Equal(gpu.Index, device.GetProperty("index").GetInt32());
    }

    [Fact]
    public void Process_tools_report_unavailable_rather_than_an_empty_list()
    {
        var (ctx, _, _) = Build();

        // 프로세스 소스가 없다. 빈 배열을 주면 "프로세스가 하나도 없다"로 읽힌다.
        Assert.Equal("processes_unavailable",
            Json(new ProcessTools(ctx).ListProcesses()).GetProperty("error").GetString());
    }
}

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
            TelemetryLayers = () => new Dictionary<string, string> { ["gpu:luid_1"] = "NVML" },
            SamplePeriod = TimeSpan.FromMilliseconds(250),
        };

        return (ctx, registry, gpu);
    }

    private static JsonElement Json(object value) =>
        JsonSerializer.SerializeToElement(value);

    /// <summary>
    /// 센서가 값을 내주지 않은 틱. 시리즈에는 NaN 이 기록되지만 <b>실측으로는 세지 않는다</b> —
    /// 실기기에서 온도 슬롯이 이 상태로 남는다(§5.4 계층 B 가 온도 센서를 0개로 돌려주는 경우).
    /// </summary>
    private static void PushWithMissingTemperature(MetricRegistry registry, float util) =>
        registry.CommitAll([0f, util, 0f, float.NaN], [true, true, true, false]);

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

    /// <summary>250ms 간격 시각을 붙여 한 프레임을 민다. 시간 축이 있어야 이력의 시각을 검증할 수 있다.</summary>
    private static readonly long T0 = new DateTime(2026, 9, 27, 7, 0, 0, DateTimeKind.Utc).Ticks;

    private static void PushAt(MetricRegistry registry, int frame, float[] values, bool[]? measured = null) =>
        registry.CommitAll(values, measured ?? [], T0 + frame * TimeSpan.TicksPerMillisecond * 250);

    private static double?[] Doubles(JsonElement array) =>
        array.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Null ? (double?)null : v.GetDouble()).ToArray();

    [Fact]
    public void History_keeps_the_spike_when_it_buckets()
    {
        var (ctx, registry, gpu) = Build();
        int slot = gpu.SlotOf(MetricKind.GpuUtil);

        // 평평한 구간 한가운데 스파이크 하나. 평균으로 줄이면 사라지는 모양이다.
        for (int i = 0; i < 120; i++)
            PushAt(registry, i, [0f, i == 60 ? 100f : 5f, 0f, 0f]);

        var result = Json(new ChronoLoadTools(ctx).GetMetricHistory(
            "GpuUtil", deviceKey: gpu.Key, windowSeconds: 60, maxPoints: 10));

        Assert.Equal("bucketed", result.GetProperty("mode").GetString());
        Assert.Contains(Doubles(result.GetProperty("max")), v => v >= 99);   // 스파이크가 살아남아야 한다
        Assert.Equal("percent", result.GetProperty("unit").GetString());
        Assert.Equal(slot, gpu.SlotOf(MetricKind.GpuUtil));
    }

    /// <summary>
    /// 실사용 보고: 요청한 점 수와 돌려받은 점 수가 달랐다. min-max 데시메이션은 칸당 0~2점을 냈다.
    /// </summary>
    [Fact]
    public void History_returns_exactly_the_requested_number_of_points_when_it_buckets()
    {
        var (ctx, registry, gpu) = Build();
        for (int i = 0; i < 200; i++) PushAt(registry, i, [0f, i % 7, 0f, 0f]);

        var result = Json(new ChronoLoadTools(ctx).GetMetricHistory(
            "GpuUtil", deviceKey: gpu.Key, windowSeconds: 60, maxPoints: 37));

        Assert.Equal(37, result.GetProperty("pointCount").GetInt32());
        foreach (string field in (string[])["offsetsMs", "avg", "min", "max", "samples"])
            Assert.Equal(37, result.GetProperty(field).GetArrayLength());

        // 칸 폭이 일정하고, 칸의 표본 수를 모두 더하면 실측 표본 수다.
        var offsets = result.GetProperty("offsetsMs").EnumerateArray().Select(v => v.GetInt64()).ToArray();
        double width = result.GetProperty("bucketMs").GetDouble();
        Assert.All(offsets.Zip(offsets.Skip(1)), p => Assert.InRange(p.Second - p.First, width - 1, width + 1));
        Assert.Equal(result.GetProperty("measuredSamples").GetInt32(),
            result.GetProperty("samples").EnumerateArray().Sum(v => v.GetInt32()));
    }

    /// <summary>
    /// 실사용 보고: 250ms 주기라고 적혀 있는데 1초에 한 번 읽는 지표는 같은 값이 네 번씩 나왔다.
    /// 유지된 칸(실측 아님)은 버리고, 시각을 붙여 실제 주기가 드러나게 한다.
    /// </summary>
    [Fact]
    public void History_drops_held_samples_and_reports_the_real_period()
    {
        var (ctx, registry, gpu) = Build();

        // 온도는 4틱에 한 번만 실측이다(Slow 티어). 나머지 세 칸은 직전 값을 유지한 것이다.
        for (int i = 0; i < 40; i++)
            PushAt(registry, i, [0f, 5f, 0f, 50f + i], [true, true, true, i % 4 == 0]);

        var result = Json(new ChronoLoadTools(ctx).GetMetricHistory(
            "GpuTemp", deviceKey: gpu.Key, windowSeconds: 60, maxPoints: 200));

        Assert.Equal("raw", result.GetProperty("mode").GetString());
        Assert.Equal(10, result.GetProperty("pointCount").GetInt32());
        Assert.Equal(1000, result.GetProperty("measuredPeriodMs").GetDouble());

        var offsets = result.GetProperty("offsetsMs").EnumerateArray().Select(v => v.GetInt64()).ToArray();
        Assert.Equal([0L, 1000, 2000, 3000], offsets[..4]);
        Assert.Equal([50.0, 54, 58, 62], Doubles(result.GetProperty("avg"))[..4].Select(v => v!.Value));

        // 첫 점의 시각이 절대 시각으로 주어진다.
        var start = DateTimeOffset.Parse(result.GetProperty("startAt").GetString()!);
        Assert.Equal(new DateTimeOffset(T0, TimeSpan.Zero), start.ToUniversalTime());
    }

    [Fact]
    public void History_of_a_metric_that_never_reported_is_empty_rather_than_zero()
    {
        // 슬롯은 등록됐지만 값이 한 번도 들어오지 않는 지표가 있다 — PDH 만 붙은 어댑터의 온도,
        // 온도 센서를 0개로 돌려주는 내장 GPU 가 그렇다. 0 으로 채우면 "0 °C" 로 읽힌다.
        var (ctx, registry, gpu) = Build();

        for (int i = 0; i < 120; i++) PushWithMissingTemperature(registry, 5f);

        var result = Json(new ChronoLoadTools(ctx).GetMetricHistory(
            "GpuTemp", deviceKey: gpu.Key, windowSeconds: 30, maxPoints: 8));

        Assert.Equal(0, result.GetProperty("pointCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("startAt").ValueKind);
        Assert.Equal("celsius", result.GetProperty("unit").GetString());
    }

    [Fact]
    public void Empty_buckets_are_null_not_zero()
    {
        var (ctx, registry, gpu) = Build();

        // 앞 절반만 값이 있고 뒤 절반은 센서가 끊겼다.
        for (int i = 0; i < 40; i++) PushAt(registry, i, [0f, 5f, 0f, 55f]);
        for (int i = 40; i < 80; i++) PushAt(registry, i, [0f, 5f, 0f, float.NaN], [true, true, true, false]);

        var result = Json(new ChronoLoadTools(ctx).GetMetricHistory(
            "GpuTemp", deviceKey: gpu.Key, windowSeconds: 60, maxPoints: 20));

        var avg = Doubles(result.GetProperty("avg"));
        Assert.Contains(avg, v => v is null);
        Assert.Contains(avg, v => v == 55);
    }

    [Fact]
    public void A_metric_that_never_reports_does_not_make_the_whole_adapter_look_stale()
    {
        // 내장 GPU 는 온도 센서를 0개로 돌려준다. 그 슬롯이 영영 비어 있다고 해서 사용률·메모리까지
        // 오래된 값으로 보이면, 에이전트는 멀쩡한 값을 의심하고 다시 묻는다.
        var (ctx, registry, _) = Build();

        for (int i = 0; i < 40; i++) PushWithMissingTemperature(registry, 20f);

        var adapter = Json(new ChronoLoadTools(ctx).GetGpuStatus())
            .GetProperty("adapters")[0];

        Assert.False(adapter.GetProperty("stale").GetBoolean());
        Assert.Equal(JsonValueKind.Null, adapter.GetProperty("temperatureCelsius").ValueKind);
    }

    [Fact]
    public void Stats_quantiles_are_exact_while_the_interval_fits_in_the_ring()
    {
        var (ctx, registry, gpu) = Build();
        new ChronoLoadTools(ctx).ResetStats(confirm: true);

        // 실사용 보고의 모양: 유휴 전력 22.5 W 근처. 예전 히스토그램은 p95 를 15.87 W 로 답했다.
        for (int i = 0; i < 100; i++) PushAt(registry, i, [0f, i, 22.5e9f + i * 1e7f, 22.5f + i * 0.01f]);

        var block = Json(new ChronoLoadTools(ctx).GetStatsSinceReset(metric: "GpuTemp"))
            .GetProperty("stats")[0];

        Assert.True(block.GetProperty("quantilesExact").GetBoolean());
        Assert.Equal(22.5f + 94 * 0.01f, block.GetProperty("p95").GetDouble(), 3);
        Assert.InRange(block.GetProperty("p95").GetDouble(),
            block.GetProperty("min").GetDouble(), block.GetProperty("max").GetDouble());

        // 온도는 백분율이 아니다 — 포화 비율은 뜻이 없으므로 싣지 않는다.
        Assert.False(block.TryGetProperty("saturatedFraction", out _));
    }

    [Fact]
    public void Percent_metrics_report_the_saturated_fraction()
    {
        var (ctx, registry, gpu) = Build();
        new ChronoLoadTools(ctx).ResetStats(confirm: true);

        // 평균 44% 인데 30% 의 시간은 100% 다. 버스트형 추론 부하의 모양이다.
        for (int i = 0; i < 100; i++) PushAt(registry, i, [0f, i % 10 < 3 ? 100f : 20f, 0f, 50f]);

        var block = Json(new ChronoLoadTools(ctx).GetStatsSinceReset(metric: "GpuUtil"))
            .GetProperty("stats")[0];

        Assert.Equal(44, block.GetProperty("avg").GetDouble(), 3);
        Assert.Equal(0.3, block.GetProperty("saturatedFraction").GetDouble(), 3);
        Assert.Equal(90, block.GetProperty("saturationThreshold").GetDouble());
    }

    /// <summary>
    /// 표본이 하나도 없는 백분율 지표(깨진 PDH 카운터라 전부 측정 불가였던 GpuCompute 등).
    /// 문턱만 있고 비율 필드가 빠지면 "모름"과 "이 지표엔 그런 필드가 없음"을 가를 수 없다.
    /// </summary>
    [Fact]
    public void An_empty_percent_metric_still_carries_the_saturated_fraction_as_null()
    {
        var (ctx, registry, _) = Build();
        new ChronoLoadTools(ctx).ResetStats(confirm: true);
        for (int i = 0; i < 10; i++) PushAt(registry, i, [0f, float.NaN, 0f, 50f]);

        var block = Json(new ChronoLoadTools(ctx).GetStatsSinceReset(metric: "GpuUtil"))
            .GetProperty("stats")[0];

        Assert.Equal(0, block.GetProperty("sampleCount").GetInt64());
        Assert.Equal(90, block.GetProperty("saturationThreshold").GetDouble());
        Assert.Equal(JsonValueKind.Null, block.GetProperty("saturatedFraction").ValueKind);
    }

    /// <summary>
    /// 130V 실측 재현: 앱이 부하 휴식 중에 켜져 휴식 3초의 0 만 표본이 되고, 부하 구간은 깨진 카운터라 전부 측정 불가였다.
    /// 포화 비율 0 만 보면 "놀았다"로 읽힌다. coverage 가 통계가 구간의 일부만의 것임을 드러낸다.
    /// </summary>
    [Fact]
    public void Coverage_shows_how_much_of_the_interval_the_statistics_stand_for()
    {
        var (ctx, registry, _) = Build();
        new ChronoLoadTools(ctx).ResetStats(confirm: true);
        // 온도는 4틱에 한 번만 실측이다(나머지는 유지값).
        for (int i = 0; i < 15; i++)
            PushAt(registry, i, [0f, i < 3 ? 0f : float.NaN, 0f, 50f], [true, true, true, i % 4 == 0]);

        var block = Json(new ChronoLoadTools(ctx).GetStatsSinceReset(metric: "GpuUtil"))
            .GetProperty("stats")[0];

        Assert.Equal(3, block.GetProperty("sampleCount").GetInt64());
        Assert.Equal(0, block.GetProperty("saturatedFraction").GetDouble());
        Assert.Equal(0.2, block.GetProperty("coverage").GetDouble(), 4);

        // 실측이 아닌(유지값) 칸은 시도로도 세지 않는다 — Slow 지표의 coverage 가 1/4 로 보이면 안 된다.
        var temp = Json(new ChronoLoadTools(ctx).GetStatsSinceReset(metric: "GpuTemp")).GetProperty("stats")[0];
        Assert.Equal(1.0, temp.GetProperty("coverage").GetDouble());
    }

    [Fact]
    public void Reset_can_skip_or_narrow_the_previous_interval()
    {
        var (ctx, registry, gpu) = Build();
        var tools = new ChronoLoadTools(ctx);
        for (int i = 0; i < 10; i++) PushAt(registry, i, [10f, 50f, 1e9f, 60f]);

        var narrowed = Json(tools.ResetStats(confirm: true, metric: "GpuUtil"));
        var previous = narrowed.GetProperty("previousInterval");
        Assert.Equal(1, previous.GetArrayLength());
        Assert.Equal("GpuUtil", previous[0].GetProperty("metric").GetString());

        // 걸러 돌려받아도 리셋은 전 지표에 걸린다 — 기준점이 지표마다 다르면 구간을 비교할 수 없다.
        Assert.Equal(0, registry.StatsSnapshot(gpu.SlotOf(MetricKind.GpuTemp), StatsScope.Mcp).Count);

        for (int i = 10; i < 20; i++) PushAt(registry, i, [10f, 50f, 1e9f, 60f]);
        var bare = Json(tools.ResetStats(confirm: true, includePrevious: false));
        Assert.Equal(JsonValueKind.Null, bare.GetProperty("previousInterval").ValueKind);
    }

    [Fact]
    public void Reset_omits_metrics_that_never_reported()
    {
        var (ctx, registry, _) = Build();
        for (int i = 0; i < 10; i++) PushWithMissingTemperature(registry, 5f);

        var done = Json(new ChronoLoadTools(ctx).ResetStats(confirm: true));

        Assert.DoesNotContain(done.GetProperty("previousInterval").EnumerateArray(),
            b => b.GetProperty("metric").GetString() == "GpuTemp");
        Assert.Equal(1, done.GetProperty("omittedEmpty").GetInt32());
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
    public async Task Process_tools_report_unavailable_rather_than_an_empty_list()
    {
        var (ctx, _, _) = Build();

        // 프로세스 소스가 없다. 빈 배열을 주면 "프로세스가 하나도 없다"로 읽힌다.
        Assert.Equal("processes_unavailable",
            Json(await new ProcessTools(ctx).ListProcesses()).GetProperty("error").GetString());
    }
}

using System.Text.Json;
using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Metrics;
using ChronoLoad.Core.Sampling;
using ChronoLoad.Mcp;

namespace ChronoLoad.Core.Tests;

/// <summary>
/// 구간 마커와 구간 통계. 실사용 보고: 벤치 단계 시각을 이력 오프셋에 손으로 맞춰야 했다.
/// </summary>
public class IntervalToolsTests
{
    private static readonly long T0 = new DateTime(2026, 9, 27, 9, 0, 0, DateTimeKind.Utc).Ticks;
    private static long At(double seconds) => T0 + (long)(seconds * TimeSpan.TicksPerSecond);

    /// <summary>250ms 마다 한 프레임. GPU 사용률이 앞 10초 10%, 뒤 10초 90~100%.</summary>
    private static (McpContext Ctx, MetricRegistry Registry, DeviceHandle Gpu) Build(int capacity = 256)
    {
        var registry = new MetricRegistry(capacity);
        var gpu = registry.Register(
            new DeviceInfo("gpu:luid_1", DeviceClass.Gpu, "GPU", "GPU", IconKind.GpuNvidia, "NVIDIA"),
            [MetricKind.GpuUtil, MetricKind.GpuTemp]);

        for (int i = 0; i < 80; i++)
        {
            float util = i < 40 ? 10f : (i % 2 == 0 ? 100f : 90f);
            registry.CommitAll([util, 50f + i % 4], [true, i % 4 == 0], At(i * 0.25));
        }

        return (new McpContext(registry, new SampleEngine(registry)), registry, gpu);
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void Window_statistics_are_exact_and_count_only_measured_samples()
    {
        var (_, registry, gpu) = Build();

        var load = WindowStatistics.Compute(registry, gpu.SlotOf(MetricKind.GpuUtil), At(10), At(19.75), [0.5, 1.0], 90);
        Assert.Equal(40, load.Count);
        Assert.Equal(95, load.Mean, 6);
        Assert.Equal(100, load.Quantiles[1]);
        Assert.Equal(1.0, load.FractionAtOrAbove);
        Assert.False(load.StartsBeforeBuffer);

        // 온도는 4틱에 한 번만 실측이다. 유지값을 세면 표본이 네 배로 부푼다.
        var temp = WindowStatistics.Compute(registry, gpu.SlotOf(MetricKind.GpuTemp), At(0), At(19.75), [0.5]);
        Assert.Equal(20, temp.Count);
        Assert.Equal(50, temp.Max);
    }

    [Fact]
    public void A_window_that_starts_before_the_ring_is_marked_truncated()
    {
        var (_, registry, gpu) = Build(capacity: 32);   // 링에는 마지막 8초만 남는다

        var w = WindowStatistics.Compute(registry, gpu.SlotOf(MetricKind.GpuUtil), At(0), At(19.75), [0.5]);

        Assert.True(w.StartsBeforeBuffer);
        Assert.Equal(32, w.Count);
    }

    [Fact]
    public void Stats_between_two_marks()
    {
        var (ctx, _, _) = Build();
        ctx.Markers.Set("idle", At(0), null);
        ctx.Markers.Set("load", At(10), null);
        ctx.Markers.Set("end", At(19.75), null);

        var result = Json(new IntervalTools(ctx).GetIntervalStats("load", "end", metric: "GpuUtil"));

        var block = result.GetProperty("stats")[0];
        Assert.Equal(40, block.GetProperty("sampleCount").GetInt64());
        Assert.Equal(95, block.GetProperty("avg").GetDouble(), 3);
        Assert.Equal(1.0, block.GetProperty("saturatedFraction").GetDouble());
        Assert.Equal("load", result.GetProperty("interval").GetProperty("fromLabel").GetString());
        Assert.Equal(9.75, result.GetProperty("interval").GetProperty("seconds").GetDouble());
    }

    [Fact]
    public void Iso_times_work_where_marks_are_not_set()
    {
        var (ctx, _, _) = Build();
        string from = new DateTimeOffset(At(0), TimeSpan.Zero).ToString("o");
        string to = new DateTimeOffset(At(9.75), TimeSpan.Zero).ToString("o");

        var block = Json(new IntervalTools(ctx).GetIntervalStats(from, to, metric: "GpuUtil"))
            .GetProperty("stats")[0];

        Assert.Equal(10, block.GetProperty("avg").GetDouble(), 3);
    }

    [Fact]
    public void Compare_puts_consecutive_segments_side_by_side()
    {
        var (ctx, _, gpu) = Build();
        ctx.Markers.Set("end", At(19.75), null);
        ctx.Markers.Set("idle", At(0), null);        // 순서가 섞여도 시각순으로 쓴다
        ctx.Markers.Set("load", At(10), null);

        var result = Json(new IntervalTools(ctx).CompareIntervals(["end", "idle", "load"], metric: "GpuUtil"));

        var segments = result.GetProperty("segments");
        Assert.Equal("idle→load", segments[0].GetProperty("name").GetString());
        Assert.Equal("load→end", segments[1].GetProperty("name").GetString());

        var row = result.GetProperty("rows")[0];
        Assert.Equal(gpu.Key, row.GetProperty("deviceKey").GetString());
        // idle→load 는 10초 지점의 첫 부하 표본(100)을 끝점으로 포함한다 — 경계는 양쪽 다 닫혀 있다.
        Assert.Equal(0.0244, row.GetProperty("segments")[0].GetProperty("saturatedFraction").GetDouble(), 3);
        Assert.Equal(1.0, row.GetProperty("segments")[1].GetProperty("saturatedFraction").GetDouble());
    }

    [Fact]
    public void Remarking_a_label_moves_it_and_says_so()
    {
        var (ctx, _, _) = Build();
        var tools = new IntervalTools(ctx);

        Assert.Equal(JsonValueKind.Null, Json(tools.Mark("step")).GetProperty("movedFrom").ValueKind);
        Assert.Equal(JsonValueKind.String, Json(tools.Mark("step")).GetProperty("movedFrom").ValueKind);
        Assert.Single(ctx.Markers.All());
    }

    [Fact]
    public void Unknown_points_and_empty_intervals_are_structured_errors()
    {
        var (ctx, _, _) = Build();
        var tools = new IntervalTools(ctx);
        ctx.Markers.Set("a", At(5), null);

        Assert.Equal("unknown_point", Json(tools.GetIntervalStats("없는마커")).GetProperty("error").GetString());
        Assert.Equal("empty_interval", Json(tools.GetIntervalStats("a", "a")).GetProperty("error").GetString());
        Assert.Equal("too_few_marks", Json(tools.CompareIntervals(["a"])).GetProperty("error").GetString());
    }
}

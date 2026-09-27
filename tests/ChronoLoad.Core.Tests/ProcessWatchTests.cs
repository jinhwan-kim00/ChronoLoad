using System.Text.Json;
using ChronoLoad.Core.Metrics;
using ChronoLoad.Core.Sampling;
using ChronoLoad.Mcp;
using ChronoLoad.Sensors;

namespace ChronoLoad.Core.Tests;

/// <summary>프로세스별 엔진 시계열(<c>watch_process</c>). 실사용 요청이었다.</summary>
public class ProcessWatchTests
{
    private static readonly int Self = Environment.ProcessId;
    private static readonly long T0 = DateTime.UtcNow.Ticks;
    private static long At(int second) => T0 + second * TimeSpan.TicksPerSecond;

    private static string Instance(int pid, string engineType, int engine = 0) =>
        $"pid_{pid}_luid_0x00000000_0x00016A86_phys_0_eng_{engine}_engtype_{engineType}";

    [Fact]
    public void Engine_instances_of_the_watched_pid_become_family_series()
    {
        using var watch = new ProcessWatch();
        Assert.NotNull(watch.Watch(Self, TimeSpan.FromMinutes(1)).Info);

        for (int s = 0; s < 3; s++)
        {
            watch.BeginEngines();
            watch.AddEngine(Instance(Self, "3D"), "gpu:a", "3D", 40);
            watch.AddEngine(Instance(Self, "Compute_0", 5), "gpu:a", "Compute_0", 70);
            watch.AddEngine(Instance(Self, "Compute_1", 6), "gpu:a", "Compute_1", 20);   // 계열 안은 최댓값
            watch.AddEngine(Instance(Self + 1, "3D"), "gpu:a", "3D", 99);                 // 다른 프로세스
            watch.Commit(At(s));
        }

        var history = watch.Get(Self)!;
        Assert.Equal(3, history.Stamps.Length);
        Assert.Equal([40f, 40f, 40f], history.Engines[("gpu:a", MetricKind.Gpu3D)]);
        Assert.Equal([70f, 70f, 70f], history.Engines[("gpu:a", MetricKind.GpuCompute)]);
        Assert.True(history.WorkingSetBytes[^1] > 0);
    }

    [Fact]
    public void An_engine_first_seen_later_is_backfilled_with_nothing_not_zero()
    {
        using var watch = new ProcessWatch();
        watch.Watch(Self, TimeSpan.FromMinutes(1));

        watch.BeginEngines();
        watch.AddEngine(Instance(Self, "3D"), "gpu:a", "3D", 10);
        watch.Commit(At(0));

        watch.BeginEngines();
        watch.AddEngine(Instance(Self, "Copy", 2), "gpu:a", "Copy", 30);
        watch.Commit(At(1));

        var history = watch.Get(Self)!;
        // 복사 엔진은 두 번째 점에서 처음 나타났다. 첫 점은 "몰랐다"(NaN), 3D 의 두 번째 점은 "안 썼다"(0).
        Assert.True(float.IsNaN(history.Engines[("gpu:a", MetricKind.GpuCopy)][0]));
        Assert.Equal(0f, history.Engines[("gpu:a", MetricKind.Gpu3D)][1]);
    }

    [Fact]
    public void Watching_a_missing_process_is_refused()
    {
        using var watch = new ProcessWatch();
        var (info, error) = watch.Watch(int.MaxValue - 1, TimeSpan.FromMinutes(1));

        Assert.Null(info);
        Assert.NotNull(error);
    }

    [Fact]
    public void History_tool_buckets_to_the_requested_points()
    {
        using var watch = new ProcessWatch();
        watch.Watch(Self, TimeSpan.FromMinutes(10));
        for (int s = 0; s < 120; s++)
        {
            watch.BeginEngines();
            watch.AddEngine(Instance(Self, "3D"), "gpu:a", "3D", s % 10 == 0 ? 100 : 10);
            watch.Commit(At(s));
        }

        var registry = new MetricRegistry(16);
        var ctx = new McpContext(registry, new SampleEngine(registry)) { ProcessWatch = watch };

        var result = JsonSerializer.SerializeToElement(new ProcessWatchTools(ctx).GetProcessHistory(Self, maxPoints: 12));

        Assert.Equal("bucketed", result.GetProperty("mode").GetString());
        Assert.Equal(12, result.GetProperty("pointCount").GetInt32());
        var gpu = result.GetProperty("gpu")[0];
        Assert.Equal("3D", gpu.GetProperty("engine").GetString());
        // 칸마다 최대가 남아 10초마다의 100% 버스트가 보인다.
        Assert.All(gpu.GetProperty("values").GetProperty("max").EnumerateArray(), m => Assert.Equal(100, m.GetDouble()));
    }

    [Fact]
    public void History_of_an_unwatched_process_is_a_structured_error()
    {
        var registry = new MetricRegistry(16);
        var ctx = new McpContext(registry, new SampleEngine(registry)) { ProcessWatch = new ProcessWatch() };

        var result = JsonSerializer.SerializeToElement(new ProcessWatchTools(ctx).GetProcessHistory(Self));

        Assert.Equal("not_watched", result.GetProperty("error").GetString());
    }
}

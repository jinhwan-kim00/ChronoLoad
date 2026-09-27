using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Metrics;

namespace ChronoLoad.Core.Tests;

/// <summary>
/// 구간 분위수의 두 경로 — 링 안이면 정확, 넘치면 스케치 (§7.3).
/// </summary>
public class IntervalSummaryTests
{
    private static (MetricRegistry Registry, int Slot) Build(int capacity)
    {
        var registry = new MetricRegistry(seriesCapacity: capacity);
        var device = registry.Register(
            new DeviceInfo("memory", DeviceClass.System, "메모리", "메모리", IconKind.Memory),
            [MetricKind.MemUsed]);
        return (registry, device.SlotOf(MetricKind.MemUsed));
    }

    [Fact]
    public void Quantiles_are_exact_while_the_interval_fits_in_the_ring()
    {
        var (registry, slot) = Build(capacity: 256);
        for (int i = 1; i <= 100; i++) registry.CommitAll([i * 1e8f]);

        var summary = registry.Summarize(slot, StatsScope.Mcp, [0.5, 0.95]);

        Assert.True(summary.Exact);
        Assert.Equal(50 * 1e8f, summary.Quantiles[0]);
        Assert.Equal(95 * 1e8f, summary.Quantiles[1]);
    }

    [Fact]
    public void Held_samples_do_not_enter_the_exact_path()
    {
        var (registry, slot) = Build(capacity: 256);

        // 실측 하나 뒤에 유지값 셋. 통계와 같은 표본만 세야 표본 수가 맞는다.
        for (int i = 0; i < 40; i++) registry.CommitAll([i], [i % 4 == 0]);

        var summary = registry.Summarize(slot, StatsScope.Mcp, [1.0]);

        Assert.True(summary.Exact);
        Assert.Equal(36, summary.Quantiles[0]);
        Assert.Equal(10, registry.StatsSnapshot(slot, StatsScope.Mcp).Count);
    }

    [Fact]
    public void A_reset_starts_a_new_exact_interval()
    {
        var (registry, slot) = Build(capacity: 256);
        for (int i = 0; i < 50; i++) registry.CommitAll([1000f]);

        registry.ResetAllStats(StatsScope.Mcp, DateTime.UtcNow.Ticks);
        for (int i = 0; i < 10; i++) registry.CommitAll([5f]);

        var mcp = registry.Summarize(slot, StatsScope.Mcp, [1.0]);
        var ui = registry.Summarize(slot, StatsScope.Ui, [1.0]);

        // 리셋 전 표본이 링에 남아 있어도 MCP 구간에는 들어가지 않는다. UI 구간은 그대로다.
        Assert.True(mcp.Exact);
        Assert.Equal(5, mcp.Quantiles[0]);
        Assert.Equal(1000, ui.Quantiles[0]);
    }

    [Fact]
    public void Past_the_ring_it_falls_back_to_the_sketch_within_the_observed_range()
    {
        var (registry, slot) = Build(capacity: 64);
        for (int i = 0; i < 1000; i++) registry.CommitAll([14.6e9f + (i % 100) * 1.4e7f]);

        var summary = registry.Summarize(slot, StatsScope.Mcp, [0.95]);
        var stats = registry.StatsSnapshot(slot, StatsScope.Mcp);

        Assert.False(summary.Exact);
        Assert.InRange(summary.Quantiles[0], stats.Min, stats.Max);

        double expected = 14.6e9 + 94 * 1.4e7;
        Assert.True(Math.Abs(summary.Quantiles[0] - expected) / expected <= PercentileTracker.RelativeAccuracy + 1e-6);
    }

    /// <summary>
    /// 실사용 보고: 재기동 직후 165초 구간인데 GPU·디스크만 근사였다. 장치가 등록된 뒤 샘플 엔진이
    /// 버퍼를 다시 잡기까지 몇 프레임은 새 슬롯에 값이 들어가지 않아, 프레임 수가 시리즈 칸 수보다 컸다.
    /// </summary>
    [Fact]
    public void A_series_that_started_after_the_reset_is_still_exact()
    {
        var registry = new MetricRegistry(seriesCapacity: 256);
        var cpu = registry.Register(
            new DeviceInfo("cpu", DeviceClass.System, "CPU", "CPU", IconKind.Cpu), [MetricKind.CpuTotal]);

        // GPU 는 뒤늦게 등록된다. 그 뒤 두 프레임은 버퍼가 아직 옛 크기라 GPU 칸에 쓰지 않는다.
        registry.CommitAll([1f]);
        var gpu = registry.Register(
            new DeviceInfo("gpu:luid_1", DeviceClass.Gpu, "GPU", "GPU", IconKind.GpuNvidia), [MetricKind.GpuUtil]);
        registry.CommitAll([1f]);
        registry.CommitAll([1f]);
        for (int i = 1; i <= 100; i++) registry.CommitAll([1f, i]);

        var summary = registry.Summarize(gpu.SlotOf(MetricKind.GpuUtil), StatsScope.Mcp, [0.95]);

        Assert.True(summary.Exact);
        Assert.Equal(95, summary.Quantiles[0]);
        Assert.True(registry.Summarize(cpu.SlotOf(MetricKind.CpuTotal), StatsScope.Mcp, [0.5]).Exact);
    }

    [Fact]
    public void Empty_interval_is_nan()
    {
        var (registry, slot) = Build(capacity: 64);

        var summary = registry.Summarize(slot, StatsScope.Mcp, [0.95], threshold: 90);

        Assert.True(double.IsNaN(summary.Quantiles[0]));
        Assert.True(double.IsNaN(summary.FractionAtOrAbove));
    }
}

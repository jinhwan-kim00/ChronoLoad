using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Metrics;

namespace ChronoLoad.Core.Tests;

/// <summary>
/// 화면의 ⟲ 와 MCP <c>reset_stats</c> 는 다른 사람이 다른 목적으로 누른다.
/// 한쪽이 상대의 기준점을 건드리면 상대가 재고 있던 구간이 소리 없이 사라진다.
/// </summary>
public class StatsScopeTests
{
    private static (MetricRegistry Registry, int Slot) Setup()
    {
        var registry = new MetricRegistry(64);
        var handle = registry.Register(
            new DeviceInfo("cpu", DeviceClass.System, "CPU", "CPU", IconKind.Cpu),
            [MetricKind.CpuTotal]);
        return (registry, handle.SlotOf(MetricKind.CpuTotal));
    }

    [Fact]
    public void Both_scopes_receive_the_same_samples()
    {
        var (registry, slot) = Setup();

        registry.CommitAll([10f]);
        registry.CommitAll([20f]);

        Assert.Equal(2, registry.StatsSnapshot(slot, StatsScope.Ui).Count);
        Assert.Equal(2, registry.StatsSnapshot(slot, StatsScope.Mcp).Count);
        Assert.Equal(15.0, registry.StatsSnapshot(slot, StatsScope.Mcp).Mean, 6);
    }

    [Fact]
    public void A_screen_reset_does_not_disturb_the_agent()
    {
        var (registry, slot) = Setup();

        registry.CommitAll([10f]);
        registry.CommitAll([90f]);

        registry.ResetAllStats(StatsScope.Ui, DateTime.UtcNow.Ticks);
        registry.CommitAll([50f]);

        // 화면은 리셋 이후 한 건만 본다
        Assert.Equal(1, registry.StatsSnapshot(slot, StatsScope.Ui).Count);
        Assert.Equal(50.0, registry.StatsSnapshot(slot, StatsScope.Ui).Mean, 6);

        // 에이전트가 재던 구간은 그대로 이어진다
        Assert.Equal(3, registry.StatsSnapshot(slot, StatsScope.Mcp).Count);
        Assert.Equal(10f, registry.StatsSnapshot(slot, StatsScope.Mcp).Min);
        Assert.Equal(90f, registry.StatsSnapshot(slot, StatsScope.Mcp).Max);
    }

    [Fact]
    public void An_agent_reset_does_not_disturb_the_screen()
    {
        var (registry, slot) = Setup();

        registry.CommitAll([10f]);
        registry.CommitAll([90f]);

        registry.ResetAllStats(StatsScope.Mcp, DateTime.UtcNow.Ticks);
        registry.CommitAll([50f]);

        Assert.Equal(1, registry.StatsSnapshot(slot, StatsScope.Mcp).Count);
        Assert.Equal(3, registry.StatsSnapshot(slot, StatsScope.Ui).Count);
        Assert.Equal(10f, registry.StatsSnapshot(slot, StatsScope.Ui).Min);
    }

    [Fact]
    public void Default_scope_is_the_screen_so_mcp_must_ask_explicitly()
    {
        var (registry, slot) = Setup();
        registry.CommitAll([42f]);
        registry.ResetAllStats(StatsScope.Ui, DateTime.UtcNow.Ticks);

        // 스코프를 생략하면 화면 기준. MCP 경로가 실수로 이 값을 읽으면 즉시 비어 보인다.
        Assert.True(registry.StatsSnapshot(slot).IsEmpty);
        Assert.False(registry.StatsSnapshot(slot, StatsScope.Mcp).IsEmpty);
    }

    [Fact]
    public void Per_slot_reset_is_also_scoped()
    {
        var (registry, slot) = Setup();
        registry.CommitAll([7f]);

        registry.ResetStats(slot, StatsScope.Mcp, DateTime.UtcNow.Ticks);

        Assert.True(registry.StatsSnapshot(slot, StatsScope.Mcp).IsEmpty);
        Assert.Equal(1, registry.StatsSnapshot(slot, StatsScope.Ui).Count);
    }

    [Fact]
    public void Reset_timestamps_are_independent()
    {
        var (registry, slot) = Setup();
        long t0 = DateTime.UtcNow.Ticks;
        long t1 = t0 + TimeSpan.FromMinutes(5).Ticks;

        registry.ResetAllStats(StatsScope.Ui, t0);
        registry.ResetAllStats(StatsScope.Mcp, t1);

        Assert.Equal(t0, registry.StatsSnapshot(slot, StatsScope.Ui).ResetTimestampUtcTicks);
        Assert.Equal(t1, registry.StatsSnapshot(slot, StatsScope.Mcp).ResetTimestampUtcTicks);
    }

    [Fact]
    public void Held_values_are_excluded_from_every_scope()
    {
        var (registry, slot) = Setup();

        registry.CommitAll([60f], [true]);
        registry.CommitAll([60f], [false]);

        Assert.Equal(1, registry.StatsSnapshot(slot, StatsScope.Ui).Count);
        Assert.Equal(1, registry.StatsSnapshot(slot, StatsScope.Mcp).Count);
    }

    [Fact]
    public void A_reconnecting_device_keeps_both_scopes()
    {
        var registry = new MetricRegistry(64);
        var info = new DeviceInfo("net:1", DeviceClass.Network, "Wi-Fi", "Wi-Fi", IconKind.NetWiFi);
        var handle = registry.Register(info, [MetricKind.NetRx]);
        int slot = handle.SlotOf(MetricKind.NetRx);

        registry.CommitAll([100f]);
        long now = DateTime.UtcNow.Ticks;
        registry.Retire("net:1", now);
        registry.Register(info, [MetricKind.NetRx]);

        Assert.Equal(1, registry.StatsSnapshot(slot, StatsScope.Ui).Count);
        Assert.Equal(1, registry.StatsSnapshot(slot, StatsScope.Mcp).Count);
    }
}

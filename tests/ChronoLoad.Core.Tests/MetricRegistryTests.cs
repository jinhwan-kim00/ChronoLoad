using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Metrics;

namespace ChronoLoad.Core.Tests;

public class MetricRegistryTests
{
    private static DeviceInfo Gpu(string key, string name) =>
        new(key, DeviceClass.Gpu, name, $"{name} 전체 이름", IconKind.GpuNvidia, "NVIDIA");

    private static DeviceInfo Net(string key, string name) =>
        new(key, DeviceClass.Network, name, $"{name} 전체 이름", IconKind.NetWiFi);

    [Fact]
    public void Registering_allocates_one_slot_per_metric()
    {
        var registry = new MetricRegistry(64);
        var handle = registry.Register(Gpu("luid:1", "RTX 4090"),
            [MetricKind.GpuUtil, MetricKind.GpuDedicated]);

        Assert.Equal(2, registry.SlotCount);
        Assert.NotEqual(-1, handle.SlotOf(MetricKind.GpuUtil));
        Assert.Equal(-1, handle.SlotOf(MetricKind.GpuTemp));
        Assert.NotNull(registry.Series(new MetricId(MetricKind.GpuUtil, handle.Index)));
    }

    [Fact]
    public void Metrics_must_match_the_device_class()
    {
        var registry = new MetricRegistry(64);

        Assert.Throws<ArgumentException>(() =>
            registry.Register(Gpu("luid:1", "RTX 4090"), [MetricKind.DiskRead]));
    }

    [Fact]
    public void Device_indices_are_assigned_per_class()
    {
        var registry = new MetricRegistry(64);
        var gpu0 = registry.Register(Gpu("luid:1", "A"), [MetricKind.GpuUtil]);
        var gpu1 = registry.Register(Gpu("luid:2", "B"), [MetricKind.GpuUtil]);
        var net0 = registry.Register(Net("guid:1", "Wi-Fi"), [MetricKind.NetRx]);

        Assert.Equal(0, gpu0.Index);
        Assert.Equal(1, gpu1.Index);
        Assert.Equal(0, net0.Index);   // 클래스가 다르면 인덱스는 다시 0부터
    }

    [Fact]
    public void Revision_advances_on_every_topology_change()
    {
        var registry = new MetricRegistry(64);
        int start = registry.Revision;

        registry.Register(Gpu("luid:1", "A"), [MetricKind.GpuUtil]);
        int afterAdd = registry.Revision;
        Assert.True(afterAdd > start);

        registry.Retire("luid:1", DateTime.UtcNow.Ticks);
        Assert.True(registry.Revision > afterAdd);
    }

    [Fact]
    public void Retiring_keeps_history_so_a_quick_reconnect_does_not_lose_the_benchmark()
    {
        // Wi-Fi 를 껐다 켜거나 드라이버가 재시작됐다고 리셋 이후 통계가 날아가면 안 된다.
        var registry = new MetricRegistry(64);
        var handle = registry.Register(Net("guid:1", "Wi-Fi"), [MetricKind.NetRx]);
        int slot = handle.SlotOf(MetricKind.NetRx);

        registry.CommitAll([100f]);
        registry.CommitAll([200f]);

        long now = DateTime.UtcNow.Ticks;
        registry.Retire("guid:1", now);

        // 유예 안에 돌아옴
        var resumed = registry.Register(Net("guid:1", "Wi-Fi"), [MetricKind.NetRx]);
        registry.PurgeRetired(now + TimeSpan.FromSeconds(30).Ticks);

        Assert.Equal(slot, resumed.SlotOf(MetricKind.NetRx));
        Assert.Equal(2, registry.Series(slot)!.Count);
        Assert.Equal(2, registry.StatsSnapshot(slot).Count);
        Assert.Equal(150.0, registry.StatsSnapshot(slot).Mean, 6);
    }

    [Fact]
    public void Purging_after_the_grace_period_frees_the_slot_for_reuse()
    {
        var registry = new MetricRegistry(64);
        var first = registry.Register(Net("guid:1", "Wi-Fi"), [MetricKind.NetRx]);
        int slot = first.SlotOf(MetricKind.NetRx);
        registry.CommitAll([42f]);

        long now = DateTime.UtcNow.Ticks;
        registry.Retire("guid:1", now);

        int purged = registry.PurgeRetired(now + TimeSpan.FromSeconds(61).Ticks);

        Assert.Equal(1, purged);
        Assert.Null(registry.Find("guid:1"));
        Assert.Null(registry.Series(slot));

        var second = registry.Register(Net("guid:2", "Ethernet"), [MetricKind.NetRx]);
        Assert.Equal(slot, second.SlotOf(MetricKind.NetRx));         // 슬롯 재사용
        Assert.Equal(0, registry.Series(slot)!.Count);               // 히스토리는 새로 시작
        Assert.Equal(1, registry.SlotCount);
    }

    [Fact]
    public void Purging_before_the_grace_period_does_nothing()
    {
        var registry = new MetricRegistry(64);
        registry.Register(Net("guid:1", "Wi-Fi"), [MetricKind.NetRx]);

        long now = DateTime.UtcNow.Ticks;
        registry.Retire("guid:1", now);

        Assert.Equal(0, registry.PurgeRetired(now + TimeSpan.FromSeconds(59).Ticks));
        Assert.NotNull(registry.Find("guid:1"));
    }

    [Fact]
    public void Retired_devices_drop_out_of_the_active_list_immediately()
    {
        var registry = new MetricRegistry(64);
        registry.Register(Gpu("luid:1", "A"), [MetricKind.GpuUtil]);
        registry.Register(Gpu("luid:2", "B"), [MetricKind.GpuUtil]);

        registry.Retire("luid:2", DateTime.UtcNow.Ticks);

        Assert.Single(registry.ActiveDevices);
        Assert.Equal("luid:1", registry.ActiveDevices[0].Key);
    }

    [Fact]
    public void Re_registering_refreshes_the_display_name_without_touching_data()
    {
        var registry = new MetricRegistry(64);
        var handle = registry.Register(Net("guid:1", "Wi-Fi · OLD"), [MetricKind.NetRx]);
        registry.CommitAll([10f]);

        registry.Register(Net("guid:1", "Wi-Fi · NEW"), [MetricKind.NetRx]);

        Assert.Equal("Wi-Fi · NEW", handle.Info.ShortName);
        Assert.Equal(1, registry.Series(handle.SlotOf(MetricKind.NetRx))!.Count);
    }

    [Fact]
    public void Reset_clears_stats_for_every_slot_including_collapsed_cards()
    {
        var registry = new MetricRegistry(64);
        var gpu = registry.Register(Gpu("luid:1", "A"), [MetricKind.GpuUtil]);
        var net = registry.Register(Net("guid:1", "Wi-Fi"), [MetricKind.NetRx]);

        registry.CommitAll([50f, 60f]);
        Assert.Equal(1, registry.StatsSnapshot(gpu.SlotOf(MetricKind.GpuUtil)).Count);

        registry.ResetAllStats(StatsScope.Ui, DateTime.UtcNow.Ticks);

        Assert.True(registry.StatsSnapshot(gpu.SlotOf(MetricKind.GpuUtil)).IsEmpty);
        Assert.True(registry.StatsSnapshot(net.SlotOf(MetricKind.NetRx)).IsEmpty);
    }
}

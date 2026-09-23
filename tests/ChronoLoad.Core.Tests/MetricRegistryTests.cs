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
    public void Re_registering_an_unchanged_device_does_not_advance_the_revision()
    {
        // §5.7 의 검증 재열거는 장치가 안 바뀌어도 30초마다 돈다. 그때마다 리비전이 오르면
        // "devicesRevision 만 비교하면 구성이 그대로인지 알 수 있다"(§10.3)가 거짓말이 된다.
        // 실기기에서 실제로 30초마다 1씩 올라가고 있었다.
        var registry = new MetricRegistry(64);
        registry.Register(Net("guid:1", "Wi-Fi"), [MetricKind.NetRx, MetricKind.NetTx]);

        int settled = registry.Revision;

        for (int i = 0; i < 5; i++)
            registry.Register(Net("guid:1", "Wi-Fi"), [MetricKind.NetRx, MetricKind.NetTx]);

        Assert.Equal(settled, registry.Revision);
    }

    [Fact]
    public void A_renamed_device_does_advance_the_revision()
    {
        // 반대쪽도 지켜야 한다. Wi-Fi 링크 속도가 바뀌면 카드 이름이 바뀌므로,
        // 캐시를 들고 있는 에이전트는 다시 물어봐야 한다.
        var registry = new MetricRegistry(64);
        registry.Register(Net("guid:1", "Wi-Fi 2.4G"), [MetricKind.NetRx]);

        int settled = registry.Revision;
        registry.Register(Net("guid:1", "Wi-Fi 1.2G"), [MetricKind.NetRx]);

        Assert.True(registry.Revision > settled);
    }

    [Fact]
    public void Extra_fields_count_as_a_change_even_though_the_record_compares_by_reference()
    {
        var registry = new MetricRegistry(64);
        var before = Gpu("luid:1", "Arc") with { Extra = new Dictionary<string, string> { ["vendorId"] = "0x8086" } };
        var same = Gpu("luid:1", "Arc") with { Extra = new Dictionary<string, string> { ["vendorId"] = "0x8086" } };
        var after = Gpu("luid:1", "Arc") with { Extra = new Dictionary<string, string> { ["vendorId"] = "0x10DE" } };

        registry.Register(before, [MetricKind.GpuUtil]);
        int settled = registry.Revision;

        registry.Register(same, [MetricKind.GpuUtil]);          // 사전은 새 인스턴스지만 내용이 같다
        Assert.Equal(settled, registry.Revision);

        registry.Register(after, [MetricKind.GpuUtil]);
        Assert.True(registry.Revision > settled);
    }

    [Fact]
    public void A_device_that_comes_back_after_being_retired_advances_the_revision()
    {
        var registry = new MetricRegistry(64);
        registry.Register(Net("guid:1", "Wi-Fi"), [MetricKind.NetRx]);
        registry.Retire("guid:1", DateTime.UtcNow.Ticks);

        int retired = registry.Revision;
        registry.Register(Net("guid:1", "Wi-Fi"), [MetricKind.NetRx]);

        Assert.True(registry.Revision > retired);
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

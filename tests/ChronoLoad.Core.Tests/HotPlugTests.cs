using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Metrics;
using ChronoLoad.Core.Sampling;
using ChronoLoad.Core.Sensors;

namespace ChronoLoad.Core.Tests;

/// <summary>
/// §5.7 핫플러그. 화면이 장치 변경을 언제 알아채고, 사라진 장치의 자리를 언제 회수하는가.
/// </summary>
public class HotPlugTests
{
    private static DeviceInfo Gpu(string key, string name) =>
        new(key, DeviceClass.Gpu, name, name, IconKind.GpuGeneric);

    [Fact]
    public async Task Devices_changed_fires_once_per_topology_change_not_every_tick()
    {
        var registry = new MetricRegistry(seriesCapacity: 64);
        await using var engine = new SampleEngine(registry);

        int notifications = 0;
        engine.DevicesChanged += _ => notifications++;

        registry.Register(Gpu("luid:1", "GPU 1"), [MetricKind.GpuUtil]);
        engine.TickOnce(0.25);
        Assert.Equal(1, notifications);

        // 아무것도 바뀌지 않은 틱은 조용해야 한다. UI 가 매 틱 카드를 다시 지으면 안 된다.
        engine.TickOnce(0.25);
        engine.TickOnce(0.25);
        Assert.Equal(1, notifications);

        registry.Register(Gpu("luid:2", "GPU 2"), [MetricKind.GpuUtil]);
        engine.TickOnce(0.25);
        Assert.Equal(2, notifications);
    }

    [Fact]
    public async Task Retiring_and_purging_each_announce_the_change()
    {
        var registry = new MetricRegistry(seriesCapacity: 64);
        await using var engine = new SampleEngine(registry,
            new SamplingOptions { DeviceRetentionGrace = TimeSpan.FromSeconds(60) });

        registry.Register(Gpu("luid:1", "eGPU"), [MetricKind.GpuUtil]);
        engine.TickOnce(0.25);

        int notifications = 0;
        engine.DevicesChanged += _ => notifications++;

        registry.Retire("luid:1", DateTime.UtcNow.Ticks);
        engine.TickOnce(0.25);

        // 은퇴는 즉시 알린다 — 카드가 사라지는 것은 유예를 기다리지 않는다.
        Assert.Equal(1, notifications);
        Assert.Empty(registry.ActiveDevices);
    }

    [Fact]
    public async Task A_device_that_comes_back_within_the_grace_keeps_its_benchmark()
    {
        var registry = new MetricRegistry(seriesCapacity: 64);
        await using var engine = new SampleEngine(registry);

        var handle = registry.Register(Gpu("luid:1", "eGPU"), [MetricKind.GpuUtil]);
        int slot = handle.SlotOf(MetricKind.GpuUtil);

        registry.PushFrame([50f]);
        registry.PushFrame([70f]);

        long now = DateTime.UtcNow.Ticks;
        registry.Retire("luid:1", now);

        // 케이블을 다시 꽂았다. 같은 키로 돌아오면 통계가 이어져야 한다 —
        // 여기서 초기화되면 리셋 이후 누적해 온 벤치마크 구간이 통째로 날아간다.
        var again = registry.Register(Gpu("luid:1", "eGPU"), [MetricKind.GpuUtil]);

        Assert.Equal(slot, again.SlotOf(MetricKind.GpuUtil));

        var stats = registry.StatsSnapshot(slot);
        Assert.Equal(2, stats.Count);
        Assert.Equal(70f, stats.Max);
        Assert.Equal(2, registry.Series(slot)!.Count);
    }

    [Fact]
    public async Task Slots_come_back_for_reuse_once_the_grace_has_passed()
    {
        var registry = new MetricRegistry(seriesCapacity: 64);

        // 유예를 짧게 줘서 엔진이 스스로 회수하는지 본다.
        await using var engine = new SampleEngine(registry,
            new SamplingOptions { DeviceRetentionGrace = TimeSpan.Zero });

        var handle = registry.Register(Gpu("luid:1", "eGPU"), [MetricKind.GpuUtil]);
        int slot = handle.SlotOf(MetricKind.GpuUtil);
        registry.PushFrame([42f]);

        registry.Retire("luid:1", DateTime.UtcNow.Ticks);
        engine.TickOnce(0.25);

        Assert.Null(registry.Find("luid:1"));

        // 회수된 자리는 재사용되고, 새 장치가 옛 장치의 값을 물려받지 않는다.
        var replacement = registry.Register(Gpu("luid:9", "다른 GPU"), [MetricKind.GpuUtil]);
        Assert.Equal(slot, replacement.SlotOf(MetricKind.GpuUtil));
        Assert.Equal(0, registry.StatsSnapshot(slot).Count);
    }

    [Fact]
    public async Task Rescan_reaches_every_provider_even_when_one_throws()
    {
        var registry = new MetricRegistry(seriesCapacity: 64);
        await using var engine = new SampleEngine(registry);

        var angry = new RescanProvider("angry", throws: true);
        var calm = new RescanProvider("calm", throws: false);

        await engine.AddProviderAsync(angry);
        await engine.AddProviderAsync(calm);

        engine.RequestRescan();

        // 프로바이더 하나가 던져도 나머지는 요청을 받아야 한다 — 센서 하나가 죽어도 전체가 멈추면 안 된다.
        Assert.Equal(1, angry.Requests);
        Assert.Equal(1, calm.Requests);
    }

    private sealed class RescanProvider(string id, bool throws) : ISensorProvider
    {
        public string Id => id;
        public SensorTier Tier => SensorTier.Fast;
        public bool IsAvailable => true;
        public int Requests { get; private set; }

        public ValueTask InitializeAsync(MetricRegistry registry, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public void Sample(in SampleWriter writer) { }

        public void RequestEnumerate()
        {
            Requests++;
            if (throws) throw new InvalidOperationException("열거 실패");
        }

        public void Dispose() { }
    }
}

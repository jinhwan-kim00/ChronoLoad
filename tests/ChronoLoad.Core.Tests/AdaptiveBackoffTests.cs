using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Metrics;
using ChronoLoad.Core.Sampling;
using ChronoLoad.Core.Sensors;

namespace ChronoLoad.Core.Tests;

/// <summary>§6.3 적응형 백오프. 언제 느려지고 언제 돌아오는가.</summary>
public class AdaptiveBackoffTests
{
    private static SamplingOptions Options(int checkEvery = 4) => new()
    {
        FastPeriod = TimeSpan.FromMilliseconds(250),
        OverloadCheckEvery = checkEvery,
        // 테스트에서는 몇 틱 만에 판정이 서야 한다. 실제 기본값(0.1)은 훨씬 느리게 반응한다.
        OverloadSmoothing = 1.0,
        SlowTickWarnRatio = 0.2,
    };

    [Fact]
    public async Task Requested_pace_changes_the_effective_period()
    {
        var registry = new MetricRegistry(seriesCapacity: 32);
        await using var engine = new SampleEngine(registry, Options());

        Assert.Equal(SamplePace.Full, engine.EffectivePace);
        Assert.Equal(TimeSpan.FromMilliseconds(250), engine.EffectiveFastPeriod);

        engine.RequestedPace = SamplePace.Background;

        Assert.Equal(SamplePace.Background, engine.EffectivePace);
        Assert.Equal(TimeSpan.FromSeconds(1), engine.EffectiveFastPeriod);
    }

    [Fact]
    public async Task Battery_saver_pace_halves_the_sample_rate()
    {
        // §6.3 의 "배터리 + 절전 → Fast ×2". 이 경로는 실기기에서 아직 한 번도 실행된 적이 없다
        // (§12) — 배터리 절전 상태를 만들지 못해서다. 감지는 못 묶어도 **효과**는 여기서 묶어둔다.
        // 그래야 남은 미검증 구간이 "GetSystemPowerStatus 가 절전을 알려주는가" 하나로 좁혀진다.
        var registry = new MetricRegistry(seriesCapacity: 32);
        await using var engine = new SampleEngine(registry, Options());

        engine.RequestedPace = SamplePace.Reduced;

        Assert.Equal(SamplePace.Reduced, engine.EffectivePace);
        Assert.Equal(TimeSpan.FromMilliseconds(500), engine.EffectiveFastPeriod);

        engine.RequestedPace = SamplePace.Full;
        Assert.Equal(TimeSpan.FromMilliseconds(250), engine.EffectiveFastPeriod);
    }

    [Fact]
    public async Task A_slow_provider_pushes_the_engine_down_a_step()
    {
        var registry = new MetricRegistry(seriesCapacity: 32);
        await using var engine = new SampleEngine(registry, Options());

        // 250ms 주기의 20% = 50ms 가 예산이다. 그보다 확실히 오래 끄는 프로바이더.
        await engine.AddProviderAsync(new SlowProvider(TimeSpan.FromMilliseconds(80)));

        for (int i = 0; i < 8; i++) engine.TickOnce(0.25);

        Assert.NotEqual(SamplePace.Full, engine.EffectivePace);
    }

    [Fact]
    public async Task It_comes_back_up_when_the_load_goes_away()
    {
        var registry = new MetricRegistry(seriesCapacity: 32);
        await using var engine = new SampleEngine(registry, Options());

        var provider = new SlowProvider(TimeSpan.FromMilliseconds(80));
        await engine.AddProviderAsync(provider);

        for (int i = 0; i < 8; i++) engine.TickOnce(0.25);
        Assert.NotEqual(SamplePace.Full, engine.EffectivePace);

        provider.Delay = TimeSpan.Zero;
        for (int i = 0; i < 24; i++) engine.TickOnce(0.25);

        // 여유가 돌아오면 원래 주기로 복귀해야 한다. 한 번 느려지면 영영 느린 앱이 되면 안 된다.
        Assert.Equal(SamplePace.Full, engine.EffectivePace);
    }

    [Fact]
    public async Task Restoring_the_window_does_not_undo_an_overload_demotion()
    {
        var registry = new MetricRegistry(seriesCapacity: 32);
        await using var engine = new SampleEngine(registry, Options());

        await engine.AddProviderAsync(new SlowProvider(TimeSpan.FromMilliseconds(80)));

        // 먼저 Full 상태에서 과부하로 강등시킨다.
        for (int i = 0; i < 8; i++) engine.TickOnce(0.25);
        var demoted = engine.EffectivePace;
        Assert.NotEqual(SamplePace.Full, demoted);

        // 창을 최소화했다 복원한다. 바깥 요청은 Full 로 돌아오지만 기기 사정은 그대로다 —
        // 두 축을 하나로 섞어 관리하면 여기서 과부하 강등까지 함께 풀려 버린다.
        engine.RequestedPace = SamplePace.Background;
        engine.RequestedPace = SamplePace.Full;

        Assert.Equal(SamplePace.Full, engine.RequestedPace);
        Assert.Equal(demoted, engine.EffectivePace);
    }

    [Fact]
    public async Task One_slow_tick_alone_does_not_demote()
    {
        var registry = new MetricRegistry(seriesCapacity: 32);

        // 실제 기본값에 가까운 완만한 평활. 튀는 한 틱은 흡수돼야 한다.
        await using var engine = new SampleEngine(registry, Options() with { OverloadSmoothing = 0.1 });

        var provider = new SlowProvider(TimeSpan.Zero);
        await engine.AddProviderAsync(provider);

        for (int i = 0; i < 20; i++) engine.TickOnce(0.25);

        provider.Delay = TimeSpan.FromMilliseconds(120);   // 한 번만 튄다
        engine.TickOnce(0.25);
        provider.Delay = TimeSpan.Zero;

        for (int i = 0; i < 8; i++) engine.TickOnce(0.25);

        Assert.Equal(SamplePace.Full, engine.EffectivePace);
    }

    private sealed class SlowProvider(TimeSpan delay) : ISensorProvider
    {
        private int _slot = -1;

        public TimeSpan Delay { get; set; } = delay;

        public string Id => "slow";
        public SensorTier Tier => SensorTier.Fast;
        public bool IsAvailable => true;

        public ValueTask InitializeAsync(MetricRegistry registry, CancellationToken cancellationToken)
        {
            var handle = registry.Register(
                new DeviceInfo("slow", DeviceClass.Gpu, "느린 장치", "테스트", IconKind.GpuGeneric),
                [MetricKind.GpuUtil]);
            _slot = handle.SlotOf(MetricKind.GpuUtil);
            return ValueTask.CompletedTask;
        }

        public void Sample(in SampleWriter writer)
        {
            if (Delay > TimeSpan.Zero) Thread.Sleep(Delay);
            writer.Write(_slot, 1f);
        }

        public void Dispose() { }
    }
}

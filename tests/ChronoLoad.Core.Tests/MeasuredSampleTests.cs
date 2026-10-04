using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Metrics;
using ChronoLoad.Core.Sampling;
using ChronoLoad.Core.Sensors;

namespace ChronoLoad.Core.Tests;

/// <summary>
/// Slow 티어 지표는 Fast 틱마다 직전 값이 다시 기록된다(시간 축을 맞추기 위해).
/// 그 반복값이 통계에 들어가면 샘플 수가 부풀고 표준편차가 실제보다 작게 나온다.
/// </summary>
public class MeasuredSampleTests
{
    [Fact]
    public void Series_remembers_whether_each_sample_was_measured()
    {
        var series = new MetricSeries(8);
        series.Write(10f, measured: true);
        series.Write(10f, measured: false);
        series.Write(20f, measured: true);

        Assert.True(series.IsMeasured(0));
        Assert.False(series.IsMeasured(1));
        Assert.True(series.IsMeasured(2));
        Assert.True(series.LatestMeasured);
        Assert.Equal(0, series.SamplesSinceMeasurement);
    }

    [Fact]
    public void Samples_since_measurement_counts_the_held_repeats()
    {
        var series = new MetricSeries(16);
        series.Write(5f, measured: true);
        series.Write(5f, measured: false);
        series.Write(5f, measured: false);

        Assert.False(series.LatestMeasured);
        Assert.Equal(2, series.SamplesSinceMeasurement);
    }

    [Fact]
    public void A_series_with_no_measurement_yet_reports_max_age()
    {
        var series = new MetricSeries(8);
        Assert.Equal(int.MaxValue, series.SamplesSinceMeasurement);
    }

    [Fact]
    public void A_series_that_only_ever_measured_unavailable_has_no_measurement()
    {
        // 절전(D3) 중에 적힌 "측정 불가" 한 칸. 깨어난 뒤에도 센서가 값을 내지 않으면 이 칸만 남는다.
        var series = new MetricSeries(8);
        series.Write(float.NaN, measured: true);
        series.Write(float.NaN, measured: false);
        Assert.False(series.HasMeasurement);

        series.Write(42f, measured: true);
        Assert.True(series.HasMeasurement);

        // 한 번 값을 얻은 지표가 다시 측정 불가가 돼도 "없는 지표"로 돌아가지 않는다.
        series.Write(float.NaN, measured: true);
        Assert.True(series.HasMeasurement);
    }

    [Fact]
    public void Measured_flags_survive_wraparound()
    {
        var series = new MetricSeries(4);
        for (int i = 0; i < 10; i++) series.Write(i, measured: i % 2 == 0);

        // 남아 있는 것은 6,7,8,9 — 짝수만 실측
        Assert.True(series.IsMeasured(0));    // 6
        Assert.False(series.IsMeasured(1));   // 7
        Assert.True(series.IsMeasured(2));    // 8
        Assert.False(series.IsMeasured(3));   // 9
    }

    [Fact]
    public void CopyLatest_can_report_measured_flags_alongside_values()
    {
        var series = new MetricSeries(8);
        series.Write(1f, true);
        series.Write(1f, false);
        series.Write(2f, true);

        Span<float> values = stackalloc float[3];
        Span<bool> measured = stackalloc bool[3];
        int n = series.CopyLatest(values, measured);

        Assert.Equal(3, n);
        Assert.Equal([1f, 1f, 2f], values.ToArray());
        Assert.Equal([true, false, true], measured.ToArray());
    }

    [Fact]
    public void Held_values_reach_the_chart_but_not_the_statistics()
    {
        var registry = new MetricRegistry(64);
        var handle = registry.Register(
            new DeviceInfo("gpu:0", DeviceClass.Gpu, "GPU", "GPU", IconKind.GpuGeneric),
            [MetricKind.GpuTemp]);
        int slot = handle.SlotOf(MetricKind.GpuTemp);

        // 실측 60 → 유지 60 → 유지 60 → 실측 80  (Slow 티어가 4틱마다 한 번 도는 모양)
        registry.CommitAll([60f], [true]);
        registry.CommitAll([60f], [false]);
        registry.CommitAll([60f], [false]);
        registry.CommitAll([80f], [true]);

        var series = registry.Series(slot)!;
        var stats = registry.StatsSnapshot(slot);

        Assert.Equal(4, series.Count);                 // 시간 축은 촘촘하게 유지
        Assert.Equal(2, stats.Count);                  // 통계는 실측 2건만
        Assert.Equal(70.0, stats.Mean, 6);             // 60,80 의 평균 — 60 을 세 번 세면 65 가 된다
        Assert.Equal(60f, stats.Min);
        Assert.Equal(80f, stats.Max);
    }

    [Fact]
    public void Held_repeats_would_otherwise_deflate_the_standard_deviation()
    {
        var registry = new MetricRegistry(64);
        var handle = registry.Register(
            new DeviceInfo("gpu:0", DeviceClass.Gpu, "GPU", "GPU", IconKind.GpuGeneric),
            [MetricKind.GpuPower]);
        int slot = handle.SlotOf(MetricKind.GpuPower);

        float[] measurements = [100f, 300f, 100f, 300f];
        foreach (var value in measurements)
        {
            registry.CommitAll([value], [true]);
            for (int i = 0; i < 3; i++) registry.CommitAll([value], [false]);
        }

        var correct = registry.StatsSnapshot(slot);

        // 같은 데이터를 전부 실측으로 취급했을 때와 비교
        var naive = StatsAccumulator.Create(0);
        foreach (var value in measurements)
            for (int i = 0; i < 4; i++) naive.Add(value);

        Assert.Equal(4, correct.Count);
        Assert.Equal(16, naive.Count);
        Assert.True(correct.StdDev > naive.StdDev,
            $"반복값을 세면 표준편차가 낮게 나온다. 실측만={correct.StdDev:F3} 전부={naive.StdDev:F3}");
    }

    [Fact]
    public async Task Engine_marks_slow_tier_repeats_as_held()
    {
        var registry = new MetricRegistry(64);
        var options = new SamplingOptions
        {
            FastPeriod = TimeSpan.FromMilliseconds(250),
            SlowPeriod = TimeSpan.FromMilliseconds(1000),   // 4 Fast 틱마다
        };
        await using var engine = new SampleEngine(registry, options);

        var fast = new FakeProvider("fast", SensorTier.Fast, MetricKind.GpuUtil, "gpu:fast", 0);
        var slow = new FakeProvider("slow", SensorTier.Slow, MetricKind.GpuTemp, "gpu:slow", 1);
        await engine.AddProviderAsync(fast);
        await engine.AddProviderAsync(slow);

        for (int i = 0; i < 8; i++) engine.TickOnce(0.25);

        var fastSeries = registry.Series(fast.Slot)!;
        var slowSeries = registry.Series(slow.Slot)!;

        // 두 시리즈의 길이가 같아야 동기화 스크럽에서 같은 인덱스가 같은 시각을 가리킨다.
        Assert.Equal(8, fastSeries.Count);
        Assert.Equal(8, slowSeries.Count);

        Assert.Equal(8, registry.StatsSnapshot(fast.Slot).Count);
        Assert.Equal(2, registry.StatsSnapshot(slow.Slot).Count);   // 8틱 ÷ 4 = 2회 실측
        Assert.Equal(8, fast.SampleCalls);
        Assert.Equal(2, slow.SampleCalls);
    }

    private sealed class FakeProvider(
        string id, SensorTier tier, MetricKind kind, string key, int seed) : ISensorProvider
    {
        private int _n = seed;

        public string Id => id;
        public SensorTier Tier => tier;
        public bool IsAvailable => true;
        public int Slot { get; private set; } = -1;
        public int SampleCalls { get; private set; }

        public ValueTask InitializeAsync(MetricRegistry registry, CancellationToken cancellationToken)
        {
            var handle = registry.Register(
                new DeviceInfo(key, DeviceClass.Gpu, id, id, IconKind.GpuGeneric), [kind]);
            Slot = handle.SlotOf(kind);
            return ValueTask.CompletedTask;
        }

        public void Sample(in SampleWriter writer)
        {
            SampleCalls++;
            writer.Write(Slot, _n++);
        }

        public void Dispose() { }
    }
}

using ChronoLoad.Core.Metrics;

namespace ChronoLoad.Core.Tests;

public class StatsAccumulatorTests
{
    [Fact]
    public void Empty_accumulator_reports_nothing()
    {
        var stats = StatsAccumulator.Create(0);

        Assert.True(stats.IsEmpty);
        Assert.True(double.IsNaN(stats.Mean));
        Assert.True(float.IsNaN(stats.Min));
        Assert.True(float.IsNaN(stats.Max));
        Assert.True(double.IsNaN(stats.StdDev));
    }

    [Fact]
    public void Tracks_mean_min_max()
    {
        var stats = StatsAccumulator.Create(0);
        foreach (float v in new[] { 4f, 8f, 15f, 16f, 23f, 42f }) stats.Add(v);

        Assert.Equal(6, stats.Count);
        Assert.Equal(18.0, stats.Mean, 10);
        Assert.Equal(4f, stats.Min);
        Assert.Equal(42f, stats.Max);
    }

    [Fact]
    public void Welford_matches_the_naive_formula_on_well_conditioned_data()
    {
        var random = new Random(1234);
        var samples = Enumerable.Range(0, 5000).Select(_ => (float)(random.NextDouble() * 100)).ToArray();

        var stats = StatsAccumulator.Create(0);
        foreach (var v in samples) stats.Add(v);

        double mean = samples.Average(v => (double)v);
        double naive = samples.Sum(v => (v - mean) * (v - mean)) / (samples.Length - 1);

        Assert.Equal(mean, stats.Mean, 6);
        Assert.Equal(Math.Sqrt(naive), stats.StdDev, 6);
    }

    [Fact]
    public void Welford_survives_a_large_offset_where_the_naive_sum_of_squares_collapses()
    {
        // 큰 평균 위에 작은 분산이 얹힌 경우. Σx² − (Σx)²/n 로 계산하면 자릿수가 상쇄돼 음수까지 나온다.
        const double offset = 1e8;
        float[] samples = [(float)(offset + 1), (float)(offset + 2), (float)(offset + 3), (float)(offset + 4)];

        var stats = StatsAccumulator.Create(0);
        foreach (var v in samples) stats.Add(v);

        Assert.True(stats.Variance >= 0, "분산이 음수가 되면 안 된다.");
        Assert.False(double.IsNaN(stats.StdDev));
    }

    [Fact]
    public void Gaps_are_excluded_from_the_aggregate()
    {
        var stats = StatsAccumulator.Create(0);
        stats.Add(10f);
        stats.Add(float.NaN);   // 이번 주기에 값 없음
        stats.Add(20f);

        Assert.Equal(2, stats.Count);
        Assert.Equal(15.0, stats.Mean, 10);
        Assert.Equal(10f, stats.Min);
        Assert.Equal(20f, stats.Max);
    }

    [Fact]
    public void Reset_clears_everything_and_stamps_the_new_start()
    {
        var stats = StatsAccumulator.Create(0);
        stats.Add(50f);

        long now = DateTime.UtcNow.Ticks;
        stats.Reset(now);

        Assert.True(stats.IsEmpty);
        Assert.Equal(now, stats.ResetTimestampUtcTicks);
        Assert.True(float.IsNaN(stats.Min));

        stats.Add(7f);
        Assert.Equal(7f, stats.Min);
        Assert.Equal(7f, stats.Max);
    }

    [Fact]
    public void Elapsed_never_goes_negative()
    {
        long now = DateTime.UtcNow.Ticks;
        var stats = StatsAccumulator.Create(now);

        Assert.Equal(TimeSpan.Zero, stats.Elapsed(now - TimeSpan.FromSeconds(5).Ticks));
        Assert.Equal(5, stats.Elapsed(now + TimeSpan.FromSeconds(5).Ticks).TotalSeconds, 3);
    }

    [Fact]
    public void Variance_needs_two_samples()
    {
        var stats = StatsAccumulator.Create(0);
        stats.Add(1f);

        Assert.True(double.IsNaN(stats.Variance));
        stats.Add(3f);
        Assert.Equal(2.0, stats.Variance, 10);
    }
}

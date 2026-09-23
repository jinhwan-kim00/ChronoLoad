using ChronoLoad.Core.Metrics;

namespace ChronoLoad.Core.Tests;

public class PercentileTrackerTests
{
    [Fact]
    public void Empty_tracker_returns_nan()
    {
        Assert.True(double.IsNaN(PercentileTracker.ForPercent().Quantile(0.95)));
    }

    [Fact]
    public void Percent_quantiles_land_within_one_bucket()
    {
        var tracker = PercentileTracker.ForPercent();
        for (int i = 0; i <= 100; i++) tracker.Add(i);

        Assert.Equal(50, tracker.Quantile(0.50), 1);
        Assert.Equal(95, tracker.Quantile(0.95), 1);
        Assert.Equal(100, tracker.Quantile(1.00), 1);
    }

    [Fact]
    public void P95_ignores_the_long_tail_of_low_values()
    {
        var tracker = PercentileTracker.ForPercent();
        for (int i = 0; i < 950; i++) tracker.Add(10);
        for (int i = 0; i < 50; i++) tracker.Add(90);

        Assert.Equal(10, tracker.Quantile(0.90), 1);
        Assert.True(tracker.Quantile(0.99) >= 89);
    }

    [Fact]
    public void Byte_scale_tracker_spans_many_orders_of_magnitude()
    {
        var tracker = PercentileTracker.ForBytes();
        foreach (double v in new[] { 1e3, 1e6, 1e9, 4e9 }) tracker.Add(v);

        double median = tracker.Quantile(0.5);
        // 로그 버킷이라 오차가 있다. 자릿수만 맞으면 충분하다.
        Assert.InRange(median, 5e5, 5e6);
        Assert.InRange(tracker.Quantile(1.0), 2e9, 8e9);
    }

    [Fact]
    public void Values_outside_the_range_still_count_toward_the_total()
    {
        var tracker = PercentileTracker.ForRange(0, 10, buckets: 11);
        tracker.Add(-5);
        tracker.Add(5);
        tracker.Add(50);

        Assert.Equal(3, tracker.Count);
        Assert.Equal(10, tracker.Quantile(1.0), 1);
    }

    [Fact]
    public void Gaps_are_ignored()
    {
        var tracker = PercentileTracker.ForPercent();
        tracker.Add(double.NaN);

        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void Reset_clears_the_histogram()
    {
        var tracker = PercentileTracker.ForPercent();
        for (int i = 0; i < 100; i++) tracker.Add(80);

        tracker.Reset();

        Assert.Equal(0, tracker.Count);
        Assert.True(double.IsNaN(tracker.Quantile(0.5)));
    }
}

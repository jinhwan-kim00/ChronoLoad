using ChronoLoad.Core.Metrics;

namespace ChronoLoad.Core.Tests;

public class PercentileTrackerTests
{
    private static void AssertWithinRelative(double expected, double actual, double tolerance = PercentileTracker.RelativeAccuracy)
    {
        double error = Math.Abs(actual - expected) / Math.Abs(expected);
        Assert.True(error <= tolerance + 1e-12, $"기대 {expected}, 실제 {actual} (상대 오차 {error:P3})");
    }

    [Fact]
    public void Empty_tracker_returns_nan()
    {
        Assert.True(double.IsNaN(new PercentileTracker().Quantile(0.95)));
        Assert.True(double.IsNaN(new PercentileTracker().FractionAtOrAbove(90)));
    }

    [Fact]
    public void Percent_quantiles_are_within_the_relative_accuracy()
    {
        var tracker = new PercentileTracker();
        for (int i = 0; i <= 100; i++) tracker.Add(i);

        AssertWithinRelative(50, tracker.Quantile(0.50));
        AssertWithinRelative(95, tracker.Quantile(0.95));
        Assert.Equal(100, tracker.Quantile(1.00));
    }

    [Fact]
    public void P95_ignores_the_long_tail_of_low_values()
    {
        var tracker = new PercentileTracker();
        for (int i = 0; i < 950; i++) tracker.Add(10);
        for (int i = 0; i < 50; i++) tracker.Add(90);

        AssertWithinRelative(10, tracker.Quantile(0.90));
        AssertWithinRelative(90, tracker.Quantile(0.99));
    }

    /// <summary>
    /// 실사용 보고: 메모리 사용량이 14.6~16 GB 였는데 p95 가 12.35 GB 로 나왔다.
    /// 64칸 로그 히스토그램의 칸 경계였다. 값의 크기와 무관하게 상대 오차 안에 들어야 한다.
    /// </summary>
    [Fact]
    public void Large_byte_values_are_not_snapped_to_a_coarse_bucket()
    {
        var tracker = new PercentileTracker();
        for (int i = 0; i < 1000; i++) tracker.Add(14.6e9 + i * 1.4e6);   // 14.6 ~ 16.0 GB

        double p95 = tracker.Quantile(0.95);
        AssertWithinRelative(14.6e9 + 950 * 1.4e6, p95);
        Assert.InRange(p95, 14.6e9, 16.0e9);
    }

    /// <summary>
    /// 실사용 보고: 유휴 전력 최소 22.51 W 인데 p95 가 15.87 W 였다(0~1000 W 64칸의 칸 폭).
    /// 분위수는 어떤 경우에도 관측 범위 밖으로 나가면 안 된다.
    /// </summary>
    [Fact]
    public void Quantiles_never_leave_the_observed_range()
    {
        var tracker = new PercentileTracker();
        foreach (double w in new[] { 22.51, 22.6, 22.8, 23.0, 23.4 }) tracker.Add(w);

        for (double q = 0; q <= 1.0; q += 0.05)
            Assert.InRange(tracker.Quantile(q), 22.51, 23.4);
    }

    [Fact]
    public void Different_magnitudes_do_not_collapse_to_the_same_value()
    {
        var read = new PercentileTracker();
        var shared = new PercentileTracker();
        read.Add(1.05e9);
        shared.Add(1.12e9);

        // 예전에는 둘 다 같은 칸(1097631034)으로 답했다.
        Assert.NotEqual(read.Quantile(0.95), shared.Quantile(0.95));
    }

    [Fact]
    public void Byte_scale_tracker_spans_many_orders_of_magnitude()
    {
        var tracker = new PercentileTracker();
        foreach (double v in new[] { 1e3, 1e6, 1e9, 4e9 }) tracker.Add(v);

        AssertWithinRelative(1e6, tracker.Quantile(0.5));
        Assert.Equal(4e9, tracker.Quantile(1.0));
    }

    [Fact]
    public void Zero_and_negative_values_keep_their_order()
    {
        var tracker = new PercentileTracker();
        tracker.Add(-5);
        tracker.Add(0);
        tracker.Add(0);
        tracker.Add(5);

        Assert.Equal(-5, tracker.Quantile(0.25));
        Assert.Equal(0, tracker.Quantile(0.5));
        AssertWithinRelative(-5, tracker.Quantile(0.01));
        Assert.Equal(5, tracker.Quantile(1.0));
    }

    [Fact]
    public void Fraction_at_or_above_counts_the_saturated_time()
    {
        var tracker = new PercentileTracker();
        for (int i = 0; i < 70; i++) tracker.Add(20);
        for (int i = 0; i < 30; i++) tracker.Add(100);

        // 평균 44% 지만 30% 의 시간은 포화였다. 평균이 가리는 것이 이것이다.
        Assert.Equal(0.30, tracker.FractionAtOrAbove(90), 3);
        Assert.Equal(1.0, tracker.FractionAtOrAbove(20));
        Assert.Equal(0.0, tracker.FractionAtOrAbove(101));
    }

    [Fact]
    public void Gaps_are_ignored()
    {
        var tracker = new PercentileTracker();
        tracker.Add(double.NaN);

        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void Reset_clears_the_histogram()
    {
        var tracker = new PercentileTracker();
        for (int i = 0; i < 100; i++) tracker.Add(80);

        tracker.Reset();

        Assert.Equal(0, tracker.Count);
        Assert.True(double.IsNaN(tracker.Quantile(0.5)));

        tracker.Add(3);
        Assert.Equal(3, tracker.Quantile(0.5));
    }
}

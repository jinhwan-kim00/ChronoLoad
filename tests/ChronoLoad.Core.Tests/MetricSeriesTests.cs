using ChronoLoad.Core.Metrics;

namespace ChronoLoad.Core.Tests;

public class MetricSeriesTests
{
    [Fact]
    public void Empty_series_reports_no_samples()
    {
        var series = new MetricSeries(8);
        Assert.Equal(0, series.Count);
        Assert.True(float.IsNaN(series.Latest));
        Assert.Equal(0, series.CopyLatest(stackalloc float[4]));
    }

    [Fact]
    public void Writes_are_readable_in_chronological_order()
    {
        var series = new MetricSeries(8);
        for (int i = 1; i <= 5; i++) series.Write(i);

        Assert.Equal(5, series.Count);
        Assert.Equal(5f, series.Latest);

        Span<float> buffer = stackalloc float[8];
        int n = series.CopyLatest(buffer);

        Assert.Equal(5, n);
        Assert.Equal([1f, 2f, 3f, 4f, 5f], buffer[..n].ToArray());
    }

    [Fact]
    public void Wraparound_keeps_the_newest_samples()
    {
        var series = new MetricSeries(4);
        for (int i = 1; i <= 10; i++) series.Write(i);

        Assert.Equal(4, series.Count);
        Assert.Equal(10, series.Written);
        Assert.Equal(10f, series.Latest);

        Span<float> buffer = stackalloc float[4];
        int n = series.CopyLatest(buffer);

        Assert.Equal(4, n);
        Assert.Equal([7f, 8f, 9f, 10f], buffer.ToArray());
    }

    [Fact]
    public void CopyLatest_into_a_smaller_span_returns_only_the_newest()
    {
        var series = new MetricSeries(16);
        for (int i = 1; i <= 10; i++) series.Write(i);

        Span<float> buffer = stackalloc float[3];
        int n = series.CopyLatest(buffer);

        Assert.Equal(3, n);
        Assert.Equal([8f, 9f, 10f], buffer.ToArray());
    }

    [Fact]
    public void Indexer_walks_from_oldest_surviving_sample()
    {
        var series = new MetricSeries(4);
        for (int i = 1; i <= 6; i++) series.Write(i);

        Assert.Equal(3f, series[0]);
        Assert.Equal(6f, series[3]);
        Assert.Throws<ArgumentOutOfRangeException>(() => series[4]);
    }

    [Fact]
    public void Decimation_preserves_spikes()
    {
        // 평균이나 단순 샘플링이면 한가운데 스파이크가 사라진다. min-max 는 살려야 한다.
        var source = new float[200];
        Array.Fill(source, 10f);
        source[97] = 99f;    // 피크
        source[150] = 1f;    // 골

        Span<float> destination = stackalloc float[20];
        int n = MetricSeries.Decimate(source, destination);

        Assert.True(n <= 20);
        var result = destination[..n].ToArray();
        Assert.Contains(99f, result);
        Assert.Contains(1f, result);
    }

    [Fact]
    public void Decimation_copies_through_when_source_already_fits()
    {
        float[] source = [1, 2, 3];
        Span<float> destination = stackalloc float[8];

        int n = MetricSeries.Decimate(source, destination);

        Assert.Equal(3, n);
        Assert.Equal([1f, 2f, 3f], destination[..n].ToArray());
    }

    [Fact]
    public void Decimation_keeps_chronological_order_within_a_bucket()
    {
        // 골이 먼저, 피크가 나중인 구간 → 출력도 같은 순서여야 형태가 유지된다.
        float[] source = [5, 1, 5, 9, 5, 5, 5, 5];
        Span<float> destination = stackalloc float[2];

        int n = MetricSeries.Decimate(source, destination);

        Assert.Equal(2, n);
        Assert.Equal(1f, destination[0]);
        Assert.Equal(9f, destination[1]);
    }

    [Fact]
    public void Decimation_ignores_gaps()
    {
        float[] source = [float.NaN, float.NaN, 4f, float.NaN];
        Span<float> destination = stackalloc float[2];

        int n = MetricSeries.Decimate(source, destination);

        Assert.True(n >= 1);
        Assert.Contains(4f, destination[..n].ToArray());
    }
}

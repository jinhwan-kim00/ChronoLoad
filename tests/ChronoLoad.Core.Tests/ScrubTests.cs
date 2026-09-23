using ChronoLoad.Core.Layout;
using ChronoLoad.Core.Metrics;

namespace ChronoLoad.Core.Tests;

/// <summary>
/// §8.7 시간 동기화 오버레이. 선이 가리키는 순간과 숫자가 읽는 순간이 같아야 한다.
/// </summary>
public class ScrubTests
{
    private static MetricSeries Filled(int count, int capacity = 3600)
    {
        var series = new MetricSeries(capacity);
        for (int i = 0; i < count; i++) series.Write(i);      // 값 = 절대 인덱스
        return series;
    }

    [Fact]
    public void The_window_index_reads_the_samples_the_chart_actually_draws()
    {
        // 차트는 최근 240개를 그리는데 버퍼에는 1000개가 있다. 표시 창 인덱스를 그대로
        // 버퍼 인덱스로 쓰면 화면에 없는 옛날 샘플을 읽는다 — 그림과 숫자가 다른 곳을
        // 가리키는데 둘 다 그럴듯해 보여서 알아차리기 어렵다. 실제로 그렇게 틀려 있었다.
        var series = Filled(1000);

        Assert.Equal(760, series.AbsoluteIndexOf(0, 240));      // 창의 왼쪽 끝
        Assert.Equal(999, series.AbsoluteIndexOf(239, 240));    // 창의 오른쪽 끝 = 가장 최근
    }

    [Fact]
    public void The_rightmost_window_index_is_always_the_newest_sample()
    {
        foreach (int count in new[] { 5, 240, 241, 3600 })
        {
            var series = Filled(count);
            int visible = Math.Min(240, count);
            Assert.Equal(count - 1, series.AbsoluteIndexOf(visible - 1, 240));
        }
    }

    [Fact]
    public void A_half_filled_buffer_maps_index_straight_through()
    {
        // 버퍼가 창보다 짧으면 차트도 있는 만큼만 그리므로 인덱스가 그대로 대응한다.
        var series = Filled(30);

        Assert.Equal(0, series.AbsoluteIndexOf(0, 240));
        Assert.Equal(29, series.AbsoluteIndexOf(29, 240));
        Assert.Equal(-1, series.AbsoluteIndexOf(30, 240));      // 아직 없는 자리
    }

    [Fact]
    public void An_empty_series_has_no_window()
    {
        Assert.Equal(-1, new MetricSeries(64).AbsoluteIndexOf(0, 240));
    }

    [Fact]
    public void A_pinned_moment_drifts_left_as_new_samples_arrive()
    {
        // 클릭으로 고정하는 것은 자리가 아니라 순간이다. 그래프가 왼쪽으로 흐르면
        // 선도 같이 흘러야 가리키던 순간을 놓치지 않는다.
        Assert.Equal(119, ScrubDrift.Advance(120, 240));
        Assert.Equal(117, ScrubDrift.Advance(120, 240, newSamples: 3));
    }

    [Fact]
    public void A_pin_on_the_newest_sample_stays_and_keeps_showing_the_present()
    {
        // 맨 오른쪽에는 흘려보낼 것이 없다. 거기 머물며 현재값을 따라가는 것이
        // "지금을 본다"는 뜻과 맞는다.
        Assert.Equal(239, ScrubDrift.Advance(239, 240));
        Assert.Equal(239, ScrubDrift.Advance(239, 240, newSamples: 10));
    }

    [Fact]
    public void A_moment_that_scrolls_off_the_left_releases_the_pin()
    {
        // 창 밖으로 나간 순간을 왼쪽 끝에 붙들어 두면, 선은 그대로인데 가리키는 순간이
        // 매 틱 달라진다 — 고치려던 문제가 반대쪽 끝에서 되살아난다.
        Assert.Null(ScrubDrift.Advance(0, 240));
        Assert.Null(ScrubDrift.Advance(2, 240, newSamples: 3));
    }
}

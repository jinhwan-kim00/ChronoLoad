using ChronoLoad.Core.Layout;

namespace ChronoLoad.Core.Tests;

/// <summary>
/// 절전 동안에는 샘플이 아예 없다. 링 버퍼에는 끊기기 직전 샘플 바로 옆에 복귀 직후
/// 샘플이 붙고, 차트는 둘을 선으로 이어 그린다 — 그 사이 값이 그렇게 흘렀다고 말하는 셈이다.
/// </summary>
public class SampleGapsTests
{
    private const long Fast = TimeSpan.TicksPerMillisecond * 250;

    private static long[] Stamps(params double[] secondsFromStart)
    {
        var origin = new DateTime(2026, 9, 26, 14, 0, 0, DateTimeKind.Utc).Ticks;
        return [.. secondsFromStart.Select(s => origin + (long)(s * TimeSpan.TicksPerSecond))];
    }

    private static (int Count, bool[] Mask) Mark(long[] stamps, long threshold)
    {
        var mask = new bool[stamps.Length];
        return (SampleGaps.Mark(stamps, threshold, mask), mask);
    }

    [Fact]
    public void Threshold_is_eight_nominal_periods()
    {
        Assert.Equal(TimeSpan.FromSeconds(2).Ticks,
                     SampleGaps.ThresholdFor(TimeSpan.FromMilliseconds(250)));
    }

    [Fact]
    public void Evenly_spaced_samples_have_no_gaps()
    {
        var (count, mask) = Mark(Stamps(0, 0.25, 0.5, 0.75, 1.0), SampleGaps.ThresholdFor(TimeSpan.FromMilliseconds(250)));

        Assert.Equal(0, count);
        Assert.All(mask, m => Assert.False(m));
    }

    /// <summary>
    /// 백오프는 공백이 아니다. 가장 느린 배속(×4 = 1초)에서도 걸리면 안 된다 —
    /// 창을 최소화해 둔 동안 차트가 온통 이음매로 덮인다.
    /// </summary>
    [Fact]
    public void Adaptive_backoff_is_not_a_gap()
    {
        var (count, _) = Mark(Stamps(0, 1, 2, 3, 4), SampleGaps.ThresholdFor(TimeSpan.FromMilliseconds(250)));
        Assert.Equal(0, count);
    }

    [Fact]
    public void A_suspend_shows_up_as_one_seam_between_two_adjacent_samples()
    {
        // 14:00:00.5 에서 자고 14:02 에 깼다 — 그 사이 샘플은 없다.
        var (count, mask) = Mark(Stamps(0, 0.25, 0.5, 120, 120.25), SampleGaps.ThresholdFor(TimeSpan.FromMilliseconds(250)));

        Assert.Equal(1, count);
        Assert.True(mask[3]);
        Assert.False(mask[0]);
        Assert.False(mask[1]);
        Assert.False(mask[2]);
        Assert.False(mask[4]);   // 복귀 뒤는 다시 정상 간격이다
    }

    [Fact]
    public void Several_suspends_are_marked_independently()
    {
        var (count, mask) = Mark(Stamps(0, 0.25, 30, 30.25, 90, 90.25), SampleGaps.ThresholdFor(TimeSpan.FromMilliseconds(250)));

        Assert.Equal(2, count);
        Assert.True(mask[2]);
        Assert.True(mask[4]);
    }

    [Fact]
    public void The_first_sample_is_never_a_gap_because_nothing_precedes_it()
    {
        var (count, mask) = Mark(Stamps(500), Fast * 8);

        Assert.Equal(0, count);
        Assert.False(mask[0]);
    }

    [Fact]
    public void Time_going_backwards_breaks_the_line_rather_than_pretending_it_continues()
    {
        // 시스템 시각이 뒤로 조정되는 경우. 이어 그리면 선이 되감긴다.
        var (count, mask) = Mark(Stamps(0, 0.25, 0.1, 0.35), Fast * 8);

        Assert.Equal(1, count);
        Assert.True(mask[2]);
    }

    [Fact]
    public void A_zero_threshold_marks_nothing()
    {
        var (count, _) = Mark(Stamps(0, 100, 200), 0);
        Assert.Equal(0, count);
    }

    [Fact]
    public void The_mask_is_cleared_before_use_so_a_reused_buffer_cannot_leak_old_gaps()
    {
        var stamps = Stamps(0, 0.25, 0.5);
        var mask = new bool[3];
        Array.Fill(mask, true);

        int count = SampleGaps.Mark(stamps, Fast * 8, mask);

        Assert.Equal(0, count);
        Assert.All(mask, m => Assert.False(m));
    }

    [Fact]
    public void A_mask_shorter_than_the_stamps_is_rejected()
    {
        var stamps = Stamps(0, 1, 2);
        Assert.Throws<ArgumentException>(() => SampleGaps.Mark(stamps, Fast, new bool[2]));
    }
}

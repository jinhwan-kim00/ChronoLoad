using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Layout;
using ChronoLoad.Core.Metrics;

namespace ChronoLoad.Core.Tests;

/// <summary>
/// 시간 폭(§9.4). <c>Ctrl</c>+휠이 사다리를 오르내리고 차트 더블클릭이 표준으로 되돌린다.
/// 폭은 점 개수가 아니라 <b>초</b>로 세므로 백오프 구간에서도 화면이 같은 길이를 덮는다.
/// </summary>
public class TimeWidthTests
{
    private static readonly long Origin = new DateTime(2026, 9, 26, 14, 0, 0, DateTimeKind.Utc).Ticks;

    private static MetricRegistry Filled(int capacity, params double[] secondsFromStart)
    {
        var registry = new MetricRegistry(capacity);
        registry.Register(new DeviceInfo("cpu", DeviceClass.System, "CPU", "CPU", IconKind.Cpu),
                          [MetricKind.CpuTotal]);
        foreach (double s in secondsFromStart)
            registry.PushFrame([1f], Origin + (long)(s * TimeSpan.TicksPerSecond));
        return registry;
    }

    private static double[] Every(double step, int count) =>
        [.. Enumerable.Range(0, count).Select(i => i * step)];

    // ── 사다리 ────────────────────────────────────────────────

    [Fact]
    public void The_standard_width_is_sixty_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), TimeWidthLadder.Standard);
    }

    [Fact]
    public void Stepping_moves_one_rung_at_a_time()
    {
        var w = TimeWidthLadder.Standard;
        Assert.Equal(TimeSpan.FromSeconds(180), w = TimeWidthLadder.Step(w, +1));
        Assert.Equal(TimeSpan.FromSeconds(600), w = TimeWidthLadder.Step(w, +1));
        Assert.Equal(TimeSpan.FromSeconds(900), w = TimeWidthLadder.Step(w, +1));
        Assert.Equal(TimeSpan.FromSeconds(600), w = TimeWidthLadder.Step(w, -1));
        Assert.Equal(TimeSpan.FromSeconds(180), w = TimeWidthLadder.Step(w, -1));
        Assert.Equal(TimeSpan.FromSeconds(60), w = TimeWidthLadder.Step(w, -1));
        Assert.Equal(TimeSpan.FromSeconds(30), TimeWidthLadder.Step(w, -1));
    }

    /// <summary>순환하지 않는다. 끝에서 더 굴려도 반대편으로 넘어가면 놀란다.</summary>
    [Fact]
    public void The_ladder_clamps_at_both_ends_instead_of_wrapping()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), TimeWidthLadder.Step(TimeSpan.FromSeconds(30), -1));
        Assert.Equal(TimeSpan.FromSeconds(900), TimeWidthLadder.Step(TimeSpan.FromSeconds(900), +1));
    }

    [Fact]
    public void A_value_off_the_ladder_snaps_to_the_nearest_rung()
    {
        Assert.Equal(TimeSpan.FromSeconds(180), TimeWidthLadder.Nearest(TimeSpan.FromSeconds(200)));
        Assert.Equal(TimeSpan.FromSeconds(60), TimeWidthLadder.Nearest(TimeSpan.FromSeconds(50)));
    }

    [Theory]
    [InlineData(30, "30s")]
    [InlineData(60, "1m")]
    [InlineData(180, "3m")]
    [InlineData(600, "10m")]
    [InlineData(900, "15m")]
    public void Labels_are_as_short_as_they_can_be(int seconds, string expected)
    {
        Assert.Equal(expected, TimeWidthLadder.Label(TimeSpan.FromSeconds(seconds)));
    }

    // ── 초 → 점 개수 ──────────────────────────────────────────

    [Fact]
    public void Sixty_seconds_at_the_full_pace_is_two_hundred_forty_points()
    {
        var registry = Filled(3600, Every(0.25, 600));   // 150초치
        Assert.Equal(241, registry.PointsWithin(TimeSpan.FromSeconds(60)));
    }

    /// <summary>
    /// 이 환산을 두는 이유. 배속이 ×4 로 내려가면 같은 60초가 점 61개다 —
    /// 점 개수를 고정했다면 화면이 4분을 덮으면서도 "60초"라고 말했을 것이다.
    /// </summary>
    [Fact]
    public void The_same_width_covers_fewer_points_when_sampling_slowed_down()
    {
        var registry = Filled(3600, Every(1.0, 300));    // 300초치, 1초 간격
        Assert.Equal(61, registry.PointsWithin(TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void Asking_for_more_than_the_buffer_holds_returns_what_there_is()
    {
        var registry = Filled(3600, Every(0.25, 100));   // 25초치뿐
        Assert.Equal(100, registry.PointsWithin(TimeSpan.FromSeconds(900)));
    }

    [Fact]
    public void An_empty_registry_has_no_points()
    {
        var registry = Filled(16);
        Assert.Equal(0, registry.PointsWithin(TimeSpan.FromSeconds(60)));
    }

    /// <summary>
    /// 절전으로 끊긴 구간(§13) 앞쪽은 "최근 60초"에 들지 않는다. 복귀 직후 점이 몇 개뿐인 것은
    /// 화면이 고장난 것이 아니라 <b>실제로 가진 전부</b>가 그것이기 때문이다.
    /// </summary>
    [Fact]
    public void Samples_from_before_a_suspend_fall_outside_the_recent_window()
    {
        // 0~10초를 250ms 로 채우고 2시간 자고 깨어나 3개만 더 찍었다.
        var before = Every(0.25, 41);
        double[] after = [7200, 7200.25, 7200.5];
        var registry = Filled(3600, [.. before, .. after]);

        Assert.Equal(3, registry.PointsWithin(TimeSpan.FromSeconds(60)));
        Assert.Equal(44, registry.PointsWithin(TimeSpan.FromHours(3)));
    }

    [Fact]
    public void A_zero_width_still_keeps_the_newest_point()
    {
        var registry = Filled(3600, Every(0.25, 40));
        Assert.Equal(1, registry.PointsWithin(TimeSpan.Zero));
    }

    [Fact]
    public void The_window_never_reaches_past_what_the_ring_still_holds()
    {
        var registry = Filled(8, Every(0.25, 40));       // 용량 8, 40프레임 밀어 넣음
        Assert.Equal(8, registry.PointsWithin(TimeSpan.FromSeconds(900)));
    }
}

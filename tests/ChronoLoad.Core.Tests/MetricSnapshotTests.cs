using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Metrics;

namespace ChronoLoad.Core.Tests;

/// <summary>
/// 스냅샷(§9.6). 흐르는 화면에서는 방금 지나간 구간을 잴 수 없으므로 지금 가진 것을
/// 통째로 떠내 얼린다. 떠낸 뒤에는 <b>새 데이터가 들어오지 않는다</b>.
/// </summary>
public class MetricSnapshotTests
{
    private static readonly long Origin = new DateTime(2026, 9, 26, 14, 0, 0, DateTimeKind.Utc).Ticks;

    private static long At(double seconds) => Origin + (long)(seconds * TimeSpan.TicksPerSecond);

    private static DeviceInfo Cpu => new("cpu", DeviceClass.System, "CPU", "CPU", IconKind.Cpu);
    private static DeviceInfo Gpu => new("gpu:0", DeviceClass.Gpu, "RTX", "RTX 4090", IconKind.GpuNvidia);

    private static MetricRegistry Registry(int capacity = 64)
    {
        var registry = new MetricRegistry(capacity);
        registry.Register(Cpu, [MetricKind.CpuTotal]);
        return registry;
    }

    [Fact]
    public void An_empty_registry_yields_an_empty_snapshot()
    {
        var snapshot = MetricSnapshot.Capture(Registry());

        Assert.True(snapshot.IsEmpty);
        Assert.Equal(0, snapshot.Count);
        Assert.Equal(TimeSpan.Zero, snapshot.Span);
    }

    [Fact]
    public void Capture_takes_values_measured_flags_and_times_together()
    {
        var registry = Registry();
        registry.PushFrame([10f], At(0));
        registry.PushFrame([20f], At(0.25));
        registry.PushFrame([30f], At(0.5));

        var snapshot = MetricSnapshot.Capture(registry);

        Assert.Equal(3, snapshot.Count);
        Assert.Equal([At(0), At(0.25), At(0.5)], snapshot.Timestamps);

        var cpu = Assert.Single(snapshot.Metrics);
        Assert.Equal(MetricKind.CpuTotal, cpu.Kind);
        Assert.Equal([10f, 20f, 30f], cpu.Values);
        Assert.Equal(snapshot.Count, cpu.Values.Length);
        Assert.Equal(snapshot.Count, cpu.Measured.Length);
    }

    /// <summary>떠낸 뒤에 레지스트리가 더 굴러가도 스냅샷은 그대로여야 한다.</summary>
    [Fact]
    public void The_snapshot_does_not_follow_the_registry_afterwards()
    {
        var registry = Registry();
        registry.PushFrame([10f], At(0));
        var snapshot = MetricSnapshot.Capture(registry);

        for (int i = 1; i < 20; i++) registry.PushFrame([99f], At(i * 0.25));

        Assert.Equal(1, snapshot.Count);
        Assert.Equal([10f], Assert.Single(snapshot.Metrics).Values);
    }

    /// <summary>
    /// 스냅샷을 열어 둔 채 장치를 빼도 이름을 계속 말해야 한다. 라이브 핸들을 참조하면
    /// 과거를 보여주다 갑자기 현재를 모른다고 하는 물건이 된다.
    /// </summary>
    [Fact]
    public void Device_names_survive_the_device_being_removed()
    {
        var registry = Registry();
        registry.Register(Gpu, [MetricKind.GpuUtil]);
        registry.PushFrame([10f, 50f], At(0));

        var snapshot = MetricSnapshot.Capture(registry);
        registry.Retire("gpu:0", At(1));
        registry.PurgeRetired(At(3600), TimeSpan.Zero);

        var gpu = snapshot.Metrics.Single(m => m.Kind == MetricKind.GpuUtil);
        Assert.Equal("RTX 4090", gpu.Device.FullName);
        Assert.Equal([50f], gpu.Values);
    }

    /// <summary>
    /// 늦게 등록된 장치는 시리즈가 짧다. 앞을 NaN 으로 비워 <b>끝을 맞춰야</b>
    /// 시각 배열과 인덱스가 그대로 대응한다(§7.4).
    /// </summary>
    [Fact]
    public void A_late_device_is_padded_at_the_front_so_the_ends_line_up()
    {
        var registry = Registry();
        for (int i = 0; i < 4; i++) registry.PushFrame([i], At(i));

        registry.Register(Gpu, [MetricKind.GpuUtil]);
        for (int i = 4; i < 7; i++) registry.PushFrame([i, i * 10], At(i));

        var snapshot = MetricSnapshot.Capture(registry);
        var gpu = snapshot.Metrics.Single(m => m.Kind == MetricKind.GpuUtil);

        Assert.Equal(7, snapshot.Count);
        Assert.Equal(7, gpu.Values.Length);
        Assert.All(gpu.Values[..4], v => Assert.True(float.IsNaN(v)));
        Assert.Equal([40f, 50f, 60f], gpu.Values[4..]);
        Assert.All(gpu.Measured[..4], m => Assert.False(m));
    }

    // ── 구간 통계 ─────────────────────────────────────────────

    [Fact]
    public void Range_stats_cover_the_half_open_interval()
    {
        var registry = Registry();
        foreach (float v in new[] { 10f, 20f, 30f, 40f, 50f })
            registry.PushFrame([v], At(registry.Frames * 0.25));

        var cpu = Assert.Single(MetricSnapshot.Capture(registry).Metrics);
        var stats = cpu.StatsOf(1, 4);          // 20, 30, 40

        Assert.Equal(3, stats.Count);
        Assert.Equal(30, stats.Mean, 6);
        Assert.Equal(20, stats.Min);
        Assert.Equal(40, stats.Max);
    }

    /// <summary>
    /// 유지값은 세지 않는다. 세면 표본 수가 주기 비율만큼 부풀고, 평균이 반복된 쪽으로 끌린다.
    /// </summary>
    [Fact]
    public void Held_repeats_are_left_out_of_the_statistics()
    {
        var registry = Registry();
        registry.CommitAll([10f], [true], At(0));
        registry.CommitAll([10f], [false], At(0.25));
        registry.CommitAll([10f], [false], At(0.5));
        registry.CommitAll([90f], [true], At(0.75));

        var stats = Assert.Single(MetricSnapshot.Capture(registry).Metrics).StatsOf(0, 4);

        Assert.Equal(2, stats.Count);            // 4개가 아니라 2개다
        Assert.Equal(50, stats.Mean, 6);         // 유지값을 세면 30 이 된다
    }

    [Fact]
    public void A_range_with_no_measured_sample_reports_nothing_rather_than_zero()
    {
        var registry = Registry();
        registry.CommitAll([10f], [true], At(0));
        registry.CommitAll([10f], [false], At(0.25));

        var stats = Assert.Single(MetricSnapshot.Capture(registry).Metrics).StatsOf(1, 2);

        Assert.False(stats.HasValue);
        Assert.Equal(0, stats.Count);
        Assert.True(double.IsNaN(stats.Mean));
    }

    [Fact]
    public void Range_bounds_outside_the_data_are_clamped_rather_than_throwing()
    {
        var registry = Registry();
        registry.PushFrame([42f], At(0));
        var cpu = Assert.Single(MetricSnapshot.Capture(registry).Metrics);

        Assert.Equal(1, cpu.StatsOf(-10, 99).Count);
        Assert.False(cpu.StatsOf(5, 9).HasValue);
        Assert.False(cpu.StatsOf(3, 1).HasValue);   // 뒤집힌 구간
    }

    // ── 시각 ─────────────────────────────────────────────────

    [Fact]
    public void Span_and_bounds_come_from_the_time_axis()
    {
        var registry = Registry();
        registry.PushFrame([1f], At(0));
        registry.PushFrame([2f], At(90));

        var snapshot = MetricSnapshot.Capture(registry);

        Assert.Equal(TimeSpan.FromSeconds(90), snapshot.Span);
        Assert.Equal(new DateTime(Origin, DateTimeKind.Utc).ToLocalTime(), snapshot.StartedLocal);
    }

    [Fact]
    public void Index_lookup_finds_the_first_point_at_or_after_a_time()
    {
        var registry = Registry();
        for (int i = 0; i < 5; i++) registry.PushFrame([i], At(i));
        var snapshot = MetricSnapshot.Capture(registry);

        Assert.Equal(0, snapshot.IndexAtOrAfter(At(-1)));
        Assert.Equal(2, snapshot.IndexAtOrAfter(At(2)));
        Assert.Equal(3, snapshot.IndexAtOrAfter(At(2.5)));
        Assert.Equal(5, snapshot.IndexAtOrAfter(At(99)));   // 범위 밖은 Count
    }

    [Fact]
    public void Capture_can_be_limited_to_the_most_recent_points()
    {
        var registry = Registry();
        for (int i = 0; i < 10; i++) registry.PushFrame([i], At(i));

        var snapshot = MetricSnapshot.Capture(registry, maxPoints: 4);

        Assert.Equal(4, snapshot.Count);
        Assert.Equal(At(6), snapshot.Timestamps[0]);
        Assert.Equal([6f, 7f, 8f, 9f], Assert.Single(snapshot.Metrics).Values);
    }
}

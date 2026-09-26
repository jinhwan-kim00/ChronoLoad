using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Metrics;

namespace ChronoLoad.Core.Tests;

/// <summary>
/// 시간 축(§7.4). 시리즈는 값만 담고 차트는 점 간격이 일정하다고 <b>가정하고</b> 그리는데,
/// 적응형 백오프(§6.3)가 Fast 주기를 배수로 늘리므로 실제 간격은 균일하지 않다.
/// 프레임마다 커밋 시각을 남겨 "이 점이 언제인가"를 되물을 수 있게 한다.
/// </summary>
public class TimeAxisTests
{
    private static readonly DateTime Origin = new(2026, 9, 26, 14, 32, 7, DateTimeKind.Utc);

    private static long At(double seconds) => Origin.Ticks + (long)(seconds * TimeSpan.TicksPerSecond);

    private static (MetricRegistry Registry, DeviceHandle Device) NewRegistry(int capacity = 16)
    {
        var registry = new MetricRegistry(capacity);
        var device = registry.Register(
            new DeviceInfo("cpu", DeviceClass.System, "CPU", "CPU", IconKind.Cpu),
            [MetricKind.CpuTotal]);
        return (registry, device);
    }

    [Fact]
    public void Frames_advance_once_per_commit_and_carry_the_commit_time()
    {
        var (registry, device) = NewRegistry();
        Assert.Equal(0, registry.Frames);
        Assert.Null(registry.LatestTimestamp);

        registry.PushFrame([10f], At(0));
        registry.PushFrame([20f], At(0.25));
        registry.PushFrame([30f], At(0.5));

        Assert.Equal(3, registry.Frames);
        Assert.Equal(At(0), registry.TimestampAtFrame(0));
        Assert.Equal(At(0.25), registry.TimestampAtFrame(1));
        Assert.Equal(At(0.5), registry.TimestampAtFrame(2));
        Assert.Equal(At(0.5), registry.LatestTimestamp);

        // 아직 오지 않은 프레임은 값이 아니라 없음이다.
        Assert.Null(registry.TimestampAtFrame(3));
        Assert.Null(registry.TimestampAtFrame(-1));

        var series = registry.Series(device.SlotOf(MetricKind.CpuTotal))!;
        Assert.Equal(3, series.Count);
    }

    [Fact]
    public void Series_index_maps_to_the_frame_that_wrote_it()
    {
        var (registry, device) = NewRegistry();
        for (int i = 0; i < 5; i++) registry.PushFrame([i], At(i * 0.25));

        var series = registry.Series(device.SlotOf(MetricKind.CpuTotal))!;
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(i, registry.FrameAt(series, i));
            Assert.Equal(At(i * 0.25), registry.TimestampAt(series, i));
        }

        Assert.Equal(-1, registry.FrameAt(series, 5));
        Assert.Null(registry.TimestampAt(series, 5));
    }

    /// <summary>
    /// 늦게 등록된 장치는 시리즈가 짧다. 인덱스 0 을 프레임 0 으로 읽으면
    /// 그 장치만 시간 축이 왼쪽으로 밀려 다른 카드와 어긋난다.
    /// </summary>
    [Fact]
    public void A_series_registered_late_still_maps_to_the_right_frames()
    {
        var (registry, cpu) = NewRegistry();
        for (int i = 0; i < 4; i++) registry.PushFrame([i], At(i));

        var gpu = registry.Register(
            new DeviceInfo("gpu:0", DeviceClass.Gpu, "GPU", "GPU", IconKind.GpuGeneric),
            [MetricKind.GpuUtil]);
        for (int i = 4; i < 7; i++) registry.PushFrame([i, i * 10], At(i));

        var cpuSeries = registry.Series(cpu.SlotOf(MetricKind.CpuTotal))!;
        var gpuSeries = registry.Series(gpu.SlotOf(MetricKind.GpuUtil))!;

        Assert.Equal(7, cpuSeries.Count);
        Assert.Equal(3, gpuSeries.Count);

        // 늦게 온 쪽의 첫 샘플은 프레임 0 이 아니라 프레임 4 다.
        Assert.Equal(4, registry.FrameAt(gpuSeries, 0));
        Assert.Equal(At(4), registry.TimestampAt(gpuSeries, 0));

        // 둘의 마지막 샘플은 같은 순간이어야 한다 — 시간 축이 하나라는 전제(§1.1).
        Assert.Equal(registry.TimestampAt(cpuSeries, cpuSeries.Count - 1),
                     registry.TimestampAt(gpuSeries, gpuSeries.Count - 1));
    }

    [Fact]
    public void Frames_pushed_out_of_the_ring_report_null_not_a_stale_time()
    {
        var (registry, _) = NewRegistry(capacity: 4);
        for (int i = 0; i < 6; i++) registry.PushFrame([i], At(i));

        Assert.Equal(6, registry.Frames);
        Assert.Null(registry.TimestampAtFrame(0));   // 밀려났다
        Assert.Null(registry.TimestampAtFrame(1));
        Assert.Equal(At(2), registry.TimestampAtFrame(2));
        Assert.Equal(At(5), registry.TimestampAtFrame(5));
    }

    [Fact]
    public void CopyTimestamps_returns_the_latest_frames_oldest_first()
    {
        var (registry, _) = NewRegistry(capacity: 8);
        for (int i = 0; i < 10; i++) registry.PushFrame([i], At(i));

        Span<long> buffer = stackalloc long[5];
        int copied = registry.CopyTimestamps(buffer);

        Assert.Equal(5, copied);
        for (int i = 0; i < copied; i++) Assert.Equal(At(5 + i), buffer[i]);
    }

    [Fact]
    public void CopyTimestamps_lines_up_with_CopyLatest_index_for_index()
    {
        var (registry, device) = NewRegistry(capacity: 8);
        for (int i = 0; i < 6; i++) registry.PushFrame([i * 1.5f], At(i));

        var series = registry.Series(device.SlotOf(MetricKind.CpuTotal))!;
        Span<float> values = stackalloc float[6];
        Span<long> stamps = stackalloc long[6];

        int n = series.CopyLatest(values);
        Assert.Equal(n, registry.CopyTimestamps(stamps));

        for (int i = 0; i < n; i++)
        {
            Assert.Equal(i * 1.5f, values[i]);
            Assert.Equal(At(i), stamps[i]);
        }
    }

    [Fact]
    public void CopyTimestamps_on_an_empty_registry_copies_nothing()
    {
        var (registry, _) = NewRegistry();
        Span<long> buffer = stackalloc long[4];
        Assert.Equal(0, registry.CopyTimestamps(buffer));
    }

    /// <summary>
    /// 이 축을 놓는 이유. 백오프로 주기가 250ms → 1000ms 로 늘어도 점 개수는 같으므로
    /// 그림만 보면 알 수 없다. 시각을 보면 그 구간이 네 배 길었다는 것이 드러난다.
    /// </summary>
    [Fact]
    public void Uneven_sampling_is_visible_in_the_time_axis_but_not_in_the_point_count()
    {
        var (registry, device) = NewRegistry(capacity: 16);

        double t = 0;
        for (int i = 0; i < 4; i++) { registry.PushFrame([i], At(t)); t += 0.25; }   // Full
        for (int i = 0; i < 4; i++) { registry.PushFrame([i], At(t)); t += 1.0; }    // Background

        var series = registry.Series(device.SlotOf(MetricKind.CpuTotal))!;
        Assert.Equal(8, series.Count);   // 점 개수는 균일하다 — 그림으로는 못 가린다

        Span<long> stamps = stackalloc long[8];
        registry.CopyTimestamps(stamps);

        Assert.Equal(TimeSpan.FromSeconds(0.25).Ticks, stamps[1] - stamps[0]);
        Assert.Equal(TimeSpan.FromSeconds(1.0).Ticks, stamps[6] - stamps[5]);

        // 절전 복귀 gap 표시(§15.1)는 이 간격 비교 하나로 판정한다.
        // 느려진 구간의 첫 샘플은 아직 이전 간격(0.25s)으로 찍히므로 넓은 간격은 3개다.
        long expected = TimeSpan.FromSeconds(0.25).Ticks;
        int gaps = 0;
        for (int i = 1; i < 8; i++) if (stamps[i] - stamps[i - 1] > expected * 2) gaps++;
        Assert.Equal(3, gaps);
    }

    [Fact]
    public void Timestamps_survive_a_stats_reset()
    {
        var (registry, device) = NewRegistry();
        for (int i = 0; i < 4; i++) registry.PushFrame([i], At(i));
        registry.ResetAllStats(StatsScope.Ui, At(4));

        var series = registry.Series(device.SlotOf(MetricKind.CpuTotal))!;
        Assert.Equal(4, registry.Frames);
        Assert.Equal(At(0), registry.TimestampAt(series, 0));
        Assert.Equal(At(3), registry.LatestTimestamp);
    }
}

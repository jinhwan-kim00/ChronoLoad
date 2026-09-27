using System.Text.Json;
using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Metrics;
using ChronoLoad.Core.Sampling;
using ChronoLoad.Mcp;

namespace ChronoLoad.Core.Tests;

/// <summary>클럭 제한 사유 — 벤더 비트를 중립 사유로 옮기고, 병목만 스로틀로 센다 (§5.4).</summary>
public class GpuLimitReasonsTests
{
    [Theory]
    [InlineData(0x001UL, GpuLimitReasons.Idle)]
    [InlineData(0x002UL, GpuLimitReasons.ApplicationClocks)]
    [InlineData(0x004UL, GpuLimitReasons.Power)]
    [InlineData(0x008UL, GpuLimitReasons.HardwareSlowdown)]
    [InlineData(0x010UL, GpuLimitReasons.SyncBoost)]
    [InlineData(0x020UL, GpuLimitReasons.Thermal)]
    [InlineData(0x040UL, GpuLimitReasons.Thermal | GpuLimitReasons.HardwareSlowdown)]
    [InlineData(0x080UL, GpuLimitReasons.PowerBrake | GpuLimitReasons.HardwareSlowdown)]
    [InlineData(0x100UL, GpuLimitReasons.DisplayClock)]
    [InlineData(0x200UL, GpuLimitReasons.BoardLimit)]
    [InlineData(0x400UL, GpuLimitReasons.Reliability)]
    [InlineData(0x1000UL, GpuLimitReasons.Unknown)]
    public void Nvml_bits_follow_the_header(ulong bits, GpuLimitReasons expected) =>
        Assert.Equal(expected, GpuLimitReasonsExtensions.FromNvml(bits));

    /// <summary>
    /// RTX 5080 실측: 유휴 내내 0x400(Reliability). 이것을 병목으로 세면 유휴 GPU 가 "항상 스로틀 중"이 된다.
    /// </summary>
    [Fact]
    public void Idle_reliability_is_not_throttling()
    {
        var idle = GpuLimitReasonsExtensions.FromNvml(0x400);

        Assert.Equal((false, false, false), idle.Split());
        Assert.Equal(GpuLimitReasons.None, idle & GpuLimitReasonsExtensions.Bottlenecks);
    }

    /// <summary>RTX 5080 실측: CUDA 부하 중 0x4 — 360 W 한도에 붙어 있었다.</summary>
    [Fact]
    public void Software_power_cap_counts_as_power_throttling()
    {
        Assert.Equal((true, false, false), GpuLimitReasonsExtensions.FromNvml(0x4).Split());
    }

    [Fact]
    public void Hardware_slowdown_is_not_counted_twice_when_its_cause_is_known()
    {
        // HwThermalSlowdown 은 HwSlowdown 과 함께 선다. 온도에 세었으면 "그 밖"에서 빼야 한다.
        Assert.Equal((false, true, false), GpuLimitReasonsExtensions.FromNvml(0x40 | 0x8).Split());

        // 원인 없이 HwSlowdown 만 서면 그것은 "그 밖"이다.
        Assert.Equal((false, false, true), GpuLimitReasonsExtensions.FromNvml(0x8).Split());
    }

    [Fact]
    public void Intel_voltage_and_low_utilization_are_not_throttling()
    {
        var r = GpuLimitReasons.Voltage | GpuLimitReasons.LowUtilization;

        Assert.Equal((false, false, false), r.Split());
    }

    [Fact]
    public void Names_are_camel_case_and_complete()
    {
        var names = (GpuLimitReasons.Power | GpuLimitReasons.HardwareSlowdown | GpuLimitReasons.Idle).Names();

        Assert.Equal(["power", "hardwareSlowdown", "idle"], names);
    }

    [Fact]
    public void Gpu_status_reports_the_limit_and_whether_it_is_throttling()
    {
        var registry = new MetricRegistry(seriesCapacity: 64);
        var gpu = registry.Register(
            new DeviceInfo("gpu:luid_1", DeviceClass.Gpu, "RTX", "RTX", IconKind.GpuNvidia, "NVIDIA"),
            [MetricKind.GpuUtil, MetricKind.GpuPower, MetricKind.GpuPowerLimit, MetricKind.GpuThrottlePower]);
        registry.CommitAll([97f, 360f, 360f, 100f]);

        var ctx = new McpContext(registry, new SampleEngine(registry))
        {
            LimitReasons = key => key == gpu.Key ? GpuLimitReasons.Power | GpuLimitReasons.Reliability : null,
        };

        var adapter = JsonSerializer.SerializeToElement(new ChronoLoadTools(ctx).GetGpuStatus())
            .GetProperty("adapters")[0];

        Assert.Equal(360, adapter.GetProperty("powerLimitWatts").GetDouble());
        Assert.Equal(100, adapter.GetProperty("powerLimitPercent").GetDouble());
        Assert.True(adapter.GetProperty("throttling").GetBoolean());
        Assert.Equal(["power", "reliability"],
            adapter.GetProperty("limitReasons").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public void Unknown_reasons_are_null_not_false()
    {
        var registry = new MetricRegistry(seriesCapacity: 64);
        registry.Register(
            new DeviceInfo("gpu:luid_1", DeviceClass.Gpu, "PDH", "PDH", IconKind.GpuGeneric), [MetricKind.GpuUtil]);

        var adapter = JsonSerializer.SerializeToElement(
                new ChronoLoadTools(new McpContext(registry, new SampleEngine(registry))).GetGpuStatus())
            .GetProperty("adapters")[0];

        // 벤더 경로가 사유를 주지 않는다. false 로 적으면 "제한 없음"이라는 거짓말이 된다.
        Assert.Equal(JsonValueKind.Null, adapter.GetProperty("throttling").ValueKind);
        Assert.Equal(JsonValueKind.Null, adapter.GetProperty("powerLimitWatts").ValueKind);
    }
}

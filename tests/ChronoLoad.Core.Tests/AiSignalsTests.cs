using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Metrics;
using ChronoLoad.Mcp;

namespace ChronoLoad.Core.Tests;

/// <summary>
/// 같은 AI 작업이 GPU 마다 다른 엔진 이름으로 잡힌다(§5.4). 이름 묶기와 어댑터별 안내를 본다.
/// </summary>
public class AiSignalsTests
{
    [Theory]
    // NVIDIA, HAGS 꺼짐일 때의 CUDA 노드
    [InlineData("Compute_0", MetricKind.GpuCompute)]
    [InlineData("Compute_1", MetricKind.GpuCompute)]
    [InlineData("Cuda", MetricKind.GpuCompute)]
    // Intel — 외장 Arc 의 CCS, 내장 Arc 의 Neural
    [InlineData("compute", MetricKind.GpuCompute)]
    [InlineData("Neural", MetricKind.GpuCompute)]
    // AMD
    [InlineData("High Priority Compute", MetricKind.GpuCompute)]
    [InlineData("High Priority 3D", MetricKind.Gpu3D)]
    // 공통
    [InlineData("3d", MetricKind.Gpu3D)]
    [InlineData("Graphics_1", MetricKind.Gpu3D)]
    [InlineData("copy", MetricKind.GpuCopy)]
    [InlineData("videodecode", MetricKind.GpuVideo)]
    [InlineData("VideoEncode", MetricKind.GpuVideo)]
    [InlineData("videoprocessing", MetricKind.GpuVideo)]
    [InlineData("jpeg_decode_1", MetricKind.GpuVideo)]
    public void Engine_types_map_to_their_family(string engineType, MetricKind family) =>
        Assert.Equal(family, GpuEngineFamilies.Classify(engineType));

    [Theory]
    [InlineData("security")]
    [InlineData("security_1")]
    [InlineData("ofa_0")]
    [InlineData("vr")]
    [InlineData("gsc")]
    [InlineData("")]
    public void Engines_outside_the_families_are_left_out(string engineType) =>
        Assert.Null(GpuEngineFamilies.Classify(engineType));

    private static readonly MetricKind[] GpuKinds =
    [
        MetricKind.GpuUtil, MetricKind.GpuCompute, MetricKind.GpuDedicated, MetricKind.GpuShared,
        MetricKind.GpuTemp, MetricKind.GpuPower, MetricKind.GpuClock,
        MetricKind.Gpu3D, MetricKind.GpuCopy, MetricKind.GpuVideo, MetricKind.GpuMemBusy,
    ];

    private static DeviceHandle Adapter(string vendor, IconKind icon, bool discrete, string hags)
    {
        var registry = new MetricRegistry(seriesCapacity: 16);
        return registry.Register(
            new DeviceInfo("gpu:luid_1", DeviceClass.Gpu, "테스트", "테스트 GPU", icon, vendor)
            {
                Extra = new Dictionary<string, string>
                {
                    ["discrete"] = discrete ? "true" : "false",
                    ["hardwareScheduling"] = hags,
                },
            },
            GpuKinds);
    }

    /// <summary>
    /// RTX 5080 실측: HAGS 켜짐에서 CUDA 필터가 <c>3d</c> 94.9% 로 잡히고 Compute 는 0 이었다.
    /// "GpuCompute 가 0 이니 AI 가 안 돈다"는 오판을 막아야 한다.
    /// </summary>
    [Fact]
    public void Nvidia_with_hardware_scheduling_points_at_3d_not_compute()
    {
        var signals = AiSignals.For(Adapter("NVIDIA", IconKind.GpuNvidia, discrete: true, hags: "true"));

        Assert.Equal(["GpuUtil", "Gpu3D"], signals.Primary);
        Assert.DoesNotContain("GpuCompute", signals.Primary);
        Assert.Contains("GpuMemBusy", signals.Supporting);
        Assert.Contains("3D", signals.Note);
    }

    [Fact]
    public void Nvidia_without_hardware_scheduling_points_at_the_compute_nodes()
    {
        var signals = AiSignals.For(Adapter("NVIDIA", IconKind.GpuNvidia, discrete: true, hags: "false"));

        Assert.Equal(["GpuUtil", "GpuCompute"], signals.Primary);
    }

    [Fact]
    public void Unknown_scheduling_on_nvidia_assumes_the_windows_default()
    {
        var signals = AiSignals.For(Adapter("NVIDIA", IconKind.GpuNvidia, discrete: true, hags: "unknown"));

        Assert.Contains("Gpu3D", signals.Primary);
    }

    [Fact]
    public void Integrated_intel_names_the_neural_engine_and_shared_memory()
    {
        var signals = AiSignals.For(Adapter("Intel", IconKind.GpuIntel, discrete: false, hags: "true"));

        Assert.Equal("GpuCompute", signals.Primary[0]);
        Assert.Contains("Neural", signals.Note);
        Assert.Equal("GpuShared", signals.Supporting.First(s => s.StartsWith("GpuShared") || s.StartsWith("GpuDedicated")));
    }

    [Fact]
    public void Discrete_intel_leads_with_the_compute_engine()
    {
        var signals = AiSignals.For(Adapter("Intel", IconKind.GpuIntel, discrete: true, hags: "true"));

        Assert.Equal(["GpuCompute", "Gpu3D", "GpuUtil"], signals.Primary);
        Assert.Contains("GpuCopy", signals.Supporting);
    }

    [Fact]
    public void Npu_is_its_neural_engine()
    {
        var signals = AiSignals.For(Adapter("Intel", IconKind.Npu, discrete: false, hags: "unknown"));

        Assert.Equal("GpuCompute", signals.Primary[0]);
    }

    [Fact]
    public void Only_metrics_the_adapter_actually_has_are_named()
    {
        var registry = new MetricRegistry(seriesCapacity: 16);
        var bare = registry.Register(
            new DeviceInfo("gpu:luid_2", DeviceClass.Gpu, "PDH", "PDH 만", IconKind.GpuNvidia, "NVIDIA"),
            [MetricKind.GpuUtil, MetricKind.GpuDedicated]);

        var signals = AiSignals.For(bare);

        Assert.All(signals.Primary.Concat(signals.Supporting),
            name => Assert.True(bare.SlotOf(Enum.Parse<MetricKind>(name)) >= 0, name));
    }
}

using ChronoLoad.App.Rendering;
using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Layout;
using ChronoLoad.Core.Metrics;

namespace ChronoLoad.App.ViewModels;

/// <summary>
/// 장치 → 카드 변환. 장치 종류마다 차트 모드·축 규칙·가중치·우선순위가 정해져 있다(설계서 §8.2, §8.6).
/// 센서가 M3에서 붙어도 여기만 통과하면 카드가 생긴다.
/// </summary>
public static class CardFactory
{
    /// <summary>네트워크 축 단계(바이트/초). 1·10·100Mbps … 2.5GbE 까지.</summary>
    private static readonly double[] NetSteps =
        [1e6 / 8, 5e6 / 8, 10e6 / 8, 25e6 / 8, 50e6 / 8, 100e6 / 8, 250e6 / 8, 500e6 / 8, 1e9 / 8, 2.5e9 / 8];

    /// <summary>디스크 축 단계(바이트/초). SATA 부터 PCIe 5.0 NVMe 까지.</summary>
    private static readonly double[] DiskSteps = [50e6, 100e6, 250e6, 500e6, 1e9, 2e9, 4e9, 8e9, 16e9];

    public static CardViewModel? TryCreate(DeviceHandle device, MetricRegistry registry)
    {
        return device.Info.Class switch
        {
            DeviceClass.System => CreateSystem(device, registry),
            DeviceClass.Network => CreatePair(device, registry, MetricKind.NetRx, MetricKind.NetTx,
                MetricUnit.BitRate, NetSteps, priority: device.Index == 0 ? 50 : 45),
            DeviceClass.Disk => CreatePair(device, registry, MetricKind.DiskRead, MetricKind.DiskWrite,
                MetricUnit.ByteRate, DiskSteps, priority: device.Index == 0 ? 60 : 55),
            DeviceClass.Gpu => CreateGpu(device, registry),
            _ => null,
        };
    }

    private static CardViewModel? CreateSystem(DeviceHandle device, MetricRegistry registry)
    {
        int cpuSlot = device.SlotOf(MetricKind.CpuTotal);
        if (cpuSlot >= 0)
        {
            return new CardViewModel
            {
                Device = device,
                Primary = registry.Series(cpuSlot)!,
                PrimarySlot = cpuSlot,
                Icon = Icons.For(device.Info.Icon),
                Mode = ChartMode.Area,
                Scale = ScaleMode.Fixed,
                FixedMax = 100,
                DisplayUnit = MetricUnit.Percent,
                Weight = 1,
                Priority = 90,
            };
        }

        int usedSlot = device.SlotOf(MetricKind.MemUsed);
        if (usedSlot < 0) return null;

        // 총량은 시계열이 아니라 장치 정보로 들고 있다. 축 상한이자 퍼센트 환산의 분모가 된다.
        double total = device.Info.Extra.TryGetValue("totalBytes", out var raw) && double.TryParse(raw, out var t)
            ? t : 1;

        int commitSlot = device.SlotOf(MetricKind.MemCommit);
        return new CardViewModel
        {
            Device = device,
            Primary = registry.Series(usedSlot)!,
            PrimarySlot = usedSlot,
            Secondary = commitSlot >= 0 ? registry.Series(commitSlot) : null,
            SecondarySlot = commitSlot,
            Icon = Icons.For(device.Info.Icon),
            Mode = ChartMode.Area,
            Scale = ScaleMode.Fixed,
            FixedMax = total,
            DisplayUnit = MetricUnit.Percent,
            DisplayFactor = 100.0 / total,
            ScaleHintUnit = MetricUnit.Bytes,   // 축 힌트는 "100%" 가 아니라 "32G" 가 유용하다
            Weight = 1,
            Priority = 70,
        };
    }

    private static CardViewModel? CreatePair(DeviceHandle device, MetricRegistry registry,
        MetricKind up, MetricKind down, MetricUnit unit, double[] steps, int priority)
    {
        int upSlot = device.SlotOf(up);
        if (upSlot < 0) return null;
        int downSlot = device.SlotOf(down);

        return new CardViewModel
        {
            Device = device,
            Primary = registry.Series(upSlot)!,
            PrimarySlot = upSlot,
            Secondary = downSlot >= 0 ? registry.Series(downSlot) : null,
            SecondarySlot = downSlot,
            Icon = Icons.For(device.Info.Icon),
            Mode = ChartMode.Mirror,
            Scale = ScaleMode.StepSnap,
            Steps = steps,
            DisplayUnit = unit,
            SecondaryIsOpposite = true,
            Badge = device.Index.ToString(),
            Weight = 1,
            Priority = priority,
        };
    }

    private static CardViewModel? CreateGpu(DeviceHandle device, MetricRegistry registry)
    {
        int utilSlot = device.SlotOf(MetricKind.GpuUtil);
        if (utilSlot < 0) return null;

        bool discrete = device.Info.Extra.GetValueOrDefault("discrete") == "true";
        bool isNpu = device.Info.Extra.GetValueOrDefault("computeOnly") == "true";
        double capacity = double.TryParse(device.Info.Extra.GetValueOrDefault("dedicatedBytes"), out var bytes)
            ? bytes : 0;
        double sharedCapacity = double.TryParse(device.Info.Extra.GetValueOrDefault("sharedBytes"), out var shared)
            ? shared : 0;

        int computeSlot = device.SlotOf(MetricKind.GpuCompute);
        int dedicatedSlot = device.SlotOf(MetricKind.GpuDedicated);
        int sharedSlot = device.SlotOf(MetricKind.GpuShared);
        int tempSlot = device.SlotOf(MetricKind.GpuTemp);
        int powerSlot = device.SlotOf(MetricKind.GpuPower);
        int clockSlot = device.SlotOf(MetricKind.GpuClock);

        // NPU 는 전용 VRAM 도 용량선도 없다. 누적 메모리를 얹으면 빈 영역만 늘어난다.
        bool combo = !isNpu && dedicatedSlot >= 0;

        return new CardViewModel
        {
            Device = device,
            Primary = registry.Series(utilSlot)!,
            PrimarySlot = utilSlot,
            Secondary = combo && computeSlot >= 0 ? registry.Series(computeSlot) : null,
            SecondarySlot = combo ? computeSlot : -1,
            MemoryDedicated = combo ? registry.Series(dedicatedSlot) : null,
            MemoryShared = combo && sharedSlot >= 0 ? registry.Series(sharedSlot) : null,
            Temperature = tempSlot >= 0 ? registry.Series(tempSlot) : null,
            Power = powerSlot >= 0 ? registry.Series(powerSlot) : null,
            Clock = clockSlot >= 0 ? registry.Series(clockSlot) : null,
            DedicatedCapacity = capacity,
            SharedCapacity = sharedCapacity,
            IsDiscrete = discrete,
            IsNpu = isNpu,
            Icon = Icons.For(device.Info.Icon),
            Mode = combo ? ChartMode.GpuCombo : ChartMode.Area,
            Scale = ScaleMode.Fixed,
            FixedMax = 100,
            DisplayUnit = MetricUnit.Percent,
            Badge = device.Index.ToString(),
            BadgeOutlined = !discrete,      // 내장 GPU·NPU 는 외곽선 배지
            Weight = isNpu ? 1.0 : 1.5,     // GPU 가 주 용도이므로 같은 조건에서 1.5배 높다
            Priority = isNpu ? 75 : device.Index == 0 ? 100 : 80,
        };
    }
}

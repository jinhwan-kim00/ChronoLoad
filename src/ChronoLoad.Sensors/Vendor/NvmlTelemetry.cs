using System.Runtime.InteropServices;

namespace ChronoLoad.Sensors.Vendor;

[StructLayout(LayoutKind.Sequential)]
internal struct NvmlUtilization
{
    public uint Gpu;
    public uint Memory;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NvmlMemory
{
    public ulong Total;
    public ulong Free;
    public ulong Used;
}

internal static partial class NvmlNative
{
    public const uint Success = 0;
    public const uint TemperatureGpu = 0;
    public const uint ClockGraphics = 0;

    [LibraryImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
    public static partial uint Init();

    [LibraryImport("nvml.dll", EntryPoint = "nvmlShutdown")]
    public static partial uint Shutdown();

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")]
    public static partial uint GetCount(out uint count);

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    public static partial uint GetHandleByIndex(uint index, out nint device);

    /// <summary>
    /// <c>nvmlPciInfo_t</c> 는 고정 길이 char 배열을 품고 있어 소스 생성 마샬러가 다루지 못한다.
    /// 필요한 값은 domain·bus·device 세 개뿐이므로 버퍼로 받아 오프셋으로 읽는다.
    /// 배치: busIdLegacy[16] · domain(16) · bus(20) · device(24) · pciDeviceId(28) …
    /// </summary>
    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetPciInfo_v3")]
    public static partial uint GetPciInfo(nint device, nint pciBuffer);

    public const int PciInfoBufferSize = 128;
    public const int PciBusOffset = 20;
    public const int PciDeviceOffset = 24;

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")]
    public static partial uint GetUtilizationRates(nint device, out NvmlUtilization utilization);

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")]
    public static partial uint GetMemoryInfo(nint device, out NvmlMemory memory);

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")]
    public static partial uint GetTemperature(nint device, uint sensor, out uint celsius);

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage")]
    public static partial uint GetPowerUsage(nint device, out uint milliwatts);

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetClockInfo")]
    public static partial uint GetClockInfo(nint device, uint type, out uint megahertz);

    /// <summary>모든 제한을 반영해 드라이버가 실제로 강제하는 전력 한도(mW).</summary>
    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")]
    public static partial uint GetEnforcedPowerLimit(nint device, out uint milliwatts);

    /// <summary>클럭 제한 사유 비트마스크. R535 부터의 이름이다.</summary>
    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetCurrentClocksEventReasons")]
    public static partial uint GetCurrentClocksEventReasons(nint device, out ulong reasons);

    /// <summary>같은 값의 옛 이름. 새 이름이 없는 드라이버에서만 쓴다.</summary>
    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetCurrentClocksThrottleReasons")]
    public static partial uint GetCurrentClocksThrottleReasons(nint device, out ulong reasons);
}

/// <summary>
/// NVIDIA NVML. 드라이버와 함께 설치되므로 <c>System32\nvml.dll</c>에서 지연 로드된다.
/// </summary>
/// <remarks>
/// NVML의 값은 PDH와 성격이 다르다. 사용률은 누적 카운터가 아니라 드라이버가 계산한 순간값이라
/// 매 틱 읽어도 되고, 온도·전력·클럭은 PDH에 아예 없는 항목이다.
/// </remarks>
public sealed class NvmlTelemetry : IVendorTelemetry
{
    private readonly List<nint> _devices = [];
    private readonly Dictionary<PciAddress, int> _byAddress = [];
    private bool _initialized;

    public string Name => "NVML";
    public bool IsAvailable { get; private set; }

    public static NvmlTelemetry? TryCreate()
    {
        var telemetry = new NvmlTelemetry();
        return telemetry.Initialize() ? telemetry : null;
    }

    private bool Initialize()
    {
        try
        {
            if (NvmlNative.Init() != NvmlNative.Success) return false;
            _initialized = true;

            if (NvmlNative.GetCount(out uint count) != NvmlNative.Success) return false;

            for (uint i = 0; i < count; i++)
            {
                if (NvmlNative.GetHandleByIndex(i, out nint device) != NvmlNative.Success) continue;

                nint buffer = Marshal.AllocHGlobal(NvmlNative.PciInfoBufferSize);
                try
                {
                    for (int b = 0; b < NvmlNative.PciInfoBufferSize; b++) Marshal.WriteByte(buffer, b, 0);
                    if (NvmlNative.GetPciInfo(device, buffer) != NvmlNative.Success) continue;

                    uint bus = (uint)Marshal.ReadInt32(buffer, NvmlNative.PciBusOffset);
                    uint slot = (uint)Marshal.ReadInt32(buffer, NvmlNative.PciDeviceOffset);

                    // NVML 은 PCI function 을 따로 주지 않는다. 그래픽 어댑터는 사실상 항상 function 0 이다.
                    _byAddress[new PciAddress(bus, slot, 0)] = _devices.Count;
                    _devices.Add(device);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            IsAvailable = _devices.Count > 0;
            if (IsAvailable) SensorLog.Write($"NVML 초기화 — 장치 {_devices.Count}개");
            return IsAvailable;
        }
        catch (DllNotFoundException)
        {
            return false;       // NVIDIA 드라이버가 없는 시스템. 정상 경로다.
        }
        catch (EntryPointNotFoundException ex)
        {
            SensorLog.Write($"NVML 진입점 없음: {ex.Message}");
            return false;
        }
    }

    public bool TryBind(PciAddress address, out int handle) =>
        _byAddress.TryGetValue(address with { Function = 0 }, out handle);

    public bool TryRead(int handle, bool full, out VendorSample sample)
    {
        sample = VendorSample.Empty;
        if (!IsAvailable || (uint)handle >= (uint)_devices.Count) return false;

        nint device = _devices[handle];
        bool any = false;

        if (NvmlNative.GetUtilizationRates(device, out var utilization) == NvmlNative.Success)
        {
            sample.UtilPercent = utilization.Gpu;
            // 같은 호출에 딸려 온다. SM 은 쉬는데 이것이 높으면 연산이 아니라 대역폭이 병목이다.
            sample.MemBusyPercent = utilization.Memory;
            any = true;
        }

        // 사용률만 필요한 틱이면 여기서 끝낸다. 아래 네 번의 호출이 이 경로 비용의 대부분이다.
        if (!full) return any;

        if (NvmlNative.GetMemoryInfo(device, out var memory) == NvmlNative.Success)
        {
            sample.MemoryUsedBytes = memory.Used;
            sample.MemoryTotalBytes = memory.Total;
            any = true;
        }

        if (NvmlNative.GetTemperature(device, NvmlNative.TemperatureGpu, out uint celsius) == NvmlNative.Success)
        {
            sample.TemperatureCelsius = celsius;
            any = true;
        }

        if (NvmlNative.GetPowerUsage(device, out uint milliwatts) == NvmlNative.Success)
        {
            sample.PowerWatts = milliwatts / 1000f;
            any = true;
        }

        if (NvmlNative.GetClockInfo(device, NvmlNative.ClockGraphics, out uint megahertz) == NvmlNative.Success)
        {
            sample.CoreClockMegahertz = megahertz;
            any = true;
        }

        if (NvmlNative.GetEnforcedPowerLimit(device, out uint limitMilliwatts) == NvmlNative.Success && limitMilliwatts > 0)
        {
            sample.PowerLimitWatts = limitMilliwatts / 1000f;
            any = true;
        }

        if (TryReadReasons(device, out ulong reasons))
        {
            sample.LimitReasons = ChronoLoad.Core.Metrics.GpuLimitReasonsExtensions.FromNvml(reasons);
            any = true;
        }

        return any;
    }

    // 새 진입점이 없는 드라이버에서 매번 예외를 치르지 않도록 한 번 가른 결과를 기억한다.
    private bool _legacyReasons;

    /// <summary>
    /// 클럭 제한 사유. <c>ClocksEventReasons</c> 가 없으면(R535 이전 드라이버) 옛 이름
    /// <c>ClocksThrottleReasons</c> 로 내려간다 — 같은 비트, 같은 뜻이다.
    /// </summary>
    private bool TryReadReasons(nint device, out ulong reasons)
    {
        reasons = 0;
        if (!_legacyReasons)
        {
            try
            {
                return NvmlNative.GetCurrentClocksEventReasons(device, out reasons) == NvmlNative.Success;
            }
            catch (EntryPointNotFoundException)
            {
                _legacyReasons = true;
            }
        }

        try
        {
            return NvmlNative.GetCurrentClocksThrottleReasons(device, out reasons) == NvmlNative.Success;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_initialized)
        {
            try { NvmlNative.Shutdown(); }
            catch (DllNotFoundException) { }
            _initialized = false;
        }

        _devices.Clear();
        _byAddress.Clear();
        IsAvailable = false;
    }
}

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

    /// <summary>
    /// PCIe 처리량(KB/s). <paramref name="counter"/> 0 = TX(GPU→호스트), 1 = RX(호스트→GPU).
    /// 드라이버가 호출 시점에 짧은 구간을 재서 준다.
    /// </summary>
    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetPcieThroughput")]
    public static partial uint GetPcieThroughput(nint device, uint counter, out uint kilobytesPerSecond);

    public const uint PcieTx = 0;
    public const uint PcieRx = 1;

    /// <summary>
    /// 필드 값 여러 개를 한 번에. <c>nvmlFieldValue_t</c> 는 40바이트:
    /// fieldId(u32)@0 · scopeId(u32)@4 · timestamp(i64, μs)@8 · latencyUsec(i64)@16 ·
    /// valueType(i32)@24 · nvmlReturn(u32)@28 · value(union 8)@32.
    /// </summary>
    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetFieldValues")]
    public static partial uint GetFieldValues(nint device, int count, nint values);

    public const int FieldValueSize = 40;
    public const uint FieldPcieTxBytes = 197;   // NVML_FI_DEV_PCIE_COUNT_TX_BYTES — 누적, 되감길 수 있다
    public const uint FieldPcieRxBytes = 198;   // NVML_FI_DEV_PCIE_COUNT_RX_BYTES

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

    /// <summary>진단용. 누적 PCIe 바이트 필드(RX·TX)의 반환 코드·값·시각과 걸린 시간.</summary>
    public (uint RxRc, ulong Rx, uint TxRc, ulong Tx, long StampUs, double Milliseconds) ProbePcieCounters(int handle)
    {
        if (!IsAvailable || (uint)handle >= (uint)_devices.Count) return default;

        nint buffer = Marshal.AllocHGlobal(NvmlNative.FieldValueSize * 2);
        try
        {
            for (int b = 0; b < NvmlNative.FieldValueSize * 2; b++) Marshal.WriteByte(buffer, b, 0);
            Marshal.WriteInt32(buffer, 0, (int)NvmlNative.FieldPcieRxBytes);
            Marshal.WriteInt32(buffer, NvmlNative.FieldValueSize, (int)NvmlNative.FieldPcieTxBytes);

            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            uint rc = NvmlNative.GetFieldValues(_devices[handle], 2, buffer);
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (rc != NvmlNative.Success) return (rc, 0, rc, 0, 0, ms);

            int tx = NvmlNative.FieldValueSize;
            return ((uint)Marshal.ReadInt32(buffer, 28), (ulong)Marshal.ReadInt64(buffer, 32),
                    (uint)Marshal.ReadInt32(buffer, tx + 28), (ulong)Marshal.ReadInt64(buffer, tx + 32),
                    Marshal.ReadInt64(buffer, 8), ms);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>진단용. PCIe 처리량 두 번(RX·TX)과 걸린 시간.</summary>
    public (long Rx, long Tx, double Milliseconds) ProbePcie(int handle)
    {
        if (!IsAvailable || (uint)handle >= (uint)_devices.Count) return (-1, -1, 0);

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        long rx = NvmlNative.GetPcieThroughput(_devices[handle], NvmlNative.PcieRx, out uint r) == NvmlNative.Success ? r : -1;
        long tx = NvmlNative.GetPcieThroughput(_devices[handle], NvmlNative.PcieTx, out uint t) == NvmlNative.Success ? t : -1;
        return (rx, tx, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

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

        // PCIe 는 전용 타이머가 읽어 둔다. 새 값이 들어왔을 때만 가져간다 — 같은 값을 매 틱 다시 적으면
        // 1초 해상도를 250ms 로 꾸미는 셈이다.
        if (TakePcie(handle, out float rx, out float tx))
        {
            sample.PcieRxBytesPerSecond = rx;
            sample.PcieTxBytesPerSecond = tx;
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

    // PCIe 누적 카운터의 직전 값과, 타이머가 구해 둔 최신 처리량. 장치별. _pcieGate 로 지킨다.
    private readonly Dictionary<int, (ulong Rx, ulong Tx, long StampUs)> _pcie = [];
    private readonly Dictionary<int, (float Rx, float Tx)> _pcieRates = [];
    private readonly Lock _pcieGate = new();
    private nint _fieldBuffer;
    private bool _pcieUnsupported;
    private Timer? _pcieTimer;

    /// <summary>PCIe 누적 카운터를 읽는 주기.</summary>
    private static readonly TimeSpan PciePeriod = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 샘플링 스레드 밖에서 1초마다 PCIe 카운터를 읽는다.
    /// </summary>
    /// <remarks>
    /// 누적 필드 호출은 따로 부르면 0.02~0.7ms 인데 <b>샘플링 틱 안에서는 평균 3.8ms</b> 가 걸렸다 —
    /// 드라이버가 값을 새로 갱신하느라 기다리는 시간으로 보인다. 매 틱 읽었더니 듀티 사이클이
    /// 0.92% → 2.30% 로 올랐다(같은 조건 A/B). 샘플러를 붙잡지 않도록 타이머로 뺀다.
    /// NVML 은 스레드 안전하지만 <see cref="Dispose"/> 가 <c>nvmlShutdown</c> 하는 동안 읽으면 안 되므로
    /// 같은 잠금 안에서만 부른다.
    /// </remarks>
    private void StartPcieTimer()
    {
        if (_pcieTimer is not null || _pcieUnsupported) return;
        _pcieTimer = new Timer(_ => PollPcie(), null, TimeSpan.Zero, PciePeriod);
    }

    private void PollPcie()
    {
        lock (_pcieGate)
        {
            if (!_initialized || _pcieUnsupported) return;
            for (int handle = 0; handle < _devices.Count; handle++)
                if (TryReadPcie(handle, out float rx, out float tx)) _pcieRates[handle] = (rx, tx);
        }
    }

    /// <summary>타이머가 구해 둔 새 처리량을 가져간다. 한 번 가져간 값은 다시 주지 않는다.</summary>
    private bool TakePcie(int handle, out float rx, out float tx)
    {
        StartPcieTimer();
        lock (_pcieGate)
        {
            if (_pcieRates.Remove(handle, out var rates))
            {
                (rx, tx) = rates;
                return true;
            }
        }

        rx = tx = float.NaN;
        return false;
    }

    /// <summary>
    /// PCIe 처리량. 누적 바이트 필드(<c>NVML_FI_DEV_PCIE_COUNT_RX/TX_BYTES</c>)의 차분이다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>nvmlDeviceGetPcieThroughput</c> 을 쓰지 않는다.</b> 드라이버가 호출 안에서 짧은 구간을 재고
    /// 돌아오는 블로킹 호출이라 RX·TX 두 번에 55~75ms 가 걸렸고(RTX 5080 실측), 값은 그 순간의 창이라
    /// TX 가 1초 사이에 2 MB/s 와 341 MB/s 를 오갔다. 누적 필드는 차분이라 두 읽기 사이 전 구간의 평균이다.
    /// </para>
    /// <para>
    /// 카운터는 되감길 수 있다(헤더 주석). 줄어들면 그 표본은 버리고 기준선만 새로 잡는다.
    /// 드라이버가 필드를 지원하지 않으면 한 번 확인하고 다시 묻지 않는다.
    /// </para>
    /// </remarks>
    private bool TryReadPcie(int handle, out float rxPerSecond, out float txPerSecond)
    {
        rxPerSecond = txPerSecond = float.NaN;
        if (_pcieUnsupported) return false;

        const int size = NvmlNative.FieldValueSize;
        if (_fieldBuffer == 0) _fieldBuffer = Marshal.AllocHGlobal(size * 2);

        for (int b = 0; b < size * 2; b++) Marshal.WriteByte(_fieldBuffer, b, 0);
        Marshal.WriteInt32(_fieldBuffer, 0, (int)NvmlNative.FieldPcieRxBytes);
        Marshal.WriteInt32(_fieldBuffer, size, (int)NvmlNative.FieldPcieTxBytes);

        try
        {
            if (NvmlNative.GetFieldValues(_devices[handle], 2, _fieldBuffer) != NvmlNative.Success) return false;
        }
        catch (EntryPointNotFoundException)
        {
            _pcieUnsupported = true;
            return false;
        }

        if (Marshal.ReadInt32(_fieldBuffer, 28) != 0 || Marshal.ReadInt32(_fieldBuffer, size + 28) != 0)
        {
            _pcieUnsupported = true;       // 이 드라이버·GPU 는 필드를 주지 않는다
            SensorLog.Write("NVML: PCIe 누적 바이트 필드를 지원하지 않는다 — PCIe 처리량 없음");
            return false;
        }

        ulong rx = (ulong)Marshal.ReadInt64(_fieldBuffer, 32);
        ulong tx = (ulong)Marshal.ReadInt64(_fieldBuffer, size + 32);
        long stamp = Marshal.ReadInt64(_fieldBuffer, 8);

        bool had = _pcie.TryGetValue(handle, out var previous);
        _pcie[handle] = (rx, tx, stamp);
        if (!had || stamp <= previous.StampUs || rx < previous.Rx || tx < previous.Tx) return false;

        double seconds = (stamp - previous.StampUs) / 1e6;
        rxPerSecond = (float)((rx - previous.Rx) / seconds);
        txPerSecond = (float)((tx - previous.Tx) / seconds);
        return true;
    }

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
        // 타이머 콜백이 끝나기를 기다린 뒤에 닫는다. 읽는 도중에 nvmlShutdown 하면 해제된 핸들을 만난다.
        if (_pcieTimer is not null)
        {
            using var stopped = new ManualResetEvent(false);
            if (_pcieTimer.Dispose(stopped)) stopped.WaitOne(TimeSpan.FromSeconds(2));
            _pcieTimer = null;
        }

        lock (_pcieGate)
        {
            if (_fieldBuffer != 0) { Marshal.FreeHGlobal(_fieldBuffer); _fieldBuffer = 0; }
            _pcieUnsupported = true;
        }

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

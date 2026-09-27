using System.Runtime.InteropServices;

namespace ChronoLoad.Sensors.Vendor;

internal static partial class LevelZeroNative
{
    public const uint Success = 0;

    // 모든 Level Zero 구조체는 공통 헤더로 시작한다: stype(4) + 패딩(4) + pNext(8).
    // 그래서 실제 값은 오프셋 16부터다.
    public const int HeaderSize = 16;

    // zes_structure_type_t — 공식 헤더에서 확인한 값
    public const uint StypePciProperties = 0x2;
    public const uint StypeFreqState = 0x1b;
    public const uint StypePowerEnergyCounter = 0x15;

    [LibraryImport("ze_loader.dll", EntryPoint = "zesInit")]
    public static partial uint Init(uint flags);

    [LibraryImport("ze_loader.dll", EntryPoint = "zesDriverGet")]
    public static partial uint DriverGet(ref uint count, nint drivers);

    [LibraryImport("ze_loader.dll", EntryPoint = "zesDeviceGet")]
    public static partial uint DeviceGet(nint driver, ref uint count, nint devices);

    /// <summary>
    /// PCIe 누적 통계. <c>zes_pci_stats_t</c> 는 <c>stype</c> 헤더가 없다 — timestamp(μs) · replay · packet ·
    /// rx(B) · tx(B) 가 각 uint64 로 0 부터 놓이고 speed 가 40 에 온다(56바이트).
    /// </summary>
    [LibraryImport("ze_loader.dll", EntryPoint = "zesDevicePciGetStats")]
    public static partial uint PciGetStats(nint device, nint stats);

    [LibraryImport("ze_loader.dll", EntryPoint = "zesDevicePciGetProperties")]
    public static partial uint DevicePciGetProperties(nint device, nint properties);

    [LibraryImport("ze_loader.dll", EntryPoint = "zesDeviceEnumTemperatureSensors")]
    public static partial uint EnumTemperatureSensors(nint device, ref uint count, nint handles);

    /// <summary>온도만은 구조체가 아니라 <c>double*</c>를 받는다. 레이아웃 위험이 없다.</summary>
    [LibraryImport("ze_loader.dll", EntryPoint = "zesTemperatureGetState")]
    public static partial uint TemperatureGetState(nint sensor, out double celsius);

    [LibraryImport("ze_loader.dll", EntryPoint = "zesDeviceEnumPowerDomains")]
    public static partial uint EnumPowerDomains(nint device, ref uint count, nint handles);

    [LibraryImport("ze_loader.dll", EntryPoint = "zesPowerGetEnergyCounter")]
    public static partial uint PowerGetEnergyCounter(nint power, nint counter);

    [LibraryImport("ze_loader.dll", EntryPoint = "zesDeviceEnumFrequencyDomains")]
    public static partial uint EnumFrequencyDomains(nint device, ref uint count, nint handles);

    [LibraryImport("ze_loader.dll", EntryPoint = "zesFrequencyGetState")]
    public static partial uint FrequencyGetState(nint frequency, nint state);
}

/// <summary>
/// Intel Level Zero Sysman. Arc 외장 GPU와 내장 GPU의 온도·전력·클럭을 읽는다.
/// </summary>
/// <remarks>
/// <para>
/// <b>구조체를 선언하지 않고 버퍼 + 오프셋으로 읽는다.</b> Level Zero 구조체는 모두
/// <c>stype</c>(4) + 패딩(4) + <c>pNext</c>(8) 헤더로 시작하고 값이 16부터 놓인다.
/// 뒤쪽 필드까지 정확히 선언하려 들면 헤더 버전에 따라 어긋날 위험이 있으므로,
/// 넉넉한 버퍼를 0으로 밀고 <c>stype</c>만 채워 넘긴 뒤 <b>앞쪽 필드만</b> 오프셋으로 읽는다.
/// </para>
/// <para>
/// <b>읽은 값은 범위로 검증한다.</b> 레이아웃이 어긋나면 쓰레기 값이 나오는데, 그걸 그대로
/// 차트에 흘리면 "3만 도" 같은 숫자가 뜬다. 물리적으로 불가능한 값은 버리고 없는 것으로 취급한다 —
/// 틀린 값을 보여주느니 값이 없는 편이 낫다.
/// </para>
/// <para>
/// 전력은 누적 에너지 카운터(μJ)와 타임스탬프(μs)로 오므로 <b>두 샘플의 차</b>로 계산한다.
/// 첫 샘플에서는 값을 낼 수 없다.
/// </para>
/// </remarks>
public sealed class LevelZeroTelemetry : IVendorTelemetry
{
    /// <summary>
    /// 스크래치 버퍼. 여기 넘기는 어떤 구조체보다 넉넉해야 한다 —
    /// 작으면 드라이버가 버퍼 밖으로 쓰고 힙이 조용히 망가진다.
    /// </summary>
    private const int BufferSize = 512;

    // 물리적으로 가능한 범위. 벗어나면 레이아웃이 어긋났다고 보고 버린다.
    private const double MinCelsius = -20, MaxCelsius = 150;
    private const double MaxWatts = 2000;
    private const double MinMegahertz = 1, MaxMegahertz = 10_000;

    private readonly List<Device> _devices = [];
    private readonly Dictionary<PciAddress, int> _byAddress = [];
    private nint _scratch;

    public string Name => "Level Zero";
    public bool IsAvailable { get; private set; }

    /// <summary>진단용. 열린 장치 수.</summary>
    public int DeviceCount => _devices.Count;

    /// <summary>
    /// 진단용. PCIe 누적 통계를 원시값으로 읽는다. 드라이버가 지원하는지 가르는 데 쓴다.
    /// </summary>
    public IReadOnlyList<(string Name, double Value)> ProbePci(int handle)
    {
        var result = new List<(string, double)>();
        if (!IsAvailable || (uint)handle >= (uint)_devices.Count) return result;

        unsafe { NativeMemory.Clear((void*)_scratch, 64); }
        uint rc = LevelZeroNative.PciGetStats(_devices[handle].Handle, _scratch);
        result.Add(("rc", rc));
        if (rc != LevelZeroNative.Success) return result;

        result.Add(("timestampUs", (ulong)Marshal.ReadInt64(_scratch, 0)));
        result.Add(("replay", (ulong)Marshal.ReadInt64(_scratch, 8)));
        result.Add(("packets", (ulong)Marshal.ReadInt64(_scratch, 16)));
        result.Add(("rxBytes", (ulong)Marshal.ReadInt64(_scratch, 24)));
        result.Add(("txBytes", (ulong)Marshal.ReadInt64(_scratch, 32)));
        result.Add(("gen", Marshal.ReadInt32(_scratch, 40)));
        result.Add(("width", Marshal.ReadInt32(_scratch, 44)));
        return result;
    }

    public static LevelZeroTelemetry? TryCreate()
    {
        var telemetry = new LevelZeroTelemetry();
        if (telemetry.Initialize()) return telemetry;

        telemetry.Dispose();
        return null;
    }

    private bool Initialize()
    {
        try
        {
            Environment.SetEnvironmentVariable("ZES_ENABLE_SYSMAN", "1");
            if (LevelZeroNative.Init(0) != LevelZeroNative.Success) return false;

            _scratch = Marshal.AllocHGlobal(BufferSize);

            foreach (nint driver in Enumerate((ref uint count, nint buffer) =>
                         LevelZeroNative.DriverGet(ref count, buffer)))
            {
                foreach (nint device in Enumerate((ref uint count, nint buffer) =>
                             LevelZeroNative.DeviceGet(driver, ref count, buffer)))
                {
                    if (!TryReadPciAddress(device, out var address)) continue;

                    _byAddress[address] = _devices.Count;
                    _devices.Add(new Device(device));
                }
            }

            IsAvailable = _devices.Count > 0;
            if (IsAvailable) SensorLog.Write($"Level Zero 초기화 — 장치 {_devices.Count}개");
            return IsAvailable;
        }
        catch (DllNotFoundException)
        {
            return false;       // oneAPI 런타임이 없는 시스템. 정상 경로다.
        }
        catch (EntryPointNotFoundException ex)
        {
            SensorLog.Write($"Level Zero 진입점 없음: {ex.Message}");
            return false;
        }
    }

    private delegate uint CountedCall(ref uint count, nint buffer);

    /// <summary>Level Zero의 "개수 먼저, 그다음 배열" 관용구.</summary>
    private static List<nint> Enumerate(CountedCall call)
    {
        var result = new List<nint>();

        uint count = 0;
        if (call(ref count, 0) != LevelZeroNative.Success || count == 0) return result;

        nint buffer = Marshal.AllocHGlobal(nint.Size * (int)count);
        try
        {
            if (call(ref count, buffer) != LevelZeroNative.Success) return result;
            for (int i = 0; i < count; i++) result.Add(Marshal.ReadIntPtr(buffer, i * nint.Size));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    private bool TryReadPciAddress(nint device, out PciAddress address)
    {
        address = default;
        Clear(_scratch);
        Marshal.WriteInt32(_scratch, 0, (int)LevelZeroNative.StypePciProperties);

        if (LevelZeroNative.DevicePciGetProperties(device, _scratch) != LevelZeroNative.Success) return false;

        // zes_pci_address_t : domain · bus · device · function (각 uint32), 헤더 바로 뒤
        uint bus = (uint)Marshal.ReadInt32(_scratch, LevelZeroNative.HeaderSize + 4);
        uint slot = (uint)Marshal.ReadInt32(_scratch, LevelZeroNative.HeaderSize + 8);
        uint function = (uint)Marshal.ReadInt32(_scratch, LevelZeroNative.HeaderSize + 12);

        address = new PciAddress(bus, slot, function);
        return true;
    }

    public bool TryBind(PciAddress address, out int handle) => _byAddress.TryGetValue(address, out handle);

    public bool TryRead(int handle, bool full, out VendorSample sample)
    {
        sample = VendorSample.Empty;

        // 이 경로는 사용률을 내주지 않는다. 가벼운 틱에는 할 일이 없다.
        if (!full || !IsAvailable || (uint)handle >= (uint)_devices.Count) return false;

        var device = _devices[handle];
        bool any = false;

        if (TryReadTemperature(device, out float celsius)) { sample.TemperatureCelsius = celsius; any = true; }
        if (TryReadPower(device, out float watts)) { sample.PowerWatts = watts; any = true; }
        if (TryReadClock(device, out float megahertz)) { sample.CoreClockMegahertz = megahertz; any = true; }

        return any;
    }

    /// <summary>센서가 여러 개면 가장 뜨거운 값을 쓴다. 어느 센서가 GPU 코어인지 가리는 것보다 안전하다.</summary>
    private bool TryReadTemperature(Device device, out float celsius)
    {
        celsius = 0;
        device.Sensors ??= Enumerate((ref uint count, nint buffer) =>
            LevelZeroNative.EnumTemperatureSensors(device.Handle, ref count, buffer));

        double hottest = double.NaN;
        foreach (nint sensor in device.Sensors)
        {
            if (LevelZeroNative.TemperatureGetState(sensor, out double value) != LevelZeroNative.Success) continue;
            if (value is <= MinCelsius or > MaxCelsius) continue;
            if (double.IsNaN(hottest) || value > hottest) hottest = value;
        }

        if (double.IsNaN(hottest)) return false;
        celsius = (float)hottest;
        return true;
    }

    private bool TryReadPower(Device device, out float watts)
    {
        watts = 0;
        device.PowerDomains ??= Enumerate((ref uint count, nint buffer) =>
            LevelZeroNative.EnumPowerDomains(device.Handle, ref count, buffer));

        if (device.PowerDomains.Count == 0) return false;

        Clear(_scratch);
        Marshal.WriteInt32(_scratch, 0, (int)LevelZeroNative.StypePowerEnergyCounter);
        // 도메인이 여러 개일 수 있다(카드 전체 · 서브디바이스). 값을 내주는 첫 도메인을 쓴다.
        bool read = false;
        foreach (nint domain in device.PowerDomains)
        {
            if (LevelZeroNative.PowerGetEnergyCounter(domain, _scratch) != LevelZeroNative.Success) continue;
            read = true;
            break;
        }
        if (!read) return false;

        // zes_power_energy_counter_t : energy(μJ, uint64) · timestamp(μs, uint64)
        ulong energy = (ulong)Marshal.ReadInt64(_scratch, LevelZeroNative.HeaderSize);
        ulong timestamp = (ulong)Marshal.ReadInt64(_scratch, LevelZeroNative.HeaderSize + 8);

        bool hadBaseline = device.HasEnergyBaseline;
        ulong previousEnergy = device.LastEnergy, previousTimestamp = device.LastTimestamp;

        device.LastEnergy = energy;
        device.LastTimestamp = timestamp;
        device.HasEnergyBaseline = true;

        // 누적 카운터라 두 샘플이 있어야 값이 나온다. 첫 샘플은 기준선만 남긴다.
        if (!hadBaseline || timestamp <= previousTimestamp || energy < previousEnergy) return false;

        double microjoules = energy - previousEnergy;
        double microseconds = timestamp - previousTimestamp;
        double value = microjoules / microseconds;      // μJ/μs = W

        if (value is < 0 or > MaxWatts) return false;
        watts = (float)value;
        return true;
    }

    /// <summary>
    /// 첫 주파수 도메인을 코어 클럭으로 본다. Intel에서 도메인 0은 GPU다.
    /// <c>zes_freq_state_t</c>의 뒤쪽 필드 배치가 헤더 버전에 따라 다를 수 있어, 값을 범위로 검증한다.
    /// </summary>
    private bool TryReadClock(Device device, out float megahertz)
    {
        megahertz = 0;
        device.FrequencyDomains ??= Enumerate((ref uint count, nint buffer) =>
            LevelZeroNative.EnumFrequencyDomains(device.Handle, ref count, buffer));

        if (device.FrequencyDomains.Count == 0) return false;

        Clear(_scratch);
        Marshal.WriteInt32(_scratch, 0, (int)LevelZeroNative.StypeFreqState);
        if (LevelZeroNative.FrequencyGetState(device.FrequencyDomains[0], _scratch) != LevelZeroNative.Success)
            return false;

        // zes_freq_state_t : currentVoltage · request · tdp · efficient · actual (각 double)
        // actual 은 헤더 + 4번째 double. 지원하지 않으면 -1.0 이 온다.
        double actual = BitConverter.Int64BitsToDouble(
            Marshal.ReadInt64(_scratch, LevelZeroNative.HeaderSize + 8 * 4));

        if (double.IsNaN(actual) || actual is < MinMegahertz or > MaxMegahertz) return false;
        megahertz = (float)actual;
        return true;
    }

    private static unsafe void Clear(nint buffer) =>
        NativeMemory.Clear((void*)buffer, BufferSize);

    // 이 클래스는 항상 버퍼 전체를 지우므로 IGCL 쪽 같은 길이 인자 가드가 필요 없다.

    public void Dispose()
    {
        if (_scratch != 0)
        {
            Marshal.FreeHGlobal(_scratch);
            _scratch = 0;
        }

        _devices.Clear();
        _byAddress.Clear();
        IsAvailable = false;
    }

    private sealed class Device(nint handle)
    {
        public nint Handle { get; } = handle;

        // 핸들 목록은 바뀌지 않는다. 틱마다 다시 열거하면 그것만으로 예산을 쓴다.
        public List<nint>? Sensors { get; set; }
        public List<nint>? PowerDomains { get; set; }
        public List<nint>? FrequencyDomains { get; set; }

        public ulong LastEnergy { get; set; }
        public ulong LastTimestamp { get; set; }
        public bool HasEnergyBaseline { get; set; }
    }
}

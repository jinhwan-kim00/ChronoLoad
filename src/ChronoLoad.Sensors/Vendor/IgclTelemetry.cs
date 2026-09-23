using System.Runtime.InteropServices;

namespace ChronoLoad.Sensors.Vendor;

internal static partial class IgclNative
{
    public const uint Success = 0;

    // igcl_api.h — CTL_MAKE_VERSION(major, minor) = (major << 16) | minor
    public const uint ImplVersion = (1u << 16) | 1u;        // CTL_IMPL_VERSION 1.1

    public const int InitArgsSize = 36;
    public const int AdapterPropertiesSize = 320;
    /// <summary>
    /// <c>ctl_power_telemetry_t</c> 의 sizeof. x64 자연 정렬로 계산하면 정확히 1024 바이트다.
    /// 드라이버가 이 값으로 레이아웃을 고르므로 한 바이트도 틀리면 안 된다.
    /// </summary>
    public const int PowerTelemetrySize = 1024;

    // ctl_power_telemetry_t 안에서 필요한 항목의 오프셋. 각 항목은 ctl_oc_telemetry_item_t(24바이트)다.
    public const int ItemTimeStamp = 8;
    public const int ItemGpuEnergyCounter = 32;
    public const int ItemGpuClockFrequency = 80;
    public const int ItemGpuTemperature = 104;

    // ctl_oc_telemetry_item_t 내부 오프셋
    public const int ItemSupported = 0;     // bool
    public const int ItemType = 8;          // ctl_data_type_t
    public const int ItemValue = 16;        // ctl_data_value_t (union)

    [LibraryImport("ControlLib.dll", EntryPoint = "ctlInit")]
    public static partial uint Init(nint initArgs, out nint apiHandle);

    [LibraryImport("ControlLib.dll", EntryPoint = "ctlClose")]
    public static partial uint Close(nint apiHandle);

    [LibraryImport("ControlLib.dll", EntryPoint = "ctlEnumerateDevices")]
    public static partial uint EnumerateDevices(nint apiHandle, ref uint count, nint devices);

    [LibraryImport("ControlLib.dll", EntryPoint = "ctlGetDeviceProperties")]
    public static partial uint GetDeviceProperties(nint adapter, nint properties);

    [LibraryImport("ControlLib.dll", EntryPoint = "ctlPowerTelemetryGet")]
    public static partial uint PowerTelemetryGet(nint adapter, nint telemetry);
}

/// <summary>
/// Intel Graphics Control Library(IGCL). Windows에서 Arc의 온도·전력을 읽는 유일한 경로다.
/// </summary>
/// <remarks>
/// <para>
/// <b>왜 Level Zero로 충분하지 않은가.</b> 실측 결과 Windows Arc 드라이버는 Level Zero Sysman으로
/// 주파수만 내주고, 온도 센서는 0개, 에너지 카운터는 <c>UNSUPPORTED_FEATURE</c>로 거절한다.
/// 같은 값을 Intel 자사 도구는 보여주므로, 그 도구가 쓰는 IGCL을 함께 붙인다.
/// 실측(Arc B580 · 유휴): 46 °C · 19.0 W · 400 MHz.
/// 두 경로는 <b>경쟁하지 않고 겹쳐 쓴다</b> — Level Zero가 클럭, IGCL이 온도·전력이다.
/// </para>
/// <para>
/// <b>IGCL 구조체는 Level Zero와 헤더가 다르다.</b> <c>stype</c>이 아니라
/// <c>Size</c>(uint32) + <c>Version</c>(uint8)으로 시작하고, 드라이버가 이 둘로 레이아웃을 고른다.
/// 그래서 <c>Size</c>는 실제 <c>sizeof</c>와 정확히 같아야 한다. 여기서는 x64 자연 정렬로 계산한
/// 상수를 쓰고, 어댑터 속성만은 버전을 2→1→0으로 낮춰가며 재시도한다.
/// </para>
/// <para>
/// <b><c>ctlInit</c> 에 <c>CTL_INIT_FLAG_USE_LEVEL_ZERO</c> 를 반드시 넘긴다.</b> IGCL 의 텔레메트리는
/// 내부적으로 Level Zero 위에 얹혀 있어서, 이 플래그가 없으면 센서·도메인 열거가
/// <c>CTL_RESULT_ERROR_ZE_LOADER</c>(0x40000019)로 실패한다. 값이 안 나오는 증상만 보고
/// "유휴라서 그렇다"고 오해하기 쉬운 지점이다.
/// </para>
/// <para>
/// 온도·전력·클럭은 센서를 하나씩 열거하지 않고 <c>ctlPowerTelemetryGet</c> 한 번으로 받는다.
/// Arc 는 <c>ctlEnumTemperatureSensors</c> 에 0개를 돌려주므로(성공 코드와 함께!) 그 경로로는
/// 온도를 영영 못 읽는다. 이 API 가 Intel 자사 오버레이가 쓰는 경로다.
/// </para>
/// </remarks>
public sealed class IgclTelemetry : IVendorTelemetry
{
    /// <summary>
    /// 스크래치 버퍼 크기. <b>여기 넘기는 모든 구조체보다 커야 한다</b> —
    /// 가장 큰 것이 <c>ctl_power_telemetry_t</c>(1024바이트)다.
    /// 이 값이 작으면 드라이버가 버퍼 밖으로 쓰고, 힙이 조용히 망가져
    /// 한참 뒤 엉뚱한 곳에서 죽는다. 실제로 그렇게 죽였다.
    /// </summary>
    private const int BufferSize = 2048;

    private const double MinCelsius = -20, MaxCelsius = 150;
    private const double MaxWatts = 2000;
    private const double MaxMegahertz = 10_000;

    private readonly List<Device> _devices = [];
    private readonly Dictionary<PciAddress, int> _byAddress = [];
    private nint _api;
    private nint _scratch;
    private nint _deviceIdBuffer;

    public string Name => "IGCL";
    public bool IsAvailable { get; private set; }

    public static IgclTelemetry? TryCreate()
    {
        var telemetry = new IgclTelemetry();
        if (telemetry.Initialize()) return telemetry;

        telemetry.Dispose();
        return null;
    }

    private bool Initialize()
    {
        try
        {
            _scratch = Marshal.AllocHGlobal(BufferSize);
            _deviceIdBuffer = Marshal.AllocHGlobal(8);      // LUID 한 개. 드라이버가 요구하는 출력 버퍼다.

            if (!TryInitApi()) return false;

            foreach (nint adapter in EnumerateAdapters())
            {
                if (!TryReadBdf(adapter, out var address, out string name)) continue;

                _byAddress[address] = _devices.Count;
                _devices.Add(new Device(adapter));
                SensorLog.Write($"IGCL 어댑터: {name} (PCI {address})");
            }

            IsAvailable = _devices.Count > 0;
            if (!IsAvailable) SensorLog.Write("IGCL: PCI 주소를 보고한 어댑터가 없다");
            return IsAvailable;
        }
        catch (DllNotFoundException)
        {
            return false;       // Intel 그래픽 드라이버가 없는 시스템. 정상 경로다.
        }
        catch (EntryPointNotFoundException ex)
        {
            SensorLog.Write($"IGCL 진입점 없음: {ex.Message}");
            return false;
        }
    }

    private bool TryInitApi()
    {
        Clear(_scratch, IgclNative.InitArgsSize);
        Marshal.WriteInt32(_scratch, 0, IgclNative.InitArgsSize);   // Size
        Marshal.WriteByte(_scratch, 4, 0);                          // Version
        Marshal.WriteInt32(_scratch, 8, (int)IgclNative.ImplVersion); // AppVersion
        Marshal.WriteInt32(_scratch, 12, 1);                        // flags = CTL_INIT_FLAG_USE_LEVEL_ZERO
        // ApplicationUID(@20, 16바이트)는 0으로 둔다. 헤더가 허용하는 값이다.

        uint rc = IgclNative.Init(_scratch, out _api);
        if (rc == IgclNative.Success && _api != 0) return true;

        SensorLog.Write($"IGCL ctlInit 실패 0x{rc:X}");
        return false;
    }

    private List<nint> EnumerateAdapters()
    {
        var result = new List<nint>();

        uint count = 0;
        if (IgclNative.EnumerateDevices(_api, ref count, 0) != IgclNative.Success || count == 0) return result;

        nint buffer = Marshal.AllocHGlobal(nint.Size * (int)count);
        try
        {
            if (IgclNative.EnumerateDevices(_api, ref count, buffer) != IgclNative.Success) return result;
            for (int i = 0; i < count; i++) result.Add(Marshal.ReadIntPtr(buffer, i * nint.Size));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    /// <summary>
    /// 어댑터 속성에서 PCI 버스·장치·기능을 읽는다.
    /// <c>Version</c>을 2→1→0으로 낮춰가며 재시도하는 이유는, 드라이버가 자신이 아는 것보다
    /// 높은 버전을 받으면 거절하기 때문이다. BDF 필드는 버전 1부터 존재한다.
    /// </summary>
    private bool TryReadBdf(nint adapter, out PciAddress address, out string name)
    {
        address = default;
        name = string.Empty;

        bool ok = false;
        for (byte version = 2; ; version--)
        {
            Clear(_scratch, IgclNative.AdapterPropertiesSize);
            Marshal.WriteInt32(_scratch, 0, IgclNative.AdapterPropertiesSize);  // Size
            Marshal.WriteByte(_scratch, 4, version);                            // Version
            Marshal.WriteIntPtr(_scratch, 8, _deviceIdBuffer);                  // pDeviceID
            Marshal.WriteInt32(_scratch, 16, 8);                                // device_id_size (LUID)

            if (IgclNative.GetDeviceProperties(adapter, _scratch) == IgclNative.Success) { ok = true; break; }
            if (version == 0) break;
        }

        if (!ok) return false;

        // ctl_device_adapter_properties_t — x64 자연 정렬 기준 오프셋
        name = Marshal.PtrToStringAnsi(_scratch + 88) ?? string.Empty;          // char name[100]
        uint bus = Marshal.ReadByte(_scratch, 200);                             // ctl_adapter_bdf_t
        uint device = Marshal.ReadByte(_scratch, 201);
        uint function = Marshal.ReadByte(_scratch, 202);

        // BDF 가 전부 0 이면 드라이버가 채우지 않은 것이다. 엉뚱한 어댑터에 붙느니 버린다.
        if (bus == 0 && device == 0 && function == 0)
        {
            SensorLog.Write($"IGCL: '{name}' 이 BDF 를 보고하지 않아 건너뛴다");
            return false;
        }

        address = new PciAddress(bus, device, function);
        return true;
    }

    public bool TryBind(PciAddress address, out int handle) => _byAddress.TryGetValue(address, out handle);

    public bool TryRead(int handle, bool full, out VendorSample sample)
    {
        sample = VendorSample.Empty;

        // 이 경로는 사용률을 내주지 않는다. 가벼운 틱에는 할 일이 없다.
        if (!full || !IsAvailable || (uint)handle >= (uint)_devices.Count) return false;

        var device = _devices[handle];

        Clear(_scratch, IgclNative.PowerTelemetrySize);
        Marshal.WriteInt32(_scratch, 0, IgclNative.PowerTelemetrySize);     // Size
        Marshal.WriteByte(_scratch, 4, device.TelemetryVersion);            // Version

        uint rc = IgclNative.PowerTelemetryGet(device.Handle, _scratch);
        if (rc != IgclNative.Success)
        {
            // 버전이 맞지 않으면 드라이버가 거절한다. 0부터 올려가며 한 번씩만 더 시도한다.
            if (device.TelemetryVersion < 2) { device.TelemetryVersion++; return false; }

            if (!device.LoggedFailure)
            {
                device.LoggedFailure = true;
                SensorLog.Write($"IGCL ctlPowerTelemetryGet 실패 0x{rc:X}");
            }
            return false;
        }

        bool any = false;

        double celsius = ReadItem(IgclNative.ItemGpuTemperature);
        if (celsius is > MinCelsius and <= MaxCelsius) { sample.TemperatureCelsius = (float)celsius; any = true; }

        double megahertz = ReadItem(IgclNative.ItemGpuClockFrequency);
        if (megahertz is > 0 and <= MaxMegahertz) { sample.CoreClockMegahertz = (float)megahertz; any = true; }

        if (TryEnergyToWatts(device, out float watts)) { sample.PowerWatts = watts; any = true; }

        return any;
    }

    /// <summary>
    /// 전력은 직접 오지 않는다. 누적 에너지(J)와 타임스탬프(s)를 받아 두 샘플의 기울기로 구한다.
    /// 첫 샘플에서는 기준선만 남기고 값을 내지 않는다.
    /// </summary>
    private bool TryEnergyToWatts(Device device, out float watts)
    {
        watts = 0;

        double joules = ReadItem(IgclNative.ItemGpuEnergyCounter);
        double seconds = ReadItem(IgclNative.ItemTimeStamp);
        if (double.IsNaN(joules) || double.IsNaN(seconds)) return false;

        bool hadBaseline = device.HasEnergyBaseline;
        double previousJoules = device.LastJoules, previousSeconds = device.LastSeconds;

        device.LastJoules = joules;
        device.LastSeconds = seconds;
        device.HasEnergyBaseline = true;

        if (!hadBaseline || seconds <= previousSeconds || joules < previousJoules) return false;

        double value = (joules - previousJoules) / (seconds - previousSeconds);
        if (value is < 0 or > MaxWatts) return false;

        watts = (float)value;
        return true;
    }

    /// <summary>
    /// <c>ctl_oc_telemetry_item_t</c> 하나를 double 로 읽는다.
    /// 값의 실제 타입은 항목마다 다르고 드라이버가 <c>type</c> 필드로 알려준다 —
    /// 고정 타입으로 읽으면 같은 비트가 엉뚱한 숫자가 된다.
    /// 지원하지 않는 항목은 <c>bSupported=false</c> 로 오므로 NaN 을 돌려준다.
    /// </summary>
    private double ReadItem(int itemOffset)
    {
        if (Marshal.ReadByte(_scratch, itemOffset + IgclNative.ItemSupported) == 0) return double.NaN;

        int type = Marshal.ReadInt32(_scratch, itemOffset + IgclNative.ItemType);
        int at = itemOffset + IgclNative.ItemValue;

        return type switch
        {
            4 => Marshal.ReadInt32(_scratch, at),                                       // INT32
            5 => (uint)Marshal.ReadInt32(_scratch, at),                                 // UINT32
            6 => Marshal.ReadInt64(_scratch, at),                                       // INT64
            7 => (ulong)Marshal.ReadInt64(_scratch, at),                                // UINT64
            8 => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(_scratch, at)),       // FLOAT
            9 => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(_scratch, at)),       // DOUBLE
            _ => double.NaN,
        };
    }

    private static unsafe void Clear(nint buffer, int length)
    {
        // 버퍼를 넘어서는 크기를 조용히 받아주면 힙이 망가진 채로 굴러간다. 여기서 끊는다.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, BufferSize);
        NativeMemory.Clear((void*)buffer, (nuint)length);
    }

    public void Dispose()
    {
        if (_api != 0)
        {
            IgclNative.Close(_api);
            _api = 0;
        }

        if (_scratch != 0) { Marshal.FreeHGlobal(_scratch); _scratch = 0; }
        if (_deviceIdBuffer != 0) { Marshal.FreeHGlobal(_deviceIdBuffer); _deviceIdBuffer = 0; }

        _devices.Clear();
        _byAddress.Clear();
        IsAvailable = false;
    }

    private sealed class Device(nint handle)
    {
        public nint Handle { get; } = handle;

        /// <summary>드라이버가 받아준 구조체 버전. 거절당하면 한 단계씩 올려 재시도한다.</summary>
        public byte TelemetryVersion { get; set; }
        public bool LoggedFailure { get; set; }

        public double LastJoules { get; set; }
        public double LastSeconds { get; set; }
        public bool HasEnergyBaseline { get; set; }
    }
}

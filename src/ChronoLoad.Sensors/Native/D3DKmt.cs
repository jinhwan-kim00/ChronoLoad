using System.Runtime.InteropServices;

namespace ChronoLoad.Sensors.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct Luid
{
    public uint LowPart;
    public int HighPart;

    public readonly ulong Value => ((ulong)(uint)HighPart << 32) | LowPart;

    /// <summary>PDH 인스턴스 이름에 쓰이는 표기. 예 <c>luid_0x00000000_0x0000E52D</c>.</summary>
    public readonly string PdhToken => $"luid_0x{(uint)HighPart:x8}_0x{LowPart:x8}";
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3DKmtAdapterInfo
{
    public uint AdapterHandle;
    public Luid AdapterLuid;
    public uint NumOfSources;
    public int PrecisePresentRegionsPreferred;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3DKmtEnumAdapters2
{
    public uint NumAdapters;
    public nint Adapters;
}

internal enum D3DKmtAdapterEnumFilter : uint
{
    /// <summary>필터 없음 — 디스플레이 어댑터와 <b>연산 전용 어댑터를 모두</b> 돌려준다.</summary>
    None = 0,
    ComputeOnly = 1,
    DisplayOnly = 2,
}

/// <summary>
/// <c>D3DKMTEnumAdapters2</c>는 디스플레이 어댑터만 돌려준다. NPU 같은 MCDM(연산 전용) 어댑터는
/// <c>EnumAdapters3</c>에 필터를 줘야 나온다 — 실기기에서 카운터에는 LUID가 4개 있는데
/// EnumAdapters2는 3개만 돌려주는 것으로 확인했다.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D3DKmtEnumAdapters3
{
    public D3DKmtAdapterEnumFilter Filter;
    public uint NumAdapters;
    public nint Adapters;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3DKmtQueryAdapterInfo
{
    public uint AdapterHandle;
    public uint Type;
    public nint PrivateDriverData;
    public uint PrivateDriverDataSize;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct D3DKmtAdapterRegistryInfo
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string AdapterString;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string BiosString;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DacType;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ChipType;
}

/// <summary><c>D3DKMT_ADAPTERADDRESS</c>. 벤더 SDK 장치 목록과 잇는 좌표다.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D3DKmtAdapterAddress
{
    public uint BusNumber;
    public uint DeviceNumber;
    public uint FunctionNumber;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3DKmtSegmentSizeInfo
{
    public ulong DedicatedVideoMemorySize;
    public ulong DedicatedSystemMemorySize;
    public ulong SharedSystemMemorySize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3DKmtDeviceIds
{
    public uint VendorId;
    public uint DeviceId;
    public uint SubVendorId;
    public uint SubSystemId;
    public uint RevisionId;
    public uint BusType;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3DKmtQueryDeviceIds
{
    public uint PhysicalAdapterIndex;
    public D3DKmtDeviceIds DeviceIds;
}

/// <summary>
/// <c>D3DKMT_ADAPTERTYPE</c>. 비트 필드라 정수 하나로 받는다.
/// <c>ComputeOnly</c>가 NPU를 GPU와 가르는 결정적 신호다.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D3DKmtAdapterType
{
    public uint Value;

    public readonly bool RenderSupported => (Value & (1u << 0)) != 0;
    public readonly bool DisplaySupported => (Value & (1u << 1)) != 0;
    public readonly bool SoftwareDevice => (Value & (1u << 2)) != 0;
    public readonly bool HybridDiscrete => (Value & (1u << 4)) != 0;
    public readonly bool HybridIntegrated => (Value & (1u << 5)) != 0;

    /// <summary>디스플레이 출력이 없는 연산 전용 어댑터. Intel AI Boost·AMD XDNA 같은 NPU가 여기 해당한다.</summary>
    public readonly bool ComputeOnly => (Value & (1u << 11)) != 0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3DKmtCloseAdapter
{
    public uint AdapterHandle;
}

/// <summary>
/// WDDM 커널 모드 썽크. DXGI를 쓰려면 COM 인터페이스를 손으로 정의해야 하지만,
/// 필요한 것(LUID · 이름 · 세그먼트 크기 · 벤더 ID)은 전부 <b>평범한 구조체</b>로 얻을 수 있다.
/// </summary>
internal static partial class D3DKmt
{
    private const uint QueryAdapterRegistryInfo = 8;
    private const uint QuerySegmentSize = 3;
    private const uint QueryAdapterAddress = 6;
    private const uint QueryAdapterType = 15;
    private const uint QueryPhysicalAdapterDeviceIds = 31;
    private const uint QueryWddm27Caps = 70;

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTEnumAdapters2(ref D3DKmtEnumAdapters2 request);

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTEnumAdapters3(ref D3DKmtEnumAdapters3 request);

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTQueryAdapterInfo(ref D3DKmtQueryAdapterInfo request);

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTCloseAdapter(ref D3DKmtCloseAdapter request);

    public sealed record Adapter(
        Luid Luid,
        string Name,
        ulong DedicatedVideoMemory,
        ulong SharedSystemMemory,
        uint VendorId,
        bool IsComputeOnly = false,
        bool VendorWasGuessed = false,
        bool IsSoftware = false,
        uint PciBus = 0,
        uint PciDevice = 0,
        uint PciFunction = 0,
        bool? HardwareScheduling = null)
    {
        /// <summary>전용 VRAM 1 GiB 이상이면 외장으로 본다. 차트 규칙과 경고 규칙을 가르는 기준이다.</summary>
        public bool IsDiscrete => DedicatedVideoMemory >= 1L * 1024 * 1024 * 1024;
    }

    public static List<Adapter> EnumerateAdapters()
    {
        var adapters = new List<Adapter>();

        nint buffer = 0;
        int count = EnumerateHandles(ref buffer);
        if (count == 0) return adapters;

        int stride = Marshal.SizeOf<D3DKmtAdapterInfo>();

        try
        {
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<D3DKmtAdapterInfo>(buffer + i * stride);
                try
                {
                    var type = QueryAdapterTypeFlags(info.AdapterHandle);
                    var segments = QuerySegments(info.AdapterHandle);
                    uint? vendorEarly = QueryVendorId(info.AdapterHandle);

                    // 소프트웨어 렌더러(WARP)도 <b>빼지 않고 보고한다</b>. 여기서 걸러버리면
                    // 프로바이더는 그 LUID 를 "D3DKMT 가 모르는 어댑터"로 보고 NPU 로 오인한다.
                    // 실기기에서 WARP 는 type=0x105(render·software, display 없음) · 벤더 0x1414(Microsoft)다.
                    bool software = type.SoftwareDevice || vendorEarly == 0x1414;
                    if (segments.DedicatedVideoMemorySize == 0 && segments.SharedSystemMemorySize == 0 && !software) continue;

                    bool computeOnly = type.ComputeOnly && !type.DisplaySupported;
                    string name = QueryRegistryInfo(info.AdapterHandle)
                                  ?? (software ? "Microsoft Basic Render Driver" : $"어댑터 {i}");
                    uint vendor = vendorEarly ?? GuessVendorFromName(name);

                    var address = Query<D3DKmtAdapterAddress>(info.AdapterHandle, QueryAdapterAddress) ?? default;

                    adapters.Add(new Adapter(info.AdapterLuid, name,
                        segments.DedicatedVideoMemorySize, segments.SharedSystemMemorySize, vendor,
                        computeOnly, vendorEarly is null, software,
                        address.BusNumber, address.DeviceNumber, address.FunctionNumber,
                        QueryHardwareScheduling(info.AdapterHandle)));
                }
                finally
                {
                    var close = new D3DKmtCloseAdapter { AdapterHandle = info.AdapterHandle };
                    D3DKMTCloseAdapter(ref close);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        // GPU 먼저(외장 → 내장), NPU는 맨 뒤. 주 용도가 GPU 모니터링이다.
        adapters.Sort((a, b) =>
            a.IsComputeOnly != b.IsComputeOnly ? a.IsComputeOnly.CompareTo(b.IsComputeOnly)
            : a.IsDiscrete != b.IsDiscrete ? b.IsDiscrete.CompareTo(a.IsDiscrete)
            : b.DedicatedVideoMemory.CompareTo(a.DedicatedVideoMemory));

        return adapters;
    }

    /// <summary>
    /// 어댑터 핸들 목록을 잡는다. <c>EnumAdapters3</c>(연산 전용 포함)를 먼저 쓰고,
    /// 없는 Windows에서는 <c>EnumAdapters2</c>로 내려간다.
    /// </summary>
    /// <returns>어댑터 수. <paramref name="buffer"/>는 호출자가 해제한다.</returns>
    private static int EnumerateHandles(ref nint buffer)
    {
        int stride = Marshal.SizeOf<D3DKmtAdapterInfo>();

        var v3 = new D3DKmtEnumAdapters3 { Filter = D3DKmtAdapterEnumFilter.None };
        if (D3DKMTEnumAdapters3(ref v3) == 0 && v3.NumAdapters > 0)
        {
            v3.Adapters = Marshal.AllocHGlobal(stride * (int)v3.NumAdapters);
            if (D3DKMTEnumAdapters3(ref v3) == 0)
            {
                buffer = v3.Adapters;
                return (int)v3.NumAdapters;
            }

            Marshal.FreeHGlobal(v3.Adapters);
        }

        var v2 = new D3DKmtEnumAdapters2();
        if (D3DKMTEnumAdapters2(ref v2) != 0 || v2.NumAdapters == 0) return 0;

        v2.Adapters = Marshal.AllocHGlobal(stride * (int)v2.NumAdapters);
        if (D3DKMTEnumAdapters2(ref v2) == 0)
        {
            buffer = v2.Adapters;
            return (int)v2.NumAdapters;
        }

        Marshal.FreeHGlobal(v2.Adapters);
        return 0;
    }

    private static string? QueryRegistryInfo(uint adapter)
    {
        var info = Query<D3DKmtAdapterRegistryInfo>(adapter, QueryAdapterRegistryInfo);
        return info?.AdapterString is { Length: > 0 } name ? name.Trim() : null;
    }

    private static D3DKmtAdapterType QueryAdapterTypeFlags(uint adapter) =>
        Query<D3DKmtAdapterType>(adapter, QueryAdapterType) ?? default;

    private static D3DKmtSegmentSizeInfo QuerySegments(uint adapter) =>
        Query<D3DKmtSegmentSizeInfo>(adapter, QuerySegmentSize) ?? default;

    /// <summary>
    /// 벤더 ID는 제조사 아이콘을 고르는 유일한 근거다. 조회가 실패하면 이름으로 추정하되,
    /// 추정했다는 사실을 <c>describe_capabilities</c>가 보고할 수 있게 구분해 둔다.
    /// </summary>
    private static uint? QueryVendorId(uint adapter)
    {
        var ids = Query<D3DKmtQueryDeviceIds>(adapter, QueryPhysicalAdapterDeviceIds);
        return ids?.DeviceIds.VendorId is { } vendor && vendor != 0 ? vendor : null;
    }

    /// <summary>
    /// 하드웨어 가속 GPU 예약(HAGS)이 이 어댑터에서 켜져 있는가. 모르면 null.
    /// </summary>
    /// <remarks>
    /// <c>D3DKMT_WDDM_2_7_CAPS</c> 의 둘째 비트(<c>HwSchEnabled</c>). 켜져 있으면 NVIDIA 드라이버가
    /// <c>Compute_0</c>·<c>Cuda</c> 노드를 따로 보고하지 않고 <c>3d</c> 노드 하나로 합친다 —
    /// CUDA 부하가 PDH 에서 3D 로 보이는 이유다(§5.4).
    /// </remarks>
    private static bool? QueryHardwareScheduling(uint adapter) =>
        Query<uint>(adapter, QueryWddm27Caps) is { } caps ? (caps & 0b10) != 0 : null;

    public static uint GuessVendorFromName(string name) =>
        name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? 0x10DEu
        : name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
          || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? 0x1002u
        : name.Contains("Intel", StringComparison.OrdinalIgnoreCase)
          || name.Contains("Arc", StringComparison.OrdinalIgnoreCase) ? 0x8086u
        : 0u;

    private static T? Query<T>(uint adapter, uint type) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        nint buffer = Marshal.AllocHGlobal(size);

        try
        {
            // 드라이버가 채우지 않은 필드가 쓰레기로 남지 않게 0으로 밀어둔다.
            for (int i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);

            var request = new D3DKmtQueryAdapterInfo
            {
                AdapterHandle = adapter,
                Type = type,
                PrivateDriverData = buffer,
                PrivateDriverDataSize = (uint)size,
            };

            return D3DKMTQueryAdapterInfo(ref request) == 0 ? Marshal.PtrToStructure<T>(buffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

using System.Runtime.InteropServices;
using ChronoLoad.Sensors.Vendor;

namespace ChronoLoad.Sensors.Native;

/// <summary>장치 전원 상태. 값은 Windows <c>DEVICE_POWER_STATE</c> 그대로다.</summary>
public enum DevicePowerState
{
    Unknown = 0,

    /// <summary>D0 — 완전히 켜져 있다. 이때만 텔레메트리를 읽는다.</summary>
    Active = 1,

    D1 = 2,
    D2 = 3,

    /// <summary>D3 — 저전력 대기. 외장 GPU 는 유휴 시 여기로 내려간다.</summary>
    Off = 4,
}

internal static partial class SetupApi
{
    public const int CR_SUCCESS = 0;
    public const int DIGCF_PRESENT = 0x02;

    public const uint SPDRP_BUSNUMBER = 0x15;

    /// <summary>PCI 에서는 <c>(device &lt;&lt; 16) | function</c> 으로 온다.</summary>
    public const uint SPDRP_ADDRESS = 0x1C;

    public static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");

    [StructLayout(LayoutKind.Sequential)]
    public struct SpDevInfoData
    {
        public int Size;
        public Guid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    /// <summary>
    /// <c>CM_DRP_DEVICE_POWER_DATA</c>. <c>CM_POWER_DATA</c> 구조체를 돌려준다.
    /// OS 가 이미 들고 있는 캐시된 값이라 <b>읽어도 장치를 깨우지 않는다</b> — 이 파일이 존재하는 이유다.
    /// </summary>
    /// <remarks>
    /// <c>CM_DRP_*</c> 는 대응하는 <c>SPDRP_*</c> 보다 1 크다(<c>SPDRP_DEVICE_POWER_DATA</c> 0x1E → 0x1F).
    /// </remarks>
    public const uint CM_DRP_DEVICE_POWER_DATA = 0x1F;

    /// <summary><c>CM_POWER_DATA</c> 에서 <c>PD_MostRecentPowerState</c> 의 오프셋(PD_Size 다음).</summary>
    public const int MostRecentPowerStateOffset = 4;
    public const int PowerDataBufferSize = 96;

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    public static partial nint GetClassDevs(ref Guid classGuid, nint enumerator, nint parent, int flags);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiEnumDeviceInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDeviceInfo(nint devInfoSet, uint index, ref SpDevInfoData devInfo);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDeviceRegistryProperty(
        nint devInfoSet, ref SpDevInfoData devInfo, uint property,
        out uint propertyType, nint buffer, uint bufferSize, out uint requiredSize);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiDestroyDeviceInfoList", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyDeviceInfoList(nint devInfoSet);

    /// <summary>DEVINST 하나만 있으면 되므로 장치 목록을 다시 훑을 필요가 없다.</summary>
    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Registry_PropertyW")]
    public static partial int GetDevNodeRegistryProperty(
        uint devInst, uint property, out uint dataType,
        nint buffer, ref uint length, uint flags);
}

/// <summary>
/// PCI 주소로 GPU 의 전원 상태를 본다.
/// </summary>
/// <remarks>
/// <para>
/// <b>왜 필요한가.</b> 외장 GPU 는 쓰지 않을 때 D3(저전력 대기)로 내려간다. 이때 벤더 SDK 로
/// 온도를 물으면 장치가 깨어난다 — 모니터링 도구가 감시 대상을 깨우는 것은 그 자체로 틀렸고,
/// 전력을 쓰고, 실제로 Intel Arc 에서는 전원 전이 중 네이티브 호출이 프로세스를 죽였다.
/// </para>
/// <para>
/// <b>왜 이 방법인가.</b> 전원 상태를 알아내는 다른 경로들(DXGI 메모리 조회, 벤더 SDK 질의)은
/// 하나같이 장치를 건드려서 깨운다. <c>DEVPKEY_Device_PowerData</c> 는 OS 가 이미 들고 있는
/// 캐시된 값이라 <b>읽어도 장치가 깨지 않는다.</b>
/// </para>
/// <para>
/// 열거는 한 번만 하고 <c>DEVINST</c> 만 들고 있는다. 상태 조회는 <c>DEVINST</c> 하나로 되므로
/// 매번 장치 목록을 다시 훑지 않아도 된다. 장치가 착탈되면 <c>DEVINST</c> 가 무효해지는데,
/// 그때는 어차피 재열거가 돈다.
/// </para>
/// </remarks>
public sealed class DevicePowerProbe
{
    private readonly Dictionary<PciAddress, uint> _devInstByAddress = [];
    private readonly HashSet<PciAddress> _loggedFailure = [];

    public int Count => _devInstByAddress.Count;

    /// <summary>전원 상태를 물어볼 수 있는 어댑터들.</summary>
    public IReadOnlyCollection<PciAddress> Addresses => _devInstByAddress.Keys;

    /// <summary>디스플레이 클래스 장치를 훑어 PCI 주소 → DEVINST 지도를 만든다.</summary>
    public void Refresh()
    {
        _devInstByAddress.Clear();

        var classGuid = SetupApi.DisplayClass;
        nint set = SetupApi.GetClassDevs(ref classGuid, 0, 0, SetupApi.DIGCF_PRESENT);
        if (set == -1 || set == 0) return;

        nint buffer = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            var info = new SetupApi.SpDevInfoData { Size = Marshal.SizeOf<SetupApi.SpDevInfoData>() };

            for (uint index = 0; SetupApi.EnumDeviceInfo(set, index, ref info); index++)
            {
                if (!TryReadUInt32(set, ref info, SetupApi.SPDRP_BUSNUMBER, buffer, out uint bus)) continue;
                if (!TryReadUInt32(set, ref info, SetupApi.SPDRP_ADDRESS, buffer, out uint address)) continue;

                var pci = new PciAddress(bus, address >> 16, address & 0xFFFF);
                _devInstByAddress[pci] = info.DevInst;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            SetupApi.DestroyDeviceInfoList(set);
        }
    }

    private static bool TryReadUInt32(
        nint set, ref SetupApi.SpDevInfoData info, uint property, nint buffer, out uint value)
    {
        value = 0;
        if (!SetupApi.GetDeviceRegistryProperty(set, ref info, property, out _, buffer, sizeof(uint), out _))
            return false;

        value = (uint)Marshal.ReadInt32(buffer);
        return true;
    }

    /// <summary>
    /// 전원 상태를 본다. 장치를 모르거나 조회가 실패하면 <see cref="DevicePowerState.Unknown"/> 이다 —
    /// <b>모르는 것을 대기 상태로 간주하지 않는다.</b> 그랬다가는 조회가 실패하는 시스템에서
    /// 온도·전력이 영영 안 나오고, 원인도 보이지 않는다.
    /// </summary>
    public DevicePowerState Query(PciAddress address)
    {
        if (!_devInstByAddress.TryGetValue(address, out uint devInst)) return DevicePowerState.Unknown;

        nint buffer = Marshal.AllocHGlobal(SetupApi.PowerDataBufferSize);
        try
        {
            uint size = SetupApi.PowerDataBufferSize;

            int rc = SetupApi.GetDevNodeRegistryProperty(
                devInst, SetupApi.CM_DRP_DEVICE_POWER_DATA, out _, buffer, ref size, 0);

            if (rc != SetupApi.CR_SUCCESS)
            {
                if (_loggedFailure.Add(address))
                    SensorLog.Write($"전원 상태 조회 실패 PCI {address} — CONFIGRET {rc}");
                return DevicePowerState.Unknown;
            }

            int state = Marshal.ReadInt32(buffer, SetupApi.MostRecentPowerStateOffset);
            return state is >= 1 and <= 4 ? (DevicePowerState)state : DevicePowerState.Unknown;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

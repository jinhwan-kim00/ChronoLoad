using System.Runtime.InteropServices;

namespace ChronoLoad.Sensors.Native;

internal enum IfType : uint
{
    Other = 1,
    Ethernet = 6,
    Ppp = 23,
    SoftwareLoopback = 24,
    Atm = 37,
    Ieee80211 = 71,
    Tunnel = 131,
    Ieee1394 = 144,
    WwanPp = 243,
    WwanPp2 = 244,
}

internal enum IfOperStatus : uint
{
    Up = 1,
    Down = 2,
    Testing = 3,
    Unknown = 4,
    Dormant = 5,
    NotPresent = 6,
    LowerLayerDown = 7,
}

/// <summary>
/// <c>MIB_IF_ROW2</c>. 1352바이트짜리 큰 구조체라 <b>열거할 때만</b> 마샬링하고,
/// 매 틱 읽는 카운터는 <see cref="IfTable"/>이 계산해 둔 오프셋으로 직접 읽는다.
/// 틱마다 인터페이스 수만큼 문자열 두 개를 새로 만들 이유가 없다.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MibIfRow2
{
    public ulong InterfaceLuid;
    public uint InterfaceIndex;
    public Guid InterfaceGuid;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Alias;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Description;

    public uint PhysicalAddressLength;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PhysicalAddress;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PermanentPhysicalAddress;

    public uint Mtu;
    public IfType Type;
    public uint TunnelType;
    public uint MediaType;
    public uint PhysicalMediumType;
    public uint AccessType;
    public uint DirectionType;

    /// <summary>비트 필드. bit0 HardwareInterface, bit1 FilterInterface, … bit4 EndPointInterface.</summary>
    public byte InterfaceAndOperStatusFlags;

    public IfOperStatus OperStatus;
    public uint AdminStatus;
    public uint MediaConnectState;
    public Guid NetworkGuid;
    public uint ConnectionType;

    public ulong TransmitLinkSpeed;
    public ulong ReceiveLinkSpeed;

    public ulong InOctets;
    public ulong InUcastPkts;
    public ulong InNUcastPkts;
    public ulong InDiscards;
    public ulong InErrors;
    public ulong InUnknownProtos;
    public ulong InUcastOctets;
    public ulong InMulticastOctets;
    public ulong InBroadcastOctets;

    public ulong OutOctets;
    public ulong OutUcastPkts;
    public ulong OutNUcastPkts;
    public ulong OutDiscards;
    public ulong OutErrors;
    public ulong OutUcastOctets;
    public ulong OutMulticastOctets;
    public ulong OutBroadcastOctets;
    public ulong OutQLen;

    // InterfaceAndOperStatusFlags 비트 배치
    //   0 HardwareInterface · 1 FilterInterface · 2 ConnectorPresent · 3 NotAuthenticated
    //   4 NotMediaConnected · 5 Paused · 6 LowPower · 7 EndPointInterface

    /// <summary>실제 하드웨어 NIC. NDIS 필터 계층 인스턴스와 가상 어댑터를 가르는 1차 기준이다.</summary>
    public readonly bool IsHardwareInterface => (InterfaceAndOperStatusFlags & 0b0000_0001) != 0;

    /// <summary>
    /// NDIS 필터 드라이버가 만든 의사 인터페이스(WFP, QoS Packet Scheduler 등).
    /// 같은 물리 NIC가 필터 계층 수만큼 중복으로 나타나므로 반드시 걸러야 한다.
    /// </summary>
    public readonly bool IsFilterInterface => (InterfaceAndOperStatusFlags & 0b0000_0010) != 0;

    public readonly bool IsNotMediaConnected => (InterfaceAndOperStatusFlags & 0b0001_0000) != 0;

    /// <summary>터널·VPN이 흔히 세우는 플래그. VPN 판정의 보조 신호다.</summary>
    public readonly bool IsEndPointInterface => (InterfaceAndOperStatusFlags & 0b1000_0000) != 0;
}

internal static partial class IpHlpApi
{
    private const uint AfUnspec = 0;

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetIfTable2(out nint table);

    [LibraryImport("iphlpapi.dll")]
    public static partial void FreeMibTable(nint memory);
}

/// <summary>
/// <c>GetIfTable2</c> 결과를 걷는다. 인터페이스가 몇 개든 <b>호출 한 번</b>으로 끝난다 —
/// <c>NetworkInterface.GetIPv4Statistics()</c> 는 인터페이스마다 개별 P/Invoke 라 비싸다.
/// </summary>
internal static class IfTable
{
    private static readonly int RowSize = Marshal.SizeOf<MibIfRow2>();
    private static readonly int TableHeader = 8;   // ULONG NumEntries + 8바이트 정렬 패딩

    private static readonly int LuidOffset = (int)Marshal.OffsetOf<MibIfRow2>(nameof(MibIfRow2.InterfaceLuid));
    private static readonly int OperStatusOffset = (int)Marshal.OffsetOf<MibIfRow2>(nameof(MibIfRow2.OperStatus));
    private static readonly int InOctetsOffset = (int)Marshal.OffsetOf<MibIfRow2>(nameof(MibIfRow2.InOctets));
    private static readonly int OutOctetsOffset = (int)Marshal.OffsetOf<MibIfRow2>(nameof(MibIfRow2.OutOctets));

    public readonly record struct Counters(ulong Luid, ulong InOctets, ulong OutOctets, IfOperStatus OperStatus);

    /// <summary>전체 행을 마샬링한다. 장치 열거처럼 드물게만 호출한다.</summary>
    public static List<MibIfRow2> Enumerate()
    {
        var rows = new List<MibIfRow2>();
        if (IpHlpApi.GetIfTable2(out nint table) != 0 || table == 0) return rows;

        try
        {
            int count = Marshal.ReadInt32(table);
            for (int i = 0; i < count; i++)
                rows.Add(Marshal.PtrToStructure<MibIfRow2>(table + TableHeader + i * RowSize));
        }
        finally
        {
            IpHlpApi.FreeMibTable(table);
        }

        return rows;
    }

    /// <summary>
    /// 매 틱 경로. 필요한 숫자 네 개만 오프셋으로 읽어 문자열 할당을 피한다.
    /// 오프셋은 <see cref="Marshal.OffsetOf"/>로 구조체 정의에서 뽑으므로 손으로 적은 상수가 아니다.
    /// </summary>
    public static void ReadCounters(List<Counters> destination)
    {
        destination.Clear();
        if (IpHlpApi.GetIfTable2(out nint table) != 0 || table == 0) return;

        try
        {
            int count = Marshal.ReadInt32(table);
            for (int i = 0; i < count; i++)
            {
                nint row = table + TableHeader + i * RowSize;
                destination.Add(new Counters(
                    (ulong)Marshal.ReadInt64(row + LuidOffset),
                    (ulong)Marshal.ReadInt64(row + InOctetsOffset),
                    (ulong)Marshal.ReadInt64(row + OutOctetsOffset),
                    (IfOperStatus)(uint)Marshal.ReadInt32(row + OperStatusOffset)));
            }
        }
        finally
        {
            IpHlpApi.FreeMibTable(table);
        }
    }
}

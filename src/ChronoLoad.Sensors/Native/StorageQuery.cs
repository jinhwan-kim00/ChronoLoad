using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ChronoLoad.Sensors.Native;

internal enum StorageBusType : uint
{
    Unknown = 0x00, Scsi = 0x01, Atapi = 0x02, Ata = 0x03, Ieee1394 = 0x04,
    Ssa = 0x05, Fibre = 0x06, Usb = 0x07, RAID = 0x08, iScsi = 0x09,
    Sas = 0x0A, Sata = 0x0B, Sd = 0x0C, Mmc = 0x0D, Virtual = 0x0E,
    FileBackedVirtual = 0x0F, Spaces = 0x10, Nvme = 0x11, Scm = 0x12, Ufs = 0x13,
}

internal enum StoragePropertyId : uint
{
    Device = 0,
    Adapter = 1,
    SeekPenalty = 7,
}

[StructLayout(LayoutKind.Sequential)]
internal struct StoragePropertyQuery
{
    public StoragePropertyId PropertyId;
    public uint QueryType;          // PropertyStandardQuery = 0
    public byte AdditionalParameters;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DeviceSeekPenaltyDescriptor
{
    public uint Version;
    public uint Size;
    [MarshalAs(UnmanagedType.U1)] public bool IncursSeekPenalty;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StorageAdapterDescriptor
{
    public uint Version;
    public uint Size;
    public uint MaximumTransferLength;
    public uint MaximumPhysicalPages;
    public uint AlignmentMask;
    [MarshalAs(UnmanagedType.U1)] public bool AdapterUsesPio;
    [MarshalAs(UnmanagedType.U1)] public bool AdapterScansDown;
    [MarshalAs(UnmanagedType.U1)] public bool CommandQueueing;
    [MarshalAs(UnmanagedType.U1)] public bool AcceleratedTransfer;
    public byte BusType;
    public ushort BusMajorVersion;
    public ushort BusMinorVersion;
}

internal static partial class StorageNative
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private const uint IoctlStorageQueryProperty = 0x002D1400;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateFile(string fileName, uint access, uint shareMode, nint security,
        uint creationDisposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint controlCode,
        nint inBuffer, uint inSize, nint outBuffer, uint outSize, out uint returned, nint overlapped);

    /// <param name="incursSeekPenalty">true = 회전 매체(HDD), false = SSD. 판정 실패면 null.</param>
    public readonly record struct DiskMedia(bool? IncursSeekPenalty, StorageBusType BusType);

    /// <summary>
    /// 물리 디스크의 매체 종류와 버스를 읽는다.
    /// <b>관리자 권한이 필요 없다</b> — 접근 권한 0으로 열면 속성 조회만 허용된다.
    /// </summary>
    public static DiskMedia Query(int physicalDriveIndex)
    {
        // access 0 : 데이터는 못 읽지만 IOCTL_STORAGE_QUERY_PROPERTY 는 통과한다.
        nint raw = CreateFile($@"\\.\PhysicalDrive{physicalDriveIndex}", 0, FileShareReadWrite,
            0, OpenExisting, 0, 0);

        if (raw == -1 || raw == 0)
        {
            Debug.WriteLine($"[ChronoLoad] PhysicalDrive{physicalDriveIndex} 열기 실패");
            return new DiskMedia(null, StorageBusType.Unknown);
        }

        using var handle = new SafeFileHandle(raw, ownsHandle: true);
        return new DiskMedia(
            QuerySeekPenalty(handle),
            QueryBusType(handle));
    }

    private static bool? QuerySeekPenalty(SafeFileHandle handle)
    {
        var descriptor = Send<DeviceSeekPenaltyDescriptor>(handle, StoragePropertyId.SeekPenalty);
        return descriptor?.IncursSeekPenalty;
    }

    private static StorageBusType QueryBusType(SafeFileHandle handle)
    {
        var descriptor = Send<StorageAdapterDescriptor>(handle, StoragePropertyId.Adapter);
        return descriptor is { } value ? (StorageBusType)value.BusType : StorageBusType.Unknown;
    }

    private static T? Send<T>(SafeFileHandle handle, StoragePropertyId propertyId) where T : struct
    {
        var query = new StoragePropertyQuery { PropertyId = propertyId, QueryType = 0 };
        int querySize = Marshal.SizeOf<StoragePropertyQuery>();
        int resultSize = Marshal.SizeOf<T>();

        nint queryBuffer = Marshal.AllocHGlobal(querySize);
        nint resultBuffer = Marshal.AllocHGlobal(resultSize);

        try
        {
            Marshal.StructureToPtr(query, queryBuffer, false);
            bool ok = DeviceIoControl(handle, IoctlStorageQueryProperty,
                queryBuffer, (uint)querySize, resultBuffer, (uint)resultSize, out _, 0);

            return ok ? Marshal.PtrToStructure<T>(resultBuffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(queryBuffer);
            Marshal.FreeHGlobal(resultBuffer);
        }
    }
}

using System.Runtime.InteropServices;

namespace ChronoLoad.Sensors.Native;

/// <summary>
/// <c>NtQuerySystemInformation</c> 으로 전 프로세스를 <b>한 번의 호출</b>로 읽는다.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="System.Diagnostics.Process"/> 로 훑으면 프로세스마다 핸들을 열고 닫는다.
/// 수백 개면 그 자체로 눈에 띄는 부하가 되고, 접근 거부된 프로세스에서 예외까지 난다.
/// 이 API 는 커널이 이미 들고 있는 표를 한 번에 복사해 준다.
/// </para>
/// <para>
/// 문서화되지 않은 API 라는 점은 감수한다 — 대안이 프로세스당 핸들 열기뿐이고,
/// 구조체 배치는 NT4 이후 바뀐 적이 없다. 실패하면 조용히 빈 목록을 돌려준다.
/// </para>
/// </remarks>
internal static partial class NtProcess
{
    private const int SystemProcessInformation = 5;
    private const uint StatusInfoLengthMismatch = 0xC0000004;

    [LibraryImport("ntdll.dll", EntryPoint = "NtQuerySystemInformation")]
    private static partial uint QuerySystemInformation(
        int systemInformationClass, nint buffer, uint length, out uint returnLength);

    /// <summary>프로세스 한 줄의 원시 값. 시간은 100ns 단위 누적이다.</summary>
    public readonly record struct Row(
        int Pid,
        int ParentPid,
        string Name,
        long KernelTime100Ns,
        long UserTime100Ns,
        long WorkingSetBytes,
        long ReadBytes,
        long WriteBytes);

    /// <summary>
    /// 전 프로세스를 읽는다. 버퍼가 모자라면 커널이 알려준 크기로 늘려 다시 시도한다 —
    /// 그 사이에 프로세스가 생기면 또 모자랄 수 있으므로 몇 번 반복한다.
    /// </summary>
    public static List<Row> Enumerate()
    {
        var rows = new List<Row>(256);
        uint size = 512 * 1024;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            nint buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                uint rc = QuerySystemInformation(SystemProcessInformation, buffer, size, out uint needed);

                if (rc == StatusInfoLengthMismatch)
                {
                    size = Math.Max(needed + 64 * 1024, size * 2);
                    continue;
                }

                if (rc != 0) return rows;

                Parse(buffer, rows);
                return rows;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return rows;
    }

    // SYSTEM_PROCESS_INFORMATION (x64). 필요한 필드만 오프셋으로 읽는다 —
    // 전체를 선언하면 쓰지도 않는 30여 개 필드의 배치를 전부 맞춰야 한다.
    //   0x00 NextEntryOffset      0x04 NumberOfThreads     0x08 WorkingSetPrivateSize
    //   0x10 HardFaultCount        0x14 ThreadsHighWatermark 0x18 CycleTime
    //   0x20 CreateTime            0x28 UserTime             0x30 KernelTime
    //   0x38 ImageName(UNICODE_STRING: Length 0x38, Buffer 0x40)
    //   0x48 BasePriority          0x50 UniqueProcessId      0x58 InheritedFromUniqueProcessId
    //   0x60 HandleCount           0x64 SessionId            0x68 UniqueProcessKey
    //   0x70 PeakVirtualSize       0x78 VirtualSize          0x80 PageFaultCount
    //   0x88 PeakWorkingSetSize    0x90 WorkingSetSize       0x98‥0xC8 각종 Quota/Pagefile
    //   0xD0 ReadOperationCount    0xD8 WriteOperationCount  0xE0 OtherOperationCount
    //   0xE8 ReadTransferCount     0xF0 WriteTransferCount   0xF8 OtherTransferCount
    private const int NextEntryOffset = 0x000;
    private const int UserTime = 0x028;
    private const int KernelTime = 0x030;
    private const int ImageNameLength = 0x038;      // UNICODE_STRING.Length (ushort)
    private const int ImageNameBuffer = 0x040;      // UNICODE_STRING.Buffer (pointer)
    private const int UniqueProcessId = 0x050;
    private const int InheritedFromUniqueProcessId = 0x058;
    private const int WorkingSetSize = 0x090;
    private const int ReadTransferCount = 0x0E8;
    private const int WriteTransferCount = 0x0F0;

    private static void Parse(nint buffer, List<Row> rows)
    {
        nint entry = buffer;

        while (true)
        {
            int next = Marshal.ReadInt32(entry, NextEntryOffset);

            int pid = (int)Marshal.ReadIntPtr(entry, UniqueProcessId);
            ushort nameLength = (ushort)Marshal.ReadInt16(entry, ImageNameLength);
            nint namePtr = Marshal.ReadIntPtr(entry, ImageNameBuffer);

            string name = namePtr != 0 && nameLength > 0
                ? Marshal.PtrToStringUni(namePtr, nameLength / 2) ?? string.Empty
                // 이름 없는 항목은 PID 0(Idle)뿐이다.
                : pid == 0 ? "System Idle Process" : string.Empty;

            rows.Add(new Row(
                Pid: pid,
                ParentPid: (int)Marshal.ReadIntPtr(entry, InheritedFromUniqueProcessId),
                Name: name,
                KernelTime100Ns: Marshal.ReadInt64(entry, KernelTime),
                UserTime100Ns: Marshal.ReadInt64(entry, UserTime),
                WorkingSetBytes: Marshal.ReadInt64(entry, WorkingSetSize),
                ReadBytes: Marshal.ReadInt64(entry, ReadTransferCount),
                WriteBytes: Marshal.ReadInt64(entry, WriteTransferCount)));

            if (next == 0) break;
            entry += next;
        }
    }
}

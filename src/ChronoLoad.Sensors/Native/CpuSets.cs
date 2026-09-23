using System.Runtime.InteropServices;

namespace ChronoLoad.Sensors.Native;

/// <summary>
/// 논리 프로세서의 <b>효율 등급</b>. 하이브리드 CPU 에서 P 코어와 E 코어를 가른다.
/// </summary>
/// <remarks>
/// <para>
/// <c>GetSystemCpuSetInformation</c> 이 논리 프로세서마다 <c>EfficiencyClass</c> 를 준다.
/// <b>값이 클수록 성능 지향</b>이고 0 이 가장 효율 지향이다. 등급의 개수와 의미는 제조사가
/// 정하므로 "1이면 P코어"처럼 고정해 읽으면 안 된다 — 등급끼리 비교만 한다.
/// </para>
/// <para>
/// 실측(Core Ultra 5 226V): 논리 0~3 이 등급 1, 4~7 이 등급 0. 코어별 사용률에서도
/// 앞 넷이 높고 뒤 넷이 낮게 나와 실제 부하 분포와 맞는다.
/// </para>
/// <para>
/// 이 정보는 부팅 후 바뀌지 않으므로 한 번만 읽는다.
/// </para>
/// </remarks>
internal static partial class CpuSets
{
    /// <summary><c>CpuSetInformation</c> — 지금은 이 종류 하나뿐이다.</summary>
    private const int TypeCpuSet = 0;

    // SYSTEM_CPU_SET_INFORMATION 안에서 필요한 값의 오프셋.
    private const int OffsetSize = 0;
    private const int OffsetType = 4;
    private const int OffsetGroup = 12;
    private const int OffsetLogicalProcessorIndex = 14;
    private const int OffsetEfficiencyClass = 18;

    [LibraryImport("kernel32.dll", EntryPoint = "GetSystemCpuSetInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemCpuSetInformation(
        nint information, uint bufferLength, out uint returnedLength, nint process, uint flags);

    /// <summary>
    /// (그룹, 논리 프로세서 번호) → 효율 등급. 읽지 못하면 빈 사전.
    /// </summary>
    public static IReadOnlyDictionary<(ushort Group, byte Processor), byte> EfficiencyClasses()
    {
        var map = new Dictionary<(ushort, byte), byte>();

        // 필요한 크기를 먼저 묻는다. 실패해도 예외로 만들지 않는다 —
        // 이 값이 없다고 코어 막대를 못 그릴 이유는 없다. 등급 구분만 사라진다.
        GetSystemCpuSetInformation(0, 0, out uint needed, 0, 0);
        if (needed == 0) return map;

        nint buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!GetSystemCpuSetInformation(buffer, needed, out uint written, 0, 0) || written == 0)
                return map;

            int offset = 0;
            while (offset + OffsetEfficiencyClass < written)
            {
                int size = Marshal.ReadInt32(buffer, offset + OffsetSize);
                if (size <= 0) break;                      // 크기가 0이면 무한 루프가 된다

                if (Marshal.ReadInt32(buffer, offset + OffsetType) == TypeCpuSet)
                {
                    ushort group = (ushort)Marshal.ReadInt16(buffer, offset + OffsetGroup);
                    byte processor = Marshal.ReadByte(buffer, offset + OffsetLogicalProcessorIndex);
                    map[(group, processor)] = Marshal.ReadByte(buffer, offset + OffsetEfficiencyClass);
                }

                offset += size;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return map;
    }
}

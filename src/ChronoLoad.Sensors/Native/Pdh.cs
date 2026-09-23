using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ChronoLoad.Sensors.Native;

[StructLayout(LayoutKind.Explicit)]
internal struct PdhFmtCounterValue
{
    [FieldOffset(0)] public uint CStatus;
    [FieldOffset(8)] public double DoubleValue;
}

internal static partial class PdhNative
{
    public const uint ErrorSuccess = 0;
    public const uint PdhFmtDouble = 0x00000200;

    /// <summary>100%를 넘는 값을 자르지 않는다. <c>% Processor Utility</c>는 터보 상태에서 100을 넘는다.</summary>
    public const uint PdhFmtNoCap100 = 0x00008000;

    public const uint PdhInvalidData = 0xC0000BC6;
    public const uint PdhCalcNegativeDenominator = 0x800007D6;
    public const uint PdhCalcNegativeValue = 0x800007D8;
    public const uint PdhCalcNegativeTimebase = 0x800007D7;

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhOpenQueryW(string? dataSource, nuint userData, out nint query);

    /// <summary>
    /// 로캘 독립. 한국어 Windows에서도 영문 카운터 경로가 그대로 통하므로
    /// 카운터 인덱스를 역조회할 필요가 없다.
    /// </summary>
    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhAddEnglishCounterW(nint query, string counterPath, nuint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out PdhFmtCounterValue value);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCloseQuery(nint query);
}

/// <summary>PDH 쿼리 하나. 카운터를 여러 개 붙여도 <see cref="Collect"/> 한 번이면 전부 갱신된다.</summary>
internal sealed class PdhQuery : IDisposable
{
    private nint _query;
    private bool _collectedOnce;

    private PdhQuery(nint query) => _query = query;

    public bool IsValid => _query != 0;

    /// <summary>PDH를 쓸 수 없는 환경(카운터 손상 등)이면 null.</summary>
    public static PdhQuery? TryOpen()
    {
        uint rc = PdhNative.PdhOpenQueryW(null, 0, out nint handle);
        if (rc != PdhNative.ErrorSuccess)
        {
            SensorLog.Write($"PdhOpenQuery 실패 0x{rc:X8}");
            return null;
        }
        return new PdhQuery(handle);
    }

    /// <summary>카운터가 없는 시스템이면 null. 호출자가 폴백 경로를 고르게 한다.</summary>
    public PdhCounter? TryAddCounter(string path)
    {
        if (!IsValid) return null;
        uint rc = PdhNative.PdhAddEnglishCounterW(_query, path, 0, out nint counter);
        if (rc != PdhNative.ErrorSuccess)
        {
            SensorLog.Write($"카운터 추가 실패 0x{rc:X8} — {path}");
            return null;
        }
        return new PdhCounter(counter, path);
    }

    /// <summary>
    /// 델타 기반 카운터는 두 번 수집해야 값이 나온다. 첫 수집 뒤의 조회는 자연히 NaN이 된다.
    /// </summary>
    public bool Collect()
    {
        if (!IsValid) return false;
        uint rc = PdhNative.PdhCollectQueryData(_query);
        if (rc != PdhNative.ErrorSuccess) return false;
        _collectedOnce = true;
        return true;
    }

    public bool HasBaseline => _collectedOnce;

    public void Dispose()
    {
        if (_query != 0)
        {
            PdhNative.PdhCloseQuery(_query);
            _query = 0;
        }
    }
}

internal sealed class PdhCounter(nint handle, string path)
{
    public string Path { get; } = path;

    /// <summary>와일드카드 배열 읽기용. 같은 핸들을 <see cref="PdhCounterArray"/>가 다시 쓴다.</summary>
    public nint RawHandle => handle;

    /// <summary>값을 못 얻으면 NaN. 첫 수집 직후·일시적 음수 델타에서 정상적으로 발생한다.</summary>
    public double Read(bool noCap100 = false)
    {
        uint format = PdhNative.PdhFmtDouble | (noCap100 ? PdhNative.PdhFmtNoCap100 : 0);
        uint rc = PdhNative.PdhGetFormattedCounterValue(handle, format, out _, out var value);

        if (rc != PdhNative.ErrorSuccess) return double.NaN;
        if (value.CStatus is not (0 or 1)) return double.NaN;   // VALID_DATA / NEW_DATA 만 신뢰
        return value.DoubleValue;
    }
}

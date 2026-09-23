using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ChronoLoad.Sensors.Native;

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PdhFmtCounterValueItem
{
    [FieldOffset(0)] public nint NamePtr;
    [FieldOffset(8)] public uint CStatus;
    [FieldOffset(16)] public double Value;
}

internal static partial class PdhArrayNative
{
    public const uint PdhMoreData = 0x800007D2;

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    public static partial uint PdhGetFormattedCounterArray(
        nint counter, uint format, ref uint bufferSize, out uint itemCount, nint itemBuffer);
}

/// <summary>
/// 와일드카드 카운터 하나를 열어 전 인스턴스를 <b>한 번의 호출로</b> 읽는다.
/// <c>GPU Engine(*)</c> 은 인스턴스가 수백 개까지 늘어나는데, 인스턴스마다 카운터를 붙이면
/// 핸들 수도 호출 횟수도 같이 늘어난다.
/// </summary>
internal sealed class PdhCounterArray : IDisposable
{
    private readonly nint _counter;
    private nint _buffer;
    private uint _bufferSize;

    public PdhCounterArray(nint counter, string path)
    {
        _counter = counter;
        Path = path;
    }

    public string Path { get; }

    /// <summary>직전 <see cref="Read"/>가 채운 인스턴스 수.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// 전 인스턴스를 읽어 콜백에 흘린다. 이름 문자열은 PDH 버퍼를 가리키므로
    /// 콜백 밖으로 들고 나가면 안 된다 — 필요하면 콜백 안에서 복사한다.
    /// </summary>
    public void Read(Action<string, double> onItem, bool noCap100 = false)
    {
        uint format = PdhNative.PdhFmtDouble | (noCap100 ? PdhNative.PdhFmtNoCap100 : 0);

        uint needed = _bufferSize;
        uint rc = PdhArrayNative.PdhGetFormattedCounterArray(_counter, format, ref needed, out uint items, _buffer);

        if (rc == PdhArrayNative.PdhMoreData)
        {
            Grow(needed);
            needed = _bufferSize;
            rc = PdhArrayNative.PdhGetFormattedCounterArray(_counter, format, ref needed, out items, _buffer);
        }

        if (rc != PdhNative.ErrorSuccess || _buffer == 0)
        {
            Count = 0;
            SensorLog.Write($"배열 읽기 실패 0x{rc:X8} — {Path}");
            return;
        }

        Count = (int)items;
        int stride = Marshal.SizeOf<PdhFmtCounterValueItem>();

        for (int i = 0; i < items; i++)
        {
            var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(_buffer + i * stride);
            if (item.NamePtr == 0) continue;
            if (item.CStatus is not (0 or 1)) continue;

            string? name = Marshal.PtrToStringUni(item.NamePtr);
            if (name is not null) onItem(name, item.Value);
        }
    }

    private void Grow(uint needed)
    {
        if (_buffer != 0) Marshal.FreeHGlobal(_buffer);
        _bufferSize = Math.Max(needed, 4096);
        _buffer = Marshal.AllocHGlobal((int)_bufferSize);
    }

    public void Dispose()
    {
        if (_buffer != 0)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = 0;
        }
    }
}

internal static class PdhQueryArrayExtensions
{
    /// <summary>와일드카드 경로를 붙인다. 카운터가 없는 시스템이면 null.</summary>
    public static PdhCounterArray? TryAddArray(this PdhQuery query, string path)
    {
        var counter = query.TryAddCounter(path);
        if (counter is null) return null;

        // PdhCounter 는 핸들을 숨기고 있으므로 배열용으로 다시 붙인다.
        var handle = counter.RawHandle;
        Debug.Assert(handle != 0);
        return new PdhCounterArray(handle, path);
    }
}

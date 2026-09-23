using System.Runtime.CompilerServices;

namespace ChronoLoad.Core.Metrics;

/// <summary>
/// 고정 용량 링 버퍼. <b>단일 생산자(샘플 스레드) / 다중 소비자(UI · MCP)</b>를 전제로 하며 lock을 쓰지 않는다.
/// 쓰기는 값을 먼저 배열에 넣고 카운터를 <see cref="Volatile"/>로 공개하므로, 소비자는 공개된 카운터보다
/// 오래된 항목만 읽는 한 일관된 값을 본다.
/// </summary>
/// <remarks>
/// 샘플마다 <b>실측 여부</b>를 1비트로 함께 보관한다. Slow 티어 지표는 Fast 틱마다 직전 값이 다시 기록되는데
/// (시간 축을 맞추기 위해 일부러 그렇게 한다), 그 반복값을 실측과 구분하지 않으면 통계가 왜곡된다.
/// </remarks>
public sealed class MetricSeries
{
    private readonly float[] _buffer;
    private readonly ulong[] _measured;
    private long _written;
    private long _lastMeasuredIndex = -1;

    public MetricSeries(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        _buffer = new float[capacity];
        _measured = new ulong[(capacity + 63) / 64];
        Array.Fill(_buffer, float.NaN);
    }

    public int Capacity => _buffer.Length;

    /// <summary>시작 이후 기록된 총 샘플 수(랩어라운드 포함).</summary>
    public long Written => Volatile.Read(ref _written);

    /// <summary>현재 버퍼에 남아 있는 유효 샘플 수.</summary>
    public int Count => (int)Math.Min(Written, _buffer.Length);

    /// <summary>가장 최근 값. 아직 아무것도 없으면 NaN. 실측값일 수도, 유지된 값일 수도 있다.</summary>
    public float Latest
    {
        get
        {
            long w = Volatile.Read(ref _written);
            return w == 0 ? float.NaN : _buffer[(int)((w - 1) % _buffer.Length)];
        }
    }

    /// <summary>가장 최근 샘플이 이번 주기에 실제로 측정된 값인지.</summary>
    public bool LatestMeasured
    {
        get
        {
            long w = Volatile.Read(ref _written);
            return w != 0 && GetMeasuredBit((int)((w - 1) % _buffer.Length));
        }
    }

    /// <summary>
    /// 마지막 실측 이후 흘러간 샘플 수. 0이면 방금 측정됐다는 뜻이고,
    /// Slow 티어(1000ms/250ms)라면 정상 범위가 0~3이다. 이 값으로 <c>stale</c>을 판정한다.
    /// </summary>
    public int SamplesSinceMeasurement
    {
        get
        {
            long w = Volatile.Read(ref _written);
            long last = Volatile.Read(ref _lastMeasuredIndex);
            return last < 0 ? int.MaxValue : (int)Math.Min(int.MaxValue, w - 1 - last);
        }
    }

    /// <summary>샘플 스레드 전용. 다른 스레드에서 호출하면 안 된다.</summary>
    /// <param name="measured">
    /// 이번 틱에 센서가 실제로 읽은 값이면 true. Slow 티어에서 직전 값을 그대로 유지하는 경우 false.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(float value, bool measured = true)
    {
        long w = _written;
        int slot = (int)(w % _buffer.Length);
        _buffer[slot] = value;
        SetMeasuredBit(slot, measured);
        if (measured) Volatile.Write(ref _lastMeasuredIndex, w);
        Volatile.Write(ref _written, w + 1);
    }

    /// <summary>
    /// 가장 최근 <paramref name="destination"/>.Length 개를 시간 순(오래된 것 먼저)으로 복사하고
    /// 실제 복사한 개수를 돌려준다. 값은 항상 destination[0..n) 에 들어간다.
    /// </summary>
    /// <remarks>
    /// 복사 도중 생산자가 버퍼를 한 바퀴 돌면 앞쪽 값이 찢어질 수 있다. 그 경우를 감지해 최대 3회 재시도하고,
    /// 그래도 실패하면 최신 값 위주로 돌려준다. 모니터링 용도에서 이 이상의 동기화 비용은 정당화되지 않는다.
    /// </remarks>
    public int CopyLatest(Span<float> destination) => CopyLatest(destination, Span<bool>.Empty);

    /// <param name="measuredDestination">
    /// 비어 있지 않으면 각 샘플의 실측 여부를 함께 채운다. 길이는 <paramref name="destination"/>과 같아야 한다.
    /// </param>
    public int CopyLatest(Span<float> destination, Span<bool> measuredDestination)
    {
        if (destination.IsEmpty) return 0;
        bool wantMeasured = !measuredDestination.IsEmpty;
        if (wantMeasured && measuredDestination.Length < destination.Length)
            throw new ArgumentException("실측 여부 버퍼가 값 버퍼보다 짧다.", nameof(measuredDestination));

        int cap = _buffer.Length;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            long before = Volatile.Read(ref _written);
            int available = (int)Math.Min(before, cap);
            int n = Math.Min(available, destination.Length);
            if (n == 0) return 0;

            long start = before - n;
            for (int i = 0; i < n; i++)
            {
                int slot = (int)((start + i) % cap);
                destination[i] = _buffer[slot];
                if (wantMeasured) measuredDestination[i] = GetMeasuredBit(slot);
            }

            // 생산자가 우리가 읽은 가장 오래된 항목을 아직 덮어쓰지 않았으면 일관된 스냅샷이다.
            long after = Volatile.Read(ref _written);
            if (after - start <= cap) return n;
        }

        // 최후 수단: 최신 한 개라도 정확히 준다.
        destination[0] = Latest;
        if (wantMeasured) measuredDestination[0] = LatestMeasured;
        return 1;
    }

    /// <summary>테스트·진단용. 인덱스 0이 가장 오래된 유효 샘플.</summary>
    public float this[int index] => _buffer[SlotOf(index)];

    /// <summary>테스트·진단용. 인덱스 0이 가장 오래된 유효 샘플.</summary>
    public bool IsMeasured(int index) => GetMeasuredBit(SlotOf(index));

    private int SlotOf(int index)
    {
        int count = Count;
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);
        long start = Written - count;
        return (int)((start + index) % _buffer.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool GetMeasuredBit(int slot) => (_measured[slot >> 6] & (1UL << (slot & 63))) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetMeasuredBit(int slot, bool value)
    {
        ulong mask = 1UL << (slot & 63);
        if (value) _measured[slot >> 6] |= mask;
        else _measured[slot >> 6] &= ~mask;
    }

    /// <summary>
    /// min-max 데시메이션. 구간마다 최솟값·최댓값을 <b>발생 순서대로</b> 남겨 스파이크가 사라지지 않게 한다.
    /// 단순 샘플링이나 평균은 짧은 피크를 지워버리므로 쓰지 않는다.
    /// </summary>
    /// <returns>destination에 채운 개수.</returns>
    public static int Decimate(ReadOnlySpan<float> source, Span<float> destination)
    {
        if (destination.IsEmpty) return 0;
        if (source.Length <= destination.Length)
        {
            source.CopyTo(destination);
            return source.Length;
        }

        // 한 버킷당 2개(min, max)를 내보내므로 버킷 수는 목표 길이의 절반.
        int buckets = Math.Max(1, destination.Length / 2);
        int outIndex = 0;

        for (int b = 0; b < buckets && outIndex < destination.Length; b++)
        {
            int from = (int)((long)b * source.Length / buckets);
            int to = (int)((long)(b + 1) * source.Length / buckets);
            if (to <= from) to = Math.Min(from + 1, source.Length);

            int minIdx = -1, maxIdx = -1;
            for (int i = from; i < to; i++)
            {
                float v = source[i];
                if (float.IsNaN(v)) continue;
                if (minIdx < 0 || v < source[minIdx]) minIdx = i;
                if (maxIdx < 0 || v > source[maxIdx]) maxIdx = i;
            }

            if (minIdx < 0)
            {
                destination[outIndex++] = float.NaN;
                if (outIndex < destination.Length) destination[outIndex++] = float.NaN;
                continue;
            }

            int first = Math.Min(minIdx, maxIdx), second = Math.Max(minIdx, maxIdx);
            destination[outIndex++] = source[first];
            if (second != first && outIndex < destination.Length)
                destination[outIndex++] = source[second];
        }

        return outIndex;
    }
}

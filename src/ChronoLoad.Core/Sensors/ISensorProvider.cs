using ChronoLoad.Core.Metrics;

namespace ChronoLoad.Core.Sensors;

/// <summary>
/// 샘플 주기 등급. 비싼 호출을 느린 티어로 내려 잦은 갱신과 저부하를 동시에 만족시킨다.
/// </summary>
public enum SensorTier : byte
{
    /// <summary>250ms. CPU 총합, 물리 메모리, 인터페이스 B/s, 디스크 처리량, GPU 사용률(네이티브 경로).</summary>
    Fast = 0,

    /// <summary>1000ms. GPU 엔진 PDH 와일드카드, 코어별 CPU, 온도·전력·클럭, 디스크 큐·응답.</summary>
    Slow = 1,

    /// <summary>2000ms. 프로세스 테이블. MCP가 구독 중일 때만 돈다.</summary>
    Lazy = 2,
}

/// <summary>
/// 프로바이더가 한 틱에 값을 쓰는 창구. 할당이 생기지 않도록 <c>ref struct</c>로 두고
/// 미리 잡아둔 버퍼의 슬롯 인덱스에 직접 쓴다.
/// </summary>
public readonly ref struct SampleWriter
{
    private readonly Span<float> _slots;
    private readonly Span<bool> _measured;

    internal SampleWriter(Span<float> slots, Span<bool> measured, long timestampUtcTicks, double elapsedSeconds)
    {
        _slots = slots;
        _measured = measured;
        TimestampUtcTicks = timestampUtcTicks;
        ElapsedSeconds = elapsedSeconds;
    }

    public long TimestampUtcTicks { get; }

    /// <summary>
    /// 이 프로바이더가 마지막으로 샘플링된 뒤 실제로 흐른 시간. 틱 지터가 있으므로
    /// 델타 기반 카운터(B/s 등)는 반드시 이 값으로 나눠야 한다.
    /// </summary>
    public double ElapsedSeconds { get; }

    /// <summary>
    /// 값을 쓰고 <b>이번 틱에 실제로 측정했다</b>고 표시한다. 표시하지 않은 슬롯은 직전 값이 유지되며
    /// 통계에는 반영되지 않는다 — 그래야 Slow 티어 지표의 샘플 수와 편차가 부풀지 않는다.
    /// </summary>
    public void Write(int slot, float value)
    {
        if ((uint)slot >= (uint)_slots.Length) return;
        _slots[slot] = value;
        if ((uint)slot < (uint)_measured.Length) _measured[slot] = true;
    }

    /// <summary>
    /// 이번 주기에 값을 얻지 못했음을 표시한다. 차트에는 공백으로 남고 통계에서는 제외된다.
    /// "읽어봤지만 값이 없었다"는 것도 관측이므로 실측으로 표시한다 — 유지된 값과 구분해야 한다.
    /// </summary>
    public void WriteUnavailable(int slot) => Write(slot, float.NaN);
}

/// <summary>
/// 센서 하나. 장치 하나당 인스턴스 하나이며, 장치가 사라지면 <see cref="IDisposable.Dispose"/> 된다.
/// </summary>
public interface ISensorProvider : IDisposable
{
    /// <summary>진단·로그용 식별자. 보통 "cpu", "gpu:LUID", "disk:SERIAL", "net:GUID".</summary>
    string Id { get; }

    SensorTier Tier { get; }

    /// <summary>초기화에 실패했거나 드라이버가 사라지면 false. 엔진이 건너뛴다.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// 레지스트리에 장치와 지표를 등록하고 슬롯 번호를 받아둔다.
    /// 실패하면 예외를 던지지 말고 <see cref="IsAvailable"/>을 false로 두는 편이 낫다 —
    /// 센서 하나가 죽어도 나머지는 계속 돌아야 한다.
    /// </summary>
    ValueTask InitializeAsync(MetricRegistry registry, CancellationToken cancellationToken);

    /// <summary>할당 없이 슬롯에 값을 쓴다. 이 메서드 안에서 블로킹 I/O를 하면 안 된다.</summary>
    void Sample(in SampleWriter writer);

    /// <summary>
    /// 장치 집합이 바뀌었을 수 있으니 다시 세라는 요청 (§5.7).
    /// </summary>
    /// <remarks>
    /// 여기서 곧바로 열거하지 말고 <b>표시만 남긴다</b>. 이 호출은 장치 이벤트 스레드에서 오는데,
    /// PDH 와일드카드 열거는 수 ms가 걸려 샘플링 스레드와 경쟁하면 틱이 밀린다.
    /// 실제 열거는 다음 <see cref="Sample"/>에서 한다.
    /// 장치 집합이 고정인 프로바이더(CPU·메모리)는 아무것도 하지 않으면 된다.
    /// </remarks>
    void RequestEnumerate() { }
}

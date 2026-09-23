namespace ChronoLoad.Core.Layout;

/// <summary>
/// GPU 메모리 차트의 세로축 상한을 정한다 (§8.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>GPU 메모리는 파형이 아니라 점유량이다.</b> 사용률처럼 순간의 오르내림을 보는 지표가 아니라
/// "얼마나 잡고 있고 얼마나 남았는가"를 보는 지표다. 축을 최고치에 맞춰 움직이면 그 둘 다
/// 읽을 수 없다 — 1 GiB 를 쓰든 7 GiB 를 쓰든 영역이 같은 높이로 그려지기 때문이다.
/// 그래서 축을 <b>용량</b>에 고정한다.
/// </para>
/// <para>
/// 렌더러가 아니라 여기에 두는 이유는 이것이 그리기 방식이 아니라 <b>판단</b>이기 때문이다.
/// 화면 없이 테스트할 수 있어야 하고, 외장 GPU 가 없는 기기에서도 외장 규칙을 검증할 수 있어야 한다.
/// </para>
/// </remarks>
public static class GpuMemoryAxis
{
    /// <summary>
    /// 넘쳤을 때 꼭대기가 축 상단에 붙지 않도록 주는 여유. 이만큼은 있어야 누적 영역의
    /// 윗변과 그 위 여백이 구분된다.
    /// </summary>
    private const double SpillHeadroom = 1.08;

    /// <summary>용량을 알 수 없는 어댑터에만 쓰는 예전 방식의 여유.</summary>
    private const double UnknownHeadroom = 1.15;

    /// <summary>
    /// "얼마나 찼는가"를 셀 때의 분모. 외장은 전용 VRAM, 내장은 공유 한도다.
    /// </summary>
    /// <remarks>
    /// 축 상한과 <b>같은 기준</b>이어야 한다. 헤더의 <c>12/22G</c>, 접힌 카드의 미터,
    /// 펼친 차트의 용량선이 서로 다른 분모를 쓰면 같은 카드 안에서 숫자가 어긋난다.
    /// 모르면 0 — 분모가 없으면 비율을 말할 수 없고, 0 을 채워 넣으면 거짓이 된다.
    /// </remarks>
    public static double CapacityReference(bool discrete, double dedicatedCapacity, double sharedCapacity)
    {
        if (discrete && dedicatedCapacity > 0) return dedicatedCapacity;
        if (!discrete && sharedCapacity > 0) return sharedCapacity;
        return 0;
    }

    /// <param name="discrete">외장 GPU 인가. 외장만 전용 VRAM 을 기준선으로 갖는다.</param>
    /// <param name="dedicatedCapacity">전용 VRAM 용량(바이트). 모르면 0.</param>
    /// <param name="sharedCapacity">공유 메모리 한도(바이트). 모르면 0.</param>
    /// <param name="peakTotal">표시 창 안의 전용+공유 최고치(바이트).</param>
    public static double Max(bool discrete, double dedicatedCapacity, double sharedCapacity, double peakTotal)
    {
        if (peakTotal < 0 || double.IsNaN(peakTotal)) peakTotal = 0;

        // 외장 — 전용 용량이 바닥선이다. 평소에는 축 상단이 곧 전용 용량이라 채움 높이가
        // 그대로 점유율이 되고, 넘칠 때만 넘친 만큼 늘어난다.
        //
        // 여유(SpillHeadroom)는 <b>넘친 뒤에만</b> 붙인다. 최고치에 그냥 곱하면 용량의 99% 를
        // 쓰는 것만으로 축이 늘어나서(11.9 × 1.08 > 12) 아직 넘기지도 않았는데 용량선이
        // 아래로 내려온다 — "선에 닿았다"를 읽을 수 없게 된다.
        if (discrete && dedicatedCapacity > 0)
            return peakTotal > dedicatedCapacity ? peakTotal * SpillHeadroom : dedicatedCapacity;

        // 내장 — 공유 한도로 완전히 고정한다. 전용 VRAM 이 없어 비교 기준이 공유 한도뿐이고,
        // 이쪽은 넘칠 수가 없으므로 축이 움직일 이유도 없다.
        if (!discrete && sharedCapacity > 0)
            return sharedCapacity;

        // 용량을 모르는 어댑터. 기준이 없으니 최고치를 따라가는 수밖에 없다.
        return Math.Max(peakTotal * UnknownHeadroom, 1);
    }
}

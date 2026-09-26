namespace ChronoLoad.Core.Layout;

/// <summary>
/// 시간 축(§7.4)에서 <b>수집이 끊긴 자리</b>를 찾는다.
/// </summary>
/// <remarks>
/// <para>
/// 절전·최대 절전에서는 샘플링 스레드가 돌지 않으므로 그 동안의 샘플이 <b>아예 없다</b>.
/// 링 버퍼에는 14:00 샘플 바로 옆에 16:00 샘플이 붙고, 차트는 둘을 선으로 이어 그린다 —
/// 두 시간 동안 값이 그렇게 이어졌다고 말하는 셈이다. <b>공백은 폭이 아니라 이음매</b>다.
/// </para>
/// <para>
/// 판정은 <b>공칭</b> Fast 주기를 기준으로 한다. 적응형 백오프(§6.3)로 느려진 것은 공백이
/// 아니라 정상적인 감속이므로, 가장 느린 배속(×4)에서도 걸리지 않게 여유를 둔다.
/// </para>
/// </remarks>
public static class SampleGaps
{
    /// <summary>
    /// 공칭 Fast 주기의 몇 배부터 공백으로 볼 것인가. 가장 느린 배속이 ×4 이므로
    /// ×8 이면 정상 감속과 두 배 차이가 난다 — 250ms 기준 2초다.
    /// </summary>
    public const int DefaultFactor = 8;

    /// <param name="nominalFastPeriod">
    /// <b>공칭</b> Fast 주기. 현재 배속이 반영된 값을 넘기면 안 된다 — 느려진 상태에서
    /// 문턱까지 같이 느슨해져 정작 절전 복귀를 놓친다.
    /// </param>
    public static long ThresholdFor(TimeSpan nominalFastPeriod) =>
        nominalFastPeriod.Ticks * DefaultFactor;

    /// <summary>
    /// 인접한 두 시각의 간격이 <paramref name="thresholdTicks"/>를 넘는 자리를 표시한다.
    /// </summary>
    /// <param name="stamps">표시할 구간의 커밋 시각(오래된 것 먼저).</param>
    /// <param name="mask">
    /// <c>mask[i]</c>가 참이면 <b>i 는 i−1 에서 이어지지 않는다</b>. 0번은 앞이 없으므로 늘 거짓이다.
    /// <paramref name="stamps"/>와 같은 길이여야 한다.
    /// </param>
    /// <returns>찾은 공백 수.</returns>
    public static int Mark(ReadOnlySpan<long> stamps, long thresholdTicks, Span<bool> mask)
    {
        if (mask.Length < stamps.Length)
            throw new ArgumentException("표시 버퍼가 시각 버퍼보다 짧다.", nameof(mask));

        mask[..stamps.Length].Clear();
        if (thresholdTicks <= 0 || stamps.Length < 2) return 0;

        int found = 0;
        for (int i = 1; i < stamps.Length; i++)
        {
            // 시각이 거꾸로 가는 일은 없어야 하지만, 그때도 "이어진다"고 말하지는 않는다.
            long delta = stamps[i] - stamps[i - 1];
            if (delta <= thresholdTicks && delta >= 0) continue;
            mask[i] = true;
            found++;
        }

        return found;
    }
}

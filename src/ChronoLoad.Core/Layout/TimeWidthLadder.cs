namespace ChronoLoad.Core.Layout;

/// <summary>
/// 메인 창이 한 번에 보여 주는 시간 폭. <c>Ctrl</c>+휠이 한 칸씩 오르내리고,
/// 차트 더블클릭이 <see cref="Standard"/>로 되돌린다(§9.4).
/// </summary>
/// <remarks>
/// <para>
/// 초안은 더블클릭에 순환을 걸었다. 순환은 원하는 곳에 닿으려면 몇 번 눌러야 하는지 세야 하고,
/// 지나치면 한 바퀴를 더 돌아야 하며, 무엇보다 <b>돌아올 문이 없다</b> — 600초에 가 있다는 것을
/// 잊으면 화면이 왜 이런지 알 수 없다. 사다리와 복귀로 나누면 둘 다 풀린다.
/// </para>
/// <para>
/// 폭은 <b>점 개수가 아니라 초</b>로 센다. 적응형 백오프(§6.3)로 주기가 늘어난 구간에서는
/// 같은 점 개수가 더 긴 시간을 덮기 때문이다. 초 → 점 개수 환산은 시간 축(§7.4)이 맡는다.
/// </para>
/// </remarks>
public static class TimeWidthLadder
{
    /// <summary>오름차순. 맨 위는 링 버퍼 전체(250ms × 3600 = 15분)다.</summary>
    public static readonly TimeSpan[] Rungs =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(180),
        TimeSpan.FromSeconds(600),
        TimeSpan.FromSeconds(900),
    ];

    /// <summary>기본 폭. 더블클릭이 어디에 있든 여기로 돌아온다.</summary>
    public static TimeSpan Standard => Rungs[1];

    /// <summary>
    /// <paramref name="current"/>에서 <paramref name="direction"/>칸 이동한 폭.
    /// 사다리 밖으로는 나가지 않는다 — 끝에서 더 굴려도 제자리다.
    /// </summary>
    /// <param name="direction">양수는 더 긴 폭(축소), 음수는 더 짧은 폭(확대).</param>
    public static TimeSpan Step(TimeSpan current, int direction)
    {
        if (direction == 0) return Nearest(current);

        int index = IndexOfNearest(current);
        // 사다리 밖의 값에서 출발하면 먼저 가장 가까운 칸으로 붙인 뒤 움직인다.
        if (Rungs[index] != current) index += direction > 0 && current > Rungs[index] ? 1
                                            : direction < 0 && current < Rungs[index] ? -1 : 0;
        else index += direction > 0 ? 1 : -1;

        return Rungs[Math.Clamp(index, 0, Rungs.Length - 1)];
    }

    /// <summary>사다리에서 가장 가까운 칸. 사다리 밖의 값을 되돌려 붙일 때 쓴다.</summary>
    public static TimeSpan Nearest(TimeSpan value) => Rungs[IndexOfNearest(value)];

    /// <summary>표시용 이름. <c>30s</c> · <c>3m</c> 처럼 가장 짧게 적는다.</summary>
    public static string Label(TimeSpan value) =>
        value.TotalSeconds < 60 ? $"{value.TotalSeconds:0}s"
        : value.Ticks % TimeSpan.TicksPerMinute == 0 ? $"{value.TotalMinutes:0}m"
        : $"{value.TotalSeconds:0}s";

    private static int IndexOfNearest(TimeSpan value)
    {
        int best = 0;
        long bestDelta = long.MaxValue;
        for (int i = 0; i < Rungs.Length; i++)
        {
            long delta = Math.Abs(Rungs[i].Ticks - value.Ticks);
            if (delta >= bestDelta) continue;
            bestDelta = delta;
            best = i;
        }
        return best;
    }
}

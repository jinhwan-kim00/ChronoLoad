namespace ChronoLoad.Core.Layout;

/// <param name="Weight">같은 조건에서의 상대 높이. GPU는 1.5, 나머지는 1.0.</param>
/// <param name="Priority">공간이 모자랄 때 <b>누가 먼저 접히는가</b>만 결정한다. 낮을수록 먼저 접힌다.</param>
/// <param name="UserCollapsed">사용자나 프리셋이 접어둔 카드. 자동 복원 대상이 아니다.</param>
/// <param name="ExpandOrder">
/// 사용자가 이 카드를 직접 편 순서. 클수록 최근이고 0은 "직접 편 적 없음"이다.
/// <b>우선순위보다 먼저 본다</b> — 방금 연 카드가 그 자리에서 다시 접히면 카드가 열리지 않는다.
/// </param>
public readonly record struct CardSpec(
    string Key, double Weight, int Priority, bool UserCollapsed = false, long ExpandOrder = 0);

/// <param name="AutoCollapsed">공간이 모자라 강제로 접힌 카드. 점선 테두리로 구분하고 공간이 생기면 되돌린다.</param>
public readonly record struct CardLayout(string Key, double Height, bool Collapsed, bool AutoCollapsed);

/// <summary>
/// 카드 높이 분배. 장치 개수가 가변이므로 "고정 높이 카드를 쌓는다"는 방식은 성립하지 않는다.
/// 가중치로 나누고, 그래도 너무 납작해지면 우선순위가 낮은 카드부터 접는다. (설계서 §8.6)
/// </summary>
/// <remarks>
/// 순수 함수다. 창 높이가 애니메이션 중이면 <b>중간 높이로 호출하지 말 것</b> —
/// 자동 접힘이 과하게 발동해 카드가 전부 접힌다. 높이가 확정된 뒤(WPF <c>SizeChanged</c>) 호출한다.
/// </remarks>
public static class LayoutEngine
{
    public const double CollapsedHeight = 28;

    /// <summary>이보다 납작해지면 차트가 형태를 잃는다. 자동 접힘 발동선.</summary>
    public const double MinExpandedHeight = 118;

    public const double Gap = 8;

    public static IReadOnlyList<CardLayout> Compute(IReadOnlyList<CardSpec> cards, double availableHeight)
    {
        ArgumentNullException.ThrowIfNull(cards);
        if (cards.Count == 0) return [];

        double total = availableHeight - Gap * (cards.Count - 1);
        var collapsed = new bool[cards.Count];
        var auto = new bool[cards.Count];

        for (int i = 0; i < cards.Count; i++)
            collapsed[i] = cards[i].UserCollapsed;

        // 접을 수 있는 카드가 남아 있는 한, 가장 납작한 카드가 기준선을 넘을 때까지 접는다.
        while (true)
        {
            int expandedCount = 0;
            double weightSum = 0;
            double minWeight = double.MaxValue;

            for (int i = 0; i < cards.Count; i++)
            {
                if (collapsed[i]) continue;
                expandedCount++;
                weightSum += cards[i].Weight;
                minWeight = Math.Min(minWeight, cards[i].Weight);
            }

            if (expandedCount <= 1 || weightSum <= 0) break;

            double avail = total - CollapsedHeight * (cards.Count - expandedCount);
            double smallest = avail * minWeight / weightSum;
            if (smallest >= MinExpandedHeight) break;

            // 누가 자리를 내주는가.
            //
            // 1순위는 <b>사용자가 가장 오래 전에 편 카드</b>다. 우선순위만 보면 방금 연 카드가
            //   바로 그 자리(Wi-Fi 50 · 디스크 60)라 즉시 다시 접혀, 클릭해도 카드가 열리지 않는다.
            //   직접 누른 선택이 표에 적힌 기본값보다 앞선다.
            // 2순위가 우선순위 표다. 아무도 손대지 않은 동안에는(전부 0) 예전과 똑같이 동작한다.
            int victim = -1;
            for (int i = 0; i < cards.Count; i++)
            {
                if (collapsed[i]) continue;
                if (victim < 0 || IsWeakerThan(cards[i], cards[victim])) victim = i;
            }

            static bool IsWeakerThan(in CardSpec candidate, in CardSpec current) =>
                candidate.ExpandOrder != current.ExpandOrder
                    ? candidate.ExpandOrder < current.ExpandOrder
                    : candidate.Priority < current.Priority;

            if (victim < 0) break;
            collapsed[victim] = true;
            auto[victim] = true;
        }

        // 최종 배분
        double finalWeightSum = 0;
        int finalExpanded = 0;
        for (int i = 0; i < cards.Count; i++)
        {
            if (collapsed[i]) continue;
            finalWeightSum += cards[i].Weight;
            finalExpanded++;
        }

        double finalAvail = total - CollapsedHeight * (cards.Count - finalExpanded);
        var result = new CardLayout[cards.Count];

        for (int i = 0; i < cards.Count; i++)
        {
            if (collapsed[i])
            {
                result[i] = new CardLayout(cards[i].Key, CollapsedHeight, true, auto[i]);
                continue;
            }

            double height = finalWeightSum > 0
                ? Math.Max(0, finalAvail) * cards[i].Weight / finalWeightSum
                : Math.Max(0, finalAvail);
            result[i] = new CardLayout(cards[i].Key, height, false, false);
        }

        return result;
    }

    /// <summary>창 최소 높이(설계서 §9.1). 이보다 작으면 카드를 전부 접어도 쓸모가 없다.</summary>
    public const double MinWindowHeight = 420;

    /// <summary>
    /// 첫 실행 창 높이. 장치 구성이 기기마다 다르므로 고정값을 쓸 수 없다.
    /// </summary>
    /// <remarks>
    /// 상한만 작업 영역에 비례(85%)하고 <b>하한은 비례시키지 않는다</b>.
    /// 4K 모니터에서 장치가 적은 노트북이 "작업 영역의 60%"에 걸려 창이 1296px로 부풀면,
    /// 카드 3개가 400px씩 차지하는 우스운 레이아웃이 된다. 필요한 만큼만 쓰는 것이 맞다.
    /// </remarks>
    public static double SuggestWindowHeight(
        IReadOnlyList<CardSpec> cards,
        double chromeHeight = 76,
        double padding = 16,
        double workAreaHeight = 1080)
    {
        double content = 0;
        foreach (var card in cards)
            content += card.UserCollapsed ? CollapsedHeight : MinExpandedHeight * 1.1 * card.Weight;

        double desired = chromeHeight + padding + Gap * Math.Max(0, cards.Count - 1) + content;
        double max = Math.Max(MinWindowHeight, workAreaHeight * 0.85);
        return Math.Clamp(desired, MinWindowHeight, max);
    }
}

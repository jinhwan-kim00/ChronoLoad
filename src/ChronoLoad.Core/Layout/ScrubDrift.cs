namespace ChronoLoad.Core.Layout;

/// <summary>
/// 고정된 스크럽선이 시간이 흐를 때 어디로 가는가 (§8.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>클릭으로 고정하는 것은 자리가 아니라 순간이다.</b> "그때 GPU가 멈춘 순간 디스크는 뭘 했나"를
/// 보려고 세우는 선이므로, 새 샘플이 들어와 그래프가 왼쪽으로 흐르면 선도 같이 흘러야 한다.
/// 자리를 고정하면 선 아래의 순간이 매 틱 달라져서, 값을 읽어 적는 동안 대상이 바뀐다.
/// </para>
/// <para>
/// <b>맨 오른쪽만 예외다.</b> 가장 최근 샘플에 세운 선은 흘려보낼 것이 없다 — 거기 머물면서
/// 계속 현재값을 보여주는 것이 "지금을 본다"는 뜻과 맞는다. 이 자리는 흐르지 않는다.
/// </para>
/// <para>
/// 화면 없이 판정할 수 있어야 해서 렌더러가 아니라 여기에 둔다.
/// </para>
/// </remarks>
public static class ScrubDrift
{
    /// <summary>
    /// 새 샘플이 들어온 뒤의 스크럽 인덱스. 창 왼쪽 밖으로 밀려나면 <c>null</c> —
    /// 그 순간은 더 이상 화면에 없으므로 고정을 놓는 편이 맞다.
    /// </summary>
    /// <param name="index">지금 스크럽 인덱스(표시 창 안, 0 = 가장 왼쪽).</param>
    /// <param name="windowPoints">표시 창의 점 개수.</param>
    /// <param name="newSamples">이번에 들어온 샘플 수. 보통 1.</param>
    public static int? Advance(int index, int windowPoints, int newSamples = 1)
    {
        if (windowPoints <= 0 || newSamples <= 0) return index;

        // 맨 오른쪽에 세운 선은 현재값을 따라간다. 흘려보내면 "지금"을 보는 수단이 사라진다.
        if (index >= windowPoints - 1) return windowPoints - 1;

        int moved = index - newSamples;
        return moved < 0 ? null : moved;
    }
}

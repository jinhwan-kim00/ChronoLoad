using ChronoLoad.Core.Layout;

namespace ChronoLoad.App.ViewModels;

/// <summary>
/// 전 카드가 공유하는 스크럽 위치. 앱 전역에 단 하나만 존재한다.
/// </summary>
/// <remarks>
/// 모든 카드가 <b>동일한 시간 축</b>을 쓰기 때문에(샘플 엔진이 Slow 티어 값도 매 틱 기록한다)
/// 인덱스 하나가 곧 시스템 전체의 한 순간을 가리킨다. "GPU가 멈춘 그 순간 디스크는 뭘 했나"를
/// 커서 하나로 확인하게 하는 것이 이 클래스의 존재 이유다.
/// </remarks>
public sealed class ScrubState
{
    /// <summary>표시 창 안의 샘플 인덱스. null이면 비활성(현재 값 표시).</summary>
    public int? Index { get; private set; }

    /// <summary>클릭으로 고정된 상태. 커서가 떠나도 유지된다.</summary>
    public bool IsPinned { get; private set; }

    /// <summary>
    /// 맨 오른쪽(현재값)에 걸린 상태. 인덱스가 아니라 <b>상태</b>로 기억한다.
    /// </summary>
    /// <remarks>
    /// 번호로 붙들면 안 된다 — 버퍼가 차는 동안에는 점 개수가 매 틱 늘어나서
    /// 같은 번호가 한 칸씩 왼쪽이 된다. "맨 오른쪽"은 그때그때 다시 계산해야 한다.
    /// </remarks>
    public bool IsTrackingLive { get; private set; }

    /// <summary>전체 패널을 띄울 카드. 나머지 카드는 요약 칩만 보여준다.</summary>
    public string? FocusCardKey { get; private set; }

    public event Action? Changed;

    public bool IsActive => Index is not null;

    /// <param name="windowPoints">그 카드가 지금 그리는 점 수. 맨 오른쪽인지 판정하는 데 쓴다.</param>
    public void Hover(string cardKey, int index, int windowPoints)
    {
        if (IsPinned) return;
        Set(index, cardKey, pinned: false, live: IsLast(index, windowPoints));
    }

    public void Leave()
    {
        if (IsPinned) return;
        Set(null, null, pinned: false, live: false);
    }

    /// <summary>클릭 토글. 고정하면 값을 읽어 적을 수 있고, 다시 클릭하면 풀린다.</summary>
    public void TogglePin(string cardKey, int index, int windowPoints)
    {
        if (IsPinned) Set(null, null, pinned: false, live: false);
        else Set(index, cardKey, pinned: true, live: IsLast(index, windowPoints));
    }

    public void Clear() => Set(null, null, pinned: false, live: false);

    private static bool IsLast(int index, int windowPoints) => index >= windowPoints - 1;

    /// <summary>고정 상태에서 한 샘플씩 이동. 범위를 벗어나면 끝에서 멈춘다.</summary>
    public void Move(int delta, int windowPoints)
    {
        if (Index is not { } current) return;

        int moved = Math.Clamp(current + delta, 0, Math.Max(0, windowPoints - 1));
        Set(moved, FocusCardKey, IsPinned, IsLast(moved, windowPoints));
    }

    /// <summary>
    /// 새 샘플이 들어왔다. 고정된 선을 그래프와 같이 흘려보낸다 (§8.7).
    /// </summary>
    /// <remarks>
    /// <b>호버 중에는 흘리지 않는다.</b> 그때 선의 자리를 정하는 것은 커서이고,
    /// 커서는 가만히 있으면 가만히 있는 것이 맞다. 흘려보내면 마우스를 안 움직였는데
    /// 값이 왼쪽으로 기어간다.
    /// </remarks>
    public void Advance(int windowPoints, int newSamples = 1)
    {
        if (Index is null) return;

        // 맨 오른쪽에 걸린 선은 고정이든 호버든 "지금"을 가리킨다. 점 개수가 늘어나도
        // 그때의 마지막 칸으로 다시 잡아야 한다 — 번호를 붙들면 한 칸씩 뒤처진다.
        if (IsTrackingLive)
        {
            Set(Math.Max(0, windowPoints - 1), FocusCardKey, IsPinned, live: true);
            return;
        }

        // 흐르는 것은 고정된 선만이다. 호버는 커서가 자리를 정하므로 가만히 두면 가만히 있는다.
        if (!IsPinned) return;

        int? next = ScrubDrift.Advance(Index.Value, windowPoints, newSamples);
        if (next is { } index) Set(index, FocusCardKey, pinned: true, live: false);
        else Set(null, null, pinned: false, live: false);   // 창 밖으로 나간 순간은 놓는다
    }

    private void Set(int? index, string? focus, bool pinned, bool live)
    {
        if (Index == index && FocusCardKey == focus && IsPinned == pinned && IsTrackingLive == live) return;

        Index = index;
        FocusCardKey = focus;
        IsPinned = pinned;
        IsTrackingLive = live;
        Changed?.Invoke();
    }
}

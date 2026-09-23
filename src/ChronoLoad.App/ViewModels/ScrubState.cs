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

    /// <summary>전체 패널을 띄울 카드. 나머지 카드는 요약 칩만 보여준다.</summary>
    public string? FocusCardKey { get; private set; }

    public event Action? Changed;

    public bool IsActive => Index is not null;

    public void Hover(string cardKey, int index)
    {
        if (IsPinned) return;
        Set(index, cardKey, pinned: false);
    }

    public void Leave()
    {
        if (IsPinned) return;
        Set(null, null, pinned: false);
    }

    /// <summary>클릭 토글. 고정하면 값을 읽어 적을 수 있고, 다시 클릭하면 풀린다.</summary>
    public void TogglePin(string cardKey, int index)
    {
        if (IsPinned) Set(null, null, pinned: false);
        else Set(index, cardKey, pinned: true);
    }

    public void Clear() => Set(null, null, pinned: false);

    /// <summary>고정 상태에서 한 샘플씩 이동. 범위를 벗어나면 끝에서 멈춘다.</summary>
    public void Move(int delta, int windowPoints)
    {
        if (Index is not { } current) return;
        Set(Math.Clamp(current + delta, 0, Math.Max(0, windowPoints - 1)), FocusCardKey, IsPinned);
    }

    private void Set(int? index, string? focus, bool pinned)
    {
        if (Index == index && FocusCardKey == focus && IsPinned == pinned) return;

        Index = index;
        FocusCardKey = focus;
        IsPinned = pinned;
        Changed?.Invoke();
    }
}

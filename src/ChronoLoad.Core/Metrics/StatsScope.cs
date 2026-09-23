namespace ChronoLoad.Core.Metrics;

/// <summary>
/// 통계 채널. 같은 시계열 위에서 <b>서로 독립적인 리셋 시점</b>을 갖는다.
/// </summary>
/// <remarks>
/// <para>
/// 화면의 ⟲ 와 MCP의 <c>reset_stats</c> 는 다른 사람이 다른 목적으로 누른다.
/// 사용자가 눈으로 구간을 재는 동안 에이전트가 리셋하면 사용자의 측정이 소리 없이 사라지고,
/// 반대로 에이전트가 벤치마크 구간을 재는 동안 사용자가 리셋하면 에이전트가 받은 숫자가 거짓이 된다.
/// 둘 중 어느 쪽도 상대의 기준점을 건드리면 안 된다.
/// </para>
/// <para>
/// <b>시계열 자체는 공유한다.</b> 나뉘는 것은 "언제부터 세기 시작했는가"뿐이다.
/// 링버퍼를 두 벌 들고 있을 이유는 없다.
/// </para>
/// </remarks>
public enum StatsScope : byte
{
    /// <summary>화면의 ⟲ 버튼과 <c>Ctrl+R</c> 이 조작한다. MCP는 읽지도 쓰지도 않는다.</summary>
    Ui = 0,

    /// <summary>MCP <c>reset_stats</c> 만 조작한다. 화면의 리셋은 이 값을 건드리지 않는다.</summary>
    Mcp = 1,
}

public static class StatsScopes
{
    public const int Count = 2;

    public static ReadOnlySpan<StatsScope> All => [StatsScope.Ui, StatsScope.Mcp];
}

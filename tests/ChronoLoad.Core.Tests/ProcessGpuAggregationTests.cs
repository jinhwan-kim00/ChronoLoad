using ChronoLoad.Sensors;

namespace ChronoLoad.Core.Tests;

/// <summary>
/// 프로세스 표의 GPU 사용률 정의(§5.6). 어댑터 쪽과 같이 <b>엔진 종류 안에서는 합, 종류끼리는 최댓값</b>이다.
/// </summary>
public class ProcessGpuAggregationTests
{
    private const string Rtx = "luid_0x00000000_0x00016567";
    private const string Arc = "luid_0x00000000_0x00017E9B";

    private static Dictionary<int, Dictionary<(string Luid, string EngineType), double>> Groups(
        int pid, params (string Luid, string EngineType, double Sum)[] groups) =>
        new() { [pid] = groups.ToDictionary(g => (g.Luid, g.EngineType), g => g.Sum) };

    /// <summary>
    /// RTX 5080 실측: NVENC 가 둘이라 <c>hevc_nvenc</c> 한 세션이 두 인스턴스에 반씩 실렸다.
    /// 인스턴스의 최댓값을 쓰자 엔진별 값은 VideoEncode 99.5% 인데 어댑터 값은 49.8% 였다.
    /// 수집기는 같은 종류의 인스턴스를 먼저 더해 넘긴다.
    /// </summary>
    [Fact]
    public void Engines_of_the_same_type_add_up_before_the_adapter_takes_the_maximum()
    {
        var (byAdapter, byEngine) = ProcessProvider.CombineEngineGroups(
            Groups(23748, (Rtx, "VideoEncode", 49.8 + 49.7), (Rtx, "3D", 2.3), (Rtx, "Copy", 1.3)));

        Assert.Equal(99.5, byAdapter[23748][Rtx], 6);
        Assert.Equal(99.5, byEngine[23748]["VideoEncode"], 6);
    }

    [Fact]
    public void Different_engine_types_do_not_add_up()
    {
        // 3D 와 Copy 가 동시에 도는 것은 두 배로 바쁜 것이 아니다.
        var (byAdapter, _) = ProcessProvider.CombineEngineGroups(
            Groups(7, (Rtx, "3D", 60), (Rtx, "Copy", 50)));

        Assert.Equal(60, byAdapter[7][Rtx]);
    }

    [Fact]
    public void Adapters_are_kept_apart_and_capped_at_100()
    {
        var (byAdapter, byEngine) = ProcessProvider.CombineEngineGroups(
            Groups(9, (Rtx, "3D", 70), (Arc, "Compute", 130)));

        Assert.Equal(70, byAdapter[9][Rtx]);
        Assert.Equal(100, byAdapter[9][Arc]);
        Assert.Equal(100, byEngine[9]["Compute"]);

        // 어댑터 키는 에이전트가 넘긴 문자열과 대조된다 — 대소문자로 어긋나면 안 된다.
        Assert.Equal(70, byAdapter[9][Rtx.ToUpperInvariant()]);
    }
}

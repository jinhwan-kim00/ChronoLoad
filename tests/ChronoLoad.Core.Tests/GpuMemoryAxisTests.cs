using ChronoLoad.Core.Layout;

namespace ChronoLoad.Core.Tests;

/// <summary>
/// §8.3 GPU 메모리 축 정책. 여기서 보는 것은 숫자가 아니라 <b>읽는 사람이 무엇을 알 수 있는가</b>다 —
/// "얼마나 잡고 있나", "얼마나 남았나", "전용을 넘겼나" 셋이 형태만으로 읽혀야 한다.
/// </summary>
public class GpuMemoryAxisTests
{
    private const double GiB = 1024d * 1024 * 1024;

    [Theory]
    [InlineData(0.5)]
    [InlineData(4)]
    [InlineData(11.9)]
    public void A_discrete_gpu_keeps_the_axis_pinned_to_its_vram_while_it_fits(double usedGiB)
    {
        // 축이 사용량을 따라 움직이면 0.5 GiB 를 쓸 때와 11.9 GiB 를 쓸 때가 같은 높이로 그려진다.
        // 점유량을 보려고 만든 차트에서 그건 아무것도 말해주지 않는다.
        double max = GpuMemoryAxis.Max(discrete: true, dedicatedCapacity: 12 * GiB,
                                       sharedCapacity: 16 * GiB, peakTotal: usedGiB * GiB);

        Assert.Equal(12 * GiB, max);
    }

    [Fact]
    public void A_discrete_gpu_grows_the_axis_only_by_what_spilled_over()
    {
        // 12 GiB 카드가 14 GiB 를 잡았다. 넘친 만큼만 늘어나야 용량선이 화면 안에 남고
        // "얼마나 넘겼나"가 선 위 두께로 읽힌다.
        double max = GpuMemoryAxis.Max(true, 12 * GiB, 16 * GiB, peakTotal: 14 * GiB);

        Assert.True(max > 14 * GiB, "넘친 꼭대기가 축 상단에 붙으면 윗변이 보이지 않는다");
        Assert.True(max < 16 * GiB, "필요 이상으로 늘리면 용량선이 아래로 눌려 기준선 구실을 못 한다");
    }

    [Fact]
    public void The_capacity_line_stays_visible_once_it_spills()
    {
        // 용량선이 축 상단에 붙어버리면 "넘었다"가 형태로 드러나지 않는다.
        double max = GpuMemoryAxis.Max(true, 12 * GiB, 16 * GiB, peakTotal: 12.05 * GiB);
        double lineRatio = 12 * GiB / max;

        Assert.True(lineRatio < 0.98, $"용량선이 축의 {lineRatio:P1} 지점 — 상단에 너무 붙었다");
    }

    [Fact]
    public void An_integrated_gpu_pins_the_axis_to_the_shared_limit_and_never_moves_it()
    {
        // 내장은 전용 VRAM 이 없어 비교 기준이 공유 한도뿐이다. 그리고 그 한도는 넘을 수 없으므로
        // 축이 움직일 이유 자체가 없다 — 고정이면 카드 두 장을 나란히 두고 비교할 수도 있다.
        double idle = GpuMemoryAxis.Max(discrete: false, dedicatedCapacity: 0.125 * GiB,
                                        sharedCapacity: 8 * GiB, peakTotal: 1.3 * GiB);
        double busy = GpuMemoryAxis.Max(false, 0.125 * GiB, 8 * GiB, peakTotal: 6.7 * GiB);

        Assert.Equal(8 * GiB, idle);
        Assert.Equal(8 * GiB, busy);
    }

    [Fact]
    public void An_adapter_with_no_known_capacity_still_gets_a_usable_axis()
    {
        // 용량을 모르면 기준이 없다. 이럴 때만 최고치를 따라간다 — 빈 차트보다는 낫다.
        double max = GpuMemoryAxis.Max(discrete: false, dedicatedCapacity: 0, sharedCapacity: 0,
                                       peakTotal: 2 * GiB);

        Assert.True(max > 2 * GiB);
    }

    [Fact]
    public void An_empty_series_does_not_collapse_the_axis_to_zero()
    {
        // 0 으로 나누면 좌표가 전부 NaN 이 되고 차트가 통째로 사라진다.
        Assert.True(GpuMemoryAxis.Max(false, 0, 0, peakTotal: 0) > 0);
        Assert.True(GpuMemoryAxis.Max(false, 0, 0, peakTotal: double.NaN) > 0);
        Assert.True(GpuMemoryAxis.Max(true, 0, 0, peakTotal: -1) > 0);
    }

    [Fact]
    public void The_capacity_reference_matches_whatever_the_axis_pins_itself_to()
    {
        // 헤더의 `1.49/8.86G`, 접힌 카드의 미터, 펼친 차트의 용량선이 전부 이 분모를 쓴다.
        // 축과 다른 기준을 쓰면 같은 카드 안에서 "80% 찼다"와 "막대가 절반"이 동시에 나온다.
        Assert.Equal(GpuMemoryAxis.Max(false, 0.125 * GiB, 8 * GiB, peakTotal: 1.3 * GiB),
                     GpuMemoryAxis.CapacityReference(false, 0.125 * GiB, 8 * GiB));

        // 외장은 넘치지 않는 동안 축 상한이 곧 전용 용량이다.
        Assert.Equal(GpuMemoryAxis.Max(true, 12 * GiB, 16 * GiB, peakTotal: 9 * GiB),
                     GpuMemoryAxis.CapacityReference(true, 12 * GiB, 16 * GiB));
    }

    [Fact]
    public void An_unknown_capacity_has_no_reference_rather_than_a_made_up_one()
    {
        // 0 을 돌려주면 호출한 쪽이 "분모가 없다"를 알 수 있다. 아무 숫자나 채워 넣으면
        // 비율이 거짓이 되고, 그 거짓이 미터의 길이로 나타난다.
        Assert.Equal(0, GpuMemoryAxis.CapacityReference(discrete: true, 0, 16 * GiB));
        Assert.Equal(0, GpuMemoryAxis.CapacityReference(discrete: false, 12 * GiB, 0));
    }

    [Fact]
    public void A_discrete_gpu_that_never_reported_its_vram_falls_back_instead_of_pinning_to_zero()
    {
        // 용량 0 을 그대로 축 상한으로 쓰면 차트가 사라진다. 외장이라도 용량을 모르면
        // 용량을 모르는 어댑터와 같은 취급이어야 한다.
        double max = GpuMemoryAxis.Max(discrete: true, dedicatedCapacity: 0, sharedCapacity: 0,
                                       peakTotal: 3 * GiB);

        Assert.True(max >= 3 * GiB);
    }
}

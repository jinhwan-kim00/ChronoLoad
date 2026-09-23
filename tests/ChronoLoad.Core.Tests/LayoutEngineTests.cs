using ChronoLoad.Core.Layout;

namespace ChronoLoad.Core.Tests;

public class LayoutEngineTests
{
    // 설계서 §8.6 의 기본 구성: GPU 2 + 디스크 2 + 네트워크 2 + CPU + 메모리
    private static CardSpec[] DefaultCards() =>
    [
        new("cpu",   1.0, 90),
        new("mem",   1.0, 70),
        new("net0",  1.0, 50),
        new("net1",  1.0, 45, UserCollapsed: true),
        new("disk0", 1.0, 60),
        new("disk1", 1.0, 55, UserCollapsed: true),
        new("gpu0",  1.5, 100),
        new("gpu1",  1.5, 80, UserCollapsed: true),
    ];

    [Fact]
    public void Splits_free_space_by_weight()
    {
        var cards = new CardSpec[] { new("a", 1.0, 10), new("b", 1.5, 20) };

        var layout = LayoutEngine.Compute(cards, 508);   // total = 508 − 8 = 500

        Assert.All(layout, l => Assert.False(l.Collapsed));
        Assert.Equal(200, layout[0].Height, 3);
        Assert.Equal(300, layout[1].Height, 3);
    }

    [Fact]
    public void Default_configuration_fits_without_auto_collapsing()
    {
        var layout = LayoutEngine.Compute(DefaultCards(), 868);

        Assert.DoesNotContain(layout, l => l.AutoCollapsed);
        Assert.Equal(3, layout.Count(l => l.Collapsed));

        double expected = (868 - 7 * 8 - 3 * 28) / 5.5;   // 132.36…
        Assert.Equal(expected, layout.First(l => l.Key == "cpu").Height, 3);
        Assert.Equal(expected * 1.5, layout.First(l => l.Key == "gpu0").Height, 3);
        Assert.True(layout.Where(l => !l.Collapsed).All(l => l.Height >= LayoutEngine.MinExpandedHeight));
    }

    [Fact]
    public void Auto_collapse_picks_the_lowest_priority_card_first()
    {
        // GPU1 을 펼치면 자리가 모자란다 → 우선순위 최저(net0 45 는 이미 접힘, 다음은 net0 50)
        var cards = DefaultCards();
        cards[7] = cards[7] with { UserCollapsed = false };   // gpu1 펼침

        var layout = LayoutEngine.Compute(cards, 868);

        var auto = layout.Where(l => l.AutoCollapsed).Select(l => l.Key).ToArray();
        Assert.NotEmpty(auto);
        Assert.Equal("net0", auto[0]);
        Assert.True(layout.Where(l => !l.Collapsed).All(l => l.Height >= LayoutEngine.MinExpandedHeight));
    }

    [Fact]
    public void A_card_the_user_just_opened_is_not_the_one_that_closes()
    {
        // 자리가 꽉 찬 상태에서 우선순위가 낮은 카드(net0 50)를 열면, 우선순위만 보는 규칙은
        // 방금 연 그 카드를 희생양으로 고른다 — 클릭해도 카드가 열리지 않는다. 실제로 그랬다.
        var cards = DefaultCards();
        cards[7] = cards[7] with { UserCollapsed = false };          // gpu1 도 펼쳐 자리를 없앤다
        cards[2] = cards[2] with { ExpandOrder = 1 };                // net0 를 방금 열었다

        var layout = LayoutEngine.Compute(cards, 868);

        Assert.False(layout.First(l => l.Key == "net0").Collapsed);
        Assert.Contains(layout, l => l.AutoCollapsed);               // 대신 누군가는 접혔다
    }

    [Fact]
    public void The_longest_untouched_card_gives_way_first()
    {
        // 사용자가 직접 연 순서가 우선순위보다 앞선다. 둘 다 사용자가 열었다면
        // 더 오래 전에 연 쪽이 자리를 내준다 — 직접 누른 선택이 표에 적힌 기본값보다 앞선다.
        var cards = new CardSpec[]
        {
            new("high", 1.0, 100, ExpandOrder: 1),   // 우선순위는 높지만 먼저 열었다
            new("low",  1.0, 30,  ExpandOrder: 2),   // 우선순위는 낮지만 방금 열었다
            new("third", 1.0, 50, ExpandOrder: 3),
        };

        var layout = LayoutEngine.Compute(cards, 300);

        Assert.True(layout.First(l => l.Key == "high").AutoCollapsed);
        Assert.False(layout.First(l => l.Key == "low").Collapsed);
    }

    [Fact]
    public void Untouched_cards_still_follow_the_priority_table()
    {
        // 아무도 손대지 않은 동안에는(ExpandOrder 가 전부 0) 예전과 똑같이 동작해야 한다.
        var cards = new CardSpec[]
        {
            new("keep", 1.0, 100),
            new("mid",  1.0, 60),
            new("low",  1.0, 30),
        };

        var layout = LayoutEngine.Compute(cards, 300);

        Assert.True(layout.First(l => l.Key == "low").AutoCollapsed);
        Assert.False(layout.First(l => l.Key == "keep").Collapsed);
    }

    [Fact]
    public void Auto_collapse_respects_priority_order_when_several_must_go()
    {
        var cards = new CardSpec[]
        {
            new("keep", 1.0, 100),
            new("mid",  1.0, 60),
            new("low",  1.0, 30),
        };

        var layout = LayoutEngine.Compute(cards, 260);   // 셋 다 펼치기엔 턱없이 부족

        Assert.False(layout[0].Collapsed);
        Assert.True(layout[2].AutoCollapsed);            // low 가 먼저
    }

    [Fact]
    public void Never_collapses_the_last_expanded_card()
    {
        var cards = new CardSpec[] { new("only", 1.0, 10) };

        var layout = LayoutEngine.Compute(cards, 40);   // 최소 높이에 한참 못 미침

        Assert.False(layout[0].Collapsed);
        Assert.False(layout[0].AutoCollapsed);
    }

    [Fact]
    public void User_collapsed_cards_are_never_marked_auto()
    {
        var layout = LayoutEngine.Compute(DefaultCards(), 868);

        foreach (var key in new[] { "net1", "disk1", "gpu1" })
        {
            var card = layout.First(l => l.Key == key);
            Assert.True(card.Collapsed);
            Assert.False(card.AutoCollapsed);        // 자동 복원 대상이 아니다
            Assert.Equal(LayoutEngine.CollapsedHeight, card.Height);
        }
    }

    [Fact]
    public void Everything_collapsed_is_a_valid_layout()
    {
        var cards = DefaultCards().Select(c => c with { UserCollapsed = true }).ToArray();

        var layout = LayoutEngine.Compute(cards, 868);

        Assert.All(layout, l => Assert.Equal(LayoutEngine.CollapsedHeight, l.Height));
    }

    [Fact]
    public void Heights_and_gaps_add_up_to_the_available_space()
    {
        var layout = LayoutEngine.Compute(DefaultCards(), 868);

        double used = layout.Sum(l => l.Height) + LayoutEngine.Gap * (layout.Count - 1);
        Assert.Equal(868, used, 3);
    }

    [Fact]
    public void Empty_input_is_handled()
    {
        Assert.Empty(LayoutEngine.Compute([], 800));
    }

    [Fact]
    public void Suggested_window_height_never_exceeds_85_percent_of_the_work_area()
    {
        var tall = Enumerable.Range(0, 30).Select(i => new CardSpec($"c{i}", 1.0, i)).ToArray();

        double height = LayoutEngine.SuggestWindowHeight(tall, workAreaHeight: 1080);

        Assert.Equal(1080 * 0.85, height, 3);
    }

    [Fact]
    public void Suggested_window_height_grows_with_the_device_count()
    {
        double small = LayoutEngine.SuggestWindowHeight(
            [new("cpu", 1.0, 90), new("mem", 1.0, 70), new("gpu0", 1.5, 100)], workAreaHeight: 2160);
        double large = LayoutEngine.SuggestWindowHeight(DefaultCards(), workAreaHeight: 2160);

        Assert.True(large > small, $"장치가 많을수록 창이 커져야 한다. small={small} large={large}");
    }

    [Fact]
    public void A_small_device_set_does_not_inflate_the_window_on_a_4K_monitor()
    {
        // 하한을 작업 영역에 비례시키면 카드 3개짜리 노트북이 1296px 창을 받는다.
        double height = LayoutEngine.SuggestWindowHeight(
            [new("cpu", 1.0, 90), new("mem", 1.0, 70), new("gpu0", 1.5, 100)], workAreaHeight: 2160);

        Assert.InRange(height, LayoutEngine.MinWindowHeight, 700);
    }

    [Fact]
    public void Suggested_window_height_respects_the_floor()
    {
        double height = LayoutEngine.SuggestWindowHeight(
            [new("cpu", 1.0, 90, UserCollapsed: true)], workAreaHeight: 1080);

        Assert.Equal(LayoutEngine.MinWindowHeight, height, 3);
    }
}

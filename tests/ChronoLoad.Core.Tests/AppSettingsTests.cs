using System.Text.Json.Nodes;
using ChronoLoad.Core.Settings;

namespace ChronoLoad.Core.Tests;

/// <summary>§11 설정 영속성. 다음 실행에서 화면이 그대로 돌아오는가.</summary>
[Collection(LocalAppDataCollection.Name)]
public class AppSettingsTests
{
    private static readonly WindowPlacement Screen = new(0, 0, 1920, 1080);

    [Fact]
    public void A_window_fully_inside_the_screen_is_restored()
    {
        Assert.True(AppSettings.IsOnScreen(new WindowPlacement(100, 100, 340, 700), [Screen]));
    }

    [Fact]
    public void A_window_on_a_monitor_that_is_gone_is_not_restored()
    {
        // 두 번째 모니터에 있던 창. 그 모니터를 뽑으면 좌표가 허공을 가리킨다 —
        // 그대로 복원하면 사용자는 앱이 실행되지 않았다고 생각한다.
        Assert.False(AppSettings.IsOnScreen(new WindowPlacement(2200, 300, 340, 700), [Screen]));
    }

    [Fact]
    public void A_sliver_hanging_off_the_edge_does_not_count_as_visible()
    {
        // 오른쪽 끝에 20px 만 걸친 창. 제목 표시줄을 잡을 수 없으면 되살려도 쓸 수 없다.
        Assert.False(AppSettings.IsOnScreen(new WindowPlacement(1900, 500, 340, 700), [Screen]));

        // 잡을 수 있을 만큼 걸쳐 있으면 되살린다.
        Assert.True(AppSettings.IsOnScreen(new WindowPlacement(1700, 500, 340, 700), [Screen]));
    }

    [Fact]
    public void A_window_in_the_gap_between_two_monitors_is_rejected()
    {
        // 왼쪽 아래 1080p + 오른쪽 위 4K 처럼 어긋난 배치. 둘을 감싸는 경계 상자에는
        // 어떤 모니터도 없는 빈 구역이 생긴다. 경계 상자만 보면 "화면 안"으로 판정되지만
        // 복원하면 아무 데도 보이지 않는다.
        WindowPlacement[] monitors =
        [
            new(0, 1080, 1920, 1080),        // 왼쪽 아래
            new(1920, 0, 3840, 2160),        // 오른쪽 위
        ];

        // 경계 상자(0,0 ~ 5760,2160) 안이지만 실제로는 어느 모니터에도 없는 자리.
        Assert.False(AppSettings.IsOnScreen(new WindowPlacement(400, 200, 340, 700), monitors));

        // 각 모니터 위에서는 당연히 통과한다.
        Assert.True(AppSettings.IsOnScreen(new WindowPlacement(400, 1200, 340, 700), monitors));
        Assert.True(AppSettings.IsOnScreen(new WindowPlacement(2200, 200, 340, 700), monitors));
    }

    [Fact]
    public void A_window_spanning_two_monitors_is_accepted()
    {
        WindowPlacement[] monitors = [new(0, 0, 1920, 1080), new(1920, 0, 1920, 1080)];

        // 경계에 걸친 창은 양쪽에서 보인다. 되살리지 않을 이유가 없다.
        Assert.True(AppSettings.IsOnScreen(new WindowPlacement(1800, 300, 340, 700), monitors));
    }

    [Fact]
    public void A_zero_sized_window_is_rejected()
    {
        Assert.False(AppSettings.IsOnScreen(new WindowPlacement(0, 0, 0, 0), [Screen]));
    }

    [Fact]
    public void Round_trip_keeps_placement_topmost_and_collapsed_state()
    {
        using var temp = new TempLocalAppData();

        var saved = AppSettings.Load();
        saved.Window = new WindowPlacement(120, 80, 340, 720);
        saved.Topmost = true;
        saved.Collapsed["gpu:luid_1"] = true;
        saved.Collapsed["net:guid_2"] = false;
        saved.Save();

        var loaded = AppSettings.Load();

        Assert.Equal(new WindowPlacement(120, 80, 340, 720), loaded.Window);
        Assert.True(loaded.Topmost);
        Assert.True(loaded.Collapsed["gpu:luid_1"]);
        Assert.False(loaded.Collapsed["net:guid_2"]);
    }

    [Fact]
    public void Unknown_keys_survive_a_save()
    {
        using var temp = new TempLocalAppData();

        // 새 버전이 쓴 파일을 옛 버전이 열었다가 저장하는 상황. 모르는 항목을 지우면
        // 사용자가 설정한 것이 조용히 사라지고, 새 버전으로 돌아왔을 때야 알아차린다.
        Directory.CreateDirectory(AppSettings.Directory);
        File.WriteAllText(AppSettings.FilePath,
            """{"schemaVersion":1,"topmost":false,"futureOption":{"nested":42}}""");

        var settings = AppSettings.Load();
        settings.Topmost = true;
        settings.Save();

        var root = JsonNode.Parse(File.ReadAllText(AppSettings.FilePath))!.AsObject();
        Assert.Equal(42, root["futureOption"]!["nested"]!.GetValue<int>());
        Assert.True(root["topmost"]!.GetValue<bool>());
    }

    [Fact]
    public void A_corrupt_file_falls_back_to_defaults_instead_of_throwing()
    {
        using var temp = new TempLocalAppData();

        Directory.CreateDirectory(AppSettings.Directory);
        File.WriteAllText(AppSettings.FilePath, "{ 이건 JSON 이 아니다");

        var settings = AppSettings.Load();

        Assert.Null(settings.Window);
        Assert.False(settings.Topmost);
        Assert.Empty(settings.Collapsed);
    }

    [Fact]
    public void The_settings_folder_follows_the_environment_so_tests_cannot_touch_real_settings()
    {
        // Environment.GetFolderPath 는 환경변수를 보지 않고 셸에 직접 묻는다. 그것만 쓰면
        // 아래 TempLocalAppData 가 아무것도 격리하지 못하고 테스트가 사용자의 실제 설정을 읽고 쓴다.
        // 실제로 그랬다 — 테스트 한 번에 사용자의 창 위치가 지워졌다.
        using var temp = new TempLocalAppData();

        Assert.StartsWith(Environment.GetEnvironmentVariable("LOCALAPPDATA")!,
                          AppSettings.Directory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_placement_written_before_the_pixel_schema_is_discarded()
    {
        using var temp = new TempLocalAppData();

        // schemaVersion 1 의 좌표는 DIP 다. 저장 당시 창이 어느 모니터에 있었는지가 파일에
        // 남아 있지 않아 환산할 수 없다. 그대로 쓰면 배율이 다른 모니터에서 어긋난 자리에 뜨는데,
        // 그 자리도 대개 어느 모니터 안이라 "화면 밖" 검사에도 걸리지 않는다.
        Directory.CreateDirectory(AppSettings.Directory);
        File.WriteAllText(AppSettings.FilePath, """
            { "schemaVersion": 1, "topmost": true,
              "window": { "left": 1680, "top": 34, "width": 340, "height": 724 } }
            """);

        var loaded = AppSettings.Load();

        Assert.Null(loaded.Window);
        Assert.True(loaded.Topmost);     // 위치 말고는 버릴 이유가 없다
    }

    [Fact]
    public void An_old_placement_does_not_come_back_as_pixels_when_the_schema_is_bumped()
    {
        using var temp = new TempLocalAppData();

        // 창을 최소화한 채로 끄면 새 좌표가 없어 Window 가 null 인 채로 저장된다.
        // 그때 옛 DIP 좌표를 남겨두면, 올라간 schemaVersion 이 그 값을 픽셀로 둔갑시킨다.
        Directory.CreateDirectory(AppSettings.Directory);
        File.WriteAllText(AppSettings.FilePath, """
            { "schemaVersion": 1, "window": { "left": 1680, "top": 34, "width": 340, "height": 724 } }
            """);

        var loaded = AppSettings.Load();
        Assert.Null(loaded.Window);
        loaded.Save();

        Assert.Null(JsonNode.Parse(File.ReadAllText(AppSettings.FilePath))!["window"]);
        Assert.Null(AppSettings.Load().Window);
    }

    [Fact]
    public void A_placement_written_with_the_current_schema_survives()
    {
        using var temp = new TempLocalAppData();

        Directory.CreateDirectory(AppSettings.Directory);
        File.WriteAllText(AppSettings.FilePath, $$"""
            { "schemaVersion": {{AppSettings.CurrentSchemaVersion}},
              "window": { "left": 2940, "top": 60, "width": 595, "height": 1267 } }
            """);

        Assert.Equal(new WindowPlacement(2940, 60, 595, 1267), AppSettings.Load().Window);
    }

}

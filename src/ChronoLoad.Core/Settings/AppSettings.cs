using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChronoLoad.Core.Settings;

/// <summary>
/// 창의 마지막 위치와 크기. 모두 없으면 첫 실행이다.
/// </summary>
/// <remarks>
/// <b>단위는 물리 픽셀이다.</b> DIP 로 두면 모니터마다 배율이 다를 때 복원 위치가 어긋난다
/// (<c>Monitors.MoveTo</c> 참조). 모니터 작업 영역도 같은 단위라 그대로 비교할 수 있다.
/// </remarks>
public readonly record struct WindowPlacement(double Left, double Top, double Width, double Height)
{
    public bool IsValid => Width > 0 && Height > 0;
}

/// <summary>
/// 다음 실행에서 화면을 그대로 되살리기 위한 설정 (§11).
/// </summary>
/// <remarks>
/// <para>
/// <b>장치별 설정은 인덱스가 아니라 <c>DeviceInfo.Key</c> 로 저장한다.</b> 인덱스는 장치가
/// 하나 빠지기만 해도 밀려서, 접어둔 것이 엉뚱한 카드에 적용된다.
/// </para>
/// <para>
/// <b>모르는 키는 지우지 않고 그대로 되쓴다.</b> 새 버전이 쓴 파일을 옛 버전이 열었다가
/// 저장하면 사용자가 설정한 것이 조용히 사라진다 — 다시 새 버전으로 돌아왔을 때야 알아차린다.
/// </para>
/// </remarks>
public sealed class AppSettings
{
    /// <summary>
    /// 2 — 창 위치의 단위가 DIP 에서 물리 픽셀로 바뀌었다.
    /// </summary>
    /// <remarks>
    /// 옛 값을 환산해서 이어쓸 수는 없다. DIP 는 <i>저장 당시 창이 놓였던 모니터</i>의 배율로
    /// 나눈 값인데, 그 모니터가 어느 것이었는지는 파일에 남아 있지 않다. 그래서 1 로 적힌
    /// 창 위치는 버리고 장치 구성에서 다시 계산한다 — 한 번만 자리를 잃는 쪽이,
    /// 매번 어긋난 자리에 뜨는 것보다 낫다.
    /// </remarks>
    public const int CurrentSchemaVersion = 2;

    /// <summary>창 위치를 물리 픽셀로 적기 시작한 스키마 버전.</summary>
    private const int PixelPlacementSchemaVersion = 2;

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    /// <summary>읽어들인 원본. 모르는 키를 보존하기 위해 들고 있는다.</summary>
    private JsonObject _raw = [];

    public WindowPlacement? Window { get; set; }
    public bool Topmost { get; set; }

    /// <summary>장치 키 → 접힘 여부. 지금 없는 장치의 항목도 남겨둔다(다시 꽂으면 되살아난다).</summary>
    public Dictionary<string, bool> Collapsed { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 설정 폴더. <c>LOCALAPPDATA</c> 환경변수가 있으면 그것을 쓴다.
    /// </summary>
    /// <remarks>
    /// <b><see cref="Environment.GetFolderPath"/> 는 환경변수를 보지 않는다.</b> Windows 셸에
    /// 직접 묻기 때문에, 환경변수를 바꿔 격리했다고 믿은 테스트가 <b>실제 사용자 설정을</b>
    /// 읽고 쓴다. 실제로 그랬다 — 테스트 한 번에 사용자의 창 위치가 지워지고, 렌더 테스트의
    /// 합성 장치 키(<c>gpu:demo</c> 등)가 실제 파일에 남았다.
    /// <para>
    /// 환경변수를 먼저 보는 쪽이 맞다. 격리가 실제로 되고, 값이 없거나 비어 있으면 셸에 묻는
    /// 원래 동작 그대로다.
    /// </para>
    /// </remarks>
    public static string Directory
    {
        get
        {
            string? local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrWhiteSpace(local))
                local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            return Path.Combine(local, "ChronoLoad");
        }
    }

    public static string FilePath => Path.Combine(Directory, "settings.json");

    /// <summary>읽는다. 파일이 없거나 깨졌으면 기본값 — 설정 하나 때문에 앱이 안 뜨면 안 된다.</summary>
    public static AppSettings Load()
    {
        var settings = new AppSettings();

        try
        {
            if (!File.Exists(FilePath)) return settings;
            if (JsonNode.Parse(File.ReadAllText(FilePath)) is not JsonObject root) return settings;

            settings._raw = root;

            settings.Window = ReadPlacement(root);

            settings.Topmost = root["topmost"]?.GetValue<bool>() ?? false;

            if (root["collapsed"] is JsonObject collapsed)
                foreach (var (key, value) in collapsed)
                    if (value?.GetValue<bool>() is { } flag)
                        settings.Collapsed[key] = flag;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                                      or FormatException or InvalidOperationException)
        {
            // 깨진 설정은 무시하고 기본값으로 뜬다. 지우지는 않는다 — 원인을 볼 수 있어야 한다.
            return new AppSettings();
        }

        return settings;
    }

    /// <summary>
    /// 저장된 창 위치를 읽는다. 스키마가 <see cref="PixelPlacementSchemaVersion"/> 보다 오래됐으면
    /// 버린다 — 값은 멀쩡해 보이지만 단위가 DIP 라, 그대로 쓰면 배율이 다른 모니터에서 어긋난다.
    /// </summary>
    internal static WindowPlacement? ReadPlacement(JsonObject root)
    {
        if ((root["schemaVersion"]?.GetValue<int>() ?? 0) < PixelPlacementSchemaVersion) return null;

        if (root["window"] is not JsonObject w) return null;

        if (Read(w, "left") is not { } left || Read(w, "top") is not { } top ||
            Read(w, "width") is not { } width || Read(w, "height") is not { } height)
            return null;

        return new WindowPlacement(left, top, width, height);
    }

    private static double? Read(JsonObject o, string name) =>
        o[name]?.GetValue<double>() is { } v && double.IsFinite(v) ? v : null;

    /// <summary>
    /// 저장한다. 임시 파일에 쓴 뒤 바꿔치기해서, 쓰는 도중에 프로세스가 죽어도
    /// 반쪽짜리 파일이 남지 않는다.
    /// </summary>
    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            _raw["schemaVersion"] = CurrentSchemaVersion;
            _raw["topmost"] = Topmost;

            if (Window is { IsValid: true } placement)
                _raw["window"] = new JsonObject
                {
                    ["left"] = placement.Left,
                    ["top"] = placement.Top,
                    ["width"] = placement.Width,
                    ["height"] = placement.Height,
                };
            else
                // 읽을 때 버린 옛 스키마의 좌표가 파일에 그대로 남아 있다. 지우지 않으면
                // 이번 저장이 schemaVersion 을 올리는 순간 그 DIP 값이 픽셀로 되살아난다 —
                // 창을 최소화한 채로 끄면 새 좌표가 없어서 실제로 그 경로를 탄다.
                // 모르는 키는 보존하지만 이건 우리 키다.
                _raw.Remove("window");

            var collapsed = new JsonObject();
            foreach (var (key, value) in Collapsed) collapsed[key] = value;
            _raw["collapsed"] = collapsed;

            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, _raw.ToJsonString(Format));

            if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
            else File.Move(temp, FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 설정을 못 써도 앱은 계속 돈다. 다음 종료에서 다시 시도한다.
        }
    }

    /// <summary>
    /// 저장된 위치가 지금도 쓸 수 있는지 본다. 모니터를 뽑았거나 해상도가 바뀌면
    /// 창이 화면 밖에 떠서 <b>찾을 수 없게</b> 된다.
    /// </summary>
    /// <param name="screens">쓸 수 있는 화면 영역들(가상 데스크톱 좌표).</param>
    public static bool IsOnScreen(WindowPlacement placement, IEnumerable<WindowPlacement> screens)
    {
        if (!placement.IsValid) return false;

        // 제목 표시줄을 잡을 수 있을 만큼은 보여야 한다. 모서리 한 점만 걸치는 것은 소용없다.
        const double NeededWidth = 120, NeededHeight = 30;

        foreach (var screen in screens)
        {
            double overlapX = Math.Min(placement.Left + placement.Width, screen.Left + screen.Width)
                              - Math.Max(placement.Left, screen.Left);
            double overlapY = Math.Min(placement.Top + placement.Height, screen.Top + screen.Height)
                              - Math.Max(placement.Top, screen.Top);

            if (overlapX >= NeededWidth && overlapY >= NeededHeight) return true;
        }

        return false;
    }
}

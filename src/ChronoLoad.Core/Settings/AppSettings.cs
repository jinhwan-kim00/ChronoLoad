using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChronoLoad.Core.Settings;

/// <summary>창의 마지막 위치와 크기. 모두 없으면 첫 실행이다.</summary>
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
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    /// <summary>읽어들인 원본. 모르는 키를 보존하기 위해 들고 있는다.</summary>
    private JsonObject _raw = [];

    public WindowPlacement? Window { get; set; }
    public bool Topmost { get; set; }

    /// <summary>장치 키 → 접힘 여부. 지금 없는 장치의 항목도 남겨둔다(다시 꽂으면 되살아난다).</summary>
    public Dictionary<string, bool> Collapsed { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChronoLoad");

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

            if (root["window"] is JsonObject w &&
                Read(w, "left") is { } left && Read(w, "top") is { } top &&
                Read(w, "width") is { } width && Read(w, "height") is { } height)
                settings.Window = new WindowPlacement(left, top, width, height);

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

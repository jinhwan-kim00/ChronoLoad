using System.Windows.Media;

namespace ChronoLoad.App.Services;

/// <summary>
/// 색 토큰의 <b>유일한 출처</b>. XAML 리소스 사전과 차트 렌더러가 같은 값을 봐야 하므로
/// C#에 한 번만 정의하고 런타임에 <c>Application.Resources</c>로 밀어 넣는다.
/// </summary>
/// <remarks>
/// 차트 렌더러는 매 프레임 색을 읽으므로 리소스 조회가 아니라 이 구조체를 직접 참조한다.
/// 두 곳에 색을 적어두면 반드시 어긋난다.
/// </remarks>
public sealed record ThemePalette(
    Color Bg, Color Surface, Color Surface2, Color Line,
    Color Fg, Color Label, Color Dim, Color Faint,
    Color Cpu, Color Mem, Color Net, Color Disk, Color Gpu, Color Warn,
    Color VendorNvidia, Color VendorAmd, Color VendorIntel)
{
    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    // Label 은 Fg 와 Dim 사이다. 장치명은 이제 카드를 구분하는 주된 수단이라(같은 아이콘의 카드가
    // 여럿이다) Dim 으로는 읽히지 않는다. 그렇다고 Fg 로 올리면 큰 숫자와 같은 무게가 되어
    // 시선이 어디로 가야 하는지 흐려진다 — 크기로만 위계를 만들기엔 10.5px 과 26px 의 차이가
    // 이미 충분하므로, 색은 반 단계만 올린다.

    /// <summary>다크는 순수 검정(#000) 대신 #0E1116 — OLED 스미어를 피하고 차트 대비를 확보한다.</summary>
    public static ThemePalette Dark { get; } = new(
        Bg: C("#0E1116"), Surface: C("#161B22"), Surface2: C("#1C232C"), Line: C("#262D38"),
        Fg: C("#E6EDF3"), Label: C("#C2CBD6"), Dim: C("#7D8792"), Faint: C("#4A5260"),
        Cpu: C("#38BDF8"), Mem: C("#A78BFA"), Net: C("#34D399"), Disk: C("#F472B6"),
        Gpu: C("#FBBF24"), Warn: C("#F87171"),
        VendorNvidia: C("#7FBC03"), VendorAmd: C("#F5333C"), VendorIntel: C("#2B7FFF"));

    public static ThemePalette Light { get; } = new(
        Bg: C("#F4F6F9"), Surface: C("#FFFFFF"), Surface2: C("#EFF2F6"), Line: C("#DDE3EA"),
        Fg: C("#11161C"), Label: C("#39424E"), Dim: C("#68727F"), Faint: C("#A9B3BF"),
        Cpu: C("#0284C7"), Mem: C("#7C3AED"), Net: C("#059669"), Disk: C("#DB2777"),
        Gpu: C("#B45309"), Warn: C("#DC2626"),
        VendorNvidia: C("#5C8A00"), VendorAmd: C("#C81018"), VendorIntel: C("#0F62C8"));

    public Color AccentFor(ChronoLoad.Core.Devices.DeviceClass cls, string key) => cls switch
    {
        ChronoLoad.Core.Devices.DeviceClass.Gpu => Gpu,
        ChronoLoad.Core.Devices.DeviceClass.Disk => Disk,
        ChronoLoad.Core.Devices.DeviceClass.Network => Net,
        _ => key.StartsWith("mem", StringComparison.OrdinalIgnoreCase) ? Mem : Cpu,
    };

    /// <summary>
    /// 아이콘 전용 제조사 색. 차트 계열 색으로는 절대 쓰지 않는다 —
    /// AMD 레드가 차트에 들어가면 경고와 구분되지 않는다.
    /// </summary>
    public Color IconColorFor(ChronoLoad.Core.Devices.IconKind icon, Color fallback) => icon switch
    {
        ChronoLoad.Core.Devices.IconKind.GpuNvidia => VendorNvidia,
        ChronoLoad.Core.Devices.IconKind.GpuAmd => VendorAmd,
        ChronoLoad.Core.Devices.IconKind.GpuIntel => VendorIntel,
        _ => fallback,
    };
}

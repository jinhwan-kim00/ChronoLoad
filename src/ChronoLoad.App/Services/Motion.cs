using System.Windows;
using System.Windows.Media.Animation;

namespace ChronoLoad.App.Services;

/// <summary>
/// 접근성 「동작 줄이기」(§9.5). 켜져 있으면 이징을 걷고 애니메이션을 <b>0초</b>로 만든다.
/// </summary>
/// <remarks>
/// <para>
/// Windows 의 <i>설정 → 접근성 → 시각 효과 → 애니메이션 효과</i> 를 끄면
/// <see cref="SystemParameters.ClientAreaAnimation"/>이 거짓이 된다. 값을 캐시하지 않고
/// 애니메이션을 만들 때마다 읽으므로, 설정을 바꾸면 다음 전환부터 곧바로 따라간다.
/// </para>
/// <para>
/// <b>지우는 것은 움직임이지 정보가 아니다.</b> 라벨이 3초 동안 떠 있는 것 같은 표시 시간은
/// 장식이 아니라 전달이므로 그대로 둔다(§9.5). 여기서 다루는 것은 이징과 전환 시간뿐이다.
/// </para>
/// </remarks>
public static class Motion
{
    /// <summary>즉시 끝내는 길이. 애니메이션을 지우는 대신 0초로 돌리면 호출부가 갈리지 않는다.</summary>
    public static readonly Duration Instant = new(TimeSpan.Zero);

    /// <summary>동작 줄이기가 켜져 있는가.</summary>
    public static bool Reduced => !SystemParameters.ClientAreaAnimation;

    /// <summary>줄이기가 켜져 있으면 0초, 아니면 원래 길이.</summary>
    public static Duration Of(Duration duration) => Reduced ? Instant : duration;

    /// <inheritdoc cref="Of(Duration)"/>
    public static Duration Of(TimeSpan duration) => Of(new Duration(duration));

    /// <summary>줄이기가 켜져 있으면 <c>null</c>. 이징이 없으면 WPF 는 선형으로 움직인다.</summary>
    public static IEasingFunction? Ease(IEasingFunction? easing) => Reduced ? null : easing;

    /// <summary>
    /// 기본 이징(<see cref="CubicEase"/> EaseInOut)을 얹은 애니메이션. 줄이기가 켜져 있으면
    /// 길이 0 에 이징도 없다 — 값이 곧바로 목표에 닿는다.
    /// </summary>
    public static DoubleAnimation Animate(double to, Duration duration,
        EasingMode mode = EasingMode.EaseInOut) =>
        new(to, Of(duration)) { EasingFunction = Ease(new CubicEase { EasingMode = mode }) };

    /// <inheritdoc cref="Animate(double, Duration, EasingMode)"/>
    public static DoubleAnimation Animate(double from, double to, Duration duration,
        EasingMode mode = EasingMode.EaseInOut) =>
        new(from, to, Of(duration)) { EasingFunction = Ease(new CubicEase { EasingMode = mode }) };
}

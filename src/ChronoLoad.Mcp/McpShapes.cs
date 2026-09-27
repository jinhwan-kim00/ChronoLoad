using System.Text.Json.Serialization;
using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Formatting;
using ChronoLoad.Core.Metrics;

namespace ChronoLoad.Mcp;

/// <summary>
/// 바이트 값은 숫자와 사람이 읽을 문자열을 <b>둘 다</b> 준다 (§10.4).
/// 에이전트는 계산에 숫자를 쓰고, 사용자에게 옮길 때는 문자열을 그대로 쓸 수 있다 —
/// 단위 환산을 각자 다시 구현하면 표기가 제각각이 된다.
/// </summary>
public readonly record struct ByteValue(
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("text")] string Text)
{
    public static ByteValue From(double bytes)
    {
        if (double.IsNaN(bytes) || bytes < 0) return new ByteValue(0, "—");

        long rounded = (long)Math.Round(bytes);
        return new ByteValue(rounded, MetricFormatter.Format(MetricUnit.Bytes, (float)bytes).ToString());
    }
}

/// <summary>
/// 모든 응답이 공유하는 머리말.
/// </summary>
/// <param name="SampledAt">값의 기준 시각. 요청 시각이 아니다.</param>
/// <param name="Stale">
/// 마지막 실측이 샘플 주기를 크게 넘겼다. 값은 주되 <b>지금 값이 아님</b>을 분명히 한다 —
/// 조용히 옛 값을 주면 에이전트가 그것을 현재로 오해한다.
/// </param>
/// <param name="DevicesRevision">
/// 장치 집합이 바뀔 때마다 증가한다. 에이전트는 이 값만 비교해
/// "내가 알던 구성이 그대로인가"를 판단한다 (§10.3).
/// </param>
public readonly record struct ResponseHeader(
    [property: JsonPropertyName("sampledAt")] string SampledAt,
    [property: JsonPropertyName("stale")] bool Stale,
    [property: JsonPropertyName("devicesRevision")] int DevicesRevision);

/// <summary>
/// 장치 배열의 원소가 공통으로 갖는 식별자.
/// </summary>
/// <remarks>
/// <paramref name="Index"/> 는 순서가 바뀔 수 있다. 재조회할 때는 반드시
/// <paramref name="Key"/>(LUID · 시리얼 · 인터페이스 GUID)를 써야 한다 (§10.3).
/// </remarks>
public readonly record struct DeviceRef(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("fullName")] string FullName);

/// <summary>리셋 이후 구간 통계 (§10.2 <c>get_stats_since_reset</c>).</summary>
/// <param name="QuantilesExact">
/// true 면 분위수를 구간의 표본 전부를 정렬해 구했다. 구간이 링(15분)보다 길면 false 이고,
/// 그때는 상대 오차 ±0.5% 스케치 근사다. 어느 쪽이든 최소~최대 범위를 벗어나지 않는다.
/// </param>
public sealed record StatsBlock(
    [property: JsonPropertyName("metric")] string Metric,
    [property: JsonPropertyName("deviceKey")] string DeviceKey,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("resetAt")] string ResetAt,
    [property: JsonPropertyName("elapsedSeconds")] double ElapsedSeconds,
    [property: JsonPropertyName("sampleCount")] long SampleCount,
    [property: JsonPropertyName("avg")] double? Avg,
    [property: JsonPropertyName("min")] double? Min,
    [property: JsonPropertyName("max")] double? Max,
    [property: JsonPropertyName("p50")] double? P50,
    [property: JsonPropertyName("p95")] double? P95,
    [property: JsonPropertyName("p99")] double? P99,
    [property: JsonPropertyName("quantilesExact")] bool? QuantilesExact,
    [property: JsonPropertyName("stdDev")] double? StdDev)
{
    /// <summary>포화 문턱(%). 백분율 지표에만 있고 나머지는 생략된다.</summary>
    [JsonPropertyName("saturationThreshold")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? SaturationThreshold { get; init; }

    /// <summary>
    /// 문턱 이상이었던 표본의 비율(0~1). 버스트형 부하에서 평균이 가리는 포화를 드러낸다.
    /// </summary>
    [JsonPropertyName("saturatedFraction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? SaturatedFraction { get; init; }
}

internal static class McpJsonHelpers
{
    /// <summary>ISO-8601 · 로컬 오프셋 (§10.4).</summary>
    public static string Iso(DateTimeOffset value) => value.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz");

    public static string Iso(long utcTicks) =>
        Iso(new DateTimeOffset(utcTicks, TimeSpan.Zero).ToLocalTime());

    /// <summary>NaN·무한대는 JSON 숫자가 아니다. null 로 내보내 "값 없음"을 명시한다.</summary>
    public static double? Finite(double value) => double.IsFinite(value) ? Math.Round(value, 4) : null;

    public static double? Finite(float value) => Finite((double)value);

    public static string UnitName(MetricUnit unit) => unit switch
    {
        MetricUnit.Percent => "percent",
        MetricUnit.Bytes => "bytes",
        MetricUnit.ByteRate => "bytesPerSecond",
        MetricUnit.BitRate => "bytesPerSecond",
        MetricUnit.Celsius => "celsius",
        MetricUnit.Watt => "watt",
        MetricUnit.Megahertz => "megahertz",
        MetricUnit.Milliseconds => "milliseconds",
        _ => "scalar",
    };

    public static DeviceRef Ref(DeviceHandle device) =>
        new(device.Index, device.Key, device.Info.ShortName, device.Info.FullName);
}

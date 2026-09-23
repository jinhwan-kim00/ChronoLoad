using System.Globalization;
using ChronoLoad.Core.Metrics;

namespace ChronoLoad.Core.Formatting;

/// <param name="Value">숫자만. 카드 헤더의 큰 글씨에 들어간다.</param>
/// <param name="Unit">단위 기호. 작게 붙는다.</param>
public readonly record struct FormattedValue(string Value, string Unit)
{
    public override string ToString() => Unit.Length == 0 ? Value : $"{Value} {Unit}";
}

/// <summary>
/// 표시용 숫자 포매팅. 카드가 쓰는 텍스트는 전부 여기를 거친다.
/// </summary>
public static class MetricFormatter
{
    private const double Kilo = 1e3, Mega = 1e6, Giga = 1e9;
    private const double KiB = 1024, MiB = KiB * 1024, GiB = MiB * 1024, TiB = GiB * 1024;

    public static FormattedValue Format(MetricKind kind, double value) => Format(kind.Unit(), value);

    public static FormattedValue Format(MetricUnit unit, double value)
    {
        if (double.IsNaN(value)) return new FormattedValue("—", string.Empty);

        return unit switch
        {
            MetricUnit.Percent => new FormattedValue(Round(value, 0), "%"),
            MetricUnit.Bytes => Bytes(value),
            MetricUnit.BitRate => BitRate(value),
            MetricUnit.ByteRate => ByteRate(value),
            MetricUnit.Celsius => new FormattedValue(Round(value, 0), "°C"),
            MetricUnit.Watt => new FormattedValue(Round(value, 0), "W"),
            MetricUnit.Megahertz => new FormattedValue(Round(value, 0), "MHz"),
            MetricUnit.Milliseconds => new FormattedValue(Round(value, 1), "ms"),
            _ => new FormattedValue(Round(value, 1), string.Empty),
        };
    }

    /// <remarks>
    /// <b>단위 기호는 줄이지 않는다.</b> <c>G</c> 하나만 붙여 두면 용량인지 속도인지, 바이트인지
    /// 비트인지 읽는 사람이 알 수 없다. 실제로 Wi-Fi 의 <c>2.4G</c>(2.4Gbps 링크 속도)를
    /// 2.4GHz 밴드로 읽는 일이 있었다. 두 글자 아껴서 값을 오해하게 만들 이유가 없다.
    /// <para>
    /// 1024 기준이면서 <c>GB</c> 로 적는 것은 Windows 의 표기와 맞춘 것이다 — 작업 관리자가
    /// 보여주는 숫자와 같은 값이어야 대조가 된다.
    /// </para>
    /// </remarks>
    private static FormattedValue Bytes(double bytes)
    {
        double abs = Math.Abs(bytes);
        return abs >= TiB ? new FormattedValue(Round(bytes / TiB, 2), "TB")
             : abs >= GiB ? new FormattedValue(Round(bytes / GiB, abs >= 10 * GiB ? 1 : 2), "GB")
             : abs >= MiB ? new FormattedValue(Round(bytes / MiB, 0), "MB")
             : new FormattedValue(Round(bytes / KiB, 0), "KB");
    }

    private static FormattedValue BitRate(double bytesPerSecond)
    {
        double bits = bytesPerSecond * 8;
        return bits >= Giga ? new FormattedValue(Round(bits / Giga, 2), "Gbps")
             : bits >= Mega ? new FormattedValue(Round(bits / Mega, bits >= 100 * Mega ? 0 : 1), "Mbps")
             : new FormattedValue(Round(bits / Kilo, 0), "Kbps");
    }

    /// <remarks>
    /// <c>/s</c> 를 반드시 붙인다. 용량 쪽이 <c>KB</c> 이므로 여기서도 <c>KB</c> 로 적으면
    /// 같은 기호가 크기와 속도 두 가지를 뜻하게 된다 — 카드 두 장을 나란히 두면 바로 헷갈린다.
    /// </remarks>
    public static FormattedValue ByteRate(double bytesPerSecond)
    {
        double abs = Math.Abs(bytesPerSecond);
        return abs >= Giga ? new FormattedValue(Round(bytesPerSecond / Giga, 2), "GB/s")
             : abs >= Mega ? new FormattedValue(Round(bytesPerSecond / Mega, abs >= 100 * Mega ? 0 : 1), "MB/s")
             : new FormattedValue(Round(bytesPerSecond / Kilo, 0), "KB/s");
    }

    /// <summary>
    /// 푸터의 세 값(평균·최대·최소)은 <b>같은 단위로 맞춰</b> 보여줘야 한다.
    /// 평균 289M 옆에 최대 1.40G 가 오면 어느 쪽이 큰지 한눈에 안 들어온다.
    /// </summary>
    public static (string[] Values, string Unit) FormatGroup(MetricUnit unit, params double[] values)
    {
        if (values.Length == 0) return ([], string.Empty);

        double max = 0;
        foreach (var v in values)
            if (!double.IsNaN(v)) max = Math.Max(max, Math.Abs(v));

        var reference = Format(unit, max);
        double divisor = DivisorFor(unit, reference.Unit);

        var texts = new string[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            if (double.IsNaN(values[i])) { texts[i] = "—"; continue; }
            double scaled = values[i] / divisor;
            texts[i] = Round(scaled, scaled >= 100 ? 0 : scaled >= 10 ? 1 : 2);
        }

        return (texts, reference.Unit);
    }

    // 여기 키는 위 포매터가 내는 기호와 글자 하나까지 같아야 한다. 어긋나면 기본 가지로 떨어져
    // 값이 1000배 틀린 채로 멀쩡해 보인다 — 단위 기호를 바꿀 때 같이 고쳐야 하는 자리다.
    private static double DivisorFor(MetricUnit unit, string suffix) => unit switch
    {
        MetricUnit.Bytes => suffix switch { "TB" => TiB, "GB" => GiB, "MB" => MiB, _ => KiB },
        MetricUnit.BitRate => suffix switch { "Gbps" => Giga / 8, "Mbps" => Mega / 8, _ => Kilo / 8 },
        MetricUnit.ByteRate => suffix switch { "GB/s" => Giga, "MB/s" => Mega, _ => Kilo },
        _ => 1,
    };

    private static string Round(double value, int digits) =>
        value.ToString("F" + digits, CultureInfo.InvariantCulture);

    /// <summary>리셋 이후 경과 시간. 1시간을 넘으면 h:mm:ss.</summary>
    public static string Elapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
}

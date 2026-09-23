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

    private static FormattedValue Bytes(double bytes)
    {
        double abs = Math.Abs(bytes);
        return abs >= TiB ? new FormattedValue(Round(bytes / TiB, 2), "T")
             : abs >= GiB ? new FormattedValue(Round(bytes / GiB, abs >= 10 * GiB ? 1 : 2), "G")
             : abs >= MiB ? new FormattedValue(Round(bytes / MiB, 0), "M")
             : new FormattedValue(Round(bytes / KiB, 0), "K");
    }

    private static FormattedValue BitRate(double bytesPerSecond)
    {
        double bits = bytesPerSecond * 8;
        return bits >= Giga ? new FormattedValue(Round(bits / Giga, 2), "Gb")
             : bits >= Mega ? new FormattedValue(Round(bits / Mega, bits >= 100 * Mega ? 0 : 1), "Mb")
             : new FormattedValue(Round(bits / Kilo, 0), "Kb");
    }

    public static FormattedValue ByteRate(double bytesPerSecond)
    {
        double abs = Math.Abs(bytesPerSecond);
        return abs >= Giga ? new FormattedValue(Round(bytesPerSecond / Giga, 2), "GB")
             : abs >= Mega ? new FormattedValue(Round(bytesPerSecond / Mega, abs >= 100 * Mega ? 0 : 1), "MB")
             : new FormattedValue(Round(bytesPerSecond / Kilo, 0), "KB");
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

    private static double DivisorFor(MetricUnit unit, string suffix) => unit switch
    {
        MetricUnit.Bytes => suffix switch { "T" => TiB, "G" => GiB, "M" => MiB, _ => KiB },
        MetricUnit.BitRate => suffix switch { "Gb" => Giga / 8, "Mb" => Mega / 8, _ => Kilo / 8 },
        MetricUnit.ByteRate => suffix switch { "GB" => Giga, "MB" => Mega, _ => Kilo },
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

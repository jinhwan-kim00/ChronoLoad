using ChronoLoad.Core.Formatting;
using ChronoLoad.Core.Metrics;

namespace ChronoLoad.Core.Tests;

/// <summary>숫자를 사람이 읽는 문자열로 바꾸는 규칙.</summary>
public class FormatTests
{
    [Fact]
    public void Percent_shows_no_decimals_in_the_header()
    {
        var formatted = MetricFormatter.Format(MetricUnit.Percent, 47.6);
        Assert.Equal("48", formatted.Value);
        Assert.Equal("%", formatted.Unit);
    }

    [Fact]
    public void Missing_values_render_as_a_dash()
    {
        Assert.Equal("—", MetricFormatter.Format(MetricUnit.Percent, double.NaN).Value);
    }

    [Fact]
    public void Network_reads_in_bits_and_disk_in_bytes()
    {
        // 같은 바이트/초라도 관례가 다르다. 125 MB/s = 1 Gbps.
        const double bytesPerSecond = 125e6;

        Assert.Equal("Gbps", MetricFormatter.Format(MetricUnit.BitRate, bytesPerSecond).Unit);
        Assert.Equal("1.00", MetricFormatter.Format(MetricUnit.BitRate, bytesPerSecond).Value);
        Assert.Equal("MB/s", MetricFormatter.Format(MetricUnit.ByteRate, bytesPerSecond).Unit);
        Assert.Equal("125", MetricFormatter.Format(MetricUnit.ByteRate, bytesPerSecond).Value);
    }

    [Fact]
    public void Group_formatting_puts_every_value_on_the_same_unit()
    {
        // 평균 289M 옆에 최대 1.40G 가 오면 어느 쪽이 큰지 한눈에 안 들어온다.
        var (values, unit) = MetricFormatter.FormatGroup(MetricUnit.ByteRate, 289e6, 1.4e9, 50e6);

        Assert.Equal("GB/s", unit);
        Assert.Equal("0.29", values[0]);
        Assert.Equal("1.40", values[1]);
        Assert.Equal("0.05", values[2]);
    }

    [Theory]
    [InlineData(MetricUnit.Bytes, 8L * 1024 * 1024 * 1024)]
    [InlineData(MetricUnit.Bytes, 512L * 1024 * 1024)]
    [InlineData(MetricUnit.BitRate, 125e6)]
    [InlineData(MetricUnit.ByteRate, 1.4e9)]
    [InlineData(MetricUnit.ByteRate, 50e6)]
    public void Group_formatting_scales_the_same_way_single_values_do(MetricUnit unit, double value)
    {
        // FormatGroup 은 단위 기호를 다시 보고 나눗셈 크기를 고른다. 그 대조표가 포매터의
        // 기호와 한 글자라도 어긋나면 기본 가지로 떨어져 값이 1000배 틀린 채 멀쩡해 보인다.
        // 단위 기호를 바꿀 때 실제로 밟게 되는 지뢰라 여기서 묶어둔다.
        var single = MetricFormatter.Format(unit, value);
        var (values, groupUnit) = MetricFormatter.FormatGroup(unit, value);

        Assert.Equal(single.Unit, groupUnit);
        Assert.Equal(single.Value, values[0]);
    }

    [Fact]
    public void Unit_symbols_say_what_they_measure()
    {
        // `G` 하나만 붙여 두면 용량인지 속도인지, 바이트인지 비트인지 알 수 없다.
        // Wi-Fi 의 2.4Gbps 링크 속도가 2.4GHz 밴드로 읽힌 적이 있다.
        Assert.Equal("GB", MetricFormatter.Format(MetricUnit.Bytes, 2L * 1024 * 1024 * 1024).Unit);
        Assert.Equal("Gbps", MetricFormatter.Format(MetricUnit.BitRate, 250e6).Unit);
        Assert.Equal("GB/s", MetricFormatter.Format(MetricUnit.ByteRate, 2e9).Unit);

        // 크기와 속도가 같은 기호를 쓰면 카드 두 장을 나란히 뒀을 때 구분되지 않는다.
        Assert.NotEqual(MetricFormatter.Format(MetricUnit.Bytes, 2e9).Unit,
                        MetricFormatter.Format(MetricUnit.ByteRate, 2e9).Unit);
    }

    [Fact]
    public void Group_formatting_handles_gaps()
    {
        var (values, _) = MetricFormatter.FormatGroup(MetricUnit.Percent, 50, double.NaN, 10);
        Assert.Equal("—", values[1]);
    }

    [Fact]
    public void Bytes_switch_scale_at_binary_boundaries()
    {
        Assert.Equal("GB", MetricFormatter.Format(MetricUnit.Bytes, 8L * 1024 * 1024 * 1024).Unit);
        Assert.Equal("8.00", MetricFormatter.Format(MetricUnit.Bytes, 8L * 1024 * 1024 * 1024).Value);
        Assert.Equal("MB", MetricFormatter.Format(MetricUnit.Bytes, 512L * 1024 * 1024).Unit);
    }

    [Fact]
    public void Elapsed_switches_to_hours_past_the_hour_mark()
    {
        Assert.Equal("12:04", MetricFormatter.Elapsed(TimeSpan.FromSeconds(12 * 60 + 4)));
        Assert.Equal("1:02:03", MetricFormatter.Elapsed(new TimeSpan(1, 2, 3)));
    }

    [Theory]
    [InlineData(MetricKind.CpuTotal, MetricUnit.Percent)]
    [InlineData(MetricKind.NetRx, MetricUnit.BitRate)]
    [InlineData(MetricKind.DiskRead, MetricUnit.ByteRate)]
    [InlineData(MetricKind.MemUsed, MetricUnit.Bytes)]
    [InlineData(MetricKind.GpuTemp, MetricUnit.Celsius)]
    public void Metric_kinds_map_to_the_expected_unit(MetricKind kind, MetricUnit expected)
    {
        Assert.Equal(expected, kind.Unit());
    }
}

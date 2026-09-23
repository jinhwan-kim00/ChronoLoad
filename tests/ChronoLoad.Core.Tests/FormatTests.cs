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

        Assert.Equal("Gb", MetricFormatter.Format(MetricUnit.BitRate, bytesPerSecond).Unit);
        Assert.Equal("1.00", MetricFormatter.Format(MetricUnit.BitRate, bytesPerSecond).Value);
        Assert.Equal("MB", MetricFormatter.Format(MetricUnit.ByteRate, bytesPerSecond).Unit);
        Assert.Equal("125", MetricFormatter.Format(MetricUnit.ByteRate, bytesPerSecond).Value);
    }

    [Fact]
    public void Group_formatting_puts_every_value_on_the_same_unit()
    {
        // 평균 289M 옆에 최대 1.40G 가 오면 어느 쪽이 큰지 한눈에 안 들어온다.
        var (values, unit) = MetricFormatter.FormatGroup(MetricUnit.ByteRate, 289e6, 1.4e9, 50e6);

        Assert.Equal("GB", unit);
        Assert.Equal("0.29", values[0]);
        Assert.Equal("1.40", values[1]);
        Assert.Equal("0.05", values[2]);
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
        Assert.Equal("G", MetricFormatter.Format(MetricUnit.Bytes, 8L * 1024 * 1024 * 1024).Unit);
        Assert.Equal("8.00", MetricFormatter.Format(MetricUnit.Bytes, 8L * 1024 * 1024 * 1024).Value);
        Assert.Equal("M", MetricFormatter.Format(MetricUnit.Bytes, 512L * 1024 * 1024).Unit);
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

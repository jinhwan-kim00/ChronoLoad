using System.Text;
using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Metrics;

namespace ChronoLoad.Core.Tests;

/// <summary>
/// CSV 내보내기(§9.6). 여러 번의 테스트 결과를 나란히 놓고 비교하는 것이 쓰임새라,
/// 열 이름만으로 어느 파일의 무엇인지 갈려야 한다.
/// </summary>
public class SnapshotCsvTests
{
    private static readonly long Origin = new DateTime(2026, 9, 26, 14, 32, 7, DateTimeKind.Utc).Ticks;

    private static long At(double seconds) => Origin + (long)(seconds * TimeSpan.TicksPerSecond);

    private static MetricSnapshot Build(Action<MetricRegistry> fill, string name = "CPU")
    {
        var registry = new MetricRegistry(64);
        registry.Register(new DeviceInfo("cpu", DeviceClass.System, name, name, IconKind.Cpu),
                          [MetricKind.CpuTotal]);
        fill(registry);
        return MetricSnapshot.Capture(registry);
    }

    private static string[] Lines(string csv) =>
        csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void The_header_names_the_device_the_metric_and_the_base_unit()
    {
        var snapshot = Build(r => r.PushFrame([10f], At(0)));
        string[] lines = Lines(SnapshotCsv.Build(snapshot, 0, snapshot.Count));

        Assert.Equal("timestamp,CPU / CpuTotal / %", lines[0]);
    }

    [Fact]
    public void One_row_per_tick_with_the_local_time_first()
    {
        var snapshot = Build(r =>
        {
            r.PushFrame([10f], At(0));
            r.PushFrame([20f], At(0.25));
        });

        string[] lines = Lines(SnapshotCsv.Build(snapshot, 0, snapshot.Count));

        Assert.Equal(3, lines.Length);            // 헤더 + 2행
        string expected = new DateTime(At(0), DateTimeKind.Utc).ToLocalTime()
            .ToString("yyyy-MM-ddTHH:mm:ss.fff");
        Assert.StartsWith(expected + ",", lines[1]);
        Assert.EndsWith(",10", lines[1]);
        Assert.EndsWith(",20", lines[2]);
    }

    /// <summary>
    /// 유지값을 채워 내면 엑셀에서 평균을 내는 순간 틀린다. 빈 칸이면 엑셀이 알아서 건너뛴다.
    /// </summary>
    [Fact]
    public void Samples_that_were_not_measured_come_out_as_empty_cells()
    {
        var snapshot = Build(r =>
        {
            r.CommitAll([10f], [true], At(0));
            r.CommitAll([10f], [false], At(0.25));
        });

        string[] lines = Lines(SnapshotCsv.Build(snapshot, 0, snapshot.Count));

        Assert.EndsWith(",10", lines[1]);
        Assert.EndsWith(",", lines[2]);           // 값 자리가 비어 있다
    }

    [Fact]
    public void Only_the_requested_range_is_written()
    {
        var snapshot = Build(r =>
        {
            for (int i = 0; i < 6; i++) r.PushFrame([i * 10f], At(i * 0.25));
        });

        string[] lines = Lines(SnapshotCsv.Build(snapshot, 2, 4));

        Assert.Equal(3, lines.Length);            // 헤더 + 2행
        Assert.EndsWith(",20", lines[1]);
        Assert.EndsWith(",30", lines[2]);
    }

    /// <summary>
    /// 장치명에 쉼표가 들어가면(드물지만 있다) 열이 하나 밀려 그 아래 전부가 어긋난다.
    /// </summary>
    [Fact]
    public void A_comma_in_a_device_name_is_quoted_instead_of_breaking_the_columns()
    {
        var snapshot = Build(r => r.PushFrame([1f], At(0)), name: "Intel(R) Core, Ultra");
        string[] lines = Lines(SnapshotCsv.Build(snapshot, 0, snapshot.Count));

        Assert.Equal("timestamp,\"Intel(R) Core, Ultra / CpuTotal / %\"", lines[0]);
        Assert.Equal(2, lines[1].Split(',').Length is var _ ? lines[1].Split(',').Length : 0);
    }

    [Fact]
    public void A_quote_in_a_device_name_is_doubled()
    {
        var snapshot = Build(r => r.PushFrame([1f], At(0)), name: "그 \"빠른\" 디스크");
        Assert.Contains("\"\"빠른\"\"", Lines(SnapshotCsv.Build(snapshot, 0, snapshot.Count))[0]);
    }

    /// <summary>값은 화면처럼 배율을 바꾸지 않는다. 행마다 단위가 다르면 열을 계산할 수 없다.</summary>
    [Theory]
    [InlineData(MetricUnit.Percent, "%")]
    [InlineData(MetricUnit.Bytes, "bytes")]
    [InlineData(MetricUnit.BitRate, "bits/s")]
    [InlineData(MetricUnit.ByteRate, "bytes/s")]
    [InlineData(MetricUnit.Milliseconds, "ms")]
    [InlineData(MetricUnit.Scalar, "")]
    public void Base_units_are_fixed_not_scaled(MetricUnit unit, string expected)
    {
        Assert.Equal(expected, SnapshotCsv.BaseUnit(unit));
    }

    [Fact]
    public void A_unitless_metric_leaves_the_unit_out_of_the_header()
    {
        var registry = new MetricRegistry(16);
        registry.Register(new DeviceInfo("disk:0", DeviceClass.Disk, "SSD", "SSD", IconKind.DiskSsd),
                          [MetricKind.DiskQueue]);
        registry.PushFrame([0.5f], At(0));

        string[] lines = Lines(SnapshotCsv.Build(MetricSnapshot.Capture(registry), 0, 1));
        Assert.Equal("timestamp,SSD / DiskQueue", lines[0]);
    }

    [Fact]
    public void The_file_is_written_with_a_bom_so_excel_keeps_korean_headers()
    {
        var snapshot = Build(r => r.PushFrame([1f], At(0)), name: "중앙처리장치");
        string path = Path.Combine(Path.GetTempPath(), $"chronoload-csv-{Guid.NewGuid():N}.csv");
        try
        {
            SnapshotCsv.Save(snapshot, 0, snapshot.Count, path);

            byte[] head = File.ReadAllBytes(path)[..3];
            Assert.Equal([0xEF, 0xBB, 0xBF], head);
            Assert.Contains("중앙처리장치", File.ReadAllText(path, Encoding.UTF8));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void The_suggested_name_carries_the_start_time_and_the_length()
    {
        var snapshot = Build(r =>
        {
            r.PushFrame([1f], At(0));
            r.PushFrame([2f], At(900));
        });

        string name = SnapshotCsv.SuggestFileName(snapshot, 0, snapshot.Count);
        var start = new DateTime(At(0), DateTimeKind.Utc).ToLocalTime();

        Assert.Equal($"chronoload-{start:yyyyMMdd-HHmmss}-15m.csv", name);
    }

    [Fact]
    public void An_empty_range_still_produces_a_usable_name_and_just_a_header()
    {
        var snapshot = Build(r => r.PushFrame([1f], At(0)));

        Assert.Equal("chronoload.csv", SnapshotCsv.SuggestFileName(snapshot, 1, 1));
        Assert.Single(Lines(SnapshotCsv.Build(snapshot, 1, 1)));
    }
}

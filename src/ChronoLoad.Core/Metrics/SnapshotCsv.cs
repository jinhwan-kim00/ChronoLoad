using System.Globalization;
using System.Text;

namespace ChronoLoad.Core.Metrics;

/// <summary>
/// 스냅샷을 CSV 로 낸다 (§9.6). 여러 번의 테스트 결과를 나란히 놓고 비교하는 것이 쓰임새다.
/// </summary>
/// <remarks>
/// <para>
/// <c>.xlsx</c> 는 쓰기 라이브러리가 필요한데 §3.2 의 의존성 정책은 <b>차트 라이브러리조차
/// 쓰지 않는다</b>. 비교용 표 하나를 위해 그 선을 넘을 이유가 없고, CSV 는 엑셀이 그대로 연다.
/// </para>
/// <para>
/// 값은 <b>기본 단위 그대로</b> 낸다. 화면처럼 KB·MB·GB 로 눈금을 바꿔 적으면 행마다 배율이
/// 달라져 열 전체를 한 번에 계산할 수 없다. 배율을 고르는 것은 읽는 쪽 일이다.
/// </para>
/// </remarks>
public static class SnapshotCsv
{
    /// <summary>엑셀은 BOM 이 없으면 UTF-8 한글 헤더를 깨뜨린다.</summary>
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>
    /// 시각 표기. ISO 8601 이되 날짜와 시각 사이는 <c>T</c> 가 아니라 <b>공백</b>이다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 엑셀은 <c>T</c> 가 끼면 날짜로 읽지 않고 <b>그냥 글자로 둔다</b>(실측). 공백으로 바꾸면
    /// 날짜/시간 값이 되어 차트 축과 수식이 붙는다.
    /// </para>
    /// <para>
    /// 밀리초는 남긴다. 250ms 로 재므로 초까지만 적으면 네 줄이 같은 시각이 되어 시계열로
    /// 그릴 수 없다. 대신 엑셀이 소수 초를 보면 표시 형식을 <c>mm:ss.0</c> 으로 골라
    /// 날짜를 가린다 — 값은 멀쩡하니 셀 서식만 바꾸면 된다. 어떤 표기를 줘도 그렇다(실측).
    /// </para>
    /// </remarks>
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

    /// <summary>
    /// 값 표기. <b>지수 표기를 쓰지 않는다.</b>
    /// </summary>
    /// <remarks>
    /// 왕복 표기(<c>"R"</c>)는 바이트·전송률처럼 큰 수를 <c>1.3421773E+10</c> 로 적는다.
    /// 엑셀은 읽지만 사람은 읽지 않는다. 소수 6자리까지 열어 두면 지수가 사라지고, 디스크 큐처럼
    /// 아주 작은 값(<c>0.00001</c>)도 0 으로 뭉개지지 않는다. 자리를 늘려도 없는 정밀도가
    /// 생기지는 않는다 — <see langword="float"/> 은 유효숫자 7자리이고 서식도 거기까지만 적는다.
    /// </remarks>
    private const string ValueFormat = "0.######";

    /// <summary>열 이름에 붙는 기본 단위. 화면 표기가 아니라 <b>저장 단위</b>다.</summary>
    public static string BaseUnit(MetricUnit unit) => unit switch
    {
        MetricUnit.Percent => "%",
        MetricUnit.Bytes => "bytes",
        MetricUnit.BitRate => "bits/s",
        MetricUnit.ByteRate => "bytes/s",
        MetricUnit.Celsius => "C",
        MetricUnit.Watt => "W",
        MetricUnit.Megahertz => "MHz",
        MetricUnit.Milliseconds => "ms",
        _ => "",
    };

    /// <summary>
    /// <paramref name="from"/> 이상 <paramref name="to"/> 미만 구간을 CSV 문자열로 만든다.
    /// </summary>
    public static string Build(MetricSnapshot snapshot, int from, int to)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        from = Math.Max(0, from);
        to = Math.Min(snapshot.Count, to);

        var text = new StringBuilder();

        text.Append("timestamp");
        foreach (var metric in snapshot.Metrics)
        {
            text.Append(',');
            // 여러 파일을 나란히 놓고 비교할 때 열 이름만으로 갈려야 한다.
            string unit = BaseUnit(metric.Unit);
            text.Append(Escape(unit.Length == 0
                ? $"{metric.Device.ShortName} / {metric.Kind}"
                : $"{metric.Device.ShortName} / {metric.Kind} / {unit}"));
        }
        text.Append('\n');

        for (int i = from; i < to; i++)
        {
            text.Append(new DateTime(snapshot.Timestamps[i], DateTimeKind.Utc)
                .ToLocalTime().ToString(TimeFormat, CultureInfo.InvariantCulture));

            foreach (var metric in snapshot.Metrics)
            {
                text.Append(',');

                // 실측이 아닌 샘플은 빈 칸이다. 유지값을 채워 내면 엑셀에서 평균을 내는
                // 순간 틀린다 — 앱 안에서 통계에 넣지 않는 것과 같은 이유다(§7.2).
                if (!metric.Measured[i]) continue;
                float value = metric.Values[i];
                if (float.IsNaN(value)) continue;

                text.Append(value.ToString(ValueFormat, CultureInfo.InvariantCulture));
            }
            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>구간을 파일로 낸다. UTF-8 <b>BOM 포함</b>.</summary>
    public static void Save(MetricSnapshot snapshot, int from, int to, string path) =>
        File.WriteAllText(path, Build(snapshot, from, to), Utf8WithBom);

    /// <summary>
    /// 기본 파일명. 시작 시각과 길이로 구분된다 — 여러 번 낸 파일이 한 폴더에 쌓인다.
    /// </summary>
    public static string SuggestFileName(MetricSnapshot snapshot, int from, int to)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        from = Math.Max(0, from);
        to = Math.Min(snapshot.Count, to);

        if (to <= from) return "chronoload.csv";

        var start = new DateTime(snapshot.Timestamps[from], DateTimeKind.Utc).ToLocalTime();
        var span = TimeSpan.FromTicks(snapshot.Timestamps[to - 1] - snapshot.Timestamps[from]);

        return $"chronoload-{start:yyyyMMdd-HHmmss}-{Length(span)}.csv";
    }

    private static string Length(TimeSpan span) =>
        span.TotalSeconds < 60 ? $"{span.TotalSeconds:0}s"
        : span.TotalMinutes < 60 ? $"{span.TotalMinutes:0}m"
        : $"{span.TotalHours:0}h";

    /// <summary>
    /// 쉼표·따옴표·줄바꿈이 든 값은 감싼다. 장치명에 쉼표가 들어가면(드물지만 있다)
    /// 열이 하나 밀려 그 아래 전부가 어긋난다.
    /// </summary>
    private static string Escape(string value) =>
        value.AsSpan().IndexOfAny(",\"\n\r") < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\"";
}

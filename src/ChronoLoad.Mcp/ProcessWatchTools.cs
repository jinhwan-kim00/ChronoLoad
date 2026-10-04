using System.ComponentModel;
using ChronoLoad.Core.Metrics;
using ChronoLoad.Sensors;
using ModelContextProtocol.Server;

namespace ChronoLoad.Mcp;

/// <summary>
/// 프로세스별 시계열 (§10.2). <c>get_process_detail</c> 은 한 순간이라, 버스트형 부하에서
/// "그 프로세스가 언제 어느 엔진을 썼나"를 볼 수 없었다(실사용 요청).
/// </summary>
/// <remarks>
/// 기록은 <see cref="ProcessWatch"/> 가 1초마다 한다. 엔진 값은 PDH 라 긴 작업이 끝날 때 몰아서 계상될 수 있다 —
/// 어댑터 전체의 하드웨어 카운터(<c>GpuRenderCompute</c>, <c>get_metric_history</c>)와 나란히 읽는다.
/// </remarks>
[McpServerToolType]
public sealed class ProcessWatchTools(McpContext ctx)
{
    [McpServerTool(Name = "watch_process")]
    [Description("프로세스 하나의 GPU 엔진 계열(어댑터별 3D·Compute·Copy·Video)·CPU·워킹셋을 1초마다 기록하기 시작한다. 이미 감시 중이면 기한만 늘린다. 최대 8개, 최대 1시간, 최근 15분을 보관한다. 기록은 get_process_history 로 읽는다.")]
    public object WatchProcess(
        [Description("프로세스 ID.")] int pid,
        [Description("기록할 시간(초). 기본 600, 최대 3600.")] int durationSeconds = 600)
    {
        if (ctx.ProcessWatch is not { } watch) return Unavailable();

        var (info, error) = watch.Watch(pid, TimeSpan.FromSeconds(Math.Clamp(durationSeconds, 1, 3600)));
        if (info is null) return ChronoLoadTools.Error("watch_failed", error ?? "감시를 걸 수 없다.");

        return new
        {
            header = MetricReader.Header(ctx),
            watching = Describe(info),
            periodMs = 1000,
            watched = watch.List().Select(Describe).ToArray(),
        };
    }

    [McpServerTool(Name = "unwatch_process")]
    [Description("프로세스 감시를 풀고 기록을 버린다.")]
    public object UnwatchProcess([Description("프로세스 ID.")] int pid)
    {
        if (ctx.ProcessWatch is not { } watch) return Unavailable();
        if (!watch.Unwatch(pid))
            return ChronoLoadTools.Error("not_watched", $"PID {pid} 는 감시 중이 아니다.");

        return new { header = MetricReader.Header(ctx), watched = watch.List().Select(Describe).ToArray() };
    }

    [McpServerTool(Name = "get_process_history")]
    [Description("watch_process 로 기록한 프로세스 시계열. 시각(startAt + offsetsMs)과 CPU%·워킹셋·어댑터별 엔진 계열 사용률. 점이 maxPoints 보다 많으면 시간으로 등분해 평균과 최대를 준다. 프로세스가 끝나도 기록은 남는다(ended=true).")]
    public object GetProcessHistory(
        [Description("프로세스 ID.")] int pid,
        [Description("조회 구간(초). 최대 900.")] int windowSeconds = 900,
        [Description("최대 점 개수. 최대 500.")] int maxPoints = 300)
    {
        if (ctx.ProcessWatch is not { } watch) return Unavailable();
        if (watch.Get(pid) is not { } history)
            return ChronoLoadTools.Error("not_watched", $"PID {pid} 는 감시 중이 아니다. watch_process 로 먼저 건다.");

        windowSeconds = Math.Clamp(windowSeconds, 1, 900);
        maxPoints = Math.Clamp(maxPoints, 2, 500);

        int first = 0;
        if (history.Stamps.Length > 0)
        {
            long cutoff = history.Stamps[^1] - TimeSpan.FromSeconds(windowSeconds).Ticks;
            while (first < history.Stamps.Length && history.Stamps[first] < cutoff) first++;
        }

        var stamps = history.Stamps[first..];
        var buckets = Buckets(stamps, maxPoints, out long width);
        long start = stamps.Length > 0 ? stamps[0] : 0;

        return new
        {
            header = MetricReader.Header(ctx),
            process = Describe(new ProcessWatch.WatchInfo(history.Pid, history.Name, history.StartedUtcTicks,
                history.UntilUtcTicks, history.Ended, history.Stamps.Length)),
            startAt = stamps.Length > 0 ? McpJsonHelpers.Iso(start) : null,
            mode = buckets is null ? "raw" : "bucketed",
            bucketMs = buckets is null ? (double?)null : width / (double)TimeSpan.TicksPerMillisecond,
            pointCount = buckets is null ? stamps.Length : maxPoints,
            offsetsMs = buckets is null
                ? stamps.Select(s => (s - start) / TimeSpan.TicksPerMillisecond).ToArray()
                : Enumerable.Range(0, maxPoints).Select(b => b * width / TimeSpan.TicksPerMillisecond).ToArray(),
            cpuPercent = Series(history.CpuPercent[first..], buckets, maxPoints),
            workingSetBytes = Series(history.WorkingSetBytes[first..], buckets, maxPoints),
            // 어댑터·계열마다 한 줄. 처음 쓰기 전의 칸과 엔진 값을 못 쓴 칸(상한 초과·깨진 카운터)은 null(값 없음), 쓰지 않은 칸은 0 이다.
            gpu = history.Engines
                .OrderBy(e => e.Key.AdapterKey).ThenBy(e => e.Key.Family)
                .Select(e => new
                {
                    adapterKey = e.Key.AdapterKey,
                    engine = e.Key.Family switch
                    {
                        MetricKind.Gpu3D => "3D",
                        MetricKind.GpuCompute => "Compute",
                        MetricKind.GpuCopy => "Copy",
                        _ => "Video",
                    },
                    values = Series(e.Value[first..], buckets, maxPoints),
                }).ToArray(),
        };
    }

    private static object Unavailable() => ChronoLoadTools.Error("process_watch_unavailable",
        "이 빌드에서는 프로세스 감시를 쓸 수 없다(GPU 수집이 없다).");

    private static object Describe(ProcessWatch.WatchInfo w) => new
    {
        pid = w.Pid,
        name = w.Name,
        since = McpJsonHelpers.Iso(w.StartedUtcTicks),
        until = McpJsonHelpers.Iso(w.UntilUtcTicks),
        // 프로세스가 끝났거나 기한이 지나 더 적지 않는다. 기록은 남아 있다.
        ended = w.Ended,
        points = w.Points,
    };

    /// <summary>등분할 필요가 없으면 null. 있으면 점마다 들어갈 칸 번호.</summary>
    private static int[]? Buckets(long[] stamps, int maxPoints, out long width)
    {
        width = 0;
        if (stamps.Length <= maxPoints) return null;

        long start = stamps[0], end = stamps[^1];
        width = Math.Max(1, (end - start + maxPoints) / maxPoints);
        var index = new int[stamps.Length];
        for (int i = 0; i < stamps.Length; i++)
            index[i] = (int)Math.Min(maxPoints - 1, (stamps[i] - start) / width);
        return index;
    }

    /// <summary>원값이면 <c>values</c> 하나, 등분이면 칸마다 <c>avg</c>·<c>max</c>. 값 없음은 null.</summary>
    private static object Series(float[] values, int[]? buckets, int count)
    {
        if (buckets is null) return new { values = values.Select(v => McpJsonHelpers.Finite(v)).ToArray() };

        var sum = new double[count];
        var n = new int[count];
        var max = new double?[count];
        for (int i = 0; i < values.Length; i++)
        {
            if (!float.IsFinite(values[i])) continue;
            int b = buckets[i];
            sum[b] += values[i];
            n[b]++;
            max[b] = max[b] is { } m ? Math.Max(m, values[i]) : values[i];
        }

        return new
        {
            avg = Enumerable.Range(0, count).Select(b => n[b] == 0 ? null : McpJsonHelpers.Finite(sum[b] / n[b])).ToArray(),
            max = max.Select(m => m is { } v ? McpJsonHelpers.Finite(v) : null).ToArray(),
        };
    }
}

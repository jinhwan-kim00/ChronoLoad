using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ChronoLoad.Mcp;

/// <summary>
/// 프로세스 관련 툴 (§10.2). 수집은 <b>요청이 있을 때만</b> 돈다 (§5.6).
/// </summary>
/// <remarks>
/// 프로세스를 <b>끝내거나 우선순위를 바꾸는 툴은 없다.</b> 이 앱은 관측 도구이고,
/// 에이전트에게 시스템을 바꿀 손잡이를 쥐여주면 사고의 폭이 관측의 가치보다 커진다 (§10.1).
/// </remarks>
[McpServerToolType]
public sealed class ProcessTools(McpContext ctx)
{
    [McpServerTool(Name = "list_processes")]
    [Description("무엇이 자원을 쓰고 있는지. CPU·메모리·GPU·GPU 메모리·디스크 I/O 로 정렬할 수 있다.")]
    public object ListProcesses(
        [Description("정렬 기준: cpu | memory | gpu | gpuMemory | diskIo")] string sortBy = "cpu",
        [Description("이 어댑터 키의 GPU 사용만으로 정렬한다. gpu · gpuMemory 정렬에만 쓰인다.")]
        string? adapterKey = null,
        [Description("반환 개수. 최대 50.")] int limit = 15,
        [Description("이름에 이 문자열을 포함하는 프로세스만. 대소문자 무시.")] string? nameFilter = null)
    {
        if (ctx.Processes is not { } source) return Unavailable();

        source.KeepAlive();
        var rows = source.Snapshot();

        if (rows.Count == 0)
            return ChronoLoadTools.Error("collecting",
                "프로세스 수집을 막 시작했다. 2초 뒤 다시 호출하면 값이 채워져 있다.");

        if (!string.IsNullOrWhiteSpace(nameFilter))
            rows = rows.Where(r => r.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)).ToArray();

        double GpuOf(ProcessRow r) => adapterKey is null
            ? r.TotalGpuPercent
            : r.GpuByAdapter.GetValueOrDefault(adapterKey);

        long GpuMemoryOf(ProcessRow r) => adapterKey is null
            ? r.TotalGpuMemoryBytes
            : r.GpuMemoryByAdapter.GetValueOrDefault(adapterKey);

        var sorted = sortBy.ToLowerInvariant() switch
        {
            "memory" => rows.OrderByDescending(r => r.WorkingSetBytes),
            "gpu" => rows.OrderByDescending(GpuOf),
            "gpumemory" => rows.OrderByDescending(GpuMemoryOf),
            "diskio" => rows.OrderByDescending(r => r.DiskBytesPerSecond),
            _ => rows.OrderByDescending(r => r.CpuPercent),
        };

        return new
        {
            header = MetricReader.Header(ctx),
            sortedBy = sortBy,
            adapterKey,
            totalProcesses = source.Snapshot().Count,
            collectedAt = source.SampledAt is { } at ? McpJsonHelpers.Iso(at) : null,
            processes = sorted.Take(Math.Clamp(limit, 1, 50)).Select(Summary).ToArray(),
        };
    }

    [McpServerTool(Name = "get_process_detail")]
    [Description("프로세스 하나의 상세. 어댑터별·엔진별 GPU 사용률과 경로·부모 PID 를 포함한다.")]
    public object GetProcessDetail([Description("프로세스 ID.")] int pid)
    {
        if (ctx.Processes is not { } source) return Unavailable();

        source.KeepAlive();
        var row = source.Snapshot().FirstOrDefault(r => r.Pid == pid);

        if (row is null)
            return ChronoLoadTools.Error("process_not_found",
                $"PID {pid} 를 찾지 못했다. 이미 종료됐거나 수집이 막 시작됐을 수 있다.");

        return new
        {
            header = MetricReader.Header(ctx),
            collectedAt = source.SampledAt is { } at ? McpJsonHelpers.Iso(at) : null,
            process = new
            {
                pid = row.Pid,
                name = row.Name,
                parentPid = row.ParentPid,
                // 경로·명령줄은 권한이 있어야 읽힌다. 없으면 null 로 두고 거짓말하지 않는다.
                executablePath = source.ResolvePath(pid),
                commandLine = row.CommandLine,
                cpuPercent = row.CpuPercent,
                workingSet = ByteValue.From(row.WorkingSetBytes),
                diskBytesPerSecond = row.DiskBytesPerSecond,
                gpuByAdapter = row.GpuByAdapter,
                gpuMemoryByAdapter = row.GpuMemoryByAdapter.ToDictionary(
                    kv => kv.Key, kv => ByteValue.From(kv.Value)),
                gpuByEngine = row.GpuByEngine,
            },
        };
    }

    private static object Unavailable() => ChronoLoadTools.Error("processes_unavailable",
        "이 빌드에서는 프로세스 수집을 사용할 수 없다. describe_capabilities 로 확인한다.");

    private static object Summary(ProcessRow r) => new
    {
        pid = r.Pid,
        name = r.Name,
        cpuPercent = r.CpuPercent,
        workingSet = ByteValue.From(r.WorkingSetBytes),
        diskBytesPerSecond = r.DiskBytesPerSecond,
        gpuPercent = Math.Round(r.TotalGpuPercent, 2),
        gpuMemory = ByteValue.From(r.TotalGpuMemoryBytes),
        gpuByAdapter = r.GpuByAdapter.Count == 0 ? null : r.GpuByAdapter,
    };
}

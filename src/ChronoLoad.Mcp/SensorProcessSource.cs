using System.Diagnostics;
using ChronoLoad.Sensors;

namespace ChronoLoad.Mcp;

/// <summary>
/// <see cref="ProcessProvider"/> 를 MCP 툴이 쓰는 모양으로 옮긴다.
/// </summary>
/// <remarks>
/// 경로와 명령줄은 여기서만 붙인다. 프로세스 <b>목록</b>에 넣으면 수백 개마다 핸들을 열어야 해서
/// "요청이 있을 때만 가볍게 돈다"는 전제가 깨진다 — 상세 조회 한 건에서만 감당할 비용이다.
/// </remarks>
public sealed class SensorProcessSource(ProcessProvider provider) : IProcessSource
{
    public void KeepAlive() => provider.KeepAlive();

    public Task KeepAliveAsync(CancellationToken cancellationToken = default) =>
        provider.KeepAliveAsync(cancellationToken);

    public DateTimeOffset? SampledAt => provider.SampledAt;

    public IReadOnlyList<ProcessRow> Snapshot() =>
        provider.Snapshot().Select(Convert).ToArray();

    private static ProcessRow Convert(ProcessSample s) => new(
        s.Pid, s.Name, s.CpuPercent, s.WorkingSetBytes, s.DiskBytesPerSecond,
        s.GpuByAdapter, s.GpuMemoryByAdapter)
    {
        ParentPid = s.ParentPid,
        GpuByEngine = s.GpuByEngine,
    };

    /// <summary>
    /// 실행 파일 경로. 권한이 없거나 이미 끝난 프로세스면 null 이다 —
    /// 접근할 수 없다는 사실을 빈 문자열로 뭉개지 않는다.
    /// </summary>
    public string? ResolvePath(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }
}

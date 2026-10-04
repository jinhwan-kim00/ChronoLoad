using ChronoLoad.Core.Metrics;
using ChronoLoad.Core.Sampling;

namespace ChronoLoad.Mcp;

/// <summary>
/// 툴이 읽는 살아 있는 상태. 앱이 이걸 채워 서버에 넘긴다.
/// </summary>
/// <remarks>
/// <para>
/// <b>MCP 는 UI 와 통계 채널이 분리되어 있다</b>(§10, R16). 화면의 리셋 버튼은
/// <see cref="StatsScope.Ui"/> 만 건드리고, 여기서는 <see cref="StatsScope.Mcp"/> 만 쓴다.
/// 에이전트가 측정 구간을 잡아둔 사이에 사용자가 화면을 리셋해도 에이전트의 구간은 살아 있어야 한다.
/// </para>
/// <para>
/// 앱이 실행 중일 때만 존재한다. 서버 자체가 앱 안에서 돌기 때문에,
/// "앱이 없다"는 상황은 이 타입이 아니라 브리지가 처리한다 (§10.1).
/// </para>
/// </remarks>
public sealed class McpContext(MetricRegistry registry, SampleEngine engine)
{
    public const StatsScope Scope = StatsScope.Mcp;

    public MetricRegistry Registry { get; } = registry;
    public SampleEngine Engine { get; } = engine;

    /// <summary>어댑터별로 어떤 센서 계층이 붙었는지. <c>describe_capabilities</c> 가 보고한다.</summary>
    /// <remarks>키는 장치 키(<c>gpu:luid_…</c>)다. 이름으로 잡으면 같은 모델 두 장이 겹친다.</remarks>
    public Func<IReadOnlyDictionary<string, string>>? TelemetryLayers { get; init; }

    /// <summary>
    /// 어댑터의 엔진 종류(engtype)별 최근 사용률과 카운터가 깨진 종류. <c>get_gpu_status</c> 가 보고한다.
    /// 인자는 장치 키, 모르면 null.
    /// </summary>
    public Func<string, ChronoLoad.Sensors.GpuEngineBreakdown?>? EngineBreakdown { get; init; }

    /// <summary>어댑터의 최근 클럭 제한 사유. 인자는 장치 키, 벤더가 주지 않으면 null.</summary>
    public Func<string, GpuLimitReasons?>? LimitReasons { get; init; }

    /// <summary>프로세스 목록 제공자. 없으면 프로세스 툴이 <c>unavailable</c> 을 돌려준다.</summary>
    public IProcessSource? Processes { get; init; }

    /// <summary>프로세스별 시계열 기록기(<c>watch_process</c>). 없으면 그 툴들이 unavailable 을 돌려준다.</summary>
    public ChronoLoad.Sensors.ProcessWatch? ProcessWatch { get; init; }

    /// <summary>구간 마커(<c>mark</c>). 앱 메모리에만 있다.</summary>
    public MarkerBook Markers { get; } = new();

    /// <summary>샘플 주기. 응답의 시간 해상도를 에이전트가 알아야 한다.</summary>
    public TimeSpan SamplePeriod { get; init; } = TimeSpan.FromMilliseconds(250);

    public DateTimeOffset Now => DateTimeOffset.Now;
}

/// <summary>프로세스 표 한 줄. 어댑터별 GPU 사용은 <see cref="GpuByAdapter"/> 로 분해된다.</summary>
/// <remarks>CPU·디스크는 차분이라 직전 수집에 없던 프로세스는 null(아직 모름)이다.</remarks>
public sealed record ProcessRow(
    int Pid,
    string Name,
    double? CpuPercent,
    long WorkingSetBytes,
    double? DiskBytesPerSecond,
    IReadOnlyDictionary<string, double> GpuByAdapter,
    IReadOnlyDictionary<string, long> GpuMemoryByAdapter)
{
    public string? ExecutablePath { get; init; }
    public string? CommandLine { get; init; }
    public int? ParentPid { get; init; }

    /// <summary>엔진 종류별 사용률(3D·Compute·Copy·Video·Neural). 상세 조회에서만 채운다.</summary>
    public IReadOnlyDictionary<string, double>? GpuByEngine { get; init; }

    /// <summary>
    /// 어댑터 키 → 이번 수집에서 측정할 수 없던 엔진 종류(값이 상한을 넘었거나 카운터가 깨졌다). 그 어댑터·엔진은 위 표에서 빠져 있다 —
    /// 0 이 아니라 모르는 것이다.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? GpuUnmeasured { get; init; }

    public double TotalGpuPercent => GpuByAdapter.Count == 0 ? 0 : GpuByAdapter.Values.Max();
    public long TotalGpuMemoryBytes => GpuMemoryByAdapter.Values.Sum();
}

/// <summary>
/// 프로세스 수집기. <b>요청이 있을 때만 돈다</b> (§5.6) — 아무도 묻지 않는 동안
/// 전 프로세스를 2초마다 훑는 것은 "부하를 주지 않는다"는 이 앱의 전제를 깬다.
/// </summary>
public interface IProcessSource
{
    /// <summary>지금 목록이 필요하다고 알린다. 수집이 꺼져 있었다면 켠다.</summary>
    void KeepAlive();

    /// <summary>
    /// <see cref="KeepAlive"/> 와 같되, 수집을 새로 켰으면 첫 실측까지 기다린다 —
    /// 기준선만 잡힌 표는 비율이 전부 null 이라 쓸모가 없다.
    /// </summary>
    Task KeepAliveAsync(CancellationToken cancellationToken = default)
    {
        KeepAlive();
        return Task.CompletedTask;
    }

    /// <summary>마지막으로 수집한 표. 아직 한 번도 수집하지 않았으면 비어 있다.</summary>
    IReadOnlyList<ProcessRow> Snapshot();

    /// <summary>마지막 수집 시각. 한 번도 없으면 null.</summary>
    DateTimeOffset? SampledAt { get; }

    /// <summary>
    /// 실행 파일 경로. <b>상세 조회에서만 부른다</b> — 프로세스마다 핸들을 여는 비용이라
    /// 목록 전체에 적용하면 "요청이 있을 때만 가볍게 돈다"는 전제가 깨진다.
    /// 권한이 없으면 null.
    /// </summary>
    string? ResolvePath(int pid) => null;
}

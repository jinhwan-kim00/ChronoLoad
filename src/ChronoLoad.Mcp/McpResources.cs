using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace ChronoLoad.Mcp;

/// <summary>
/// 툴 호출 없이 붙여 넣을 수 있는 읽기 전용 자료 (§10.4).
/// </summary>
/// <remarks>
/// 툴과 같은 내용을 왜 리소스로도 내는가 — 클라이언트가 대화 맥락에 <b>첨부</b>할 수 있기 때문이다.
/// "지금 상태를 보고 판단해줘" 같은 요청에서 에이전트가 툴을 고를 필요 없이 바로 읽는다.
/// </remarks>
[McpServerResourceType]
public sealed class McpResources(McpContext ctx)
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    [McpServerResource(UriTemplate = "chronoload://snapshot", Name = "시스템 스냅샷",
        MimeType = "application/json")]
    [Description("CPU·메모리·GPU·디스크·네트워크의 현재 값.")]
    public string Snapshot() =>
        JsonSerializer.Serialize(new ChronoLoadTools(ctx).GetSystemSnapshot(), Pretty);

    [McpServerResource(UriTemplate = "chronoload://stats", Name = "리셋 이후 통계",
        MimeType = "application/json")]
    [Description("MCP 스코프의 리셋 이후 누적 통계. 전 지표.")]
    public string Stats() =>
        JsonSerializer.Serialize(new ChronoLoadTools(ctx).GetStatsSinceReset(), Pretty);
}

/// <summary>GPU 워크로드 진단 템플릿 (§10.4).</summary>
[McpServerPromptType]
public sealed class McpPrompts
{
    [McpServerPrompt(Name = "analyze_gpu_workload")]
    [Description("GPU 워크로드가 어디서 막히는지 — 연산·VRAM·디스크 I/O·네트워크 중 무엇인지 진단한다.")]
    public static string AnalyzeGpuWorkload(
        [Description("분석할 어댑터 키. 생략하면 가장 바쁜 어댑터.")] string? adapterKey = null,
        [Description("관심 있는 작업이나 프로세스 이름. 예: 학습 스크립트, 렌더러.")] string? workload = null)
    {
        string target = adapterKey is null ? "가장 바쁜 어댑터" : $"어댑터 `{adapterKey}`";
        string subject = workload is null ? "" : $"\n관심 대상은 \"{workload}\" 이다.\n";

        return $"""
            ChronoLoad 로 {target} 의 병목을 진단한다.{subject}
            다음 순서로 확인한다.

            1. `describe_capabilities` — 어떤 어댑터가 있고 어떤 센서 계층이 붙었는지 본다.
               계층이 `PDH` 인 어댑터는 온도·전력·클럭이 없다. 값이 비는 것과 0 인 것을 혼동하지 않는다.
            2. `get_gpu_status` — 사용률, 전용/공유 메모리, `vramExceeded`, 온도, 전력, 클럭.
            3. `list_processes` 를 `sortBy: "gpu"` 와 `"gpuMemory"` 로 각각 불러 무엇이 쓰고 있는지 본다.
            4. `get_metric_history` 로 `GpuUtil` 과 `GpuDedicated` 의 최근 추이를 본다.
               평균만 보면 주기적인 정체(stall)가 보이지 않는다.
            5. 사용률이 낮은데 느리다면 GPU 가 병목이 아니다.
               `get_disk_status` 와 `get_network_interfaces` 로 데이터 공급 쪽을 확인한다.

            판단 기준
            - `vramExceeded` 가 true 이거나 공유 메모리가 늘고 있으면 **VRAM 부족**이다.
              전용 메모리를 넘긴 할당은 시스템 메모리로 넘어가고 그 순간 속도가 무너진다.
            - 사용률이 100% 에 붙어 있고 클럭이 유지되면 **연산 한계**다.
            - 사용률이 톱니처럼 오르내리고 디스크 읽기가 그 골에 맞물리면 **입력 파이프라인**이 원인이다.
            - 온도가 높고 클럭이 떨어지면 **발열 제한**이다. 전력이 한계에 붙어 있으면 전력 제한이다.

            수치를 인용할 때는 `sampledAt` 과 측정 구간을 함께 밝힌다.
            값이 없는 항목은 0 으로 적지 말고 "측정할 수 없음"이라고 쓴다.
            """;
    }
}

using System.Text.Json.Serialization;
using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Metrics;

namespace ChronoLoad.Mcp;

/// <summary>
/// 이 어댑터에서 AI 작업이 <b>어느 지표에 나타나는지</b> (§5.4 · §10.2).
/// </summary>
/// <param name="Primary">연산 부하가 실리는 지표. 이 이름으로 <c>get_metric_history</c> 를 부른다.</param>
/// <param name="Supporting">병목을 가르는 데 함께 보는 지표 — 대역폭, 호스트↔장치 전송, 메모리.</param>
/// <param name="Note">왜 그 지표인가. 에이전트가 사용자에게 그대로 옮길 수 있는 한 줄이다.</param>
/// <remarks>
/// <para>
/// 같은 "AI 추론"이 GPU 마다 다른 엔진 이름으로 잡힌다. WDDM 엔진 종류에 연산이 없어서
/// <c>Compute</c>·<c>Cuda</c>·<c>Neural</c> 이 전부 드라이버가 붙인 이름이기 때문이다. 그래서
/// 에이전트가 "GpuCompute 가 0 이니 AI 는 안 돈다"고 읽는 일이 생긴다 — HAGS 가 켜진 NVIDIA 에서
/// CUDA 는 3D 로 잡히므로 실제로 그렇게 오판한다. 이 표가 그 오판을 막는다.
/// </para>
/// <para>
/// 규칙은 제조사와 HAGS 로 정한다. 엔진 종류를 보고 정하지 않는 것은, PDH 엔진 인스턴스가 그 엔진을
/// 쓰는 프로세스가 있을 때만 나타나서 유휴 때는 판단할 근거가 없기 때문이다. 지금 실제로 무엇이
/// 움직이는지는 <c>get_gpu_status(verbose)</c> 의 <c>engineTypes</c> 가 보여 준다.
/// </para>
/// </remarks>
public sealed record AiSignals(
    [property: JsonPropertyName("primary")] string[] Primary,
    [property: JsonPropertyName("supporting")] string[] Supporting,
    [property: JsonPropertyName("note")] string Note)
{
    public static AiSignals For(DeviceHandle device)
    {
        var info = device.Info;
        bool? hags = info.Extra.GetValueOrDefault("hardwareScheduling") switch
        {
            "true" => true,
            "false" => false,
            _ => null,
        };
        bool discrete = info.Extra.GetValueOrDefault("discrete") == "true";

        string[] Present(params MetricKind[] kinds) =>
            kinds.Where(k => device.SlotOf(k) >= 0).Select(k => k.ToString()).ToArray();

        // 전력 한도에 붙어 클럭이 깎이는 것이 AI 부하의 흔한 천장이다. 실측: RTX 5080 CUDA 부하 360 W / 한도 360 W.
        string[] Limits() => Present(MetricKind.GpuThrottlePower, MetricKind.GpuThrottleThermal,
            MetricKind.GpuThrottleOther, MetricKind.GpuPower, MetricKind.GpuPowerLimit, MetricKind.GpuClock);

        string[] memory = discrete
            ? Present(MetricKind.GpuDedicated, MetricKind.GpuShared)
            : Present(MetricKind.GpuShared, MetricKind.GpuDedicated);

        if (info.Icon == IconKind.Npu)
            return new AiSignals(Present(MetricKind.GpuCompute, MetricKind.GpuUtil), memory,
                "NPU 는 Neural 엔진 하나뿐이다. GpuCompute 가 곧 그 엔진의 사용률이다.");

        switch (info.Vendor)
        {
            case "NVIDIA" when hags != false:
                return new AiSignals(
                    Present(MetricKind.GpuUtil, MetricKind.Gpu3D),
                    [.. Present(MetricKind.GpuMemBusy, MetricKind.GpuCopy, MetricKind.GpuPcieRx, MetricKind.GpuPcieTx),
                     .. memory, .. Limits()],
                    (hags == true ? "하드웨어 가속 GPU 예약(HAGS)이 켜져 있어" : "HAGS 상태를 모르지만 Windows 11 기본값이 켜짐이라") +
                    " CUDA 는 Compute 가 아니라 3D 엔진으로 잡힌다 — GpuCompute 가 0 이어도 AI 가 안 도는 것이 아니다." +
                    " GpuUtil 은 NVML 의 커널 실행 시간(SM)이라 그래픽과 섞이지 않는다." +
                    " GpuMemBusy(메모리 컨트롤러)가 높고 GpuUtil 이 낮으면 연산이 아니라 VRAM 대역폭이 병목이다." +
                    " GpuCopy 는 복사 엔진이 바쁜 시간이고 실제로 오간 바이트는 GpuPcieRx(호스트→GPU)·GpuPcieTx(GPU→호스트)다.");

            case "NVIDIA":
                return new AiSignals(
                    Present(MetricKind.GpuUtil, MetricKind.GpuCompute),
                    [.. Present(MetricKind.GpuMemBusy, MetricKind.GpuCopy, MetricKind.GpuPcieRx, MetricKind.GpuPcieTx,
                        MetricKind.Gpu3D), .. memory, .. Limits()],
                    "HAGS 가 꺼져 있어 CUDA 가 Compute_0·Compute_1·Cuda 엔진으로 따로 잡힌다(GpuCompute)." +
                    " DirectML 은 3D 큐를 쓰기도 한다. GpuMemBusy 가 높고 GpuUtil 이 낮으면 VRAM 대역폭 병목이다.");

            case "Intel":
                return new AiSignals(
                    Present(MetricKind.GpuRenderCompute, MetricKind.GpuCompute, MetricKind.Gpu3D, MetricKind.GpuUtil),
                    [.. Present(MetricKind.GpuCopy), .. memory, .. Limits()],
                    (discrete
                        ? "Arc 외장은 OpenVINO·oneAPI 연산이 Compute(CCS) 엔진에 실린다. 커널에 따라 3D(렌더) 엔진을 쓰기도 한다." +
                          " GpuCopy 는 호스트↔VRAM 전송이다."
                        : "내장 Arc 는 Neural 엔진이 Compute 자리를 대신한다 — GpuCompute 에 합쳐 센다." +
                          " 오래된 내장은 추론이 3D(렌더) 엔진에 실린다. 메모리는 공유(시스템 RAM)가 본체다.") +
                    " GpuRenderCompute 는 하드웨어 활동 카운터(250ms, 시간 가중)로 3D+Compute 를 합친 값이라 믿을 만하다." +
                    " GpuCompute·Gpu3D 는 PDH 엔진 값(1초)이라 긴 작업이 끝날 때 몰아서 계상돼 0 과 100 을 오갈 수 있다 —" +
                    " 어느 엔진인지를 가를 때만 쓰고, 여러 초의 평균으로 읽는다. GpuVideo 도 IGCL 미디어 활동(250ms)이다.");

            case "AMD":
                return new AiSignals(
                    Present(MetricKind.GpuCompute, MetricKind.Gpu3D, MetricKind.GpuUtil),
                    [.. Present(MetricKind.GpuCopy), .. memory],
                    "Compute_N·High Priority Compute 엔진이 GpuCompute 에 들어온다. DirectML 은 3D 큐를 쓰기도 한다.");

            default:
                return new AiSignals(Present(MetricKind.GpuUtil, MetricKind.GpuCompute, MetricKind.Gpu3D), memory,
                    "제조사를 모른다. 사용률과 엔진 계열을 함께 본다.");
        }
    }
}

using ChronoLoad.Core.Devices;

namespace ChronoLoad.Core.Metrics;

/// <summary>
/// GPU 에서 AI 작업이 <b>어느 지표에 실리는가</b> — 제조사·설정별 규칙 (§5.4).
/// </summary>
/// <remarks>
/// <para>
/// MCP 의 <c>aiSignals.primary</c> 와 GPU 카드의 보조선이 이 규칙 하나를 함께 쓴다. 따로 두면
/// 에이전트가 "B580 은 GpuRenderCompute 를 보라"고 할 때 화면에는 다른 선이 그려진다.
/// </para>
/// <para>
/// 제조사와 HAGS 로 정하고 엔진 인스턴스를 보고 정하지 않는다. PDH 엔진 인스턴스는 그 엔진을 쓰는
/// 프로세스가 있을 때만 나타나서, 유휴 때는 판단할 근거가 없다.
/// </para>
/// </remarks>
public static class GpuAiSignals
{
    /// <summary>
    /// AI 연산이 실리는 지표 후보. 앞쪽이 우선이다. 장치가 실제로 가진 것만 남기는 것은 호출자 몫이다.
    /// </summary>
    public static MetricKind[] PrimaryCandidates(DeviceInfo info)
    {
        if (info.Icon == IconKind.Npu) return [MetricKind.GpuCompute, MetricKind.GpuUtil];

        bool? hags = info.Extra.GetValueOrDefault("hardwareScheduling") switch
        {
            "true" => true,
            "false" => false,
            _ => null,
        };

        return info.Vendor switch
        {
            // HAGS 가 켜져 있으면(Windows 11 기본) CUDA 가 Compute 가 아니라 3D 노드로 합산된다.
            "NVIDIA" when hags != false => [MetricKind.GpuUtil, MetricKind.Gpu3D],
            "NVIDIA" => [MetricKind.GpuUtil, MetricKind.GpuCompute],
            // PDH 엔진 값은 Intel 에서 튄다. 하드웨어 카운터가 먼저다.
            "Intel" => [MetricKind.GpuRenderCompute, MetricKind.GpuCompute, MetricKind.Gpu3D, MetricKind.GpuUtil],
            "AMD" => [MetricKind.GpuCompute, MetricKind.Gpu3D, MetricKind.GpuUtil],
            _ => [MetricKind.GpuUtil, MetricKind.GpuCompute, MetricKind.Gpu3D],
        };
    }

    /// <summary>
    /// GPU 카드에 굵은 사용률 선과 함께 그릴 보조선. <c>GpuUtil</c> 이 아닌 첫 후보 중 장치가 가진 것.
    /// 없으면 null — 보조선을 그리지 않는다.
    /// </summary>
    /// <remarks>
    /// 예전에는 늘 <c>GpuCompute</c> 였다. HAGS 가 켜진 NVIDIA 에서는 AI 추론 중에도 0 에 붙어 있어
    /// "연산을 안 한다"로 읽혔고, Intel 에서는 1초 간격으로 0 과 100 을 오갔다.
    /// </remarks>
    public static MetricKind? ChartSecondary(DeviceInfo info, Func<MetricKind, bool> has)
    {
        foreach (var kind in PrimaryCandidates(info))
            if (kind != MetricKind.GpuUtil && has(kind)) return kind;
        return null;
    }

    /// <summary>화면에 적는 짧은 이름. 오버레이 범례와 스냅샷 창이 쓴다.</summary>
    public static string Label(MetricKind kind) => kind switch
    {
        MetricKind.GpuUtil => "사용률",
        MetricKind.GpuCompute => "Compute",
        MetricKind.Gpu3D => "3D",
        MetricKind.GpuRenderCompute => "렌더+컴퓨트",
        MetricKind.GpuCopy => "Copy",
        MetricKind.GpuVideo => "Video",
        MetricKind.GpuMemBusy => "메모리 대역폭",
        _ => kind.ToString(),
    };
}

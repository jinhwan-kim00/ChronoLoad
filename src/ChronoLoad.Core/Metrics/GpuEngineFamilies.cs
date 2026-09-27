namespace ChronoLoad.Core.Metrics;

/// <summary>
/// GPU 엔진 종류(<c>engtype</c>)를 계열 지표로 묶는다(§5.4).
/// </summary>
/// <remarks>
/// <para>
/// <b>엔진 이름은 표준이 아니다.</b> WDDM 의 엔진 종류(<c>DXGK_ENGINE_TYPE</c>)에는 3D·영상·복사는 있지만
/// 연산(Compute)이 없다. <c>Compute</c>·<c>Cuda</c>·<c>Neural</c> 은 전부 드라이버가 <c>OTHER</c> 엔진에
/// 붙인 이름이라 제조사·드라이버·설정마다 다르다. 그래서 정확히 같은 이름이 아니라 앞부분·부분 일치로 묶는다.
/// </para>
/// <para>AI 작업이 어느 이름으로 나타나는가 — 조사와 이 PC 실측:</para>
/// <list type="bullet">
/// <item>NVIDIA, HAGS 꺼짐: <c>Compute_0</c>·<c>Compute_1</c>·<c>Cuda</c>. <b>HAGS 켜짐(Windows 11 기본)</b>: 따로 나오지 않고
/// <c>3d</c> 노드로 합산된다. RTX 5080 에서 CUDA 필터(<c>bilateral_cuda</c>)가 <c>3d</c> 94.9%·<c>copy</c> 2.4% 로 잡혔다</item>
/// <item>Intel Arc 외장: <c>compute</c>(CCS). 내장 Arc 는 <c>Neural</c> 이 그 자리를 대신한다 — NPU 의 <c>neural</c> 과 이름이 같다</item>
/// <item>AMD: <c>Compute_0</c>…, <c>High Priority Compute</c>(연산), <c>High Priority 3D</c>(3D)</item>
/// </list>
/// </remarks>
public static class GpuEngineFamilies
{
    /// <summary>엔진 종류의 계열 지표. 계열에 넣지 않는 엔진(보안·광류·VR·GSC 등)은 null.</summary>
    public static MetricKind? Classify(string engineType)
    {
        string t = engineType.Trim().ToLowerInvariant();
        if (t.Length == 0) return null;

        // 연산이 먼저다. "High Priority Compute" 처럼 앞이 다른 이름이 있다.
        if (t.Contains("compute", StringComparison.Ordinal)
            || t.StartsWith("cuda", StringComparison.Ordinal)
            || t.StartsWith("neural", StringComparison.Ordinal))
            return MetricKind.GpuCompute;

        // "High Priority 3D"(AMD) 도 3D 다.
        if (t.Contains("3d", StringComparison.Ordinal) || t.StartsWith("graphics", StringComparison.Ordinal))
            return MetricKind.Gpu3D;

        if (t.StartsWith("copy", StringComparison.Ordinal)) return MetricKind.GpuCopy;

        if (t.StartsWith("video", StringComparison.Ordinal) || t.StartsWith("jpeg", StringComparison.Ordinal))
            return MetricKind.GpuVideo;

        return null;
    }
}

using ChronoLoad.Core.Devices;

namespace ChronoLoad.Core.Metrics;

/// <summary>
/// 수집 가능한 지표 종류. 장치 개수는 런타임에 정해지므로 종류만 열거하고
/// 실제 시리즈는 <see cref="MetricId"/>(종류 + 장치 인덱스)로 식별한다.
/// </summary>
public enum MetricKind : byte
{
    // System
    CpuTotal = 0,
    MemUsed,
    MemCommit,
    // Network (인터페이스별)
    NetRx,
    NetTx,
    // Disk (물리 디스크별)
    DiskRead,
    DiskWrite,
    DiskActive,
    DiskQueue,
    DiskLatency,
    // GPU (어댑터별)
    GpuUtil,
    GpuCompute,
    GpuDedicated,
    GpuShared,
    GpuTemp,
    GpuPower,
    GpuClock,
    // GPU 엔진 계열(어댑터별). GpuCompute 와 같은 정의 — 계열 안의 엔진 종류끼리는 최댓값.
    // 뒤에 붙인다: 앞의 값을 밀면 저장된 순서에 기대는 곳이 조용히 어긋난다.
    Gpu3D,
    GpuCopy,
    GpuVideo,
    // 메모리 컨트롤러가 VRAM 을 읽고 쓴 시간 비율(NVML). LLM 디코드처럼 대역폭에 묶인 AI 부하의 지표다.
    GpuMemBusy,
    // 드라이버가 지금 강제하는 전력 한도(W). 전력이 여기에 붙어 있으면 클럭이 깎인다.
    GpuPowerLimit,
    // 클럭 제한 사유(§5.4). 제한 중이면 100, 아니면 0 — 평균이 곧 "제한에 걸려 있던 시간 비율"이다.
    GpuThrottlePower,
    GpuThrottleThermal,
    GpuThrottleOther,
    // 렌더(3D)+컴퓨트 엔진이 돈 시간 비율 — 하드웨어 활동 카운터(IGCL, 250ms, 시간 가중).
    // 3D 와 Compute 를 가르지 못하지만 PDH 엔진 값처럼 튀지 않는다(§5.4).
    GpuRenderCompute,
    // PCIe 처리량(B/s). Rx = 호스트→GPU(업로드), Tx = GPU→호스트(다운로드). NVML 누적 카운터의 차분.
    GpuPcieRx,
    GpuPcieTx,
}

public static class MetricKindExtensions
{
    /// <summary>이 지표가 어느 장치 묶음에 속하는지. 카드 색을 결정한다.</summary>
    public static DeviceClass Class(this MetricKind kind) => kind switch
    {
        MetricKind.CpuTotal or MetricKind.MemUsed or MetricKind.MemCommit => DeviceClass.System,
        MetricKind.NetRx or MetricKind.NetTx => DeviceClass.Network,
        >= MetricKind.DiskRead and <= MetricKind.DiskLatency => DeviceClass.Disk,
        _ => DeviceClass.Gpu,
    };

    /// <summary>표시 단위. 오토스케일과 포매팅 규칙을 가른다.</summary>
    public static MetricUnit Unit(this MetricKind kind) => kind switch
    {
        MetricKind.CpuTotal or MetricKind.GpuUtil or MetricKind.GpuCompute or MetricKind.DiskActive
            or MetricKind.Gpu3D or MetricKind.GpuCopy or MetricKind.GpuVideo or MetricKind.GpuMemBusy
            or MetricKind.GpuThrottlePower or MetricKind.GpuThrottleThermal or MetricKind.GpuThrottleOther
            or MetricKind.GpuRenderCompute
            => MetricUnit.Percent,
        // 네트워크는 관례상 비트/초(Mbps), 디스크는 바이트/초(MB/s)로 읽는다.
        MetricKind.NetRx or MetricKind.NetTx => MetricUnit.BitRate,
        MetricKind.DiskRead or MetricKind.DiskWrite or MetricKind.GpuPcieRx or MetricKind.GpuPcieTx
            => MetricUnit.ByteRate,
        MetricKind.MemUsed or MetricKind.MemCommit or MetricKind.GpuDedicated or MetricKind.GpuShared
            => MetricUnit.Bytes,
        MetricKind.GpuTemp => MetricUnit.Celsius,
        MetricKind.GpuPower or MetricKind.GpuPowerLimit => MetricUnit.Watt,
        MetricKind.GpuClock => MetricUnit.Megahertz,
        MetricKind.DiskLatency => MetricUnit.Milliseconds,
        _ => MetricUnit.Scalar,
    };
}

public enum MetricUnit : byte
{
    Scalar = 0,
    Percent,
    Bytes,

    /// <summary>비트/초. 네트워크 — 회선 속도가 Mbps 로 표기되므로 그 관례를 따른다.</summary>
    BitRate,

    /// <summary>바이트/초. 디스크 — 제조사 스펙과 벤치마크가 MB/s 로 표기된다.</summary>
    ByteRate,
    Celsius,
    Watt,
    Megahertz,
    Milliseconds,
}

/// <summary>지표 종류 + 장치 인덱스. 장치 인덱스는 같은 <see cref="DeviceClass"/> 안에서만 유효하다.</summary>
public readonly record struct MetricId(MetricKind Kind, byte DeviceIndex)
{
    public DeviceClass Class => Kind.Class();

    public override string ToString() =>
        Kind.Class() == DeviceClass.System ? Kind.ToString() : $"{Kind}[{DeviceIndex}]";
}

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
            => MetricUnit.Percent,
        // 네트워크는 관례상 비트/초(Mbps), 디스크는 바이트/초(MB/s)로 읽는다.
        MetricKind.NetRx or MetricKind.NetTx => MetricUnit.BitRate,
        MetricKind.DiskRead or MetricKind.DiskWrite => MetricUnit.ByteRate,
        MetricKind.MemUsed or MetricKind.MemCommit or MetricKind.GpuDedicated or MetricKind.GpuShared
            => MetricUnit.Bytes,
        MetricKind.GpuTemp => MetricUnit.Celsius,
        MetricKind.GpuPower => MetricUnit.Watt,
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

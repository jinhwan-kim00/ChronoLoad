namespace ChronoLoad.Core.Devices;

/// <summary>지표 종류 묶음. 카드 색과 1:1 대응한다.</summary>
public enum DeviceClass : byte
{
    /// <summary>CPU·메모리처럼 항상 하나뿐인 시스템 지표.</summary>
    System = 0,
    Gpu = 1,
    Disk = 2,
    Network = 3,
}

/// <summary>
/// 카드 아이콘 종류. 색이 "지표 종류"를 말한다면 아이콘은 "이 장치가 무엇인가"를 말한다.
/// (UX 설계서 §03)
/// </summary>
public enum IconKind : byte
{
    Unknown = 0,
    Cpu,
    Memory,
    // GPU — 제조사
    GpuNvidia,
    GpuAmd,
    GpuIntel,
    GpuGeneric,

    /// <summary>연산 전용 가속기(Intel AI Boost, AMD XDNA 등). WDDM 어댑터로 열거되지만 GPU가 아니다.</summary>
    Npu,

    // 디스크 — 매체
    DiskSsd,
    DiskHdd,
    DiskGeneric,
    // 네트워크 — 연결 종류
    NetEthernet,
    NetWiFi,
    NetCellular,
    NetTunnel,
    NetGeneric,
}

/// <summary>
/// 장치 하나의 표시 정보. 이름 조립은 프로바이더가 책임지고 UI는 문자열을 만들지 않는다.
/// </summary>
/// <param name="Key">
/// 재부팅·재연결을 넘어 안정적인 식별자. GPU는 LUID, 디스크는 시리얼, 네트워크는 인터페이스 GUID.
/// 통계·설정·프리셋이 모두 이 키를 기준으로 장치를 따라간다.
/// </param>
/// <param name="ShortName">라벨 페이드·hover용 축약 이름. 예 "RTX 4090", "990 PRO NVMe".</param>
/// <param name="FullName">툴팁·오버레이 첫 줄용 전체 사양.</param>
public sealed record DeviceInfo(
    string Key,
    DeviceClass Class,
    string ShortName,
    string FullName,
    IconKind Icon,
    string? Vendor = null)
{
    /// <summary>용량·링크 속도처럼 시계열이 아닌 부가 정보(오버레이 표시용).</summary>
    public IReadOnlyDictionary<string, string> Extra { get; init; } =
        new Dictionary<string, string>(0);

    public override string ToString() => $"{Class}:{ShortName}";
}

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

    /// <summary>표시 내용이 같은가.</summary>
    /// <remarks>
    /// <b><c>record</c> 의 기본 같음 비교로는 판단할 수 없다.</b> <see cref="Extra"/> 가 사전이라
    /// 내용이 같아도 참조가 다르면 다른 것으로 나온다. 재열거는 매번 새 사전을 만들므로
    /// 기본 비교를 쓰면 "항상 바뀌었다"가 되고, 그 판정이 곧 <c>devicesRevision</c> 으로 새어 나간다.
    /// </remarks>
    public bool HasSameDescription(DeviceInfo other)
    {
        if (ReferenceEquals(this, other)) return true;

        if (Key != other.Key || Class != other.Class || Icon != other.Icon
            || ShortName != other.ShortName || FullName != other.FullName || Vendor != other.Vendor)
            return false;

        if (Extra.Count != other.Extra.Count) return false;

        foreach (var (key, value) in Extra)
            if (!other.Extra.TryGetValue(key, out string? theirs) || theirs != value)
                return false;

        return true;
    }

    /// <summary>
    /// <see cref="Extra"/> 중 수시로 바뀌는 값의 키. 구성 비교(<see cref="HasSameConfiguration"/>)에서 뺀다.
    /// </summary>
    /// <remarks>
    /// Wi-Fi 링크 속도가 그렇다 — 이 PC 실측으로 40초에 1922 ↔ 2162 ↔ 2402 Mbps 를 여섯 번 오갔다.
    /// </remarks>
    public IReadOnlyCollection<string> LiveExtraKeys { get; init; } = [];

    /// <summary>
    /// 구성이 같은가 — MCP <c>devicesRevision</c> 의 기준이다(§10.3). 표시 이름과 <see cref="LiveExtraKeys"/> 는 보지 않는다.
    /// </summary>
    /// <remarks>
    /// <see cref="HasSameDescription"/> 은 화면을 다시 그릴지의 기준이라 이름 하나만 달라도 다르다. 그 기준을 구성에
    /// 쓰면 링크 속도가 바뀔 때마다 구성이 바뀐 것이 되어, "이 값만 비교하면 구성이 그대로인지 안다"는 약속이
    /// Wi-Fi 가 있는 기기에서는 늘 거짓이 된다. 에이전트는 장치를 이름이 아니라 키로 찾는다.
    /// </remarks>
    public bool HasSameConfiguration(DeviceInfo other)
    {
        if (ReferenceEquals(this, other)) return true;

        if (Key != other.Key || Class != other.Class || Icon != other.Icon || Vendor != other.Vendor)
            return false;

        bool Live(string key) => LiveExtraKeys.Contains(key) || other.LiveExtraKeys.Contains(key);

        int count = 0;
        foreach (var (key, value) in Extra)
        {
            if (Live(key)) continue;
            if (!other.Extra.TryGetValue(key, out string? theirs) || theirs != value) return false;
            count++;
        }

        return count == other.Extra.Keys.Count(k => !Live(k));
    }

    public override string ToString() => $"{Class}:{ShortName}";
}

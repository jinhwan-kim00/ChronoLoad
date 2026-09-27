namespace ChronoLoad.Core.Metrics;

/// <summary>
/// GPU 클럭이 원하는 만큼 오르지 못하는 이유 — 벤더 중립 (§5.4).
/// </summary>
/// <remarks>
/// <para>
/// NVML 의 <c>nvmlDeviceGetCurrentClocksEventReasons</c> 비트와 IGCL <c>ctl_power_telemetry_t</c> 의
/// <c>gpu*Limited</c> 플래그를 한 줄로 옮긴다. 둘은 이름도 개수도 다르지만 에이전트가 묻는 것은 같다 —
/// "지금 느린 것이 전력 때문인가, 온도 때문인가, 아니면 할 일이 없어서인가".
/// </para>
/// <para>
/// <b>병목은 <see cref="GpuLimitReasonsExtensions.Bottlenecks"/> 뿐이다.</b> 나머지는 클럭이 그 자리에 있는 이유이긴 해도
/// 성능을 깎는 제약이 아니다 — 섞어 세면 "항상 스로틀 중"이 된다.
/// </para>
/// <list type="bullet">
/// <item><see cref="Idle"/>·<see cref="LowUtilization"/> — 할 일이 없다</item>
/// <item><see cref="Voltage"/>·<see cref="Reliability"/> — 전압-클럭 곡선의 끝, 곧 <b>천장</b>이다. 부하가 없어도 선다:
/// RTX 5080 은 유휴 내내 <c>0x400</c>(Reliability)이었고 CUDA 부하가 걸리자 <c>0x4</c>(전력 상한)로 바뀌었다.
/// 처음에는 이것을 병목으로 셌더니 18초 측정의 44% 가 "그 밖 제한"으로 나왔다 — 전부 유휴 구간이었다</item>
/// <item>설정 계열(<see cref="ApplicationClocks"/>·<see cref="SyncBoost"/>·<see cref="DisplayClock"/>)</item>
/// </list>
/// </remarks>
[Flags]
public enum GpuLimitReasons : uint
{
    None = 0,

    /// <summary>전력 상한. NVML <c>SwPowerCap</c>(0x4), IGCL <c>gpuPowerLimited</c>.</summary>
    Power = 1 << 0,

    /// <summary>온도. NVML <c>SwThermalSlowdown</c>(0x20)·<c>HwThermalSlowdown</c>(0x40), IGCL <c>gpuTemperatureLimited</c>.</summary>
    Thermal = 1 << 1,

    /// <summary>외부 전력 브레이크 — 클럭을 절반 이하로. NVML <c>HwPowerBrakeSlowdown</c>(0x80).</summary>
    PowerBrake = 1 << 2,

    /// <summary>하드웨어 감속 — 클럭을 절반 이하로(온도·전력 브레이크·과전력). NVML <c>HwSlowdown</c>(0x8).</summary>
    HardwareSlowdown = 1 << 3,

    /// <summary>전류 한계. IGCL <c>gpuCurrentLimited</c>.</summary>
    Current = 1 << 4,

    /// <summary>신뢰성 전압 한계 — 더 올릴 전압이 없다(곡선의 천장). IGCL <c>gpuVoltageLimited</c>. 병목이 아니다.</summary>
    Voltage = 1 << 5,

    /// <summary>신뢰성 정책(곡선의 천장). NVML <c>Reliability</c>(0x400). 유휴에도 선다 — 병목이 아니다.</summary>
    Reliability = 1 << 6,

    /// <summary>보드 한계 정책. NVML <c>BoardLimit</c>(0x200).</summary>
    BoardLimit = 1 << 7,

    /// <summary>할 일이 없어 클럭이 내려간다. NVML <c>GpuIdle</c>(0x1). 병목이 아니다.</summary>
    Idle = 1 << 8,

    /// <summary>사용률이 낮아 클럭을 올리지 않는다. IGCL <c>gpuUtilizationLimited</c>. 병목이 아니다.</summary>
    LowUtilization = 1 << 9,

    /// <summary>애플리케이션 클럭 설정. NVML <c>ApplicationsClocksSetting</c>(0x2).</summary>
    ApplicationClocks = 1 << 10,

    /// <summary>동기 부스트 그룹. NVML <c>SyncBoost</c>(0x10).</summary>
    SyncBoost = 1 << 11,

    /// <summary>디스플레이 클럭 설정. NVML <c>DisplayClockSetting</c>(0x100).</summary>
    DisplayClock = 1 << 12,

    /// <summary>이 코드가 모르는 비트가 섰다. 드라이버가 새 사유를 더한 경우다.</summary>
    Unknown = 1u << 31,
}

public static class GpuLimitReasonsExtensions
{
    /// <summary>성능을 깎는 제약만. 유휴·저사용·설정 계열을 뺀다.</summary>
    public const GpuLimitReasons Bottlenecks =
        GpuLimitReasons.Power | GpuLimitReasons.Thermal | GpuLimitReasons.PowerBrake
        | GpuLimitReasons.HardwareSlowdown | GpuLimitReasons.Current | GpuLimitReasons.BoardLimit;

    /// <summary>전력 계열 지표(<see cref="MetricKind.GpuThrottlePower"/>)에 세는 사유.</summary>
    public const GpuLimitReasons PowerFamily =
        GpuLimitReasons.Power | GpuLimitReasons.PowerBrake | GpuLimitReasons.Current;

    /// <summary>온도 계열 지표(<see cref="MetricKind.GpuThrottleThermal"/>)에 세는 사유.</summary>
    public const GpuLimitReasons ThermalFamily = GpuLimitReasons.Thermal;

    /// <summary>그 밖의 병목(<see cref="MetricKind.GpuThrottleOther"/>).</summary>
    public const GpuLimitReasons OtherFamily = GpuLimitReasons.HardwareSlowdown | GpuLimitReasons.BoardLimit;

    /// <summary>
    /// NVML <c>clocksEventReasons</c> 비트마스크를 옮긴다. 값은 <c>nvml.h</c> 의 정의 그대로다.
    /// </summary>
    public static GpuLimitReasons FromNvml(ulong bits)
    {
        var r = GpuLimitReasons.None;
        if ((bits & 0x001) != 0) r |= GpuLimitReasons.Idle;
        if ((bits & 0x002) != 0) r |= GpuLimitReasons.ApplicationClocks;
        if ((bits & 0x004) != 0) r |= GpuLimitReasons.Power;
        if ((bits & 0x008) != 0) r |= GpuLimitReasons.HardwareSlowdown;
        if ((bits & 0x010) != 0) r |= GpuLimitReasons.SyncBoost;
        if ((bits & 0x020) != 0) r |= GpuLimitReasons.Thermal;
        if ((bits & 0x040) != 0) r |= GpuLimitReasons.Thermal | GpuLimitReasons.HardwareSlowdown;
        if ((bits & 0x080) != 0) r |= GpuLimitReasons.PowerBrake | GpuLimitReasons.HardwareSlowdown;
        if ((bits & 0x100) != 0) r |= GpuLimitReasons.DisplayClock;
        if ((bits & 0x200) != 0) r |= GpuLimitReasons.BoardLimit;
        if ((bits & 0x400) != 0) r |= GpuLimitReasons.Reliability;
        if ((bits & ~0x7FFUL) != 0) r |= GpuLimitReasons.Unknown;
        return r;
    }

    /// <summary>
    /// 세 계열 지표에 넣을 값. 하드웨어 감속(0x8)은 온도·전력 브레이크가 원인으로 함께 서 있으면
    /// 그쪽에 이미 센 것이므로 "그 밖"에서 뺀다 — NVML 은 <c>HwThermalSlowdown</c> 과 <c>HwSlowdown</c> 을 같이 세운다.
    /// </summary>
    public static (bool Power, bool Thermal, bool Other) Split(this GpuLimitReasons reasons)
    {
        bool power = (reasons & PowerFamily) != 0;
        bool thermal = (reasons & ThermalFamily) != 0;
        var other = reasons & OtherFamily;
        if (power || thermal) other &= ~GpuLimitReasons.HardwareSlowdown;
        return (power, thermal, other != 0);
    }

    /// <summary>MCP 응답에 쓰는 이름(camelCase). 사유 하나당 하나.</summary>
    public static string[] Names(this GpuLimitReasons reasons)
    {
        var names = new List<string>();
        foreach (var flag in Enum.GetValues<GpuLimitReasons>())
        {
            if (flag == GpuLimitReasons.None || !reasons.HasFlag(flag)) continue;
            string n = flag.ToString();
            names.Add(char.ToLowerInvariant(n[0]) + n[1..]);
        }
        return [.. names];
    }
}

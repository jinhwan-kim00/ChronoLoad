namespace ChronoLoad.Sensors.Vendor;

/// <summary>
/// 벤더 네이티브 경로가 채우는 값. 채우지 못한 항목은 NaN으로 남긴다.
/// </summary>
/// <remarks>
/// 온도·전력·클럭은 <b>PDH로는 얻을 수 없다</b>. WDDM 성능 카운터에는 그런 항목이 없다.
/// 이 구조체가 채워지는지 여부가 오버레이에 온도·전력을 띄울 수 있는지를 가른다.
/// </remarks>
public struct VendorSample
{
    public float UtilPercent;
    public float MemoryUsedBytes;
    public float MemoryTotalBytes;
    public float TemperatureCelsius;
    public float PowerWatts;
    public float CoreClockMegahertz;

    public static VendorSample Empty => new()
    {
        UtilPercent = float.NaN,
        MemoryUsedBytes = float.NaN,
        MemoryTotalBytes = float.NaN,
        TemperatureCelsius = float.NaN,
        PowerWatts = float.NaN,
        CoreClockMegahertz = float.NaN,
    };
}

/// <summary>PCI 주소. 벤더 SDK의 장치 목록과 WDDM 어댑터를 잇는 유일하게 공통된 좌표다.</summary>
public readonly record struct PciAddress(uint Bus, uint Device, uint Function)
{
    public override string ToString() => $"{Bus:X2}:{Device:X2}.{Function}";
}

/// <summary>
/// 벤더 네이티브 텔레메트리 한 벌(NVML · Level Zero · IGCL …).
/// 로드에 실패하면 <see cref="IsAvailable"/>이 false가 되고 나머지 경로는 그대로 돈다 —
/// 센서 하나가 죽어도 앱은 계속 동작해야 한다.
/// </summary>
public interface IVendorTelemetry : IDisposable
{
    /// <summary>진단·능력 보고에 쓰는 이름. "NVML", "Level Zero" 등.</summary>
    string Name { get; }

    bool IsAvailable { get; }

    /// <summary>PCI 주소로 장치를 찾는다. 찾으면 이후 <see cref="TryRead"/>에 쓸 핸들 인덱스를 준다.</summary>
    bool TryBind(PciAddress address, out int handle);

    /// <summary>바인딩된 장치의 값을 읽는다. 일부만 채워질 수 있다.</summary>
    /// <summary>
    /// 한 장치의 값을 읽는다.
    /// </summary>
    /// <param name="full">
    /// <c>false</c> 면 <b>사용률만</b> 읽는다. 온도·전력·클럭은 초 단위로도 충분히 촘촘한데
    /// 네이티브 호출 비용은 사용률과 같아서, 매 틱 전부 읽으면 샘플링 예산(§12)을 그것만으로 넘긴다.
    /// 사용률만 Fast 티어로 남기고 나머지는 몇 틱에 한 번 읽는다.
    /// </param>
    bool TryRead(int handle, bool full, out VendorSample sample);
}

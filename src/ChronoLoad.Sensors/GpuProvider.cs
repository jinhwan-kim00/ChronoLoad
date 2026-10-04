using System.Text.RegularExpressions;
using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Metrics;
using ChronoLoad.Core.Sensors;
using ChronoLoad.Sensors.Native;
using ChronoLoad.Sensors.Vendor;
using Microsoft.Win32;

namespace ChronoLoad.Sensors;

/// <summary>
/// 가속기별 사용률과 메모리. GPU와 NPU를 함께 다룬다. 계층 A(WDDM 성능 카운터) 경로다.
/// </summary>
/// <remarks>
/// <para>
/// <b>열거의 원천은 PDH 인스턴스다.</b> 처음에는 <c>D3DKMTEnumAdapters2/3</c>로 어댑터를 찾고
/// 카운터를 붙이려 했는데, 실기기에서 카운터에는 LUID가 4개 있는 반면 D3DKMT는 3개만 돌려줬다.
/// 빠진 하나가 NPU였다 — <c>EnumAdapters3</c>에 <c>Filter=None</c>을 줘도 나오지 않는다.
/// 그래서 순서를 뒤집었다. <b>측정할 수 있는 것(PDH 인스턴스)을 먼저 찾고</b>, D3DKMT는
/// 이름·용량·벤더를 채우는 보조 자료로만 쓴다.
/// </para>
/// <para>
/// NPU 판정은 <c>engtype_Neural</c> 엔진 유무로 한다. 작업 관리자가 NPU를 보여주는 근거도 같은 카운터다.
/// </para>
/// <para>
/// 두 카운터의 성격이 다르다. <c>GPU Adapter Memory</c>는 어댑터마다 인스턴스가 하나뿐이라 싸고,
/// <c>GPU Engine</c>은 프로세스 × 엔진으로 수백 개까지 늘어난다(실기기 1,200개 이상).
/// 그래서 <b>메모리는 매 틱, 엔진은 4틱마다</b> 읽고, 엔진 값은 읽지 않은 틱에 "실측 아님"으로 표시한다.
/// </para>
/// </remarks>
public sealed class GpuProvider : ISensorProvider
{
    private const string EnginePath = @"\GPU Engine(*)\Utilization Percentage";
    private const string DedicatedPath = @"\GPU Adapter Memory(*)\Dedicated Usage";
    private const string SharedPath = @"\GPU Adapter Memory(*)\Shared Usage";

    /// <summary>엔진 와일드카드를 읽는 주기(Fast 틱 기준). 250ms × 4 = 1초.</summary>
    private const int EngineEvery = 4;

    /// <summary>
    /// 온도·전력·클럭을 읽는 주기(Fast 틱 기준). 이 값들은 물리적으로 초 단위로 움직이는데
    /// 네이티브 호출 비용은 사용률과 같다 — 매 틱 읽으면 §12 의 듀티 사이클 예산을 넘긴다.
    /// 실측: 전부 매 틱 읽으면 1.27%, 이 주기를 두면 0.9%대.
    /// </summary>
    private const int VendorSlowEvery = 4;

    /// <summary>PnP <c>ComputeAccelerator</c> 장치 클래스. NPU의 친숙한 이름을 여기서 얻는다.</summary>
    private const string ComputeAcceleratorClass =
        @"SYSTEM\CurrentControlSet\Control\Class\{f01a9d53-3ff6-48d2-9f97-c8a7004be10c}";

    private static readonly Regex LuidPattern =
        new(@"luid_0x(?<high>[0-9A-Fa-f]{8})_0x(?<low>[0-9A-Fa-f]{8})", RegexOptions.Compiled);

    private readonly Dictionary<string, Accelerator> _accelerators = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, D3DKmt.Adapter> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _engineTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _computeAcceleratorNames = [];
    private readonly List<IVendorTelemetry> _vendors = [];
    private readonly DevicePowerProbe _power = new();

    private PdhQuery? _query;
    private PdhCounterArray? _engine;
    private PdhCounterArray? _dedicated;
    private PdhCounterArray? _shared;
    private MetricRegistry? _registry;
    // 장치 이벤트 스레드가 쓰고 샘플링 스레드가 읽는다.
    private volatile bool _enumeratePending = true;
    private bool _rebindPending;
    private bool _intelPathDeferred;
    private long _tick;

    public string Id => "gpu";
    public SensorTier Tier => SensorTier.Fast;
    public bool IsAvailable { get; private set; }

    /// <summary>벤더 ID를 커널에서 못 얻어 이름으로 추정한 어댑터가 있는지. 능력 보고에 쓴다.</summary>
    public bool VendorGuessed { get; private set; }

    /// <summary>
    /// 계층 B(벤더 SDK)를 쓸지. 끄면 PDH 만으로 동작한다 —
    /// 네이티브 SDK 가 의심될 때 원인을 가르는 스위치다.
    /// <see cref="InitializeAsync"/> 전에 설정해야 한다.
    /// </summary>
    public bool UseVendorTelemetry { get; init; } = true;

    /// <summary>
    /// 벤더 SDK 호출 직전·직후를 로그에 남긴다. 네이티브 크래시는 스택을 남기지 않으므로,
    /// "마지막 로그가 무엇이었나"가 사실상 유일한 단서다. 진단 용도로만 켠다.
    /// </summary>
    public bool TraceVendorCalls { get; init; }

    /// <summary>
    /// 프로세스별 엔진 기록기(<c>watch_process</c>). 엔진 와일드카드를 읽을 때 감시 중인 PID 몫을 넘긴다 —
    /// 같은 결과를 나눠 쓰므로 추가 쿼리가 없다.
    /// </summary>
    public ProcessWatch Watch { get; } = new();

    public ValueTask InitializeAsync(MetricRegistry registry, CancellationToken cancellationToken)
    {
        _registry = registry;

        foreach (var adapter in D3DKmt.EnumerateAdapters())
            _metadata[adapter.Luid.PdhToken] = adapter;

        _computeAcceleratorNames.AddRange(ReadComputeAcceleratorNames());
        _power.Refresh();

        OpenVendorPaths();

        _query = PdhQuery.TryOpen();
        if (_query is not null)
        {
            _dedicated = _query.TryAddArray(DedicatedPath);
            _shared = _query.TryAddArray(SharedPath);
            _engine = _query.TryAddArray(EnginePath);
            _query.Collect();
        }

        IsAvailable = _dedicated is not null;
        if (!IsAvailable) SensorLog.Write("GPU 카운터를 열 수 없다 (원격 세션이거나 WDDM 드라이버 없음)");

        // 비율 카운터는 수집이 두 번 쌓이기 전에 인스턴스 배열을 돌려주지 않는다.
        _enumeratePending = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 벤더 네이티브 경로(계층 B)를 연다. 없으면 조용히 계층 A(PDH)만 쓴다.
    /// IGCL 을 Level Zero 보다 앞세우는 것은, 한 번의 호출로 온도·전력·클럭을 다 주기 때문이다.
    /// </summary>
    private void OpenVendorPaths()
    {
        if (!UseVendorTelemetry) return;

        if (NvmlTelemetry.TryCreate() is { } nvml) _vendors.Add(nvml);
        OpenIntelPath();
    }

    /// <summary>
    /// 어댑터 착탈·드라이버 재시작 시 다음 샘플에서 다시 열거하도록 <b>표시만</b> 한다.
    /// </summary>
    /// <remarks>
    /// 이 호출은 장치 이벤트 스레드에서 온다. 여기서 실제 작업을 하면 안 된다 —
    /// 벤더 SDK 핸들을 여기서 닫으면 <b>샘플링 스레드가 같은 핸들로 읽는 도중에 닫히고,
    /// 네이티브 호출이 해제된 핸들을 만나 프로세스가 세그폴트로 죽는다.</b> 실제로 그렇게 죽였다.
    /// 모든 재열거·재개방은 <see cref="Sample"/> 안, 즉 샘플링 스레드에서만 한다.
    /// </remarks>
    public void RequestEnumerate() => _enumeratePending = true;

    /// <summary>
    /// 어댑터 목록을 다시 읽고, 집합이 달라졌으면 벤더 경로를 다시 연다.
    /// <b>반드시 샘플링 스레드에서만 호출한다.</b>
    /// </summary>
    /// <remarks>
    /// NVML·IGCL·Level Zero 는 초기화 시점의 장치 목록을 들고 있어서, 나중에 꽂힌 eGPU 는
    /// 기존 핸들로는 절대 보이지 않는다. 다시 열지 않으면 새 어댑터가 계층 A(PDH)로만 잡혀
    /// 온도·전력이 영영 비어 있게 된다.
    /// 반대로 집합이 그대로면 손대지 않는다 — 30초 안전망은 아무 일이 없어도 계속 도는데,
    /// 그때마다 <c>nvmlInit</c>/<c>Shutdown</c> 을 반복하면 비용과 실패 위험만 쌓인다.
    /// </remarks>
    private void RefreshAdapters()
    {
        var adapters = D3DKmt.EnumerateAdapters().ToDictionary(a => a.Luid.PdhToken);

        bool changed = adapters.Count != _metadata.Count || !adapters.Keys.All(_metadata.ContainsKey);

        _metadata.Clear();
        foreach (var (token, adapter) in adapters) _metadata[token] = adapter;

        if (!changed) return;

        SensorLog.Write($"어댑터 집합 변경 — 벤더 경로 재개방 (어댑터 {adapters.Count}개)");
        _power.Refresh();

        foreach (var vendor in _vendors) vendor.Dispose();
        _vendors.Clear();
        OpenVendorPaths();

        foreach (var accelerator in _accelerators.Values) accelerator.Vendors.Clear();
        _rebindPending = true;
    }

    /// <summary>재열거 뒤 살아남은 가속기의 벤더 경로를 다시 붙인다.</summary>
    private void RebindVendors()
    {
        _rebindPending = false;
        foreach (var accelerator in _accelerators.Values)
        {
            if (accelerator.Vendors.Count > 0) continue;
            BindVendor(accelerator, _metadata.GetValueOrDefault(accelerator.Token));
        }
    }

    private void Enumerate(MetricRegistry registry)
    {
        CollectEngineTypes();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int npuOrdinal = 0;

        _dedicated!.Read((instance, _) =>
        {
            string? token = ParseLuidToken(instance);
            if (token is null) return;

            // 분리돼 내린 어댑터는 PDH 에 남아 있어도 다시 올리지 않는다(RetireGone).
            listed.Add(token);
            if (_goneTokens.Contains(token)) return;

            // 소프트웨어 렌더러(WARP)는 모니터링 대상이 아니다.
            var known = _metadata.GetValueOrDefault(token);
            if (known?.IsSoftware == true) return;

            seen.Add(token);
            if (_accelerators.ContainsKey(token)) return;

            // NPU 판정은 engtype_Neural 유무로 한다. D3DKMT 가 모르는 어댑터라는 사실만으로는
            // 부족하다 — WARP 도 같은 모습이기 때문이다.
            bool isNpu = IsNeuralOnly(token);
            var info = BuildInfo(token, known, isNpu, npuOrdinal);
            if (isNpu) npuOrdinal++;

            // 온도·전력·클럭 슬롯은 벤더 경로가 없어도 등록한다. 값이 없으면 공백으로 남을 뿐이고,
            // 나중에 드라이버가 붙었을 때 시리즈를 새로 만들 필요가 없다.
            var handle = registry.Register(info,
                [MetricKind.GpuUtil, MetricKind.GpuCompute, MetricKind.GpuDedicated, MetricKind.GpuShared,
                 MetricKind.GpuTemp, MetricKind.GpuPower, MetricKind.GpuClock,
                 MetricKind.Gpu3D, MetricKind.GpuCopy, MetricKind.GpuVideo, MetricKind.GpuMemBusy,
                 MetricKind.GpuPowerLimit, MetricKind.GpuThrottlePower, MetricKind.GpuThrottleThermal,
                 MetricKind.GpuThrottleOther, MetricKind.GpuRenderCompute, MetricKind.GpuPcieRx, MetricKind.GpuPcieTx]);

            var accelerator = new Accelerator
            {
                Token = token,
                Handle = handle,
                IsNpu = isNpu,
                UtilSlot = handle.SlotOf(MetricKind.GpuUtil),
                ComputeSlot = handle.SlotOf(MetricKind.GpuCompute),
                DedicatedSlot = handle.SlotOf(MetricKind.GpuDedicated),
                SharedSlot = handle.SlotOf(MetricKind.GpuShared),
                TempSlot = handle.SlotOf(MetricKind.GpuTemp),
                PowerSlot = handle.SlotOf(MetricKind.GpuPower),
                ClockSlot = handle.SlotOf(MetricKind.GpuClock),
                Graphics3DSlot = handle.SlotOf(MetricKind.Gpu3D),
                CopySlot = handle.SlotOf(MetricKind.GpuCopy),
                VideoSlot = handle.SlotOf(MetricKind.GpuVideo),
                MemBusySlot = handle.SlotOf(MetricKind.GpuMemBusy),
                PowerLimitSlot = handle.SlotOf(MetricKind.GpuPowerLimit),
                ThrottlePowerSlot = handle.SlotOf(MetricKind.GpuThrottlePower),
                ThrottleThermalSlot = handle.SlotOf(MetricKind.GpuThrottleThermal),
                ThrottleOtherSlot = handle.SlotOf(MetricKind.GpuThrottleOther),
                RenderComputeSlot = handle.SlotOf(MetricKind.GpuRenderCompute),
                PcieRxSlot = handle.SlotOf(MetricKind.GpuPcieRx),
                PcieTxSlot = handle.SlotOf(MetricKind.GpuPcieTx),
            };

            BindVendor(accelerator, known);
            _accelerators[token] = accelerator;
        });

        foreach (string token in _accelerators.Keys.ToArray())
        {
            if (seen.Contains(token)) continue;
            registry.Retire(_accelerators[token].Handle.Key, DateTime.UtcNow.Ticks);
            _accelerators.Remove(token);
        }

        // PDH 에서도 사라졌으면 잊는다. 같은 LUID 로 다시 나타나면 새 장치로 받는다.
        // 읽기가 통째로 비었으면 판단하지 않는다 — 그걸로 잊으면 남아 있는 어댑터가 다시 올라왔다 내려간다.
        if (listed.Count > 0) _goneTokens.IntersectWith(listed);

        if (_accelerators.Count > 0)
            SensorLog.Write($"가속기 {_accelerators.Count}개 등록 " +
                            $"(NPU {_accelerators.Values.Count(a => a.IsNpu)}개" +
                            $"{(VendorGuessed ? ", 벤더 일부 이름 추정" : "")})");
    }

    /// <summary>
    /// PCI 주소로 벤더 SDK 장치를 찾아 붙인다. WDDM LUID 와 벤더 장치 목록을 잇는
    /// 공통 좌표가 PCI 주소뿐이다.
    /// </summary>
    /// <remarks>
    /// <b>첫 경로에서 멈추지 않고 붙는 대로 전부 쌓는다.</b> 한 벤더 SDK가 모든 값을 내주리라는
    /// 보장이 없기 때문이다 — 실측에서 Intel Arc 는 Level Zero 로 클럭만, IGCL 로 온도·전력만
    /// 내준다. 두 경로를 겹쳐야 한 장치의 값이 채워진다.
    /// </remarks>
    private void BindVendor(Accelerator accelerator, D3DKmt.Adapter? known)
    {
        if (known is null) return;

        var address = new PciAddress(known.PciBus, known.PciDevice, known.PciFunction);
        accelerator.PciAddress = address;

        // 잠들어 있으면 붙이지 않는다. 바인딩 과정 자체가 장치를 건드린다.
        accelerator.PowerState = _power.Query(address);
        if (accelerator.PowerState == DevicePowerState.Gone) return;
        if (accelerator.PowerState == DevicePowerState.Off) { accelerator.HasSlept = true; return; }

        foreach (var vendor in _vendors)
        {
            if (!vendor.IsAvailable || !vendor.TryBind(address, out int handle)) continue;

            accelerator.Vendors.Add(new VendorBinding(vendor, handle));
            SensorLog.Write($"{known.Name} → {vendor.Name} 바인딩 (PCI {address})");
        }
    }

    /// <summary>
    /// LUID별 엔진 종류를 모은다. <c>engtype_Neural</c> 만 있는 어댑터가 NPU다.
    /// </summary>
    private void CollectEngineTypes()
    {
        _engineTypes.Clear();
        _engine?.Read((instance, _) =>
        {
            string? token = ParseLuidToken(instance);
            if (token is null) return;

            if (!_engineTypes.TryGetValue(token, out var types))
                _engineTypes[token] = types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            types.Add(ParseEngineType(instance));
        });
    }

    private bool IsNeuralOnly(string token) =>
        _engineTypes.TryGetValue(token, out var types)
        && types.Count > 0
        && types.All(t => t.Equals("Neural", StringComparison.OrdinalIgnoreCase));

    private DeviceInfo BuildInfo(string token, D3DKmt.Adapter? known, bool isNpu, int npuOrdinal)
    {
        uint vendor = known?.VendorId ?? 0;
        if (known?.VendorWasGuessed == true) VendorGuessed = true;

        string name = known?.Name
                      ?? (isNpu && npuOrdinal < _computeAcceleratorNames.Count
                          ? _computeAcceleratorNames[npuOrdinal]
                          : isNpu ? "NPU" : "가속기");

        if (vendor == 0) vendor = D3DKmt.GuessVendorFromName(name);

        var icon = isNpu ? IconKind.Npu : vendor switch
        {
            0x10DE => IconKind.GpuNvidia,
            0x1002 or 0x1022 => IconKind.GpuAmd,
            0x8086 => IconKind.GpuIntel,
            _ => IconKind.GpuGeneric,
        };

        string vendorName = vendor switch
        {
            0x10DE => "NVIDIA", 0x1002 or 0x1022 => "AMD", 0x8086 => "Intel", _ => "Unknown",
        };

        bool discrete = known?.IsDiscrete ?? false;
        string kind = isNpu ? "연산 전용" : discrete ? "외장" : "내장";
        string memory = known is null ? string.Empty
            : discrete ? $" · {known.DedicatedVideoMemory / (1024.0 * 1024 * 1024):0.#} GiB"
            : $" · 공유 {known.SharedSystemMemory / (1024.0 * 1024 * 1024):0.#} GiB";

        return new DeviceInfo(
            Key: $"gpu:{token}",
            Class: DeviceClass.Gpu,
            ShortName: isNpu ? ShortenNpu(name) : ShortenModel(name),
            FullName: $"{name} · {kind}{memory}",
            Icon: icon,
            Vendor: vendorName)
        {
            Extra = new Dictionary<string, string>
            {
                ["discrete"] = discrete ? "true" : "false",
                ["computeOnly"] = isNpu ? "true" : "false",
                ["dedicatedBytes"] = (known?.DedicatedVideoMemory ?? 0).ToString(),
                ["sharedBytes"] = (known?.SharedSystemMemory ?? 0).ToString(),
                ["vendorId"] = $"0x{vendor:X4}",
                ["luid"] = token,
                ["metadataSource"] = known is null ? "PDH 전용 (D3DKMT 미열거)" : "D3DKMT",
                // HAGS. 켜져 있으면 NVIDIA 의 CUDA 가 3D 엔진으로 합산된다(§5.4). MCP 의 aiSignals 가 읽는다.
                ["hardwareScheduling"] = known?.HardwareScheduling switch
                {
                    true => "true",
                    false => "false",
                    null => "unknown",
                },
            },
        };
    }

    /// <summary>"NVIDIA GeForce RTX 5080" → "RTX 5080".</summary>
    private static string ShortenModel(string name)
    {
        foreach (string prefix in (string[])["NVIDIA GeForce ", "NVIDIA ", "AMD Radeon ", "AMD ", "Intel(R) ", "Intel "])
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return name[prefix.Length..].Trim();

        return name;
    }

    /// <summary>"Intel(R) AI Boost" → "AI Boost NPU".</summary>
    private static string ShortenNpu(string name)
    {
        string trimmed = ShortenModel(name);
        return trimmed.Contains("NPU", StringComparison.OrdinalIgnoreCase) ? trimmed : $"{trimmed} NPU";
    }

    private static IEnumerable<string> ReadComputeAcceleratorNames()
    {
        RegistryKey? classKey = null;
        try { classKey = Registry.LocalMachine.OpenSubKey(ComputeAcceleratorClass); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }

        if (classKey is null) yield break;

        using (classKey)
        {
            foreach (string subKeyName in classKey.GetSubKeyNames())
            {
                // 장치 인스턴스는 0000·0001… 네 자리다. Properties 같은 하위 키는 건너뛴다.
                if (subKeyName.Length != 4 || !subKeyName.All(char.IsAsciiDigit)) continue;

                string? name = null;
                try
                {
                    using var device = classKey.OpenSubKey(subKeyName);
                    name = device?.GetValue("DriverDesc") as string;
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(name)) yield return name!;
            }
        }
    }

    public void Sample(in SampleWriter writer)
    {
        if (!IsAvailable || _query is null || _registry is null) return;

        if (TraceVendorCalls) SensorLog.Write($"[틱 {_tick + 1}] 수집");
        if (!_query.Collect()) return;

        _tick++;

        // 장치 이벤트가 온 틱에는 전원 상태를 바로 다시 본다. 분리된 eGPU 를 Slow 틱까지 기다리며
        // 계속 읽으면 그 사이에 네이티브 호출이 사라진 장치를 만난다.
        bool deviceEvent = _enumeratePending;

        if (_enumeratePending)
        {
            RefreshAdapters();
            Enumerate(_registry);
            // 이번 틱의 슬롯 버퍼에는 새 슬롯 자리가 없다. SampleWriter 가 범위 밖을 무시하므로
            // 다음 틱부터 값이 들어간다.
            if (_accelerators.Count > 0) _enumeratePending = false;
            PublishLayers();
        }

        if (_rebindPending) RebindVendors();

        if (TraceVendorCalls) SensorLog.Write($"[틱 {_tick}] 메모리");
        ReadMemory(writer);
        if (TraceVendorCalls) SensorLog.Write($"[틱 {_tick}] 벤더");
        ReadVendor(writer, deviceEvent);

        // 벤더 경로가 사용률을 매 틱 주는 어댑터는 비싼 엔진 와일드카드를 탈 이유가 없다.
        if (_tick % EngineEvery == 0)
        {
            if (TraceVendorCalls) SensorLog.Write($"[틱 {_tick}] 엔진");
            ReadEngines(writer);
            if (TraceVendorCalls) SensorLog.Write($"[틱 {_tick}] 엔진 완료");
        }

        if (_tick % VendorSlowEvery == 0) PublishLayers();
    }

    /// <summary>
    /// 온도·전력·클럭은 <b>PDH로는 얻을 수 없다</b>. 벤더 경로가 붙은 어댑터만 값이 들어간다.
    /// 사용률도 벤더 경로가 있으면 여기서 매 틱 갱신한다(PDH 엔진은 4틱마다라 덜 촘촘하다).
    /// </summary>
    private void ReadVendor(in SampleWriter writer, bool deviceEvent)
    {
        bool refreshPower = _tick % VendorSlowEvery == 0 || deviceEvent;
        List<Accelerator>? gone = null;

        // 미뤄둔 Intel 경로는 장치가 깨어난 뒤에 연다.
        if (refreshPower && _intelPathDeferred && AnyIntelAdapterAwake())
        {
            SensorLog.Write("Intel 어댑터가 깨어났다 — 벤더 경로 개방");
            OpenIntelPath();
            _rebindPending = true;
        }

        // 전원 상태는 벤더 경로가 붙지 않은 어댑터도 따라간다. 잠들어 있어서 아직 경로를 안 연
        // 어댑터가 언제 깨어나는지 봐야 하고, 그 전이 자체가 진단 정보다.
        if (refreshPower)
        {
            foreach (var accelerator in _accelerators.Values)
            {
                if (accelerator.PciAddress is not { } pci) continue;

                var state = _power.Query(pci);
                if (state == DevicePowerState.Gone)
                {
                    (gone ??= []).Add(accelerator);
                    continue;
                }

                if (state != accelerator.PowerState)
                {
                    SensorLog.Write($"{accelerator.Handle.Info.ShortName} 전원 {accelerator.PowerState} → {state}");

                    // 잠들어 있어서 벤더 경로를 못 붙였던 어댑터는 깨어난 지금 다시 시도한다.
                    // 이게 없으면 "앱을 켤 때 마침 자고 있던" 어댑터는 이후 아무리 일해도
                    // 온도·전력·클럭이 영영 비어 있다 — 값이 없는 이유도 화면에 드러나지 않는다.
                    if (state != DevicePowerState.Off && accelerator.Vendors.Count == 0)
                        _rebindPending = true;
                }

                accelerator.PowerState = state;
                if (state == DevicePowerState.Off) accelerator.HasSlept = true;
                accelerator.Handle.Availability = state == DevicePowerState.Off
                    ? DeviceAvailability.Standby
                    : DeviceAvailability.Active;
            }
        }

        foreach (var accelerator in _accelerators.Values)
        {
            if (accelerator.Vendors.Count == 0) continue;

            // 여러 경로가 붙어 있으면 값을 합친다. 먼저 값을 내놓은 경로가 이긴다 —
            // 바인딩 순서가 곧 우선순위다(NVML → Level Zero → IGCL).
            bool full = _tick % VendorSlowEvery == 0;

            // 저전력 대기 중인 장치는 건드리지 않는다. 벤더 SDK 로 값을 물으면 장치가 깨어나는데,
            // 모니터링 도구가 감시 대상을 깨우는 것은 그 자체로 틀렸다 — 전력을 쓰고, 지연이 생기고,
            // Intel Arc 에서는 전원 전이 중 네이티브 호출이 프로세스를 죽이기까지 했다.
            //
            // 가벼운 틱에도 사용률을 네이티브로 읽으므로(NVML·IGCL) 한 번이라도 잠든 적이 있는 장치는
            // 그때도 전원을 본다. 4틱마다만 보면 그 사이에 잠든 장치를 최대 세 번 두드리게 된다.
            // 전원 조회는 OS 캐시를 읽을 뿐이라 장치를 깨우지 않지만 공짜는 아니다 — 모든 어댑터에
            // 매 틱 걸었더니 듀티 사이클이 0.97% → 1.13% 로 올랐다. 잠들지 않는 데스크톱 GPU 에는 걸지 않는다.
            if (!full && accelerator.HasSlept && accelerator.PciAddress is { } address)
                accelerator.PowerState = _power.Query(address);

            if (accelerator.PowerState == DevicePowerState.Gone)
            {
                if (gone?.Contains(accelerator) != true) (gone ??= []).Add(accelerator);
                continue;
            }

            if (accelerator.PowerState == DevicePowerState.Off)
            {
                foreach (var binding in accelerator.Vendors) binding.Telemetry.SetActive(binding.Handle, false);
                accelerator.HasVendorUtil = false;
                accelerator.HasVendorMedia = false;
                if (full)
                {
                    writer.WriteUnavailable(accelerator.TempSlot);
                    writer.WriteUnavailable(accelerator.PowerSlot);
                    writer.WriteUnavailable(accelerator.ClockSlot);
                    writer.WriteUnavailable(accelerator.PowerLimitSlot);
                    writer.WriteUnavailable(accelerator.ThrottlePowerSlot);
                    writer.WriteUnavailable(accelerator.ThrottleThermalSlot);
                    writer.WriteUnavailable(accelerator.ThrottleOtherSlot);
                    _limitReasons[accelerator.Handle.Key] = null;
                }
                continue;
            }

            float util = float.NaN, memBusy = float.NaN, temp = float.NaN, power = float.NaN, clock = float.NaN;
            float renderCompute = float.NaN, media = float.NaN, pcieRx = float.NaN, pcieTx = float.NaN;
            float powerLimit = float.NaN;
            GpuLimitReasons? reasons = null;
            foreach (var binding in accelerator.Vendors)
            {
                binding.Telemetry.SetActive(binding.Handle, true);

                if (TraceVendorCalls && full)
                    SensorLog.Write($"→ {binding.Telemetry.Name} 읽기 {accelerator.Handle.Info.ShortName} " +
                                    $"(전원 {accelerator.PowerState})");

                bool got = binding.Telemetry.TryRead(binding.Handle, full, out var sample);

                if (TraceVendorCalls && full)
                    SensorLog.Write($"← {binding.Telemetry.Name} 읽기 완료");

                if (!got) continue;

                if (float.IsNaN(util)) util = sample.UtilPercent;
                if (float.IsNaN(memBusy)) memBusy = sample.MemBusyPercent;
                if (float.IsNaN(renderCompute)) renderCompute = sample.RenderComputePercent;
                if (float.IsNaN(media)) media = sample.MediaPercent;
                if (float.IsNaN(pcieRx)) pcieRx = sample.PcieRxBytesPerSecond;
                if (float.IsNaN(pcieTx)) pcieTx = sample.PcieTxBytesPerSecond;
                if (float.IsNaN(temp)) temp = sample.TemperatureCelsius;
                if (float.IsNaN(power)) power = sample.PowerWatts;
                if (float.IsNaN(clock)) clock = sample.CoreClockMegahertz;
                if (float.IsNaN(powerLimit)) powerLimit = sample.PowerLimitWatts;
                reasons ??= sample.LimitReasons;

                // 온도·전력·클럭이 다 찼으면 남은 경로는 물어볼 이유가 없다. 사용률을 조건에
                // 넣지 않는 것은, 그건 계층 A(PDH)가 이미 모든 어댑터에서 채우기 때문이다 —
                // 그것 하나 때문에 매 틱 네이티브 호출을 더 하는 것은 수지가 맞지 않는다.
                if (full && !float.IsNaN(temp) && !float.IsNaN(power) && !float.IsNaN(clock)) break;
            }

            accelerator.HasVendorUtil = !float.IsNaN(util);
            if (accelerator.HasVendorUtil) writer.Write(accelerator.UtilSlot, util);
            if (!float.IsNaN(memBusy)) writer.Write(accelerator.MemBusySlot, memBusy);
            if (!float.IsNaN(renderCompute)) writer.Write(accelerator.RenderComputeSlot, renderCompute);
            if (!float.IsNaN(pcieRx)) writer.Write(accelerator.PcieRxSlot, pcieRx);
            if (!float.IsNaN(pcieTx)) writer.Write(accelerator.PcieTxSlot, pcieTx);
            accelerator.HasVendorMedia = !float.IsNaN(media);
            if (accelerator.HasVendorMedia) writer.Write(accelerator.VideoSlot, media);

            if (!float.IsNaN(temp)) writer.Write(accelerator.TempSlot, temp);
            if (!float.IsNaN(power)) writer.Write(accelerator.PowerSlot, power);
            if (!float.IsNaN(clock)) writer.Write(accelerator.ClockSlot, clock);
            if (!float.IsNaN(powerLimit)) writer.Write(accelerator.PowerLimitSlot, powerLimit);

            // 제한 사유는 켜짐 100 · 꺼짐 0 으로 적는다. 1초에 한 번 찍으므로 구간 평균이 곧
            // "그 제한에 걸려 있던 시간 비율"이고, 포화 비율·이력도 다른 지표와 똑같이 나온다.
            if (full && reasons is { } r)
            {
                var (onPower, onThermal, onOther) = r.Split();
                writer.Write(accelerator.ThrottlePowerSlot, onPower ? 100 : 0);
                writer.Write(accelerator.ThrottleThermalSlot, onThermal ? 100 : 0);
                writer.Write(accelerator.ThrottleOtherSlot, onOther ? 100 : 0);
            }

            if (full) _limitReasons[accelerator.Handle.Key] = reasons;
        }

        if (gone is not null) RetireGone(gone);
    }

    /// <summary>
    /// 장치 노드가 사라진 어댑터를 놓는다. 벤더 경로를 먼저 끊고 카드를 은퇴시킨다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>PDH 가 아직 그 어댑터를 보여도 기다리지 않는다.</b> eGPU 를 뽑으면 그 GPU 를 쓰던 프로세스가
    /// 참조를 놓을 때까지 WDDM 어댑터와 PDH 인스턴스가 남는다. 그것을 기준으로 삼으면 카드는
    /// 한동안 대기로 보이고, 그 사이에 벤더 경로가 사라진 장치의 핸들을 다시 잡을 수 있다.
    /// </para>
    /// <para>
    /// 은퇴한 토큰은 PDH 에서 사라질 때까지 <see cref="Enumerate"/> 가 다시 등록하지 않는다.
    /// 히스토리는 은퇴 규칙대로 60초 보관되므로, 그 안에 다시 꽂으면 이어진다.
    /// </para>
    /// </remarks>
    private void RetireGone(List<Accelerator> gone)
    {
        foreach (var accelerator in gone)
        {
            foreach (var binding in accelerator.Vendors) binding.Telemetry.SetActive(binding.Handle, false);
            accelerator.Vendors.Clear();

            SensorLog.Write($"{accelerator.Handle.Info.ShortName} 분리됨 — 벤더 경로를 끊고 카드를 내린다");
            _registry?.Retire(accelerator.Handle.Key, DateTime.UtcNow.Ticks);
            _accelerators.Remove(accelerator.Token);
            _limitReasons.TryRemove(accelerator.Handle.Key, out _);
            _goneTokens.Add(accelerator.Token);
        }

        // 다음 틱에 다시 열거한다. WDDM 어댑터가 정말 빠졌으면 벤더 경로도 그때 다시 열린다(RefreshAdapters).
        _enumeratePending = true;
        PublishLayers();
    }

    /// <summary>분리돼 은퇴시킨 어댑터의 LUID 토큰. PDH 에서 사라질 때까지 다시 등록하지 않는다.</summary>
    private readonly HashSet<string> _goneTokens = new(StringComparer.OrdinalIgnoreCase);

    // 샘플링 스레드만 쓰고 MCP 스레드가 읽는다. 값 하나짜리 갱신이라 동시 사전으로 둔다.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, GpuLimitReasons?> _limitReasons =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 어댑터(장치 키)의 최근 클럭 제한 사유. 벤더 경로가 사유를 주지 않으면 null.
    /// <c>get_gpu_status</c> 가 이름 목록으로 보고한다.
    /// </summary>
    public GpuLimitReasons? LimitReasons(string deviceKey) => _limitReasons.GetValueOrDefault(deviceKey);

    private void ReadMemory(in SampleWriter writer)
    {
        foreach (var accelerator in _accelerators.Values) accelerator.Pending = default;

        _dedicated?.Read((instance, value) => Stash(instance, value, static (a, v) => a.Pending.Dedicated = v));
        _shared?.Read((instance, value) => Stash(instance, value, static (a, v) => a.Pending.Shared = v));

        foreach (var accelerator in _accelerators.Values)
        {
            writer.Write(accelerator.DedicatedSlot, (float)Math.Max(0, accelerator.Pending.Dedicated));
            writer.Write(accelerator.SharedSlot, (float)Math.Max(0, accelerator.Pending.Shared));
        }
    }

    /// <summary>
    /// 엔진 인스턴스를 어댑터별·<c>engtype</c>별로 모은다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 전부 더하면 100%를 훌쩍 넘는다. 작업 관리자와 같은 정의를 쓰려면
    /// <b>그룹 안에서는 합산하고 그룹끼리는 최댓값</b>을 취해야 한다.
    /// </para>
    /// <para>
    /// 쓰지 않은 인스턴스(<see cref="EngineCounterGuard"/> — 상한을 넘은 값, 깨진 카운터)가 있는 그룹은 남은 인스턴스의
    /// 합이 하한일 뿐이다. 하한이 이미 100 이면 그대로 맞으므로 쓰고, 아니면 그 그룹과 그것이 속한 계열·사용률을
    /// <b>측정 불가</b>로 적는다. 깨진 카운터가 있는 종류는 따로 알린다 — 한 틱 튄 것과 달리 계속 그렇기 때문이다.
    /// </para>
    /// </remarks>
    private void ReadEngines(in SampleWriter writer)
    {
        if (_engine is null)
        {
            foreach (var accelerator in _accelerators.Values)
            {
                writer.WriteUnavailable(accelerator.UtilSlot);
                writer.WriteUnavailable(accelerator.ComputeSlot);
                writer.WriteUnavailable(accelerator.Graphics3DSlot);
                writer.WriteUnavailable(accelerator.CopySlot);
                writer.WriteUnavailable(accelerator.VideoSlot);
            }
            return;
        }

        foreach (var accelerator in _accelerators.Values)
        {
            accelerator.EngineGroups.Clear();
            accelerator.UnknownGroups.Clear();
            accelerator.BrokenGroups.Clear();
        }

        bool watching = Watch.Any;
        if (watching) Watch.BeginEngines();
        _engineGuard.Begin();

        _engine.Read((instance, value) =>
        {
            var reading = _engineGuard.Read(instance, value);

            string? token = ParseLuidToken(instance);
            if (token is null || !_accelerators.TryGetValue(token, out var accelerator)) return;

            string group = ParseEngineType(instance);
            if (reading != EngineReading.Trusted)
            {
                MarkUnknown(accelerator, instance, group, reading == EngineReading.Broken);
                return;
            }

            accelerator.EngineGroups.TryGetValue(group, out double sum);
            accelerator.EngineGroups[group] = sum + value;

            if (watching) Watch.AddEngine(instance, accelerator.Handle.Key, group, value);
        }, noCap100: true, onInvalid: !_engineGuard.WantsInvalid ? null : instance =>
        {
            // 움직이던(또는 이미 깨진) 인스턴스가 음수 차분으로 무효가 됐다 — 누적값이 거꾸로 갔다.
            if (!_engineGuard.Invalid(instance)) return;
            if (ParseLuidToken(instance) is { } token && _accelerators.TryGetValue(token, out var accelerator))
                MarkUnknown(accelerator, instance, ParseEngineType(instance), broken: true);
        });

        _engineGuard.End();
        if (_engineGuard.Count > 0 || _brokenInstances.Count > 0) _brokenInstances = _engineGuard.SnapshotBroken();
        if (watching) Watch.Commit(DateTime.UtcNow.Ticks);

        var breakdown = new Dictionary<string, GpuEngineBreakdown>(StringComparer.OrdinalIgnoreCase);
        Span<double> families = stackalloc double[4];   // 3D · Compute · Copy · Video
        Span<bool> familyUnknown = stackalloc bool[4];

        foreach (var accelerator in _accelerators.Values)
        {
            double best = 0;
            families.Clear();
            familyUnknown.Clear();
            var types = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);

            foreach (var (group, sum) in accelerator.EngineGroups)
            {
                double value = Math.Clamp(sum, 0, 100);
                best = Math.Max(best, value);
                bool unknown = accelerator.UnknownGroups.Contains(group) && value < 100;
                types[GroupName(group)] = unknown ? null : Math.Round(value, 2);

                // 계열 안에서는 종류끼리 최댓값이다 — 사용률 정의(그룹 안은 합, 그룹끼리는 최댓값)를
                // 계열 단위로 옮긴 것이다. 이름을 묶는 규칙은 GpuEngineFamilies 에 있다.
                if (GpuEngineFamilies.Classify(group) is { } family)
                    families[FamilySlot(family)] = Math.Max(families[FamilySlot(family)], value);
            }

            // 쓰지 않은 인스턴스뿐이라 합이 없는 그룹도 있다. 그 그룹은 값이 아니라 측정 불가다.
            foreach (string group in accelerator.UnknownGroups)
            {
                types.TryAdd(GroupName(group), null);
                if (GpuEngineFamilies.Classify(group) is { } family) familyUnknown[FamilySlot(family)] = true;
            }

            // 벤더 경로가 이미 더 촘촘한 사용률을 넣었으면 덮어쓰지 않는다.
            if (!accelerator.HasVendorUtil)
                WriteOrUnknown(writer, accelerator.UtilSlot, best, accelerator.UnknownGroups.Count > 0);

            WriteOrUnknown(writer, accelerator.Graphics3DSlot, families[0], familyUnknown[0]);
            WriteOrUnknown(writer, accelerator.ComputeSlot, families[1], familyUnknown[1]);
            WriteOrUnknown(writer, accelerator.CopySlot, families[2], familyUnknown[2]);
            if (!accelerator.HasVendorMedia)
                WriteOrUnknown(writer, accelerator.VideoSlot, families[3], familyUnknown[3]);

            breakdown[accelerator.Handle.Key] = new GpuEngineBreakdown(
                types,
                accelerator.BrokenGroups.Count == 0
                    ? []
                    : accelerator.BrokenGroups.Select(GroupName).Order(StringComparer.OrdinalIgnoreCase).ToArray());
        }

        _engineBreakdown = breakdown;

        void MarkUnknown(Accelerator accelerator, string instance, string group, bool broken)
        {
            accelerator.UnknownGroups.Add(group);
            if (broken) accelerator.BrokenGroups.Add(group);
            if (watching) Watch.AddUnknownEngine(instance, accelerator.Handle.Key, group);
        }

        static string GroupName(string group) => group.Length == 0 ? "(unnamed)" : group;

        static int FamilySlot(MetricKind kind) => kind switch
        {
            MetricKind.Gpu3D => 0,
            MetricKind.GpuCompute => 1,
            MetricKind.GpuCopy => 2,
            _ => 3,
        };
    }

    /// <summary>
    /// 쓰지 않은 인스턴스가 섞인 값은 하한이다. 하한이 이미 100 이면 참값도 100 이므로 그대로 쓰고,
    /// 아니면 측정 불가로 적는다 — 0 을 쓰면 통계가 그것을 "쉬었다"로 센다.
    /// </summary>
    private static void WriteOrUnknown(in SampleWriter writer, int slot, double value, bool unknown)
    {
        if (unknown && value < 100) writer.WriteUnavailable(slot);
        else writer.Write(slot, (float)value);
    }

    private readonly EngineCounterGuard _engineGuard = new();

    // 샘플링 스레드가 통째로 갈아 끼우고 프로세스 수집 스레드가 읽는다. 깨진 것이 없으면 빈 집합 그대로다.
    private volatile HashSet<string> _brokenInstances = new(StringComparer.Ordinal);

    /// <summary>
    /// 이 엔진 인스턴스의 카운터가 깨졌다고 이미 알아봤는가. 프로세스 표(<see cref="ProcessProvider"/>)가 쓴다 —
    /// 그쪽은 요청이 있을 때만 켜져서 첫 응답 전에 누적값이 거꾸로 가는 것을 스스로 볼 기회가 없다.
    /// 이쪽은 1초마다 줄곧 보고 있다.
    /// </summary>
    public bool IsEngineInstanceBroken(string instance) => _brokenInstances.Contains(instance);

    // 샘플링 스레드가 통째로 갈아 끼우고 MCP 스레드가 읽는다. 사전을 고치지 않고 새로 만든다.
    private volatile Dictionary<string, GpuEngineBreakdown> _engineBreakdown =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>어댑터(장치 키)의 엔진 종류별 최근 사용률. <c>get_gpu_status(verbose)</c> 가 보고한다.</summary>
    public GpuEngineBreakdown? EngineBreakdown(string deviceKey) =>
        _engineBreakdown.GetValueOrDefault(deviceKey);

    /// <summary>인스턴스명 예: <c>pid_4_luid_0x00000000_0x000180A3_phys_0_eng_0_engtype_Neural</c>.</summary>
    private static string? ParseLuidToken(string instance)
    {
        var match = LuidPattern.Match(instance);
        return match.Success ? $"luid_0x{match.Groups["high"].Value}_0x{match.Groups["low"].Value}" : null;
    }

    private static string ParseEngineType(string instance)
    {
        const string marker = "engtype_";
        int at = instance.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? "Other" : instance[(at + marker.Length)..];
    }

    private void Stash(string instance, double value, Action<Accelerator, double> assign)
    {
        string? token = ParseLuidToken(instance);
        if (token is not null && _accelerators.TryGetValue(token, out var accelerator)) assign(accelerator, value);
    }

    /// <summary>
    /// Intel 경로를 연다. <b>Intel 어댑터가 하나도 깨어 있지 않으면 열지 않고 미룬다.</b>
    /// </summary>
    /// <remarks>
    /// <c>ctlInit</c> · <c>ctlEnumerateDevices</c> · <c>ctlGetDeviceProperties</c> 는 모두 장치를 건드려
    /// D3 에 내려가 있던 GPU 를 깨운다. 값을 읽을 때만 조심해서는 소용이 없다 —
    /// <b>여는 행위 자체가 깨우기 때문에</b> 여는 시점도 미뤄야 한다.
    /// 잠든 장치를 깨우지 않는 것이 목적이고, 전원 전이 중 네이티브 호출이 프로세스를 죽이는 것도 함께 피한다.
    /// </remarks>
    private void OpenIntelPath()
    {
        if (!UseVendorTelemetry) return;

        if (!AnyIntelAdapterAwake())
        {
            if (!_intelPathDeferred)
            {
                _intelPathDeferred = true;
                SensorLog.Write("Intel 어댑터가 저전력 대기 — 깨우지 않고 벤더 경로 개방을 미룬다");
            }
            return;
        }

        _intelPathDeferred = false;

        // IGCL 이 붙으면 Level Zero 는 열지 않는다. IGCL 을 CTL_INIT_FLAG_USE_LEVEL_ZERO 로 열면
        // IGCL 이 내부적으로 Level Zero 로더를 초기화하는데, 거기에 대고 zesInit 을 또 부르면
        // 같은 로더를 두 주인이 잡는다. 게다가 IGCL 이 주는 값(온도·전력·클럭)이
        // Level Zero 가 주는 값(클럭)을 완전히 포함하므로, 열어봐야 얻는 것도 없다.
        IVendorTelemetry? intel =
            IgclTelemetry.TryCreate() as IVendorTelemetry ?? LevelZeroTelemetry.TryCreate();

        if (intel is null) return;

        _vendors.Add(intel);
    }

    /// <summary>
    /// Intel 어댑터 중 하나라도 D0 인가. 전원 상태를 모르는 경우는 <b>깨어 있다고 본다</b> —
    /// 조회가 안 되는 시스템에서 Intel 텔레메트리를 영영 포기하는 쪽이 더 나쁘다.
    /// </summary>
    private bool AnyIntelAdapterAwake()
    {
        bool sawIntel = false;

        foreach (var adapter in _metadata.Values)
        {
            if (adapter.IsSoftware || adapter.VendorId != 0x8086u) continue;
            sawIntel = true;

            var state = _power.Query(new PciAddress(adapter.PciBus, adapter.PciDevice, adapter.PciFunction));
            if (state is not (DevicePowerState.Off or DevicePowerState.Gone)) return true;
        }

        // Intel 어댑터를 못 찾았으면 막을 이유가 없다. 판단은 IGCL 에 맡긴다.
        return !sawIntel;
    }

    /// <summary>
    /// 어댑터(장치 키)별로 어떤 계층이 붙었는지. <c>describe_capabilities</c>·<c>get_gpu_status</c> 가 보고한다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>샘플링 스레드에서 만든 사본을 준다.</b> 예전에는 MCP 스레드가 가속기 사전을 직접 훑었다 —
    /// 재열거와 겹치면 열거 중 수정 예외가 난다. 키도 이름이었는데, 같은 모델 두 장이면
    /// <c>ToDictionary</c> 가 중복 키로 던진다.
    /// </para>
    /// <para>
    /// 벤더 경로가 없는 이유가 절전이면 <c>PDH (standby)</c> 로 적는다. 절전 중인 어댑터는 깨우지 않으려고
    /// 벤더 경로를 열지 않으므로(§6.3), 같은 장치가 한 번은 PDH, 깨어난 뒤에는 IGCL 로 보인다 —
    /// 이유가 적혀 있지 않으면 두 툴이 서로 다른 말을 하는 것처럼 읽힌다.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, string> TelemetryLayers => _layers;

    private volatile IReadOnlyDictionary<string, string> _layers = new Dictionary<string, string>();

    private void PublishLayers()
    {
        var layers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in _accelerators.Values)
        {
            layers[a.Handle.Key] = a.Vendors.Count > 0
                ? string.Join("+", a.Vendors.Select(v => v.Telemetry.Name))
                : a.PowerState == DevicePowerState.Off ? "PDH (standby)" : "PDH";
        }
        _layers = layers;
    }

    public void Dispose()
    {
        Watch.Dispose();
        foreach (var vendor in _vendors) vendor.Dispose();
        _vendors.Clear();
        _engine?.Dispose();
        _dedicated?.Dispose();
        _shared?.Dispose();
        _query?.Dispose();
        _query = null;
        _accelerators.Clear();
        IsAvailable = false;
    }

    private sealed class Accelerator
    {
        public required string Token { get; init; }
        public required DeviceHandle Handle { get; init; }
        public bool IsNpu { get; init; }
        public int UtilSlot { get; init; }
        public int ComputeSlot { get; init; }
        public int DedicatedSlot { get; init; }
        public int SharedSlot { get; init; }
        public int TempSlot { get; init; }
        public int PowerSlot { get; init; }
        public int ClockSlot { get; init; }

        /// <summary>붙은 벤더 경로. 비어 있으면 계층 A(PDH)만 쓴다.</summary>
        public List<VendorBinding> Vendors { get; } = [];

        /// <summary>벤더 SDK 와 전원 상태 조회를 잇는 좌표. 모르면 null 이다.</summary>
        public PciAddress? PciAddress { get; set; }

        /// <summary>마지막으로 본 전원 상태. <see cref="DevicePowerState.Off"/> 면 건드리지 않는다.</summary>
        public DevicePowerState PowerState { get; set; } = DevicePowerState.Unknown;
        public bool HasVendorUtil { get; set; }

        /// <summary>절전(D3)에 들어간 것을 본 적이 있는가. 그런 장치만 매 틱 전원을 확인한다.</summary>
        public bool HasSlept { get; set; }
        public Dictionary<string, double> EngineGroups { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>이번 엔진 읽기에서 쓰지 않은 인스턴스가 있던 엔진 종류. 이번 틱 측정 불가다.</summary>
        public HashSet<string> UnknownGroups { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>그중 카운터가 깨진(누적값이 거꾸로 간) 인스턴스가 있던 엔진 종류.</summary>
        public HashSet<string> BrokenGroups { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Graphics3DSlot { get; init; }
        public int CopySlot { get; init; }
        public int VideoSlot { get; init; }
        public int MemBusySlot { get; init; }
        public int PowerLimitSlot { get; init; }
        public int ThrottlePowerSlot { get; init; }
        public int ThrottleThermalSlot { get; init; }
        public int ThrottleOtherSlot { get; init; }
        public int RenderComputeSlot { get; init; }
        public int PcieRxSlot { get; init; }
        public int PcieTxSlot { get; init; }

        /// <summary>
        /// 벤더 경로가 미디어 활동을 매 틱 주는가. 그러면 PDH 의 1초 Video 계열로 덮어쓰지 않는다 —
        /// 하드웨어 카운터 쪽이 촘촘하고 튀지 않는다. B580 의 QSV 인코딩은 PDH 에서 copy 로만 잡혔다.
        /// </summary>
        public bool HasVendorMedia { get; set; }
        public Memory Pending;

        public struct Memory
        {
            public double Dedicated;
            public double Shared;
        }
    }
}

/// <summary>어댑터 하나의 엔진 종류별 최근 사용률(§5.4).</summary>
/// <param name="Types">엔진 종류 → 사용률(%). 그룹 안은 합이다. 카운터가 깨져 알 수 없으면 null.</param>
/// <param name="Broken">카운터가 깨진 인스턴스가 있는 엔진 종류. 드라이버가 누적값을 거꾸로 돌린 경우다.</param>
public sealed record GpuEngineBreakdown(IReadOnlyDictionary<string, double?> Types, IReadOnlyList<string> Broken);

/// <summary>한 어댑터에 붙은 벤더 SDK 하나와 그 안에서의 장치 인덱스.</summary>
internal readonly record struct VendorBinding(IVendorTelemetry Telemetry, int Handle);

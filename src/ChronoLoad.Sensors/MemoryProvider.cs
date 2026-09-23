using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Metrics;
using ChronoLoad.Core.Sensors;
using ChronoLoad.Sensors.Native;

namespace ChronoLoad.Sensors;

/// <summary>
/// 물리 메모리 사용량과 커밋 차지. PDH를 거치지 않고 <c>GlobalMemoryStatusEx</c>를 직접 부른다 —
/// 호출 비용이 마이크로초 단위라 카운터 인프라를 쓸 이유가 없다.
/// </summary>
public sealed class MemoryProvider : ISensorProvider
{
    private int _usedSlot = -1;
    private int _commitSlot = -1;
    private ulong _totalPhysical;

    public string Id => "memory";
    public SensorTier Tier => SensorTier.Fast;
    public bool IsAvailable { get; private set; }

    /// <summary>총 물리 메모리. 변하지 않으므로 시리즈가 아니라 장치 정보로 노출한다.</summary>
    public ulong TotalPhysicalBytes => _totalPhysical;

    public ValueTask InitializeAsync(MetricRegistry registry, CancellationToken cancellationToken)
    {
        var status = new MemoryStatusEx { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatusEx>() };
        IsAvailable = Kernel32.GlobalMemoryStatusEx(ref status);
        _totalPhysical = IsAvailable ? status.TotalPhys : 0;

        var info = new DeviceInfo(
            Key: "memory",
            Class: DeviceClass.System,
            ShortName: $"메모리 {FormatGiB(_totalPhysical)}",
            FullName: $"물리 메모리 {FormatGiB(_totalPhysical)} · 커밋 한도 {FormatGiB(ReadCommitLimit())}",
            Icon: IconKind.Memory)
        {
            Extra = new Dictionary<string, string>
            {
                ["totalBytes"] = _totalPhysical.ToString(),
            },
        };

        var handle = registry.Register(info, [MetricKind.MemUsed, MetricKind.MemCommit]);
        _usedSlot = handle.SlotOf(MetricKind.MemUsed);
        _commitSlot = handle.SlotOf(MetricKind.MemCommit);
        return ValueTask.CompletedTask;
    }

    public void Sample(in SampleWriter writer)
    {
        var status = new MemoryStatusEx { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatusEx>() };
        if (Kernel32.GlobalMemoryStatusEx(ref status))
        {
            _totalPhysical = status.TotalPhys;
            writer.Write(_usedSlot, status.TotalPhys - status.AvailPhys);
        }
        else
        {
            writer.WriteUnavailable(_usedSlot);
        }

        var perf = new PerformanceInformation { Cb = (uint)System.Runtime.InteropServices.Marshal.SizeOf<PerformanceInformation>() };
        if (Kernel32.GetPerformanceInfo(ref perf, perf.Cb))
            writer.Write(_commitSlot, (float)((double)perf.CommitTotal * perf.PageSize));
        else
            writer.WriteUnavailable(_commitSlot);
    }

    private static ulong ReadCommitLimit()
    {
        var perf = new PerformanceInformation { Cb = (uint)System.Runtime.InteropServices.Marshal.SizeOf<PerformanceInformation>() };
        return Kernel32.GetPerformanceInfo(ref perf, perf.Cb)
            ? (ulong)perf.CommitLimit * perf.PageSize
            : 0;
    }

    private static string FormatGiB(ulong bytes) =>
        bytes == 0 ? "?" : $"{bytes / (1024.0 * 1024 * 1024):0.#}G";

    public void Dispose() => IsAvailable = false;
}

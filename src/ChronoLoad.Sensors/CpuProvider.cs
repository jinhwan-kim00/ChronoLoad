using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Metrics;
using ChronoLoad.Core.Sensors;
using ChronoLoad.Sensors.Native;
using Microsoft.Win32;

namespace ChronoLoad.Sensors;

/// <summary>
/// CPU 총 사용률. 1차는 <c>% Processor Utility</c>로 간다 — 터보와 코어 파킹을 반영해
/// 작업 관리자가 보여주는 값과 일치하기 때문이다. 없는 시스템에서는 <c>% Processor Time</c>으로 내려간다.
/// </summary>
/// <remarks>
/// 코어별 사용률은 오버레이에서만 쓰이므로 Slow 티어의 별도 프로바이더로 M4에서 붙인다.
/// 여기서 와일드카드 인스턴스를 열거하면 Fast 티어 비용이 괜히 올라간다.
/// </remarks>
public sealed class CpuProvider : ISensorProvider
{
    private const string UtilityPath = @"\Processor Information(_Total)\% Processor Utility";
    private const string TimePath = @"\Processor Information(_Total)\% Processor Time";

    private PdhQuery? _query;
    private PdhCounter? _counter;
    private bool _usingFallback;
    private int _slot = -1;

    public string Id => "cpu";
    public SensorTier Tier => SensorTier.Fast;
    public bool IsAvailable { get; private set; }

    /// <summary>폴백 카운터를 쓰고 있는지. <c>describe_capabilities</c>에 보고된다.</summary>
    public bool UsingFallbackCounter => _usingFallback;

    public ValueTask InitializeAsync(MetricRegistry registry, CancellationToken cancellationToken)
    {
        _query = PdhQuery.TryOpen();
        if (_query is not null)
        {
            _counter = _query.TryAddCounter(UtilityPath);
            if (_counter is null)
            {
                _counter = _query.TryAddCounter(TimePath);
                _usingFallback = _counter is not null;
            }
        }

        IsAvailable = _counter is not null;
        if (IsAvailable) _query!.Collect();   // 델타 기준선

        var info = new DeviceInfo(
            Key: "cpu",
            Class: DeviceClass.System,
            ShortName: "CPU",
            FullName: BuildFullName(),
            Icon: IconKind.Cpu)
        {
            Extra = new Dictionary<string, string>
            {
                ["logicalCores"] = Environment.ProcessorCount.ToString(),
                ["counter"] = _usingFallback ? "% Processor Time (폴백)" : "% Processor Utility",
            },
        };

        var handle = registry.Register(info, [MetricKind.CpuTotal]);
        _slot = handle.SlotOf(MetricKind.CpuTotal);
        return ValueTask.CompletedTask;
    }

    public void Sample(in SampleWriter writer)
    {
        if (!IsAvailable || _slot < 0)
        {
            if (_slot >= 0) writer.WriteUnavailable(_slot);
            return;
        }

        if (!_query!.Collect())
        {
            writer.WriteUnavailable(_slot);
            return;
        }

        double value = _counter!.Read(noCap100: true);
        if (double.IsNaN(value))
        {
            writer.WriteUnavailable(_slot);
            return;
        }

        // % Processor Utility 는 터보 상태에서 100을 넘긴다. 표시는 100에서 자른다.
        writer.Write(_slot, (float)Math.Clamp(value, 0, 100));
    }

    private static string BuildFullName()
    {
        string name = "CPU";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("ProcessorNameString") is string s && s.Length > 0)
                name = s.Trim();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // 권한이 없으면 이름만 포기한다. 측정에는 영향이 없다.
        }

        return $"{name} · 논리 코어 {Environment.ProcessorCount}";
    }

    public void Dispose()
    {
        _query?.Dispose();
        _query = null;
        _counter = null;
        IsAvailable = false;
    }
}

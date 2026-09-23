using System.Globalization;
using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Metrics;
using ChronoLoad.Core.Sensors;
using ChronoLoad.Sensors.Native;

namespace ChronoLoad.Sensors;

/// <summary>
/// 물리 디스크별 읽기·쓰기 처리량과 활성 시간.
/// </summary>
/// <remarks>
/// <para>
/// PDH 와일드카드 카운터 하나로 전 인스턴스를 읽는다. 디스크마다 카운터를 붙이면
/// 핸들과 호출이 디스크 수만큼 늘어난다.
/// </para>
/// <para>
/// <b>합산 카드는 만들지 않는다.</b> NVMe 7GB/s 와 HDD 200MB/s 를 같은 축에 올리면
/// HDD 포화가 평균에 묻힌다(설계서 §5.5).
/// </para>
/// </remarks>
public sealed class DiskProvider : ISensorProvider
{
    private const string ReadPath = @"\PhysicalDisk(*)\Disk Read Bytes/sec";
    private const string WritePath = @"\PhysicalDisk(*)\Disk Write Bytes/sec";
    private const string IdlePath = @"\PhysicalDisk(*)\% Idle Time";

    private readonly Dictionary<int, Disk> _disks = [];
    private PdhQuery? _query;
    private PdhCounterArray? _read;
    private PdhCounterArray? _write;
    private PdhCounterArray? _idle;
    private MetricRegistry? _registry;
    // 장치 이벤트 스레드가 쓰고 샘플링 스레드가 읽는다.
    private volatile bool _enumeratePending = true;

    public string Id => "disk";
    public SensorTier Tier => SensorTier.Fast;
    public bool IsAvailable { get; private set; }

    public ValueTask InitializeAsync(MetricRegistry registry, CancellationToken cancellationToken)
    {
        _registry = registry;
        _query = PdhQuery.TryOpen();

        if (_query is not null)
        {
            _read = _query.TryAddArray(ReadPath);
            _write = _query.TryAddArray(WritePath);
            _idle = _query.TryAddArray(IdlePath);
        }

        IsAvailable = _read is not null && _write is not null;
        if (!IsAvailable) return ValueTask.CompletedTask;

        _query!.Collect();      // 델타 기준선
        // 비율 카운터는 수집이 두 번 쌓이기 전에는 인스턴스 배열조차 돌려주지 않는다.
        // 그래서 열거는 첫 유효 샘플까지 미룬다.
        _enumeratePending = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>디스크 착탈 시 다음 샘플에서 다시 열거하도록 표시한다.</summary>
    public void RequestEnumerate() => _enumeratePending = true;

    /// <summary>PDH 인스턴스 목록에서 물리 디스크를 찾아 등록한다.</summary>
    public void Enumerate(MetricRegistry registry)
    {
        var seen = new HashSet<int>();

        _read!.Read((instance, _) =>
        {
            // 인스턴스 이름은 "0 C:" 또는 "1 D: E:". _Total 은 합산이라 건너뛴다.
            if (instance.StartsWith("_Total", StringComparison.OrdinalIgnoreCase)) return;
            if (!TryParseIndex(instance, out int index)) return;

            seen.Add(index);
            if (_disks.ContainsKey(index)) return;

            var media = StorageNative.Query(index);
            var handle = registry.Register(BuildInfo(index, instance, media),
                [MetricKind.DiskRead, MetricKind.DiskWrite, MetricKind.DiskActive]);

            _disks[index] = new Disk
            {
                Handle = handle,
                ReadSlot = handle.SlotOf(MetricKind.DiskRead),
                WriteSlot = handle.SlotOf(MetricKind.DiskWrite),
                ActiveSlot = handle.SlotOf(MetricKind.DiskActive),
                Instance = instance,
            };
        });

        SensorLog.Write($"디스크 {_disks.Count}개 등록");

        foreach (int index in _disks.Keys.ToArray())
        {
            if (seen.Contains(index)) continue;
            registry.Retire(_disks[index].Handle.Key, DateTime.UtcNow.Ticks);
            _disks.Remove(index);
        }
    }

    private static bool TryParseIndex(string instance, out int index)
    {
        int space = instance.IndexOf(' ');
        string head = space < 0 ? instance : instance[..space];
        return int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out index);
    }

    private static DeviceInfo BuildInfo(int index, string instance, StorageNative.DiskMedia media)
    {
        var icon = media.IncursSeekPenalty switch
        {
            true => IconKind.DiskHdd,
            false => IconKind.DiskSsd,
            null => IconKind.DiskGeneric,
        };

        string mediaText = media.IncursSeekPenalty switch
        {
            true => "HDD",
            false => "SSD",
            null => "드라이브",
        };

        string letters = instance.Contains(' ') ? instance[(instance.IndexOf(' ') + 1)..] : string.Empty;
        string bus = media.BusType == StorageBusType.Unknown ? string.Empty : media.BusType.ToString().ToUpperInvariant();

        return new DeviceInfo(
            Key: $"disk:{index}",
            Class: DeviceClass.Disk,
            ShortName: letters.Length > 0 ? $"{letters} {mediaText}" : $"디스크 {index} {mediaText}",
            FullName: $"PhysicalDrive{index} · {mediaText}{(bus.Length > 0 ? " · " + bus : "")}" +
                      $"{(letters.Length > 0 ? " · " + letters : "")}",
            Icon: icon)
        {
            Extra = new Dictionary<string, string>
            {
                ["bus"] = bus,
                ["media"] = mediaText,
                ["instance"] = instance,
            },
        };
    }

    public void Sample(in SampleWriter writer)
    {
        if (!IsAvailable || _query is null || _registry is null) return;
        if (!_query.Collect()) return;

        if (_enumeratePending)
        {
            Enumerate(_registry);
            // 이번 틱의 슬롯 버퍼는 이미 잡혀 있어 새 슬롯이 들어갈 자리가 없다.
            // SampleWriter 가 범위를 넘는 슬롯을 무시하므로 다음 틱부터 값이 들어간다.
            if (_disks.Count > 0) _enumeratePending = false;
        }

        foreach (var disk in _disks.Values) disk.Pending = default;

        _read!.Read((instance, value) => Stash(instance, value, static (d, v) => d.Pending.Read = v));
        _write!.Read((instance, value) => Stash(instance, value, static (d, v) => d.Pending.Write = v));

        // % Idle Time 은 샘플 경계 문제로 100을 넘거나 음수가 될 수 있다. 반드시 클램프한다.
        _idle?.Read((instance, value) =>
            Stash(instance, value, static (d, v) => d.Pending.Active = Math.Clamp(100 - v, 0, 100)));

        foreach (var disk in _disks.Values)
        {
            writer.Write(disk.ReadSlot, (float)Math.Max(0, disk.Pending.Read));
            writer.Write(disk.WriteSlot, (float)Math.Max(0, disk.Pending.Write));
            if (disk.ActiveSlot >= 0) writer.Write(disk.ActiveSlot, (float)disk.Pending.Active);
        }
    }

    private void Stash(string instance, double value, Action<Disk, double> assign)
    {
        if (!TryParseIndex(instance, out int index)) return;
        if (_disks.TryGetValue(index, out var disk)) assign(disk, value);
    }

    public void Dispose()
    {
        _read?.Dispose();
        _write?.Dispose();
        _idle?.Dispose();
        _query?.Dispose();
        _query = null;
        _disks.Clear();
        IsAvailable = false;
    }

    private sealed class Disk
    {
        public required DeviceHandle Handle { get; init; }
        public int ReadSlot { get; init; }
        public int WriteSlot { get; init; }
        public int ActiveSlot { get; init; }
        public required string Instance { get; init; }
        public Sample Pending;

        public struct Sample
        {
            public double Read;
            public double Write;
            public double Active;
        }
    }
}

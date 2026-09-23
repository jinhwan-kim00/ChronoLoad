using ChronoLoad.Core.Metrics;
using ChronoLoad.Core.Sensors;
using ChronoLoad.Sensors.Native;

namespace ChronoLoad.Sensors;

/// <summary>
/// 논리 코어별 사용률. CPU 카드의 오버레이 코어 미니 바가 읽는다 (§5.1, UX §04).
/// </summary>
/// <remarks>
/// <para>
/// <b>총 사용률과 프로바이더를 나눈 이유는 티어다.</b> 총합은 Fast(250ms)이고 코어별은
/// Slow(1000ms)다. 와일드카드 인스턴스 열거는 코어 수만큼 문자열을 만들어 내므로
/// Fast 티어에 두면 아끼려던 비용이 그대로 돌아온다.
/// </para>
/// <para>
/// <b>CPU 장치에 채널로 붙는다.</b> 코어를 각각 장치로 등록하면 카드가 코어 수만큼 생기고,
/// 지표 종류로 등록하면 <c>CpuCore0..63</c> 같은 열거가 필요하다. 둘 다 코어 수가 화면과
/// MCP 목록에 새어 나가게 만든다 — 코어 값은 오버레이에서만 읽히므로 채널이 맞다.
/// </para>
/// </remarks>
public sealed class CoreProvider : ISensorProvider
{
    private const string UtilityPath = @"\Processor Information(*)\% Processor Utility";
    private const string TimePath = @"\Processor Information(*)\% Processor Time";

    /// <summary>
    /// 합계 인스턴스. <c>Processor Information</c> 은 코어마다 <c>0,0</c> 같은 이름을 주면서
    /// 그룹 합계 <c>0,_Total</c> 과 전체 합계 <c>_Total</c> 도 같이 돌려준다.
    /// 거르지 않으면 코어가 실제보다 두 개 많아지고 그 둘만 항상 평균값이라 눈에 띄지도 않는다.
    /// </summary>
    private const string TotalMarker = "_Total";

    private PdhQuery? _query;
    private PdhCounterArray? _array;
    private bool _usingFallback;

    /// <summary>인스턴스 이름 → 채널 인덱스. 순서는 초기화 때 고정한다.</summary>
    private readonly Dictionary<string, int> _channelByName = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<int> _slots = [];

    /// <summary>
    /// 이번 틱의 코어 값. <see cref="SampleWriter"/> 는 <c>ref struct</c> 라 콜백이 잡을 수 없어
    /// 한 번 거쳐 간다. 델리게이트도 필드에 캐시해 틱마다 할당이 생기지 않게 한다.
    /// </summary>
    private float[] _values = [];
    private readonly Action<string, double> _onItem;

    public CoreProvider()
    {
        _onItem = (name, value) =>
        {
            if (!_channelByName.TryGetValue(name, out int channel)) return;
            if ((uint)channel >= (uint)_values.Length) return;

            // % Processor Utility 는 터보에서 100 을 넘는다. 막대는 0~100 눈금이라 잘라서 쓴다.
            _values[channel] = (float)Math.Clamp(value, 0, 100);
        };
    }

    public string Id => "cpu:cores";
    public SensorTier Tier => SensorTier.Slow;
    public bool IsAvailable { get; private set; }

    /// <summary>찾아낸 논리 코어 수. 0이면 이 경로가 동작하지 않은 것이다.</summary>
    public int CoreCount => _slots.Count;

    public ValueTask InitializeAsync(MetricRegistry registry, CancellationToken cancellationToken)
    {
        // CPU 장치에 얹히므로 CpuProvider 가 먼저 등록되어 있어야 한다.
        if (registry.Find("cpu") is not { } cpu)
        {
            SensorLog.Write("코어별 사용률: CPU 장치가 아직 없다 — CpuProvider 뒤에 추가해야 한다");
            return ValueTask.CompletedTask;
        }

        _query = PdhQuery.TryOpen();
        if (_query is not null)
        {
            _array = _query.TryAddArray(UtilityPath);
            if (_array is null)
            {
                _array = _query.TryAddArray(TimePath);
                _usingFallback = _array is not null;
            }
        }

        if (_array is null) return ValueTask.CompletedTask;

        // 첫 수집은 델타 기준선이라 값이 없다. 인스턴스 이름은 두 번째 수집에서야 온전히 나온다.
        _query!.Collect();
        _query.Collect();

        var names = new List<string>();
        _array.Read((name, _) => { if (!name.Contains(TotalMarker, StringComparison.OrdinalIgnoreCase)) names.Add(name); });

        if (names.Count == 0)
        {
            SensorLog.Write("코어별 사용률: 인스턴스를 찾지 못했다");
            return ValueTask.CompletedTask;
        }

        names.Sort(CompareInstance);
        for (int i = 0; i < names.Count; i++) _channelByName[names[i]] = i;

        // 단위가 총 사용률과 같으므로 종류도 같다. 채널은 종류가 아니라 자리로 구분된다.
        _slots = registry.RegisterChannels(cpu, MetricKind.CpuTotal, names.Count);
        _values = new float[names.Count];
        IsAvailable = true;

        // 효율 등급은 채널 순서와 같은 순서로 장치 정보에 얹는다. 시계열이 아니라 부팅 후
        // 고정인 값이라 슬롯을 쓸 이유가 없고, 여기 두면 MCP 의 장치 정보에도 그대로 나간다.
        string classes = EfficiencyClasses(names);
        if (classes.Length > 0)
        {
            var extra = new Dictionary<string, string>(cpu.Info.Extra) { ["coreEfficiencyClasses"] = classes };
            registry.Register(cpu.Info with { Extra = extra }, [MetricKind.CpuTotal]);
        }

        SensorLog.Write($"코어별 사용률 {names.Count}개" + (_usingFallback ? " (% Processor Time 폴백)" : "")
                        + (classes.Length > 0 ? $" · 효율 등급 {classes}" : " · 효율 등급 없음"));
        return ValueTask.CompletedTask;
    }

    public void Sample(in SampleWriter writer)
    {
        if (!IsAvailable || _array is null) return;

        // 이번 틱에 이름이 빠진 코어는 값이 없는 것이다. 직전 값을 남겨두면 파킹된 코어가
        // 계속 바쁜 것처럼 보이므로, 먼저 전부 비워두고 들어온 것만 채운다.
        Array.Fill(_values, float.NaN);

        if (_query!.Collect()) _array.Read(_onItem, noCap100: true);

        for (int i = 0; i < _slots.Count; i++) writer.Write(_slots[i], _values[i]);
    }

    /// <summary>
    /// 채널 순서대로의 효율 등급을 <c>1,1,1,1,0,0,0,0</c> 형태로 만든다.
    /// 등급을 못 읽거나 전부 같으면 빈 문자열 — 구분할 것이 없으면 적지 않는다.
    /// </summary>
    private static string EfficiencyClasses(List<string> names)
    {
        var map = CpuSets.EfficiencyClasses();
        if (map.Count == 0) return string.Empty;

        var classes = new byte[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            var (group, processor) = ParseInstance(names[i]);
            if (processor > byte.MaxValue || !map.TryGetValue(((ushort)group, (byte)processor), out byte cls))
                return string.Empty;        // 하나라도 못 맞추면 순서가 어긋난 것이다

            classes[i] = cls;
        }

        return classes.Distinct().Count() < 2 ? string.Empty : string.Join(',', classes);
    }

    /// <summary>
    /// <c>0,10</c> 이 <c>0,9</c> 보다 앞에 오지 않게 그룹·번호를 숫자로 비교한다.
    /// 문자열 정렬로 두면 코어가 12개를 넘는 기기에서 막대 순서가 뒤엉킨다.
    /// </summary>
    private static int CompareInstance(string a, string b)
    {
        var (ga, ca) = ParseInstance(a);
        var (gb, cb) = ParseInstance(b);
        int byGroup = ga.CompareTo(gb);
        return byGroup != 0 ? byGroup
             : ca != cb ? ca.CompareTo(cb)
             : string.CompareOrdinal(a, b);
    }

    /// <summary><c>0,3</c> → 그룹 0, 논리 프로세서 3.</summary>
    private static (int Group, int Processor) ParseInstance(string name)
    {
        int comma = name.IndexOf(',');
        if (comma < 0) return (0, int.TryParse(name, out int only) ? only : int.MaxValue);

        int group = int.TryParse(name.AsSpan(0, comma), out int g) ? g : 0;
        int processor = int.TryParse(name.AsSpan(comma + 1), out int c) ? c : int.MaxValue;
        return (group, processor);
    }

    public void Dispose()
    {
        IsAvailable = false;
        _array?.Dispose();
        _query?.Dispose();
        _array = null;
        _query = null;
    }
}

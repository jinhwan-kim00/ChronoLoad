using System.Diagnostics;
using ChronoLoad.Core.Metrics;
using ChronoLoad.Core.Sensors;

namespace ChronoLoad.Core.Sampling;

public sealed record SamplingOptions
{
    /// <summary>기본 250ms. 100ms는 델타 카운터의 양자화 노이즈가 커지고, 500ms는 짧은 스파이크를 놓친다.</summary>
    public TimeSpan FastPeriod { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan SlowPeriod { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan LazyPeriod { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>연속 실패가 이 횟수에 닿으면 프로바이더를 잠시 끈다.</summary>
    public int FailuresBeforeDisable { get; init; } = 3;

    /// <summary>끈 프로바이더를 다시 시도하기까지의 간격.</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>샘플링이 주기의 이 비율을 넘으면 과부하로 본다.</summary>
    public double SlowTickWarnRatio { get; init; } = 0.2;

    /// <summary>
    /// 과부하 판정을 몇 틱마다 내릴지. 매 틱 판단하면 우연한 스파이크 한 번에 주기가 내려간다 —
    /// 실측에서 틱별 소요는 1300~3800µs 로 세 배 가까이 흔들린다.
    /// </summary>
    public int OverloadCheckEvery { get; init; } = 40;

    /// <summary>
    /// 과부하 판정에 쓰는 지수이동평균의 가중치. 작을수록 느리게 반응한다.
    /// </summary>
    public double OverloadSmoothing { get; init; } = 0.1;

    /// <summary>
    /// 사라진 장치의 시리즈·통계를 붙잡아 두는 기간 (§5.7). 이 안에 같은 키로 돌아오면
    /// 누적 통계가 이어진다 — Wi-Fi 를 껐다 켰다고 벤치마크 구간이 날아가면 안 된다.
    /// </summary>
    public TimeSpan DeviceRetentionGrace { get; init; } = MetricRegistry.DefaultRetentionGrace;
}

/// <summary>
/// 샘플 주기 단계 (§6.3 적응형 백오프). 값은 기본 주기에 곱하는 배수다.
/// </summary>
/// <remarks>
/// 화면을 아무도 보고 있지 않을 때까지 250ms 로 도는 것은 R9("저부하")를 배신하는 일이다.
/// 다만 <b>렌더와 달리 샘플링은 함부로 끊지 않는다</b> — 시계열에 생긴 구멍은 나중에 메울 수 없다.
/// 그래서 멈추는 대신 느리게 간다.
/// </remarks>
public enum SamplePace
{
    /// <summary>사용자가 보고 있다. 설계 기본 주기.</summary>
    Full = 1,

    /// <summary>배터리 + 절전 모드. 모니터가 배터리를 갉아먹으면 본말전도다.</summary>
    Reduced = 2,

    /// <summary>최소화·클로킹. 값은 계속 쌓되 가장 느리게.</summary>
    Background = 4,
}

/// <summary>한 틱이 커밋됐다는 알림. UI는 이걸 받아 무효화만 하고 값은 레지스트리에서 읽는다.</summary>
public readonly record struct SampleCommitted(long TimestampUtcTicks, long TickIndex, int SlotCount);

/// <summary>
/// 2-티어 샘플 루프. WPF <c>DispatcherTimer</c>는 UI 부하에 밀리므로 쓰지 않고
/// 전용 백그라운드 태스크에서 <see cref="PeriodicTimer"/>로 돈다.
/// </summary>
/// <remarks>
/// <para>
/// <b>모든 시리즈는 Fast 틱마다 기록된다.</b> Slow 티어 프로바이더는 값을 덜 자주 갱신할 뿐이고
/// 엔진은 직전 값을 그대로 다시 쓴다. 이렇게 해야 전 카드가 <b>동일한 시간 축</b>을 갖고,
/// 동기화 스크럽(UX §08)에서 같은 인덱스가 같은 시각을 가리킨다.
/// </para>
/// <para>
/// <c>timeBeginPeriod</c>는 호출하지 않는다. 시스템 전역 타이머 해상도를 올리면 전력 소비가 늘어나는데,
/// 모니터링 도구가 관측 대상을 바꾸는 것은 부적절하다.
/// </para>
/// </remarks>
public sealed class SampleEngine : IAsyncDisposable
{
    private readonly MetricRegistry _registry;
    private readonly SamplingOptions _options;
    private readonly List<ProviderState> _providers = [];
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();

    private float[] _scratch = [];
    private bool[] _measured = [];
    private int _scratchRevision = -1;
    private int _notifiedRevision = -1;
    private long _lastPurgeTimestamp;
    private PeriodicTimer? _timer;
    private SamplePace _requestedPace = SamplePace.Full;
    private SamplePace _overloadPace = SamplePace.Full;
    private double _durationEwmaSeconds;
    private Task? _loop;
    private long _lastTimestamp;

    public SampleEngine(MetricRegistry registry, SamplingOptions? options = null)
    {
        _registry = registry;
        _options = options ?? new SamplingOptions();
    }

    public event Action<SampleCommitted>? Committed;

    /// <summary>
    /// 장치 집합이 실제로 바뀌었을 때만 발생한다. 재열거를 했어도 결과가 같으면 발생하지 않는다 —
    /// 구독자(UI·MCP)는 이 이벤트만 보고 카드를 다시 지으면 된다.
    /// </summary>
    public event Action<int>? DevicesChanged;

    /// <summary>
    /// 모든 프로바이더에 재열거를 요청한다. <see cref="DeviceWatcher"/> 가 호출한다.
    /// 어느 스레드에서 불러도 안전하며, 실제 열거는 다음 틱에 일어난다.
    /// </summary>
    public void RequestRescan()
    {
        ISensorProvider[] snapshot;
        lock (_gate) snapshot = _providers.Select(p => p.Provider).ToArray();

        foreach (var provider in snapshot)
        {
            try { provider.RequestEnumerate(); }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChronoLoad] {provider.Id} 재열거 요청 실패: {ex.Message}");
            }
        }
    }

    public long TickCount { get; private set; }

    /// <summary>직전 틱의 샘플링 소요 시간. 성능 점검용.</summary>
    public TimeSpan LastSampleDuration { get; private set; }

    /// <summary>
    /// 지금까지 샘플링에 쓴 시간의 총합. §12 의 듀티 사이클 목표는 <b>평균</b> 기준이라,
    /// 마지막 한 틱만 재면 판단할 수 없다 — 틱마다 몇 배씩 흔들린다.
    /// </summary>
    public TimeSpan TotalSampleDuration { get; private set; }

    /// <summary>표본 구간 전체의 평균 샘플링 시간.</summary>
    public TimeSpan MeanSampleDuration =>
        TickCount == 0 ? TimeSpan.Zero : TotalSampleDuration / TickCount;

    /// <summary>
    /// 바깥이 요청한 단계(창 상태·전원). 내부 과부하 강등과 <b>따로</b> 관리하고 둘 중 느린 쪽을 쓴다 —
    /// 섞어 두면 창을 복원했을 때 과부하 강등까지 함께 풀려 버린다.
    /// </summary>
    public SamplePace RequestedPace
    {
        get => _requestedPace;
        set
        {
            if (_requestedPace == value) return;
            _requestedPace = value;
            ApplyPace();
        }
    }

    /// <summary>실제로 적용 중인 단계. 요청과 과부하 강등 중 느린 쪽이다.</summary>
    public SamplePace EffectivePace =>
        (SamplePace)Math.Max((int)_requestedPace, (int)_overloadPace);

    /// <summary>지금 적용 중인 Fast 티어 주기.</summary>
    public TimeSpan EffectiveFastPeriod => _options.FastPeriod * (int)EffectivePace;

    public IReadOnlyList<string> DisabledProviders
    {
        get
        {
            lock (_gate)
                return _providers.Where(p => p.DisabledUntil is not null).Select(p => p.Provider.Id).ToArray();
        }
    }

    public async ValueTask AddProviderAsync(ISensorProvider provider, CancellationToken cancellationToken = default)
    {
        try
        {
            await provider.InitializeAsync(_registry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"[ChronoLoad] {provider.Id} 초기화 실패: {ex.Message}");
        }

        lock (_gate) _providers.Add(new ProviderState(provider));
    }

    public void Start()
    {
        if (_loop is not null) throw new InvalidOperationException("이미 시작됐다.");
        _lastTimestamp = Stopwatch.GetTimestamp();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>테스트·하네스용. 루프를 돌리지 않고 한 틱만 수동으로 진행한다.</summary>
    public void TickOnce(double? forcedElapsedSeconds = null)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = forcedElapsedSeconds ?? Math.Max(1e-6,
            (double)(now - _lastTimestamp) / Stopwatch.Frequency);
        _lastTimestamp = now;
        Tick(elapsed);
    }

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(EffectiveFastPeriod);
        _timer = timer;
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                long now = Stopwatch.GetTimestamp();
                double elapsed = Math.Max(1e-6, (double)(now - _lastTimestamp) / Stopwatch.Frequency);
                _lastTimestamp = now;
                Tick(elapsed);
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료
        }
        finally
        {
            _timer = null;
        }
    }

    private void Tick(double elapsedSeconds)
    {
        long begin = Stopwatch.GetTimestamp();
        long nowUtc = DateTime.UtcNow.Ticks;
        TickCount++;

        EnsureScratch();
        // 이번 틱에 실제로 측정된 슬롯만 통계에 반영한다. 나머지는 직전 값이 그대로 유지된다.
        Array.Clear(_measured);

        int slowEvery = Math.Max(1, (int)Math.Round(_options.SlowPeriod / _options.FastPeriod));
        int lazyEvery = Math.Max(1, (int)Math.Round(_options.LazyPeriod / _options.FastPeriod));

        ProviderState[] snapshot;
        lock (_gate) snapshot = _providers.ToArray();

        foreach (var state in snapshot)
        {
            bool due = state.Provider.Tier switch
            {
                SensorTier.Fast => true,
                SensorTier.Slow => TickCount % slowEvery == 0,
                _ => TickCount % lazyEvery == 0,
            };
            if (!due) continue;

            if (state.DisabledUntil is { } until)
            {
                if (nowUtc < until) continue;
                state.DisabledUntil = null;
                state.ConsecutiveFailures = 0;
            }

            if (!state.Provider.IsAvailable) continue;

            // 프로바이더가 마지막으로 돈 뒤 실제로 흐른 시간 — 델타 카운터가 이 값으로 나눈다.
            double providerElapsed = state.LastSampledTimestamp == 0
                ? elapsedSeconds
                : Math.Max(1e-6, (double)(begin - state.LastSampledTimestamp) / Stopwatch.Frequency);

            try
            {
                var writer = new SampleWriter(_scratch, _measured, nowUtc, providerElapsed);
                state.Provider.Sample(in writer);
                state.LastSampledTimestamp = begin;
                state.ConsecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                state.ConsecutiveFailures++;
                Debug.WriteLine($"[ChronoLoad] {state.Provider.Id} 샘플 실패 " +
                                $"({state.ConsecutiveFailures}회): {ex.Message}");

                if (state.ConsecutiveFailures >= _options.FailuresBeforeDisable)
                    state.DisabledUntil = nowUtc + _options.RetryInterval.Ticks;
            }
        }

        // Slow 티어 값은 갱신되지 않아도 직전 값이 스크래치에 남아 있으므로 시간 축이 어긋나지 않는다.
        // 다만 유지된 값은 실측이 아니므로 통계에서는 빠진다.
        // 시각은 틱 시작값을 쓴다. 프로바이더가 본 것(SampleWriter)과 같은 값이어야
        // 시간 축과 델타 계산이 같은 순간을 가리킨다.
        _registry.CommitAll(_scratch, _measured, nowUtc);

        LastSampleDuration = Stopwatch.GetElapsedTime(begin);
        TotalSampleDuration += LastSampleDuration;
        TrackOverload();
        if (LastSampleDuration > _options.FastPeriod * _options.SlowTickWarnRatio)
            Debug.WriteLine($"[ChronoLoad] 틱 {TickCount} 샘플링 {LastSampleDuration.TotalMilliseconds:F1}ms " +
                            $"— 주기의 {_options.SlowTickWarnRatio:P0}를 넘었다.");

        Committed?.Invoke(new SampleCommitted(nowUtc, TickCount, _scratch.Length));

        PurgeAndNotify(nowUtc, begin);
    }

    /// <summary>
    /// 유예가 지난 장치의 슬롯을 회수하고, 장치 집합이 바뀌었으면 알린다.
    /// </summary>
    /// <remarks>
    /// 회수를 매 틱 돌리지 않는 것은 레지스트리 잠금을 잡기 때문이다. 1초에 한 번이면 충분하다 —
    /// 유예 자체가 60초라 몇백 ms 늦게 회수해도 아무 차이가 없다.
    /// </remarks>
    private void PurgeAndNotify(long nowUtc, long begin)
    {
        if (Stopwatch.GetElapsedTime(_lastPurgeTimestamp, begin) >= TimeSpan.FromSeconds(1))
        {
            _lastPurgeTimestamp = begin;
            _registry.PurgeRetired(nowUtc, _options.DeviceRetentionGrace);
        }

        int revision = _registry.Revision;
        if (revision == _notifiedRevision) return;

        _notifiedRevision = revision;
        try { DevicesChanged?.Invoke(revision); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ChronoLoad] 장치 변경 구독자 예외: {ex.Message}");
        }
    }

    /// <summary>
    /// 샘플링이 예산을 계속 넘으면 스스로 한 단계 느려지고, 여유가 돌아오면 되돌린다 (§6.3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>단일 틱이 아니라 지수이동평균으로 판단한다.</b> 틱별 소요는 실측에서 세 배 가까이
    /// 흔들려서, 한 번 튄 값으로 강등하면 멀쩡한 기기가 계속 느린 주기로 떨어진다.
    /// </para>
    /// <para>
    /// 되돌리는 문턱은 내리는 문턱의 절반이다. 같은 값이면 경계에서 오르내리기를 반복한다.
    /// </para>
    /// </remarks>
    private void TrackOverload()
    {
        double seconds = LastSampleDuration.TotalSeconds;
        _durationEwmaSeconds = _durationEwmaSeconds == 0
            ? seconds
            : _durationEwmaSeconds + _options.OverloadSmoothing * (seconds - _durationEwmaSeconds);

        if (_options.OverloadCheckEvery <= 0 || TickCount % _options.OverloadCheckEvery != 0) return;

        double budget = EffectiveFastPeriod.TotalSeconds * _options.SlowTickWarnRatio;
        var before = _overloadPace;

        if (_durationEwmaSeconds > budget && _overloadPace != SamplePace.Background)
        {
            _overloadPace = _overloadPace == SamplePace.Full ? SamplePace.Reduced : SamplePace.Background;
            Debug.WriteLine($"[ChronoLoad] 샘플링 {_durationEwmaSeconds * 1000:F1}ms 가 예산 " +
                            $"{budget * 1000:F1}ms 를 넘어 {_overloadPace} 로 강등");
        }
        else if (_durationEwmaSeconds < budget * 0.5 && _overloadPace != SamplePace.Full)
        {
            _overloadPace = _overloadPace == SamplePace.Background ? SamplePace.Reduced : SamplePace.Full;
        }

        if (before != _overloadPace) ApplyPace();
    }

    /// <summary>
    /// 주기를 갈아끼운다. 루프를 다시 만들지 않는 이유는 그 사이에 틱이 비기 때문이다 —
    /// <see cref="PeriodicTimer.Period"/> 는 돌고 있는 타이머에 바로 적용된다.
    /// </summary>
    private void ApplyPace()
    {
        var timer = _timer;
        if (timer is null) return;

        try { timer.Period = EffectiveFastPeriod; }
        catch (ObjectDisposedException) { /* 종료 중이다 */ }
    }

    private void EnsureScratch()
    {
        int revision = _registry.Revision;
        if (revision == _scratchRevision && _scratch.Length >= _registry.SlotCount) return;

        int needed = _registry.SlotCount;
        var next = new float[needed];
        Array.Fill(next, float.NaN);
        // 기존 값을 옮겨 Slow 티어의 "직전 값 유지"가 장치 변경에도 끊기지 않게 한다.
        _scratch.AsSpan(0, Math.Min(_scratch.Length, needed)).CopyTo(next);
        _scratch = next;
        _measured = new bool[needed];
        _scratchRevision = revision;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        lock (_gate)
        {
            foreach (var state in _providers) state.Provider.Dispose();
            _providers.Clear();
        }

        _cts.Dispose();
    }

    private sealed class ProviderState(ISensorProvider provider)
    {
        public ISensorProvider Provider { get; } = provider;
        public int ConsecutiveFailures { get; set; }
        public long? DisabledUntil { get; set; }
        public long LastSampledTimestamp { get; set; }
    }
}

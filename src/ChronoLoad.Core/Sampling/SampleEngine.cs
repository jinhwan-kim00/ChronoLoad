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
/// <b>우선순위를 높인 전용 스레드</b>에서 돈다.
/// </summary>
/// <remarks>
/// <para>
/// <b>모든 시리즈는 Fast 틱마다 기록된다.</b> Slow 티어 프로바이더는 값을 덜 자주 갱신할 뿐이고
/// 엔진은 직전 값을 그대로 다시 쓴다. 이렇게 해야 전 카드가 <b>동일한 시간 축</b>을 갖고,
/// 동기화 스크럽(UX §08)에서 같은 인덱스가 같은 시각을 가리킨다.
/// </para>
/// <para>
/// <b>왜 전용 스레드이고 왜 우선순위를 올리는가.</b> 예전에는 스레드 풀 위의 <see cref="PeriodicTimer"/> 였다.
/// 감시 대상이 CPU 를 다 쓰면 모니터가 CPU 를 못 받는다 — 전 코어를 채운 실측에서 30초 동안 120 틱이어야 할
/// 것이 42 틱만 돌았고 간격이 최대 1.2초로 벌어졌다. 부하를 재려는 순간에 시계열이 가장 성겨지는 셈이다.
/// 샘플러는 CPU 를 1% 안팎만 쓰므로 스레드 하나를
/// <see cref="ThreadPriority.Highest"/> 로 올려도 감시 대상을 밀어내지 않는다. 프로세스 우선순위는 건드리지
/// 않는다 — UI 와 MCP 까지 올릴 이유는 없다.
/// </para>
/// <para>
/// <b>우선순위만으로는 모자랐다.</b> 틱을 멈춘 것은 스케줄링이 아니라 네트워크 카운터 호출이 부하 속에서
/// 막히는 것이었다. 그 호출은 <c>NetworkProvider</c> 가 자기 스레드로 옮겼다. 틱 하나가 느린 프로바이더
/// 하나에 전부 묶이므로, 어느 프로바이더가 얼마나 걸리는지를 <see cref="ProviderDurations"/> 로 남긴다.
/// </para>
/// <para>
/// <b>밀린 틱은 따라잡지 않는다.</b> <see cref="PeriodicTimer"/> 는 틱이 늦으면 다음 틱을 곧바로 한 번 더 돌린다.
/// 그 틱은 PDH 비율 카운터의 수집 간격이 0 에 가까워 값이 튄다. 늦으면 늦은 대로 다음 주기부터 다시 센다 —
/// 시간 축(§7.4)이 틱마다 시각을 남기므로 간격이 벌어진 것은 그대로 드러난다.
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
    private Thread? _thread;

    // 주기가 바뀌면 자고 있던 루프를 깨워 새 주기로 다시 예약하게 한다.
    private readonly AutoResetEvent _paceChanged = new(false);
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

    /// <summary>
    /// 진단용. 프로바이더별 평균·최대 소요 시간. 샘플링이 느려졌을 때 어느 센서 탓인지 가른다.
    /// </summary>
    public IReadOnlyList<(string Id, TimeSpan Mean, TimeSpan Max, long Samples)> ProviderDurations
    {
        get
        {
            lock (_gate)
                return _providers.Select(p => (p.Provider.Id,
                    p.Samples == 0 ? TimeSpan.Zero : p.TotalDuration / p.Samples, p.MaxDuration, p.Samples)).ToArray();
        }
    }

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

    /// <summary>
    /// 배속을 적용하지 <b>않은</b> Fast 주기. "정상 간격"의 기준이 필요한 곳이 쓴다 —
    /// 공백 판정 문턱(§7.4)을 현재 배속에 맞추면, 느려진 상태에서 문턱까지 같이 느슨해져
    /// 정작 절전 복귀를 놓친다.
    /// </summary>
    public TimeSpan NominalFastPeriod => _options.FastPeriod;

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

    /// <summary>샘플링 스레드의 우선순위. 감시 대상이 CPU 를 다 써도 제때 깨어나야 한다.</summary>
    public const ThreadPriority SamplerPriority = ThreadPriority.Highest;

    public void Start()
    {
        if (_loop is not null) throw new InvalidOperationException("이미 시작됐다.");
        _lastTimestamp = Stopwatch.GetTimestamp();

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _loop = done.Task;
        _thread = new Thread(() =>
        {
            try { Run(_cts.Token); }
            finally { done.TrySetResult(); }
        })
        {
            Name = "ChronoLoad sampler",
            IsBackground = true,
            Priority = SamplerPriority,
        };
        _thread.Start();
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

    /// <summary>
    /// 샘플링 루프. 다음 틱 시각까지 기다렸다가 한 틱을 돌린다. 주기는 매번 다시 읽는다 —
    /// 적응형 백오프(§6.3)가 바꾼 주기가 다음 틱부터 바로 적용된다.
    /// </summary>
    private void Run(CancellationToken token)
    {
        WaitHandle[] wake = [token.WaitHandle, _paceChanged];
        long last = Stopwatch.GetTimestamp();
        long next = last + PeriodTicks();

        while (!token.IsCancellationRequested)
        {
            long wait = next - Stopwatch.GetTimestamp();
            if (wait > 0)
            {
                int signaled = WaitHandle.WaitAny(wake, TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency));
                if (signaled == 0) break;

                // 주기가 바뀌었다 — 직전 틱에서 새 주기만큼 뒤로 다시 잡는다. 1초 주기로 자던 루프가
                // 창을 복원한 뒤에도 옛 예약 시각까지 자지 않게 한다.
                if (signaled == 1)
                {
                    next = last + PeriodTicks();
                    continue;
                }
            }

            long now = Stopwatch.GetTimestamp();
            double elapsed = Math.Max(1e-6, (double)(now - _lastTimestamp) / Stopwatch.Frequency);
            _lastTimestamp = now;
            last = now;

            try
            {
                Tick(elapsed);
            }
            catch (Exception ex)
            {
                // 프로바이더 예외는 Tick 안에서 잡는다. 여기까지 온 것은 엔진 자체의 문제라
                // 루프를 죽이지 않고 다음 틱을 본다 — 모니터가 조용히 멈추는 것이 더 나쁘다.
                Debug.WriteLine($"[ChronoLoad] 틱 실패: {ex}");
            }

            // 다음 틱은 이번 틱의 예정 시각에서 한 주기 뒤. 이미 지났으면 따라잡지 않고 지금부터 한 주기 뒤다.
            long period = PeriodTicks();
            next += period;
            long after = Stopwatch.GetTimestamp();
            if (next <= after) next = after + period;
        }
    }

    private long PeriodTicks() => (long)(EffectiveFastPeriod.TotalSeconds * Stopwatch.Frequency);

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
                long providerBegin = Stopwatch.GetTimestamp();
                state.Provider.Sample(in writer);
                var took = Stopwatch.GetElapsedTime(providerBegin);
                state.TotalDuration += took;
                state.Samples++;
                if (took > state.MaxDuration) state.MaxDuration = took;
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
    /// 주기를 갈아끼운다. 루프는 매 틱 <see cref="EffectiveFastPeriod"/> 를 다시 읽지만, 자고 있는 동안에는
    /// 옛 예약 시각까지 깨지 않으므로 깨워서 새 주기로 다시 예약하게 한다.
    /// </summary>
    private void ApplyPace()
    {
        try { _paceChanged.Set(); }
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
        _paceChanged.Dispose();
    }

    private sealed class ProviderState(ISensorProvider provider)
    {
        public ISensorProvider Provider { get; } = provider;
        public int ConsecutiveFailures { get; set; }
        public long? DisabledUntil { get; set; }
        public long LastSampledTimestamp { get; set; }
        public TimeSpan TotalDuration { get; set; }
        public TimeSpan MaxDuration { get; set; }
        public long Samples { get; set; }
    }
}

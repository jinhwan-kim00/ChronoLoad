using System.Text.Json;
using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Metrics;
using ChronoLoad.Mcp;
using ChronoLoad.Sensors;

namespace ChronoLoad.Core.Tests;

/// <summary>
/// 드라이버가 엔진 누적값을 거꾸로 돌려 PDH 카운터가 깨진 경우와, 정상 카운터가 계상이 몰려 튄 경우를 가른다 (§5.4).
/// 실측값을 그대로 쓴다 — 깨짐은 Arc 130V 의 OpenVINO 추론(<c>engtype_Neural</c>), 정상 튐은 같은 GPU 의 ffmpeg OpenCL.
/// </summary>
public class BrokenEngineCounterTests
{
    private const string Neural = "pid_332392_luid_0x00000000_0x0001301B_phys_0_eng_13_engtype_Neural";
    private const string Idle = "pid_4_luid_0x00000000_0x0001301B_phys_0_eng_13_engtype_Neural";
    private const string OpenCl = "pid_6816_luid_0x00000000_0x0001301B_phys_0_eng_5_engtype_Compute";

    private static void Tick(EngineCounterGuard guard, Action body)
    {
        guard.Begin();
        body();
        guard.End();
    }

    [Fact]
    public void A_counter_that_runs_backwards_stays_untrusted_while_it_lives()
    {
        var guard = new EngineCounterGuard();

        // 1초 차분 +28.7e9(100ns) → 279,947%. 이것만으로는 깨짐인지 모른다 — 이번 값만 쓰지 않는다.
        Tick(guard, () =>
        {
            Assert.Equal(EngineReading.Skipped, guard.Read(Neural, 279947.485));
            Assert.Equal(EngineReading.Trusted, guard.Read(Idle, 0));
        });
        Assert.Equal(0, guard.Count);

        // 다음 틱은 차분이 음수(−13.8e9)라 PDH 가 상태 무효를 준다. 직전 읽기에서 유효했던 인스턴스라 거꾸로 간 것이다.
        Tick(guard, () =>
        {
            Assert.True(guard.Invalid(Neural));
            Assert.Equal(EngineReading.Trusted, guard.Read(Idle, 0));
        });
        Assert.Equal(1, guard.Count);

        // 쓰레기가 우연히 그럴듯한 범위에 떨어져도 믿지 않는다 — 걸러지지 않으면 100 으로 잘려 거짓 포화가 된다.
        Tick(guard, () => Assert.Equal(EngineReading.Broken, guard.Read(Neural, 54.2)));
        Tick(guard, () => Assert.Equal(EngineReading.Broken, guard.Read(Neural, 27007.512)));
        Assert.Equal(1, guard.Count);
    }

    /// <summary>
    /// 130V 실측(09:40:15~19): 부하 회차 사이 4초 동안 원시값이 멈춰 0 을 내다가, 재개 첫 읽기에서 바로 거꾸로 갔다.
    /// 앱이 그 휴식 중에 켜지면 이 인스턴스가 "움직인 적"이 없다. 그래도 직전 읽기에서 유효했으므로 깨짐이다 —
    /// 놓치면 남은 다른 프로세스의 0 이 GpuCompute 의 유일한 표본이 되어 통계가 0% 로 굳는다.
    /// </summary>
    [Fact]
    public void A_counter_that_was_idle_and_then_runs_backwards_is_broken()
    {
        var guard = new EngineCounterGuard();

        for (int i = 0; i < 4; i++)
            Tick(guard, () => Assert.Equal(EngineReading.Trusted, guard.Read(Neural, 0)));

        Tick(guard, () => Assert.True(guard.Invalid(Neural)));      // d1 = −15,485,284,158
        Assert.Equal(1, guard.Count);
    }

    [Fact]
    public void Idle_zeros_do_not_earn_trust_back()
    {
        var guard = new EngineCounterGuard();
        Tick(guard, () => guard.Read(Neural, 0));
        Tick(guard, () => guard.Invalid(Neural));

        // 휴식이 10초를 넘어도 풀리지 않는다. 풀리면 재개 첫 무효가 다시 새 인스턴스의 것처럼 지나간다.
        for (int i = 0; i < EngineCounterGuard.ForgiveAfter * 3; i++)
            Tick(guard, () => Assert.Equal(EngineReading.Broken, guard.Read(Neural, 0)));

        Tick(guard, () => Assert.True(guard.Invalid(Neural)));
        Assert.Equal(1, guard.Count);
    }

    [Fact]
    public void A_normal_counter_that_bunched_up_is_skipped_once_not_called_broken()
    {
        var guard = new EngineCounterGuard();

        // ffmpeg nlmeans_opencl: 원시값은 단조 증가인데 계상이 몰려 1초에 1,413% 가 들어왔다.
        Tick(guard, () => Assert.Equal(EngineReading.Skipped, guard.Read(OpenCl, 1413)));
        Tick(guard, () => Assert.Equal(EngineReading.Trusted, guard.Read(OpenCl, 375.98)));
        Tick(guard, () => Assert.Equal(EngineReading.Trusted, guard.Read(OpenCl, 0)));
        Assert.Equal(0, guard.Count);
    }

    [Fact]
    public void A_new_instance_with_invalid_status_is_not_called_broken()
    {
        var guard = new EngineCounterGuard();

        // 새 인스턴스는 기준선이 없어 첫 수집에서 늘 무효다. 그것을 깨짐으로 세면 측정 불가가 일상이 된다.
        Assert.False(guard.WantsInvalid);           // 첫 읽기의 무효는 전부 기준선이 없어서다
        Tick(guard, () => Assert.False(guard.Invalid(Neural)));
        Tick(guard, () => Assert.Equal(EngineReading.Trusted, guard.Read(Neural, 37.5)));
        Assert.Equal(0, guard.Count);
        Assert.True(guard.WantsInvalid);

        // 한 번 빠졌다가(프로세스 재시작 등) 다시 나타난 인스턴스도 직전 읽기에 없었으므로 새 것이다.
        Tick(guard, () => guard.Read(Idle, 0));
        Tick(guard, () => Assert.False(guard.Invalid(Neural)));
        Assert.Equal(0, guard.Count);
    }

    [Fact]
    public void A_broken_instance_survives_a_short_absence_and_is_forgotten_after_a_long_one()
    {
        var guard = new EngineCounterGuard();
        Tick(guard, () => guard.Read(Neural, 73127.215));
        Tick(guard, () => guard.Invalid(Neural));
        Assert.Equal(1, guard.Count);

        // 한 틱 열거에서 빠져도 잊지 않는다. 다시 나타난 첫 무효는 기준선이 없어서일 수도 있지만 이미 깨진 인스턴스다.
        Tick(guard, () => guard.Read(Idle, 0));
        Tick(guard, () => Assert.True(guard.Invalid(Neural)));
        Assert.Equal(1, guard.Count);

        // 오래 보이지 않으면 프로세스가 끝난 것으로 본다.
        for (int i = 0; i < EngineCounterGuard.ForgetAfter; i++) Tick(guard, () => guard.Read(Idle, 0));
        Assert.Equal(0, guard.Count);

        // 같은 이름이 다시 나타나면(PID 재사용) 새 인스턴스로 받는다.
        Tick(guard, () => Assert.Equal(EngineReading.Trusted, guard.Read(Neural, 12)));
    }

    [Fact]
    public void A_long_run_of_plausible_values_earns_trust_back_and_invalid_status_resets_it()
    {
        var guard = new EngineCounterGuard();
        Tick(guard, () => guard.Read(Neural, 40));
        Tick(guard, () => guard.Invalid(Neural));    // 드라이버 재시작처럼 한 번 되감겼을 수도 있다

        for (int i = 1; i < EngineCounterGuard.ForgiveAfter - 1; i++)
            Tick(guard, () => Assert.Equal(EngineReading.Broken, guard.Read(Neural, 40)));

        // 무효 상태(음수 차분)는 연속을 끊는다. 깨진 카운터가 그렇게 섞여 나온다.
        Tick(guard, () => Assert.True(guard.Invalid(Neural)));

        for (int i = 1; i < EngineCounterGuard.ForgiveAfter; i++)
            Tick(guard, () => Assert.Equal(EngineReading.Broken, guard.Read(Neural, 40)));

        Tick(guard, () => Assert.Equal(EngineReading.Trusted, guard.Read(Neural, 40)));
        Assert.Equal(0, guard.Count);
    }
    [Theory]
    [InlineData(0, true)]
    [InlineData(1000, true)]
    [InlineData(1000.1, false)]
    [InlineData(-0.1, false)]
    [InlineData(double.NaN, false)]
    [InlineData(1.8e14, false)]     // B580 OpenCL
    public void Plausible_range(double value, bool plausible) =>
        Assert.Equal(plausible, EngineCounterGuard.IsPlausible(value));

    private static readonly int Self = Environment.ProcessId;
    private static string Instance(int pid, string engineType, int engine = 0) =>
        $"pid_{pid}_luid_0x00000000_0x0001301B_phys_0_eng_{engine}_engtype_{engineType}";

    [Fact]
    public void A_watched_family_with_a_broken_counter_is_unknown_not_zero()
    {
        using var watch = new ProcessWatch();
        watch.Watch(Self, TimeSpan.FromMinutes(1));
        long t0 = DateTime.UtcNow.Ticks;

        watch.BeginEngines();
        watch.AddEngine(Instance(Self, "3D"), "gpu:a", "3D", 8);
        watch.AddUnknownEngine(Instance(Self, "Neural", 13), "gpu:a", "Neural");
        watch.Commit(t0);

        // 같은 계열의 다른 엔진이 이미 100 이면 깨진 카운터와 무관하게 참값이 100 이다.
        watch.BeginEngines();
        watch.AddEngine(Instance(Self, "Compute", 2), "gpu:a", "Compute", 100);
        watch.AddUnknownEngine(Instance(Self, "Neural", 13), "gpu:a", "Neural");
        watch.Commit(t0 + TimeSpan.TicksPerSecond);

        var history = watch.Get(Self)!;
        var compute = history.Engines[("gpu:a", MetricKind.GpuCompute)];
        Assert.True(float.IsNaN(compute[0]));
        Assert.Equal(100f, compute[1]);
        Assert.Equal(8f, history.Engines[("gpu:a", MetricKind.Gpu3D)][0]);
    }

    private static DeviceHandle IntegratedArc()
    {
        var registry = new MetricRegistry(seriesCapacity: 16);
        return registry.Register(
            new DeviceInfo("gpu:luid_0x00000000_0x0001301B", DeviceClass.Gpu, "Arc 130V", "Arc 130V", IconKind.GpuIntel, "Intel")
            {
                Extra = new Dictionary<string, string> { ["discrete"] = "false", ["hardwareScheduling"] = "true" },
            },
            [MetricKind.GpuUtil, MetricKind.GpuCompute, MetricKind.Gpu3D, MetricKind.GpuRenderCompute, MetricKind.GpuShared]);
    }

    [Fact]
    public void A_broken_family_leaves_the_primary_signals_and_the_note_says_why()
    {
        var arc = IntegratedArc();
        var healthy = AiSignals.For(arc);
        var broken = AiSignals.For(arc, ["Neural"]);

        Assert.Contains("GpuCompute", healthy.Primary);
        Assert.DoesNotContain("GpuCompute", broken.Primary);
        Assert.Equal("GpuRenderCompute", broken.Primary[0]);
        Assert.Contains("Neural", broken.Note);
        Assert.Contains("GpuRenderCompute 로 판단한다", broken.Note);
    }

    [Fact]
    public void Gpu_status_says_which_counters_are_broken_and_leaves_compute_unknown()
    {
        var registry = new MetricRegistry(seriesCapacity: 16);
        var arc = registry.Register(
            new DeviceInfo("gpu:luid_0x00000000_0x0001301B", DeviceClass.Gpu, "Arc 130V", "Arc 130V", IconKind.GpuIntel, "Intel")
            {
                Extra = new Dictionary<string, string> { ["discrete"] = "false" },
            },
            [MetricKind.GpuUtil, MetricKind.GpuCompute, MetricKind.GpuRenderCompute]);
        registry.CommitAll([99.9f, float.NaN, 96f], [true, true, true]);

        var ctx = new McpContext(registry, new ChronoLoad.Core.Sampling.SampleEngine(registry))
        {
            EngineBreakdown = key => key == arc.Key
                ? new GpuEngineBreakdown(new Dictionary<string, double?> { ["Neural"] = null, ["3D"] = 7.71 }, ["Neural"])
                : null,
        };

        var adapter = JsonSerializer.SerializeToElement(new ChronoLoadTools(ctx).GetGpuStatus(verbose: true))
            .GetProperty("adapters")[0];

        Assert.Equal(JsonValueKind.Null, adapter.GetProperty("computePercent").ValueKind);   // 0 이 아니다
        Assert.Equal("Neural", adapter.GetProperty("brokenEngineCounters")[0].GetString());
        Assert.Equal(JsonValueKind.Null, adapter.GetProperty("engineTypes").GetProperty("Neural").ValueKind);
        Assert.DoesNotContain("GpuCompute",
            adapter.GetProperty("aiSignals").GetProperty("primary").EnumerateArray().Select(e => e.GetString()));
    }

    private sealed class Rows(params ProcessRow[] rows) : IProcessSource
    {
        public void KeepAlive() { }
        public IReadOnlyList<ProcessRow> Snapshot() => rows;
        public DateTimeOffset? SampledAt => DateTimeOffset.Now;
    }

    private static readonly Dictionary<string, double> NoGpu = new();
    private static readonly Dictionary<string, long> NoMemory = new();

    [Fact]
    public async Task Gpu_sort_keeps_a_process_whose_gpu_use_could_not_be_measured()
    {
        const string key = "gpu:luid_0x00000000_0x0001301B";
        var registry = new MetricRegistry(seriesCapacity: 16);
        var ctx = new McpContext(registry, new ChronoLoad.Core.Sampling.SampleEngine(registry))
        {
            Processes = new Rows(
                new ProcessRow(332392, "RSttStreamerOV.exe", 21, 589_713_408, 0, NoGpu, NoMemory)
                {
                    GpuUnmeasured = new Dictionary<string, IReadOnlyList<string>> { [key] = ["Neural"] },
                },
                new ProcessRow(4, "System", 1, 1, 0, NoGpu, NoMemory),
                new ProcessRow(1000, "dwm.exe", 2, 1, 0, new Dictionary<string, double> { [key] = 3 }, NoMemory)),
        };

        var listed = JsonSerializer.SerializeToElement(await new ProcessTools(ctx).ListProcesses(sortBy: "gpu"));
        var names = listed.GetProperty("processes").EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToArray();

        // 측정 불가는 0 이 아니다 — 쓰지 않는 프로세스처럼 빠지거나 맨 뒤로 밀리면 GPU 를 다 쓰는 범인이 목록에서 사라진다.
        Assert.Equal(["RSttStreamerOV.exe", "dwm.exe"], names);
        Assert.Equal(1, listed.GetProperty("excludedIdle").GetInt32());
        // gpuPercent 만 보고 "안 쓴다"로 읽지 않게 0 이 아니라 null 이다.
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("processes")[0].GetProperty("gpuPercent").ValueKind);
        Assert.Equal(3, listed.GetProperty("processes")[1].GetProperty("gpuPercent").GetDouble());
        Assert.Equal("Neural", listed.GetProperty("processes")[0].GetProperty("gpuUnmeasured").GetProperty(key)[0].GetString());
    }

    [Fact]
    public void A_broken_engine_outside_the_families_keeps_the_primary_signals()
    {
        var arc = IntegratedArc();
        var signals = AiSignals.For(arc, ["GSC"]);

        Assert.Equal(AiSignals.For(arc).Primary, signals.Primary);
        Assert.Contains("GSC", signals.Note);
    }
}

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChronoLoad.App.Services;
using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Metrics;
using ChronoLoad.Core.Sampling;
using ChronoLoad.Core.Sensors;
using ChronoLoad.Sensors;

namespace ChronoLoad.App;

public partial class App : Application
{
    private SampleEngine? _engine;
    private DeviceWatcher? _watcher;
    private ProcessProvider? _processes;
    private Mcp.McpHost? _mcp;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        int renderTestIndex = Array.FindIndex(e.Args, a => a == "--render-test");
        if (renderTestIndex >= 0 && renderTestIndex + 1 < e.Args.Length)
        {
            int scrubIndex = Array.FindIndex(e.Args, a => a == "--scrub");
            int widthIndex = Array.FindIndex(e.Args, a => a == "--width");
            int heightIndex = Array.FindIndex(e.Args, a => a == "--height");
            double? windowHeight = heightIndex >= 0 && heightIndex + 1 < e.Args.Length
                && double.TryParse(e.Args[heightIndex + 1], out double dip) ? dip : null;
            TimeSpan? width = widthIndex >= 0 && widthIndex + 1 < e.Args.Length
                && double.TryParse(e.Args[widthIndex + 1], out double seconds)
                ? TimeSpan.FromSeconds(seconds) : null;
            int? scrub = scrubIndex >= 0 && scrubIndex + 1 < e.Args.Length
                         && int.TryParse(e.Args[scrubIndex + 1], out int v) ? v : null;
            RunRenderTest(e.Args[renderTestIndex + 1], e.Args.Contains("--light"),
                e.Args.Contains("--collapsed"), scrub, e.Args.Contains("--hotplug"),
                e.Args.Contains("--about"), e.Args.Contains("--scrub-drift"),
                e.Args.Contains("--gap"), width, e.Args.Contains("--settings"),
                e.Args.Contains("--snapshot"), windowHeight);
            return;
        }

        var registry = new MetricRegistry();
        var options = new SamplingOptions();
        _engine = new SampleEngine(registry, options);

        // PDH·GlobalMemoryStatusEx 초기화는 밀리초 단위라 창을 띄우기 전에 마쳐도 된다.
        var gpu = new GpuProvider();
        foreach (ISensorProvider provider in
                 new ISensorProvider[] { new CpuProvider(), new CoreProvider(), new MemoryProvider(), new NetworkProvider(),
                                         new DiskProvider(), gpu })
            _engine.AddProviderAsync(provider).AsTask().GetAwaiter().GetResult();

        _engine.Start();

        // §5.7 핫플러그. 감지는 전용 스레드에서 돌고 재열거는 샘플링 스레드가 한다.
        _watcher = new DeviceWatcher();
        _watcher.Changed += _ => _engine.RequestRescan();
        _watcher.Start();

        StartMcp(registry, _engine, gpu, options);

        var main = new MainWindow(registry, _engine);
        main.Show();

        // 정보 창을 실제 경로 그대로 띄워 확인하기 위한 진입점(수동 점검용).
        if (e.Args.Contains("--show-about"))
            main.Dispatcher.InvokeAsync(() =>
            {
                var about = new Controls.AboutWindow(main);
                about.Show();
                about.Activate();
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// 인프로세스 MCP 서버 (§10.1). <b>앱이 실행 중일 때만</b> 존재하므로 여기서 함께 켜고 끈다.
    /// </summary>
    /// <remarks>
    /// 서버가 서지 못해도(포트 충돌 등) 앱은 그대로 돌아간다 — 모니터의 본래 일은 화면이지
    /// MCP 가 아니다. 실패는 센서 로그에 남는다.
    /// </remarks>
    private void StartMcp(MetricRegistry registry, SampleEngine engine, GpuProvider gpu,
                          SamplingOptions options)
    {
        _processes = new ProcessProvider();

        var context = new Mcp.McpContext(registry, engine)
        {
            TelemetryLayers = () => gpu.TelemetryLayers,
            Processes = new Mcp.SensorProcessSource(_processes),
            SamplePeriod = options.FastPeriod,
        };

        // 기동도 같은 모양이다. 지금은 통과하지만 같은 함정을 두 곳에 남길 이유가 없다.
        _mcp = Task.Run(() => Mcp.McpHost.StartAsync(context)).GetAwaiter().GetResult();
        if (_mcp is not null) SensorLog.Write($"MCP 서버 http://127.0.0.1:{_mcp.Port}/mcp");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 토큰 파일부터 지운다. 남아 있으면 브리지가 죽은 앱에 계속 붙으려 한다.
        Finish(() => _mcp?.DisposeAsync().AsTask());
        _processes?.Dispose();
        _watcher?.Dispose();
        Finish(() => _engine?.DisposeAsync().AsTask());
        base.OnExit(e);
    }

    /// <summary>
    /// 종료 정리를 <b>스레드 풀에서</b> 돌리고 기다린다. 제한 시간을 넘으면 포기한다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>OnExit</c> 는 Dispatcher 스레드에서 돈다. 여기서 async 정리를 그냥 블로킹으로
    /// 기다리면, <c>ConfigureAwait(false)</c> 가 없는 이어받기가 <b>그 Dispatcher 로 돌아오려 한다</b> —
    /// 스레드는 이미 막혀 있고 Dispatcher 는 내려가는 중이라 그 이어받기는 영영 실행되지 않는다.
    /// 창은 닫혔는데 프로세스만 남는다. 실제로 그랬다.
    /// </para>
    /// <para>
    /// <see cref="Task.Run(Func{Task})"/> 안에서는 동기화 컨텍스트가 없으므로 이어받기가 풀로 간다.
    /// 라이브러리 쪽에 <c>ConfigureAwait(false)</c> 를 다는 것과 <b>둘 다</b> 한다 —
    /// 하나는 위생이고 하나는 방벽이다. 새 await 하나가 다시 앱을 좀비로 만들면 안 된다.
    /// </para>
    /// </remarks>
    private static void Finish(Func<Task?> work, int timeoutMs = 3000)
    {
        try
        {
            if (!Task.Run(() => work() ?? Task.CompletedTask).Wait(timeoutMs))
                SensorLog.Write("종료 정리가 제한 시간을 넘겼다. 나머지는 프로세스 종료에 맡긴다.");
        }
        catch (Exception ex)
        {
            // 정리에 실패해도 끝은 나야 한다. 못 나가는 것보다 낫다.
            SensorLog.Write($"종료 정리 실패: {ex.Message}");
        }
    }

    /// <summary>
    /// 창을 화면 밖에 띄워 실제로 렌더한 뒤 PNG로 남긴다.
    /// 설계서 §15의 골든 이미지 비교가 쓸 기반이자, 지금은 렌더러를 눈으로 확인하는 수단이다.
    /// </summary>
    private void RunRenderTest(string outputPath, bool light, bool collapsed, int? scrubIndex = null,
                               bool hotPlug = false, bool about = false, bool scrubDrift = false,
                               bool gap = false, TimeSpan? width = null, bool settings = false,
                               bool snapshot = false, double? windowHeight = null)
    {
        // 렌더 테스트는 합성 장치를 쓴다. 실제 설정 폴더를 그대로 쓰면 사용자의 창 위치를
        // 읽어 와 그림이 달라지고, 끝낼 때 gpu:demo 같은 가짜 장치 키를 사용자 파일에 남긴다.
        // 실제로 남겼다. 임시 폴더로 돌려 읽기도 쓰기도 격리한다.
        Environment.SetEnvironmentVariable("LOCALAPPDATA",
            Path.Combine(Path.GetTempPath(), "chronoload-render-" + Environment.ProcessId));

        ThemeService.Instance.Mode = light ? AppTheme.Light : AppTheme.Dark;

        var registry = BuildSyntheticRegistry(gap);

        // 대기 표시(§6.3)를 렌더 테스트에서도 볼 수 있게 한 장치를 재워 둔다.
        // 실기기에서는 외장 GPU 가 유휴일 때 이 상태가 된다.
        if (registry.ActiveDevices.LastOrDefault(d => d.Info.Class == DeviceClass.Gpu) is { } sleeping)
            sleeping.Availability = DeviceAvailability.Standby;

        // 핫플러그를 태울 때는 엔진이 있어야 한다 — 장치 변경 통지가 엔진에서 나오기 때문에,
        // 엔진 없이 검사하면 실제와 다른 경로를 보게 된다. 루프는 돌리지 않고 틱만 손으로 민다.
        var engine = hotPlug || scrubDrift ? new SampleEngine(registry) : null;

        var window = new MainWindow(registry, engine, collapsed, scrubIndex, width)
        {
            Left = -10_000,
            Top = -10_000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();

        // README 스크린샷은 이 노트북 화면(작업 영역이 낮다)에 눌리지 않은 모습이어야 한다.
        // 창을 띄운 <b>뒤에</b> 높이를 준다 — 앞서 주면 첫 실행 높이 계산(작업 영역 85% 상한)이 덮는다.
        if (windowHeight is { } dip)
            window.Dispatcher.InvokeAsync(() => window.Height = dip,
                System.Windows.Threading.DispatcherPriority.Loaded);

        if (hotPlug) RunHotPlugScript(window, registry, engine!, outputPath);
        else if (scrubDrift) RunScrubDriftScript(window, registry, engine!, outputPath);
        else if (about) CaptureAfter(500, window, outputPath, () => CaptureAbout(window, outputPath));
        else if (settings) CaptureAfter(500, window, outputPath, () => CaptureSettings(window, outputPath));
        else if (snapshot) CaptureAfter(500, window, outputPath,
            () => CaptureSnapshot(window, registry, outputPath));
        else CaptureAfter(600, window, outputPath, Shutdown);
    }

    /// <summary>
    /// 고정한 스크럽선이 시간이 흐를 때 그래프와 같이 왼쪽으로 흐르는지 본다 (§8.7).
    /// </summary>
    /// <remarks>
    /// 선이 가리키는 <b>순간</b>이 유지되는지는 한 장면으로 확인할 수 없다 — 두 시점을 찍어
    /// 비교해야 한다. WPF 는 합성 마우스 메시지를 입력으로 받지 않으므로 사람 손 없이
    /// 검증하려면 이 경로가 필요하다.
    /// </remarks>
    private void RunScrubDriftScript(MainWindow window, MetricRegistry registry, SampleEngine engine,
                                     string outputPath)
    {
        string stem = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
            Path.GetFileNameWithoutExtension(outputPath));

        // 연속한 틱을 따로 찍는다. 흐르는 거리뿐 아니라 <b>틱 사이에 튀지 않는지</b>도 봐야 한다 —
        // 오버레이 위치를 패널 폭으로 정하던 동안, 값이 한 글자 달라질 때마다 요약 칩이
        // 반대편으로 뛰었다. 한 장만 찍으면 그 진동은 절대 안 보인다.
        void Step(int stage, int ticks, Action next)
        {
            for (int i = 0; i < ticks; i++) engine.TickOnce(0.25);
            CaptureAfter(700, window, $"{stem}-{stage}.png", next);
        }

        // 엔진 틱만 돌린다. 틱 하나가 샘플 하나를 커밋하므로 창이 한 칸씩 미끄러지고 Committed 도
        // 그만큼 발생한다. registry.PushFrame 을 같이 부르면 샘플은 둘씩 느는데 Committed 는
        // 하나라, 멀쩡한 코드가 어긋난 것처럼 보인다.
        CaptureAfter(600, window, $"{stem}-1-pinned.png",
            () => Step(2, 1,
            () => Step(3, 1,
            () => Step(4, 1,
            () => Step(5, 40, Shutdown)))));
    }

    /// <summary>
    /// 장치가 붙고 떨어지는 과정을 실제로 태우고 단계마다 찍는다.
    /// 카드 diff 는 애니메이션·레이아웃·창 높이가 한꺼번에 얽히는 곳이라 눈으로 볼 수 있어야 한다.
    /// </summary>
    private void RunHotPlugScript(MainWindow window, MetricRegistry registry, SampleEngine engine,
                                  string outputPath)
    {
        string stem = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
            Path.GetFileNameWithoutExtension(outputPath));

        const string key = "luid:hotplug";
        DeviceInfo egpu = new(key, DeviceClass.Gpu, "eGPU 3070", "NVIDIA GeForce RTX 3070 Ti · 외장",
            IconKind.GpuNvidia);

        CaptureAfter(500, window, $"{stem}-1-before.png", () =>
        {
            // 연결
            var handle = registry.Register(egpu,
                [MetricKind.GpuUtil, MetricKind.GpuCompute, MetricKind.GpuDedicated, MetricKind.GpuShared]);

            // 새 카드가 빈 차트로 나타나면 등장 애니메이션만 보이고 내용은 확인할 수 없다.
            // 기존 슬롯은 마지막 값을 그대로 이어 붙여, 다른 카드의 그래프에 구멍이 생기지 않게 한다.
            int util = handle.SlotOf(MetricKind.GpuUtil);
            var frame = new float[registry.SlotCount];
            for (int i = 0; i < 60; i++)
            {
                for (int slot = 0; slot < frame.Length; slot++)
                    frame[slot] = registry.Series(slot)?.Latest ?? float.NaN;

                frame[util] = 30 + 25 * MathF.Sin(i / 5f);
                registry.PushFrame(frame);
            }

            engine.TickOnce(0.25);

            CaptureAfter(700, window, $"{stem}-2-added.png", () =>
            {
                // 분리
                registry.Retire(key, DateTime.UtcNow.Ticks);
                engine.TickOnce(0.25);

                CaptureAfter(700, window, $"{stem}-3-removed.png", () =>
                {
                    // 유예 안에 재연결 — 통계가 이어져야 한다
                    registry.Register(egpu,
                        [MetricKind.GpuUtil, MetricKind.GpuCompute, MetricKind.GpuDedicated, MetricKind.GpuShared]);
                    engine.TickOnce(0.25);

                    CaptureAfter(700, window, $"{stem}-4-reconnected.png", () =>
                    {
                        window.Close();
                        Shutdown();
                    });
                });
            });
        });
    }

    /// <summary>정보 창을 띄워 따로 찍는다. 대화 상자는 별도 창이라 본 창 캡처에 잡히지 않는다.</summary>
    /// <summary>스냅샷 창(§9.6)을 띄워 따로 담는다.</summary>
    private void CaptureSnapshot(Window owner, Core.Metrics.MetricRegistry registry, string outputPath)
    {
        var snapshot = Core.Metrics.MetricSnapshot.Capture(registry);
        var window = new Controls.SnapshotWindow(owner, snapshot,
            Core.Layout.SampleGaps.ThresholdFor(TimeSpan.FromMilliseconds(250)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10_000,
            Top = -10_000,
            ShowActivated = false,
        };
        window.Show();

        string stem = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
            Path.GetFileNameWithoutExtension(outputPath));

        CaptureAfter(700, window, stem + "-snapshot.png", () =>
        {
            // 클릭으로 한 자리를 잡고 Shift+클릭으로 반대쪽을 찍는 경로. 끌기와 결과가 같아야 한다.
            window.SelectAt((int)(snapshot.Count * 0.20), extend: false);
            window.SelectAt((int)(snapshot.Count * 0.55), extend: true);
        });
        CaptureAfter(1100, window, stem + "-snapshot-shift.png", () =>
        {
            // 선택과 크롭은 드래그로만 닿는 경로다. 눈으로 확인할 수 있게 한 장 더 담는다.
            window.Preselect((int)(snapshot.Count * 0.35), (int)(snapshot.Count * 0.62));
            CaptureAfter(400, window, stem + "-snapshot-selected.png", () =>
            {
                window.CropNow();
                CaptureAfter(400, window, stem + "-snapshot-cropped.png", () =>
                {
                    // 점 하나만 집으면 구간이 아니라 순간이다 — 오버레이로 그때의 값을 읽는다.
                    int at = (int)(snapshot.Count * 0.45);
                    window.Preselect(at, at);
                    CaptureAfter(400, window, stem + "-snapshot-scrub.png", () =>
                    {
                        window.Close();
                        owner.Close();
                        Shutdown();
                    });
                });
            });
        });
    }

    /// <summary>설정 팝오버(§11)를 띄워 따로 담는다. 본 창 캡처에는 나오지 않는 별도 창이다.</summary>
    private void CaptureSettings(Window owner, string outputPath)
    {
        var popover = new Controls.SettingsWindow(owner, Core.Settings.AppSettings.Load())
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10_000,
            Top = -10_000,
            ShowActivated = false,
        };
        popover.Show();

        CaptureAfter(500, popover, Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
            Path.GetFileNameWithoutExtension(outputPath) + "-settings.png"), () =>
        {
            popover.Close();
            owner.Close();
            Shutdown();
        });
    }

    private void CaptureAbout(Window owner, string outputPath)
    {
        var about = new Controls.AboutWindow(owner)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10_000,
            Top = -10_000,
            ShowActivated = false,
        };
        about.Show();

        CaptureAfter(500, about, Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
            Path.GetFileNameWithoutExtension(outputPath) + "-about.png"), () =>
        {
            about.Close();
            owner.Close();
            Shutdown();
        });
    }

    private static void CaptureAfter(int delayMs, Window window, string path, Action next)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Capture(window, path);
            next();
        };
        timer.Start();
    }

    private static void Capture(Window window, string outputPath)
    {
        int width = (int)Math.Ceiling(window.ActualWidth);
        int height = (int)Math.Ceiling(window.ActualHeight);
        var bitmap = new RenderTargetBitmap(width * 2, height * 2, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
        Console.WriteLine($"{outputPath} ({width}x{height})");
    }

    /// <summary>
    /// 센서 없이 렌더러를 구동하기 위한 합성 데이터. 영역·미러·스파크라인 세 모드를 모두 태운다.
    /// </summary>
    /// <param name="gap">
    /// 중간에 2분짜리 절전을 끼워 넣는다. 공백은 폭이 없고 <b>이음매</b>로만 보이므로(§7.4)
    /// 값을 눈으로 확인하려면 일부러 만들어 봐야 한다.
    /// </param>
    private static MetricRegistry BuildSyntheticRegistry(bool gap = false)
    {
        const int Points = 240;
        const double TotalMemory = 32L * 1024 * 1024 * 1024;
        var registry = new MetricRegistry();
        var random = new Random(20260923);

        // 오버레이의 코어 미니 바(§5.1)를 센서 없이도 확인할 수 있게 채널을 붙인다.
        // 한두 코어만 물린 모양과 고르게 퍼진 모양이 구분되는지가 이 막대의 존재 이유다.
        // 성능 4 + 효율 8 하이브리드로 두어 등급 구분(틈과 캡션)도 함께 확인한다.
        const int Cores = 12;
        var cpu = registry.Register(
            new DeviceInfo("cpu", DeviceClass.System, "CPU",
                "Intel Core Ultra 7 270K · 논리 코어 12", IconKind.Cpu)
            {
                Extra = new Dictionary<string, string>
                {
                    ["coreEfficiencyClasses"] = "1,1,1,1,0,0,0,0,0,0,0,0",
                },
            },
            [MetricKind.CpuTotal]);

        var coreSlots = registry.RegisterChannels(cpu, MetricKind.CpuTotal, Cores);

        var memory = registry.Register(
            new DeviceInfo("memory", DeviceClass.System, "메모리 32G",
                "물리 메모리 32G · 커밋 한도 52G", IconKind.Memory)
            { Extra = new Dictionary<string, string> { ["totalBytes"] = TotalMemory.ToString("F0") } },
            [MetricKind.MemUsed, MetricKind.MemCommit]);

        var net = registry.Register(
            new DeviceInfo("net:demo", DeviceClass.Network, "Ethernet 2.5G",
                "Intel I225-V · 2.5 Gbps · 연결됨", IconKind.NetEthernet),
            [MetricKind.NetRx, MetricKind.NetTx]);

        const double Vram = 24L * 1024 * 1024 * 1024;
        var gpu = registry.Register(
            new DeviceInfo("gpu:demo", DeviceClass.Gpu, "RTX 4090",
                "NVIDIA GeForce RTX 4090 · 외장 · 24 GiB GDDR6X", IconKind.GpuNvidia, "NVIDIA")
            {
                Extra = new Dictionary<string, string>
                {
                    ["discrete"] = "true",
                    ["computeOnly"] = "false",
                    ["dedicatedBytes"] = Vram.ToString("F0"),
                },
            },
            [MetricKind.GpuUtil, MetricKind.GpuCompute, MetricKind.GpuDedicated, MetricKind.GpuShared]);

        var npu = registry.Register(
            new DeviceInfo("npu:demo", DeviceClass.Gpu, "AI Boost NPU",
                "Intel(R) AI Boost · 연산 전용", IconKind.Npu, "Intel")
            {
                Extra = new Dictionary<string, string>
                {
                    ["discrete"] = "false",
                    ["computeOnly"] = "true",
                },
            },
            [MetricKind.GpuUtil]);

        var frame = new float[registry.SlotCount];
        // 합성 데이터에도 시간 축을 준다. 기본값(지금 시각)으로 밀어 넣으면 프레임 간격이
        // 사실상 0 이라 공백 판정이 아무 의미도 갖지 못한다.
        long stamp = new DateTime(2026, 9, 26, 14, 32, 7, DateTimeKind.Utc).Ticks;
        long step = TimeSpan.FromMilliseconds(250).Ticks;
        double cpuValue = 34, memRatio = 0.52, rx = 0, tx = 0, gpuValue = 62, phase = 0;
        double dedicated = 9.0 * 1024 * 1024 * 1024, dedicatedTarget = dedicated, shared = 0, npuValue = 12;

        for (int i = 0; i < Points; i++)
        {
            phase += 0.02;
            cpuValue = Math.Clamp(cpuValue + random.NextDouble() * 14 - 7 + Math.Sin(phase * 1.7) * 3, 3, 99);
            memRatio = Math.Clamp(memRatio + random.NextDouble() * 0.01 - 0.004, 0.35, 0.93);
            rx = Math.Clamp(rx * 0.72 + (random.NextDouble() < 0.06 ? random.NextDouble() * 180e6 + 40e6 : random.NextDouble() * 8e6), 0, 300e6);
            tx = Math.Clamp(tx * 0.75 + (random.NextDouble() < 0.05 ? random.NextDouble() * 26e6 + 4e6 : random.NextDouble() * 2e6), 0, 60e6);
            gpuValue = Math.Clamp(gpuValue + random.NextDouble() * 18 - 9 + Math.Sin(phase * 0.9) * 6, 5, 100);

            frame[cpu.SlotOf(MetricKind.CpuTotal)] = (float)cpuValue;

            // 코어 0~1 은 늘 물려 있고 나머지는 총 사용률을 따라 흩어진다 — 단일 스레드 병목이
            // 막대 모양으로 드러나는지 보기 위한 배치다. 마지막 코어는 파킹(값 없음)으로 둔다.
            for (int c = 0; c < Cores; c++)
                frame[coreSlots[c]] = c == Cores - 1 ? float.NaN
                    : (float)Math.Clamp(c < 2 ? 88 + random.NextDouble() * 12
                                              : cpuValue + random.NextDouble() * 40 - 20, 0, 100);
            frame[memory.SlotOf(MetricKind.MemUsed)] = (float)(memRatio * TotalMemory);
            frame[memory.SlotOf(MetricKind.MemCommit)] = (float)(Math.Min(0.99, memRatio + 0.08) * TotalMemory);
            frame[net.SlotOf(MetricKind.NetRx)] = (float)rx;
            frame[net.SlotOf(MetricKind.NetTx)] = (float)tx;
            // 모델 로드/해제처럼 계단형으로 움직이고, 후반부에 VRAM 을 넘겨 스필오버를 만든다.
            if (i > Points * 0.72) dedicatedTarget = Vram * 0.99;
            else if (random.NextDouble() < 0.02) dedicatedTarget = (random.NextDouble() * 18 + 4) * 1024 * 1024 * 1024;
            dedicated += (dedicatedTarget - dedicated) * 0.10;
            shared += ((i > Points * 0.78 ? 2.6 * 1024 * 1024 * 1024 : 0) - shared) * 0.08;
            npuValue = Math.Clamp(npuValue + random.NextDouble() * 10 - 5 + Math.Sin(phase * 1.3) * 4, 0, 85);

            frame[gpu.SlotOf(MetricKind.GpuUtil)] = (float)gpuValue;
            frame[gpu.SlotOf(MetricKind.GpuCompute)] = (float)(gpuValue * 0.78);
            frame[gpu.SlotOf(MetricKind.GpuDedicated)] = (float)dedicated;
            frame[gpu.SlotOf(MetricKind.GpuShared)] = (float)shared;
            frame[npu.SlotOf(MetricKind.GpuUtil)] = (float)npuValue;

            // 절전은 샘플을 남기지 않는다 — 시각만 건너뛰고 값은 그대로 이어진다.
            if (gap && (i == (int)(Points * 0.45) || i == (int)(Points * 0.8)))
                stamp += TimeSpan.FromMinutes(2).Ticks;

            registry.PushFrame(frame, stamp);
            stamp += step;
        }

        return registry;
    }
}

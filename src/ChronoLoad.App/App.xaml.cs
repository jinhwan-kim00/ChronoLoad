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
            int? scrub = scrubIndex >= 0 && scrubIndex + 1 < e.Args.Length
                         && int.TryParse(e.Args[scrubIndex + 1], out int v) ? v : null;
            RunRenderTest(e.Args[renderTestIndex + 1], e.Args.Contains("--light"),
                e.Args.Contains("--collapsed"), scrub, e.Args.Contains("--hotplug"),
                e.Args.Contains("--about"));
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

        _mcp = Mcp.McpHost.StartAsync(context).GetAwaiter().GetResult();
        if (_mcp is not null) SensorLog.Write($"MCP 서버 http://127.0.0.1:{_mcp.Port}/mcp");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 토큰 파일부터 지운다. 남아 있으면 브리지가 죽은 앱에 계속 붙으려 한다.
        _mcp?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _processes?.Dispose();
        _watcher?.Dispose();
        _engine?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }

    /// <summary>
    /// 창을 화면 밖에 띄워 실제로 렌더한 뒤 PNG로 남긴다.
    /// 설계서 §15의 골든 이미지 비교가 쓸 기반이자, 지금은 렌더러를 눈으로 확인하는 수단이다.
    /// </summary>
    private void RunRenderTest(string outputPath, bool light, bool collapsed, int? scrubIndex = null,
                               bool hotPlug = false, bool about = false)
    {
        ThemeService.Instance.Mode = light ? AppTheme.Light : AppTheme.Dark;

        var registry = BuildSyntheticRegistry();

        // 대기 표시(§6.3)를 렌더 테스트에서도 볼 수 있게 한 장치를 재워 둔다.
        // 실기기에서는 외장 GPU 가 유휴일 때 이 상태가 된다.
        if (registry.ActiveDevices.LastOrDefault(d => d.Info.Class == DeviceClass.Gpu) is { } sleeping)
            sleeping.Availability = DeviceAvailability.Standby;

        // 핫플러그를 태울 때는 엔진이 있어야 한다 — 장치 변경 통지가 엔진에서 나오기 때문에,
        // 엔진 없이 검사하면 실제와 다른 경로를 보게 된다. 루프는 돌리지 않고 틱만 손으로 민다.
        var engine = hotPlug ? new SampleEngine(registry) : null;

        var window = new MainWindow(registry, engine, collapsed, scrubIndex)
        {
            Left = -10_000,
            Top = -10_000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();

        if (hotPlug) RunHotPlugScript(window, registry, engine!, outputPath);
        else if (about) CaptureAfter(500, window, outputPath, () => CaptureAbout(window, outputPath));
        else CaptureAfter(600, window, outputPath, Shutdown);
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
    private static MetricRegistry BuildSyntheticRegistry()
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

            registry.PushFrame(frame);
        }

        return registry;
    }
}

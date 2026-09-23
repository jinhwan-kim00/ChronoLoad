using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ChronoLoad.Core.Sampling;

namespace ChronoLoad.App.Services;

internal static partial class VisibilityNative
{
    /// <summary>DWMWA_CLOAKED. 창이 "최소화는 아니지만 화면에 없는" 상태인지.</summary>
    private const int DwmwaCloaked = 14;

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;   // 1 = 절전 모드(에너지 세이버)
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetSystemPowerStatus")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>
    /// 다른 가상 데스크톱으로 넘어갔거나 셸이 숨긴 창인지.
    /// 최소화와 달리 <see cref="Window.WindowState"/> 로는 알 수 없다.
    /// </summary>
    public static bool IsCloaked(nint hwnd)
    {
        if (hwnd == 0) return false;
        return DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    /// <summary>배터리로 돌면서 절전 모드가 켜져 있는가.</summary>
    public static bool IsOnBatterySaver()
    {
        if (!GetSystemPowerStatus(out var status)) return false;

        // AcLineStatus 0 = 배터리, 1 = 전원 연결, 255 = 알 수 없음.
        return status.AcLineStatus == 0 && status.SystemStatusFlag == 1;
    }
}

/// <summary>
/// 창이 보이는지와 전원 상태를 보고 샘플 주기를 정한다 (§6.3 적응형 백오프).
/// </summary>
/// <remarks>
/// <para>
/// <b>렌더와 샘플링을 따로 판단한다.</b> 보이지 않을 때 그리는 것은 순수한 낭비라 바로 끊지만,
/// 샘플링은 느리게 갈 뿐 멈추지 않는다 — 시계열에 생긴 구멍은 창을 복원해도 메울 수 없고,
/// "그때 무슨 일이 있었나"를 보려고 만든 앱에서 그 구멍이 가장 아쉬운 자리에 생긴다.
/// </para>
/// <para>
/// <b>다른 창에 완전히 가려진 경우는 다루지 않는다.</b> 설계서 §6.3 에 적어뒀지만,
/// Windows 는 이를 알려주는 값을 주지 않는다. 창 목록을 훑어 사각형을 겹쳐보는 근사는
/// 그 계산 자체가 아끼려는 비용만큼 들고 결과도 틀리기 쉬워서, 하지 않는 편을 택했다.
/// </para>
/// </remarks>
public sealed class VisibilityWatch : IDisposable
{
    /// <summary>전원 상태 확인 주기. 배터리·절전 전환은 초 단위로 알아도 충분하다.</summary>
    private static readonly TimeSpan PowerPollInterval = TimeSpan.FromSeconds(10);

    private readonly Window _window;
    private readonly SampleEngine _engine;
    private readonly System.Windows.Threading.DispatcherTimer _powerPoll;

    private nint _hwnd;

    public VisibilityWatch(Window window, SampleEngine engine)
    {
        _window = window;
        _engine = engine;

        _powerPoll = new System.Windows.Threading.DispatcherTimer { Interval = PowerPollInterval };
        _powerPoll.Tick += (_, _) => Reevaluate();

        window.StateChanged += (_, _) => Reevaluate();
        window.IsVisibleChanged += (_, _) => Reevaluate();
        window.Activated += (_, _) => Reevaluate();
        window.Deactivated += (_, _) => Reevaluate();
    }

    /// <summary>화면을 그릴 필요가 있는가. 창이 이 값을 보고 렌더를 건너뛴다.</summary>
    public bool ShouldRender { get; private set; } = true;

    public event Action? Changed;

    public void Start()
    {
        _hwnd = new WindowInteropHelper(_window).Handle;
        _powerPoll.Start();
        Reevaluate();
    }

    private void Reevaluate()
    {
        bool hidden = _window.WindowState == WindowState.Minimized
                      || !_window.IsVisible
                      || VisibilityNative.IsCloaked(_hwnd);

        var pace = hidden
            ? SamplePace.Background
            : VisibilityNative.IsOnBatterySaver() ? SamplePace.Reduced : SamplePace.Full;

        bool render = !hidden;

        if (_engine.RequestedPace == pace && ShouldRender == render) return;

        _engine.RequestedPace = pace;
        ShouldRender = render;
        Changed?.Invoke();
    }

    public void Dispose() => _powerPoll.Stop();
}

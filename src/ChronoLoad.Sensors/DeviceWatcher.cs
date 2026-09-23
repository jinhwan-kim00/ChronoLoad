using System.Runtime.InteropServices;
using ChronoLoad.Sensors.Native;

namespace ChronoLoad.Sensors;

/// <summary>장치 집합이 바뀌었을 법한 사건. 무엇이 바뀌었는지가 아니라 "다시 세어봐야 한다"는 신호다.</summary>
public enum DeviceChangeReason
{
    /// <summary>디스플레이 어댑터 도착·제거. eGPU 연결이 여기로 온다.</summary>
    DisplayAdapter,

    /// <summary>물리 디스크 도착·제거.</summary>
    Disk,

    /// <summary>IP 인터페이스 변경. Wi-Fi 라디오 on/off 가 여기로 온다.</summary>
    NetworkInterface,

    /// <summary>절전 복귀·디스플레이 구성 변경. 델타 기준선을 다시 잡아야 한다.</summary>
    PowerOrDisplay,

    /// <summary>주기 검증. 이벤트를 놓쳤어도 여기서 복구된다.</summary>
    Verification,
}

/// <summary>
/// 하드웨어 착탈을 감지해 재열거를 요청한다 (§5.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>디바운스가 핵심이다.</b> eGPU 를 한 번 꽂으면 <c>WM_DEVICECHANGE</c> 가 수십 번 온다.
/// 그때마다 재열거하면 PDH 와일드카드를 초당 수십 번 긁게 된다. 300ms 동안 잠잠해진 뒤에 한 번만 알린다.
/// </para>
/// <para>
/// <b>이벤트를 놓쳐도 복구된다.</b> 30초마다 무조건 한 번 재열거를 요청하는 안전망이 있다.
/// 이벤트 경로가 어떤 이유로 실패해도 최악의 경우 30초 뒤에는 반영된다는 뜻이다 —
/// 장치 감지는 조용히 실패하기 쉬운 영역이라 이 보험이 없으면 "왜 안 잡히지"를 영영 못 찾는다.
/// </para>
/// <para>
/// 메시지 전용 창과 메시지 펌프를 <b>전용 스레드</b>에서 돌린다. UI 스레드에 얹으면
/// 콘솔 하네스에서 검증할 수 없고, 장치 이벤트가 렌더링과 경쟁한다.
/// </para>
/// </remarks>
public sealed class DeviceWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan VerificationPeriod = TimeSpan.FromSeconds(30);

    private const int WM_APP_QUIT = 0x8000 + 1;

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cts = new();

    // 델리게이트를 필드로 붙잡아 둔다. 네이티브가 콜백을 들고 있는 동안 GC 가 수집하면 프로세스가 죽는다.
    private readonly DeviceNotify.WndProc _wndProc;
    private readonly DeviceNotify.IpInterfaceChangeCallback _ipCallback;

    private readonly ManualResetEventSlim _ready = new(false);
    private Thread? _pump;
    private Timer? _debounceTimer;
    private Timer? _verificationTimer;
    private nint _hwnd;
    private nint _displayNotification;
    private nint _diskNotification;
    private nint _ipNotification;
    private DeviceChangeReason _pendingReason;
    private bool _pending;

    public DeviceWatcher()
    {
        _wndProc = HandleMessage;
        _ipCallback = HandleIpChange;
    }

    /// <summary>재열거가 필요하다. 디바운스를 거친 뒤 <b>스레드 풀</b>에서 호출된다.</summary>
    public event Action<DeviceChangeReason>? Changed;

    public bool IsWatching => _hwnd != 0;

    public void Start()
    {
        if (_pump is not null) throw new InvalidOperationException("이미 시작됐다.");

        _verificationTimer = new Timer(_ => Signal(DeviceChangeReason.Verification),
            null, VerificationPeriod, VerificationPeriod);

        StartIpNotification();

        _pump = new Thread(PumpMessages)
        {
            Name = "ChronoLoad.DeviceWatcher",
            IsBackground = true,
        };
        _pump.SetApartmentState(ApartmentState.STA);
        _pump.Start();

        // 창이 생기기 전에 Dispose 가 들어오면 종료 메시지를 보낼 곳이 없어 스레드가 남는다.
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    private void StartIpNotification()
    {
        try
        {
            nint handle = 0;
            uint rc = DeviceNotify.NotifyIpInterfaceChange(
                0 /* AF_UNSPEC */, Marshal.GetFunctionPointerForDelegate(_ipCallback), 0,
                initialNotification: false, ref handle);

            if (rc == 0) _ipNotification = handle;
            else SensorLog.Write($"NotifyIpInterfaceChange 실패 {rc}");
        }
        catch (DllNotFoundException)
        {
            // 네트워크 알림 없이도 30초 안전망으로 복구된다.
        }
    }

    /// <summary>
    /// 창의 생성·메시지 루프·소멸을 <b>모두 이 스레드에서</b> 한다.
    /// </summary>
    /// <remarks>
    /// 창은 만든 스레드의 소유물이다. 다른 스레드에서 <c>DestroyWindow</c> 를 부르면 실패하고,
    /// 스레드가 먼저 끝나면 창이 암묵적으로 파괴되어 핸들이 대롱거린다.
    /// 그 상태로 프로세스가 내려가면서 네이티브가 관리 델리게이트를 다시 부르면 죽는다 —
    /// 실제로 종료 시점에 간헐적으로 그렇게 죽였다.
    /// </remarks>
    private void PumpMessages()
    {
        if (!CreateMessageWindow())
        {
            _ready.Set();
            return;
        }

        RegisterInterface(DeviceNotify.DisplayDeviceArrival, ref _displayNotification, "디스플레이 어댑터");
        RegisterInterface(DeviceNotify.DiskInterface, ref _diskNotification, "디스크");

        SensorLog.Write("DeviceWatcher 시작 — 어댑터·디스크·네트워크 감시 중");
        _ready.Set();

        while (DeviceNotify.GetMessage(out var message, 0, 0, 0) > 0)
        {
            DeviceNotify.TranslateMessage(ref message);
            DeviceNotify.DispatchMessage(ref message);
        }

        if (_displayNotification != 0) { DeviceNotify.UnregisterDeviceNotification(_displayNotification); _displayNotification = 0; }
        if (_diskNotification != 0) { DeviceNotify.UnregisterDeviceNotification(_diskNotification); _diskNotification = 0; }

        if (_hwnd != 0)
        {
            DeviceNotify.DestroyWindow(_hwnd);
            _hwnd = 0;
        }
    }

    private bool CreateMessageWindow()
    {
        nint className = Marshal.StringToHGlobalUni("ChronoLoad.DeviceWatcher");
        try
        {
            var wndClass = new DeviceNotify.WndClassEx
            {
                Size = Marshal.SizeOf<DeviceNotify.WndClassEx>(),
                WndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                ClassName = className,
            };

            if (DeviceNotify.RegisterClassEx(ref wndClass) == 0)
            {
                SensorLog.Write($"창 클래스 등록 실패 {Marshal.GetLastWin32Error()}");
                return false;
            }

            _hwnd = DeviceNotify.CreateWindowEx(0, className, 0, 0, 0, 0, 0, 0,
                DeviceNotify.HWND_MESSAGE, 0, 0, 0);

            if (_hwnd == 0)
            {
                SensorLog.Write($"메시지 창 생성 실패 {Marshal.GetLastWin32Error()}");
                return false;
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(className);
        }
    }

    private void RegisterInterface(Guid interfaceClass, ref nint notification, string label)
    {
        var filter = new DeviceNotify.DevBroadcastDeviceInterface
        {
            Size = Marshal.SizeOf<DeviceNotify.DevBroadcastDeviceInterface>(),
            DeviceType = DeviceNotify.DBT_DEVTYP_DEVICEINTERFACE,
            ClassGuid = interfaceClass,
        };

        nint buffer = Marshal.AllocHGlobal(filter.Size);
        try
        {
            Marshal.StructureToPtr(filter, buffer, false);
            notification = DeviceNotify.RegisterDeviceNotification(
                _hwnd, buffer, DeviceNotify.DEVICE_NOTIFY_WINDOW_HANDLE);

            if (notification == 0)
                SensorLog.Write($"{label} 알림 등록 실패 {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private nint HandleMessage(nint hwnd, int message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case DeviceNotify.WM_DEVICECHANGE:
                int eventType = (int)wParam;
                if (eventType is DeviceNotify.DBT_DEVICEARRIVAL
                              or DeviceNotify.DBT_DEVICEREMOVECOMPLETE
                              or DeviceNotify.DBT_DEVNODES_CHANGED)
                    Signal(ClassifyDeviceChange(lParam));
                break;

            case DeviceNotify.WM_POWERBROADCAST:
                if ((int)wParam is DeviceNotify.PBT_APMRESUMESUSPEND
                                or DeviceNotify.PBT_APMRESUMEAUTOMATIC)
                    Signal(DeviceChangeReason.PowerOrDisplay);
                break;

            case DeviceNotify.WM_DISPLAYCHANGE:
                Signal(DeviceChangeReason.PowerOrDisplay);
                break;

            case WM_APP_QUIT:
            case DeviceNotify.WM_DESTROY:
                DeviceNotify.PostQuitMessage(0);
                return 0;
        }

        return DeviceNotify.DefWindowProc(hwnd, message, wParam, lParam);
    }

    /// <summary>어느 인터페이스 클래스에서 온 알림인지 본다. 분류가 안 되면 어댑터로 취급한다.</summary>
    private static DeviceChangeReason ClassifyDeviceChange(nint lParam)
    {
        if (lParam == 0) return DeviceChangeReason.DisplayAdapter;

        try
        {
            var header = Marshal.PtrToStructure<DeviceNotify.DevBroadcastDeviceInterface>(lParam);
            if (header.DeviceType != DeviceNotify.DBT_DEVTYP_DEVICEINTERFACE)
                return DeviceChangeReason.DisplayAdapter;

            if (header.ClassGuid == DeviceNotify.DiskInterface) return DeviceChangeReason.Disk;
            return DeviceChangeReason.DisplayAdapter;
        }
        catch (ArgumentException)
        {
            return DeviceChangeReason.DisplayAdapter;
        }
    }

    private void HandleIpChange(nint context, nint row, int notificationType) =>
        Signal(DeviceChangeReason.NetworkInterface);

    /// <summary>
    /// 디바운스 창을 연다. 이미 열려 있으면 만료를 뒤로 민다 —
    /// 이벤트가 쏟아지는 동안에는 기다렸다가 잠잠해진 뒤 한 번만 알린다.
    /// </summary>
    private void Signal(DeviceChangeReason reason)
    {
        if (_cts.IsCancellationRequested) return;

        lock (_gate)
        {
            // 주기 검증보다 실제 이벤트가 더 구체적인 이유다. 덮어쓰지 않는다.
            if (!_pending || reason != DeviceChangeReason.Verification) _pendingReason = reason;
            _pending = true;

            _debounceTimer ??= new Timer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _debounceTimer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        DeviceChangeReason reason;
        lock (_gate)
        {
            if (!_pending) return;
            _pending = false;
            reason = _pendingReason;
        }

        if (_cts.IsCancellationRequested) return;

        try
        {
            Changed?.Invoke(reason);
        }
        catch (Exception ex)
        {
            // 구독자가 던져도 감시는 계속돼야 한다.
            SensorLog.Write($"DeviceWatcher 구독자 예외: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();

        _verificationTimer?.Dispose();
        _debounceTimer?.Dispose();

        // 네이티브가 콜백을 더 부르지 못하게 먼저 끊는다.
        if (_ipNotification != 0)
        {
            DeviceNotify.CancelMibChangeNotify2(_ipNotification);
            _ipNotification = 0;
        }

        if (_hwnd != 0) DeviceNotify.PostMessage(_hwnd, WM_APP_QUIT, 0, 0);

        // 펌프 스레드가 창을 정리하고 빠져나갈 때까지 기다린다. 기다리지 않고 나가면
        // 네이티브가 이미 수집된 델리게이트를 부를 수 있다.
        if (_pump is not null && !_pump.Join(TimeSpan.FromSeconds(3)))
            SensorLog.Write("DeviceWatcher 펌프 스레드가 제때 끝나지 않았다");

        // 네이티브 쪽 호출이 완전히 끝나기 전에 델리게이트가 수집되면 안 된다.
        GC.KeepAlive(_wndProc);
        GC.KeepAlive(_ipCallback);

        _ready.Dispose();
        _cts.Dispose();
    }
}

using System.Runtime.InteropServices;

namespace ChronoLoad.Sensors.Native;

/// <summary>
/// 장치 착탈 알림을 받기 위한 Win32 진입점.
/// </summary>
/// <remarks>
/// <b>메시지 전용 창(<c>HWND_MESSAGE</c>)을 쓴다.</b> WPF 창에 훅을 거는 방법도 있지만 그러면
/// 콘솔 하네스에서 장치 감지를 검증할 수 없고, UI 스레드가 장치 이벤트에 묶인다.
/// 다만 메시지 전용 창은 <b>브로드캐스트를 받지 못하므로</b>
/// <c>DBT_DEVNODES_CHANGED</c>에 기댈 수 없다 — 인터페이스 클래스를 명시해
/// <c>RegisterDeviceNotification</c>으로 등록해야 도착·제거 알림이 온다.
/// </remarks>
internal static partial class DeviceNotify
{
    public const int WM_DESTROY = 0x0002;
    public const int WM_DEVICECHANGE = 0x0219;
    public const int WM_POWERBROADCAST = 0x0218;
    public const int WM_DISPLAYCHANGE = 0x007E;

    public const int DBT_DEVICEARRIVAL = 0x8000;
    public const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    public const int DBT_DEVNODES_CHANGED = 0x0007;

    public const int PBT_APMRESUMESUSPEND = 0x0007;
    public const int PBT_APMRESUMEAUTOMATIC = 0x0012;

    public const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x0000;
    public const int DBT_DEVTYP_DEVICEINTERFACE = 0x0005;

    public static readonly nint HWND_MESSAGE = -3;

    /// <summary>디스플레이 어댑터 도착·제거. eGPU 연결이 이 경로로 온다.</summary>
    public static readonly Guid DisplayDeviceArrival = new("1ca05180-a699-450a-9a0c-de4fbe3ddd89");

    /// <summary>물리 디스크.</summary>
    public static readonly Guid DiskInterface = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");

    [StructLayout(LayoutKind.Sequential)]
    public struct DevBroadcastDeviceInterface
    {
        public int Size;
        public int DeviceType;
        public int Reserved;
        public Guid ClassGuid;
        public short Name;          // 가변 길이의 시작점. 등록할 때는 쓰지 않는다.
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WndClassEx
    {
        public int Size;
        public int Style;
        public nint WndProc;
        public int ClsExtra;
        public int WndExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public nint MenuName;
        public nint ClassName;
        public nint IconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public nint Hwnd;
        public int Message;
        public nint WParam;
        public nint LParam;
        public int Time;
        public int X;
        public int Y;
    }

    public delegate nint WndProc(nint hwnd, int message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static partial ushort RegisterClassEx(ref WndClassEx wndClass);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true)]
    public static partial nint CreateWindowEx(
        int exStyle, nint className, nint windowName, int style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(nint hwnd, int message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static partial int GetMessage(out Msg message, nint hwnd, int filterMin, int filterMax);

    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(ref Msg message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(ref Msg message);

    [LibraryImport("user32.dll", EntryPoint = "PostQuitMessage")]
    public static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint hwnd, int message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "RegisterDeviceNotificationW", SetLastError = true)]
    public static partial nint RegisterDeviceNotification(nint recipient, nint filter, int flags);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterDeviceNotification", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterDeviceNotification(nint handle);

    /// <summary>
    /// IP 인터페이스 변경 알림. 창이 없어도 동작하는 콜백 방식이라 Wi-Fi 라디오 on/off 가 여기로 온다.
    /// </summary>
    [LibraryImport("iphlpapi.dll", EntryPoint = "NotifyIpInterfaceChange")]
    public static partial uint NotifyIpInterfaceChange(
        ushort family, nint callback, nint context,
        [MarshalAs(UnmanagedType.U1)] bool initialNotification, ref nint notificationHandle);

    [LibraryImport("iphlpapi.dll", EntryPoint = "CancelMibChangeNotify2")]
    public static partial uint CancelMibChangeNotify2(nint notificationHandle);

    public delegate void IpInterfaceChangeCallback(nint context, nint row, int notificationType);
}

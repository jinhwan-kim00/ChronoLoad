using System.Runtime.InteropServices;
using ChronoLoad.Core.Settings;

namespace ChronoLoad.App.Services;

/// <summary>
/// 실제 모니터 배치를 묻는다.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>SystemParameters.VirtualScreen*</c> 으로는 부족하다.</b> 그것은 모든 모니터를 감싸는
/// 경계 상자라서, 배치가 어긋나 있으면(예: 왼쪽 아래 1080p + 오른쪽 위 4K) 상자 안에
/// 어떤 모니터도 없는 빈 구역이 생긴다. 그 구역에 저장된 창은 "화면 안"으로 판정되고도
/// 복원하면 보이지 않는다.
/// </para>
/// <para>
/// 여기서 다루는 좌표는 모두 <b>물리 픽셀</b>이다. WPF 의 <c>Left/Top</c>(DIP)과 섞지 않으려고
/// 창의 사각형도 <c>GetWindowRect</c> 로 받아 같은 단위에서 비교한다 — 모니터마다 배율이 다르면
/// DIP↔픽셀 환산이 어긋나고, 그 오차가 바로 "창이 조금 잘려 보임"으로 나타난다.
/// </para>
/// </remarks>
internal static partial class Monitors
{
    private const int MonitorDefaultToNull = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public int Flags;
    }

    private delegate bool MonitorEnumProc(nint monitor, nint dc, ref Rect rect, nint data);

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplayMonitors")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnumProc callback, nint data);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rect rect);

    [LibraryImport("user32.dll", EntryPoint = "MonitorFromWindow")]
    private static partial nint MonitorFromWindow(nint window, int flags);

    /// <summary>모니터별 작업 영역(작업표시줄 제외), 물리 픽셀.</summary>
    public static IReadOnlyList<WindowPlacement> WorkAreas()
    {
        var areas = new List<WindowPlacement>();

        // 델리게이트를 지역 변수로 붙잡아 둔다. 열거 중에 수집되면 네이티브가 죽은 콜백을 부른다.
        MonitorEnumProc callback = (nint monitor, nint dc, ref Rect area, nint data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
                areas.Add(new WindowPlacement(
                    info.Work.Left, info.Work.Top,
                    info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top));

            return true;
        };

        EnumDisplayMonitors(0, 0, callback, 0);
        GC.KeepAlive(callback);
        return areas;
    }

    /// <summary>창이 지금 놓인 자리, 물리 픽셀. 핸들이 없으면 null.</summary>
    public static WindowPlacement? RectOf(nint window)
    {
        if (window == 0 || !GetWindowRect(window, out var r)) return null;
        return new WindowPlacement(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>어떤 모니터에도 걸치지 않는가. 겹침이 0 일 때만 참이다.</summary>
    public static bool IsOffAllMonitors(nint window) =>
        window != 0 && MonitorFromWindow(window, MonitorDefaultToNull) == 0;
}

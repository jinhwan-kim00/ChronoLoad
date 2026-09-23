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

    [LibraryImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint after, int x, int y, int cx, int cy, uint flags);

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

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

    /// <summary>
    /// 창을 물리 픽셀 좌표에 그대로 놓는다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>WPF 의 <c>Left</c>/<c>Top</c> 으로 복원하면 배율이 다른 모니터에서 어긋난다.</b>
    /// 그 값은 DIP 이고, DIP↔픽셀 환산에 쓰이는 배율은 <i>창이 지금 놓여 있는</i> 모니터의 것이다.
    /// 200% 모니터에서 뜬 창에 175% 모니터에서 저장한 DIP 좌표를 넣으면 창이 실제 자리보다
    /// 멀리 간다(실측: 420px). 그 자리가 여전히 어느 모니터 안이면 화면 밖 검사도 통과해 버려
    /// 아무도 눈치채지 못한다. 여기서는 환산을 아예 하지 않는다.
    /// </para>
    /// <para>
    /// <b>두 번 놓는다.</b> 배율이 다른 모니터로 넘어가는 첫 호출은 <c>WM_DPICHANGED</c> 를 일으키고,
    /// WPF 는 그 처리에서 창을 새 배율에 맞춰 다시 잡는다 — 방금 준 픽셀 크기가 배율비만큼
    /// 줄어든다(실측: 200%→175% 로 옮길 때 595×1267 이 525×1109 가 됐다). 두 번째 호출은
    /// 이미 같은 배율 안이라 아무것도 일으키지 않고 크기만 제자리로 돌려놓는다.
    /// </para>
    /// </remarks>
    public static bool MoveTo(nint window, WindowPlacement rect)
    {
        if (window == 0 || !rect.IsValid) return false;
        if (!Place(window, rect)) return false;

        if (RectOf(window) is { } placed && Differs(placed, rect)) Place(window, rect);
        return true;

        static bool Place(nint window, WindowPlacement rect) => SetWindowPos(window, 0,
            (int)Math.Round(rect.Left), (int)Math.Round(rect.Top),
            (int)Math.Round(rect.Width), (int)Math.Round(rect.Height),
            SwpNoZOrder | SwpNoActivate);

        // 반올림 한 픽셀 차이로 다시 놓지는 않는다.
        static bool Differs(WindowPlacement a, WindowPlacement b) =>
            Math.Abs(a.Left - b.Left) > 1 || Math.Abs(a.Top - b.Top) > 1
            || Math.Abs(a.Width - b.Width) > 1 || Math.Abs(a.Height - b.Height) > 1;
    }

    /// <summary>어떤 모니터에도 걸치지 않는가. 겹침이 0 일 때만 참이다.</summary>
    public static bool IsOffAllMonitors(nint window) =>
        window != 0 && MonitorFromWindow(window, MonitorDefaultToNull) == 0;
}

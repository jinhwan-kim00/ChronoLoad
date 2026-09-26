using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ChronoLoad.App.Services;

/// <summary>
/// OS 가 그리는 제목 표시줄을 팔레트에 맞춘다.
/// </summary>
/// <remarks>
/// <para>
/// 메인 창은 <c>WindowChrome</c> 으로 제목 표시줄까지 직접 그리지만, 스냅샷 창은
/// 여러 개를 띄워 놓고 OS 의 창 목록·스냅 레이아웃으로 다루는 창이라 기본 테두리를 쓴다.
/// 그러면 다크 테마에서 <b>제목 표시줄만 하얗게</b> 남는다 — DWM 에 색을 알려 주면 된다.
/// </para>
/// <para>
/// 캡션 색·글자 색·테두리 색(34~36)은 Windows 11 부터다. 그 이전에서는 실패 코드가
/// 돌아오므로 다크 모드 플래그(20)만 걸리고, 그 편이 Windows 10 의 기본 어두운 캡션이다.
/// 어느 쪽이든 실패를 막을 방법이 없고 막을 이유도 없다 — 색이 조금 다를 뿐이다.
/// </para>
/// </remarks>
internal static partial class WindowFrame
{
    private const int UseImmersiveDarkMode = 20;
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int TextColor = 36;

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, in int value, int size);

    /// <summary>
    /// <see cref="Window.SourceInitialized"/> 이후에 부른다. 그 전에는 핸들이 없다.
    /// </summary>
    public static void Apply(Window window, ThemePalette palette, bool dark)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;

        int flag = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, in flag, sizeof(int));

        // 캡션은 바로 아래 헤더 띠와 같은 색이다. 두 줄이 한 덩어리로 읽혀야 한다.
        Set(hwnd, CaptionColor, palette.Surface);
        Set(hwnd, TextColor, palette.Fg);
        Set(hwnd, BorderColor, palette.Line);
    }

    private static void Set(nint hwnd, int attribute, Color color)
    {
        int colorref = color.R | (color.G << 8) | (color.B << 16);
        DwmSetWindowAttribute(hwnd, attribute, in colorref, sizeof(int));
    }
}

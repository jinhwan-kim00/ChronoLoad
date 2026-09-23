using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ChronoLoad.App.Services;
using ChronoLoad.Mcp;

namespace ChronoLoad.App.Controls;

/// <summary>
/// 버전과 동작 상태를 보여주는 정보 창.
/// </summary>
/// <remarks>
/// 버전만 적어두면 "그래서 지금 뭐가 되고 있나"는 여전히 알 수 없다. 그래서
/// <b>MCP 서버 주소</b>처럼 실제로 확인하고 싶은 것을 함께 적는다 — 이것 때문에 설정 창을
/// 따로 열거나 로그를 뒤지지 않아도 된다.
/// </remarks>
public sealed class AboutWindow : Window
{
    /// <summary>그림자가 잘리지 않도록 창 안쪽에 비워두는 여백.</summary>
    private const double ShadowMargin = 14;

    public AboutWindow(Window owner)
    {
        var palette = ThemeService.Instance.Palette;

        Owner = owner;
        Title = "ChronoLoad 정보";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 300 + ShadowMargin * 2;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        // 그림자를 그리려면 창 자체가 투명해야 한다. 내용은 안쪽 Border 가 그린다.
        AllowsTransparency = true;
        Background = Brushes.Transparent;

        // 본 창이 항상 위에 있으면 이 창도 같이 올려야 한다. 아니면 정보 창이 본 창 뒤에 깔려
        // "열리지 않는다"처럼 보인다 — 모달이었을 때는 그 상태로 앱 전체가 잠겼다.
        Topmost = owner.Topmost;

        var root = new StackPanel { Margin = new Thickness(18, 16, 18, 14) };

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var mark = new BrandMark { Margin = new Thickness(0, 0, 9, 0), Cursor = Cursors.Arrow };
        mark.ApplyTheme(palette);
        header.Children.Add(mark);
        header.Children.Add(new TextBlock
        {
            Text = "ChronoLoad",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(palette.Fg),
            VerticalAlignment = VerticalAlignment.Center,
        });
        root.Children.Add(header);

        root.Children.Add(new TextBlock
        {
            Text = "GPU 중심 실시간 시스템 모니터",
            FontSize = 11,
            Foreground = Brush(palette.Dim),
            Margin = new Thickness(0, 6, 0, 14),
        });

        foreach (var (name, value) in Facts())
            root.Children.Add(Row(name, value, palette));

        var close = new Button
        {
            Content = "닫기",
            Width = 68,
            Height = 27,
            Margin = new Thickness(0, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = Brush(palette.Fg),
            Background = Brush(palette.Surface2),
            BorderBrush = Brush(palette.Line),
            Cursor = Cursors.Hand,
        };
        close.Click += (_, _) => Close();
        root.Children.Add(close);

        // 카드와 같은 표면·테두리를 쓰면 본 창의 일부처럼 보인다. 셋을 함께 올려
        // "위에 떠 있는 별개의 창"으로 읽히게 한다 — 그림자가 그중 가장 강한 신호다.
        Content = new Border
        {
            Margin = new Thickness(ShadowMargin),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush(Blend(palette.Line, palette.Dim, 0.55)),
            CornerRadius = new CornerRadius(12),
            Background = Brush(palette.Surface2),
            Child = root,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 22,
                ShadowDepth = 5,
                Direction = 270,
                Opacity = 0.5,
                Color = Colors.Black,
            },
        };

        // 제목 표시줄이 없으므로 창 아무 데나 끌어서 옮긴다.
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        KeyDown += (_, e) => { if (e.Key is Key.Escape or Key.Enter) Close(); };

        // 다른 곳을 누르면 닫힌다. 작은 정보 창이 화면에 남아 길을 막지 않게 한다.
        Deactivated += (_, _) => Close();
    }

    private static IEnumerable<(string, string)> Facts()
    {
        var assembly = Assembly.GetExecutingAssembly();

        yield return ("버전", assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                                      ?.InformationalVersion.Split('+')[0]
                              ?? assembly.GetName().Version?.ToString(3)
                              ?? "알 수 없음");

        yield return (".NET", Environment.Version.ToString());

        // 앱이 살아 있을 때만 MCP 가 뜬다(§10.1). 주소가 보이면 연결할 수 있다는 뜻이다.
        yield return ("MCP", McpTokenFile.TryRead() is { } token
            ? $"http://127.0.0.1:{token.Port}/mcp"
            : "실행 중 아님");
    }

    private static Grid Row(string name, string value, ThemePalette palette)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock { Text = name, FontSize = 11, Foreground = Brush(palette.Dim) };
        var content = new TextBlock
        {
            Text = value,
            FontSize = 11,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Foreground = Brush(palette.Label),
            TextWrapping = TextWrapping.Wrap,
        };

        Grid.SetColumn(content, 1);
        grid.Children.Add(label);
        grid.Children.Add(content);
        return grid;
    }

    /// <summary>두 색을 섞는다. 테두리를 카드보다 밝게 하되 본문을 압도하지는 않게.</summary>
    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * amount),
        (byte)(from.G + (to.G - from.G) * amount),
        (byte)(from.B + (to.B - from.B) * amount));

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ChronoLoad.App.Services;
using ChronoLoad.Core.Settings;

namespace ChronoLoad.App.Controls;

/// <summary>
/// 설정 팝오버 (§11). 메인 화면의 <b>단일 화면 원칙</b>을 깨지 않도록 별도 창으로 띄운다.
/// </summary>
/// <remarks>
/// <para>
/// 여기 있는 것은 셋뿐이다. 설정이 적은 것은 만들다 만 것이 아니라 <b>의도</b>다 —
/// 곁눈질로 읽는 위젯에서 고를 것이 늘면 그만큼 외울 것이 는다(§8.6 프리셋을 걷어낸 이유).
/// </para>
/// <para>
/// 그럼에도 이 창이 필요한 이유는 <b>불투명도에 탈출구가 있어야</b> 하기 때문이다.
/// <c>Shift</c>+휠은 발견되지 않는 조작이라, 그것만 두면 기능이 없는 것과 같다(§9.5).
/// </para>
/// </remarks>
public sealed class SettingsWindow : Window
{
    /// <summary>그림자가 잘리지 않도록 창 안쪽에 비워두는 여백.</summary>
    private const double ShadowMargin = 14;

    private readonly AppSettings _settings;
    private readonly Slider _opacity;

    /// <summary>값이 바뀔 때마다 오른다. 창이 곧바로 받아 화면에 반영하고 저장을 예약한다.</summary>
    public event Action? SettingsChanged;

    public SettingsWindow(Window owner, AppSettings settings)
    {
        _settings = settings;
        var palette = ThemeService.Instance.Palette;

        Owner = owner;
        Title = "ChronoLoad 설정";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 300 + ShadowMargin * 2;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;

        // 본 창이 항상 위면 이 창도 올린다. 아니면 뒤에 깔려 "열리지 않는다"처럼 보인다.
        Topmost = owner.Topmost;

        var root = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var close = CloseButton(palette);
        close.MouseLeftButtonUp += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new TextBlock
        {
            Text = "설정",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(palette.Fg),
            VerticalAlignment = VerticalAlignment.Center,
        });
        root.Children.Add(header);

        // ── 불투명도 ─────────────────────────────────────────
        var readout = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Foreground = Brush(palette.Dim),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _opacity = new Slider
        {
            Minimum = AppSettings.MinOpacity * 100,
            Maximum = 100,
            // Shift+휠과 같은 간격으로 움직인다. 두 경로가 서로 다른 값을 만들면 안 된다.
            TickFrequency = 5,
            IsSnapToTickEnabled = true,
            Value = Math.Round(settings.Opacity * 100),
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = Brush(palette.Cpu),
        };
        _opacity.ValueChanged += (_, e) =>
        {
            readout.Text = $"{e.NewValue:0}%";
            _settings.Opacity = e.NewValue / 100;
            SettingsChanged?.Invoke();
        };
        readout.Text = $"{_opacity.Value:0}%";

        root.Children.Add(LabelRow("창 불투명도", readout, palette));
        root.Children.Add(_opacity);
        root.Children.Add(Hint("Shift + 휠로도 바꿀 수 있다", palette));

        // ── 항상 위 ──────────────────────────────────────────
        Border? topmostChip = null;
        topmostChip = Chip("항상 위", settings.Topmost, palette, () =>
        {
            _settings.Topmost = !_settings.Topmost;
            Paint(topmostChip!, _settings.Topmost, palette);
            SettingsChanged?.Invoke();
        });
        root.Children.Add(Separator(palette));
        root.Children.Add(LabelRow("창", topmostChip, palette));

        // ── 테마 ─────────────────────────────────────────────
        root.Children.Add(Separator(palette));
        root.Children.Add(new TextBlock
        {
            Text = "테마",
            FontSize = 12.5,
            Foreground = Brush(palette.Fg),
            Margin = new Thickness(0, 0, 0, 6),
        });

        var themes = new StackPanel { Orientation = Orientation.Horizontal };
        var chips = new List<(Border Chip, AppTheme Mode)>();
        foreach (var (mode, name) in new[]
                 {
                     (AppTheme.System, "시스템"), (AppTheme.Light, "라이트"), (AppTheme.Dark, "다크"),
                 })
        {
            var current = mode;
            Border? chip = null;
            chip = Chip(name, ThemeService.Instance.Mode == current, palette, () =>
            {
                ThemeService.Instance.Mode = current;
                _settings.Theme = current.ToString().ToLowerInvariant();
                foreach (var (other, otherMode) in chips) Paint(other, otherMode == current, palette);
                SettingsChanged?.Invoke();
            });
            chip.Margin = new Thickness(0, 0, 6, 0);
            chips.Add((chip, current));
            themes.Children.Add(chip);
        }
        root.Children.Add(themes);
        root.Children.Add(Hint("제목 표시줄의 달 버튼은 라이트·다크만 오간다", palette));

        Content = new Border
        {
            Margin = new Thickness(ShadowMargin),
            Background = Brush(palette.Surface),
            BorderBrush = Brush(palette.Line),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = root,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 22, ShadowDepth = 3, Opacity = 0.45, Color = Colors.Black,
            },
        };

        // 확인 버튼을 두지 않는 것은 값이 즉시 적용되기 때문이다 — 미리보기가 곧 결과이므로
        // "적용"과 "취소"가 가리킬 상태가 없다. 닫는 길은 ✕ 와 Esc 둘이다.
        //
        // 포커스를 잃으면 닫히게 두었다가 걷어냈다. 불투명도는 <b>본 창을 보면서</b> 맞추는
        // 값인데, 본 창을 한 번 누르면 설정 창이 사라져 버렸다 — 미리보기를 확인하는 행동이
        // 곧 창을 닫는 행동이 되면 안 된다.
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /// <summary>바깥에서 값을 바꿨을 때(<c>Shift</c>+휠) 슬라이더를 따라오게 한다.</summary>
    public void SyncOpacity() => _opacity.Value = Math.Round(_settings.Opacity * 100);

    /// <summary>제목 줄 오른쪽의 ✕. 제목 표시줄의 닫기 버튼과 같은 글리프를 쓴다.</summary>
    private static Border CloseButton(ThemePalette palette)
    {
        var glyph = new System.Windows.Shapes.Path
        {
            Data = Rendering.Icons.Close,
            Stroke = Brush(palette.Dim),
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stretch = Stretch.None,
            Width = Rendering.Icons.DesignSize,
            Height = Rendering.Icons.DesignSize,
            LayoutTransform = new ScaleTransform(12 / Rendering.Icons.DesignSize,
                                                 12 / Rendering.Icons.DesignSize),
        };

        var button = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,   // 없으면 ✕ 획 위에서만 눌린다
            Cursor = Cursors.Hand,
            ToolTip = "닫기 (Esc)",
            Child = glyph,
        };
        button.MouseEnter += (_, _) => glyph.Stroke = Brush(palette.Fg);
        button.MouseLeave += (_, _) => glyph.Stroke = Brush(palette.Dim);
        return button;
    }

    private static Grid LabelRow(string text, UIElement trailing, ThemePalette palette)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = text, FontSize = 12.5, Foreground = Brush(palette.Fg),
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(trailing, 1);
        grid.Children.Add(trailing);
        return grid;
    }

    /// <summary>
    /// 켜짐·꺼짐을 색으로 말하는 칩. WPF 기본 체크박스·라디오를 쓰지 않는 이유는 두 가지다 —
    /// 상자를 흰색으로 칠해 다크 팔레트 위에서 <b>창에서 가장 밝은 것</b>이 되고, 색을 눌러
    /// 다듬으면 이번에는 선택 표시가 사라진다. 시간 폭 칩(§9.4)과 같은 언어로 직접 그린다.
    /// </summary>
    private static Border Chip(string text, bool on, ThemePalette palette, Action toggle)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 3, 9, 4),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = text, FontSize = 12 },
        };
        Paint(chip, on, palette);
        chip.MouseLeftButtonUp += (_, _) => toggle();
        return chip;
    }

    private static void Paint(Border chip, bool on, ThemePalette palette)
    {
        chip.Background = Brush(on ? Tint(palette.Cpu, palette.Surface2) : palette.Surface2);
        chip.BorderBrush = Brush(on ? palette.Cpu : palette.Line);
        ((TextBlock)chip.Child).Foreground = Brush(on ? palette.Fg : palette.Dim);
    }

    /// <summary>강조색을 배경에 옅게 섞는다. 테두리만으로는 켜짐이 약하게 읽힌다.</summary>
    private static Color Tint(Color accent, Color surface) => Color.FromRgb(
        (byte)(surface.R + (accent.R - surface.R) * 0.18),
        (byte)(surface.G + (accent.G - surface.G) * 0.18),
        (byte)(surface.B + (accent.B - surface.B) * 0.18));
    private static TextBlock Hint(string text, ThemePalette palette) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = Brush(palette.Faint),
        Margin = new Thickness(0, 5, 0, 0),
        TextWrapping = TextWrapping.Wrap,
    };

    private static Border Separator(ThemePalette palette) => new()
    {
        Height = 1,
        Background = Brush(palette.Line),
        Margin = new Thickness(0, 14, 0, 12),
    };

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

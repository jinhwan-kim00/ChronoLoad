using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ChronoLoad.App.Rendering;
using ChronoLoad.App.Services;
using ChronoLoad.App.ViewModels;
using ChronoLoad.Core.Metrics;

namespace ChronoLoad.App.Controls;

/// <summary>
/// 카드 하나. 시각 트리를 XAML 없이 코드로 만든다 — 내용의 대부분이 커스텀 렌더러라
/// 템플릿·바인딩을 얹으면 얻는 것 없이 간접층만 늘어난다.
/// </summary>
/// <remarks>
/// 값 갱신도 바인딩이 아니라 <see cref="Refresh"/> 직접 호출이다. 4Hz로 도는 경로에서
/// 바인딩 엔진을 거칠 이유가 없고, 갱신 시점을 샘플 커밋에 정확히 맞출 수 있다.
/// </remarks>
public sealed class CardView : Border
{
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Courier New");
    private static readonly Duration CollapseDuration = new(TimeSpan.FromMilliseconds(180));

    private readonly Grid _grid;
    private readonly RowDefinition _chartRow;
    private readonly RowDefinition _footerRow;
    private readonly System.Windows.Shapes.Path _iconPath;
    private readonly Border _badge;
    private readonly TextBlock _badgeText;
    private readonly TextBlock _label;
    private readonly ChartSurface _spark;
    private readonly System.Windows.Shapes.Path _chevron;
    private readonly RotateTransform _chevronRotation = new(0);
    private readonly TextBlock _value;
    private readonly TextBlock _unit;
    private readonly ChartSurface _chart;
    /// <summary>헤더 높이. 값 글꼴의 줄 상자를 여기에 맞춘다.</summary>
    private const double HeaderHeight = 26;

    private readonly TextBlock _standby;
    private readonly TextBlock _footer;
    private readonly TextBlock _scale;
    private readonly Border _overlay;
    private readonly StackPanel _overlayRows;
    private readonly System.Windows.Shapes.Ellipse _scrubDot;

    private bool _collapsed;

    public CardView(CardViewModel model)
    {
        Model = model;
        CornerRadius = new CornerRadius(12);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(10, 8, 10, 6);
        SnapsToDevicePixels = true;

        _grid = new Grid();
        _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
        _chartRow = new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 0 };
        _footerRow = new RowDefinition { Height = new GridLength(20) };
        _grid.RowDefinitions.Add(_chartRow);
        _grid.RowDefinitions.Add(_footerRow);

        // ── 헤더 ─────────────────────────────────────────────
        var header = new Grid { Height = HeaderHeight };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Background = Brushes.Transparent;   // 히트 테스트용
        header.Cursor = System.Windows.Input.Cursors.Hand;

        var iconHost = new Grid { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        _iconPath = new System.Windows.Shapes.Path
        {
            Data = model.Icon,
            StrokeThickness = 1.7,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = Icons.DesignSize,
            Height = Icons.DesignSize,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _badgeText = new TextBlock
        {
            FontFamily = MonoFont,
            FontSize = 8,
            FontWeight = FontWeights.Bold,
            Text = model.Badge ?? string.Empty,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _badge = new Border
        {
            CornerRadius = new CornerRadius(3),
            MinWidth = 9,
            Height = 10,
            Padding = new Thickness(1, 0, 1, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -2, -2),
            Child = _badgeText,
            Visibility = model.Badge is null ? Visibility.Collapsed : Visibility.Visible,
        };
        iconHost.Children.Add(_iconPath);
        iconHost.Children.Add(_badge);
        Grid.SetColumn(iconHost, 0);
        header.Children.Add(iconHost);

        _label = new TextBlock
        {
            FontSize = 10.5,
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 150,
            Text = model.ShortName,
        };
        _standby = new TextBlock
        {
            FontSize = 9,
            Margin = new Thickness(6, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Text = "대기",
            Visibility = Visibility.Collapsed,
        };

        var labelRow = new StackPanel { Orientation = Orientation.Horizontal };
        labelRow.Children.Add(_label);
        labelRow.Children.Add(_standby);
        Grid.SetColumn(labelRow, 1);
        header.Children.Add(labelRow);

        _spark = new ChartSurface
        {
            Mode = ChartMode.Sparkline,
            Scale = ScaleMode.PeakRelative,
            Series = model.Primary,
            Height = 18,
            Margin = new Thickness(7, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        Grid.SetColumn(_spark, 2);
        header.Children.Add(_spark);

        _chevron = new System.Windows.Shapes.Path
        {
            Data = Icons.Chevron,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stretch = Stretch.None,
            Width = Icons.DesignSize,
            Height = Icons.DesignSize,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _chevronRotation,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.7,
        };
        Grid.SetColumn(_chevron, 3);
        header.Children.Add(_chevron);

        var valuePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        // 스크럽 중임을 알리는 점. "지금 값이 아님"을 한 글자도 쓰지 않고 표시한다.
        _scrubDot = new System.Windows.Shapes.Ellipse
        {
            Width = 4,
            Height = 4,
            Margin = new Thickness(0, 0, 5, 4),
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed,
        };
        valuePanel.Children.Add(_scrubDot);

        _value = new TextBlock
        {
            FontFamily = MonoFont,
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Text = "0",

            // 26px 글꼴의 기본 줄 상자는 약 34px 이라 26px 헤더를 넘쳐 아래가 잘렸다.
            // 줄 높이를 헤더에 맞춰 고정한다 — 숫자에는 디센더가 없어 글리프는 온전히 들어간다.
            LineHeight = HeaderHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // 값이 바뀔 때 자릿수가 흔들리면 눈이 따라가야 한다. 고정폭 숫자는 타협 불가.
        TextOptions.SetTextFormattingMode(_value, TextFormattingMode.Ideal);
        System.Windows.Documents.Typography.SetNumeralAlignment(_value, FontNumeralAlignment.Tabular);
        _unit = new TextBlock
        {
            FontSize = 11,
            Margin = new Thickness(2, 0, 0, 3),
            VerticalAlignment = VerticalAlignment.Bottom,
            Text = "%",
        };
        valuePanel.Children.Add(_value);
        valuePanel.Children.Add(_unit);
        Grid.SetColumn(valuePanel, 4);
        header.Children.Add(valuePanel);

        Grid.SetRow(header, 0);
        _grid.Children.Add(header);

        // ── 차트 ─────────────────────────────────────────────
        _chart = new ChartSurface
        {
            Series = model.Primary,
            SecondarySeries = model.Secondary,
            Mode = model.Mode,
            Scale = model.Scale,
            FixedMax = model.FixedMax,
            Steps = model.Steps,
            MemoryDedicated = model.MemoryDedicated,
            MemoryShared = model.MemoryShared,
            DedicatedCapacity = model.DedicatedCapacity,
            IsDiscrete = model.IsDiscrete,
        };
        Grid.SetRow(_chart, 1);
        _grid.Children.Add(_chart);

        // ── 동기화 오버레이 ──────────────────────────────────
        _overlayRows = new StackPanel();
        _overlay = new Border
        {
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(7, 4, 7, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
            IsHitTestVisible = false,          // 레이아웃을 밀지도, 커서를 가로막지도 않는다
            Visibility = Visibility.Collapsed,
            Child = _overlayRows,
        };
        Grid.SetRow(_overlay, 1);
        _grid.Children.Add(_overlay);

        _chart.MouseMove += (_, e) =>
        {
            double width = _chart.ActualWidth;
            if (width < 4) return;
            int index = (int)Math.Round(e.GetPosition(_chart).X / width * (_chart.WindowPoints - 1));
            ScrubHover?.Invoke(this, Math.Clamp(index, 0, _chart.WindowPoints - 1));
        };
        _chart.MouseLeave += (_, _) => ScrubLeave?.Invoke(this);
        _chart.MouseLeftButtonUp += (_, e) =>
        {
            double width = _chart.ActualWidth;
            if (width < 4) return;
            int index = (int)Math.Round(e.GetPosition(_chart).X / width * (_chart.WindowPoints - 1));
            e.Handled = true;
            ScrubToggle?.Invoke(this, Math.Clamp(index, 0, _chart.WindowPoints - 1));
        };

        // ── 푸터 ─────────────────────────────────────────────
        var footerGrid = new Grid { Height = 20 };
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _footer = new TextBlock { FontFamily = MonoFont, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };
        _scale = new TextBlock { FontFamily = MonoFont, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_footer, 0);
        Grid.SetColumn(_scale, 1);
        footerGrid.Children.Add(_footer);
        footerGrid.Children.Add(_scale);
        Grid.SetRow(footerGrid, 2);
        _grid.Children.Add(footerGrid);

        Child = _grid;

        header.MouseLeftButtonUp += (_, e) => { e.Handled = true; ToggleRequested?.Invoke(this); };
        ToolTip = model.FullName;
    }

    public CardViewModel Model { get; }

    public event Action<CardView>? ToggleRequested;

    /// <summary>차트 위에서 커서가 움직였다. 인덱스는 표시 창 기준이다.</summary>
    public event Action<CardView, int>? ScrubHover;
    public event Action<CardView>? ScrubLeave;
    public event Action<CardView, int>? ScrubToggle;

    public int WindowPoints => _chart.WindowPoints;

    /// <summary>경고 상태. 접힌 카드에서도 테두리로 드러난다.</summary>
    public bool IsAlert { get; set; }

    public void ApplyTheme(ThemePalette palette)
    {
        var accent = Model.Accent(palette);
        var iconColor = Model.IconColor(palette);

        Background = Frozen(palette.Surface);
        BorderBrush = Frozen(IsAlert ? Blend(palette.Warn, 0.55, palette.Line) : palette.Line);

        _iconPath.Stroke = Frozen(iconColor);
        _badge.Background = Model.BadgeOutlined ? Frozen(palette.Surface) : Frozen(iconColor);
        _badge.BorderBrush = Frozen(iconColor);
        _badge.BorderThickness = new Thickness(Model.BadgeOutlined ? 1 : 0);
        _badgeText.Foreground = Frozen(Model.BadgeOutlined ? iconColor : palette.Surface);

        _label.Foreground = Frozen(palette.Label);
        _standby.Foreground = Frozen(palette.Warn);
        _chevron.Stroke = Frozen(palette.Faint);
        _value.Foreground = Frozen(palette.Fg);
        _unit.Foreground = Frozen(palette.Dim);
        _footer.Foreground = Frozen(palette.Dim);
        _scale.Foreground = Frozen(palette.Faint);

        _chart.Accent = accent;
        _chart.Palette = palette;
        _overlay.Background = Frozen(palette.Surface2);
        _overlay.BorderBrush = Frozen(palette.Line);
        _scrubDot.Fill = Frozen(palette.Dim);
        _spark.Accent = accent;
        _spark.Palette = palette;
    }

    /// <summary>최초 실행·새 장치·hover 에서 장치 이름을 잠시 보여준다(UX §04).</summary>
    /// <summary>
    /// 장치 이름을 다시 읽는다. 같은 장치가 이름만 바꾸는 경우가 있다 —
    /// Wi-Fi 의 SSID 나 드라이버 재설치 뒤의 어댑터 표기 같은 것들이다.
    /// 카드를 새로 만들면 통계와 접힘 상태가 날아가므로 문구만 갈아끼운다.
    /// </summary>
    public void RefreshIdentity()
    {
        _label.Text = Model.ShortName;
        ToolTip = Model.FullName;
    }


    /// <summary>
    /// 28px 스트립에 라벨과 스파크라인을 함께 넣으면 어느 쪽도 읽히지 않는다.
    /// 접힘 상태와 라벨 상태가 서로 다른 시점에 바뀌므로 표시 결정은 한 곳에서만 한다.
    /// </summary>
    /// <summary>
    /// 접힌 카드에서만 스파크라인을 보여준다. 펼친 카드에는 본 차트가 있다.
    /// </summary>
    /// <remarks>
    /// 라벨과 스파크라인은 <b>서로 다른 열</b>에 있어 함께 보여도 겹치지 않는다.
    /// 예전에는 둘을 배타적으로 묶어 hover 때만 라벨을 띄웠는데, 같은 아이콘의 카드가
    /// 여럿(GPU 4장·디스크 2장·NIC 2장)이면 접힌 줄을 구분할 수 없었다.
    /// </remarks>
    private void UpdateSparkVisibility() =>
        _spark.Visibility = _collapsed ? Visibility.Visible : Visibility.Collapsed;

    public void ApplyCollapsed(bool collapsed, bool animate)
    {
        _chartRow.Height = collapsed ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        _footerRow.Height = collapsed ? new GridLength(0) : new GridLength(20);
        _chart.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        _collapsed = collapsed;
        UpdateSparkVisibility();
        Padding = collapsed ? new Thickness(10, 1, 10, 1) : new Thickness(10, 8, 10, 6);

        var target = collapsed ? -90d : 0d;
        if (animate) _chevronRotation.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(target, CollapseDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        else { _chevronRotation.BeginAnimation(RotateTransform.AngleProperty, null); _chevronRotation.Angle = target; }

        BorderThickness = new Thickness(1);
        if (Model.AutoCollapsed) BorderBrush = DashedHint(BorderBrush);
    }

    /// <summary>높이를 목표값으로 옮긴다. 프리셋 전환에서 여러 카드가 동시에 움직여도 타이밍이 같다.</summary>
    public void AnimateHeight(double target, bool animate)
    {
        if (!animate || double.IsNaN(Height) || Math.Abs(Height - target) < 0.5)
        {
            BeginAnimation(HeightProperty, null);
            Height = target;
            return;
        }

        BeginAnimation(HeightProperty, new DoubleAnimation(target, CollapseDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    /// <summary>
    /// 전 카드가 같은 인덱스를 받는다. 포커스 카드만 전체 패널을 띄우고 나머지는 요약 칩이다.
    /// </summary>
    public void ApplyScrub(int? index, bool isFocus, ThemePalette palette)
    {
        _chart.ScrubIndex = index;
        _scrubDot.Visibility = index is null ? Visibility.Collapsed : Visibility.Visible;
        _value.Foreground = Frozen(index is null ? palette.Fg : palette.Dim);

        if (index is null || Model.Collapsed)
        {
            _overlay.Visibility = Visibility.Collapsed;
            return;
        }

        _overlayRows.Children.Clear();

        if (isFocus)
        {
            // 전체 패널의 첫 줄은 언제나 전체 장치명이다 — 이름 확인과 값 읽기가 한 제스처로 해결된다.
            _overlayRows.Children.Add(new TextBlock
            {
                Text = Model.FullName,
                FontFamily = MonoFont,
                FontSize = 9,
                Foreground = Frozen(palette.Dim),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 240,
                Margin = new Thickness(0, 0, 0, 2),
            });
        }

        foreach (var (label, value) in Model.OverlayRows(index.Value, isFocus))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (label.Length > 0)
                row.Children.Add(new TextBlock
                {
                    Text = label,
                    FontFamily = MonoFont,
                    FontSize = 9.5,
                    Foreground = Frozen(palette.Dim),
                    Margin = new Thickness(0, 0, 5, 0),
                });

            row.Children.Add(new TextBlock
            {
                Text = value,
                FontFamily = MonoFont,
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Frozen(palette.Fg),
            });
            _overlayRows.Children.Add(row);
        }

        _overlay.Visibility = Visibility.Visible;
        PositionOverlay(index.Value);
    }

    /// <summary>커서 반대편으로 자동 플립해 데이터를 가리지 않는다.</summary>
    private void PositionOverlay(int index)
    {
        _overlay.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double panelWidth = _overlay.DesiredSize.Width;
        double chartWidth = _chart.ActualWidth;

        // 직전 렌더의 ScrubX 를 쓰면 한 프레임 늦는다. 인덱스에서 직접 계산한다.
        double x = _chart.XForIndex(index);

        double left = x + 8 + panelWidth > chartWidth ? x - 8 - panelWidth : x + 8;
        left = Math.Clamp(left, 2, Math.Max(2, chartWidth - panelWidth - 2));
        _overlay.Margin = new Thickness(left, 2, 0, 0);
    }

    public void Refresh(MetricRegistry registry)
    {
        // 전원 상태는 장치가 스스로 오르내린다. 매 갱신에 따라간다.
        _standby.Visibility = Model.IsStandby ? Visibility.Visible : Visibility.Collapsed;

        var formatted = Model.HeaderValue(_chart.ScrubIndex);
        _value.Text = formatted.Value;
        _unit.Text = formatted.Unit;

        if (Model.Collapsed)
        {
            _spark.Invalidate();
            return;
        }

        _chart.AverageValue = Model.Mode == ChartMode.Area ? Model.AverageForChart(registry) : double.NaN;
        _chart.Invalidate();
        _footer.Text = Model.FooterText(registry);
        _scale.Text = Model.ScaleText(_chart.LastAxisMax);
        IsAlert = _chart.IsSpilling;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color Blend(Color color, double amount, Color onto) => Color.FromRgb(
        (byte)(color.R * amount + onto.R * (1 - amount)),
        (byte)(color.G * amount + onto.G * (1 - amount)),
        (byte)(color.B * amount + onto.B * (1 - amount)));

    private static Brush DashedHint(Brush baseBrush) => baseBrush;   // 점선 테두리는 M4 마감에서
}

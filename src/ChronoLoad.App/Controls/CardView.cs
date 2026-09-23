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

    /// <summary>스파크라인과 메모리 미터를 함께 담는다. 접힘 여부를 이 하나로 켠다.</summary>
    private readonly Grid _sparkHost;

    /// <summary>메모리 미터. 채움 칸과 빈 칸을 비율대로 나눠 폭 계산 없이 그린다.</summary>
    private readonly Grid _meterTrack;
    private readonly Border _meterFill;

    private readonly System.Windows.Shapes.Path _chevron;
    private readonly RotateTransform _chevronRotation = new(0);
    private readonly TextBlock _value;
    private readonly TextBlock _unit;

    /// <summary>큰 숫자 옆의 보조 수치. GPU 카드의 <c>12/22G</c> 가 여기 들어간다.</summary>
    private readonly TextBlock _subValue;
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

        // 퍼센트 지표는 스파크라인도 0~100 에 고정한다. 상대 스케일로 그리면 3% 대에서
        // 미세하게 흔들리는 것과 90% 대에서 흔들리는 것이 같은 모양으로 나와, 접힌 카드에서
        // "지금 바쁜가"를 오히려 잘못 읽게 된다. 접힌 카드는 그 판단 하나를 위해 있다.
        // 전송률처럼 상한이 없는 지표는 절대 축을 쓰면 늘 바닥에 붙으므로 상대 스케일 그대로 둔다.
        bool percentScale = model.DisplayUnit == MetricUnit.Percent && model.FixedMax > 0;
        bool hasMeter = model.MemoryDedicated is not null && model.MemoryCapacity > 0;

        _spark = new ChartSurface
        {
            Mode = ChartMode.Sparkline,
            Scale = percentScale ? ScaleMode.Fixed : ScaleMode.PeakRelative,
            FixedMax = model.FixedMax,
            Series = model.Primary,
            Height = hasMeter ? 12 : 18,
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        // 메모리 미터. 사용률은 시간축을 가진 꺾은선이고 메모리 점유는 지금 한 값이라,
        // 같은 그림에 겹치면 둘 다 읽기 어렵다. 꺾은선 위에 가로 바로 따로 얹는다.
        // 형태도 이쪽이 맞다 — 한계값 대비 단일 비율은 미터가 읽기 쉽고, 2조각 파이는 그렇지 않다.
        _meterFill = new Border { CornerRadius = new CornerRadius(2) };
        _meterTrack = new Grid
        {
            Height = 4,
            VerticalAlignment = VerticalAlignment.Top,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            },
        };
        _meterTrack.Children.Add(_meterFill);

        var sparkHost = new Grid
        {
            Height = 18,
            Margin = new Thickness(7, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        if (hasMeter) sparkHost.Children.Add(_meterTrack);
        sparkHost.Children.Add(_spark);

        _sparkHost = sparkHost;
        Grid.SetColumn(sparkHost, 2);
        header.Children.Add(sparkHost);

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

        // 사용률과 메모리는 성질이 다르다. 같은 크기로 나란히 두면 어느 쪽을 먼저 봐야 할지
        // 알 수 없고, 26px 로 둘을 한 줄에 넣으면 라벨과 스파크라인 자리를 다 먹는다.
        // 큰 숫자는 사용률 하나로 두고 메모리는 한 단계 아래에 붙인다.
        //
        // 단위(`%`)와 같은 11px·Dim 으로 두면 값이 아니라 장식으로 읽힌다 — 이건 읽으라고
        // 놓은 숫자다. 14px 에 SemiBold 로 올리고 색도 Label 까지 끌어올린다.
        _subValue = new TextBlock
        {
            FontFamily = MonoFont,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(6, 0, 0, 2),
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed,
        };

        valuePanel.Children.Add(_value);
        valuePanel.Children.Add(_unit);
        valuePanel.Children.Add(_subValue);
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
            SharedCapacity = model.SharedCapacity,
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

        // 차트 행 하나에 가두면 카드가 짧을 때 패널 아래쪽이 잘린다 — 코어 막대처럼 줄이 늘어나는
        // 내용에서 바로 드러난다. 푸터 행까지 걸쳐 필요한 만큼 내려가게 둔다.
        // 스크럽 중에만 보이는 요소라 푸터를 잠깐 덮는 편이 내용이 잘리는 것보다 낫다.
        Grid.SetRowSpan(_overlay, 2);
        _grid.Children.Add(_overlay);

        // 차트가 아니라 카드에 붙인다. 차트 바깥 여백에서도 스크럽이 잡히게 하기 위해서다 —
        // 아래 TryIndexAt 참조. 머리글 클릭(접기)은 자식에서 이미 Handled 로 끝나므로
        // 여기까지 오지 않는다.
        // 차트 띠를 벗어나면(머리글·푸터) 호버를 놓는다. 그러지 않으면 카드를 접으려고
        // 머리글에 커서를 올리는 동안 스크럽선이 옛 자리에 남아 전 카드가 과거를 가리킨다.
        MouseMove += (_, e) =>
        {
            if (TryIndexAt(e, out int index)) ScrubHover?.Invoke(this, index);
            else ScrubLeave?.Invoke(this);
        };
        MouseLeave += (_, _) => ScrubLeave?.Invoke(this);
        MouseLeftButtonUp += (_, e) =>
        {
            if (!TryIndexAt(e, out int index)) return;
            e.Handled = true;
            ScrubToggle?.Invoke(this, index);
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

    /// <summary>스크럽 인덱스의 범위. 차트가 실제로 그린 점 수다.</summary>
    public int WindowPoints => Math.Max(2, _chart.PointCount);

    /// <summary>
    /// 차트 좌우 바깥으로 두는 여유. 이만큼 벗어나도 양 끝 점을 집은 것으로 친다.
    /// </summary>
    /// <remarks>
    /// <b>맨 오른쪽 점은 커서로 집을 수 없었다.</b> 240개를 그리는 차트에서 한 점이 차지하는
    /// 폭은 1/239 — 2px 남짓이고, 그 점은 차트의 맨 끝이라 반쪽만 남는다. 하필 그 점이
    /// "지금"이라 가장 자주 쓰인다(§8.7 의 현재값 추적). 카드 안쪽 여백까지 같은 점으로 쳐서
    /// 집을 수 있게 한다.
    /// </remarks>
    private const double ScrubSlack = 12;

    /// <summary>커서 위치 → 표시 창 인덱스. 스크럽 대상이 아닌 자리면 false.</summary>
    private bool TryIndexAt(System.Windows.Input.MouseEventArgs e, out int index)
    {
        index = 0;
        if (_collapsed) return false;

        var point = e.GetPosition(_chart);
        double width = _chart.ActualWidth, height = _chart.ActualHeight;
        if (width < 4 || height < 4) return false;

        // 세로로는 여유를 주지 않는다. 머리글·푸터까지 스크럽 영역으로 치면 카드를 접으려고
        // 머리글에 커서를 올리는 동안에도 값이 과거로 바뀐다.
        if (point.Y < 0 || point.Y > height) return false;
        if (point.X < -ScrubSlack || point.X > width + ScrubSlack) return false;

        // 인덱스는 차트가 실제로 그린 점 수를 기준으로 잡는다. WindowPoints 로 잡으면
        // 버퍼가 차기 전(기동 직후 1분)에는 집은 자리와 읽히는 값이 어긋난다.
        int last = WindowPoints - 1;
        index = Math.Clamp((int)Math.Round(point.X / width * last), 0, last);
        return true;
    }

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
        _subValue.Foreground = Frozen(palette.Label);
        _footer.Foreground = Frozen(palette.Dim);

        // 스케일 힌트는 보통 "축이 어디까지인지" 알려주는 보조라 가장 흐린 단계다.
        // GPU 카드에서만은 그 자리가 메모리 **전체 크기**를 말하므로 읽을 값이다 —
        // Faint 로 두면 사용량과 짝지어 읽히지 않는다.
        _scale.Foreground = Frozen(Model.MemoryDedicated is not null ? palette.Dim : palette.Faint);

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
        _sparkHost.Visibility = _collapsed ? Visibility.Visible : Visibility.Collapsed;

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

        if (isFocus && BuildCoreBars(index.Value, palette) is { } cores) _overlayRows.Children.Add(cores);

        _overlay.Visibility = Visibility.Visible;
        PositionOverlay(index.Value);
    }

    /// <summary>
    /// 논리 코어별 사용률 미니 바 (§5.1, UX §04). 코어 채널이 없으면 null.
    /// </summary>
    /// <remarks>
    /// <b>숫자가 아니라 막대인 이유.</b> 여기서 읽는 것은 "8번 코어가 몇 퍼센트인가"가 아니라
    /// "한 코어만 물려 있나, 고르게 퍼졌나"다 — 단일 스레드 병목과 전체 부하를 가르는 판단이고,
    /// 형태로 봐야 한 눈에 들어온다. 코어 16개의 숫자를 늘어놓으면 오버레이가 표가 된다.
    /// <para>
    /// 값이 없는 코어(파킹·이번 틱 누락)는 빈 눈금으로 남긴다. 0% 로 그리면 "쉬는 중"으로
    /// 읽히는데 실제로는 "모른다"이므로 둘을 같은 그림으로 만들면 안 된다.
    /// </para>
    /// </remarks>
    private StackPanel? BuildCoreBars(int index, ThemePalette palette)
    {
        var usage = Model.CoreUsage(index);
        if (usage.Length == 0) return null;

        // 막대가 너무 얇으면 값을 못 읽는다. 코어가 많을 때만 좁히고, 보통은 넉넉히 준다.
        double barWidth = usage.Length <= 12 ? 10 : usage.Length <= 24 ? 6 : 4;
        const double BarHeight = 26, Gap = 2;

        var classes = Model.CoreClasses;
        bool classed = classes.Count == usage.Length;

        // 효율 등급은 순서가 있는 값(클수록 성능 지향)이라 <b>같은 색의 진하기</b>로 나타낸다.
        // 자리를 등급끼리 모으거나 틈을 주는 방법도 있었지만, 그러면 막대 위치가 코어 번호와
        // 어긋나고 등급이 셋 이상인 칩에서 틈이 늘어난다. 진하기는 등급 수가 몇이든 그대로 는다.
        var ranks = classed ? classes.Distinct().Order().ToArray() : [];
        var fills = new Dictionary<byte, System.Windows.Media.Brush>();
        var accent = Model.Accent(palette);

        foreach (byte cls in ranks)
        {
            // 가장 옅은 단계도 빈 눈금과는 확실히 갈려야 한다. 0x99 아래로 내리면
            // 값이 작은 효율 코어가 "값 없음"처럼 보인다.
            double t = ranks.Length <= 1 ? 1 : Array.IndexOf(ranks, cls) / (double)(ranks.Length - 1);
            fills[cls] = Frozen(System.Windows.Media.Color.FromArgb(
                (byte)(0x99 + (0xFF - 0x99) * t), accent.R, accent.G, accent.B));
        }

        var plain = Frozen(accent);
        var track = Frozen(palette.Line);

        var column = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        var bars = new StackPanel { Orientation = Orientation.Horizontal };

        for (int core = 0; core < usage.Length; core++)
        {
            var slot = new Border
            {
                Width = barWidth,
                Height = BarHeight,
                Background = track,
                Margin = new Thickness(0, 0, Gap, 0),
            };

            if (!float.IsNaN(usage[core]))
                slot.Child = new Border
                {
                    Background = classed ? fills[classes[core]] : plain,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Height = Math.Max(1, BarHeight * Math.Clamp(usage[core], 0, 100) / 100),
                };

            bars.Children.Add(slot);
        }

        column.Children.Add(bars);
        column.Children.Add(new TextBlock
        {
            // 진하기가 무엇을 뜻하는지 말해주지 않으면 그냥 색이 다른 막대로 보인다.
            Text = classed && ranks.Length > 1
                ? $"논리 코어 {usage.Length} · 진할수록 고성능"
                : $"논리 코어 {usage.Length}",
            FontFamily = MonoFont,
            FontSize = 9.5,
            Foreground = Frozen(palette.Dim),
            Margin = new Thickness(0, 2, 0, 0),
        });

        return column;
    }

    /// <summary>커서 반대편으로 자동 플립해 데이터를 가리지 않는다.</summary>
    private void PositionOverlay(int index)
    {
        // 패널 폭은 <b>내용</b>에서 잰다. _overlay.DesiredSize 에는 우리가 방금 설정한
        // Margin.Left 가 더해져 있어서, 그것으로 재면 지난 틱에 오른쪽으로 밀어둔 여백이
        // 이번 틱의 "패널 폭"이 된다 — 플립 조건이 매 틱 뒤집혀 칩이 좌우 끝으로 튄다.
        // 오버레이를 매 틱 다시 채우기 시작하면서 드러났다.
        _overlayRows.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double panelWidth = _overlayRows.DesiredSize.Width
                            + _overlay.Padding.Left + _overlay.Padding.Right
                            + _overlay.BorderThickness.Left + _overlay.BorderThickness.Right;

        double chartWidth = _chart.ActualWidth;

        // 직전 렌더의 ScrubX 를 쓰면 한 프레임 늦는다. 인덱스에서 직접 계산한다.
        double x = _chart.XForIndex(index);

        double left = x + 8 + panelWidth > chartWidth ? x - 8 - panelWidth : x + 8;
        left = Math.Clamp(left, 2, Math.Max(2, chartWidth - panelWidth - 2));
        _overlay.Margin = new Thickness(left, 2, 0, 0);
    }

    /// <summary>
    /// GPU 카드의 메모리 요약 — 헤더의 <c>12/22G</c> 와 접힌 카드의 미터.
    /// </summary>
    /// <remarks>
    /// 미터는 채움 칸과 빈 칸의 별 비율로만 그린다. 실제 폭을 재서 그리면 창 크기가 바뀔 때마다
    /// 다시 계산해야 하고, 그 타이밍이 레이아웃과 어긋나면 한 프레임씩 튄다.
    /// 넘친 경우 비율을 1 로 자른다 — 막대가 칸을 넘어 삐져나오게 두면 "얼마나 넘었나"가
    /// 아니라 "레이아웃이 깨졌나"로 읽힌다. 넘겼다는 사실은 색이 말한다.
    /// </remarks>
    private void UpdateMemoryHeadline()
    {
        if (Model.MemoryHeadline(_chart.ScrubIndex) is not { } memory)
        {
            _subValue.Visibility = Visibility.Collapsed;
            return;
        }

        _subValue.Text = memory.Text;
        _subValue.Visibility = Visibility.Visible;

        double filled = Math.Clamp(memory.Ratio, 0, 1);
        _meterTrack.ColumnDefinitions[0].Width = new GridLength(filled, GridUnitType.Star);
        _meterTrack.ColumnDefinitions[1].Width = new GridLength(1 - filled, GridUnitType.Star);

        var palette = ThemeService.Instance.Palette;
        var fill = memory.Over ? palette.Warn : Model.Accent(palette);

        _meterFill.Background = Frozen(fill);
        // 빈 칸은 회색이 아니라 같은 색의 옅은 단계다. 그래야 막대 전체가 한 상태로 읽힌다.
        _meterTrack.Background = Frozen(Color.FromArgb(0x33, fill.R, fill.G, fill.B));
    }

    public void Refresh(MetricRegistry registry)
    {
        // 전원 상태는 장치가 스스로 오르내린다. 매 갱신에 따라간다.
        _standby.Visibility = Model.IsStandby ? Visibility.Visible : Visibility.Collapsed;

        var formatted = Model.HeaderValue(_chart.ScrubIndex);
        _value.Text = formatted.Value;
        _unit.Text = formatted.Unit;
        UpdateMemoryHeadline();

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

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using ChronoLoad.App.Controls;
using ChronoLoad.App.Rendering;
using ChronoLoad.App.Services;
using ChronoLoad.App.ViewModels;
using ChronoLoad.Core.Formatting;
using ChronoLoad.Core.Layout;
using ChronoLoad.Core.Metrics;
using ChronoLoad.Core.Sampling;
using ChronoLoad.Core.Settings;

namespace ChronoLoad.App;

public partial class MainWindow : Window
{
    private readonly MetricRegistry _registry;
    private readonly SampleEngine? _engine;
    private readonly List<CardView> _cards = [];
    private readonly ThemeService _theme = ThemeService.Instance;

    /// <summary>
    /// 창 높이가 바뀌는 동안 중간 높이로 레이아웃을 계산하면 자동 접힘이 과하게 발동해 카드가 전부 접힌다.
    /// 크기가 멎은 뒤에 한 번만 계산한다.
    /// </summary>
    private readonly DispatcherTimer _resizeSettle = new() { Interval = TimeSpan.FromMilliseconds(60) };

    /// <summary>
    /// 제목 표시줄 + 요약 바의 높이. <b>첫 레이아웃 전에만 쓰는 폴백</b>이다 —
    /// 실제 값은 <see cref="ChromeOf"/> 가 측정에서 역산한다.
    /// 상수로 박아두면 크롬이 조금만 바뀌어도 카드 높이가 어긋나 마지막 카드가 잘린다.
    /// </summary>
    private const double FallbackChromeHeight = 61;

    /// <summary>마지막으로 안정된 상태에서 측정한 크롬 높이.</summary>
    private double _chrome = FallbackChromeHeight;

    private readonly BrandMark _brand = new();
    private VisibilityWatch? _visibility;

    private readonly ScrubState _scrub = new();
    private readonly int? _initialScrub;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _saveSettle = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly bool _startCollapsed;
    private bool _suppressResizeLayout;

    public MainWindow(MetricRegistry registry, SampleEngine? engine, bool startCollapsed = false,
        int? initialScrub = null)
    {
        _registry = registry;
        _engine = engine;
        _initialScrub = initialScrub;
        _startCollapsed = startCollapsed;
        Topmost = _settings.Topmost;

        // 설정 저장은 모아서 한 번에 한다. 창을 끌 때마다 파일을 쓸 이유가 없다.
        _saveSettle.Tick += (_, _) => { _saveSettle.Stop(); SaveSettings(); };

        InitializeComponent();
        BuildChromeIcons();

        _theme.Changed += ApplyTheme;
        _theme.Apply();

        TitleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        ResetButton.Click += (_, _) => ResetStats();
        BuildBrandMenu();
        PinButton.Click += (_, _) => { Topmost = !Topmost; ApplyTheme(); MarkSettingsDirty(); };
        ThemeButton.Click += (_, _) => _theme.Toggle();
        MinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;
        CloseButton.Click += (_, _) => Close();

        _resizeSettle.Tick += (_, _) =>
        {
            _resizeSettle.Stop();
            RemeasureChrome();          // 크기 변화가 멎은 지금이 크롬을 잴 수 있는 순간이다
            ApplyLayout(animate: false);
        };
        SizeChanged += (_, _) => { if (!_suppressResizeLayout) _resizeSettle.Start(); MarkSettingsDirty(); };
        LocationChanged += (_, _) => MarkSettingsDirty();

        _scrub.Changed += ApplyScrub;
        KeyDown += OnKeyDown;
        Loaded += OnLoaded;

        if (_engine is not null)
        {
            _engine.Committed += OnCommitted;

            // 장치 변경은 샘플링 스레드에서 온다. 카드 조작은 UI 스레드에서만 한다.
            _engine.DevicesChanged += _ => Dispatcher.InvokeAsync(SyncCards, DispatcherPriority.Background);
        }
    }

    public MainWindow() : this(new MetricRegistry(), null) { }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        BuildCards();

        RestorePlacement();

        RemeasureChrome();
        ApplyLayout(animate: false);

        // §6.3 적응형 백오프. 창이 안 보이면 렌더를 끊고 샘플 주기를 늦춘다.
        if (_engine is not null)
        {
            _visibility = new VisibilityWatch(this, _engine);
            _visibility.Changed += () => { if (_visibility.ShouldRender) RefreshCards(); };
            _visibility.Start();
        }

        // 렌더 테스트에서 스크럽 상태를 재현하기 위한 진입점.
        if (_initialScrub is { } index && _cards.Count > 0)
        {
            var focus = _cards.FirstOrDefault(c => !c.Model.Collapsed) ?? _cards[0];
            _scrub.TogglePin(focus.Model.Key, index);
        }
    }

    // ── 카드 ────────────────────────────────────────────────────
    private void BuildCards()
    {
        CardHost.Children.Clear();
        _cards.Clear();

        foreach (var device in _registry.ActiveDevices.OrderBy(OrderOf))
        {
            if (CreateCard(device) is not { } card) continue;

            _cards.Add(card);
            CardHost.Children.Add(card);
        }

        // 표시 순서는 CPU → 메모리 → 네트워크 → 디스크 → GPU 고정.
        ApplyTheme();
    }

    private CardView? CreateCard(DeviceHandle device)
    {
        var model = CardFactory.TryCreate(device, _registry);
        if (model is null) return null;

        // 지난번에 접어둔 상태를 되살린다. 처음 보는 장치는 펼친 채로 둔다 —
        // 새로 꽂은 것을 접어서 보여주면 무엇이 늘었는지 알아차리기 어렵다.
        model.UserCollapsed = _settings.Collapsed.GetValueOrDefault(device.Key, _startCollapsed);

        var card = new CardView(model) { Margin = new Thickness(0, 0, 0, 8) };
        card.ToggleRequested += OnCardToggled;
        card.ScrubHover += (c, i) => _scrub.Hover(c.Model.Key, i);
        card.ScrubLeave += _ => _scrub.Leave();
        card.ScrubToggle += (c, i) => _scrub.TogglePin(c.Model.Key, i);
        return card;
    }

    /// <summary>
    /// 장치 집합이 바뀌었을 때 <b>바뀐 카드만</b> 넣고 뺀다 (§5.7).
    /// </summary>
    /// <remarks>
    /// 전체를 다시 짓지 않는 이유는 눈에 보이는 것 전부가 끊기기 때문이다 —
    /// Wi-Fi 하나 켰다고 모든 카드가 껌뻑이면 안 되고, 스크럽 고정과 접힘 상태도 날아간다.
    /// 시리즈와 통계는 레지스트리가 들고 있으므로, 같은 키로 돌아온 장치는 카드만 다시 붙이면
    /// <b>리셋 이후 누적 통계가 그대로 이어진다</b>(§5.7 60초 보관 규칙).
    /// </remarks>
    private void SyncCards()
    {
        var devices = _registry.ActiveDevices.OrderBy(OrderOf).ToArray();
        var wanted = new HashSet<string>(devices.Select(d => d.Key), StringComparer.OrdinalIgnoreCase);

        var removed = _cards.Where(c => !wanted.Contains(c.Model.Key)).ToArray();
        var added = new List<CardView>();

        foreach (var card in removed)
        {
            _cards.Remove(card);
            FadeOutAndRemove(card);
        }

        // 순서는 OrderOf 가 정한다. 새 카드가 목록 중간에 끼어도 자리를 지키게 인덱스로 꽂는다.
        var byKey = _cards.ToDictionary(c => c.Model.Key, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < devices.Length; i++)
        {
            if (byKey.TryGetValue(devices[i].Key, out var existing))
            {
                // 레지스트리가 같은 DeviceHandle 을 재사용하므로 모델은 이미 새 정보를 가리킨다.
                // 화면에 박아둔 문구만 다시 읽으면 된다.
                existing.RefreshIdentity();
                continue;
            }

            if (CreateCard(devices[i]) is not { } card) continue;

            _cards.Insert(Math.Min(i, _cards.Count), card);
            CardHost.Children.Insert(Math.Min(i, CardHost.Children.Count), card);
            added.Add(card);
        }

        if (removed.Length == 0 && added.Count == 0) return;

        ApplyTheme();
        ResuggestWindowHeight();
        ApplyLayout(animate: true);

        foreach (var card in added) FadeIn(card);

        Announce(added.Select(c => c.Model.ShortName), removed.Select(c => c.Model.ShortName));
    }

    /// <summary>
    /// 카드가 사라지는 애니메이션. 높이와 투명도를 같이 줄여 아래 카드가 밀려 올라오게 한다.
    /// </summary>
    private void FadeOutAndRemove(CardView card)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(180));

        var fade = new DoubleAnimation(0, duration);
        var shrink = new DoubleAnimation(card.ActualHeight, 0, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        shrink.Completed += (_, _) =>
        {
            card.BeginAnimation(HeightProperty, null);
            CardHost.Children.Remove(card);
        };

        card.IsHitTestVisible = false;
        card.BeginAnimation(OpacityProperty, fade);
        card.BeginAnimation(HeightProperty, shrink);
    }

    private static void FadeIn(CardView card)
    {
        card.Opacity = 0;
        card.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(320))));
    }

    /// <summary>
    /// 화면에서는 카드의 등장·소멸 자체가 알림이다. 그것이 보이지 않는 사용자를 위해
    /// 같은 내용을 라이브 영역으로 한 번 읽어준다 (§5.7).
    /// </summary>
    private void Announce(IEnumerable<string> added, IEnumerable<string> removed)
    {
        var parts = new List<string>();
        foreach (string name in added) parts.Add($"{name} 연결됨");
        foreach (string name in removed) parts.Add($"{name} 제거됨");
        if (parts.Count == 0) return;

        LiveRegion.Text = string.Join(", ", parts);
        System.Windows.Automation.AutomationProperties.SetName(LiveRegion, LiveRegion.Text);
    }

    private static int OrderOf(DeviceHandle device) => device.Info.Class switch
    {
        Core.Devices.DeviceClass.System => device.Key.StartsWith("mem", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
        Core.Devices.DeviceClass.Network => 2,
        Core.Devices.DeviceClass.Disk => 3,
        _ => 4,
    } * 100 + device.Index;

    private static CardSpec ToSpec(CardView card) => new(
        card.Model.Key, card.Model.Weight, card.Model.Priority, card.Model.UserCollapsed);

    private void OnCardToggled(CardView card)
    {
        card.Model.UserCollapsed = !card.Model.Collapsed;
        card.Model.AutoCollapsed = false;
        ApplyLayout(animate: true);
        MarkSettingsDirty();
    }

    // ── 레이아웃 ─────────────────────────────────────────────────
    /// <param name="availableOverride">
    /// 카드에 줄 높이를 직접 지정한다. 창 높이를 막 바꾼 직후에는 <c>CardHost.ActualHeight</c> 가
    /// 아직 옛 값이라, 그걸로 계산하면 이전 높이에 맞춰 배치해 마지막 카드가 잘린다.
    /// 목표 높이를 아는 호출자는 레이아웃 패스를 기다리지 말고 그 값을 넘긴다.
    /// </param>
    private void ApplyLayout(bool animate, double? availableOverride = null)
    {
        if (_cards.Count == 0) return;

        double available = availableOverride
            ?? CardHost.ActualHeight - CardHost.Margin.Top - CardHost.Margin.Bottom;
        if (available <= 0) available = AvailableFor(ActualHeight);

        var specs = _cards.Select(ToSpec).ToArray();
        var layout = LayoutEngine.Compute(specs, available);

        for (int i = 0; i < _cards.Count; i++)
        {
            var card = _cards[i];
            card.Model.AutoCollapsed = layout[i].AutoCollapsed;
            card.ApplyCollapsed(layout[i].Collapsed, animate);
            card.AnimateHeight(layout[i].Height, animate);
        }

        ApplyTheme();
        RefreshCards();
    }


    /// <summary>
    /// 장치 수가 바뀌면 창 높이도 다시 제안한다. 카드가 늘었는데 창이 그대로면
    /// 전부 접힌 채로 나타나고, 줄었는데 그대로면 빈 공간만 남는다.
    /// </summary>
    /// <summary>
    /// 지난 실행의 창 위치·크기를 되살린다. 없거나 쓸 수 없으면 장치 구성에서 계산한다.
    /// </summary>
    /// <remarks>
    /// 저장된 위치가 <b>지금 화면에 걸치는지 반드시 확인한다.</b> 모니터를 뽑았거나 배치가 바뀌면
    /// 창이 보이지 않는 좌표에 떠서, 사용자는 앱이 실행되지 않았다고 생각하게 된다.
    /// </remarks>
    private void RestorePlacement()
    {
        if (_settings.Window is not { IsValid: true } saved)
        {
            // 첫 실행 창 높이는 장치 구성에서 계산한다 — 고정값은 기기마다 맞지 않는다.
            ResuggestWindowHeight(recenter: true);
            return;
        }

        Left = saved.Left;
        Top = saved.Top;
        Width = saved.Width;
        Height = saved.Height;

        if (IsRestoredPlacementUsable()) return;

        ChronoLoad.Sensors.SensorLog.Write("저장된 창 위치를 쓸 수 없어 기본 위치로 되돌린다");
        ResuggestWindowHeight(recenter: true);
    }

    /// <summary>
    /// 복원한 자리가 지금 모니터 배치에서 실제로 쓸 수 있는지 본다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>창을 실제로 옮겨 놓고 Windows 에게 묻는다.</b> 저장된 DIP 좌표를 픽셀로 환산해
    /// 직접 계산하면 모니터마다 배율이 다를 때 어긋난다. 이미 배치된 창의 사각형을
    /// <c>GetWindowRect</c> 로 받으면 그 환산을 Windows 가 한 뒤의 값이라 틀릴 여지가 없다.
    /// </para>
    /// <para>
    /// 모서리만 걸친 것도 못 쓴다 — 제목 표시줄을 잡을 수 없으면 옮길 수도 닫을 수도 없다.
    /// </para>
    /// </remarks>
    private bool IsRestoredPlacementUsable()
    {
        nint handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == 0) return true;               // 아직 판단할 수 없다. 그대로 둔다.

        if (Monitors.IsOffAllMonitors(handle)) return false;
        if (Monitors.RectOf(handle) is not { } rect) return true;

        var areas = Monitors.WorkAreas();
        return areas.Count == 0 || AppSettings.IsOnScreen(rect, areas);
    }

    /// <summary>창을 옮기거나 카드를 접을 때마다 부른다. 실제 저장은 디바운스된다.</summary>
    private void MarkSettingsDirty()
    {
        _saveSettle.Stop();
        _saveSettle.Start();
    }

    private void SaveSettings()
    {
        // 최소화 상태의 좌표를 저장하면 다음 실행에서 창이 엉뚱한 곳에 뜬다.
        if (WindowState == WindowState.Normal && Width > 0 && Height > 0)
            _settings.Window = new WindowPlacement(Left, Top, Width, Height);

        _settings.Topmost = Topmost;

        // 자동 접힘(공간 부족)은 사용자의 선택이 아니므로 저장하지 않는다.
        foreach (var card in _cards) _settings.Collapsed[card.Model.Key] = card.Model.UserCollapsed;

        _settings.Save();
    }

    private void ResuggestWindowHeight(bool recenter = false)
    {
        if (_cards.Count == 0) return;

        var specs = _cards.Select(ToSpec).ToArray();
        var work = SystemParameters.WorkArea;
        double target = LayoutEngine.SuggestWindowHeight(specs, workAreaHeight: work.Height);

        if (recenter)
        {
            // 가로도 함께 되돌린다. 세로만 옮기면 쓸 수 없는 자리에서 복귀했을 때
            // 창이 여전히 화면 밖 가로 좌표에 남는다 — 실제로 그렇게 남겨뒀었다.
            Height = Math.Min(target, work.Height);
            Top = work.Top + Math.Max(0, (work.Height - Height) / 2);
            Left = work.Left + Math.Max(0, (work.Width - Width) / 2);
            return;
        }

        if (Math.Abs(Height - target) < 1) return;

        AnimateWindowHeight(target);
    }

    /// <summary>
    /// 크롬(제목 표시줄 + 요약 바)의 높이를 다시 잰다.
    /// </summary>
    /// <remarks>
    /// <b>레이아웃이 안정된 순간에만 불러야 한다.</b> 크롬 높이 자체는 창 크기와 무관하게 일정하지만,
    /// 창 높이 애니메이션 도중에는 <c>ActualHeight</c> 와 <c>CardHost.ActualHeight</c> 의 갱신 시점이
    /// 어긋나 역산값이 105 대신 77 이나 86 으로 튄다. 그 값으로 배치하면 카드가 넘쳐 마지막 카드가 잘린다.
    /// 그래서 아무 때나 재지 않고, 마지막으로 제대로 잰 값을 계속 쓴다.
    /// </remarks>
    private void RemeasureChrome()
    {
        double chrome = ActualHeight - CardHost.ActualHeight;
        if (chrome > 0) _chrome = chrome;
    }

    /// <summary>주어진 창 높이에서 카드들이 실제로 쓸 수 있는 높이.</summary>
    private double AvailableFor(double windowHeight) =>
        windowHeight - _chrome - CardHost.Margin.Top - CardHost.Margin.Bottom;

    /// <summary><see cref="AvailableFor"/> 의 역함수. 카드 높이 합에서 필요한 창 높이를 구한다.</summary>
    private double WindowHeightFor(double contentHeight) =>
        contentHeight + _chrome + CardHost.Margin.Top + CardHost.Margin.Bottom;

    private void AnimateWindowHeight(double target)
    {
        _suppressResizeLayout = true;
        var animation = new DoubleAnimation(target, new Duration(TimeSpan.FromMilliseconds(220)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        animation.Completed += (_, _) =>
        {
            BeginAnimation(HeightProperty, null);
            Height = target;
            _suppressResizeLayout = false;
            ApplyLayout(animate: false, availableOverride: AvailableFor(target));
        };
        BeginAnimation(HeightProperty, animation);
    }

    // ── 값 갱신 ─────────────────────────────────────────────────
    private void OnCommitted(SampleCommitted commit)
    {
        // 보이지 않는 창을 그리는 것은 순수한 낭비다. 샘플링은 계속 돌고 있으므로
        // 복원했을 때 히스토리에는 구멍이 없다 (§6.3).
        if (_visibility is { ShouldRender: false }) return;

        Dispatcher.InvokeAsync(RefreshCards, DispatcherPriority.Render);
    }

    /// <summary>
    /// 스크럽 상태가 바뀌면 <b>전 카드</b>에 같은 인덱스를 밀어 넣는다.
    /// 모든 카드가 동일한 시간 축을 쓰므로 인덱스 하나가 시스템 전체의 한 순간을 가리킨다.
    /// </summary>
    private void ApplyScrub()
    {
        var palette = _theme.Palette;
        foreach (var card in _cards)
            card.ApplyScrub(_scrub.Index, _scrub.FocusCardKey == card.Model.Key, palette);

        RefreshCards();
    }

    private void RefreshCards()
    {
        foreach (var card in _cards) card.Refresh(_registry);

        if (_cards.Count > 0)
        {
            var stats = _registry.StatsSnapshot(_cards[0].Model.PrimarySlot);
            ElapsedText.Text = MetricFormatter.Elapsed(stats.Elapsed(DateTime.UtcNow.Ticks));
        }

    }

    private void ResetStats()
    {
        // 화면의 리셋은 화면 스코프만 건드린다. MCP 쪽 기준점은 그대로 흘러간다.
        _registry.ResetAllStats(StatsScope.Ui, DateTime.UtcNow.Ticks);

        var spin = new DoubleAnimation(-360, new Duration(TimeSpan.FromMilliseconds(450)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        if (ResetButton.Content is FrameworkElement { RenderTransform: RotateTransform rotate })
            rotate.BeginAnimation(RotateTransform.AngleProperty, spin);

        RefreshCards();
    }


    // ── 크롬 ────────────────────────────────────────────────────
    private void BuildChromeIcons()
    {
        PinButton.Content = Glyph(Icons.Pin);
        ThemeButton.Content = Glyph(Icons.Theme);
        MinimizeButton.Content = Glyph(Icons.Minimize);
        CloseButton.Content = Glyph(Icons.Close);

        var reset = Glyph(Icons.Reset, 15);
        reset.RenderTransformOrigin = new Point(0.5, 0.5);
        reset.RenderTransform = new RotateTransform(0);
        ResetButton.Content = reset;
    }

    private static System.Windows.Shapes.Path Glyph(Geometry data, double size = 13)
    {
        double scale = size / Icons.DesignSize;
        return new System.Windows.Shapes.Path
        {
            Data = data,
            StrokeThickness = 1.6 / scale,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
            Width = Icons.DesignSize,
            Height = Icons.DesignSize,
            LayoutTransform = new ScaleTransform(scale, scale),
        };
    }



    /// <summary>
    /// 앱 마크와 그 메뉴. 창 아이콘을 누르면 메뉴가 열리는 것은 Windows 창의 오랜 관례라
    /// 따로 설명할 필요가 없다.
    /// </summary>
    private void BuildBrandMenu()
    {
        var menu = new ContextMenu();

        var about = new MenuItem { Header = "ChronoLoad 정보(_A)" };
        // 모달로 띄우지 않는다. 정보 창 하나가 안 보이는 자리에 떴다고 앱 전체가 잠기면 안 된다.
        about.Click += (_, _) => { var w = new AboutWindow(this); w.Show(); w.Activate(); };
        menu.Items.Add(about);

        menu.Items.Add(new Separator());

        var close = new MenuItem { Header = "닫기(_C)" };
        close.Click += (_, _) => Close();
        menu.Items.Add(close);

        // 왼쪽 클릭으로 연다. 오른쪽 클릭은 WPF 가 기본으로 처리한다.
        _brand.MouseLeftButtonUp += (_, e) =>
        {
            menu.PlacementTarget = _brand;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
            e.Handled = true;      // 클릭이 창 드래그로 넘어가지 않게 막는다
        };

        _brand.ContextMenu = menu;
        BrandHost.Content = _brand;
    }

    private void ApplyTheme()
    {
        var palette = _theme.Palette;

        _brand.ApplyTheme(palette);

        PinButton.Foreground = new SolidColorBrush(Topmost ? palette.Gpu : palette.Dim);
        foreach (var button in new[] { ThemeButton, MinimizeButton, CloseButton })
            if (button.Content is Shape shape) shape.Stroke = new SolidColorBrush(palette.Dim);
        if (PinButton.Content is Shape pin) pin.Stroke = new SolidColorBrush(Topmost ? palette.Gpu : palette.Dim);
        if (ResetButton.Content is Shape reset) reset.Stroke = new SolidColorBrush(palette.Fg);

        for (int i = 0; i < _cards.Count; i++)
        {
            _cards[i].ApplyTheme(palette);
        }
    }

    // ── 키보드 ──────────────────────────────────────────────────
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (ctrl && e.Key == Key.R) { ResetStats(); e.Handled = true; return; }

        if (e.Key == Key.Escape) { _scrub.Clear(); e.Handled = true; return; }

        if (e.Key is Key.Left or Key.Right && _scrub.IsPinned)
        {
            int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
            _scrub.Move(e.Key == Key.Left ? -step : step,
                _cards.Count > 0 ? _cards[0].WindowPoints : 240);
            e.Handled = true;
            return;
        }

        if (e.Key is >= Key.D1 and <= Key.D9)
        {
            int index = e.Key - Key.D1;
            if (!ctrl && index < _cards.Count)
            {
                OnCardToggled(_cards[index]);
            }
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _saveSettle.Stop();
        SaveSettings();

        if (_engine is not null) _engine.Committed -= OnCommitted;
        _visibility?.Dispose();
        _theme.Changed -= ApplyTheme;
        base.OnClosed(e);
    }
}

using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ChronoLoad.App.Rendering;
using ChronoLoad.App.Services;
using ChronoLoad.Core.Devices;
using ChronoLoad.Core.Formatting;
using ChronoLoad.Core.Layout;
using ChronoLoad.Core.Metrics;

namespace ChronoLoad.App.Controls;

/// <summary>
/// 스냅샷 창 (§9.6). 흐름을 멈추고 구간을 잰다.
/// </summary>
/// <remarks>
/// <para>
/// 모달리스이고 여러 개 열 수 있다 — 테스트 A·B 를 나란히 놓고 비교하는 것이 주 용도다.
/// 그래서 제목이 <b>데이터 시작 시각</b>이다. 창이 여럿일 때 무엇이 언제 남긴 것인지는
/// 거기서만 갈린다.
/// </para>
/// <para>
/// 저장하지 않는다. 앱을 닫으면 사라지고, 남기려면 CSV 로 낸다.
/// </para>
/// </remarks>
public sealed class SnapshotWindow : Window
{
    /// <summary>주 판의 높이. 값을 읽는 창이라 곁눈질용 메인 창(약 72)보다 넉넉하다.</summary>
    private const double ChartHeight = 116;
    /// <summary>홈통 폭. "30.9GB" 가 잘리지 않을 만큼은 있어야 한다 — 잘린 단위는 없느니만 못하다.</summary>
    private const double GutterWidth = 44;
    /// <summary>요약 띠 높이. 줄 전체가 접기 버튼이므로 손이 닿을 만큼은 있어야 한다.</summary>
    private const double StripHeight = 24;
    private const int MinViewPoints = 8;

    private readonly MetricSnapshot _snapshot;
    private readonly ThemePalette _palette;
    private readonly long _gapThreshold;

    /// <summary>크롭 스택. 맨 위가 지금 보는 분석 도메인이고, 바닥이 떠낸 전체다.</summary>
    private readonly List<(int From, int To)> _crops = [];

    private readonly List<Row> _rows = [];
    private readonly StackPanel _host = new();
    private readonly Border _fitButton, _cropButton, _undoButton, _exportButton;
    private readonly Canvas _ruler = new() { Height = 14 };
    private readonly Border _scrollThumb = new();
    private readonly Border _scrollTrack = new();
    private readonly TextBlock _rangeStart = new(), _rangeEnd = new(), _zoomText = new();
    private readonly TextBlock _instant = new();

    private int _viewStart, _viewCount;
    private int? _selectA, _selectB;
    private bool _dragging;

    /// <summary>닫히는 시점에 이 창이 활성이었는가. 그때만 소유 창으로 활성을 돌려준다.</summary>
    private bool _activeWhenClosing;

    public SnapshotWindow(Window owner, MetricSnapshot snapshot, long gapThresholdTicks)
    {
        _snapshot = snapshot;
        _gapThreshold = gapThresholdTicks;
        _crops.Add((0, snapshot.Count));
        (_viewStart, _viewCount) = Domain;

        var palette = _palette = ThemeService.Instance.Palette;

        Owner = owner;
        // 창이 여럿일 때 무엇이 언제 남긴 것인지는 제목에서만 갈린다(§9.6).
        Title = $"ChronoLoad — {snapshot.StartedLocal:HH:mm:ss} 부터 "
              + $"{Describe(snapshot.Span)} · {snapshot.Count:N0} 샘플";
        Width = 900;
        // GPU 는 판을 둘 쓰므로(§9.6) 620 이면 마지막 장치가 잘린 채로 열린다. 그렇다고
        // 고정으로 키우면 작업 영역이 낮은 노트북에서 창이 화면 밖으로 나간다 — 씌운다.
        Height = Math.Min(860, SystemParameters.WorkArea.Height * 0.9);
        MinWidth = 520;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = true;
        Background = new SolidColorBrush(palette.Bg);
        Topmost = owner.Topmost;

        var root = new DockPanel { LastChildFill = true };

        var toolbar = Toolbar(palette, out _fitButton, out _cropButton, out _undoButton, out _exportButton);
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        var axis = Axis(palette);
        DockPanel.SetDock(axis, Dock.Bottom);
        root.Children.Add(axis);

        _host.Margin = new Thickness(10, 8, 10, 4);
        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _host,
            Background = Brushes.Transparent,
        });

        Content = root;
        BuildRows(palette);
        PreviewMouseWheel += OnWheel;
        PreviewKeyDown += OnKey;
        Refresh();
    }

    /// <summary>
    /// 제목 표시줄은 OS 가 그린다. 핸들이 생긴 뒤라야 색을 알려 줄 수 있다.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 캡션 바로 아래는 이제 툴바다. 두 줄이 한 덩어리로 읽히도록 색을 맞춘다.
        WindowFrame.Apply(this, _palette, _palette.Surface2,
                          ReferenceEquals(_palette, ThemePalette.Dark));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        _activeWhenClosing = IsActive;
    }

    /// <summary>
    /// 활성을 소유 창에 돌려준다.
    /// </summary>
    /// <remarks>
    /// 저장 대화상자를 한 번 띄우고 나서 이 창을 닫으면 메인 창이 다른 앱 뒤로 가라앉는다는
    /// 보고가 있었다. 창 하나짜리 재현 하네스로는 재현되지 않아 정확한 경로는 못 짚었지만,
    /// 이 창이 활성인 채로 닫혔으면 활성이 소유 창으로 가는 것이 어차피 맞는 동작이다.
    /// 활성이 아니었으면 건드리지 않는다 — 다른 앱을 쓰던 사람의 앞창을 빼앗게 된다.
    /// </remarks>
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_activeWhenClosing && Owner is { IsLoaded: true } owner) owner.Activate();
    }

    /// <summary>지금 분석 도메인. 요약도 내보내기도 이 범위만 본다.</summary>
    private (int From, int To) Domain => _crops[^1];

    private (int From, int To)? Selection =>
        _selectA is { } a && _selectB is { } b && Math.Abs(a - b) >= 1
            ? (Math.Min(a, b), Math.Max(a, b) + 1)
            : null;

    /// <summary>
    /// 끌지 않고 한 번 누른 자리. 구간이 아니라 <b>순간</b>이라 통계가 아니라 그때의 값을 읽는다
    /// — 메인 창의 스크럽 고정(§8.7)과 같은 동작이다.
    /// </summary>
    private int? Scrub =>
        _selectA is { } a && _selectB == a ? a : null;

    // ── 뼈대 ─────────────────────────────────────────────────

    private Border Toolbar(ThemePalette palette, out Border fit, out Border crop,
                           out Border undo, out Border export)
    {
        var bar = new DockPanel { Height = 38, LastChildFill = false };

        fit = IconButton(Icons.FitWidth, "전체 — 크롭을 풀고 전체 구간을 폭에 맞춘다", palette, ResetCrop);
        crop = IconButton(Icons.Crop, "선택 구간으로 크롭", palette, ApplyCrop);
        undo = IconButton(Icons.Undo, "되돌리기 — 직전 크롭을 취소한다", palette, UndoCrop);
        export = IconButton(Icons.Export, "CSV 내보내기", palette, Export);

        foreach (var button in new[] { fit, crop, undo })
        {
            DockPanel.SetDock(button, Dock.Left);
            bar.Children.Add(button);
        }
        DockPanel.SetDock(export, Dock.Right);
        bar.Children.Add(export);

        return new Border
        {
            Child = bar,
            Background = new SolidColorBrush(palette.Surface2),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = new SolidColorBrush(palette.Line),
            Padding = new Thickness(8, 0, 8, 0),
        };
    }

    private UIElement Axis(ThemePalette palette)
    {
        var stack = new StackPanel { Margin = new Thickness(10, 0, 10, 8) };

        // 눈금자는 차트와 같은 홈통만큼 들여 맞춘다.
        // 처음 만들 때는 폭이 0 이라 라벨을 놓을 자리가 없다. 배치가 끝난 뒤 다시 그린다.
        _ruler.SizeChanged += (_, _) => UpdateRuler(TimeTicks());
        var rulerHost = new DockPanel { Margin = new Thickness(GutterWidth + 5, 0, 0, 2) };
        rulerHost.Children.Add(_ruler);
        stack.Children.Add(rulerHost);

        var row = new DockPanel { Height = 20, LastChildFill = true };
        AsAxisLabel(_rangeStart, palette.Dim);
        AsAxisLabel(_rangeEnd, palette.Dim);
        AsAxisLabel(_zoomText, palette.Fg);

        DockPanel.SetDock(_rangeStart, Dock.Left);
        row.Children.Add(_rangeStart);

        AsAxisLabel(_instant, palette.Fg);
        _instant.Margin = new Thickness(10, 0, 0, 0);
        _instant.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(_instant, Dock.Left);
        row.Children.Add(_instant);

        var zoom = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        zoom.Children.Add(IconButton(Icons.ZoomOut, "축소", palette, () => Zoom(+1, 0.5), 22));
        _zoomText.Margin = new Thickness(5, 0, 5, 0);
        zoom.Children.Add(_zoomText);
        zoom.Children.Add(IconButton(Icons.ZoomIn, "확대 — Ctrl + 휠", palette, () => Zoom(-1, 0.5), 22));
        DockPanel.SetDock(zoom, Dock.Right);
        row.Children.Add(zoom);

        _rangeEnd.Margin = new Thickness(8, 0, 10, 0);
        DockPanel.SetDock(_rangeEnd, Dock.Right);
        row.Children.Add(_rangeEnd);

        _scrollTrack.Height = 8;
        _scrollTrack.CornerRadius = new CornerRadius(4);
        _scrollTrack.Background = new SolidColorBrush(palette.Surface2);
        _scrollTrack.BorderThickness = new Thickness(1);
        _scrollTrack.BorderBrush = new SolidColorBrush(palette.Line);
        _scrollTrack.Margin = new Thickness(8, 0, 0, 0);
        _scrollTrack.VerticalAlignment = VerticalAlignment.Center;

        _scrollThumb.CornerRadius = new CornerRadius(4);
        _scrollThumb.Background = new SolidColorBrush(palette.Faint);
        _scrollThumb.HorizontalAlignment = HorizontalAlignment.Left;
        var thumbHost = new Canvas { ClipToBounds = true, Height = 8 };
        thumbHost.Children.Add(_scrollThumb);
        _scrollTrack.Child = thumbHost;
        _scrollTrack.SizeChanged += (_, _) => LayoutThumb();
        row.Children.Add(_scrollTrack);

        stack.Children.Add(row);
        return stack;
    }

    // ── 행 ───────────────────────────────────────────────────

    private void BuildRows(ThemePalette palette)
    {
        foreach (var group in _snapshot.Metrics.GroupBy(m => m.Device.Key))
        {
            var metrics = group.ToArray();
            var (primary, secondary, subPrimary, subSecondary, subName) = Pick(metrics);
            if (primary is null) continue;

            var row = new Row(this, primary, secondary, subPrimary, subSecondary, subName,
                              palette, _snapshot, _gapThreshold);
            _rows.Add(row);
            _host.Children.Add(row.Panel);
        }
    }

    /// <summary>
    /// 카드(§8.2)와 같은 선택이다 — 장치당 하나, 짝이 있으면 겹쳐 그린다.
    /// <b>GPU 만 판을 하나 더</b> 받는다.
    /// </summary>
    /// <remarks>
    /// 사용률은 퍼센트이고 메모리는 바이트다. 한 그림에 겹치면 둘 중 하나는 읽을 수 없는데,
    /// 스냅샷을 여는 이유가 대개 "그 구간에 VRAM 이 얼마나 찼나" 라 빼 둘 수도 없다.
    /// 그렇다고 장치를 둘로 쪼개면 한 GPU 의 이야기가 카드 두 장으로 갈린다 — 판만 더 붙인다.
    /// </remarks>
    private static (SnapshotMetric? Primary, SnapshotMetric? Secondary,
                    SnapshotMetric? SubPrimary, SnapshotMetric? SubSecondary, string SubName)
        Pick(SnapshotMetric[] metrics)
    {
        SnapshotMetric? Find(MetricKind kind) => metrics.FirstOrDefault(m => m.Kind == kind);

        return metrics[0].Device.Class switch
        {
            DeviceClass.Network => (Find(MetricKind.NetRx), Find(MetricKind.NetTx), null, null, ""),
            DeviceClass.Disk => (Find(MetricKind.DiskRead), Find(MetricKind.DiskWrite), null, null, ""),
            DeviceClass.Gpu => (Find(MetricKind.GpuUtil), Find(MetricKind.GpuCompute),
                                Find(MetricKind.GpuDedicated), Find(MetricKind.GpuShared), "메모리"),
            _ => (Find(MetricKind.CpuTotal) ?? Find(MetricKind.MemUsed), Find(MetricKind.MemCommit),
                  null, null, ""),
        };
    }

    // ── 갱신 ─────────────────────────────────────────────────

    private void Refresh()
    {
        var (from, to) = Domain;
        _viewCount = Math.Clamp(_viewCount, Math.Min(MinViewPoints, to - from), to - from);
        _viewStart = Math.Clamp(_viewStart, from, Math.Max(from, to - _viewCount));

        var ticks = TimeTicks();
        var range = Selection ?? Domain;

        int? scrub = Scrub;
        foreach (var row in _rows)
            row.Update(_viewStart, _viewCount, Selection is null ? null : _selectA,
                       Selection is null ? null : _selectB, scrub, ticks, range);

        UpdateRuler(ticks);
        LayoutThumb();

        _rangeStart.Text = Local(_viewStart).ToString("HH:mm:ss");
        _rangeEnd.Text = Local(Math.Min(_viewStart + _viewCount, _snapshot.Count) - 1).ToString("HH:mm:ss");

        // 시각은 한 곳에만 적는다. 행마다 되풀이하면 같은 값이 여덟 번 보인다.
        // 자리는 하나고, 끈 것이면 구간을 집은 것이면 순간을 적는다 — 둘은 같이 나올 수 없다.
        _instant.Text = Selection is { } picked
            ? $"{Local(picked.From):HH:mm:ss} ~ {Local(picked.To - 1):HH:mm:ss} · " +
              Elapsed(Local(picked.To - 1) - Local(picked.From))
            : scrub is { } at ? Local(at).ToString("HH:mm:ss.fff") : "";
        _instant.Visibility = _instant.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _zoomText.Text = $"{(double)(to - from) / Math.Max(1, _viewCount):0.#}×";

        Enable(_cropButton, Selection is not null);
        Enable(_undoButton, _crops.Count > 1);
        Enable(_fitButton, _crops.Count > 1 || _viewCount < to - from);
    }

    private DateTime Local(int index) =>
        _snapshot.Count == 0 ? _snapshot.CapturedUtc.ToLocalTime()
        : new DateTime(_snapshot.Timestamps[Math.Clamp(index, 0, _snapshot.Count - 1)], DateTimeKind.Utc)
            .ToLocalTime();

    /// <summary>
    /// 세로 격자 자리(0~1). <b>어림수 시각</b>에 놓는다 — 데이터 시작부터 등간격으로 그으면
    /// 14:35:07 같은 눈금이 되어 그 숫자로는 아무것도 셈할 수 없다.
    /// </summary>
    private List<(double Fraction, DateTime Time)> TimeTicks()
    {
        var result = new List<(double, DateTime)>();
        if (_viewCount < 2) return result;

        var start = Local(_viewStart);
        var end = Local(_viewStart + _viewCount - 1);
        double seconds = (end - start).TotalSeconds;
        if (seconds <= 0) return result;

        // 화면에 4~8개가 들어가는 가장 큰 눈금을 고른다.
        double[] steps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];
        double step = steps.FirstOrDefault(s => seconds / s <= 8, steps[^1]);

        var first = start.AddTicks(-(start.Ticks % (long)(step * TimeSpan.TicksPerSecond)))
                         .AddSeconds(step);
        for (var t = first; t <= end; t = t.AddSeconds(step))
            result.Add(((t - start).TotalSeconds / seconds, t));

        return result;
    }

    private void UpdateRuler(List<(double Fraction, DateTime Time)> ticks)
    {
        _ruler.Children.Clear();
        double width = _ruler.ActualWidth;
        if (width < 20) return;

        var palette = ThemeService.Instance.Palette;
        foreach (var (fraction, time) in ticks)
        {
            var label = Mono(time.ToString(time.Second == 0 ? "HH:mm" : "HH:mm:ss"), 9, palette.Faint);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, width * fraction - label.DesiredSize.Width / 2);
            _ruler.Children.Add(label);
        }
    }

    private void LayoutThumb()
    {
        var (from, to) = Domain;
        int total = Math.Max(1, to - from);
        double track = Math.Max(0, _scrollTrack.ActualWidth - 2);

        _scrollThumb.Width = Math.Max(16, track * _viewCount / total);
        _scrollThumb.Height = 8;
        Canvas.SetLeft(_scrollThumb, (track - _scrollThumb.Width) * (total == _viewCount ? 0
            : (double)(_viewStart - from) / (total - _viewCount)));
    }

    // ── 조작 ─────────────────────────────────────────────────

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0) return;

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            // 커서가 가리키는 시각을 붙든 채 배율만 바꾼다.
            var chart = _rows.Select(r => r.Hovered).FirstOrDefault(c => c is not null);
            double anchor = chart is null ? 0.5
                : Math.Clamp(e.GetPosition(chart).X / Math.Max(1, chart.ActualWidth), 0, 1);
            Zoom(e.Delta > 0 ? -1 : +1, anchor);
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            e.Handled = true;
            Pan(e.Delta > 0 ? -Math.Max(1, _viewCount / 8) : Math.Max(1, _viewCount / 8));
        }
        // 맨 휠은 ScrollViewer 가 세로로 쓴다.
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        _selectA = _selectB = null;
        Refresh();
    }

    /// <param name="direction">양수는 축소(더 넓게), 음수는 확대.</param>
    private void Zoom(int direction, double anchor)
    {
        var (from, to) = Domain;
        int total = to - from;
        int next = (int)Math.Round(_viewCount * (direction > 0 ? 1.6 : 1 / 1.6));
        next = Math.Clamp(next, Math.Min(MinViewPoints, total), total);
        if (next == _viewCount) return;

        double focus = _viewStart + anchor * _viewCount;
        _viewCount = next;
        _viewStart = (int)Math.Round(focus - anchor * next);
        Refresh();
    }

    private void Pan(int delta)
    {
        _viewStart += delta;
        Refresh();
    }

    private void ApplyCrop()
    {
        if (Selection is not { } selection) return;
        _crops.Add(selection);
        _selectA = _selectB = null;
        (_viewStart, _viewCount) = (selection.From, selection.To - selection.From);
        Refresh();
    }

    /// <summary>
    /// 자르되 버리지 않는다. 배열은 그대로 두고 경계만 좁히므로 언제든 돌아올 수 있다 —
    /// 되돌릴 수 없으면 사용자는 자르기를 아예 피하게 되고, 그러면 기능이 없는 것과 같다.
    /// </summary>
    private void UndoCrop()
    {
        if (_crops.Count <= 1) return;
        _crops.RemoveAt(_crops.Count - 1);
        (_viewStart, _viewCount) = Domain;
        Refresh();
    }

    private void ResetCrop()
    {
        _crops.RemoveRange(1, _crops.Count - 1);
        (_viewStart, _viewCount) = Domain;
        _selectA = _selectB = null;
        Refresh();
    }

    /// <summary>
    /// 선택이 있으면 <b>저장 대화상자 안의 체크 상자</b>로 어느 쪽을 낼지 고른다.
    /// </summary>
    /// <remarks>
    /// 요약 띠는 선택을 따라가는데 파일은 늘 크롭 구간이 나가면, 화면에서 읽은 숫자와 파일 속
    /// 숫자가 말없이 달라진다. 그렇다고 저장 전에 확인 창을 하나 더 띄우면 선택이 있을 때마다
    /// 누를 것이 는다 — 고르는 자리는 어차피 열리는 대화상자 안에 있으면 된다.
    /// </remarks>
    private void Export()
    {
        // 선택이 크롭 구간과 같으면 고를 것이 없다.
        (int From, int To)? selection = Selection is { } picked && picked != Domain ? picked : null;
        (int From, int To) preferred = selection ?? Domain;

        string suggested = SnapshotCsv.SuggestFileName(_snapshot, preferred.From, preferred.To);
        var answer = SaveDialog.Show(this, suggested, "CSV 파일", ".csv",
            selection is { } option ? ("내보낼 범위", $"선택 구간만 ({Duration(option)})", true) : null);
        // 대화상자가 남긴 활성을 이 창으로 되돌린다. 여기서 흐트러지면 나중에 이 창을 닫을 때
        // 활성이 엉뚱한 곳으로 간다.
        Activate();
        if (answer is not { } result) return;

        var (from, to) = result.Checked ? preferred : Domain;
        string path = result.Path;

        // 이름을 건드리지 않은 채 체크만 풀었으면 이름도 따라간다 — 그러지 않으면 파일명 끝의
        // 길이가 내용과 어긋난 채 남는다. 손댄 이름은 그대로 존중한다.
        if (Path.GetFileName(path) == suggested && (from, to) != preferred)
            path = Path.Combine(Path.GetDirectoryName(path) ?? "",
                                SnapshotCsv.SuggestFileName(_snapshot, from, to));

        try
        {
            SnapshotCsv.Save(_snapshot, from, to, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"저장하지 못했다.\n{ex.Message}", "ChronoLoad",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 구간의 길이. 예 <c>94초</c>. 시각까지 적으면 셸이 글씨를 잘라 버린다 —
    /// 시작 시각은 어차피 기본 파일명이 들고 있다.
    /// </summary>
    private string Duration((int From, int To) range) =>
        Describe(Local(range.To - 1) - Local(range.From));

    /// <param name="extend">
    /// <c>Shift</c> 를 누른 채 눌렀는가. 그러면 <b>이미 잡아 둔 자리를 한 끝으로 삼는다.</b>
    /// 긴 구간일수록 끝까지 끄는 동안 손이 흔들리고, 한 번 놓치면 처음부터 다시 끌어야 한다 —
    /// 한 번 찍고 반대쪽을 <c>Shift</c>+클릭하면 길이와 무관하게 두 번이면 끝난다.
    /// 이미 구간이 잡혀 있으면 끝을 옮긴다(목록·텍스트 선택과 같은 관습이다).
    /// </param>
    internal void BeginSelect(SnapshotChart chart, double x, bool extend = false)
    {
        SelectAt(Clamp(chart.IndexAt(x)), extend);

        // 이어서 끌면 그 끝이 따라온다 — Shift+클릭으로 대강 잡고 끌어서 다듬을 수 있다.
        _dragging = true;
    }

    /// <summary>좌표를 뗀 선택. 렌더 테스트도 이리로 들어온다.</summary>
    internal void SelectAt(int index, bool extend)
    {
        if (extend && _selectA is not null) _selectB = index;
        else _selectA = _selectB = index;
        Refresh();
    }

    internal void DragSelect(SnapshotChart chart, double x)
    {
        if (!_dragging) return;
        _selectB = Clamp(chart.IndexAt(x));
        Refresh();
    }

    internal void EndSelect() => _dragging = false;

    /// <summary>렌더 테스트 전용 — 드래그·클릭 없이 상태를 세운다.</summary>
    internal void CropNow() => ApplyCrop();

    /// <inheritdoc cref="CropNow"/>
    internal void Preselect(int from, int to)
    {
        _selectA = Clamp(from);
        _selectB = Clamp(to);
        Refresh();
    }

    internal void ToggleRow(Row row)
    {
        row.Collapsed = !row.Collapsed;
        Refresh();
    }

    private int Clamp(int index)
    {
        var (from, to) = Domain;
        return Math.Clamp(index, from, Math.Max(from, to - 1));
    }

    // ── 작은 조립기 ───────────────────────────────────────────

    /// <summary>
    /// 끈 구간의 길이. <see cref="Describe"/> 보다 한 자리 더 적는다 — 250ms 로 재는데
    /// <c>13초</c> 로 뭉개면 무엇을 골랐는지 흐려진다.
    /// </summary>
    private static string Elapsed(TimeSpan span) =>
        span.TotalSeconds < 60 ? $"{span.TotalSeconds:0.0}초"
        : span.TotalMinutes < 60 ? $"{span.TotalMinutes:0.0}분"
        : $"{span.TotalHours:0.0}시간";

    /// <summary>
    /// 오버레이에 적는 계열 이름. 짝이 있는 지표만 이름이 필요하다 —
    /// 계열이 하나뿐이면 무엇인지는 장치 이름이 이미 말한다.
    /// </summary>
    private static string SeriesName(MetricKind kind) => kind switch
    {
        MetricKind.GpuDedicated => "전용",
        MetricKind.GpuShared => "공유",
        MetricKind.GpuCompute => "Compute",
        MetricKind.NetRx => "수신",
        MetricKind.NetTx => "송신",
        MetricKind.DiskRead => "읽기",
        MetricKind.DiskWrite => "쓰기",
        MetricKind.MemCommit => "커밋",
        MetricKind.MemUsed => "사용",
        _ => "",
    };

    private static string Describe(TimeSpan span) =>
        span.TotalSeconds < 60 ? $"{span.TotalSeconds:0}초"
        : span.TotalMinutes < 60 ? $"{span.TotalMinutes:0}분"
        : $"{span.TotalHours:0.#}시간";

    private static TextBlock Text(string text, double size, Color color, FontWeight weight,
                                  Thickness margin) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        Foreground = Frozen(color),
        Margin = margin,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBlock Mono(string text, double size, Color color) => new()
    {
        Text = text,
        FontSize = size,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        Foreground = Frozen(color),
        VerticalAlignment = VerticalAlignment.Center,
    };

    // FrameworkElement.Style 을 가리지 않도록 이름을 달리한다(CS0108).
    private static void AsAxisLabel(TextBlock block, Color color)
    {
        block.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        block.FontSize = 10.5;
        block.Foreground = Frozen(color);
        block.VerticalAlignment = VerticalAlignment.Center;
    }

    /// <summary>
    /// 할 수 없는 버튼은 숨기지 않고 흐리게 둔다. 사라졌다 나타나면 툴바의 자리가 움직여
    /// 근육 기억이 무너지고, 흐린 버튼은 "지금은 안 되지만 언젠가 되는 것"을 말한다.
    /// </summary>
    private static void Enable(Border button, bool on)
    {
        button.Opacity = on ? 1 : 0.35;
        button.IsHitTestVisible = on;
    }

    private static Border IconButton(Geometry glyph, string tip, ThemePalette palette,
                                     Action click, double size = 26)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = glyph,
            Stroke = Frozen(palette.Fg),
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
            Width = Icons.DesignSize,
            Height = Icons.DesignSize,
            LayoutTransform = new ScaleTransform(14 / Icons.DesignSize, 14 / Icons.DesignSize),
        };

        var button = new Border
        {
            Width = size,
            Height = size - 4,
            CornerRadius = new CornerRadius(6),
            Background = Frozen(palette.Surface),
            BorderThickness = new Thickness(1),
            BorderBrush = Frozen(palette.Line),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = tip,
            Child = path,
        };
        button.MouseLeftButtonUp += (_, _) => click();
        return button;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 차트 한 판 — 그림과 왼쪽 홈통, 그리고 그 위에 뜨는 오버레이.
    /// </summary>
    /// <remarks>
    /// 한 장치가 판을 둘 가질 수 있다. GPU 는 사용률(%)과 메모리(바이트)의 <b>축이 다르다</b> —
    /// 한 그림에 겹치면 둘 중 하나는 읽을 수 없다. 그렇다고 장치를 둘로 쪼개면 "이 GPU 의
    /// 이야기"가 카드 두 장으로 갈린다. 판만 하나 더 붙인다.
    /// </remarks>
    internal sealed class Plot
    {
        private readonly SnapshotMetric _primary;
        private readonly SnapshotMetric? _secondary;
        private readonly ThemePalette _palette;
        private readonly Color _accent;
        private readonly TextBlock _top, _mid, _bottom;
        private readonly Border _overlay;
        private readonly StackPanel _overlayRows;

        /// <summary>메모리 판인가. 축 규칙과 용량선이 다르다(§8.3).</summary>
        private readonly bool _memory;
        private readonly bool _discrete;
        private readonly double _dedicatedCapacity, _sharedCapacity;

        public SnapshotChart Chart { get; }
        public Grid Body { get; }

        public Plot(SnapshotWindow window, SnapshotMetric primary, SnapshotMetric? secondary,
                    ThemePalette palette, Color accent, MetricSnapshot snapshot,
                    long gapThreshold, double height)
        {
            _primary = primary;
            _secondary = secondary;
            _palette = palette;
            _accent = accent;

            // 용량은 시계열이 아니라 장치의 부가 정보로 온다 — 스냅샷이 장치 정보를 통째로
            // 들고 있으므로 따로 실어 나를 것이 없다(`CardFactory` 와 같은 키를 읽는다).
            _memory = primary.Kind == MetricKind.GpuDedicated;
            var extra = primary.Device.Extra;
            _discrete = extra.GetValueOrDefault("discrete") == "true";
            _dedicatedCapacity = Bytes(extra.GetValueOrDefault("dedicatedBytes"));
            _sharedCapacity = Bytes(extra.GetValueOrDefault("sharedBytes"));

            Chart = new SnapshotChart
            {
                Height = height,
                Palette = palette,
                Accent = accent,
                Timestamps = snapshot.Timestamps,
                GapThresholdTicks = gapThreshold,
                Metric = primary,
                Secondary = secondary,
                FixedMax = 100,
                Stacked = _memory,
                Discrete = _discrete,
                CapacityLine = _memory ? _dedicatedCapacity : 0,
            };
            Chart.MouseLeftButtonDown += (_, e) =>
            {
                Chart.CaptureMouse();
                window.BeginSelect(Chart, e.GetPosition(Chart).X,
                                   (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
            };
            Chart.MouseMove += (_, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                    window.DragSelect(Chart, e.GetPosition(Chart).X);
            };
            Chart.MouseLeftButtonUp += (_, _) => { Chart.ReleaseMouseCapture(); window.EndSelect(); };

            _top = Mono("", 8.5, palette.Faint);
            _mid = Mono("", 8.5, palette.Faint);
            _bottom = Mono("", 8.5, palette.Faint);
            foreach (var label in new[] { _top, _mid, _bottom }) label.TextAlignment = TextAlignment.Right;

            var gutter = new Grid { Width = GutterWidth, Margin = new Thickness(0, 0, 5, 0) };
            gutter.RowDefinitions.Add(new RowDefinition());
            gutter.RowDefinitions.Add(new RowDefinition());
            gutter.RowDefinitions.Add(new RowDefinition());
            _top.VerticalAlignment = VerticalAlignment.Top;
            _mid.VerticalAlignment = VerticalAlignment.Center;
            _bottom.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetRow(_mid, 1);
            Grid.SetRow(_bottom, 2);
            gutter.Children.Add(_top);
            gutter.Children.Add(_mid);
            gutter.Children.Add(_bottom);

            // 오버레이는 차트 위에 떠 있어야 하므로 같은 칸에 겹쳐 놓는다.
            _overlayRows = new StackPanel();
            _overlay = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 3, 7, 4),
                BorderThickness = new Thickness(1),
                Background = Frozen(Color.FromArgb(0xEC, palette.Surface2.R, palette.Surface2.G, palette.Surface2.B)),
                BorderBrush = Frozen(palette.Line),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                Child = _overlayRows,
            };

            var plot = new Grid();
            plot.Children.Add(Chart);
            plot.Children.Add(_overlay);

            Body = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            Body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(plot, 1);
            Body.Children.Add(gutter);
            Body.Children.Add(plot);
        }

        /// <summary>
        /// 구간 통계 한 줄.
        /// </summary>
        /// <param name="both">
        /// 짝까지 적는가. 요약 띠는 장치명과 <c>선택 구간</c> 칩이 이미 자리를 쓰므로 주 계열만
        /// 적고, 곁들이 판의 이름표는 자리가 남으므로 <b>전용과 공유를 나란히</b> 적는다 —
        /// 스냅샷에서 보려는 것이 대개 "전용이 얼마나 찼고 공유로 얼마나 샜나" 다.
        /// </param>
        public string Summarize(int from, int to, bool both)
        {
            string text = Stat(_primary, from, to, both);
            return both && _secondary is not null
                ? text + "    " + Stat(_secondary, from, to, true)
                : text;
        }

        /// <summary>표본이 없으면 <c>0</c> 이 아니라 <c>—</c> 다. 없는 것과 0 은 다르다.</summary>
        private static string Stat(SnapshotMetric metric, int from, int to, bool named)
        {
            string head = named && SeriesName(metric.Kind) is { Length: > 0 } name ? name + " " : "";
            var stats = metric.StatsOf(from, to);
            if (!stats.HasValue) return head + "—";

            var (values, symbol) = MetricFormatter.FormatGroup(metric.Unit, stats.Mean, stats.Min, stats.Max);
            string gap = named ? "  " : "   ";
            return $"{head}평균 {values[0]}{symbol}{gap}최소 {values[1]}{gap}최대 {values[2]}";
        }

        public void Update(int viewStart, int viewCount, int? selectA, int? selectB, int? scrub,
                           List<(double Fraction, DateTime Time)> ticks)
        {
            // 축 상한은 여기서 정해 차트에 넘긴다. 차트가 그린 뒤에 읽으면 라벨이 한 프레임
            // 늦어, 처음 뜰 때 "0KB" 같은 값이 남는다 — 실제로 그랬다.
            double max = Resolve(viewStart, viewCount);
            Chart.FixedMax = max;
            Chart.ViewStart = viewStart;
            Chart.ViewCount = viewCount;
            Chart.SelectionStart = selectA;
            Chart.SelectionEnd = selectB;
            Chart.ScrubIndex = scrub;
            Chart.TimeTicks = [.. ticks.Select(t => t.Fraction)];
            Chart.Invalidate();

            _top.Text = Axis(max, _primary.Unit);
            _mid.Text = Axis(max / 2, _primary.Unit);
            _bottom.Text = "0";

            ShowOverlay(scrub, viewStart, viewCount);
        }

        /// <summary>집은 순간의 값을 차트 위에 띄운다. 시각은 아래 축에 한 번만 적으므로 여기엔 값만 둔다.</summary>
        private void ShowOverlay(int? scrub, int viewStart, int viewCount)
        {
            if (scrub is not { } index || index < viewStart || index >= viewStart + viewCount)
            {
                _overlay.Visibility = Visibility.Collapsed;
                return;
            }

            _overlayRows.Children.Clear();
            _overlayRows.Children.Add(Line(_primary, index, _accent, 1));
            if (_secondary is not null) _overlayRows.Children.Add(Line(_secondary, index, _accent, 0.75));

            _overlay.Visibility = Visibility.Visible;

            // 패널 폭은 <b>내용</b>에서 잰다. 테두리를 두른 쪽을 재면 지난번에 밀어둔 여백이
            // 이번 폭에 섞여 플립 조건이 매번 뒤집힌다 — 메인 창에서 실제로 겪은 일이다.
            _overlayRows.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double width = _overlayRows.DesiredSize.Width
                           + _overlay.Padding.Left + _overlay.Padding.Right
                           + _overlay.BorderThickness.Left + _overlay.BorderThickness.Right;

            double chart = Chart.ActualWidth;
            double x = Chart.XOf(index);
            double left = x + 8 + width > chart ? x - 8 - width : x + 8;
            _overlay.Margin = new Thickness(Math.Clamp(left, 2, Math.Max(2, chart - width - 2)), 3, 0, 0);
        }

        private StackPanel Line(SnapshotMetric metric, int index, Color color, double opacity)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Border
            {
                Width = 6, Height = 6,
                CornerRadius = new CornerRadius(1.5),
                Background = new SolidColorBrush(color) { Opacity = opacity },
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });

            // 색이 같고 진하기만 다른 두 선이라, 이름이 없으면 어느 쪽이 전용이고 어느 쪽이
            // 공유인지 그림만 보고는 알 수 없다. 값보다 이름이 먼저 온다.
            if (SeriesName(metric.Kind) is { Length: > 0 } name)
            {
                var label = Mono(name, 11, _palette.Dim);
                label.Margin = new Thickness(0, 0, 6, 0);
                row.Children.Add(label);
            }

            float v = metric.Values[index];
            var formatted = MetricFormatter.Format(metric.Unit, float.IsNaN(v) ? 0 : v);
            row.Children.Add(Mono(float.IsNaN(v) ? "—" : formatted.Value + formatted.Unit, 11, _palette.Fg));
            return row;
        }

        /// <summary>
        /// 퍼센트는 0~100 고정, 메모리는 <b>용량 기준</b>(§8.3), 나머지는 보이는 구간의
        /// 최고치에 여유를 얹는다.
        /// </summary>
        /// <remarks>
        /// 메모리를 최고치에 맞춰 움직이면 1GB 를 쓰든 7GB 를 쓰든 영역 높이가 같아져
        /// "얼마나 잡고 있고 얼마나 남았는가"를 읽을 수 없다. 그 판단은 그리기가 아니므로
        /// <c>Core</c> 의 <see cref="GpuMemoryAxis"/> 가 갖고 있고, 메인 창과 같은 것을 쓴다.
        /// </remarks>
        private double Resolve(int viewStart, int viewCount)
        {
            if (_primary.Unit == MetricUnit.Percent) return 100;

            int from = Math.Max(0, viewStart);
            int to = Math.Min(_primary.Count, viewStart + viewCount);
            double peak = 0;
            for (int i = from; i < to; i++)
            {
                // 쌓아 그리므로 최고치도 <b>합계</b>로 잰다. 따로 재면 축이 낮게 잡혀 띠가 잘린다.
                double at = Finite(_primary.Values[i]);
                if (_secondary is not null)
                    at = _memory ? at + Finite(_secondary.Values[i])
                                 : Math.Max(at, Finite(_secondary.Values[i]));
                peak = Math.Max(peak, at);
            }

            if (_memory) return GpuMemoryAxis.Max(_discrete, _dedicatedCapacity, _sharedCapacity, peak);
            return peak > 0 ? peak * 1.08 : 1;
        }

        private static double Bytes(string? text) =>
            double.TryParse(text, out double value) ? value : 0;

        private static double Finite(float v) => float.IsNaN(v) ? 0 : v;

        private static string Axis(double value, MetricUnit unit)
        {
            var formatted = MetricFormatter.Format(unit, value);
            return unit == MetricUnit.Percent ? formatted.Value : formatted.Value + formatted.Unit;
        }
    }

    /// <summary>장치 한 줄 — 요약 띠와 차트 한두 판.</summary>
    internal sealed class Row
    {
        /// <summary>곁들이 판의 높이. 주 판보다 낮다 — 딸린 것이지 맞선 것이 아니다.</summary>
        private const double SubChartHeight = 77;

        private readonly TextBlock _stats, _scope;
        private readonly TextBlock? _subLabel;
        private readonly string _subName;
        private readonly System.Windows.Shapes.Path _chevron;
        private readonly StackPanel _bodies;
        private readonly Plot _main;
        private readonly Plot? _sub;

        public StackPanel Panel { get; }
        public bool Collapsed { get; set; }

        /// <summary>커서가 얹힌 판의 차트. <c>Ctrl</c>+휠이 확대 기준점을 잡을 때 쓴다.</summary>
        public SnapshotChart? Hovered =>
            _main.Chart.IsMouseOver ? _main.Chart
            : _sub is not null && _sub.Chart.IsMouseOver ? _sub.Chart
            : null;

        public Row(SnapshotWindow window, SnapshotMetric primary, SnapshotMetric? secondary,
                   SnapshotMetric? subPrimary, SnapshotMetric? subSecondary, string subName,
                   ThemePalette palette, MetricSnapshot snapshot, long gapThreshold)
        {
            _subName = subName;
            var accent = palette.AccentFor(primary.Device.Class, primary.Device.Key);

            _chevron = new System.Windows.Shapes.Path
            {
                Data = Icons.Chevron,
                Stroke = Frozen(palette.Faint),
                StrokeThickness = 1.7,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Stretch = Stretch.None,
                Width = Icons.DesignSize,
                Height = Icons.DesignSize,
                LayoutTransform = new ScaleTransform(11 / Icons.DesignSize, 11 / Icons.DesignSize),
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
            };

            // 배경이 없는 패널은 <b>그린 픽셀 위에서만</b> 히트된다. 투명 배경을 깔지 않으면
            // 셰브런과 글자 위만 눌려, 접으려면 11px 짜리 화살표를 정확히 찍어야 한다.
            var strip = new DockPanel
            {
                Height = StripHeight,
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
            };
            DockPanel.SetDock(_chevron, Dock.Left);
            strip.Children.Add(_chevron);

            var dot = new Border
            {
                Width = 8, Height = 8,
                CornerRadius = new CornerRadius(2),
                Background = Frozen(accent),
                Margin = new Thickness(7, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(dot, Dock.Left);
            strip.Children.Add(dot);

            var name = Text(primary.Device.ShortName, 12, palette.Fg, FontWeights.SemiBold,
                new Thickness(0, 0, 10, 0));
            DockPanel.SetDock(name, Dock.Left);
            strip.Children.Add(name);

            _scope = Mono("표시 구간", 9.5, palette.Gpu);
            _scope.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(_scope, Dock.Right);
            strip.Children.Add(_scope);

            _stats = Mono("", 11, palette.Dim);
            strip.Children.Add(_stats);

            strip.MouseLeftButtonUp += (_, _) => window.ToggleRow(this);

            _main = new Plot(window, primary, secondary, palette, accent, snapshot,
                             gapThreshold, ChartHeight);

            _bodies = new StackPanel();
            _bodies.Children.Add(_main.Body);

            if (subPrimary is not null)
            {
                // 이름표도 점도 셰브런도 없다. 딸린 판이지 다른 장치가 아니다 —
                // 적는 것은 무엇을 재는지와 그 구간의 숫자뿐이고, 들여쓰기는 홈통에 맞춘다.
                _subLabel = Mono("", 10, palette.Dim);
                _subLabel.Margin = new Thickness(GutterWidth + 5, 7, 0, 0);
                // 전용과 공유를 나란히 적으면 최소 폭(520)에서는 줄이 넘친다. 잘리더라도
                // 말줄임으로 잘렸다는 것이 보여야 한다 — 그냥 잘리면 값이 틀려 보인다.
                _subLabel.TextTrimming = TextTrimming.CharacterEllipsis;
                _bodies.Children.Add(_subLabel);

                _sub = new Plot(window, subPrimary, subSecondary, palette, accent, snapshot,
                                gapThreshold, SubChartHeight);
                _bodies.Children.Add(_sub.Body);
            }

            Panel = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };
            Panel.Children.Add(strip);
            Panel.Children.Add(_bodies);
        }

        public void Update(int viewStart, int viewCount, int? selectA, int? selectB, int? scrub,
                           List<(double Fraction, DateTime Time)> ticks, (int From, int To) range)
        {
            _bodies.Visibility = Collapsed ? Visibility.Collapsed : Visibility.Visible;
            _chevron.RenderTransform = new RotateTransform(Collapsed ? -90 : 0);

            // 접어도 숫자는 남긴다. 접는 목적이 "자리를 비워 다른 장치를 크게 보는 것"이라
            // 접힌 장치가 비교 대상에서 빠지면 안 된다.
            _stats.Text = _main.Summarize(range.From, range.To, both: false);
            _scope.Text = selectA is not null && selectB is not null ? "선택 구간" : "표시 구간";

            if (Collapsed) return;

            _main.Update(viewStart, viewCount, selectA, selectB, scrub, ticks);

            if (_sub is null || _subLabel is null) return;
            _subLabel.Text = $"{_subName}   " + _sub.Summarize(range.From, range.To, both: true);
            _sub.Update(viewStart, viewCount, selectA, selectB, scrub, ticks);
        }
    }
}

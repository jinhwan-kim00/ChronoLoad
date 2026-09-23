using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChronoLoad.App.Services;

namespace ChronoLoad.App.Controls;

/// <summary>
/// 제목 표시줄 왼쪽의 앱 마크. 실행 파일 아이콘과 같은 모양이다.
/// </summary>
/// <remarks>
/// <b>ico 파일을 불러오지 않고 도형으로 그린다.</b> <c>ApplicationIcon</c> 으로 지정한 파일은
/// SDK 가 WPF 리소스에서 빼기 때문에 XAML 에서 참조하면 시작할 때 죽는다. 게다가 도형으로 두면
/// 어느 DPI 에서도 또렷하고, 테마 팔레트의 지표 색을 그대로 쓸 수 있다 —
/// 아이콘과 화면이 같은 색 언어를 쓰게 된다.
/// </remarks>
public sealed class BrandMark : Grid
{
    private readonly System.Windows.Shapes.Rectangle[] _bars;

    public BrandMark()
    {
        Width = 18;
        Height = 18;
        Background = Brushes.Transparent;      // 히트 테스트용
        Cursor = System.Windows.Input.Cursors.Hand;

        // 실행 파일 아이콘과 같은 비율: 막대 셋이 왼쪽에서 오른쪽으로 높아진다.
        double[] heights = [0.42, 0.64, 0.86];
        _bars = new System.Windows.Shapes.Rectangle[3];

        for (int i = 0; i < 3; i++)
        {
            var bar = new System.Windows.Shapes.Rectangle
            {
                Width = 4,
                Height = 18 * heights[i],
                RadiusX = 2,
                RadiusY = 2,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(i * 6.5, 0, 0, 0),
            };

            _bars[i] = bar;
            Children.Add(bar);
        }
    }

    public void ApplyTheme(ThemePalette palette)
    {
        Color[] colors = [palette.Cpu, palette.Mem, palette.Gpu];
        for (int i = 0; i < _bars.Length; i++)
        {
            var brush = new SolidColorBrush(colors[i]);
            brush.Freeze();
            _bars[i].Fill = brush;
        }
    }
}

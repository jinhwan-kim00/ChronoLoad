using System.Windows;
using System.Windows.Media;
using ChronoLoad.Core.Devices;

namespace ChronoLoad.App.Rendering;

/// <summary>
/// 16px 그리드 · 1.7px 스트로크 · 라운드 캡의 단색 라인 아이콘.
/// 제조사 글리프는 원본 로고가 아니라 <b>같은 규칙으로 자체 제작한 추상 글리프</b>다(UX §03 상표 항목).
/// </summary>
public static class Icons
{
    public const double DesignSize = 16;

    public static Geometry Cpu { get; } = Group(
        RoundedRect(4, 4, 8, 8, 1.5),
        Lines((6, 2, 6, 4), (10, 2, 10, 4), (6, 12, 6, 14), (10, 12, 10, 14),
              (2, 6, 4, 6), (2, 10, 4, 10), (12, 6, 14, 6), (12, 10, 14, 10)));

    public static Geometry Memory { get; } = Group(
        RoundedRect(2, 3, 12, 4, 1),
        RoundedRect(2, 9, 12, 4, 1),
        Lines((4.5, 5, 5.2, 5), (4.5, 11, 5.2, 11)));

    public static Geometry Ethernet { get; } = Group(
        RoundedRect(4, 5.5, 8, 6, 1),
        Parse("M6.5,5.5 V3.4 H9.5 V5.5"),
        Lines((5.9, 8, 5.9, 9.6), (8, 8, 8, 9.6), (10.1, 8, 10.1, 9.6)));

    public static Geometry WiFi { get; } = Group(
        Parse("M2.4,6.3 A8.6,8.6 0 0 1 13.6,6.3"),
        Parse("M4.8,8.9 A5.2,5.2 0 0 1 11.2,8.9"),
        Lines((8, 12.2, 8.05, 12.2)));

    public static Geometry Ssd { get; } = Group(
        RoundedRect(2, 3.5, 12, 8, 1.5),
        Lines((4.8, 6.3, 11.2, 6.3), (4.8, 8.7, 8.4, 8.7),
              (5, 11.5, 5, 13.5), (8, 11.5, 8, 13.5), (11, 11.5, 11, 13.5)));

    public static Geometry Hdd { get; } = Group(
        Circle(8, 8, 5.8), Circle(8, 8, 1.4), Lines((12.6, 12.6, 9.3, 9.3)));

    public static Geometry Drive { get; } = Group(
        RoundedRect(2, 4.5, 12, 7, 1.5), Lines((4.6, 8, 4.65, 8), (8, 8, 11.4, 8)));

    /// <summary>NVIDIA — 눈 모양 소용돌이.</summary>
    public static Geometry GpuNvidia { get; } = Group(
        Parse("M2.4,8.6 C4.1,5.5 6.7,3.7 9.4,3.7 C12.0,3.7 13.6,5.1 13.6,7.0 " +
              "C13.6,10.0 10.3,12.3 7.0,12.3 C5.1,12.3 4.0,11.4 4.0,10.1 C4.0,8.2 6.1,6.5 8.4,6.5"),
        Parse("M8.4,6.5 C9.4,6.5 10.1,7.0 10.1,7.9"));

    /// <summary>AMD — 사선 화살표.</summary>
    public static Geometry GpuAmd { get; } = Group(
        Lines((13.4, 2.6, 13.4, 13.4), (13.4, 2.6, 3.0, 13.4),
              (8.3, 2.6, 3.1, 7.8), (10.9, 2.6, 3.0, 10.5)));

    /// <summary>Intel — 원 안의 i.</summary>
    public static Geometry GpuIntel { get; } = Group(
        Circle(8, 8, 5.9), Lines((8, 7.3, 8, 10.9), (8, 4.9, 8.05, 4.9)));

    /// <summary>NPU — 칩 테두리 안의 노드 그래프. CPU 칩(바깥 핀)과 한눈에 갈린다.</summary>
    public static Geometry Npu { get; } = Group(
        RoundedRect(2.5, 2.5, 11, 11, 2),
        Lines((5.5, 5.5, 10.5, 8), (5.5, 10.5, 10.5, 8), (5.5, 5.5, 5.5, 10.5)),
        Circle(5.5, 5.5, 1.05), Circle(5.5, 10.5, 1.05), Circle(10.5, 8, 1.05));

    public static Geometry GpuGeneric { get; } = Group(
        RoundedRect(2, 4, 12, 9, 1.5), RoundedRect(5, 7, 6, 3, 0.5),
        Lines((5, 2, 5, 4), (11, 2, 11, 4)));

    // ── 스냅샷 창 (§8.8 · §9.6) ─────────────────────────────────

    /// <summary>스냅샷 — 카메라. 본체 · 돌출부 · 렌즈.</summary>
    public static Geometry Snapshot { get; } = Group(
        RoundedRect(2, 5, 12, 8, 2),
        Parse("M5.8,5 L6.6,3.2 H9.4 L10.2,5"),
        Circle(8, 9, 2.5));

    /// <summary>전체 보기 — 양끝 기둥과 바깥으로 벌어지는 화살표. 가로만 맞춘다는 뜻이다.</summary>
    public static Geometry FitWidth { get; } = Group(
        Lines((2.4, 3.5, 2.4, 12.5), (13.6, 3.5, 13.6, 12.5)),
        Parse("M6.2,8 H3.2 M5,6.2 L3.1,8 L5,9.8"),
        Parse("M9.8,8 H12.8 M11,6.2 L12.9,8 L11,9.8"));

    /// <summary>크롭 — 겹친 L 두 개. 관습이 굳은 글리프라 설명이 필요 없다.</summary>
    public static Geometry Crop { get; } = Group(
        Parse("M4.5,1.8 V11.5 H14.2"),
        Parse("M1.8,4.5 H11.5 V14.2"));

    /// <summary>
    /// 되돌리기 — <b>열린 갈고리</b>. <see cref="Reset"/>(닫힌 원호)과 실루엣부터 갈라야 한다.
    /// 둘 다 우리말로는 "되돌린다"지만 하나는 통계 기준점을 옮기고 하나는 크롭을 취소한다.
    /// </summary>
    public static Geometry Undo { get; } = Group(
        Parse("M2.6,6.4 H9.6 A3.8,3.8 0 0 1 9.6,14 H6.4"),
        Parse("M5.6,3.4 L2.6,6.4 L5.6,9.4"));

    /// <summary>내보내기 — 아래 화살표와 바닥선.</summary>
    public static Geometry Export { get; } = Group(
        Parse("M8,2.6 V10.4"),
        Parse("M4.8,7.2 L8,10.4 L11.2,7.2"),
        Parse("M2.8,12.6 H13.2"));

    public static Geometry ZoomOut { get; } = Group(
        Circle(7, 7, 4), Lines((10, 10, 13.6, 13.6), (5, 7, 9, 7)));

    public static Geometry ZoomIn { get; } = Group(
        Circle(7, 7, 4), Lines((10, 10, 13.6, 13.6), (5, 7, 9, 7), (7, 5, 7, 9)));

    public static Geometry Chevron { get; } = Parse("M4,6 L8,10 L12,6");
    public static Geometry Reset { get; } = Parse("M13,8 A5,5 0 1 1 11.4,4.3 M13,2 V5 H10");
    public static Geometry Pin { get; } = Parse("M8,10 V14 M5,3 H11 L10,8 L12,10 H4 L6,8 Z");
    public static Geometry Theme { get; } = Parse("M8,2.5 A5.5,5.5 0 1 0 8,13.5 A4.2,4.2 0 0 1 8,2.5 Z");

    /// <summary>
    /// 설정 — 슬라이더 두 줄. 톱니로 먼저 그렸다가 바꿨다: 16px 로 줄이면 이가 뭉개져
    /// <b>해처럼</b> 보이는데, 바로 옆이 테마(달)라 둘이 밝기 한 쌍으로 읽혔다.
    /// 슬라이더는 어느 크기에서도 달과 헷갈리지 않는다.
    /// </summary>
    public static Geometry Settings { get; } = Group(
        Lines((2.5, 5.5, 13.5, 5.5), (2.5, 10.5, 13.5, 10.5)),
        Circle(5.8, 5.5, 1.7), Circle(10.2, 10.5, 1.7));
    public static Geometry Minimize { get; } = Parse("M3.5,8.5 H12.5");

    /// <summary>
    /// 시계 — 헤더 숫자가 "지금이 아니라 스크럽한 시점의 값"임을 표시한다(§8.7).
    /// 예전의 점 표시는 숫자 바로 앞에 붙어 <c>.96</c> 처럼 소수점으로 읽혔다.
    /// </summary>
    public static Geometry Clock { get; } = Group(
        Circle(8, 8, 5.6),
        Parse("M8,4.9 V8 L10.2,9.5"));
    public static Geometry Close { get; } = Parse("M4,4 L12,12 M12,4 L4,12");

    public static Geometry For(IconKind kind) => kind switch
    {
        IconKind.Cpu => Cpu,
        IconKind.Memory => Memory,
        IconKind.GpuNvidia => GpuNvidia,
        IconKind.GpuAmd => GpuAmd,
        IconKind.GpuIntel => GpuIntel,
        IconKind.Npu => Npu,
        IconKind.DiskSsd => Ssd,
        IconKind.DiskHdd => Hdd,
        IconKind.DiskGeneric => Drive,
        IconKind.NetEthernet => Ethernet,
        IconKind.NetWiFi => WiFi,
        _ => GpuGeneric,
    };

    // ── 조립 헬퍼 ────────────────────────────────────────────────
    private static Geometry Parse(string path)
    {
        var geometry = Geometry.Parse(path);
        geometry.Freeze();
        return geometry;
    }

    private static Geometry RoundedRect(double x, double y, double w, double h, double radius)
    {
        var geometry = new RectangleGeometry(new Rect(x, y, w, h), radius, radius);
        geometry.Freeze();
        return geometry;
    }

    private static Geometry Circle(double cx, double cy, double r)
    {
        var geometry = new EllipseGeometry(new Point(cx, cy), r, r);
        geometry.Freeze();
        return geometry;
    }

    private static Geometry Lines(params (double X1, double Y1, double X2, double Y2)[] segments)
    {
        var group = new GeometryGroup();
        foreach (var (x1, y1, x2, y2) in segments)
            group.Children.Add(new LineGeometry(new Point(x1, y1), new Point(x2, y2)));
        group.Freeze();
        return group;
    }

    private static Geometry Group(params Geometry[] parts)
    {
        var group = new GeometryGroup();
        foreach (var part in parts) group.Children.Add(part);
        group.Freeze();
        return group;
    }
}

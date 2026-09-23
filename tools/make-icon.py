# -*- coding: utf-8 -*-
"""앱 아이콘(app.ico)을 만든다.

아이콘은 16px 에서 읽히는지가 전부다. 그 크기에서 살아남는 것은 큰 덩어리와 색 대비뿐이라,
차트 선 대신 **막대 세 개**를 쓴다 — 선은 16px 로 줄이면 뭉개진다.

색은 앱이 쓰는 지표 색을 그대로 가져왔다(CPU 하늘 · 메모리 보라 · GPU 호박).
어두운 작업표시줄에서는 밝은 막대가, 밝은 작업표시줄에서는 어두운 타일이 형태를 만든다.

    python tools/make-icon.py
"""
from PIL import Image, ImageDraw

SIZES = [256, 128, 64, 48, 32, 24, 16]
SUPER = 8                      # 수퍼샘플 배율. 모서리와 막대 끝을 매끄럽게 만든다

TILE = (24, 32, 41, 255)       # #182029
BARS = [(56, 189, 248, 255),   # #38BDF8  CPU
        (167, 139, 250, 255),  # #A78BFA  메모리
        (251, 191, 36, 255)]   # #FBBF24  GPU


def render(size: int) -> Image.Image:
    s = size * SUPER
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)

    margin = s * 0.03
    d.rounded_rectangle([margin, margin, s - margin, s - margin],
                        radius=s * 0.22, fill=TILE)

    bar_w = s * 0.155
    gap = s * 0.085
    total = bar_w * 3 + gap * 2
    x = (s - total) / 2
    base = s * 0.775
    heights = [0.30, 0.47, 0.64]

    for color, h in zip(BARS, heights):
        top = base - s * h
        d.rounded_rectangle([x, top, x + bar_w, base],
                            radius=bar_w / 2, fill=color)
        x += bar_w + gap

    return im.resize((size, size), Image.LANCZOS)


def main() -> None:
    frames = [render(n) for n in SIZES]
    out = r"src/ChronoLoad.App/app.ico"
    frames[0].save(out, format="ICO",
                   sizes=[(n, n) for n in SIZES],
                   append_images=frames[1:])
    print(f"{out} — {', '.join(str(n) for n in SIZES)}")

    # 눈으로 확인할 시트. 실제 크기 그대로 늘어놓는다.
    sheet = Image.new("RGBA", (sum(SIZES) + 20 * len(SIZES), 256 + 40), (18, 22, 28, 255))
    x = 10
    for n, frame in zip(SIZES, frames):
        sheet.paste(frame, (x, 20 + (256 - n) // 2), frame)
        x += n + 20
    sheet.save("tools/icon-preview.png")
    print("tools/icon-preview.png")


if __name__ == "__main__":
    main()

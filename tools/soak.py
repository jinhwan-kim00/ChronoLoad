# -*- coding: utf-8 -*-
"""앱을 띄워 두고 메모리 추세를 기록한다 (§12 누수 목표 검증).

    python tools/soak.py --minutes 240

핵심은 마지막에 찍히는 **판정 가능 여부**다. 짧게 돌리고 "누수 없음"이라고 적는 것이
이 항목에서 가장 흔한 자기기만이라, 관측 길이가 목표를 가릴 만큼 되는지 먼저 계산한다.
워킹셋은 틱마다 흔들리므로(실측 σ ≈ 0.8MB) 10MB/24h 를 구분하려면 네 시간 이상이 필요하다.

워밍업 구간(기본 2분)은 추세 계산에서 뺀다. 기동 직후에는 JIT 와 WPF 캐시 때문에
30MB 가량 오르는데, 그것을 누수로 세면 어떤 실행이든 "누수 있음"이 된다.
"""
from __future__ import annotations

import argparse
import statistics
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

CANDIDATES = [
    "src/ChronoLoad.App/bin/Release/net10.0-windows/win-x64/ChronoLoad.App.exe",
    "src/ChronoLoad.App/bin/Release/net10.0-windows/ChronoLoad.App.exe",
    "src/ChronoLoad.App/bin/Debug/net10.0-windows/ChronoLoad.App.exe",
]


def find_exe() -> Path:
    for relative in CANDIDATES:
        path = ROOT / relative
        if path.exists():
            return path

    sys.exit("실행 파일을 찾지 못했다. 먼저 빌드하거나 --exe 로 경로를 준다.")


def probe(pid: int) -> tuple[int, int, int, int] | None:
    """워킹셋·프라이빗·스레드·핸들. 프로세스가 없으면 None."""
    script = (
        f"$p = Get-Process -Id {pid} -ErrorAction SilentlyContinue; "
        "if ($p) { '{0},{1},{2},{3}' -f $p.WorkingSet64, $p.PrivateMemorySize64, "
        "$p.Threads.Count, $p.HandleCount }"
    )
    out = subprocess.run(["powershell", "-NoProfile", "-Command", script],
                         capture_output=True, text=True).stdout.strip()

    if not out:
        return None

    a, b, c, d = out.split(",")
    return int(a), int(b), int(c), int(d)


def analyse(samples: list[tuple[float, float]], warmup: float, target_mb_per_day: float) -> None:
    steady = [(t, mb) for t, mb in samples if t >= warmup]
    if len(steady) < 4:
        print(f"\n표본이 {len(steady)}개뿐이라 추세를 낼 수 없다. 더 길게 돌린다.")
        return

    xs = [t for t, _ in steady]
    ys = [mb for _, mb in steady]
    n = len(xs)
    mx, my = sum(xs) / n, sum(ys) / n

    denominator = sum((x - mx) ** 2 for x in xs)
    slope = sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / denominator if denominator else 0.0
    noise = statistics.pstdev([y - (my + slope * (x - mx)) for x, y in zip(xs, ys)])

    span = xs[-1] - xs[0]
    detectable = 2 * noise / span * 1440 if span else float("inf")

    print()
    print(f"  정상 구간   {min(ys):.1f} ~ {max(ys):.1f} MB, 표본 {n}개 ({xs[0]:.1f}~{xs[-1]:.1f}분)")
    print(f"  기울기      {slope * 1440:+.0f} MB/24h  (잔차 σ {noise:.2f} MB)")
    print(f"  구분 가능   {detectable:.0f} MB/24h 이상")
    print()

    if detectable <= target_mb_per_day:
        verdict = "목표 이내" if abs(slope * 1440) <= target_mb_per_day else "목표 초과 — 누수 의심"
        print(f"  판정: {verdict} (목표 {target_mb_per_day:.0f} MB/24h)")
    else:
        need = 2 * noise / target_mb_per_day * 1440
        print(f"  판정 불가. 목표 {target_mb_per_day:.0f} MB/24h 를 가리려면 "
              f"약 {need / 60:.1f}시간 관측이 필요하다.")


def main() -> None:
    parser = argparse.ArgumentParser(description="ChronoLoad 메모리 소크 테스트")
    parser.add_argument("--minutes", type=float, default=240, help="관측 시간(분). 기본 240")
    parser.add_argument("--interval", type=float, default=60, help="표본 간격(초). 기본 60")
    parser.add_argument("--warmup", type=float, default=2, help="추세에서 제외할 앞부분(분). 기본 2")
    parser.add_argument("--target", type=float, default=10, help="§12 증가 목표(MB/24h). 기본 10")
    parser.add_argument("--exe", type=Path, help="실행 파일 경로")
    parser.add_argument("--out", type=Path, default=ROOT / "soak.log", help="기록 파일")
    args = parser.parse_args()

    exe = args.exe or find_exe()
    print(f"  대상   {exe}")
    print(f"  계획   {args.minutes:.0f}분, {args.interval:.0f}초 간격")
    print(f"  기록   {args.out}")

    process = subprocess.Popen([str(exe)])
    time.sleep(15)          # 기동과 첫 샘플이 자리를 잡을 때까지

    samples: list[tuple[float, float]] = []
    start = time.time()
    lines = ["# 경과(분), 워킹셋(MB), 프라이빗(MB), 스레드, 핸들"]

    try:
        while (elapsed := (time.time() - start) / 60) < args.minutes:
            reading = probe(process.pid)
            if reading is None:
                lines.append(f"# {elapsed:.1f}분: 프로세스가 사라졌다")
                print(f"\n  {elapsed:.1f}분 지점에서 프로세스가 사라졌다 — 비정상 종료를 의심한다.")
                break

            ws, private, threads, handles = reading
            mb = ws / 1048576
            samples.append((elapsed, mb))
            lines.append(f"{elapsed:6.1f}, {mb:7.1f}, {private / 1048576:7.1f}, {threads:3}, {handles:5}")
            args.out.write_text("\n".join(lines) + "\n", encoding="utf-8")

            time.sleep(args.interval)
    except KeyboardInterrupt:
        print("\n  중단됨. 지금까지의 표본으로 분석한다.")
    finally:
        process.kill()

    if samples:
        analyse(samples, args.warmup, args.target)


if __name__ == "__main__":
    main()

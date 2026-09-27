# ChronoLoad

GPU 중심의 실시간 시스템 모니터. 화면 구석에 세워 두고 **지금 무엇이 얼마나 쓰이고 있는지**를
한눈에 보기 위한 WPF 앱이며, 같은 정보를 **MCP**로 AI 에이전트에게도 제공한다.

<img src="docs/screenshot.png" width="320" alt="ChronoLoad 화면">

## 무엇이 다른가

- **GPU가 주인공이다.** 어댑터마다 카드 하나. 사용률과 전용·공유 메모리를 한 차트에 겹쳐 그려서,
  VRAM이 넘쳐 시스템 메모리로 새는 순간을 바로 알아볼 수 있다.
- **다중 GPU와 NPU를 따로 센다.** RTX·Arc·AI Boost가 섞여 있어도 각각의 카드로 분리된다.
- **리셋 기준 통계.** 벤치마크를 시작하는 시점에 리셋하면 그 구간의 평균·최소·최대·p95를 누적한다.
- **흐름을 멈추고 구간을 잴 수 있다.** 스냅샷 버튼을 누르면 지금 버퍼가 그대로 얼어붙은 창이 열린다.
  구간을 끌어 평균·최소·최대를 읽고, 그 구간만 남겨 CSV 로 낸다 — 여러 번의 테스트를 나란히 비교할 수 있다.
- **시간 축이 하나다.** 모든 카드가 같은 시각 축을 쓰므로, 차트 위에 커서를 올리면
  "GPU가 멈춘 그 순간 디스크는 뭘 했나"를 한 번에 볼 수 있다.
- **감시 대상에 부담을 주지 않는다.** 샘플링 듀티 사이클 1% 미만이 설계 목표다. 데스크톱 0.97%,
  저전력 8코어 노트북은 순간 관측 1.47% · 4.3시간 적분 0.63%. 잠들어 있는 외장 GPU는 **깨우지 않는다**.

## 요구 사항

- Windows 10 1809 이상 (Windows 11 권장)
- .NET 10 런타임

GPU 온도·전력·클럭은 벤더 라이브러리가 있을 때만 나온다. 없으면 사용률과 메모리까지는 그대로 나온다.

| 벤더 | 경로 | 얻는 값 |
|---|---|---|
| NVIDIA | `nvml.dll` (드라이버에 포함) | 사용률 · 메모리 · 온도 · 전력 · 클럭 |
| Intel | `ControlLib.dll` (IGCL) | 온도 · 전력 · 클럭 |
| Intel | `ze_loader.dll` (Level Zero) | 클럭 — IGCL이 없을 때의 대체 경로 |
| AMD | — | **미지원.** 사용률·메모리는 PDH로 나온다 |
| 공통 | Windows PDH 카운터 | 사용률 · 전용/공유 메모리 |

## 빌드와 실행

```bash
git clone https://github.com/jinhwan-kim00/ChronoLoad.git
cd ChronoLoad
dotnet run --project src/ChronoLoad.App
```

게시본을 만들려면:

```bash
dotnet publish src/ChronoLoad.App -c Release -r win-x64
```

## 사용법

| 동작 | 방법 |
|---|---|
| 카드 접기 · 펴기 | 카드 헤더 클릭, 또는 `1`~`9` |
| 값 읽기 | 차트 위에 마우스를 올리면 그 시각의 값이 전 카드에 함께 표시된다 |
| 그 시점 고정 | 차트 클릭. 고정 상태에서 `←` `→`로 이동(`Shift`와 함께 10칸) |
| 고정 해제 | `Esc` |
| 시간 폭 | `Ctrl`+휠 — 30s · 60s · 3m · 10m · 15m. 표준(60s)이 아니면 제목 표시줄에 폭이 뜬다 |
| 표준 폭으로 복귀 | 차트 더블클릭 |
| 통계 리셋 | 제목 표시줄 왼쪽 버튼, 또는 `Ctrl+R` |
| 스냅샷 창 | ⟲ 옆 카메라 버튼 — 지금 버퍼를 떠내 고정한다. 여러 개 열어 비교할 수 있다 |
| 항상 위 | 핀 버튼 |
| 창 불투명도 | `Shift`+휠, 또는 설정에서 슬라이더 (25~100%) |
| 설정 | 슬라이더 버튼 — 불투명도 · 항상 위 · 테마 |
| 테마 | 달 버튼(라이트·다크), 설정에서는 시스템까지 셋 |
| 정보 · 버전 | 왼쪽 위 앱 마크 클릭 |

창 위치·크기, 항상 위, 불투명도, 테마, 카드별 접힘 상태는 **자동으로 저장**되어 다음 실행에 복원된다
(`%LOCALAPPDATA%\ChronoLoad\settings.json`). 장치는 인덱스가 아니라 키로 기억하므로,
장치가 하나 빠져도 나머지 설정이 밀리지 않는다.

공간이 모자라면 카드가 스스로 접힌다. 스크롤은 없다 — 세로로 긴 창에서 스크롤은
"한눈에 본다"는 목적과 충돌한다.

## MCP

앱이 실행 중일 때 **로컬 MCP 서버**가 함께 뜬다. 에이전트가 시스템 상태를 직접 조회할 수 있다.

### 연결

배포 패키지를 쓴다면 압축을 푼 폴더의 `chronoload-mcp.exe` 를 가리키면 된다.

```bash
claude mcp add chronoload -- <압축을 푼 경로>\chronoload-mcp.exe
```

소스에서 빌드했다면:

```bash
claude mcp add chronoload -- <저장소>\tools\ChronoLoad.McpBridge\bin\Debug\net10.0-windows\chronoload-mcp.exe
```

브리지는 stdio ↔ HTTP 프록시다. HTTP 전송을 직접 지원하는 클라이언트라면
`http://127.0.0.1:7667/mcp`에 붙어도 된다(토큰 필요, 아래 참조).

> **앱을 새 버전으로 바꿨으면 MCP 클라이언트를 다시 시작한다.** 툴 목록과 파라미터는
> 클라이언트가 연결할 때 한 번 읽어 두므로, 그대로 두면 새 툴이 보이지 않고 새 파라미터를
> 넘기면 호출이 실패한다. 배포 패키지를 다시 빌드할 때는 앱과 함께 **브리지(`chronoload-mcp.exe`)도
> 멈춰야** 폴더를 지울 수 있다 — 클라이언트가 브리지를 붙잡고 있다.

### 툴

| 툴 | 쓰임 |
|---|---|
| `get_system_snapshot` | CPU·메모리 + GPU·디스크·네트워크 배열. 첫 조회용 |
| `get_gpu_status` | 어댑터별 사용률·메모리·온도·전력·클럭, VRAM 초과 여부, 센서 계층. AI 작업이 어느 지표에 잡히는지(`aiSignals` — HAGS 가 켜진 NVIDIA 는 CUDA 가 3D 로 잡힌다), NVIDIA 메모리 컨트롤러 사용률, 전력 한도와 클럭 제한 사유(전력·온도 — 제한에 걸린 시간 비율은 `GpuThrottle*` 지표의 구간 평균). `verbose` 면 엔진 계열(3D·Compute·Copy·Video) 분해 |
| `get_disk_status` | 디스크별 읽기·쓰기·활성 비율, 매체(SSD/HDD) |
| `get_network_interfaces` | 인터페이스별 수신·송신. 터널은 기본 제외 |
| `get_metric_history` | 최근 시계열. 실측 표본만 시각과 함께 주고, 많으면 시간으로 등분해 칸마다 평균·최소·최대(스파이크는 최대에 남는다) |
| `get_stats_since_reset` | 리셋 이후 평균·최소·최대·p50·p95·p99·표준편차. 15분 안이면 분위수가 정확값이고, 사용률은 90% 이상이었던 시간 비율도 준다 |
| `reset_stats` | MCP 쪽 기준점만 옮긴다. 직전 구간을 반환한다(지표·장치로 좁히거나 생략 가능) |
| `list_processes` | CPU·메모리·GPU·GPU 메모리·디스크 I/O로 정렬. GPU·디스크 정렬은 쓰지 않는 프로세스를 뺀다 |
| `get_process_detail` | 프로세스 하나의 어댑터별·엔진별 GPU 사용률 |
| `watch_process` · `get_process_history` · `unwatch_process` | 프로세스 하나의 엔진·CPU·메모리를 1초마다 기록하고 시계열로 읽는다 |
| `mark` · `list_marks` | 지금 시각에 이름을 붙인다(벤치 단계 시작·끝 등) |
| `get_interval_stats` · `compare_intervals` | 두 마커(또는 시각) 사이의 통계, 여러 구간을 지표별 한 표로 비교 |
| `describe_capabilities` | 무엇을 관측할 수 있는지, 지금 샘플 주기는 어떤지 |

리소스 `chronoload://snapshot` · `chronoload://stats`와
프롬프트 `analyze_gpu_workload`(병목 진단 템플릿)도 함께 제공한다.

### 분석 흐름

에이전트에게 말로 시키면 된다. 툴 이름을 외울 필요는 없다 — 예:

> ChronoLoad MCP 로 지금 GPU 구성을 보고, 음성 인식 서비스를 8통화로 돌리는 동안 병목이 어디인지 분석해줘.
> 단계마다 마커를 찍고 끝나면 단계별로 비교해줘.

에이전트가 밟는 순서는 대개 이렇다.

1. **구성 파악** — `describe_capabilities` 로 장치와 센서 계층을, `get_gpu_status` 로 어댑터별 상태와
   **`aiSignals`** 를 본다. `aiSignals` 는 그 GPU 에서 AI 작업이 **어느 지표에 잡히는지** 알려 준다.
   제조사·설정마다 다르다(아래 표).
2. **단계 구분** — 벤치 단계가 바뀔 때마다 `mark("idle")`, `mark("load-8calls")`, `mark("cooldown")` 처럼
   이름을 찍는다. 리셋과 달리 앞 구간을 잃지 않는다.
3. **대상 프로세스 기록** — `list_processes(sortBy="gpu")` 로 PID 를 찾고 `watch_process(pid)` 를 건다.
   그 프로세스의 어댑터별 엔진·CPU·워킹셋이 1초마다 쌓인다.
4. **비교** — 끝나면 `compare_intervals(["idle","load-8calls","cooldown"], untilNow=true)` 로 단계별 평균·p95·최대·
   포화 비율을 지표별 한 표로 받는다. 특정 구간만 자세히 보려면 `get_interval_stats(from, to)`.
5. **모양 확인** — 버스트가 어떻게 생겼는지는 `get_metric_history(metric, deviceKey, windowSeconds)` 로,
   프로세스 쪽은 `get_process_history(pid)` 로 본다. 점마다 시각이 있어(`startAt` + `offsetsMs`)
   마커 시각과 초 단위로 맞출 수 있다.

구간 하나만 재면 되는 경우에는 예전 방식도 된다 — `reset_stats(confirm=true, includePrevious=false)` 로
기준점을 옮기고 부하를 건 뒤 `get_stats_since_reset` 을 부른다.

#### 무엇이 병목인가 — 질문별로 볼 지표

| 질문 | 지표 | 읽는 법 |
|---|---|---|
| 연산이 포화됐나 | `GpuUtil`, Intel 은 `GpuRenderCompute` | 평균보다 **`saturatedFraction`**(90% 이상이었던 시간 비율)을 본다. 버스트형 부하는 평균이 포화를 가린다 |
| 어느 엔진이 일하나 | `Gpu3D` · `GpuCompute` · `GpuCopy` · `GpuVideo` | `aiSignals.primary` 가 가리키는 쪽. HAGS 가 켜진 NVIDIA 는 CUDA 가 `Gpu3D` 로 잡힌다 |
| VRAM 대역폭이 모자라나 | `GpuMemBusy` (NVIDIA) | 이것이 높은데 `GpuUtil` 이 낮으면 연산이 아니라 대역폭이 병목이다 |
| 호스트↔GPU 전송이 많나 | `GpuPcieRx`(업로드) · `GpuPcieTx`(다운로드), NVIDIA | `GpuCopy` 는 복사 엔진이 바빴던 **시간**이지 옮긴 **양**이 아니다 |
| 전력·온도 때문에 클럭이 깎이나 | `GpuThrottlePower` · `GpuThrottleThermal`, `powerLimitPercent` | 구간 평균이 곧 "제한에 걸려 있던 시간 비율"이다. `limitReasons` 에 지금 선 사유가 있다 |
| VRAM 이 넘쳤나 | `GpuDedicated` · `GpuShared`, `vramExceeded` | 전용이 용량에 붙고 공유가 늘면 시스템 RAM 으로 새는 중이다 |
| CPU 가 발목을 잡나 | `CpuTotal`, 프로세스별 `cpuPercent` | `get_process_history` 에서 GPU 가 쉬는 틈과 CPU 가 바쁜 틈이 겹치는지 본다 |

#### GPU 마다 다른 점

| | NVIDIA | Intel Arc |
|---|---|---|
| 연산 사용률 | `GpuUtil`(NVML, 250ms) | `GpuRenderCompute`(하드웨어 카운터, 250ms) — 3D 와 Compute 를 합친 값 |
| 엔진별 값(`Gpu3D`·`GpuCompute` 등) | 고르다 | **1초에 한 번이고 튄다.** 어느 엔진인지 가를 때만, 여러 초 평균으로 읽는다 |
| `GpuMemBusy`, PCIe 송수신 | 있다 | 없다(드라이버가 주지 않는다) |
| 전력 한도 | 있다 | 없다. 제한 사유(`limitReasons`)는 있다 |
| 유휴 시 | 늘 깨어 있다 | 절전(`availability: standby`)에 들어가면 온도·전력·클럭이 비는데, 깨우지 않으려는 의도다 |

#### 알아 둘 한계

- **링 버퍼는 15분이다.** 이력·구간 통계·마커 비교는 그 안에서만 된다. 밖으로 나간 부분은 `truncated` 로 알린다.
  리셋 통계(`get_stats_since_reset`)는 더 길게 누적되지만 15분을 넘으면 분위수가 근사(±0.5%)가 된다(`quantilesExact`)
- **마커와 프로세스 기록은 앱 메모리에만 있다.** 앱을 다시 켜면 사라진다
- 프로세스 기록은 최대 8개, 한 번에 최대 1시간, 최근 15분을 보관한다. 프로세스가 끝나도 기록은 남는다
- 응답이 커질 수 있다. `metric` · `deviceKey` 로 좁혀 부르면 빠르고 읽기도 쉽다

### 응답을 읽을 때

- 모든 응답에 `sampledAt` · `stale` · `devicesRevision`이 붙는다.
  `devicesRevision`만 비교하면 "내가 알던 장치 구성이 그대로인가"를 알 수 있다.
- 장치 배열의 원소는 `index`와 `key`를 모두 갖는다. **재조회에는 `key`를 쓴다** —
  인덱스는 장치가 빠지면 밀린다.
- **값이 없으면 `null`이다. 0이 아니다.** 계층이 `PDH`인 어댑터는 온도·전력·클럭이 없고,
  `availability`가 `standby`면 저전력 대기라 일부러 읽지 않은 것이다.
- `sampling.pace`가 `full`이 아니면 창이 최소화됐거나 기기가 버거워 주기가 느려진 상태다.

### 앱이 꺼져 있으면

`initialize`와 목록 조회는 성공하고, 실제 툴 호출만
`{"error":"app_not_running"}`을 돌려준다. 앱을 켜면 클라이언트를 다시 시작하지 않아도 바로 붙는다.

헤드리스 모드는 제공하지 않는다. 앱 없이 수집하면 "리셋 기준 통계"도 "히스토리"도 가질 수 없어
이 MCP의 가치 대부분이 사라진다. 반쪽짜리 응답보다 명확한 실패가 낫다.

### 보안

- `127.0.0.1` 고정. 외부 바인딩 옵션은 없다
- 기동 시 임의 토큰을 `%LOCALAPPDATA%\ChronoLoad\mcp.token`에 쓰고 종료 시 지운다
- `Origin` 헤더 검증(DNS 리바인딩 방어), 토큰 비교는 상수 시간
- **읽기 전용이 원칙.** 상태를 바꾸는 툴은 `reset_stats` · `mark` · `watch_process` · `unwatch_process` 뿐이고
  바꾸는 것은 전부 MCP 쪽 메모리(기준점·이름표·기록 목록)다. 프로세스 종료·우선순위 변경 같은 것은 제공하지 않는다

## 배포 패키지 만들기

```cmd
build-release              :: 자체 포함 — 받는 사람이 .NET 을 설치하지 않아도 된다
build-release framework    :: 런타임 의존 — 작지만 .NET 10 데스크톱 런타임이 필요하다
```

두 가지가 `dist\` 에 나온다.

| | 쓰임 |
|---|---|
| `ChronoLoad-<버전>-<종류>\` | **푼 그대로.** 이 PC 에서 그냥 쓸 때는 안의 `ChronoLoad.exe` 에 바로가기를 만든다 |
| `ChronoLoad-<버전>-<종류>.zip` | 남에게 줄 때 |

둘 다 실행 파일, MCP 브리지, 이 README(스크린샷 포함), 개정 이력을 담는다.

| 종류 | 크기 | 받는 사람에게 필요한 것 |
|---|---:|---|
| 자체 포함 | 약 123 MB | 없음 |
| 런타임 의존 | 약 1.8 MB | [.NET 10 데스크톱 런타임](https://dotnet.microsoft.com/download/dotnet/10.0) |

> 다시 빌드하면 그 폴더를 지웠다 새로 만든다. **바로가기로 띄워 둔 앱은 먼저 닫는다** —
> 열려 있으면 폴더를 지우지 못하고, 스크립트가 그 이유를 알려주며 멈춘다.

## 문서

- [PROJECT.md](PROJECT.md) — 설계서. 요구사항부터 구현에서 막혔던 지점까지
- [CHANGE_LOG.md](CHANGE_LOG.md) — 개정 이력
- [docs/ux-design.html](docs/ux-design.html) — UX 설계서(브라우저로 열면 동작하는 목업)

## 현재 상태

1.0.0. 화면·수집·MCP·스냅샷 창이 모두 동작한다. 데스크톱(외장 GPU 2장)과 노트북
(Lunar Lake 내장 GPU + NPU, 서로 다른 배율의 모니터 2대) 두 기기에서 확인했고,
원격 데스크톱 접속 상태에서도 물리 GPU 열거와 가상 어댑터 필터링을 실측했다.

설계서에 쓰여 있는데 구현되지 않은 항목은 남아 있지 않다. 남은 것은 **실측해야 정할 것**들이다.

- AMD ADLX 경로 — 검증할 하드웨어가 없어 보류
- 배터리 절전 경로 — 코드는 있으나 아직 한 번도 실행된 적이 없다.
  Windows 11의 "항상 절전 모드 사용"은 효과(화면 밝기 감소)가 실제로 적용되는데도
  Win32·WinRT 상태 API가 둘 다 꺼짐으로 답한다. 감지할 방법을 다시 찾아야 한다
- 24시간 누수 테스트 — 4시간 19분까지 늘렸다. **GDI 객체는 누수 없음으로 판정**했고 핸들·스레드도
  추세가 없다. 다만 커밋 메모리는 잡음이 커(σ 5.4MB) 이 길이로는 10MB/24h 목표를 **판정할 수 없다** —
  약 26시간이 필요하다
- 저전력 8코어 노트북의 샘플링 듀티 사이클 — 짧은 순간 관측 1.47%, 4.3시간 적분 0.63%.
  어느 쪽을 목표와 견줄 대표값으로 삼을지 아직 정하지 않았다
- 배포본(단일 파일 + 압축)이 일반 빌드보다 커밋 메모리를 104MB 더 쓴다 — 압축 해제본이
  파일 매핑이 아니라 프라이빗 커밋에 올라가기 때문으로 보인다 (확인 중)
- 내장 GPU의 클럭 값이 현재값이 아니라 최대값으로 보인다 (확인 중)

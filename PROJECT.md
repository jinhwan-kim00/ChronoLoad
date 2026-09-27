# ChronoLoad — 설계서

> GPU 워크로드 중심의 실시간 시스템 모니터. WPF 세로형 위젯 + MCP 서버.

- **문서 버전**: 1.41 — 개정 이력은 [`CHANGE_LOG.md`](CHANGE_LOG.md)
- **최초 작성**: 2026-09-23 · **최종 갱신**: 2026-09-27
- **대상 런타임**: .NET 10 (`net10.0-windows`), Windows 10 20H2 이상 / Windows 11
- **UX 시각 설계서**: [`docs/ux-design.html`](docs/ux-design.html) — 브라우저로 열면 라이브 목업이 동작합니다

---

## 1. 목표와 비목표

### 1.1 목표
ChronoLoad는 **GPU를 쓰는 애플리케이션(추론/학습/렌더링/게임)을 돌려놓고, 모니터 구석에 세워둔 채 곁눈질로 상태를 파악**하는 것을 목표로 한다.

1. CPU · 메모리 · **네트워크 인터페이스별** · **물리 디스크별** · **GPU 어댑터별** 부하를 하나의 화면에서 실시간 차트로 관찰
2. "리셋" 시점 기준의 **구간 통계**(평균/최대/최소)를 누적 — 벤치마크 구간 비교용
3. 숫자·텍스트는 최소화하고 **그래프 형태 인지**를 1차 채널로 사용
4. 화면 전환(탭/페이지) 없이 **스크롤 없는 단일 화면**. 장치가 늘어난 만큼의 세로 압박은 **접기와 자동 접힘**으로 흡수
5. 한 지점을 가리키면 **모든 카드가 같은 시각을 보여주는** 동기화 오버레이로 인과관계를 추적
6. **장치 구성이 런타임에 바뀌어도**(Wi-Fi 켜기, eGPU 연결, 디스크 착탈) 재시작 없이 즉시 반영
7. 모니터링 도구 자신이 부하가 되지 않을 것 (CPU < 1%, 메모리 < 130MB 목표)
8. MCP 서버로 동일 데이터를 에이전트에 노출

### 1.2 비목표 (v1 범위 외)
- 원격 호스트 모니터링, 멀티 머신 대시보드 — **다른 기계를 보는 것**이 비목표다.
  원격 데스크톱으로 접속해 그 기계에서 이 앱을 쓰는 것은 **주요 사용 방식에 든다**(§13)
- 장기 시계열 영속화(DB) 및 과거 구간 리플레이 — v1은 메모리 내 링버퍼만
- 팬 컨트롤/오버클럭 등 하드웨어 제어
- **VPN·터널 인터페이스의 기본 표시** — 이중 계상 때문에 제외한다(§5.3). 설정에서 opt-in
- **스크럽 A/B 동시 비교** — 단일 화면 원칙과 충돌. 구간 비교는 리셋 마커가 담당
- **앱이 실행 중이 아닐 때의 MCP 응답** — 헤드리스 샘플링·자동 실행 모두 없다(§10.1)
- 크로스 플랫폼 UI — 단, `ChronoLoad.Core`는 플랫폼 중립 인터페이스 유지

---

## 2. 요구사항 → 설계 매핑

| # | 요구사항 | 설계 반영 | 상세 |
|---|---|---|---|
| R1 | GPU 애플리케이션 모니터링이 주 용도 | GPU 카드 가중치 1.5, VRAM 스필오버를 1급 경고로 | §5.4, §8.3 |
| R2 | WPF 기반 UI | `net10.0-windows` WPF, MVVM(CommunityToolkit.Mvvm), 커스텀 크롬 | §8 |
| R3 | 다크모드 | `ThemeService` + `ResourceDictionary` 스왑, 시스템 테마 추종 | §8.4 |
| R4 | 리셋 기준 평균/최대/최소 | `StatsAccumulator`(O(1)) + 차트 리셋 마커 | §7.3 |
| R5 | 텍스트 최소화 | 카드당 숫자 4개 + **장치명 라벨 상시**. 오버레이는 예외 | §9.3 |
| R6 | 세로형 스마트폰 레이아웃 | 340 × (장치 구성에서 계산), 최소 300×420 | §9.1 |
| R7 | 탭 없는 단일 화면 | 수직 스택 + 가중치 높이 분배 + 자동 접힘 | §8.6 |
| R8 | GPU 단일 차트에 사용률 + 전용/공유 누적 | 이중 축 조합 차트 | §8.3 |
| R9 | 최대한 잦은 갱신, 저부하 | 2-티어 샘플러(250ms/1000ms) + 적응형 백오프 | §6 |
| R10 | MCP 제공 (앱 실행 중에만) | 인프로세스 HTTP 서버 + stdio 브리지 | §10 |
| R11 | 다중 GPU 필수 | 어댑터당 카드, DXGI + LUID 조인, 외장/내장 분기 | §5.4 |
| R12 | 디스크 모니터링 | `PhysicalDisk` PDH, 물리 디스크별 카드 | §5.5 |
| R13 | 항목별 접기 | 28px 스트립 + 가중치 분배 + 자동 접힘 | §8.6 |
| R14 | 온도·전력 오버레이 + 전 카드 시간 동기화 | 단일 `ScrubState` 구독 | §8.7 |
| **R15** | **제조사·매체 아이콘** | GPU=제조사 글리프+브랜드색, 디스크=SSD/HDD, 네트워크=연결 종류 | §8.8 |
| **R16** | **네트워크 인터페이스 분리** | 인터페이스당 카드. **VPN·터널 제외** | §5.3 |
| **R17** | **장치 변경 실시간 반영** | `DeviceWatcher` — 이벤트 구독 + 디바운스 + diff 적용 | §5.7 |
| **R18** | **실제 장치 이름 확인** | 헤더 라벨 **상시 표시** + 툴팁 전체 사양 + 오버레이 첫 줄 | §9.3 |

---

## 3. 시스템 아키텍처

```
┌──────────────────────────────────────────────────────────────┐
│  ChronoLoad.App (WPF, net10.0-windows)                       │
│  ┌────────────────┐  ┌──────────────────┐  ┌──────────────┐  │
│  │ MainWindow     │  │ ViewModels       │  │ ThemeService │  │
│  │  CardHost      │◄─┤ (MVVM Toolkit)   │  │ LayoutEngine │  │
│  │   ChartCard xN │  │  ScrubState      │  │ PresetStore  │  │
│  │   ChartSurface │  │  DeviceCatalog   │  │ WindowState  │  │
│  └────────────────┘  └────────┬─────────┘  └──────────────┘  │
└───────────────────────────────┼──────────────────────────────┘
                                │ 스냅샷 · 장치 diff
┌───────────────────────────────▼──────────────────────────────┐
│  ChronoLoad.Core                                             │
│  ┌──────────────┐ ┌───────────────┐ ┌─────────────────────┐  │
│  │ SampleEngine │ │ MetricRegistry│ │ StatsAccumulator    │  │
│  │ (2-tier tick)│►│  MetricId →   │►│ (리셋 기준 통계)      │  │
│  └──────┬───────┘ │  MetricSeries │ └─────────────────────┘  │
│         │         └───────────────┘                          │
│  ┌──────▼────────────────────────────────────────────────┐   │
│  │ DeviceWatcher — 장치 추가·제거·상태변경 이벤트 → diff    │   │
│  └───────────────────────────────────────────────────────┘   │
└─────────┼────────────────────────────────────────────────────┘
          │ ISensorProvider[] (장치 수만큼 동적 생성/폐기)
┌─────────▼────────────────────────────────────────────────────┐
│  ChronoLoad.Sensors (net10.0-windows)                        │
│  CpuProvider │ MemoryProvider                                │
│  NetProvider xN (인터페이스) │ DiskProvider xN │ GpuProvider xN │
│  ProcessProvider (NtQuerySystemInformation + GPU Engine PDH)  │
└──────────────────────────────────────────────────────────────┘
          ▲
┌─────────┴────────────────────────────────────────────────────┐
│  ChronoLoad.Mcp — Streamable HTTP  http://127.0.0.1:7667/mcp │
└─────────▲────────────────────────────────────────────────────┘
          │ 프록시 (앱이 살아있을 때만)
┌─────────┴────────────────────────────────────────────────────┐
│  chronoload-mcp.exe  — stdio ↔ HTTP 브리지                    │
└──────────────────────────────────────────────────────────────┘
```

### 3.1 프로젝트 구성
```
ChronoLoad.sln
├─ src/
│  ├─ ChronoLoad.Core/          # 모델, 링버퍼, 통계, 샘플 엔진, 레이아웃 엔진, DeviceWatcher 추상화
│  ├─ ChronoLoad.Sensors/       # Windows 구현 (PDH, NVML, ADLX, IGCL, IpHlpApi, DXGI, IOCTL)
│  ├─ ChronoLoad.Mcp/           # MCP 툴/리소스 + HTTP 호스트
│  ├─ ChronoLoad.Mcp.Bridge/    # stdio ↔ HTTP 프록시 (단일 파일 게시)
│  └─ ChronoLoad.App/           # WPF UI (진입점)
└─ tests/
   ├─ ChronoLoad.Core.Tests/
   └─ ChronoLoad.Sensors.Tests/  # 하드웨어 통합 테스트 (CI에서 skip)
```

### 3.2 의존성 정책
| 패키지 | 용도 | 라이선스 | 비고 |
|---|---|---|---|
| `CommunityToolkit.Mvvm` | MVVM 소스 생성기 | MIT | |
| `Microsoft.Extensions.Hosting` | DI/로깅/수명주기 | MIT | |
| `ModelContextProtocol.AspNetCore` | MCP 서버 | MIT | 공식 C# SDK |
| `LibreHardwareMonitorLib` | 온도/전력 폴백 | MPL-2.0 | **선택적**. 수정 없이 참조만 하므로 소스 링크 고지로 충족 |
| 차트 라이브러리 | — | — | **사용하지 않음** (§8.1) |

---

## 4. 핵심 설계 원칙

1. **샘플링과 렌더링의 분리** — 센서 스레드는 UI 스레드를 블로킹하지 않는다.
2. **할당 제로 지향** — 정상 루프에서 Gen0 할당이 없도록 슬롯 기반 버퍼, 사전 할당 링버퍼, `StreamGeometry` 재사용.
3. **부분 실패 허용** — 센서 하나가 죽어도 나머지는 계속 동작.
4. **단일 진실 원천** — UI와 MCP가 같은 `SampleEngine`을 읽는다.
5. **보이는 것만 계산** — 최소화/가려짐이면 렌더 중단. 접힌 카드는 렌더하지 않지만 **샘플링과 통계는 계속한다**.
6. **장치 집합은 런타임 변수다** — 개수를 컴파일 타임에 가정하지 않는다. 더 나아가 **실행 중에 바뀔 수 있다**(§5.7). 모든 자료구조와 설정이 이를 전제로 설계된다.

---

## 5. 데이터 수집 계층

```csharp
public interface ISensorProvider : IDisposable
{
    string Id { get; }                      // "cpu", "gpu:LUID", "disk:SERIAL", "net:GUID"
    SensorTier Tier { get; }                // Fast(250ms) | Slow(1000ms) | Lazy(2000ms)
    DeviceInfo Info { get; }                // 표시 이름, 전체 사양, 아이콘 종류, 벤더
    bool IsAvailable { get; }
    ReadOnlySpan<MetricId> Metrics { get; }
    ValueTask InitializeAsync(CancellationToken ct);
    void Sample(SampleWriter w);            // 할당 없이 슬롯에 기록
}

public sealed record DeviceInfo(
    string Key,        // 안정 식별자 — LUID / 디스크 시리얼 / 인터페이스 GUID
    string ShortName,  // 헤더 라벨용     "RTX 4090", "990 PRO NVMe", "Wi-Fi · HOME-5G"
    string FullName,   // hover 툴팁용    "NVIDIA GeForce RTX 4090 · 외장 · 24 GiB GDDR6X · 드라이버 566.36"
    IconKind Icon,     // Nvidia/Amd/Intel/Ssd/Hdd/Ethernet/WiFi/Cellular/…
    string? Vendor);
```

`ShortName` / `FullName` 을 프로바이더가 직접 제공하는 것이 R18의 핵심이다. UI는 문자열을 조립하지 않는다.

### 5.1 CPU
- **1차**: PDH `\Processor Information(_Total)\% Processor Utility` — 터보/코어 파킹 반영, 작업 관리자와 일치. 100% 초과 가능하므로 클램프
- **폴백**: `% Processor Time`
- **보조(Slow)**: 논리 코어별 사용률 — 오버레이 코어 미니 바 ✅ 구현됨. `\Processor Information(*)\% Processor Utility` 와일드카드, `_Total` 이 붙은 합계 인스턴스는 제외(그룹 합계 `0,_Total` 과 전체 합계 `_Total` 둘 다 온다)
  - **총합과 프로바이더를 나눈 이유는 티어다.** 총합은 Fast(250ms), 코어별은 Slow(1000ms). 와일드카드 열거를 Fast 에 두면 아끼려던 비용이 그대로 돌아온다
  - **CPU 장치에 채널로 붙는다.** 코어를 장치로 등록하면 카드가 코어 수만큼 생기고, 지표 종류로 등록하면 `CpuCore0..63` 열거가 필요하다 — 둘 다 코어 수가 화면과 MCP 지표 목록에 새어 나간다. 채널은 슬롯만 받고 종류를 늘리지 않는다
  - **코어별도 시계열로 들고 있다.** 오버레이는 스크럽 시점을 읽으므로 최신 값만 두면 과거를 고정했을 때 그 줄만 현재가 되어 같은 패널의 총 사용률과 아귀가 맞지 않는다. 16코어 × 15분 = 링버퍼 16개로, 실측에서 문제 되는 크기가 아니다
  - 값이 없는 코어(파킹·이번 틱 누락)는 **빈 눈금**으로 남긴다. 0% 로 그리면 "쉬는 중"으로 읽히는데 실제로는 "모른다"다
  - 실측(Core Ultra 5 226V, 8코어): `87/90/86/88 · 65/53/50/78%` — P코어 4 와 LP-E코어 4 가 그대로 갈린다

#### 코어 종류는 막대의 진하기로 구분한다

`GetSystemCpuSetInformation` 의 `EfficiencyClass` 를 쓴다. **값이 클수록 성능 지향**이고 0 이
가장 효율 지향이다. 등급의 개수와 의미는 제조사가 정하므로 "1이면 P코어"처럼 고정해 읽지 않고
등급끼리 비교만 한다. 부팅 후 바뀌지 않는 값이라 시계열이 아니라 장치 정보(`coreEfficiencyClasses`)로 싣는다.

실측(Core Ultra 5 226V): 논리 0~3 이 등급 1, 4~7 이 등급 0. 코어별 사용률 분포와 정확히 맞는다.

| 방법 | 왜 안 골랐나 / 골랐나 |
|---|---|
| 등급끼리 모아 **틈**을 준다 | 막대 위치가 코어 번호와 어긋나고, 등급이 셋 이상인 칩에서 틈이 계속 늘어난다 |
| 등급마다 **다른 색** | 색은 이미 카드의 지표 종류를 말한다. 한 화면에서 같은 기호에 두 뜻을 얹으면 둘 다 흐려진다 |
| **같은 색의 진하기** ✅ | 효율 등급은 순서가 있는 값이라 순차 스케일이 형태상 맞고, 등급이 몇이든 그대로 늘어난다. 자리는 코어 번호 그대로 남는다 |

- 가장 옅은 단계도 알파 `0x99` 아래로 내리지 않는다. 더 내리면 값이 작은 효율 코어가
  **빈 눈금(값 없음)처럼** 보인다 — 두 상태가 같은 그림이 되면 안 된다
- 막대 아래에 `진할수록 고성능` 을 적는다. 설명 없는 진하기 차이는 그냥 색이 다른 막대로 보인다
- 등급을 못 읽거나 전부 같으면 진하기를 쓰지 않는다. 구분할 것이 없으면 구분하는 척하지 않는다

> **오버레이는 차트 행과 푸터 행에 걸친다.** 한 행에 가두면 카드가 짧을 때 패널 아래가 잘린다 —
> 코어 막대처럼 줄이 늘어나는 내용에서 바로 드러났다. 스크럽 중에만 보이는 요소라
> 푸터를 잠깐 덮는 편이 내용이 잘리는 것보다 낫다.
- `FullName`은 레지스트리 `HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0\ProcessorNameString` + 코어/스레드 수

### 5.2 메모리
- `GlobalMemoryStatusEx` → `ullTotalPhys`, `ullAvailPhys`
- 커밋: `GetPerformanceInfo` → `CommitTotal`, `CommitLimit`
- 표시: 물리 사용량(영역) + 커밋 차지(얇은 라인)
- `FullName`은 `Win32_PhysicalMemory`(WMI, 시작 시 1회 비동기)로 용량·속도·슬롯 수

### 5.3 네트워크 — 인터페이스별 (R16)

**열거**: `GetIfTable2`(IpHlpApi) 1회 호출로 전 인터페이스의 `InOctets`/`OutOctets`/`Type`/`OperStatus`/`Alias`/`Description`/`TransmitLinkSpeed`를 한 번에 가져온다. 인터페이스마다 개별 P/Invoke를 돌리지 않는다.

**종류 판정** (`MIB_IF_ROW2.Type` 기준)

| 종류 | 판정 | 아이콘 |
|---|---|---|
| 이더넷 | `IF_TYPE_ETHERNET_CSMACD(6)` + 엔드포인트 플래그 없음 | RJ45 플러그 |
| Wi-Fi | `IF_TYPE_IEEE80211(71)` | 전파 아크 |
| 셀룰러 | `IF_TYPE_WWANPP(243)` / `IF_TYPE_WWANPP2(244)` | 신호 막대 |
| **VPN / 터널** | `IF_TYPE_TUNNEL(131)`, `IF_TYPE_PPP(23)`, 또는 `InterfaceAndOperStatusFlags.EndPointInterface` | **식별해서 제외** |
| 기타 / 가상 | 위에 해당 없음 | 양방향 화살표 |

**VPN·터널을 표시하지 않는 이유**
터널을 지나는 바이트는 **하위 물리 NIC에도 그대로 계상**된다. 1GB를 VPN으로 내려받으면 VPN 카드에 1GB, 이더넷 카드에도 1GB가 찍힌다. 같은 트래픽이 두 카드에 나타나면 "지금 회선을 얼마나 쓰고 있나"에 틀린 답을 주게 된다. 실제 회선 사용량은 하위 물리 인터페이스가 정확히 보여주므로 **잃는 정보가 없다**. 설정에 `showTunnelInterfaces`(기본 `false`)를 두고, 켜면 해당 카드 오버레이에 이중 계상 주의를 함께 표시한다.

**어떤 인터페이스를 카드로 만드는가** — 일반적인 Windows 기기에는 Hyper-V·WSL·Bluetooth PAN·루프백 등 가상 어댑터가 10개 넘게 있다.

| 단계 | 규칙 |
|---|---|
| 1. 제외 | `IF_TYPE_SOFTWARE_LOOPBACK`, `OperStatus != IfOperStatusUp`, 터널(위) |
| 2. 기본 경로 우선 | `GetBestRoute2`로 기본 게이트웨이를 가진 인터페이스를 찾아 **기본 펼침**. 나머지는 접힘 |
| 3. 사용자 결정 우선 | 설정의 인터페이스별 표시/숨김이 2번 규칙을 덮어쓴다 |

> **무트래픽 5분 숨김은 걷어냈다.** 가상 어댑터는 1번의 NDIS 필터·터널 제외가 이미 거르므로
> 이 규칙이 할 일이 없었고, 구현도 호출되지 않는 죽은 코드로만 남아 있었다.

**Wi-Fi 밴드·SSID·신호 세기는 읽지 않는다.** 성능은 링크 속도에 그대로 드러난다 —
밴드가 2.4GHz 인지 5GHz 인지, SSID 가 무엇인지를 따로 읽어도 **판단이 달라지지 않는다.**
`WlanOpenHandle` 경로 하나를 들이고 알림까지 구독할 값이 아니다.

**표시**
- 0선 기준 미러 차트 — 위 = 수신, 아래 = 송신
- 축 스케일은 **인터페이스별로 독립**. 2.5GbE와 Wi-Fi를 같은 축에 올리면 Wi-Fi가 바닥에 붙는다
- 링크 속도를 아는 인터페이스는 축 상한을 **링크 속도로 캡**해서 회선 점유율이 위치로 읽히게 한다
- 32bit 카운터 랩어라운드 방어 필요

### 5.4 가속기 — GPU와 NPU (R11)

**열거의 원천은 PDH 인스턴스다.** 설계 단계에서는 DXGI(또는 D3DKMT)로 어댑터를 찾고 카운터를 붙이려 했지만,
실기기 검증에서 뒤집었다.

| 관측 | 결론 |
|---|---|
| `GPU Adapter Memory` 인스턴스에 LUID가 4개인데 `D3DKMTEnumAdapters2`는 3개만 반환 | 열거 API가 측정 가능한 장치를 다 알려주지 못한다 |
| 빠진 LUID의 엔진이 `engtype_Neural` | 그게 NPU였다. `EnumAdapters3`에 `Filter=None`을 줘도 나오지 않는다 |
| 남은 하나는 벤더 `0x1414`(Microsoft), `SoftwareDevice` 비트 | WARP 소프트웨어 렌더러. NPU와 겉모습(디스플레이 없음·전용 메모리 0·이름 없음)이 같다 |

그래서 **측정할 수 있는 것(PDH 인스턴스)을 먼저 찾고**, D3DKMT는 이름·용량·벤더를 채우는 보조 자료로만 쓴다.

1. `\GPU Adapter Memory(*)\Dedicated Usage` 와일드카드로 LUID 집합을 얻는다
2. `D3DKMTEnumAdapters3`(없으면 `2`)로 LUID → 이름·전용/공유 용량·벤더 ID 메타데이터를 만든다
   - `KMTQAITYPE_ADAPTERREGISTRYINFO`(8) → 어댑터 이름
   - `KMTQAITYPE_GETSEGMENTSIZE`(3) → 전용·공유 메모리 크기
   - `KMTQAITYPE_PHYSICALADAPTERDEVICEIDS`(31) → **벤더 ID**. 실기기에서 `0x8086`·`0x10DE`·`0x1414`를 정확히 반환했다
   - DXGI COM 대신 D3DKMT를 쓴 이유: 필요한 값이 전부 평범한 구조체로 나와 COM 인터페이스를 손으로 정의할 필요가 없다
3. **소프트웨어 렌더러 제외** — `SoftwareDevice` 비트 또는 벤더 `0x1414`.
   네이티브 계층은 걸러내지 말고 `IsSoftware` 플래그로 **보고만** 해야 한다. 여기서 빼버리면
   프로바이더가 그 LUID를 "D3DKMT가 모르는 어댑터"로 보고 NPU로 오인한다(실제로 겪었다)
4. **NPU 판정은 `engtype_Neural` 유무**로 한다. 작업 관리자가 NPU를 보여주는 근거도 같은 카운터다.
   친숙한 이름(`Intel(R) AI Boost`)은 PnP `ComputeAccelerator` 클래스
   (`{f01a9d53-3ff6-48d2-9f97-c8a7004be10c}`) 레지스트리의 `DriverDesc`에서 가져온다
5. 가속기당 슬롯 4개(사용률·Compute·전용·공유) 등록. 온도·전력·클럭은 벤더 네이티브 경로(M7)에서 추가

**제조사 판정 (R15)** — `KMTQAITYPE_PHYSICALADAPTERDEVICEIDS`의 PCI Vendor ID 하나로 끝낸다. `0x10DE` NVIDIA, `0x1002`/`0x1022` AMD, `0x8086` Intel, `0x5143` Qualcomm, 그 외 일반. **모델명 문자열 파싱은 하지 않는다** — 로캘·드라이버 버전에 따라 형식이 달라진다.

**외장/내장/NPU 판정**

| 종류 | 조건 | 차트 | 아이콘 |
|---|---|---|---|
| 외장 GPU | 전용 VRAM ≥ 1 GiB | 조합 차트 + 용량선 + 스필오버 경고 | 제조사 글리프, 채운 배지 |
| 내장 GPU | 전용 VRAM < 1 GiB, `engtype_Neural` 아님 | 조합 차트, 용량선·경고 없음 | 제조사 글리프, 외곽선 배지 |
| **NPU** | 엔진이 `engtype_Neural` 뿐 | **사용률 단일 영역 차트** | NPU 글리프, 외곽선 배지 |

> **NPU에 누적 메모리 차트를 얹지 않는 이유** — 전용 VRAM도 용량선도 없어서 빈 영역만 늘어난다.
> 연산 전용 가속기에서 알고 싶은 것은 "지금 쓰이고 있는가" 하나다. 가중치도 1.0으로 두어 GPU(1.5)보다 낮다.

**계층 A — WDDM 성능 카운터 (벤더 무관, 항상 시도)**

| 데이터 | 카운터 |
|---|---|
| 엔진별 사용률 | `GPU Engine(*)\Utilization Percentage` |
| 어댑터 전용 메모리 | `GPU Adapter Memory(*)\Dedicated Usage` |
| 어댑터 공유 메모리 | `GPU Adapter Memory(*)\Shared Usage` |
| 프로세스별 GPU 메모리 | `GPU Process Memory(*)\Local Usage` / `Non Local Usage` |

- **사용률 집계**: 인스턴스명의 LUID로 어댑터를 분리 → `engtype`별 그룹핑 → **그룹 내 합산, 그룹 간 최댓값**. 단순 합산하면 100%를 넘는다
- **엔진 계열 지표**: 같은 결과에서 계열별 값을 뽑아 어댑터마다 **`Gpu3D`·`GpuCompute`·`GpuCopy`·`GpuVideo`** 네 시계열로 둔다. 계열 안의 `engtype` 끼리는 최댓값이다(위 정의를 계열 단위로 옮긴 것). 이력·구간 통계가 다른 지표와 똑같이 나온다 — 병목이 어느 엔진인지는 스냅샷 한 장이 아니라 시간에 걸친 모양으로 봐야 하기 때문이다

  | 계열 | `engtype` (대소문자 무시) |
  |---|---|
  | Compute | `compute` 를 **포함**하는 이름(`compute`, `Compute_0`, `High Priority Compute`), `cuda*`, `neural*` |
  | 3D | `3d` 를 포함하는 이름(`3d`, `High Priority 3D`), `graphics*` |
  | Copy | `copy*` |
  | Video | `video*`(decode·encode·processing), `jpeg*` |
  | (넣지 않음) | `security*`, `ofa*`, `vr`, `gsc`, 이름 없음 |

  규칙은 `Core/Metrics/GpuEngineFamilies` 한 곳에 둔다.

- **AI 작업이 어느 엔진에 잡히는가 — 제조사마다 다르다.** WDDM 의 엔진 종류(`DXGK_ENGINE_TYPE`)에는 3D·영상·복사·암호는 있어도 **연산이 없다.** `Compute`·`Cuda`·`Neural` 은 전부 드라이버가 `OTHER` 엔진에 붙인 이름(`DXGK_NODEMETADATA.FriendlyName`)이라 제조사·드라이버·설정마다 다르다. 같은 추론이 GPU 마다 다른 이름으로 나타난다

  | 어댑터 | AI 연산이 실리는 엔진 | 함께 보는 것 | 근거 |
  |---|---|---|---|
  | NVIDIA · **HAGS 켜짐**(Windows 11 기본) | **`3d`** — `Compute_0`·`Cuda` 노드가 따로 보고되지 않고 3D 노드 하나로 합쳐진다. 커널 실행 시간은 NVML `GpuUtil`(SM) | `GpuMemBusy`, `GpuCopy`, 전용 메모리, 전력·클럭 | RTX 5080 실측: CUDA 필터(`bilateral_cuda`) 중 `3d` 94.9%·`copy` 2.4%, `compute` 인스턴스 없음. 하네스로 `GpuUtil` 최대 97%·`Gpu3D` 최대 97.1%·`GpuCompute` 0% |
  | NVIDIA · HAGS 꺼짐 | `Compute_0`·`Compute_1`·`Cuda` | 위와 같음 | 공개 보고 — HAGS 를 끄면 Cuda 그래프가 돌아온다 |
  | Intel Arc 외장 | `compute`(CCS). 커널에 따라 `3d`(렌더). **하드웨어 카운터 `GpuRenderCompute`(250ms)가 믿을 만하다** — PDH 엔진 값은 튄다(아래 Intel 주의점 8) | `GpuCopy`(호스트↔VRAM), 전용 메모리 | B580 엔진 목록 `3d · compute · copy · videodecode · videoprocessing · gsc` |
  | Intel Arc 내장 | **`Neural`** 이 `compute` 자리를 대신한다. 오래된 내장은 렌더(`3d`) | 공유 메모리 | 사용자 실측(Arc 내장). NPU 의 `neural` 과 이름이 같지만 NPU 판정은 "엔진이 `neural` 뿐"이라 겹치지 않는다 |
  | AMD | `Compute_N`, `High Priority Compute`. DirectML 은 3D 큐를 쓰기도 한다 | `GpuCopy`, 전용 메모리 | |
  | NPU | `neural` 하나 | 공유 메모리 | |

  HAGS 는 어댑터마다 `D3DKMT_WDDM_2_7_CAPS.HwSchEnabled`(`KMTQAITYPE` 70)로 읽어 부가 정보 `hardwareScheduling` 에 둔다(이 PC: RTX 5080·B580 둘 다 `true`, NPU 는 D3DKMT 미열거라 `unknown`). MCP 는 이 표를 어댑터마다 **`aiSignals`**(`primary`·`supporting`·`note`)로 준다(§10.2). 엔진 인스턴스를 보고 정하지 않는 것은, PDH 엔진 인스턴스가 그 엔진을 쓰는 프로세스가 있을 때만 나타나 유휴 때는 판단할 근거가 없기 때문이다

- **PCIe 처리량 `GpuPcieRx`·`GpuPcieTx`** (B/s): Rx = 호스트→GPU(업로드), Tx = GPU→호스트(다운로드). 복사 엔진 사용률(`GpuCopy`)은 엔진이 바빴던 **시간**이지 옮긴 **양**이 아니다 — 실측으로 `GpuCopy` 최대 28.8% 인 구간에 PCIe 는 5 GB/s 였다

  | 경로 | 결과 |
  |---|---|
  | NVML `nvmlDeviceGetFieldValues` 의 누적 바이트 필드 197(TX)·198(RX) | **쓴다.** 차분이라 두 읽기 사이의 평균이다. 1초마다 **샘플링 스레드 밖의 전용 타이머**가 읽는다 — 따로 부르면 0.02~0.7ms 인데 샘플링 틱 안에서는 평균 3.8ms 가 걸렸고(드라이버가 값을 갱신하느라 기다린다), 매 틱 읽었더니 듀티 사이클이 0.92% → 2.30% 로 올랐다(같은 조건 A/B). 타이머로 옮긴 뒤 0.88~0.89%. 카운터가 줄면(되감김) 그 표본은 버린다 |
  | NVML `nvmlDeviceGetPcieThroughput` | 쓰지 않는다. 호출 안에서 짧은 구간을 재는 블로킹 호출이라 RX·TX 두 번에 55~75ms, 값은 그 순간의 창이라 1초 사이에 2 MB/s ↔ 341 MB/s 로 튄다 |
  | IGCL | **없다.** `ctl_pci_state_t` 는 링크 세대·폭·최대 대역폭뿐이다 |
  | Level Zero `zesDevicePciGetStats` | 누적 rx/tx 바이트가 있지만 Windows B580 에서 `0x78000003`(`ZE_RESULT_ERROR_UNSUPPORTED_FEATURE`). 하네스 `--l0-probe` 로 확인 |

- **`GpuMemBusy`**: NVML `utilization.memory` — 메모리 컨트롤러가 VRAM 을 읽고 쓴 시간 비율(%). `nvmlDeviceGetUtilizationRates` 한 번에 SM 사용률과 함께 오므로 **추가 호출 없이** 매 틱 받는다. `GpuUtil` 이 낮은데 이것이 높으면 연산이 아니라 VRAM 대역폭이 병목이다(LLM 디코드가 전형). 실측: CUDA 필터 부하에서 SM 99%·메모리 4~7% — 연산에 묶인 부하. NVIDIA 외에는 값이 없다(IGCL 의 VRAM 대역폭 카운터는 B580 에서 `bSupported=false`)

- **전력 한도와 클럭 제한 사유**: AI 부하의 흔한 천장은 연산 유닛이 아니라 전력이다 — RTX 5080 CUDA 부하 중 전력 360 W / 한도 360 W 로 붙어 있었고 제한 사유가 `0x4`(SW 전력 상한)였다. 1초에 한 번(전체 읽기) 읽는다

  | 지표 | 뜻 | NVIDIA (NVML) | Intel (IGCL) |
  |---|---|---|---|
  | `GpuPowerLimit` (W) | 드라이버가 지금 강제하는 한도 | `nvmlDeviceGetEnforcedPowerLimit` — 모든 제한을 반영한 값 | **없음.** `ctlPowerGetLimits` 는 성공하지만 B580 은 지속·버스트·최대 한도와 기본 TDP 가 전부 `-1`. 텔레메트리 v1 의 `gpuPowerPercent` 도 부하 중 0 에 고정 — 쓰지 않는다 |
  | `GpuThrottlePower` (%) | 전력 계열 제한이 섰으면 100, 아니면 0 | `SwPowerCap`(0x4), `HwPowerBrakeSlowdown`(0x80) | `gpuPowerLimited`, `gpuCurrentLimited` |
  | `GpuThrottleThermal` (%) | 온도 계열 | `SwThermalSlowdown`(0x20), `HwThermalSlowdown`(0x40) | `gpuTemperatureLimited` |
  | `GpuThrottleOther` (%) | 그 밖의 병목 | `HwSlowdown`(0x8, 원인 비트 없이 설 때만), `BoardLimit`(0x200) | — |

  **스로틀을 0/100 시계열로 두는 이유**: 1초 표본이라 구간 평균이 곧 "그 제한에 걸려 있던 시간 비율"이고, 이력·포화 비율(§7.3)이 다른 지표와 똑같이 나온다. 비트마스크를 그대로 두면 통계를 낼 수 없다. 사유 전체는 벤더 중립 `GpuLimitReasons` 로 옮겨 MCP 가 최근 값을 `limitReasons` 이름 목록과 `throttling`(병목이 하나라도 섰는가)으로 보고한다. NVML 은 `nvmlDeviceGetCurrentClocksEventReasons`(R535~)를 먼저 쓰고 없으면 같은 비트의 옛 이름 `…ThrottleReasons` 로 내려간다

  **병목이 아닌 사유는 세지 않는다.** `Idle`(0x1)·IGCL `gpuUtilizationLimited` 는 할 일이 없는 것이고, NVML `Reliability`(0x400)·IGCL `gpuVoltageLimited` 는 **전압-클럭 곡선의 끝(천장)** 이다. RTX 5080 은 유휴 내내 `0x400` 이었다 — 처음에 병목으로 셌더니 18초 측정의 44% 가 "그 밖 제한"으로 나왔고 전부 유휴 구간이었다. `limitReasons` 에는 그대로 싣되 `throttling`·스로틀 지표에서는 뺀다
- **비용**: `GPU Engine(*)` 인스턴스는 수백 개. 와일드카드 쿼리는 Slow 티어에 두고 **한 번의 결과를 전 어댑터가 나눠 쓴다**
- 카운터명은 인덱스 기반(`PdhLookupPerfNameByIndex`)으로 해석, 실패 시 영문명

**계층 B — 벤더 네이티브 (있으면 우선, Fast 티어)**

*NVIDIA — `nvml.dll`* ✅ 구현됨
- `nvmlDeviceGetUtilizationRates`, `nvmlDeviceGetMemoryInfo`, `nvmlDeviceGetTemperature`, `nvmlDeviceGetPowerUsage`, `nvmlDeviceGetClockInfo`
- 어댑터 매칭: `nvmlDeviceGetPciInfo_v3` → PCI 주소. WDDM 쪽 주소는 `KMTQAITYPE_ADAPTERADDRESS`(6)로 얻는다.
  **LUID와 벤더 장치 목록을 잇는 공통 좌표가 PCI 주소뿐이다**
- `nvmlPciInfo_t`는 고정 길이 char 배열을 품고 있어 소스 생성 마샬러(`LibraryImport`)가 다루지 못한다.
  필요한 값은 bus·device 둘뿐이라 버퍼로 받아 오프셋(20·24)으로 읽는다
- **NVML이 붙으면 사용률이 Fast 티어로 올라간다.** 실측: 같은 5초 구간에서 PDH 엔진 경로는 5샘플,
  NVML 경로는 19샘플

*AMD — ADLX(`amdadlx64.dll`) / 레거시 `atiadlxx.dll`*
`IADLXGPUMetrics`로 사용률·VRAM·온도·전력·클럭·팬을 한 번에. 매칭은 `IADLXGPU::UniqueId` / PCI 주소.

*Intel — 2단 경로*

Intel은 iGPU와 Arc dGPU를 같은 API 계열로 다룰 수 있어 투자 대비 효과가 크다. **IGCL을 1차, Level Zero Sysman을 2차**로 둔다.

| 항목 | IGCL (1차) | Level Zero Sysman (2차) |
|---|---|---|
| 라이브러리 | `ControlLib.dll` (드라이버 동봉) | `ze_loader.dll` (oneAPI 런타임) |
| 초기화 | `ctlInit` → `ctlEnumerateDevices` | `zesInit(0)`. 구버전 로더는 프로세스 시작 **전에** `ZES_ENABLE_SYSMAN=1` 필요 |
| 어댑터 매칭 | `ctl_device_adapter_properties_t.adapter_id`(**LUID**) → DXGI와 직접 조인 | `zes_pci_properties_t.address`(domain:bus:device.function) → PCI 주소 경유 |
| 사용률 | `ctlEnumEngineGroups` + `ctlEngineGetActivity` | `zesDeviceEnumEngineGroups` + `zesEngineGetActivity` |
| 온도 | `ctlEnumTemperatureSensors` + `ctlTemperatureGetState` | `zesDeviceEnumTemperatureSensors` + `zesTemperatureGetState` |
| 전력 | `ctlEnumPowerDomains` + `ctlPowerGetEnergyCounter` | `zesDeviceEnumPowerDomains` + `zesPowerGetEnergyCounter` |
| 클럭 | `ctlEnumFrequencyDomains` + `ctlFrequencyGetState` (요청/실제 + **스로틀 사유**) | `zesDeviceEnumFrequencyDomains` + `zesFrequencyGetState` |
| 메모리 | `ctlEnumMemoryModules` + `ctlMemoryGetState` / `ctlMemoryGetBandwidth` | `zesDeviceEnumMemoryModules` + `zesMemoryGetState` / `zesMemoryGetBandwidth` |
| 권장 최소 드라이버 | 30.0.101.1660 이상 | oneAPI 런타임 설치 환경 |

**Intel 고유 주의점 — 이걸 놓치면 값이 틀린다**

1. **전력·엔진 활동은 누적 카운터다.** `EnergyCounter`는 μJ 누적, `EngineActivity`는 `activeTime`/`timestamp`(μs) 누적. 전력 = ΔE/Δt, 사용률 = Δactive/Δtimestamp로 계산해야 하며 **첫 샘플에서는 값을 낼 수 없다**. 첫 250ms 동안 `null`을 반환하고 UI는 `—`를 표시한다.
2. **iGPU 전력은 CPU 패키지와 공유될 수 있다.** `ctl_power_properties_t`(또는 `zes_power_properties_t`)의 도메인 종류를 확인하고, GPU 전용이 아니면 오버레이에 `패키지 공유`를 함께 표기한다. GPU 단독 소비량인 것처럼 보여주면 안 된다.
3. **엔진 그룹 명명이 다르다.**

   | UI 분류 | NVML | IGCL / Level Zero | PDH `engtype` |
   |---|---|---|---|
   | 3D / Render | graphics util | `RENDER_ALL` | `engtype_3D` |
   | Compute | sm util | `COMPUTE_ALL` | `engtype_Compute` |
   | Copy | — | `COPY_ALL` | `engtype_Copy` |
   | Media | enc/dec util | `MEDIA_DECODE` / `MEDIA_ENCODE` | `engtype_VideoDecode` / `Encode` |
   | Neural (NPU) | — | — | `engtype_Neural` |

   `*_ALL` 그룹과 개별 인스턴스 그룹이 함께 열거되므로 **둘을 합산하면 두 배가 된다**. `*_ALL`이 있으면 그것만 쓴다.
4. **iGPU는 UMA라 전용 VRAM이 0~512MB다.** 외장/내장 임계값(1 GiB)에 자연히 내장으로 분류되고 차트는 공유 메모리 기준으로 그려진다. **Arc dGPU는 8~16GB 전용 VRAM이라 외장으로 분류**되어 스필오버 경고 대상이 된다.
5. **하이브리드 노트북에서는 iGPU가 디스플레이만 담당하고 연산은 dGPU가 한다.** iGPU 카드를 기본 접힘으로 두는 근거다.
6. **사용률은 `ctlPowerTelemetryGet` 의 활동 카운터로 매 틱 구한다.** 누적 활동 초를 타임스탬프로 나눈 기울기라 두 읽기 사이 **전 구간의 시간 가중 평균**이다 — 250ms 사이의 버스트가 빠짐없이 들어간다. 같은 구조체에 그룹이 셋 온다: 전체(`globalActivityCounter`, 오프셋 128)·렌더+컴퓨트(152)·미디어(176). **전체는 미디어를 세지 않는다** — QSV 인코딩에서 전체 17%, 미디어 200%. 그래서 `GpuUtil` 은 세 그룹의 최댓값이다(무엇이든 돈 시간 — NVML 과 같은 정의). 렌더+컴퓨트는 `GpuRenderCompute` 로, 미디어는 `GpuVideo` 로 매 틱 적는다(PDH 의 1초 Video 계열을 덮는다 — B580 의 QSV 인코딩은 PDH 에서 `copy` 로만 잡혔다). **그룹 카운터는 그룹 안 엔진들의 활동 시간 합이라**(미디어가 1초에 2.02초 = 엔진 둘) 100 으로 자른다 — 100 은 "엔진 하나 몫 이상 바빴다"이다. IGCL 의 엔진 그룹은 이 셋뿐이라 3D·Compute·Copy 를 가르지 못한다. 온도·전력·클럭은 그대로 1초에 한 번이다
7. **클럭은 직전 1초 활동이 0.5% 이상일 때만 낸다.** `gpuCurrentClockFrequency` 는 렌더 블록이 절전(RC6)에 들어가 있어도 마지막 요청 주파수를 돌려준다 — 실사용에서 유휴 B580 이 2850 MHz(최대)로 고정돼 보였다. 이 PC 실측: 깨어난 직후 첫 읽기가 2850 MHz·1.035 V, 이어서 400 MHz·0.74 V, QSV 인코딩 부하에서 550 → 1950 MHz. 돌고 있는 클럭이 없을 때는 값을 비운다(`null`). 0 은 측정값이 아니다. 구조체 버전 1 의 `gpuEffectiveClock` 은 B580 이 지원하지 않는다(`bSupported=false`)
8. **PDH 엔진 값을 Intel 에서 믿지 않는다.** B580 에 OpenCL 연산(ffmpeg `avgblur_opencl`)을 걸자 PDH `compute` 가 1초 간격으로 **1.8e14(쓰레기), 53.2, 143.8, 인스턴스 없음** 순으로 나왔다. 같은 때 하드웨어 카운터는 전체 100%·렌더+컴퓨트 99.5% 로 고르다 — 실사용 보고("GpuUtil 99.9% 일정한데 GpuCompute 가 0↔100")를 그대로 재현한 것이다. 하네스 실측 14초: `GpuRenderCompute` 평균 99.5%(최소 96.7%), PDH `GpuCompute` 평균 3.5%(최대 49%). 한 인스턴스가 1000 을 넘기면 그 표본에서 뺀다 — 100 으로 자르면 "그 1초는 포화"라는 거짓 표본이 된다. PDH 계열 값은 어느 엔진인지를 가를 때만 쓰고 여러 초의 평균으로 읽는다. NVIDIA 는 PDH 값이 고르다(CUDA 부하 1초 간격 95.7~97.7%)

*계층 B′ — `D3DKMTQueryStatistics` (벤더 무관 보조)*
어댑터 세그먼트별 메모리(로컬/논로컬)를 PDH보다 정확하게 분해한다. 특히 Intel에서 UMA를 다루는 방식이 모호할 때 교차검증용.

*계층 C — LibreHardwareMonitorLib* — 온도/전력/팬만 필요할 때, 빌드 플래그로 선택

**어댑터마다 계층이 다를 수 있다** — dGPU는 NVML, iGPU는 IGCL로 동시 동작하는 것이 정상 시나리오다. `describe_capabilities`가 어댑터별 계층을 보고한다.

### 5.5 디스크 — 물리 디스크별 (R12)

**열거**: `PhysicalDisk` PDH 인스턴스(`0 C:`, `1 D: E:`) 또는 `IOCTL_STORAGE_QUERY_PROPERTY`. `_Total`은 제외하고 물리 디스크마다 프로바이더 1개.

**매체 판정 (R15)**
1. `CreateFile(\\.\PhysicalDriveN, FILE_READ_ATTRIBUTES)` — **관리자 권한 불필요**
2. `IOCTL_STORAGE_QUERY_PROPERTY` + `StorageDeviceSeekPenaltyProperty` → `DEVICE_SEEK_PENALTY_DESCRIPTOR.IncursSeekPenalty`
   - `FALSE` → **SSD**, `TRUE` → **HDD**
3. 보조: WMI `MSFT_PhysicalDisk.MediaType`(3=HDD, 4=SSD, 5=SCM) — 시작 시 1회 비동기 조회. WMI는 느리므로 판정 경로가 아니라 이름 보강용
4. 버스 종류: `StorageAdapterProperty` → `STORAGE_BUS_TYPE`(`BusTypeNvme=0x11`, `BusTypeSata=0x0B`, `BusTypeUsb=0x07`) — **아이콘이 아니라 오버레이 텍스트로** 제공
5. 판정 실패 → 일반 드라이브 아이콘

**카운터**

| 데이터 | 카운터 | 티어 |
|---|---|---|
| 읽기 처리량 | `PhysicalDisk(n)\Disk Read Bytes/sec` | Fast |
| 쓰기 처리량 | `PhysicalDisk(n)\Disk Write Bytes/sec` | Fast |
| 활성 시간 | `100 − PhysicalDisk(n)\% Idle Time` | Fast |
| 큐 길이 | `PhysicalDisk(n)\Avg. Disk Queue Length` | Slow ✅ |
| 응답 시간 | `PhysicalDisk(n)\Avg. Disk sec/Transfer` × 1000 | Slow ✅ |

- **`% Idle Time`은 100을 넘거나 음수가 될 수 있다**(샘플 경계) → `Clamp(0,100)` 필수

> **한 프로바이더에 티어는 하나뿐이다.** 디스크 프로바이더는 Fast 인데 큐와 응답 시간은 Slow 다.
> 네 틱에 한 번만 **쓰는** 방식으로 같은 효과를 낸다 — 쓰지 않은 슬롯은 직전 값이 유지되고
> 실측으로 세지 않으므로(§7.2) 통계도 왜곡되지 않는다. 실측 표본 수가 1/4 인 것으로 확인된다.

> `get_disk_status` 는 처음부터 `queueLength`·`latencyMs` 를 약속하고 있었다. 슬롯이 없어
> 늘 `null` 을 돌려주던 것을 이제 채운다 — **계약이 먼저 있고 구현이 늦은 경우**였다.
- 표시: 네트워크와 동일한 미러 차트. 현재값은 읽기+쓰기 합계
- **포화 판정은 처리량이 아니라 활성 시간으로** — 작은 랜덤 I/O는 처리량이 낮아도 디스크를 포화시킨다. 활성/큐/응답시간은 오버레이와 경고 상태로
- **합산 카드는 만들지 않는다** — NVMe 7GB/s와 HDD 200MB/s를 같은 축에 올리면 HDD 포화가 평균에 묻힌다

### 5.6 프로세스 (MCP 전용, Lazy 티어) ✅ 구현됨
- `NtQuerySystemInformation(SystemProcessInformation)` 1회로 전 프로세스 CPU/메모리/스레드/핸들
- GPU는 `GPU Engine(pid_*)` / `GPU Process Memory(pid_*)` 파싱 후 PID 조인. **인스턴스명의 LUID로 어댑터별 분해**까지 제공
- 기본 2초, **MCP 요청이 없으면 수집하지 않는다**(마지막 요청 후 60초 뒤 중단)
- **프로세스별 시계열(`watch_process`)** — 지정한 PID(최대 8개, 최대 1시간)의 어댑터별 엔진 계열·CPU%·워킹셋을 1초마다 기록한다(15분 보관). **추가 쿼리가 없다** — 엔진 값은 GPU 프로바이더가 어댑터 사용률을 구하려고 1초마다 읽는 `GPU Engine(*)` 결과에서 감시 중인 PID 몫만 떼어 온다. CPU·워킹셋은 열어 둔 핸들로 `GetProcessTimes`·`K32GetProcessMemoryInfo`. 감시 있음/없음 듀티 사이클 0.90~0.98% 로 차이가 없다. 처음 보는 엔진은 앞 칸을 `null`(몰랐다)로, 이후 인스턴스가 없는 칸은 0(안 썼다)으로 둔다. 프로세스가 끝나도 기록은 남는다. 엔진 값은 PDH 라 Intel 에서는 §5.4 Intel 주의점 8 의 성질을 그대로 갖는다
- **멈춰 있던 수집을 켤 때는 기준선을 잡고 1초 뒤 한 번 더 수집한 다음에 답한다.** CPU·디스크는 두 수집의 차분이라 기준선만 있는 표는 비율을 모른다. 전에는 그 표를 그대로 돌려줘, 한동안 부르지 않다가 부르면 전부 0 이었다 — 에이전트는 "다 놀고 있다"로 읽었다. 직전 수집에 없던 프로세스(방금 뜬 것)의 비율은 **`null`** 이다

### 5.7 `DeviceWatcher` — 장치 변경 실시간 반영 (R17) ✅ 구현됨

**감지 경로**

| 대상 | 이벤트 소스 | 비고 |
|---|---|---|
| GPU 어댑터 추가/제거 | `RegisterDeviceNotification(GUID_DISPLAY_DEVICE_ARRIVAL)` | **메시지 전용 창**에 등록. DXGI COM 없이 되고 콘솔에서도 검증된다. 실측: eGPU 연결이 13.3초 지점에서 `DisplayAdapter` 로 잡혔다 |
| GPU 드라이버 재시작(TDR) | `DXGI_ERROR_DEVICE_REMOVED` + PDH 인스턴스 소실 | 같은 LUID로 돌아오면 히스토리 유지 |
| 디스크 · 볼륨 | `WM_DEVICECHANGE` (`GUID_DEVINTERFACE_DISK`, `_VOLUME`) | WPF `HwndSource.AddHook`, `RegisterDeviceNotification` |
| 네트워크 인터페이스 추가/제거/Up/Down | `NotifyIpInterfaceChange` (IpHlpApi) | **Wi-Fi 라디오 on/off가 여기로 들어온다** |
| 절전 복귀 · 디스플레이 변경 | `WM_POWERBROADCAST`, `WM_DISPLAYCHANGE` | 델타 기준선 재설정 |

> **크래시를 드라이버 탓으로 돌리기 전에 자기 버퍼부터 세어볼 것.** 이 절의 전원 게이팅과
> 지연 개방은 그 자체로 옳은 설계지만, 당시 관측된 세그폴트의 원인은 아니었다.
> 진짜 원인은 512바이트 버퍼에 1024바이트 구조체를 넘긴 것이었고, 그 힙 손상이
> 실행 중 크래시와 종료 중 크래시로 동시에 나타나 "D3 전이 중이라 죽는다"처럼 보였다.
> 버퍼를 고치자 두 증상이 함께 사라졌다(부하 상태 40/40 무결점).

**전원 상태 게이팅 — 잠든 장치를 깨우지 않는다**

외장 GPU 는 쓰지 않을 때 D3(저전력 대기)로 내려간다. 이때 벤더 SDK 로 온도를 물으면 **장치가 깨어난다.**
모니터링 도구가 감시 대상을 깨우는 것은 그 자체로 틀렸고, 전력을 쓰고, Intel Arc 에서는
전원 전이 중 네이티브 호출이 **프로세스를 세그폴트로 죽였다**(실측 25회 중 6회).

```
매 Slow 틱:  CM_Get_DevNode_Registry_Property(CM_DRP_DEVICE_POWER_DATA)
             → PD_MostRecentPowerState
             → D3 면 벤더 SDK 를 아예 건드리지 않고 슬롯은 공백 처리
개방 시점:   Intel 어댑터가 전부 D3 면 IGCL 을 열지도 않는다 (ctlInit 자체가 깨운다)
             → 깨어난 것이 확인된 뒤에 열고 다시 바인딩한다
```

> **`DEVPKEY_Device_PowerData` 를 쓰는 이유** — 전원 상태를 알아내는 다른 경로(DXGI 메모리 조회,
> 벤더 SDK 질의)는 하나같이 장치를 건드려 깨운다. 이 값은 OS 가 이미 들고 있는 캐시라 읽어도 깨지 않는다.
> 실측으로 12초 내내 조회했지만 Arc 는 `Off` 를 유지했다.
>
> **모르는 상태는 "깨어 있음"으로 본다.** 조회가 실패하는 시스템에서 대기 중으로 간주하면
> 온도·전력이 영영 안 나오고 원인도 보이지 않는다.

**처리 규칙**

```
이벤트 수신
  → 300ms 디바운스 (eGPU 한 번 꽂으면 WM_DEVICECHANGE가 수십 번 온다)
  → 백그라운드 스레드에서 재열거 (샘플링은 멈추지 않는다)
  → 현재 집합과 diff
  → UI 스레드에서 diff만 적용
레이트 리밋: 재열거 최대 초당 1회 / 연속 3회 실패 시 60초 백오프
안전망: 이벤트를 놓쳐도 30초 주기 검증 재열거(장치 키 목록 해시 비교)로 복구
```

| 변화 | 동작 |
|---|---|
| **추가** ✅ | 프로바이더 생성, `MetricRegistry` 슬롯 추가, 카드 등장(320ms 페이드 인), **저장된 접힘 상태**에 따라 접힘/펼침 결정 |
| **제거** ✅ | 프로바이더 폐기, 카드 제거(180ms, 높이·투명도 동시 축소). **시리즈·통계는 60초간 보관** 후 폐기 |
| **재연결** ✅ | 60초 안에 같은 키로 돌아오면 **히스토리·통계가 이어진다**. 슬롯 번호와 장치 인덱스까지 그대로 |
| **Up ↔ Down** | 제거가 아니라 **Unavailable 상태**. 카드 유지, 통계 연속, 차트에 공백 구간 |

> **왜 "제거"와 "Down"을 구분하는가** — Wi-Fi를 껐다 켜거나 드라이버가 재시작되면 장치가 잠깐 사라졌다 돌아온다. 이때 카드를 지웠다 새로 만들면 **리셋 이후 누적 통계가 초기화**되어 벤치마크 구간이 통째로 날아간다. 60초 보관 규칙은 이걸 막는다.

**사용자 알림** — 토스트·배너는 쓰지 않는다(텍스트 최소화 원칙, 그리고 구석의 위젯이 말을 거는 것은 성가시다). **카드의 등장/소멸 자체 + 전역 바 상태 점 개수 변화**가 알림이다. 라벨은 상시 표시이므로 무엇이 생겼는지는 카드에 그대로 적혀 있다. 단 스크린 리더 사용자에게는 보이지 않으므로 `LiveRegion`으로 별도 고지한다.

---

## 6. 샘플링 & 스케줄링

### 6.1 티어 구조
| 티어 | 주기 | 대상 | 근거 |
|---|---|---|---|
| Fast | **250ms** | CPU 총합, 물리 메모리, 인터페이스별 B/s, 디스크 처리량·활성시간, GPU 사용률(NVML·IGCL)·메모리 | PDH/NVML 호출 비용 합계 < 1ms. 사람이 "즉각"으로 느끼는 하한 ~200ms |
| Slow | **1000ms** | GPU 엔진 PDH 와일드카드(엔진 계열 포함), 코어별 CPU, GPU 온도·전력·클럭, 디스크 큐·응답, 커밋 차지 | 인스턴스 열거가 비싸거나 변화가 느린 항목 |
| Lazy | **2000ms** | 프로세스 테이블 | MCP 구독 중일 때만 |

> **왜 250ms인가** — 델타 기반 카운터는 주기가 짧을수록 양자화 노이즈가 커진다. 100ms에서는 값이 튀고 500ms에서는 짧은 스파이크를 놓친다. 250ms면 60초 창에 240포인트로 차트 밀도도 적절하다. 설정에서 100/250/500/1000ms 선택 가능.

> **Slow 티어의 반복값은 통계에서 제외한다.** Fast 틱마다 직전 값이 다시 기록되지만
> `SampleWriter`가 "이번 틱에 실제로 측정했는지"를 함께 표시하고, 시리즈는 전부 기록하되
> `StatsAccumulator`는 실측만 받는다. 반복을 세면 샘플 수가 주기 비율만큼 부풀고,
> 편차가 0인 반복값이 표준편차를 끌어내려 실제보다 안정적으로 보인다.
> 시리즈에는 샘플당 1비트로 실측 여부가 남아 `stale` 판정과 MCP 응답의 실제 해상도 보고에 쓰인다.

> **장치가 늘어도 주기는 유지된다** — `GetIfTable2`와 `PhysicalDisk` 와일드카드는 **개수와 무관하게 1회 호출**이다. 늘어나는 것은 GPU 어댑터당 네이티브 호출 3회(≈0.1ms) 정도. §6.3의 자동 강등이 안전망으로 남는다.

### 6.2 타이밍
- `PeriodicTimer` + 전용 백그라운드 `Task`. WPF `DispatcherTimer`는 UI 부하에 밀리므로 쓰지 않는다
- `timeBeginPeriod`는 **호출하지 않는다** — 시스템 전역 타이머 해상도를 올리면 전력 소비가 늘어난다
- 경과 시간은 `Stopwatch.GetTimestamp()`로 측정(틱 지터 보정)

### 6.3 적응형 백오프 ✅ 구현됨

| 조건 | 동작 | 상태 |
|---|---|---|
| 최소화 / `DwmGetWindowAttribute(CLOAKED)` | 렌더 중단, Fast ×4 (1000ms) | ✅ |
| 다른 창에 완전히 가려짐 | 렌더 중단, 샘플은 유지 | ❌ 아래 참조 |
| 배터리 + 절전 모드 | Fast ×2 (500ms) | ✅ |
| 샘플링이 지속적으로 주기의 20% 초과 | 한 단계 강등, 여유가 돌아오면 복귀 | ✅ |
| 카드가 접힘 | 샘플링 유지, **렌더만 중단**(스파크라인만) | ✅ |

**두 축을 따로 관리한다.** 바깥이 요청한 단계(창·전원)와 내부 과부하 강등을 분리하고 느린 쪽을 쓴다.
하나로 합치면 창을 복원했을 때 과부하 강등까지 함께 풀려, 버거운 기기가 계속 틱을 밀어낸다.

**단일 틱이 아니라 지수이동평균으로 판단하고, 복귀 문턱은 강등 문턱의 절반이다.**
틱별 소요는 실측에서 1300~3800µs 로 세 배 가까이 흔들린다. 한 번 튄 값으로 강등하면
멀쩡한 기기가 계속 느려지고, 두 문턱이 같으면 경계에서 오르내리기를 반복한다.

> **"다른 창에 완전히 가려짐"은 구현하지 않았다.** Windows 가 이를 알려주는 값을 주지 않는다.
> 창 목록을 훑어 사각형을 겹쳐 보는 근사는 계산 자체가 아끼려는 비용만큼 들고 결과도 틀리기 쉽다.
> 최소화와 클로킹이 실제 사용의 대부분을 덮으므로 거기서 멈췄다.

> 현재 단계는 MCP `describe_capabilities` 의 `sampling.pace` 와 `effectivePeriodMs` 로 보인다.
> 이게 없으면 에이전트는 값이 성긴 이유를 알 수 없고 "장치가 조용하다"로 오해한다.

### 6.4 렌더 파이프라인
- 샘플 스레드 → `Dispatcher.InvokeAsync(DispatcherPriority.Render)`로 스냅샷 1건
- **새 샘플이 있을 때만** `InvalidateVisual()` → 250ms 주기에서 최대 4Hz
- 스크럽 중에는 대상 인덱스가 고정이므로 오버레이 내용을 다시 그리지 않는다

---

## 7. 데이터 모델

### 7.1 동적 지표 집합
```csharp
public enum MetricKind : byte {
    CpuTotal, MemUsed, MemCommit,
    NetRx, NetTx,
    DiskRead, DiskWrite, DiskActive, DiskQueue, DiskLatency,
    GpuUtil, GpuCompute, GpuDedicated, GpuShared, GpuTemp, GpuPower, GpuClock
}
public readonly record struct MetricId(MetricKind Kind, byte DeviceIndex);

public sealed class MetricRegistry              // 장치 변경 시 재구성 가능
{
    public int SlotOf(MetricId id);             // O(1)
    public MetricSeries Series(MetricId id);
    public ref StatsAccumulator Stats(MetricId id);
    public void AddDevice(DeviceInfo d, ReadOnlySpan<MetricKind> kinds);
    public void RetireDevice(string key, TimeSpan keepFor);   // 60초 보관 후 폐기
}
```
- 센서는 초기화 시 슬롯 번호를 받아두고 매 샘플마다 `Span<float>`에 직접 쓴다 — 박싱·딕셔너리 조회 없음
- 장치 집합이 바뀌면 레지스트리를 재구성하되 **DeviceInfo.Key(LUID/시리얼/인터페이스 GUID)를 기준으로 기존 시리즈를 이어받는다**

### 7.2 링 버퍼 `MetricSeries`
- 고정 용량 `float[]` + **샘플당 실측 여부 1비트** + 쓰기 인덱스. lock-free 단일 생산자 / 다중 소비자
- 용량 **3600 포인트** = 250ms × 3600 = **15분**
- 메모리: GPU 2 + 디스크 2 + 네트워크 2 기준 지표 약 28종 → 28 × 3600 × 4B ≈ **400KB**
- 차트 창: 60초(기본) / 180초 / 600초. 축소 시 **min-max 데시메이션**으로 극값 보존

### 7.3 리셋 기준 통계
```csharp
public struct StatsAccumulator
{
    public long Count; public double Sum, M2;   // Welford
    public float Min, Max; public long ResetTimestampTicks;
    public void Add(float v); public void Reset(long nowTicks);
}
```
- O(1) 갱신, 고정 메모리
- **분위수(p50·p95·p99)는 두 경로다.** 리셋 이후 프레임이 링 길이(15분) 안이면 링에 그 구간의 실측 표본이 전부 남아 있으므로 **정렬해서 정확히** 구한다(최근접 순위). 넘치면 **상대 오차 ±0.5% 로그 버킷 스케치**(DDSketch 방식)로 근사한다. 어느 쪽이든 답은 관측된 최솟값~최댓값 안으로 자르고, 양 끝 순위는 버킷이 아니라 실제 최솟값·최댓값이다. 어느 경로였는지는 MCP 응답의 `quantilesExact` 가 말한다
  - 리셋 시점의 프레임 번호를 `StatsAccumulator.ResetFrame` 에 둔다. 링에서 떠낼 칸 수는 "리셋 이후 프레임 수"와 "시리즈에 기록된 칸 수" 중 작은 쪽이다 — 장치가 등록된 뒤 샘플 엔진이 버퍼를 다시 잡기까지 몇 프레임은 새 슬롯에 쓰지 않으므로, 프레임 수만 보면 재기동 뒤 첫 구간이 리셋 전까지 내내 근사로 떨어진다(실사용 보고: 165초 구간인데 GPU·디스크만 `quantilesExact=false`). 떠낸 실측 표본 수가 누산기의 `Count` 와 다르면 정확하다고 주장하지 않고 스케치로 돌아간다
- **시각으로 자른 구간**(`get_interval_stats`·`compare_intervals`)은 누산기 없이 링의 실측 표본을 그 자리에서 센다(`Core/Metrics/WindowStatistics`). 정의는 리셋 구간과 같다(최근접 순위, 표본 표준편차, 유지값 제외). 링 밖으로 나간 앞부분은 근사로 메우지 않고 `truncated` 로 알린다 — 사후에 구간끼리 비교하려는 기능이라 구간마다 기준이 다르면 비교가 무너진다
  - **고정 칸 히스토그램을 쓰지 않는 이유**: 바이트 계열을 1 B~16 TB 64칸으로 나누면 칸 하나가 ×1.62 배라 14.6~16 GB 가 12.35 GB 로 답해졌고, 전력을 0~1000 W 64칸으로 나누면 칸 폭 15.9 W 라 유휴 22.5 W 의 p95 가 15.87 W(최소보다 작다)로 나왔다. 서로 다른 지표가 같은 칸 경계에 떨어져 같은 숫자(1097631034)를 내기도 했다. 단위마다 범위를 맞추는 한 되풀이되는 문제다. 로그 스케치는 값의 크기와 무관하게 상대 오차를 보장하고, 메모리는 실제로 관측된 값의 폭에만 비례한다
- **포화 비율**: 백분율 지표는 문턱(기본 90%) 이상이었던 표본의 비율을 함께 낸다. 버스트형 부하에서는 평균이 포화를 가린다 — 평균 62% 인데 100% 구간이 반복되는 경우를 이 숫자가 드러낸다
- **리셋 의미론**: 통계만 초기화하고 차트 히스토리는 유지, 리셋 시점에 수직 마커선. 전역 리셋은 **접힌 카드와 표시하지 않는 지표까지 전부** 초기화한다

---

### 7.4 시간 축 — 샘플에 시각을 붙인다 ✅ 구현됨

`MetricSeries` 는 값만 담는다. 차트는 점 간격이 일정하다고 **가정하고** 그린다.
그런데 §6.3 적응형 백오프가 Fast 주기를 배수로 늘린다(`EffectiveFastPeriod = FastPeriod × EffectivePace`).
절전 복귀나 과부하 구간에서는 더 크게 벌어진다. 즉 **버퍼의 시간 축은 균일하지 않은데 균일한 척 그려 왔다.**

지금까지 탈이 나지 않은 이유는 표시 창이 240점(약 1분)이고 그 사이에 pace 가 바뀌는 일이 드물어서다.
§9.6 스냅샷 창은 사정이 다르다 — 타이틀에 데이터 시작 시각을 적고, 드래그한 구간의 평균을 말하고,
CSV 에 시각 열을 낸다. 셋 다 **시각이 실제로 있어야** 성립한다.

**값 링과 같은 길이의 시각 링을 둔다.** `MetricRegistry` 가 `long[]`(UTC ticks) 하나를 들고
`CommitAll` 이 틱마다 한 칸 쓴다. `SampleEngine` 은 이미 `SampleCommitted.TimestampUtcTicks` 를
들고 있으므로 넘겨받기만 하면 된다.

> 비용은 3600 × 8B = **28.8KB 하나**다. 슬롯 수와 무관하다 — 모든 슬롯이 한 틱에 함께 커밋되므로
> 시각은 슬롯당이 아니라 **틱당** 하나다. 슬롯마다 붙였으면 40배를 냈을 것이다.

> 절전 복귀 공백 표시(§13)는 이 축 없이 구현할 수 없었다. 인접 두 틱의 간격이
> 기대 주기를 크게 넘으면 그 사이가 공백이다 — 그 전에는 그 사실을 알 방법이 아예 없었다.

**API** — `MetricRegistry` 에 붙는다.

| 멤버 | 하는 일 |
|---|---|
| `Frames` | 커밋된 프레임 총수. 시리즈 인덱스와 시각을 잇는 좌표계 |
| `TimestampAtFrame(long)` | 프레임의 커밋 시각. 아직 오지 않았거나 링에서 밀려났으면 `null` |
| `FrameAt(series, index)` | 시리즈 인덱스 → 프레임. 범위 밖이면 −1 |
| `TimestampAt(series, index)` | 위 둘의 합성. 스냅샷 창과 CSV 가 쓴다 |
| `CopyTimestamps(Span<long>)` | 최신 N개를 시간 순으로. `MetricSeries.CopyLatest` 와 같은 규약이라 인덱스가 그대로 대응한다 |

> **인덱스↔프레임 대응은 "시리즈는 첫 기록 이후 매 프레임 한 번씩 기록된다"는 불변식에 기댄다.**
> `CommitAll` 이 유일한 기록 경로이고, 회수된 슬롯도 목록에서 빠지지 않으므로(`ReleaseSlot` 은
> `null` 로만 둔다) 살아 있는 슬롯이 프레임을 건너뛰지 않는다. 늦게 등록된 시리즈는 `Count` 가
> 작을 뿐 **끝이 같다** — 그래서 `Frames − Count + index` 로 환산된다.

> 슬롯 하나만 따로 기록하던 `Commit(slot, …)` 은 **걷어냈다.** 호출하는 곳이 없었고, 남겨 두면
> 그 경로로 쓴 시리즈만 프레임이 어긋나 인덱스↔시각 대응이 조용히 깨진다. 쓰이지 않는 함정이다.

**실측** — 이 노트북에서 20초 관측(`dotnet run --project tools/ChronoLoad.Harness -- 20`).

| 항목 | 값 |
|---|---|
| 틱 : 프레임 | 79 : 79 — 불변식대로 1:1 |
| 구간 | 19.50s (78 간격 × 250ms) |
| 간격 중앙 | 251.9ms (기대 250ms) |
| 간격 최대 | 265.6ms |

---

## 8. 렌더링 계층

### 8.1 차트 엔진: 자체 구현 (결정)
| 후보 | 단점 | 판정 |
|---|---|---|
| LiveCharts2 (SkiaSharp) | 네이티브 ~10MB, 자체 렌더 루프가 상시 CPU 점유, 누적+이중축 커스터마이징 제약 | ✗ |
| ScottPlot 5 WPF | 스타일링 자유도 낮음, 실시간 스택 영역 부적합 | ✗ |
| OxyPlot | 개발 정체, 스택 영역 실시간 갱신 시 재할당 | ✗ |
| **`FrameworkElement.OnRender` 직접 구현** | 직접 만들어야 함(~500 LOC) | **✓ 채택** |

WPF는 리테인드 모드라 **변경이 없으면 렌더 비용이 0**이다. 전 카드 동기화 스크럽처럼 카드 간 협조가 필요한 기능은 라이브러리 위에서 구현하기가 오히려 어렵다.

구현 요점: `StreamGeometry` 재사용(`Clear()` 후 재기록), `Pen`/`Brush`는 `Freeze()` 후 정적 캐시, 격자선은 `GuidelineSet`, 텍스트는 `GlyphRun` 캐시(`FormattedText`는 렌더마다 할당).

### 8.2 차트 종류
| 카드 | 표현 |
|---|---|
| CPU | 영역 + 평균선(점선) |
| 메모리 | 물리 사용량 영역 + 커밋 라인 |
| 네트워크 (인터페이스별) | 미러 영역 (위=RX, 아래=TX), 인터페이스별 독립 축 |
| 디스크 (물리별) | 미러 영역 (위=읽기, 아래=쓰기) |
| GPU (어댑터별) | 조합 차트 (§8.3) |
| NPU | **사용률 단일 영역 차트** + 평균선. 누적 메모리 없음 |

#### 카드 헤더 — 큰 숫자와 GPU 의 메모리 요약

큰 숫자는 주 지표의 현재값이다. **GPU 카드만 그 옆에 메모리 요약이 붙는다.**

```
[아이콘] Arc(TM) 130V GPU (8GB)   ▬▬▭▭▭▭▭▭  ⌄  21 % 1.48GB
                                  ~~~~~~~~
    …차트…
    ~ 18.0  ▲ 22.5  ▼ 5.54  %   ▪ 0+1.48              8.86 GB
```

| 자리 | 내용 | 답하는 질문 |
|---|---|---|
| 큰 숫자 (26px) | **사용률** | 지금 바쁜가 |
| 작은 글씨 (11px) | **쓴 양** — 예 `1.48GB` | 얼마나 잡았나 |
| 미터 바 (접힘 시) | 용량 대비 비율. 스파크라인 **위**에 가로로 | 얼마나 찼나 |
| 푸터 `▪` | 전용+공유 **분해** | 어디에 잡았나 |
| 푸터 오른쪽 | **전체 크기**(용량) | 전체가 얼마인가 |

**전체 크기는 헤더에 두지 않는다.** 카드가 살아 있는 동안 바뀌지 않는 값이라 매 갱신마다
눈에 들어올 이유가 없고, 하단에 이미 자리가 있다. 비율은 미터가 말하므로 헤더에서
`1.48/8.86GB` 처럼 다시 말할 필요도 없다.

**한 번 메모리로 바꿨다가 되돌렸다.** 큰 숫자를 점유 메모리로 바꿔보니 카드를 접었을 때
"지금 바쁜가"가 사라졌다 — 접힌 카드가 답해야 할 질문이 바로 그것이다. 둘 다 필요하므로
사용률은 큰 숫자로, 메모리는 작은 글씨와 미터로 나눠 담는다. 26px 로 `21%/1.48/8.86GB` 를
한 줄에 넣으면 라벨과 스파크라인 자리를 다 먹는다는 점도 같은 결론을 가리켰다.

**미터는 꺾은선 위에 따로 얹는다.** 사용률은 시간축을 가진 꺾은선이고 메모리 점유는 지금 한
값이라, 같은 그림에 겹치면 둘 다 읽기 어렵다. 형태도 이쪽이 맞다 — **한계값 대비 단일 비율은
미터가 읽기 쉽고 2조각 파이는 그렇지 않다.** 빈 칸은 회색이 아니라 같은 색의 옅은 단계로 둬서
막대 전체가 한 상태로 읽히게 하고, 넘치면 채움이 경고색으로 바뀐다(길이는 100% 에서 자른다 —
칸을 넘어 삐져나오면 "얼마나 넘었나"가 아니라 "레이아웃이 깨졌나"로 읽힌다).

분모는 **외장=전용 VRAM, 내장=공유 한도**다(§8.3). 미터·푸터의 전체 크기·차트의 용량선이
전부 이 하나를 쓴다 — 서로 다른 분모를 쓰면 같은 카드 안에서 "80% 찼다"와 "막대가 절반"이
동시에 나온다. NPU 는 누적 메모리가 없어 사용률만 보여준다.

> **GPU 카드만 푸터 오른쪽이 축 상한이 아니라 용량이다.** 그 자리가 답할 것은 "전체가
> 얼마인가"이고 GPU 메모리에서 그것은 용량이다. 둘은 넘치는 동안에만 갈라지는데, 그때는
> 용량선이 차트 안에 파선으로 보이므로 이 숫자가 그 선을 가리키는 것으로 읽힌다.
> 축 상한(`28.4 G` 같은 1.08 × 최고치)을 적어두면 오히려 짝이 없다.

> **접힌 카드에는 전체 크기가 없다.** 푸터가 없기 때문이다. 대신 미터가 비율을 말한다 —
> 접힌 줄에서 알아야 할 것은 "얼마나 찼나"이지 "전체가 몇 기가인가"가 아니다.

#### 접힌 카드의 스파크라인 축

| 지표 | 축 |
|---|---|
| 퍼센트 (CPU · 메모리 · GPU·NPU 사용률) | **0~100 고정** |
| 전송률 (네트워크 · 디스크) | 표시 창의 최고치 기준 상대 |

퍼센트를 상대 축으로 그리면 **3% 대에서 미세하게 흔들리는 것과 90% 대에서 흔들리는 것이
같은 모양으로 나온다.** 접힌 카드는 "지금 바쁜가" 하나를 읽으려고 있는데 그 판단을
정확히 반대로 유도한다. 고정 축이면 선의 **높이**가 곧 답이고, 카드 여럿을 나란히 놓고
비교할 수도 있다.

전송률은 상한이 없어서 사정이 다르다. 절대 축을 쓸 기준이 없고 단계에 스냅하면
유휴 구간에서 늘 바닥에 붙어 아무것도 보이지 않는다. 그쪽은 상대 축을 유지한다.

### 8.3 GPU 조합 차트 (R8)
- **좌축** 0–100% 사용률: 전체(실선) + **AI 신호(점선)** — 이 GPU 에서 AI 연산이 실리는 지표다. MCP `aiSignals.primary` 와 **같은 규칙**(`Core/Metrics/GpuAiSignals`)으로 `GpuUtil` 이 아닌 첫 후보를 고른다

  | 어댑터 | 점선 |
  |---|---|
  | NVIDIA · HAGS 켜짐(기본) | `Gpu3D` — CUDA 가 3D 노드로 합산된다 |
  | NVIDIA · HAGS 꺼짐 | `GpuCompute` |
  | Intel Arc | `GpuRenderCompute` — 하드웨어 카운터(250ms). PDH 만 붙었으면 `GpuCompute` |
  | AMD · 그 밖 | `GpuCompute` |

  예전에는 늘 `GpuCompute` 였다. HAGS 가 켜진 NVIDIA 에서는 AI 추론 중에도 0 에 붙어 "연산을 안 한다"로 읽혔고, Intel 에서는 1초 간격으로 0 과 100 을 오갔다(§5.4). 스냅샷 창(§9.6)도 같은 규칙을 쓴다
- **우축** 메모리 바이트: **누적 영역** = 전용(솔리드 20%) + 공유(대각 해치 30%)
- 시각 위계: 메모리는 뒤·저채도, 사용률은 앞·고채도. 사용률 라인이 누적 영역 위를 지날 때 묻히지 않도록 **같은 경로를 Surface 색 4px로 먼저 긋는 헤일로**를 적용
- 색약 대응: 전용/공유를 색이 아니라 **채움 패턴**으로도 구분

**외장/내장 분기**

| | 외장 (전용 ≥ 1 GiB) | 내장 |
|---|---|---|
| 우축 상한 | 전용 용량에 **고정**. 넘칠 때만 `최고치 × 1.08` | 공유 한도에 **완전 고정** |
| 전용 VRAM 용량선 | 표시 (수평 파선) | **표시하지 않음** — 축 상단이 곧 한도다 |
| 공유 영역 색 | **경고색 상시** | 평소 색 |
| 스필오버 경고(카드 테두리) | **있음** — 전용 용량 초과 시 | **없음** |

> 내장 GPU는 공유 메모리를 쓰는 것이 정상이다. 외장 규칙을 적용하면 iGPU가 상시 경고 상태가 되어 경고의 의미가 사라진다.

#### 축은 사용량이 아니라 용량에 고정한다

**GPU 메모리는 파형이 아니라 점유량이다.** 사용률처럼 순간의 오르내림을 보는 지표가 아니라
"얼마나 잡고 있고 얼마나 남았는가"를 보는 지표다. 축을 최고치에 맞춰 움직이면 그 둘 다
읽을 수 없다 — **0.5 GiB 를 쓰든 11.9 GiB 를 쓰든 영역이 같은 높이로 그려지기 때문이다.**

- **외장** — 전용 용량이 바닥선이다. 평소에는 축 상단이 곧 전용 용량이라 채움 높이가
  그대로 점유율이고, 넘칠 때만 넘친 만큼 늘어난다. 용량선이 고정 기준으로 남아
  "선에 닿았다 / 넘었다"를 **형태만으로** 읽을 수 있다
- **내장** — 공유 한도로 완전히 고정한다. 전용 VRAM 이 없어 비교 기준이 공유 한도뿐이고,
  이쪽은 넘칠 수가 없으므로 축이 움직일 이유 자체가 없다. 고정이면 카드 두 장을 나란히 두고
  비교할 수도 있다

> **여유는 넘친 뒤에만 붙인다.** 최고치에 그냥 1.08 을 곱하면 용량의 99% 를 쓰는 것만으로
> 축이 늘어난다(11.9 × 1.08 > 12). 아직 넘기지도 않았는데 용량선이 아래로 내려와
> "선에 닿았다"를 읽을 수 없게 된다. 처음 구현에서 실제로 그렇게 짰고 테스트가 잡았다.

> **외장에서 공유 메모리 사용은 양과 무관하게 눈에 띄어야 한다.** 전용을 다 채우기 전부터
> 공유로 넘어가는 경우가 많아, "용량선을 넘었을 때"만 경고색을 쓰면 정작 성능이 떨어지는
> 구간을 놓친다. 색은 상시 경고색으로 두고 **얼마나 넘어갔는지는 띠의 높이가 말하게** 한다.
> 카드 테두리 경고는 그대로 "전용 용량 초과"일 때만 켠다 — 둘은 다른 신호다.

축 정책은 렌더러가 아니라 `ChronoLoad.Core.Layout.GpuMemoryAxis` 에 있다. 그리기 방식이 아니라
**판단**이고, 화면 없이 테스트할 수 있어야 하며, **외장 GPU 가 없는 기기에서도 외장 규칙을
검증할 수 있어야** 하기 때문이다(개발 노트북에 외장 GPU 가 없다 — §12).

### 8.4 테마
- `Themes/Dark.xaml`, `Themes/Light.xaml`, 모든 색은 `DynamicResource`
- 기본 = 시스템 추종(`WM_SETTINGCHANGE` + `AppsUseLightTheme` 감시)
- 다크는 순수 검정 대신 `#0E1116`
- 토큰 전체는 UX 문서 §14

### 8.5 창 동작
- `WindowStyle=None` + `AllowsTransparency=False` + 커스텀 리사이즈 그립
- `Topmost` 토글, 위치·크기 영속화, `PerMonitorV2`

#### `WindowChrome` 를 쓰는 이유 — 타이틀바 위의 흰 줄

`WindowStyle=None` 만으로는 창 위쪽 **5dip 이 비클라이언트로 남고**, DWM 이 그 띠를 기본
프레임 색으로 칠한다. 다크 테마에서는 타이틀바 위에 흰 줄로 보인다.

실측 — 창 상단에서 아래로 물리 픽셀 색:

| 창 y (물리px, 200%) | 색 | 정체 |
|---|---|---|
| 0~1 | `#3E3E3E` | 시스템 테두리 |
| **2~11** | **`#F9F2E9`** | **DWM 이 칠한 비클라이언트 띠 — 이게 흰 줄이다** |
| 12~ | `#0E1116` | 앱 배경 |

타이틀바 밑줄이 설계값 34dip 이 아니라 **39dip** 에 찍히는 것으로도 같은 사실이 보인다.
클라이언트가 5dip 아래에서 시작하고 있었다.

`WindowChrome` 에 `GlassFrameThickness="0"` 을 주면 그 띠가 사라지고 클라이언트가 창 전체를
덮는다. 고친 뒤 같은 측정에서 `창y=0` 부터 `#0E1116` 이고 밑줄은 34dip 제자리다.
Windows 11 의 둥근 모서리도 그대로 남는다.

- **`AllowsTransparency="True"`** — 불투명도(§9.4)가 실제로 뒤를 비치게 하려면 이것이 있어야 한다.
  `WindowChrome` 과 함께 써도 크기 조절·둥근 모서리·타이틀바 흰 줄 처리가 전부 그대로다(실측)
- **`CaptionHeight="0"`** — 끌기는 타이틀바의 `DragMove` 로 직접 처리한다. 0 이 아니면
  시스템 캡션이 그 영역을 먼저 가져가 버튼 클릭이 먹지 않는다
- **버튼 묶음에만 `IsHitTestVisibleInChrome="True"`** — 상단 6dip 은 리사이즈 테두리이고
  버튼은 세로 가운데 정렬이라 윗변이 정확히 그 띠와 맞닿는다. 표시는 멀쩡한데 윗변이
  안 눌리는 상태가 된다. 버튼을 감싼 `DockPanel` 에만 표시하면 버튼은 전부 눌리고,
  버튼 사이 빈 곳(배경이 없어 히트테스트가 통과한다)으로는 여전히 위에서 잡아 크기를 조절할 수 있다
- `WM_NCHITTEST` 로 확인한다. 버튼 위는 `HTCLIENT`, 버튼 사이 상단 6dip 은 `HTTOP`,
  좌우 가장자리는 `HTLEFT`/`HTRIGHT` 여야 한다
- **첫 실행 창 높이는 장치 구성에서 계산**: `76 + 16 + 갭 + Σ(펼침 × 130 × 가중치) + Σ(접힘 × 28)`.
  상한은 작업 영역의 85%, **하한은 최소 창 높이 420**이다. 하한을 작업 영역에 비례시키면
  4K 모니터에서 장치가 적은 노트북이 1296px 창을 받아 카드 3개가 400px씩 차지하는 레이아웃이 된다.
  GPU2+디스크2+네트워크2면 약 950, 카드 3개짜리 노트북이면 약 560

### 8.6 접기 · 높이 분배 (R13)

```
available = 카드영역 높이 − 갭 − (접힌 카드 수 × 28)
각 펼친 카드 높이 = available × (가중치 ÷ 펼친 카드 가중치 합)
while (최소 펼친 높이 < 118 && 펼친 카드 > 1)
    자리를 내줄 카드를 접기(AutoCollapsed=true) → 재계산
        1순위: 사용자가 가장 오래 전에 편 카드 (직접 편 적 없으면 가장 먼저)
        2순위: 우선순위 표
```

#### 누가 자리를 내주는가

**우선순위만 보면 카드가 열리지 않는다.** 자리가 꽉 찬 상태에서 우선순위가 낮은 카드
(Wi-Fi 50 · 디스크 60)를 열면, 바로 그 카드가 희생양으로 뽑혀 같은 배치 안에서 다시 접힌다.
클릭해도 아무 일이 일어나지 않는 것으로 보인다 — 실제로 그랬다.

그래서 **사용자가 직접 편 순서**(`ExpandOrder`)를 1순위로 둔다. 방금 연 카드가 가장 늦게
접히므로 반드시 열리고, 여러 번 열었다면 **가장 오래 전에 연 카드**가 자리를 내준다.
우선순위 표는 2순위로 내려가 아무도 손대지 않은 카드들 사이의 순서를 정한다 —
사용자가 아무것도 건드리지 않은 동안에는 예전과 똑같이 동작한다.

> **직접 누른 선택이 표에 적힌 기본값보다 앞선다.** Wi-Fi 와 디스크를 차례로 열면
> CPU·메모리가 자리를 내준다. 표의 우선순위가 높아도 그렇다 — 사용자가 고른 것이
> 고르지 않은 것보다 중요하다는 뜻이고, CPU 를 다시 열면 그때는 CPU 가 가장 최근이 된다.

> 세션 안에서만 의미가 있어 설정에 저장하지 않는다. 다시 켜면 우선순위 표가 다시 기준이 된다.

| 카드 | 가중치 | 우선순위 | 기본 |
|---|---|---|---|
| GPU 0 | 1.5 | 100 | 펼침 |
| CPU | 1.0 | 90 | 펼침 |
| GPU 1..N | 1.5 | 80 | 접힘 |
| 메모리 | 1.0 | 70 | 펼침 |
| 디스크 0 (시스템) | 1.0 | 60 | 펼침 |
| 디스크 1..N | 1.0 | 55 | 접힘 |
| 네트워크 — 기본 경로 | 1.0 | 50 | 펼침 |
| 네트워크 — 기타 | 1.0 | 45 | 접힘 |

- 접힘 = 28px 스트립(아이콘 + 전체폭 스파크라인 + 현재값). GPU 는 스파크라인 위에 메모리 미터가 함께 얹힌다(§8.2)
- **스파크라인 축은 지표에 따라 다르다**(§8.2) — 퍼센트는 0~100 고정, 전송률은 표시 창 최고치 기준 상대.
  전송률에 절대 축을 쓸 기준이 없어서이고, 퍼센트를 상대로 두면 3% 와 90% 가 같은 모양이 되어
  접힌 카드가 답해야 할 질문을 정확히 반대로 유도한다
- 자동 접힘은 **점선 테두리**로 구분하고 공간이 생기면 자동 복원. 사용자가 직접 접은 카드는 복원 대상이 아니다
- 접힌 카드도 샘플링·통계·경고가 살아 있다
- solo: 아이콘 더블클릭 → 그 카드만 펼치기 / 복원

> **구현 함정** — 창 높이를 애니메이션으로 바꾸는 동안 중간 높이로 레이아웃을 계산하면 자동 접힘이 과하게 발동해 모든 카드가 접혀버린다. **높이 애니메이션 완료 후 다시 계산**해야 한다(웹 목업에서는 `ResizeObserver`, WPF에서는 `SizeChanged`).

> **프리셋은 만들었다가 걷어냈다.** 장치 종류 규칙으로 저장하는 프리셋 5종 + 사용자 정의를
> 구현해 봤지만, 실제로 쓰이지 않았다. 카드가 6~8장인 화면에서는 직접 접었다 펴는 편이 빠르고,
> 프리셋을 고르려면 그 프리셋이 무엇을 펴는지 먼저 외워야 한다 — 곁눈질로 읽는 위젯에서
> 외울 것을 늘리는 기능은 값을 못 한다. 자동 접힘이 공간 압박을 이미 흡수하고 있었던 것도 크다.

### 8.7 시간 동기화 오버레이 (R14) ✅ 구현됨
```csharp
public sealed class ScrubState : ObservableObject   // 앱 전역 단일 인스턴스
{
    public int? Index { get; }          // 링버퍼 인덱스 (null = 비활성)
    public bool IsPinned { get; }
    public string? FocusCardId { get; } // 전체 패널을 띄울 카드
}
```
- 모든 `ChartCard`가 구독한다. 한 카드의 마우스 이동이 전 카드의 스크럽선과 오버레이를 갱신
- **호버 카드**: 전체 패널 — **첫 줄은 언제나 전체 장치명**, 이어서 온도·전력·클럭·엔진별 분해 / 활성·큐·버스
  - **GPU 조합 차트의 전체 패널은 범례를 겸한다.** 사용률·AI 신호·전용·공유·용량 줄 앞에 차트와 같은 모양의 선 견본(굵은 실선·파선·옅은 채움·빗금·가로 파선)을 붙인다. 선이 다섯 가지인데 화면 어디에도 이름이 없어 "채워지지 않은 얇은 선이 뭐냐"는 질문이 나왔다. 범례를 카드에 상시 두면 텍스트 예산(§9.3)을 먹으므로 값을 읽으러 올린 손이 닿는 자리에 둔다. 요약 칩에는 붙이지 않는다 — 여러 카드에 동시에 뜨는 칩이 전부 범례를 달면 시끄럽다. 렌더 테스트 `--scrub N --scrub-focus gpu` 로 확인한다
- **나머지 카드**: 요약 칩 1~2줄. **계열이 여럿이면 각각을 보여준다** — 미러 차트는 `↓`/`↑` 두 줄,
  GPU 는 사용률과 쓴 메모리. 한쪽만 내면 미러 차트가 "읽기만 있고 쓰기는 없는" 것처럼 읽히고,
  GPU 는 사용률만으로 메모리 상태를 알 수 없다
- **접힌 카드**: 헤더 현재값이 그 시점 값으로 바뀐다
- 스크럽 중 모든 헤더 값 앞에 **점 프리픽스**로 "지금 값 아님" 표시
- 클릭 고정, `←`/`→` 이동(`Shift`+10), `Esc` 해제
- 오버레이는 레이아웃을 밀지 않고 커서 반대편으로 자동 플립

#### 고정하는 것은 자리가 아니라 순간이다

새 샘플이 들어오면 그래프가 한 칸 왼쪽으로 흐른다. **고정된 선도 같이 흘러야 한다** —
자리에 붙들어 두면 선 아래의 순간이 매 틱 달라져서, 값을 읽어 적는 동안 대상이 바뀐다.
"그때 GPU 가 멈춘 순간 디스크는 뭘 했나"를 보려고 세우는 선이므로 순간을 붙드는 쪽이 맞다.

| 상황 | 동작 |
|---|---|
| 창 가운데 고정 | 선이 그래프와 같이 왼쪽으로 흐른다. 값은 그대로 |
| **맨 오른쪽** | 제자리에 머물며 **현재값을 계속 따라간다** — 흘려보낼 것이 없고, 그게 "지금을 본다"는 뜻이다. 고정이든 호버든 같다 |
| 창 왼쪽 밖으로 밀려남 | 고정을 놓는다. 왼쪽 끝에 붙들면 선은 그대로인데 가리키는 순간이 매 틱 달라져, 고치려던 문제가 반대쪽 끝에서 되살아난다 |
| 호버(고정 아님) | 흐르지 않는다. 자리를 정하는 것은 커서이고, 커서가 가만히 있으면 값도 가만히 있어야 한다 |

판정은 `ChronoLoad.Core.Layout.ScrubDrift` 에 있다. 화면 없이 테스트할 수 있어야 해서다.

> **"맨 오른쪽"은 번호가 아니라 상태로 기억한다**(`ScrubState.IsTrackingLive`).
> 인덱스 239 로 붙들면 안 된다 — 버퍼가 차는 동안(기동 후 1분)에는 점 개수가 매 틱 늘어나서
> 같은 번호가 한 칸씩 왼쪽이 된다. 오른쪽 끝을 잡아놓고도 선이 계속 흘러가는 것으로 보인다.
> 상태로 두면 틱마다 그때의 마지막 칸을 다시 계산한다.

> 같은 이유로 `ChartSurface.PointCount` 는 **마지막 렌더가 아니라 데이터에서 센다.**
> 렌더는 한 프레임 뒤처지는데, 그 수로 "맨 오른쪽"을 정하면 역시 한 칸씩 어긋난다.
> 접힌 카드의 차트는 아예 렌더되지 않아 0 으로 남는 문제도 같이 사라진다.

#### 표시 창 인덱스 → 버퍼 인덱스

**스크럽 인덱스는 버퍼 인덱스가 아니다.** 차트는 가장 최근 `WindowPoints`(240) 개만 그리는데
버퍼는 3600 개를 들고 있다. 그대로 쓰면 **화면에 없는 옛날 샘플**을 읽는다 —
그림과 숫자가 다른 곳을 가리키는데 둘 다 그럴듯해 보인다. 실제로 그렇게 틀려 있었고,
버퍼가 아직 차기 전에는 그 절대 위치가 움직이지 않아 "값이 멈춘 것"처럼 보였다.

변환은 `MetricSeries.AbsoluteIndexOf` 하나로 모았다: `count - min(windowPoints, count) + index`.
버퍼가 창보다 짧으면 차트도 있는 만큼만 그리므로 그대로 대응한다.

커서 위치 → 인덱스도 같은 기준(`ChartSurface.PointCount`, 실제로 그린 점 수)을 쓴다.
`WindowPoints` 로 잡으면 기동 직후 1분 동안 클릭한 자리와 읽히는 값이 어긋난다.

**값을 읽는 쪽도 차트와 같은 창 크기로 읽는다.** 시간 폭(§9.4)은 점 수를 시각으로 환산하므로 60초 창이
틱 간격에 따라 239·241·242점으로 매 틱 바뀐다. 그런데 헤더·오버레이(`CardViewModel.SampleAt`)만
240 으로 고정해 읽고 있었다. 맨 오른쪽에 고정하면 인덱스가 `창 − 1` 이라, 창이 240 보다 큰 틱에는 버퍼 끝을
넘어 **전 카드가 동시에 "—" 로 깜빡였고**(실사용 보고), 창이 작을 때는 엉뚱한 과거를 읽었다 — 30초 폭에서
맨 오른쪽을 고정하면 30초 전 값(CPU 37%, 최신 96%)이 나왔다. 카드가 차트에 창 크기를 줄 때 뷰모델에도 주고,
`SampleAt` 의 창 크기 인자에서 기본값을 없앴다 — 다시 어긋날 수 없게. 렌더 테스트 `--width 30 --scrub 120` 으로
고정 안 한 화면과 같은 값인지 본다(합성 데이터는 240점이라 60초 창보다 짧아 이 조건이 안 생긴다)

#### 맨 오른쪽 점은 집을 수 있어야 한다

240개를 그리는 차트에서 한 점이 차지하는 폭은 1/239 — **2px 남짓**이고, 마지막 점은 차트의
맨 끝이라 반쪽만 남는다. 하필 그 점이 "지금"이라 현재값 추적에 가장 자주 쓰이는데
커서로 집을 수가 없었다.

그래서 마우스 처리를 차트가 아니라 **카드**에 붙이고, 차트 좌우 바깥 12px 까지 양 끝 점으로 친다.
카드 안쪽 여백(10px)이 그대로 여유가 된다.

- **세로로는 여유를 주지 않는다.** 머리글·푸터까지 스크럽 영역으로 치면 카드를 접으려고
  머리글에 커서를 올리는 동안에도 값이 과거로 바뀐다
- 차트 띠를 벗어나면 호버를 놓는다. 그러지 않으면 머리글에 커서를 둔 동안 스크럽선이
  옛 자리에 남아 전 카드가 과거를 가리킨다
- 머리글 클릭(접기)은 자식에서 `Handled` 로 끝나므로 카드까지 올라오지 않는다

> **오버레이는 매 틱 다시 채운다.** 선이 흐르면 패널이 따라가야 하고, 맨 오른쪽에 세운 선은
> 값 자체가 계속 바뀐다. 다시 채우지 않으면 차트의 점은 새 값으로 움직이는데 옆의 숫자만
> 옛 값으로 남아 **같은 화면이 두 말을 한다.** 스크럽 중에만 도는 경로다.

> 눈으로 확인하는 경로: `--render-test <경로> --scrub <인덱스> --scrub-drift`.
> **연속한 틱을 따로 찍고** 마지막에 40샘플 뒤를 찍는다. 흐르는 거리뿐 아니라 틱 사이에
> 튀지 않는지도 봐야 하기 때문이다. WPF 는 합성 마우스 메시지를 입력으로 받지 않아
> 사람 손 없이 검증하려면 이 경로가 필요하다.

#### 패널 위치는 내용 폭으로 잰다

`FrameworkElement.DesiredSize` 에는 **그 요소의 `Margin` 이 더해져 있다.** 오버레이는 위치를
`Margin.Left` 로 잡으므로, `_overlay.DesiredSize` 로 패널 폭을 재면 **지난 틱에 밀어둔 여백이
이번 틱의 패널 폭**이 된다. 그러면 "오른쪽에 놓을까 왼쪽에 놓을까"가 매 틱 뒤집혀
요약 칩이 좌우 끝으로 튄다.

| 틱 | `Margin.Left` | 잰 폭 | 결정 |
|---|---:|---:|---|
| A | 0 | 90 | 선 오른쪽 (`left=465`) |
| B | 465 | **555** | 안 들어간다 → 왼쪽 끝 (`left=2`) |
| C | 2 | 92 | 선 오른쪽 … 반복 |

내용(`_overlayRows`)을 재고 패널의 패딩·테두리를 더한다. 스크럽을 고정한 채 두면 바로 보이고,
한 장면만 찍어서는 절대 안 보이는 종류의 버그다.

### 8.8 아이콘 체계 (R15)

아이콘은 **16px 그리드, 1.7px 스트로크, 라운드 캡, 획 4개 이하**의 단색 라인 아이콘으로 통일한다.

| 대상 | 구분 축 | 값 |
|---|---|---|
| GPU | **제조사** | NVIDIA(소용돌이) · AMD(사선 화살표) · Intel(원 안의 i) · 기타(일반 카드) |
| GPU 배지 | 외장/내장 | 외장 = 채운 배지, 내장 = 외곽선 배지 |
| 디스크 | **매체** | SSD(모듈+접점) · HDD(플래터+암) · 이동식/미상 |
| 네트워크 | **연결 종류** | 이더넷(RJ45) · Wi-Fi(전파 아크) · 셀룰러(막대) · 기타 |

**제조사 색은 아이콘에만 쓴다**

| 토큰 | 다크 / 라이트 |
|---|---|
| `Vendor.Nvidia` | `#7FBC03` / `#5C8A00` |
| `Vendor.Amd` | `#F5333C` / `#C81018` |
| `Vendor.Intel` | `#2B7FFF` / `#0F62C8` |
| `Vendor.Other` | `Accent.Gpu` 재사용 |

브랜드 색을 지표 색 체계에 섞으면 세 곳에서 충돌한다.

| 충돌 | 해소 |
|---|---|
| NVIDIA 그린 ↔ 네트워크 그린 | 네트워크는 차트·푸터·상태 점에만, NVIDIA는 아이콘에만 — 역할이 겹치지 않는다 |
| AMD 레드 ↔ 경고 레드 | **경고는 절대 아이콘 색으로 표현하지 않는다**(테두리 글로우 + 해치만). 아이콘 레드는 언제나 "AMD" |
| Intel 블루 ↔ CPU 시안 | Intel은 진한 블루, CPU는 하늘색 시안. 위치와 글리프 모양이 추가 단서 |

**차트 선·채움·푸터·상태 점은 어느 제조사든 앰버**다. 네트워크는 종류와 무관하게 전부 그린 — "이더넷 색 / Wi-Fi 색" 같은 관습은 존재하지 않으므로 발명하지 않는다.

> **상표** — 제조사 로고와 브랜드 색은 등록 상표다. 원본 로고 파일을 쓰지 않고 **16px 그리드에 맞춰 자체 제작한 단색 추상 글리프**를 쓰며, 색도 브랜드 원본(`#76B900`, `#ED1C24`, `#0068B5`)이 아니라 **대비 3:1을 확보하도록 조정한 값**이다. 공식 로고·색을 그대로 쓰려면 각 사 브랜드 가이드라인의 허용 범위를 별도로 확인해야 한다.

#### 스냅샷 창 글리프 (§9.6)

같은 규칙(16px 그리드 · 1.7px 스트로크 · 라운드 캡)으로 여섯 개를 더 만든다.

| 글리프 | 쓰임 | 경로 |
|---|---|---|
| `Snapshot` | 메인 창 제목 표시줄 — 스냅샷 창 열기 | 본체 `RoundedRect(2,5,12,8,2)` · 돌출부 `M5.8,5 L6.6,3.2 H9.4 L10.2,5` · 렌즈 `Circle(8,9,2.5)` |
| `FitWidth` | 전체 보기 | 양끝 기둥 `(2.4,3.5→2.4,12.5)`·`(13.6,3.5→13.6,12.5)` · 바깥 화살표 `M6.2,8 H3.2 M5,6.2 L3.1,8 L5,9.8` (좌우 대칭) |
| `Crop` | 선택 구간으로 크롭 | `M4.5,1.8 V11.5 H14.2` · `M1.8,4.5 H11.5 V14.2` |
| `Undo` | 되돌리기 | `M2.6,6.4 H9.6 A3.8,3.8 0 0 1 9.6,14 H6.4` · 화살촉 `M5.6,3.4 L2.6,6.4 L5.6,9.4` |
| `Export` | CSV 내보내기 | 축 `M8,2.6 V10.4` · 화살촉 `M4.8,7.2 L8,10.4 L11.2,7.2` · 바닥 `M2.8,12.6 H13.2` |
| `ZoomOut` / `ZoomIn` | 축소 · 확대 | `Circle(7,7,4)` · 손잡이 `(10,10→13.6,13.6)` · `−`는 `(5,7→9,7)`, `+`는 거기에 `(7,5→7,9)` |

> **`Undo` 는 `Reset`(⟲)과 실루엣부터 달라야 한다.** 둘 다 우리말로는 "되돌린다"로 묶이지만
> 하나는 통계 기준점을 옮기는 것이고 하나는 크롭을 취소하는 것이다. 같은 화면에 함께 놓이지는
> 않아도 같은 앱 안에서 다른 일을 하므로, `Reset` 은 **닫힌 원호**, `Undo` 는 **열린 갈고리**로 가른다.

> **스냅샷 버튼은 제목 표시줄의 ⟲ 바로 옆에 둔다.** 둘은 짝이다 — ⟲ 는 측정 구간의 시작점을 옮기고,
> 스냅샷은 그 구간을 통째로 떠낸다. 둘 다 "지금 재고 있는 것"을 다루므로 같은 자리에 있어야 한다.

> **아이콘만 두되 hover 툴팁에 이름을 적는다.** 크롭·되돌리기·내보내기는 관습이 굳은 글리프라
> 대체로 읽히지만, 읽히지 않는 사람에게 탈출구가 없으면 안 된다(§9.5). 제목 표시줄이 이미
> 아이콘만 쓰고 있어(⟲·핀·테마) 여기에만 텍스트 버튼을 섞으면 그쪽이 더 어색하다.

---

## 9. UI/UX 설계 (요약)

> 전체 시각 설계서: **[`docs/ux-design.html`](docs/ux-design.html)** — 라이브 목업에서 접기·동기화 오버레이·장치 핫플러그를 직접 조작할 수 있다

### 9.1 캔버스
- 기본 **340 × (장치 구성에서 계산)** — GPU2+디스크2+네트워크2면 약 950
- 최소 **300 × 420** (접기 덕분에 v0.1의 620에서 내려왔다)
- 최대 폭 420 (그 이상은 여백만 — 가로형으로 변형하지 않는다)

### 9.2 수직 스택 (탭 없음)
```
┌─────────────────────┐  34px  타이틀바 (앱마크 / 리셋 / 경과 / 핀 / 테마 / 최소화 / 닫기)
├─────────────────────┤  44px  전역 바 (⟲ · 경과 12:04 · 카드 수만큼 상태 점)
├─────────────────────┤ 134px  CPU
├─────────────────────┤ 134px  메모리
├─────────────────────┤ 134px  Ethernet
├─────────────────────┤  28px  Wi-Fi (접힘)
├─────────────────────┤ 134px  디스크 0 (SSD)
├─────────────────────┤  28px  디스크 1 (HDD, 접힘)
├─────────────────────┤ 193px  GPU 0 (NVIDIA)
├─────────────────────┤  28px  GPU 1 (Intel, 접힘)
└─────────────────────┘  8px   여백
```

### 9.3 텍스트 예산과 장치 이름 (R5, R18)

상시 화면에서 카드당 허용 문자 요소는 **최대 5개**: 현재값 + 단위, `~` 평균, `▲` 최대, `▼` 최소, 축 스케일 힌트.

**예외 4가지**
- 네트워크·디스크는 `▼` 대신 `↑`(반대 방향 최대) — 수신/읽기의 최소값은 거의 항상 0이라 정보가 없다
- GPU는 `▪`(현재 전용 VRAM, 스필오버 시 `+공유`) 추가
- **GPU 헤더의 쓴 양**(예 `1.48GB`) 추가 — 푸터의 `▪` 는 전용·공유의 **분해**를,
  헤더의 이것은 **지금 잡은 총량**을 말한다. 답하는 질문이 다르므로 둘 다 남긴다(§8.2)
- **오버레이와 라벨은 예산 밖**

#### 단위 기호는 줄이지 않는다

| 종류 | 기호 | 예 |
|---|---|---|
| 용량 | `KB` `MB` `GB` `TB` (1024 기준) | `메모리 15.5GB`, `8.86 GB` |
| 바이트 전송률 | `KB/s` `MB/s` `GB/s` | `0 KB/s` |
| 비트 전송률 | `Kbps` `Mbps` `Gbps` | `62 Kbps`, `Wi-Fi 2.4Gbps` |

**`G` 하나만 붙이면 무엇의 G 인지 알 수 없다.** 용량인지 속도인지, 바이트인지 비트인지가
전부 기호에 없었다. 실제로 Wi-Fi 의 `2.4G`(2.4Gbps 링크 속도)를 2.4GHz 밴드로 읽는 일이
있었고 — 그 기기의 실제 밴드는 5GHz 였다 — 디스크의 `KB`(KB/s)와 메모리의 `K`(KB)가
같은 줄에 나란히 놓이기도 했다. 두 글자 아껴서 값을 오해하게 만들 이유가 없다.

1024 기준이면서 `GB` 로 적는 것은 Windows 표기와 맞춘 것이다. 작업 관리자가 보여주는
숫자와 같은 값이어야 대조할 수 있다.

> `MetricFormatter.FormatGroup` 은 단위 기호를 다시 보고 나눗셈 크기를 고른다.
> 그 대조표가 포매터의 기호와 한 글자라도 어긋나면 기본 가지로 떨어져 **값이 1000배 틀린 채
> 멀쩡해 보인다.** 기호를 바꿀 때 반드시 같이 고쳐야 하고, 테스트로 묶어뒀다.

**장치 이름은 상시 표시한다 (R18)**

초안은 페이드·hover·`L` 키로 이어지는 3단계였다. 1.1 에서 **상시 표시**로 바꿨고(§17)
그 뒤로도 단계 표가 남아 있었다 — 같은 문서가 두 말을 하고 있었다.

| 계기 | 표시 | 내용 |
|---|---|---|
| 항상 | 헤더 라벨 | `ShortName` |
| hover 유지 (툴팁 지연) | 네이티브 툴팁 | `FullName` |
| 차트 hover | 오버레이 첫 줄 | `FullName` + 스크럽 시각 |

> 같은 아이콘의 카드가 여럿(GPU 4장·디스크 2장·NIC 2개)이면 아이콘만으로는 어느 줄이 무엇인지
> 알 수 없다. "텍스트 최소화"는 읽을 것을 줄이자는 뜻이지 **무엇을 보고 있는지 감추자는 뜻이 아니다.**

### 9.4 인터랙션
| 동작 | 결과 |
|---|---|
| 카드 헤더 클릭 | 접기/펼치기 |
| `1`~`9` | 카드 토글 |
| 카드 hover | 전체 장치명 툴팁 |
| 차트 hover | 전 카드 동기화 스크럽 + 오버레이 |
| 차트 클릭 / `←``→` / `Esc` | 스크럽 고정 / 이동 / 해제 |
| `Ctrl`+휠 | **시간 폭** — 30s · 60s · 180s · 600s · 900s(버퍼 전체) 사다리 |
| 차트 더블클릭 | **표준 시간 폭(60s)으로 복귀** |
| 전역 ⟲ / `Ctrl+R` | 전 지표 통계 리셋(접힌 카드 포함) |

> **한 결과에 경로를 여럿 두지 않는다.** 아이콘 더블클릭 solo 는 헤더 클릭·숫자 키와 같은
> 결과에 닿고, 카드별 리셋과 ⟲ 600ms 롱프레스는 전역 리셋으로 충분한 일을 나눠 맡는다.
> 곁눈질로 읽는 위젯에서 경로가 늘면 외울 것만 는다 — 프리셋을 걷어낸 것과 같은 판단(§8.6).
> 셋 다 **구현된 적이 없다.** 지운 것은 코드가 아니라 문서다.
| 제목 표시줄 스냅샷 버튼 | **스냅샷 창 열기**(§9.6) — 지금 버퍼를 복제해 고정 표시 |
| `Shift`+휠 | 창 불투명도 25~100% (노치당 5%) ✅ — 설정 팝오버(§11)에도 슬라이더로 있다 |

#### 시간 폭은 `Ctrl`+휠, 더블클릭은 표준 복귀 ✅ 구현됨

초안은 더블클릭에 `60s → 180s → 600s` 순환을 걸어 두었다. 순환은 **원하는 곳에 닿으려면 몇 번
눌러야 하는지 세야 하고**, 지나치면 한 바퀴를 더 돌아야 한다. 더 나쁜 것은 돌아올 문이 없다는
점이다 — 600s 에 가 있다는 것을 잊으면 화면이 왜 이런지 알 수 없다.

| 제스처 | 하는 일 |
|---|---|
| `Ctrl`+휠 | 사다리를 한 칸씩 오르내린다 — 30 · 60 · 180 · 600 · 900초 |
| 차트 더블클릭 | **어디에 있든 표준 60초로 돌아온다** |

> **시간 폭은 전 카드 공통이다.** 카드마다 다른 폭을 쓰면 §1.1 의 "시간 축이 하나"가 깨지고,
> 같은 세로선이 같은 순간을 가리키지 않게 된다.

> **저장하지 않는다.** 다음 실행은 늘 60초로 시작한다. 곁눈질 위젯은 늘 같은 자리에 같은 것이
> 있어야 하는데, 어제 600초로 두고 껐다는 사실을 기억하지 못한 채 열면 화면이 낯설다.
> §11 의 설정 항목을 늘리지 않는 이유이기도 하다.

> **표준이 아닐 때만 말한다.** 제목 표시줄 경과 시간 옆에 현재 폭(`180s`)을 작게 띄우고 60초로
> 돌아오면 지운다. 기본 상태에서는 아무 비용도 치르지 않으면서, 벗어나 있을 때는 그 사실이
> 화면에 남는다 — 더블클릭이 무엇을 되돌리는지도 그 칩이 가리킨다.

> **폭은 점 개수가 아니라 초로 센다.** `WindowPoints` 240 을 그대로 쓰면 적응형 백오프(§6.3)로
> 주기가 늘어난 구간에서 "60초"가 60초가 아니게 된다. §7.4 의 시각 링에서 `지금 − 60초` 에
> 해당하는 인덱스를 찾아 거기서부터 그린다. 시각 링을 놓는 세 번째 이유였다.

**구현** — 사다리는 `Core/Layout/TimeWidthLadder`, 초 → 점 개수 환산은 `MetricRegistry.PointsWithin`
(오름차순 시각 배열의 이진 탐색)이다. 점 수는 **매 갱신에 다시 묻는다** — 배속이 바뀌면 같은
60초가 다른 점 수가 되기 때문이다.

> 사다리는 **순환하지 않는다.** 양 끝에서 더 굴려도 제자리다. 순환은 반대편으로 넘어가
> 놀라게 하고, 그러느니 멈춰 서는 편이 낫다.

> 폭이 바뀌면 **스크럽 고정을 놓는다.** 점 수가 달라지면 고정선이 가리키던 자리도 달라지는데,
> 어디를 가리키는지 알 수 없게 된 선을 남겨 두는 것보다 놓는 편이 정직하다.

> 절전으로 끊긴 구간(§13) 앞쪽은 "최근 60초"에 들지 않는다. 복귀 직후 점이 몇 개뿐인 것은
> 화면이 고장난 것이 아니라 **실제로 가진 전부**가 그것이기 때문이다.

**렌더 테스트** — `--render-test <경로> --width <초>` 로 표준이 아닌 폭을 잡아 낸다.

> 스냅샷 창은 `--snapshot` 으로 낸다. 기본·`Shift`+클릭·선택·크롭·순간 **다섯 장**이 나온다 —
> 전부 드래그나 클릭으로만 닿는 경로라 눈으로 확인할 방법이 달리 없다.
#### 불투명도는 맨 휠이 아니라 `Shift`+휠이다 ✅ 구현됨

> **`Window.Opacity` 만으로는 뒤가 비치지 않는다.** `AllowsTransparency` 가 거짓인 창은 OS
> 수준에서 불투명한 HWND 라, WPF 는 내용을 **창 배경색 위에** 알파로 그릴 뿐이다 — 흐려지기만
> 하고 아래 창은 그대로 가려진다. `AllowsTransparency="True"`(§8.5)로 켠다.

> 레이어드 윈도우(`WS_EX_LAYERED` + `SetLayeredWindowAttributes`)로 우회하려 했으나 **막힌다.**
> `SetWindowLongPtr` 이 이전 값을 정상으로 돌려주고 오류도 없는데 되읽으면 그대로다 —
> WPF `Window` 가 자기 확장 스타일을 강제해 호출 안에서 되돌린다. 오류를 내지 않으므로
> 로그를 심어 보기 전에는 "왜 안 되는지"가 보이지 않는다.

> 대가는 소프트웨어 합성이다. 실측해 보니 이 창 크기·4Hz 에서는 **CPU 0.575%** 로
> 소크 실측(0.632%, §12)과 같은 수준이라 사실상 차이가 없었다.

> **하한을 60% 에서 25% 로 내렸다.** 60% 로는 뒤가 거의 보이지 않아 기능이 있으나 마나였다 —
> 위젯을 반투명하게 두는 목적 자체가 아래 창을 곁눈으로 보기 위함인데 그것이 되지 않았다.
> 0 까지 열지 않는 이유는 창을 **다시 찾지 못하게** 되기 때문이다. 불투명도는 히트 테스트에
> 영향을 주지 않으므로 보이지 않아도 눌리기는 하지만, 보이지 않는 것을 누를 수는 없다.

맨 휠은 차트 위에 커서를 올린 채 무심코 굴리다 창이 흐려진다. 이 앱은 **곁눈질로 같은 자리를**
**읽는 것**이 가치이므로, 의도하지 않은 시인성 변화는 그 가치를 직접 깎는다.
`Ctrl`+휠은 확대 관례라 피하고, 스크롤이 없는 창이므로 `Shift`+휠 자리가 비어 있다.

> 수식 키+휠은 **발견되지 않는 조작**이다 — ⟲ 600ms 롱프레스를 걷어낸 것과 같은 문제를 안는다.
> 그래서 이것을 **유일한 경로로 두지 않는다.** 불투명도는 §11 의 영속 설정 항목이므로 설정
> 팝오버에 값으로 노출하고, `Shift`+휠은 그 단축 경로다. 발견하지 못해도 도달할 수 있으면
> 단축키는 더해주기만 한다.

### 9.5 접근성
- 대비 WCAG AA 이상. 색상 단독 정보 전달 금지 — 솔리드/해치, 글리프 모양, 인덱스 배지, 순서 고정 병용
- **적록색약 사용자에게 NVIDIA 그린과 AMD 레드는 구분이 어렵다** → 글리프 모양이 1차 채널, 색은 보조. 색을 제거해도 정보 손실이 없어야 한다
- 아이콘으로 식별이 안 되는 사용자를 위한 탈출구: hover 라벨, `L` 유지, 설정 "라벨 항상 표시"
- 접힌 카드도 스크린 리더에서는 전체 값이 읽힌다
- **장치 추가·제거는 `LiveRegion`으로 고지** — 시각적으로는 카드 등장이 알림이지만 스크린 리더에는 보이지 않는다
- 접근성 "동작 줄이기" 시 모든 이징 제거 ✅ — `App/Services/Motion`. `SystemParameters.ClientAreaAnimation`
  이 거짓이면 길이를 0초로 돌리고 이징을 뗀다. 값을 캐시하지 않으므로 설정을 바꾸면 다음 전환부터 곧바로 따라간다
- 단 **라벨 3초 표시 시간은 유지**(정보 전달이 목적이지 장식이 아니다). 지우는 것은 **움직임이지 정보가 아니다**

---

### 9.6 스냅샷 창 — 흐름을 멈추고 구간을 잰다 ✅ 구현됨

메인 창은 곁눈질용이다. 흐르는 화면에서는 방금 지나간 스파이크를 붙들 수 없다.
스크럽 고정(§8.7)이 **한 순간**을 붙들지만, **구간**을 재려면 흐름 자체를 멈춰야 한다.

메인 창 **제목 표시줄의 스냅샷 버튼**(§8.8 `Snapshot`)으로 지금 링 버퍼에 있는 것을 통째로
복제해 새 창에 띄운다. 그 창에는 새 데이터가 들어오지 않는다.

> 더블클릭이 아니라 버튼인 이유 — **더블클릭은 발견되지 않는다.** 게다가 차트 위 더블클릭은
> 이미 시간 창 전환(§9.4)이 쓰고 있다. 한 제스처에 두 기능을 얹으면 어느 쪽도 확실하지 않다.
> 버튼은 ⟲ 바로 옆에 둔다 — 하나는 측정 구간의 시작점을 옮기고 하나는 그 구간을 떠내는,
> 같은 일의 두 면이다.

#### 복제는 링 버퍼를 하나 더 만드는 것이다

**설계는 `MetricSeries` 복제였으나 구현은 평범한 배열이다.** 전용 차트를 두기로 하면서 링 버퍼의
성질이 필요 없어졌다 — 임의 구간을 그리고, 구간 통계를 내고, CSV 로 내보내는 일이 전부 배열
인덱스 하나로 끝난다. 랩어라운드는 떠내는 순간 사라진다(`Core/Metrics/MetricSnapshot`).

`ChartSurface` 를 재사용하지 않은 이유도 같다. 저쪽은 늘 **가장 최근** N 점을 그리고 스크럽선을
얹는다. 여기는 확대·스크롤로 정한 구간을 그리고 선택 밴드를 얹는다. 라이브 차트에 조망 구간
개념을 집어넣어 봐야 라이브 쪽에는 쓸 일이 없고 그 위험만 나눠 갖는다 —
그래서 `Rendering/SnapshotChart` 를 따로 둔다.

| 복제 대상 | 비용 |
|---|---|
| 값 + 실측 비트 | 슬롯당 3600 × 4B + 450B ≈ **14.9KB**. 슬롯 40개면 약 600KB |
| 시각 링(§7.4) | 28.8KB — 창당 하나 |
| 장치 메타데이터(`DeviceInfo`) | `record` 참조 복사 |

> **장치 메타데이터를 반드시 함께 복제한다.** 스냅샷을 열어 둔 채 eGPU 를 빼면 `DeviceHandle` 이
> 회수되는데(§5.7), 창은 그 장치의 이름과 용량을 계속 말해야 한다. 라이브 핸들을 참조하면
> 스냅샷이 **과거를 보여주다가 갑자기 현재를 모른다고 말하는** 물건이 된다.

#### 창의 성격

| 항목 | 결정 | 이유 |
|---|---|---|
| 모달리스, 여러 개 | 허용 | 테스트 A·B 를 나란히 놓고 비교하는 것이 이 기능의 주 용도다 |
| 타이틀 | **데이터 시작 시각 + 길이 + 표본 수** (예 `ChronoLoad — 14:32:07 부터 15분 · 3,600 샘플`) | 창이 여러 개일 때 무엇이 언제 남긴 것인지는 타이틀에서만 갈린다. 창 안에 같은 줄을 또 두지 않는다 — 제목 표시줄 바로 아래 같은 글이 겹쳐 보이고, 그 34dip 은 차트가 쓰는 편이 낫다 |
| 메인 창과의 연동 | 없음 | 메인 창의 리셋·장치 변경·테마 외 모든 것이 스냅샷에 닿지 않는다. 테마만 따라간다 |
| 소유 관계 | **첫 자리를 잡을 때만** 소유자를 걸고 곧 놓는다 | 아래 |
| 영속성 | 없음 | 앱을 닫으면 사라진다. 남기려면 CSV 로 낸다 |

#### 소유자는 자리만 잡고 놓는다

`Owner` 를 걸어 두면 **Win32 가 소유된 창을 늘 소유자 위에 둔다.** 메인 창을 눌러도 올라오지
않아 "창이 고장 났다"로 읽힌다 — 실측하면 메인 창을 `Activate()` 해도 z-순서가 그대로다.

그렇다고 처음부터 안 걸 수는 없다. `CenterOwner` 가 소유자를 알아야 첫 자리를 메인 창
가운데로 잡는다. 그래서 **걸고, 배치가 끝난 `Loaded` 에서 놓는다.** 놓고 나면 서로 독립한
창이라 무엇이든 위로 올릴 수 있다.

| 놓으면서 생기는 일 | 대응 |
|---|---|
| 메인 창이 닫혀도 따라 닫히지 않는다 | 메인 창의 `Closed` 를 구독해 닫는다. 기본 `ShutdownMode` 가 `OnLastWindowClose` 라 남겨 두면 **창 없는 프로세스**가 살아 있게 된다 |
| 닫힐 때 활성이 돌아갈 곳이 없다 | 메인 창을 따로 들고 있다가, 이 창이 활성인 채로 닫혔으면 거기로 돌려준다 |

> 실측 — 소유자를 건 채로는 메인 창을 활성으로 만들어도 `main=2 · snap=1`, 놓으면
> `main=1 · snap=2` 로 뒤집힌다. 놓은 뒤에도 첫 자리는 그대로 가운데다.

#### 기본 창테두리를 쓰되 색은 맞춘다

메인 창은 `WindowChrome` 으로 제목 표시줄까지 직접 그린다(§8.5). 스냅샷 창은 그러지 않는다 —
여러 개를 띄워 놓고 OS 의 창 목록·스냅 레이아웃·`Alt`+`Tab` 으로 다루는 창이고, 거기서 제목이
읽히려면 OS 가 아는 제목 표시줄이어야 한다.

그러면 다크 테마에서 **제목 표시줄만 하얗게** 남는다. DWM 에 색을 알려 주면 된다
(`App/Services/WindowFrame`).

| 속성 | 값 |
|---|---|
| `DWMWA_USE_IMMERSIVE_DARK_MODE` (20) | 다크 팔레트일 때 1 |
| `DWMWA_CAPTION_COLOR` (35) | 창에서 **바로 아래에 오는 것**과 같은 색. 스냅샷 창은 툴바이므로 `Surface2` — 두 줄이 한 덩어리로 읽힌다 |
| `DWMWA_TEXT_COLOR` (36) | `Fg` |
| `DWMWA_BORDER_COLOR` (34) | `Line` |

34~36 은 Windows 11 부터다. 그 이전에서는 실패 코드가 돌아오고 20 만 걸리는데, 그것이
Windows 10 의 기본 어두운 캡션이다 — 색이 조금 다를 뿐이라 따로 갈래를 두지 않는다.

세로 스크롤바도 같은 문제다. OS 기본 스크롤바는 다크 위에 흰 기둥으로 남고, 화살표 단추까지
달려 17dip 을 차지하니 창 가장자리를 밀고 나간 것처럼 보인다. 손잡이만 남긴 12dip 짜리로
바꾼다 — `App.xaml` 의 **암시적 `ScrollBar` 스타일**이고 브러시는 `DynamicResource` 라
테마 전환을 그대로 따라간다.

#### 레이아웃

```
┌ ChronoLoad — 14:32:07 부터 15분 ───────────────────── ─ □ ✕ ┐
│ (전체) (크롭) (되돌리기)                       (내보내기)    │  툴바 — 전부 아이콘
├──────────────────────────────────────────────────────────────┤
│ ⌄ ▦ CPU          평균 61.9%  최소 35.5  최대 97.0   [선택]   │  ← 요약 띠(클릭=접기)
│100┤╌╌╌╌╌╌┆╌╌╌╌╌╌┆╌╌╌╌╌╌┆╌╌╌╌╌╌┆╌╌╌╌╌╌┆╌╌╌╌╌╌╌╌╌╌╌╌╌          │
│   │      ┆   ╱╲ ┆░░░░░░┆       ┆      ┆                      │  ← 96dip
│ 50┼╌╌╌╌╌╌┆╌╱╌╌╌╲┆░선택░┆╌╌╌╌╌╌╌┆╌╌╌╌╌╌┆╌╌╌╌╌╌╌╌╌╌╌╌╌         │
│   │  ╱╲╱╲┆╱     ┆░░░░░░┆  ╱╲   ┆   ╱╲ ┆                      │
│  0└──────┴──────┴──────┴───────┴──────┴─────────────         │
│ ⌄ ▦ GPU0 RTX 5080  평균 54.3%  최소 15.8  최대 96.0 [선택]   │
│   ( 차트 )                                                   │  세로 스크롤
│ › ▦ 이더넷 Realtek  평균 36.9%  최소 11.1  최대 62.0 [선택]  │  ← 접힘
├──────────────────────────────────────────────────────────────┤
│      14:33    14:36    14:39    14:42    14:45               │  ← 눈금자(어림수 시각)
│ 14:32:07 ◀━━━━━━━━━━━━━━━━━━━━━━━━▶ 14:47:07   ⊖ 2.4× ⊕      │
└──────────────────────────────────────────────────────────────┘
```

**차트 높이는 116dip 이다.** 메인 창의 펼친 카드는 최소 118dip 이지만 헤더 26 · 푸터 20 을 빼면
차트에 남는 것은 약 72dip 이다. 여기는 요약 띠가 헤더와 푸터를 겸하므로 그만큼을 차트에 주고,
**값을 읽는 창이라 곁눈질용보다 더 준다** — 세로가 눌리면 평평한 구간의 오르내림이 선 굵기에
묻힌다. 접힌 줄은 요약 띠만 남아 22dip 이다.

#### GPU 는 판을 둘 쓴다

사용률은 **퍼센트**이고 메모리는 **바이트**다. 한 그림에 겹치면 둘 중 하나는 읽을 수 없는데,
스냅샷을 여는 이유가 대개 "그 구간에 VRAM 이 얼마나 찼나" 라서 빼 둘 수도 없다.

> **그렇다고 장치를 둘로 쪼개지는 않는다.** 카드가 둘이 되면 한 GPU 의 이야기가 두 곳으로
> 갈리고, 접기·선택·요약이 따로 놀아 "같은 순간을 가로질러 본다"가 무너진다. **판만 더 붙인다.**

| 항목 | 결정 |
|---|---|
| 붙는 조건 | `GpuDedicated` 가 있는 장치. NPU 처럼 사용률만 있는 어댑터에는 붙지 않는다 |
| 계열 | 전용(주) + 공유(짝) — 메인 창의 GPU 메모리 카드와 같은 짝이다 |
| 요약 띠 | 장치 줄은 **사용률만** 적는다. 장치명과 `선택 구간` 칩이 이미 자리를 쓴다 |
| 높이 | **77dip.** 주 판(116)보다 낮다 — 딸린 것이지 맞선 것이 아니다 |
| 이름표 | 홈통에 맞춰 들여쓴 `메모리   전용 평균 …  최소 …  최대 …    공유 평균 …  최소 …  최대 …` 한 줄. **점도 셰브런도 장치명도 없다.** 보려는 것이 대개 "전용이 얼마나 찼고 공유로 얼마나 샜나" 라 **둘을 나란히** 적는다. 최소 폭에서는 넘치므로 말줄임으로 자른다 — 그냥 잘리면 값이 틀려 보인다 |
| 접기 | 요약 띠 하나로 **둘 다** 접힌다. 판이 둘이어도 장치는 하나다 |
| 그리기 | **전용을 바닥에 깔고 공유를 그 위에 쌓는다.** 띠의 윗변이 곧 합계다 |
| 축 | **용량 기준**(`GpuMemoryAxis`) — 메인 창과 같은 것을 쓴다 |
| 용량선 | 전용 VRAM 자리에 파선. 넘긴 구간이 있으면 경고색 |

첫 창 높이는 **860dip 과 작업 영역의 90% 중 작은 쪽**이다. GPU 가 판을 둘 쓰면서 620 으로는
마지막 장치가 잘린 채 열렸는데, 그렇다고 고정으로 키우면 작업 영역이 낮은 노트북에서 창이
화면 밖으로 나간다.

#### 짝이 있는 계열에는 이름을 붙인다

두 계열은 **같은 색에 진하기만 다르다**(§8.2 의 미러 차트와 같은 규칙). 그림만으로는 어느 쪽이
전용이고 어느 쪽이 공유인지 알 수 없으므로, 순간을 집었을 때 뜨는 오버레이에 이름을 먼저 적는다.

| 지표 | 이름 |
|---|---|
| `GpuDedicated` · `GpuShared` | `전용` · `공유` |
| `NetRx` · `NetTx` | `수신` · `송신` |
| `DiskRead` · `DiskWrite` | `읽기` · `쓰기` |
| `GpuCompute` | `Compute` |
| `MemUsed` · `MemCommit` | `사용` · `커밋` |

> **계열이 하나뿐이면 이름을 적지 않는다.** CPU 사용률에 `사용률` 을 붙여 봐야 장치 이름이
> 이미 말한 것을 되풀이할 뿐이다. GPU 사용률도 주 계열은 이름 없이 값만 적고, 짝인
> `Compute` 에만 붙인다.

#### 메모리 판은 메인 창의 규칙을 그대로 쓴다

**메모리는 파형이 아니라 점유량이다**(§8.3). 축을 최고치에 맞춰 움직이면 1GB 를 쓰든 7GB 를
쓰든 영역이 같은 높이로 그려져 "얼마나 잡고 있고 얼마나 남았는가"를 읽을 수 없다. 그래서
축 상한을 `GpuMemoryAxis.Max` 에 맡긴다 — **메인 창과 같은 함수**다. 판단이지 그리기가
아니므로 `Core` 에 있고, 두 창이 다른 답을 낼 수 없다.

| 항목 | 규칙 |
|---|---|
| 쌓는 순서 | **전용이 아래, 공유가 위.** 띠의 윗변이 합계다 |
| 최고치 | 따로가 아니라 **합계**로 잰다. 따로 재면 축이 낮게 잡혀 띠가 잘린다 |
| 공유 띠 | 색이 아니라 **해치 패턴**으로도 구분한다(색약 배려). 외장은 경고색, 내장은 평소 색 |
| 용량선 | 외장에서 전용 VRAM 자리에 파선. 파선 절반이 잘리지 않도록 최소 1px 안으로 들인다 |

> **스필 판정만 메인 창과 다르다.** 메인 창은 *지금* 넘쳤는지를 본다 — 흐르는 화면이라
> 마지막 샘플이 곧 현재다. 스냅샷은 흐름이 멈춘 창이라 **보고 있는 구간에 한 번이라도
> 넘긴 적이 있는지**로 본다. 구간을 재려고 연 창에서 "마지막 순간에 넘쳤나"는 읽을 값이 아니다.

#### 가이드 눈금

메인 창은 격자를 옅게 한 겹만 깐다 — 곁눈질로 **형태**만 보면 되기 때문이다.
스냅샷 창은 값을 **읽는** 창이라 눈금이 있어야 한다.

| 축 | 눈금 | 라벨 |
|---|---|---|
| 세로(값) | 퍼센트는 25·50·75 에 가로선, 50% 선만 조금 진하게. 용량·전송률은 단위에 맞춘 어림수 | 차트 왼쪽 26dip 홈통에 최대·중간·0 셋 |
| 가로(시간) | **어림수 시각**에 세로선 | 차트 아래 눈금자 줄에 **한 번만** |

> **세로선은 데이터 시작이 아니라 어림수 시각에 놓는다.** 시작점(14:32:07)부터 등간격으로 그으면
> 눈금이 14:35:07 · 14:38:07 로 읽히는데, 그 숫자로는 아무것도 셈할 수 없다. 14:33 · 14:36 · 14:39
> 에 놓아야 "여기서 3분 뒤"가 눈으로 계산된다. 간격은 배율에 따라 1·2·5·10·15·30초 → 1·2·5·10·15·30분
> 중에서 고른다 — **화면에 4~8개가 들어가는 가장 큰 값**이다.

> **시간 눈금은 모든 차트에 같은 x 에 그린다.** 차트마다 다르면 세로로 정렬되지 않고, 그러면
> "같은 순간을 가로질러 본다"는 이 창의 존재 이유가 무너진다. 값 눈금은 지표마다 다르므로 차트마다 다르다.

> **스냅샷 창은 §9.3 텍스트 예산의 예외다.** 카드당 문자 요소 5개 제한은 곁눈질용 위젯의 규칙이고
> 이 창은 들여다보는 창이다. 예외라는 것을 적어 두지 않으면 나중에 누군가 "규칙 위반"으로 지운다.

#### 장치별 접기

요약 띠를 클릭하면 그 장치의 차트가 접힌다 — 메인 창과 **같은 제스처**(§8.6)다.

| 항목 | 스냅샷 창 | 메인 창 |
|---|---|---|
| 접는 제스처 | 요약 띠 클릭 — **줄 전체가 버튼**이다 | 카드 헤더 클릭 — 같다 |
| 접힌 줄에 남는 것 | **요약 숫자 전부** | 28dip 스파크라인 |
| 자동 접힘 | **없다** | 자리가 모자라면 접는다 |
| 상태 저장 | 창을 닫으면 사라진다 | `settings.json` 에 남는다 |

> **접어도 숫자는 남긴다.** 메인 창에서 접는 것은 "지금 관심 없다"는 뜻이라 스파크라인만 남기면 되지만,
> 이 창에서 접는 것은 **자리를 비워 다른 장치를 크게 보려는 것**이다. 접힌 장치가 비교 대상에서
> 빠지면 안 된다 — 선택 구간을 옮기면 접힌 줄의 숫자도 함께 바뀐다.

> **요약 띠에 투명 배경을 깐다.** WPF 는 <b>그린 픽셀 위에서만</b> 히트 테스트한다 —
> 배경이 없는 패널은 셰브런과 글자 위만 눌려, 접으려면 11px 짜리 화살표를 정확히 찍어야 한다.
> 차트가 빈 구간에서도 커서를 받으려고 투명 사각형을 먼저 까는 것과 같은 이유다(§8.7).

> **자동 접힘은 넣지 않는다.** 메인 창의 자동 접힘은 스크롤이 없어서 생긴 장치다(§8.6).
> 여기는 세로 스크롤이 있어 자리가 모자랄 일이 없고, 내가 접지 않은 것이 접히면 그저 고장으로 보인다.
**요약은 차트마다 그 머리에 붙인다.** 장치별 카드를 위에 몰아 두는 안도 있었지만,
차트가 8개면 스크롤한 순간 숫자와 그림이 갈라진다. 숫자는 그것을 설명하는 그림 옆에 있어야 한다.

**툴바는 아이콘만 쓴다.** 글리프 사양은 §8.8, 이름은 hover 툴팁에 적는다.

| 글리프 | 이름 | 하는 일 | 비활성 조건 |
|---|---|---|---|
| `FitWidth` | 전체 | 크롭을 풀고 전체 구간을 폭에 맞춘다 | 크롭이 없을 때 |
| `Crop` | 선택 구간으로 크롭 | 분석 도메인을 선택 구간으로 좁힌다 | 선택이 없을 때 |
| `Undo` | 되돌리기 | 직전 크롭을 취소한다 | 크롭 스택이 비었을 때 |
| `Export` | CSV 내보내기 | 파일로 낸다. 선택이 있으면 **저장 대화상자에서 범위를 고른다** | 없음 — 늘 활성 |

> 최소 확대 단위는 **8점**이다. 그보다 좁히면 선이 아니라 점 몇 개가 되어 형태를 읽을 수 없다.
> 배율은 `크롭 구간 ÷ 보이는 점 수` 로 적는다.

> **할 수 없는 버튼은 숨기지 않고 흐리게 둔다.** 버튼이 사라졌다 나타나면 툴바의 자리가 움직여
> 근육 기억이 무너진다. 흐린 버튼은 "지금은 안 되지만 언젠가 되는 것"을 말한다 — 선택을 해야
> 크롭할 수 있다는 것을 아이콘이 스스로 가르친다.

#### 구간 선택과 통계

| 동작 | 결과 |
|---|---|
| 차트 위 드래그 | 반투명 밴드로 구간 선택. 축 줄에 `시작 ~ 끝 · 길이` |
| 차트 클릭(끌지 않음) | **그 순간**을 집는다 — 구간이 아니므로 통계가 아니라 그때의 값을 읽는다 |
| `Shift`+클릭 | **이미 잡아 둔 자리를 한 끝으로 삼아** 구간을 만든다. 구간이 있으면 끝을 옮긴다 |
| 선택 중 | 요약 띠의 값이 **표시 구간 → 선택 구간**으로 바뀐다 |
| `Esc` | 선택·순간 해제, 요약이 표시 구간으로 복귀 |

> **`Shift`+클릭을 두는 이유는 끌기가 길이에 약하기 때문이다.** 구간이 길수록 끝까지 끄는
> 동안 손이 흔들리고, 한 번 놓치면 처음부터 다시 끌어야 한다. 한 번 찍고 반대쪽을 찍으면
> 길이와 무관하게 **두 번**이면 끝난다. 목록·텍스트 선택에서 굳은 관습이라 따로 가르칠 것도 없다.
> 찍은 뒤 이어서 끌면 그 끝이 따라오므로, 대강 잡고 다듬는 것도 된다.

**순간을 집으면 오버레이가 뜬다.** 메인 창의 스크럽(§8.7)과 같은 읽기다 — 점선 세로선이
전 차트에 같은 x 로 서고, 각 계열의 값이 점으로 찍히고, 차트 위 칩에 계열 색과 값이 적힌다.

> **시각은 한 곳에만 적는다.** 축이 하나이므로 행마다 되풀이하면 같은 값이 여덟 번 보인다.
> 아래 축 바에 `23:34:34.000` 로 한 번 적고, 칩에는 값만 둔다.

> 칩의 좌우 플립은 **내용**에서 폭을 잰다. 테두리를 두른 쪽을 재면 지난번에 밀어둔 여백이
> 이번 폭에 섞여 조건이 매번 뒤집힌다 — 메인 창에서 실제로 겪은 일이라 같은 방식으로 피한다.

**선택은 모든 차트에 동시에 걸린다.** 시간 축이 하나라는 원칙(§1.1)이 여기서도 유지된다 —
"GPU 가 튄 그 구간에 디스크는 뭘 했나"가 이 창의 존재 이유다.

> **통계는 실측 샘플만 센다.** Slow 티어 지표는 Fast 틱마다 직전 값이 다시 기록되는데(§7.2),
> 그 유지값을 세면 표본 수가 주기 비율만큼 부풀고 편차 0인 반복이 표준편차를 끌어내린다.
> 실측 표본이 0개인 구간은 `0` 이 아니라 **`—`** 로 적는다. 없는 것과 0 은 다르다(§10.4).

**고른 구간이 언제부터 언제까지인지는 축 줄에 적는다** — `23:32:28 ~ 23:32:44 · 16.0초`.
요약 띠는 **얼마였나**(평균·최소·최대)를 말하고 이 줄이 **언제였나**를 말한다. 끄는 동안
따라 움직이므로, 원하는 길이에 맞춰 손을 멈출 수 있다.

| 자리 | 적는 것 |
|---|---|
| 축 줄 왼쪽 끝 | 보이는 구간의 시작 — 선택과 무관하다(`Dim`) |
| 그 옆 | **선택 구간** 또는 집은 **순간**. 자리는 하나다 — 둘은 같이 나올 수 없다(`Fg`) |
| 축 줄 오른쪽 끝 | 보이는 구간의 끝(`Dim`) |

> **길이는 소수 한 자리까지 적는다.** 250ms 로 재는데 `13초` 로 뭉개면 무엇을 골랐는지
> 흐려진다. 반면 양 끝 시각은 초까지만 적는다 — 밀리초까지 넣으면 줄이 길어져 최소 폭에서
> 스크롤바를 밀어낸다. 정밀도는 길이 쪽이 쓸모 있다.
>
> 한 점만 집었을 때는 반대다. 그때는 **순간**이 전부이므로 `23:32:35.250` 처럼 밀리초까지 적는다.

#### 크롭 — 자르되 버리지 않는다

선택 구간으로 크롭하면 **분석 도메인**이 좁아진다. 요약도 내보내기도 그 범위만 본다.

> **배열은 그대로 두고 경계만 좁힌다.** 잘못 자른 것을 되돌릴 수 없으면 사용자는 자르기를
> 아예 피하게 되고, 그러면 기능이 없는 것과 같다. 원본을 남기는 값은 600KB 이고
> `[되돌리기]`·`[전체]` 를 얻는다 — 교환이 명백하다.

#### 확대 · 축소와 스크롤

| 항목 | 결정 |
|---|---|
| 확대 축 | **가로(시간)만.** 세로는 지표의 축이라 고정한다 — 퍼센트 0~100, GPU 메모리는 용량 기준(§8.2·§8.3) |
| 배율 범위 | 1× (크롭 구간 전체가 폭에 들어감) ~ 샘플당 4px |
| `Ctrl`+휠 | **커서 위치 기준 확대·축소.** 커서가 가리키는 시각을 붙든 채 배율만 바뀐다 |
| 하단 `ZoomOut`/`ZoomIn` 버튼 | 같은 일 — 화면 중앙 기준. 휠을 모르는 사람의 경로다 |
| 휠 / `Shift`+휠 | 세로 / 가로 스크롤 |
| 가로 스크롤바 | **창 전체에 하나.** 차트마다 두면 시간 축이 어긋난다 |

> 배율이 낮아 한 픽셀에 여러 샘플이 겹치면 `Decimate`(min-max, §7.2)로 그린다.
> 평균이나 단순 솎아내기로 줄이면 **짧은 피크가 사라진다** — 스냅샷을 여는 이유가 대개 그 피크다.

**두 창의 휠 배치는 한 자리에서만 같고, 그 한 자리가 일부러 같다.**

| 휠 | 메인 창 | 스냅샷 창 |
|---|---|---|
| 맨 휠 | — (스크롤이 없는 창, §9.2) | 세로 스크롤 |
| `Shift`+휠 | 창 불투명도 | 가로 스크롤 |
| `Ctrl`+휠 | **시간 폭** — 사다리 5칸 | **시간 폭** — 연속 확대 |

> `Ctrl`+휠만 두 창에서 같은 뜻이다 — 둘 다 "얼마나 긴 시간을 볼 것인가"다. 나머지 둘은
> 스크롤이 있는 창과 없는 창의 차이로 갈리므로, 한쪽을 익혀도 다른 쪽에서 헛돌지 않는다.

#### 내보내기 — CSV 하나만 낸다

`.xlsx` 는 쓰기 라이브러리가 필요하다. §3.2 의 의존성 정책은 **차트 라이브러리조차 쓰지 않는다** —
비교용 표 하나를 위해 그 선을 넘을 이유가 없다. CSV 는 엑셀이 그대로 연다.

| 항목 | 결정 | 이유 |
|---|---|---|
| 인코딩 | UTF-8 **BOM 포함** | 없으면 엑셀이 한글 헤더를 깨뜨린다 |
| 행 | 한 틱 = 한 행 | 모든 지표가 같은 틱 그리드에 기록되므로(§7.2) 리샘플링이 필요 없다 |
| 첫 열 | `timestamp` — `2026-09-26 14:32:07.250` (로컬) | §7.4 의 시각 링에서 그대로 나온다. **ISO 8601 이되 `T` 가 아니라 공백**이다 — 엑셀은 `T` 가 끼면 날짜로 읽지 않고 그냥 글자로 둔다(실측) |
| 열 이름 | `장치명 / 지표 / 단위` 를 한 줄에 합침 | 여러 파일을 나란히 놓고 비교할 때 열 이름만으로 갈려야 한다 |
| 실측이 아닌 샘플 | **빈 칸** | 유지값을 채워 내면 엑셀에서 평균을 내는 순간 틀린다. 앱 안에서 통계에 넣지 않는 것과 같은 이유다 |
| 숫자 표기 | **지수 없는 소수** (`0.######`) | 왕복 표기는 바이트·전송률을 `1.3421773E+10` 로 적는다 — 엑셀은 읽지만 사람은 읽지 않는다. 소수 6자리면 디스크 큐(`0.00001`)도 0 으로 뭉개지지 않고, `float` 은 유효숫자 7자리라 자리를 늘려도 없는 정밀도가 생기지 않는다 |

> **엑셀은 소수 초가 있으면 표시 형식을 `mm:ss.0` 으로 고른다.** 어떤 표기를 줘도 그렇다(실측 —
> `2026/09/26`·`.25`·`.000`·시각만, 전부 같았다). 값 자체는 멀쩡한 날짜/시간이라 차트 축과
> 수식은 그대로 붙고, 셀 서식만 바꾸면 날짜가 보인다. 밀리초를 빼면 표시는 깔끔해지지만 250ms
> 로 재므로 **네 줄이 같은 시각**이 되어 시계열로 그릴 수 없다. 표시보다 값을 택한다.
| 범위 | 기본은 **크롭 구간**, 선택이 있으면 대화상자에서 고른다. 확대 배율과는 무관 | 보이는 것이 아니라 정한 것을 낸다 |
| 기본 파일명 | `chronoload-20260926-143207-15m.csv` | 시작 시각과 길이로 구분된다 |

##### 범위는 저장 대화상자 안에서 고른다

요약 띠는 선택을 따라가는데(§9.6 요약) 파일은 늘 크롭 구간이 나가면, 화면에서 읽은 숫자와
파일 속 숫자가 **말없이** 달라진다. 그렇다고 저장 전에 확인 창을 하나 더 띄우면 선택이
있을 때마다 누를 것이 는다 — 고르는 자리는 어차피 열리는 대화상자 안에 있으면 된다.

**대화상자 아래에 체크 상자를 단다.** 선택이 있고 그것이 크롭 구간과 다를 때만 나타난다.
`내보낼 범위  ☑ 선택 구간만 (94초)` — **기본은 켬**이다. 방금 끌어 고른 구간을 내려는 것이
보통이다. 끄면 크롭 구간 전체가 나간다.

WPF 의 `Microsoft.Win32.SaveFileDialog` 는 속을 셸의 `IFileSaveDialog` 로 만들면서도
`IFileDialogCustomize` 를 밖으로 내주지 않는다. 그래서 셸 대화상자를 직접 연다
(`App/Services/SaveDialog`).

> **COM 인터페이스는 쓰는 자리까지만 선언한다.** vtable 은 메서드 **순서**로 잡히므로 뒷자리는
> 없어도 되지만 **중간을 하나라도 빠뜨리면 엉뚱한 함수가 불린다.** 순서를 건드릴 일이 있으면
> MSDN 의 선언 순서를 그대로 따른다.

셸이 추가 컨트롤을 놓는 자리는 **저장·취소 버튼 왼쪽**으로 고정이고 고를 수 없다. 폭도 좁아
`선택 구간만 — 14:32:07~14:33:41 · 94초` 같은 글씨는 두 줄로 접히다 결국 잘린다(실측).
**시각 그룹의 이름표만큼 왼쪽으로 밀리므로** 이름표는 그룹(`내보낼 범위`)에 두고 상자 글씨는
길이만 적는다. 시작 시각은 어차피 기본 파일명이 들고 있다.

> **파일 형식 줄과 버튼 사이의 빈 띠는 우리 것이 아니다.** 커스터마이즈를 하나도 걸지 않아도
> 그대로 있고, DPI 인식을 꺼도 같고, 창 높이를 줄이면 그 띠가 아니라 **파일 목록이 줄어든다**
> (셋 다 실측). Windows 11 저장 대화상자가 고정으로 잡아 두는 자리라 이쪽에서 없앨 수 없다.

파일명을 건드리지 않은 채 체크만 풀면 **이름도 따라간다.** 그러지 않으면 파일명 끝의 길이가
내용과 어긋난 채 남는다. 손댄 이름은 그대로 존중한다.

##### 닫을 때 활성은 소유 창으로 돌려준다

저장 대화상자를 한 번 띄우고 나서 스냅샷 창을 닫으면 **메인 창이 다른 앱 뒤로 가라앉는다**는
보고가 있었다. 창 하나짜리 재현 하네스(소유 창 + 자식 창 + 저장 대화상자)로는 재현되지 않아
정확한 경로는 짚지 못했다. 다만 대화상자가 닫힌 뒤 이 창을 다시 활성으로 만들고, 이 창이
활성인 채로 닫혔으면 활성을 소유 창에 돌려주는 것은 **어차피 맞는 동작**이라 그렇게 한다.

활성이 아니었으면 건드리지 않는다 — 다른 앱을 쓰던 사람의 앞창을 빼앗게 된다.

#### 범위 밖

- **hover 십자선 읽기** — 구간 통계가 목적이므로 한 점 읽기는 넣지 않는다. 필요해지면 그때 더한다
- **장치당 차트는 하나** — 주 지표와 짝(네트워크 수신·송신, 디스크 읽기·쓰기)까지다.
  **GPU 메모리만 예외로 판을 하나 더 받는다**(위). GPU 온도·전력·클럭, 디스크 큐·지연은
  **차트에 없지만 CSV 에는 들어간다.** 여덟 장치에 지표마다 줄을 주면 스크롤이 끝나지 않는다 —
  훑는 것은 차트로, 파고드는 것은 CSV 로 한다
- **스냅샷 저장·다시 열기** — CSV 가 그 자리를 맡는다. 자체 포맷을 만들면 버전 관리가 따라붙는다
- **두 스냅샷 겹쳐 보기** — 창을 나란히 놓는 것으로 시작하고, 부족하면 그때 설계한다

---

## 10. MCP 서버 설계 (R10) ✅ 구현됨

### 10.1 토폴로지 — 앱 실행 중에만 동작
1. **인프로세스 서버** — WPF 앱이 `ModelContextProtocol.AspNetCore`로 `http://127.0.0.1:7667/mcp`(Streamable HTTP)를 호스팅
2. **stdio 브리지** — `chronoload-mcp.exe`가 stdio ↔ HTTP 프록시

| 상황 | 동작 |
|---|---|
| 앱 미실행 상태로 호출 | 모든 툴이 `{"error":"app_not_running","hint":"ChronoLoad를 실행한 뒤 다시 시도하세요"}` (MCP `isError: true`) |
| 앱이 도중에 종료 | 진행 중 호출은 오류. 브리지는 살아남아 토큰 파일을 재확인하며 대기 |
| 앱 재실행 | 브리지가 자동 재연결. 클라이언트 재시작 불필요 |
| 헤드리스 샘플링 / 앱 자동 실행 | **제공하지 않음** |

> 헤드리스 모드는 "리셋 기준 통계"와 "히스토리"를 가질 수 없어 이 MCP의 가치 대부분을 잃는다. 반쪽짜리 응답보다 명확한 실패가 에이전트에게 낫다. 에이전트가 사용자 몰래 GUI를 띄우지도 않는다.

**보안** — 실측 확인: 토큰 없이 401, 외부 `Origin` 403
- `127.0.0.1` 고정. 외부 바인딩 옵션 없음
- 기동 시 랜덤 토큰 → `%LOCALAPPDATA%\ChronoLoad\mcp.token`(ACL: 현재 사용자만). 종료 시 삭제
- 토큰 비교는 **상수 시간**(`FixedTimeEquals`). 문자열 `==` 는 첫 불일치에서 빠져나와 응답 시간으로 한 글자씩 새어 나갈 여지를 남긴다
- `Origin` 헤더 검증(DNS 리바인딩 방어). 헤더가 없는 비브라우저 클라이언트는 통과
- 읽기 전용 원칙. 상태를 바꾸는 툴은 `reset_stats`·`mark`·`watch_process`·`unwatch_process` 뿐이고 **바꾸는 것은 전부 MCP 쪽 메모리**(기준점·이름표·기록 목록)다. 시스템도, 화면 통계도 건드리지 않는다. **프로세스 종료·우선순위 변경 툴은 제공하지 않는다**

> **토큰 파일에 PID 를 함께 적고 읽을 때 확인한다.** 앱이 정상 종료하면 파일을 지우지만
> 강제 종료되거나 죽으면 남는다. 그대로 두면 브리지가 없는 앱에 계속 연결을 시도해,
> 사용자는 `app_not_running` 대신 알 수 없는 타임아웃만 보게 된다. 죽은 PID 면 낡은 파일을 지운다.

> **ACL 강화 실패는 서버를 세우는 일을 막지 않는다.** 상위 폴더인 `%LOCALAPPDATA%` 가 이미
> 사용자 전용이라 이것이 유일한 방어선이 아니다. 추가 잠금이 안 됐다고 모니터링 전체를
> 포기하는 것은 균형이 맞지 않는다.

### 10.2 툴 목록

| 툴 | 입력 | 출력 요약 |
|---|---|---|
| `get_system_snapshot` | — | CPU/메모리 + **`gpus[]`, `disks[]`, `networks[]` 배열** + 호스트 정보 |
| `get_gpu_status` | `adapterKey?`, `adapterIndex?`, `verbose?` | 생략 시 전 어댑터. 모델명, **제조사**, 외장/내장, 사용률, **`memoryBusyPercent`**(NVML), **`aiSignals`**(AI 작업이 실리는 지표, §5.4), **`powerLimitWatts`·`powerLimitPercent`**, **`throttling`·`limitReasons`**(클럭 제한 사유, §5.4), 전용/공유 메모리, 온도, 전력, 클럭, `vramExceeded`, **활성 센서 계층**. `verbose` 면 `engines`(3D·Compute·Copy·Video 계열, 시계열과 같은 값)와 `engineTypes`(`engtype` 별 원값) |
| `get_disk_status` | `diskIndex?` | 읽기/쓰기 B/s, 활성 %, 큐, 응답 ms, **매체(SSD/HDD)**, 버스, 모델·용량 |
| `get_network_interfaces` | `includeTunnels?` | 인터페이스별 이름/**종류**/링크 속도/RX·TX B/s/누적. Wi-Fi는 SSID·신호·대역. **터널은 기본 제외**이며 포함 시 `countedTwiceOn` 필드로 하위 인터페이스를 명시 |
| `get_metric_history` | `metric`, `deviceKey?`, `windowSeconds`(≤900), `maxPoints`(≤500) | **실측 표본만**, 시각과 함께. `startAt` + `offsetsMs[i]` 가 점의 시각이고 `measuredPeriodMs` 가 그 지표의 실제 갱신 주기다. 표본이 `maxPoints` 이하면 `raw`(점 하나 = 실측 하나), 넘치면 `bucketed` — 시간을 **정확히 `maxPoints` 칸으로 등분**해 칸마다 `avg`·`min`·`max`·`samples`. 빈 칸은 `null` |
| `get_stats_since_reset` | `metric?`, `deviceKey?`, `saturationThreshold?`(기본 90) | `{ resetAt, elapsedSeconds, sampleCount, avg, min, max, p50, p95, p99, quantilesExact, stdDev }`. 백분율 지표는 `saturationThreshold`·`saturatedFraction` 을 더한다(§7.3) |
| `reset_stats` | `confirm: true`, `includePrevious?`, `metric?`, `deviceKey?` | 리셋 후 **직전 구간 통계를 반환**. 표본이 없던 지표는 빼고 그 수를 `omittedEmpty` 로. 거르는 인자는 **돌려받을 범위만** 좁힌다 — 리셋은 늘 전 지표에 걸린다. 지표마다 기준점이 다르면 구간끼리 비교할 수 없다 |
| `list_processes` | `sortBy`(cpu\|memory\|gpu\|gpuMemory\|diskIo), `adapterKey?`, `limit`(≤50), `nameFilter?` | PID, 이름, CPU%, 워킹셋, 어댑터별 GPU%·메모리, 디스크 I/O. `gpu`·`gpuMemory`·`diskIo` 정렬은 **그 값이 0 인 프로세스를 빼고** 수를 `excludedIdle` 로 — 동률 0 이 PID 순으로 뒤따라 붙으면 쓰지 않는 프로세스가 순위에 끼어 보인다. 동률은 CPU → 워킹셋 순. 어댑터 키는 대소문자를 가리지 않는다 |
| `get_process_detail` | `pid` | 위 + 경로, 명령줄(권한 허용 시), 부모 PID, 어댑터별·엔진별 GPU 사용률 |
| `watch_process` | `pid`, `durationSeconds?`(기본 600, ≤3600) | 1초 기록 시작(§5.6). 최대 8개. 이미 감시 중이면 기한만 늘린다 |
| `get_process_history` | `pid`, `windowSeconds?`(≤900), `maxPoints?`(≤500) | `startAt` + `offsetsMs`, `cpuPercent`·`workingSetBytes`, 어댑터·계열별 `gpu[]`. 넘치면 시간 등분해 칸마다 `avg`·`max` |
| `unwatch_process` | `pid` | 감시를 풀고 기록을 버린다 |
| `mark` | `label`, `note?` | 지금 시각에 이름을 붙인다. 같은 이름이면 옮기고 `movedFrom` 으로 알린다. 앱 메모리에만 있다(최대 200개) |
| `list_marks` | — | 마커 목록, 링 버퍼 안인지(`inBuffer`) |
| `get_interval_stats` | `from`, `to?`(생략 시 지금), `metric?`, `deviceKey?`, `saturationThreshold?` | 두 시점(마커 이름 또는 ISO-8601) 사이의 정확한 통계(§7.3). 리셋 구간과 무관하다. 표본이 없는 지표는 빼고 `omittedEmpty` |
| `compare_intervals` | `marks[]`, `untilNow?`, `metric?`, `deviceKey?`, `saturationThreshold?` | 마커를 시각순으로 이은 구간들을 지표별 한 행에. 칸마다 `n`·`avg`·`p95`·`max`, 백분율은 `saturatedFraction` |
| `describe_capabilities` | — | 어댑터별 센서 계층(NVML/ADLX/IGCL/LevelZero/PDH), 디스크·인터페이스 목록, 샘플 주기, 버퍼 용량, **`devicesRevision`**, GPU·NPU 마다 `aiSignals`. 계층 표의 키는 **장치 키**다 — 이름으로 잡으면 같은 모델 두 장이 겹친다. 절전이라 벤더 경로를 아직 열지 않은 어댑터는 `PDH (standby)` 로 적는다(§6.3). 이유가 없으면 같은 장치가 한 번은 PDH, 깨어난 뒤에는 IGCL 로 보여 두 툴이 서로 다른 말을 하는 것처럼 읽힌다 |

### 10.2-1 브리지의 실패 설계

앱이 꺼져 있을 때 **`initialize` 와 각종 `*/list` 는 성공**시키고, 실제 툴 호출만
`app_not_running` 으로 돌려준다.

> `initialize` 에서 실패하면 클라이언트가 세션 자체를 포기해, 나중에 앱을 켜도
> 클라이언트를 다시 시작하기 전까지 붙지 않는다. 또 JSON-RPC **오류가 아니라 정상 결과에
> `isError`** 로 돌려준다 — 전송 오류로 보내면 클라이언트가 서버를 죽은 것으로 간주한다.

### 10.3 장치 변경과 MCP
- 모든 응답에 **`devicesRevision`**(장치 집합이 바뀔 때마다 증가하는 정수)을 포함한다. 에이전트는 이 값만 비교해 "내가 알던 구성이 그대로인가"를 판단할 수 있다
  - **재열거만으로는 오르지 않는다.** §5.7 의 검증 재열거는 장치가 안 바뀌어도 30초마다 도는데, 그때마다 올리면 이 약속이 거짓이 된다(구현 초기에 실제로 그랬다 — §12)
  - 반대로 **장치 설명이 바뀌면 오른다.** Wi-Fi 링크 속도가 재협상되면 카드 이름이 바뀌므로(`Wi-Fi 2.4Gbps` → `Wi-Fi 2.2Gbps`) 캐시를 들고 있는 에이전트는 다시 물어봐야 한다. 키·인덱스는 그대로다
- 장치 배열의 원소는 항상 `index`와 **`key`(LUID/시리얼/인터페이스 GUID)** 를 포함한다. `index`는 순서가 바뀔 수 있으므로 **재조회 시에는 `key`를 쓴다**
- `get_metric_history` / `get_stats_since_reset`은 `deviceKey`를 받아 인덱스 변동의 영향을 받지 않는다

### 10.4 리소스 · 응답 지침
- 리소스 `chronoload://snapshot`, `chronoload://stats`
- 프롬프트 `analyze_gpu_workload` — 병목(연산 / VRAM / 디스크 I/O / 네트워크) 진단 템플릿
- **직렬화는 SDK 기본값을 두 군데 바꾼다**(`McpHost.ToolJson`). SDK 기본은 `null` 필드를 **통째로 생략**해 "값이 없으면 null" 이 전선 위에서 사라졌고(에이전트는 null 인지 필드가 없는지 가를 수 없다), 비ASCII 를 전부 `\uXXXX` 로 바꿔 한글 설명 문장이 글자당 6바이트로 부풀었다. 둘 다 툴 메서드를 직접 부르는 테스트로는 보이지 않아, 서버를 세워 JSON-RPC 로 부르는 테스트(`McpWireTests`)를 둔다
- 바이트는 `{"bytes": 8589934592, "text": "8.00 GB"}` 형태로 둘 다. 계산은 `bytes` 로 한다 — `text` 는 사람이 읽는 줄이고 반올림이 들어 있다
- 시각은 ISO-8601(로컬 오프셋). 모든 응답에 `sampledAt`, `stale` 포함
- 기본 요약형, `verbose: true`로 확장

---

## 11. 설정 및 영속성
- 경로: `%LOCALAPPDATA%\ChronoLoad\settings.json` (원자적 쓰기: temp → `File.Replace`)

**항목은 여섯이다.** 초안에는 열두 가지가 나열돼 있었지만 대부분 만들지 않았다.
설정이 적은 것은 만들다 만 것이 아니라 의도다 — 곁눈질로 읽는 위젯에서 고를 것이 늘면
그만큼 외울 것이 는다(§8.6 프리셋을 걷어낸 이유와 같다).

| 키 | 값 | 어디서 바꾸나 |
|---|---|---|
| `schemaVersion` | 정수 | — |
| `window` | 물리 픽셀 `left`·`top`·`width`·`height` | 창을 옮기고 크기를 바꾸면 |
| `topmost` | 불리언 | 핀 버튼 · 설정 팝오버 |
| `opacity` | 0.25 ~ 1.0 | `Shift`+휠 · 설정 팝오버 |
| `theme` | `system` · `dark` · `light` | 달 버튼(라이트·다크만) · 설정 팝오버(셋 다) |
| `collapsed` | 장치 키 → 접힘 여부 | 카드 헤더 클릭 · `1`~`9` |
- **장치별 설정은 인덱스가 아니라 `DeviceInfo.Key`로 저장** — 장치 순서가 바뀌어도 설정이 따라간다
- `schemaVersion` 필드, 알 수 없는 키는 보존
- 설정 UI는 별도 팝오버 창 (메인 화면의 단일 화면 원칙을 깨지 않는다) ✅ 구현됨

#### 설정 팝오버 ✅ 구현됨

제목 표시줄의 슬라이더 버튼으로 연다. 불투명도 · 항상 위 · 테마 셋뿐이다.

| 항목 | 메모 |
|---|---|
| 창 불투명도 | 슬라이더 25~100%, 5% 눈금. `Shift`+휠과 **같은 간격** — 두 경로가 다른 값을 만들면 안 된다 |
| 항상 위 | 핀 버튼과 같은 값 |
| 테마 | **시스템·라이트·다크 셋.** 달 버튼은 라이트·다크만 오가므로 팝오버가 더 넓다 |

> **확인 버튼이 없다.** 값이 즉시 적용되므로 미리보기가 곧 결과이고, "적용"과 "취소"가
> 가리킬 상태가 없다. 닫는 길은 제목 줄 오른쪽의 **✕** 와 `Esc` 둘이다.

> **포커스를 잃으면 닫히게 두었다가 걷어냈다.** 불투명도는 **본 창을 보면서** 맞추는 값인데,
> 본 창을 한 번 누르면 설정 창이 사라져 버렸다 — 미리보기를 확인하는 행동이 곧 창을 닫는
> 행동이 되면 안 된다. 팝오버의 관습(바깥을 누르면 닫힘)보다 이쪽이 맞다.

> ✕ 는 24dip 상자에 **투명 배경**을 깐다. 없으면 12dip 짜리 획 위에서만 눌린다 —
> 스냅샷 창의 요약 띠에서 겪은 것과 같은 함정이다(§9.6).

> **OS 기본 체크박스·라디오를 쓰지 않는다.** 상자를 흰색으로 칠해 다크 팔레트 위에서 창에서
> 가장 밝은 것이 되고, 색을 눌러 다듬으면 이번에는 선택 표시가 사라진다. 시간 폭 칩(§9.4)과
> 같은 언어로 직접 그린다 — 켜짐은 강조색 테두리와 옅은 배경으로 말한다.

> **테마가 그동안 저장되지 않았다.** 달 버튼으로 바꿔도 재시작하면 잃었다. 팝오버를 만들며
> 함께 고쳤다 — 설정 창이 있는데 그 값이 남지 않으면 창이 거짓말을 하는 셈이다.

#### 설정 폴더는 `LOCALAPPDATA` 를 먼저 본다

`Environment.GetFolderPath` 는 **환경변수를 보지 않고 Windows 셸에 직접 묻는다.** 그래서
환경변수를 바꿔 격리했다고 믿은 테스트가 실제 사용자 설정을 읽고 썼다. 실제로 그랬다 —
테스트 한 번에 사용자의 창 위치가 지워지고, 렌더 테스트의 합성 장치 키(`gpu:demo` 등)가
실제 파일에 남았다. 조용히 일어나서 한참 뒤에야 "창 위치가 자꾸 초기화된다"로 드러났다.

환경변수를 먼저 보고, 없거나 비어 있으면 셸에 묻는다. 격리가 실제로 되고 실사용 동작은 그대로다.
렌더 테스트는 시작할 때 이 값을 임시 폴더로 돌려 읽기도 쓰기도 격리한다.

#### 창 위치는 물리 픽셀로 적는다 (`schemaVersion` 2)

WPF 의 `Left`/`Top` 은 DIP 이고, DIP↔픽셀 환산에 쓰이는 배율은 **창이 지금 놓여 있는 모니터**의
것이다. 그래서 DIP 로 저장하면 배율이 다른 모니터에서 복원 위치가 어긋난다.

노트북 실측 — 175% 모니터에서 물리 2940px 에 있던 창은 `left: 1680`(DIP)으로 저장되고,
재시작 때 창이 아직 200% 모니터에 있으므로 1680 × 2.0 = **3360px** 에 놓였다. 420px 어긋났는데,
그 자리도 두 번째 모니터 안이라 §11 의 "화면 밖" 검사를 그대로 통과했다.
**틀린 값이 유효해 보이는 형태로 틀렸다는 점이 이 버그의 성질이다.**

그래서 저장·복원 모두 `GetWindowRect` / `SetWindowPos` 의 물리 픽셀을 쓴다. 모니터 작업 영역도
같은 단위라 겹침 계산이 그대로 맞는다.

- **`SetWindowPos` 는 두 번 부른다.** 배율이 다른 모니터로 넘어가는 첫 호출이 `WM_DPICHANGED` 를
  일으키고, WPF 가 그 처리에서 창을 새 배율에 맞춰 다시 잡는다 — 방금 준 픽셀 크기가 배율비만큼
  줄어든다(실측 595×1267 → 525×1109). 두 번째 호출은 같은 배율 안이라 크기만 제자리로 돌려놓는다
- **`schemaVersion` 1 의 창 위치는 버린다.** 그 DIP 값이 어느 모니터 배율로 나눈 것인지가 파일에
  남아 있지 않아 환산할 수 없다. 한 번 자리를 잃는 쪽이 매번 어긋난 자리에 뜨는 것보다 낫다.
  위치 외의 항목(테마·접힘·Topmost)은 그대로 이어받는다

---

## 12. 성능 목표 및 검증

| 지표 | 목표 | 측정 |
|---|---|---|
| CPU 점유 (8코어, GPU2+디스크2+NIC2) | 평균 < 1.0%, 피크 < 2.5% | 자기 프로세스 24h 관찰, ETW |
| 워킹셋 | 시작 < 230MB, 증가 < 10MB/24h | 장시간 누수 테스트 |
| GC | 정상 루프 Gen0 < 1회/분 | `dotnet-counters` |
| 샘플 지터 | 250ms 주기 p99 < 40ms | 내부 계측 |
| 시작 시간 | 첫 프레임까지 < 800ms | ReadyToRun, 센서 초기화 비동기화 |
| GPU 사용률 오차 | 작업 관리자 대비 ±3%p (어댑터 각각) | 수동 교차검증 |
| **장치 변경 반영 지연** | 이벤트 → 카드 반영 < 500ms | Wi-Fi on/off, eGPU 착탈 실측 |
| **장치 변경 중 샘플 손실** | 0 | 재열거 동안 Fast 티어 틱 카운트 확인 |

#### 워킹셋 목표를 90MB 에서 230MB 로 고친 이유

처음 90MB 는 근거 없이 잡은 숫자였다. 실제로 재 보니 구성 요소는 이렇다.

| 계층 | 워킹셋 | 증분 |
|---|---:|---:|
| .NET + PDH 카운터 | 47.5 MB | — |
| + `DeviceWatcher` | 48.4 MB | +0.9 |
| + 벤더 SDK (NVML · IGCL/Level Zero) | 69.4 MB | **+21.0** |
| + ASP.NET Core MCP 호스트 | 86.6 MB | **+17.2** |
| + WPF (게시 빌드, 카드 10장) | 209.2 MB | **+122.6** |

**대부분은 WPF 다.** 우리 코드가 아니라 프레임워크와 하드웨어 렌더 표면의 비용이라
줄일 여지가 거의 없다. 남은 손잡이는 ASP.NET Core 를 `HttpListener` 기반 최소 서버로
바꾸는 것 하나인데, 얻는 것은 약 17MB(8%)이고 잃는 것은 SDK 가 처리해 주는 전송 계층이다.
지금은 교환이 맞지 않는다고 본다.

#### 두 번째 기기 — 노트북 검증

0.9.0 까지의 실측은 전부 **데스크톱 한 대**(단일 모니터 2560×1440, 배터리 없음,
외장 GPU 2장 + 외장 Arc + NPU)에서 나왔다. 그 환경에 없는 조건을 노트북에서 확인했다.

**검증 기기** — Samsung Galaxy Book5 Pro (940XHA) · Core Ultra 5 226V (Lunar Lake, 8코어) ·
Arc 130V 내장 GPU + AI Boost NPU · RAM 16GB · Windows 11 26200 ·
내장 2880×1800 @200% + 가상 디스플레이 1668×2224 @175%

| 항목 | 결과 |
|---|---|
| **내장 GPU 텔레메트리** | ✅ `telemetryLayers` 가 `IGCL` 로 뜬다. 내장에서도 `ctlPowerTelemetryGet` 경로가 그대로 붙는다. 다만 **온도는 나오지 않는다**(아래) |
| **전용 VRAM 없는 어댑터** | ✅ 전용 **용량** 128 MiB(임계값 1 GiB 미만) → `discrete:false` 로 내장 분류. 전용 **사용량**은 내내 0 이고 공유가 1.3~1.5 GiB 로 움직인다. `vramExceeded:false` 고정. §5.4 의 설계대로다 |
| **모니터별 DPI 배율 차이** | ❌ → ✅ **어긋났다.** 200%↔175% 사이에서 창 위치가 420px 밀렸다. 저장 단위를 물리 픽셀로 바꿔 고쳤다(§11) |
| **배터리 절전 → `Reduced`** | ⚠️ **검증할 수 없었다.** 이 기기에서는 Windows 설정이 "절전 모드 켬"이어도 상태 API 가 꺼짐으로 답한다(아래) |
| **누수 24h** | 🔶 **부분 판정.** 4시간 19분 관측 — GDI 는 누수 없음으로 판정했고 핸들·스레드도 추세가 없다. 커밋 메모리는 잡음이 커서 이 길이로 판정할 수 없다(아래) |

**이 기기에서 새로 드러난 것** — 어느 것도 데스크톱에서는 재현되지 않았다.
공통 원인은 하나다: **데스크톱에서는 모든 지표에 값이 들어왔다.**

| 증상 | 원인 | 조치 |
|---|---|---|
| `get_metric_history` 가 `GpuTemp` 에서 **툴 호출째 실패** | 값이 없는 구간은 시리즈에 `NaN` 으로 남는데 JSON 에 `NaN` 을 쓸 수 없다. 최신값 경로만 `null` 로 걸러내고 히스토리 경로는 그대로 내보냈다 | 히스토리도 `null` 로 내보낸다. 부분 결손 구간도 NaN 만 `null` 이고 실측값은 남는다 |
| 모든 GPU 어댑터가 영영 `stale:true` | `IsStale` 이 **한 번도 측정된 적 없는** 슬롯까지 셌다. 내장 GPU 는 온도 센서가 0개라 그 슬롯이 영구히 비어 있다 | 실측 이력이 없는 슬롯은 세지 않는다. 없는 값은 `null` 로 이미 말하고 있다 |
| `devicesRevision` 이 **30초마다** 증가 | §5.7 검증 재열거가 같은 장치를 다시 등록할 때 `Register` 가 무조건 리비전을 올렸다. §10.3 의 "이 값만 비교하면 구성이 그대로인지 알 수 있다"가 거짓이 된다 | 내용이 실제로 달라졌을 때만 올린다. `DeviceInfo` 가 `record` 라도 `Extra` 사전은 참조 비교라 내용 비교가 따로 필요하다 |

> **UI 쪽으로도 새어 나갔다.** `SampleEngine.DevicesChanged` 는 리비전이 바뀔 때 발생하므로
> `SyncCards` 가 **30초마다** 돌고 있었다. 카드 diff 가 있어서 실제로 다시 지어지지는 않았고
> `RefreshIdentity` 만 반복됐다 — 그래서 아무도 눈치채지 못했다. "장치가 바뀌었을 때만"이라는
> 이벤트 계약은 그동안 지켜지지 않았다.

**내장 Arc 130V 의 IGCL 관측** — 값이 나오는 것과 믿을 수 있는 것은 다르다.

| 항목 | 관측 | 판단 |
|---|---|---|
| 온도 | 표본 60개 중 **0개** — 항상 `null` | Arc dGPU 는 `ctlPowerTelemetryGet` 으로 온도를 주지만 이 내장은 주지 않는다. `bSupported=false` 인지 범위 밖 값인지는 아직 가르지 않았다. 어느 쪽이든 `null` 이 맞다 |
| 전력 | 0.3~2.2 W | 유휴 iGPU 로 타당한 범위. 단 §5.4 의 "패키지 공유" 표기 여부는 아직 확인하지 않았다 |
| 클럭 | **1850 MHz 에서 한 번도 변하지 않음**(표본 15개, 표준편차 0) | 1850 은 Arc 130V 의 **최대** 그래픽 주파수다. 0.5 W 에서 최대 클럭일 수 없으므로 현재 클럭이 아니라 최대/요청 클럭을 받고 있을 가능성이 높다. GPU 부하를 걸고 Level Zero(`zesFrequencyGetState`) 와 대조해 가려야 한다 — **미해결** |

**§12 목표 대비** — 이 기기는 두 항목을 넘긴다. 데스크톱보다 코어가 적고 느리다.

| 지표 | 목표 | 데스크톱 | 이 노트북 |
|---|---|---|---|
| 샘플링 듀티 사이클 | 평균 < 1% | 0.97% | **순간 1.47% / 적분 0.632%** — 아래 병기 |
| 워킹셋 | 시작 < 230MB | 209MB (게시 빌드) | **420MB** (배포본) · **295MB** (Release 빌드) |

**듀티 사이클 — 두 측정을 병기한다.** 하나로 줄이면 어느 쪽이든 거짓이 된다.

| 측정 | 값 | 조건 |
|---|---:|---|
| 순간 관측 | **1.47%** | 하네스에서 짧은 구간. 코어별 미니 바 붙이기 전 1.29%, `--no-vendor` 로 IGCL 을 떼도 1.20% |
| 장시간 적분 | **0.632%** | 게시본 4시간 19분, `TotalProcessorTime` 증분 ÷ 경과 ÷ 8코어. 구간 중앙 0.524%, p95 1.093%, 최대 1.175% |

> 두 값이 두 배 넘게 벌어지는 것 자체가 관측 대상이다. 적분값은 30분 버킷으로 갈라도
> 0.47~0.76% 로 평탄해 특정 구간만 싼 것이 아니다. 순간 관측이 **틱이 몰리는 순간**을 잡았거나
> 하네스의 측정 구간이 대표성이 없다는 뜻이다. §17 의 "목표를 코어 수에 묶을지"를 따지기 전에
> **어느 수를 목표와 견줄 것인지부터** 정해야 한다.

> 벤더 SDK 탓은 아니다. `--no-vendor` 로 IGCL 을 떼도 1.20% 다. 남는 것은 PDH 와일드카드
> 쿼리이고, 8코어 저전력 CPU 에서 그 비용이 그대로 드러난다.

**워킹셋 — 배포 포장이 100MB를 더 쓴다.** 같은 코드를 95초씩 띄워 잰 값이다.

| 빌드 | 워킹셋 | 프라이빗 | 핸들 |
|---|---:|---:|---:|
| 자체 포함 + 단일 파일 + 압축 (배포본) | 420.7 MB | 364.8 MB | 962 |
| 프레임워크 의존 + 일반 빌드 (Release) | 294.6 MB | 260.6 MB | 854 |
| 차이 | −126.1 | **−104.2** | −108 |

> 유력한 원인은 `EnableCompressionInSingleFile` 이다. 압축된 어셈블리는 디스크에서 매핑하지 못하고
> 풀어서 프라이빗 커밋에 올라간다. 다만 **이 비교는 변수가 둘 섞여 있다** — 배포본은 자체 포함이고
> 비교 대상은 프레임워크 의존이다. 104MB 중 압축 몫을 가르려면 압축만 뺀 자체 포함 단일 파일을
> 하나 더 만들어 3자 비교해야 한다.

> 압축을 빼도 프라이빗 260MB 는 여전히 230MB 목표 위다. 이쪽은 포장이 아니라 앱 자체의 몫이라
> 소크 테스트로는 잡히지 않는다 — **누수가 아니라 초기 커밋 수준**의 문제다.

> 위 표의 데스크톱 209MB 와 세로로 견주지 않는다. 그 값이 어떤 포장의 게시 빌드였는지
> 기록해 두지 않았다 — 포장만으로 100MB 가 갈리는 것을 방금 봤으니, 포장을 모르는 수와의
> 비교는 의미가 없다.

#### 배터리 절전 경로 — 검증 실패, 그리고 그 이유

**`Reduced` 분기는 여전히 한 번도 실행된 적이 없다.** 노트북을 구했는데도 그렇다.

| 층 | 확인 | 결과 |
|---|---|---|
| 구조체·호출 | `SYSTEM_POWER_STATUS` 크기, `GetSystemPowerStatus` 반환 | ✅ 12바이트, `ok=True`. 마샬링은 정상이다 |
| 전원 감지 | 어댑터 착탈에 따른 `AcLineStatus` | ✅ 0↔1 로 정확히 따라간다 |
| **절전 감지** | `SystemStatusFlag` 가 1 이 되는가 | ❌ **한 번도 1 이 되지 않았다** |
| 효과 | `SamplePace.Reduced` → 500ms | ✅ 단위 테스트로 묶었다(`Battery_saver_pace_halves_the_sample_rate`) |

**세 값이 서로 어긋난다.** 배터리 방전 중, 잔량 86~87%.

| 출처 | 값 |
|---|---|
| Windows 설정 UI (시스템 > 전원 및 배터리) | 절전 모드 **켬**, "항상 절전 모드 사용" **켬** |
| `GetSystemPowerStatus().SystemStatusFlag` (앱이 쓰는 경로) | **0** |
| WinRT `Windows.System.Power.PowerManager.EnergySaverStatus` | **Off** |

설정이 켜져 있다고 해서 Windows 가 절전을 **실제로 적용하고 있는지**는 알 수 없다.
그래서 절전의 부수 효과("절전 모드 사용 중에는 화면 밝기 줄이기")를 눈금으로 삼아,
토글을 껐다 켜면서 밝기와 두 API 를 3초 간격으로 같이 기록했다.

| 시각 | `AcLineStatus` | `SystemStatusFlag` | WinRT | 화면 밝기 | |
|---|---|---|---|---|---|
| 22:59:34 | 0 | 0 | `Off` | 49% | 절전 켬 (기준) |
| 23:00:17 | 0 | 0 | `Off` | **70%** | ← "항상 절전 모드 사용" **끔** |
| 23:00:26 | 0 | 0 | `Off` | **49%** | ← 다시 **켬** |

밝기가 토글과 맞물려 49↔70 으로 움직인다. **Windows 는 절전을 실제로 적용하고 있다.**
그 9초 동안 두 상태 API 는 미동도 없었다.

> **앱의 버그가 아니다.** 앱이 쓰는 문서화된 Win32 API 와, 구현이 다른 WinRT API 가 서로
> 일치하고 **둘 다** 실제 적용 상태와 어긋난다. 두 API 를 바꿔봐야 얻을 것이 없다.
> **이 구성에서 §6.3 의 배터리 분기는 도달 불가능하다** — 감지할 방법이 없어서다.

유력한 가설은 Windows 11 24H2 에서 갈라진 두 개념이다. 상태 API 가 보고하는 것은 잔량
임계값으로 **자동 발동**하는 옛 "배터리 절약 모드"이고, "항상 절전 모드 사용"은 그와 별개의
새 모드라서 그 플래그를 세우지 않는 것으로 보인다. 확인하지 않았으므로 가설이다.

남은 검증 경로는 셋이다. 어느 것도 아직 하지 않았다.

1. **임계값 자동 발동으로 시험한다** — "항상"을 끄고 배터리를 임계값 아래로 내리거나
   임계값을 올린다. 가설이 맞으면 그때는 플래그가 선다. **이 갈래가 가장 값싸고 결정적이다**
2. 표준 "균형 조정" 구성표로 바꿔 재시도한다 — 이 기기는 벤더 구성표 `SAMSUNG MODE`
   (`ab6534a3-…`)를 쓴다. `powercfg /q SCHEME_CURRENT de830923-…`(에너지 절약 하위 그룹)가
   빈 결과를 돌려주지만, 이 설정들은 기본 숨김이라 빈 결과만으로 "없다"고 단정할 수 없다
3. 다른 제조사 노트북에서 같은 확인을 한다

> 이 항목에서 가장 쉬운 자기기만은 "노트북에서 돌려봤다"고 적는 것이다. **돌려봤지만
> 조건이 만들어지지 않았다.** 조건을 만들지 못한 것과 조건에서 동작한 것은 다르고,
> 여기 적힌 값은 전부 전자다.

#### 누수 테스트 방법

```bash
python tools/soak.py --minutes 1440      # 24시간
```

> **`--minutes 240` 은 더 이상 최소선이 아니다.** 그 수는 12분 소크의 잔차 σ 0.77MB 에서 나온 것인데,
> 실제로 4시간 19분을 돌려 보니 σ 가 훨씬 컸다(아래). 필요한 관측 길이는 **약 26시간**이다.

> **스크립트에 결함이 하나 있다.** `analyse()` 가 **워킹셋**으로 판정하는데, 워킹셋은 OS 트림과
> 4분 주기 톱니에 지배되어 σ 가 19MB 다. 판정은 **프라이빗 바이트**로 해야 한다 — 같은 실행에서
> σ 5.38MB 로 네 배 조용하다. 아직 고치지 않았다.

스크립트가 마지막에 **판정 가능 여부**를 먼저 찍는다. 관측이 짧으면 기울기 대신
"판정 불가"와 필요한 시간을 알려준다 — 짧게 돌리고 "누수 없음"이라고 적는 것이
이 항목에서 가장 흔한 자기기만이기 때문이다.

#### 12분 소크 결과 (게시 빌드, 창 표시 상태)

| 구간 | 값 |
|---|---|
| 워밍업 0~2분 | 188.1 → 221.0 MB |
| 정상 2~12분 | 220.6 ~ 223.5 MB (표본 21개) |
| 기울기 | +0.054 MB/분 (잔차 σ = 0.77 MB) |
| 핸들 | 869~896, 마지막 871 — 단조 증가 없음 |
| 스레드 | 18~24 — 단조 증가 없음 |

**이 테스트로 말할 수 있는 것은 "눈에 띄는 누수는 없다"까지다.** 기울기를 24시간으로 늘리면
+77MB 가 되지만 이는 노이즈 안이다 — 잔차 σ 0.77MB, 관측 10.1분이면 **구분 가능한 최소 추세가
약 219MB/24h** 다. 즉 이 창으로는 10MB/24h 목표를 판정할 수 없다.
같은 잡음 수준에서 10MB/24h 를 가리려면 **약 4시간**의 연속 관측이 필요하다.

> **이 4시간 추정은 틀렸다.** 실제로 4시간 19분을 돌려 보니 잔차 σ 가 0.77MB 가 아니라
> 5.38MB(프라이빗)·19.03MB(워킹셋)였다. 12분 창에는 4분 주기 톱니가 두세 번밖에 들어가지
> 않아 잡음이 국소 기울기로 흡수됐던 것이다. 아래 절이 정정본이다.

> 짧은 소크를 돌리고 "누수 없음"이라고 적는 것이 이 항목에서 가장 흔한 자기기만이다.
> 판정할 수 없는 것을 판정했다고 쓰면 나중에 아무도 다시 재지 않는다.

#### 4시간 19분 소크 결과 (배포본, 창 표시 상태)

2026-09-24 02:18:59 ~ 06:38:07, 60초 간격 260표본. 윈도우 업데이트 재부팅(06:43)으로 끊겼다.
AC 연결 상태였고 절전 진입 흔적은 없다(샘플 간격 중앙값 60초, 90초 초과 0건).
워밍업 2분을 뺀 뒤 `tools/soak.py` 와 같은 식으로 계산했다.

| 항목 | 범위 | 기울기/24h | 잔차 σ | 구분 가능 | 판정 |
|---|---|---:|---:|---:|---|
| 워킹셋 | 315.6 ~ 431.9 MB | −536.6 MB | 19.03 MB | ≥213 MB/24h | **판정 불가** (91시간 필요) |
| 프라이빗 | 356.9 ~ 373.9 MB | −21.4 MB | 5.38 MB | ≥60 MB/24h | **판정 불가** (26시간 필요) |
| 핸들 | 928 ~ 954 | −5.1 | 4.50 | ≥50 | 추세 없음 |
| 스레드 | 28 ~ 33 | −2.4 | 0.67 | ≥7.5 | 추세 없음 |
| **GDI 객체** | 47 ~ 48 | **±0.0** | 0.06 | ≥0.7 | ✅ **누수 없음** |
| USER 객체 | 57 ~ 59 | −1.3 | 0.62 | ≥7.0 | 추세 없음 |

**판정할 수 있는 것은 GDI 하나다.** 정수 카운트라 잡음이 사실상 없어서(σ 0.06) 구분 가능한
최소 추세가 0.7개/24h 다. 4시간 19분 동안 47개에서 단 1개도 늘지 않았다 — WPF 에서 가장 흔한
누수 경로가 막혀 있다는 것은 이 관측으로 말할 수 있다.

**메모리는 여전히 말할 수 없다.** 두 지표 다 기울기가 음수이고 단조 증가가 없지만,
잡음이 목표(10MB/24h)보다 훨씬 커서 "추세가 없어 보인다"와 "누수가 없다"를 가를 수 없다.
12분 소크에서 σ 0.77MB 를 근거로 "4시간이면 된다"고 적었던 것이 틀렸다.

> **왜 σ 가 24배로 커졌나** — 워킹셋이 **4분 주기로 12.8MB 씩 떨어지는 톱니**를 그린다(46회, 폭 중앙 −12.8MB).
> 12분 창에는 이 주기가 두세 번밖에 들어가지 않아 잡음이 아니라 국소 기울기로 흡수됐다.
> 관측을 늘리자 정체가 드러난 것이고, 이것이 **짧은 소크가 σ 를 과소평가하는 구체적인 기전**이다.
> 185분 지점의 −55.9MB 급락은 톱니가 아니라 OS 트림이다(같은 시각 프라이빗은 그대로다).

> 절대 수준도 12분 소크의 220MB 에서 370MB 로 올랐는데, 그중 약 104MB 는 배포 포장 탓이다(위 표).
> 나머지는 카드 구성과 코어별 미니 바가 늘어난 몫으로 보이나 갈라 재지 않았다.

> 목표를 실측에 맞춰 내리는 것은 기준을 포기하는 것과 다르다. **지킬 수 없는 숫자를
> 걸어두면 그 항목 전체가 무시된다.** 230MB 는 지금 구성에서 여유가 조금 있는 값이고,
> 여기서 올라가면 그때는 원인이 있는 것이다. 증가분 목표(10MB/24h)가 실제 감시 대상이다.

---

## 13. 에러 처리 / 폴백 매트릭스

| 상황 | 동작 |
|---|---|
| PDH 카운터 없음/손상 | 해당 지표 "사용 불가"(접힌 채 고정, 흐림). 60초마다 재시도 |
| NVML/ADLX/IGCL 로드 실패 | 해당 어댑터만 계층 A로 강등. `describe_capabilities`에 반영 |
| GPU 없음(열거 0개) | GPU 카드가 생기지 않고 남은 카드가 높이를 흡수 |
| **원격 데스크톱 접속 중** | **숨기지 않는다.** 물리 GPU 카드는 그대로 보인다 (아래) |
| 어댑터 일부만 실패 | 실패한 어댑터 카드만 사용 불가. 나머지는 정상 |
| **GPU 핫플러그 / TDR** | §5.7 규칙. LUID가 같으면 히스토리·통계 유지 |
| **Wi-Fi 라디오 off** | 인터페이스가 사라지면 카드 제거 + 60초 보관, Down만 되면 Unavailable |
| **디스크 착탈** | 시리얼이 같으면 히스토리 유지 |
| 프로세스 정보 접근 거부 | 이름/PID만, 나머지 `null`. **관리자 권한을 요구하지 않는다** |
| MCP 포트 사용 중 | ✅ 7667→7668… 최대 10회, 실제 포트를 토큰 파일에 기록. **포트 문제가 아닌 실패에서는 옮기지 않는다** — 권한·설정 탓이면 번호를 바꿔도 같은 이유로 실패한다 |
| MCP 호출 시 앱 미실행 | `app_not_running` 오류. 자동 실행하지 않음 |
| 샘플 예외 | 센서별 연속 3회 실패 시 비활성화, 60초 후 재시도. 앱은 크래시하지 않는다 |
| 절전/최대 절전 복귀 | 델타 기준선 재설정, 통계 유지, **끊긴 자리를 이음매로 표시**(아래) |
| 창이 너무 작음 | 자동 접힘. 그래도 부족하면 최소 1개는 펼친 상태 보장 |

#### 공백은 폭이 아니라 이음매다 ✅ 구현됨

절전·최대 절전 동안에는 샘플링 스레드가 돌지 않으므로 그 동안의 샘플이 **아예 없다.**
링 버퍼에는 잠들기 직전 샘플 바로 옆에 깨어난 직후 샘플이 붙는다. 차트가 둘을 선으로 이으면
**두 시간 동안 값이 그렇게 흘렀다고 말하는 셈**이다. 지울 구간이 있는 것이 아니라, 있지도 않은
연속성을 그려 온 것이다.

| 항목 | 결정 |
|---|---|
| 판정 | 인접 두 프레임의 시각 간격(§7.4)이 문턱을 넘으면 끊긴 것으로 본다 (`Core/Layout/SampleGaps`) |
| 문턱 | **공칭** Fast 주기 × 8 — 250ms 기준 2초 |
| 표시 | 그 자리에서 선과 채움을 끊고, 이음매에 **가는 세로선 두 줄**을 긋는다 |

> **문턱은 현재 배속이 아니라 공칭 주기로 잡는다.** 적응형 백오프(§6.3)로 느려진 것은 공백이
> 아니라 정상적인 감속이다. 가장 느린 배속이 ×4 이므로 ×8 이면 두 배 여유가 있고, 반대로
> 현재 배속에 문턱을 맞추면 느려진 상태에서 문턱까지 같이 느슨해져 **정작 절전 복귀를 놓친다.**

> **세로선을 두 줄로 긋는 이유** — 한 줄이면 리셋 마커(§7.3)와 구분되지 않는다. 하나는
> "여기서부터 다시 센다"이고 하나는 "여기서 끊겼다"라 뜻이 전혀 다르다.

> 시각이 거꾸로 가는 경우(시스템 시각 조정)도 끊는다. 이어 그리면 선이 되감긴다.

> 접힌 카드의 스파크라인에는 표시하지 않는다. 형태만 보는 18dip 띠라 이음매를 넣을 자리가 없다.

**렌더 테스트** — `--render-test <경로> --gap` 으로 중간에 2분짜리 절전을 두 번 끼운 화면을 낸다.
합성 데이터에도 250ms 간격의 시각을 주므로 공백 판정이 실제와 같은 조건에서 돈다.

#### 원격 데스크톱에서 GPU 카드를 숨기지 않는 이유

초안에는 "원격 데스크톱이면 GPU 카드를 전부 숨긴다"고 적혀 있었다. **잘못된 규칙이라 걷어낸다.**
이 앱은 개발 작업을 돕는 용도이고, 그 작업의 상당 부분이 원격 접속으로 이루어진다.
접속했다고 GPU가 노는 것이 아니다 — 빌드·학습·인코딩은 그대로 돌고 있고,
오히려 **화면 앞에 없을 때야말로 무엇이 돌고 있는지 알고 싶다.**

숨겨야 할 것은 GPU 가 아니라 **원격 세션이 만들어내는 가상 디스플레이 어댑터** 하나뿐이고,
그것은 §5.4 의 소프트웨어 어댑터 필터(`SoftwareDevice` 비트 또는 벤더 `0x1414`)가 이미 거른다.
물리 GPU 는 세션과 무관하게 PDH 인스턴스로 잡히므로 카드도 그대로 남는다.

> 물리 모니터가 없는 상태에서는 외장 GPU 가 저전력 대기로 내려갈 수 있다. 그때는 카드를
> 숨기는 대신 **`대기` 로 표시한다**(§6.3) — 값이 비는 이유가 고장이 아님을 말해야 하기 때문이다.

**실측 확인 (2026-09-26)** — 원격 데스크톱으로 접속한 상태에서 확인했다.

| 항목 | 결과 |
|---|---|
| 물리 GPU 열거 | ✅ 각 GPU 를 모두 찾아냈다. 세션 종류와 무관하게 PDH 인스턴스로 잡힌다는 전제가 맞았다 |
| 가상 디스플레이 어댑터 | ✅ 걸러져 카드가 생기지 않는다. §5.4 의 소프트웨어 어댑터 필터가 의도대로 동작한다 |

> 이 절은 설계 근거만 적혀 있었고 실측으로 뒷받침된 것은 이번이 처음이다. 초안의 규칙을
> 그대로 구현했다면 원격 접속 중에는 GPU 카드가 통째로 사라졌을 것이고, 정작 **화면 앞에
> 없을 때 무엇이 돌고 있는지** 가 안 보였을 것이다.

> **이 접속에서 확인되지 않은 것이 둘 남는다.** 물리 모니터가 없을 때 외장 GPU 가 `대기` 로
> 표시되는지(위 단락), 그리고 원격 세션의 해상도·배율에서 창 위치 복원(§11)이 어긋나지 않는지.
> 둘 다 이번 확인의 범위 밖이었다.

---

## 14. 개발 로드맵

| 마일스톤 | 범위 | 완료 기준 |
|---|---|---|
| **M0 — 골격** ✅ | 솔루션 구조(Core / Sensors / Harness / Tests) | 빌드·테스트 통과. WPF 창은 M2로 미룸 |
| **M1 — 코어 파이프라인** ✅ | `SampleEngine`, `MetricRegistry`, `MetricSeries`, `StatsAccumulator`, `PercentileTracker`, `LayoutEngine`, CPU·메모리 센서 | 하네스에서 실측값 출력, 단위 테스트 47개 통과 |
| **M2 — 렌더러 + 레이아웃** ✅ | `ChartSurface`(영역·미러·스파크라인), WPF 커스텀 크롬, `CardView`, 테마 서비스, 접기 UX, 라벨 페이드, 오프스크린 PNG 렌더 테스트 | 다크·라이트 렌더 확인. 테스트 74개 통과 |
| **M3 — 장치별 센서** ✅ | 인터페이스별 `NetworkProvider`(NDIS 필터·터널 제외), `DiskProvider`(SSD/HDD·버스 판정), `GpuProvider`(PDH 기준 열거 + D3DKMT 메타데이터 + NPU), GPU 조합 차트, NPU 단일 차트 | 실기기(RTX 5080 + Arc B580 + AI Boost NPU + SSD·HDD + NIC 2개)에서 전 장치 인식, 듀티 사이클 0.97% |
| **M4 — 아이콘 · 오버레이 · 라벨** ✅ | 제조사/매체/연결/NPU 글리프, 브랜드 색, `ScrubState` 전 카드 동기화, 온도·전력·클럭 오버레이, 라벨 페이드 + hover 장치명, **코어별 사용률 미니 바** | 렌더 테스트에서 전 카드 스크럽선·요약 칩·전체 패널 확인 |
| **M5 — DeviceWatcher** ✅ | 이벤트 구독, 디바운스, 30초 검증 재열거, 전원 게이팅, UI diff, 60초 보관·재연결 | 렌더 테스트로 연결→분리→재연결 4단계 확인, 통계·슬롯 연속성 유지 |
| **M6 — MCP** ✅ | 인프로세스 서버 + stdio 브리지 + 툴 10종, `devicesRevision`, 앱 미실행 오류 | Claude Code에서 연결·조회, 앱 종료·재실행 시 재연결 확인 |
| **M7 — 안정화** 🔶 | ~~적응형 백오프~~ · ~~게시(ReadyToRun)~~ · ~~워킹셋 목표 실측 교정~~ · ~~두 번째 기기 검증(내장 GPU·다중 배율)~~(완료) · ADLX 보류 · **배터리 절전 경로** · **24h 누수 테스트**(4h19m 까지, GDI 만 판정) · **듀티 사이클 대표값 확정** · **배포본 커밋 메모리 104MB** — 항목별 목록은 **§15** | §12 목표 전부 충족 |
| **M8 — 스냅샷 창** ✅ | ~~§7.4 샘플 시각 링~~ · ~~복제~~ · ~~구간 드래그·크롭~~ · ~~가로 확대·스크롤~~ · ~~요약 띠~~ · ~~CSV 내보내기~~ | 구간 통계가 실측 표본만 세는 것과 CSV 가 BOM 을 달고 나오는 것을 단위 테스트로, 화면은 `--render-test --snapshot` 으로 확인 |

---

## 15. 다음 작업

M7 안에서 처리하기로 **이미 정해진** 항목들이다. §17 「구현 중 재검토할 지점」은
실측해야 정할 수 있는 *질문*을 모은 표이고, 여기는 답이 나와 있는 *작업*이다. 섞지 않는다.

> 이 절은 2026-09-26 에 만들었다. 그 전까지 이 항목들은 커밋 메시지(`3eec91f`) 말미와
> 대화에만 있었다. **커밋 메시지는 검색되지 않고 대화는 사라진다** — 정해진 일을 적어 둘
> 자리가 없었다. 설계서에 없는 결정은 다음 사람에게 존재하지 않는 결정이다.

### 15.1 구현 — 문서에 설계만 있고 코드가 없다

**지금은 비어 있다.** 설계서에 쓰여 있는데 코드가 없는 항목은 남지 않았다.

> 이 절을 비워 두는 이유는, 설계만 있고 코드가 없는 상태가 **가장 나쁘기** 때문이다.
> 읽는 사람은 문서를 믿고 동작한다고 생각한다. 새로 설계한 것이 바로 구현되지 않는다면
> 여기 적어 두고, 구현하거나 문서에서 지우거나 둘 중 하나로 끝낸다.

### 15.2 제거 — 문서에 남았지만 하지 않기로 한 것

**지금은 비어 있다.** 하지 않기로 한 것이 문서에 남아 있는 상태가 없다.

> 걷어낸 것들 — 「장치 이름을 보는 3단계」 표(§9.3), 인터페이스 무트래픽 5분 숨김(§5.3·§13과
> 죽은 코드), Wi-Fi 밴드·SSID·신호 세기(§5.3), 아이콘 더블클릭 solo 와 카드별 리셋·⟲ 롱프레스(§9.4·§7.3),
> UX 문서의 접힘 프리셋 절. 마지막 것은 절만 지운 것이 아니라 **라이브 목업의 버튼과 그 코드까지**
> 걷어냈다 — 동작하는 목업을 남겨 두면 읽는 사람은 그 기능이 있다고 믿는다.

> 설계만 있고 코드가 없는 것(§15.1)과 반대 방향의 빚이다. 이쪽은 **코드는 없는데 문서가 있다고**
> 말하는 상태다. 둘 다 읽는 사람을 속이므로 같은 무게로 다룬다.

### 15.3 검증 — 조건을 만들어야 하는 것

| 항목 | 위치 | 필요한 조건 |
|---|---|---|
| 24시간 소크 재실행 | §12 | 실측 잔차로 계산하면 10MB/24h 를 가리는 데 **약 26시간**이 필요하다. 4h19m 에서 윈도우 업데이트 재부팅으로 끊겼으므로, 돌리기 전에 활성 시간을 확인한다 |
| 원격 데스크톱 잔여 2건 | §13 | 물리 모니터가 없을 때 외장 GPU 가 `대기` 로 표시되는지, 원격 세션의 해상도·배율에서 창 위치 복원(§11)이 어긋나지 않는지 |

그 밖의 검증 항목 — 배터리 절전 감지, IGCL 클럭(내장), 듀티 사이클 대표값, 소크 판정 지표,
배포본 커밋 메모리 104MB, iGPU 전력 표기 — 은 **§17 의 표**에 있다. 실측 결과에 따라 결론이
갈리는 것들이라 이 절로 옮기지 않는다.

### 15.4 요청 — 실사용 피드백에서 나온 기능

2026-09-27 음성 인식 서비스(`RSttStreamerOV`) 부하 분석에 MCP 를 붙여 본 평가 두 번에서 나왔다.
엔진 계열 시계열·포화 비율은 1.35, 전력 한도·스로틀은 1.37, PCIe·구간 마커·프로세스 시계열은 1.38 에서
반영했고, 남은 것이 이것이다.
**아직 설계하지 않았다** — 설계가 정해지면 §15.1 로 올리거나 여기서 지운다.

| 요청 | 분석 가치 | 메모 |
|---|---|---|
| 구간 히스토그램 | 중간 | 포화 비율은 1.35 에서 넣었다. 분포 모양 전체가 필요하면 스케치(§7.3)에서 뽑을 수 있다 |
| 프로세스 트리·이름 단위 합산 | 낮음 | `dotnet` → `RSttStreamerOV.exe` 같은 구조. 부모 PID 는 이미 수집한다 |
| 프로세스 표 CSV 내보내기 | 낮음 | |

---

## 16. 테스트 전략
- **단위**: 링버퍼 경계, `StatsAccumulator` 정확도(Welford vs 나이브), 데시메이션 극값 보존, 카운터 랩어라운드, **`LayoutEngine`**(경계값, 카드 1개, 가중치 합 0, 창 높이 변화 중 재계산)
- **`DeviceWatcher`**: 가짜 이벤트 소스로 추가/제거/Up-Down/재연결 시나리오. **60초 보관 후 재연결 시 통계가 이어지는지**가 핵심 케이스
- **센서 통합**: 실기기 전용(`[Trait("Category","Hardware")]`, CI 제외). 어댑터 열거와 PDH 인스턴스 조인 정확도, SSD/HDD 판정, 인터페이스 종류 판정
- **렌더**: `RenderTargetBitmap` 골든 이미지 비교(테마·접힘·스크럽 상태별)
- **MCP**: 인메모리 전송으로 스키마·계약 테스트. 앱 미실행 경로의 오류 형식도 계약 대상. `devicesRevision` 증가 확인
- **장시간**: 24시간 + 장치 착탈 반복 후 워킹셋·핸들·GDI 증가 확인

---

## 17. 결정된 사항 (구 열린 질문)

2026-09-23 전부 정리됨. 설계 확정, 구현 착수.

| # | 질문 | 결정 |
|---|---|---|
| 1 | 제조사 글리프의 16px 식별 정확도 | **확정 — 현행 유지.** 이니셜 배지 폴백안은 폐기 |
| 2 | 워크로드에 따른 레이아웃 자동 전환 | **기각.** 언제 전환할지에 대한 명확한 정책을 세우기 어렵고, 예측 불가능한 레이아웃 변화는 이 앱의 가치(곁눈질로 같은 자리를 읽는 것)를 해친다. 수동 프리셋조차 쓰이지 않아 걷어냈다(§8.6) |
| 3 | 장치 12개 초과 워크스테이션 | **보류.** 접기와 자동 접힘으로 당장은 충분. 실제로 문제가 되면 그때 요약 카드 모드를 설계한다 |
| 4 | VPN 이중 계상의 시각적 표현 | **보류.** 터널은 기본 제외(§5.3)로 문제가 사라졌다. 터널 정보가 필요한 상황이 실제로 생기면 그때 요구사항을 다시 잡는다 |

### 구현에서 확정된 사항

| 항목 | 결정 | 계기 |
|---|---|---|
| 첫 실행 창 높이 하한 | 작업 영역 비례(60%)가 아니라 **최소 창 높이 420 고정**. 상한만 85% 비례 | 4K 모니터에서 카드 3개짜리 구성이 1296px 창을 받는 문제를 단위 테스트가 잡아냄 |
| `InvariantGlobalization` | **끈다**. WPF 텍스트 레이아웃이 실제 컬처를 요구한다 | 렌더 테스트가 `CultureNotFoundException` 으로 죽음 |
| PDH 카운터 추가 | `PdhAddEnglishCounterW` 로 충분. 카운터 인덱스 역조회 불필요 | 한국어 Windows 에서 영문 경로가 그대로 통하는 것을 실측 |
| 네트워크 vs 디스크 단위 | `MetricUnit.BitRate`(Mbps) 와 `ByteRate`(MB/s) 를 분리 | 같은 바이트/초라도 관례가 다르다 |
| 차트 도형 재사용 | 한 프레임 안에서는 **역할별로 다른 `StreamGeometry`** | WPF 리테인드 모드에서 `DrawGeometry` 는 도형을 복사하지 않고 참조만 잡는다 |
| 영역 그라데이션 매핑 | 도형 경계가 아니라 **컨트롤 높이에 고정**(`MappingMode.Absolute`) | 값이 낮을 때 그라데이션이 눌려 색이 진해 보임 |
| 가속기 열거 순서 | DXGI/D3DKMT 가 아니라 **PDH 인스턴스가 원천** | D3DKMT 가 NPU 를 열거하지 않는다(§5.4) |
| 네트워크 인터페이스 필터 | `FilterInterface`·`HardwareInterface` 비트로 NDIS 필터 계층 제외 | Realtek 2.5GbE 하나가 WFP·QoS·802.3 세 벌로 잡혔다 |
| PDH 비율 카운터 열거 | 인스턴스 열거를 **첫 유효 샘플까지 지연** | 수집이 두 번 쌓이기 전에는 인스턴스 배열조차 돌려주지 않는다 |
| 프로바이더 단위 | 장치 하나당 하나가 아니라 **종류 하나당 하나**(N개 장치 등록) | `GetIfTable2`·PDH 와일드카드가 호출 한 번에 전 인스턴스를 준다 |
| 통계 채널 | UI·MCP **스코프 분리**. 시계열은 공유, 리셋 시점만 분리 | 한쪽 리셋이 상대의 측정 구간을 지우면 안 된다 |
| 벤더 장치 매칭 | LUID가 아니라 **PCI 주소**(`KMTQAITYPE_ADAPTERADDRESS`) | 벤더 SDK는 LUID를 모른다. 공통 좌표가 PCI 주소뿐이다 |
| 벤더 경로 바인딩 | 첫 성공에서 멈추지 않고 **붙는 대로 전부** | 한 SDK가 모든 값을 내주지 않는다 — Arc는 Level Zero가 클럭, IGCL이 온도·전력이다 |
| 온도·전력·클럭 주기 | Fast 틱이 아니라 **4틱에 한 번** | 물리적으로 초 단위로 움직이는데 호출 비용은 사용률과 같다. 매 틱 읽으면 듀티 사이클 1.27%, 나누면 0.96% |
| 듀티 사이클 측정 | 마지막 한 틱이 아니라 **구간 평균**(`MeanSampleDuration`) | 단일 표본은 틱마다 몇 배씩 흔들려 §12 판단에 쓸 수 없다 |
| 차트 히트 테스트 | `OnRender` 첫 줄에 **투명 사각형**을 깐다 | `FrameworkElement`는 그린 픽셀 위에서만 히트된다 — 빈 구간에서 스크럽이 죽는다 |
| 오버레이 위치 계산 | 직전 렌더의 좌표가 아니라 **인덱스에서 직접 계산**(`XForIndex`) | 렌더 결과에 의존하면 한 프레임 늦어 칩이 스크럽선을 따라가지 못한다 |
| 장치명 라벨 | 페이드가 아니라 **상시 표시** | 같은 아이콘의 카드가 여럿(GPU 4장·디스크 2장·NIC 2장)이면 아이콘만으로는 어느 줄이 무엇인지 알 수 없다. "텍스트 최소화"는 읽을 것을 줄이자는 뜻이지 **무엇을 보고 있는지 감추자는 뜻이 아니다** |
| 카드 갱신 | 전체 재생성이 아니라 **diff** | 다시 지으면 스크럽 고정·접힘 상태가 날아가고 모든 카드가 껌뻑인다. Wi-Fi 하나 켰다고 화면 전체가 끊기면 안 된다 |
| 토큰 파일 쓰기 | **포트를 실제로 잡은 뒤에** 쓴다 | 먼저 쓰면 두 번째 인스턴스가 포트 충돌로 실패하면서 정상 동작 중인 첫 인스턴스의 토큰을 지운다. 앱은 계속 서비스하는데 브리지는 찾지 못하는 상태가 된다 — 실제로 재현했다 |
| 크롬 높이 | 상수가 아니라 **안정된 순간에 측정한 값을 캐시** | 76 으로 박아뒀는데 실제로는 105 였다. 게다가 창 높이 애니메이션 중에는 `ActualHeight` 와 `CardHost.ActualHeight` 의 갱신 시점이 어긋나 역산값이 77·86 으로 튄다 — 아무 때나 재면 안 된다 |
| `RequestEnumerate` | **표시만 남기고** 실제 작업은 샘플링 스레드에서 | 이벤트 스레드에서 벤더 핸들을 닫으면 샘플링 스레드가 그 핸들로 읽는 중에 닫혀 프로세스가 죽는다 |
| 메시지 창 수명 | 생성·메시지 루프·소멸을 **같은 스레드**에서 | 창은 만든 스레드의 소유물이다. 다른 스레드의 `DestroyWindow` 는 실패하고 핸들이 대롱거린다 |
| 장치 알림 등록 | 브로드캐스트가 아니라 **인터페이스 클래스 등록** | 메시지 전용 창은 `DBT_DEVNODES_CHANGED` 같은 브로드캐스트를 받지 못한다 |
| 전원 상태 조회 | `CM_Get_DevNode_Registry_Property(CM_DRP_DEVICE_POWER_DATA)` | `CM_Get_DevNode_Property(DEVPKEY_Device_PowerData)` 는 이 시스템에서 값을 돌려주지 않았다 — 조용히 `Unknown` 만 나온다 |
| 창 불투명도 | `Window.Opacity` 가 아니라 **`AllowsTransparency="True"`** 를 켠 뒤의 `Opacity` | 끄고 쓰면 내용이 창 배경색 쪽으로 흐려질 뿐 뒤가 비치지 않는다. `WS_EX_LAYERED` 로 우회하려 해도 WPF `Window` 가 확장 스타일을 강제해 **오류 없이** 되돌린다 — `SetWindowLongPtr` 이 이전 값을 정상 반환하는데 되읽으면 그대로다. 대가인 소프트웨어 합성은 실측 CPU 0.575% 로 무시할 수준이었다 |
| 종료 정리 | async 정리는 **스레드 풀에서** 돌리고 기다린다 | `OnExit` 는 Dispatcher 스레드에서 도는데, 거기서 async 정리를 블로킹으로 기다리면 `await` 뒤의 이어받기가 그 Dispatcher 로 돌아가려 한다. 스레드는 막혀 있고 Dispatcher 는 내려가는 중이라 영영 실행되지 않는다 — **창은 닫혔는데 프로세스만 남는다.** 실행할 때마다 창 핸들 0 짜리 좀비가 쌓이고 다음 빌드가 파일 잠금으로 실패한다 |
| 그 정리에 `ConfigureAwait(false)` 만으로는 부족 | 라이브러리에도 달되 **호출부에서 `Task.Run` 으로 감싼다** | 우리 `await` 는 막을 수 있지만 ASP.NET Core 종료 경로 **안쪽**까지는 손이 닿지 않는다. 라이브러리만 고친 상태로 막힌 컨텍스트 위에서 돌려 보니 여전히 멎었다 — 회귀 테스트가 그 사실을 붙잡고 있다. 제한 시간(3초)도 함께 둔다: 모니터가 좀비로 남는 것보다 덜 정리되는 편이 낫다 |
| 네이티브 스크래치 버퍼 | **그 버퍼로 넘기는 가장 큰 구조체보다 크게**, 그리고 길이 인자를 가드한다 | `ctl_power_telemetry_t` 는 1024바이트인데 버퍼가 512였다. 드라이버가 버퍼 밖에 쓰고 힙이 조용히 망가져, 한참 뒤 전혀 다른 곳에서 죽었다 |

### 벤더 경로 (계층 B)

| 벤더 | 라이브러리 | 이 기기 존재 | 상태 |
|---|---|---|---|
| NVIDIA | `nvml.dll` 8.17 | O | ✅ 사용률·메모리·온도·전력·클럭 |
| Intel | `ControlLib.dll`(IGCL) 1.2 | O | ✅ 온도·전력·클럭 |
| Intel | `ze_loader.dll` 1.32 (Level Zero Sysman) | O | ✅ 클럭 (IGCL 없을 때의 대체 경로) |
| AMD | `amdadlx64.dll` | X | **보류** — 아래 참조 |

> **AMD(ADLX) 경로는 쓰지 않고 보류한다.** 이 기기에 AMD GPU 도 라이브러리도 없어
> 한 줄도 실행해 볼 수 없다. 이번 Intel 작업에서 검증 없이 쓴 네이티브 코드가 어떻게 되는지
> 이미 세 번 확인했다 — 구조체 오프셋 두 번, 버퍼 크기 한 번, 그중 하나는 힙을 조용히 뭉개며
> 엉뚱한 곳에서 프로세스를 죽였다. 돌려볼 수 없는 상태에서 ADLX 를 쓰면 같은 종류의 결함을
> **발견할 방법 없이** 심는 것이 된다. AMD GPU 를 확보한 뒤에 한다.
> 그때까지 AMD 어댑터는 계층 A(PDH)로 사용률·메모리까지 나온다.

**한 어댑터에 여러 경로를 겹쳐 붙인다.** 한 SDK가 모든 값을 내주지 않기 때문이다. `BindVendor`는
첫 성공에서 멈추지 않고 붙는 대로 전부 쌓고, `ReadVendor`가 바인딩 순서를 우선순위로 삼아 합친다.

#### Intel 경로에서 실제로 막혔던 것

| 증상 | 실제 원인 |
|---|---|
| Level Zero 온도 센서 0개, 에너지 카운터 `UNSUPPORTED_FEATURE`(0x78000003) | Windows Arc 드라이버는 Level Zero Sysman으로 **주파수만** 내준다. `ZES_ENABLE_SYSMAN=1`을 줘도 같다 |
| IGCL 센서 열거가 `ZE_LOADER`(0x40000019)로 실패 | `ctlInit`의 `flags`에 **`CTL_INIT_FLAG_USE_LEVEL_ZERO`가 필수**다. IGCL 텔레메트리가 내부적으로 Level Zero 위에 얹혀 있다 |
| 플래그를 준 뒤에도 온도만 계속 비어 있음 | `ctlEnumTemperatureSensors`가 **성공 코드와 함께 0개**를 돌려준다. 온도는 `ctlPowerTelemetryGet` 한 번으로 받아야 한다 — Intel 자사 오버레이가 쓰는 경로다 |

> **구조체 레이아웃을 선언하지 않고 버퍼 + 오프셋으로 읽는다.** Level Zero는 `stype`, IGCL은
> `Size`+`Version`으로 드라이버가 레이아웃을 고른다. 뒤쪽 필드까지 선언하면 헤더 버전에 따라
> 어긋나므로, 넉넉한 버퍼를 0으로 밀고 앞쪽 필드만 오프셋으로 읽는다.
> 그리고 **읽은 값은 물리적 범위로 검증한다** — 레이아웃이 어긋나면 쓰레기 값이 나오는데,
> 틀린 값을 차트에 흘리느니 값이 없는 편이 낫다.

실측(Arc B580): 유휴 46 °C · 19.0 W · 400 MHz → 부하 51~63 °C · 22~101 W · 400~2850 MHz.
부하는 로컬 OpenVINO TTS 서비스로 걸었다 — Intel 런타임이라 부하가 Arc 로 간다.

**전 주기 검증** (200초 단일 실행, Arc 가 대기 상태에서 시작):

| 시각 | 사건 |
|---|---|
| 0.2s | Arc 가 D3 — 벤더 경로를 열지 않고 미룬다. **장치를 깨우지 않았다** |
| 16.3s | 부하 인가로 Arc 가 깨어남 → 한 틱 안에 IGCL 개방·바인딩 |
| 16–116s | 온도 51~63 °C · 전력 22~101 W · 클럭 400~2850 MHz, 99 샘플 |
| 116.3s | `Active → Off` — 온도·전력·클럭 중단, 사용률(PDH)은 199 샘플까지 계속 |
| 종료 | IGCL 이 열린 채 Arc 는 D3 인 상태에서 `ctlClose` 정상 종료 |

### MCP 구현에서 확인된 것

| 항목 | 결과 |
|---|---|
| 툴 · 리소스 · 프롬프트 | 10 · 2 · 1, 브리지 경유로 전부 응답 확인 |
| 실측 응답 | GPU 4개(NVML 2 · PDH 2), 디스크 2, 네트워크 2, 프로세스 336개 |
| 분위수 | `PercentileTracker` 를 레지스트리에 연결. p95 가 리셋 구간 전체를 덮는다 |
| 샘플링 부하 | 분위수 추적 추가 후에도 듀티 사이클 0.79~0.81% |

> **값이 없는 항목은 `null` 로 내보낸다.** 0 으로 채우면 에이전트가 "GPU 가 놀고 있다"로 읽는데
> 사실은 "모른다"다. 계층 A(PDH)만 붙은 어댑터의 온도가 정확히 이 경우다.

### 구현 중 재검토할 지점
설계 단계에서 확정할 수 없고 실물에서만 판단 가능한 항목들이다. 해당 마일스톤에서 실측 후 결정한다.

| 항목 | 판단 시점 | 기준 |
|---|---|---|
| Fast 티어 250ms가 실제로 부하 목표를 지키는지 | M3 (센서 전부 붙은 뒤) | §12의 CPU < 1.0% |
| `GPU Engine(*)` 와일드카드 쿼리 실비용 | M3 | Slow 티어 1회 쿼리 < 5ms |
| 펼친 카드 최소 높이 118dip이 적정한지 | M4 | 실제 차트에서 형태 인지 가능 여부 |
| 장치 제거 후 통계 보관 60초 | M5 | Wi-Fi·eGPU 착탈 실측 지연 |
| **배터리 절전을 무엇으로 감지할지** | M7 | Windows 11 24H2+ 의 "항상 절전 모드 사용"은 효과가 적용되는데도 Win32·WinRT 상태 API 가 둘 다 꺼짐으로 답한다(§12 실측). 임계값 자동 발동에서는 서는지부터 확인한다 |
| **IGCL 클럭이 현재값인지 최대값인지** | M7 | 외장 B580 에서는 **현재(요청) 주파수**로 확인 — 부하에 따라 400~1950 MHz 로 움직이되, 렌더 블록이 절전일 때 마지막 요청값(최대 2850 MHz)이 남는다. 활동 0.5% 미만이면 비우는 것으로 처리했다(§5.4 Intel 주의점 7). 내장 Arc 130V 의 1850 MHz 고정(§12)이 같은 현상인지는 그 기기에서 부하를 걸어 확인해야 한다 |
| **듀티 사이클을 무엇으로 재는지** | M7 | 같은 기기에서 순간 관측 1.47%, 4.3시간 적분 0.632% — 두 배 넘게 벌어진다(§12). 목표와 견줄 대표값을 먼저 정해야 "1% 를 넘는지"를 물을 수 있다 |
| **듀티 사이클 목표를 코어 수에 묶을지** | M7 | 벤더 계층을 떼도 순간 관측이 1.20% 다(§12). 코어별 와일드카드가 코어 수에 비례해 늘어나므로 고정 1% 가 코어 수와 무관한 것이 맞는지부터 본다 |
| **소크 판정을 프라이빗으로 옮길지** | M7 | `tools/soak.py` 가 워킹셋으로 판정하는데 OS 트림과 4분 주기 톱니에 지배된다(σ 19MB). 프라이빗은 같은 실행에서 σ 5.4MB(§12) |
| **배포본 커밋 메모리 104MB** | M7 | 압축만 뺀 자체 포함 단일 파일로 3자 비교해 `EnableCompressionInSingleFile` 몫을 가른다(§12) |
| **iGPU 전력의 "패키지 공유" 표기** | M7 | §5.4 주의점 2. 내장 Arc 에서 `ctl_power_properties_t` 도메인 종류를 실제로 확인하지 않았다 |

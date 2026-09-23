# ChronoLoad — 설계서

> GPU 워크로드 중심의 실시간 시스템 모니터. WPF 세로형 위젯 + MCP 서버.

- **문서 버전**: 0.4
- **작성일**: 2026-09-23
- **대상 런타임**: .NET 10 (`net10.0-windows`), Windows 10 20H2 이상 / Windows 11
- **UX 시각 설계서**: [`docs/ux-design.html`](docs/ux-design.html) — 브라우저로 열면 라이브 목업이 동작합니다

### 개정 이력
| 버전 | 변경 |
|---|---|
| 0.1 | 최초 설계 — CPU·메모리·네트워크·GPU 4카드, 리셋 통계, 자체 차트 렌더러, MCP |
| 0.2 | 다중 GPU 필수, 디스크 카드 추가, 카드 접기 + 높이 자동 분배, 시간 동기화 오버레이(온도·전력), MCP는 앱 실행 중에만 |
| 0.3 | GPU 제조사 아이콘·브랜드 색(NVIDIA/AMD/Intel), 디스크 SSD/HDD 아이콘, 접힘 프리셋, 최초 실행 라벨 페이드, Intel 수집 경로 대폭 보강 |
| 0.4 | 네트워크를 인터페이스별 카드로 분리(이더넷/Wi-Fi, VPN·터널은 제외), 장치 변경 실시간 감지, 카드 hover 시 전체 장치명 표시 |
| 0.5 | UI·MCP 통계 채널 분리, NPU를 1급 장치로 추가, 가속기 열거를 PDH 인스턴스 기준으로 전환 |
| 0.6 | NVML 계층 B 연결(온도·전력·클럭), 시간 동기화 오버레이 구현 |
| 0.7 | Intel 계층 B 연결(IGCL + Level Zero), 벤더 경로 다중 바인딩, 느린 신호 티어 분리 |
| 0.8 | `DeviceWatcher` 구현(§5.7), 전원 상태 게이팅 — 잠든 장치를 깨우지 않는다 |
| 0.9 | 카드 diff 적용 — 바뀐 카드만 넣고 뺀다. 창 높이 계산을 측정 기반으로 교정 |
| 1.0 | MCP 서버 구현(§10) — 툴 10개 · 리소스 2개 · 프롬프트 1개, stdio 브리지, 프로세스 수집 |
| **1.1** | 적응형 백오프(§6.3), 게시(ReadyToRun), 워킹셋 목표 실측 교정, **장치명 라벨 상시 표시** |

---

## 1. 목표와 비목표

### 1.1 목표
ChronoLoad는 **GPU를 쓰는 애플리케이션(추론/학습/렌더링/게임)을 돌려놓고, 모니터 구석에 세워둔 채 곁눈질로 상태를 파악**하는 것을 목표로 한다.

1. CPU · 메모리 · **네트워크 인터페이스별** · **물리 디스크별** · **GPU 어댑터별** 부하를 하나의 화면에서 실시간 차트로 관찰
2. "리셋" 시점 기준의 **구간 통계**(평균/최대/최소)를 누적 — 벤치마크 구간 비교용
3. 숫자·텍스트는 최소화하고 **그래프 형태 인지**를 1차 채널로 사용
4. 화면 전환(탭/페이지) 없이 **스크롤 없는 단일 화면**. 장치가 늘어난 만큼의 세로 압박은 **접기·프리셋**으로 흡수
5. 한 지점을 가리키면 **모든 카드가 같은 시각을 보여주는** 동기화 오버레이로 인과관계를 추적
6. **장치 구성이 런타임에 바뀌어도**(Wi-Fi 켜기, eGPU 연결, 디스크 착탈) 재시작 없이 즉시 반영
7. 모니터링 도구 자신이 부하가 되지 않을 것 (CPU < 1%, 메모리 < 130MB 목표)
8. MCP 서버로 동일 데이터를 에이전트에 노출

### 1.2 비목표 (v1 범위 외)
- 원격 호스트 모니터링, 멀티 머신 대시보드
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
| R13 | 항목별 접기 | 28px 스트립 + 가중치 분배 + 자동 접힘 + 프리셋 | §8.6 |
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
6. **장치 집합은 런타임 변수다** — 개수를 컴파일 타임에 가정하지 않는다. 더 나아가 **실행 중에 바뀔 수 있다**(§5.7). 모든 자료구조·설정·프리셋이 이를 전제로 설계된다.

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
- **보조(Slow)**: 논리 코어별 사용률 — 오버레이 코어 미니 바
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
| 2. 무트래픽 숨김 | 누적 옥텟이 **5분간 전혀 변하지 않으면** 숨긴다. 트래픽이 생기면 다시 나타난다 — 이 한 줄이 가상 어댑터 대부분을 걸러낸다 |
| 3. 기본 경로 우선 | `GetBestRoute2`로 기본 게이트웨이를 가진 인터페이스를 찾아 **기본 펼침**. 나머지는 접힘 |
| 4. 사용자 결정 우선 | 설정의 인터페이스별 표시/숨김이 2번 규칙을 덮어쓴다 |

**Wi-Fi 상세** — `WlanOpenHandle` → `WlanQueryInterface(wlan_intf_opcode_current_connection)`으로 SSID, 신호 품질(%), PHY 속도, 채널·대역을 얻어 오버레이와 `FullName`에 채운다. `WlanRegisterNotification`으로 연결/해제/SSID 변경을 구독한다(§5.7).

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
| 큐 길이 | `PhysicalDisk(n)\Avg. Disk Queue Length` | Slow |
| 응답 시간 | `PhysicalDisk(n)\Avg. Disk sec/Transfer` × 1000 | Slow |

- **`% Idle Time`은 100을 넘거나 음수가 될 수 있다**(샘플 경계) → `Clamp(0,100)` 필수
- 표시: 네트워크와 동일한 미러 차트. 현재값은 읽기+쓰기 합계
- **포화 판정은 처리량이 아니라 활성 시간으로** — 작은 랜덤 I/O는 처리량이 낮아도 디스크를 포화시킨다. 활성/큐/응답시간은 오버레이와 경고 상태로
- **합산 카드는 만들지 않는다** — NVMe 7GB/s와 HDD 200MB/s를 같은 축에 올리면 HDD 포화가 평균에 묻힌다

### 5.6 프로세스 (MCP 전용, Lazy 티어) ✅ 구현됨
- `NtQuerySystemInformation(SystemProcessInformation)` 1회로 전 프로세스 CPU/메모리/스레드/핸들
- GPU는 `GPU Engine(pid_*)` / `GPU Process Memory(pid_*)` 파싱 후 PID 조인. **인스턴스명의 LUID로 어댑터별 분해**까지 제공
- 기본 2초, **MCP 요청이 없으면 수집하지 않는다**(마지막 요청 후 60초 뒤 중단)

### 5.7 `DeviceWatcher` — 장치 변경 실시간 반영 (R17) ✅ 구현됨

**감지 경로**

| 대상 | 이벤트 소스 | 비고 |
|---|---|---|
| GPU 어댑터 추가/제거 | `RegisterDeviceNotification(GUID_DISPLAY_DEVICE_ARRIVAL)` | **메시지 전용 창**에 등록. DXGI COM 없이 되고 콘솔에서도 검증된다. 실측: eGPU 연결이 13.3초 지점에서 `DisplayAdapter` 로 잡혔다 |
| GPU 드라이버 재시작(TDR) | `DXGI_ERROR_DEVICE_REMOVED` + PDH 인스턴스 소실 | 같은 LUID로 돌아오면 히스토리 유지 |
| 디스크 · 볼륨 | `WM_DEVICECHANGE` (`GUID_DEVINTERFACE_DISK`, `_VOLUME`) | WPF `HwndSource.AddHook`, `RegisterDeviceNotification` |
| 네트워크 인터페이스 추가/제거/Up/Down | `NotifyIpInterfaceChange` (IpHlpApi) | **Wi-Fi 라디오 on/off가 여기로 들어온다** |
| Wi-Fi 연결 상태 · SSID | `WlanRegisterNotification` | 오버레이·`FullName` 갱신 |
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
| **추가** ✅ | 프로바이더 생성, `MetricRegistry` 슬롯 추가, 카드 등장(320ms 페이드 인), **프리셋 규칙에 따라** 접힘/펼침 결정 |
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
| Fast | **250ms** | CPU 총합, 물리 메모리, 인터페이스별 B/s, 디스크 처리량·활성시간, GPU 사용률·메모리(네이티브 경로) | PDH/NVML 호출 비용 합계 < 1ms. 사람이 "즉각"으로 느끼는 하한 ~200ms |
| Slow | **1000ms** | GPU 엔진 PDH 와일드카드, 코어별 CPU, GPU 온도·전력·클럭, 디스크 큐·응답, 커밋 차지, Wi-Fi 신호 | 인스턴스 열거가 비싸거나 변화가 느린 항목 |
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
- O(1) 갱신, 고정 메모리. p95는 고정 버킷 히스토그램으로 근사
- **리셋 의미론**: 통계만 초기화하고 차트 히스토리는 유지, 리셋 시점에 수직 마커선. 전역 리셋은 **접힌 카드와 표시하지 않는 지표까지 전부** 초기화한다. 길게 누르면 히스토리까지 삭제

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

### 8.3 GPU 조합 차트 (R8)
- **좌축** 0–100% 사용률: 전체(실선) + Compute(점선)
- **우축** 메모리 바이트: **누적 영역** = 전용(솔리드 20%) + 공유(대각 해치 30%)
- 시각 위계: 메모리는 뒤·저채도, 사용률은 앞·고채도. 사용률 라인이 누적 영역 위를 지날 때 묻히지 않도록 **같은 경로를 Surface 색 4px로 먼저 긋는 헤일로**를 적용
- 색약 대응: 전용/공유를 색이 아니라 **채움 패턴**으로도 구분

**외장/내장 분기**

| | 외장 (전용 ≥ 1 GiB) | 내장 |
|---|---|---|
| 우축 상한 | `max(전용용량 × 1.15, (전용+공유) × 1.10)` | `max(공유한도 × 0.55, 현재사용 × 1.15)` |
| 전용 VRAM 용량선 | 표시 (수평 파선) | **표시하지 않음** |
| 스필오버 경고 | **있음** | **없음** |

> 내장 GPU는 공유 메모리를 쓰는 것이 정상이다. 외장 규칙을 적용하면 iGPU가 상시 경고 상태가 되어 경고의 의미가 사라진다.

> **왜 합계가 아니라 전용 용량이 축 기준인가** — 합계를 축 최대값으로 쓰면 VRAM이 꽉 찼는지가 그래프 높이로 드러나지 않는다. 용량선을 고정 기준으로 두면 "선에 닿았다 / 넘었다"라는 **이진 판단**이 형태만으로 가능해진다.

### 8.4 테마
- `Themes/Dark.xaml`, `Themes/Light.xaml`, 모든 색은 `DynamicResource`
- 기본 = 시스템 추종(`WM_SETTINGCHANGE` + `AppsUseLightTheme` 감시)
- 다크는 순수 검정 대신 `#0E1116`
- 토큰 전체는 UX 문서 §14

### 8.5 창 동작
- `WindowStyle=None` + `AllowsTransparency=False` + 커스텀 리사이즈 그립
- `Topmost` 토글, 위치·크기 영속화, `PerMonitorV2`
- **첫 실행 창 높이는 장치 구성에서 계산**: `76 + 16 + 갭 + Σ(펼침 × 130 × 가중치) + Σ(접힘 × 28)`.
  상한은 작업 영역의 85%, **하한은 최소 창 높이 420**이다. 하한을 작업 영역에 비례시키면
  4K 모니터에서 장치가 적은 노트북이 1296px 창을 받아 카드 3개가 400px씩 차지하는 레이아웃이 된다.
  GPU2+디스크2+네트워크2면 약 950, 카드 3개짜리 노트북이면 약 560

### 8.6 접기 · 높이 분배 · 프리셋 (R13)

```
available = 카드영역 높이 − 갭 − (접힌 카드 수 × 28)
각 펼친 카드 높이 = available × (가중치 ÷ 펼친 카드 가중치 합)
while (최소 펼친 높이 < 118 && 펼친 카드 > 1)
    우선순위 최저 카드를 자동 접기(AutoCollapsed=true) → 재계산
```

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

- 접힘 = 28px 스트립(아이콘 + 전체폭 스파크라인 + 현재값). **스파크라인은 자체 창 최대값 기준 상대 스케일** — 절대 축이면 값이 낮을 때 바닥에 붙어 형태가 사라진다
- 자동 접힘은 **점선 테두리**로 구분하고 공간이 생기면 자동 복원. 사용자가 직접 접은 카드는 복원 대상이 아니다
- 접힌 카드도 샘플링·통계·경고가 살아 있다
- solo: 아이콘 더블클릭 → 그 카드만 펼치기 / 복원

> **구현 함정** — 창 높이를 애니메이션으로 바꾸는 동안(프리셋 "최소" 전환 등) 중간 높이로 레이아웃을 계산하면 자동 접힘이 과하게 발동해 모든 카드가 접혀버린다. **높이 애니메이션 완료 후 다시 계산**해야 한다(웹 목업에서는 `ResizeObserver`, WPF에서는 `SizeChanged`).

**프리셋**

| 프리셋 | 펼침 | 단축키 |
|---|---|---|
| 기본 | CPU · 메모리 · 기본 네트워크 · 디스크0 · GPU0 | `Ctrl+1` |
| AI 워크로드 | GPU 전부 · 메모리 · 디스크0 | `Ctrl+2` |
| 렌더 / 게임 | GPU0 · CPU | `Ctrl+3` |
| 데이터 전송 | 네트워크 전부 · 디스크 전부 | `Ctrl+4` |
| 최소 | 없음 (전부 접힘, **창 높이도 축소**) | `Ctrl+5` |
| 사용자 정의 ×5 | 현재 배치 저장 | `Ctrl+6`~`9` |

프리셋은 **장치 인스턴스가 아니라 종류 규칙**으로 저장한다.
```json
{ "gpu:*": "expanded", "disk:system": "expanded", "disk:*": "collapsed",
  "net:*": "collapsed", "cpu": "collapsed" }
```
장치가 붙거나 빠져도 프리셋이 유효하다 — **§5.7의 핫플러그와 맞물리는 지점**이다. 새로 붙은 Wi-Fi는 `net:*` 규칙을 따라 자동으로 올바른 상태가 된다.

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
- **호버 카드**: 전체 패널 — **첫 줄은 언제나 전체 장치명**, 이어서 온도·전력·클럭·엔진별 분해 / SSID·신호 / 활성·큐·버스
- **나머지 카드**: 요약 칩 1~2줄
- **접힌 카드**: 헤더 현재값이 그 시점 값으로 바뀐다
- 스크럽 중 모든 헤더 값 앞에 **점 프리픽스**로 "지금 값 아님" 표시
- 클릭 고정, `←`/`→` 이동(`Shift`+10), `Esc` 해제
- 오버레이는 레이아웃을 밀지 않고 커서 반대편으로 자동 플립

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

---

## 9. UI/UX 설계 (요약)

> 전체 시각 설계서: **[`docs/ux-design.html`](docs/ux-design.html)** — 라이브 목업에서 접기·프리셋·동기화 오버레이·장치 핫플러그를 직접 조작할 수 있다

### 9.1 캔버스
- 기본 **340 × (장치 구성에서 계산)** — GPU2+디스크2+네트워크2면 약 950
- 최소 **300 × 420** (접기 덕분에 v0.1의 620에서 내려왔다)
- 최대 폭 420 (그 이상은 여백만 — 가로형으로 변형하지 않는다)

### 9.2 수직 스택 (탭 없음)
```
┌─────────────────────┐  32px  타이틀바 (핀 / 프리셋 / 테마 / 최소화 / 닫기)
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

**예외 3가지**
- 네트워크·디스크는 `▼` 대신 `↑`(반대 방향 최대) — 수신/읽기의 최소값은 거의 항상 0이라 정보가 없다
- GPU는 `▪`(현재 전용 VRAM, 스필오버 시 `+공유`) 추가
- **오버레이와 라벨은 예산 밖**

**장치 이름을 보는 3단계 (R18)**

| 계기 | 표시 | 내용 |
|---|---|---|
| 최초 실행 / 새 장치 연결 | 헤더 라벨 3초 후 300ms 페이드 | `ShortName` |
| **카드 hover 0.25초** | 헤더 라벨 페이드 인 | `ShortName` |
| hover 유지 (툴팁 지연) | 네이티브 툴팁 | `FullName` |
| 차트 hover | 오버레이 첫 줄 | `FullName` + 스크럽 시각 |
| `L` 키 유지 / 설정 "항상 표시" | 전 카드 라벨 | `ShortName` |

0.25초 지연을 두는 이유 — 카드 사이를 빠르게 지날 때 라벨이 연달아 깜빡이면 화면이 소란스러워진다. 접힌 카드에서는 hover 동안 스파크라인 대신 라벨이 보인다.

### 9.4 인터랙션
| 동작 | 결과 |
|---|---|
| 카드 헤더 클릭 | 접기/펼치기 |
| 아이콘 더블클릭 | solo / 복원 |
| `1`~`9` / `Ctrl+1`~`9` | 카드 토글 / 프리셋 전환 |
| 카드 hover | 장치 이름 + 개별 리셋 아이콘 |
| 차트 hover | 전 카드 동기화 스크럽 + 오버레이 |
| 차트 클릭 / `←``→` / `Esc` | 스크럽 고정 / 이동 / 해제 |
| 차트 더블클릭 | 시간 창 60s → 180s → 600s |
| 전역 ⟲ / `Ctrl+R` | 전 지표 통계 리셋(접힌 카드 포함) |
| ⟲ 길게 누르기 600ms | 통계 + 히스토리 전체 초기화 |
| 휠 | 창 불투명도 60~100% |

### 9.5 접근성
- 대비 WCAG AA 이상. 색상 단독 정보 전달 금지 — 솔리드/해치, 글리프 모양, 인덱스 배지, 순서 고정 병용
- **적록색약 사용자에게 NVIDIA 그린과 AMD 레드는 구분이 어렵다** → 글리프 모양이 1차 채널, 색은 보조. 색을 제거해도 정보 손실이 없어야 한다
- 아이콘으로 식별이 안 되는 사용자를 위한 탈출구: hover 라벨, `L` 유지, 설정 "라벨 항상 표시"
- 접힌 카드도 스크린 리더에서는 전체 값이 읽힌다
- **장치 추가·제거는 `LiveRegion`으로 고지** — 시각적으로는 카드 등장이 알림이지만 스크린 리더에는 보이지 않는다
- 접근성 "동작 줄이기" 시 모든 이징 제거. 단 **라벨 3초 표시 시간은 유지**(정보 전달이 목적이지 장식이 아니다)

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
- 읽기 전용 원칙. 상태 변경 툴은 `reset_stats` 하나뿐. **프로세스 종료·우선순위 변경 툴은 제공하지 않는다**

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
| `get_gpu_status` | `adapterIndex?` | 생략 시 전 어댑터. 모델명, **제조사**, 외장/내장, 사용률(엔진 그룹 분해), 전용/공유 메모리, 온도, 전력, 클럭, 드라이버, `vramExceeded`, **활성 센서 계층** |
| `get_disk_status` | `diskIndex?` | 읽기/쓰기 B/s, 활성 %, 큐, 응답 ms, **매체(SSD/HDD)**, 버스, 모델·용량 |
| `get_network_interfaces` | `includeTunnels?` | 인터페이스별 이름/**종류**/링크 속도/RX·TX B/s/누적. Wi-Fi는 SSID·신호·대역. **터널은 기본 제외**이며 포함 시 `countedTwiceOn` 필드로 하위 인터페이스를 명시 |
| `get_metric_history` | `metric`, `deviceKey?`, `windowSeconds`(≤900), `maxPoints`(≤500) | min-max 데시메이션 시계열 |
| `get_stats_since_reset` | `metric?`, `deviceKey?` | `{ resetAt, elapsedSeconds, sampleCount, avg, min, max, p95, stdDev }` |
| `reset_stats` | `confirm: true` | 리셋 후 **직전 구간 통계를 반환** |
| `list_processes` | `sortBy`(cpu\|memory\|gpu\|gpuMemory\|diskIo), `adapterIndex?`, `limit`(≤50), `nameFilter?` | PID, 이름, CPU%, 워킹셋, 어댑터별 GPU%·메모리, 디스크 I/O |
| `get_process_detail` | `pid` | 위 + 경로, 명령줄(권한 허용 시), 부모 PID, 어댑터별·엔진별 GPU 사용률 |
| `describe_capabilities` | — | 어댑터별 센서 계층(NVML/ADLX/IGCL/LevelZero/PDH), 디스크·인터페이스 목록, 샘플 주기, 버퍼 용량, **`devicesRevision`** |

### 10.2-1 브리지의 실패 설계

앱이 꺼져 있을 때 **`initialize` 와 각종 `*/list` 는 성공**시키고, 실제 툴 호출만
`app_not_running` 으로 돌려준다.

> `initialize` 에서 실패하면 클라이언트가 세션 자체를 포기해, 나중에 앱을 켜도
> 클라이언트를 다시 시작하기 전까지 붙지 않는다. 또 JSON-RPC **오류가 아니라 정상 결과에
> `isError`** 로 돌려준다 — 전송 오류로 보내면 클라이언트가 서버를 죽은 것으로 간주한다.

### 10.3 장치 변경과 MCP
- 모든 응답에 **`devicesRevision`**(장치 집합이 바뀔 때마다 증가하는 정수)을 포함한다. 에이전트는 이 값만 비교해 "내가 알던 구성이 그대로인가"를 판단할 수 있다
- 장치 배열의 원소는 항상 `index`와 **`key`(LUID/시리얼/인터페이스 GUID)** 를 포함한다. `index`는 순서가 바뀔 수 있으므로 **재조회 시에는 `key`를 쓴다**
- `get_metric_history` / `get_stats_since_reset`은 `deviceKey`를 받아 인덱스 변동의 영향을 받지 않는다

### 10.4 리소스 · 응답 지침
- 리소스 `chronoload://snapshot`, `chronoload://stats`
- 프롬프트 `analyze_gpu_workload` — 병목(연산 / VRAM / 디스크 I/O / 네트워크) 진단 템플릿
- 바이트는 `{"bytes": 8589934592, "text": "8.0 GiB"}` 형태로 둘 다
- 시각은 ISO-8601(로컬 오프셋). 모든 응답에 `sampledAt`, `stale` 포함
- 기본 요약형, `verbose: true`로 확장

---

## 11. 설정 및 영속성
- 경로: `%LOCALAPPDATA%\ChronoLoad\settings.json` (원자적 쓰기: temp → `File.Replace`)
- 항목: 테마, 창 위치·크기·Topmost·불투명도, 샘플 주기, 표시 창, **카드 순서·표시·접힘 상태**, **프리셋 목록과 현재 프리셋**, `labelsShown` 플래그, `showTunnelInterfaces`(기본 false), 인터페이스·디스크·어댑터별 표시 설정, MCP 활성화·포트, 자동 시작
- **장치별 설정은 인덱스가 아니라 `DeviceInfo.Key`로 저장** — 장치 순서가 바뀌어도 설정이 따라간다
- 프리셋은 §8.6의 **규칙 형식**으로 저장 — 장치 구성이 바뀌어도 유효
- `schemaVersion` 필드, 알 수 없는 키는 보존
- 설정 UI는 별도 팝오버 창 (메인 화면의 단일 화면 원칙을 깨지 않는다)

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

#### 이 개발 기기에서는 검증할 수 없는 항목

지금까지의 실측은 전부 **데스크톱 한 대**(단일 모니터 2560×1440, 배터리 없음,
외장 GPU 2장 + 외장 Arc + NPU)에서 나왔다. 아래는 그 환경에 없는 조건이라
**한 번도 실행된 적이 없는 경로**다. 노트북에서 확인한다.

| 항목 | 무엇을 보는가 | 왜 여기서는 못 했나 |
|---|---|---|
| **배터리 절전 → `Reduced`** | `describe_capabilities` 의 `sampling.pace` 가 `reduced`, `effectivePeriodMs` 가 500 | 배터리가 없어 `GetSystemPowerStatus` 의 절전 분기가 실행되지 않는다 (§6.3) |
| **내장 GPU 텔레메트리** | `telemetryLayers` 에 `IGCL` 이 뜨는지, 아니면 `PDH` 로 떨어지는지 | 이 기기의 Intel 은 **외장** Arc 뿐이다. 내장은 경로가 다를 수 있다 |
| **전용 VRAM 없는 어댑터** | GPU 콤보 차트와 `vramExceeded` 판정이 공유 메모리만 쓰는 어댑터에서 어떻게 보이는지 | 외장 GPU 는 모두 전용 메모리를 갖는다 |
| **모니터별 DPI 배율 차이** | 150% 노트북 ↔ 100% 외장 사이에서 창을 옮기고 재시작했을 때 크기·위치 | 단일 모니터라 배율이 하나뿐이다 (§11) |
| **누수 24h** | `tools/soak.py` 의 판정 줄 | 개발 중에는 기기를 계속 써야 해서 장시간 점유할 수 없었다 |

> **배터리 경로는 특히 주의해서 본다.** 코드가 있고 컴파일되지만 **단 한 번도 실행된 적이 없다.**
> 이런 코드는 "있으니까 될 것"이라고 넘기기 쉬운데, 이번 작업에서 검증 없이 쓴 네이티브 코드가
> 어떻게 되는지 세 번 확인했다(구조체 오프셋 두 번, 버퍼 크기 한 번).

#### 누수 테스트 방법

```bash
python tools/soak.py --minutes 1440      # 24시간
python tools/soak.py --minutes 240       # 4시간 — 10MB/24h 를 가릴 수 있는 최소선
```

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

> 짧은 소크를 돌리고 "누수 없음"이라고 적는 것이 이 항목에서 가장 흔한 자기기만이다.
> 판정할 수 없는 것을 판정했다고 쓰면 나중에 아무도 다시 재지 않는다.

> 목표를 실측에 맞춰 내리는 것은 기준을 포기하는 것과 다르다. **지킬 수 없는 숫자를
> 걸어두면 그 항목 전체가 무시된다.** 230MB 는 지금 구성에서 여유가 조금 있는 값이고,
> 여기서 올라가면 그때는 원인이 있는 것이다. 증가분 목표(10MB/24h)가 실제 감시 대상이다.

---

## 13. 에러 처리 / 폴백 매트릭스

| 상황 | 동작 |
|---|---|
| PDH 카운터 없음/손상 | 해당 지표 "사용 불가"(접힌 채 고정, 흐림). 60초마다 재시도 |
| NVML/ADLX/IGCL 로드 실패 | 해당 어댑터만 계층 A로 강등. `describe_capabilities`에 반영 |
| GPU 없음 / 원격 데스크톱 | GPU 카드 전부 숨김, 남은 카드가 높이 흡수 |
| 어댑터 일부만 실패 | 실패한 어댑터 카드만 사용 불가. 나머지는 정상 |
| **GPU 핫플러그 / TDR** | §5.7 규칙. LUID가 같으면 히스토리·통계 유지 |
| **Wi-Fi 라디오 off** | 인터페이스가 사라지면 카드 제거 + 60초 보관, Down만 되면 Unavailable |
| **디스크 착탈** | 시리얼이 같으면 히스토리 유지 |
| 인터페이스 무트래픽 5분 | 카드 숨김. 트래픽이 생기면 복귀 |
| 프로세스 정보 접근 거부 | 이름/PID만, 나머지 `null`. **관리자 권한을 요구하지 않는다** |
| MCP 포트 사용 중 | 7667→7668… 최대 10회, 실제 포트를 토큰 파일에 기록 |
| MCP 호출 시 앱 미실행 | `app_not_running` 오류. 자동 실행하지 않음 |
| 샘플 예외 | 센서별 연속 3회 실패 시 비활성화, 60초 후 재시도. 앱은 크래시하지 않는다 |
| 절전/최대 절전 복귀 | 델타 기준선 재설정, 통계 유지, 공백 구간을 gap으로 표시 |
| 창이 너무 작음 | 자동 접힘. 그래도 부족하면 최소 1개는 펼친 상태 보장 |

---

## 14. 개발 로드맵

| 마일스톤 | 범위 | 완료 기준 |
|---|---|---|
| **M0 — 골격** ✅ | 솔루션 구조(Core / Sensors / Harness / Tests) | 빌드·테스트 통과. WPF 창은 M2로 미룸 |
| **M1 — 코어 파이프라인** ✅ | `SampleEngine`, `MetricRegistry`, `MetricSeries`, `StatsAccumulator`, `PercentileTracker`, `LayoutEngine`, CPU·메모리 센서 | 하네스에서 실측값 출력, 단위 테스트 47개 통과 |
| **M2 — 렌더러 + 레이아웃** ✅ | `ChartSurface`(영역·미러·스파크라인), WPF 커스텀 크롬, `CardView`, 테마 서비스, 접기 UX, 프리셋, 라벨 페이드, 오프스크린 PNG 렌더 테스트 | 다크·라이트·기본·AI·최소 렌더 확인. 테스트 74개 통과 |
| **M3 — 장치별 센서** ✅ | 인터페이스별 `NetworkProvider`(NDIS 필터·터널 제외), `DiskProvider`(SSD/HDD·버스 판정), `GpuProvider`(PDH 기준 열거 + D3DKMT 메타데이터 + NPU), GPU 조합 차트, NPU 단일 차트 | 실기기(RTX 5080 + Arc B580 + AI Boost NPU + SSD·HDD + NIC 2개)에서 전 장치 인식, 듀티 사이클 0.97% |
| **M4 — 아이콘 · 오버레이 · 라벨** ✅ | 제조사/매체/연결/NPU 글리프, 브랜드 색, `ScrubState` 전 카드 동기화, 온도·전력·클럭 오버레이, 라벨 페이드 + hover 장치명 | 렌더 테스트에서 전 카드 스크럽선·요약 칩·전체 패널 확인 |
| **M5 — DeviceWatcher** ✅ | 이벤트 구독, 디바운스, 30초 검증 재열거, 전원 게이팅, UI diff, 60초 보관·재연결 | 렌더 테스트로 연결→분리→재연결 4단계 확인, 통계·슬롯 연속성 유지 |
| **M6 — MCP** ✅ | 인프로세스 서버 + stdio 브리지 + 툴 10종, `devicesRevision`, 앱 미실행 오류 | Claude Code에서 연결·조회, 앱 종료·재실행 시 재연결 확인 |
| **M7 — 안정화** 🔶 | ~~적응형 백오프~~ · ~~게시(ReadyToRun)~~ · ~~워킹셋 목표 실측 교정~~(완료) · ADLX 보류 · **24h 누수 테스트** | §12 목표 전부 충족 |

---

## 15. 테스트 전략
- **단위**: 링버퍼 경계, `StatsAccumulator` 정확도(Welford vs 나이브), 데시메이션 극값 보존, 카운터 랩어라운드, **`LayoutEngine`**(경계값, 카드 1개, 가중치 합 0, 창 높이 변화 중 재계산), **프리셋 규칙 → 장치 집합 적용**
- **`DeviceWatcher`**: 가짜 이벤트 소스로 추가/제거/Up-Down/재연결 시나리오. **60초 보관 후 재연결 시 통계가 이어지는지**가 핵심 케이스
- **센서 통합**: 실기기 전용(`[Trait("Category","Hardware")]`, CI 제외). 어댑터 열거와 PDH 인스턴스 조인 정확도, SSD/HDD 판정, 인터페이스 종류 판정
- **렌더**: `RenderTargetBitmap` 골든 이미지 비교(테마·접힘·스크럽 상태별)
- **MCP**: 인메모리 전송으로 스키마·계약 테스트. 앱 미실행 경로의 오류 형식도 계약 대상. `devicesRevision` 증가 확인
- **장시간**: 24시간 + 장치 착탈 반복 후 워킹셋·핸들·GDI 증가 확인

---

## 16. 결정된 사항 (구 열린 질문)

2026-09-23 전부 정리됨. 설계 확정, 구현 착수.

| # | 질문 | 결정 |
|---|---|---|
| 1 | 제조사 글리프의 16px 식별 정확도 | **확정 — 현행 유지.** 이니셜 배지 폴백안은 폐기 |
| 2 | 프리셋 워크로드 자동 전환 | **기각.** 언제 전환할지에 대한 명확한 정책을 세우기 어렵고, 예측 불가능한 레이아웃 변화는 이 앱의 가치(곁눈질로 같은 자리를 읽는 것)를 해친다 |
| 3 | 장치 12개 초과 워크스테이션 | **보류.** 접기·자동 접힘·프리셋으로 당장은 충분. 실제로 문제가 되면 그때 요약 카드 모드를 설계한다 |
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
| 무트래픽 인터페이스 숨김 5분이 적정한지 | M5 | 가상 어댑터가 실제로 걸러지는지 |
| 장치 제거 후 통계 보관 60초 | M5 | Wi-Fi·eGPU 착탈 실측 지연 |

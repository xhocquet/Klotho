# Godot NavMesh Visualizer — 사용 가이드

Godot 에디터에서 직렬화된 `FPNavMesh`(`.bytes`)를 3D 뷰포트에 시각화하고, 경로탐색·에이전트 시뮬레이션을 검증하는 에디터 도구.

> 대상: `com.xpturn.klotho` Godot 어댑터 · **Godot 4.x mono (.NET)** · 에디터 전용(`#if TOOLS`)
> 관련: [Navigation.md](Navigation.md) · NavMesh 익스포터(`Klotho: Export FPNavMesh`) · 구현 계획 [IMP55](IMP/IMP55/Plan-GodotNavMeshVisualizer.md)

---

## 1. 사전 조건

1. **Godot mono(.NET) 빌드** + 설치된 `dotnet` SDK.
2. 프로젝트에 **Klotho 애드온이 활성화**되어 있을 것 (`Project > Project Settings > Plugins`에서 Klotho 플러그인 Enable). 애드온 진입점은 [`plugin.gd`](../com.xpturn.klotho/Godot~/plugin.gd).
3. **C# 솔루션이 한 번 빌드**되어 있을 것 (`Project > Tools > C#: Build`, 또는 첫 실행 시 자동 빌드). 비주얼라이저는 C# `[Tool]` 클래스라 어셈블리가 빌드돼야 메뉴가 동작한다.
4. **편집 중인 3D 씬이 열려 있을 것** — 오버레이 지오메트리는 편집 씬 루트에 임시 노드로 부착된다. 씬이 없으면 도크·정보는 뜨지만 3D 지오메트리는 표시되지 않는다(경고 출력).
5. 입력 `.bytes` 파일 — NavMesh 익스포터(`Klotho: Export FPNavMesh`, `NavigationRegion3D` 선택 후 실행)가 생성한 `<scene_dir>/<RegionName>.NavMeshData.bytes`. 샘플: [`Samples/GodotP2pSample/NavigationRegion3D.NavMeshData.bytes`](../Samples/GodotP2pSample/NavigationRegion3D.NavMeshData.bytes).

---

## 2. 열기 / 닫기

- 상단 메뉴 **`Project > Tools > Klotho: NavMesh Visualizer`** 를 클릭하면 토글된다.
- 켜면 우측에 **`FPNavMesh`** 도크가 나타나고 3D 뷰포트 오버레이·입력이 활성화된다.
- 다시 클릭하면 도크와 오버레이가 제거되고 입력 가로채기가 멈춘다(에디터 기본 동작 복귀).

> 도구가 꺼져 있는 동안에는 3D 뷰포트 입력/드로잉에 전혀 개입하지 않는다.

---

## 3. NavMesh 로드 (`NavMesh Data` 섹션)

1. 텍스트 필드에 `.bytes`의 `res://` 경로 입력 (예: `res://NavigationRegion3D.NavMeshData.bytes`).
2. **`Load`** 클릭 → 파싱 후 지오메트리가 뷰포트에 표시되고, 정점/삼각형/그리드/Blocked/경계·내부 에지 카운트가 라벨로 표시된다.
3. **`Unload`** 로 비운다.

> 카운트가 익스포터 사이드카 `.json`(예: `NavigationRegion3D.NavMeshData.json`)과 일치하는지로 로드 정합성을 확인할 수 있다.

---

## 4. 시각화 레이어 (`Visualization Layers`)

체크박스로 on/off. 지오메트리 레이어는 즉시 다시 그려진다.

| 토글 | 표시 |
|---|---|
| Triangles | 삼각형 채움(파랑; Blocked는 빨강) |
| Edges / Boundary | 내부 에지 / 경계 에지 |
| Vertices | 정점 마커(라인 십자) |
| Tri Indices | 삼각형 인덱스 라벨(2D 오버레이) |
| Centers | 삼각형 중심점 |
| Blocked | Blocked 삼각형 강조 포함 여부 |
| Cost Heatmap | costMultiplier 그라디언트(녹→적) |

> 라벨(Tri Indices·Cell Labels·에이전트 `#i`)은 3D 위에 2D로 그려지며, **마우스를 3D 뷰포트에 한 번 올린 뒤** 카메라가 잡히면 표시된다. 라벨은 카메라에서 일정 거리(약 40m) 이내만 그린다.

---

## 5. 경로탐색 (`Pathfinding`)

1. **`Set Start`** / **`Set End`** / **`Inspect`** 중 하나를 눌러 모드 진입(다시 누르면 해제).
2. **3D 뷰포트에서 `Shift` + 좌클릭** → NavMesh 위 지점이 설정된다.
3. 시작·끝이 모두 설정되면 **자동으로 경로 탐색**되며, **`Find Path`** 로 수동 재탐색, **`Clear Path`** 로 초기화한다.
4. 결과(코리도 삼각형 수·웨이포인트 수)와 함께 **Corridor / Waypoints / Portals** 토글로 표시를 제어한다.
5. `Inspect` 모드에서 삼각형을 Shift+클릭하면 `Info` 섹션에 상세(정점 인덱스·이웃·areaMask·cost·blocked·area)가 뜬다.

---

## 6. 에이전트 시뮬레이션 (`Agent Simulation`)

결정론 `FPNavAgentSystem`을 에디터에서 직접 구동한다.

- **재생 제어**: `▶ Play`(토글로 일시정지) · `Step`(1틱) · `Reset` · 현재 `Tick` 표시.
- **`Sim Speed`** 슬라이더(0.25~4×). 고정 dt 1/60 누산기로 진행.
- **에이전트 기본값**: `Speed` · `Radius` · `Accel` 스핀박스, `Avoidance`(ORCA) 체크.
- **배치**: `Place Agent` 모드 + Shift+클릭으로 추가, `Set Dest` 모드 + Shift+클릭으로 선택 에이전트 목적지 지정.
- **좌표 직접 스폰**: `x, y, z` 두 줄(시작/목적지) 입력 후 `Spawn`(기존 에이전트 초기화 후 1기 생성). `Remove All`로 전체 제거.
- **표시 토글**: `Agents`(원형) · `Paths`(코리도+코너 라인) · `Velocity`(실제/희망 속도 화살표) · `ORCA`(회피 반평면 라인).

> 시뮬레이션이 도는 동안 동적 메시와 라벨이 매 틱 갱신된다. 도구가 비활성이면 틱이 멈춘다.

---

## 7. 공간 그리드 / 정보 (`Spatial Grid` · `Info`)

- **Grid Lines / Cell Labels** 토글. 마우스가 올라간 셀은 하이라이트되고, `(col, row) - 삼각형 수`가 표시된다.
- **Info**: 선택(Inspect) 또는 호버 중인 삼각형의 상세를 보여준다.

---

## 8. 제약 / 참고

- **PlayMode 런타임 에이전트 시각화는 미지원** — 에디터 시뮬레이션(로드한 `.bytes` 기반)만 제공한다.
- **좌표계**: 레이캐스트·쿼리는 시뮬레이션 좌표계 기준이다. NavMesh가 시뮬레이션 좌표계로 베이킹된 전제에서만 피킹이 정확하다.
- **라인 굵기**: 경계 에지 등은 색·알파로만 강조한다(렌더러상 라인 굵기 제어 불가).
- 오버레이 노드는 편집 씬에 직렬화되지 않는 임시 노드다(저장에 포함되지 않음).

---

## 9. 문제 해결

| 증상 | 확인 |
|---|---|
| 메뉴 `Klotho: NavMesh Visualizer`가 없음 | 애드온 활성화 + `C#: Build` 1회 실행했는지 |
| Load 후에도 3D에 아무것도 안 보임 | **3D 씬이 열려 있는지**(없으면 지오메트리 미부착), 경로가 올바른 `res://`인지, `.bytes`가 비어있지 않은지 |
| `No triangles` / 빈 데이터 | 익스포터에서 NavMesh를 **베이크 후** 익스포트했는지 |
| Shift+클릭이 안 먹음 | 도구가 켜져 있는지, 모드 버튼이 활성인지, 클릭 지점이 NavMesh 위인지 |
| 라벨이 안 보임 | 마우스를 3D 뷰포트에 한 번 올렸는지(카메라 캐시), 거리(~40m) 이내인지, Tri Indices/Cell Labels 토글 |

---

*도구 진입점: `Project > Tools > Klotho: NavMesh Visualizer` · 구현: [`com.xpturn.klotho/Godot~/Adapters/Editor/GodotFPNavMeshVisualizer*.cs`](../com.xpturn.klotho/Godot~/Adapters/Editor/)*

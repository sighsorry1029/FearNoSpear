# 테스트 매트릭스

## 1. 기본 재현 조건

- 산 바이옴 또는 높은 절벽 위에서 테스트한다.
- 아래쪽으로 긴 낙차나 경사가 있는 지점을 고른다.
- 같은 위치와 각도로 창을 반복해서 던진다.
- 투척 후 아이템 드롭 존재 여부와 BepInEx 로그를 확인한다.
- 멀티플레이 테스트에서는 던진 클라이언트, 주변 클라이언트, 데디케이트 서버 로그를 함께 확인한다.

## 2. 모드 조합별 테스트

| 케이스 | RenderLimits | SkadiNet | FearNoSpear | 목적 |
|---|---:|---:|---:|---|
| A | Off | Off | Off | 바닐라 기준선 확인 |
| B | Off | Off | On | 바닐라 환경에서 소실 완화 확인 |
| C | On 기본값 | Off | Off | RenderLimits 단독 영향 확인 |
| D | On 기본값 | Off | On | RenderLimits 환경에서 rescue 확인 |
| E | On, Loaded/Generated 증가 | Off | On | 로드 범위 증가가 재현율을 낮추는지 확인 |
| F | On | On, `OwnershipIntensity=0` | On | SkadiNet ownership 영향 분리 |
| G | On | On 기본값 | On | 실제 사용 조합 확인 |
| H | On | On, scheduler/payload 개별 0 | On | SkadiNet 기능별 영향 분리 |

## 3. RenderLimits 비교 권장값

초기 안정성 테스트:

```ini
Active zones = 2
Loaded zones = 4
Generated zones = 6
```

계속 재현되면:

```ini
Active zones = 2
Loaded zones = 4
Generated zones = 8
```

## 4. SkadiNet 비교 권장값

1차 원인 분리:

```ini
OwnershipIntensity = 0
```

그 다음 하나씩만 변경:

```ini
SchedulerThroughput = 0
PayloadReducerStrength = 0
RpcAoiAggression = 0
```

## 5. FearNoSpear 권장 테스트 설정

```ini
[General]
Lock Configuration = On
SpearIndicatorStyle = BeamAndHud
MaxDisplayedSpears = 1
CleanDeathPins = true
OwnerOnlyTombstones = true
```

## 6. 성공 판정

- 정상 hit에서는 창이 정확히 하나만 남는다.
- TTL rescue 또는 `ZNetScene.Destroy` rescue 로그가 뜬 경우 창 아이템이 하나 생성된다.
- 멀티플레이에서 두 클라이언트가 같은 창을 중복 생성하지 않는다.
- 표시 개수를 1~5로 바꾸면 자신의 추적된 창을 가까운 순서로 자동 표시한다.
- 창을 주우면 정확히 해당 창의 표시가 사라지고 다음으로 가까운 창을 표시한다.
- `Off`에서는 표시와 로컬 위치 조회만 멈추고 소실 방지와 투척자 전용 자동줍기는 유지한다.
- RenderLimits loaded/generated 값을 올렸을 때 rescue 빈도나 소실 빈도가 줄어드는지 관찰할 수 있다.

## 7. 실패 판정

- 창이 2개 이상 생김: owner gate, ZDO claim flag, invalid-ZNetView fallback 시간을 확인한다. 기존 창을 item-data 동등성만으로 삭제해서는 안 된다.
- rescue 로그는 있는데 item이 없음: `SpawnOnHit` 경로와 `ItemDrop.DropItem` fallback을 확인한다.
- 화살, 적 투사체, 비창 투사체가 추적됨: spear detection 조건을 확인한다.
- 월드 종료 중 item이 생성됨: shutdown/world unload guard를 확인한다.
- SkadiNet 사용 시 owner invalid로 rescue가 skip됨: 마지막으로 이 클라이언트의 소유권이 확인됐는지, 그 확인이 고정된 2초 이내인지 확인한다. Rescue는 TTL 만료 전 1초와 물리 프레임 안전 여유를 기준으로 작동하며 별도 설정은 없다.
- 표시가 오래된 위치에 남음: 정확한 드롭 식별자, 픽업 응답 억제, 서버 캐시와 로컬 위치 우선 적용을 확인한다.

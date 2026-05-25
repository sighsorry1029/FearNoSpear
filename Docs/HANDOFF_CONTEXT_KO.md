# 한국어 handoff context

## 1. 왜 이 모드가 필요한가

Valheim에서 투척창은 플레이어 인벤토리의 item이 그대로 날아가는 방식이 아니라, 비행 중에는 `Projectile`로 존재하고 정상 hit가 발생한 뒤에야 원래 item data를 이용해 dropped item으로 다시 생성되는 구조로 보인다.

따라서 projectile이 다음 조건에서 먼저 제거되면 창 item이 생성되지 않는다.

- TTL 만료
- terrain/object collision 누락
- loaded/active area 밖 cleanup
- ZDO 제거
- network ownership 전환 또는 owner-only simulation 중단

사용자의 “`find`로도 나타나지 않는다”는 관찰은 이 모델과 잘 맞는다. 창 item이 어딘가에 남아 있는 것이 아니라, item이 되기 전 projectile이 사라진 것이다.

## 2. 바닐라 쪽 의심 경로

Codex가 Valheim assembly에서 확인해야 할 흐름:

1. `Projectile.Setup(...)`에서 recoverable projectile이 `m_spawnItem`에 원래 item data를 저장하는지 확인
2. `Projectile.FixedUpdate`가 `m_nview.IsValid()` 및 `m_nview.IsOwner()` 조건에 의존하는지 확인
3. `Projectile.OnHit(...)`이 `m_didHit`을 세팅하고 `SpawnOnHit(...)`을 호출하는지 확인
4. `Projectile.SpawnOnHit(...)`이 `m_spawnItem`을 이용해 `ItemDrop`을 생성하는지 확인
5. TTL 만료 또는 `ZNetScene.Destroy` 경로에서 `SpawnOnHit`이 생략될 수 있는지 확인

이 모드 초안은 5번의 마지막 손실 경로를 막는 safety net이다.

## 3. RenderLimits 관련 판단

RenderLimits는 단순히 그래픽 렌더링만 조절하는 모드로 보기 어렵다. active/loaded/generated zone 범위와 scene/ZDO object query에 영향을 준다면 다음 문제가 생길 수 있다.

- 높은 산에서 던진 창이 먼 sector로 빠르게 이동한다.
- 해당 sector가 loaded/generated 상태가 아니거나 active area 밖으로 취급된다.
- projectile이 아직 지형에 hit하지 않았는데 scene cleanup 대상이 된다.
- projectile이 `SpawnOnHit` 전에 제거되어 창 item이 생성되지 않는다.

따라서 RenderLimits는 이 버그를 “만드는” 유일한 원인이라고 단정할 수는 없지만, 재현율을 높이는 강한 후보이다.

## 4. SkadiNet 관련 판단

SkadiNet은 ZDO scheduling, ownership recovery, payload reduction, RPC/AOI 계열 기능을 가진 네트워크 최적화 모드다. projectile 자체를 삭제한다고 단정하기는 어렵지만, projectile simulation이 owner-only인 경우 ownership 전환/지연이 문제가 될 수 있다.

가능한 경로:

1. 창 projectile이 기존 owner에서 다른 peer로 ownership 전환된다.
2. 새 owner가 projectile을 충분히 빨리 instantiate/simulate하지 못한다.
3. collision/hit 처리 없이 TTL 또는 area cleanup이 먼저 발생한다.
4. RenderLimits의 loaded area 축소와 겹치면 소실 확률이 올라간다.

따라서 SkadiNet은 단독 주범보다는 RenderLimits와 결합한 간접 악화 요인으로 보는 것이 좋다.

## 5. 모드 설계 원칙

### 대상

- `m_respawnItemOnHit == true`
- `m_spawnItem != null`
- `m_spawnItem` 또는 `m_weapon`의 skill/name이 spear로 판정되는 projectile

### 동작

- setup 직후 tracker component 부착
- 초기 TTL을 최소값까지 올림
- TTL 만료 직전 `SpawnOnHit` 경로 호출 후 projectile destroy
- `ZNetScene.Destroy(GameObject)` 직전 unhit projectile이면 rescue
- component `OnDestroy`에서 마지막 fallback rescue

### 중복 방지

- 기본적으로 current `ZNetView` owner만 rescue
- ZNetView가 invalid가 된 경우 last-known owner만 허용하는 옵션
- valid ZDO가 있으면 `FearNoSpear.Rescued` bool claim flag를 세팅하여 중복 rescue 방지
- `m_didHit`과 tracker `_normalHit`으로 정상 hit 이후 rescue 금지
- `_rescueAttempted`으로 같은 component의 중복 rescue 금지

## 6. Codex가 보강하면 좋은 부분

- target Valheim version에서 `SpawnOnHit` signature를 정확히 확정
- `SpawnOnHit` 실패 시 `ItemDrop.DropItem` fallback 추가 여부 검토
- fallback 사용 시 item data clone이 필요한지 확인
- `ApplicationQuit`/world unload 중 OnDestroy rescue가 발생하지 않도록 guard 추가 검토
- owner invalid 상태에서 last-known owner rescue가 멀티에서 안전한지 테스트
- RenderLimits로 인해 projectile GameObject 자체가 tracker 없이 생성/제거되는 경우가 있는지 확인

## 7. 배포 금지/주의 파일

이 zip에는 포함하지 않았지만, 사용자가 분석용으로 제공한 파일은 다음과 같다.

- `assembly_valheim_publicized(4).dll`
- `Assembly-CSharp_publicized(4).dll`
- `SkadiNet(1).dll`
- `RenderLimits.dll`
- 기타 Valheim publicized assemblies

Valheim/BepInEx/SkadiNet/RenderLimits DLL은 공개 배포 zip에 넣지 않는다.

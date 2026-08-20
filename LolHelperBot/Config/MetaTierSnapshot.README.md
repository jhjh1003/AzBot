# MetaTierSnapshot.json 채우는 법

`/밴픽추천`에 일반 메타(op.gg 기준) 티어·카운터픽 데이터를 붙이기 위한 수동 스냅샷 파일입니다.

**왜 자동 크롤링이 아니라 수동인가:** op.gg는 `robots.txt`상으로는 크롤링을 막지 않지만 ToS가
불확실해서(AfterUpgrade.md 참고), 자동 스크래퍼 대신 사람이 가끔 op.gg를 직접 보고 옮겨 적는
방식을 택했습니다. 이 파일이 비어 있거나 없어도 `/밴픽추천`의 클랜 자체 데이터 기반 추천(베스트
픽, 상대했을 때 승률 안 좋은 챔피언, 우리 티어픽 카운터)은 정상 동작합니다 — 메타 데이터는 있으면
추가되는 보너스입니다.

## 업데이트 방법

**캡처로 넘기고 싶다면:** 직접 JSON을 안 만져도, op.gg 화면을 캡처해서
[Config/OpggCaptures/](OpggCaptures/) 폴더에 넣어두면 그걸 보고 이 파일을 채워드립니다 —
뭘 캡처하면 되는지는 그 폴더의 README 참고.

**직접 채우고 싶다면:**
1. op.gg에서 라인별 티어 리스트(자유 랭크/솔로 랭크 기준 아무 쪽이나 참고용으로 선택)를 확인합니다.
2. 각 라인 상위 몇 개 챔피언의 티어(OP/1/2/...), 승률, 픽률, 밴률, 그리고 그 챔피언의 "카운터" 탭에 나오는
   상위 카운터 챔피언 몇 개를 아래 형식대로 `positions` 아래 해당 라인 배열에 채워 넣습니다.
3. `updatedAt`을 오늘 날짜(YYYY-MM-DD)로 갱신합니다.
4. 저장만 하면 됩니다 — 봇 재시작 없이 `/밴픽추천` 실행 시마다 이 파일을 다시 읽습니다
   (단, `dotnet run`이 아니라 이미 빌드된 실행 파일을 그대로 띄워둔 상태라면 빌드 출력 폴더의
   `Config\MetaTierSnapshot.json`도 같이 덮어써야 반영됩니다 — 소스 폴더 파일만 고치면 다음 빌드
   때 복사됩니다).

라인 키는 `TOP` / `JUNGLE` / `MIDDLE` / `BOTTOM` / `UTILITY` 다섯 개만 사용합니다(대소문자
무관하게 읽지만 그대로 맞춰 쓰는 걸 권장). 챔피언명은 영문 표기(Riot API 표준 championName,
예: `MonkeyKing`은 오공, `Wukong`이 아닙니다)로 맞춰야 클랜 데이터와 매칭됩니다.

## 예시

```json
{
  "updatedAt": "2026-08-20",
  "source": "op.gg 라인별 티어 리스트 (수동 스냅샷)",
  "positions": {
    "TOP": [
      { "champion": "Aatrox", "tier": "OP", "winRate": 51.2, "pickRate": 12.3, "banRate": 18.4, "counters": ["Malphite", "Poppy", "Ornn"] },
      { "champion": "Jax", "tier": "1", "winRate": 50.6, "pickRate": 8.1, "banRate": 11.2, "counters": ["Renekton", "Fiora"] }
    ],
    "JUNGLE": [],
    "MIDDLE": [],
    "BOTTOM": [],
    "UTILITY": []
  }
}
```

- `tier`, `winRate`, `pickRate`, `banRate`는 표시와 메타픽 TOP 3 선정에 사용합니다.
- `counters`는 없어도 되지만(빈 배열 `[]`), 있으면 `/밴픽추천` 픽 추천 줄에 "카운터: X, Y" 형태로
  같이 표시됩니다.

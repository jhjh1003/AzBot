# op.gg 캡처 폴더

여기에 캡처(스크린샷)를 넣어두면, 그걸 보고 `../MetaTierSnapshot.json`을 채워넣습니다.
이 폴더 자체는 원본 자료 보관용이고, 실제로 `/밴픽추천`이 읽는 건 `MetaTierSnapshot.json`입니다.

**봇(.NET)은 이 폴더를 자동으로 읽지 않습니다.** 스크린샷 파싱은 Claude Code(=이 세션)가
직접 이미지를 보고 처리합니다 — .NET 봇에 비전 API를 따로 붙이면 API 키·비용·파싱 실패 처리
로직이 추가로 필요해지는데, 정확도는 어차피 똑같은 모델이라 별 이득이 없어서(2026-08-20 결정).

## 폴더 구조 — 날짜별 하위 폴더

캡처는 날짜별 하위 폴더에 넣습니다. 폴더명은 `YYMMDD` 형식(예: `260820` = 2026-08-20).

```
OpggCaptures/
  260820/
    tier_TOP.png
    tier_JUNGLE.png
    tier_MIDDLE.png
    tier_BOTTOM.png
    tier_UTILITY.png
    counter_Ahri.png
    PROCESSED.md   ← 처리 끝나면 여기 남김 (아래 "처리 방법" 참고)
  260827/
    ...
```

## 처리 방법

1. 새 날짜 폴더에 캡처를 넣습니다.
2. "최신 캡처 처리해줘" / "캡처 넣었어, 갱신해줘"라고 말합니다.
3. `PROCESSED.md`가 **없는** 가장 최근 날짜 폴더를 찾아서 그 안의 이미지를 읽고
   `../MetaTierSnapshot.json`을 갱신한 다음, 그 폴더 안에 `PROCESSED.md`(처리 일시 기록)를
   남깁니다 — 다음에 또 "처리해줘" 해도 이미 처리한 폴더는 건너뜁니다.

## 뭘 캡처하면 되는지

### 1) 라인별 티어 리스트 (5장, 필수)

op.gg 티어 리스트 페이지에서 **라인 하나씩 선택**해서, 챔피언명·티어·승률이 보이는 화면을
캡처합니다. 상위 10~15위 정도까지 나오면 충분합니다.

파일명: `tier_TOP.png`, `tier_JUNGLE.png`, `tier_MIDDLE.png`, `tier_BOTTOM.png`, `tier_UTILITY.png`
(원딜=BOTTOM, 서폿=UTILITY 입니다.)

→ `MetaTierSnapshot.json`의 `positions.{라인}` 배열(`champion`/`tier`/`winRate`)을 채우는 데 씀.

### 2) 챔피언별 카운터 탭 (필요한 만큼)

각 챔피언 상세 페이지에는 "이 챔피언을 상대로 강한 챔피언(카운터)" 탭이 있습니다. 그 목록이
보이는 화면을 챔피언별로 캡처합니다.

파일명: `counter_{챔피언명}.png` (예: `counter_Ahri.png`)

**어떤 챔피언을 캡처하면 좋을지 우선순위:**
1. **우리 클랜 라인별 베스트픽** (`/티어픽` 결과에 나오는 챔피언들) — 상대가 우리 주력픽을
   카운터할 수 있는 챔피언이 뭔지 알아야 밴 판단에 도움이 됩니다.
2. 위 1)의 티어 리스트 캡처에서 **S/A 티어로 나온 챔피언들** — 메타 최상위 픽들 카운터도 있으면
   좋습니다.

전부 다 할 필요는 없고, 라인당 3~5명 정도만 있어도 충분히 쓸모 있습니다.

## 챔피언명 주의사항

`MetaTierSnapshot.json`에는 **Riot API 표준 championName**(영문, 띄어쓰기·특수문자 없음)으로
적어야 클랜 데이터와 매칭됩니다. op.gg 표시명과 다른 대표적인 예:

| op.gg 표시 | JSON에 적을 값 |
|---|---|
| 오공 / Wukong | `MonkeyKing` |
| K'Sante | `KSante` |
| Kai'Sa | `Kaisa` |
| Kha'Zix | `Khazix` |
| Cho'Gath | `Chogath` |
| Vel'Koz | `Velkoz` |
| LeBlanc | `Leblanc` |
| Dr. Mundo | `DrMundo` |
| Jarvan IV | `JarvanIV` |
| Xin Zhao | `XinZhao` |
| Nunu & Willump | `Nunu` |

헷갈리면 그냥 op.gg 표시명 그대로 캡처만 넣어주셔도, 옮겨 적을 때 맞춰서 변환하면 됩니다.

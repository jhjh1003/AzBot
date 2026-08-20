# 배포 가이드 — 로컬 PC 밖에서 봇 돌리기

지금까지는 `dotnet run`으로 내 PC에서만 봇을 돌렸습니다. 이 문서는 **컨테이너(Docker)로 패키징
해서 클라우드 VM 같은 곳에서 24시간 돌리는 방법**을 정리합니다. 왜 Netlify/Supabase 같은
서버리스 서비스가 안 맞고 어떤 곳이 맞는지는 ARCHITECTURE.md보다는 이 문서보다 대화 맥락(또는
AfterUpgrade.md)을 참고하세요 — 요약하면 **디스코드 봇은 웹소켓을 24시간 붙잡고 있어야 해서
"항상 켜져 있는 프로세스"를 돌려주는 곳(VM, VPS)이 필요**하고, Oracle Cloud Always Free 같은
평생 무료 VM을 추천합니다.

## 1. 로컬에서 Docker로 먼저 테스트 (배포 전 필수)

Docker Desktop이 설치돼 있다면, 저장소 루트에서:

```powershell
docker build -t azbot .
```

빌드가 끝나면 실행해봅니다(자기 컴퓨터에서만 테스트하는 거라 볼륨 없이도 됩니다 — 컨테이너
지우면 DB도 같이 지워짐에 유의):

```powershell
docker run --rm `
  -e Discord__Token="여기에_봇_토큰" `
  -e Discord__GuildId="여기에_서버_ID" `
  -e Riot__ApiKey="여기에_Riot_API_키" `
  azbot
```

콘솔에 `[준비 완료] 길드 ... 에 슬래시 커맨드를 등록했습니다.`가 뜨면 성공입니다. 디스코드에서
`/ping`을 쳐서 확인하세요. `Ctrl+C`로 종료합니다.

## 2. 실제로 배포할 때 — 데이터가 안 날아가게 볼륨 마운트

컨테이너를 지웠다 다시 만들어도 SQLite DB가 남아있으려면 **반드시 볼륨을 마운트**해야 합니다.
`Storage__DatabasePath`는 Dockerfile에서 이미 `/data/lol-helper.db`로 고정해뒀습니다.

```bash
docker volume create azbot-data

docker run -d \
  --name azbot \
  --restart unless-stopped \
  -v azbot-data:/data \
  -e Discord__Token="여기에_봇_토큰" \
  -e Discord__GuildId="여기에_서버_ID" \
  -e Riot__ApiKey="여기에_Riot_API_키" \
  azbot
```

- `--restart unless-stopped` — VM이 재부팅돼도 봇이 자동으로 다시 뜹니다.
- `-d` — 백그라운드 실행. 로그는 `docker logs -f azbot`으로 봅니다.
- 기존에 로컬(`%LOCALAPPDATA%\LolHelperBot\lol-helper.db`)에 쌓아둔 데이터를 그대로 옮기고
  싶다면, 그 파일을 새 볼륨 안(`/data/lol-helper.db`)으로 복사해 넣으면 됩니다
  (`docker cp` 또는 볼륨을 마운트한 상태에서 직접 넣기).

## 3. 필요한 환경변수

| 변수 | 값 |
|---|---|
| `Discord__Token` | Discord 봇 토큰 |
| `Discord__GuildId` | 봇이 등록될 서버(길드) ID |
| `Riot__ApiKey` | Riot API 키 |
| `Riot__Region` | 생략 시 `kr` |
| `Riot__AccountRegion` | 생략 시 `asia` |
| `Storage__DatabasePath` | Dockerfile이 이미 `/data/lol-helper.db`로 지정해둠 — 보통 안 건드려도 됨 |

**주의**: 이 값들은 이미지 안에 들어가지 않습니다(`.dockerignore`가 시크릿 관련 파일을 다
빼둠). 실행할 때마다 `-e`로 넣거나, `.env` 파일 + `docker run --env-file .env`를 씁니다. `.env`
파일 자체는 `.gitignore`/`.dockerignore`에 이미 걸려 있어서 커밋되지 않습니다.

## 4. 클라우드 VM에 올리기 (예: Oracle Cloud Always Free)

1. Oracle Cloud 계정 생성 → Always Free 규격 VM(예: Ampere A1, 1 OCPU/6GB 정도면 이 봇엔 넉넉)
   인스턴스 생성. Ubuntu 이미지 권장.
2. VM에 SSH 접속 후 Docker 설치.
   ```bash
   curl -fsSL https://get.docker.com | sudo sh
   sudo usermod -aG docker $USER   # 재접속 필요
   ```
3. 이 저장소를 VM에 클론(또는 `git archive`로 소스만 옮겨도 됨):
   ```bash
   git clone https://github.com/jhjh1003/AzBot.git
   cd AzBot
   docker build -t azbot .
   ```
4. 위 2절의 `docker run` 명령으로 실행. 방화벽/보안그룹 설정은 **필요 없습니다** — 이 봇은
   외부에서 들어오는 연결을 받는 서버가 아니라, 봇이 디스코드로 "나가는" 연결만 만들기
   때문입니다(포트 개방 불필요).
5. VM이 재부팅돼도 자동으로 다시 뜨는지 확인하려면 VM을 한 번 재부팅해보고
   `docker ps`로 `azbot` 컨테이너가 다시 떠 있는지 확인하세요.

## 5. 앞으로 고려할 것

- **CI로 이미지 자동 빌드**: 지금은 수동으로 `docker build`. GitHub Actions로 push할 때마다
  이미지를 빌드해서 레지스트리에 올리는 것도 나중에 고려 가능(당장은 불필요).
- **DB를 Postgres(Supabase 등)로 옮기기**: 지금 SQLite는 이 규모에서 충분하지만, 나중에
  백업/이중화가 필요해지면 `MatchRepository`/`MemberRepository`를 Postgres로 바꾸는 걸
  검토할 수 있습니다(지금은 필요성 낮음, AfterUpgrade.md 참고).

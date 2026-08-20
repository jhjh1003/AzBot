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

## 4. 클라우드 VM에 올리기 — Oracle Cloud Always Free, 스텝 바이 스텝

### 4-1. Oracle Cloud 계정 만들기

1. https://www.oracle.com/cloud/free/ 접속 → "Start for free" 클릭.
2. 이메일 인증 → 국가/이름 등 기본 정보 입력 → **결제 정보(카드) 입력**을 요구합니다. Always
   Free 리소스만 쓰면 돈이 안 빠져나가지만, 본인 확인용으로 카드 등록 자체는 필수입니다(해외
   결제 가능한 카드 필요). 이 단계에서 막히면 카드사에 "해외 승인" 여부부터 확인해보세요.
3. 가입 완료 후 콘솔(Console) 로그인.

### 4-2. VM(인스턴스) 만들기

1. 콘솔 왼쪽 상단 ☰ 메뉴 → **Compute → Instances → Create Instance**.
2. **Name**: `azbot` 등 원하는 이름.
3. **Image and shape**:
   - Image: **Ubuntu** (최신 LTS, 예: 24.04) 선택.
   - Shape: **"Edit"** 클릭 → **Ampere(Arm 기반)** 계열 중 `VM.Standard.A1.Flex` 선택 → OCPU
     1개, 메모리 6GB 정도로 설정(이 봇 규모엔 넉넉함). 이게 **Always Free**로 표시되는지
     확인하고 진행하세요(Always Free 한도 안에서만 무료입니다 — 화면에 "Always Free eligible"
     문구가 뜹니다).
4. **Networking**: 기본값 그대로 두면 됩니다(새 VCN 자동 생성, Public IP 자동 할당). 별도로
   포트를 열 필요는 없습니다 — 이 봇은 외부에서 들어오는 연결을 받는 서버가 아니라 디스코드
   쪽으로 "나가는" 연결만 만들기 때문입니다.
5. **Add SSH keys**: "Generate a key pair for me" 선택 → **Private Key 다운로드 버튼을 꼭
   눌러서 저장**(다시 못 받습니다). 파일명 예: `ssh-key-2026-08-21.key`.
6. **Create** 클릭 → 1~2분 기다리면 인스턴스가 "RUNNING" 상태가 됩니다.
7. 인스턴스 상세 페이지에서 **Public IP Address**를 확인해서 메모해두세요(예: `123.45.67.89`).

### 4-3. SSH로 VM에 접속하기 (Windows)

Windows 11은 OpenSSH 클라이언트가 기본 내장돼 있어서 PowerShell에서 바로 됩니다. 다운받은
개인키 파일이 있는 폴더에서:

```powershell
# 개인키 권한을 너무 열어두면 ssh가 거부합니다 — 본인만 읽을 수 있게 제한
icacls .\ssh-key-2026-08-21.key /inheritance:r
icacls .\ssh-key-2026-08-21.key /grant:r "$($env:USERNAME):(R)"

ssh -i .\ssh-key-2026-08-21.key ubuntu@123.45.67.89
```

(Oracle Ubuntu 이미지의 기본 사용자는 `ubuntu`입니다.) 처음 접속 시 "fingerprint를 신뢰하냐"는
질문엔 `yes`를 입력합니다. 접속되면 VM의 셸이 뜹니다 — 이제부터 아래 명령은 전부 **VM
안에서** 실행합니다.

### 4-4. VM에 Docker 설치

```bash
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker $USER
```

`usermod` 실행 후에는 `exit`로 나갔다가 4-3의 `ssh` 명령으로 다시 접속해야 그룹 변경이
적용됩니다(매번 `sudo` 안 붙이려면).

### 4-5. 코드 가져와서 빌드 + 실행

```bash
git clone https://github.com/jhjh1003/AzBot.git
cd AzBot
docker build -t azbot .
```

빌드가 끝나면(레포가 작아서 몇 분 안 걸립니다), 시크릿을 담을 `.env` 파일을 VM에 직접
만듭니다(이 파일은 절대 git에 올리지 않습니다 — VM 로컬에만 존재):

```bash
cat > .env <<'EOF'
Discord__Token=여기에_봇_토큰
Discord__GuildId=여기에_서버_ID
Riot__ApiKey=여기에_Riot_API_키
EOF

docker volume create azbot-data

docker run -d \
  --name azbot \
  --restart unless-stopped \
  -v azbot-data:/data \
  --env-file .env \
  azbot
```

### 4-6. 확인

```bash
docker ps                 # azbot 컨테이너가 Up 상태인지
docker logs -f azbot       # [준비 완료] ... 메시지 확인 (Ctrl+C로 로그 보기 종료, 컨테이너는 안 멈춤)
```

디스코드에서 `/ping`을 쳐서 응답 오면 성공입니다. 기존에 로컬에서 쌓아둔 DB
(`%LOCALAPPDATA%\LolHelperBot\lol-helper.db`)를 이어서 쓰고 싶다면, VM으로 파일을 복사한
뒤(`scp -i 키파일 lol-helper.db ubuntu@IP:~/`) 아래처럼 볼륨 안에 넣어줍니다:

```bash
docker run --rm -v azbot-data:/data -v ~/:/host alpine cp /host/lol-helper.db /data/lol-helper.db
```

### 4-7. VM 재부팅돼도 자동으로 다시 뜨는지 확인 (선택)

```bash
sudo reboot
```

몇 분 후 다시 SSH 접속해서 `docker ps`로 `azbot`이 다시 떠 있는지 확인합니다
(`--restart unless-stopped` 덕분에 Docker 데몬이 뜨면 컨테이너도 자동으로 같이 뜹니다).

## 5. 업데이트(재배포) — 로컬에서 고치고 VM에 반영하기

로컬 흐름은 지금까지 하던 대로입니다: 코드 수정 → `dotnet run`으로 테스트 → 문제없으면
`git push`. VM에 반영하는 건 아래 4줄이 전부입니다(볼륨은 그대로 재사용되므로 **DB는 안
날아갑니다**):

```bash
ssh -i .\ssh-key-2026-08-21.key ubuntu@123.45.67.89   # VM 접속
cd AzBot
git pull
docker build -t azbot .
docker stop azbot && docker rm azbot
docker run -d --name azbot --restart unless-stopped -v azbot-data:/data --env-file .env azbot
```

자주 쓸 것 같으면 VM에 아래처럼 스크립트로 저장해두고 `./redeploy.sh` 한 줄로 끝낼 수도
있습니다:

```bash
cat > redeploy.sh <<'EOF'
#!/bin/bash
set -e
cd ~/AzBot
git pull
docker build -t azbot .
docker stop azbot || true
docker rm azbot || true
docker run -d --name azbot --restart unless-stopped -v azbot-data:/data --env-file .env azbot
echo "재배포 완료. 로그: docker logs -f azbot"
EOF
chmod +x redeploy.sh
```

## 6. 앞으로 고려할 것

- **CI로 이미지 자동 빌드**: 지금은 수동으로 `docker build`. GitHub Actions로 push할 때마다
  이미지를 빌드해서 레지스트리에 올리는 것도 나중에 고려 가능(당장은 불필요).
- **DB를 Postgres(Supabase 등)로 옮기기**: 지금 SQLite는 이 규모에서 충분하지만, 나중에
  백업/이중화가 필요해지면 `MatchRepository`/`MemberRepository`를 Postgres로 바꾸는 걸
  검토할 수 있습니다(지금은 필요성 낮음, AfterUpgrade.md 참고).

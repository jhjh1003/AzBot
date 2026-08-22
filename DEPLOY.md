# 배포 가이드 — 로컬 PC 밖에서 봇 돌리기

지금까지는 `dotnet run`으로 내 PC에서만 봇을 돌렸습니다. 이 문서는 **컨테이너(Docker)로 패키징
해서 클라우드 VM 같은 곳에서 24시간 돌리는 방법**을 정리합니다. 왜 Netlify/Supabase 같은
서버리스 서비스가 안 맞고 어떤 곳이 맞는지는 ARCHITECTURE.md보다는 이 문서보다 대화 맥락(또는
AfterUpgrade.md)을 참고하세요 — 요약하면 **디스코드 봇은 웹소켓을 24시간 붙잡고 있어야 해서
"항상 켜져 있는 프로세스"를 돌려주는 곳(VM, VPS)이 필요**합니다. VM 제공자는 **Vultr(서울
리전, 월 소액 유료)**를 씁니다 — 아래 2026-08-22 업데이트 참고.

**2026-08-22 업데이트 (1) — 빌드는 VM 밖에서**: 처음엔 VM 위에서 직접 `docker build`
(= `dotnet publish`)를 돌리는 방식으로 썼는데, Oracle Always Free의 `E2.1.Micro`(1GB RAM)에서
빌드 도중 메모리 부족으로 죽는 문제가 있었습니다. **원인은 "이 봇을 실행할 사양"이 부족한 게
아니라 "이 봇을 빌드할 사양"이 부족했던 것**입니다 — 봇 자체는 실행할 때 100~200MB면 충분합니다.
그래서 지금은 **빌드는 GitHub Actions가 하고, VM은 완성된 이미지를 내려받기(`docker pull`)만**
하도록 바꿨습니다. VM에서 `docker build`를 아예 안 하니 1GB RAM VM으로도 충분합니다.

**2026-08-22 업데이트 (2) — Oracle Cloud Always Free 포기**: 위 문제를 고치고 VM을 다시
만들려던 차에, **Oracle 계정 자체가 며칠 안 썼다고 예고 없이 정지**됐습니다. 무료 티어 계정을
비활성/의심 계정으로 판단해 통보 없이 정지시키는 사례가 흔하고, 무료 계정은 이의 신청도 잘 안
받아줘서 복구가 사실상 어렵습니다. 이 봇은 원래 자원을 거의 안 쓰므로(디스코드 웹소켓 하나 +
SQLite), 이런 리스크를 계속 감수하느니 **월 소액(약 6천~8천원)짜리 VPS로 전환**하기로 했습니다
— 아래는 **Vultr(서울 리전)** 기준입니다.

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

## 4. GitHub Actions로 이미지 자동 빌드 (VM에서 빌드 안 하기)

`.github/workflows/docker-publish.yml`이 이미 저장소에 있습니다. `main` 브랜치에
`LolHelperBot/` 또는 `Dockerfile`이 바뀐 커밋을 push하면(또는 GitHub의 Actions 탭에서 수동
실행하면) **GitHub의 서버(러너)가 대신 `docker build`를 돌려서**, 완성된 이미지를
`ghcr.io/jhjh1003/azbot`(GHCR, GitHub Container Registry)에 올려줍니다. 별도 계정 가입이나
결제 정보 입력이 필요 없습니다 — 이미 갖고 있는 GitHub 계정 그대로 씁니다.

### 4-1. 처음 한 번: 워크플로 동작 확인

1. 지금 이 변경사항(전적재배정 기능 + 이 워크플로 파일)을 `main`에 push합니다.
2. GitHub 저장소 페이지 → **Actions** 탭 → "Docker 이미지 빌드 & GHCR 푸시"가 실행 중/완료로
   뜨는지 확인합니다(3~5분 정도 걸립니다. 로컬 1GB VM보다 훨씬 빠릅니다).
3. 초록 체크가 뜨면 성공. 저장소 페이지 오른쪽 사이드바(또는 GitHub 프로필 → **Packages**)에
   `azbot` 패키지가 새로 생긴 게 보입니다.

### 4-2. 이미지를 공개로 전환 (VM에서 로그인 없이 pull하기 위해 — 권장)

기본적으로 GHCR에 올라간 이미지는 비공개(내 계정만 접근 가능)입니다. VM에서 매번 로그인하는
걸 피하려면 공개로 바꿔두는 게 편합니다(소스 코드 자체는 이미 공개 저장소이고, 이미지 안에도
시크릿이 안 들어가므로 공개해도 안전합니다):

1. GitHub 프로필 → **Packages** → `azbot` 클릭.
2. 오른쪽 **Package settings** → 맨 아래 **Danger Zone** → **Change visibility** → **Public**.

비공개로 유지하고 싶다면 5-6에서 VM이 `docker login`하도록 PAT(Personal Access Token,
`read:packages` 권한)를 하나 만들어서 쓰면 됩니다.

## 5. 클라우드 VM에 올리기 — Vultr(서울 리전), 스텝 바이 스텝

빌드를 VM에서 안 하게 됐으니 애초에 사양은 크게 안 봐도 됩니다 — 그냥 **가장 저렴한 플랜
(1GB RAM 안팎)**이면 충분합니다. 서울 리전을 쓰면 Riot KR API·디스코드 응답 지연도 가장
낮습니다.

### 5-1. (로컬) SSH 키 만들기

Vultr는 Oracle처럼 개인키를 만들어서 주는 게 아니라, **내가 만든 공개키를 등록**하는 방식입니다.
PowerShell에서(Windows 11은 OpenSSH가 기본 내장돼 있어 별도 설치 불필요):

```powershell
ssh-keygen -t ed25519 -f "$HOME\.ssh\azbot_vultr" -C "azbot"
```

암호(passphrase)는 그냥 Enter로 비워도 됩니다(입력해도 되지만, 이후 자동화 스크립트에서는
매번 물어봐서 번거로움). 완료되면 `azbot_vultr`(개인키)·`azbot_vultr.pub`(공개키) 두 파일이
`~/.ssh`에 생깁니다. 공개키 내용을 화면에 출력해서 복사해두세요(다음 단계에서 붙여넣습니다):

```powershell
Get-Content "$HOME\.ssh\azbot_vultr.pub"
```

### 5-2. Vultr 계정 만들기 + 결제수단 등록

1. https://www.vultr.com 접속 → 가입(이메일 또는 Google/GitHub 계정으로 가능).
2. 계정 생성 후 **Billing → Payment Methods**에서 카드 또는 PayPal 등록(사용한 만큼만
   과금되는 종량제라, 인스턴스를 안 지우면 매달 플랜 금액이 청구됩니다).

### 5-3. VM(인스턴스) 만들기

1. 대시보드 우측 상단 **+ Deploy** → **Deploy New Server**.
2. **Choose Server**: **Cloud Compute – Shared CPU** (가장 저렴한 일반형).
3. **CPU & Storage Technology**: 기본값(Regular Performance / Intel or AMD) 그대로.
4. **Server Location**: **Seoul, South Korea**.
5. **Server Image**: **Ubuntu 24.04 LTS x64**.
6. **Server Size**: 1GB RAM 플랜(가장 저렴한 축, 월 약 6천~8천원대) 선택 — 이 봇은 이 정도면
   넉넉합니다.
7. **SSH Keys** → **Add New** → 5-1에서 복사해둔 공개키(`azbot_vultr.pub` 내용)를 붙여넣고
   저장 → 방금 추가한 키를 체크.
8. **Server Hostname & Label**: `azbot` 등 원하는 이름.
9. **Deploy Now** 클릭 → 1~2분 기다리면 상태가 "Running"으로 바뀝니다.
10. 인스턴스 목록에서 방금 만든 서버를 클릭해 **IP Address**를 확인해서 메모해두세요(예:
    `123.45.67.89`).

### 5-4. SSH로 VM에 접속하기 (Windows)

```powershell
ssh -i "$HOME\.ssh\azbot_vultr" root@123.45.67.89
```

(Vultr Ubuntu 이미지의 기본 사용자는 `root`입니다 — Oracle의 `ubuntu`와 다릅니다.) 처음 접속 시
"fingerprint를 신뢰하냐"는 질문엔 `yes`를 입력합니다. 접속되면 VM의 셸이 뜹니다 — 이제부터
아래 명령은 전부 **VM 안에서** 실행합니다.

### 5-5. VM에 Docker 설치

이미 `root`로 접속했으므로 `sudo`나 `usermod` 단계 없이 바로 설치됩니다:

```bash
curl -fsSL https://get.docker.com | sh
```

### 5-6. 이미지 받아서 실행 (git clone도, docker build도 필요 없음)

VM에는 소스 코드가 아예 필요 없습니다 — GitHub Actions가 이미 만들어둔 이미지를 그대로
받습니다:

```bash
# 4-2에서 이미지를 공개로 바꿨다면 로그인 없이 바로 pull 가능
docker pull ghcr.io/jhjh1003/azbot:latest
```

(이미지를 비공개로 유지했다면, pull 전에 한 번만 로그인:
`echo <PAT> | docker login ghcr.io -u jhjh1003 --password-stdin`)

시크릿을 담을 `.env` 파일을 VM에 직접 만듭니다(이 파일은 절대 git에 올리지 않습니다 — VM
로컬에만 존재):

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
  ghcr.io/jhjh1003/azbot:latest
```

### 5-7. 확인

```bash
docker ps                 # azbot 컨테이너가 Up 상태인지
docker logs -f azbot       # [준비 완료] ... 메시지 확인 (Ctrl+C로 로그 보기 종료, 컨테이너는 안 멈춤)
```

디스코드에서 `/ping`을 쳐서 응답 오면 성공입니다. 기존에 로컬에서 쌓아둔 DB
(`%LOCALAPPDATA%\LolHelperBot\lol-helper.db`)를 이어서 쓰고 싶다면, VM으로 파일을 복사한
뒤(`scp -i "$HOME\.ssh\azbot_vultr" lol-helper.db root@IP:~/`) 아래처럼 볼륨 안에 넣어줍니다:

```bash
docker run --rm -v azbot-data:/data -v ~/:/host alpine cp /host/lol-helper.db /data/lol-helper.db
```

### 5-8. VM 재부팅돼도 자동으로 다시 뜨는지 확인 (선택)

```bash
sudo reboot
```

몇 분 후 다시 SSH 접속해서 `docker ps`로 `azbot`이 다시 떠 있는지 확인합니다
(`--restart unless-stopped` 덕분에 Docker 데몬이 뜨면 컨테이너도 자동으로 같이 뜹니다).

## 6. 업데이트(재배포) — 로컬에서 고치고 VM에 반영하기

로컬 흐름은 지금까지 하던 대로입니다: 코드 수정 → `dotnet run`으로 테스트 → 문제없으면
`git push`(→ Actions가 자동으로 새 이미지를 빌드해서 GHCR에 올려줌, 3~5분 소요). VM에
반영하는 건 **`git pull`도 `docker build`도 없이** 이미지만 새로 받으면 끝입니다(볼륨은 그대로
재사용되므로 **DB는 안 날아갑니다**):

```bash
ssh -i "$HOME\.ssh\azbot_vultr" root@123.45.67.89   # VM 접속

docker pull ghcr.io/jhjh1003/azbot:latest
docker stop azbot && docker rm azbot
docker run -d --name azbot --restart unless-stopped -v azbot-data:/data --env-file .env ghcr.io/jhjh1003/azbot:latest
```

자주 쓸 것 같으면 VM에 아래처럼 스크립트로 저장해두고 `./redeploy.sh` 한 줄로 끝낼 수도
있습니다(단, `git push` 후 Actions 빌드가 끝날 때까지 3~5분 기다렸다가 실행해야 최신 이미지를
받습니다 — GitHub 저장소 Actions 탭에서 초록 체크 확인):

```bash
cat > redeploy.sh <<'EOF'
#!/bin/bash
set -e
docker pull ghcr.io/jhjh1003/azbot:latest
docker stop azbot || true
docker rm azbot || true
docker run -d --name azbot --restart unless-stopped -v azbot-data:/data --env-file .env ghcr.io/jhjh1003/azbot:latest
echo "재배포 완료. 로그: docker logs -f azbot"
EOF
chmod +x redeploy.sh
```

## 7. 앞으로 고려할 것

- **DB를 Postgres(Supabase 등)로 옮기기**: 지금 SQLite는 이 규모에서 충분하지만, 나중에
  백업/이중화가 필요해지면 `MatchRepository`/`MemberRepository`를 Postgres로 바꾸는 걸
  검토할 수 있습니다(지금은 필요성 낮음, AfterUpgrade.md 참고).
- **redeploy.sh를 Actions 완료 알림에 맞춰 자동 실행**: 지금은 Actions 빌드가 끝났는지
  VM에서 수동으로 확인 후 `./redeploy.sh`를 돌려야 합니다. VM이 주기적으로(예: cron) 새 이미지
  태그가 있는지 확인해서 자동으로 pull+재시작하게 만들 수도 있습니다(당장은 배포 빈도가 낮아
  불필요, 필요해지면 검토).

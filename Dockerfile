# [AI-GENERATED] 이 파일은 AI(Claude)가 생성했습니다.
# Source: Claude (Claude Code / Cowork)
# Date: 2026-08-20
# Reviewer: (박정훈)
# Review: 로컬 PC에서만 돌아가던 봇을 클라우드(Oracle Cloud 등)로 옮기기 위한 컨테이너 이미지.
# 빌드/실행 방법은 저장소 루트의 DEPLOY.md 참고. 이 Dockerfile은 로컬에 Docker가 없는 환경에서
# 작성돼 실제 빌드 테스트는 못 했습니다 — 배포 전에 DEPLOY.md의 로컬 테스트 절차를 꼭 먼저
# 실행해서 확인해 주세요.

# ---- 1단계: 빌드 ----
# SDK 이미지는 무겁지만(컴파일러 포함) 빌드에만 쓰고, 최종 이미지에는 안 들어갑니다.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# csproj만 먼저 복사해서 종속성 복원(restore)을 캐싱합니다.
# 소스 코드(.cs)만 바뀌고 csproj가 그대로면, 이 레이어는 다시 안 돌고 캐시를 씁니다.
COPY LolHelperBot/LolHelperBot.csproj LolHelperBot/
RUN dotnet restore LolHelperBot/LolHelperBot.csproj

# 나머지 소스 전체 복사 후 릴리즈 빌드로 게시(publish).
COPY LolHelperBot/ LolHelperBot/
RUN dotnet publish LolHelperBot/LolHelperBot.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ---- 2단계: 실행 ----
# runtime 이미지는 SDK(컴파일러) 없이 실행에 필요한 것만 있어서 훨씬 가볍습니다.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

# SQLite DB 파일을 여기에 저장합니다. 컨테이너를 지우고 다시 만들어도 데이터가 남아있으려면
# 이 경로를 반드시 볼륨(-v 또는 docker-compose volumes)으로 마운트해야 합니다.
# 예: docker run -v azbot-data:/data ...
ENV Storage__DatabasePath=/data/lol-helper.db
VOLUME ["/data"]

COPY --from=build /app/publish .

# Discord__Token / Discord__GuildId / Riot__ApiKey 는 이미지에 안 들어있습니다 —
# 실행할 때 -e 옵션이나 .env 파일로 반드시 주입해야 합니다. DEPLOY.md 참고.
ENTRYPOINT ["dotnet", "LolHelperBot.dll"]

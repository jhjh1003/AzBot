// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-18
// Reviewer: (박정훈)
// Review: 로컬 스모크 테스트 예정 - Discord 봇 연결 + Riot API 연결 확인용 최소 구현
// Ref: 정훈새하프로젝트 협업개발 지침 v1.1 (R1 자격증명 보호, R8 운영 환경 보호)

using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using LolHelperBot.Services;
using LolHelperBot.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// Windows 콘솔 기본 코드페이지(CP949)로는 한글 로그가 깨져서 콘솔 실험 도구(timeline-raw 등)
// 결과를 파일로 리다이렉트했을 때 못 읽는 문제가 있었음 — UTF-8로 고정. Discord 자체 통신에는
// 영향 없음(Discord.Net은 별도로 UTF-8 사용).
Console.OutputEncoding = System.Text.Encoding.UTF8;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    // user-secrets(로컬 개발용, git 추적 안 됨) → 환경변수 순으로 appsettings.json 값을 덮어씁니다.
    // 실제 토큰/키는 절대 appsettings.json에 커밋하지 말고 user-secrets나 환경변수로만 주입하세요. (README 참고)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

var discordToken = configuration["Discord:Token"];
var guildIdRaw = configuration["Discord:GuildId"];
var riotApiKey = configuration["Riot:ApiKey"];
var riotRegion = configuration["Riot:Region"] ?? "kr";
var riotAccountRegion = configuration["Riot:AccountRegion"] ?? "asia";

if (string.IsNullOrWhiteSpace(discordToken))
{
    Console.Error.WriteLine("[설정 오류] Discord 봇 토큰이 없습니다. 환경변수 Discord__Token 을 설정하세요.");
    Environment.ExitCode = 1;
    return;
}

if (string.IsNullOrWhiteSpace(guildIdRaw) || !ulong.TryParse(guildIdRaw, out var guildId))
{
    Console.Error.WriteLine("[설정 오류] 테스트용 서버(길드) ID가 없습니다. 환경변수 Discord__GuildId 를 설정하세요. " +
        "(디스코드에서 개발자 모드 켜고 서버 아이콘 우클릭 → ID 복사)");
    Environment.ExitCode = 1;
    return;
}

if (string.IsNullOrWhiteSpace(riotApiKey))
{
    Console.Error.WriteLine("[안내] Riot__ApiKey 가 없어서 /riotcheck 명령은 동작하지 않습니다. /ping 은 정상 동작합니다.");
}

// 슬래시 커맨드만 쓰므로 Privileged Gateway Intent(MessageContent 등)는 켜지 않습니다.
var socketConfig = new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.Guilds,
};

var client = new DiscordSocketClient(socketConfig);
RiotApiClient riotApiClient;
try
{
    riotApiClient = new RiotApiClient(riotApiKey ?? string.Empty, riotRegion, riotAccountRegion);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"[설정 오류] {ex.Message}");
    Environment.ExitCode = 1;
    return;
}

var databasePath = configuration["Storage:DatabasePath"];
if (string.IsNullOrWhiteSpace(databasePath))
{
    databasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LolHelperBot",
        "lol-helper.db");
}

var memberRepository = new MemberRepository(databasePath);
await memberRepository.InitializeAsync();
Console.WriteLine($"[데이터베이스 준비] {memberRepository.DatabasePath}");

var matchRepository = new MatchRepository(databasePath);
await matchRepository.InitializeAsync();

// 기여도 점수 가중치(/아재전적, /명예의전당) — 이 txt 파일만 고치면 코드 수정 없이 튜닝됩니다.
var contributionWeightsPath = Path.Combine(AppContext.BaseDirectory, "Config", "ContributionScoreWeights.txt");
var contributionScoreCalculator = new ContributionScoreCalculator(contributionWeightsPath);

// /밴픽추천 2단계 — op.gg 기준 일반 메타 티어/카운터픽 수동 스냅샷 (Config/MetaTierSnapshot.README.md 참고).
var metaTierSnapshotPath = Path.Combine(AppContext.BaseDirectory, "Config", "MetaTierSnapshot.json");
var metaTierRepository = new MetaTierRepository(metaTierSnapshotPath);

// 리팩토링 2단계 — /밴픽추천 계산 로직을 서비스로 분리(Modules는 "서비스 호출 → Embed 변환"만 담당).
var banPickRecommendationService = new BanPickRecommendationService(matchRepository, metaTierRepository);

// 리팩토링 2단계 — /티어픽 계산 로직도 같은 패턴으로 서비스 분리.
var championTierService = new ChampionTierService(matchRepository);

// AfterUpgrade.md 1단계 실험 전용 진입점: `dotnet run -- timeline-test [매치수]`
// Discord 봇은 켜지 않고, 저장된 최근 클랜 매치에 대해 Timeline API를 찍어보고 콘솔에만 출력합니다.
if (args.Length > 0 && args[0] == "timeline-test")
{
    var timelineTestCount = args.Length > 1 && int.TryParse(args[1], out var parsedCount) ? parsedCount : 10;
    await TimelineExperiment.RunAsync(riotApiClient, matchRepository, guildId, timelineTestCount);
    return;
}

// 기여도 점수 v4(15분 라인전/후반 분리) 검증용 1회성 실험: `dotnet run -- v4-test [매치수]`
if (args.Length > 0 && args[0] == "v4-test")
{
    var v4TestCount = args.Length > 1 && int.TryParse(args[1], out var parsedV4Count) ? parsedV4Count : 4;
    await ContributionScoreV4Experiment.RunAsync(riotApiClient, matchRepository, guildId, v4TestCount);
    return;
}

// 기여도 v4.0.0 백필: `dotnet run -- v4-backfill [연월]` (생략하면 이번 달)
if (args.Length > 0 && args[0] == "v4-backfill")
{
    var backfillYearMonth = args.Length > 1 ? args[1] : null;
    await ContributionV4Backfill.RunAsync(riotApiClient, matchRepository, guildId, backfillYearMonth);
    return;
}

// 케이틀린 빌드별 승률 1회성 조회: `dotnet run -- caitlyn-build <닉네임일부>`
if (args.Length > 1 && args[0] == "caitlyn-build")
{
    await CaitlynBuildExperiment.RunAsync(riotApiKey ?? string.Empty, riotAccountRegion, memberRepository.DatabasePath, args[1]);
    return;
}

// 픽창 선픽/후픽 순서 확인용 1회성 실험: `dotnet run -- match-raw <matchId>`
if (args.Length > 1 && args[0] == "match-raw")
{
    await MatchRawDumpExperiment.RunAsync(riotApiKey ?? string.Empty, riotAccountRegion, args[1]);
    return;
}

// op.gg 시간별 OP스코어 그래프 비교용 1회성 실험: `dotnet run -- timeline-raw <matchId>`
if (args.Length > 1 && args[0] == "timeline-raw")
{
    await TimelineRawDumpExperiment.RunAsync(riotApiKey ?? string.Empty, riotAccountRegion, args[1]);
    return;
}

// /밴픽추천 2단계 스모크 테스트: `dotnet run -- banpick-test [라인]` (Riot API 호출 없이 DB만 조회).
if (args.Length > 0 && args[0] == "banpick-test")
{
    var banPickTestPosition = args.Length > 1 ? args[1] : null;
    await BanPickQueryExperiment.RunAsync(matchRepository, metaTierRepository, banPickRecommendationService, guildId, banPickTestPosition);
    return;
}

// /티어픽 리팩토링 스모크 테스트: `dotnet run -- tier-test [라인]`.
if (args.Length > 0 && args[0] == "tier-test")
{
    var tierTestPosition = args.Length > 1 ? args[1] : null;
    await ChampionTierQueryExperiment.RunAsync(matchRepository, championTierService, databasePath, guildId, tierTestPosition);
    return;
}

var services = new ServiceCollection()
    .AddSingleton(client)
    .AddSingleton(riotApiClient)
    .AddSingleton(memberRepository)
    .AddSingleton(matchRepository)
    .AddSingleton(contributionScoreCalculator)
    .AddSingleton(metaTierRepository)
    .AddSingleton(banPickRecommendationService)
    .AddSingleton(championTierService)
    .AddSingleton<InteractionService>(sp => new InteractionService(sp.GetRequiredService<DiscordSocketClient>()))
    .BuildServiceProvider();

var interactionService = services.GetRequiredService<InteractionService>();

client.Log += LogAsync;
interactionService.Log += LogAsync;

client.Ready += async () =>
{
    // 길드(서버) 단위로 등록하면 즉시 반영됩니다 (개발 중 테스트용).
    // 나중에 실제 배포 시에는 전역 등록(RegisterCommandsGloballyAsync)으로 바꾸세요.
    await interactionService.RegisterCommandsToGuildAsync(guildId, deleteMissing: true);
    Console.WriteLine($"[준비 완료] 길드 {guildId} 에 슬래시 커맨드를 등록했습니다.");
};

client.InteractionCreated += async interaction =>
{
    var ctx = new SocketInteractionContext(client, interaction);
    var result = await interactionService.ExecuteCommandAsync(ctx, services);

    if (!result.IsSuccess)
    {
        Console.Error.WriteLine($"[명령 실행 오류] {result.Error}: {result.ErrorReason}");

        const string errorMessage = "명령을 처리하는 중 오류가 발생했습니다. 잠시 후 다시 시도해 주세요.";
        if (interaction.HasResponded)
        {
            await interaction.FollowupAsync(errorMessage, ephemeral: true);
        }
        else
        {
            await interaction.RespondAsync(errorMessage, ephemeral: true);
        }
    }
};

await interactionService.AddModulesAsync(typeof(Program).Assembly, services);

await client.LoginAsync(TokenType.Bot, discordToken);
await client.StartAsync();

Console.WriteLine("봇을 시작했습니다. 종료하려면 Ctrl+C 를 누르세요.");
await Task.Delay(Timeout.Infinite);

Task LogAsync(LogMessage message)
{
    Console.WriteLine(message.ToString());
    return Task.CompletedTask;
}

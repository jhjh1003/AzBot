// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-18
// Reviewer: (박정훈)
// Review: 로컬 스모크 테스트 예정 - Riot API 키가 정상 동작하는지 확인용 최소 클라이언트.
// 소환사 정보 없이도 호출 가능한 lol/status 엔드포인트만 사용합니다(스모크 테스트 목적).
// 매치 데이터 수집(카운터픽/듀오 시너지 집계)은 다음 단계에서 별도 서비스로 추가합니다.

using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using static LolHelperBot.Services.RiotIdParser;

namespace LolHelperBot.Services;

public class RiotApiClient
{
    private static readonly Regex RegionPattern = new("^[a-z0-9]+$", RegexOptions.CultureInvariant);

    private readonly HttpClient _platformHttpClient;
    private readonly HttpClient _accountHttpClient;
    private readonly string _region;
    private readonly bool _hasApiKey;

    public RiotApiClient(string apiKey, string region, string accountRegion = "asia")
    {
        _region = string.IsNullOrWhiteSpace(region) ? "kr" : region.Trim().ToLowerInvariant();
        if (!RegionPattern.IsMatch(_region))
        {
            throw new ArgumentException("Riot 리전은 영문 소문자와 숫자만 사용할 수 있습니다.");
        }

        var normalizedAccountRegion = string.IsNullOrWhiteSpace(accountRegion)
            ? "asia"
            : accountRegion.Trim().ToLowerInvariant();
        if (!RegionPattern.IsMatch(normalizedAccountRegion))
        {
            throw new ArgumentException("Riot Account 리전은 영문 소문자와 숫자만 사용할 수 있습니다.");
        }

        _hasApiKey = !string.IsNullOrWhiteSpace(apiKey);

        // HttpClient는 애플리케이션 생명주기 동안 재사용합니다 (소켓 고갈 방지, 하네스 지침 참고).
        _platformHttpClient = CreateHttpClient($"https://{_region}.api.riotgames.com", apiKey);
        _accountHttpClient = CreateHttpClient($"https://{normalizedAccountRegion}.api.riotgames.com", apiKey);
    }

    private static HttpClient CreateHttpClient(string baseAddress, string apiKey)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(baseAddress),
            Timeout = TimeSpan.FromSeconds(10),
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            client.DefaultRequestHeaders.Add("X-Riot-Token", apiKey);
        }

        return client;
    }

    /// <summary>
    /// lol/status/v4/platform-data 는 소환사 정보 없이도 호출할 수 있어서
    /// API 키/리전 설정이 제대로 되어 있는지 확인하는 스모크 테스트에 적합합니다.
    /// </summary>
    public async Task<PlatformStatusResult> CheckPlatformStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!_hasApiKey)
        {
            return PlatformStatusResult.Failure("Riot API 키가 설정되지 않았습니다 (환경변수 Riot__ApiKey 확인).");
        }

        try
        {
            using var response = await _platformHttpClient.GetAsync("/lol/status/v4/platform-data", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return PlatformStatusResult.Failure(
                    $"Riot API 응답 실패: {(int)response.StatusCode} {response.ReasonPhrase}. " +
                    "24시간 테스트 키는 만료되면 재발급이 필요합니다.");
            }

            var data = await response.Content.ReadFromJsonAsync<RiotPlatformDataResponse>(cancellationToken: cancellationToken);
            var incidentCount = data?.Incidents?.Count ?? 0;
            var maintenanceCount = data?.Maintenances?.Count ?? 0;

            return PlatformStatusResult.Success(_region, incidentCount, maintenanceCount);
        }
        catch (Exception ex)
        {
            // 내부 예외 상세는 콘솔 로그에만 남기고, 사용자에게는 정제된 메시지만 전달합니다 (하네스 지침 R5).
            Console.Error.WriteLine($"[Riot API 호출 오류] {ex}");
            return PlatformStatusResult.Failure("Riot API 호출 중 오류가 발생했습니다. 콘솔 로그를 확인하세요.");
        }
    }

    public async Task<RiotAccountLookupResult> FindLeagueAccountAsync(
        string riotId,
        CancellationToken cancellationToken = default)
    {
        if (!_hasApiKey)
        {
            return RiotAccountLookupResult.Failure("Riot API 키가 설정되지 않았습니다.");
        }

        if (!TryParseRiotId(riotId, out var gameName, out var tagLine))
        {
            return RiotAccountLookupResult.Failure("롤 아이디를 `게임이름#태그` 형식으로 입력해 주세요. 예: Hide on bush#KR1");
        }

        try
        {
            var accountPath = $"/riot/account/v1/accounts/by-riot-id/{Uri.EscapeDataString(gameName)}/{Uri.EscapeDataString(tagLine)}";
            using var accountResponse = await _accountHttpClient.GetAsync(accountPath, cancellationToken);

            if (accountResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return RiotAccountLookupResult.Failure("해당 Riot ID를 찾을 수 없습니다. 게임 이름과 태그를 확인해 주세요.");
            }

            if (!accountResponse.IsSuccessStatusCode)
            {
                return RiotAccountLookupResult.Failure(GetApiFailureReason(accountResponse));
            }

            var account = await accountResponse.Content.ReadFromJsonAsync<RiotAccountResponse>(
                cancellationToken: cancellationToken);
            if (account is null || string.IsNullOrWhiteSpace(account.Puuid))
            {
                return RiotAccountLookupResult.Failure("Riot 계정 응답을 읽지 못했습니다.");
            }

            var summonerPath = $"/lol/summoner/v4/summoners/by-puuid/{Uri.EscapeDataString(account.Puuid)}";
            using var summonerResponse = await _platformHttpClient.GetAsync(summonerPath, cancellationToken);

            if (summonerResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return RiotAccountLookupResult.Failure($"{_region.ToUpperInvariant()} 리전의 LoL 계정을 찾을 수 없습니다.");
            }

            if (!summonerResponse.IsSuccessStatusCode)
            {
                return RiotAccountLookupResult.Failure(GetApiFailureReason(summonerResponse));
            }

            return RiotAccountLookupResult.Success(
                account.GameName ?? gameName,
                account.TagLine ?? tagLine,
                account.Puuid,
                _region);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Riot 계정 조회 오류] {ex}");
            return RiotAccountLookupResult.Failure("Riot 계정 조회 중 오류가 발생했습니다. 콘솔 로그를 확인하세요.");
        }
    }

    /// <summary>
    /// 클랜 전적 배치 수집용 — 매치ID 목록만 가볍게 조회합니다 (상세 조회는 GetFullMatchAsync 별도 호출).
    /// </summary>
    public async Task<MatchIdListResult> GetMatchIdsAsync(
        string puuid,
        int queueId,
        int start = 0,
        int count = 20,
        CancellationToken cancellationToken = default)
    {
        if (!_hasApiKey)
        {
            return MatchIdListResult.Failure("Riot API 키가 설정되지 않았습니다.");
        }

        start = Math.Max(0, start);
        count = Math.Clamp(count, 1, 100);

        try
        {
            var listPath = $"/lol/match/v5/matches/by-puuid/{Uri.EscapeDataString(puuid)}/ids" +
                $"?queue={queueId}&start={start}&count={count}";
            using var listResponse = await _accountHttpClient.GetAsync(listPath, cancellationToken);
            if (!listResponse.IsSuccessStatusCode)
            {
                return MatchIdListResult.Failure(GetApiFailureReason(listResponse));
            }

            var matchIds = await listResponse.Content.ReadFromJsonAsync<List<string>>(
                cancellationToken: cancellationToken) ?? [];
            return MatchIdListResult.Success(matchIds);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Riot 매치ID 조회 오류] {ex}");
            return MatchIdListResult.Failure("매치 ID 조회 중 오류가 발생했습니다. 콘솔 로그를 확인하세요.");
        }
    }

    /// <summary>
    /// 클랜 전적 배치 수집용 — 매치 하나의 전체 참가자(10명) 상세를 조회합니다.
    /// </summary>
    public async Task<FullMatchResult> GetFullMatchAsync(
        string matchId,
        CancellationToken cancellationToken = default)
    {
        if (!_hasApiKey)
        {
            return FullMatchResult.Failure("Riot API 키가 설정되지 않았습니다.");
        }

        try
        {
            using var matchResponse = await _accountHttpClient.GetAsync(
                $"/lol/match/v5/matches/{Uri.EscapeDataString(matchId)}",
                cancellationToken);
            if (!matchResponse.IsSuccessStatusCode)
            {
                return FullMatchResult.Failure(GetApiFailureReason(matchResponse));
            }

            var match = await matchResponse.Content.ReadFromJsonAsync<MatchResponse>(
                cancellationToken: cancellationToken);
            if (match?.Info?.Participants is null)
            {
                return FullMatchResult.Failure("매치 데이터를 읽지 못했습니다.");
            }

            var participants = match.Info.Participants
                .Where(participant => participant.Puuid is not null)
                .Select(participant => new FullMatchParticipant(
                    participant.Puuid!,
                    participant.TeamId,
                    participant.Win,
                    participant.ChampionName ?? "Unknown",
                    participant.TeamPosition ?? string.Empty,
                    participant.Kills,
                    participant.Deaths,
                    participant.Assists,
                    participant.TotalMinionsKilled + participant.NeutralMinionsKilled,
                    participant.RiotIdGameName,
                    participant.RiotIdTagLine,
                    participant.DamageDealtToChampions,
                    participant.DamageTaken,
                    participant.DamageSelfMitigated,
                    participant.GoldEarned,
                    participant.VisionScore,
                    participant.TimeCCingOthers,
                    participant.TotalHealsOnTeammates,
                    participant.WardsPlaced,
                    participant.DamageDealtToObjectives,
                    participant.DamageShieldedOnTeammates))
                .ToList();

            return FullMatchResult.Success(new FullMatchDetail(
                matchId,
                match.Info.QueueId,
                match.Info.GameDuration,
                DateTimeOffset.FromUnixTimeMilliseconds(match.Info.GameCreation),
                participants));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Riot 매치 상세 조회 오류] {ex}");
            return FullMatchResult.Failure("매치 상세 조회 중 오류가 발생했습니다. 콘솔 로그를 확인하세요.");
        }
    }

    /// <summary>
    /// 15분 라인전/후반 분리 및 골드 스윙 분석 실험용 — Match-V5 Timeline API를 호출합니다.
    /// 매치 상세(GetFullMatchAsync)와 별개의 호출이라 API 콜 수가 늘어나므로, 실제 기능으로 굳히기 전
    /// TimelineExperiment로 소량 매치만 찍어보는 용도로 우선 사용합니다.
    /// </summary>
    public async Task<TimelineResult> GetTimelineAsync(
        string matchId,
        CancellationToken cancellationToken = default)
    {
        if (!_hasApiKey)
        {
            return TimelineResult.Failure("Riot API 키가 설정되지 않았습니다.");
        }

        try
        {
            using var response = await _accountHttpClient.GetAsync(
                $"/lol/match/v5/matches/{Uri.EscapeDataString(matchId)}/timeline",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return TimelineResult.Failure(GetApiFailureReason(response));
            }

            var timeline = await response.Content.ReadFromJsonAsync<TimelineResponse>(
                cancellationToken: cancellationToken);
            if (timeline?.Info?.Frames is null)
            {
                return TimelineResult.Failure("타임라인 데이터를 읽지 못했습니다.");
            }

            var frames = timeline.Info.Frames
                .Select(frame => new TimelineFrame(
                    frame.Timestamp,
                    (frame.ParticipantFrames ?? new Dictionary<string, TimelineParticipantFrameResponse>())
                        .Where(pair => int.TryParse(pair.Key, out _))
                        .ToDictionary(
                            pair => int.Parse(pair.Key),
                            pair => new TimelineParticipantFrame(
                                pair.Value.ParticipantId,
                                pair.Value.TotalGold,
                                pair.Value.Xp,
                                pair.Value.MinionsKilled + pair.Value.JungleMinionsKilled,
                                pair.Value.DamageStats?.TotalDamageDoneToChampions ?? 0,
                                pair.Value.DamageStats?.TotalDamageTaken ?? 0,
                                pair.Value.TimeEnemySpentControlled,
                                pair.Value.Position?.X ?? 0,
                                pair.Value.Position?.Y ?? 0))))
                .ToList();

            var allEvents = timeline.Info.Frames.SelectMany(frame => frame.Events ?? []).ToList();

            var kills = allEvents
                .Where(evt => evt.Type == "CHAMPION_KILL")
                .Select(evt => new TimelineKillEvent(
                    evt.Timestamp,
                    evt.KillerId,
                    evt.VictimId,
                    evt.AssistingParticipantIds ?? []))
                .ToList();

            // v4 실험(15분 라인전/후반 분리)용 — 시야점수·오브젝트딜량은 프레임에 없어서, 와드 설치
            // 이벤트 수 / 오브젝트 처치 관여 이벤트로 근사(proxy)합니다. 정확한 값이 아니라 "근사치"입니다.
            var wardsPlaced = allEvents
                .Where(evt => evt.Type == "WARD_PLACED" && evt.CreatorId is not null)
                .Select(evt => new TimelineParticipantEvent(evt.Timestamp, evt.CreatorId!.Value))
                .ToList();

            var objectiveKills = allEvents
                .Where(evt => (evt.Type == "ELITE_MONSTER_KILL" || evt.Type == "BUILDING_KILL") && evt.KillerId is not null)
                .Select(evt => new TimelineParticipantEvent(evt.Timestamp, evt.KillerId!.Value))
                .ToList();

            // v4.0.0 "오브젝트 몬스터 종류별 보너스"용 — 막타 친 사람 + 몬스터 종류(DRAGON/HORDE=
            // 유충/BARON_NASHOR/RIFTHERALD 등)를 따로 남깁니다. 어시스트 목록은 없어서(킬러만) 팀
            // 전체 관여도는 여전히 damageDealtToObjectives가 더 정확합니다.
            var eliteMonsterKills = allEvents
                .Where(evt => evt.Type == "ELITE_MONSTER_KILL" && evt.KillerId is not null)
                .Select(evt => new TimelineMonsterKillEvent(evt.Timestamp, evt.KillerId!.Value, evt.MonsterType ?? "UNKNOWN"))
                .ToList();

            return TimelineResult.Success(new TimelineDetail(
                matchId,
                timeline.Info.FrameInterval,
                frames,
                kills,
                wardsPlaced,
                objectiveKills,
                eliteMonsterKills));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Riot 타임라인 조회 오류] {ex}");
            return TimelineResult.Failure("타임라인 조회 중 오류가 발생했습니다. 콘솔 로그를 확인하세요.");
        }
    }

    // TryParseRiotId는 2026-08-20 리팩토링 1단계에서 RiotIdParser.cs로 이관됨
    // (AtoZModule.cs/ClanStatsModule.cs에 있던 완전히 동일한 복붙 코드와 통합). 아래 using static 참고.

    private static string GetApiFailureReason(HttpResponseMessage response) => response.StatusCode switch
    {
        System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
            "Riot API 키가 유효하지 않거나 만료되었습니다.",
        System.Net.HttpStatusCode.TooManyRequests =>
            "Riot API 요청 한도를 초과했습니다. 잠시 후 다시 시도해 주세요.",
        _ => $"Riot API 응답 실패: {(int)response.StatusCode} {response.ReasonPhrase}",
    };

    private class RiotPlatformDataResponse
    {
        [JsonPropertyName("incidents")]
        public List<object>? Incidents { get; set; }

        [JsonPropertyName("maintenances")]
        public List<object>? Maintenances { get; set; }
    }

    private class RiotAccountResponse
    {
        [JsonPropertyName("puuid")]
        public string? Puuid { get; set; }

        [JsonPropertyName("gameName")]
        public string? GameName { get; set; }

        [JsonPropertyName("tagLine")]
        public string? TagLine { get; set; }
    }

    private class MatchResponse
    {
        [JsonPropertyName("info")]
        public MatchInfoResponse? Info { get; set; }
    }

    private class MatchInfoResponse
    {
        [JsonPropertyName("gameCreation")]
        public long GameCreation { get; set; }

        [JsonPropertyName("gameDuration")]
        public long GameDuration { get; set; }

        [JsonPropertyName("queueId")]
        public int QueueId { get; set; }

        [JsonPropertyName("participants")]
        public List<MatchParticipantResponse>? Participants { get; set; }
    }

    private class MatchParticipantResponse
    {
        [JsonPropertyName("puuid")]
        public string? Puuid { get; set; }

        [JsonPropertyName("win")]
        public bool Win { get; set; }

        [JsonPropertyName("teamId")]
        public int TeamId { get; set; }

        [JsonPropertyName("championName")]
        public string? ChampionName { get; set; }

        [JsonPropertyName("kills")]
        public int Kills { get; set; }

        [JsonPropertyName("deaths")]
        public int Deaths { get; set; }

        [JsonPropertyName("assists")]
        public int Assists { get; set; }

        [JsonPropertyName("teamPosition")]
        public string? TeamPosition { get; set; }

        [JsonPropertyName("totalMinionsKilled")]
        public int TotalMinionsKilled { get; set; }

        [JsonPropertyName("neutralMinionsKilled")]
        public int NeutralMinionsKilled { get; set; }

        [JsonPropertyName("riotIdGameName")]
        public string? RiotIdGameName { get; set; }

        [JsonPropertyName("riotIdTagline")]
        public string? RiotIdTagLine { get; set; }

        // 기여도 점수(/아재전적, /명예의전당)용 지표. 전부 "그 판 우리 팀 5명끼리" 상대 비교에만 씁니다.
        [JsonPropertyName("totalDamageDealtToChampions")]
        public long DamageDealtToChampions { get; set; }

        [JsonPropertyName("totalDamageTaken")]
        public long DamageTaken { get; set; }

        [JsonPropertyName("damageSelfMitigated")]
        public long DamageSelfMitigated { get; set; }

        [JsonPropertyName("goldEarned")]
        public long GoldEarned { get; set; }

        [JsonPropertyName("visionScore")]
        public int VisionScore { get; set; }

        [JsonPropertyName("timeCCingOthers")]
        public int TimeCCingOthers { get; set; }

        [JsonPropertyName("totalHealsOnTeammates")]
        public long TotalHealsOnTeammates { get; set; }

        [JsonPropertyName("wardsPlaced")]
        public int WardsPlaced { get; set; }

        [JsonPropertyName("damageDealtToObjectives")]
        public long DamageDealtToObjectives { get; set; }

        // v4.0.0 서폿 후반 "힐+보호막" 복합 지표용으로 2026-08-21 추가.
        [JsonPropertyName("totalDamageShieldedOnTeammates")]
        public long DamageShieldedOnTeammates { get; set; }
    }

    private class TimelineResponse
    {
        [JsonPropertyName("info")]
        public TimelineInfoResponse? Info { get; set; }
    }

    private class TimelineInfoResponse
    {
        [JsonPropertyName("frameInterval")]
        public int FrameInterval { get; set; }

        [JsonPropertyName("frames")]
        public List<TimelineFrameResponse>? Frames { get; set; }
    }

    private class TimelineFrameResponse
    {
        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }

        [JsonPropertyName("participantFrames")]
        public Dictionary<string, TimelineParticipantFrameResponse>? ParticipantFrames { get; set; }

        [JsonPropertyName("events")]
        public List<TimelineEventResponse>? Events { get; set; }
    }

    private class TimelineParticipantFrameResponse
    {
        [JsonPropertyName("participantId")]
        public int ParticipantId { get; set; }

        [JsonPropertyName("totalGold")]
        public long TotalGold { get; set; }

        [JsonPropertyName("xp")]
        public long Xp { get; set; }

        [JsonPropertyName("minionsKilled")]
        public int MinionsKilled { get; set; }

        [JsonPropertyName("jungleMinionsKilled")]
        public int JungleMinionsKilled { get; set; }

        // v4 실험(15분 라인전/후반 분리)용으로 2026-08-21 추가.
        [JsonPropertyName("timeEnemySpentControlled")]
        public int TimeEnemySpentControlled { get; set; }

        [JsonPropertyName("damageStats")]
        public TimelineDamageStatsResponse? DamageStats { get; set; }

        // v4.0.0 로밍/다른라인기여도 근사치용(2026-08-21) — 맵 좌표.
        [JsonPropertyName("position")]
        public TimelinePositionResponse? Position { get; set; }
    }

    private class TimelinePositionResponse
    {
        [JsonPropertyName("x")]
        public int X { get; set; }

        [JsonPropertyName("y")]
        public int Y { get; set; }
    }

    private class TimelineDamageStatsResponse
    {
        [JsonPropertyName("totalDamageDoneToChampions")]
        public long TotalDamageDoneToChampions { get; set; }

        [JsonPropertyName("totalDamageTaken")]
        public long TotalDamageTaken { get; set; }
    }

    private class TimelineEventResponse
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }

        [JsonPropertyName("killerId")]
        public int? KillerId { get; set; }

        [JsonPropertyName("victimId")]
        public int VictimId { get; set; }

        [JsonPropertyName("assistingParticipantIds")]
        public List<int>? AssistingParticipantIds { get; set; }

        // WARD_PLACED 이벤트의 설치자 — v4 실험에서 시야 근사치(와드 개수)로 사용.
        [JsonPropertyName("creatorId")]
        public int? CreatorId { get; set; }

        // ELITE_MONSTER_KILL의 몬스터 종류(DRAGON/HORDE=유충/BARON_NASHOR/RIFTHERALD 등) —
        // v4.0.0 오브젝트 보너스용으로 2026-08-21 추가.
        [JsonPropertyName("monsterType")]
        public string? MonsterType { get; set; }
    }
}

public record PlatformStatusResult(bool IsSuccess, string Message)
{
    public static PlatformStatusResult Success(string region, int incidentCount, int maintenanceCount) =>
        new(true, $"✅ Riot API 연결 성공 (리전: {region.ToUpperInvariant()}) — 진행 중 이슈 {incidentCount}건, 점검 {maintenanceCount}건.");

    public static PlatformStatusResult Failure(string reason) =>
        new(false, $"❌ Riot API 연결 실패 — {reason}");
}

public record RiotAccountLookupResult(
    bool IsSuccess,
    string Message,
    string? GameName = null,
    string? TagLine = null,
    string? Puuid = null,
    string? Region = null)
{
    public static RiotAccountLookupResult Success(string gameName, string tagLine, string puuid, string region) =>
        new(true, "Riot 계정을 확인했습니다.", gameName, tagLine, puuid, region);

    public static RiotAccountLookupResult Failure(string reason) => new(false, reason);
}

public record MatchIdListResult(bool IsSuccess, string Message, IReadOnlyList<string>? MatchIds = null)
{
    public static MatchIdListResult Success(IReadOnlyList<string> matchIds) =>
        new(true, "매치 ID를 조회했습니다.", matchIds);

    public static MatchIdListResult Failure(string reason) => new(false, reason);
}

public record FullMatchParticipant(
    string Puuid,
    int TeamId,
    bool Win,
    string ChampionName,
    string TeamPosition,
    int Kills,
    int Deaths,
    int Assists,
    int CreepScore,
    string? RiotGameName = null,
    string? RiotTagLine = null,
    long? DamageDealt = null,
    long? DamageTaken = null,
    long? DamageMitigated = null,
    long? GoldEarned = null,
    int? VisionScore = null,
    int? CcTimeDealt = null,
    long? HealAmount = null,
    int? WardsPlaced = null,
    long? DamageToObjectives = null,
    long? DamageShieldedOnTeammates = null);

public record FullMatchDetail(
    string MatchId,
    int QueueId,
    long GameDurationSeconds,
    DateTimeOffset GameCreatedAt,
    IReadOnlyList<FullMatchParticipant> Participants);

public record FullMatchResult(bool IsSuccess, string Message, FullMatchDetail? Match = null)
{
    public static FullMatchResult Success(FullMatchDetail match) =>
        new(true, "매치 상세를 조회했습니다.", match);

    public static FullMatchResult Failure(string reason) => new(false, reason);
}

// 15분 라인전/후반 분리, 골드 스윙 분석 실험용 (TimelineExperiment/ContributionScoreV4Experiment 전용
// — AfterUpgrade.md 1단계 실험. DamageDealtToChampions/DamageTaken/CcTimeDealtCumulative는 2026-08-21
// v4 실험을 위해 추가됨 — 전부 그 프레임 시점까지의 "누적치"입니다).
public record TimelineParticipantFrame(
    int ParticipantId,
    long TotalGold,
    long Xp,
    int Cs,
    long DamageDealtToChampions,
    long DamageTaken,
    int CcTimeDealtCumulative,
    int PositionX,
    int PositionY);

public record TimelineFrame(long TimestampMs, IReadOnlyDictionary<int, TimelineParticipantFrame> ParticipantFrames);

public record TimelineKillEvent(long TimestampMs, int? KillerId, int VictimId, IReadOnlyList<int> AssistingParticipantIds);

// WARD_PLACED(설치자)·ELITE_MONSTER_KILL/BUILDING_KILL(처치자) 이벤트 — v4 실험에서 시야/오브젝트
// 기여도의 "근사치"로 씁니다(정확한 시야점수·오브젝트딜량은 타임라인에 없어서 개수 기반 proxy).
public record TimelineParticipantEvent(long TimestampMs, int ParticipantId);

// ELITE_MONSTER_KILL 전용(몬스터 종류 포함) — v4.0.0 오브젝트 보너스 지표용, 2026-08-21 추가.
public record TimelineMonsterKillEvent(long TimestampMs, int ParticipantId, string MonsterType);

public record TimelineDetail(
    string MatchId,
    int FrameIntervalMs,
    IReadOnlyList<TimelineFrame> Frames,
    IReadOnlyList<TimelineKillEvent> Kills,
    IReadOnlyList<TimelineParticipantEvent> WardsPlaced,
    IReadOnlyList<TimelineParticipantEvent> ObjectiveKillParticipations,
    IReadOnlyList<TimelineMonsterKillEvent> EliteMonsterKills);

public record TimelineResult(bool IsSuccess, string Message, TimelineDetail? Timeline = null)
{
    public static TimelineResult Success(TimelineDetail timeline) =>
        new(true, "타임라인을 조회했습니다.", timeline);

    public static TimelineResult Failure(string reason) => new(false, reason);
}

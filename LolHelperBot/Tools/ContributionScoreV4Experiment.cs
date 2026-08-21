// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-21
// Reviewer: (박정훈)
// Review: 기여도 점수 v4 검증용 1회성 실험 코드. v4.0.0(2026-08-21, "팀 승리 플랜 기준" 재설계 —
// Config/ContributionScoreWeightsV4.txt 주석에 원문/구현방침 그대로 옮겨둠)을 실제로 계산해서,
// 지금 운영 중인 v3(전체 게임 기준 맞라인 비교) 순위와 얼마나 달라지는지 콘솔에 찍어봅니다.
// `dotnet run -- v4-test [매치수]` — 최근 클랜 매치(5인큐) N건 대상, 매치당 API 2콜(상세+타임라인).
//
// 실제 명령어(/아재전적, /명예의전당)에는 아직 연결 안 함 — 검증 결과 보고 정식 반영 여부 결정.
//
// v4.0.0에서 새로 추가된 지표와 구현 방식(가중치 파일 주석과 같은 내용, 코드 쪽 메모):
//   - xp/solo_kill: 정확히 계산(프레임 XP, "어시스트 없는 킬" 이벤트 필터).
//   - objective_kill_bonus: ELITE_MONSTER_KILL의 monsterType별 가중치(드래곤3/유충1.5/바론5/전령3)
//     로 damage_to_objectives에 얹는 보너스. 막타 크레딧만 있어서 팀 관여도는 여전히
//     damage_to_objectives가 더 정확 — 이건 "막타 친 사람 보너스"일 뿐.
//   - heal_and_shield: heal_amount + totalDamageShieldedOnTeammates 합산.
//   - other_lane_impact(미드)/roaming(서폿): 맵을 대각선(x-y)으로 TOP/MID/BOT 3등분하는 "근사
//     좌표 존"으로 분류 — 정확한 타워 좌표 기반이 아니라 참고용 근사치입니다. 1분 스냅샷 단위로만
//     판정 가능(원래 스펙의 "30초" 기준보다 거칠음).
//   - 봇듀오 0.7/0.3 블렌드: EARLY/LATE 각각 원딜·서폿의 "블렌드 전 개인 점수"를 계산해두고,
//     전부 계산한 뒤 마지막에 한 번에 블렌드합니다(순환 참조 방지).

using LolHelperBot.Services;
using static LolHelperBot.Services.ClanConstants;

namespace LolHelperBot.Tools;

public static class ContributionScoreV4Experiment
{
    private const long FifteenMinutesMs = 15 * 60 * 1000;
    private const long OneMinuteMs = 60 * 1000;

    // 맵을 대각선으로 3등분하는 근사 존 경계. 정확한 타워 좌표가 아니라 "x-y 차이가 이 정도면
    // 탑/봇 쪽에 가깝다"는 거친 근사치입니다.
    private const int ZoneDiffThreshold = 2500;

    public static async Task RunAsync(
        RiotApiClient riotApiClient,
        MatchRepository matchRepository,
        ulong guildId,
        int matchCount)
    {
        var v3WeightsPath = Path.Combine(AppContext.BaseDirectory, "Config", "ContributionScoreWeights.txt");
        var v4WeightsPath = Path.Combine(AppContext.BaseDirectory, "Config", "ContributionScoreWeightsV4.txt");

        var v3Calculator = new ContributionScoreCalculator(v3WeightsPath);
        var v4Weights = LoadWeights(v4WeightsPath);

        Console.WriteLine($"[v4-test] 최근 클랜 매치 {matchCount}건에 v3(전체 게임)/v4.0.0(15분 라인전+후반, 팀 승리 플랜) 순위를 같이 계산합니다...\n");

        var clanMatches = await matchRepository.GetClanMatchesAsync(guildId, FlexQueueId, minTeammates: 5, limit: matchCount);
        if (clanMatches.Count == 0)
        {
            Console.WriteLine("[v4-test] 저장된 클랜 매치(5명 전원 우리 멤버)가 없습니다. /atoz 전적수집을 먼저 실행하세요.");
            return;
        }

        var diag = new Dictionary<(string Position, string Phase, string Metric), List<double>>();

        foreach (var clanMatch in clanMatches)
        {
            var matchResult = await riotApiClient.GetFullMatchAsync(clanMatch.MatchId);
            await Task.Delay(RiotApiDelay);
            if (!matchResult.IsSuccess || matchResult.Match is null)
            {
                Console.WriteLine($"[{clanMatch.MatchId}] 매치 상세 조회 실패: {matchResult.Message}");
                continue;
            }

            var timelineResult = await riotApiClient.GetTimelineAsync(clanMatch.MatchId);
            await Task.Delay(RiotApiDelay);
            if (!timelineResult.IsSuccess || timelineResult.Timeline is null)
            {
                Console.WriteLine($"[{clanMatch.MatchId}] 타임라인 조회 실패: {timelineResult.Message}");
                continue;
            }

            var v3Ranked = v3Calculator.TryCalculate(clanMatch.Participants);
            ProcessMatch(clanMatch, matchResult.Match, timelineResult.Timeline, v4Weights, v3Ranked, diag);
        }

        PrintDiagnostics(diag);
    }

    private static void PrintDiagnostics(Dictionary<(string Position, string Phase, string Metric), List<double>> diag)
    {
        Console.WriteLine("===== 진단: 포지션별 평균 Advantage(50=동률, 100=완전 압도, 0=완전 열세) =====");
        var positions = new[] { "TOP", "JUNGLE", "MIDDLE", "BOTTOM", "UTILITY" };
        var phases = new[] { "EARLY", "LATE" };
        var metrics = new[] { "kda", "gold", "cs", "xp", "dmgDealt", "dmgTaken", "cc", "obj", "objBonus", "wards", "killPart", "solo", "roam", "healShield" };

        foreach (var position in positions)
        {
            foreach (var phase in phases)
            {
                var line = $"  [{position,-7} {phase,-5}]";
                foreach (var metric in metrics)
                {
                    if (diag.TryGetValue((position, phase, metric), out var values) && values.Count > 0)
                    {
                        line += $" {metric}={values.Average():F0}";
                    }
                }

                Console.WriteLine(line);
            }
        }
    }

    private static void ProcessMatch(
        ClanMatchRow clanMatch,
        FullMatchDetail match,
        TimelineDetail timeline,
        IReadOnlyDictionary<string, Dictionary<string, double>> v4Weights,
        IReadOnlyList<ContributionScoreRow>? v3Ranked,
        Dictionary<(string Position, string Phase, string Metric), List<double>> diag)
    {
        Console.WriteLine($"===== {clanMatch.MatchId} ({clanMatch.GameCreatedAt.ToOffset(TimeSpan.FromHours(9)):MM/dd HH:mm}, {clanMatch.GameDurationSeconds / 60}분) =====");

        if (timeline.Frames.Count == 0)
        {
            Console.WriteLine("  (타임라인 프레임 없음 — 건너뜀)\n");
            return;
        }

        // participantId(1~10) = FullMatchDetail.Participants 배열 순서(index+1). Riot API 계약.
        var indexed = match.Participants.Select((p, i) => (ParticipantId: i + 1, Participant: p)).ToList();
        var lastFrame = timeline.Frames[^1];
        var frame15 = timeline.Frames
            .Where(f => f.TimestampMs <= FifteenMinutesMs)
            .OrderByDescending(f => f.TimestampMs)
            .FirstOrDefault() ?? timeline.Frames[0];

        int KillsUpTo(int pid, long ts) => timeline.Kills.Count(k => k.TimestampMs <= ts && k.KillerId == pid);
        int DeathsUpTo(int pid, long ts) => timeline.Kills.Count(k => k.TimestampMs <= ts && k.VictimId == pid);
        int AssistsUpTo(int pid, long ts) => timeline.Kills.Count(k => k.TimestampMs <= ts && k.AssistingParticipantIds.Contains(pid));
        int WardsUpTo(int pid, long ts) => timeline.WardsPlaced.Count(w => w.TimestampMs <= ts && w.ParticipantId == pid);
        int ObjectivesUpTo(int pid, long ts) => timeline.ObjectiveKillParticipations.Count(o => o.TimestampMs <= ts && o.ParticipantId == pid);
        int SoloKillsUpTo(int killerPid, int victimPid, long ts) =>
            timeline.Kills.Count(k => k.TimestampMs <= ts && k.KillerId == killerPid && k.VictimId == victimPid && k.AssistingParticipantIds.Count == 0);
        double Kda(int kills, int deaths, int assists) => (kills + assists) / (double)Math.Max(1, deaths);

        double MonsterWeight(string monsterType) => monsterType switch
        {
            "DRAGON" => 3.0,
            "HORDE" => 1.5, // 유충(보이드 그럽)
            "BARON_NASHOR" => 5.0,
            "RIFTHERALD" => 3.0,
            "ATAKHAN" => 5.0,
            _ => 2.0,
        };
        double ObjectivePointsUpTo(int pid, long ts) =>
            timeline.EliteMonsterKills.Where(e => e.TimestampMs <= ts && e.ParticipantId == pid).Sum(e => MonsterWeight(e.MonsterType));

        // 맵을 대각선(x-y)으로 3등분하는 근사 존 분류 — 정확한 타워 좌표 기반 아님(파일 상단 주석 참고).
        string ZoneOf(int x, int y)
        {
            var diff = x - y;
            if (diff > ZoneDiffThreshold) return "BOT";
            if (diff < -ZoneDiffThreshold) return "TOP";
            return "MID";
        }

        // 1분 스냅샷 중 "자기 라인 존을 벗어났고 + 그 분(¡1분 버킷) 안에 킬/어시를 냈다"를 세서
        // 로밍/다른라인기여도의 "성공한 이탈" 횟수로 씁니다.
        int SuccessfulRoamMinutes(int pid, string homeZone, long fromMs, long toMs)
        {
            var count = 0;
            foreach (var frame in timeline.Frames.Where(f => f.TimestampMs > fromMs && f.TimestampMs <= toMs))
            {
                if (!frame.ParticipantFrames.TryGetValue(pid, out var pf))
                {
                    continue;
                }

                if (ZoneOf(pf.PositionX, pf.PositionY) == homeZone)
                {
                    continue;
                }

                var bucketStart = frame.TimestampMs - OneMinuteMs;
                var gotKillOrAssist = timeline.Kills.Any(k =>
                    k.TimestampMs > bucketStart && k.TimestampMs <= frame.TimestampMs &&
                    (k.KillerId == pid || k.AssistingParticipantIds.Contains(pid)));
                if (gotKillOrAssist)
                {
                    count++;
                }
            }

            return count;
        }

        // 서폿 로밍의 "역효과"(내가 자리 비운 사이 원딜이 죽음) — 봇 듀오 파트너 사망 횟수.
        int PartnerDeathsWhileAway(int supportPid, int partnerPid, string homeZone, long fromMs, long toMs)
        {
            var count = 0;
            foreach (var frame in timeline.Frames.Where(f => f.TimestampMs > fromMs && f.TimestampMs <= toMs))
            {
                if (!frame.ParticipantFrames.TryGetValue(supportPid, out var pf) || ZoneOf(pf.PositionX, pf.PositionY) == homeZone)
                {
                    continue;
                }

                var bucketStart = frame.TimestampMs - OneMinuteMs;
                if (timeline.Kills.Any(k => k.TimestampMs > bucketStart && k.TimestampMs <= frame.TimestampMs && k.VictimId == partnerPid))
                {
                    count++;
                }
            }

            return count;
        }

        double Advantage(double mine, double theirs)
        {
            var sum = mine + theirs;
            return sum > 0 ? mine * 100.0 / sum : 50.0;
        }

        var teamAIds = indexed.Where(x => x.Participant.TeamId == 100).Select(x => x.ParticipantId).ToHashSet();
        var teamBIds = indexed.Where(x => x.Participant.TeamId == 200).Select(x => x.ParticipantId).ToHashSet();

        int TeamKillsInWindow(HashSet<int> teamIds, long fromMs, long toMs) =>
            timeline.Kills.Count(k => k.TimestampMs > fromMs && k.TimestampMs <= toMs && k.KillerId is not null && teamIds.Contains(k.KillerId.Value));

        var v3RankByChampion = v3Ranked?.ToDictionary(r => (r.Participant.TeamId, r.Participant.ChampionName), r => r.Rank);

        var results = new List<(ClanMatchParticipantRow Row, double EarlyOwn, double LateOwn, double V4Own)>();

        foreach (var row in clanMatch.Participants)
        {
            var mine = indexed.FirstOrDefault(x =>
                x.Participant.TeamId == row.TeamId &&
                x.Participant.TeamPosition == row.TeamPosition &&
                x.Participant.ChampionName == row.ChampionName);
            var opponent = indexed.FirstOrDefault(x =>
                x.Participant.TeamId != row.TeamId &&
                x.Participant.TeamPosition == row.TeamPosition);

            if (mine.Participant is null || opponent.Participant is null)
            {
                continue; // 라인 정보가 없는 구 데이터 — 건너뜀.
            }

            // 봇 듀오 파트너(같은 팀, 반대 포지션) — 로밍 역효과 계산용.
            var duoPartner = row.TeamPosition switch
            {
                "BOTTOM" => indexed.FirstOrDefault(x => x.Participant.TeamId == row.TeamId && x.Participant.TeamPosition == "UTILITY"),
                "UTILITY" => indexed.FirstOrDefault(x => x.Participant.TeamId == row.TeamId && x.Participant.TeamPosition == "BOTTOM"),
                _ => default,
            };

            var myTeamIds = row.TeamId == 100 ? teamAIds : teamBIds;

            var myFrame15 = frame15.ParticipantFrames.GetValueOrDefault(mine.ParticipantId);
            var oppFrame15 = frame15.ParticipantFrames.GetValueOrDefault(opponent.ParticipantId);
            var myFrameFinal = lastFrame.ParticipantFrames.GetValueOrDefault(mine.ParticipantId);
            var oppFrameFinal = lastFrame.ParticipantFrames.GetValueOrDefault(opponent.ParticipantId);

            double ScorePhase(string weightPrefix, bool isEarly)
            {
                var lineWeights = v4Weights.GetValueOrDefault($"{weightPrefix}_{row.TeamPosition}", EmptyWeights);
                if (lineWeights.Count == 0)
                {
                    return 0;
                }

                long myGold, oppGold, myCs, oppCs, myDmgDealt, oppDmgDealt, myDmgTaken, oppDmgTaken, myXp, oppXp;
                int myCc, oppCc;
                int myKills, myDeaths, myAssists, oppKills, oppDeaths, oppAssists;
                int myWards, oppWards, myObj, oppObj, mySolo, oppSolo;
                double myObjPoints, oppObjPoints;
                long myHealShield, oppHealShield;
                long teamKillsForParticipation;

                // 로밍 판정용 프레임 구간(0분 스폰 시점은 로밍으로 안 침). isEarly=false(후반)는
                // 15분 프레임 자체는 라인전에 포함시키고 그 다음부터.
                var windowFrom = isEarly ? 0 : FifteenMinutesMs;
                var windowTo = isEarly ? FifteenMinutesMs : long.MaxValue;

                if (isEarly)
                {
                    myGold = myFrame15?.TotalGold ?? 0; oppGold = oppFrame15?.TotalGold ?? 0;
                    myCs = myFrame15?.Cs ?? 0; oppCs = oppFrame15?.Cs ?? 0;
                    myXp = myFrame15?.Xp ?? 0; oppXp = oppFrame15?.Xp ?? 0;
                    myDmgDealt = myFrame15?.DamageDealtToChampions ?? 0; oppDmgDealt = oppFrame15?.DamageDealtToChampions ?? 0;
                    myDmgTaken = myFrame15?.DamageTaken ?? 0; oppDmgTaken = oppFrame15?.DamageTaken ?? 0;
                    myCc = myFrame15?.CcTimeDealtCumulative ?? 0; oppCc = oppFrame15?.CcTimeDealtCumulative ?? 0;
                    myKills = KillsUpTo(mine.ParticipantId, FifteenMinutesMs); myDeaths = DeathsUpTo(mine.ParticipantId, FifteenMinutesMs); myAssists = AssistsUpTo(mine.ParticipantId, FifteenMinutesMs);
                    oppKills = KillsUpTo(opponent.ParticipantId, FifteenMinutesMs); oppDeaths = DeathsUpTo(opponent.ParticipantId, FifteenMinutesMs); oppAssists = AssistsUpTo(opponent.ParticipantId, FifteenMinutesMs);
                    myWards = WardsUpTo(mine.ParticipantId, FifteenMinutesMs); oppWards = WardsUpTo(opponent.ParticipantId, FifteenMinutesMs);
                    myObj = ObjectivesUpTo(mine.ParticipantId, FifteenMinutesMs); oppObj = ObjectivesUpTo(opponent.ParticipantId, FifteenMinutesMs);
                    myObjPoints = ObjectivePointsUpTo(mine.ParticipantId, FifteenMinutesMs); oppObjPoints = ObjectivePointsUpTo(opponent.ParticipantId, FifteenMinutesMs);
                    mySolo = SoloKillsUpTo(mine.ParticipantId, opponent.ParticipantId, FifteenMinutesMs);
                    oppSolo = SoloKillsUpTo(opponent.ParticipantId, mine.ParticipantId, FifteenMinutesMs);
                    myHealShield = 0; oppHealShield = 0; // 힐/보호막은 시간별 값이 없어 후반에만 반영.
                    teamKillsForParticipation = TeamKillsInWindow(myTeamIds, long.MinValue, FifteenMinutesMs);
                }
                else
                {
                    var myGoldFinal = myFrameFinal?.TotalGold ?? 0; var oppGoldFinal = oppFrameFinal?.TotalGold ?? 0;
                    myGold = myGoldFinal - (myFrame15?.TotalGold ?? 0); oppGold = oppGoldFinal - (oppFrame15?.TotalGold ?? 0);
                    myCs = (myFrameFinal?.Cs ?? 0) - (myFrame15?.Cs ?? 0); oppCs = (oppFrameFinal?.Cs ?? 0) - (oppFrame15?.Cs ?? 0);
                    myXp = (myFrameFinal?.Xp ?? 0) - (myFrame15?.Xp ?? 0); oppXp = (oppFrameFinal?.Xp ?? 0) - (oppFrame15?.Xp ?? 0);
                    myDmgDealt = (myFrameFinal?.DamageDealtToChampions ?? 0) - (myFrame15?.DamageDealtToChampions ?? 0);
                    oppDmgDealt = (oppFrameFinal?.DamageDealtToChampions ?? 0) - (oppFrame15?.DamageDealtToChampions ?? 0);
                    myDmgTaken = (myFrameFinal?.DamageTaken ?? 0) - (myFrame15?.DamageTaken ?? 0);
                    oppDmgTaken = (oppFrameFinal?.DamageTaken ?? 0) - (oppFrame15?.DamageTaken ?? 0);
                    myCc = (myFrameFinal?.CcTimeDealtCumulative ?? 0) - (myFrame15?.CcTimeDealtCumulative ?? 0);
                    oppCc = (oppFrameFinal?.CcTimeDealtCumulative ?? 0) - (oppFrame15?.CcTimeDealtCumulative ?? 0);
                    myKills = mine.Participant.Kills - KillsUpTo(mine.ParticipantId, FifteenMinutesMs);
                    myDeaths = mine.Participant.Deaths - DeathsUpTo(mine.ParticipantId, FifteenMinutesMs);
                    myAssists = mine.Participant.Assists - AssistsUpTo(mine.ParticipantId, FifteenMinutesMs);
                    oppKills = opponent.Participant.Kills - KillsUpTo(opponent.ParticipantId, FifteenMinutesMs);
                    oppDeaths = opponent.Participant.Deaths - DeathsUpTo(opponent.ParticipantId, FifteenMinutesMs);
                    oppAssists = opponent.Participant.Assists - AssistsUpTo(opponent.ParticipantId, FifteenMinutesMs);
                    myWards = (row.WardsPlaced ?? 0) - WardsUpTo(mine.ParticipantId, FifteenMinutesMs);
                    oppWards = (opponent.Participant.WardsPlaced ?? 0) - WardsUpTo(opponent.ParticipantId, FifteenMinutesMs);
                    myObj = timeline.ObjectiveKillParticipations.Count(o => o.ParticipantId == mine.ParticipantId) - ObjectivesUpTo(mine.ParticipantId, FifteenMinutesMs);
                    oppObj = timeline.ObjectiveKillParticipations.Count(o => o.ParticipantId == opponent.ParticipantId) - ObjectivesUpTo(opponent.ParticipantId, FifteenMinutesMs);
                    var myObjPointsTotal = timeline.EliteMonsterKills.Where(e => e.ParticipantId == mine.ParticipantId).Sum(e => MonsterWeight(e.MonsterType));
                    var oppObjPointsTotal = timeline.EliteMonsterKills.Where(e => e.ParticipantId == opponent.ParticipantId).Sum(e => MonsterWeight(e.MonsterType));
                    myObjPoints = myObjPointsTotal - ObjectivePointsUpTo(mine.ParticipantId, FifteenMinutesMs);
                    oppObjPoints = oppObjPointsTotal - ObjectivePointsUpTo(opponent.ParticipantId, FifteenMinutesMs);
                    var myTotalSolo = timeline.Kills.Count(k => k.KillerId == mine.ParticipantId && k.VictimId == opponent.ParticipantId && k.AssistingParticipantIds.Count == 0);
                    var oppTotalSolo = timeline.Kills.Count(k => k.KillerId == opponent.ParticipantId && k.VictimId == mine.ParticipantId && k.AssistingParticipantIds.Count == 0);
                    mySolo = myTotalSolo - SoloKillsUpTo(mine.ParticipantId, opponent.ParticipantId, FifteenMinutesMs);
                    oppSolo = oppTotalSolo - SoloKillsUpTo(opponent.ParticipantId, mine.ParticipantId, FifteenMinutesMs);
                    myHealShield = (mine.Participant.HealAmount ?? 0) + (mine.Participant.DamageShieldedOnTeammates ?? 0);
                    oppHealShield = (opponent.Participant.HealAmount ?? 0) + (opponent.Participant.DamageShieldedOnTeammates ?? 0);
                    teamKillsForParticipation = TeamKillsInWindow(myTeamIds, FifteenMinutesMs, long.MaxValue);
                }

                var goldAdv = Advantage(myGold, oppGold);
                var csAdv = Advantage(myCs, oppCs);
                var xpAdv = Advantage(myXp, oppXp);
                var dmgDealtAdv = Advantage(myDmgDealt, oppDmgDealt);
                var dmgTakenAdv = Advantage(myDmgTaken, oppDmgTaken);
                var ccAdv = Advantage(myCc, oppCc);
                var wardsAdv = Advantage(myWards, oppWards);
                var visionAdv = wardsAdv; // proxy: 타임라인엔 시야점수 시계열이 없어서 와드 개수로 근사.
                var objAdv = Advantage(myObj, oppObj);
                var objBonusAdv = Advantage(myObjPoints, oppObjPoints);
                var healShieldAdv = Advantage(myHealShield, oppHealShield);
                var soloAdv = Advantage(mySolo, oppSolo);
                var myKda = Kda(myKills, myDeaths, myAssists);
                var oppKda = Kda(oppKills, oppDeaths, oppAssists);
                var kdaAdv = Advantage(myKda, oppKda);
                var killParticipation = teamKillsForParticipation > 0
                    ? (myKills + myAssists) * 100.0 / teamKillsForParticipation
                    : 0.0;

                // 미드 "다른라인기여도" / 서폿 "로밍" — 근사 존 분류 + 1분 버킷 킬/어시 판정.
                var roamAdv = 50.0;
                if (row.TeamPosition is "MIDDLE" or "UTILITY")
                {
                    var homeZone = row.TeamPosition == "MIDDLE" ? "MID" : "BOT";
                    var myRoam = (double)SuccessfulRoamMinutes(mine.ParticipantId, homeZone, windowFrom, windowTo);
                    var oppRoam = (double)SuccessfulRoamMinutes(opponent.ParticipantId, homeZone, windowFrom, windowTo);

                    if (row.TeamPosition == "UTILITY" && duoPartner.Participant is not null)
                    {
                        // 로밍 역효과: 내가 자리 비운 사이 원딜이 죽은 횟수만큼 깎음(0 밑으로는 안 내림 —
                        // Advantage()가 음수를 못 받아들여서, 대신 "성공 로밍" 점수에서 상쇄만 함).
                        myRoam = Math.Max(0.0, myRoam - PartnerDeathsWhileAway(mine.ParticipantId, duoPartner.ParticipantId, homeZone, windowFrom, windowTo));
                    }

                    roamAdv = Advantage(myRoam, oppRoam);
                }

                var damageDeficit = Math.Max(0.0, 50.0 - dmgDealtAdv) / 50.0;
                var offsetSource = Math.Max(0.0, dmgTakenAdv - 50.0) + Math.Max(0.0, ccAdv - 50.0);
                var damageDeficitOffset = damageDeficit * offsetSource;

                var phaseLabel = isEarly ? "EARLY" : "LATE";
                void Record(string metric, double value)
                {
                    var key = (row.TeamPosition, phaseLabel, metric);
                    if (!diag.TryGetValue(key, out var list))
                    {
                        list = new List<double>();
                        diag[key] = list;
                    }

                    list.Add(value);
                }
                Record("kda", kdaAdv);
                Record("gold", goldAdv);
                Record("cs", csAdv);
                Record("xp", xpAdv);
                Record("dmgDealt", dmgDealtAdv);
                Record("dmgTaken", dmgTakenAdv);
                Record("cc", ccAdv);
                Record("obj", objAdv);
                Record("objBonus", objBonusAdv);
                Record("wards", wardsAdv);
                Record("killPart", killParticipation);
                Record("solo", soloAdv);
                Record("roam", roamAdv);
                Record("healShield", healShieldAdv);

                var weightedSum =
                    lineWeights.GetValueOrDefault("gold_earned") * goldAdv +
                    lineWeights.GetValueOrDefault("creep_score") * csAdv +
                    lineWeights.GetValueOrDefault("xp") * xpAdv +
                    lineWeights.GetValueOrDefault("damage_dealt") * dmgDealtAdv +
                    lineWeights.GetValueOrDefault("damage_taken") * dmgTakenAdv +
                    lineWeights.GetValueOrDefault("cc_time") * ccAdv +
                    lineWeights.GetValueOrDefault("vision_score") * visionAdv +
                    lineWeights.GetValueOrDefault("wards_placed") * wardsAdv +
                    lineWeights.GetValueOrDefault("heal_and_shield") * healShieldAdv +
                    lineWeights.GetValueOrDefault("damage_to_objectives") * objAdv +
                    lineWeights.GetValueOrDefault("objective_kill_bonus") * objBonusAdv +
                    lineWeights.GetValueOrDefault("solo_kill") * soloAdv +
                    lineWeights.GetValueOrDefault("other_lane_impact") * roamAdv +
                    lineWeights.GetValueOrDefault("roaming") * roamAdv +
                    lineWeights.GetValueOrDefault("kda") * kdaAdv +
                    lineWeights.GetValueOrDefault("kill_participation") * killParticipation +
                    lineWeights.GetValueOrDefault("damage_deficit_offset") * damageDeficitOffset;

                var totalWeight = lineWeights.Values.Sum();
                return totalWeight > 0 ? weightedSum / totalWeight : 0.0;
            }

            var earlyOwn = ScorePhase("EARLY", isEarly: true);
            var lateOwn = ScorePhase("LATE", isEarly: false);
            var v4Own = (earlyOwn + lateOwn) / 2.0;

            results.Add((row, earlyOwn, lateOwn, v4Own));
        }

        // 봇 듀오 0.7/0.3 블렌드 — 전부 계산한 "블렌드 전 개인 점수"끼리만 참조(순환 방지).
        var blended = results.Select(r =>
        {
            if (r.Row.TeamPosition is not ("BOTTOM" or "UTILITY"))
            {
                return (r.Row, r.V4Own);
            }

            var partnerPosition = r.Row.TeamPosition == "BOTTOM" ? "UTILITY" : "BOTTOM";
            var partner = results.FirstOrDefault(p => p.Row.TeamId == r.Row.TeamId && p.Row.TeamPosition == partnerPosition);
            var blendedScore = partner.Row is null ? r.V4Own : r.V4Own * 0.7 + partner.V4Own * 0.3;
            return (r.Row, blendedScore);
        }).ToList();

        var v4RankByRow = blended
            .OrderByDescending(x => x.Item2)
            .Select((x, i) => (x.Row, Rank: i + 1))
            .ToDictionary(x => x.Row, x => x.Rank);

        foreach (var (row, score) in blended.OrderBy(r => GetPositionOrder(r.Row.TeamPosition)))
        {
            var v3Rank = v3RankByChampion?.GetValueOrDefault((row.TeamId, row.ChampionName), 0) ?? 0;
            var v4Rank = v4RankByRow[row];
            var mark = v3Rank == v4Rank ? "  " : "❗";
            Console.WriteLine(
                $"  {mark} [{row.TeamPosition,-7}] {row.ChampionName,-12} v3={v3Rank}위  v4={v4Rank}위  (blend={score:F1})");
        }

        Console.WriteLine();
    }

    private static readonly Dictionary<string, double> EmptyWeights = new();

    private static int GetPositionOrder(string position) => position switch
    {
        "TOP" => 0,
        "JUNGLE" => 1,
        "MIDDLE" => 2,
        "BOTTOM" => 3,
        "UTILITY" => 4,
        _ => 5,
    };

    private static IReadOnlyDictionary<string, Dictionary<string, double>> LoadWeights(string path)
    {
        var weights = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            Console.WriteLine($"[v4-test] 가중치 파일을 찾을 수 없습니다: {path}");
            return weights;
        }

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var equalsIndex = line.IndexOf('=');
            if (equalsIndex <= 0)
            {
                continue;
            }

            var key = line[..equalsIndex].Trim();
            var valueText = line[(equalsIndex + 1)..].Trim();
            var dotIndex = key.IndexOf('.');
            if (dotIndex <= 0 || !double.TryParse(valueText, out var weight))
            {
                continue;
            }

            var position = key[..dotIndex].Trim().ToUpperInvariant();
            var metric = key[(dotIndex + 1)..].Trim().ToLowerInvariant();

            if (!weights.TryGetValue(position, out var lineWeights))
            {
                lineWeights = new Dictionary<string, double>();
                weights[position] = lineWeights;
            }

            lineWeights[metric] = weight;
        }

        return weights;
    }
}

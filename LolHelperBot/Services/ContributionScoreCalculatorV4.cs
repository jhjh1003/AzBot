// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-21
// Reviewer: (박정훈)
// Review: 기여도 점수 v4.0.0("팀 승리 플랜" 기준, 15분 라인전/후반 분리) 정식 계산 서비스.
// Tools/ContributionScoreV4Experiment.cs에서 검증한 로직을 재사용 가능한 서비스로 옮겼습니다
// (실험 도구는 이 서비스를 호출해서 콘솔 출력만 담당하도록 리팩터링 예정).
//
// v3(ContributionScoreCalculator)와 다르게, 이 계산은 Match-V5 상세만으로는 안 되고 Timeline
// API 데이터가 반드시 필요합니다(15분 스냅샷 + 킬/와드/오브젝트 이벤트). 그래서 계산 시점에
// FullMatchDetail + TimelineDetail을 둘 다 받습니다 — /전적수집이나 백필 스크립트가 API를 호출해서
// 넘겨줘야 하고, 결과는 MatchRepository.UpsertContributionV4Async로 저장해서 /아재전적·
// /명예의전당이 매번 재계산하지 않고 저장된 점수만 읽도록 설계했습니다.
//
// 가중치는 Config/ContributionScoreWeightsV4.txt에서 읽습니다(그 파일 상단 주석에 원본 스펙과
// 구현 방침 전부 기록돼 있음).

namespace LolHelperBot.Services;

public class ContributionScoreCalculatorV4
{
    private const long FifteenMinutesMs = 15 * 60 * 1000;
    private const long OneMinuteMs = 60 * 1000;
    private const int ZoneDiffThreshold = 2500;

    private readonly string _weightsFilePath;
    private IReadOnlyDictionary<string, Dictionary<string, double>>? _cachedWeights;

    public ContributionScoreCalculatorV4(string weightsFilePath)
    {
        _weightsFilePath = weightsFilePath;
    }

    /// <summary>
    /// 한 매치의 우리 팀(정확히 5명) 참가자별 v4.0.0 점수를 계산합니다. Timeline 프레임이 없거나
    /// 라인 매칭이 안 되는 참가자는 결과에서 빠집니다(호출부가 개수를 확인해야 함).
    /// </summary>
    public IReadOnlyList<ContributionScoreV4Row> Calculate(
        IReadOnlyList<ClanMatchParticipantRow> teamOfFive,
        FullMatchDetail match,
        TimelineDetail timeline)
    {
        if (teamOfFive.Count != 5 || timeline.Frames.Count == 0)
        {
            return [];
        }

        var weights = GetWeights();
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
            "HORDE" => 1.5,
            "BARON_NASHOR" => 5.0,
            "RIFTHERALD" => 3.0,
            "ATAKHAN" => 5.0,
            _ => 2.0,
        };
        double ObjectivePointsUpTo(int pid, long ts) =>
            timeline.EliteMonsterKills.Where(e => e.TimestampMs <= ts && e.ParticipantId == pid).Sum(e => MonsterWeight(e.MonsterType));

        string ZoneOf(int x, int y)
        {
            var diff = x - y;
            if (diff > ZoneDiffThreshold) return "BOT";
            if (diff < -ZoneDiffThreshold) return "TOP";
            return "MID";
        }

        int SuccessfulRoamMinutes(int pid, string homeZone, long fromMs, long toMs)
        {
            var count = 0;
            foreach (var frame in timeline.Frames.Where(f => f.TimestampMs > fromMs && f.TimestampMs <= toMs))
            {
                if (!frame.ParticipantFrames.TryGetValue(pid, out var pf) || ZoneOf(pf.PositionX, pf.PositionY) == homeZone)
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

        // 15분 이후(LATE) 팀내부 비교용 — 5명의 participantId를 먼저 찾아둡니다.
        var teamMatches = teamOfFive
            .Select(row => (Row: row, Mine: indexed.FirstOrDefault(x =>
                x.Participant.TeamId == row.TeamId &&
                x.Participant.TeamPosition == row.TeamPosition &&
                x.Participant.ChampionName == row.ChampionName)))
            .ToList();
        var teamParticipantIds = teamMatches
            .Where(t => t.Mine.Participant is not null)
            .Select(t => t.Mine.ParticipantId)
            .ToList();

        // LATE 원자료(골드/CS/경험치/딜/받은딜/CC/와드/오브젝트/힐+보호막/KDA) — participantId 기준으로
        // 한 번만 계산해서 캐싱합니다(맞라인 상대뿐 아니라 팀원 4명분까지 필요해서 재사용 필요).
        var lateRawCache = new Dictionary<int, (long Gold, long Cs, long Xp, long DmgDealt, long DmgTaken, int Cc, int Wards, int ObjCount, double ObjPoints, long HealShield, double Kda)>();
        (long Gold, long Cs, long Xp, long DmgDealt, long DmgTaken, int Cc, int Wards, int ObjCount, double ObjPoints, long HealShield, double Kda) GetLateRaw(int pid)
        {
            if (lateRawCache.TryGetValue(pid, out var cached))
            {
                return cached;
            }

            var f15 = frame15.ParticipantFrames.GetValueOrDefault(pid);
            var fFinal = lastFrame.ParticipantFrames.GetValueOrDefault(pid);
            var participant = indexed.First(x => x.ParticipantId == pid).Participant;

            var gold = (fFinal?.TotalGold ?? 0) - (f15?.TotalGold ?? 0);
            var cs = (fFinal?.Cs ?? 0) - (f15?.Cs ?? 0);
            var xp = (fFinal?.Xp ?? 0) - (f15?.Xp ?? 0);
            var dmgDealt = (fFinal?.DamageDealtToChampions ?? 0) - (f15?.DamageDealtToChampions ?? 0);
            var dmgTaken = (fFinal?.DamageTaken ?? 0) - (f15?.DamageTaken ?? 0);
            var cc = (fFinal?.CcTimeDealtCumulative ?? 0) - (f15?.CcTimeDealtCumulative ?? 0);
            var wards = (participant.WardsPlaced ?? 0) - WardsUpTo(pid, FifteenMinutesMs);
            var objCount = timeline.ObjectiveKillParticipations.Count(o => o.ParticipantId == pid) - ObjectivesUpTo(pid, FifteenMinutesMs);
            var objPointsTotal = timeline.EliteMonsterKills.Where(e => e.ParticipantId == pid).Sum(e => MonsterWeight(e.MonsterType));
            var objPoints = objPointsTotal - ObjectivePointsUpTo(pid, FifteenMinutesMs);
            var kills = participant.Kills - KillsUpTo(pid, FifteenMinutesMs);
            var deaths = participant.Deaths - DeathsUpTo(pid, FifteenMinutesMs);
            var assists = participant.Assists - AssistsUpTo(pid, FifteenMinutesMs);
            var healShield = (participant.HealAmount ?? 0) + (participant.DamageShieldedOnTeammates ?? 0);
            var kda = Kda(kills, deaths, assists);

            var result = (gold, cs, xp, dmgDealt, dmgTaken, cc, wards, objCount, objPoints, healShield, kda);
            lateRawCache[pid] = result;
            return result;
        }

        // 15분 이후 지표는 "맞라인 상대 비교"(30%) + "팀 내부(나 제외 4명 평균) 비교"(70%)를 섞습니다
        // — 팀파이트/오브젝트 위주라 맞라인 상대가 어디 있는지도 모를 시기라, "지금 이 팀에서 누가
        // 잘하고 있나"를 더 크게 봅니다(2026-08-21 사용자 요청, 비율은 30:70).
        const double LateOpponentWeight = 0.3;
        const double LateInternalWeight = 0.7;

        double BlendedLateAdvantage(int myPid, double mineVal, double oppVal, Func<(long Gold, long Cs, long Xp, long DmgDealt, long DmgTaken, int Cc, int Wards, int ObjCount, double ObjPoints, long HealShield, double Kda), double> selector)
        {
            var oppAdv = Advantage(mineVal, oppVal);
            var others = teamParticipantIds
                .Where(id => id != myPid)
                .Select(id => selector(GetLateRaw(id)))
                .ToList();
            var avgOthers = others.Count > 0 ? others.Average() : mineVal;
            var internalAdv = Advantage(mineVal, avgOthers);
            return LateOpponentWeight * oppAdv + LateInternalWeight * internalAdv;
        }

        var raw = new List<(ClanMatchParticipantRow Row, double EarlyOwn, double LateOwn, double V4Own)>();

        foreach (var (row, mine) in teamMatches)
        {
            var opponent = indexed.FirstOrDefault(x =>
                x.Participant.TeamId != row.TeamId &&
                x.Participant.TeamPosition == row.TeamPosition);

            if (mine.Participant is null || opponent.Participant is null)
            {
                continue;
            }

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
                var lineWeights = weights.GetValueOrDefault($"{weightPrefix}_{row.TeamPosition}", EmptyWeights);
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
                    myHealShield = 0; oppHealShield = 0;
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

                var myKda = Kda(myKills, myDeaths, myAssists);
                var oppKda = Kda(oppKills, oppDeaths, oppAssists);

                double goldAdv, csAdv, xpAdv, dmgDealtAdv, dmgTakenAdv, ccAdv, wardsAdv, objAdv, objBonusAdv, healShieldAdv, kdaAdv;
                if (isEarly)
                {
                    // 라인전은 맞라인 상대 1:1 비교만(사용자 지정 — 15분 이전은 그대로 유지).
                    goldAdv = Advantage(myGold, oppGold);
                    csAdv = Advantage(myCs, oppCs);
                    xpAdv = Advantage(myXp, oppXp);
                    dmgDealtAdv = Advantage(myDmgDealt, oppDmgDealt);
                    dmgTakenAdv = Advantage(myDmgTaken, oppDmgTaken);
                    ccAdv = Advantage(myCc, oppCc);
                    wardsAdv = Advantage(myWards, oppWards);
                    objAdv = Advantage(myObj, oppObj);
                    objBonusAdv = Advantage(myObjPoints, oppObjPoints);
                    healShieldAdv = Advantage(myHealShield, oppHealShield);
                    kdaAdv = Advantage(myKda, oppKda);
                }
                else
                {
                    // 후반은 "맞라인 상대 비교"(30%) + "팀 내부 비교"(70%) 블렌드.
                    goldAdv = BlendedLateAdvantage(mine.ParticipantId, myGold, oppGold, r => r.Gold);
                    csAdv = BlendedLateAdvantage(mine.ParticipantId, myCs, oppCs, r => r.Cs);
                    xpAdv = BlendedLateAdvantage(mine.ParticipantId, myXp, oppXp, r => r.Xp);
                    dmgDealtAdv = BlendedLateAdvantage(mine.ParticipantId, myDmgDealt, oppDmgDealt, r => r.DmgDealt);
                    dmgTakenAdv = BlendedLateAdvantage(mine.ParticipantId, myDmgTaken, oppDmgTaken, r => r.DmgTaken);
                    ccAdv = BlendedLateAdvantage(mine.ParticipantId, myCc, oppCc, r => r.Cc);
                    wardsAdv = BlendedLateAdvantage(mine.ParticipantId, myWards, oppWards, r => r.Wards);
                    objAdv = BlendedLateAdvantage(mine.ParticipantId, myObj, oppObj, r => r.ObjCount);
                    objBonusAdv = BlendedLateAdvantage(mine.ParticipantId, myObjPoints, oppObjPoints, r => r.ObjPoints);
                    healShieldAdv = BlendedLateAdvantage(mine.ParticipantId, myHealShield, oppHealShield, r => r.HealShield);
                    kdaAdv = BlendedLateAdvantage(mine.ParticipantId, myKda, oppKda, r => r.Kda);
                }

                var visionAdv = wardsAdv;
                var soloAdv = Advantage(mySolo, oppSolo); // solo_kill은 LATE 가중치에서 아직 안 쓰임 — 블렌드 대상 아님.
                var killParticipation = teamKillsForParticipation > 0
                    ? (myKills + myAssists) * 100.0 / teamKillsForParticipation
                    : 0.0;

                var roamAdv = 50.0;
                if (row.TeamPosition is "MIDDLE" or "UTILITY")
                {
                    var homeZone = row.TeamPosition == "MIDDLE" ? "MID" : "BOT";
                    var myRoam = (double)SuccessfulRoamMinutes(mine.ParticipantId, homeZone, windowFrom, windowTo);
                    var oppRoam = (double)SuccessfulRoamMinutes(opponent.ParticipantId, homeZone, windowFrom, windowTo);

                    if (row.TeamPosition == "UTILITY" && duoPartner.Participant is not null)
                    {
                        myRoam = Math.Max(0.0, myRoam - PartnerDeathsWhileAway(mine.ParticipantId, duoPartner.ParticipantId, homeZone, windowFrom, windowTo));
                    }

                    roamAdv = Advantage(myRoam, oppRoam);
                }

                var damageDeficit = Math.Max(0.0, 50.0 - dmgDealtAdv) / 50.0;
                var offsetSource = Math.Max(0.0, dmgTakenAdv - 50.0) + Math.Max(0.0, ccAdv - 50.0);
                var damageDeficitOffset = damageDeficit * offsetSource;

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

            raw.Add((row, earlyOwn, lateOwn, v4Own));
        }

        // 봇 듀오 0.7/0.3 블렌드 — 블렌드 전 개인 점수끼리만 참조(순환 참조 방지).
        return raw.Select(r =>
        {
            if (r.Row.TeamPosition is not ("BOTTOM" or "UTILITY"))
            {
                return new ContributionScoreV4Row(r.Row, r.EarlyOwn, r.LateOwn, r.V4Own);
            }

            var partnerPosition = r.Row.TeamPosition == "BOTTOM" ? "UTILITY" : "BOTTOM";
            var partner = raw.FirstOrDefault(p => p.Row.TeamId == r.Row.TeamId && p.Row.TeamPosition == partnerPosition);
            var finalScore = partner.Row is null ? r.V4Own : r.V4Own * 0.7 + partner.V4Own * 0.3;
            return new ContributionScoreV4Row(r.Row, r.EarlyOwn, r.LateOwn, finalScore);
        }).ToList();
    }

    private static readonly Dictionary<string, double> EmptyWeights = new();

    private IReadOnlyDictionary<string, Dictionary<string, double>> GetWeights()
    {
        if (_cachedWeights is not null)
        {
            return _cachedWeights;
        }

        var weights = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(_weightsFilePath))
        {
            foreach (var rawLine in File.ReadAllLines(_weightsFilePath))
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
        }
        else
        {
            Console.Error.WriteLine($"[기여도 v4] 가중치 파일을 찾을 수 없습니다: {_weightsFilePath}");
        }

        _cachedWeights = weights;
        return weights;
    }
}

/// <summary>
/// 한 명의 v4.0.0 점수 계산 결과. FinalScore는 봇듀오 블렌드까지 적용된 값(순위 매길 때 이걸 씀).
/// </summary>
public record ContributionScoreV4Row(ClanMatchParticipantRow Row, double EarlyScore, double LateScore, double FinalScore);

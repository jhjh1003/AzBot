// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-20
// Reviewer: (박정훈)
// Review: AfterUpgrade.md 1단계 실험 — "일단 최근 10경기만 Timeline API 호출해서 결과값 한번 보자"는
// 요청에 따른 1회성 실험 코드. 실제 기능(15분 분리/골드 스윙)으로 굳히기 전 데이터 모양만 확인하는 용도.
// `dotnet run -- timeline-test [매치수]` 로 실행하며, Discord 봇은 켜지 않고 콘솔에만 출력합니다.
//
// 2026-08-20 리팩토링 1단계: Services/에서 Tools/로 이동 + 네임스페이스를 LolHelperBot.Tools로 분리.
// "실험"이라면서도 Program.cs 정식 진입점에 배선돼 있어 프로덕션 코드와 구분이 안 된다는 외부
// 코드리뷰 지적에 따라, 위치와 이름부터 "이건 진짜 기능이 아니라 개발용 도구다"를 명확히 함.

using LolHelperBot.Services;
using static LolHelperBot.Services.ClanConstants;

namespace LolHelperBot.Tools;

public static class TimelineExperiment
{
    private const long FifteenMinutesMs = 15 * 60 * 1000;

    public static async Task RunAsync(
        RiotApiClient riotApiClient,
        MatchRepository matchRepository,
        ulong guildId,
        int matchCount)
    {
        Console.WriteLine($"[timeline-test] 최근 클랜 매치 {matchCount}건을 대상으로 Timeline API를 호출합니다...");

        var clanMatches = await matchRepository.GetClanMatchesAsync(
            guildId, FlexQueueId, minTeammates: 5, limit: matchCount);

        if (clanMatches.Count == 0)
        {
            Console.WriteLine("[timeline-test] 저장된 클랜 매치(5명 전원 우리 멤버)가 없습니다. /전적수집을 먼저 실행하세요.");
            return;
        }

        Console.WriteLine($"[timeline-test] 대상 매치 {clanMatches.Count}건 확인. 매치당 API 2콜(상세+타임라인) — 시간이 걸릴 수 있습니다.\n");

        var successCount = 0;
        var failCount = 0;

        foreach (var clanMatch in clanMatches)
        {
            var matchResult = await riotApiClient.GetFullMatchAsync(clanMatch.MatchId);
            await Task.Delay(RiotApiDelay);
            if (!matchResult.IsSuccess || matchResult.Match is null)
            {
                Console.WriteLine($"[{clanMatch.MatchId}] 매치 상세 조회 실패: {matchResult.Message}");
                failCount++;
                continue;
            }

            var timelineResult = await riotApiClient.GetTimelineAsync(clanMatch.MatchId);
            await Task.Delay(RiotApiDelay);
            if (!timelineResult.IsSuccess || timelineResult.Timeline is null)
            {
                Console.WriteLine($"[{clanMatch.MatchId}] 타임라인 조회 실패: {timelineResult.Message}");
                failCount++;
                continue;
            }

            PrintMatchReport(matchResult.Match, timelineResult.Timeline);
            successCount++;
        }

        Console.WriteLine($"\n[timeline-test] 완료 — 성공 {successCount}건, 실패 {failCount}건.");
    }

    private static void PrintMatchReport(FullMatchDetail match, TimelineDetail timeline)
    {
        Console.WriteLine($"===== {match.MatchId} (경기시간 {match.GameDurationSeconds / 60}분, 프레임 {timeline.Frames.Count}개) =====");

        // participantId(1~10)는 Riot API 계약상 Participants 배열 순서와 동일합니다 (index+1).
        var participantIdToDetail = match.Participants
            .Select((p, index) => (ParticipantId: index + 1, Participant: p))
            .ToDictionary(x => x.ParticipantId, x => x.Participant);

        // 15분(또는 그 이전 종료 시 마지막 프레임)에 가장 가까운 프레임을 찾습니다.
        var frameAt15 = timeline.Frames
            .Where(f => f.TimestampMs <= FifteenMinutesMs)
            .OrderByDescending(f => f.TimestampMs)
            .FirstOrDefault() ?? timeline.Frames.LastOrDefault();

        if (frameAt15 is null)
        {
            Console.WriteLine("  (프레임 데이터 없음)");
            return;
        }

        Console.WriteLine($"  15분 시점 프레임 타임스탬프: {frameAt15.TimestampMs / 1000}초");

        var positions = new[] { "TOP", "JUNGLE", "MIDDLE", "BOTTOM", "UTILITY" };
        foreach (var position in positions)
        {
            var lanePair = participantIdToDetail
                .Where(kv => kv.Value.TeamPosition == position)
                .OrderBy(kv => kv.Value.TeamId)
                .ToList();

            if (lanePair.Count != 2)
            {
                continue; // 포지션 데이터가 없는 매치(구 데이터)는 건너뜁니다.
            }

            var (idA, pA) = lanePair[0];
            var (idB, pB) = lanePair[1];

            frameAt15.ParticipantFrames.TryGetValue(idA, out var fA);
            frameAt15.ParticipantFrames.TryGetValue(idB, out var fB);

            var goldDiff = (fA?.TotalGold ?? 0) - (fB?.TotalGold ?? 0);
            var xpDiff = (fA?.Xp ?? 0) - (fB?.Xp ?? 0);
            var csDiff = (fA?.Cs ?? 0) - (fB?.Cs ?? 0);

            Console.WriteLine(
                $"  [{position}] {pA.ChampionName}({(pA.Win ? "승" : "패")}) vs {pB.ChampionName}({(pB.Win ? "승" : "패")}) " +
                $"— 골드차 {goldDiff:+#;-#;0} / XP차 {xpDiff:+#;-#;0} / CS차 {csDiff:+#;-#;0} (양수=A안 챔피언 우위)");
        }

        var killsBefore15 = timeline.Kills.Count(k => k.TimestampMs <= FifteenMinutesMs);
        var killsAfter15 = timeline.Kills.Count - killsBefore15;
        Console.WriteLine($"  킬 이벤트: 15분 이전 {killsBefore15}건 / 15분 이후 {killsAfter15}건 (총 {timeline.Kills.Count}건)");

        // 골드 스윙 실험: 연속 프레임 사이에 팀 골드 합계 차이가 가장 크게 벌어진 구간을 찾아봅니다.
        var teamAIds = match.Participants.Select((p, i) => (Id: i + 1, p.TeamId)).Where(x => x.TeamId == 100).Select(x => x.Id).ToHashSet();
        var teamGoldDiffs = timeline.Frames
            .Select(f => (
                f.TimestampMs,
                Diff: f.ParticipantFrames.Where(pf => teamAIds.Contains(pf.Key)).Sum(pf => pf.Value.TotalGold)
                    - f.ParticipantFrames.Where(pf => !teamAIds.Contains(pf.Key)).Sum(pf => pf.Value.TotalGold)))
            .ToList();

        if (teamGoldDiffs.Count >= 2)
        {
            var biggestSwing = teamGoldDiffs
                .Zip(teamGoldDiffs.Skip(1), (prev, curr) => (prev.TimestampMs, curr.TimestampMs, Swing: curr.Diff - prev.Diff))
                .OrderByDescending(x => Math.Abs(x.Swing))
                .First();
            Console.WriteLine(
                $"  최대 골드 스윙 구간: {biggestSwing.Item1 / 60000}분~{biggestSwing.Item2 / 60000}분, " +
                $"팀 골드차 변화 {biggestSwing.Swing:+#;-#;0} (양수=100팀 쪽으로 벌어짐)");
        }

        Console.WriteLine();
    }
}

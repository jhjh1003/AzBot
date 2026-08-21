// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-21
// Reviewer: (박정훈)
// Review: 기여도 점수 v4.0.0을 /아재전적·/명예의전당에 적용하기 위한 백필 스크립트. 사용자 요청대로
// "전체 데이터 재검증 없이 이번 달(8월) 매치만" 대상으로 함 — 매치당 API 2콜(상세+타임라인)이라
// 전체 기간을 다 돌리면 비용이 크고, 지금 /명예의전당도 월별 집계라 이번 달분만 있으면 충분함.
// `dotnet run -- v4-backfill [연월]` (생략하면 이번 달, KST 기준, 예: 2026-08).

using LolHelperBot.Services;
using static LolHelperBot.Services.ClanConstants;

namespace LolHelperBot.Tools;

public static class ContributionV4Backfill
{
    public static async Task RunAsync(
        RiotApiClient riotApiClient,
        MatchRepository matchRepository,
        ulong guildId,
        string? yearMonth)
    {
        DateTimeOffset monthStartKst;
        if (string.IsNullOrWhiteSpace(yearMonth))
        {
            var nowKst = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9));
            monthStartKst = new DateTimeOffset(nowKst.Year, nowKst.Month, 1, 0, 0, 0, TimeSpan.FromHours(9));
        }
        else if (DateOnly.TryParseExact(yearMonth + "-01", "yyyy-MM-dd", out var parsed))
        {
            monthStartKst = new DateTimeOffset(parsed.Year, parsed.Month, 1, 0, 0, 0, TimeSpan.FromHours(9));
        }
        else
        {
            Console.WriteLine("[v4-backfill] 연월 형식이 올바르지 않습니다. `2026-08`처럼 입력하세요.");
            return;
        }

        var monthEndKst = monthStartKst.AddMonths(1);
        var v4WeightsPath = Path.Combine(AppContext.BaseDirectory, "Config", "ContributionScoreWeightsV4.txt");
        var v4Calculator = new ContributionScoreCalculatorV4(v4WeightsPath);

        Console.WriteLine($"[v4-backfill] {monthStartKst:yyyy-MM} 매치를 대상으로 기여도 v4.0.0을 계산해서 저장합니다...");

        var matches = await matchRepository.GetContributionStatsInRangeAsync(
            guildId, FlexQueueId, monthStartKst.ToUniversalTime(), monthEndKst.ToUniversalTime());

        if (matches.Count == 0)
        {
            Console.WriteLine($"[v4-backfill] {monthStartKst:yyyy-MM}에 저장된 매치가 없습니다.");
            return;
        }

        Console.WriteLine($"[v4-backfill] 대상 {matches.Count}건. 매치당 API 2콜(상세+타임라인) — 시간이 걸릴 수 있습니다.\n");

        var successCount = 0;
        var skipCount = 0;
        var failCount = 0;

        foreach (var clanMatch in matches)
        {
            if (clanMatch.Participants.Count != 5)
            {
                skipCount++;
                continue; // 5명 전원 우리 멤버가 아닌 매치는 v3와 마찬가지로 계산 대상 아님.
            }

            var matchResult = await riotApiClient.GetFullMatchAsync(clanMatch.MatchId);
            await Task.Delay(RiotApiDelay);
            if (!matchResult.IsSuccess || matchResult.Match is null)
            {
                Console.WriteLine($"  [{clanMatch.MatchId}] 매치 상세 조회 실패: {matchResult.Message}");
                failCount++;
                continue;
            }

            var timelineResult = await riotApiClient.GetTimelineAsync(clanMatch.MatchId);
            await Task.Delay(RiotApiDelay);
            if (!timelineResult.IsSuccess || timelineResult.Timeline is null)
            {
                Console.WriteLine($"  [{clanMatch.MatchId}] 타임라인 조회 실패: {timelineResult.Message}");
                failCount++;
                continue;
            }

            var scores = v4Calculator.Calculate(clanMatch.Participants, matchResult.Match, timelineResult.Timeline);
            if (scores.Count != 5)
            {
                Console.WriteLine($"  [{clanMatch.MatchId}] 라인 매칭 실패(구 데이터 등) — {scores.Count}/5명만 계산됨, 건너뜀");
                skipCount++;
                continue;
            }

            await matchRepository.UpsertContributionV4Async(
                guildId,
                clanMatch.MatchId,
                scores.Select(s => (s.Row.DiscordUserId, s.EarlyScore, s.LateScore, s.FinalScore)).ToList());

            successCount++;
            Console.WriteLine($"  [{clanMatch.MatchId}] 저장 완료 (5명)");
        }

        Console.WriteLine($"\n[v4-backfill] 완료 — 성공 {successCount}건, 건너뜀 {skipCount}건, 실패 {failCount}건.");
    }
}

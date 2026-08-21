// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-21
// Reviewer: (박정훈)
// Review: 기여도 점수 v4(정식 서비스 ContributionScoreCalculatorV4) 검증/진단용 콘솔 도구.
// 계산 로직은 전부 Services/ContributionScoreCalculatorV4.cs로 옮겨졌고, 이 파일은 그 결과를
// v3(ContributionScoreCalculator)와 나란히 찍어서 비교하는 용도만 합니다(중복 로직 없음 — 로직이
// 바뀌면 서비스만 고치면 이 도구도 자동으로 최신 상태가 됩니다).
// `dotnet run -- v4-test [매치수]`.

using LolHelperBot.Services;
using static LolHelperBot.Services.ClanConstants;

namespace LolHelperBot.Tools;

public static class ContributionScoreV4Experiment
{
    public static async Task RunAsync(
        RiotApiClient riotApiClient,
        MatchRepository matchRepository,
        ulong guildId,
        int matchCount)
    {
        var v3WeightsPath = Path.Combine(AppContext.BaseDirectory, "Config", "ContributionScoreWeights.txt");
        var v4WeightsPath = Path.Combine(AppContext.BaseDirectory, "Config", "ContributionScoreWeightsV4.txt");

        var v3Calculator = new ContributionScoreCalculator(v3WeightsPath);
        var v4Calculator = new ContributionScoreCalculatorV4(v4WeightsPath);

        Console.WriteLine($"[v4-test] 최근 클랜 매치 {matchCount}건에 v3(전체 게임)/v4.0.0(15분 라인전+후반, 팀 승리 플랜) 순위를 같이 계산합니다...\n");

        var clanMatches = await matchRepository.GetClanMatchesAsync(guildId, FlexQueueId, minTeammates: 5, limit: matchCount);
        if (clanMatches.Count == 0)
        {
            Console.WriteLine("[v4-test] 저장된 클랜 매치(5명 전원 우리 멤버)가 없습니다. /atoz 전적수집을 먼저 실행하세요.");
            return;
        }

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
            var v4Ranked = v4Calculator.Calculate(clanMatch.Participants, matchResult.Match, timelineResult.Timeline);
            PrintComparison(clanMatch, v3Ranked, v4Ranked);
        }
    }

    private static void PrintComparison(
        ClanMatchRow clanMatch,
        IReadOnlyList<ContributionScoreRow>? v3Ranked,
        IReadOnlyList<ContributionScoreV4Row> v4Rows)
    {
        Console.WriteLine($"===== {clanMatch.MatchId} ({clanMatch.GameCreatedAt.ToOffset(TimeSpan.FromHours(9)):MM/dd HH:mm}, {clanMatch.GameDurationSeconds / 60}분) =====");

        if (v4Rows.Count != 5)
        {
            Console.WriteLine($"  (v4 계산 결과가 5명이 아님 — {v4Rows.Count}명. 타임라인/라인 매칭 문제일 수 있음. 건너뜀)\n");
            return;
        }

        var v3RankByChampion = v3Ranked?.ToDictionary(r => (r.Participant.TeamId, r.Participant.ChampionName), r => r.Rank);
        var v4RankByRow = v4Rows
            .OrderByDescending(r => r.FinalScore)
            .Select((r, i) => (r.Row, Rank: i + 1))
            .ToDictionary(x => x.Row, x => x.Rank);

        foreach (var v4Row in v4Rows.OrderBy(r => GetPositionOrder(r.Row.TeamPosition)))
        {
            var row = v4Row.Row;
            var v3Rank = v3RankByChampion?.GetValueOrDefault((row.TeamId, row.ChampionName), 0) ?? 0;
            var v4Rank = v4RankByRow[row];
            var mark = v3Rank == v4Rank ? "  " : "❗";
            Console.WriteLine(
                $"  {mark} [{row.TeamPosition,-7}] {row.ChampionName,-12} v3={v3Rank}위  v4={v4Rank}위  " +
                $"(early={v4Row.EarlyScore:F1} late={v4Row.LateScore:F1} blend={v4Row.FinalScore:F1})");
        }

        Console.WriteLine();
    }

    private static int GetPositionOrder(string position) => position switch
    {
        "TOP" => 0,
        "JUNGLE" => 1,
        "MIDDLE" => 2,
        "BOTTOM" => 3,
        "UTILITY" => 4,
        _ => 5,
    };
}

// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-20
// Reviewer: (박정훈)
// Review: /밴픽추천 2단계에서 새로 추가한 MatchRepository.GetMatchupStatsAsync 등이 실제 DB에서
// 기대한 모양의 데이터를 돌려주는지 확인하기 위한 1회성 스모크 테스트. Discord 없이
// `dotnet run -- banpick-test [라인]` 으로 실행합니다.
//
// 2026-08-20 리팩토링 1단계: Services/에서 Tools/로 이동 + 네임스페이스를 LolHelperBot.Tools로 분리.
// "실험"이라면서도 Program.cs 정식 진입점에 배선돼 있어 프로덕션 코드와 구분이 안 된다는 외부
// 코드리뷰 지적에 따라, 위치와 이름부터 "이건 진짜 기능이 아니라 개발용 도구다"를 명확히 함.
//
// 2026-08-20 리팩토링 2단계: 위쪽 절반(우리 Top3/(1)/(3) 계산)은 원래 있던 원시 쿼리 로직 그대로
// 두고, 아래에 BanPickRecommendationService(실제 /밴픽추천이 쓰는 서비스)의 결과도 같이 출력하도록
// 추가함 — 서비스로 로직을 옮기면서 결과가 안 바뀌었는지 눈으로 바로 대조하기 위함(회귀 확인용).

using LolHelperBot.Services;
using static LolHelperBot.Services.ClanConstants;

namespace LolHelperBot.Tools;

public static class BanPickQueryExperiment
{
    public static async Task RunAsync(
        MatchRepository matchRepository,
        MetaTierRepository metaTierRepository,
        BanPickRecommendationService banPickRecommendationService,
        ulong guildId,
        string? positionArg)
    {
        var positions = string.IsNullOrWhiteSpace(positionArg)
            ? new[] { "TOP", "JUNGLE", "MIDDLE", "BOTTOM", "UTILITY" }
            : new[] { positionArg.ToUpperInvariant() };

        var metaSnapshot = await metaTierRepository.LoadAsync();
        Console.WriteLine(metaSnapshot is null
            ? "[banpick-test] 메타 스냅샷 없음 (Config/MetaTierSnapshot.json 비어있음 — 예상된 상태)"
            : $"[banpick-test] 메타 스냅샷 로드됨: {metaSnapshot.UpdatedAt} / {metaSnapshot.Source}");

        foreach (var pos in positions)
        {
            Console.WriteLine($"\n===== {pos} =====");

            var tierRows = await matchRepository.GetChampionTierAsync(guildId, FlexQueueId, pos);
            if (tierRows.Count == 0)
            {
                Console.WriteLine("  (데이터 없음)");
                continue;
            }

            var sampled = tierRows.Where(r => r.Games >= MinSampleSize).ToList();
            if (sampled.Count == 0) sampled = tierRows.ToList();

            var top3 = sampled
                .OrderByDescending(r => r.Wins * 1.0 / r.Games)
                .ThenByDescending(r => r.Games)
                .Take(3)
                .ToList();
            Console.WriteLine("  우리 Top3: " + string.Join(", ", top3.Select(r =>
                $"{r.ChampionName}({r.Games}판 {Math.Round(r.Wins * 100.0 / r.Games)}%)")));

            var opponentRows = await matchRepository.GetOpponentChampionStatsAsync(guildId, FlexQueueId, pos);
            var worstOpponent = opponentRows
                .Where(r => r.Games >= MinSampleSize && r.Wins * 1.0 / r.Games < 0.5)
                .OrderBy(r => r.Wins * 1.0 / r.Games)
                .ThenByDescending(r => r.Games)
                .FirstOrDefault();
            Console.WriteLine("  (1)맞상대 승률낮음: " + (worstOpponent is null
                ? "없음"
                : $"{worstOpponent.ChampionName} ({worstOpponent.Games}판 {Math.Round(worstOpponent.Wins * 100.0 / worstOpponent.Games)}%)"));

            var champNames = top3.Select(r => r.ChampionName).ToList();
            var matchupRows = await matchRepository.GetMatchupStatsAsync(guildId, FlexQueueId, pos, champNames);
            Console.WriteLine($"  (3)우리Top3 상대 매치업 전체({matchupRows.Count}건 중 표본>=3):");
            foreach (var row in matchupRows.Where(r => r.Games >= 3).OrderBy(r => r.Wins * 1.0 / r.Games).Take(5))
            {
                Console.WriteLine($"    - {row.ChampionName}: {row.Games}판 우리승률 {Math.Round(row.Wins * 100.0 / row.Games)}%");
            }
        }

        Console.WriteLine("\n[회귀 확인] BanPickRecommendationService 결과 (위 원시 쿼리와 대조용):");
        var recommendation = await banPickRecommendationService.BuildAsync(guildId, positionArg is null ? null : positionArg.ToUpperInvariant());
        if (recommendation is null)
        {
            Console.WriteLine("  (데이터 없음)");
            return;
        }

        foreach (var line in recommendation.Lines)
        {
            Console.WriteLine($"\n===== {line.Position} (서비스) =====");
            if (!line.HasData)
            {
                Console.WriteLine("  (데이터 없음)");
                continue;
            }

            Console.WriteLine("  픽 Top3: " + string.Join(", ", line.Picks.Select(p =>
                $"{p.ChampionName}({p.Games}판 {Math.Round(p.Wins * 100.0 / p.Games)}%" +
                (p.MetaCounters.Count > 0 ? $", 카운터:{string.Join("/", p.MetaCounters)}" : "") + ")")));

            foreach (var ban in line.Bans)
            {
                var detail = ban.Reason switch
                {
                    BanReasonKind.WorstOpponent => $"{ban.ChampionName} ({ban.Games}판 {Math.Round(ban.Wins * 100.0 / ban.Games)}%)",
                    BanReasonKind.MetaTier => $"{ban.ChampionName} (티어 {ban.MetaTier}, {ban.MetaWinRate:0.#}%)",
                    BanReasonKind.OurPickCounter => $"{ban.ChampionName} ({ban.Games}판 {Math.Round(ban.Wins * 100.0 / ban.Games)}%, 우리픽:{string.Join("/", ban.OurTopPicks ?? [])})",
                    _ => ban.ChampionName,
                };
                Console.WriteLine($"  밴[{ban.Reason}]: {detail}");
            }
        }
    }
}

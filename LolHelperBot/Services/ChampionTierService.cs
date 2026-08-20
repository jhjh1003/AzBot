// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-20
// Reviewer: (박정훈)
// Review: 리팩토링 2단계 — 서비스 계층 추출 2호(/티어픽). BanPickRecommendationService와 같은
// 패턴: Discord 타입을 모르는 순수 계산만 하고, Embed로 그리는 건 ClanStatsModule 몫.
// 겸사겸사 외부 코드리뷰에서 지적된 N+1 쿼리(챔피언 한 줄마다 플레이어 지분 쿼리를 따로 호출하던
// 부분)도 MatchRepository의 배치 쿼리(GetChampionPlayersBatchAsync 등)로 바꿔서 같이 해결함.

using static LolHelperBot.Services.ClanConstants;
using static LolHelperBot.Services.PositionOrder;

namespace LolHelperBot.Services;

public class ChampionTierService
{
    private const int TopTakeCount = 5;

    private readonly MatchRepository _matchRepository;

    public ChampionTierService(MatchRepository matchRepository)
    {
        _matchRepository = matchRepository;
    }

    /// <summary>
    /// 라인별 챔피언 티어 + 라인 무관 전체 워스트 챔피언을 계산합니다. 저장된 전적이 아예 없으면
    /// null을 반환합니다(호출부가 "먼저 /전적수집을 실행하라"는 안내 메시지를 보여줄 수 있도록).
    /// </summary>
    public async Task<ChampionTierResult?> BuildAsync(ulong guildId, string? positionFilter)
    {
        var rows = await _matchRepository.GetChampionTierAsync(guildId, FlexQueueId, positionFilter);
        if (rows.Count == 0)
        {
            return null;
        }

        var filtered = rows.Where(row => row.Games >= MinSampleSize).ToList();
        if (filtered.Count == 0)
        {
            filtered = rows.ToList();
        }

        var lineGroups = filtered
            .GroupBy(row => row.TeamPosition)
            .OrderBy(group => GetPositionOrder(group.Key))
            .Select(group => (
                Position: group.Key,
                TopRows: group
                    .OrderByDescending(row => row.Wins * 1.0 / row.Games)
                    .ThenByDescending(row => row.Games)
                    .Take(TopTakeCount)
                    .ToList()))
            .ToList();

        // N+1 쿼리 방지 — 모든 라인의 (라인, 챔피언) 쌍을 모아서 한 번에 조회합니다.
        var pairs = lineGroups
            .SelectMany(line => line.TopRows.Select(row => (line.Position, row.ChampionName)))
            .Distinct()
            .ToList();
        var playersByPair = await _matchRepository.GetChampionPlayersBatchAsync(guildId, FlexQueueId, pairs);

        var lines = lineGroups
            .Select(line => new ChampionTierLine(
                line.Position,
                line.TopRows
                    .Select(row => new ChampionTierEntry(
                        row.ChampionName,
                        row.Games,
                        row.Wins,
                        playersByPair.GetValueOrDefault((line.Position, row.ChampionName), [])))
                    .ToList()))
            .ToList();

        // 라인 구분 없이 전체 챔피언 중 승률 워스트 5개.
        var overallRows = await _matchRepository.GetOverallChampionStatsAsync(guildId, FlexQueueId);
        var overallSampled = overallRows.Where(row => row.Games >= MinSampleSize).ToList();
        if (overallSampled.Count == 0)
        {
            overallSampled = overallRows.ToList();
        }

        var worstOverallRows = overallSampled
            .Where(row => row.Wins * 1.0 / row.Games < 0.5)
            .OrderBy(row => row.Wins * 1.0 / row.Games)
            .ThenByDescending(row => row.Games)
            .Take(TopTakeCount)
            .ToList();

        var worstChampionNames = worstOverallRows.Select(row => row.ChampionName).ToList();
        var overallPlayersByChampion = await _matchRepository.GetOverallChampionPlayersBatchAsync(
            guildId, FlexQueueId, worstChampionNames);

        var worstOverall = worstOverallRows
            .Select(row => new ChampionTierEntry(
                row.ChampionName,
                row.Games,
                row.Wins,
                overallPlayersByChampion.GetValueOrDefault(row.ChampionName, [])))
            .ToList();

        return new ChampionTierResult(lines, worstOverall);
    }
}

/// <summary>티어픽 계산 결과. Discord 타입을 전혀 참조하지 않는 순수 데이터입니다.</summary>
public record ChampionTierResult(
    IReadOnlyList<ChampionTierLine> Lines,
    IReadOnlyList<ChampionTierEntry> WorstOverall);

public record ChampionTierLine(string Position, IReadOnlyList<ChampionTierEntry> TopChampions);

public record ChampionTierEntry(string ChampionName, int Games, int Wins, IReadOnlyList<ChampionPlayerRow> Players);

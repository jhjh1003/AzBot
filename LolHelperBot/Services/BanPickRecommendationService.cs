// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-20
// Reviewer: (박정훈)
// Review: 리팩토링 2단계 — 서비스 계층 추출 1호. 외부 코드리뷰에서 지적한 "커맨드 핸들러가
// 필터링·집계·추천 로직까지 직접 수행한다"는 문제를, 가장 최근에 만든(그래서 구조가 아직 생생한)
// /밴픽추천부터 시범 삼아 뜯어냄. 이 클래스는 Discord 타입을 전혀 모르는 순수 데이터 계산만
// 하고, 결과를 어떻게 임베드로 그릴지는 ClanStatsModule.ShowBanPickRecommendationAsync가
// 담당합니다("서비스 호출 → Embed 변환"으로 역할 분리).
//
// 앞으로 다른 커맨드(티어픽, 명예의전당 등)도 같은 패턴으로 옮길 때 이 파일을 템플릿으로 참고하면 됩니다.

using static LolHelperBot.Services.ClanConstants;

namespace LolHelperBot.Services;

public class BanPickRecommendationService
{
    private const int PerLineTakeCount = 3;

    private readonly MatchRepository _matchRepository;
    private readonly MetaTierRepository _metaTierRepository;

    public BanPickRecommendationService(MatchRepository matchRepository, MetaTierRepository metaTierRepository)
    {
        _matchRepository = matchRepository;
        _metaTierRepository = metaTierRepository;
    }

    /// <summary>
    /// 밴픽 추천을 계산합니다. 저장된 자유 랭크 전적이 아예 없으면 null을 반환합니다
    /// (호출부가 "먼저 /atoz 전적수집을 실행하라"는 안내 메시지를 보여줄 수 있도록).
    /// </summary>
    public async Task<BanPickRecommendation?> BuildAsync(ulong guildId, string? positionFilter)
    {
        var positionsToShow = positionFilter is null
            ? new[] { "TOP", "JUNGLE", "MIDDLE", "BOTTOM", "UTILITY" }
            : new[] { positionFilter };

        var tierRows = await _matchRepository.GetChampionTierAsync(guildId, FlexQueueId, positionFilter);
        if (tierRows.Count == 0)
        {
            return null;
        }

        var metaSnapshot = await _metaTierRepository.LoadAsync();

        var lines = new List<BanPickLineResult>();
        foreach (var pos in positionsToShow)
        {
            lines.Add(await BuildLineAsync(guildId, pos, tierRows, metaSnapshot));
        }

        return new BanPickRecommendation(lines, metaSnapshot is not null, metaSnapshot?.UpdatedAt);
    }

    private async Task<BanPickLineResult> BuildLineAsync(
        ulong guildId,
        string position,
        IReadOnlyList<ChampionTierRow> tierRows,
        MetaTierSnapshot? metaSnapshot)
    {
        var lineTierRows = tierRows.Where(row => row.TeamPosition == position).ToList();
        var metaEntries = metaSnapshot?.GetForPosition(position) ?? [];
        var selectedMetaEntries = metaEntries
            .Where(entry => IsTopMetaTier(entry.Tier))
            .OrderBy(entry => GetTierRank(entry.Tier))
            .ThenByDescending(GetMetaPowerScore)
            .Take(PerLineTakeCount)
            .ToList();
        var metaPairs = selectedMetaEntries
            .Select(entry => (position, entry.Champion))
            .ToList();
        var metaPlayersByPair = await _matchRepository.GetChampionPlayersBatchAsync(
            guildId, FlexQueueId, metaPairs);
        var metaPicks = selectedMetaEntries
            .Select(entry =>
            {
                var players = metaPlayersByPair.GetValueOrDefault((position, entry.Champion), []);
                return new BanPickMetaCandidate(
                    entry.Champion,
                    entry.Tier,
                    entry.WinRate,
                    entry.PickRate,
                    entry.BanRate,
                    players.Sum(player => player.Games),
                    players.Sum(player => player.Wins),
                    players);
            })
            .ToList();

        if (lineTierRows.Count == 0)
        {
            return new BanPickLineResult(position, HasData: false, [], metaPicks, []);
        }

        var sampledLineTierRows = lineTierRows.Where(row => row.Games >= MinSampleSize).ToList();
        if (sampledLineTierRows.Count == 0)
        {
            sampledLineTierRows = lineTierRows;
        }

        // --- 픽 추천: 클랜 데이터 기준 라인 베스트픽 Top3 (+ 메타 스냅샷에 카운터 정보가 있으면 같이) ---
        var metaByChampion = metaEntries.ToDictionary(entry => entry.Champion, StringComparer.OrdinalIgnoreCase);
        var picks = sampledLineTierRows
            .Where(row => row.Wins * 1.0 / row.Games >= 0.5)
            .OrderByDescending(row => row.Wins * 1.0 / row.Games)
            .ThenByDescending(row => row.Games)
            .Take(PerLineTakeCount)
            .Select(row =>
            {
                metaByChampion.TryGetValue(row.ChampionName, out var metaEntry);
                return new BanPickPickCandidate(
                    row.ChampionName,
                    row.Games,
                    row.Wins,
                    metaEntry?.Counters ?? [],
                    metaEntry?.Tier,
                    metaEntry is not null && IsTopMetaTier(metaEntry.Tier));
            })
            .ToList();

        // --- 밴 추천: 아래 3가지 기준에서 각각 1개씩, 중복 없이 ---
        var bans = new List<BanPickBanCandidate>();
        var alreadyBanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // (1) 맞상대 승률 낮은 챔프 — 이 라인에서 우리가 누굴 만나든 승률이 안 좋았던 상대.
        var opponentRows = await _matchRepository.GetOpponentChampionStatsAsync(guildId, FlexQueueId, position);
        var worstOpponent = opponentRows
            .Where(row => row.Games >= MinSampleSize && row.Wins * 1.0 / row.Games < 0.5)
            .OrderBy(row => row.Wins * 1.0 / row.Games)
            .ThenByDescending(row => row.Games)
            .FirstOrDefault();
        if (worstOpponent is not null)
        {
            bans.Add(BanPickBanCandidate.WorstOpponent(worstOpponent.ChampionName, worstOpponent.Games, worstOpponent.Wins));
            alreadyBanned.Add(worstOpponent.ChampionName);
        }

        // (2) 메타 티어 높은 챔프 — op.gg 수동 스냅샷 기준 (파일이 없거나 비어 있으면 자연스럽게 건너뜀).
        var metaCandidate = metaSnapshot?.GetForPosition(position)
            .Where(entry => !alreadyBanned.Contains(entry.Champion))
            .OrderBy(entry => GetTierRank(entry.Tier))
            .ThenByDescending(entry => entry.WinRate)
            .FirstOrDefault();
        if (metaCandidate is not null)
        {
            bans.Add(BanPickBanCandidate.ForMetaTier(metaCandidate.Champion, metaCandidate.Tier, metaCandidate.WinRate));
            alreadyBanned.Add(metaCandidate.Champion);
        }

        // (3) 우리 AZ 티어픽 상대 카운터 — 이 라인 베스트픽들이 잡았을 때 유독 승률이 안 나온 상대.
        var ourTopPicks = sampledLineTierRows
            .OrderByDescending(row => row.Wins * 1.0 / row.Games)
            .ThenByDescending(row => row.Games)
            .Take(PerLineTakeCount)
            .Select(row => row.ChampionName)
            .ToList();
        var matchupRows = await _matchRepository.GetMatchupStatsAsync(guildId, FlexQueueId, position, ourTopPicks);
        var matchupCandidate = matchupRows
            .Where(row => row.Games >= 3 && row.Wins * 1.0 / row.Games < 0.5 && !alreadyBanned.Contains(row.ChampionName))
            .OrderBy(row => row.Wins * 1.0 / row.Games)
            .ThenByDescending(row => row.Games)
            .FirstOrDefault();
        if (matchupCandidate is not null)
        {
            bans.Add(BanPickBanCandidate.OurPickCounter(
                matchupCandidate.ChampionName, matchupCandidate.Games, matchupCandidate.Wins, ourTopPicks));
        }

        return new BanPickLineResult(position, HasData: true, picks, metaPicks, bans);
    }

    private static bool IsTopMetaTier(string tier) =>
        tier.Equals("OP", StringComparison.OrdinalIgnoreCase) ||
        tier.Equals("1", StringComparison.OrdinalIgnoreCase) ||
        tier.StartsWith("S", StringComparison.OrdinalIgnoreCase);

    // 승률 50% 초과분을 두 배로 반영하고 픽률+밴률을 더해, 성능과 실제 밴픽 존재감을 함께 봅니다.
    private static double GetMetaPowerScore(MetaTierEntry entry) =>
        ((entry.WinRate - 50.0) * 2.0) + entry.PickRate + entry.BanRate;

    // op.gg 숫자 티어(1 > 2 > ...)와 예전 문자 티어(S > A > ...)를 모두 지원합니다.
    private static int GetTierRank(string tier)
    {
        if (string.IsNullOrEmpty(tier))
        {
            return 9;
        }

        return char.ToUpperInvariant(tier[0]) switch
        {
            'O' => 0,
            '1' => 1,
            '2' => 2,
            '3' => 3,
            '4' => 4,
            '5' => 5,
            'S' => 0,
            'A' => 1,
            'B' => 2,
            'C' => 3,
            'D' => 4,
            _ => 99,
        };
    }
}

/// <summary>밴픽추천 전체 결과. Discord 타입을 전혀 참조하지 않는 순수 데이터입니다.</summary>
public record BanPickRecommendation(
    IReadOnlyList<BanPickLineResult> Lines,
    bool HasMetaSnapshot,
    string? MetaSnapshotUpdatedAt);

/// <summary>한 라인(TOP/JUNGLE/...)의 픽/밴 추천. HasData가 false면 그 라인은 데이터가 아예 없다는 뜻.</summary>
public record BanPickLineResult(
    string Position,
    bool HasData,
    IReadOnlyList<BanPickPickCandidate> Picks,
    IReadOnlyList<BanPickMetaCandidate> MetaPicks,
    IReadOnlyList<BanPickBanCandidate> Bans);

public record BanPickPickCandidate(
    string ChampionName,
    int Games,
    int Wins,
    IReadOnlyList<string> MetaCounters,
    string? MetaTier,
    bool IsHoneyPick);

public record BanPickMetaCandidate(
    string ChampionName,
    string Tier,
    double WinRate,
    double PickRate,
    double BanRate,
    int AzGames,
    int AzWins,
    IReadOnlyList<ChampionPlayerRow> Players);

public enum BanReasonKind
{
    /// <summary>맞상대로 만났을 때 우리 승률이 낮았던 챔피언 (클랜 데이터).</summary>
    WorstOpponent,

    /// <summary>op.gg 수동 스냅샷 기준 메타 티어가 높은 챔피언.</summary>
    MetaTier,

    /// <summary>우리 라인 베스트픽들이 상대했을 때 유독 승률이 안 나온 챔피언 (클랜 데이터).</summary>
    OurPickCounter,
}

/// <summary>
/// 밴 후보 1개. Reason에 따라 어떤 필드가 채워지는지가 다릅니다(WorstOpponent/OurPickCounter는
/// Games·Wins, MetaTier는 MetaTier·MetaWinRate, OurPickCounter는 추가로 OurTopPicks).
/// </summary>
public record BanPickBanCandidate(
    BanReasonKind Reason,
    string ChampionName,
    int Games,
    int Wins,
    string? MetaTier = null,
    double? MetaWinRate = null,
    IReadOnlyList<string>? OurTopPicks = null)
{
    public static BanPickBanCandidate WorstOpponent(string championName, int games, int wins) =>
        new(BanReasonKind.WorstOpponent, championName, games, wins);

    public static BanPickBanCandidate ForMetaTier(string championName, string tier, double winRate) =>
        new(BanReasonKind.MetaTier, championName, Games: 0, Wins: 0, MetaTier: tier, MetaWinRate: winRate);

    public static BanPickBanCandidate OurPickCounter(string championName, int games, int wins, IReadOnlyList<string> ourTopPicks) =>
        new(BanReasonKind.OurPickCounter, championName, games, wins, OurTopPicks: ourTopPicks);
}

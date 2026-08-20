// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-19
// Reviewer: (박정훈)
// Review: 게임별 기여도 점수(/아재전적, /명예의전당) 계산기.
// 가중치는 코드가 아니라 Config/ContributionScoreWeights.txt 파일에서 읽어옵니다 — 숫자 튜닝은
// 코드를 안 건드리고 그 파일만 고치면 됩니다(봇 재시작 필요).
//
// v3 설계 (맞라인 상대 비교): 처음엔 "같은 팀 5명끼리 비율/순위 비교"(A안) 방식으로 만들었는데,
// 실데이터로 검증해보니 원딜/서폿처럼 특정 지표를 구조적으로 독점하는 라인이 계속 유리하고
// 정글처럼 어느 지표도 확실히 못 이기는 라인은 계속 불리한 구조적 편향이 있었습니다
// (자세한 검증 로그는 CHANGELOG.md 참고). 그래서 사용자 제안대로 "같은 팀 5명"이 아니라
// **같은 라인 상대(맞라인)와의 1:1 비교**로 축을 바꿨습니다 — 정글도 "정글끼리", 서폿도
// "서폿끼리"만 비교하니 라인 간 구조적 차이가 훨씬 줄어듭니다.
//
// 지표별로 "나 vs 맞라인 상대"의 2자 비율(0~100, 50이 동률)을 구하고, 라인별 가중치를 곱해서
// 가중평균을 냅니다. 딜량 격차가 크게 벌어지면(예: 탱커형 vs 딜러형 탑처럼 역할이 다른 경우)
// 받은 피해량·CC로 상쇄해주는 보정항도 있습니다(damage_deficit_offset).
// 킬 관여율만 예외로 "같은 팀 5명 중 관여 비율"을 그대로 씁니다(맞라인 상대가 아니라 우리 팀
// 전체에 얼마나 기여했는지를 보는 지표라서).

namespace LolHelperBot.Services;

public class ContributionScoreCalculator
{
    private readonly string _weightsFilePath;
    private IReadOnlyDictionary<string, Dictionary<string, double>>? _cachedWeights;

    public ContributionScoreCalculator(string weightsFilePath)
    {
        _weightsFilePath = weightsFilePath;
    }

    /// <summary>
    /// 같은 매치·같은 팀의 AtoZ 멤버 참가 기록(정확히 5명)을 받아 기여도 점수 순위를 매깁니다.
    /// 본인의 기본 지표(딜량/받은피해/골드/시야/CC)가 없으면 계산 자체가 불가능해 null을 반환합니다
    /// (예: `.rofl` 업로드로만 저장된 경기). 맞라인 상대 지표가 없는 경우는 그 지표만 중립값(50점)
    /// 으로 대체해서, 한두 명 상대 정보가 빠져도 나머지 인원 순위는 정상적으로 나오게 합니다.
    /// </summary>
    public IReadOnlyList<ContributionScoreRow>? TryCalculate(IReadOnlyList<ClanMatchParticipantRow> teamOfFive)
    {
        if (teamOfFive.Count != 5)
        {
            return null;
        }

        if (teamOfFive.Any(p =>
                p.DamageDealt is null || p.DamageTaken is null || p.DamageMitigated is null ||
                p.GoldEarned is null || p.VisionScore is null || p.CcTimeDealt is null))
        {
            return null;
        }

        var weights = GetWeights();

        // 킬 관여율은 맞라인 상대가 아니라 "우리 팀 5명 중 비율"을 그대로 씁니다.
        var totalKills = teamOfFive.Sum(p => p.Kills);
        var killParticipationByUser = teamOfFive.ToDictionary(
            p => p.DiscordUserId,
            p => totalKills > 0 ? (p.Kills + p.Assists) * 100.0 / totalKills : 0.0);

        var scored = teamOfFive.Select(p =>
        {
            var lineWeights = weights.GetValueOrDefault(p.TeamPosition, EmptyWeights);

            // 나 vs 맞라인 상대 2자 비율(0~100, 50=동률). 상대 지표가 없으면 중립값 50으로 처리.
            double Advantage(double? mine, double? theirs)
            {
                if (mine is null) return 50.0;
                if (theirs is null || theirs < 0) return 50.0;
                var sum = mine.Value + theirs.Value;
                return sum > 0 ? mine.Value * 100.0 / sum : 50.0;
            }

            var goldAdv = Advantage(p.GoldEarned, p.OpponentGoldEarned);
            var csAdv = Advantage(p.CreepScore, p.OpponentCreepScore);
            var damageDealtAdv = Advantage(p.DamageDealt, p.OpponentDamageDealt);
            var damageTakenAdv = Advantage(p.DamageTaken, p.OpponentDamageTaken);
            var ccAdv = Advantage(p.CcTimeDealt, p.OpponentCcTimeDealt);
            var visionAdv = Advantage(p.VisionScore, p.OpponentVisionScore);
            var wardsAdv = Advantage(p.WardsPlaced, p.OpponentWardsPlaced);
            var healAdv = Advantage(p.HealAmount, p.OpponentHealAmount);
            var objectivesAdv = Advantage(p.DamageToObjectives, p.OpponentDamageToObjectives);

            var myKda = Kda(p.Kills, p.Deaths, p.Assists);
            var opponentKda = p.OpponentKills is null || p.OpponentDeaths is null || p.OpponentAssists is null
                ? (double?)null
                : Kda(p.OpponentKills.Value, p.OpponentDeaths.Value, p.OpponentAssists.Value);
            var kdaAdv = Advantage(myKda, opponentKda);

            // 딜량 격차 보정: 딜량에서 많이 밀렸을수록(deficit), 받은피해·CC가 평균(50) 이상인 만큼
            // 보너스를 줍니다. 탱커형 라이너가 딜은 못 넣어도 몸빵/CC로 기여한 걸 인정해주기 위함.
            var damageDeficit = Math.Max(0.0, 50.0 - damageDealtAdv) / 50.0;
            var offsetSource = Math.Max(0.0, damageTakenAdv - 50.0) + Math.Max(0.0, ccAdv - 50.0);
            var damageDeficitOffset = damageDeficit * offsetSource;

            // 서폿 등에서 "CC와 힐 중 더 크게 기여한 쪽"에 가중치를 주기 위한 항.
            var ccOrHeal = Math.Max(ccAdv, healAdv);

            var weightedSum =
                lineWeights.GetValueOrDefault("gold_earned") * goldAdv +
                lineWeights.GetValueOrDefault("creep_score") * csAdv +
                lineWeights.GetValueOrDefault("damage_dealt") * damageDealtAdv +
                lineWeights.GetValueOrDefault("damage_taken") * damageTakenAdv +
                lineWeights.GetValueOrDefault("cc_time") * ccAdv +
                lineWeights.GetValueOrDefault("vision_score") * visionAdv +
                lineWeights.GetValueOrDefault("wards_placed") * wardsAdv +
                lineWeights.GetValueOrDefault("heal_amount") * healAdv +
                lineWeights.GetValueOrDefault("damage_to_objectives") * objectivesAdv +
                lineWeights.GetValueOrDefault("kda") * kdaAdv +
                lineWeights.GetValueOrDefault("kill_participation") * killParticipationByUser[p.DiscordUserId] +
                lineWeights.GetValueOrDefault("damage_deficit_offset") * damageDeficitOffset +
                lineWeights.GetValueOrDefault("cc_or_heal") * ccOrHeal;

            // 라인마다 가중치 개수/합이 다르면 가중치 합이 큰 라인이 항상 유리해지므로,
            // 가중치 총합으로 나눠서(가중평균) 모든 라인의 만점을 100점으로 통일합니다.
            var totalWeight = lineWeights.Values.Sum();
            var score = totalWeight > 0 ? weightedSum / totalWeight : 0.0;

            return (Participant: p, Score: score);
        })
        .OrderByDescending(x => x.Score)
        .ToList();

        return scored
            .Select((x, index) => new ContributionScoreRow(x.Participant, x.Score, index + 1))
            .ToList();
    }

    private static double Kda(int kills, int deaths, int assists) =>
        (kills + assists) / (double)Math.Max(1, deaths);

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
            Console.Error.WriteLine($"[기여도 점수] 가중치 파일을 찾을 수 없습니다: {_weightsFilePath} — 기여도 점수가 전부 0으로 계산됩니다.");
        }

        _cachedWeights = weights;
        return weights;
    }
}

/// <summary>
/// 한 명의 기여도 점수 계산 결과. Rank 1이 그 판의 베스트(👑), Rank 5가 워스트(💀)입니다.
/// </summary>
public record ContributionScoreRow(ClanMatchParticipantRow Participant, double Score, int Rank);

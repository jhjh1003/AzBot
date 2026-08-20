// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-20
// Reviewer: (박정훈)
// Review: /밴픽추천 2단계 — 일반 메타(op.gg) 티어/카운터픽 데이터를 붙이는 부분.
// AfterUpgrade.md에 남겼던 대로 op.gg는 ToS가 불확실해서 자동 크롤링은 보류하고,
// 대신 Config/MetaTierSnapshot.json에 사용자가 주기적으로 수동 스냅샷을 채워넣는 방식으로 갑니다.
// 이 파일이 없거나 비어 있어도 클랜 자체 데이터 기반 추천(픽/일부 밴)은 그대로 동작해야 하므로,
// 못 읽으면 예외를 던지지 않고 null을 반환해서 호출부가 "메타 데이터 없음"으로 우아하게 처리합니다.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace LolHelperBot.Services;

public class MetaTierRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _snapshotFilePath;

    public MetaTierRepository(string snapshotFilePath)
    {
        _snapshotFilePath = snapshotFilePath;
    }

    public async Task<MetaTierSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_snapshotFilePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_snapshotFilePath);
            var raw = await JsonSerializer.DeserializeAsync<SnapshotFileResponse>(stream, JsonOptions, cancellationToken);
            if (raw?.Positions is null || raw.Positions.Count == 0)
            {
                return null;
            }

            var positions = raw.Positions
                .Where(pair => pair.Value is { Count: > 0 })
                .ToDictionary(
                    pair => pair.Key.ToUpperInvariant(),
                    pair => (IReadOnlyList<MetaTierEntry>)pair.Value!
                        .Where(entry => !string.IsNullOrWhiteSpace(entry.Champion))
                        .Select(entry => new MetaTierEntry(
                            entry.Champion!,
                            entry.Tier ?? "?",
                            entry.WinRate,
                            entry.PickRate,
                            entry.BanRate,
                            entry.Counters ?? []))
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);

            if (positions.Count == 0)
            {
                return null;
            }

            return new MetaTierSnapshot(raw.UpdatedAt, raw.Source, positions);
        }
        catch (Exception ex)
        {
            // 사용자가 수동으로 채워넣는 파일이라 JSON 형식이 깨질 수 있습니다 — 조용히 null 반환하고
            // 콘솔에만 원인을 남깁니다 (밴픽추천의 클랜 데이터 기반 부분은 이 파일 없이도 동작해야 함).
            Console.Error.WriteLine($"[메타 티어 스냅샷 로드 오류] {_snapshotFilePath}: {ex.Message}");
            return null;
        }
    }

    private class SnapshotFileResponse
    {
        [JsonPropertyName("updatedAt")]
        public string? UpdatedAt { get; set; }

        [JsonPropertyName("source")]
        public string? Source { get; set; }

        [JsonPropertyName("positions")]
        public Dictionary<string, List<SnapshotEntryResponse>?>? Positions { get; set; }
    }

    private class SnapshotEntryResponse
    {
        [JsonPropertyName("champion")]
        public string? Champion { get; set; }

        [JsonPropertyName("tier")]
        public string? Tier { get; set; }

        [JsonPropertyName("winRate")]
        public double WinRate { get; set; }

        [JsonPropertyName("pickRate")]
        public double PickRate { get; set; }

        [JsonPropertyName("banRate")]
        public double BanRate { get; set; }

        [JsonPropertyName("counters")]
        public List<string>? Counters { get; set; }
    }
}

public record MetaTierEntry(
    string Champion,
    string Tier,
    double WinRate,
    double PickRate,
    double BanRate,
    IReadOnlyList<string> Counters);

public record MetaTierSnapshot(
    string? UpdatedAt,
    string? Source,
    IReadOnlyDictionary<string, IReadOnlyList<MetaTierEntry>> PositionsByLine)
{
    public IReadOnlyList<MetaTierEntry> GetForPosition(string position) =>
        PositionsByLine.TryGetValue(position, out var list) ? list : [];

    /// <summary>
    /// 어느 라인 목록에 있든 상관없이 챔피언명으로 카운터 목록을 찾습니다 (표기용).
    /// </summary>
    public IReadOnlyList<string> GetCounters(string championName)
    {
        foreach (var entries in PositionsByLine.Values)
        {
            var match = entries.FirstOrDefault(entry =>
                string.Equals(entry.Champion, championName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match.Counters;
            }
        }

        return [];
    }
}

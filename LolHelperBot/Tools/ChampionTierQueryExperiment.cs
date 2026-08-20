// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-20
// Reviewer: (박정훈)
// Review: /티어픽 리팩토링(ChampionTierService 추출 + N+1 쿼리를 배치 쿼리로 교체) 검증용
// 1회성 스모크 테스트. `dotnet run -- tier-test [라인]`으로 실행. 특히 SQLite의 row-value
// IN 문법(`(team_position, champion_name) IN ((...),(...))`)이 이 프로젝트가 쓰는
// Microsoft.Data.Sqlite/SQLitePCLRaw 버전에서 실제로 동작하는지가 이번 변경의 핵심 리스크라,
// 서비스 결과와 완전히 독립적인 원시 단일 쿼리를 하나 더 돌려서 숫자가 일치하는지 대조합니다.

using LolHelperBot.Services;
using Microsoft.Data.Sqlite;
using static LolHelperBot.Services.ClanConstants;

namespace LolHelperBot.Tools;

public static class ChampionTierQueryExperiment
{
    public static async Task RunAsync(
        MatchRepository matchRepository,
        ChampionTierService championTierService,
        string databasePath,
        ulong guildId,
        string? positionArg)
    {
        var result = await championTierService.BuildAsync(guildId, positionArg?.ToUpperInvariant());
        if (result is null)
        {
            Console.WriteLine("[tier-test] 데이터 없음");
            return;
        }

        foreach (var line in result.Lines)
        {
            Console.WriteLine($"\n===== {line.Position} =====");
            foreach (var entry in line.TopChampions)
            {
                var shareText = string.Join(", ", entry.Players.Select(p => $"{p.DiscordUserId}:{p.Games}판/{p.Wins}승"));
                Console.WriteLine($"  {entry.ChampionName}: {entry.Games}판 {entry.Wins}승 — 지분[{shareText}]");
            }
        }

        Console.WriteLine("\n===== 전체 워스트 =====");
        foreach (var entry in result.WorstOverall)
        {
            var shareText = string.Join(", ", entry.Players.Select(p => $"{p.DiscordUserId}:{p.Games}판/{p.Wins}승"));
            Console.WriteLine($"  {entry.ChampionName}: {entry.Games}판 {entry.Wins}승 — 지분[{shareText}]");
        }

        // --- 회귀 확인: 첫 라인의 1위 챔피언 지분을 배치 쿼리와 무관한 원시 단일 쿼리로 재계산해서 대조 ---
        var firstEntry = result.Lines.FirstOrDefault(l => l.TopChampions.Count > 0)?.TopChampions.First();
        var firstLine = result.Lines.FirstOrDefault(l => l.TopChampions.Count > 0);
        if (firstEntry is null || firstLine is null)
        {
            return;
        }

        Console.WriteLine($"\n[회귀 확인] {firstLine.Position}/{firstEntry.ChampionName} 지분을 원시 단일 쿼리로 재계산:");
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT discord_user_id, COUNT(*) AS games, SUM(win) AS wins
            FROM match_participations
            WHERE guild_id = $guildId AND queue_id = $queueId
                AND team_position = $position AND champion_name = $champion
            GROUP BY discord_user_id
            ORDER BY games DESC;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());
        command.Parameters.AddWithValue("$queueId", FlexQueueId);
        command.Parameters.AddWithValue("$position", firstLine.Position);
        command.Parameters.AddWithValue("$champion", firstEntry.ChampionName);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Console.WriteLine($"  (원시쿼리) {reader.GetString(0)}: {reader.GetInt32(1)}판/{reader.GetInt32(2)}승");
        }
    }
}

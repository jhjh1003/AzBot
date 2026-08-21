// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-21
// Reviewer: (박정훈)
// Review: "/atoz 부캐충돌해결"이 "미해결 충돌 기록을 찾지 못했습니다"를 내는 문제 진단용 1회성 도구.
// match_owner_conflicts에 저장된 riot_game_name의 실제 문자(코드포인트)를 하나씩 찍어서, 화면에
// 보이는 공백이 일반 스페이스(U+0020)인지 다른 유니코드 공백류 문자인지 확인합니다.
// `dotnet run -- inspect-conflict <matchId>` (읽기 전용 — DB 안 건드림).

using Microsoft.Data.Sqlite;

namespace LolHelperBot.Tools;

public static class ConflictInspectExperiment
{
    public static async Task RunAsync(string databasePath, string matchId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT guild_id, match_id, team_id, puuid, riot_game_name, riot_tag_line,
                champion_name, team_position, default_owner_discord_user_id, resolved, detected_at_utc
            FROM match_owner_conflicts
            WHERE match_id = $matchId;
            """;
        command.Parameters.AddWithValue("$matchId", matchId);

        await using var reader = await command.ExecuteReaderAsync();
        var found = false;
        while (await reader.ReadAsync())
        {
            found = true;
            var gameName = reader.GetString(4);
            var tagLine = reader.GetString(5);
            Console.WriteLine($"guild_id={reader.GetString(0)} match_id={reader.GetString(1)} team_id={reader.GetInt32(2)}");
            Console.WriteLine($"puuid={reader.GetString(3)}");
            Console.WriteLine($"riot_game_name=\"{gameName}\" (길이={gameName.Length})");
            Console.WriteLine("  코드포인트: " + string.Join(" ", gameName.Select(c => $"U+{(int)c:X4}({c})")));
            Console.WriteLine($"riot_tag_line=\"{tagLine}\" (길이={tagLine.Length})");
            Console.WriteLine("  코드포인트: " + string.Join(" ", tagLine.Select(c => $"U+{(int)c:X4}({c})")));
            Console.WriteLine($"champion_name={reader.GetString(6)} team_position={reader.GetString(7)}");
            Console.WriteLine($"default_owner_discord_user_id={reader.GetString(8)} resolved={reader.GetInt32(9)} detected_at_utc={reader.GetString(10)}");
            Console.WriteLine();
        }

        if (!found)
        {
            Console.WriteLine($"[inspect-conflict] match_owner_conflicts에 match_id={matchId} 행이 없습니다.");
        }
    }
}

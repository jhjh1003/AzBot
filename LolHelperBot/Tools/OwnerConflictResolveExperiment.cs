// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-21
// Reviewer: (박정훈)
// Review: "/atoz 부캐충돌해결"과 완전히 동일한 로직을 콘솔에서 여러 매치에 한 번에 돌리기 위한
// 도구입니다(디스코드 슬래시커맨드는 한 번에 매치 하나씩만 처리 가능 — 여러 건 처리할 때 편의용).
// ClanStatsModule.BuildParticipationStats를 그대로 재사용해서 로직 중복이 없습니다.
// `dotnet run -- resolve-conflict <riotId> <memberDisplayNameContains> <matchId1> [matchId2...]`

using LolHelperBot.Modules;
using LolHelperBot.Services;
using Microsoft.Data.Sqlite;
using static LolHelperBot.Services.RiotIdParser;

namespace LolHelperBot.Tools;

public static class OwnerConflictResolveExperiment
{
    public static async Task RunAsync(
        RiotApiClient riotApiClient,
        MatchRepository matchRepository,
        string databasePath,
        ulong guildId,
        string riotId,
        string memberDisplayNameContains,
        IReadOnlyList<string> matchIds)
    {
        if (!TryParseRiotId(riotId, out var gameName, out var tagLine))
        {
            Console.WriteLine("[resolve-conflict] 롤아이디를 `게임이름#태그` 형식으로 입력하세요.");
            return;
        }

        // 멤버 닉네임으로 discord_user_id를 찾음(정확히 일치하는 이름 우선 — CaitlynBuildExperiment와 같은 패턴).
        ulong memberDiscordUserId;
        string memberName;
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString()))
        {
            await connection.OpenAsync();
            var memberCommand = connection.CreateCommand();
            memberCommand.CommandText = "SELECT discord_user_id, discord_display_name FROM members WHERE guild_id = $guildId AND discord_display_name LIKE $pattern LIMIT 5;";
            memberCommand.Parameters.AddWithValue("$guildId", guildId.ToString());
            memberCommand.Parameters.AddWithValue("$pattern", $"%{memberDisplayNameContains}%");

            var candidates = new List<(ulong UserId, string Name)>();
            await using var reader = await memberCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                candidates.Add((ulong.Parse(reader.GetString(0)), reader.GetString(1)));
            }

            if (candidates.Count == 0)
            {
                Console.WriteLine($"[resolve-conflict] 닉네임에 '{memberDisplayNameContains}'가 들어간 멤버를 못 찾았습니다.");
                return;
            }

            var exact = candidates.FirstOrDefault(c => c.Name == memberDisplayNameContains);
            var chosen = exact.Name is not null ? exact : candidates[0];
            if (candidates.Count > 1)
            {
                Console.WriteLine($"[resolve-conflict] 여러 명 매칭됨: {string.Join(", ", candidates.Select(c => c.Name))} — '{chosen.Name}'로 진행합니다.");
            }

            memberDiscordUserId = chosen.UserId;
            memberName = chosen.Name;
        }

        Console.WriteLine($"[resolve-conflict] 대상 멤버: {memberName} ({memberDiscordUserId}), 롤아이디: {gameName}#{tagLine}\n");

        foreach (var matchId in matchIds)
        {
            var conflict = await matchRepository.FindUnresolvedConflictAsync(guildId, matchId, gameName, tagLine);
            if (conflict is null)
            {
                Console.WriteLine($"  [{matchId}] ❌ 미해결 충돌 기록을 찾지 못했습니다(이미 해결됐거나 값이 안 맞음).");
                continue;
            }

            var matchResult = await riotApiClient.GetFullMatchAsync(matchId);
            if (!matchResult.IsSuccess || matchResult.Match is null)
            {
                Console.WriteLine($"  [{matchId}] ❌ 매치 재조회 실패: {matchResult.Message}");
                continue;
            }

            var participant = matchResult.Match.Participants.FirstOrDefault(p => p.Puuid == conflict.Puuid);
            if (participant is null)
            {
                Console.WriteLine($"  [{matchId}] ❌ 매치에서 해당 참가자를 찾지 못했습니다.");
                continue;
            }

            await matchRepository.DeleteParticipationIfMatchesAsync(guildId, matchId, conflict.DefaultOwnerDiscordUserId, conflict.Puuid);

            var opponent = matchResult.Match.Participants
                .FirstOrDefault(p => p.TeamId != participant.TeamId && p.TeamPosition == participant.TeamPosition);

            await matchRepository.SaveParticipationAsync(
                guildId,
                matchId,
                matchResult.Match.QueueId,
                matchResult.Match.GameDurationSeconds,
                matchResult.Match.GameCreatedAt,
                memberDiscordUserId,
                participant.Puuid,
                participant.TeamId,
                participant.ChampionName,
                participant.TeamPosition,
                participant.Win,
                participant.Kills,
                participant.Deaths,
                participant.Assists,
                participant.CreepScore,
                opponent?.ChampionName,
                AtoZModule.ClanStatsModule.BuildParticipationStats(participant, opponent));

            await matchRepository.MarkConflictResolvedAsync(guildId, matchId, conflict.Puuid);

            Console.WriteLine($"  [{matchId}] ✅ {gameName}#{tagLine}({participant.ChampionName}) 기록을 {memberName}로 저장했습니다.");
        }
    }
}

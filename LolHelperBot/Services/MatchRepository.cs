// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-19
// Reviewer: (박정훈)
// Review: 클랜 자유 랭크 전적 집계(티어픽/승률순위/조합추천)용 저장소.
// 등록된 AtoZ 멤버가 참여한 경기만 저장합니다 (비회원 참가자 데이터는 저장하지 않음).

using Microsoft.Data.Sqlite;

namespace LolHelperBot.Services;

public class MatchRepository
{
    private readonly string _connectionString;

    public MatchRepository(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("데이터베이스 경로가 올바르지 않습니다.", nameof(databasePath));
        Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS match_participations (
                guild_id TEXT NOT NULL,
                match_id TEXT NOT NULL,
                discord_user_id TEXT NOT NULL,
                puuid TEXT NOT NULL,
                queue_id INTEGER NOT NULL,
                team_id INTEGER NOT NULL,
                champion_name TEXT NOT NULL,
                team_position TEXT NOT NULL,
                opponent_champion_name TEXT,
                win INTEGER NOT NULL,
                kills INTEGER NOT NULL,
                deaths INTEGER NOT NULL,
                assists INTEGER NOT NULL,
                creep_score INTEGER NOT NULL,
                damage_dealt INTEGER,
                damage_taken INTEGER,
                damage_mitigated INTEGER,
                gold_earned INTEGER,
                vision_score INTEGER,
                cc_time_dealt INTEGER,
                heal_amount INTEGER,
                wards_placed INTEGER,
                damage_to_objectives INTEGER,
                opponent_kills INTEGER,
                opponent_deaths INTEGER,
                opponent_assists INTEGER,
                opponent_damage_dealt INTEGER,
                opponent_damage_taken INTEGER,
                opponent_gold_earned INTEGER,
                opponent_creep_score INTEGER,
                opponent_vision_score INTEGER,
                opponent_cc_time_dealt INTEGER,
                opponent_heal_amount INTEGER,
                opponent_wards_placed INTEGER,
                opponent_damage_to_objectives INTEGER,
                game_duration_seconds INTEGER NOT NULL,
                game_created_at_utc TEXT NOT NULL,
                collected_at_utc TEXT NOT NULL,
                PRIMARY KEY (guild_id, match_id, discord_user_id)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        // 기존에 만들어진 DB에는 아래 컬럼들이 없을 수 있으므로 없으면 추가합니다
        // (CREATE TABLE IF NOT EXISTS는 이미 있는 테이블의 컬럼을 바꿔주지 않기 때문).
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var checkColumnCommand = connection.CreateCommand();
        checkColumnCommand.CommandText = "PRAGMA table_info(match_participations);";
        await using (var reader = await checkColumnCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        var columnsToAdd = new (string Name, string SqlType)[]
        {
            ("opponent_champion_name", "TEXT"),
            ("damage_dealt", "INTEGER"),
            ("damage_taken", "INTEGER"),
            ("damage_mitigated", "INTEGER"),
            ("gold_earned", "INTEGER"),
            ("vision_score", "INTEGER"),
            ("cc_time_dealt", "INTEGER"),
            ("heal_amount", "INTEGER"),
            ("wards_placed", "INTEGER"),
            ("damage_to_objectives", "INTEGER"),
            ("opponent_kills", "INTEGER"),
            ("opponent_deaths", "INTEGER"),
            ("opponent_assists", "INTEGER"),
            ("opponent_damage_dealt", "INTEGER"),
            ("opponent_damage_taken", "INTEGER"),
            ("opponent_gold_earned", "INTEGER"),
            ("opponent_creep_score", "INTEGER"),
            ("opponent_vision_score", "INTEGER"),
            ("opponent_cc_time_dealt", "INTEGER"),
            ("opponent_heal_amount", "INTEGER"),
            ("opponent_wards_placed", "INTEGER"),
            ("opponent_damage_to_objectives", "INTEGER"),
        };

        foreach (var (columnName, sqlType) in columnsToAdd)
        {
            if (existingColumns.Contains(columnName))
            {
                continue;
            }

            var addColumnCommand = connection.CreateCommand();
            addColumnCommand.CommandText = $"ALTER TABLE match_participations ADD COLUMN {columnName} {sqlType};";
            await addColumnCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        // 5명 전원이 우리 멤버가 아니라서 저장하지 않은 매치도 "이미 확인함"으로 기록해서,
        // /전적수집을 다시 돌릴 때 Riot API로 같은 매치를 또 조회하지 않게 합니다.
        var checkedCommand = connection.CreateCommand();
        checkedCommand.CommandText = """
            CREATE TABLE IF NOT EXISTS checked_matches (
                guild_id TEXT NOT NULL,
                match_id TEXT NOT NULL,
                queue_id INTEGER NOT NULL,
                all_clan_saved INTEGER NOT NULL,
                checked_at_utc TEXT NOT NULL,
                PRIMARY KEY (guild_id, match_id)
            );
            """;
        await checkedCommand.ExecuteNonQueryAsync(cancellationToken);

        // 부캐를 여러 명이 돌려쓰다가 기본 소유자와 같은 경기·같은 팀에 동시에 나와서
        // discord_user_id가 겹치는 바람에 저장이 안 된 참가자를 기록해둡니다 (관리자가 나중에 수동으로 배정).
        var conflictCommand = connection.CreateCommand();
        conflictCommand.CommandText = """
            CREATE TABLE IF NOT EXISTS match_owner_conflicts (
                guild_id TEXT NOT NULL,
                match_id TEXT NOT NULL,
                team_id INTEGER NOT NULL,
                puuid TEXT NOT NULL,
                riot_game_name TEXT NOT NULL,
                riot_tag_line TEXT NOT NULL,
                champion_name TEXT NOT NULL,
                team_position TEXT NOT NULL,
                default_owner_discord_user_id TEXT NOT NULL,
                detected_at_utc TEXT NOT NULL,
                resolved INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (guild_id, match_id, puuid)
            );
            """;
        await conflictCommand.ExecuteNonQueryAsync(cancellationToken);

        // 기여도 점수 v4.0.0(15분 라인전/후반, 팀 승리 플랜 기준) — Timeline API로 미리 계산해둔
        // 최종 점수만 저장합니다(원자료 40여 컬럼 대신 계산 끝난 점수만 — ContributionScoreCalculatorV4
        // 참고). 2026-08-21, v4-backfill 명령으로 8월 매치부터 채움. 이 테이블에 없는 매치는
        // ShowAjaeMatchesAsync/ShowHonorBoardAsync가 v3로 자동 폴백합니다.
        var v4Command = connection.CreateCommand();
        v4Command.CommandText = """
            CREATE TABLE IF NOT EXISTS match_contribution_v4 (
                guild_id TEXT NOT NULL,
                match_id TEXT NOT NULL,
                discord_user_id TEXT NOT NULL,
                early_score REAL NOT NULL,
                late_score REAL NOT NULL,
                final_score REAL NOT NULL,
                computed_at_utc TEXT NOT NULL,
                PRIMARY KEY (guild_id, match_id, discord_user_id)
            );
            """;
        await v4Command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 기여도 점수 v4.0.0 — 매치 하나의 참가자별 최종 점수(봇듀오 블렌드까지 적용됨)를 저장합니다.
    /// 이미 있으면 덮어씁니다(가중치 파일을 튜닝하고 재계산할 수 있게).
    /// </summary>
    public async Task UpsertContributionV4Async(
        ulong guildId,
        string matchId,
        IReadOnlyList<(ulong DiscordUserId, double EarlyScore, double LateScore, double FinalScore)> scores,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        foreach (var (discordUserId, earlyScore, lateScore, finalScore) in scores)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO match_contribution_v4
                    (guild_id, match_id, discord_user_id, early_score, late_score, final_score, computed_at_utc)
                VALUES
                    ($guildId, $matchId, $discordUserId, $earlyScore, $lateScore, $finalScore, $computedAt)
                ON CONFLICT (guild_id, match_id, discord_user_id) DO UPDATE SET
                    early_score = excluded.early_score,
                    late_score = excluded.late_score,
                    final_score = excluded.final_score,
                    computed_at_utc = excluded.computed_at_utc;
                """;
            command.Parameters.AddWithValue("$guildId", guildId.ToString());
            command.Parameters.AddWithValue("$matchId", matchId);
            command.Parameters.AddWithValue("$discordUserId", discordUserId.ToString());
            command.Parameters.AddWithValue("$earlyScore", earlyScore);
            command.Parameters.AddWithValue("$lateScore", lateScore);
            command.Parameters.AddWithValue("$finalScore", finalScore);
            command.Parameters.AddWithValue("$computedAt", DateTimeOffset.UtcNow.ToString("o"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// 기여도 점수 v4.0.0 — 여러 매치의 참가자별 최종 점수를 한 번에 읽어옵니다(N+1 쿼리 방지).
    /// 반환 키는 (match_id, discord_user_id)이고, 이 테이블에 없는 매치는 결과에서 그냥 빠집니다
    /// (호출부가 v3로 폴백해야 함).
    /// </summary>
    public async Task<IReadOnlyDictionary<(string MatchId, ulong DiscordUserId), double>> GetContributionV4ScoresAsync(
        ulong guildId,
        IReadOnlyCollection<string> matchIds,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<(string, ulong), double>();
        if (matchIds.Count == 0)
        {
            return result;
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var placeholders = string.Join(",", matchIds.Select((_, i) => $"$m{i}"));
        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT match_id, discord_user_id, final_score
            FROM match_contribution_v4
            WHERE guild_id = $guildId AND match_id IN ({placeholders});
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());
        var matchIdList = matchIds.ToList();
        for (var i = 0; i < matchIdList.Count; i++)
        {
            command.Parameters.AddWithValue($"$m{i}", matchIdList[i]);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var matchId = reader.GetString(0);
            var discordUserId = ulong.Parse(reader.GetString(1));
            var finalScore = reader.GetDouble(2);
            result[(matchId, discordUserId)] = finalScore;
        }

        return result;
    }

    /// <summary>
    /// 이미 확인한(저장 여부와 무관하게) 매치ID는 걸러내서 돌려줍니다. 매치 상세 조회는 Riot API 호출 비용이 크므로,
    /// 이미 알고 있는 매치는 다시 조회하지 않기 위한 사전 필터링입니다.
    /// </summary>
    public async Task<HashSet<string>> FilterNewMatchIdsAsync(
        ulong guildId,
        IReadOnlyCollection<string> candidateMatchIds,
        CancellationToken cancellationToken = default)
    {
        var newIds = new HashSet<string>(candidateMatchIds, StringComparer.Ordinal);
        if (newIds.Count == 0)
        {
            return newIds;
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT match_id FROM checked_matches WHERE guild_id = $guildId
            UNION
            SELECT DISTINCT match_id FROM match_participations WHERE guild_id = $guildId;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            newIds.Remove(reader.GetString(0));
        }

        return newIds;
    }

    /// <summary>
    /// 매치를 확인했음을 기록합니다 (5명 전원 우리 멤버라 저장했는지 여부와 무관하게 다음 수집에서 다시 안 훑도록).
    /// </summary>
    public async Task MarkMatchCheckedAsync(
        ulong guildId,
        string matchId,
        int queueId,
        bool allClanSaved,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO checked_matches (guild_id, match_id, queue_id, all_clan_saved, checked_at_utc)
            VALUES ($guildId, $matchId, $queueId, $allClanSaved, $checkedAt)
            ON CONFLICT (guild_id, match_id) DO UPDATE SET
                all_clan_saved = excluded.all_clan_saved,
                checked_at_utc = excluded.checked_at_utc;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());
        command.Parameters.AddWithValue("$matchId", matchId);
        command.Parameters.AddWithValue("$queueId", queueId);
        command.Parameters.AddWithValue("$allClanSaved", allClanSaved ? 1 : 0);
        command.Parameters.AddWithValue("$checkedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// "확인은 했지만 5명을 못 채워서 저장 안 한" 매치 기록을 지웁니다.
    /// 새 멤버/부캐가 등록되면 예전엔 5명 미달이었던 매치가 이제 채워질 수 있으므로,
    /// 다음 /전적수집에서 그 매치들을 다시 평가하게 만듭니다. 이미 저장된(all_clan_saved=1) 매치는 건드리지 않습니다.
    /// </summary>
    public async Task<int> ResetUnqualifiedCheckedMatchesAsync(
        ulong guildId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM checked_matches WHERE guild_id = $guildId AND all_clan_saved = 0;";
        command.Parameters.AddWithValue("$guildId", guildId.ToString());
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 부캐 소유자 충돌(같은 discord_user_id로 겹치는 참가자)을 기록해둡니다.
    /// 충돌 그룹 안에서 실제로 DB에 저장된(= PK 경쟁에서 "이긴") 참가자는 alreadySaved=true로 넘겨서
    /// 곧바로 해결된 것으로 표시합니다 — 이미 정상 저장돼 있어 손댈 필요가 없기 때문에
    /// /부캐충돌목록에는 진짜로 누락된 나머지 참가자만 보이게 됩니다.
    /// </summary>
    public async Task SaveOwnerConflictAsync(
        ulong guildId,
        string matchId,
        int teamId,
        string puuid,
        string riotGameName,
        string riotTagLine,
        string championName,
        string teamPosition,
        ulong defaultOwnerDiscordUserId,
        bool alreadySaved,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO match_owner_conflicts (
                guild_id, match_id, team_id, puuid, riot_game_name, riot_tag_line,
                champion_name, team_position, default_owner_discord_user_id, detected_at_utc, resolved
            )
            VALUES (
                $guildId, $matchId, $teamId, $puuid, $gameName, $tagLine,
                $championName, $teamPosition, $ownerId, $now, $resolved
            )
            ON CONFLICT (guild_id, match_id, puuid) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());
        command.Parameters.AddWithValue("$matchId", matchId);
        command.Parameters.AddWithValue("$teamId", teamId);
        command.Parameters.AddWithValue("$puuid", puuid);
        command.Parameters.AddWithValue("$gameName", riotGameName);
        command.Parameters.AddWithValue("$tagLine", riotTagLine);
        command.Parameters.AddWithValue("$championName", championName);
        command.Parameters.AddWithValue("$teamPosition", teamPosition);
        command.Parameters.AddWithValue("$ownerId", defaultOwnerDiscordUserId.ToString());
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$resolved", alreadySaved ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 아직 해결 안 된 부캐 소유자 충돌 목록을 매치 발생 순으로 반환합니다.
    /// </summary>
    public async Task<IReadOnlyList<OwnerConflictRow>> GetUnresolvedConflictsAsync(
        ulong guildId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT match_id, team_id, puuid, riot_game_name, riot_tag_line,
                champion_name, team_position, default_owner_discord_user_id
            FROM match_owner_conflicts
            WHERE guild_id = $guildId AND resolved = 0
            ORDER BY detected_at_utc, match_id, team_id;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());

        var results = new List<OwnerConflictRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new OwnerConflictRow(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                ulong.Parse(reader.GetString(7))));
        }

        return results;
    }

    /// <summary>
    /// 매치ID + 게임이름 + 태그로 미해결 충돌 하나를 찾습니다 (게임이름/태그는 대소문자 구분 없이 비교).
    /// </summary>
    public async Task<OwnerConflictRow?> FindUnresolvedConflictAsync(
        ulong guildId,
        string matchId,
        string riotGameName,
        string riotTagLine,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT match_id, team_id, puuid, riot_game_name, riot_tag_line,
                champion_name, team_position, default_owner_discord_user_id
            FROM match_owner_conflicts
            WHERE guild_id = $guildId AND resolved = 0 AND match_id = $matchId
                AND riot_game_name = $gameName COLLATE NOCASE
                AND riot_tag_line = $tagLine COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());
        command.Parameters.AddWithValue("$matchId", matchId);
        command.Parameters.AddWithValue("$gameName", riotGameName);
        command.Parameters.AddWithValue("$tagLine", riotTagLine);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new OwnerConflictRow(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            ulong.Parse(reader.GetString(7)));
    }

    /// <summary>
    /// 충돌 하나를 해결됨으로 표시합니다.
    /// </summary>
    public async Task MarkConflictResolvedAsync(
        ulong guildId,
        string matchId,
        string puuid,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE match_owner_conflicts SET resolved = 1
            WHERE guild_id = $guildId AND match_id = $matchId AND puuid = $puuid;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());
        command.Parameters.AddWithValue("$matchId", matchId);
        command.Parameters.AddWithValue("$puuid", puuid);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 특정 매치에서 특정 discord_user_id + puuid로 저장된 참가 기록이 있으면 지웁니다.
    /// 충돌 해결 시, 그 puuid가 원래 기본 소유자 이름으로 잘못 저장돼 있었다면 재배정 전에 지우는 용도입니다.
    /// </summary>
    public async Task DeleteParticipationIfMatchesAsync(
        ulong guildId,
        string matchId,
        ulong discordUserId,
        string puuid,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM match_participations
            WHERE guild_id = $guildId AND match_id = $matchId AND discord_user_id = $discordUserId AND puuid = $puuid;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());
        command.Parameters.AddWithValue("$matchId", matchId);
        command.Parameters.AddWithValue("$discordUserId", discordUserId.ToString());
        command.Parameters.AddWithValue("$puuid", puuid);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// "부캐 소유자 충돌"(같은 경기에 본인+빌린 사람이 동시에 있어서 저장 자체가 씹힌 경우)과 달리,
    /// 저장은 정상적으로 됐지만 그 경기만 다른 사람이 빌려서 한 경우를 위한 기능입니다.
    /// 이미 저장된 참가 기록(match_participations, match_contribution_v4)의 discord_user_id를
    /// fromDiscordUserId에서 toDiscordUserId로 그대로 옮깁니다(다시 Riot API를 조회하지 않음 —
    /// 이미 저장된 통계는 정확하고, 누구 것으로 볼지만 바뀌는 것이기 때문).
    /// 대상 멤버가 같은 경기에 이미 자기 기록을 갖고 있으면(=진짜 부캐 충돌 케이스) 덮어쓰지 않고 실패로 반환합니다
    /// — 그 경우엔 /atoz 부캐충돌목록·부캐충돌해결을 대신 써야 합니다.
    /// </summary>
    public async Task<ReassignParticipationOutcome> ReassignParticipationOwnerAsync(
        ulong guildId,
        string matchId,
        ulong fromDiscordUserId,
        ulong toDiscordUserId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        var selectCommand = connection.CreateCommand();
        selectCommand.Transaction = transaction;
        selectCommand.CommandText = """
            SELECT champion_name, team_position, kills, deaths, assists, win
            FROM match_participations
            WHERE guild_id = $guildId AND match_id = $matchId AND discord_user_id = $fromId;
            """;
        selectCommand.Parameters.AddWithValue("$guildId", guildId.ToString());
        selectCommand.Parameters.AddWithValue("$matchId", matchId);
        selectCommand.Parameters.AddWithValue("$fromId", fromDiscordUserId.ToString());

        string championName;
        string teamPosition;
        int kills, deaths, assists;
        bool win;
        await using (var reader = await selectCommand.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new ReassignParticipationOutcome(ReassignParticipationStatus.SourceNotFound);
            }

            championName = reader.GetString(0);
            teamPosition = reader.GetString(1);
            kills = reader.GetInt32(2);
            deaths = reader.GetInt32(3);
            assists = reader.GetInt32(4);
            win = reader.GetInt32(5) != 0;
        }

        var checkCommand = connection.CreateCommand();
        checkCommand.Transaction = transaction;
        checkCommand.CommandText = """
            SELECT COUNT(*) FROM match_participations
            WHERE guild_id = $guildId AND match_id = $matchId AND discord_user_id = $toId;
            """;
        checkCommand.Parameters.AddWithValue("$guildId", guildId.ToString());
        checkCommand.Parameters.AddWithValue("$matchId", matchId);
        checkCommand.Parameters.AddWithValue("$toId", toDiscordUserId.ToString());
        var existingCount = (long)(await checkCommand.ExecuteScalarAsync(cancellationToken))!;
        if (existingCount > 0)
        {
            return new ReassignParticipationOutcome(ReassignParticipationStatus.TargetAlreadyHasRecord);
        }

        var updateCommand = connection.CreateCommand();
        updateCommand.Transaction = transaction;
        updateCommand.CommandText = """
            UPDATE match_participations SET discord_user_id = $toId
            WHERE guild_id = $guildId AND match_id = $matchId AND discord_user_id = $fromId;
            """;
        updateCommand.Parameters.AddWithValue("$guildId", guildId.ToString());
        updateCommand.Parameters.AddWithValue("$matchId", matchId);
        updateCommand.Parameters.AddWithValue("$fromId", fromDiscordUserId.ToString());
        updateCommand.Parameters.AddWithValue("$toId", toDiscordUserId.ToString());
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);

        // 기여도 v4 점수도 같은 매치에 계산돼 있으면(match_contribution_v4) 같이 옮겨서
        // /명예의전당·/아재전적이 재배정 후 새 소유자 기준으로 보이게 합니다. 없으면 그냥 0행 갱신.
        var updateV4Command = connection.CreateCommand();
        updateV4Command.Transaction = transaction;
        updateV4Command.CommandText = """
            UPDATE match_contribution_v4 SET discord_user_id = $toId
            WHERE guild_id = $guildId AND match_id = $matchId AND discord_user_id = $fromId;
            """;
        updateV4Command.Parameters.AddWithValue("$guildId", guildId.ToString());
        updateV4Command.Parameters.AddWithValue("$matchId", matchId);
        updateV4Command.Parameters.AddWithValue("$fromId", fromDiscordUserId.ToString());
        updateV4Command.Parameters.AddWithValue("$toId", toDiscordUserId.ToString());
        await updateV4Command.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new ReassignParticipationOutcome(
            ReassignParticipationStatus.Success, championName, teamPosition, kills, deaths, assists, win);
    }

    public async Task SaveParticipationAsync(
        ulong guildId,
        string matchId,
        int queueId,
        long gameDurationSeconds,
        DateTimeOffset gameCreatedAt,
        ulong discordUserId,
        string puuid,
        int teamId,
        string championName,
        string teamPosition,
        bool win,
        int kills,
        int deaths,
        int assists,
        int creepScore,
        string? opponentChampionName = null,
        ParticipationStats? stats = null,
        CancellationToken cancellationToken = default)
    {
        stats ??= ParticipationStats.Empty;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO match_participations (
                guild_id, match_id, discord_user_id, puuid, queue_id, team_id,
                champion_name, team_position, opponent_champion_name, win, kills, deaths, assists,
                creep_score, damage_dealt, damage_taken, damage_mitigated, gold_earned, vision_score, cc_time_dealt,
                heal_amount, wards_placed, damage_to_objectives,
                opponent_kills, opponent_deaths, opponent_assists, opponent_damage_dealt, opponent_damage_taken,
                opponent_gold_earned, opponent_creep_score, opponent_vision_score, opponent_cc_time_dealt,
                opponent_heal_amount, opponent_wards_placed, opponent_damage_to_objectives,
                game_duration_seconds, game_created_at_utc, collected_at_utc
            )
            VALUES (
                $guildId, $matchId, $discordUserId, $puuid, $queueId, $teamId,
                $championName, $teamPosition, $opponentChampionName, $win, $kills, $deaths, $assists,
                $creepScore, $damageDealt, $damageTaken, $damageMitigated, $goldEarned, $visionScore, $ccTimeDealt,
                $healAmount, $wardsPlaced, $damageToObjectives,
                $opponentKills, $opponentDeaths, $opponentAssists, $opponentDamageDealt, $opponentDamageTaken,
                $opponentGoldEarned, $opponentCreepScore, $opponentVisionScore, $opponentCcTimeDealt,
                $opponentHealAmount, $opponentWardsPlaced, $opponentDamageToObjectives,
                $gameDuration, $gameCreatedAt, $collectedAt
            )
            ON CONFLICT (guild_id, match_id, discord_user_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());
        command.Parameters.AddWithValue("$matchId", matchId);
        command.Parameters.AddWithValue("$discordUserId", discordUserId.ToString());
        command.Parameters.AddWithValue("$puuid", puuid);
        command.Parameters.AddWithValue("$queueId", queueId);
        command.Parameters.AddWithValue("$teamId", teamId);
        command.Parameters.AddWithValue("$championName", championName);
        command.Parameters.AddWithValue("$teamPosition", teamPosition);
        command.Parameters.AddWithValue("$opponentChampionName", (object?)opponentChampionName ?? DBNull.Value);
        command.Parameters.AddWithValue("$win", win ? 1 : 0);
        command.Parameters.AddWithValue("$kills", kills);
        command.Parameters.AddWithValue("$deaths", deaths);
        command.Parameters.AddWithValue("$assists", assists);
        command.Parameters.AddWithValue("$creepScore", creepScore);
        command.Parameters.AddWithValue("$damageDealt", (object?)stats.DamageDealt ?? DBNull.Value);
        command.Parameters.AddWithValue("$damageTaken", (object?)stats.DamageTaken ?? DBNull.Value);
        command.Parameters.AddWithValue("$damageMitigated", (object?)stats.DamageMitigated ?? DBNull.Value);
        command.Parameters.AddWithValue("$goldEarned", (object?)stats.GoldEarned ?? DBNull.Value);
        command.Parameters.AddWithValue("$visionScore", (object?)stats.VisionScore ?? DBNull.Value);
        command.Parameters.AddWithValue("$ccTimeDealt", (object?)stats.CcTimeDealt ?? DBNull.Value);
        command.Parameters.AddWithValue("$healAmount", (object?)stats.HealAmount ?? DBNull.Value);
        command.Parameters.AddWithValue("$wardsPlaced", (object?)stats.WardsPlaced ?? DBNull.Value);
        command.Parameters.AddWithValue("$damageToObjectives", (object?)stats.DamageToObjectives ?? DBNull.Value);
        command.Parameters.AddWithValue("$opponentKills", (object?)stats.OpponentKills ?? DBNull.Value);
        command.Parameters.AddWithValue("$opponentDeaths", (object?)stats.OpponentDeaths ?? DBNull.Value);
        command.Parameters.AddWithValue("$opponentAssists", (object?)stats.OpponentAssists ?? DBNull.Value);
        command.Parameters.AddWithValue("$opponentDamageDealt", (object?)stats.OpponentDamageDealt ?? DBNull.Value);
        command.Parameters.AddWithValue("$opponentDamageTaken", (object?)stats.OpponentDamageTaken ?? DBNull.Value);
        command.Parameters.AddWithValue("$opponentGoldEarned", (object?)stats.OpponentGoldEarned ?? DBNull.Value);
        command.Parameters.AddWithValue("$opponentCreepScore", (object?)stats.OpponentCreepScore ?? DBNull.Value);
        command.Parameters.AddWithValue("$opponentVisionScore", (object?)stats.OpponentVisionScore ?? DBNull.Value);
        command.Parameters.AddWithValue("$opponentCcTimeDealt", (object?)stats.OpponentCcTimeDealt ?? DBNull.Value);
        command.Parameters.AddWithValue("$opponentHealAmount", (object?)stats.OpponentHealAmount ?? DBNull.Value);
        command.Parameters.AddWithValue("$opponentWardsPlaced", (object?)stats.OpponentWardsPlaced ?? DBNull.Value);
        command.Parameters.AddWithValue("$opponentDamageToObjectives", (object?)stats.OpponentDamageToObjectives ?? DBNull.Value);
        command.Parameters.AddWithValue("$gameDuration", gameDurationSeconds);
        command.Parameters.AddWithValue("$gameCreatedAt", gameCreatedAt.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$collectedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 라인별 챔피언 티어(우리 클랜 데이터 기준 승률/판수)를 집계합니다. position이 null이면 전체 라인.
    /// </summary>
    public async Task<IReadOnlyList<ChampionTierRow>> GetChampionTierAsync(
        ulong guildId,
        int queueId,
        string? teamPosition,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT team_position, champion_name, COUNT(*) AS games, SUM(win) AS wins
            FROM match_participations
            WHERE guild_id = $guildId AND queue_id = $queueId
                AND ($position IS NULL OR team_position = $position)
            GROUP BY team_position, champion_name
            ORDER BY team_position, (SUM(win) * 1.0 / COUNT(*)) DESC, games DESC;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());
        command.Parameters.AddWithValue("$queueId", queueId);
        command.Parameters.AddWithValue("$position", (object?)teamPosition ?? DBNull.Value);

        var results = new List<ChampionTierRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ChampionTierRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3)));
        }

        return results;
    }

    /// <summary>
    /// 여러 (라인, 챔피언) 조합을 누가 플레이했는지(판수 포함) 한 번의 쿼리로 집계합니다.
    /// /티어픽 상위권 아이디 표기용 — 예전엔 챔피언 한 줄마다 이 쿼리를 따로 호출했는데
    /// (N+1 패턴, 외부 코드리뷰 지적), 필요한 (라인,챔피언) 쌍을 한 번에 모아서 SQLite의
    /// row-value IN 문법으로 한 번만 조회하도록 2026-08-20 리팩토링에서 바꿈.
    /// </summary>
    public async Task<IReadOnlyDictionary<(string Position, string ChampionName), IReadOnlyList<ChampionPlayerRow>>> GetChampionPlayersBatchAsync(
        ulong guildId,
        int queueId,
        IReadOnlyList<(string Position, string ChampionName)> pairs,
        CancellationToken cancellationToken = default)
    {
        var empty = new Dictionary<(string, string), IReadOnlyList<ChampionPlayerRow>>();
        if (pairs.Count == 0)
        {
            return empty;
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var valuePlaceholders = pairs.Select((_, index) => $"($pos{index}, $champ{index})").ToList();
        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT team_position, champion_name, discord_user_id, COUNT(*) AS games, SUM(win) AS wins
            FROM match_participations
            WHERE guild_id = $guildId AND queue_id = $queueId
                AND (team_position, champion_name) IN ({string.Join(",", valuePlaceholders)})
            GROUP BY team_position, champion_name, discord_user_id
            ORDER BY games DESC;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());
        command.Parameters.AddWithValue("$queueId", queueId);
        for (var i = 0; i < pairs.Count; i++)
        {
            command.Parameters.AddWithValue($"$pos{i}", pairs[i].Position);
            command.Parameters.AddWithValue($"$champ{i}", pairs[i].ChampionName);
        }

        var results = new Dictionary<(string, string), List<ChampionPlayerRow>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = (reader.GetString(0), reader.GetString(1));
            if (!results.TryGetValue(key, out var list))
            {
                list = [];
                results[key] = list;
            }

            list.Add(new ChampionPlayerRow(
                ulong.Parse(reader.GetString(2)),
                reader.GetInt32(3),
                reader.GetInt32(4)));
        }

        return results.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ChampionPlayerRow>)pair.Value);
    }

    /// <summary>
    /// 라인 구분 없이 챔피언별 전체 승률/판수를 집계합니다 (/티어픽의 "전체 워스트" 섹션용).
    /// </summary>
    public async Task<IReadOnlyList<ChampionOverallRow>> GetOverallChampionStatsAsync(
        ulong guildId,
        int queueId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT champion_name, COUNT(*) AS games, SUM(win) AS wins
            FROM match_participations
            WHERE guild_id = $guildId AND queue_id = $queueId
            GROUP BY champion_name
            ORDER BY (SUM(win) * 1.0 / COUNT(*)) ASC, games DESC;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());
        command.Parameters.AddWithValue("$queueId", queueId);

        var results = new List<ChampionOverallRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ChampionOverallRow(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt32(2)));
        }

        return results;
    }

    /// <summary>
    /// 같은 라인 상대 챔피언별로, 우리 쪽 승률/판수를 집계합니다 (/밴픽추천의 "주의 챔피언"용).
    /// 승률이 낮을수록 그 상대 챔피언한테 우리가 약하다는 뜻입니다. position이 null이면 전체 라인 통합.
    /// </summary>
    public async Task<IReadOnlyList<ChampionOverallRow>> GetOpponentChampionStatsAsync(
        ulong guildId,
        int queueId,
        string? teamPosition,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT opponent_champion_name, COUNT(*) AS games, SUM(win) AS wins
            FROM match_participations
            WHERE guild_id = $guildId AND queue_id = $queueId
                AND opponent_champion_name IS NOT NULL
                AND ($position IS NULL OR team_position = $position)
            GROUP BY opponent_champion_name
            ORDER BY (SUM(win) * 1.0 / COUNT(*)) ASC, games DESC;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());
        command.Parameters.AddWithValue("$queueId", queueId);
        command.Parameters.AddWithValue("$position", (object?)teamPosition ?? DBNull.Value);

        var results = new List<ChampionOverallRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ChampionOverallRow(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt32(2)));
        }

        return results;
    }

    /// <summary>
    /// 밴픽추천 2단계 "우리 AZ 티어픽 상대 카운터"용 — 지정한 우리 챔피언 목록(주로 그 라인
    /// 베스트픽들)이 맞라인에서 만난 상대 챔피언별 승률을 집계합니다. GetOpponentChampionStatsAsync와
    /// 달리 "우리가 그 라인에서 누굴 잡든" 이 아니라 "우리 베스트픽이 잡았을 때만" 좁혀서 봅니다 —
    /// 우리 주력 픽한테 유독 승률이 안 나오는 상대(=밴 우선순위)를 가려내기 위함.
    /// </summary>
    public async Task<IReadOnlyList<ChampionOverallRow>> GetMatchupStatsAsync(
        ulong guildId,
        int queueId,
        string teamPosition,
        IReadOnlyList<string> ourChampionNames,
        CancellationToken cancellationToken = default)
    {
        if (ourChampionNames.Count == 0)
        {
            return [];
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var placeholders = ourChampionNames.Select((_, index) => $"$champ{index}").ToList();
        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT opponent_champion_name, COUNT(*) AS games, SUM(win) AS wins
            FROM match_participations
            WHERE guild_id = $guildId AND queue_id = $queueId AND team_position = $position
                AND opponent_champion_name IS NOT NULL
                AND champion_name IN ({string.Join(",", placeholders)})
            GROUP BY opponent_champion_name
            ORDER BY (SUM(win) * 1.0 / COUNT(*)) ASC, games DESC;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());
        command.Parameters.AddWithValue("$queueId", queueId);
        command.Parameters.AddWithValue("$position", teamPosition);
        for (var i = 0; i < ourChampionNames.Count; i++)
        {
            command.Parameters.AddWithValue($"$champ{i}", ourChampionNames[i]);
        }

        var results = new List<ChampionOverallRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ChampionOverallRow(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt32(2)));
        }

        return results;
    }

    /// <summary>
    /// 라인 구분 없이 여러 챔피언을 각각 누가 플레이했는지(판수 포함) 한 번의 쿼리로 집계합니다.
    /// /티어픽 전체 워스트 지분율 표기용 — GetChampionPlayersBatchAsync와 같은 이유(N+1 제거)로
    /// 2026-08-20 리팩토링에서 배치 쿼리로 바꿈.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ChampionPlayerRow>>> GetOverallChampionPlayersBatchAsync(
        ulong guildId,
        int queueId,
        IReadOnlyList<string> championNames,
        CancellationToken cancellationToken = default)
    {
        if (championNames.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<ChampionPlayerRow>>();
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var placeholders = championNames.Select((_, index) => $"$champ{index}").ToList();
        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT champion_name, discord_user_id, COUNT(*) AS games, SUM(win) AS wins
            FROM match_participations
            WHERE guild_id = $guildId AND queue_id = $queueId AND champion_name IN ({string.Join(",", placeholders)})
            GROUP BY champion_name, discord_user_id
            ORDER BY games DESC;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());
        command.Parameters.AddWithValue("$queueId", queueId);
        for (var i = 0; i < championNames.Count; i++)
        {
            command.Parameters.AddWithValue($"$champ{i}", championNames[i]);
        }

        var results = new Dictionary<string, List<ChampionPlayerRow>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var championName = reader.GetString(0);
            if (!results.TryGetValue(championName, out var list))
            {
                list = [];
                results[championName] = list;
            }

            list.Add(new ChampionPlayerRow(
                ulong.Parse(reader.GetString(1)),
                reader.GetInt32(2),
                reader.GetInt32(3)));
        }

        return results.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ChampionPlayerRow>)pair.Value);
    }

    /// <summary>
    /// 등록된 멤버별 승률(자유 랭크)을 집계합니다.
    /// </summary>
    public async Task<IReadOnlyList<MemberWinRateRow>> GetMemberWinRatesAsync(
        ulong guildId,
        int queueId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT discord_user_id, COUNT(*) AS games, SUM(win) AS wins,
                SUM(kills) AS kills, SUM(deaths) AS deaths, SUM(assists) AS assists
            FROM match_participations
            WHERE guild_id = $guildId AND queue_id = $queueId
            GROUP BY discord_user_id
            ORDER BY (SUM(win) * 1.0 / COUNT(*)) DESC, games DESC;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());
        command.Parameters.AddWithValue("$queueId", queueId);

        var results = new List<MemberWinRateRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new MemberWinRateRow(
                ulong.Parse(reader.GetString(0)),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5)));
        }

        return results;
    }

    /// <summary>
    /// 특정 멤버의 라인(포지션)별 판수/승수를 집계합니다 (/내전적용).
    /// </summary>
    public async Task<IReadOnlyList<MemberPositionStatRow>> GetMemberPositionStatsAsync(
        ulong guildId,
        int queueId,
        ulong discordUserId,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var dateFilter = fromUtc is not null && toUtc is not null
            ? "AND game_created_at_utc >= $start AND game_created_at_utc < $end"
            : "";

        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT team_position, COUNT(*) AS games, SUM(win) AS wins
            FROM match_participations
            WHERE guild_id = $guildId AND queue_id = $queueId AND discord_user_id = $discordUserId {dateFilter}
            GROUP BY team_position
            ORDER BY games DESC;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());
        command.Parameters.AddWithValue("$queueId", queueId);
        command.Parameters.AddWithValue("$discordUserId", discordUserId.ToString());
        if (fromUtc is not null && toUtc is not null)
        {
            command.Parameters.AddWithValue("$start", fromUtc.Value.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$end", toUtc.Value.UtcDateTime.ToString("O"));
        }

        var results = new List<MemberPositionStatRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new MemberPositionStatRow(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt32(2)));
        }

        return results;
    }

    /// <summary>
    /// 특정 멤버의 챔피언별 판수/승수를 집계합니다 (/내전적의 모스트·워스트 챔피언용).
    /// </summary>
    public async Task<IReadOnlyList<MemberChampionStatRow>> GetMemberChampionStatsAsync(
        ulong guildId,
        int queueId,
        ulong discordUserId,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var dateFilter = fromUtc is not null && toUtc is not null
            ? "AND game_created_at_utc >= $start AND game_created_at_utc < $end"
            : "";

        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT champion_name, COUNT(*) AS games, SUM(win) AS wins
            FROM match_participations
            WHERE guild_id = $guildId AND queue_id = $queueId AND discord_user_id = $discordUserId {dateFilter}
            GROUP BY champion_name;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());
        command.Parameters.AddWithValue("$queueId", queueId);
        command.Parameters.AddWithValue("$discordUserId", discordUserId.ToString());
        if (fromUtc is not null && toUtc is not null)
        {
            command.Parameters.AddWithValue("$start", fromUtc.Value.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$end", toUtc.Value.UtcDateTime.ToString("O"));
        }

        var results = new List<MemberChampionStatRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new MemberChampionStatRow(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt32(2)));
        }

        return results;
    }

    /// <summary>
    /// 특정 멤버의 "라인별 챔피언" 판수/승수를 집계합니다 (/내전적의 라인별 승률 모스트 챔피언용).
    /// </summary>
    public async Task<IReadOnlyList<MemberPositionChampionStatRow>> GetMemberChampionStatsByPositionAsync(
        ulong guildId,
        int queueId,
        ulong discordUserId,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var dateFilter = fromUtc is not null && toUtc is not null
            ? "AND game_created_at_utc >= $start AND game_created_at_utc < $end"
            : "";

        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT team_position, champion_name, COUNT(*) AS games, SUM(win) AS wins
            FROM match_participations
            WHERE guild_id = $guildId AND queue_id = $queueId AND discord_user_id = $discordUserId {dateFilter}
            GROUP BY team_position, champion_name;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());
        command.Parameters.AddWithValue("$queueId", queueId);
        command.Parameters.AddWithValue("$discordUserId", discordUserId.ToString());
        if (fromUtc is not null && toUtc is not null)
        {
            command.Parameters.AddWithValue("$start", fromUtc.Value.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$end", toUtc.Value.UtcDateTime.ToString("O"));
        }

        var results = new List<MemberPositionChampionStatRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new MemberPositionChampionStatRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3)));
        }

        return results;
    }

    /// <summary>
    /// 같은 팀에서 원딜(BOTTOM)+서폿(UTILITY)으로 함께 나온 멤버 조합의 판수/승수를 집계합니다.
    /// 포지션으로 이미 짝이 고정되므로(팀당 원딜/서폿 각 1명) 중복 집계 걱정 없이 조인만으로 충분합니다.
    /// </summary>
    public async Task<IReadOnlyList<BottomDuoRow>> GetBottomDuoStatsAsync(
        ulong guildId,
        int queueId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.discord_user_id, b.discord_user_id,
                COUNT(*) AS games, SUM(a.win) AS wins
            FROM match_participations a
            JOIN match_participations b
                ON a.guild_id = b.guild_id
                AND a.match_id = b.match_id
                AND a.team_id = b.team_id
            WHERE a.guild_id = $guildId AND a.queue_id = $queueId
                AND a.team_position = 'BOTTOM' AND b.team_position = 'UTILITY'
            GROUP BY a.discord_user_id, b.discord_user_id
            ORDER BY (SUM(a.win) * 1.0 / COUNT(*)) DESC, games DESC;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());
        command.Parameters.AddWithValue("$queueId", queueId);

        var results = new List<BottomDuoRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new BottomDuoRow(
                ulong.Parse(reader.GetString(0)),
                ulong.Parse(reader.GetString(1)),
                reader.GetInt32(2),
                reader.GetInt32(3)));
        }

        return results;
    }

    /// <summary>
    /// 같은 팀에서 특정 두 라인(포지션)으로 나온 챔피언 조합의 판수/승수를 집계합니다.
    /// (예: BOTTOM+UTILITY, JUNGLE+MIDDLE 등 — /조합추천처럼 "아무 조합"이 아니라 특정 라인 페어링 전용)
    /// </summary>
    public async Task<IReadOnlyList<LaneChampionDuoRow>> GetLaneChampionDuoStatsAsync(
        ulong guildId,
        int queueId,
        string positionA,
        string positionB,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.champion_name, b.champion_name,
                COUNT(*) AS games, SUM(a.win) AS wins
            FROM match_participations a
            JOIN match_participations b
                ON a.guild_id = b.guild_id
                AND a.match_id = b.match_id
                AND a.team_id = b.team_id
            WHERE a.guild_id = $guildId AND a.queue_id = $queueId
                AND a.team_position = $positionA AND b.team_position = $positionB
            GROUP BY a.champion_name, b.champion_name
            ORDER BY (SUM(a.win) * 1.0 / COUNT(*)) DESC, games DESC;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());
        command.Parameters.AddWithValue("$queueId", queueId);
        command.Parameters.AddWithValue("$positionA", positionA);
        command.Parameters.AddWithValue("$positionB", positionB);

        var results = new List<LaneChampionDuoRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new LaneChampionDuoRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3)));
        }

        return results;
    }

    /// <summary>
    /// 같은 팀에서 함께 나온 챔피언 조합(듀오)의 판수/승수를 집계합니다 (조합추천용 시너지 데이터).
    /// 같은 팀 안에서는 챔피언이 겹치지 않으므로 champion_name 알파벳 순으로 짝지어도 각 쌍이 한 번씩만 집계됩니다.
    /// </summary>
    public async Task<IReadOnlyList<ChampionSynergyRow>> GetChampionSynergyAsync(
        ulong guildId,
        int queueId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.champion_name, b.champion_name,
                COUNT(*) AS games, SUM(a.win) AS wins
            FROM match_participations a
            JOIN match_participations b
                ON a.guild_id = b.guild_id
                AND a.match_id = b.match_id
                AND a.team_id = b.team_id
                AND a.champion_name < b.champion_name
            WHERE a.guild_id = $guildId AND a.queue_id = $queueId
            GROUP BY a.champion_name, b.champion_name
            ORDER BY (SUM(a.win) * 1.0 / COUNT(*)) DESC, games DESC;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());
        command.Parameters.AddWithValue("$queueId", queueId);

        var results = new List<ChampionSynergyRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ChampionSynergyRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3)));
        }

        return results;
    }

    /// <summary>
    /// 같은 팀에 AtoZ 등록 멤버가 minTeammates명 이상 함께 있었던 경기를 최근 순으로 반환합니다
    /// (경기당 참가자 목록 포함). "멤버끼리 같이 한 자유랭크"만 따로 보고 싶을 때 씁니다.
    /// </summary>
    public async Task<IReadOnlyList<ClanMatchRow>> GetClanMatchesAsync(
        ulong guildId,
        int queueId,
        int minTeammates,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var matchIds = new List<string>();
        var idsCommand = connection.CreateCommand();
        idsCommand.CommandText = """
            SELECT match_id, MAX(game_created_at_utc) AS played_at
            FROM match_participations
            WHERE guild_id = $guildId AND queue_id = $queueId
                AND match_id IN (
                    SELECT match_id FROM match_participations
                    WHERE guild_id = $guildId AND queue_id = $queueId
                    GROUP BY match_id, team_id
                    HAVING COUNT(*) >= $minTeammates
                )
            GROUP BY match_id
            ORDER BY played_at DESC
            LIMIT $limit;
            """;
        idsCommand.Parameters.AddWithValue("$guildId", guildId.ToString());
        idsCommand.Parameters.AddWithValue("$queueId", queueId);
        idsCommand.Parameters.AddWithValue("$minTeammates", minTeammates);
        idsCommand.Parameters.AddWithValue("$limit", limit);

        await using (var idsReader = await idsCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await idsReader.ReadAsync(cancellationToken))
            {
                matchIds.Add(idsReader.GetString(0));
            }
        }

        if (matchIds.Count == 0)
        {
            return [];
        }

        var placeholders = matchIds.Select((_, index) => $"$matchId{index}").ToList();
        var detailCommand = connection.CreateCommand();
        detailCommand.CommandText = $"""
            SELECT {ClanMatchParticipantColumns}
            FROM match_participations
            WHERE guild_id = $guildId AND queue_id = $queueId AND match_id IN ({string.Join(",", placeholders)});
            """;
        detailCommand.Parameters.AddWithValue("$guildId", guildId.ToString());
        detailCommand.Parameters.AddWithValue("$queueId", queueId);
        for (var i = 0; i < matchIds.Count; i++)
        {
            detailCommand.Parameters.AddWithValue($"$matchId{i}", matchIds[i]);
        }

        var participantsByMatch = new Dictionary<string, List<ClanMatchParticipantRow>>();
        var metaByMatch = new Dictionary<string, (DateTimeOffset CreatedAt, long DurationSeconds)>();

        await using (var detailReader = await detailCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await detailReader.ReadAsync(cancellationToken))
            {
                var matchId = detailReader.GetString(0);
                var participant = ReadClanMatchParticipantRow(detailReader);

                if (!participantsByMatch.TryGetValue(matchId, out var list))
                {
                    list = [];
                    participantsByMatch[matchId] = list;
                }
                list.Add(participant);

                if (!metaByMatch.ContainsKey(matchId))
                {
                    metaByMatch[matchId] = (
                        DateTimeOffset.Parse(detailReader.GetString(10)),
                        detailReader.GetInt64(9));
                }
            }
        }

        return matchIds
            .Where(participantsByMatch.ContainsKey)
            .Select(matchId => new ClanMatchRow(
                matchId,
                metaByMatch[matchId].CreatedAt,
                metaByMatch[matchId].DurationSeconds,
                participantsByMatch[matchId]))
            .ToList();
    }

    /// <summary>
    /// 지정한 기간(UTC, [start, end) 반개구간)에 열린 경기 중, 기여도 점수 계산에 필요한
    /// 지표(딜량/받은피해/경감/골드/시야/CC)가 전부 채워진 참가자 행만 매치별로 묶어 반환합니다
    /// (/명예의전당용). `.rofl` 업로드로만 저장된 경기처럼 이 지표가 없는 경기는 자동으로 빠집니다.
    /// </summary>
    public async Task<IReadOnlyList<ClanMatchRow>> GetContributionStatsInRangeAsync(
        ulong guildId,
        int queueId,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ClanMatchParticipantColumns}
            FROM match_participations
            WHERE guild_id = $guildId AND queue_id = $queueId
                AND game_created_at_utc >= $start AND game_created_at_utc < $end
                AND damage_dealt IS NOT NULL AND damage_taken IS NOT NULL AND damage_mitigated IS NOT NULL
                AND gold_earned IS NOT NULL AND vision_score IS NOT NULL AND cc_time_dealt IS NOT NULL
            ORDER BY game_created_at_utc DESC;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());
        command.Parameters.AddWithValue("$queueId", queueId);
        command.Parameters.AddWithValue("$start", rangeStartUtc.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$end", rangeEndUtc.UtcDateTime.ToString("O"));

        var participantsByMatch = new Dictionary<string, List<ClanMatchParticipantRow>>();
        var metaByMatch = new Dictionary<string, (DateTimeOffset CreatedAt, long DurationSeconds)>();
        var matchOrder = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var matchId = reader.GetString(0);
            var participant = ReadClanMatchParticipantRow(reader);

            if (!participantsByMatch.TryGetValue(matchId, out var list))
            {
                list = [];
                participantsByMatch[matchId] = list;
                matchOrder.Add(matchId);
            }
            list.Add(participant);

            if (!metaByMatch.ContainsKey(matchId))
            {
                metaByMatch[matchId] = (
                    DateTimeOffset.Parse(reader.GetString(10)),
                    reader.GetInt64(9));
            }
        }

        return matchOrder
            .Select(matchId => new ClanMatchRow(
                matchId,
                metaByMatch[matchId].CreatedAt,
                metaByMatch[matchId].DurationSeconds,
                participantsByMatch[matchId]))
            .ToList();
    }

    // GetClanMatchesAsync / GetContributionStatsInRangeAsync가 공유하는 컬럼 목록 + 리더.
    // 컬럼 순서를 바꾸면 아래 ReadClanMatchParticipantRow의 ordinal도 같이 바꿔야 합니다.
    private const string ClanMatchParticipantColumns = """
        match_id, discord_user_id, team_id, champion_name, team_position,
        win, kills, deaths, assists, game_duration_seconds, game_created_at_utc,
        damage_dealt, damage_taken, damage_mitigated, gold_earned, vision_score, cc_time_dealt,
        creep_score, heal_amount, wards_placed, damage_to_objectives,
        opponent_kills, opponent_deaths, opponent_assists, opponent_damage_dealt, opponent_damage_taken,
        opponent_gold_earned, opponent_creep_score, opponent_vision_score, opponent_cc_time_dealt,
        opponent_heal_amount, opponent_wards_placed, opponent_damage_to_objectives
        """;

    private static ClanMatchParticipantRow ReadClanMatchParticipantRow(SqliteDataReader reader)
    {
        long? GetLong(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
        int? GetInt(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

        return new ClanMatchParticipantRow(
            ulong.Parse(reader.GetString(1)),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5) != 0,
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            DamageDealt: GetLong(11),
            DamageTaken: GetLong(12),
            DamageMitigated: GetLong(13),
            GoldEarned: GetLong(14),
            VisionScore: GetInt(15),
            CcTimeDealt: GetInt(16),
            CreepScore: GetInt(17),
            HealAmount: GetLong(18),
            WardsPlaced: GetInt(19),
            DamageToObjectives: GetLong(20),
            OpponentKills: GetInt(21),
            OpponentDeaths: GetInt(22),
            OpponentAssists: GetInt(23),
            OpponentDamageDealt: GetLong(24),
            OpponentDamageTaken: GetLong(25),
            OpponentGoldEarned: GetLong(26),
            OpponentCreepScore: GetInt(27),
            OpponentVisionScore: GetInt(28),
            OpponentCcTimeDealt: GetInt(29),
            OpponentHealAmount: GetLong(30),
            OpponentWardsPlaced: GetInt(31),
            OpponentDamageToObjectives: GetLong(32));
    }
}

public record ChampionTierRow(string TeamPosition, string ChampionName, int Games, int Wins);

public record ChampionPlayerRow(ulong DiscordUserId, int Games, int Wins);

public record ChampionOverallRow(string ChampionName, int Games, int Wins);

public record OwnerConflictRow(
    string MatchId,
    int TeamId,
    string Puuid,
    string RiotGameName,
    string RiotTagLine,
    string ChampionName,
    string TeamPosition,
    ulong DefaultOwnerDiscordUserId);

/// <summary>ReassignParticipationOwnerAsync의 결과 상태. "부캐 충돌"과 겹치지 않게 대상 멤버가 이미
/// 같은 경기 기록을 갖고 있으면 TargetAlreadyHasRecord로 막습니다.</summary>
public enum ReassignParticipationStatus
{
    Success,
    SourceNotFound,
    TargetAlreadyHasRecord,
}

public record ReassignParticipationOutcome(
    ReassignParticipationStatus Status,
    string? ChampionName = null,
    string? TeamPosition = null,
    int Kills = 0,
    int Deaths = 0,
    int Assists = 0,
    bool Win = false);

public record MemberWinRateRow(ulong DiscordUserId, int Games, int Wins, int Kills, int Deaths, int Assists);

public record MemberPositionStatRow(string TeamPosition, int Games, int Wins);

public record MemberChampionStatRow(string ChampionName, int Games, int Wins);

public record MemberPositionChampionStatRow(string TeamPosition, string ChampionName, int Games, int Wins);

/// <summary>
/// 기여도 점수(맞라인 상대 비교) 계산용 부가 지표 묶음. 본인 지표 + 같은 라인 상대(맞라인) 지표를
/// 함께 담습니다. 전부 nullable — `.rofl` 업로드나 백필 전 데이터는 비어 있을 수 있습니다.
/// </summary>
public record ParticipationStats(
    long? DamageDealt = null,
    long? DamageTaken = null,
    long? DamageMitigated = null,
    long? GoldEarned = null,
    int? VisionScore = null,
    int? CcTimeDealt = null,
    long? HealAmount = null,
    int? WardsPlaced = null,
    long? DamageToObjectives = null,
    int? OpponentKills = null,
    int? OpponentDeaths = null,
    int? OpponentAssists = null,
    long? OpponentDamageDealt = null,
    long? OpponentDamageTaken = null,
    long? OpponentGoldEarned = null,
    int? OpponentCreepScore = null,
    int? OpponentVisionScore = null,
    int? OpponentCcTimeDealt = null,
    long? OpponentHealAmount = null,
    int? OpponentWardsPlaced = null,
    long? OpponentDamageToObjectives = null)
{
    public static readonly ParticipationStats Empty = new();
}

public record ChampionSynergyRow(
    string ChampionA,
    string ChampionB,
    int Games,
    int Wins);

public record BottomDuoRow(
    ulong AdcDiscordUserId,
    ulong SupportDiscordUserId,
    int Games,
    int Wins);

public record LaneChampionDuoRow(
    string ChampionA,
    string ChampionB,
    int Games,
    int Wins);

public record ClanMatchParticipantRow(
    ulong DiscordUserId,
    int TeamId,
    string ChampionName,
    string TeamPosition,
    bool Win,
    int Kills,
    int Deaths,
    int Assists,
    long? DamageDealt = null,
    long? DamageTaken = null,
    long? DamageMitigated = null,
    long? GoldEarned = null,
    int? VisionScore = null,
    int? CcTimeDealt = null,
    int? CreepScore = null,
    long? HealAmount = null,
    int? WardsPlaced = null,
    long? DamageToObjectives = null,
    int? OpponentKills = null,
    int? OpponentDeaths = null,
    int? OpponentAssists = null,
    long? OpponentDamageDealt = null,
    long? OpponentDamageTaken = null,
    long? OpponentGoldEarned = null,
    int? OpponentCreepScore = null,
    int? OpponentVisionScore = null,
    int? OpponentCcTimeDealt = null,
    long? OpponentHealAmount = null,
    int? OpponentWardsPlaced = null,
    long? OpponentDamageToObjectives = null);

public record ClanMatchRow(
    string MatchId,
    DateTimeOffset GameCreatedAt,
    long GameDurationSeconds,
    IReadOnlyList<ClanMatchParticipantRow> Participants);

using Microsoft.Data.Sqlite;

namespace LolHelperBot.Services;

public class MemberRepository
{
    private readonly string _connectionString;

    public string DatabasePath { get; }

    public MemberRepository(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(DatabasePath)
            ?? throw new ArgumentException("데이터베이스 경로가 올바르지 않습니다.", nameof(databasePath));
        Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS members (
                guild_id TEXT NOT NULL,
                discord_user_id TEXT NOT NULL,
                discord_display_name TEXT NOT NULL,
                puuid TEXT NOT NULL,
                riot_game_name TEXT NOT NULL,
                riot_tag_line TEXT NOT NULL,
                platform_region TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                PRIMARY KEY (guild_id, discord_user_id),
                UNIQUE (guild_id, puuid)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        var altCommand = connection.CreateCommand();
        altCommand.CommandText = """
            CREATE TABLE IF NOT EXISTS member_alt_accounts (
                guild_id TEXT NOT NULL,
                puuid TEXT NOT NULL,
                owner_discord_user_id TEXT NOT NULL,
                riot_game_name TEXT NOT NULL,
                riot_tag_line TEXT NOT NULL,
                platform_region TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                PRIMARY KEY (guild_id, puuid)
            );
            """;
        await altCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<MemberRegistrationResult> RegisterAsync(
        ulong guildId,
        ulong discordUserId,
        string discordDisplayName,
        RiotAccountLookupResult account,
        CancellationToken cancellationToken = default)
    {
        if (!account.IsSuccess || account.Puuid is null || account.GameName is null ||
            account.TagLine is null || account.Region is null)
        {
            throw new ArgumentException("확인된 Riot 계정만 등록할 수 있습니다.", nameof(account));
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var ownerCommand = connection.CreateCommand();
        ownerCommand.CommandText = """
            SELECT discord_user_id
            FROM members
            WHERE guild_id = $guildId AND puuid = $puuid;
            """;
        ownerCommand.Parameters.AddWithValue("$guildId", guildId.ToString());
        ownerCommand.Parameters.AddWithValue("$puuid", account.Puuid);

        var existingOwner = (string?)await ownerCommand.ExecuteScalarAsync(cancellationToken);
        if (existingOwner is not null && existingOwner != discordUserId.ToString())
        {
            return MemberRegistrationResult.AlreadyRegistered();
        }

        var altOwnerCommand = connection.CreateCommand();
        altOwnerCommand.CommandText = """
            SELECT owner_discord_user_id
            FROM member_alt_accounts
            WHERE guild_id = $guildId AND puuid = $puuid;
            """;
        altOwnerCommand.Parameters.AddWithValue("$guildId", guildId.ToString());
        altOwnerCommand.Parameters.AddWithValue("$puuid", account.Puuid);

        var altOwner = (string?)await altOwnerCommand.ExecuteScalarAsync(cancellationToken);
        if (altOwner is not null)
        {
            return MemberRegistrationResult.Failure("❌ 이 Riot 계정은 이미 다른 AtoZ 멤버의 부캐로 등록되어 있습니다.");
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO members (
                guild_id, discord_user_id, discord_display_name, puuid,
                riot_game_name, riot_tag_line, platform_region, created_at_utc, updated_at_utc
            )
            VALUES (
                $guildId, $discordUserId, $discordDisplayName, $puuid,
                $gameName, $tagLine, $region, $now, $now
            )
            ON CONFLICT (guild_id, discord_user_id) DO UPDATE SET
                discord_display_name = excluded.discord_display_name,
                puuid = excluded.puuid,
                riot_game_name = excluded.riot_game_name,
                riot_tag_line = excluded.riot_tag_line,
                platform_region = excluded.platform_region,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());
        command.Parameters.AddWithValue("$discordUserId", discordUserId.ToString());
        command.Parameters.AddWithValue("$discordDisplayName", discordDisplayName);
        command.Parameters.AddWithValue("$puuid", account.Puuid);
        command.Parameters.AddWithValue("$gameName", account.GameName);
        command.Parameters.AddWithValue("$tagLine", account.TagLine);
        command.Parameters.AddWithValue("$region", account.Region);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return MemberRegistrationResult.Success(account.GameName, account.TagLine);
    }

    public async Task<RegisteredMember?> GetByDiscordUserAsync(
        ulong guildId,
        ulong discordUserId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT puuid, riot_game_name, riot_tag_line, platform_region
            FROM members
            WHERE guild_id = $guildId AND discord_user_id = $discordUserId;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());
        command.Parameters.AddWithValue("$discordUserId", discordUserId.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new RegisteredMember(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3));
    }

    /// <summary>
    /// 클랜 전적 수집/집계용 — 해당 서버에 등록된 AtoZ 멤버 전원을 반환합니다.
    /// </summary>
    public async Task<IReadOnlyList<RegisteredMemberWithId>> GetAllByGuildAsync(
        ulong guildId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT discord_user_id, discord_display_name, puuid, riot_game_name, riot_tag_line, platform_region
            FROM members
            WHERE guild_id = $guildId;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());

        var results = new List<RegisteredMemberWithId>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new RegisteredMemberWithId(
                ulong.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }

        return results;
    }

    /// <summary>
    /// 부캐(다른 Riot 계정)를 이미 본캐로 등록된 멤버에게 연결합니다.
    /// 전적 집계 시 부캐로 플레이한 경기도 본캐(owner) 기준으로 합산됩니다.
    /// </summary>
    public async Task<MemberRegistrationResult> RegisterAltAsync(
        ulong guildId,
        ulong ownerDiscordUserId,
        RiotAccountLookupResult account,
        CancellationToken cancellationToken = default)
    {
        if (!account.IsSuccess || account.Puuid is null || account.GameName is null ||
            account.TagLine is null || account.Region is null)
        {
            throw new ArgumentException("확인된 Riot 계정만 등록할 수 있습니다.", nameof(account));
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var mainOwnerCommand = connection.CreateCommand();
        mainOwnerCommand.CommandText = """
            SELECT discord_user_id
            FROM members
            WHERE guild_id = $guildId AND puuid = $puuid;
            """;
        mainOwnerCommand.Parameters.AddWithValue("$guildId", guildId.ToString());
        mainOwnerCommand.Parameters.AddWithValue("$puuid", account.Puuid);

        var mainOwner = (string?)await mainOwnerCommand.ExecuteScalarAsync(cancellationToken);
        if (mainOwner is not null)
        {
            return mainOwner == ownerDiscordUserId.ToString()
                ? MemberRegistrationResult.Failure("❌ 이 계정은 이미 본인의 본캐로 등록되어 있습니다.")
                : MemberRegistrationResult.AlreadyRegistered();
        }

        var altOwnerCommand = connection.CreateCommand();
        altOwnerCommand.CommandText = """
            SELECT owner_discord_user_id
            FROM member_alt_accounts
            WHERE guild_id = $guildId AND puuid = $puuid;
            """;
        altOwnerCommand.Parameters.AddWithValue("$guildId", guildId.ToString());
        altOwnerCommand.Parameters.AddWithValue("$puuid", account.Puuid);

        var altOwner = (string?)await altOwnerCommand.ExecuteScalarAsync(cancellationToken);
        if (altOwner is not null && altOwner != ownerDiscordUserId.ToString())
        {
            return MemberRegistrationResult.AlreadyRegistered();
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO member_alt_accounts (
                guild_id, puuid, owner_discord_user_id,
                riot_game_name, riot_tag_line, platform_region, created_at_utc, updated_at_utc
            )
            VALUES (
                $guildId, $puuid, $ownerId,
                $gameName, $tagLine, $region, $now, $now
            )
            ON CONFLICT (guild_id, puuid) DO UPDATE SET
                owner_discord_user_id = excluded.owner_discord_user_id,
                riot_game_name = excluded.riot_game_name,
                riot_tag_line = excluded.riot_tag_line,
                platform_region = excluded.platform_region,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());
        command.Parameters.AddWithValue("$puuid", account.Puuid);
        command.Parameters.AddWithValue("$ownerId", ownerDiscordUserId.ToString());
        command.Parameters.AddWithValue("$gameName", account.GameName);
        command.Parameters.AddWithValue("$tagLine", account.TagLine);
        command.Parameters.AddWithValue("$region", account.Region);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return MemberRegistrationResult.AltSuccess(account.GameName, account.TagLine);
    }

    /// <summary>
    /// 클랜 전적 수집/집계용 — 해당 서버에 등록된 부캐 계정 전원을 반환합니다.
    /// </summary>
    public async Task<IReadOnlyList<AltAccount>> GetAllAltAccountsByGuildAsync(
        ulong guildId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT puuid, owner_discord_user_id, riot_game_name, riot_tag_line, platform_region
            FROM member_alt_accounts
            WHERE guild_id = $guildId;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());

        var results = new List<AltAccount>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new AltAccount(
                reader.GetString(0),
                ulong.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return results;
    }

    /// <summary>
    /// 잘못 등록된 멤버를 제거합니다. 연결된 부캐도 함께 제거됩니다.
    /// 이미 수집된 전적 기록(match_participations)은 건드리지 않습니다 — 재등록 후 다시 수집하면 이어서 쌓입니다.
    /// </summary>
    public async Task<MemberDeletionResult> DeleteMemberAsync(
        ulong guildId,
        ulong discordUserId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var countAltsCommand = connection.CreateCommand();
        countAltsCommand.CommandText = """
            SELECT COUNT(*) FROM member_alt_accounts
            WHERE guild_id = $guildId AND owner_discord_user_id = $discordUserId;
            """;
        countAltsCommand.Parameters.AddWithValue("$guildId", guildId.ToString());
        countAltsCommand.Parameters.AddWithValue("$discordUserId", discordUserId.ToString());
        var altCount = (long)(await countAltsCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);

        var deleteAltsCommand = connection.CreateCommand();
        deleteAltsCommand.CommandText = """
            DELETE FROM member_alt_accounts
            WHERE guild_id = $guildId AND owner_discord_user_id = $discordUserId;
            """;
        deleteAltsCommand.Parameters.AddWithValue("$guildId", guildId.ToString());
        deleteAltsCommand.Parameters.AddWithValue("$discordUserId", discordUserId.ToString());
        await deleteAltsCommand.ExecuteNonQueryAsync(cancellationToken);

        var deleteMemberCommand = connection.CreateCommand();
        deleteMemberCommand.CommandText = """
            DELETE FROM members WHERE guild_id = $guildId AND discord_user_id = $discordUserId;
            """;
        deleteMemberCommand.Parameters.AddWithValue("$guildId", guildId.ToString());
        deleteMemberCommand.Parameters.AddWithValue("$discordUserId", discordUserId.ToString());
        var affected = await deleteMemberCommand.ExecuteNonQueryAsync(cancellationToken);

        return new MemberDeletionResult(affected > 0, (int)altCount);
    }

    /// <summary>
    /// 등록된 부캐 하나를 게임이름#태그로 찾아 삭제합니다. 본캐 등록에는 영향을 주지 않습니다.
    /// </summary>
    public async Task<bool> DeleteAltAsync(
        ulong guildId,
        string gameName,
        string tagLine,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM member_alt_accounts
            WHERE guild_id = $guildId
                AND riot_game_name = $gameName COLLATE NOCASE
                AND riot_tag_line = $tagLine COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$guildId", guildId.ToString());
        command.Parameters.AddWithValue("$gameName", gameName);
        command.Parameters.AddWithValue("$tagLine", tagLine);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);

        return affected > 0;
    }
}

public record RegisteredMember(string Puuid, string GameName, string TagLine, string Region);

public record RegisteredMemberWithId(
    ulong DiscordUserId,
    string DisplayName,
    string Puuid,
    string GameName,
    string TagLine,
    string Region);

public record AltAccount(
    string Puuid,
    ulong OwnerDiscordUserId,
    string GameName,
    string TagLine,
    string Region);

public record MemberDeletionResult(bool Deleted, int RemovedAltCount);

public record MemberRegistrationResult(bool IsSuccess, string Message)
{
    public static MemberRegistrationResult Success(string gameName, string tagLine) =>
        new(true, $"✅ AtoZ 멤버 등록 완료: **{gameName}#{tagLine}**");

    public static MemberRegistrationResult AltSuccess(string gameName, string tagLine) =>
        new(true, $"✅ 부캐 연결 완료: **{gameName}#{tagLine}** (전적 통계는 본캐 기준으로 합산됩니다)");

    public static MemberRegistrationResult AlreadyRegistered() =>
        new(false, "❌ 이 Riot 계정은 이미 다른 AtoZ 멤버에게 등록되어 있습니다.");

    public static MemberRegistrationResult Failure(string message) => new(false, message);
}

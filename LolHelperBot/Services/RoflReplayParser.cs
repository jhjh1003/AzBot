// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-19
// Reviewer: (박정훈)
// Review: 리플(.rofl) 파일 메타데이터 파싱 — 라이엇 비공식/비문서화 포맷입니다.
// 클라이언트 버전에 따라 메타데이터 위치가 완전히 다릅니다:
//   - 14.11 미만(구버전): 헤더(오프셋 262부터 26바이트 FileInfo) 안의 metadataOffset/payloadHeaderOffset로 위치 계산
//   - 14.11 이상(신버전): 파일 맨 끝 4바이트가 메타데이터 길이, 그 앞에 메타데이터 JSON이 붙어있음
//   - 정확히 14.10: 라이엇이 해당 버전에서 일시적으로 메타데이터를 제거함 (분석 불가)
// 위 구조는 오픈소스 rofl-parser.js(https://github.com/gzordrai/rofl-parser.js)의 실제 소스코드를 참고해 검증했습니다.
// statsJson 안의 참가자별 필드명은 라이엇이 공식 문서화하지 않아 여러 후보 키를 시도합니다.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LolHelperBot.Services;

public static class RoflReplayParser
{
    private static readonly byte[] Magic = "RIOT"u8.ToArray();
    private static readonly Regex VersionPattern = new(@"^(\d{2})\.(\d{1,2})$", RegexOptions.Compiled);

    public static RoflParseResult Parse(byte[] data)
    {
        try
        {
            if (data.Length < 4 || !data.AsSpan(0, 4).SequenceEqual(Magic))
            {
                return RoflParseResult.Failure("`.rofl` 리플레이 파일 형식이 아닌 것 같습니다 (매직 바이트 불일치).");
            }

            var metadataExtraction = ExtractMetadataJson(data);
            if (!metadataExtraction.IsSuccess || metadataExtraction.Json is null)
            {
                return RoflParseResult.Failure(metadataExtraction.ErrorMessage ?? "메타데이터를 찾지 못했습니다.");
            }

            using var metadataDoc = JsonDocument.Parse(metadataExtraction.Json);
            var root = metadataDoc.RootElement;

            if (!root.TryGetProperty("statsJson", out var statsJsonElement) ||
                statsJsonElement.ValueKind != JsonValueKind.String)
            {
                return RoflParseResult.Failure("리플 메타데이터에서 참가자 정보(statsJson)를 찾지 못했습니다.");
            }

            var gameDurationSeconds = root.TryGetProperty("gameLength", out var gameLengthElement) &&
                gameLengthElement.TryGetInt64(out var gameLengthMs)
                ? gameLengthMs / 1000
                : 0L;

            using var statsDoc = JsonDocument.Parse(statsJsonElement.GetString() ?? "[]");
            if (statsDoc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return RoflParseResult.Failure("리플 참가자 정보(statsJson) 형식이 예상과 다릅니다.");
            }

            var participants = new List<RoflParticipant>();
            foreach (var player in statsDoc.RootElement.EnumerateArray())
            {
                var championName = GetString(player, "SKIN", "CHAMPION") ?? "Unknown";
                var winValue = GetString(player, "WIN");
                var win = winValue is not null && winValue.Equals("Win", StringComparison.OrdinalIgnoreCase);
                var teamId = GetInt(player, "TEAM") ?? 0;
                var kills = GetInt(player, "CHAMPIONS_KILLED") ?? 0;
                var deaths = GetInt(player, "NUM_DEATHS") ?? 0;
                var assists = GetInt(player, "ASSISTS") ?? 0;
                var creepScore = (GetInt(player, "MINIONS_KILLED") ?? 0) + (GetInt(player, "NEUTRAL_MINIONS_KILLED") ?? 0);
                var position = NormalizePosition(GetString(player, "TEAM_POSITION", "INDIVIDUAL_POSITION"));
                var puuid = GetString(player, "PUUID");
                var gameName = GetString(player, "RIOT_ID_GAME_NAME", "GAME_NAME");
                var tagLine = GetString(player, "RIOT_ID_TAG_LINE", "TAG_LINE");

                participants.Add(new RoflParticipant(
                    string.IsNullOrWhiteSpace(puuid) ? null : puuid,
                    string.IsNullOrWhiteSpace(gameName) ? null : gameName,
                    string.IsNullOrWhiteSpace(tagLine) ? null : tagLine,
                    championName,
                    position,
                    teamId,
                    win,
                    kills,
                    deaths,
                    assists,
                    creepScore));
            }

            if (participants.Count == 0)
            {
                return RoflParseResult.Failure("리플에서 참가자 정보를 하나도 읽지 못했습니다.");
            }

            var syntheticMatchId = BuildSyntheticMatchId(gameDurationSeconds, participants);
            return RoflParseResult.Success(new RoflMatch(syntheticMatchId, gameDurationSeconds, participants));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[리플 파싱 오류] {ex}");
            return RoflParseResult.Failure(
                "리플 파일을 분석하는 중 오류가 발생했습니다. (.rofl은 비공식 포맷이라 클라이언트 버전에 따라 구조가 다를 수 있어요)");
        }
    }

    /// <summary>
    /// 클라이언트 버전(파일 바이트 15~19에 있는 게임 버전 문자열)에 따라 메타데이터 JSON의 위치가 다릅니다.
    /// 버전을 읽지 못하면 구버전(헤더 기반) 방식으로 대체 시도합니다.
    /// </summary>
    private static (bool IsSuccess, string? Json, string? ErrorMessage) ExtractMetadataJson(byte[] data)
    {
        var useNewFormat = false;

        if (data.Length >= 20)
        {
            var versionRaw = Encoding.ASCII.GetString(data, 15, 5).TrimEnd('.', '\0');
            var versionMatch = VersionPattern.Match(versionRaw);
            if (versionMatch.Success)
            {
                var major = int.Parse(versionMatch.Groups[1].Value);
                var minor = int.Parse(versionMatch.Groups[2].Value);

                if (major == 14 && minor == 10)
                {
                    return (false, null,
                        "이 리플은 14.10 버전으로 기록된 파일입니다. 라이엇이 해당 버전에서 일시적으로 메타데이터를 제거해서 분석할 수 없습니다.");
                }

                useNewFormat = major > 14 || (major == 14 && minor >= 11);
            }
        }

        return useNewFormat ? ExtractFromFileEnd(data) : ExtractFromHeader(data);
    }

    /// <summary>14.11 이상 — 파일 맨 끝 4바이트가 메타데이터 길이, 그 앞에 메타데이터 JSON이 붙어있습니다.</summary>
    private static (bool IsSuccess, string? Json, string? ErrorMessage) ExtractFromFileEnd(byte[] data)
    {
        if (data.Length < 4)
        {
            return (false, null, "리플 파일이 손상된 것 같습니다 (파일이 너무 작음).");
        }

        var metadataLength = BitConverter.ToUInt32(data, data.Length - 4);
        var metadataStart = (long)data.Length - metadataLength - 4;

        if (metadataLength == 0 || metadataStart < 0 || metadataStart >= data.Length - 4)
        {
            return (false, null, "리플 파일 끝부분에서 메타데이터를 찾지 못했습니다 (손상되었거나 지원하지 않는 버전).");
        }

        var json = Encoding.UTF8.GetString(data, (int)metadataStart, (int)metadataLength);
        return (true, json, null);
    }

    /// <summary>14.11 미만(구버전) — 오프셋 262부터 26바이트짜리 FileInfo 헤더에 metadataOffset/payloadHeaderOffset이 있습니다.</summary>
    private static (bool IsSuccess, string? Json, string? ErrorMessage) ExtractFromHeader(byte[] data)
    {
        const int fileInfoOffset = 262;
        const int fileInfoLength = 26;

        if (data.Length < fileInfoOffset + fileInfoLength)
        {
            return (false, null, "리플 파일 헤더가 예상보다 짧습니다 (버전이 다르거나 손상된 파일일 수 있음).");
        }

        var metadataOffset = BitConverter.ToUInt32(data, fileInfoOffset + 6);
        var payloadHeaderOffset = BitConverter.ToUInt32(data, fileInfoOffset + 14);

        if (metadataOffset == 0 || payloadHeaderOffset <= metadataOffset || payloadHeaderOffset > (uint)data.Length)
        {
            return (false, null, "리플 파일 헤더를 해석하지 못했습니다 (버전이 다르거나 손상된 파일일 수 있음).");
        }

        var json = Encoding.UTF8.GetString(data, (int)metadataOffset, (int)(payloadHeaderOffset - metadataOffset));
        return (true, json, null);
    }

    /// <summary>
    /// rofl 메타데이터에는 Riot의 공식 매치ID(예: KR_1234567890)가 들어있지 않아서,
    /// 경기 길이 + 참가자 기록을 조합한 고정 해시로 대체 ID를 만듭니다.
    /// 같은 리플을 다시 업로드해도 항상 같은 ID가 나오므로 중복 저장은 방지되지만,
    /// 같은 경기를 /전적수집(Riot API)으로도 이미 모았다면 서로 다른 ID로 취급되어 중복 집계될 수 있습니다.
    /// </summary>
    private static string BuildSyntheticMatchId(long gameDurationSeconds, IReadOnlyList<RoflParticipant> participants)
    {
        var signature = string.Join(
            "|",
            participants
                .Select(p => $"{p.ChampionName}:{p.Kills}:{p.Deaths}:{p.Assists}:{p.TeamId}")
                .OrderBy(s => s, StringComparer.Ordinal));
        var hashInput = $"{gameDurationSeconds}|{signature}";
        var hashBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(hashInput));
        return "rofl-" + Convert.ToHexString(hashBytes)[..16].ToLowerInvariant();
    }

    private static string NormalizePosition(string? position)
    {
        if (string.IsNullOrWhiteSpace(position))
        {
            return string.Empty;
        }

        return position.Trim().ToUpperInvariant() switch
        {
            "TOP" => "TOP",
            "JUNGLE" => "JUNGLE",
            "MIDDLE" or "MID" => "MIDDLE",
            "BOTTOM" or "BOT" or "ADC" => "BOTTOM",
            "UTILITY" or "SUPPORT" => "UTILITY",
            _ => string.Empty,
        };
    }

    private static string? GetString(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var value))
            {
                continue;
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return value.GetString();
                case JsonValueKind.Number:
                    return value.ToString();
            }
        }

        return null;
    }

    private static int? GetInt(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
            {
                return intValue;
            }

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }
}

public record RoflParticipant(
    string? Puuid,
    string? RiotGameName,
    string? RiotTagLine,
    string ChampionName,
    string TeamPosition,
    int TeamId,
    bool Win,
    int Kills,
    int Deaths,
    int Assists,
    int CreepScore);

public record RoflMatch(string SyntheticMatchId, long GameDurationSeconds, IReadOnlyList<RoflParticipant> Participants);

public record RoflParseResult(bool IsSuccess, string Message, RoflMatch? Match = null)
{
    public static RoflParseResult Success(RoflMatch match) => new(true, "리플 파일을 분석했습니다.", match);

    public static RoflParseResult Failure(string reason) => new(false, reason);
}

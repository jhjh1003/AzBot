// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-21
// Reviewer: (박정훈)
// Review: "기" 멤버의 AZ 자랭 케이틀린 최근 20경기 — 핵심 특성(키스톤)별 승률 1회성 조회.
// "칼날비"(Hail of Blades)/"기발=기민한 발놀림"(Fleet Footwork) 같은 특성 이름은 Data Dragon의
// runesReforged.json(공식, 인증 불필요)으로 정확히 해석합니다. 우리 DB엔 특성 선택을 저장 안 해서
// 매치별로 Riot API 원본을 다시 불러 participant.perks에서 뽑습니다.
// `dotnet run -- caitlyn-build <displayNameContains>`.

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LolHelperBot.Tools;

public static class CaitlynBuildExperiment
{
    private const int RecentGameCount = 20;

    public static async Task RunAsync(string apiKey, string accountRegion, string databasePath, string displayNameContains)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        await connection.OpenAsync();

        var memberCommand = connection.CreateCommand();
        memberCommand.CommandText = "SELECT discord_user_id, discord_display_name, puuid FROM members WHERE discord_display_name LIKE $pattern LIMIT 5;";
        memberCommand.Parameters.AddWithValue("$pattern", $"%{displayNameContains}%");

        var members = new List<(string UserId, string Name, string Puuid)>();
        await using (var reader = await memberCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                members.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        if (members.Count == 0)
        {
            Console.WriteLine($"[caitlyn-build] 닉네임에 '{displayNameContains}'가 들어간 멤버를 못 찾았습니다.");
            return;
        }

        // 부분일치가 여러 명이면 정확히 일치하는 닉네임을 우선.
        var exactMatch = members.FirstOrDefault(m => m.Name == displayNameContains);
        var chosen = exactMatch.Name is not null ? exactMatch : members[0];

        if (members.Count > 1)
        {
            Console.WriteLine($"[caitlyn-build] '{displayNameContains}'로 여러 명 매칭됨: {string.Join(", ", members.Select(m => m.Name))} — {(exactMatch.Name is not null ? "정확히 일치하는" : "첫 번째")} '{chosen.Name}'로 진행합니다.");
        }

        var (userId, name, puuid) = chosen;
        Console.WriteLine($"[caitlyn-build] 대상: {name} (discord_user_id={userId})\n");

        var matchCommand = connection.CreateCommand();
        matchCommand.CommandText = """
            SELECT DISTINCT match_id, win
            FROM match_participations
            WHERE discord_user_id = $userId AND champion_name = 'Caitlyn'
            ORDER BY match_id DESC
            LIMIT $limit;
            """;
        matchCommand.Parameters.AddWithValue("$userId", userId);
        matchCommand.Parameters.AddWithValue("$limit", RecentGameCount);

        var matches = new List<(string MatchId, bool Win)>();
        await using (var reader = await matchCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                matches.Add((reader.GetString(0), reader.GetInt64(1) != 0));
            }
        }

        if (matches.Count == 0)
        {
            Console.WriteLine($"[caitlyn-build] {name}님의 케이틀린 자유 랭크 전적이 DB에 없습니다.");
            return;
        }

        Console.WriteLine($"[caitlyn-build] 최근 케이틀린 {matches.Count}판 대상 — 특성(룬) 이름 사전(Data Dragon) 로딩 중...");

        using var ddragonClient = new HttpClient { BaseAddress = new Uri("https://ddragon.leagueoflegends.com"), Timeout = TimeSpan.FromSeconds(15) };
        var versions = await ddragonClient.GetFromJsonAsync<List<string>>("/api/versions.json") ?? [];
        var latestVersion = versions.FirstOrDefault() ?? "14.1.1";
        var runesData = await ddragonClient.GetFromJsonAsync<JsonElement>($"/cdn/{latestVersion}/data/ko_KR/runesReforged.json");

        // perkId -> 이름 (트리별 슬롯을 전부 순회하며 평평하게 모음. 키스톤이든 일반 룬이든 다 여기 있음).
        var runeNameById = new Dictionary<int, string>();
        foreach (var tree in runesData.EnumerateArray())
        {
            foreach (var slot in tree.GetProperty("slots").EnumerateArray())
            {
                foreach (var rune in slot.GetProperty("runes").EnumerateArray())
                {
                    var id = rune.GetProperty("id").GetInt32();
                    var runeName = rune.GetProperty("name").GetString() ?? $"#{id}";
                    runeNameById[id] = runeName;
                }
            }
        }

        Console.WriteLine("[caitlyn-build] Riot API로 매치별 특성 조회 중...\n");

        using var client = new HttpClient
        {
            BaseAddress = new Uri($"https://{accountRegion}.api.riotgames.com"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        client.DefaultRequestHeaders.Add("X-Riot-Token", apiKey);

        var results = new List<(string MatchId, bool Win, string Keystone)>();

        foreach (var (matchId, win) in matches)
        {
            using var response = await client.GetAsync($"/lol/match/v5/matches/{Uri.EscapeDataString(matchId)}");
            await Task.Delay(1200);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"  [{matchId}] 조회 실패: {(int)response.StatusCode}");
                continue;
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var participants = json.GetProperty("info").GetProperty("participants");
            JsonElement? mine = null;
            foreach (var p in participants.EnumerateArray())
            {
                if (p.TryGetProperty("puuid", out var puuidProp) && puuidProp.GetString() == puuid)
                {
                    mine = p;
                    break;
                }
            }

            if (mine is null)
            {
                Console.WriteLine($"  [{matchId}] 참가자 정보를 못 찾음(puuid 불일치 — 부캐/키 교체 가능성)");
                continue;
            }

            if (!mine.Value.TryGetProperty("perks", out var perks) ||
                !perks.TryGetProperty("styles", out var styles))
            {
                Console.WriteLine($"  [{matchId}] 특성 정보 없음");
                continue;
            }

            // styles[0]이 주 특성 트리, 그 안 selections[0]이 키스톤(맨 위 칸).
            var primaryStyle = styles.EnumerateArray().FirstOrDefault(s =>
                s.TryGetProperty("description", out var d) && d.GetString() == "primaryStyle");
            if (primaryStyle.ValueKind == JsonValueKind.Undefined ||
                !primaryStyle.TryGetProperty("selections", out var selections) ||
                selections.GetArrayLength() == 0)
            {
                Console.WriteLine($"  [{matchId}] 키스톤 특성을 못 찾음");
                continue;
            }

            var keystoneId = selections[0].GetProperty("perk").GetInt32();
            var keystoneName = runeNameById.GetValueOrDefault(keystoneId, $"#{keystoneId}(알수없음)");

            results.Add((matchId, win, keystoneName));
            Console.WriteLine($"  [{matchId}] {(win ? "승" : "패")} — {keystoneName}");
        }

        Console.WriteLine("\n===== 키스톤 특성별 승률 집계 (최근 20판 기준) =====");
        foreach (var group in results.GroupBy(r => r.Keystone).OrderByDescending(g => g.Count()))
        {
            var wins = group.Count(g => g.Win);
            var total = group.Count();
            Console.WriteLine($"  {group.Key}: {total}판 {wins}승 · 승률 {wins * 100.0 / total:F0}%");
        }
    }
}

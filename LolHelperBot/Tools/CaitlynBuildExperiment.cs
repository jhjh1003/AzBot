// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-21
// Reviewer: (박정훈)
// Review: "기" 멤버의 AZ 자랭 케이틀린 빌드별 승률 1회성 조회 실험 코드. 우리 DB엔 아이템 빌드를
// 저장 안 해서(승률/KDA 등만 저장), 매치별로 Riot API 원본을 다시 불러 item0~6을 뽑고, Data
// Dragon(공식, 인증 불필요)으로 아이템 이름을 정확히 해석해서 보여줍니다 — "칼날비"/"기발" 같은
// 커뮤니티 은어를 제가 임의로 아이템에 매칭하면 틀릴 위험이 있어서, 실제 아이템 이름을 그대로
// 보여주고 사용자가 직접 어느 쪽인지 확인하도록 함. `dotnet run -- caitlyn-build <displayNameContains>`.

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LolHelperBot.Tools;

public static class CaitlynBuildExperiment
{
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

        // 부분일치가 여러 명이면 정확히 일치하는 닉네임을 우선(예: "기"로 검색했을 때
        // "니가그린기린그림"이 아니라 정말 닉네임이 "기"인 사람을 골라야 함).
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
            ORDER BY match_id DESC;
            """;
        matchCommand.Parameters.AddWithValue("$userId", userId);

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

        Console.WriteLine($"[caitlyn-build] 케이틀린 {matches.Count}판 발견 — 아이템 이름 사전(Data Dragon) 로딩 중...");

        using var ddragonClient = new HttpClient { BaseAddress = new Uri("https://ddragon.leagueoflegends.com"), Timeout = TimeSpan.FromSeconds(15) };
        var versions = await ddragonClient.GetFromJsonAsync<List<string>>("/api/versions.json") ?? [];
        var latestVersion = versions.FirstOrDefault() ?? "14.1.1";
        var itemData = await ddragonClient.GetFromJsonAsync<JsonElement>($"/cdn/{latestVersion}/data/ko_KR/item.json");
        var itemsRoot = itemData.GetProperty("data");

        string ItemName(int id)
        {
            if (id == 0) return "";
            return itemsRoot.TryGetProperty(id.ToString(), out var item) && item.TryGetProperty("name", out var n)
                ? n.GetString() ?? $"#{id}"
                : $"#{id}(알수없음)";
        }

        int ItemGold(int id)
        {
            if (id == 0) return 0;
            return itemsRoot.TryGetProperty(id.ToString(), out var item) &&
                item.TryGetProperty("gold", out var gold) &&
                gold.TryGetProperty("total", out var total)
                ? total.GetInt32()
                : 0;
        }

        Console.WriteLine($"[caitlyn-build] Riot API로 매치별 빌드(item0~6) 조회 중...\n");

        using var client = new HttpClient
        {
            BaseAddress = new Uri($"https://{accountRegion}.api.riotgames.com"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        client.DefaultRequestHeaders.Add("X-Riot-Token", apiKey);

        var buildResults = new List<(string MatchId, bool Win, string SignatureItem)>();

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

            // item6은 보통 장신구(와드) 슬롯이라 빌드 분류에서 제외. 나머지 중 골드 총액이 가장 비싼
            // 아이템(보통 코어/신화급 딜템)을 "빌드 시그니처"로 잡음.
            var items = new List<int>();
            for (var i = 0; i <= 5; i++)
            {
                if (mine.Value.TryGetProperty($"item{i}", out var itemProp) && itemProp.ValueKind == JsonValueKind.Number)
                {
                    var itemId = itemProp.GetInt32();
                    if (itemId != 0)
                    {
                        items.Add(itemId);
                    }
                }
            }

            var signatureId = items.OrderByDescending(ItemGold).FirstOrDefault();
            var signature = signatureId == 0 ? "(완성 아이템 없음)" : ItemName(signatureId);
            var fullBuild = string.Join(", ", items.Select(ItemName));

            buildResults.Add((matchId, win, signature));
            Console.WriteLine($"  [{matchId}] {(win ? "승" : "패")} — 시그니처: {signature} | 전체 빌드: {fullBuild}");
        }

        Console.WriteLine("\n===== 빌드(최고가 아이템 기준)별 승률 집계 =====");
        foreach (var group in buildResults.GroupBy(b => b.SignatureItem).OrderByDescending(g => g.Count()))
        {
            var wins = group.Count(g => g.Win);
            var total = group.Count();
            Console.WriteLine($"  {group.Key}: {total}판 {wins}승 · 승률 {wins * 100.0 / total:F0}%");
        }
    }
}

// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-21
// Reviewer: (박정훈)
// Review: "픽창에서 라인별 선픽/후픽 체크 가능?" 질문에 답하려고, Match-V5 상세 API가 밴 순서
// (pickTurn)는 주는데 실제 챔피언 픽 순서도 주는지 raw JSON으로 직접 확인하는 1회성 실험 코드.
// `dotnet run -- match-raw <matchId>`. 확인 끝나면 지워도 되는 임시 도구.

using System.Net.Http.Json;
using System.Text.Json;

namespace LolHelperBot.Tools;

public static class MatchRawDumpExperiment
{
    public static async Task RunAsync(string apiKey, string accountRegion, string matchId)
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"https://{accountRegion}.api.riotgames.com"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        client.DefaultRequestHeaders.Add("X-Riot-Token", apiKey);

        Console.WriteLine($"[match-raw] {matchId} 매치 상세 원본 조회 중...");
        using var response = await client.GetAsync($"/lol/match/v5/matches/{Uri.EscapeDataString(matchId)}");
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[match-raw] 실패: {(int)response.StatusCode} {response.ReasonPhrase}");
            return;
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var info = json.GetProperty("info");

        Console.WriteLine("\n===== info.teams (밴 정보 — pickTurn이 밴에만 있는지 확인) =====");
        Console.WriteLine(JsonSerializer.Serialize(info.GetProperty("teams"), new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine("\n===== info.participants[0] 전체 필드 목록(픽 순서 관련 필드가 있는지 확인) =====");
        var participant0 = info.GetProperty("participants")[0];
        foreach (var prop in participant0.EnumerateObject())
        {
            var name = prop.Name.ToLowerInvariant();
            if (name.Contains("pick") || name.Contains("turn") || name.Contains("order") || name.Contains("draft") || name.Contains("phase"))
            {
                Console.WriteLine($"  ⭐ {prop.Name} = {prop.Value}");
            }
        }

        Console.WriteLine("\n(위에 ⭐ 표시된 줄이 없으면 participants에는 픽 순서 관련 필드가 전혀 없다는 뜻)");
        Console.WriteLine("\n===== info.participants[0] 필드명 전체 목록 =====");
        Console.WriteLine(string.Join(", ", participant0.EnumerateObject().Select(p => p.Name)));
    }
}

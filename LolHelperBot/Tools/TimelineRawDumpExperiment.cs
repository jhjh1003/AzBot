// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-21
// Reviewer: (박정훈)
// Review: op.gg의 "시간별 OP스코어 그래프"와 우리 기여도 점수(정적/판 종료 후 1회)를 비교해보자는
// 요청에 따른 1회성 실험 코드. RiotApiClient.GetTimelineAsync는 이미 필요한 필드(totalGold/xp/cs)만
// 추려서 역직렬화하는데, 이 도구는 그 전에 Riot가 실제로 더 주는 원본 필드가 뭔지(damageStats,
// championStats 등)를 확인하려고 raw JSON을 그대로 찍어봅니다. `dotnet run -- timeline-raw <matchId>`.
// 결과 확인 후 필요 없으면 지워도 되는 임시 도구입니다(TimelineExperiment.cs와 같은 성격).

using System.Net.Http.Json;
using System.Text.Json;

namespace LolHelperBot.Tools;

public static class TimelineRawDumpExperiment
{
    public static async Task RunAsync(string apiKey, string accountRegion, string matchId)
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"https://{accountRegion}.api.riotgames.com"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        client.DefaultRequestHeaders.Add("X-Riot-Token", apiKey);

        Console.WriteLine($"[timeline-raw] {matchId} 타임라인 원본 조회 중...");
        using var response = await client.GetAsync($"/lol/match/v5/matches/{Uri.EscapeDataString(matchId)}/timeline");
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[timeline-raw] 실패: {(int)response.StatusCode} {response.ReasonPhrase}");
            return;
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var frames = json.GetProperty("info").GetProperty("frames");
        var frameCount = frames.GetArrayLength();
        Console.WriteLine($"[timeline-raw] 프레임 {frameCount}개. 5번째 프레임(대략 5분 시점) 참가자1 전체 필드 + 이벤트 타입 종류를 찍습니다.\n");

        // 참가자1(participantId=1)의 5번째 프레임 raw 구조 전체를 그대로 출력 — 어떤 필드가 있는지 확인용.
        var sampleFrame = frames[Math.Min(5, frameCount - 1)];
        var participant1Frame = sampleFrame.GetProperty("participantFrames").GetProperty("1");
        Console.WriteLine("===== participantFrames[\"1\"] (raw) =====");
        Console.WriteLine(JsonSerializer.Serialize(participant1Frame, new JsonSerializerOptions { WriteIndented = true }));

        // 전체 프레임에 걸쳐 등장하는 이벤트 타입 종류를 모아서 보여줍니다(어떤 이벤트로 뭘 더 뽑을 수 있는지 감 잡기용).
        var eventTypes = new SortedSet<string>();
        foreach (var frame in frames.EnumerateArray())
        {
            if (!frame.TryGetProperty("events", out var events))
            {
                continue;
            }

            foreach (var evt in events.EnumerateArray())
            {
                if (evt.TryGetProperty("type", out var typeProp))
                {
                    eventTypes.Add(typeProp.GetString() ?? "?");
                }
            }
        }

        Console.WriteLine("\n===== 등장한 이벤트 타입 목록 =====");
        foreach (var type in eventTypes)
        {
            Console.WriteLine($"  - {type}");
        }
    }
}

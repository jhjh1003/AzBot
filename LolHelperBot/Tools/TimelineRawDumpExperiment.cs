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

        // 정글 체류시간/갱 분석 가능 여부 확인용(2026-08-21) — CHAMPION_KILL 이벤트에 위치(x,y)가
        // 있는지, 그리고 participantFrame에 매 분 위치가 있는지 직접 찍어봅니다.
        Console.WriteLine("\n===== CHAMPION_KILL 이벤트 샘플(위치 포함 여부 확인) =====");
        var sampleKill = frames.EnumerateArray()
            .SelectMany(f => f.TryGetProperty("events", out var evts) ? evts.EnumerateArray() : [])
            .FirstOrDefault(e => e.TryGetProperty("type", out var t) && t.GetString() == "CHAMPION_KILL");
        if (sampleKill.ValueKind != JsonValueKind.Undefined)
        {
            Console.WriteLine(JsonSerializer.Serialize(sampleKill, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine("  (이 매치엔 CHAMPION_KILL 이벤트가 없음)");
        }

        // v4.0.0 "오브젝트 골드환산" 스펙 검증용(2026-08-21) — 드래곤/바론/유충/전령 구분(monsterType)이
        // 이벤트에 있는지 확인.
        Console.WriteLine("\n===== ELITE_MONSTER_KILL 이벤트 전체(오브젝트 종류 구분 필드 확인) =====");
        var monsterKills = frames.EnumerateArray()
            .SelectMany(f => f.TryGetProperty("events", out var evts) ? evts.EnumerateArray() : [])
            .Where(e => e.TryGetProperty("type", out var t) && t.GetString() == "ELITE_MONSTER_KILL")
            .ToList();
        foreach (var mk in monsterKills.Take(5))
        {
            Console.WriteLine(JsonSerializer.Serialize(mk, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine($"(총 {monsterKills.Count}건 중 5건만 표시)");

        Console.WriteLine("\n===== 전체 참가자 1의 매 프레임 position 궤적(분 단위) =====");
        foreach (var frame in frames.EnumerateArray())
        {
            var ts = frame.GetProperty("timestamp").GetInt64() / 60000;
            if (frame.GetProperty("participantFrames").TryGetProperty("1", out var pf) &&
                pf.TryGetProperty("position", out var pos))
            {
                Console.WriteLine($"  {ts}분: x={pos.GetProperty("x").GetInt32()}, y={pos.GetProperty("y").GetInt32()}");
            }
        }
    }
}

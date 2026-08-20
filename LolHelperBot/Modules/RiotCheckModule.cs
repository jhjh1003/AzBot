// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-18
// Reviewer: (박정훈)
// Review: 로컬 스모크 테스트 예정 - Riot API 키/연결 확인용

using Discord.Interactions;
using LolHelperBot.Services;

namespace LolHelperBot.Modules;

public class RiotCheckModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly RiotApiClient _riotApiClient;

    public RiotCheckModule(RiotApiClient riotApiClient)
    {
        _riotApiClient = riotApiClient;
    }

    [SlashCommand("riotcheck", "Riot API 키/연결이 정상인지 확인합니다.")]
    public async Task RiotCheckAsync()
    {
        await DeferAsync();
        var result = await _riotApiClient.CheckPlatformStatusAsync();
        await FollowupAsync(result.Message);
    }
}

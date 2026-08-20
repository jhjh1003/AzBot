// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-18
// Reviewer: (박정훈)
// Review: 로컬 스모크 테스트 예정 - 디스코드 봇이 슬래시 커맨드에 응답하는지 확인용

using Discord.Interactions;

namespace LolHelperBot.Modules;

public class PingModule : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("ping", "봇이 살아있는지 확인합니다.")]
    public async Task PingAsync()
    {
        await RespondAsync("🏓 pong! 봇이 정상적으로 응답하고 있습니다.");
    }
}

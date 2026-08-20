// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-20
// Reviewer: (박정훈)
// Review: 리팩토링 1단계 — AtoZModule/ClanStatsModule 두 곳에 똑같이 복붙돼 있던 운영자 권한
// 체크 로직을 한 곳으로 모음(외부 코드리뷰에서 지적된 중복 코드 문제). 서버 소유자이거나
// 서버 관리/관리자 권한이 있어야 등록·삭제·수집 같은 운영자 전용 명령을 쓸 수 있습니다.
// SocketInteractionContext 확장 메서드라, 각 모듈에서 기존과 거의 같은 형태(`Context.CanManageMembers()`)로 씁니다.

using Discord.Interactions;
using Discord.WebSocket;

namespace LolHelperBot.Services;

public static class PermissionChecker
{
    public static bool CanManageMembers(this SocketInteractionContext context)
    {
        if (context.Guild?.OwnerId == context.User.Id)
        {
            return true;
        }

        return context.User is SocketGuildUser guildUser &&
            (guildUser.GuildPermissions.Administrator || guildUser.GuildPermissions.ManageGuild);
    }
}

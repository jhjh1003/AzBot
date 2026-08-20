// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-20
// Reviewer: (박정훈)
// Review: 리팩토링 2단계 — ChampionTierService를 뽑아내는 과정에서, ClanStatsModule에 있던
// GetPositionOrder(탑/정글/미드/원딜/서폿 정렬 순서)가 Module 쪽에서도 여전히 쓰이고
// (내전적/아재전적) 새 서비스 쪽에서도 필요해져서, 서비스 하나에 복붙하는 대신 공용 유틸로 뺌
// (ClanConstants.cs 등과 같은 패턴 — using static으로 가져다 씀).

namespace LolHelperBot.Services;

public static class PositionOrder
{
    // 탑 → 정글 → 미드 → 원딜 → 서폿 순으로 표시하기 위한 정렬 키.
    public static int GetPositionOrder(string position) => position switch
    {
        "TOP" => 0,
        "JUNGLE" => 1,
        "MIDDLE" => 2,
        "BOTTOM" => 3,
        "UTILITY" => 4,
        _ => 5,
    };
}

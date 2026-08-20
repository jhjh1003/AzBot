// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-20
// Reviewer: (박정훈)
// Review: 리팩토링 1단계 — AtoZModule/ClanStatsModule/RiotApiClient 세 곳에 완전히 똑같이
// 복붙돼 있던 "게임이름#태그" 파싱 로직을 한 곳으로 모음(외부 코드리뷰에서 지적된 중복 코드 문제).

namespace LolHelperBot.Services;

public static class RiotIdParser
{
    public static bool TryParseRiotId(string riotId, out string gameName, out string tagLine)
    {
        gameName = string.Empty;
        tagLine = string.Empty;

        if (string.IsNullOrWhiteSpace(riotId))
        {
            return false;
        }

        var separatorIndex = riotId.LastIndexOf('#');
        if (separatorIndex <= 0 || separatorIndex == riotId.Length - 1)
        {
            return false;
        }

        gameName = riotId[..separatorIndex].Trim();
        tagLine = riotId[(separatorIndex + 1)..].Trim();
        return gameName.Length > 0 && tagLine.Length > 0;
    }
}

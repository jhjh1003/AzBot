// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-20
// Reviewer: (박정훈)
// Review: 리팩토링 1단계 — AtoZModule/ClanStatsModule 두 곳에 똑같이 복붙돼 있던 마크다운
// 이스케이프 로직을 한 곳으로 모음(외부 코드리뷰에서 지적된 중복 코드 문제). 롤아이디/닉네임처럼
// 사용자가 직접 입력한 값을 Discord 임베드에 넣을 때, 마크다운 특수문자 때문에 렌더링이 깨지는
// 걸 막기 위해 씁니다.

namespace LolHelperBot.Services;

public static class MarkdownFormatter
{
    public static string EscapeMarkdown(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("*", "\\*", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal)
        .Replace("`", "\\`", StringComparison.Ordinal)
        .Replace("~", "\\~", StringComparison.Ordinal);
}

// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-19
// Reviewer: (박정훈)
// Review: 일반 사용자가 쓸 수 있는 조회(읽기 전용) 명령어 안내. README.md 5~6장 내용을 기준으로 합니다.
// 운영자 전용 명령(/atoz 멤버등록·부캐등록·멤버목록·멤버삭제·부캐삭제, /전적수집, /리플업로드의 저장:true)은
// 여기 안내에서 의도적으로 뺐습니다 — 일반 사용자는 조회 기능만 쓸 수 있으면 되기 때문입니다.

using Discord;
using Discord.Interactions;

namespace LolHelperBot.Modules;

public class HelpModule : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("기능안내", "일반 멤버가 쓸 수 있는 조회 명령어를 안내합니다.")]
    public async Task ShowGuideAsync()
    {
        var embed = new EmbedBuilder()
            .WithTitle("📖 AtoZ 봇 조회 기능 안내")
            .WithColor(Color.Teal)
            .WithDescription(
                "누구나 쓸 수 있는 조회 명령어만 모았습니다. " +
                "계정 등록(`/atoz 멤버등록`)은 운영자에게 요청해 주세요 — 등록해두면 롤아이디 입력을 생략할 수 있어요.")
            .AddField(
                "📊 AtoZ 클랜 전적 통계 (우리끼리 함께한 자유 랭크 데이터 기준)",
                "`/티어픽 [라인]` — 라인별 챔피언 승률 TOP 5 (누가 몇 판·몇 승률로 플레이했는지 표시) + 전체 워스트 챔피언 TOP 5\n" +
                "`/밴픽추천 [라인]` — 라인별 픽 추천 Top 3(+메타 카운터)와, 맞상대·메타티어·우리픽카운터 기준 밴 추천 최대 3개\n" +
                "`/승률순위` — 등록 멤버 전체 승률·KDA 순위\n" +
                "`/내전적 [멤버]` — 총 승률·라인별 승률(라인별 모스트 챔피언 TOP 3 포함)·전체 모스트/워스트 챔피언 TOP 3\n" +
                "`/조합추천` — 같은 팀에서 함께 나왔을 때 승률 좋은 챔피언 조합 TOP 10\n" +
                "`/바텀듀오` — 원딜+서폿 멤버 조합 승률 모스트 10 / 워스트 10\n" +
                "`/봇듀오챔프승률` — 원딜+서폿 챔피언 조합 승률 모스트 10 / 워스트 10\n" +
                "`/정글미드듀오챔프승률` — 정글+미드 챔피언 조합 승률 모스트 10 / 워스트 10\n" +
                "`/아재전적 [개수]` — 5명 전원이 5인큐한 경기 목록만 (그 판 기여도 순위 👑/💀 표시)\n" +
                "`/명예의전당 [연월]` — 이번 달(또는 지정한 달) 기여도 베스트 플레이어 랭킹")
            .AddField(
                "🎞️ 리플 데이터 확인",
                "`/리플업로드 리플파일:<.rofl>` — 리플 내용을 미리보기(누구나 가능, DB 저장은 운영자 전용)")
            .WithFooter("자세한 설명은 프로젝트 README.md를 참고하세요.")
            .Build();

        await RespondAsync(embed: embed, ephemeral: true);
    }
}

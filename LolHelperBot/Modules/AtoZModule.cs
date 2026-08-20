using System.Text;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using LolHelperBot.Services;
using static LolHelperBot.Services.ClanConstants;
using static LolHelperBot.Services.MarkdownFormatter;
using static LolHelperBot.Services.RiotIdParser;

namespace LolHelperBot.Modules;

[Group("atoz", "AtoZ 멤버를 관리합니다.")]
public partial class AtoZModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly RiotApiClient _riotApiClient;
    private readonly MemberRepository _memberRepository;
    private readonly MatchRepository _matchRepository;

    public AtoZModule(RiotApiClient riotApiClient, MemberRepository memberRepository, MatchRepository matchRepository)
    {
        _riotApiClient = riotApiClient;
        _memberRepository = memberRepository;
        _matchRepository = matchRepository;
    }

    [SlashCommand("멤버등록", "Discord 사용자와 Riot 계정을 연결해 AtoZ 멤버로 등록합니다.")]
    [DefaultMemberPermissions(GuildPermission.ManageGuild)]
    public async Task RegisterMemberAsync(
        [Summary("롤아이디", "게임이름#태그 형식의 Riot ID")]
        string riotId,
        [Summary("멤버", "등록할 Discord 멤버. 생략하면 명령을 실행한 운영자")]
        IUser? member = null)
    {
        await DeferAsync(ephemeral: true);

        if (Context.Guild is null)
        {
            await FollowupAsync("이 명령은 AtoZ Discord 서버에서만 사용할 수 있습니다.", ephemeral: true);
            return;
        }

        if (!Context.CanManageMembers())
        {
            await FollowupAsync(
                "❌ 멤버 등록은 서버 소유자와 서버 관리 권한이 있는 운영자만 사용할 수 있습니다.",
                ephemeral: true);
            return;
        }

        var targetMember = member ?? Context.User;
        if (targetMember.IsBot)
        {
            await FollowupAsync("❌ 봇 계정은 AtoZ 멤버로 등록할 수 없습니다.", ephemeral: true);
            return;
        }

        var account = await _riotApiClient.FindLeagueAccountAsync(riotId);
        if (!account.IsSuccess)
        {
            await FollowupAsync($"❌ 등록 실패: {account.Message}", ephemeral: true);
            return;
        }

        var displayName = targetMember is SocketGuildUser guildUser
            ? guildUser.DisplayName
            : targetMember.Username;
        var result = await _memberRepository.RegisterAsync(
            Context.Guild.Id,
            targetMember.Id,
            displayName,
            account);

        var message = result.IsSuccess
            ? $"{result.Message}\nDiscord 멤버: **{EscapeMarkdown(displayName)}**"
            : result.Message;

        if (result.IsSuccess)
        {
            // 새 멤버가 등록되면, 예전엔 "5명 미달"이라 저장 안 됐던 매치가 이 사람 덕분에 채워질 수 있으므로
            // 다음 /atoz 전적수집에서 다시 평가하도록 캐시를 리셋합니다.
            var resetCount = await _matchRepository.ResetUnqualifiedCheckedMatchesAsync(Context.Guild.Id);
            if (resetCount > 0)
            {
                message += $"\n🔄 이전에 5명 미달로 저장 안 됐던 매치 {resetCount}건을 다음 `/atoz 전적수집`에서 다시 확인하도록 초기화했습니다.";
            }
        }

        await FollowupAsync(message, ephemeral: true);
    }

    [SlashCommand("부캐등록", "이미 본캐로 등록된 멤버에게 부캐(다른 Riot 계정)를 연결합니다. 전적 통계는 본캐 기준으로 합산됩니다.")]
    [DefaultMemberPermissions(GuildPermission.ManageGuild)]
    public async Task RegisterAltAsync(
        [Summary("부캐롤아이디", "게임이름#태그 형식의 부캐 Riot ID")]
        string riotId,
        [Summary("본캐멤버", "부캐를 연결할 본캐 Discord 멤버. 생략하면 명령을 실행한 운영자 본인")]
        IUser? member = null)
    {
        await DeferAsync(ephemeral: true);

        if (Context.Guild is null)
        {
            await FollowupAsync("이 명령은 AtoZ Discord 서버에서만 사용할 수 있습니다.", ephemeral: true);
            return;
        }

        if (!Context.CanManageMembers())
        {
            await FollowupAsync(
                "❌ 부캐 등록은 서버 소유자와 서버 관리 권한이 있는 운영자만 사용할 수 있습니다.",
                ephemeral: true);
            return;
        }

        var targetMember = member ?? Context.User;
        if (targetMember.IsBot)
        {
            await FollowupAsync("❌ 봇 계정에는 부캐를 등록할 수 없습니다.", ephemeral: true);
            return;
        }

        var mainAccount = await _memberRepository.GetByDiscordUserAsync(Context.Guild.Id, targetMember.Id);
        if (mainAccount is null)
        {
            await FollowupAsync(
                "❌ 먼저 `/atoz 멤버등록`으로 본캐를 등록해야 부캐를 연결할 수 있습니다.",
                ephemeral: true);
            return;
        }

        var account = await _riotApiClient.FindLeagueAccountAsync(riotId);
        if (!account.IsSuccess)
        {
            await FollowupAsync($"❌ 등록 실패: {account.Message}", ephemeral: true);
            return;
        }

        var result = await _memberRepository.RegisterAltAsync(Context.Guild.Id, targetMember.Id, account);

        var displayName = targetMember is SocketGuildUser guildUser
            ? guildUser.DisplayName
            : targetMember.Username;
        var message = result.IsSuccess
            ? $"{result.Message}\n본캐: **{EscapeMarkdown(displayName)}** ({EscapeMarkdown(mainAccount.GameName)}#{EscapeMarkdown(mainAccount.TagLine)})"
            : result.Message;

        if (result.IsSuccess)
        {
            // 새 부캐가 등록되면, 예전엔 "5명 미달"이라 저장 안 됐던 매치가 이 계정 덕분에 채워질 수 있으므로
            // 다음 /atoz 전적수집에서 다시 평가하도록 캐시를 리셋합니다.
            var resetCount = await _matchRepository.ResetUnqualifiedCheckedMatchesAsync(Context.Guild.Id);
            if (resetCount > 0)
            {
                message += $"\n🔄 이전에 5명 미달로 저장 안 됐던 매치 {resetCount}건을 다음 `/atoz 전적수집`에서 다시 확인하도록 초기화했습니다.";
            }
        }

        await FollowupAsync(message, ephemeral: true);
    }

    [SlashCommand("멤버목록", "AtoZ에 등록된 멤버(본캐/부캐) 현황을 보여줍니다. 잘못 등록됐는지 확인용.")]
    [DefaultMemberPermissions(GuildPermission.ManageGuild)]
    public async Task ListMembersAsync()
    {
        await DeferAsync(ephemeral: true);

        if (Context.Guild is null)
        {
            await FollowupAsync("이 명령은 AtoZ Discord 서버에서만 사용할 수 있습니다.", ephemeral: true);
            return;
        }

        if (!Context.CanManageMembers())
        {
            await FollowupAsync(
                "❌ 멤버 목록 조회는 서버 소유자와 서버 관리 권한이 있는 운영자만 사용할 수 있습니다.",
                ephemeral: true);
            return;
        }

        var members = await _memberRepository.GetAllByGuildAsync(Context.Guild.Id);
        if (members.Count == 0)
        {
            await FollowupAsync("등록된 AtoZ 멤버가 없습니다.", ephemeral: true);
            return;
        }

        var altAccounts = await _memberRepository.GetAllAltAccountsByGuildAsync(Context.Guild.Id);
        var altsByOwner = altAccounts.ToLookup(alt => alt.OwnerDiscordUserId);
        var winRates = await _matchRepository.GetMemberWinRatesAsync(Context.Guild.Id, FlexQueueId);
        var gamesByUserId = winRates.ToDictionary(row => row.DiscordUserId, row => row.Games);

        var lines = new StringBuilder();
        foreach (var member in members.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var games = gamesByUserId.GetValueOrDefault(member.DiscordUserId, 0);
            lines.AppendLine(
                $"**{EscapeMarkdown(member.DisplayName)}** — {EscapeMarkdown(member.GameName)}#{EscapeMarkdown(member.TagLine)} " +
                $"({member.Region.ToUpperInvariant()}) · 수집된 경기 {games}판");

            foreach (var alt in altsByOwner[member.DiscordUserId])
            {
                lines.AppendLine($"　└ 부캐: {EscapeMarkdown(alt.GameName)}#{EscapeMarkdown(alt.TagLine)}");
            }
        }

        var embed = new EmbedBuilder()
            .WithTitle("AtoZ 등록 멤버 현황")
            .WithColor(Color.Teal)
            .WithDescription(lines.ToString())
            .WithFooter($"본캐 {members.Count}명 · 부캐 {altAccounts.Count}개 · 수집된 경기 수는 자유 랭크(/atoz 전적수집) 기준")
            .Build();

        await FollowupAsync(embed: embed, ephemeral: true);
    }

    [SlashCommand("멤버삭제", "잘못 등록된 AtoZ 멤버를 삭제합니다 (연결된 부캐도 함께 삭제). 이미 수집된 전적 기록은 남아있습니다.")]
    [DefaultMemberPermissions(GuildPermission.ManageGuild)]
    public async Task DeleteMemberAsync(
        [Summary("멤버", "등록을 삭제할 Discord 멤버")]
        IUser member)
    {
        await DeferAsync(ephemeral: true);

        if (Context.Guild is null)
        {
            await FollowupAsync("이 명령은 AtoZ Discord 서버에서만 사용할 수 있습니다.", ephemeral: true);
            return;
        }

        if (!Context.CanManageMembers())
        {
            await FollowupAsync(
                "❌ 멤버 삭제는 서버 소유자와 서버 관리 권한이 있는 운영자만 사용할 수 있습니다.",
                ephemeral: true);
            return;
        }

        var result = await _memberRepository.DeleteMemberAsync(Context.Guild.Id, member.Id);
        if (!result.Deleted)
        {
            await FollowupAsync("❌ 등록된 멤버가 아닙니다.", ephemeral: true);
            return;
        }

        var displayName = member is SocketGuildUser guildUser ? guildUser.DisplayName : member.Username;
        var message = $"✅ **{EscapeMarkdown(displayName)}** 등록을 삭제했습니다.";
        if (result.RemovedAltCount > 0)
        {
            message += $" 연결된 부캐 {result.RemovedAltCount}개도 함께 삭제했습니다.";
        }
        message += "\n(이미 수집된 전적 기록은 삭제되지 않습니다. `/atoz 멤버등록`으로 다시 등록하면 계속 이어서 쌓입니다.)";

        await FollowupAsync(message, ephemeral: true);
    }

    [SlashCommand("부캐삭제", "등록된 부캐 연결을 삭제합니다. 본캐 등록에는 영향이 없습니다.")]
    [DefaultMemberPermissions(GuildPermission.ManageGuild)]
    public async Task DeleteAltAsync(
        [Summary("부캐롤아이디", "삭제할 부캐의 게임이름#태그")]
        string riotId)
    {
        await DeferAsync(ephemeral: true);

        if (Context.Guild is null)
        {
            await FollowupAsync("이 명령은 AtoZ Discord 서버에서만 사용할 수 있습니다.", ephemeral: true);
            return;
        }

        if (!Context.CanManageMembers())
        {
            await FollowupAsync(
                "❌ 부캐 삭제는 서버 소유자와 서버 관리 권한이 있는 운영자만 사용할 수 있습니다.",
                ephemeral: true);
            return;
        }

        if (!TryParseRiotId(riotId, out var gameName, out var tagLine))
        {
            await FollowupAsync("❌ 부캐 롤아이디를 `게임이름#태그` 형식으로 입력해 주세요.", ephemeral: true);
            return;
        }

        var deleted = await _memberRepository.DeleteAltAsync(Context.Guild.Id, gameName, tagLine);
        await FollowupAsync(
            deleted
                ? $"✅ 부캐 **{EscapeMarkdown(gameName)}#{EscapeMarkdown(tagLine)}** 연결을 삭제했습니다."
                : "❌ 해당 부캐 등록을 찾지 못했습니다.",
            ephemeral: true);
    }

    // TryParseRiotId/CanManageMembers/EscapeMarkdown은 2026-08-20 리팩토링 1단계에서
    // Services/RiotIdParser.cs, PermissionChecker.cs, MarkdownFormatter.cs로 이관됨
    // (ClanStatsModule.cs에 있던 완전히 동일한 복붙 코드와 통합). 위쪽 using static 참고.
}

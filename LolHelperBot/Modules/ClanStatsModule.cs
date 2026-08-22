// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-19
// Reviewer: (박정훈)
// Review: 아재클랜(AtoZ) 자유 랭크 전적을 모아 티어픽/승률순위/조합추천을 보여주는 기능.
// /atoz 전적수집(운영자 전용)으로 등록 멤버들의 매치를 모아 저장하고, 나머지 명령은 저장된 데이터를 조회만 합니다.
// /atoz 리플업로드는 .rofl 내전 리플레이를 자유 랭크와 분리해 저장합니다.

using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Discord;
using Discord.Interactions;
using LolHelperBot.Services;
using static LolHelperBot.Services.ClanConstants;
using static LolHelperBot.Services.MarkdownFormatter;
using static LolHelperBot.Services.PositionOrder;
using static LolHelperBot.Services.RiotIdParser;

namespace LolHelperBot.Modules;

public partial class AtoZModule
{
    public class ClanStatsModule : InteractionModuleBase<SocketInteractionContext>
    {
        // /아재전적(구 함께한전적)은 무조건 5인큐(같은 팀 5명 전원 우리 멤버)만 보여줍니다 — 2026-08-20 개명 시
        // 최소인원 파라미터를 없애면서 상수로 고정. 자유 랭크 수집은 애초에 5명 전원인 경기만
        // 저장하므로 결과는 예전 최소인원 파라미터가 뭐였든 사실상 항상 5명 경기였음.
        private const int AjaeMatchMinTeammates = 5;

        private const long MaxReplayFileSizeBytes = 100 * 1024 * 1024;

        // 리플 파일 다운로드 전용 HttpClient. 소켓 고갈 방지를 위해 앱 생명주기 동안 재사용합니다.
        private static readonly HttpClient ReplayDownloadHttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        // /atoz 전적수집 중복 실행 방지용 가드. InteractionModuleBase는 인터랙션마다 인스턴스가 새로
        // 생성되므로 인스턴스 필드로는 상태를 못 지키고, static + Interlocked로 프로세스 전체에서
        // "지금 수집 중인가"를 관리합니다(0=유휴, 1=진행 중). 다른 조회 명령(/티어픽 등)은 짧은 SQLite
        // 쿼리 하나씩이라 전적수집과 동시에 실행돼도 안전합니다(Microsoft.Data.Sqlite 기본 busy timeout
        // 30초 안에서 알아서 재시도됨) — 진짜 위험한 건 /atoz 전적수집을 실수로 두 번 동시에 돌리는
        // 경우(Riot API 요청 두 배, checked_matches 캐시 경합)라 이것만 막습니다.
        private static int _isCollecting;

        private readonly RiotApiClient _riotApiClient;
        private readonly MemberRepository _memberRepository;
        private readonly MatchRepository _matchRepository;
        private readonly ContributionScoreCalculator _contributionScoreCalculator;
        private readonly MetaTierRepository _metaTierRepository;
        private readonly BanPickRecommendationService _banPickRecommendationService;
        private readonly ChampionTierService _championTierService;

        public ClanStatsModule(
            RiotApiClient riotApiClient,
            MemberRepository memberRepository,
            MatchRepository matchRepository,
            ContributionScoreCalculator contributionScoreCalculator,
            MetaTierRepository metaTierRepository,
            BanPickRecommendationService banPickRecommendationService,
            ChampionTierService championTierService)
        {
            _riotApiClient = riotApiClient;
            _memberRepository = memberRepository;
            _matchRepository = matchRepository;
            _contributionScoreCalculator = contributionScoreCalculator;
            _metaTierRepository = metaTierRepository;
            _banPickRecommendationService = banPickRecommendationService;
            _championTierService = championTierService;
        }

        [SlashCommand("전적수집", "등록된 AtoZ 멤버들의 최근 자유 랭크 전적을 모아 통계 DB에 저장합니다(2026-08-01 이후 경기만).")]
        [DefaultMemberPermissions(GuildPermission.ManageGuild)]
        public async Task CollectMatchesAsync(
            [Summary("최근경기수", "멤버별로 확인할 최근 경기 수 (기본 20, 최대 300). 이 수보다 오래됐어도 2026-08-01 이전 경기는 큰 값을 줘도 저장 안 됨")]
        int recentCount = 20)
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
                    "❌ 전적 수집은 서버 소유자와 서버 관리 권한이 있는 운영자만 사용할 수 있습니다.",
                    ephemeral: true);
                return;
            }

            recentCount = Math.Clamp(recentCount, 1, 300);

            // 이미 다른 /atoz 전적수집이 진행 중이면 겹쳐 돌리지 않고 바로 안내합니다(Riot API 요청이
            // 두 배로 나가고 checked_matches 캐시가 경합할 수 있어서).
            if (Interlocked.CompareExchange(ref _isCollecting, 1, 0) != 0)
            {
                await FollowupAsync(
                    "⏳ 이미 다른 `/atoz 전적수집`이 진행 중입니다. 끝날 때까지 기다렸다가 다시 시도해 주세요.",
                    ephemeral: true);
                return;
            }

            try
            {
                await CollectMatchesCoreAsync(recentCount);
            }
            finally
            {
                Interlocked.Exchange(ref _isCollecting, 0);
            }
        }

        private async Task CollectMatchesCoreAsync(int recentCount)
        {
            var members = await _memberRepository.GetAllByGuildAsync(Context.Guild!.Id);
            if (members.Count == 0)
            {
                await FollowupAsync("등록된 AtoZ 멤버가 없습니다. `/atoz 멤버등록`을 먼저 사용해 주세요.", ephemeral: true);
                return;
            }

            var altAccounts = await _memberRepository.GetAllAltAccountsByGuildAsync(Context.Guild.Id);

            // 부캐로 플레이한 경기도 본캐(owner) 기준으로 합산되도록 puuid -> 본캐 discord_user_id 매핑을 만듭니다.
            var puuidToUserId = new Dictionary<string, ulong>(StringComparer.Ordinal);
            foreach (var member in members)
            {
                puuidToUserId[member.Puuid] = member.DiscordUserId;
            }
            foreach (var alt in altAccounts)
            {
                puuidToUserId[alt.Puuid] = alt.OwnerDiscordUserId;
            }

            // 매치ID를 조회할 계정 목록 (본캐 + 부캐 전부). Label은 오류 메시지 표시용입니다.
            var accountsToScan = members
                .Select(member => (member.Puuid, Label: member.DisplayName))
                .Concat(altAccounts.Select(alt => (alt.Puuid, Label: $"{alt.GameName}#{alt.TagLine} (부캐)")))
                .ToList();

            var newMatchCount = 0;
            var errors = new List<string>();
            var ownerCollisionWarnings = new List<string>();

            foreach (var account in accountsToScan)
            {
                var (idsSuccess, matchIds, idsErrorMessage) = await GetAllMatchIdsAsync(account.Puuid, recentCount, MatchCollectionCutoffUtc);
                if (!idsSuccess)
                {
                    errors.Add($"{account.Label}: {idsErrorMessage}");
                    continue;
                }

                var newIds = await _matchRepository.FilterNewMatchIdsAsync(Context.Guild.Id, matchIds);
                foreach (var matchId in newIds)
                {
                    var matchResult = await _riotApiClient.GetFullMatchAsync(matchId);
                    await Task.Delay(RiotApiDelay);

                    if (!matchResult.IsSuccess || matchResult.Match is null)
                    {
                        errors.Add($"{matchId}: {matchResult.Message}");
                        continue;
                    }

                    var match = matchResult.Match;

                    // 2026-08-01 이전 경기는 수집 대상에서 제외합니다(사용자 요청 — 그 이전 데이터는
                    // 정합성 이슈가 있었던 적이 있어 신뢰 안 함, ClanConstants.MatchCollectionCutoffUtc 참고).
                    // "확인 완료(저장 안 됨)"로 캐싱해서 다음 수집 때 이 매치를 다시 조회하지 않게 합니다 —
                    // 날짜는 안 바뀌므로 한 번 걸러진 매치는 영원히 걸러집니다.
                    if (match.GameCreatedAt < MatchCollectionCutoffUtc)
                    {
                        await _matchRepository.MarkMatchCheckedAsync(Context.Guild.Id, match.MatchId, match.QueueId, allClanSaved: false);
                        continue;
                    }

                    // 팀(team_id)별로 나눠서, 5명 전원이 본캐/부캐 포함 우리 멤버로 확인되는 팀만 저장합니다.
                    // (매치메이킹으로 섞인 랜덤 팀원까지 통계에 끼는 걸 막기 위함 — "우리끼리 5명" 게임만 수집)
                    var qualifyingTeams = match.Participants
                        .GroupBy(participant => participant.TeamId)
                        .Where(team => team.Count() == 5 && team.All(p => puuidToUserId.ContainsKey(p.Puuid)));

                    var savedAny = false;
                    foreach (var team in qualifyingTeams)
                    {
                        // 부캐를 여러 명이 돌려쓰는 경우, 그 부캐의 기본 소유자와 실제 플레이어가 같은 경기에
                        // 동시에 나오면(본캐+부캐가 같은 팀에 동시 존재) discord_user_id가 겹쳐서 한쪽 기록이
                        // 저장 시 조용히 씹힙니다. 충돌난 참가자들을 기록해두고, 운영자가 나중에
                        // /atoz 부캐충돌목록·/atoz 부캐충돌해결로 실제 플레이어를 지정할 수 있게 합니다.
                        var ownerGroups = team.GroupBy(p => puuidToUserId[p.Puuid]).Where(g => g.Count() > 1).ToList();
                        if (ownerGroups.Count > 0)
                        {
                            var conflictCount = ownerGroups.Sum(g => g.Count() - 1);
                            ownerCollisionWarnings.Add(
                                $"{match.MatchId}: 부캐 소유자 충돌로 {conflictCount}명 데이터가 누락될 수 있음 " +
                                "(같은 사람으로 등록된 계정 2개가 한 팀에 동시에 있었음 — 부캐를 다른 사람이 썼을 가능성. `/atoz 부캐충돌목록` 참고)");

                            foreach (var ownerGroup in ownerGroups)
                            {
                                // 그룹 안에서 가장 먼저 저장 루프를 도는 참가자가 discord_user_id PK 경쟁에서 "이겨서"
                                // 이미 정상 저장됩니다 — 그 사람은 alreadySaved=true로 기록해서 목록에서 빠지게 하고,
                                // 나머지(실제로 누락된 사람들)만 해결이 필요한 상태로 남겨둡니다.
                                var groupParticipants = ownerGroup.ToList();
                                for (var i = 0; i < groupParticipants.Count; i++)
                                {
                                    var participant = groupParticipants[i];
                                    if (participant.RiotGameName is null || participant.RiotTagLine is null)
                                    {
                                        continue; // 롤아이디 정보가 없으면 나중에 지정할 방법이 없어 기록을 건너뜁니다.
                                    }

                                    await _matchRepository.SaveOwnerConflictAsync(
                                        Context.Guild.Id,
                                        match.MatchId,
                                        team.Key,
                                        participant.Puuid,
                                        participant.RiotGameName,
                                        participant.RiotTagLine,
                                        participant.ChampionName,
                                        participant.TeamPosition,
                                        ownerGroup.Key,
                                        alreadySaved: i == 0);
                                }
                            }
                        }

                        foreach (var participant in team)
                        {
                            // 밴픽추천의 "상대했을 때 승률 안좋은 챔프" + 기여도 점수(맞라인 상대 비교)용 —
                            // 같은 라인 상대(맞라인)를 찾아서 챔피언명과 상대 지표를 함께 저장합니다.
                            var opponent = match.Participants
                                .FirstOrDefault(p => p.TeamId != participant.TeamId && p.TeamPosition == participant.TeamPosition);

                            await _matchRepository.SaveParticipationAsync(
                                Context.Guild.Id,
                                match.MatchId,
                                match.QueueId,
                                match.GameDurationSeconds,
                                match.GameCreatedAt,
                                puuidToUserId[participant.Puuid],
                                participant.Puuid,
                                participant.TeamId,
                                participant.ChampionName,
                                participant.TeamPosition,
                                participant.Win,
                                participant.Kills,
                                participant.Deaths,
                                participant.Assists,
                                participant.CreepScore,
                                opponent?.ChampionName,
                                BuildParticipationStats(participant, opponent));
                        }

                        savedAny = true;
                    }

                    // 팀이 5명 전원 우리 멤버가 아니라 저장 안 된 매치도 "확인함"으로 표시해서,
                    // 다음 수집 때 Riot API를 또 호출하지 않도록 합니다.
                    await _matchRepository.MarkMatchCheckedAsync(Context.Guild.Id, match.MatchId, match.QueueId, savedAny);

                    if (savedAny)
                    {
                        newMatchCount++;
                    }
                }
            }

            var summary = $"✅ 전적 수집 완료 — 멤버 {members.Count}명(부캐 {altAccounts.Count}개 포함) 확인, " +
                $"5명 전원 우리 멤버였던 매치 {newMatchCount}건 저장 (일부만 우리 멤버인 매치는 저장하지 않음).";
            if (errors.Count > 0)
            {
                var errorPreview = string.Join("\n", errors.Take(5));
                summary += $"\n⚠️ 오류 {errors.Count}건 (최대 5건 표시):\n{errorPreview}";
            }
            if (ownerCollisionWarnings.Count > 0)
            {
                var warningPreview = string.Join("\n", ownerCollisionWarnings.Take(5));
                summary += $"\n🔀 부캐 소유자 충돌 {ownerCollisionWarnings.Count}건 (최대 5건 표시, 데이터 누락 가능):\n{warningPreview}";
            }

            await FollowupAsync(summary, ephemeral: true);
        }

        [SlashCommand("부캐충돌목록", "부캐를 여러 명이 돌려쓰다가 데이터가 누락된 매치 목록을 보여줍니다.")]
        [DefaultMemberPermissions(GuildPermission.ManageGuild)]
        public async Task ShowOwnerConflictsAsync()
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
                    "❌ 이 명령은 서버 소유자와 서버 관리 권한이 있는 운영자만 사용할 수 있습니다.",
                    ephemeral: true);
                return;
            }

            var conflicts = await _matchRepository.GetUnresolvedConflictsAsync(Context.Guild.Id);
            if (conflicts.Count == 0)
            {
                await FollowupAsync("✅ 미해결 부캐 소유자 충돌이 없습니다.", ephemeral: true);
                return;
            }

            var nameByUserId = await GetDisplayNameLookupAsync(Context.Guild.Id);

            var lines = conflicts
                .GroupBy(conflict => (conflict.MatchId, conflict.TeamId))
                .Select(group =>
                {
                    var defaultOwnerName = nameByUserId.GetValueOrDefault(group.First().DefaultOwnerDiscordUserId, "알 수 없음");
                    var participantList = string.Join(" / ", group.Select(conflict =>
                        $"`{conflict.RiotGameName}#{conflict.RiotTagLine}` {GetKoreanPosition(conflict.TeamPosition)} {EscapeMarkdown(conflict.ChampionName)}"));
                    return $"**{group.Key.MatchId}** (기본 소유자: {EscapeMarkdown(defaultOwnerName)})\n　{participantList}";
                });

            var embed = new EmbedBuilder()
                .WithTitle("🔀 미해결 부캐 소유자 충돌 목록")
                .WithColor(Color.Orange)
                .WithDescription(string.Join("\n", lines))
                .WithFooter("/atoz 부캐충돌해결 매치아이디: 롤아이디: 멤버: 로 각 계정의 실제 플레이어를 지정해 주세요.")
                .Build();

            await FollowupAsync(embed: embed, ephemeral: true);
        }

        [SlashCommand("부캐충돌해결", "부캐 소유자 충돌 매치에서 특정 계정의 기록을 실제 멤버로 지정해 저장합니다.")]
        [DefaultMemberPermissions(GuildPermission.ManageGuild)]
        public async Task ResolveOwnerConflictAsync(
            [Summary("매치아이디", "/atoz 부캐충돌목록에 나온 매치 ID (예: KR_8286962111)")]
        string matchId,
            [Summary("롤아이디", "충돌 목록에 나온 계정 중 실제로 배정할 대상의 게임이름#태그")]
        string riotId,
            [Summary("멤버", "이 경기 기록을 귀속시킬 실제 디스코드 멤버")]
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
                    "❌ 이 명령은 서버 소유자와 서버 관리 권한이 있는 운영자만 사용할 수 있습니다.",
                    ephemeral: true);
                return;
            }

            if (!TryParseRiotId(riotId, out var gameName, out var tagLine))
            {
                await FollowupAsync("❌ 롤아이디를 `게임이름#태그` 형식으로 입력해 주세요.", ephemeral: true);
                return;
            }

            var conflict = await _matchRepository.FindUnresolvedConflictAsync(Context.Guild.Id, matchId, gameName, tagLine);
            if (conflict is null)
            {
                await FollowupAsync(
                    "❌ 해당 매치·롤아이디의 미해결 충돌 기록을 찾지 못했습니다. `/atoz 부캐충돌목록`으로 정확한 값을 확인해 주세요.",
                    ephemeral: true);
                return;
            }

            var matchResult = await _riotApiClient.GetFullMatchAsync(matchId);
            if (!matchResult.IsSuccess || matchResult.Match is null)
            {
                await FollowupAsync($"❌ 매치 재조회 실패: {matchResult.Message}", ephemeral: true);
                return;
            }

            var participant = matchResult.Match.Participants.FirstOrDefault(p => p.Puuid == conflict.Puuid);
            if (participant is null)
            {
                await FollowupAsync("❌ 매치에서 해당 참가자를 찾지 못했습니다.", ephemeral: true);
                return;
            }

            // 이 puuid가 충돌에서 "이겨서" 기본 소유자 이름으로 이미 저장돼 있었다면, 재배정을 위해 그 행을 지웁니다.
            await _matchRepository.DeleteParticipationIfMatchesAsync(
                Context.Guild.Id, matchId, conflict.DefaultOwnerDiscordUserId, conflict.Puuid);

            var opponent = matchResult.Match.Participants
                .FirstOrDefault(p => p.TeamId != participant.TeamId && p.TeamPosition == participant.TeamPosition);

            await _matchRepository.SaveParticipationAsync(
                Context.Guild.Id,
                matchId,
                matchResult.Match.QueueId,
                matchResult.Match.GameDurationSeconds,
                matchResult.Match.GameCreatedAt,
                member.Id,
                participant.Puuid,
                participant.TeamId,
                participant.ChampionName,
                participant.TeamPosition,
                participant.Win,
                participant.Kills,
                participant.Deaths,
                participant.Assists,
                participant.CreepScore,
                opponent?.ChampionName,
                BuildParticipationStats(participant, opponent));

            await _matchRepository.MarkConflictResolvedAsync(Context.Guild.Id, matchId, conflict.Puuid);

            var displayName = member is Discord.WebSocket.SocketGuildUser guildUser ? guildUser.DisplayName : member.Username;
            await FollowupAsync(
                $"✅ `{matchId}`의 `{gameName}#{tagLine}` 기록을 **{EscapeMarkdown(displayName)}**로 저장했습니다.",
                ephemeral: true);
        }

        // "부캐 충돌"(같은 경기에 본인+빌린 사람이 동시에 있어서 저장 자체가 씹힌 경우, 위 두 명령)과 달리,
        // 저장은 정상적으로 됐지만 그 경기만 다른 사람이 계정을 빌려서 한 경우를 위한 명령입니다.
        // 2026-08-22, 사용자 요청: "부캐충돌 말고 그 판만 빌려썼을 때 바꿀 수 있는 운영자용 기능".
        [SlashCommand("전적재배정", "부캐를 남이 잠깐 빌려서 한 경기의 기록 소유자를 실제 플레이어로 바꿉니다 (부캐 충돌과는 다른 경우입니다).")]
        [DefaultMemberPermissions(GuildPermission.ManageGuild)]
        public async Task ReassignParticipationAsync(
            [Summary("매치아이디", "기록을 옮길 매치 ID (예: KR_8348359834) — /atoz 아재전적 등에서 확인 가능")]
        string matchId,
            [Summary("기존멤버", "지금 이 경기 기록이 잘못 붙어 있는 멤버(빌려준 부캐의 등록 주인)")]
        IUser fromMember,
            [Summary("새멤버", "실제로 그 계정을 빌려서 이 경기를 플레이한 멤버")]
        IUser toMember)
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
                    "❌ 이 명령은 서버 소유자와 서버 관리 권한이 있는 운영자만 사용할 수 있습니다.",
                    ephemeral: true);
                return;
            }

            if (fromMember.Id == toMember.Id)
            {
                await FollowupAsync("❌ 기존멤버와 새멤버가 같습니다.", ephemeral: true);
                return;
            }

            var outcome = await _matchRepository.ReassignParticipationOwnerAsync(
                Context.Guild.Id, matchId, fromMember.Id, toMember.Id);

            var fromName = fromMember is Discord.WebSocket.SocketGuildUser fromGuildUser ? fromGuildUser.DisplayName : fromMember.Username;
            var toName = toMember is Discord.WebSocket.SocketGuildUser toGuildUser ? toGuildUser.DisplayName : toMember.Username;

            if (outcome.Status == ReassignParticipationStatus.SourceNotFound)
            {
                await FollowupAsync(
                    $"❌ `{matchId}`에 **{EscapeMarkdown(fromName)}** 이름으로 저장된 기록을 찾지 못했습니다. " +
                        "매치 ID와 기존멤버를 다시 확인해 주세요.",
                    ephemeral: true);
                return;
            }

            if (outcome.Status == ReassignParticipationStatus.TargetAlreadyHasRecord)
            {
                await FollowupAsync(
                    $"❌ **{EscapeMarkdown(toName)}**은(는) 이미 `{matchId}` 경기 기록이 있습니다 " +
                        "(같은 경기에 본인 계정 + 빌린 부캐가 동시에 있었던 '부캐 충돌'로 보입니다). " +
                        "이 경우는 `/atoz 부캐충돌목록`·`/atoz 부캐충돌해결`을 사용해 주세요.",
                    ephemeral: true);
                return;
            }

            var resultMark = outcome.Win ? "승" : "패";
            await FollowupAsync(
                $"✅ `{matchId}` {GetPositionIcon(outcome.TeamPosition!)} {GetKoreanPosition(outcome.TeamPosition!)} " +
                    $"**{EscapeMarkdown(outcome.ChampionName!)}** {outcome.Kills}/{outcome.Deaths}/{outcome.Assists} ({resultMark}) 기록을 " +
                    $"**{EscapeMarkdown(fromName)}** → **{EscapeMarkdown(toName)}**로 재배정했습니다.",
                ephemeral: true);
        }

        /// <summary>
        /// Riot API는 한 번에 최대 100경기까지만 주기 때문에, totalWanted가 100을 넘으면 start를 옮겨가며 여러 번 호출합니다.
        /// 오래전에 등록된 클랜원이 있는 매치는 "가장 최근 N경기"에 안 들어갈 수 있어서, 깊게 훑어야 할 때(딥 백필) 필요합니다.
        /// startTime을 주면 그 이전 매치는 Riot 서버가 애초에 목록에서 빼고 응답하므로(예: 2026-08-01
        /// 컷오프), totalWanted를 크게 잡아도 오래된 경기의 상세 조회(API 호출)를 낭비하지 않습니다.
        /// </summary>
        private async Task<(bool IsSuccess, List<string> MatchIds, string? ErrorMessage)> GetAllMatchIdsAsync(
            string puuid,
            int totalWanted,
            DateTimeOffset? startTime = null)
        {
            var all = new List<string>();
            var start = 0;

            while (all.Count < totalWanted)
            {
                var pageSize = Math.Min(100, totalWanted - all.Count);
                var idsResult = await _riotApiClient.GetMatchIdsAsync(puuid, FlexQueueId, start: start, count: pageSize, startTime: startTime);
                await Task.Delay(RiotApiDelay);

                if (!idsResult.IsSuccess || idsResult.MatchIds is null)
                {
                    return (false, all, idsResult.Message);
                }

                if (idsResult.MatchIds.Count == 0)
                {
                    break; // 더 이상 과거 경기가 없음
                }

                all.AddRange(idsResult.MatchIds);
                start += idsResult.MatchIds.Count;

                if (idsResult.MatchIds.Count < pageSize)
                {
                    break; // 마지막 페이지
                }
            }

            return (true, all, null);
        }

        [SlashCommand("전적등록후보", "기준 계정의 최근 자유 랭크 경기에서 자주 함께한 사람(아직 미등록)을 찾아 등록 후보로 보여줍니다.")]
        [DefaultMemberPermissions(GuildPermission.ManageGuild)]
        public async Task ShowRegistrationCandidatesAsync(
            [Summary("기준아이디", "게임이름#태그 형식의 기준 Riot ID (보통 이미 등록된 멤버 계정을 추천)")]
        string referenceRiotId,
            [Summary("최근경기수", "기준 계정의 최근 자유 랭크 경기 수 (기본 50, 최대 100)")]
        int recentCount = 50)
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
                    "❌ 이 명령은 서버 소유자와 서버 관리 권한이 있는 운영자만 사용할 수 있습니다.",
                    ephemeral: true);
                return;
            }

            recentCount = Math.Clamp(recentCount, 1, 100);

            var account = await _riotApiClient.FindLeagueAccountAsync(referenceRiotId);
            if (!account.IsSuccess || account.Puuid is null || account.GameName is null || account.TagLine is null)
            {
                await FollowupAsync($"❌ 기준 계정 조회 실패: {account.Message}", ephemeral: true);
                return;
            }

            var idsResult = await _riotApiClient.GetMatchIdsAsync(account.Puuid, FlexQueueId, count: recentCount);
            await Task.Delay(RiotApiDelay);

            if (!idsResult.IsSuccess || idsResult.MatchIds is null)
            {
                await FollowupAsync($"❌ 매치 목록 조회 실패: {idsResult.Message}", ephemeral: true);
                return;
            }

            // 이미 본캐/부캐로 등록된 puuid는 후보에서 제외합니다.
            var members = await _memberRepository.GetAllByGuildAsync(Context.Guild.Id);
            var altAccounts = await _memberRepository.GetAllAltAccountsByGuildAsync(Context.Guild.Id);
            var knownPuuids = new HashSet<string>(
                members.Select(m => m.Puuid).Concat(altAccounts.Select(a => a.Puuid)),
                StringComparer.Ordinal);

            var teammateCounts = new Dictionary<string, (string GameName, string TagLine, int Count)>(StringComparer.Ordinal);
            var checkedMatchCount = 0;
            var errorCount = 0;

            foreach (var matchId in idsResult.MatchIds)
            {
                var matchResult = await _riotApiClient.GetFullMatchAsync(matchId);
                await Task.Delay(RiotApiDelay);

                if (!matchResult.IsSuccess || matchResult.Match is null)
                {
                    errorCount++;
                    continue;
                }

                checkedMatchCount++;

                var referenceParticipant = matchResult.Match.Participants
                    .FirstOrDefault(p => p.Puuid == account.Puuid);
                if (referenceParticipant is null)
                {
                    continue;
                }

                var teammates = matchResult.Match.Participants.Where(p =>
                    p.TeamId == referenceParticipant.TeamId &&
                    p.Puuid != account.Puuid &&
                    !knownPuuids.Contains(p.Puuid) &&
                    !string.IsNullOrWhiteSpace(p.RiotGameName) &&
                    !string.IsNullOrWhiteSpace(p.RiotTagLine));

                foreach (var teammate in teammates)
                {
                    if (teammateCounts.TryGetValue(teammate.Puuid, out var existing))
                    {
                        teammateCounts[teammate.Puuid] = (existing.GameName, existing.TagLine, existing.Count + 1);
                    }
                    else
                    {
                        teammateCounts[teammate.Puuid] = (teammate.RiotGameName!, teammate.RiotTagLine!, 1);
                    }
                }
            }

            var candidateLines = teammateCounts
                .OrderByDescending(kv => kv.Value.Count)
                .Take(15)
                .Select((kv, index) =>
                    $"{index + 1}. **{EscapeMarkdown(kv.Value.GameName)}#{EscapeMarkdown(kv.Value.TagLine)}** — 같은 팀 {kv.Value.Count}번")
                .ToList();

            var embedBuilder = new EmbedBuilder()
                .WithTitle($"{account.GameName}#{account.TagLine} 기준 등록 후보")
                .WithColor(Color.Gold)
                .WithDescription(candidateLines.Count > 0
                    ? string.Join("\n", candidateLines)
                    : "새로운 후보를 찾지 못했습니다 (전부 이미 등록돼 있거나, 팀원 Riot ID 정보가 없는 오래된 경기였을 수 있어요).")
                .AddField("확인한 경기", $"{checkedMatchCount}판 확인 (요청 {recentCount}판 중 {errorCount}판 오류)")
                .WithFooter("여기 나온 Riot ID로 어떤 디스코드 멤버인지 확인한 다음 /atoz 멤버등록 또는 /atoz 부캐등록으로 등록해 주세요.");

            await FollowupAsync(embed: embedBuilder.Build(), ephemeral: true);
        }

        [SlashCommand("리플업로드", ".rofl 내전 리플레이를 미리 보거나 내전 데이터로 저장합니다 (기본은 미리보기만).")]
        [DefaultMemberPermissions(GuildPermission.ManageGuild)]
        public async Task UploadReplayAsync(
            [Summary("리플파일", ".rofl 리플레이 파일")]
        IAttachment replayFile,
            [Summary("저장", "true로 지정하면 자유 랭크와 분리된 내전 데이터로 저장합니다 (기본 false)")]
        bool save = false,
            [Summary("재지정롤아이디", "여러 명이 돌려쓰는 부캐처럼, 이 리플에서만 특정 계정(게임이름 또는 게임이름#태그)의 기록을 다른 멤버로 재지정하고 싶을 때 입력")]
        string reassignRiotId = "",
            [Summary("재지정멤버", "재지정롤아이디의 기록을 귀속시킬 AtoZ 멤버 (재지정롤아이디와 함께 입력해야 동작함)")]
        IUser? reassignMember = null)
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
                    "❌ 내전 리플 등록은 서버 소유자와 서버 관리 권한이 있는 운영자만 사용할 수 있습니다.",
                    ephemeral: true);
                return;
            }

            if (!replayFile.Filename.EndsWith(".rofl", StringComparison.OrdinalIgnoreCase))
            {
                await FollowupAsync("❌ `.rofl` 리플레이 파일만 업로드할 수 있습니다.", ephemeral: true);
                return;
            }

            if (replayFile.Size > MaxReplayFileSizeBytes)
            {
                await FollowupAsync("❌ 파일이 너무 큽니다 (100MB 초과).", ephemeral: true);
                return;
            }

            byte[] data;
            try
            {
                data = await ReplayDownloadHttpClient.GetByteArrayAsync(replayFile.Url);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[리플 다운로드 오류] {ex}");
                await FollowupAsync("❌ 리플 파일을 다운로드하지 못했습니다.", ephemeral: true);
                return;
            }

            var parseResult = RoflReplayParser.Parse(data);
            if (!parseResult.IsSuccess || parseResult.Match is null)
            {
                await FollowupAsync(
                    $"❌ 리플 분석 실패: {parseResult.Message}\n" +
                    "-# .rofl은 라이엇 비공식 포맷이라 클라이언트 버전에 따라 구조가 다를 수 있어요. 실패하면 알려주세요, 파싱 로직을 조정해볼게요.",
                    ephemeral: true);
                return;
            }

            var match = parseResult.Match;

            // 리플 메타데이터 자체에는 매치ID가 없지만, 파일명을 안 바꿨다면 클라이언트 기본 파일명("지역-게임ID.rofl")에서
            // 실제 매치ID를 추정할 수 있습니다. 실패하면 경기 길이+참가자 기록 기반 해시 ID로 대체합니다.
            var filenameMatchId = TryExtractMatchIdFromFilename(replayFile.Filename);
            var effectiveMatchId = filenameMatchId ?? match.SyntheticMatchId;

            var members = await _memberRepository.GetAllByGuildAsync(Context.Guild.Id);
            var altAccounts = await _memberRepository.GetAllAltAccountsByGuildAsync(Context.Guild.Id);

            var puuidToUserId = new Dictionary<string, ulong>(StringComparer.Ordinal);
            foreach (var m in members) puuidToUserId[m.Puuid] = m.DiscordUserId;
            foreach (var alt in altAccounts) puuidToUserId[alt.Puuid] = alt.OwnerDiscordUserId;

            // 구버전 리플처럼 PUUID가 없는 경우를 대비해 게임이름#태그로도 매칭을 시도합니다.
            var riotIdToUserId = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in members) riotIdToUserId[$"{m.GameName}#{m.TagLine}"] = m.DiscordUserId;
            foreach (var alt in altAccounts) riotIdToUserId[$"{alt.GameName}#{alt.TagLine}"] = alt.OwnerDiscordUserId;

            var nameByUserId = members.ToDictionary(m => m.DiscordUserId, m => m.DisplayName);

            var resolvedParticipants = match.Participants.Select(participant =>
            {
                ulong? discordUserId = null;
                if (participant.Puuid is not null && puuidToUserId.TryGetValue(participant.Puuid, out var byPuuid))
                {
                    discordUserId = byPuuid;
                }
                else if (participant.RiotGameName is not null && participant.RiotTagLine is not null &&
                    riotIdToUserId.TryGetValue($"{participant.RiotGameName}#{participant.RiotTagLine}", out var byRiotId))
                {
                    discordUserId = byRiotId;
                }

                return (Participant: participant, DiscordUserId: discordUserId);
            }).ToList();

            // 부캐를 여러 명이 돌려쓰는 경우처럼, 이 리플 한 건에 한해서만 특정 계정의 기록을 다른 멤버로 재지정합니다.
            // (member_alt_accounts에 등록된 기본 소유자와는 별개로, 이번 업로드에서만 적용됩니다.)
            string? reassignmentNote = null;
            if (!string.IsNullOrWhiteSpace(reassignRiotId) && reassignMember is not null)
            {
                var parts = reassignRiotId.Split('#', 2);
                var reassignGameName = parts[0].Trim();
                var reassignTagLine = parts.Length > 1 ? parts[1].Trim() : null;

                var targetIndex = resolvedParticipants.FindIndex(row =>
                    row.Participant.RiotGameName is not null &&
                    row.Participant.RiotGameName.Equals(reassignGameName, StringComparison.OrdinalIgnoreCase) &&
                    (reassignTagLine is null ||
                        (row.Participant.RiotTagLine?.Equals(reassignTagLine, StringComparison.OrdinalIgnoreCase) ?? false)));

                if (targetIndex >= 0)
                {
                    resolvedParticipants[targetIndex] = (resolvedParticipants[targetIndex].Participant, reassignMember.Id);
                    var reassignLabel = reassignMember is Discord.WebSocket.SocketGuildUser reassignGuildUser
                        ? reassignGuildUser.DisplayName
                        : reassignMember.Username;
                    nameByUserId[reassignMember.Id] = reassignLabel;
                    reassignmentNote = $"🔁 `{reassignRiotId}` 기록을 **{EscapeMarkdown(reassignLabel)}**(으)로 재지정했습니다.";
                }
                else
                {
                    reassignmentNote = $"⚠️ 재지정 대상 계정(`{reassignRiotId}`)을 리플 참가자 중에서 찾지 못했습니다. " +
                        "리플에 기록된 게임이름과 정확히 일치하는지 확인해 주세요.";
                }
            }

            var matchedCount = resolvedParticipants.Count(row => row.DiscordUserId is not null);

            var preview = new StringBuilder();
            foreach (var (participant, discordUserId) in resolvedParticipants)
            {
                var result = participant.Win ? "🟢 승" : "🔴 패";
                var label = discordUserId is not null
                    ? $" ← {EscapeMarkdown(nameByUserId.GetValueOrDefault(discordUserId.Value, "AtoZ 멤버"))}"
                    : "";
                preview.AppendLine(
                    $"{result} {GetKoreanPosition(participant.TeamPosition)} **{EscapeMarkdown(participant.ChampionName)}** " +
                    $"{participant.Kills}/{participant.Deaths}/{participant.Assists}{label}");
            }

            // 5명 전원이 본캐/부캐 포함 우리 멤버로 확인되는 팀만 저장합니다 (랜덤 팀원 섞인 매치 통계 오염 방지).
            var qualifyingTeamIds = resolvedParticipants
                .GroupBy(row => row.Participant.TeamId)
                .Where(team => team.Count() == 5 && team.All(row => row.DiscordUserId is not null))
                .Select(team => team.Key)
                .ToHashSet();

            if (save)
            {
                foreach (var (participant, discordUserId) in resolvedParticipants.Where(
                    row => qualifyingTeamIds.Contains(row.Participant.TeamId)))
                {
                    var opponentChampionName = match.Participants
                        .FirstOrDefault(p => p.TeamId != participant.TeamId && p.TeamPosition == participant.TeamPosition)
                        ?.ChampionName;

                    await _matchRepository.SaveParticipationAsync(
                        Context.Guild.Id,
                        effectiveMatchId,
                        InternalGameQueueId,
                        match.GameDurationSeconds,
                        DateTimeOffset.UtcNow,
                        discordUserId!.Value,
                        participant.Puuid ?? $"rofl-unknown-{discordUserId}",
                        participant.TeamId,
                        participant.ChampionName,
                        participant.TeamPosition,
                        participant.Win,
                        participant.Kills,
                        participant.Deaths,
                        participant.Assists,
                        participant.CreepScore,
                        opponentChampionName);
                }
            }

            var qualifyingTeamCount = qualifyingTeamIds.Count;
            var savedThisRun = save && qualifyingTeamCount > 0;

            var embedBuilder = new EmbedBuilder()
                .WithTitle(savedThisRun ? "✅ 내전 리플 데이터 저장 완료" : "🔍 내전 리플 데이터 미리보기 (저장 안 함)")
                .WithColor(savedThisRun ? Color.Green : Color.LightGrey)
                .WithDescription(preview.ToString())
                .AddField("AtoZ 멤버 매칭", matchedCount > 0
                    ? $"{matchedCount}명 매칭됨"
                    : "매칭된 AtoZ 멤버가 없습니다 (등록된 계정과 puuid/롤아이디가 일치하지 않음).")
                .AddField("저장 대상 (5명 전원 우리 멤버인 팀만)", qualifyingTeamCount > 0
                    ? $"{qualifyingTeamCount}개 팀 저장 가능" + (savedThisRun ? " → 저장함" : " (저장하려면 `저장:true`)")
                    : "5명 전원이 우리 멤버로 확인된 팀이 없어 저장되지 않습니다 (매칭 안 된 인원 확인 또는 `재지정` 옵션 사용).")
                .AddField("매치ID", filenameMatchId is not null
                    ? $"`{filenameMatchId}` (파일명 기반 추정 — 실제 Riot 매치ID와 다를 수 있음)"
                    : $"`{effectiveMatchId}` (파일명에서 추출 실패 — 리플 내용 기반 대체 ID 사용, 파일명을 바꾸지 않았는지 확인해 주세요)")
                .WithFooter(savedThisRun
                    ? "자유 랭크(queue 440)와 분리된 내전 데이터로 저장했습니다."
                    : "실제로 저장하려면 `저장:true` 옵션과 함께 다시 실행하세요 (운영자만 가능).");

            if (reassignmentNote is not null)
            {
                embedBuilder.AddField("재지정", reassignmentNote);
            }

            var embed = embedBuilder.Build();

            await FollowupAsync(embed: embed, ephemeral: true);
        }

        // 리팩토링 2단계(2026-08-20): 계산(쿼리·정렬·필터, N+1이었던 플레이어 지분 조회 포함)은
        // ChampionTierService로 옮기고, 여기는 "서비스 호출 → Embed로 그리기"만 담당합니다.
        [SlashCommand("티어픽", "우리 클랜 전적 데이터 기준 라인별 챔피언 티어를 보여줍니다.", true)]
        public async Task ShowChampionTierAsync(
            [Summary("라인", "탑/정글/미드/원딜/서폿 중 선택. 생략하면 전체 라인")]
        [Choice("탑", "TOP")]
        [Choice("정글", "JUNGLE")]
        [Choice("미드", "MIDDLE")]
        [Choice("원딜", "BOTTOM")]
        [Choice("서폿", "UTILITY")]
        string position = "")
        {
            await DeferAsync();

            if (Context.Guild is null)
            {
                await FollowupAsync("이 명령은 AtoZ Discord 서버에서만 사용할 수 있습니다.");
                return;
            }

            var positionFilter = string.IsNullOrEmpty(position) ? null : position;
            var tierResult = await _championTierService.BuildAsync(Context.Guild.Id, positionFilter);
            if (tierResult is null)
            {
                await FollowupAsync("아직 수집된 자유 랭크 전적이 없습니다. 운영자가 `/atoz 전적수집`을 먼저 실행해야 해요.");
                return;
            }

            var nameByUserId = await GetDisplayNameLookupAsync(Context.Guild.Id);

            var embed = new EmbedBuilder()
                .WithTitle(positionFilter is null ? "AtoZ 라인별 챔피언 티어" : $"AtoZ {GetKoreanPosition(positionFilter)} 챔피언 티어")
                .WithColor(Color.Gold)
                .WithFooter($"자유 랭크 · 최소 {MinSampleSize}판 이상 기준 (표본 부족 시 전체 표시)");

            foreach (var line in tierResult.Lines)
            {
                var lineTexts = line.TopChampions.Select((entry, index) =>
                {
                    var winRate = Math.Round(entry.Wins * 100.0 / entry.Games);
                    var rank = index == 0 ? "👑" : $"{index + 1}.";
                    var text = $"{rank} **{EscapeMarkdown(entry.ChampionName)}** — {entry.Games}판 {entry.Wins}승 · 승률 {winRate}%";

                    var shareText = FormatPlayerShares(entry.Players, nameByUserId);
                    if (shareText.Length > 0)
                    {
                        text += $" ({shareText})";
                    }

                    return text;
                });

                embed.AddField(GetKoreanPosition(line.Position), string.Join("\n", lineTexts));
            }

            var worstLines = tierResult.WorstOverall.Select((entry, index) =>
            {
                var winRate = Math.Round(entry.Wins * 100.0 / entry.Games);
                var rank = index == 0 ? "💀" : $"{index + 1}.";
                var text = $"{rank} **{EscapeMarkdown(entry.ChampionName)}** — {entry.Games}판 {entry.Wins}승 · 승률 {winRate}%";

                var shareText = FormatPlayerShares(entry.Players, nameByUserId);
                if (shareText.Length > 0)
                {
                    text += $" ({shareText})";
                }

                return text;
            }).ToList();

            var worstOverallText = worstLines.Count > 0
                ? string.Join("\n", worstLines)
                : "승률 50% 미만인 챔피언이 없습니다 👍";

            embed.AddField(
                $"전체 워스트 챔피언 TOP 5 (라인 무관, 승률 50% 미만, 최소 {MinSampleSize}판 이상)",
                worstOverallText);

            await FollowupAsync(embed: embed.Build());
        }

        // /밴픽추천 2단계(2026-08-20) — 라인별로 픽 3개 + 밴 3개(기준별 1개씩)를 보여주는 구조로 개편.
        // 밴 추천 3개는 서로 다른 근거를 하나씩 씁니다: (1) 맞상대 승률 낮은 챔프(클랜 데이터),
        // (2) 메타 티어 높은 챔프(op.gg 수동 스냅샷 — MetaTierRepository, 없으면 건너뜀),
        // (3) 우리 라인 베스트픽들이 유독 승률이 안 나왔던 상대(클랜 데이터, "우리 AZ 티어픽 카운터").
        // 같은 챔피언이 중복 추천되지 않도록 앞에서 뽑힌 챔피언은 다음 기준에서 제외합니다.
        //
        // 리팩토링 2단계(2026-08-20): 실제 계산(쿼리·필터·중복 제거)은 BanPickRecommendationService로
        // 옮기고, 여기는 "서비스 호출 → Embed로 그리기"만 담당합니다. 서비스는 Discord 타입을 모르는
        // 순수 데이터(BanPickRecommendation)를 돌려주므로, 나중에 필요하면 Embed 없이도 재사용/테스트 가능.
        [SlashCommand("밴픽추천", "우리 클랜 데이터 + 일반 메타(op.gg 수동 스냅샷) 기준으로 라인별 픽/밴 추천을 보여줍니다.", true)]
        public async Task ShowBanPickRecommendationAsync(
            [Summary("라인", "탑/정글/미드/원딜/서폿 중 선택. 생략하면 5라인 전체")]
        [Choice("탑", "TOP")]
        [Choice("정글", "JUNGLE")]
        [Choice("미드", "MIDDLE")]
        [Choice("원딜", "BOTTOM")]
        [Choice("서폿", "UTILITY")]
        string position = "")
        {
            await DeferAsync();

            if (Context.Guild is null)
            {
                await FollowupAsync("이 명령은 AtoZ Discord 서버에서만 사용할 수 있습니다.");
                return;
            }

            var positionFilter = string.IsNullOrEmpty(position) ? null : position;
            var recommendation = await _banPickRecommendationService.BuildAsync(Context.Guild.Id, positionFilter);
            if (recommendation is null)
            {
                await FollowupAsync("아직 수집된 자유 랭크 전적이 없습니다. 운영자가 `/atoz 전적수집`을 먼저 실행해야 해요.");
                return;
            }

            var nameByUserId = await GetDisplayNameLookupAsync(Context.Guild.Id);
            var embeds = new List<Embed>();

            foreach (var line in recommendation.Lines)
            {
                var pickText = line.HasData && line.Picks.Count > 0
                    ? string.Join("\n", line.Picks.Select((pick, index) =>
                    {
                        var winRate = Math.Round(pick.Wins * 100.0 / pick.Games);
                        var marker = pick.IsHoneyPick ? "🐝" : GetRankLabel(index);
                        var metaTierText = pick.IsHoneyPick ? $" · 메타 {EscapeMarkdown(pick.MetaTier ?? "?")}티어 일치" : "";
                        var text = $"{marker} **{EscapeMarkdown(pick.ChampionName)}** — {pick.Games}판 {pick.Wins}승 · 승률 {winRate}%{metaTierText}";
                        if (pick.MetaCounters.Count > 0)
                        {
                            text += $"\n　ㄴ 메타 카운터: {string.Join(", ", pick.MetaCounters.Take(3).Select(EscapeMarkdown))}";
                        }
                        return text;
                    }))
                    : line.HasData ? "승률 50% 이상인 챔피언이 없습니다." : "우리팀 자유 랭크 데이터 없음";

                var banText = line.HasData && line.Bans.Count > 0
                    ? string.Join("\n", line.Bans.Select(FormatBanLine))
                    : "표본 부족 또는 특이 밴 후보 없음";

                var lineName = GetKoreanPosition(line.Position);
                var embed = new EmbedBuilder()
                    .WithTitle($"{GetPositionEmoji(line.Position)} {lineName} 밴픽 추천")
                    .WithColor(GetPositionColor(line.Position))
                    .AddField("🐝 우리팀 추천픽 TOP 3", pickText);

                if (line.MetaPicks.Count == 0)
                {
                    embed.AddField("🌐 메타픽 TOP 3 (OP~1티어)", "OP~1티어 메타 데이터 없음");
                }
                else
                {
                    foreach (var (metaPick, index) in line.MetaPicks.Select((pick, index) => (pick, index)))
                    {
                        embed.AddField(
                            $"🌐 메타픽 TOP 3 · {index + 1}위",
                            FormatMetaPickLine(metaPick, index, nameByUserId));
                    }
                }

                embed.AddField("🚫 밴 추천", banText);

                embeds.Add(embed.Build());
            }

            var metaFooterNote = !recommendation.HasMetaSnapshot
                ? "메타 스냅샷 없음(Config/MetaTierSnapshot.json 참고)"
                : $"메타 스냅샷 기준일 {recommendation.MetaSnapshotUpdatedAt ?? "미기재"}";

            if (embeds.Count > 0)
            {
                var last = embeds[^1].ToEmbedBuilder()
                    .WithFooter(
                        $"우리팀: 자유 랭크 최소 {MinSampleSize}판(표본 부족 시 전체) · " +
                        $"{metaFooterNote} · 메타 데이터는 실시간이 아닙니다.")
                    .Build();
                embeds[^1] = last;
            }

            await FollowupAsync(embeds: embeds.ToArray());

            static string FormatMetaPickLine(
                BanPickMetaCandidate pick,
                int index,
                IReadOnlyDictionary<ulong, string> nameByUserId)
            {
                var hasReliableSample = pick.AzGames >= MetaPickMinSampleSize;
                var azWinRate = pick.AzGames > 0 ? pick.AzWins * 100.0 / pick.AzGames : 0.0;
                var marker = pick.AzGames == 0 || !hasReliableSample
                    ? "🧪"
                    : azWinRate >= 50.0 ? "🐝" : "⚠️";

                var text = $"{index + 1}. {marker} **{EscapeMarkdown(pick.ChampionName)}** — {EscapeMarkdown(pick.Tier)}티어 · " +
                    $"메타 승률 {pick.WinRate:0.##}% · 픽 {pick.PickRate:0.##}% · 밴 {pick.BanRate:0.##}%";

                if (pick.AzGames == 0)
                {
                    return text + "\n　ㄴ AZ 자랭: 플레이 기록 없음";
                }

                var delta = azWinRate - pick.WinRate;
                text += $"\n　ㄴ AZ 자랭: {pick.AzGames}판 {pick.AzWins}승 · 승률 {azWinRate:0.#}% " +
                    $"(메타 대비 {delta:+0.#;-0.#;0}%p)";
                if (!hasReliableSample)
                {
                    text += $" · 표본 부족({MetaPickMinSampleSize}판 미만)";
                }

                var playerText = string.Join(" · ", pick.Players
                    .OrderByDescending(player => player.Games)
                    .ThenByDescending(player => player.Wins * 1.0 / player.Games)
                    .Select(player =>
                    {
                        var name = nameByUserId.GetValueOrDefault(player.DiscordUserId, $"사용자 {player.DiscordUserId}");
                        var winRate = player.Wins * 100.0 / player.Games;
                        return $"{EscapeMarkdown(name)}({player.Games}판, {winRate:0.#}%)";
                    }));
                return text + $"\n　ㄴ 주 플레이어: {playerText}";
            }

            static string GetPositionEmoji(string position) => position switch
            {
                "TOP" => "🛡️",
                "JUNGLE" => "🌲",
                "MIDDLE" => "✨",
                "BOTTOM" => "🏹",
                "UTILITY" => "💚",
                _ => "🎯",
            };

            static Color GetPositionColor(string position) => position switch
            {
                "TOP" => Color.DarkRed,
                "JUNGLE" => Color.DarkGreen,
                "MIDDLE" => Color.Purple,
                "BOTTOM" => Color.Blue,
                "UTILITY" => Color.Teal,
                _ => Color.Gold,
            };

            // 밴 후보 1개를 근거(Reason)에 맞는 문구로 그립니다 — 예전엔 이 3가지가 각각 인라인으로
            // 흩어져 있었는데, 서비스 분리 후엔 여기 한 곳에 모여서 오히려 한눈에 비교하기 쉬워졌습니다.
            static string FormatBanLine(BanPickBanCandidate ban) => ban.Reason switch
            {
                BanReasonKind.WorstOpponent =>
                    $"💀 **{EscapeMarkdown(ban.ChampionName)}** — 맞상대 승률 낮음 " +
                    $"(상대 {ban.Games}판 중 우리 승률 {Math.Round(ban.Wins * 100.0 / ban.Games)}%)",
                BanReasonKind.MetaTier =>
                    $"🔥 **{EscapeMarkdown(ban.ChampionName)}** — 메타 티어 {EscapeMarkdown(ban.MetaTier ?? "?")} " +
                    $"(op.gg 기준 승률 {ban.MetaWinRate:0.#}%)",
                BanReasonKind.OurPickCounter => FormatOurPickCounterBan(ban),
                _ => EscapeMarkdown(ban.ChampionName),
            };

            static string FormatOurPickCounterBan(BanPickBanCandidate ban)
            {
                var winRate = Math.Round(ban.Wins * 100.0 / ban.Games);
                var severity = winRate <= 40 ? "🚨" : "⚠️";
                var contextNames = string.Join("/", (ban.OurTopPicks ?? []).Select(EscapeMarkdown));
                return $"{severity} **{EscapeMarkdown(ban.ChampionName)}** — 우리 베스트픽({contextNames}) 상대 승률 {winRate}% " +
                    $"(표본 {ban.Games}판, 작은 표본 주의)";
            }
        }

        [SlashCommand("승률순위", "등록된 AtoZ 멤버들의 자유 랭크 승률 순위를 보여줍니다.", true)]
        public async Task ShowWinRateRankingAsync()
        {
            await DeferAsync();

            if (Context.Guild is null)
            {
                await FollowupAsync("이 명령은 AtoZ Discord 서버에서만 사용할 수 있습니다.");
                return;
            }

            var rows = await _matchRepository.GetMemberWinRatesAsync(Context.Guild.Id, FlexQueueId);
            if (rows.Count == 0)
            {
                await FollowupAsync("아직 수집된 자유 랭크 전적이 없습니다. 운영자가 `/atoz 전적수집`을 먼저 실행해야 해요.");
                return;
            }

            var sampled = rows.Where(row => row.Games >= MinSampleSize).ToList();
            if (sampled.Count == 0)
            {
                sampled = rows.ToList();
            }

            var nameByUserId = await GetDisplayNameLookupAsync(Context.Guild.Id);
            var lines = sampled.Select((row, index) =>
            {
                var displayName = nameByUserId.TryGetValue(row.DiscordUserId, out var name) ? name : "알 수 없는 멤버";
                var winRate = Math.Round(row.Wins * 100.0 / row.Games);
                var kda = (row.Kills + row.Assists) / (double)Math.Max(1, row.Deaths);
                return $"{GetRankLabel(index)} **{EscapeMarkdown(displayName)}** — {row.Games}판 {row.Wins}승 · 승률 {winRate:F0}% · KDA {kda:F2}";
            });

            var embed = new EmbedBuilder()
                .WithTitle("AtoZ 자유 랭크 승률 순위")
                .WithColor(Color.Blue)
                .WithDescription(string.Join("\n", lines))
                .WithFooter($"운영자가 /atoz 전적수집 을 실행한 시점까지의 데이터 기준입니다. 최소 {MinSampleSize}판 이상 (표본 부족 시 전체 표시)")
                .Build();

            await FollowupAsync(embed: embed);
        }

        [SlashCommand("내전적", "우리 클랜 데이터 기준 자유 랭크 총 승률·최근 전적·라인별 승률·모스트/워스트 챔피언을 보여줍니다.", true)]
        public async Task ShowMyClanStatsAsync(
            [Summary("멤버", "확인할 AtoZ 멤버. 생략하면 명령을 실행한 본인")]
        IUser? member = null,
            [Summary("월", "이번 해 월(또는 월 범위) 필터. 예: 8(8월만), 8~8(8월만), 6~8(6~8월). 생략하면 전체 기간 통합")]
        string? 월 = null)
        {
            await DeferAsync();

            if (Context.Guild is null)
            {
                await FollowupAsync("이 명령은 AtoZ Discord 서버에서만 사용할 수 있습니다.");
                return;
            }

            DateTimeOffset? rangeStartUtc = null;
            DateTimeOffset? rangeEndUtc = null;
            string periodLabel = "전체 기간(통합)";

            if (!string.IsNullOrWhiteSpace(월))
            {
                if (!TryParseMonthRange(월, out var startMonth, out var endMonth))
                {
                    await FollowupAsync("월 형식이 올바르지 않습니다. `8`(8월만), `8~8`, `6~8`처럼 입력해 주세요(1~12, 시작<=끝).");
                    return;
                }

                var nowKst = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9));
                var rangeStartKst = new DateTimeOffset(nowKst.Year, startMonth, 1, 0, 0, 0, TimeSpan.FromHours(9));
                var rangeEndKst = new DateTimeOffset(nowKst.Year, endMonth, 1, 0, 0, 0, TimeSpan.FromHours(9)).AddMonths(1);
                rangeStartUtc = rangeStartKst.ToUniversalTime();
                rangeEndUtc = rangeEndKst.ToUniversalTime();
                periodLabel = startMonth == endMonth
                    ? $"{nowKst.Year}년 {startMonth}월"
                    : $"{nowKst.Year}년 {startMonth}월~{endMonth}월";
            }

            var targetMember = member ?? Context.User;

            var positionRows = await _matchRepository.GetMemberPositionStatsAsync(
                Context.Guild.Id, FlexQueueId, targetMember.Id, rangeStartUtc, rangeEndUtc);
            if (positionRows.Count == 0)
            {
                var who = targetMember.Id == Context.User.Id ? "본인의" : "해당 멤버의";
                var periodNote = rangeStartUtc is null ? "" : $" ({periodLabel} 기준)";
                await FollowupAsync(
                    $"{who} 저장된 자유 랭크 전적이 없습니다{periodNote}. AtoZ 등록 여부와 `/atoz 전적수집` 실행 여부를 확인해 주세요.");
                return;
            }

            var championRows = await _matchRepository.GetMemberChampionStatsAsync(
                Context.Guild.Id, FlexQueueId, targetMember.Id, rangeStartUtc, rangeEndUtc);
            var positionChampionRows = await _matchRepository.GetMemberChampionStatsByPositionAsync(
                Context.Guild.Id, FlexQueueId, targetMember.Id, rangeStartUtc, rangeEndUtc);

            // "최근 10경기 서머리"·"최근 전적"용 — 기간(월) 필터와 무관하게 항상 진짜 최신 10경기 기준입니다
            // (/아재전적처럼 필터가 없는 게 자연스러움). 5인큐만 저장되므로 팀 전체가 함께 딸려와서
            // 기여도 순위 계산이 바로 가능합니다.
            const int RecentMatchCount = 10;
            const int RecentMatchDisplayCount = 3;
            var recentMatches = await _matchRepository.GetRecentMemberMatchesAsync(
                Context.Guild.Id, FlexQueueId, targetMember.Id, RecentMatchCount);
            var recentV4Scores = await _matchRepository.GetContributionV4ScoresAsync(
                Context.Guild.Id, recentMatches.Select(m => m.MatchId).ToList());

            var totalGames = positionRows.Sum(row => row.Games);
            var totalWins = positionRows.Sum(row => row.Wins);
            var totalWinRate = Math.Round(totalWins * 100.0 / totalGames);

            var displayName = targetMember is Discord.WebSocket.SocketGuildUser guildUser
                ? guildUser.DisplayName
                : targetMember.Username;

            var positionChampionLookup = positionChampionRows
                .GroupBy(row => row.TeamPosition)
                .ToDictionary(g => g.Key, g => g.ToList());

            var positionLines = positionRows
                .OrderBy(row => GetPositionOrder(row.TeamPosition))
                .Select(row =>
                {
                    var line = $"{GetKoreanPosition(row.TeamPosition)}: {row.Games}판 {row.Wins}승 · 승률 {Math.Round(row.Wins * 100.0 / row.Games)}%";

                    if (positionChampionLookup.TryGetValue(row.TeamPosition, out var champsInLine))
                    {
                        var topChamps = champsInLine
                            .OrderByDescending(c => c.Games)
                            .ThenByDescending(c => c.Wins * 1.0 / c.Games)
                            .Take(3)
                            .Select(c => $"{EscapeMarkdown(c.ChampionName)} {c.Games}판 {Math.Round(c.Wins * 100.0 / c.Games)}%");
                        line += $"\n　ㄴ 모스트: {string.Join(", ", topChamps)}";
                    }

                    return line;
                });

            // 표본 부족(최소판수 미달)은 전체로 폴백하고, 그 안에서 "모스트"는 승률 50% 이상,
            // "워스트"는 승률 50% 미만인 챔피언만 후보로 삼습니다 (표본만 채운 애매한 챔피언이 섞이는 걸 방지).
            var sampledChampions = championRows.Where(row => row.Games >= MinSampleSize).ToList();
            if (sampledChampions.Count == 0)
            {
                sampledChampions = championRows.ToList();
            }

            var mostPool = sampledChampions.Where(row => row.Wins * 1.0 / row.Games >= 0.5).ToList();
            var worstPool = sampledChampions.Where(row => row.Wins * 1.0 / row.Games < 0.5).ToList();

            var mostChampionText = mostPool.Count > 0
                ? string.Join("\n", mostPool
                    .OrderByDescending(row => row.Games)
                    .ThenByDescending(row => row.Wins * 1.0 / row.Games)
                    .Take(3)
                    .Select((row, index) =>
                        $"{GetRankLabel(index)} **{EscapeMarkdown(row.ChampionName)}** — {row.Games}판 {row.Wins}승 · 승률 {Math.Round(row.Wins * 100.0 / row.Games)}%"))
                : "승률 50% 이상인 챔피언이 없습니다";

            var worstChampionText = worstPool.Count > 0
                ? string.Join("\n", worstPool
                    .OrderBy(row => row.Wins * 1.0 / row.Games)
                    .ThenByDescending(row => row.Games)
                    .Take(3)
                    .Select((row, index) =>
                        $"{(index == 0 ? "💀" : $"{index + 1}.")} **{EscapeMarkdown(row.ChampionName)}** — {row.Games}판 {row.Wins}승 · 승률 {Math.Round(row.Wins * 100.0 / row.Games)}%"))
                : "승률 50% 미만인 챔피언이 없습니다 👍";

            // 최근 N경기 각각의 AZ기여도 순위를 한 번씩만 계산해서(매치당 O(팀원수)) 서머리·최근 전적 둘 다에 씁니다.
            var recentRankByMatch = recentMatches.ToDictionary(
                m => m.MatchId,
                m => ComputeContributionRanks(m, recentV4Scores).GetValueOrDefault(targetMember.Id, 0));

            string? RecentSummaryText()
            {
                if (recentMatches.Count == 0)
                {
                    return null;
                }

                var mineByMatch = recentMatches.ToDictionary(
                    m => m.MatchId,
                    m => m.Participants.First(p => p.DiscordUserId == targetMember.Id));

                var recentWins = mineByMatch.Values.Count(p => p.Win);
                var recentLosses = recentMatches.Count - recentWins;
                var recentWinRate = Math.Round(recentWins * 100.0 / recentMatches.Count);

                var mostText = mineByMatch.Values
                    .GroupBy(p => p.ChampionName)
                    .OrderByDescending(g => g.Count())
                    .ThenByDescending(g => g.Count(p => p.Win) * 1.0 / g.Count())
                    .Take(3)
                    .Select(g => $"{EscapeMarkdown(g.Key)} {g.Count()}판 {Math.Round(g.Count(p => p.Win) * 100.0 / g.Count())}%");

                var ranks = recentRankByMatch.Values.Where(rank => rank > 0).ToList();
                var avgRankText = ranks.Count > 0
                    ? $"**{ranks.Average():F1}위**"
                    : "정보 없음(리플 업로드로만 저장된 경기 등)";

                return $"**{recentMatches.Count}판 {recentWins}승 {recentLosses}패** · 승률 **{recentWinRate:F0}%**\n" +
                    $"모스트: {string.Join(", ", mostText)}\n" +
                    $"평균 AZ기여도: {avgRankText}";
            }

            string? RecentMatchesText()
            {
                if (recentMatches.Count == 0)
                {
                    return null;
                }

                var lines = recentMatches
                    .Take(RecentMatchDisplayCount)
                    .Select(m =>
                    {
                        var mine = m.Participants.First(p => p.DiscordUserId == targetMember.Id);
                        var playedAt = m.GameCreatedAt.ToOffset(TimeSpan.FromHours(9));
                        var winMark = mine.Win ? "🔵" : "🔴";
                        var rank = recentRankByMatch.GetValueOrDefault(m.MatchId, 0);
                        var rankMark = rank switch { 0 => "", 1 => " 👑", 5 => " 💀", _ => $" ({rank}위)" };
                        return $"{winMark} {playedAt:MM/dd HH:mm} · {GetPositionIcon(mine.TeamPosition)} {GetKoreanPosition(mine.TeamPosition)} " +
                            $"**{EscapeMarkdown(mine.ChampionName)}** {mine.Kills}/{mine.Deaths}/{mine.Assists}{rankMark}";
                    });

                return string.Join("\n", lines);
            }

            var embedBuilder = new EmbedBuilder()
                .WithTitle($"{EscapeMarkdown(displayName)}의 AtoZ 자유 랭크 전적 ({periodLabel})")
                .WithColor(totalWins * 2 >= totalGames ? Color.Green : Color.Red)
                .AddField("총 승률", $"**{totalGames}판 {totalWins}승** · 승률 **{totalWinRate:F0}%**");

            var recentSummaryText = RecentSummaryText();
            if (recentSummaryText is not null)
            {
                // "최근"은 월 필터와 무관하게 항상 진짜 최신 기준이라, 월 필터를 걸어둔 상태면 헷갈리지 않게 표시해둡니다.
                var recentTitle = rangeStartUtc is null
                    ? $"최근 {recentMatches.Count}경기 서머리"
                    : $"최근 {recentMatches.Count}경기 서머리 (기간 필터와 무관, 진짜 최신 기준)";
                embedBuilder.AddField(recentTitle, recentSummaryText);
            }

            var recentMatchesText = RecentMatchesText();
            if (recentMatchesText is not null)
            {
                embedBuilder.AddField("최근 전적", recentMatchesText);
            }

            var embed = embedBuilder
                .AddField("라인별 승률", string.Join("\n\n", positionLines))
                .AddField($"모스트 챔피언 TOP 3 (승률 50% 이상, 최소 {MinSampleSize}판 이상 기준)", mostChampionText)
                .AddField($"워스트 챔피언 TOP 3 (승률 50% 미만, 최소 {MinSampleSize}판 이상 기준)", worstChampionText)
                .WithFooter("우리 클랜이 모은 데이터(같은 팀 5명 전원 AtoZ 멤버인 경기) 기준입니다.")
                .Build();

            await FollowupAsync(embed: embed);
        }

        [SlashCommand("조합추천", "우리 클랜 전적 데이터 기준, 같은 팀에서 함께 나왔을 때 승률이 좋은 챔피언 조합을 보여줍니다.", true)]
        public async Task ShowDuoRecommendationAsync()
        {
            await DeferAsync();

            if (Context.Guild is null)
            {
                await FollowupAsync("이 명령은 AtoZ Discord 서버에서만 사용할 수 있습니다.");
                return;
            }

            var rows = await _matchRepository.GetChampionSynergyAsync(Context.Guild.Id, FlexQueueId);
            if (rows.Count == 0)
            {
                await FollowupAsync("같은 팀으로 함께 플레이한 자유 랭크 전적이 아직 없습니다. 운영자가 `/atoz 전적수집`을 먼저 실행해야 해요.");
                return;
            }

            var filtered = rows.Where(row => row.Games >= MinSampleSize).ToList();
            if (filtered.Count == 0)
            {
                filtered = rows.ToList();
            }

            var lines = filtered
                .OrderByDescending(row => row.Wins * 1.0 / row.Games)
                .ThenByDescending(row => row.Games)
                .Take(10)
                .Select((row, index) =>
                {
                    var winRate = Math.Round(row.Wins * 100.0 / row.Games);
                    return $"{GetRankLabel(index)} **{EscapeMarkdown(row.ChampionA)}** + **{EscapeMarkdown(row.ChampionB)}** — " +
                        $"{row.Games}판 {row.Wins}승 · 승률 {winRate:F0}%";
                });

            var embed = new EmbedBuilder()
                .WithTitle("AtoZ 조합 추천 (같은 팀 챔피언 조합 승률)")
                .WithColor(Color.Purple)
                .WithDescription(string.Join("\n", lines))
                .WithFooter($"자유 랭크 · 같은 팀에서 함께 나온 챔피언 조합 기준, 최소 {MinSampleSize}판 이상 (표본 부족 시 전체 표시)")
                .Build();

            await FollowupAsync(embed: embed);
        }

        [SlashCommand("바텀듀오", "원딜+서폿 멤버 조합별 승률을 모스트 10 / 워스트 10으로 보여줍니다.", true)]
        public async Task ShowBottomDuoStatsAsync()
        {
            await DeferAsync();

            if (Context.Guild is null)
            {
                await FollowupAsync("이 명령은 AtoZ Discord 서버에서만 사용할 수 있습니다.");
                return;
            }

            var rows = await _matchRepository.GetBottomDuoStatsAsync(Context.Guild.Id, FlexQueueId);
            if (rows.Count == 0)
            {
                await FollowupAsync("같은 팀 원딜+서폿으로 함께한 자유 랭크 전적이 아직 없습니다. 운영자가 `/atoz 전적수집`을 먼저 실행해야 해요.");
                return;
            }

            var sampled = rows.Where(row => row.Games >= MinSampleSize).ToList();
            if (sampled.Count == 0)
            {
                sampled = rows.ToList();
            }

            var nameByUserId = await GetDisplayNameLookupAsync(Context.Guild.Id);

            string FormatLine(BottomDuoRow row, string rank)
            {
                var adcName = nameByUserId.GetValueOrDefault(row.AdcDiscordUserId, "알 수 없는 멤버");
                var supportName = nameByUserId.GetValueOrDefault(row.SupportDiscordUserId, "알 수 없는 멤버");
                var winRate = Math.Round(row.Wins * 100.0 / row.Games);
                return $"{rank} **{EscapeMarkdown(adcName)}**(원딜) + **{EscapeMarkdown(supportName)}**(서폿) — " +
                    $"{row.Games}판 {row.Wins}승 · 승률 {winRate:F0}%";
            }

            var mostPool = sampled.Where(row => row.Wins * 1.0 / row.Games >= 0.5).ToList();
            var worstPool = sampled.Where(row => row.Wins * 1.0 / row.Games < 0.5).ToList();

            var mostText = mostPool.Count > 0
                ? string.Join("\n", mostPool
                    .OrderByDescending(row => row.Wins * 1.0 / row.Games)
                    .ThenByDescending(row => row.Games)
                    .Take(10)
                    .Select((row, index) => FormatLine(row, GetRankLabel(index))))
                : "승률 50% 이상인 바텀 조합이 없습니다.";

            var worstText = worstPool.Count > 0
                ? string.Join("\n", worstPool
                    .OrderBy(row => row.Wins * 1.0 / row.Games)
                    .ThenByDescending(row => row.Games)
                    .Take(10)
                    .Select((row, index) => FormatLine(row, index == 0 ? "💀" : $"{index + 1}.")))
                : "승률 50% 미만인 바텀 조합이 없습니다 👍";

            var embed = new EmbedBuilder()
                .WithTitle("AtoZ 바텀(원딜+서폿) 조합 승률")
                .WithColor(Color.Purple)
                .AddField("모스트 10 (승률 50% 이상)", mostText)
                .AddField("워스트 10 (승률 50% 미만)", worstText)
                .WithFooter($"자유 랭크 · 같은 팀 원딜+서폿 조합 기준, 최소 {MinSampleSize}판 이상 (표본 부족 시 전체 표시)")
                .Build();

            await FollowupAsync(embed: embed);
        }

        [SlashCommand("봇듀오챔프승률", "원딜+서폿 챔피언 조합별 승률을 모스트 10 / 워스트 10으로 보여줍니다.", true)]
        public Task ShowBotDuoChampionStatsAsync() =>
            ShowLaneChampionDuoAsync("AtoZ 봇(원딜+서폿) 챔피언 조합 승률", "BOTTOM", "UTILITY", "원딜", "서폿");

        [SlashCommand("정글미드듀오챔프승률", "정글+미드 챔피언 조합별 승률을 모스트 10 / 워스트 10으로 보여줍니다.", true)]
        public Task ShowJungleMidDuoChampionStatsAsync() =>
            ShowLaneChampionDuoAsync("AtoZ 정글+미드 챔피언 조합 승률", "JUNGLE", "MIDDLE", "정글", "미드");

        private async Task ShowLaneChampionDuoAsync(string title, string positionA, string positionB, string labelA, string labelB)
        {
            await DeferAsync();

            if (Context.Guild is null)
            {
                await FollowupAsync("이 명령은 AtoZ Discord 서버에서만 사용할 수 있습니다.");
                return;
            }

            var rows = await _matchRepository.GetLaneChampionDuoStatsAsync(Context.Guild.Id, FlexQueueId, positionA, positionB);
            if (rows.Count == 0)
            {
                await FollowupAsync($"같은 팀 {labelA}+{labelB}으로 함께한 자유 랭크 전적이 아직 없습니다. 운영자가 `/atoz 전적수집`을 먼저 실행해야 해요.");
                return;
            }

            var sampled = rows.Where(row => row.Games >= MinSampleSize).ToList();
            if (sampled.Count == 0)
            {
                sampled = rows.ToList();
            }

            string FormatLine(LaneChampionDuoRow row, string rank)
            {
                var winRate = Math.Round(row.Wins * 100.0 / row.Games);
                return $"{rank} **{EscapeMarkdown(row.ChampionA)}**({labelA}) + **{EscapeMarkdown(row.ChampionB)}**({labelB}) — " +
                    $"{row.Games}판 {row.Wins}승 · 승률 {winRate:F0}%";
            }

            var mostPool = sampled.Where(row => row.Wins * 1.0 / row.Games >= 0.5).ToList();
            var worstPool = sampled.Where(row => row.Wins * 1.0 / row.Games < 0.5).ToList();

            var mostText = mostPool.Count > 0
                ? string.Join("\n", mostPool
                    .OrderByDescending(row => row.Wins * 1.0 / row.Games)
                    .ThenByDescending(row => row.Games)
                    .Take(10)
                    .Select((row, index) => FormatLine(row, GetRankLabel(index))))
                : "승률 50% 이상인 조합이 없습니다.";

            var worstText = worstPool.Count > 0
                ? string.Join("\n", worstPool
                    .OrderBy(row => row.Wins * 1.0 / row.Games)
                    .ThenByDescending(row => row.Games)
                    .Take(10)
                    .Select((row, index) => FormatLine(row, index == 0 ? "💀" : $"{index + 1}.")))
                : "승률 50% 미만인 조합이 없습니다 👍";

            var embed = new EmbedBuilder()
                .WithTitle(title)
                .WithColor(Color.Purple)
                .AddField("모스트 10 (승률 50% 이상)", mostText)
                .AddField("워스트 10 (승률 50% 미만)", worstText)
                .WithFooter($"자유 랭크 · 같은 팀 {labelA}+{labelB} 챔피언 조합 기준, 최소 {MinSampleSize}판 이상 (표본 부족 시 전체 표시)")
                .Build();

            await FollowupAsync(embed: embed);
        }

        [SlashCommand("아재전적", "AtoZ 멤버 5명 전원이 한 팀으로 5인큐한 자유 랭크 경기 기록만 최근 순으로 보여줍니다.", true)]
        public async Task ShowAjaeMatchesAsync(
            [Summary("개수", "최근 몇 경기까지 보여줄지 (기본 5, 최대 10)")]
        int count = 5)
        {
            await DeferAsync();

            if (Context.Guild is null)
            {
                await FollowupAsync("이 명령은 AtoZ Discord 서버에서만 사용할 수 있습니다.");
                return;
            }

            count = Math.Clamp(count, 1, 10);

            var matches = await _matchRepository.GetClanMatchesAsync(Context.Guild.Id, FlexQueueId, AjaeMatchMinTeammates, count);
            if (matches.Count == 0)
            {
                await FollowupAsync(
                    "AtoZ 멤버 5명 전원이 한 팀으로 5인큐한 자유 랭크 경기가 아직 없습니다. " +
                    "운영자가 `/atoz 전적수집`으로 데이터를 먼저 모아야 해요.");
                return;
            }

            var nameByUserId = await GetDisplayNameLookupAsync(Context.Guild.Id);
            var v4Scores = await _matchRepository.GetContributionV4ScoresAsync(
                Context.Guild.Id, matches.Select(m => m.MatchId).ToList());

            var embedBuilder = new EmbedBuilder()
                .WithTitle("AtoZ 아재전적 (5인큐)")
                .WithColor(Color.Gold)
                .WithFooter("시간은 KST 기준. 👑/💀/(N위)는 그 판 우리 팀 5명 상대 비교 기여도 순위입니다(v4.0.0 — 15분 라인전+후반 분리, " +
                    "v4-backfill 안 된 옛날 경기는 v3로 자동 대체. 리플 업로드로만 저장된 경기는 표시 안 됨).");

            foreach (var match in matches)
            {
                var playedAt = match.GameCreatedAt.ToOffset(TimeSpan.FromHours(9));
                var minutes = Math.Max(0, match.GameDurationSeconds) / 60;

                // 팀별로(5명 채워졌으면) 기여도 순위를 계산해서, 그 판 베스트(👑)/워스트(💀)를 표시합니다.
                var rankByUserId = ComputeContributionRanks(match, v4Scores);

                // AtoZ 5인큐 전적은 5명 전원이 같은 팀이라 승패가 판 전체에 하나뿐 — 줄마다 반복 표시하지 않고
                // 날짜 앞에 한 번만(🔵승/🔴패) 표시합니다.
                var win = match.Participants.Count > 0 && match.Participants[0].Win;
                var winMark = win ? "🔵" : "🔴";

                var lines = match.Participants
                    .OrderBy(p => p.TeamId)
                    .ThenBy(p => GetPositionOrder(p.TeamPosition))
                    .Select(p =>
                    {
                        var name = nameByUserId.GetValueOrDefault(p.DiscordUserId, "알 수 없는 멤버");
                        var rankMark = rankByUserId.TryGetValue(p.DiscordUserId, out var rank)
                            ? rank switch { 1 => " 👑", 5 => " 💀", _ => $" ({rank}위)" }
                            : "";
                        return $"{GetPositionIcon(p.TeamPosition)} {GetKoreanPosition(p.TeamPosition)} **{EscapeMarkdown(p.ChampionName)}** " +
                            $"{p.Kills}/{p.Deaths}/{p.Assists} — {EscapeMarkdown(name)}{rankMark}";
                    });

                // 매치ID를 작게라도 같이 보여줘서, 부캐를 빌려쓴 걸 발견했을 때 바로
                // /atoz 전적재배정 매치아이디:로 붙여넣을 수 있게 합니다.
                embedBuilder.AddField(
                    $"{winMark} {playedAt:MM/dd HH:mm} · {minutes}분 · {match.Participants.Count}명 참여",
                    string.Join("\n", lines) + $"\n`{match.MatchId}`");
            }

            await FollowupAsync(embed: embedBuilder.Build());
        }

        [SlashCommand("명예의전당", "이번 달(또는 지정한 연월)의 기여도 베스트 플레이어 랭킹을 보여줍니다.", true)]
        public async Task ShowHonorBoardAsync(
            [Summary("연월", "예: 2026-08. 생략하면 이번 달(KST 기준)")]
        string? 연월 = null)
        {
            await DeferAsync();

            if (Context.Guild is null)
            {
                await FollowupAsync("이 명령은 AtoZ Discord 서버에서만 사용할 수 있습니다.");
                return;
            }

            DateTimeOffset monthStartKst;
            string monthLabel;
            if (string.IsNullOrWhiteSpace(연월))
            {
                var nowKst = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9));
                monthStartKst = new DateTimeOffset(nowKst.Year, nowKst.Month, 1, 0, 0, 0, TimeSpan.FromHours(9));
            }
            else if (TryParseYearMonth(연월, out var year, out var month))
            {
                monthStartKst = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.FromHours(9));
            }
            else
            {
                await FollowupAsync("연월 형식이 올바르지 않습니다. `2026-08`처럼 입력해 주세요.");
                return;
            }

            monthLabel = $"{monthStartKst.Year}년 {monthStartKst.Month}월";
            var monthEndKst = monthStartKst.AddMonths(1);

            var matches = await _matchRepository.GetContributionStatsInRangeAsync(
                Context.Guild.Id, FlexQueueId, monthStartKst.ToUniversalTime(), monthEndKst.ToUniversalTime());
            var v4Scores = await _matchRepository.GetContributionV4ScoresAsync(
                Context.Guild.Id, matches.Select(m => m.MatchId).ToList());

            var bestCounts = new Dictionary<ulong, int>();
            var worstCounts = new Dictionary<ulong, int>();
            var gamesScored = new Dictionary<ulong, int>();
            var totalMatchesScored = 0;

            foreach (var match in matches)
            {
                foreach (var teamGroup in match.Participants.GroupBy(p => p.TeamId))
                {
                    var teamList = teamGroup.ToList();

                    // v4.0.0 점수가 5명 전원 있으면 그걸로(백필된 경기), 없으면 v3로 폴백.
                    var v4TeamScores = teamList
                        .Select(p => (Participant: p, Score: v4Scores.GetValueOrDefault((match.MatchId, p.DiscordUserId), double.NaN)))
                        .ToList();

                    IReadOnlyList<(ulong DiscordUserId, int Rank)> rankedRows;
                    if (v4TeamScores.All(x => !double.IsNaN(x.Score)))
                    {
                        rankedRows = v4TeamScores
                            .OrderByDescending(x => x.Score)
                            .Select((x, i) => (x.Participant.DiscordUserId, Rank: i + 1))
                            .ToList();
                    }
                    else
                    {
                        var v3Ranked = _contributionScoreCalculator.TryCalculate(teamList);
                        if (v3Ranked is null)
                        {
                            continue;
                        }

                        rankedRows = v3Ranked.Select(r => (r.Participant.DiscordUserId, r.Rank)).ToList();
                    }

                    totalMatchesScored++;
                    foreach (var (userId, rank) in rankedRows)
                    {
                        gamesScored[userId] = gamesScored.GetValueOrDefault(userId) + 1;
                        if (rank == 1)
                        {
                            bestCounts[userId] = bestCounts.GetValueOrDefault(userId) + 1;
                        }
                        else if (rank == 5)
                        {
                            worstCounts[userId] = worstCounts.GetValueOrDefault(userId) + 1;
                        }
                    }
                }
            }

            if (gamesScored.Count == 0)
            {
                await FollowupAsync(
                    $"{monthLabel}에 기여도 점수를 계산할 수 있는 경기가 없습니다. " +
                    "`/atoz 전적수집`으로 모은 자유 랭크 경기만 집계됩니다.");
                return;
            }

            var nameByUserId = await GetDisplayNameLookupAsync(Context.Guild.Id);

            var rankingLines = gamesScored.Keys
                .OrderByDescending(userId => bestCounts.GetValueOrDefault(userId))
                .ThenByDescending(userId => gamesScored[userId])
                .Take(10)
                .Select((userId, index) =>
                {
                    var name = nameByUserId.GetValueOrDefault(userId, "알 수 없는 멤버");
                    var best = bestCounts.GetValueOrDefault(userId);
                    var worst = worstCounts.GetValueOrDefault(userId);
                    var points = best * 100;
                    return $"{GetRankLabel(index)} **{EscapeMarkdown(name)}** — {points}점 " +
                        $"(👑 베스트 {best}회 / 💀 워스트 {worst}회, 총 {gamesScored[userId]}판)";
                });

            var embed = new EmbedBuilder()
                .WithTitle($"🏆 명예의 전당 — {monthLabel}")
                .WithColor(Color.Gold)
                .WithDescription(string.Join("\n", rankingLines))
                .WithFooter(
                    $"베스트 플레이어 1회당 100점 · 이번 달 계산된 경기 {totalMatchesScored}건. " +
                    "매달 새로 집계됩니다(누적 아님). `/atoz 전적수집`으로 모은 자유 랭크 경기만 집계됩니다.")
                .Build();

            await FollowupAsync(embed: embed);
        }

        private static bool TryParseYearMonth(string input, out int year, out int month)
        {
            year = 0;
            month = 0;
            var match = Regex.Match(input.Trim(), @"^(\d{4})[-./]?(\d{1,2})$");
            if (!match.Success)
            {
                return false;
            }

            year = int.Parse(match.Groups[1].Value);
            month = int.Parse(match.Groups[2].Value);
            return month is >= 1 and <= 12;
        }

        /// <summary>
        /// /내전적의 "월" 필터 파싱 — "8"(8월만), "8~8"(8월만), "6~8"(6~8월) 형식. 연도는 안 받고
        /// 항상 이번 해(KST 기준)로 고정합니다. startMonth/endMonth 둘 다 1~12, start&lt;=end.
        /// </summary>
        private static bool TryParseMonthRange(string input, out int startMonth, out int endMonth)
        {
            startMonth = 0;
            endMonth = 0;
            var match = Regex.Match(input.Trim(), @"^(\d{1,2})\s*(?:~\s*(\d{1,2}))?$");
            if (!match.Success)
            {
                return false;
            }

            startMonth = int.Parse(match.Groups[1].Value);
            endMonth = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : startMonth;
            return startMonth is >= 1 and <= 12 && endMonth is >= 1 and <= 12 && startMonth <= endMonth;
        }

        /// <summary>
        /// 매치 한 판(팀별로 5명까지)의 기여도 순위를 계산합니다. v4.0.0 점수가 팀 전원 있으면 그걸 쓰고
        /// (백필된 경기), 없으면 v3(ContributionScoreCalculator, 전체 게임 기준)로 폴백합니다.
        /// 둘 다 불가능한 팀(리플 업로드로만 저장된 경기 등)은 그냥 결과에서 빠집니다.
        /// /아재전적·/내전적이 공유합니다.
        /// </summary>
        private Dictionary<ulong, int> ComputeContributionRanks(
            ClanMatchRow match,
            IReadOnlyDictionary<(string MatchId, ulong DiscordUserId), double> v4Scores)
        {
            var rankByUserId = new Dictionary<ulong, int>();

            foreach (var teamGroup in match.Participants.GroupBy(p => p.TeamId))
            {
                var teamList = teamGroup.ToList();
                var v4TeamScores = teamList
                    .Select(p => (Participant: p, Score: v4Scores.GetValueOrDefault((match.MatchId, p.DiscordUserId), double.NaN)))
                    .ToList();

                if (v4TeamScores.All(x => !double.IsNaN(x.Score)))
                {
                    var v4Ranked = v4TeamScores.OrderByDescending(x => x.Score).ToList();
                    for (var i = 0; i < v4Ranked.Count; i++)
                    {
                        rankByUserId[v4Ranked[i].Participant.DiscordUserId] = i + 1;
                    }

                    continue;
                }

                var ranked = _contributionScoreCalculator.TryCalculate(teamList);
                if (ranked is null)
                {
                    continue;
                }

                foreach (var row in ranked)
                {
                    rankByUserId[row.Participant.DiscordUserId] = row.Rank;
                }
            }

            return rankByUserId;
        }

        /// <summary>
        /// 기여도 점수(맞라인 상대 비교)용 지표 묶음을 만듭니다. opponent가 null이면(맞라인 상대를
        /// 못 찾음 — 예: 팀 조합이 특이하거나 데이터 누락) 상대 지표는 전부 비워둡니다.
        /// </summary>
        // internal(private 아님) — Tools/OwnerConflictResolveExperiment.cs가 재사용합니다(같은 로직 중복 방지).
        internal static ParticipationStats BuildParticipationStats(FullMatchParticipant self, FullMatchParticipant? opponent) =>
            new(
                DamageDealt: self.DamageDealt,
                DamageTaken: self.DamageTaken,
                DamageMitigated: self.DamageMitigated,
                GoldEarned: self.GoldEarned,
                VisionScore: self.VisionScore,
                CcTimeDealt: self.CcTimeDealt,
                HealAmount: self.HealAmount,
                WardsPlaced: self.WardsPlaced,
                DamageToObjectives: self.DamageToObjectives,
                OpponentKills: opponent?.Kills,
                OpponentDeaths: opponent?.Deaths,
                OpponentAssists: opponent?.Assists,
                OpponentDamageDealt: opponent?.DamageDealt,
                OpponentDamageTaken: opponent?.DamageTaken,
                OpponentGoldEarned: opponent?.GoldEarned,
                OpponentCreepScore: opponent?.CreepScore,
                OpponentVisionScore: opponent?.VisionScore,
                OpponentCcTimeDealt: opponent?.CcTimeDealt,
                OpponentHealAmount: opponent?.HealAmount,
                OpponentWardsPlaced: opponent?.WardsPlaced,
                OpponentDamageToObjectives: opponent?.DamageToObjectives);

        private async Task<Dictionary<ulong, string>> GetDisplayNameLookupAsync(ulong guildId)
        {
            var members = await _memberRepository.GetAllByGuildAsync(guildId);
            return members.ToDictionary(member => member.DiscordUserId, member => member.DisplayName);
        }

        // 리그 클라이언트가 리플을 저장할 때 쓰는 기본 파일명 형식: "{지역}-{게임ID}.rofl" (예: KR1-1234567890.rofl).
        // 파일명을 바꾸지 않았다면 여기서 Riot 매치ID 형태(지역_게임ID)를 추정할 수 있습니다.
        private static readonly Regex ReplayFilenamePattern = new(
            @"^([A-Za-z]{2,5}\d{0,2})[-_](\d{5,})(?:\.rofl)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static string? TryExtractMatchIdFromFilename(string filename)
        {
            var match = ReplayFilenamePattern.Match(filename.Trim());
            if (!match.Success)
            {
                return null;
            }

            var platform = match.Groups[1].Value.ToUpperInvariant();
            var gameId = match.Groups[2].Value;
            return $"{platform}_{gameId}";
        }

        // "모스트"류 순위 표시용 — 1/2/3위는 메달 이모지, 그 밑은 숫자.
        private static string GetRankLabel(int index) => index switch
        {
            0 => "👑",
            1 => "🥈",
            2 => "🥉",
            _ => $"{index + 1}.",
        };

        // /티어픽에서 챔피언 한 줄에 "누가 몇 % 지분으로 플레이했는지"를 만들어줍니다. 표시할 게 없으면 빈 문자열.
        private static string FormatPlayerShares(IReadOnlyList<ChampionPlayerRow> players, Dictionary<ulong, string> nameByUserId)
        {
            if (players.Count == 0)
            {
                return string.Empty;
            }

            // players는 이미 판수(games) 내림차순으로 정렬돼서 넘어옵니다 (SQL ORDER BY games DESC).
            var parts = players.Select(player =>
            {
                var name = EscapeMarkdown(nameByUserId.GetValueOrDefault(player.DiscordUserId, "알 수 없음"));
                var winRate = Math.Round(player.Wins * 100.0 / player.Games);
                return $"{name} {player.Games}판 · 승률{winRate:F0}%";
            });

            return string.Join(", ", parts);
        }

        private static string GetKoreanPosition(string position) => position switch
        {
            "TOP" => "탑",
            "JUNGLE" => "정글",
            "MIDDLE" => "미드",
            "BOTTOM" => "원딜",
            "UTILITY" => "서폿",
            _ => "기타",
        };

        // /아재전적 줄 앞에 붙는 라인 아이콘 — 갑옷(탑)/풀(정글)/마법사(미드)/화살(원딜)/방패(서폿)
        private static string GetPositionIcon(string position) => position switch
        {
            "TOP" => "🪓",
            "JUNGLE" => "🌿",
            "MIDDLE" => "🧙",
            "BOTTOM" => "🏹",
            "UTILITY" => "🛡️",
            _ => "❔",
        };

        // GetTierRank는 2026-08-20 리팩토링 2단계에서 BanPickRecommendationService로 이관됨(사용처가 그 서비스뿐이었음).
        // GetPositionOrder는 같은 날 Services/PositionOrder.cs로 이관됨(모듈+서비스 양쪽에서 쓰여서 공용 유틸로 뺌).

        // TryParseRiotId/CanManageMembers/EscapeMarkdown은 2026-08-20 리팩토링 1단계에서
        // Services/RiotIdParser.cs, PermissionChecker.cs, MarkdownFormatter.cs로 이관됨
        // (AtoZModule.cs에 있던 완전히 동일한 복붙 코드와 통합). 위쪽 using static 참고.
    }
}

// [AI-GENERATED] 이 코드는 AI(Claude)가 생성했습니다.
// Source: Claude (Claude Code / Cowork)
// Date: 2026-08-20
// Reviewer: (박정훈)
// Review: 리팩토링 1단계 — 여러 Module/Service 파일에 각자 독립적으로 하드코딩돼 있던 상수를
// 한 곳으로 모음(외부 코드리뷰에서 지적된 "매직 넘버 산재" 문제). 예전엔 FlexQueueId가 4개 파일,
// MinSampleSize가 2개 파일, RiotApiDelay가 2개 파일에 각각 따로 적혀 있어서, 값을 하나 바꾸면
// 나머지를 깜빡하고 안 바꿀 위험이 있었습니다.
//
// 각 파일은 `using static LolHelperBot.Services.ClanConstants;`로 이 값들을 그대로
// 가져다 쓰므로(이름은 예전 로컬 상수와 동일하게 유지) 호출부 코드는 안 바뀌고, 값의 출처만
// 여기 하나로 합쳐집니다.

namespace LolHelperBot.Services;

public static class ClanConstants
{
    /// <summary>Riot 자유 랭크(Flex) 큐 ID. 우리 클랜 통계는 전부 이 큐만 봅니다.</summary>
    public const int FlexQueueId = 440;

    /// <summary>.rofl로 등록한 내전을 자유 랭크와 분리하는 내부 큐 ID.</summary>
    public const int InternalGameQueueId = 0;

    /// <summary>승률/모스트/워스트 계열 통계에서 후보로 삼는 최소 판수 (5판 이하는 표본 부족으로 제외).</summary>
    public const int MinSampleSize = 6;

    /// <summary>메타픽에 붙는 AZ 자랭 승률을 신뢰 표본으로 판정하는 최소 판수.</summary>
    public const int MetaPickMinSampleSize = 3;

    /// <summary>Riot API 요청 한도를 지키기 위한 호출 간 최소 대기 시간.</summary>
    public static readonly TimeSpan RiotApiDelay = TimeSpan.FromMilliseconds(1200);

    /// <summary>
    /// `/atoz 전적수집`이 저장하는 매치의 하한선(2026-08-01 00:00 KST). 이보다 오래된 경기는
    /// `최근경기수`를 아무리 크게 줘도(예: 300) 저장하지 않습니다 — 그 이전 데이터는 API 키
    /// 교체 이력 등으로 정합성 문제가 있었던 적이 있어(CHANGELOG 참고), 사용자 요청으로
    /// 2026-08-22부터 수집 범위를 이 날짜 이후로 제한했습니다. 이미 저장된 그 이전 데이터는
    /// 안 건드립니다.
    /// </summary>
    public static readonly DateTimeOffset MatchCollectionCutoffUtc =
        new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(9)).ToUniversalTime();
}

#nullable enable
using SkiaSharp;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Tools;

namespace TwitchDownloaderCore.Chat.Timeline
{
    public readonly record struct StreamerIdentity(
        string? Name,
        string? Login,
        int Id);

    public readonly record struct VideoTimelineBounds(
        string? Id,
        string? Title,
        DateTime CreatedAt,
        double StartSeconds,
        double EndSeconds,
        double LengthSeconds);

    public readonly record struct TimelineBadge(
        string Id,
        string Version);

    public readonly record struct TimelineFragment(
        string Text,
        string? EmoticonId);

    public sealed record TimelineMessageContent(
        string Body,
        IReadOnlyList<TimelineFragment> Fragments,
        int EmoteCount,
        int BitsSpent)
    {
        public static readonly TimelineMessageContent Deleted = new(
            "<message deleted>",
            [new TimelineFragment("<message deleted>", null)],
            EmoteCount: 0,
            BitsSpent: 0);
    }

    public sealed record TimelineAuthor(
        string Id,
        string? Login,
        string DisplayName,
        string? RawColorHex,
        string? AvatarUrl,
        IReadOnlyList<TimelineBadge> Badges);

    public sealed record TimelineReplyInfo(
        string? ParentMsgId,
        string? ParentUserLogin,
        string? ParentDisplayName,
        string? ParentMsgBody);

    public sealed record TimelineHypeChatInfo(
        double EffectiveDecimalAmount,
        string Currency,
        string Level);

    public sealed record TimelinePowerUpInfo(
        string? Type,
        string? EmoteId);

    public enum TimelineNoticeKind
    {
        None = 0,
        CompactSystemNotice = 1,
        AccentedSystemNotice = 2,
        Announcement = 3,
        ExtendedUserNotice = 4,
        LegacyHighlight = 5
    }

    public sealed record TimelineNoticeInfo(
        TimelineNoticeKind Kind,
        HighlightType IconType,
        string? SystemText = null,
        string? NoticeType = null,
        SKColor? CustomAccentColor = null,
        TimelineMessageContent? CustomUserContent = null,
        string? SubPlan = null,
        int CumulativeMonths = 0,
        int StreakMonths = 0,
        int MilestoneValue = 0,
        int MassGiftCount = 0,
        int SenderTotalGifts = 0,
        string? RecipientDisplayName = null,
        IReadOnlyDictionary<string, string>? NoticeParams = null)
    {
        public static readonly TimelineNoticeInfo Regular = new(TimelineNoticeKind.None, HighlightType.None);
    }

    /// <summary>
    /// Immutable render-ready representation of a single chat message or synthesized broadcast notice.
    /// Pre-splits custom subscription/streak lines and strips redundant reply mentions once during compilation
    /// so parallel segment workers never clone or mutate <c>ChatRoot</c> objects during rendering.
    /// </summary>
    public sealed record TimelineMessage(
        string Id,
        DateTime CreatedAt,
        double AppearSeconds,
        double? ModeratedAtSeconds,
        bool IsSynthetic,
        TimelineAuthor Author,
        TimelineMessageContent Content,
        TimelineNoticeInfo Notice,
        TimelineReplyInfo? Reply = null,
        TimelineHypeChatInfo? HypeChat = null,
        TimelinePowerUpInfo? PowerUp = null,
        string? SharedChatSourceRoomId = null,
        bool IsFirstMessage = false,
        bool IsReturningChatter = false,
        bool IsChannelPointHighlight = false);

    public sealed record StickyPredictionOutcome(
        string Id,
        string Title,
        string Color,
        long TotalPoints,
        int TotalUsers);

    public sealed record StickyPollChoice(
        string ChoiceId,
        string Title,
        int TotalVoters);

    public abstract record StickyOverlayEvent(
        double OffsetSeconds,
        bool CarriedOver);

    public sealed record PredictionOverlayEvent(
        double OffsetSeconds,
        bool CarriedOver,
        string PredictionId,
        string Title,
        string Status,
        int WindowSeconds,
        string? WinningOutcomeId,
        IReadOnlyList<StickyPredictionOutcome> Outcomes)
        : StickyOverlayEvent(OffsetSeconds, CarriedOver);

    public sealed record PollOverlayEvent(
        double OffsetSeconds,
        bool CarriedOver,
        string PollId,
        string PubsubType,
        string Status,
        string Title,
        int DurationSeconds,
        int TotalVoters,
        IReadOnlyList<StickyPollChoice> Choices)
        : StickyOverlayEvent(OffsetSeconds, CarriedOver);

    public sealed record PinnedChatOverlayEvent(
        double OffsetSeconds,
        bool CarriedOver,
        string Action,
        string PinId,
        string Type,
        string? RawMessageText,
        string? RawSenderDisplayName,
        string? RawSenderColor,
        string PinnedByDisplayName,
        long StartsAt,
        long EndsAt)
        : StickyOverlayEvent(OffsetSeconds, CarriedOver);

    public sealed record HypeTrainOverlayEvent(
        double OffsetSeconds,
        bool CarriedOver,
        string PubsubType,
        int? RawLevel,
        int? RawGoal,
        int? RawTotal,
        int? RawProgress)
        : StickyOverlayEvent(OffsetSeconds, CarriedOver);

    /// <summary>
    /// Immutable canonical timeline compiled from <c>ChatRoot</c> + <see cref="ChatRenderOptions"/>.
    /// Shared safely across all parallel segment compositors without locking or defensive cloning.
    /// </summary>
    public sealed class CanonicalTimeline
    {
        public StreamerIdentity Streamer { get; }
        public VideoTimelineBounds Video { get; }
        public IReadOnlyList<TimelineMessage> Messages { get; }
        public IReadOnlyList<StickyOverlayEvent> StickyEvents { get; }

        public CanonicalTimeline(
            StreamerIdentity streamer,
            VideoTimelineBounds video,
            IReadOnlyList<TimelineMessage> messages,
            IReadOnlyList<StickyOverlayEvent> stickyEvents)
        {
            Streamer = streamer;
            Video = video;
            Messages = messages;
            StickyEvents = stickyEvents;
        }

        public (int StartTick, int EndTick, int TotalTicks) GetTickRange(ChatRenderOptions options)
        {
            int framerate = Math.Max(1, options.Framerate);
            int startTick = options.StartOverride >= 0
                ? options.StartOverride * framerate
                : (int)Math.Floor(Video.StartSeconds) * framerate;
            int endTick = options.EndOverride >= 0
                ? options.EndOverride * framerate
                : (int)Math.Ceiling(Video.EndSeconds * framerate);
            int totalTicks = Math.Max(0, endTick - startTick);
            return (startTick, startTick + totalTicks, totalTicks);
        }
    }
}

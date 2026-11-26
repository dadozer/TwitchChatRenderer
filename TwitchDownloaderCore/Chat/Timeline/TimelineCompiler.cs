#nullable enable
using SkiaSharp;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using TwitchDownloaderCore.Extensions;
using TwitchDownloaderCore.Interfaces;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Tools;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.Chat.Timeline
{
    /// <summary>
    /// Pure compiler pass that transforms deserialized <see cref="ChatRoot"/> DTOs and <see cref="ChatRenderOptions"/>
    /// into an immutable <see cref="CanonicalTimeline"/> without mutating <see cref="ChatRoot"/> or cloning <see cref="Comment"/>s.
    /// </summary>
    public static class TimelineCompiler
    {
        private static readonly SKColor Purple = new(0xFF7B2CF2);

        private sealed class MutableTimelineEntry
        {
            public required string Id { get; init; }
            public required DateTime CreatedAt { get; init; }
            public double AppearSeconds { get; set; }
            public double? ModeratedAtSeconds { get; set; }
            public bool IsSynthetic { get; init; }
            public required TimelineAuthor Author { get; init; }
            public required TimelineMessageContent Content { get; init; }
            public required TimelineNoticeInfo Notice { get; init; }
            public TimelineReplyInfo? Reply { get; init; }
            public TimelineHypeChatInfo? HypeChat { get; init; }
            public TimelinePowerUpInfo? PowerUp { get; init; }
            public string? SharedChatSourceRoomId { get; init; }
            public bool IsFirstMessage { get; init; }
            public bool IsReturningChatter { get; init; }
            public bool IsChannelPointHighlight { get; init; }

            public TimelineMessage Freeze() => new(
                Id,
                CreatedAt,
                AppearSeconds,
                ModeratedAtSeconds,
                IsSynthetic,
                Author,
                Content,
                Notice,
                Reply,
                HypeChat,
                PowerUp,
                SharedChatSourceRoomId,
                IsFirstMessage,
                IsReturningChatter,
                IsChannelPointHighlight);
        }

        public static CanonicalTimeline Compile(ChatRoot root, ChatRenderOptions options, ITaskLogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(root);
            ArgumentNullException.ThrowIfNull(options);

            var streamer = root.streamer is not null
                ? new StreamerIdentity(root.streamer.name, root.streamer.login, root.streamer.id)
                : new StreamerIdentity(null, null, 0);

            var videoBounds = ResolveVideoBounds(root);

            var ignoredUsers = options.IgnoreUsersArray is { Length: > 0 }
                ? new HashSet<string>(options.IgnoreUsersArray, StringComparer.InvariantCultureIgnoreCase)
                : null;

            Regex? bannedWordsRegex = null;
            if (options.BannedWordsArray is { Length: > 0 })
            {
                var escaped = string.Join('|', options.BannedWordsArray.Select(Regex.Escape));
                bannedWordsRegex = new Regex(
                    @$"(?<=\b|[\d\p{{Pc}}]){escaped}(?=\b|[\d\p{{Pc}}])",
                    RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            var realEntries = new List<MutableTimelineEntry>(root.comments?.Count ?? 0);
            if (root.comments is { Count: > 0 })
            {
                for (int i = 0; i < root.comments.Count; i++)
                {
                    var comment = root.comments[i];
                    if (comment is null)
                    {
                        continue;
                    }

                    if (IsRestricted(comment, ignoredUsers, bannedWordsRegex))
                    {
                        continue;
                    }

                    var compiled = TryCompileComment(comment, options, logger);
                    if (compiled is not null)
                    {
                        realEntries.Add(compiled);
                    }
                }
            }

            EnsureSortedChronologically(realEntries);

            var stickyEvents = new List<StickyOverlayEvent>();
            var syntheticEntries = new List<MutableTimelineEntry>();

            if (root.events is { Count: > 0 })
            {
                ProcessBroadcastEvents(
                    root.events,
                    realEntries,
                    syntheticEntries,
                    stickyEvents,
                    options,
                    logger);
            }

            if (options.ModerationMode == ModerationDisplayMode.Hide && realEntries.Count > 0)
            {
                realEntries.RemoveAll(static e => e.ModeratedAtSeconds.HasValue);
            }

            if (syntheticEntries.Count > 0)
            {
                realEntries.AddRange(syntheticEntries);
                EnsureSortedChronologically(realEntries);
            }

            if (options.DisperseCommentOffsets && realEntries.Count >= 2)
            {
                DisperseOffsets(realEntries);
            }

            FloorOffsetsSinglePass(realEntries, options.UpdateRate);

            var frozenMessages = new TimelineMessage[realEntries.Count];
            for (int i = 0; i < realEntries.Count; i++)
            {
                frozenMessages[i] = realEntries[i].Freeze();
            }

            return new CanonicalTimeline(streamer, videoBounds, frozenMessages, stickyEvents);
        }

        private static VideoTimelineBounds ResolveVideoBounds(ChatRoot root)
        {
            if (root.video is not null)
            {
                double endSec = root.video.end <= 0 && root.video.length > 0
                    ? root.video.length
                    : root.video.end;

                return new VideoTimelineBounds(
                    root.video.id,
                    root.video.title,
                    root.video.created_at,
                    root.video.start,
                    endSec,
                    root.video.length);
            }

            double minOffset = double.MaxValue;
            double maxOffset = 0;

            if (root.comments is { Count: > 0 })
            {
                foreach (var c in root.comments)
                {
                    if (c is null) continue;
                    if (c.content_offset_seconds < minOffset) minOffset = c.content_offset_seconds;
                    if (c.content_offset_seconds > maxOffset) maxOffset = c.content_offset_seconds;
                }
            }

            if (root.events is { Count: > 0 })
            {
                foreach (var ev in root.events)
                {
                    if (ev is null) continue;
                    if (ev.content_offset_seconds < minOffset) minOffset = ev.content_offset_seconds;
                    if (ev.content_offset_seconds > maxOffset) maxOffset = ev.content_offset_seconds;
                }
            }

            if (minOffset == double.MaxValue)
            {
                minOffset = 0;
            }

            double start = Math.Max(0, Math.Floor(minOffset));
            double end = Math.Max(start, Math.Ceiling(maxOffset));
            return new VideoTimelineBounds(null, null, default, start, end, -1);
        }

        private static bool IsRestricted(Comment comment, HashSet<string>? ignoredUsers, Regex? bannedWordsRegex)
        {
            if (ignoredUsers is null && bannedWordsRegex is null)
            {
                return false;
            }

            var commenter = comment.commenter;
            var loginName = commenter?.name;
            var displayName = commenter?.display_name;
            var body = comment.message?.body;

            if (ignoredUsers is not null)
            {
                if (!string.IsNullOrEmpty(loginName) && ignoredUsers.Contains(loginName))
                {
                    return true;
                }

                if (!string.IsNullOrEmpty(displayName) && ContainsNonAscii(displayName) && ignoredUsers.Contains(displayName))
                {
                    return true;
                }
            }

            if (bannedWordsRegex is not null && !string.IsNullOrEmpty(body) && bannedWordsRegex.IsMatch(body))
            {
                return true;
            }

            return false;
        }

        private static bool ContainsNonAscii(string value)
        {
            foreach (char ch in value)
            {
                if (ch > 127)
                {
                    return true;
                }
            }
            return false;
        }

        private static MutableTimelineEntry? TryCompileComment(Comment comment, ChatRenderOptions options, ITaskLogger? logger)
        {
            if (comment.message is null)
            {
                return null;
            }

            int bitsSpent = comment.message.bits_spent;
            if (bitsSpent == 0)
            {
                if (comment.bits_spent > 0)
                {
                    bitsSpent = comment.bits_spent;
                }
                else if (comment.event_data is { bits_spent: > 0 })
                {
                    bitsSpent = comment.event_data.bits_spent;
                }
            }

            string? msgId = comment.event_data?.notice_type
                ?? comment.event_data?.msg_id
                ?? (comment.user_notice_params != null && comment.user_notice_params.TryGetValue("msg-id", out var rawId) ? rawId : null)
                ?? comment.message.user_notice_params?.msg_id;

            if (!string.IsNullOrEmpty(msgId))
            {
                if (msgId == "announcement" && !options.RenderAnnouncements)
                {
                    return null;
                }

                if (msgId == "raid" && !options.RenderRaidNotices)
                {
                    return null;
                }

                if (msgId is "sub" or "resub" or "subgift" or "submysterygift" or "viewermilestone" or "primepaidupgrade" or "giftpaidupgrade" or "anongiftpaidupgrade" or "standardpayforward" or "communitypayforward"
                    && !options.SubMessages)
                {
                    return null;
                }
            }

            bool isExtendedUserNotice = comment.event_type == "usernotice"
                || !string.IsNullOrEmpty(comment.event_data?.notice_type)
                || (comment.user_notice_params != null
                    && comment.user_notice_params.TryGetValue("msg-id", out var extNoticeId)
                    && extNoticeId is not "highlighted-message" and not "");

            bool isChannelPointHighlight = comment.event_data is { is_highlighted: true }
                || string.Equals(msgId, "highlighted-message", StringComparison.OrdinalIgnoreCase);

            if (!isExtendedUserNotice && comment.message.user_notice_params?.msg_id is { } legacyNoticeId)
            {
                if (legacyNoticeId is not "highlighted-message" and not "sub" and not "resub" and not "subgift" and not "announcement" and not "raid" and not "viewermilestone" and not "submysterygift" and not "")
                {
                    logger?.LogVerbose($"{comment._id} has invalid {nameof(comment.message.user_notice_params)}: {legacyNoticeId}.");
                    return null;
                }

                if (legacyNoticeId == "highlighted-message")
                {
                    isChannelPointHighlight = true;
                }
            }

            // support synthetic pre-flagged comments if passed via unit tests
            if (comment.IsSyntheticNotice)
            {
                string synthBody = comment.message.body ?? "";
                var synthAuthor = BuildAuthor(comment);
                var synthContent = new TimelineMessageContent(synthBody, [new TimelineFragment(synthBody, null)], 0, 0);
                var synthNotice = comment.IsSyntheticCompactNotice
                    ? new TimelineNoticeInfo(TimelineNoticeKind.CompactSystemNotice, HighlightType.None, SystemText: synthBody)
                    : new TimelineNoticeInfo(
                        TimelineNoticeKind.AccentedSystemNotice,
                        HighlightType.None,
                        SystemText: synthBody,
                        CustomAccentColor: new SKColor(comment.SyntheticAccentColor ?? 0xFF9146FF));

                return new MutableTimelineEntry
                {
                    Id = comment._id ?? "",
                    CreatedAt = comment.created_at,
                    AppearSeconds = comment.content_offset_seconds,
                    ModeratedAtSeconds = comment.ModeratedAtSeconds,
                    IsSynthetic = true,
                    Author = synthAuthor,
                    Content = synthContent,
                    Notice = synthNotice
                };
            }

            if (comment.commenter is null)
            {
                logger?.LogVerbose($"{comment._id} lacks fragments and/or a commenter.");
                return null;
            }

            List<TimelineFragment> rawFragments;
            if (comment.message.fragments is { Count: > 0 })
            {
                rawFragments = new List<TimelineFragment>(comment.message.fragments.Count);
                for (int i = 0; i < comment.message.fragments.Count; i++)
                {
                    var f = comment.message.fragments[i];
                    if (f is null) continue;
                    rawFragments.Add(new TimelineFragment(f.text ?? "", f.emoticon?.emoticon_id));
                }
            }
            else if (comment.message.body is not null)
            {
                rawFragments = [new TimelineFragment(comment.message.body, null)];
            }
            else
            {
                logger?.LogVerbose($"{comment._id} lacks fragments and/or a commenter.");
                return null;
            }

            string rawBody = comment.message.body ?? string.Concat(rawFragments.Select(static f => f.Text));
            int emoteCount = comment.message.emoticons?.Count ?? 0;
            if (emoteCount == 0)
            {
                for (int i = 0; i < rawFragments.Count; i++)
                {
                    if (!string.IsNullOrEmpty(rawFragments[i].EmoticonId))
                    {
                        emoteCount++;
                    }
                }
            }

            TimelineReplyInfo? replyInfo = null;
            if (comment.event_data?.reply is { } rawReply)
            {
                replyInfo = new TimelineReplyInfo(
                    rawReply.parent_msg_id,
                    rawReply.parent_user_login,
                    rawReply.parent_display_name,
                    rawReply.parent_msg_body);
            }

            var baseContent = new TimelineMessageContent(rawBody, rawFragments, emoteCount, bitsSpent);
            if (options.RenderReplies && replyInfo is not null)
            {
                baseContent = StripRedundantReplyLeadingMention(baseContent, replyInfo);
            }

            bool isFirstMessage = comment.event_data is { is_first_message: true }
                || (comment.user_notice_params != null && comment.user_notice_params.TryGetValue("first-msg", out var fm) && fm is "1" or "true");
            bool isReturningChatter = comment.event_data is { is_returning_chatter: true }
                || (comment.user_notice_params != null && comment.user_notice_params.TryGetValue("returning-chatter", out var rc) && rc is "1" or "true");

            TimelineHypeChatInfo? hypeChat = null;
            if (comment.event_data?.hype_chat is { } rawHype)
            {
                hypeChat = new TimelineHypeChatInfo(
                    rawHype.EffectiveDecimalAmount,
                    rawHype.currency ?? "USD",
                    rawHype.level ?? "ONE");
            }

            TimelinePowerUpInfo? powerUp = null;
            if (comment.event_data?.power_up is { } rawPower)
            {
                powerUp = new TimelinePowerUpInfo(rawPower.type, rawPower.emote_id);
            }

            string? sharedChatSourceRoomId = comment.event_data?.shared_chat?.source_room_id;
            var author = BuildAuthor(comment);

            var notice = CompileNoticeInfo(
                comment,
                author,
                ref baseContent,
                isChannelPointHighlight,
                options,
                out bool dropComment);

            if (dropComment)
            {
                return null;
            }

            return new MutableTimelineEntry
            {
                Id = comment._id ?? "",
                CreatedAt = comment.created_at,
                AppearSeconds = comment.content_offset_seconds,
                ModeratedAtSeconds = comment.ModeratedAtSeconds,
                IsSynthetic = false,
                Author = author,
                Content = baseContent,
                Notice = notice,
                Reply = replyInfo,
                HypeChat = hypeChat,
                PowerUp = powerUp,
                SharedChatSourceRoomId = sharedChatSourceRoomId,
                IsFirstMessage = isFirstMessage,
                IsReturningChatter = isReturningChatter,
                IsChannelPointHighlight = isChannelPointHighlight
            };
        }

        private static TimelineAuthor BuildAuthor(Comment comment)
        {
            var commenter = comment.commenter;
            var badges = Array.Empty<TimelineBadge>();
            if (comment.message?.user_badges is { Count: > 0 } rawBadges)
            {
                var list = new List<TimelineBadge>(rawBadges.Count);
                for (int i = 0; i < rawBadges.Count; i++)
                {
                    var b = rawBadges[i];
                    if (b is not null && !string.IsNullOrEmpty(b._id))
                    {
                        list.Add(new TimelineBadge(b._id, b.version ?? ""));
                    }
                }
                if (list.Count > 0)
                {
                    badges = list.ToArray();
                }
            }

            return new TimelineAuthor(
                commenter?._id ?? "",
                commenter?.name,
                commenter?.display_name ?? commenter?.name ?? "User",
                comment.message?.user_color,
                commenter?.logo,
                badges);
        }

        private static Dictionary<string, string>? MergeNoticeParams(Comment comment)
        {
            if (comment.user_notice_params is not { Count: > 0 } && comment.event_data?.notice_params is not { Count: > 0 })
            {
                return null;
            }

            var merged = new Dictionary<string, string>(StringComparer.Ordinal);
            if (comment.event_data?.notice_params is { Count: > 0 } edParams)
            {
                foreach (var kvp in edParams)
                {
                    if (!string.IsNullOrEmpty(kvp.Value))
                    {
                        merged[kvp.Key] = kvp.Value;
                    }
                }
            }

            if (comment.user_notice_params is { Count: > 0 } topParams)
            {
                foreach (var kvp in topParams)
                {
                    if (!string.IsNullOrEmpty(kvp.Value))
                    {
                        merged[kvp.Key] = kvp.Value;
                    }
                }
            }

            return merged.Count > 0 ? merged : null;
        }

        private static TimelineNoticeInfo CompileNoticeInfo(
            Comment comment,
            TimelineAuthor author,
            ref TimelineMessageContent content,
            bool isChannelPointHighlight,
            ChatRenderOptions options,
            out bool dropComment)
        {
            dropComment = false;

            string? noticeType = comment.event_data?.notice_type
                ?? (comment.user_notice_params != null && comment.user_notice_params.TryGetValue("msg-id", out var rawNotice) ? rawNotice : null);

            if (comment.event_type == "usernotice" || !string.IsNullOrEmpty(noticeType))
            {
                string? sysMsg = comment.event_data?.system_message
                    ?? (comment.user_notice_params != null && comment.user_notice_params.TryGetValue("system-msg", out var rawSys) ? rawSys.Replace(@"\s", " ") : null);
                string? userMsg = comment.event_data?.user_message;

                if (noticeType == "announcement")
                {
                    return new TimelineNoticeInfo(
                        TimelineNoticeKind.Announcement,
                        HighlightType.None,
                        NoticeType: "announcement",
                        CustomAccentColor: GetAnnouncementAccentColor(comment.event_data?.announcement_color));
                }

                if (comment.event_data != null && !string.IsNullOrEmpty(sysMsg))
                {
                    if (!options.SubMessages && noticeType is "sub" or "resub" or "subgift" or "submysterygift" or "viewermilestone")
                    {
                        dropComment = true;
                        return TimelineNoticeInfo.Regular;
                    }

                    SKColor accent = noticeType == "viewermilestone" ? new SKColor(0xFF00C7AC) : Purple;
                    var iconType = noticeType switch
                    {
                        "sub" or "resub" => comment.event_data.sub_plan == "Prime" ? HighlightType.SubscribedPrime : HighlightType.SubscribedTier,
                        "subgift" => HighlightType.GiftedSingle,
                        "submysterygift" => HighlightType.GiftedMany,
                        "viewermilestone" => HighlightType.WatchStreak,
                        "raid" => HighlightType.Raid,
                        _ => HighlightType.SubscribedTier
                    };

                    bool hasCustomUserMessage = !string.IsNullOrWhiteSpace(userMsg)
                        && !string.Equals(userMsg.Trim(), sysMsg.Trim(), StringComparison.OrdinalIgnoreCase);

                    return new TimelineNoticeInfo(
                        TimelineNoticeKind.ExtendedUserNotice,
                        iconType,
                        SystemText: sysMsg,
                        NoticeType: noticeType,
                        CustomAccentColor: accent,
                        CustomUserContent: hasCustomUserMessage ? content : null,
                        SubPlan: comment.event_data.sub_plan,
                        CumulativeMonths: comment.event_data.cumulative_months ?? 0,
                        StreakMonths: comment.event_data.streak_months ?? 0,
                        MilestoneValue: comment.event_data.milestone_value ?? 0,
                        MassGiftCount: comment.event_data.mass_gift_count ?? 0,
                        SenderTotalGifts: comment.event_data.sender_total_gifts ?? 0,
                        RecipientDisplayName: comment.event_data.recipient_display_name ?? comment.event_data.recipient_user_name,
                        NoticeParams: MergeNoticeParams(comment));
                }
            }

            HighlightType legacyType = isChannelPointHighlight
                ? HighlightType.ChannelPointHighlight
                : HighlightIcons.GetHighlightType(comment);

            if (legacyType is HighlightType.None)
            {
                return TimelineNoticeInfo.Regular;
            }

            if (legacyType is not HighlightType.ChannelPointHighlight && !options.SubMessages)
            {
                dropComment = true;
                return TimelineNoticeInfo.Regular;
            }

            if (legacyType is HighlightType.ChannelPointHighlight)
            {
                return new TimelineNoticeInfo(TimelineNoticeKind.LegacyHighlight, HighlightType.ChannelPointHighlight);
            }

            var noticeParams = MergeNoticeParams(comment);

            switch (legacyType)
            {
                case HighlightType.SubscribedTier:
                case HighlightType.SubscribedPrime:
                {
                    var stripped = StripLeadingDisplayName(content, author.DisplayName);
                    var (sysPart, customPart) = SplitContentOnDelimiter(stripped, isSubSplit: true);
                    return new TimelineNoticeInfo(
                        TimelineNoticeKind.LegacyHighlight,
                        legacyType,
                        SystemText: sysPart,
                        CustomUserContent: customPart,
                        SubPlan: comment.event_data?.sub_plan,
                        CumulativeMonths: comment.event_data?.cumulative_months ?? 0,
                        StreakMonths: comment.event_data?.streak_months ?? 0,
                        NoticeParams: noticeParams);
                }

                case HighlightType.WatchStreak:
                {
                    var stripped = StripLeadingDisplayName(content, author.DisplayName);
                    var (sysPart, customPart) = SplitContentOnDelimiter(stripped, isSubSplit: false);
                    return new TimelineNoticeInfo(
                        TimelineNoticeKind.LegacyHighlight,
                        legacyType,
                        SystemText: sysPart,
                        CustomUserContent: customPart,
                        MilestoneValue: comment.event_data?.milestone_value ?? 0,
                        NoticeParams: noticeParams);
                }

                case HighlightType.CharityDonation:
                {
                    content = StripCharityPrefix(content, author.DisplayName);
                    return new TimelineNoticeInfo(
                        TimelineNoticeKind.LegacyHighlight,
                        legacyType,
                        NoticeParams: noticeParams);
                }

                case HighlightType.BitBadgeTierNotification:
                {
                    if (content.Fragments.Count == 1)
                    {
                        var bitsBadgeVersion = author.Badges.FirstOrDefault(static x => x.Id == "bits").Version;
                        string newBody = !string.IsNullOrEmpty(bitsBadgeVersion)
                            ? (bitsBadgeVersion.Length > 3
                                ? $"just earned a new {bitsBadgeVersion.AsSpan(0, bitsBadgeVersion.Length - 3)}K Bits badge!"
                                : $"just earned a new {bitsBadgeVersion} Bits badge!")
                            : "just earned a new Bits badge!";
                        content = new TimelineMessageContent(newBody, [new TimelineFragment(newBody, null)], 0, content.BitsSpent);
                    }
                    return new TimelineNoticeInfo(
                        TimelineNoticeKind.LegacyHighlight,
                        legacyType,
                        NoticeParams: noticeParams);
                }

                default:
                    return new TimelineNoticeInfo(
                        TimelineNoticeKind.LegacyHighlight,
                        legacyType,
                        SystemText: content.Body,
                        SubPlan: comment.event_data?.sub_plan,
                        MassGiftCount: comment.event_data?.mass_gift_count ?? 0,
                        SenderTotalGifts: comment.event_data?.sender_total_gifts ?? 0,
                        RecipientDisplayName: comment.event_data?.recipient_display_name ?? comment.event_data?.recipient_user_name,
                        NoticeParams: noticeParams);
            }
        }

        private static TimelineMessageContent StripLeadingDisplayName(TimelineMessageContent content, string displayName)
        {
            if (string.IsNullOrEmpty(displayName)
                || content.Body.Length <= displayName.Length + 1
                || !content.Body.StartsWith(displayName, StringComparison.OrdinalIgnoreCase)
                || content.Fragments.Count == 0)
            {
                return content;
            }

            string trimmedBody = content.Body[(displayName.Length + 1)..];
            var newFrags = new List<TimelineFragment>(content.Fragments.Count);
            string firstText = content.Fragments[0].Text;

            if (firstText.Equals(displayName, StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 1; i < content.Fragments.Count; i++)
                {
                    newFrags.Add(content.Fragments[i]);
                }
            }
            else if (firstText.Length > displayName.Length + 1)
            {
                newFrags.Add(content.Fragments[0] with { Text = firstText[(displayName.Length + 1)..] });
                for (int i = 1; i < content.Fragments.Count; i++)
                {
                    newFrags.Add(content.Fragments[i]);
                }
            }
            else
            {
                for (int i = 0; i < content.Fragments.Count; i++)
                {
                    newFrags.Add(content.Fragments[i]);
                }
            }

            return content with { Body = trimmedBody, Fragments = newFrags };
        }

        private static (string SystemText, TimelineMessageContent? CustomContent) SplitContentOnDelimiter(TimelineMessageContent content, bool isSubSplit)
        {
            if (content.Fragments.Count == 0)
            {
                return (content.Body, null);
            }

            string firstText = content.Fragments[0].Text;
            string delim = !isSubSplit
                || firstText.Contains("! ", StringComparison.Ordinal)
                || firstText.Contains("They've subscribed", StringComparison.Ordinal)
                || firstText.Contains("converted from", StringComparison.Ordinal)
                    ? "! "
                    : ". ";

            int splitPosition = firstText.IndexOf(delim, StringComparison.Ordinal);
            if (splitPosition == -1 || (splitPosition + delim.Length >= firstText.Length && content.Fragments.Count == 1))
            {
                return (content.Body, null);
            }

            splitPosition += delim.Length - 1;
            string sysText = firstText[..(splitPosition + 1)];
            string customBody = content.Body.Length > splitPosition + 1 ? content.Body[(splitPosition + 1)..] : "";
            string remFirst = firstText[(splitPosition + 1)..];

            var customFrags = new List<TimelineFragment>(content.Fragments.Count);
            if (remFirst.Length > 0)
            {
                customFrags.Add(content.Fragments[0] with { Text = remFirst });
            }

            for (int i = 1; i < content.Fragments.Count; i++)
            {
                customFrags.Add(content.Fragments[i]);
            }

            if (string.IsNullOrWhiteSpace(customBody) && customFrags.Count == 0)
            {
                return (sysText, null);
            }

            return (sysText, new TimelineMessageContent(customBody, customFrags, content.EmoteCount, content.BitsSpent));
        }

        private static TimelineMessageContent StripCharityPrefix(TimelineMessageContent content, string displayName)
        {
            int prefixLen = (displayName?.Length ?? 0) + 2;
            if (string.IsNullOrEmpty(displayName)
                || content.Body.Length < prefixLen
                || !content.Body.StartsWith(displayName, StringComparison.OrdinalIgnoreCase)
                || content.Body.AsSpan(displayName.Length, 2) is not ": ")
            {
                return content;
            }

            string newBody = content.Body[prefixLen..];
            var newFrags = new List<TimelineFragment>(content.Fragments.Count);
            if (content.Fragments.Count > 0)
            {
                string firstText = content.Fragments[0].Text;
                if (firstText.Equals(displayName, StringComparison.OrdinalIgnoreCase)
                    || firstText.Equals($"{displayName}:", StringComparison.OrdinalIgnoreCase)
                    || firstText.Equals($"{displayName}: ", StringComparison.OrdinalIgnoreCase))
                {
                    for (int i = 1; i < content.Fragments.Count; i++)
                    {
                        newFrags.Add(content.Fragments[i]);
                    }
                }
                else if (firstText.Length >= prefixLen
                    && firstText.StartsWith(displayName, StringComparison.OrdinalIgnoreCase)
                    && firstText.AsSpan(displayName.Length, 2) is ": ")
                {
                    newFrags.Add(content.Fragments[0] with { Text = firstText[prefixLen..] });
                    for (int i = 1; i < content.Fragments.Count; i++)
                    {
                        newFrags.Add(content.Fragments[i]);
                    }
                }
                else
                {
                    for (int i = 0; i < content.Fragments.Count; i++)
                    {
                        newFrags.Add(content.Fragments[i]);
                    }
                }
            }

            return content with { Body = newBody, Fragments = newFrags };
        }

        private static TimelineMessageContent StripRedundantReplyLeadingMention(TimelineMessageContent content, TimelineReplyInfo reply)
        {
            if (content.Fragments.Count == 0)
            {
                return content;
            }

            string firstText = content.Fragments[0].Text;
            if (string.IsNullOrEmpty(firstText) || firstText[0] != '@')
            {
                return content;
            }

            ReadOnlySpan<char> span = firstText.AsSpan();
            int matchLen = 0;

            if (!string.IsNullOrEmpty(reply.ParentDisplayName)
                && span.Length > reply.ParentDisplayName.Length + 1
                && span[1..].StartsWith(reply.ParentDisplayName, StringComparison.OrdinalIgnoreCase)
                && char.IsWhiteSpace(span[1 + reply.ParentDisplayName.Length]))
            {
                matchLen = 1 + reply.ParentDisplayName.Length + 1;
            }
            else if (!string.IsNullOrEmpty(reply.ParentUserLogin)
                && span.Length > reply.ParentUserLogin.Length + 1
                && span[1..].StartsWith(reply.ParentUserLogin, StringComparison.OrdinalIgnoreCase)
                && char.IsWhiteSpace(span[1 + reply.ParentUserLogin.Length]))
            {
                matchLen = 1 + reply.ParentUserLogin.Length + 1;
            }

            if (matchLen <= 0)
            {
                return content;
            }

            string remainingFirst = firstText[matchLen..].TrimStart();
            if (string.IsNullOrEmpty(remainingFirst) && content.Fragments.Count == 1)
            {
                return content;
            }

            string trimmedBody = !string.IsNullOrEmpty(content.Body) && content.Body.Length >= matchLen
                ? content.Body[matchLen..].TrimStart()
                : content.Body;

            var newFrags = new List<TimelineFragment>(content.Fragments.Count);
            if (!string.IsNullOrEmpty(remainingFirst))
            {
                newFrags.Add(content.Fragments[0] with { Text = remainingFirst });
            }
            for (int i = 1; i < content.Fragments.Count; i++)
            {
                newFrags.Add(content.Fragments[i]);
            }

            return content with { Body = trimmedBody, Fragments = newFrags };
        }

        private static void ProcessBroadcastEvents(
            List<ChatBroadcastEvent> rawEvents,
            List<MutableTimelineEntry> realEntries,
            List<MutableTimelineEntry> syntheticEntries,
            List<StickyOverlayEvent> stickyEvents,
            ChatRenderOptions options,
            ITaskLogger? logger)
        {
            var sortedEvents = new List<ChatBroadcastEvent>(rawEvents.Count);
            for (int i = 0; i < rawEvents.Count; i++)
            {
                if (rawEvents[i] is not null)
                {
                    sortedEvents.Add(rawEvents[i]);
                }
            }

            if (sortedEvents.Count == 0)
            {
                return;
            }

            sortedEvents.Sort(static (a, b) => a.content_offset_seconds.CompareTo(b.content_offset_seconds));

            var entriesById = new Dictionary<string, List<MutableTimelineEntry>>(StringComparer.Ordinal);
            var entriesByUserId = new Dictionary<string, List<MutableTimelineEntry>>(StringComparer.Ordinal);
            var entriesByUserLogin = new Dictionary<string, List<MutableTimelineEntry>>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in realEntries)
            {
                if (!string.IsNullOrEmpty(entry.Id))
                {
                    if (!entriesById.TryGetValue(entry.Id, out var list))
                    {
                        entriesById[entry.Id] = list = new List<MutableTimelineEntry>(1);
                    }
                    list.Add(entry);
                }

                if (!string.IsNullOrEmpty(entry.Author.Id))
                {
                    if (!entriesByUserId.TryGetValue(entry.Author.Id, out var list))
                    {
                        entriesByUserId[entry.Author.Id] = list = [];
                    }
                    list.Add(entry);
                }

                if (!string.IsNullOrEmpty(entry.Author.Login))
                {
                    if (!entriesByUserLogin.TryGetValue(entry.Author.Login, out var list))
                    {
                        entriesByUserLogin[entry.Author.Login] = list = [];
                    }
                    list.Add(entry);
                }
            }

            double earliestOffset = double.MaxValue;
            if (realEntries.Count > 0)
            {
                earliestOffset = Math.Min(earliestOffset, realEntries[0].AppearSeconds);
            }
            if (sortedEvents.Count > 0)
            {
                earliestOffset = Math.Min(earliestOffset, sortedEvents[0].content_offset_seconds);
            }
            if (earliestOffset == double.MaxValue)
            {
                earliestOffset = 0.0;
            }

            double initialBaselineCutoff = Math.Max(earliestOffset, options.StartOverride > 0 ? options.StartOverride : 0.0) + 2.0;
            bool hasSeenInitialRoomstate = false;

            bool? prevEmoteOnly = null;
            bool? prevSubsOnly = null;
            int? prevSlowSeconds = null;
            int? prevFollowersOnly = null;
            bool? prevR9k = null;

            var seenPredictions = new Dictionary<string, string>(StringComparer.Ordinal);
            var seenPolls = new Dictionary<string, string>(StringComparer.Ordinal);
            var seenPins = new HashSet<string>(StringComparer.Ordinal);
            int lastHypeTrainLevel = 0;
            bool hypeTrainActive = false;
            var seenOutgoingRaids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int skippedMissingTypeCount = 0;
            int skippedInvalidDataCount = 0;

            foreach (var ev in sortedEvents)
            {
                if (ev.data.ValueKind != JsonValueKind.Object)
                {
                    skippedInvalidDataCount++;
                    continue;
                }

                string? normalizedEventType = ev.event_type?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(normalizedEventType))
                {
                    skippedMissingTypeCount++;
                    continue;
                }

                var d = ev.data;
                switch (normalizedEventType)
                {
                    case "clearmsg":
                    {
                        string? targetMsgId = GetJsonString(d, "target_msg_id");
                        string? targetLogin = GetJsonString(d, "target_user_login");

                        if (options.ModerationMode != ModerationDisplayMode.Off
                            && !string.IsNullOrEmpty(targetMsgId)
                            && entriesById.TryGetValue(targetMsgId, out var matches))
                        {
                            foreach (var target in matches)
                            {
                                double modAt = Math.Max(target.AppearSeconds, ev.content_offset_seconds);
                                target.ModeratedAtSeconds = Math.Min(target.ModeratedAtSeconds ?? double.MaxValue, modAt);
                            }
                        }

                        if (options.ShowModerationNotices)
                        {
                            string notice = !string.IsNullOrEmpty(targetLogin)
                                ? $"A message from {targetLogin} was deleted."
                                : "A message was deleted by a moderator.";
                            syntheticEntries.Add(CreateSyntheticEntry(ev.content_offset_seconds, notice, compact: true));
                        }
                        break;
                    }

                    case "clearchat":
                    {
                        string action = (GetJsonString(d, "action") ?? "timeout").ToLowerInvariant();
                        string? targetLogin = GetJsonString(d, "target_user_login");
                        string? targetUserId = GetJsonString(d, "target_user_id");
                        int? durationSec = d.TryGetProperty("ban_duration_seconds", out var durEl) && durEl.ValueKind == JsonValueKind.Number && durEl.TryGetInt32(out int parsedDur)
                            ? parsedDur
                            : null;

                        if (action == "clear_all")
                        {
                            if (!options.EnableClearAll)
                            {
                                break;
                            }

                            if (options.ModerationMode != ModerationDisplayMode.Off)
                            {
                                foreach (var c in realEntries)
                                {
                                    if (c.AppearSeconds <= ev.content_offset_seconds + 0.001)
                                    {
                                        c.ModeratedAtSeconds = Math.Min(c.ModeratedAtSeconds ?? double.MaxValue, ev.content_offset_seconds);
                                    }
                                }
                            }

                            if (options.ShowModerationNotices)
                            {
                                syntheticEntries.Add(CreateSyntheticEntry(ev.content_offset_seconds, "Chat was cleared by a moderator.", compact: true));
                            }
                        }
                        else
                        {
                            if (options.ModerationMode != ModerationDisplayMode.Off)
                            {
                                List<MutableTimelineEntry>? userEntries = null;
                                if (!string.IsNullOrEmpty(targetUserId))
                                {
                                    entriesByUserId.TryGetValue(targetUserId, out userEntries);
                                }
                                if (userEntries is null && !string.IsNullOrEmpty(targetLogin))
                                {
                                    entriesByUserLogin.TryGetValue(targetLogin, out userEntries);
                                }

                                if (userEntries is not null)
                                {
                                    foreach (var c in userEntries)
                                    {
                                        if (c.AppearSeconds <= ev.content_offset_seconds + 0.001)
                                        {
                                            c.ModeratedAtSeconds = Math.Min(c.ModeratedAtSeconds ?? double.MaxValue, ev.content_offset_seconds);
                                        }
                                    }
                                }
                            }

                            if (options.ShowModerationNotices && !string.IsNullOrEmpty(targetLogin))
                            {
                                string notice = action == "ban" || durationSec is null or <= 0
                                    ? $"{targetLogin} was permanently banned."
                                    : $"{targetLogin} was timed out for {FormatTimeoutDuration(durationSec.Value)}.";
                                syntheticEntries.Add(CreateSyntheticEntry(ev.content_offset_seconds, notice, compact: true));
                            }
                        }
                        break;
                    }

                    case "roomstate":
                    {
                        if (!options.ShowRoomstateNotices)
                        {
                            break;
                        }

                        bool isInitialBaseline = !hasSeenInitialRoomstate || ev.content_offset_seconds <= initialBaselineCutoff;
                        hasSeenInitialRoomstate = true;

                        bool? eoVal = d.TryGetProperty("emote_only", out var eoEl) && eoEl.ValueKind is JsonValueKind.True or JsonValueKind.False
                            ? eoEl.GetBoolean()
                            : null;
                        bool? soVal = d.TryGetProperty("subs_only", out var soEl) && soEl.ValueKind is JsonValueKind.True or JsonValueKind.False
                            ? soEl.GetBoolean()
                            : null;
                        int? slowVal = d.TryGetProperty("slow_seconds", out var slEl) && slEl.ValueKind == JsonValueKind.Number && slEl.TryGetInt32(out int parsedSlow)
                            ? parsedSlow
                            : null;
                        int? foVal = d.TryGetProperty("followers_only_minutes", out var foEl) && foEl.ValueKind == JsonValueKind.Number && foEl.TryGetInt32(out int parsedFo)
                            ? parsedFo
                            : null;
                        bool? r9kVal = d.TryGetProperty("r9k", out var r9kEl) && r9kEl.ValueKind is JsonValueKind.True or JsonValueKind.False
                            ? r9kEl.GetBoolean()
                            : null;

                        if (isInitialBaseline)
                        {
                            if (eoVal.HasValue) prevEmoteOnly = eoVal.Value;
                            if (soVal.HasValue) prevSubsOnly = soVal.Value;
                            if (slowVal.HasValue) prevSlowSeconds = slowVal.Value;
                            if (foVal.HasValue) prevFollowersOnly = foVal.Value;
                            if (r9kVal.HasValue) prevR9k = r9kVal.Value;
                            break;
                        }

                        var turnedOnNotices = new List<(Action Commit, string Notice)>(2);
                        var turnedOffNotices = new List<(Action Commit, string Notice)>(2);

                        if (eoVal.HasValue)
                        {
                            bool v = eoVal.Value;
                            if (prevEmoteOnly is null)
                            {
                                prevEmoteOnly = v;
                                if (v) turnedOnNotices.Add((() => prevEmoteOnly = true, "This room is now in emote-only mode."));
                            }
                            else if (prevEmoteOnly.Value != v)
                            {
                                if (v) turnedOnNotices.Add((() => prevEmoteOnly = true, "This room is now in emote-only mode."));
                                else turnedOffNotices.Add((() => prevEmoteOnly = false, "This room is no longer in emote-only mode."));
                            }
                        }

                        if (soVal.HasValue)
                        {
                            bool v = soVal.Value;
                            if (prevSubsOnly is null)
                            {
                                prevSubsOnly = v;
                                if (v) turnedOnNotices.Add((() => prevSubsOnly = true, "This room is now in subscribers-only mode."));
                            }
                            else if (prevSubsOnly.Value != v)
                            {
                                if (v) turnedOnNotices.Add((() => prevSubsOnly = true, "This room is now in subscribers-only mode."));
                                else turnedOffNotices.Add((() => prevSubsOnly = false, "This room is no longer in subscribers-only mode."));
                            }
                        }

                        if (slowVal.HasValue)
                        {
                            int v = slowVal.Value;
                            if (prevSlowSeconds is null)
                            {
                                prevSlowSeconds = v;
                                if (v > 0) turnedOnNotices.Add((() => prevSlowSeconds = v, $"This room is now in {v}s slow mode."));
                            }
                            else if (prevSlowSeconds.Value != v)
                            {
                                if (v > 0) turnedOnNotices.Add((() => prevSlowSeconds = v, $"This room is now in {v}s slow mode."));
                                else if (prevSlowSeconds.Value > 0) turnedOffNotices.Add((() => prevSlowSeconds = 0, "This room is no longer in slow mode."));
                                else prevSlowSeconds = v;
                            }
                        }

                        if (foVal.HasValue)
                        {
                            int v = foVal.Value;
                            if (prevFollowersOnly is null)
                            {
                                prevFollowersOnly = v;
                                if (v >= 0)
                                {
                                    string text = v == 0 ? "This room is now in followers-only mode." : $"This room is now in {v}m followers-only mode.";
                                    turnedOnNotices.Add((() => prevFollowersOnly = v, text));
                                }
                            }
                            else if (prevFollowersOnly.Value != v)
                            {
                                if (v >= 0)
                                {
                                    string text = v == 0 ? "This room is now in followers-only mode." : $"This room is now in {v}m followers-only mode.";
                                    turnedOnNotices.Add((() => prevFollowersOnly = v, text));
                                }
                                else if (prevFollowersOnly.Value >= 0)
                                {
                                    turnedOffNotices.Add((() => prevFollowersOnly = v, "This room is no longer in followers-only mode."));
                                }
                                else
                                {
                                    prevFollowersOnly = v;
                                }
                            }
                        }

                        if (r9kVal.HasValue)
                        {
                            bool v = r9kVal.Value;
                            if (prevR9k is null)
                            {
                                prevR9k = v;
                                if (v) turnedOnNotices.Add((() => prevR9k = true, "This room is now in unique-chat (R9K) mode."));
                            }
                            else if (prevR9k.Value != v)
                            {
                                if (v) turnedOnNotices.Add((() => prevR9k = true, "This room is now in unique-chat (R9K) mode."));
                                else turnedOffNotices.Add((() => prevR9k = false, "This room is no longer in unique-chat (R9K) mode."));
                            }
                        }

                        if (turnedOnNotices.Count > 0)
                        {
                            foreach (var (commit, notice) in turnedOnNotices)
                            {
                                commit();
                                syntheticEntries.Add(CreateSyntheticEntry(ev.content_offset_seconds, notice, compact: true));
                            }
                        }
                        else if (turnedOffNotices.Count == 1)
                        {
                            var (commit, notice) = turnedOffNotices[0];
                            commit();
                            syntheticEntries.Add(CreateSyntheticEntry(ev.content_offset_seconds, notice, compact: true));
                        }
                        break;
                    }

                    case "channel_points":
                    {
                        if (!options.RenderChannelPointRedemptions)
                        {
                            break;
                        }

                        string userDisplay = GetJsonString(d, "user_display_name") ?? GetJsonString(d, "user_login") ?? "Chatter";
                        string rewardTitle = GetJsonString(d, "reward_title") ?? "Channel Point Reward";
                        int rewardCost = GetJsonInt(d, "reward_cost", 0);
                        string? bgColorHex = GetJsonString(d, "reward_background_color");
                        string? userInput = GetJsonString(d, "user_input");

                        uint accentColor = ResolveChannelPointAccentColor(bgColorHex, 0xFF9146FF);
                        string costPart = rewardCost > 0 ? $" ({rewardCost:N0})" : "";
                        string body = string.IsNullOrWhiteSpace(userInput)
                            ? $"{userDisplay} redeemed {rewardTitle}{costPart}"
                            : $"{userDisplay} redeemed {rewardTitle}{costPart}: {userInput.Trim()}";

                        syntheticEntries.Add(CreateSyntheticEntry(ev.content_offset_seconds, body, compact: false, accentColor: accentColor));
                        break;
                    }

                    case "raid":
                    {
                        if (!options.RenderRaidNotices)
                        {
                            break;
                        }

                        string pubsubType = (GetJsonString(d, "pubsub_type") ?? "").ToLowerInvariant();
                        string? targetDisplay = GetJsonString(d, "target_display_name") ?? GetJsonString(d, "target_login");
                        int viewerCount = GetJsonInt(d, "viewer_count", 0);

                        if (!string.IsNullOrEmpty(targetDisplay) && pubsubType is "raid_go_v2" or "raid_update_v2")
                        {
                            string raidKey = $"{targetDisplay}:{pubsubType == "raid_go_v2"}";
                            if (pubsubType == "raid_go_v2" || seenOutgoingRaids.Add(targetDisplay))
                            {
                                if (seenOutgoingRaids.Add(raidKey))
                                {
                                    string body = viewerCount > 0
                                        ? $"Raiding {targetDisplay} with {viewerCount:N0} viewers!"
                                        : $"Raiding {targetDisplay}!";
                                    syntheticEntries.Add(CreateSyntheticEntry(ev.content_offset_seconds, body, compact: false, accentColor: 0xFF9146FF));
                                }
                            }
                        }
                        break;
                    }

                    case "prediction":
                    {
                        if (options.PredictionMode == OverlayWidgetMode.StickyTop)
                        {
                            stickyEvents.Add(ParsePredictionStickyEvent(ev));
                        }
                        else if (options.PredictionMode == OverlayWidgetMode.ChatScroll)
                        {
                            string predId = GetJsonString(d, "prediction_id") ?? "";
                            string title = GetJsonString(d, "title") ?? "Prediction";
                            string status = (GetJsonString(d, "status") ?? "ACTIVE").ToUpperInvariant();
                            string? winningId = GetJsonString(d, "winning_outcome_id");

                            seenPredictions.TryGetValue(predId, out string? prevStatus);
                            if (prevStatus != status)
                            {
                                seenPredictions[predId] = status;
                                string? scrollText = null;
                                if (status == "ACTIVE" && prevStatus is null)
                                {
                                    scrollText = $"Prediction started: \"{title}\"";
                                }
                                else if (status == "LOCKED")
                                {
                                    scrollText = $"Prediction locked: \"{title}\"";
                                }
                                else if (status == "RESOLVED")
                                {
                                    string? winnerTitle = FindWinningOutcomeTitle(d, winningId);
                                    scrollText = !string.IsNullOrEmpty(winnerTitle)
                                        ? $"Prediction resolved: \"{title}\" — Winner: {winnerTitle}"
                                        : $"Prediction resolved: \"{title}\"";
                                }
                                else if (status == "CANCELED")
                                {
                                    scrollText = $"Prediction canceled: \"{title}\"";
                                }

                                if (scrollText is not null)
                                {
                                    syntheticEntries.Add(CreateSyntheticEntry(ev.content_offset_seconds, scrollText, compact: false, accentColor: 0xFF387AFF));
                                }
                            }
                        }
                        break;
                    }

                    case "poll":
                    {
                        if (options.PollMode == OverlayWidgetMode.StickyTop)
                        {
                            stickyEvents.Add(ParsePollStickyEvent(ev));
                        }
                        else if (options.PollMode == OverlayWidgetMode.ChatScroll)
                        {
                            string pollId = GetJsonString(d, "poll_id") ?? "";
                            string title = GetJsonString(d, "title") ?? "Poll";
                            string pubsubType = (GetJsonString(d, "pubsub_type") ?? "").ToUpperInvariant();
                            string status = (GetJsonString(d, "status") ?? "ACTIVE").ToUpperInvariant();
                            if (pubsubType == "POLL_COMPLETE") status = "COMPLETED";

                            seenPolls.TryGetValue(pollId, out string? prevStatus);
                            if (prevStatus != status)
                            {
                                seenPolls[pollId] = status;
                                if (status == "ACTIVE" && prevStatus is null)
                                {
                                    syntheticEntries.Add(CreateSyntheticEntry(ev.content_offset_seconds, $"Poll started: \"{title}\"", compact: false, accentColor: 0xFF00DB84));
                                }
                                else if (status is "COMPLETED" or "TERMINATED" && prevStatus is not ("COMPLETED" or "TERMINATED"))
                                {
                                    string? winnerChoice = FindWinningPollChoice(d);
                                    string scrollText = !string.IsNullOrEmpty(winnerChoice)
                                        ? $"Poll ended: \"{title}\" — Winner: {winnerChoice}"
                                        : $"Poll ended: \"{title}\"";
                                    syntheticEntries.Add(CreateSyntheticEntry(ev.content_offset_seconds, scrollText, compact: false, accentColor: 0xFF00DB84));
                                }
                            }
                        }
                        break;
                    }

                    case "pinned_chat":
                    {
                        if (options.PinnedChatMode == OverlayWidgetMode.StickyTop)
                        {
                            stickyEvents.Add(ParsePinnedChatStickyEvent(ev));
                        }
                        else if (options.PinnedChatMode == OverlayWidgetMode.ChatScroll)
                        {
                            string action = (GetJsonString(d, "action") ?? "pin-message").ToLowerInvariant();
                            string pinId = GetJsonString(d, "pin_id") ?? "";
                            string msg = (GetJsonString(d, "message_text") ?? "").Trim();
                            if (string.IsNullOrEmpty(msg)
                                && d.TryGetProperty("message", out var msgObj)
                                && msgObj.ValueKind == JsonValueKind.Object)
                            {
                                msg = (GetJsonString(msgObj, "text") ?? "").Trim();
                            }

                            if (action == "pin-message" && !string.IsNullOrEmpty(msg) && seenPins.Add(pinId))
                            {
                                string sender = GetJsonString(d, "sender_display_name") ?? "Chatter";
                                string? pinnedBy = GetJsonString(d, "pinned_by_display_name");
                                string prefix = !string.IsNullOrEmpty(pinnedBy) ? $"Pinned by {pinnedBy} — " : "Pinned message — ";
                                syntheticEntries.Add(CreateSyntheticEntry(ev.content_offset_seconds, $"{prefix}{sender}: {msg}", compact: false, accentColor: 0xFF9146FF));
                            }
                        }
                        break;
                    }

                    case "hype_train":
                    {
                        if (options.HypeTrainMode == OverlayWidgetMode.StickyTop)
                        {
                            stickyEvents.Add(ParseHypeTrainStickyEvent(ev));
                        }
                        else if (options.HypeTrainMode == OverlayWidgetMode.ChatScroll)
                        {
                            string pubsubType = (GetJsonString(d, "pubsub_type") ?? "").ToLowerInvariant();
                            int level = Math.Max(1, GetJsonInt(d, "level", 1));

                            if (pubsubType == "hype-train-start" && !hypeTrainActive)
                            {
                                hypeTrainActive = true;
                                lastHypeTrainLevel = level;
                                syntheticEntries.Add(CreateSyntheticEntry(ev.content_offset_seconds, $"Hype Train started at Level {level}!", compact: false, accentColor: 0xFFFFB31A));
                            }
                            else if (pubsubType == "hype-train-level-up" && level > lastHypeTrainLevel)
                            {
                                hypeTrainActive = true;
                                lastHypeTrainLevel = level;
                                syntheticEntries.Add(CreateSyntheticEntry(ev.content_offset_seconds, $"Hype Train leveled up to Level {level}!", compact: false, accentColor: 0xFFFFB31A));
                            }
                            else if (pubsubType == "hype-train-end" && hypeTrainActive)
                            {
                                hypeTrainActive = false;
                                syntheticEntries.Add(CreateSyntheticEntry(ev.content_offset_seconds, $"Hype Train ended at Level {level}!", compact: false, accentColor: 0xFFFFB31A));
                            }
                        }
                        break;
                    }
                }
            }

            if (skippedMissingTypeCount > 0)
            {
                logger?.LogWarning($"Skipped {skippedMissingTypeCount:N0} broadcast event(s) missing 'event_type'/'type'.");
            }

            if (skippedInvalidDataCount > 0)
            {
                logger?.LogWarning($"Skipped {skippedInvalidDataCount:N0} broadcast event(s) with missing or non-object 'data'.");
            }
        }

        private static PredictionOverlayEvent ParsePredictionStickyEvent(ChatBroadcastEvent ev)
        {
            var d = ev.data;
            string predId = GetJsonString(d, "prediction_id") ?? "";
            string title = GetJsonString(d, "title") ?? "Prediction";
            string status = (GetJsonString(d, "status") ?? "ACTIVE").ToUpperInvariant();
            int windowSec = GetJsonInt(d, "prediction_window_seconds", 120);
            string? winningId = GetJsonString(d, "winning_outcome_id");

            var outcomes = new List<StickyPredictionOutcome>();
            if (d.TryGetProperty("outcomes", out var outcomesEl) && outcomesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in outcomesEl.EnumerateArray())
                {
                    outcomes.Add(new StickyPredictionOutcome(
                        GetJsonString(item, "id") ?? "",
                        GetJsonString(item, "title") ?? "",
                        GetJsonString(item, "color") ?? "BLUE",
                        GetJsonLong(item, "total_points", 0),
                        GetJsonInt(item, "total_users", 0)));
                }
            }

            return new PredictionOverlayEvent(
                ev.content_offset_seconds,
                ev.carried_over ?? false,
                predId,
                title,
                status,
                windowSec,
                winningId,
                outcomes);
        }

        private static PollOverlayEvent ParsePollStickyEvent(ChatBroadcastEvent ev)
        {
            var d = ev.data;
            string pollId = GetJsonString(d, "poll_id") ?? "";
            string pubsubType = (GetJsonString(d, "pubsub_type") ?? "").ToUpperInvariant();
            string status = (GetJsonString(d, "status") ?? "ACTIVE").ToUpperInvariant();
            string title = GetJsonString(d, "title") ?? "Poll";
            int durationSec = GetJsonInt(d, "duration_seconds", 60);
            int totalVoters = GetJsonInt(d, "total_voters", 0);

            var choices = new List<StickyPollChoice>();
            int sumChoiceVotes = 0;
            if (d.TryGetProperty("choices", out var choicesEl) && choicesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in choicesEl.EnumerateArray())
                {
                    int voters = GetJsonInt(item, "total_voters", 0);
                    sumChoiceVotes += voters;
                    choices.Add(new StickyPollChoice(
                        GetJsonString(item, "choice_id") ?? "",
                        GetJsonString(item, "title") ?? "",
                        voters));
                }
            }

            return new PollOverlayEvent(
                ev.content_offset_seconds,
                ev.carried_over ?? false,
                pollId,
                pubsubType,
                status,
                title,
                durationSec,
                Math.Max(totalVoters, sumChoiceVotes),
                choices);
        }

        private static PinnedChatOverlayEvent ParsePinnedChatStickyEvent(ChatBroadcastEvent ev)
        {
            var d = ev.data;
            string action = (GetJsonString(d, "action") ?? "pin-message").ToLowerInvariant();
            string pinId = GetJsonString(d, "pin_id") ?? "";
            string type = GetJsonString(d, "type") ?? "MOD_PIN";

            string? rawMsgText = GetJsonString(d, "message_text");
            if (string.IsNullOrWhiteSpace(rawMsgText)
                && d.TryGetProperty("message", out var msgObj)
                && msgObj.ValueKind == JsonValueKind.Object)
            {
                rawMsgText = GetJsonString(msgObj, "text");
            }

            string? rawSenderName = GetJsonString(d, "sender_display_name");
            string? rawSenderColor = GetJsonString(d, "sender_color");
            if (string.IsNullOrWhiteSpace(rawSenderName)
                && d.TryGetProperty("message", out var msgObjForSender)
                && msgObjForSender.ValueKind == JsonValueKind.Object
                && msgObjForSender.TryGetProperty("sender", out var senderObj)
                && senderObj.ValueKind == JsonValueKind.Object)
            {
                rawSenderName = GetJsonString(senderObj, "display_name");
                if (string.IsNullOrWhiteSpace(rawSenderColor))
                {
                    rawSenderColor = GetJsonString(senderObj, "chat_color");
                }
            }

            string pinnedBy = GetJsonString(d, "pinned_by_display_name") ?? "";
            long startsAt = GetJsonLong(d, "starts_at", 0);
            long endsAt = GetJsonLong(d, "ends_at", 0);

            return new PinnedChatOverlayEvent(
                ev.content_offset_seconds,
                ev.carried_over ?? false,
                action,
                pinId,
                type,
                rawMsgText,
                rawSenderName,
                rawSenderColor,
                pinnedBy,
                startsAt,
                endsAt);
        }

        private static HypeTrainOverlayEvent ParseHypeTrainStickyEvent(ChatBroadcastEvent ev)
        {
            var d = ev.data;
            string pubsubType = (GetJsonString(d, "pubsub_type") ?? "").ToLowerInvariant();
            int? rawLevel = TryGetJsonInt(d, "level");
            int? rawGoal = TryGetJsonInt(d, "goal");
            int? rawTotal = TryGetJsonInt(d, "total");
            int? rawProgress = TryGetJsonInt(d, "progress");

            return new HypeTrainOverlayEvent(
                ev.content_offset_seconds,
                ev.carried_over ?? false,
                pubsubType,
                rawLevel,
                rawGoal,
                rawTotal,
                rawProgress);
        }

        private static MutableTimelineEntry CreateSyntheticEntry(double offsetSeconds, string bodyText, bool compact, uint? accentColor = null)
        {
            var author = new TimelineAuthor("0", "twitch", "Twitch", null, null, Array.Empty<TimelineBadge>());
            var content = new TimelineMessageContent(bodyText, [new TimelineFragment(bodyText, null)], 0, 0);
            var notice = compact
                ? new TimelineNoticeInfo(TimelineNoticeKind.CompactSystemNotice, HighlightType.None, SystemText: bodyText)
                : new TimelineNoticeInfo(
                    TimelineNoticeKind.AccentedSystemNotice,
                    HighlightType.None,
                    SystemText: bodyText,
                    CustomAccentColor: new SKColor(accentColor ?? 0xFF9146FF));

            return new MutableTimelineEntry
            {
                Id = $"synthetic-{Guid.NewGuid():N}",
                CreatedAt = DateTime.UtcNow,
                AppearSeconds = offsetSeconds,
                ModeratedAtSeconds = null,
                IsSynthetic = true,
                Author = author,
                Content = content,
                Notice = notice
            };
        }

        private static void EnsureSortedChronologically(List<MutableTimelineEntry> entries)
        {
            if (entries.Count <= 1)
            {
                return;
            }

            bool needsSort = false;
            double prev = entries[0].AppearSeconds;
            for (int i = 1; i < entries.Count; i++)
            {
                double cur = entries[i].AppearSeconds;
                if (cur < prev)
                {
                    needsSort = true;
                    break;
                }
                prev = cur;
            }

            if (needsSort)
            {
                var sorted = entries.OrderBy(static e => e.AppearSeconds).ToList();
                entries.Clear();
                entries.AddRange(sorted);
            }
        }

        private static void DisperseOffsets(List<MutableTimelineEntry> entries)
        {
            var span = CollectionsMarshal.AsSpan(entries);

            var minVideoCreated = DateTime.MaxValue;
            var maxVideoCreated = DateTime.MinValue;
            bool allWholeSeconds = true;
            bool hasSubSecondCreatedAt = false;
            int realCount = 0;

            foreach (var e in span)
            {
                if (e.IsSynthetic || e.CreatedAt == default)
                {
                    continue;
                }

                if ((e.CreatedAt - DateTime.MinValue).TotalSeconds < e.AppearSeconds)
                {
                    continue;
                }

                realCount++;

                if (e.AppearSeconds % 1 != 0)
                {
                    allWholeSeconds = false;
                }

                if (e.CreatedAt.Ticks % TimeSpan.TicksPerSecond != 0)
                {
                    hasSubSecondCreatedAt = true;
                }

                var estimate = e.CreatedAt - TimeSpan.FromSeconds(e.AppearSeconds);
                if (estimate < minVideoCreated) minVideoCreated = estimate;
                if (estimate > maxVideoCreated) maxVideoCreated = estimate;
            }

            const double MAX_DRIFT_SECONDS = 1.5;
            double driftSeconds = realCount >= 2 ? (maxVideoCreated - minVideoCreated).TotalSeconds : 0;
            if (realCount >= 2 && allWholeSeconds && hasSubSecondCreatedAt && driftSeconds > 0.01 && driftSeconds < MAX_DRIFT_SECONDS)
            {
                foreach (var e in span)
                {
                    if (e.IsSynthetic || e.CreatedAt == default)
                    {
                        continue;
                    }

                    e.AppearSeconds = (e.CreatedAt - minVideoCreated).TotalSeconds;
                }

                EnsureSortedChronologically(entries);
            }

            JitterOffsets(entries);
        }

        private static void JitterOffsets(List<MutableTimelineEntry> entries)
        {
            var jitterRnd = new Random(entries.Count);

            for (int i = 0; i < entries.Count - 1; i++)
            {
                double baseOffset = entries[i].AppearSeconds;
                if (Math.Abs(entries[i + 1].AppearSeconds - baseOffset) > 0.0001)
                {
                    continue;
                }

                int startIndex = i;
                while (i < entries.Count - 1 && Math.Abs(entries[i + 1].AppearSeconds - baseOffset) <= 0.0001)
                {
                    i++;
                }

                double scaleFactor = 1.0;
                if (i < entries.Count - 1)
                {
                    double gapToNext = entries[i + 1].AppearSeconds - baseOffset;
                    if (gapToNext > 0.0001 && gapToNext < 1.0)
                    {
                        scaleFactor = gapToNext;
                    }
                }

                int groupCount = i - startIndex + 1;
                for (int c = 1; c < groupCount; c++)
                {
                    double jitter = jitterRnd.NextDouble() * 0.8 - 0.4;
                    double distributedOffset = (c + jitter) / groupCount;
                    entries[startIndex + c].AppearSeconds = baseOffset + distributedOffset * scaleFactor;
                }
            }
        }

        private static void FloorOffsetsSinglePass(List<MutableTimelineEntry> entries, double updateRate)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.ModeratedAtSeconds is double rawModSec && rawModSec < entry.AppearSeconds)
                {
                    entry.ModeratedAtSeconds = entry.AppearSeconds;
                }

                if (updateRate <= 0)
                {
                    continue;
                }

                if (updateRate > 1)
                {
                    entry.AppearSeconds = Math.Floor(entry.AppearSeconds);
                    if (entry.ModeratedAtSeconds is double modSec)
                    {
                        entry.ModeratedAtSeconds = Math.Max(entry.AppearSeconds, Math.Floor(modSec));
                    }
                }
                else
                {
                    entry.AppearSeconds = Math.Floor(entry.AppearSeconds / updateRate) * updateRate;
                    if (entry.ModeratedAtSeconds is double modSec)
                    {
                        entry.ModeratedAtSeconds = Math.Max(
                            entry.AppearSeconds,
                            Math.Floor(modSec / updateRate) * updateRate);
                    }
                }
            }
        }

        private static string FormatTimeoutDuration(int seconds)
        {
            if (seconds <= 0) return "0s";
            if (seconds < 60) return $"{seconds}s";
            if (seconds < 3600)
            {
                int mins = seconds / 60;
                int remSec = seconds % 60;
                return remSec == 0 ? $"{mins}m" : $"{mins}m {remSec}s";
            }
            if (seconds < 86400)
            {
                int hours = seconds / 3600;
                int remMins = (seconds % 3600) / 60;
                return remMins == 0 ? $"{hours}h" : $"{hours}h {remMins}m";
            }

            int days = seconds / 86400;
            int remHours = (seconds % 86400) / 3600;
            return remHours == 0 ? $"{days}d" : $"{days}d {remHours}h";
        }

        private static uint ResolveChannelPointAccentColor(string? hex, uint fallback)
        {
            if (!string.IsNullOrWhiteSpace(hex) && SKColor.TryParse(hex, out var parsed))
            {
                if (parsed.RelativeLuminance() >= 0.04)
                {
                    return (uint)parsed;
                }
            }
            return fallback;
        }

        internal static SKColor GetAnnouncementAccentColor(string? colorName)
        {
            return (colorName ?? "PRIMARY").ToUpperInvariant() switch
            {
                "BLUE" => new SKColor(0xFF00A8FC),
                "GREEN" => new SKColor(0xFF00DB84),
                "ORANGE" => new SKColor(0xFFFFB31A),
                "PURPLE" => new SKColor(0xFF9146FF),
                _ => new SKColor(0xFF9146FF)
            };
        }

        private static string? FindWinningOutcomeTitle(JsonElement d, string? winningId)
        {
            if (string.IsNullOrEmpty(winningId) || !d.TryGetProperty("outcomes", out var outcomesEl) || outcomesEl.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            long totalPts = 0;
            string? winTitle = null;
            long winPts = 0;
            foreach (var item in outcomesEl.EnumerateArray())
            {
                long pts = GetJsonLong(item, "total_points", 0);
                totalPts += pts;
                if (string.Equals(GetJsonString(item, "id"), winningId, StringComparison.Ordinal))
                {
                    winTitle = GetJsonString(item, "title");
                    winPts = pts;
                }
            }

            if (string.IsNullOrEmpty(winTitle))
            {
                return null;
            }

            if (totalPts > 0)
            {
                int pct = (int)Math.Round(winPts * 100.0 / totalPts);
                return $"{winTitle} ({pct}%)";
            }

            return winTitle;
        }

        private static string? FindWinningPollChoice(JsonElement d)
        {
            if (!d.TryGetProperty("choices", out var choicesEl) || choicesEl.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? bestTitle = null;
            int bestVotes = -1;
            int totalVotes = 0;
            foreach (var item in choicesEl.EnumerateArray())
            {
                int votes = GetJsonInt(item, "total_voters", 0);
                totalVotes += votes;
                if (votes > bestVotes)
                {
                    bestVotes = votes;
                    bestTitle = GetJsonString(item, "title");
                }
            }

            if (string.IsNullOrEmpty(bestTitle))
            {
                return null;
            }

            if (totalVotes > 0 && bestVotes >= 0)
            {
                int pct = (int)Math.Round(bestVotes * 100.0 / totalVotes);
                return $"{bestTitle} ({pct}%)";
            }

            return bestTitle;
        }

        private static string? GetJsonString(JsonElement obj, string propName)
        {
            return obj.TryGetProperty(propName, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;
        }

        private static int GetJsonInt(JsonElement obj, string propName, int fallback)
        {
            return TryGetJsonInt(obj, propName) ?? fallback;
        }

        private static int? TryGetJsonInt(JsonElement obj, string propName)
        {
            if (obj.TryGetProperty(propName, out var el))
            {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v))
                    return v;
                if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out int sv))
                    return sv;
            }
            return null;
        }

        private static long GetJsonLong(JsonElement obj, string propName, long fallback)
        {
            if (obj.TryGetProperty(propName, out var el))
            {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long v))
                    return v;
                if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out long sv))
                    return sv;
            }
            return fallback;
        }
    }
}

using SkiaSharp;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TwitchDownloaderCore.Chat;
using TwitchDownloaderCore.Extensions;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Models.Render;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Tools;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore
{
    public sealed partial class ChatRenderer
    {
        private const double STICKY_RESOLVED_LINGER_SECONDS = 15.0;
        private const double STICKY_MAX_HEIGHT_RATIO = 0.25;

        private readonly ConcurrentDictionary<string, TwitchEmote> _gigantifiedEmoteCache = new(StringComparer.Ordinal);
        private List<ChatBroadcastEvent> _stickyBroadcastEvents = [];

        private sealed class ActivePredictionOutcome
        {
            public string Id { get; init; }
            public string Title { get; init; }
            public string Color { get; init; }
            public long TotalPoints { get; init; }
            public int TotalUsers { get; init; }
        }

        private sealed class ActivePredictionState
        {
            public string PredictionId { get; set; }
            public string Title { get; set; }
            public string Status { get; set; } // ACTIVE, LOCKED, RESOLVED, CANCELED
            public int WindowSeconds { get; set; }
            public double StartedOffsetSeconds { get; set; }
            public double LastUpdatedOffsetSeconds { get; set; }
            public double? ResolvedOffsetSeconds { get; set; }
            public string WinningOutcomeId { get; set; }
            public List<ActivePredictionOutcome> Outcomes { get; set; } = [];
        }

        private sealed class ActivePollChoice
        {
            public string ChoiceId { get; init; }
            public string Title { get; init; }
            public int TotalVoters { get; init; }
        }

        private sealed class ActivePollState
        {
            public string PollId { get; set; }
            public string Title { get; set; }
            public string Status { get; set; } // ACTIVE, COMPLETED, TERMINATED, ARCHIVED
            public int DurationSeconds { get; set; }
            public double StartedOffsetSeconds { get; set; }
            public double LastUpdatedOffsetSeconds { get; set; }
            public double? CompletedOffsetSeconds { get; set; }
            public int TotalVoters { get; set; }
            public List<ActivePollChoice> Choices { get; set; } = [];
        }

        private sealed class ActivePinnedChatState
        {
            public string PinId { get; set; }
            public string Type { get; set; }
            public string MessageText { get; set; }
            public string SenderDisplayName { get; set; }
            public string SenderColor { get; set; }
            public string PinnedByDisplayName { get; set; }
            public double StartedOffsetSeconds { get; set; }
            public double? ExpiresOffsetSeconds { get; set; }
        }

        private sealed class ActiveHypeTrainState
        {
            public int Level { get; set; }
            public int Goal { get; set; }
            public int Total { get; set; }
            public int Progress { get; set; }
            public double StartedOffsetSeconds { get; set; }
            public double LastUpdatedOffsetSeconds { get; set; }
            public double? EndedOffsetSeconds { get; set; }
            public bool IsEnded { get; set; }
        }

        private enum StickyWidgetKind
        {
            PredictionOpenOrResolved = 1,
            Poll = 2,
            PinnedChat = 3,
            PredictionLocked = 4,
            HypeTrain = 5
        }

        private sealed class StickyOverlayTracker
        {
            private readonly List<ChatBroadcastEvent> _events;
            private readonly ChatRenderOptions _options;
            private int _eventCursor = 0;

            public ActivePredictionState Prediction { get; private set; }
            public ActivePollState Poll { get; private set; }
            public ActivePinnedChatState PinnedChat { get; private set; }
            public ActiveHypeTrainState HypeTrain { get; private set; }

            public StickyOverlayTracker(List<ChatBroadcastEvent> events, ChatRenderOptions options)
            {
                _events = events ?? [];
                _options = options;
            }

            public void AdvanceTo(double currentTimeSeconds)
            {
                while (_eventCursor < _events.Count && _events[_eventCursor].content_offset_seconds <= currentTimeSeconds + 0.0001)
                {
                    ApplyEvent(_events[_eventCursor]);
                    _eventCursor++;
                }

                ExpireStaleStates(currentTimeSeconds);
            }

            private void ApplyEvent(ChatBroadcastEvent ev)
            {
                if (ev.data.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                switch (ev.event_type)
                {
                    case "prediction" when _options.PredictionMode == OverlayWidgetMode.StickyTop:
                        ApplyPredictionEvent(ev);
                        break;
                    case "poll" when _options.PollMode == OverlayWidgetMode.StickyTop:
                        ApplyPollEvent(ev);
                        break;
                    case "pinned_chat" when _options.PinnedChatMode == OverlayWidgetMode.StickyTop:
                        ApplyPinnedChatEvent(ev);
                        break;
                    case "hype_train" when _options.HypeTrainMode == OverlayWidgetMode.StickyTop:
                        ApplyHypeTrainEvent(ev);
                        break;
                }
            }

            private void ApplyPredictionEvent(ChatBroadcastEvent ev)
            {
                var d = ev.data;
                string predId = GetJsonString(d, "prediction_id") ?? "";
                string title = GetJsonString(d, "title") ?? "Prediction";
                string status = (GetJsonString(d, "status") ?? "ACTIVE").ToUpperInvariant();
                int windowSec = GetJsonInt(d, "prediction_window_seconds", 120);
                string winningId = GetJsonString(d, "winning_outcome_id");

                var outcomes = new List<ActivePredictionOutcome>();
                if (d.TryGetProperty("outcomes", out var outcomesEl) && outcomesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in outcomesEl.EnumerateArray())
                    {
                        outcomes.Add(new ActivePredictionOutcome
                        {
                            Id = GetJsonString(item, "id") ?? "",
                            Title = GetJsonString(item, "title") ?? "",
                            Color = GetJsonString(item, "color") ?? "BLUE",
                            TotalPoints = GetJsonLong(item, "total_points", 0),
                            TotalUsers = GetJsonInt(item, "total_users", 0)
                        });
                    }
                }

                bool isSamePrediction = Prediction != null && string.Equals(Prediction.PredictionId, predId, StringComparison.Ordinal);
                double startedOffset = isSamePrediction ? Prediction.StartedOffsetSeconds : ev.content_offset_seconds;
                double? resolvedOffset = isSamePrediction ? Prediction.ResolvedOffsetSeconds : null;

                if (status is "RESOLVED" or "CANCELED" && resolvedOffset is null)
                {
                    resolvedOffset = ev.content_offset_seconds;
                }

                Prediction = new ActivePredictionState
                {
                    PredictionId = predId,
                    Title = title,
                    Status = status,
                    WindowSeconds = Math.Max(5, windowSec),
                    StartedOffsetSeconds = startedOffset,
                    LastUpdatedOffsetSeconds = ev.content_offset_seconds,
                    ResolvedOffsetSeconds = resolvedOffset,
                    WinningOutcomeId = winningId,
                    Outcomes = outcomes
                };
            }

            private void ApplyPollEvent(ChatBroadcastEvent ev)
            {
                var d = ev.data;
                string pollId = GetJsonString(d, "poll_id") ?? "";
                string pubsubType = (GetJsonString(d, "pubsub_type") ?? "").ToUpperInvariant();
                string status = (GetJsonString(d, "status") ?? "ACTIVE").ToUpperInvariant();

                // When POLL_ARCHIVE arrives right after POLL_COMPLETE, preserve the COMPLETED state so it can linger for 15s
                if ((status == "ARCHIVED" || pubsubType == "POLL_ARCHIVE") && Poll != null && string.Equals(Poll.PollId, pollId, StringComparison.Ordinal))
                {
                    Poll.Status = "COMPLETED";
                    Poll.CompletedOffsetSeconds ??= ev.content_offset_seconds;
                    return;
                }

                string title = GetJsonString(d, "title") ?? "Poll";
                int durationSec = GetJsonInt(d, "duration_seconds", 60);
                int totalVoters = GetJsonInt(d, "total_voters", 0);

                var choices = new List<ActivePollChoice>();
                int sumChoiceVotes = 0;
                if (d.TryGetProperty("choices", out var choicesEl) && choicesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in choicesEl.EnumerateArray())
                    {
                        int voters = GetJsonInt(item, "total_voters", 0);
                        sumChoiceVotes += voters;
                        choices.Add(new ActivePollChoice
                        {
                            ChoiceId = GetJsonString(item, "choice_id") ?? "",
                            Title = GetJsonString(item, "title") ?? "",
                            TotalVoters = voters
                        });
                    }
                }

                bool isSamePoll = Poll != null && string.Equals(Poll.PollId, pollId, StringComparison.Ordinal);
                double startedOffset = isSamePoll ? Poll.StartedOffsetSeconds : ev.content_offset_seconds;
                double? completedOffset = isSamePoll ? Poll.CompletedOffsetSeconds : null;

                if ((status is "COMPLETED" or "TERMINATED" || pubsubType == "POLL_COMPLETE") && completedOffset is null)
                {
                    status = "COMPLETED";
                    completedOffset = ev.content_offset_seconds;
                }

                Poll = new ActivePollState
                {
                    PollId = pollId,
                    Title = title,
                    Status = status,
                    DurationSeconds = Math.Max(5, durationSec),
                    StartedOffsetSeconds = startedOffset,
                    LastUpdatedOffsetSeconds = ev.content_offset_seconds,
                    CompletedOffsetSeconds = completedOffset,
                    TotalVoters = Math.Max(totalVoters, sumChoiceVotes),
                    Choices = choices
                };
            }

            private void ApplyPinnedChatEvent(ChatBroadcastEvent ev)
            {
                var d = ev.data;
                string action = (GetJsonString(d, "action") ?? "pin-message").ToLowerInvariant();
                if (action == "unpin-message")
                {
                    PinnedChat = null;
                    return;
                }

                string pinId = GetJsonString(d, "pin_id") ?? "";
                string type = GetJsonString(d, "type") ?? "MOD_PIN";
                string msgText = (GetJsonString(d, "message_text") ?? "").Trim();
                string senderName = GetJsonString(d, "sender_display_name") ?? "Chatter";
                string senderColor = GetJsonString(d, "sender_color") ?? "#9146FF";
                string pinnedBy = GetJsonString(d, "pinned_by_display_name") ?? "";
                long startsAt = GetJsonLong(d, "starts_at", 0);
                long endsAt = GetJsonLong(d, "ends_at", 0);

                double? expiresOffset = null;
                if (endsAt > startsAt && startsAt > 0)
                {
                    expiresOffset = ev.content_offset_seconds + (endsAt - startsAt);
                }

                PinnedChat = new ActivePinnedChatState
                {
                    PinId = pinId,
                    Type = type,
                    MessageText = msgText,
                    SenderDisplayName = senderName,
                    SenderColor = senderColor,
                    PinnedByDisplayName = pinnedBy,
                    StartedOffsetSeconds = ev.content_offset_seconds,
                    ExpiresOffsetSeconds = expiresOffset
                };
            }

            private void ApplyHypeTrainEvent(ChatBroadcastEvent ev)
            {
                var d = ev.data;
                string pubsubType = (GetJsonString(d, "pubsub_type") ?? "").ToLowerInvariant();
                int level = Math.Max(1, GetJsonInt(d, "level", HypeTrain?.Level ?? 1));
                int goal = Math.Max(1, GetJsonInt(d, "goal", HypeTrain?.Goal ?? 1000));
                int total = GetJsonInt(d, "total", HypeTrain?.Total ?? 0);
                int progress = GetJsonInt(d, "progress", HypeTrain?.Progress ?? 0);

                bool isEnded = pubsubType == "hype-train-end";
                double startedOffset = HypeTrain is { IsEnded: false } ? HypeTrain.StartedOffsetSeconds : ev.content_offset_seconds;
                double? endedOffset = isEnded ? (HypeTrain?.EndedOffsetSeconds ?? ev.content_offset_seconds) : null;

                HypeTrain = new ActiveHypeTrainState
                {
                    Level = level,
                    Goal = goal,
                    Total = total,
                    Progress = progress,
                    StartedOffsetSeconds = startedOffset,
                    LastUpdatedOffsetSeconds = ev.content_offset_seconds,
                    EndedOffsetSeconds = endedOffset,
                    IsEnded = isEnded
                };
            }

            private void ExpireStaleStates(double currentTimeSeconds)
            {
                if (Prediction != null)
                {
                    if (Prediction.ResolvedOffsetSeconds is double resAt)
                    {
                        if (currentTimeSeconds - resAt >= STICKY_RESOLVED_LINGER_SECONDS)
                        {
                            Prediction = null;
                        }
                    }
                    else if (Prediction.Status == "ACTIVE" && currentTimeSeconds - Prediction.StartedOffsetSeconds > Prediction.WindowSeconds + 30)
                    {
                        // Auto-transition to LOCKED if lock event was missed, or expire if stale for >15m
                        if (currentTimeSeconds - Prediction.LastUpdatedOffsetSeconds > 900)
                        {
                            Prediction = null;
                        }
                        else
                        {
                            Prediction.Status = "LOCKED";
                        }
                    }
                    else if (currentTimeSeconds - Prediction.LastUpdatedOffsetSeconds > 1200)
                    {
                        Prediction = null;
                    }
                }

                if (Poll != null)
                {
                    if (Poll.CompletedOffsetSeconds is double compAt)
                    {
                        if (currentTimeSeconds - compAt >= STICKY_RESOLVED_LINGER_SECONDS)
                        {
                            Poll = null;
                        }
                    }
                    else if (Poll.Status == "ACTIVE" && currentTimeSeconds - Poll.StartedOffsetSeconds > Poll.DurationSeconds + STICKY_RESOLVED_LINGER_SECONDS)
                    {
                        Poll = null;
                    }
                }

                if (PinnedChat != null)
                {
                    if (PinnedChat.ExpiresOffsetSeconds is double expAt && currentTimeSeconds >= expAt)
                    {
                        PinnedChat = null;
                    }
                }

                if (HypeTrain != null)
                {
                    if (HypeTrain.EndedOffsetSeconds is double endAt)
                    {
                        if (currentTimeSeconds - endAt >= STICKY_RESOLVED_LINGER_SECONDS)
                        {
                            HypeTrain = null;
                        }
                    }
                    else if (currentTimeSeconds - HypeTrain.LastUpdatedOffsetSeconds > 180)
                    {
                        HypeTrain = null;
                    }
                }
            }

            public bool HasAnyActive =>
                Prediction != null || Poll != null || PinnedChat != null || HypeTrain != null;

            public long ComputeSignature(double currentTimeSeconds)
            {
                if (!HasAnyActive)
                {
                    return 0;
                }

                HashCode hc = new();
                if (Prediction != null)
                {
                    hc.Add(1);
                    hc.Add(Prediction.PredictionId);
                    hc.Add(Prediction.Status);
                    hc.Add(Prediction.WinningOutcomeId);
                    if (Prediction.Status == "ACTIVE")
                    {
                        int remSec = Math.Max(0, (int)Math.Ceiling(Prediction.WindowSeconds - (currentTimeSeconds - Prediction.StartedOffsetSeconds)));
                        hc.Add(remSec);
                    }
                    foreach (var o in Prediction.Outcomes)
                    {
                        hc.Add(o.TotalPoints);
                    }
                }

                if (Poll != null)
                {
                    hc.Add(2);
                    hc.Add(Poll.PollId);
                    hc.Add(Poll.Status);
                    hc.Add(Poll.TotalVoters);
                    if (Poll.Status == "ACTIVE")
                    {
                        int remSec = Math.Max(0, (int)Math.Ceiling(Poll.DurationSeconds - (currentTimeSeconds - Poll.StartedOffsetSeconds)));
                        hc.Add(remSec);
                    }
                    foreach (var c in Poll.Choices)
                    {
                        hc.Add(c.TotalVoters);
                    }
                }

                if (PinnedChat != null)
                {
                    hc.Add(3);
                    hc.Add(PinnedChat.PinId);
                    hc.Add(PinnedChat.MessageText);
                }

                if (HypeTrain != null)
                {
                    hc.Add(4);
                    hc.Add(HypeTrain.Level);
                    hc.Add(HypeTrain.Progress);
                    hc.Add(HypeTrain.Goal);
                    hc.Add(HypeTrain.IsEnded);
                }

                long sig = (uint)hc.ToHashCode();
                return sig == 0 ? 1L : sig;
            }
        }

        private void PrepareExtendedEventsAndComments()
        {
            chatRoot.comments ??= [];
            chatRoot.events ??= [];

            // 1. Normalize extended fields on comments[] and filter disabled notice types
            for (int i = chatRoot.comments.Count - 1; i >= 0; i--)
            {
                var c = chatRoot.comments[i];
                if (c?.message is null)
                {
                    continue;
                }

                if (c.message.bits_spent == 0)
                {
                    if (c.bits_spent > 0)
                    {
                        c.message.bits_spent = c.bits_spent;
                    }
                    else if (c.event_data is { bits_spent: > 0 })
                    {
                        c.message.bits_spent = c.event_data.bits_spent;
                    }
                }

                string msgId = c.event_data?.notice_type
                    ?? c.event_data?.msg_id
                    ?? (c.user_notice_params != null && c.user_notice_params.TryGetValue("msg-id", out var rawId) ? rawId : null)
                    ?? c.message.user_notice_params?.msg_id;

                if (!string.IsNullOrEmpty(msgId))
                {
                    if (msgId == "announcement" && !renderOptions.RenderAnnouncements)
                    {
                        chatRoot.comments.RemoveAt(i);
                        continue;
                    }

                    if (msgId == "raid" && !renderOptions.RenderRaidNotices)
                    {
                        chatRoot.comments.RemoveAt(i);
                        continue;
                    }

                    if (msgId is "sub" or "resub" or "subgift" or "submysterygift" or "viewermilestone" or "primepaidupgrade" or "giftpaidupgrade" or "anongiftpaidupgrade" or "standardpayforward" or "communitypayforward"
                        && !renderOptions.SubMessages)
                    {
                        chatRoot.comments.RemoveAt(i);
                        continue;
                    }
                }

                if (c.event_data is { is_highlighted: true } || msgId == "highlighted-message")
                {
                    c.message.user_notice_params ??= new UserNoticeParams { msg_id = "highlighted-message" };
                }

                if (c.user_notice_params != null)
                {
                    if (c.user_notice_params.TryGetValue("first-msg", out var fm) && fm is "1" or "true")
                    {
                        c.event_data ??= new CommentEventData();
                        c.event_data.is_first_message = true;
                    }
                    if (c.user_notice_params.TryGetValue("returning-chatter", out var rc) && rc is "1" or "true")
                    {
                        c.event_data ??= new CommentEventData();
                        c.event_data.is_returning_chatter = true;
                    }
                }
            }

            if (chatRoot.events.Count == 0)
            {
                _stickyBroadcastEvents = [];
                return;
            }

            // Build fast lookup tables for moderation matching
            var commentsById = new Dictionary<string, List<Comment>>(StringComparer.Ordinal);
            var commentsByUserId = new Dictionary<string, List<Comment>>(StringComparer.Ordinal);
            var commentsByUserLogin = new Dictionary<string, List<Comment>>(StringComparer.OrdinalIgnoreCase);

            foreach (var c in chatRoot.comments)
            {
                if (!string.IsNullOrEmpty(c._id))
                {
                    if (!commentsById.TryGetValue(c._id, out var list))
                    {
                        commentsById[c._id] = list = new List<Comment>(1);
                    }
                    list.Add(c);
                }

                if (!string.IsNullOrEmpty(c.commenter?._id))
                {
                    if (!commentsByUserId.TryGetValue(c.commenter._id, out var list))
                    {
                        commentsByUserId[c.commenter._id] = list = [];
                    }
                    list.Add(c);
                }

                if (!string.IsNullOrEmpty(c.commenter?.name))
                {
                    if (!commentsByUserLogin.TryGetValue(c.commenter.name, out var list))
                    {
                        commentsByUserLogin[c.commenter.name] = list = [];
                    }
                    list.Add(c);
                }
            }

            var syntheticComments = new List<Comment>();
            var stickyEvents = new List<ChatBroadcastEvent>();

            // Sort broadcast events chronologically so state transitions are always evaluated in order
            chatRoot.events.Sort(static (a, b) => (a?.content_offset_seconds ?? 0).CompareTo(b?.content_offset_seconds ?? 0));

            double earliestOffset = double.MaxValue;
            if (chatRoot.comments.Count > 0)
            {
                earliestOffset = Math.Min(earliestOffset, chatRoot.comments[0].content_offset_seconds);
            }
            if (chatRoot.events.Count > 0 && chatRoot.events[0] != null)
            {
                earliestOffset = Math.Min(earliestOffset, chatRoot.events[0].content_offset_seconds);
            }
            if (earliestOffset == double.MaxValue)
            {
                earliestOffset = 0.0;
            }
            double initialBaselineCutoff = Math.Max(earliestOffset, renderOptions.StartOverride > 0 ? renderOptions.StartOverride : 0.0) + 2.0;
            bool hasSeenInitialRoomstate = false;

            // State trackers for deduplicating scroll-injected notices
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

            foreach (var ev in chatRoot.events)
            {
                if (ev is null || ev.data.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var d = ev.data;
                switch (ev.event_type)
                {
                    case "clearmsg":
                    {
                        string targetMsgId = GetJsonString(d, "target_msg_id");
                        string targetLogin = GetJsonString(d, "target_user_login");

                        if (renderOptions.ModerationMode != ModerationDisplayMode.Off
                            && !string.IsNullOrEmpty(targetMsgId)
                            && commentsById.TryGetValue(targetMsgId, out var matches))
                        {
                            foreach (var target in matches)
                            {
                                double modAt = Math.Max(target.content_offset_seconds, ev.content_offset_seconds);
                                target.ModeratedAtSeconds = Math.Min(target.ModeratedAtSeconds ?? double.MaxValue, modAt);
                            }
                        }

                        if (renderOptions.ShowModerationNotices)
                        {
                            string notice = !string.IsNullOrEmpty(targetLogin)
                                ? $"A message from {targetLogin} was deleted."
                                : "A message was deleted by a moderator.";
                            syntheticComments.Add(CreateSyntheticNoticeComment(ev.content_offset_seconds, notice, compact: true));
                        }
                        break;
                    }

                    case "clearchat":
                    {
                        string action = (GetJsonString(d, "action") ?? "timeout").ToLowerInvariant();
                        string targetLogin = GetJsonString(d, "target_user_login");
                        string targetUserId = GetJsonString(d, "target_user_id");
                        int? durationSec = d.TryGetProperty("ban_duration_seconds", out var durEl) && durEl.ValueKind == JsonValueKind.Number && durEl.TryGetInt32(out int parsedDur)
                            ? parsedDur
                            : null;

                        if (action == "clear_all")
                        {
                            if (!renderOptions.EnableClearAll)
                            {
                                break;
                            }

                            if (renderOptions.ModerationMode != ModerationDisplayMode.Off)
                            {
                                foreach (var c in chatRoot.comments)
                                {
                                    if (c.content_offset_seconds <= ev.content_offset_seconds + 0.001)
                                    {
                                        c.ModeratedAtSeconds = Math.Min(c.ModeratedAtSeconds ?? double.MaxValue, ev.content_offset_seconds);
                                    }
                                }
                            }

                            if (renderOptions.ShowModerationNotices)
                            {
                                syntheticComments.Add(CreateSyntheticNoticeComment(ev.content_offset_seconds, "Chat was cleared by a moderator.", compact: true));
                            }
                        }
                        else
                        {
                            if (renderOptions.ModerationMode != ModerationDisplayMode.Off)
                            {
                                List<Comment> userComments = null;
                                if (!string.IsNullOrEmpty(targetUserId))
                                {
                                    commentsByUserId.TryGetValue(targetUserId, out userComments);
                                }
                                if (userComments is null && !string.IsNullOrEmpty(targetLogin))
                                {
                                    commentsByUserLogin.TryGetValue(targetLogin, out userComments);
                                }

                                if (userComments != null)
                                {
                                    foreach (var c in userComments)
                                    {
                                        if (c.content_offset_seconds <= ev.content_offset_seconds + 0.001)
                                        {
                                            c.ModeratedAtSeconds = Math.Min(c.ModeratedAtSeconds ?? double.MaxValue, ev.content_offset_seconds);
                                        }
                                    }
                                }
                            }

                            if (renderOptions.ShowModerationNotices && !string.IsNullOrEmpty(targetLogin))
                            {
                                string notice = action == "ban" || durationSec is null or <= 0
                                    ? $"{targetLogin} was permanently banned."
                                    : $"{targetLogin} was timed out for {durationSec.Value}s.";
                                syntheticComments.Add(CreateSyntheticNoticeComment(ev.content_offset_seconds, notice, compact: true));
                            }
                        }
                        break;
                    }

                    case "roomstate":
                    {
                        if (!renderOptions.ShowRoomstateNotices)
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
                            // Initial IRC channel-join snapshot: record the starting baseline state silently without spamming chat
                            if (eoVal.HasValue) prevEmoteOnly = eoVal.Value;
                            if (soVal.HasValue) prevSubsOnly = soVal.Value;
                            if (slowVal.HasValue) prevSlowSeconds = slowVal.Value;
                            if (foVal.HasValue) prevFollowersOnly = foVal.Value;
                            if (r9kVal.HasValue) prevR9k = r9kVal.Value;
                            break;
                        }

                        // Evaluate actual mid-stream changes against established baseline state.
                        // If a property was never seen in the baseline (prev is null), initialize it without claiming "no longer in X mode".
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

                        // If a partial ROOMSTATE packet turned one mode ON while omitted tags defaulted to OFF in the same packet,
                        // only commit & emit the mode(s) that turned ON.
                        if (turnedOnNotices.Count > 0)
                        {
                            foreach (var (commit, notice) in turnedOnNotices)
                            {
                                commit();
                                syntheticComments.Add(CreateSyntheticNoticeComment(ev.content_offset_seconds, notice, compact: true));
                            }
                        }
                        else if (turnedOffNotices.Count == 1)
                        {
                            var (commit, notice) = turnedOffNotices[0];
                            commit();
                            syntheticComments.Add(CreateSyntheticNoticeComment(ev.content_offset_seconds, notice, compact: true));
                        }
                        break;
                    }

                    case "channel_points":
                    {
                        if (!renderOptions.RenderChannelPointRedemptions)
                        {
                            break;
                        }

                        string userDisplay = GetJsonString(d, "user_display_name") ?? GetJsonString(d, "user_login") ?? "Chatter";
                        string rewardTitle = GetJsonString(d, "reward_title") ?? "Channel Point Reward";
                        int rewardCost = GetJsonInt(d, "reward_cost", 0);
                        string bgColorHex = GetJsonString(d, "reward_background_color");
                        string userInput = GetJsonString(d, "user_input");

                        uint accentColor = TryParseColorUint(bgColorHex, 0xFF9146FF);
                        string costPart = rewardCost > 0 ? $" ({rewardCost:N0})" : "";
                        string body = string.IsNullOrWhiteSpace(userInput)
                            ? $"{userDisplay} redeemed {rewardTitle}{costPart}"
                            : $"{userDisplay} redeemed {rewardTitle}{costPart}: {userInput.Trim()}";

                        syntheticComments.Add(CreateSyntheticNoticeComment(ev.content_offset_seconds, body, compact: false, accentColor: accentColor));
                        break;
                    }

                    case "raid":
                    {
                        if (!renderOptions.RenderRaidNotices)
                        {
                            break;
                        }

                        string pubsubType = (GetJsonString(d, "pubsub_type") ?? "").ToLowerInvariant();
                        string targetDisplay = GetJsonString(d, "target_display_name") ?? GetJsonString(d, "target_login");
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
                                    syntheticComments.Add(CreateSyntheticNoticeComment(ev.content_offset_seconds, body, compact: false, accentColor: 0xFF9146FF));
                                }
                            }
                        }
                        break;
                    }

                    case "prediction":
                    {
                        if (renderOptions.PredictionMode == OverlayWidgetMode.StickyTop)
                        {
                            stickyEvents.Add(ev);
                        }
                        else if (renderOptions.PredictionMode == OverlayWidgetMode.ChatScroll)
                        {
                            string predId = GetJsonString(d, "prediction_id") ?? "";
                            string title = GetJsonString(d, "title") ?? "Prediction";
                            string status = (GetJsonString(d, "status") ?? "ACTIVE").ToUpperInvariant();
                            string winningId = GetJsonString(d, "winning_outcome_id");

                            seenPredictions.TryGetValue(predId, out string prevStatus);
                            if (prevStatus != status)
                            {
                                seenPredictions[predId] = status;
                                string scrollText = null;
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
                                    string winnerTitle = FindWinningOutcomeTitle(d, winningId);
                                    scrollText = !string.IsNullOrEmpty(winnerTitle)
                                        ? $"Prediction resolved: \"{title}\" — Winner: {winnerTitle}"
                                        : $"Prediction resolved: \"{title}\"";
                                }
                                else if (status == "CANCELED")
                                {
                                    scrollText = $"Prediction canceled: \"{title}\"";
                                }

                                if (scrollText != null)
                                {
                                    syntheticComments.Add(CreateSyntheticNoticeComment(ev.content_offset_seconds, scrollText, compact: false, accentColor: 0xFF387AFF));
                                }
                            }
                        }
                        break;
                    }

                    case "poll":
                    {
                        if (renderOptions.PollMode == OverlayWidgetMode.StickyTop)
                        {
                            stickyEvents.Add(ev);
                        }
                        else if (renderOptions.PollMode == OverlayWidgetMode.ChatScroll)
                        {
                            string pollId = GetJsonString(d, "poll_id") ?? "";
                            string title = GetJsonString(d, "title") ?? "Poll";
                            string pubsubType = (GetJsonString(d, "pubsub_type") ?? "").ToUpperInvariant();
                            string status = (GetJsonString(d, "status") ?? "ACTIVE").ToUpperInvariant();
                            if (pubsubType == "POLL_COMPLETE") status = "COMPLETED";

                            seenPolls.TryGetValue(pollId, out string prevStatus);
                            if (prevStatus != status)
                            {
                                seenPolls[pollId] = status;
                                if (status == "ACTIVE" && prevStatus is null)
                                {
                                    syntheticComments.Add(CreateSyntheticNoticeComment(ev.content_offset_seconds, $"Poll started: \"{title}\"", compact: false, accentColor: 0xFF00DB84));
                                }
                                else if (status is "COMPLETED" or "TERMINATED" && prevStatus is not ("COMPLETED" or "TERMINATED"))
                                {
                                    string winnerChoice = FindWinningPollChoice(d);
                                    string scrollText = !string.IsNullOrEmpty(winnerChoice)
                                        ? $"Poll ended: \"{title}\" — Winner: {winnerChoice}"
                                        : $"Poll ended: \"{title}\"";
                                    syntheticComments.Add(CreateSyntheticNoticeComment(ev.content_offset_seconds, scrollText, compact: false, accentColor: 0xFF00DB84));
                                }
                            }
                        }
                        break;
                    }

                    case "pinned_chat":
                    {
                        if (renderOptions.PinnedChatMode == OverlayWidgetMode.StickyTop)
                        {
                            stickyEvents.Add(ev);
                        }
                        else if (renderOptions.PinnedChatMode == OverlayWidgetMode.ChatScroll)
                        {
                            string action = (GetJsonString(d, "action") ?? "pin-message").ToLowerInvariant();
                            string pinId = GetJsonString(d, "pin_id") ?? "";
                            if (action == "pin-message" && seenPins.Add(pinId))
                            {
                                string sender = GetJsonString(d, "sender_display_name") ?? "Chatter";
                                string msg = (GetJsonString(d, "message_text") ?? "").Trim();
                                string pinnedBy = GetJsonString(d, "pinned_by_display_name");
                                string prefix = !string.IsNullOrEmpty(pinnedBy) ? $"Pinned by {pinnedBy} — " : "Pinned message — ";
                                syntheticComments.Add(CreateSyntheticNoticeComment(ev.content_offset_seconds, $"{prefix}{sender}: {msg}", compact: false, accentColor: 0xFF9146FF));
                            }
                        }
                        break;
                    }

                    case "hype_train":
                    {
                        if (renderOptions.HypeTrainMode == OverlayWidgetMode.StickyTop)
                        {
                            stickyEvents.Add(ev);
                        }
                        else if (renderOptions.HypeTrainMode == OverlayWidgetMode.ChatScroll)
                        {
                            string pubsubType = (GetJsonString(d, "pubsub_type") ?? "").ToLowerInvariant();
                            int level = Math.Max(1, GetJsonInt(d, "level", 1));

                            if (pubsubType == "hype-train-start" && !hypeTrainActive)
                            {
                                hypeTrainActive = true;
                                lastHypeTrainLevel = level;
                                syntheticComments.Add(CreateSyntheticNoticeComment(ev.content_offset_seconds, $"Hype Train started at Level {level}!", compact: false, accentColor: 0xFFFFB31A));
                            }
                            else if (pubsubType == "hype-train-level-up" && level > lastHypeTrainLevel)
                            {
                                hypeTrainActive = true;
                                lastHypeTrainLevel = level;
                                syntheticComments.Add(CreateSyntheticNoticeComment(ev.content_offset_seconds, $"Hype Train leveled up to Level {level}!", compact: false, accentColor: 0xFFFFB31A));
                            }
                            else if (pubsubType == "hype-train-end" && hypeTrainActive)
                            {
                                hypeTrainActive = false;
                                syntheticComments.Add(CreateSyntheticNoticeComment(ev.content_offset_seconds, $"Hype Train ended at Level {level}!", compact: false, accentColor: 0xFFFFB31A));
                            }
                        }
                        break;
                    }
                }
            }

            if (renderOptions.ModerationMode == ModerationDisplayMode.Hide)
            {
                chatRoot.comments.RemoveAll(static c => c.ModeratedAtSeconds.HasValue);
            }
            else if (renderOptions.UpdateRate > 0)
            {
                foreach (var c in chatRoot.comments)
                {
                    if (c.ModeratedAtSeconds is double modSec)
                    {
                        c.ModeratedAtSeconds = renderOptions.UpdateRate > 1
                            ? Math.Floor(modSec)
                            : Math.Floor(modSec / renderOptions.UpdateRate) * renderOptions.UpdateRate;
                    }
                }
            }

            if (syntheticComments.Count > 0)
            {
                chatRoot.comments.AddRange(syntheticComments);
                // Stable chronological sort by content_offset_seconds
                var sorted = chatRoot.comments.OrderBy(static c => c.content_offset_seconds).ToList();
                chatRoot.comments.Clear();
                chatRoot.comments.AddRange(sorted);
            }

            _stickyBroadcastEvents = stickyEvents;
        }

        private static Comment CreateSyntheticNoticeComment(double offsetSeconds, string bodyText, bool compact, uint? accentColor = null)
        {
            return new Comment
            {
                _id = $"synthetic-{Guid.NewGuid():N}",
                content_offset_seconds = offsetSeconds,
                created_at = DateTime.UtcNow,
                commenter = new Commenter
                {
                    display_name = "Twitch",
                    name = "twitch",
                    _id = "0"
                },
                message = new Message
                {
                    body = bodyText,
                    fragments = [new Fragment { text = bodyText }],
                    user_badges = [],
                    emoticons = []
                },
                IsSyntheticNotice = true,
                IsSyntheticCompactNotice = compact,
                SyntheticAccentColor = accentColor
            };
        }

        private static string FindWinningOutcomeTitle(JsonElement d, string winningId)
        {
            if (string.IsNullOrEmpty(winningId) || !d.TryGetProperty("outcomes", out var outcomesEl) || outcomesEl.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            long totalPts = 0;
            string winTitle = null;
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

        private static string FindWinningPollChoice(JsonElement d)
        {
            if (!d.TryGetProperty("choices", out var choicesEl) || choicesEl.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string bestTitle = null;
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

        private static string GetJsonString(JsonElement obj, string propName)
        {
            return obj.TryGetProperty(propName, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;
        }

        private static int GetJsonInt(JsonElement obj, string propName, int fallback)
        {
            if (obj.TryGetProperty(propName, out var el))
            {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v))
                    return v;
                if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out int sv))
                    return sv;
            }
            return fallback;
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

        private static uint TryParseColorUint(string hex, uint fallback)
        {
            if (!string.IsNullOrWhiteSpace(hex) && SKColor.TryParse(hex, out var parsed))
            {
                return (uint)parsed;
            }
            return fallback;
        }

        private static SKColor GetAnnouncementAccentColor(string colorName)
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

        private static SKColor GetHypeChatLevelColor(string level)
        {
            return (level ?? "ONE").ToUpperInvariant() switch
            {
                "ONE" or "TWO" => new SKColor(0xFF008069),
                "THREE" => new SKColor(0xFF006080),
                "FOUR" => new SKColor(0xFF6441A5),
                "FIVE" or "SIX" or "SEVEN" => new SKColor(0xFFD97706),
                _ => new SKColor(0xFFE11D48)
            };
        }

        private static string FormatHypeChatCurrency(CommentHypeChatInfo hypeChat)
        {
            double val = hypeChat.EffectiveDecimalAmount;
            string curr = (hypeChat.currency ?? "USD").ToUpperInvariant();
            string symbol = curr switch
            {
                "USD" => "$",
                "EUR" => "€",
                "GBP" => "£",
                "JPY" => "¥",
                "CAD" => "CA$",
                "AUD" => "A$",
                _ => ""
            };

            string formattedNum = curr == "JPY" || val % 1 == 0
                ? val.ToString("N0", CultureInfo.InvariantCulture)
                : val.ToString("F2", CultureInfo.InvariantCulture);

            return string.IsNullOrEmpty(symbol) ? $"{formattedNum} {curr}" : $"{symbol}{formattedNum}";
        }

        private bool TryDrawExtendedComment(
            Comment comment,
            List<SectionImage> sectionImages,
            List<EmotePosition> emotePositionList,
            int commentIndex,
            ref Point drawPos,
            ref Point defaultPos,
            SectionRenderContext ctx,
            out SKColor? customAccentColor)
        {
            customAccentColor = null;

            // 1. Synthetic compact 1-line gray system notices (moderation, roomstate)
            if (comment.IsSyntheticNotice && comment.IsSyntheticCompactNotice)
            {
                drawPos.X = renderOptions.SidePadding;
                defaultPos.X = drawPos.X;
                if (renderOptions.Timestamp)
                {
                    DrawTimestamp(comment, sectionImages, ref drawPos, ref defaultPos, ctx);
                }

                using var mutedFont = ctx.MessageFont.Clone();
                mutedFont.Color = renderOptions.MessageColor.WithAlpha(155);
                DrawText(comment.message.body, mutedFont, false, sectionImages, ref drawPos, defaultPos, false, ctx);
                foreach (var s in sectionImages) s.Flush();
                return true;
            }

            // 2. Synthetic accented notices (Channel Points, Outgoing Raid, Scroll-mode Overlay transitions)
            if (comment.IsSyntheticNotice && !comment.IsSyntheticCompactNotice)
            {
                customAccentColor = new SKColor(comment.SyntheticAccentColor ?? 0xFF9146FF);
                drawPos.X += renderOptions.AccentIndentWidth - renderOptions.AccentStrokeWidth / 2;
                defaultPos.X = drawPos.X;

                if (renderOptions.Timestamp)
                {
                    DrawTimestamp(comment, sectionImages, ref drawPos, ref defaultPos, ctx);
                }

                DrawMessage(comment, sectionImages, emotePositionList, false, ref drawPos, defaultPos, ctx);
                foreach (var s in sectionImages) s.Flush();
                return true;
            }

            // 3. v2.0-extended USERNOTICE (event_type == "usernotice" or event_data.notice_type != null)
            string noticeType = comment.event_data?.notice_type
                ?? (comment.user_notice_params != null && comment.user_notice_params.TryGetValue("msg-id", out var rawNotice) ? rawNotice : null);

            if (comment.event_type == "usernotice" || !string.IsNullOrEmpty(noticeType))
            {
                string sysMsg = comment.event_data?.system_message
                    ?? (comment.user_notice_params != null && comment.user_notice_params.TryGetValue("system-msg", out var rawSys) ? rawSys.Replace(@"\s", " ") : null);
                string userMsg = comment.event_data?.user_message;

                if (noticeType == "announcement")
                {
                    customAccentColor = GetAnnouncementAccentColor(comment.event_data?.announcement_color);
                    drawPos.X += renderOptions.AccentIndentWidth - renderOptions.AccentStrokeWidth / 2;
                    defaultPos.X = drawPos.X;

                    // Draw compact inline "📢" announcement label + normal user message on the same row (zero wasted vertical lines)
                    using var badgeFont = ctx.NameFont.Clone();
                    badgeFont.Color = customAccentColor.Value;
                    DrawText("[Announcement]", badgeFont, true, sectionImages, ref drawPos, defaultPos, false, ctx);
                    DrawNonAccentedMessage(comment, sectionImages, emotePositionList, false, commentIndex, ref drawPos, ref defaultPos, ctx);
                    return true;
                }

                // Sub / Resub / Subgift / Submysterygift / Raid / Viewermilestone with v2.0-extended event_data
                if (comment.event_data != null && !string.IsNullOrEmpty(sysMsg))
                {
                    if (!renderOptions.SubMessages && noticeType is "sub" or "resub" or "subgift" or "submysterygift" or "viewermilestone")
                    {
                        return false;
                    }

                    customAccentColor = noticeType == "viewermilestone" ? new SKColor(0xFF00C7AC) : Purple;
                    drawPos.X += renderOptions.AccentIndentWidth - renderOptions.AccentStrokeWidth / 2;
                    defaultPos.X = drawPos.X;

                    var iconType = noticeType switch
                    {
                        "sub" or "resub" => comment.event_data.sub_plan == "Prime" ? HighlightType.SubscribedPrime : HighlightType.SubscribedTier,
                        "subgift" => HighlightType.GiftedSingle,
                        "submysterygift" => HighlightType.GiftedMany,
                        "viewermilestone" => HighlightType.WatchStreak,
                        "raid" => HighlightType.Raid,
                        _ => HighlightType.SubscribedTier
                    };

                    var highlightIcon = highlightIcons.GetHighlightIcon(iconType, ctx.MessageFont.Color);
                    int iconY = (int)((renderOptions.SectionHeight - highlightIcon.Height) / 2.0);
                    Point iconPoint = new() { X = drawPos.X, Y = iconY };
                    Point messageLineStart = drawPos;

                    bool hasCustomUserMessage = !string.IsNullOrWhiteSpace(userMsg)
                        && !string.Equals(userMsg.Trim(), sysMsg.Trim(), StringComparison.OrdinalIgnoreCase);

                    if (noticeType is "sub" or "resub" or "viewermilestone")
                    {
                        string compactSummary = noticeType == "viewermilestone"
                            ? BuildCompactWatchStreakSummary(comment, sysMsg)
                            : BuildCompactSubSummary(comment, sysMsg);

                        DrawCompactSubOrStreakHeaderLine(
                            comment,
                            sectionImages,
                            ref drawPos,
                            highlightIcon,
                            iconPoint,
                            compactSummary,
                            includeUsername: !hasCustomUserMessage,
                            ctx);
                    }
                    else if (noticeType is "subgift" or "submysterygift")
                    {
                        DrawCompactGiftMessageLine(comment, sysMsg, sectionImages, ref drawPos, highlightIcon, iconPoint, ctx);
                    }
                    else
                    {
                        var canvas = sectionImages[^1].Canvas;
                        canvas.DrawImage(highlightIcon, iconPoint.X, iconPoint.Y);
                        drawPos.X += highlightIcon.Width + renderOptions.WordSpacing;
                        defaultPos.X = drawPos.X;

                        using var sysFont = ctx.MessageFont.Clone();
                        sysFont.Color = renderOptions.MessageColor.WithAlpha(220);
                        DrawText(sysMsg, sysFont, false, sectionImages, ref drawPos, defaultPos, false, ctx);
                    }

                    if (hasCustomUserMessage)
                    {
                        AddImageSection(sectionImages, ref drawPos, messageLineStart);
                        drawPos = messageLineStart;
                        defaultPos = messageLineStart;
                        DrawNonAccentedMessage(comment, sectionImages, emotePositionList, false, commentIndex, ref drawPos, ref defaultPos, ctx);
                    }
                    else
                    {
                        foreach (var s in sectionImages) s.Flush();
                    }

                    return true;
                }
            }

            return false;
        }

        [GeneratedRegex(@"subscribed for (\d+) months?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex SubMonthsRegex { get; }

        [GeneratedRegex(@"on a (\d+) months? streak", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex SubStreakRegex { get; }

        [GeneratedRegex(@"(\d+)\s+consecutive\s+streams?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex WatchStreakCountRegex { get; }

        [GeneratedRegex(@"is gifting (\d+)\s+Tier\s+(\d)\s+Subs?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex GiftMysteryCountRegex { get; }

        [GeneratedRegex(@"gifted a total of (\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex GiftTotalCountRegex { get; }

        [GeneratedRegex(@"gifted a (?:Tier\s+(\d)|Prime)\s+sub to\s+([^\s!]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex GiftSingleRecipientRegex { get; }

        [GeneratedRegex(@"got from\s+([^\s!]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex GiftContinuingFromRegex { get; }

        private static string GetNoticeParam(Comment comment, string key)
        {
            if (comment.user_notice_params != null && comment.user_notice_params.TryGetValue(key, out var val) && !string.IsNullOrEmpty(val))
            {
                return val;
            }

            if (comment.event_data?.notice_params != null && comment.event_data.notice_params.TryGetValue(key, out var edVal) && !string.IsNullOrEmpty(edVal))
            {
                return edVal;
            }

            return null;
        }

        private static string BuildCompactSubSummary(Comment comment, string rawSystemText)
        {
            rawSystemText ??= "";

            // 1. Determine Prime vs Tier 1/2/3 Sub
            string subPlanCode = comment.event_data?.sub_plan ?? GetNoticeParam(comment, "msg-param-sub-plan");
            string planLabel;
            if (!string.IsNullOrEmpty(subPlanCode))
            {
                planLabel = subPlanCode.Trim() switch
                {
                    "Prime" or "prime" => "Prime Sub",
                    "2000" => "Tier 2 Sub",
                    "3000" => "Tier 3 Sub",
                    _ => "Tier 1 Sub"
                };
            }
            else if (rawSystemText.Contains("converted from", StringComparison.OrdinalIgnoreCase))
            {
                int primeIdx = rawSystemText.LastIndexOf("Prime", StringComparison.OrdinalIgnoreCase);
                int t1Idx = rawSystemText.LastIndexOf("Tier 1", StringComparison.OrdinalIgnoreCase);
                int t2Idx = rawSystemText.LastIndexOf("Tier 2", StringComparison.OrdinalIgnoreCase);
                int t3Idx = rawSystemText.LastIndexOf("Tier 3", StringComparison.OrdinalIgnoreCase);
                int maxIdx = Math.Max(Math.Max(primeIdx, t1Idx), Math.Max(t2Idx, t3Idx));

                if (maxIdx >= 0 && maxIdx == primeIdx) planLabel = "Prime Sub";
                else if (maxIdx >= 0 && maxIdx == t3Idx) planLabel = "Tier 3 Sub";
                else if (maxIdx >= 0 && maxIdx == t2Idx) planLabel = "Tier 2 Sub";
                else planLabel = "Tier 1 Sub";
            }
            else if (rawSystemText.Contains("Prime", StringComparison.OrdinalIgnoreCase))
            {
                planLabel = "Prime Sub";
            }
            else if (rawSystemText.Contains("Tier 3", StringComparison.OrdinalIgnoreCase))
            {
                planLabel = "Tier 3 Sub";
            }
            else if (rawSystemText.Contains("Tier 2", StringComparison.OrdinalIgnoreCase))
            {
                planLabel = "Tier 2 Sub";
            }
            else
            {
                planLabel = "Tier 1 Sub";
            }

            // 2. Determine cumulative months (# Months)
            int months = comment.event_data?.cumulative_months ?? 0;
            if (months <= 0)
            {
                string paramMonths = GetNoticeParam(comment, "msg-param-cumulative-months") ?? GetNoticeParam(comment, "msg-param-months");
                if (!string.IsNullOrEmpty(paramMonths) && int.TryParse(paramMonths, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pm))
                {
                    months = pm;
                }
            }
            if (months <= 0 && !string.IsNullOrEmpty(rawSystemText))
            {
                var m = SubMonthsRegex.Match(rawSystemText);
                if (m.Success && int.TryParse(m.Groups[1].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rm))
                {
                    months = rm;
                }
            }

            // 3. Determine streak months (# Month Streak)
            int streak = comment.event_data?.streak_months ?? 0;
            if (streak <= 0)
            {
                string shareStreak = GetNoticeParam(comment, "msg-param-should-share-streak");
                string paramStreak = GetNoticeParam(comment, "msg-param-streak-months");
                if (shareStreak != "0" && !string.IsNullOrEmpty(paramStreak) && int.TryParse(paramStreak, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ps))
                {
                    streak = ps;
                }
            }
            if (streak <= 0 && !string.IsNullOrEmpty(rawSystemText))
            {
                var sm = SubStreakRegex.Match(rawSystemText);
                if (sm.Success && int.TryParse(sm.Groups[1].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rsm))
                {
                    streak = rsm;
                }
            }

            var sb = new StringBuilder(planLabel);
            if (months > 0)
            {
                sb.Append(months == 1 ? " • 1 Month" : $" • {months} Months");
            }
            if (streak > 0)
            {
                sb.Append($" • {streak} Month Streak");
            }

            return sb.ToString();
        }

        private static string BuildCompactWatchStreakSummary(Comment comment, string rawSystemText)
        {
            rawSystemText ??= "";

            int streakCount = comment.event_data?.milestone_value ?? 0;
            if (streakCount <= 0)
            {
                string paramVal = GetNoticeParam(comment, "msg-param-value");
                if (!string.IsNullOrEmpty(paramVal) && int.TryParse(paramVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pv))
                {
                    streakCount = pv;
                }
            }
            if (streakCount <= 0 && !string.IsNullOrEmpty(rawSystemText))
            {
                var m = WatchStreakCountRegex.Match(rawSystemText);
                if (m.Success && int.TryParse(m.Groups[1].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rv))
                {
                    streakCount = rv;
                }
            }

            return streakCount > 0
                ? $"{streakCount} Stream Watch Streak!"
                : "Stream Watch Streak!";
        }

        private void DrawCompactSubOrStreakHeaderLine(
            Comment comment,
            List<SectionImage> sectionImages,
            ref Point drawPos,
            SKImage highlightIcon,
            Point iconPoint,
            string compactSummary,
            bool includeUsername,
            SectionRenderContext ctx)
        {
            var canvas = sectionImages[^1].Canvas;
            canvas.DrawImage(highlightIcon, iconPoint.X, iconPoint.Y);

            float curX = iconPoint.X + highlightIcon.Width + Math.Max(4, renderOptions.WordSpacing / 2);
            float maxRightX = renderOptions.ChatWidth - renderOptions.SidePadding;
            float availableWidth = Math.Max(40f, maxRightX - curX);

            using var msgFont = ctx.MessageFont.Clone();
            if (!includeUsername)
            {
                string lineText = compactSummary;
                float measured = msgFont.MeasureText(lineText);
                if (measured > availableWidth && measured > 1f)
                {
                    msgFont.TextSize = Math.Max(7f, msgFont.TextSize * (availableWidth / measured));
                }

                DrawSingleLineTextDirect(canvas, lineText, curX, drawPos.Y, msgFont, ctx);
                drawPos.X = (int)Math.Ceiling(curX + msgFont.MeasureText(lineText));
            }
            else
            {
                string displayName = comment.commenter?.display_name ?? comment.commenter?.name ?? "User";
                string tailSummary = $" • {compactSummary}";

                using var nameFont = ctx.NameFont.Clone();
                nameFont.Color = Purple;

                float totalWidth = nameFont.MeasureText(displayName) + msgFont.MeasureText(tailSummary);

                if (totalWidth > availableWidth && totalWidth > 1f)
                {
                    float scale = availableWidth / totalWidth;
                    msgFont.TextSize = Math.Max(7f, msgFont.TextSize * scale);
                    nameFont.TextSize = Math.Max(7f, nameFont.TextSize * scale);
                }

                DrawSingleLineTextDirect(canvas, displayName, curX, drawPos.Y, nameFont, ctx);
                curX += nameFont.MeasureText(displayName);

                DrawSingleLineTextDirect(canvas, tailSummary, curX, drawPos.Y, msgFont, ctx);
                curX += msgFont.MeasureText(tailSummary);

                drawPos.X = (int)Math.Ceiling(curX);
            }
        }

        private void DrawCompactGiftMessageLine(
            Comment comment,
            string rawSystemText,
            List<SectionImage> sectionImages,
            ref Point drawPos,
            SKImage highlightIcon,
            Point iconPoint,
            SectionRenderContext ctx)
        {
            rawSystemText ??= "";
            var canvas = sectionImages[^1].Canvas;
            canvas.DrawImage(highlightIcon, iconPoint.X, iconPoint.Y);

            float curX = iconPoint.X + highlightIcon.Width + Math.Max(4, renderOptions.WordSpacing / 2);
            float maxRightX = renderOptions.ChatWidth - renderOptions.SidePadding;
            float availableWidth = Math.Max(40f, maxRightX - curX);

            bool isAnonymous = comment.commenter?._id is "274598607"
                || rawSystemText.StartsWith("An anonymous", StringComparison.OrdinalIgnoreCase);
            string gifterName = isAnonymous
                ? "Anonymous"
                : (comment.commenter?.display_name ?? comment.commenter?.name ?? "User");

            string subPlanCode = comment.event_data?.sub_plan ?? GetNoticeParam(comment, "msg-param-sub-plan");
            string tierShort = subPlanCode switch
            {
                "2000" => "Tier 2",
                "3000" => "Tier 3",
                _ => rawSystemText.Contains("Tier 3", StringComparison.OrdinalIgnoreCase) ? "Tier 3"
                    : rawSystemText.Contains("Tier 2", StringComparison.OrdinalIgnoreCase) ? "Tier 2"
                    : "Tier 1"
            };

            // Determine total gifts in channel if present
            int totalGifts = comment.event_data?.sender_total_gifts ?? 0;
            if (totalGifts <= 0)
            {
                string paramTotal = GetNoticeParam(comment, "msg-param-sender-count");
                if (!string.IsNullOrEmpty(paramTotal) && int.TryParse(paramTotal, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pt))
                {
                    totalGifts = pt;
                }
            }
            if (totalGifts <= 0)
            {
                var tm = GiftTotalCountRegex.Match(rawSystemText);
                if (tm.Success && int.TryParse(tm.Groups[1].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rt))
                {
                    totalGifts = rt;
                }
            }

            // Case 1: Community Mystery Gift ("is gifting X Tier Y Subs to Channel's community!")
            int massCount = comment.event_data?.mass_gift_count ?? 0;
            if (massCount <= 0)
            {
                string paramMass = GetNoticeParam(comment, "msg-param-mass-gift-count");
                if (!string.IsNullOrEmpty(paramMass) && int.TryParse(paramMass, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pmc))
                {
                    massCount = pmc;
                }
            }
            if (massCount <= 0 && rawSystemText.Contains("is gifting", StringComparison.OrdinalIgnoreCase))
            {
                var mm = GiftMysteryCountRegex.Match(rawSystemText);
                if (mm.Success && int.TryParse(mm.Groups[1].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rmc))
                {
                    massCount = rmc;
                }
            }

            using var nameFont = ctx.NameFont.Clone();
            nameFont.Color = Purple;
            using var msgFont = ctx.MessageFont.Clone();

            if (massCount > 0 || comment.event_data?.notice_type == "submysterygift")
            {
                int count = Math.Max(1, massCount);
                string subWord = count == 1 ? "Sub" : "Subs";
                string summary = totalGifts > 0
                    ? $" • Gifted {count} {tierShort} {subWord} • {totalGifts} Total"
                    : $" • Gifted {count} {tierShort} {subWord}";

                float totalW = nameFont.MeasureText(gifterName) + msgFont.MeasureText(summary);
                if (totalW > availableWidth && totalW > 1f)
                {
                    float scale = availableWidth / totalW;
                    nameFont.TextSize = Math.Max(7f, nameFont.TextSize * scale);
                    msgFont.TextSize = Math.Max(7f, msgFont.TextSize * scale);
                }

                DrawSingleLineTextDirect(canvas, gifterName, curX, drawPos.Y, nameFont, ctx);
                curX += nameFont.MeasureText(gifterName);
                DrawSingleLineTextDirect(canvas, summary, curX, drawPos.Y, msgFont, ctx);
                curX += msgFont.MeasureText(summary);
                drawPos.X = (int)Math.Ceiling(curX);
                return;
            }

            // Case 2: Single Recipient Gift ("gifted a Tier X sub to Recipient!")
            string recipient = comment.event_data?.recipient_display_name
                ?? comment.event_data?.recipient_user_name
                ?? GetNoticeParam(comment, "msg-param-recipient-display-name")
                ?? GetNoticeParam(comment, "msg-param-recipient-user-name");

            if (string.IsNullOrEmpty(recipient) && rawSystemText.Contains("gifted a ", StringComparison.OrdinalIgnoreCase))
            {
                var rm = GiftSingleRecipientRegex.Match(rawSystemText);
                if (rm.Success)
                {
                    recipient = rm.Groups[2].Value.TrimEnd('!', '.');
                }
            }

            if (!string.IsNullOrEmpty(recipient))
            {
                float arrowSlotW = MeasureVectorRightArrowSlot(msgFont);
                string tail = $" • {tierShort} Gift Sub";

                float totalW = nameFont.MeasureText(gifterName)
                    + arrowSlotW
                    + nameFont.MeasureText(recipient)
                    + msgFont.MeasureText(tail);

                if (totalW > availableWidth && totalW > 1f)
                {
                    float scale = availableWidth / totalW;
                    nameFont.TextSize = Math.Max(7f, nameFont.TextSize * scale);
                    msgFont.TextSize = Math.Max(7f, msgFont.TextSize * scale);
                    arrowSlotW = MeasureVectorRightArrowSlot(msgFont);
                }

                DrawSingleLineTextDirect(canvas, gifterName, curX, drawPos.Y, nameFont, ctx);
                curX += nameFont.MeasureText(gifterName);

                DrawVectorRightArrow(canvas, curX, drawPos.Y, msgFont, ctx);
                curX += arrowSlotW;

                DrawSingleLineTextDirect(canvas, recipient, curX, drawPos.Y, nameFont, ctx);
                curX += nameFont.MeasureText(recipient);

                DrawSingleLineTextDirect(canvas, tail, curX, drawPos.Y, msgFont, ctx);
                curX += msgFont.MeasureText(tail);

                drawPos.X = (int)Math.Ceiling(curX);
                return;
            }

            // Case 3: Continuing Gift or Paying Forward
            if (rawSystemText.Contains("continuing the Gift Sub", StringComparison.OrdinalIgnoreCase))
            {
                var cm = GiftContinuingFromRegex.Match(rawSystemText);
                string fromUser = cm.Success && !rawSystemText.Contains("anonymous user", StringComparison.OrdinalIgnoreCase)
                    ? cm.Groups[1].Value.TrimEnd('!', '.')
                    : null;
                string tail = !string.IsNullOrEmpty(fromUser)
                    ? $" • Continuing Gift Sub ({fromUser})"
                    : " • Continuing Gift Sub";

                float totalW = nameFont.MeasureText(gifterName) + msgFont.MeasureText(tail);
                if (totalW > availableWidth && totalW > 1f)
                {
                    float scale = availableWidth / totalW;
                    nameFont.TextSize = Math.Max(7f, nameFont.TextSize * scale);
                    msgFont.TextSize = Math.Max(7f, msgFont.TextSize * scale);
                }

                DrawSingleLineTextDirect(canvas, gifterName, curX, drawPos.Y, nameFont, ctx);
                curX += nameFont.MeasureText(gifterName);
                DrawSingleLineTextDirect(canvas, tail, curX, drawPos.Y, msgFont, ctx);
                curX += msgFont.MeasureText(tail);
                drawPos.X = (int)Math.Ceiling(curX);
                return;
            }

            // Fallback: single-line auto-fitted text
            float rawW = msgFont.MeasureText(rawSystemText);
            if (rawW > availableWidth && rawW > 1f)
            {
                msgFont.TextSize = Math.Max(7f, msgFont.TextSize * (availableWidth / rawW));
            }
            DrawSingleLineTextDirect(canvas, rawSystemText, curX, drawPos.Y, msgFont, ctx);
            drawPos.X = (int)Math.Ceiling(curX + msgFont.MeasureText(rawSystemText));
        }

        private static float MeasureVectorRightArrowSlot(SKPaint font)
        {
            float padX = Math.Max(4f, font.TextSize * 0.34f);
            float arrowW = Math.Max(8f, font.TextSize * 0.62f);
            return padX * 2f + arrowW;
        }

        private void DrawVectorRightArrow(SKCanvas canvas, float x, float baselineY, SKPaint font, SectionRenderContext ctx)
        {
            float padX = Math.Max(4f, font.TextSize * 0.34f);
            float arrowW = Math.Max(8f, font.TextSize * 0.62f);

            SKRect capBounds = new();
            font.MeasureText("H", ref capBounds);
            float centerY = baselineY - (capBounds.Height > 1f ? capBounds.Height * 0.50f : font.TextSize * 0.36f);

            float startX = x + padX;
            float endX = startX + arrowW;
            float headLen = Math.Max(3.5f, arrowW * 0.42f);
            float headHalfH = Math.Max(3.0f, arrowW * 0.36f);
            float strokeW = Math.Max(1.8f, font.TextSize * 0.11f);

            using var path = new SKPath();
            path.MoveTo(startX, centerY);
            path.LineTo(endX - strokeW * 0.25f, centerY);
            path.MoveTo(endX - headLen, centerY - headHalfH);
            path.LineTo(endX, centerY);
            path.LineTo(endX - headLen, centerY + headHalfH);

            if (renderOptions.Outline && ctx.OutlinePaint != null)
            {
                using var outlineArrowPaint = new SKPaint
                {
                    Color = ctx.OutlinePaint.Color,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = strokeW + ctx.OutlinePaint.StrokeWidth,
                    StrokeCap = SKStrokeCap.Round,
                    StrokeJoin = SKStrokeJoin.Round,
                    IsAntialias = true
                };
                canvas.DrawPath(path, outlineArrowPaint);
            }

            using var arrowPaint = new SKPaint
            {
                Color = font.Color,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = strokeW,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
                IsAntialias = true
            };
            canvas.DrawPath(path, arrowPaint);
        }

        private float DrawSingleLineTextDirect(SKCanvas canvas, string text, float x, float y, SKPaint font, SectionRenderContext ctx)
        {
            if (string.IsNullOrEmpty(text))
            {
                return x;
            }

            if (font.ContainsGlyphs(text))
            {
                if (renderOptions.Outline && ctx?.OutlinePaint != null)
                {
                    using var outlinePath = font.GetTextPath(text, x, y);
                    canvas.DrawPath(outlinePath, ctx.OutlinePaint);
                }

                canvas.DrawText(text, x, y, font);
                return x + font.MeasureText(text);
            }

            // Fallback per-run when text contains characters/symbols/emojis not in the primary font
            Span<char> emojiKeyStack = stackalloc char[16];
            float curX = x;
            int i = 0;
            while (i < text.Length)
            {
                int elemLen = StringInfo.GetNextTextElementLength(text.AsSpan(i));
                if (elemLen <= 0) elemLen = 1;
                ReadOnlySpan<char> elem = text.AsSpan(i, elemLen);

                if (!font.ContainsGlyphs(elem))
                {
                    // Check if this text element is a known emoji in the emoji cache
                    if (elemLen <= emojiKeyStack.Length)
                    {
                        int written = elem.CopyToExcept(emojiKeyStack, EmojiExcludeChars);
                        if (written > 0 && TryGetEmojiImage(emojiKeyStack[..written], out var emojiImg) && emojiImg != null)
                        {
                            float targetSize = Math.Max(10f, font.TextSize * 1.05f);
                            float topY = y - targetSize * 0.82f;
                            var destRect = new SKRect(curX, topY, curX + targetSize, topY + targetSize);
                            canvas.DrawImage(emojiImg, destRect);
                            curX += targetSize + Math.Max(1f, font.TextSize * 0.08f);
                            i += elemLen;
                            continue;
                        }
                    }
                }

                SKPaint activeFont = font;
                SKPaint clonedFallback = null;
                if (!font.ContainsGlyphs(elem) && ctx?.FallbackFontCache != null)
                {
                    int codepoint = elemLen >= 2 && char.IsHighSurrogate(elem[0]) && char.IsLowSurrogate(elem[1])
                        ? char.ConvertToUtf32(elem[0], elem[1])
                        : elem[0];
                    var fb = GetFallbackFont(codepoint, ctx.FallbackFontCache);
                    if (fb != null)
                    {
                        clonedFallback = fb.Clone();
                        clonedFallback.TextSize = font.TextSize;
                        clonedFallback.Color = font.Color;
                        activeFont = clonedFallback;
                    }
                }

                using (clonedFallback)
                {
                    string runStr = elem.ToString();
                    if (renderOptions.Outline && ctx?.OutlinePaint != null)
                    {
                        using var outlinePath = activeFont.GetTextPath(runStr, curX, y);
                        canvas.DrawPath(outlinePath, ctx.OutlinePaint);
                    }

                    canvas.DrawText(runStr, curX, y, activeFont);
                    curX += activeFont.MeasureText(runStr);
                }

                i += elemLen;
            }

            return curX;
        }

        private void DrawSharedChatInlineBadge(Comment comment, List<SectionImage> sectionImages, ref Point drawPos, SectionRenderContext ctx)
        {
            if (!renderOptions.RenderSharedChatBadge
                || comment.event_data?.shared_chat is null
                || string.IsNullOrEmpty(comment.event_data.shared_chat.source_room_id))
            {
                return;
            }

            var canvas = sectionImages[^1].Canvas;
            int badgeSize = Math.Max(10, (int)Math.Round(30 * renderOptions.ReferenceScale * renderOptions.BadgeScale));
            float badgeY = (renderOptions.SectionHeight - badgeSize) / 2f;
            float badgeX = drawPos.X;

            var bgPaint = GetCachedPaint(Purple, ctx.PaintCache);
            float cornerRadius = Math.Max(2f, badgeSize * 0.22f);
            canvas.DrawRoundRect(badgeX, badgeY, badgeSize, badgeSize, cornerRadius, cornerRadius, bgPaint);

            // Draw two interlocking rings / link symbol inside the badge
            using var ringPaint = new SKPaint
            {
                Color = SKColors.White,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(1.5f, badgeSize * 0.11f),
                IsAntialias = true
            };
            float r = badgeSize * 0.21f;
            float cy = badgeY + badgeSize * 0.5f;
            canvas.DrawCircle(badgeX + badgeSize * 0.39f, cy, r, ringPaint);
            canvas.DrawCircle(badgeX + badgeSize * 0.61f, cy, r, ringPaint);

            drawPos.X += badgeSize + Math.Max(2, renderOptions.WordSpacing / 2);
        }

        private void DrawHypeChatInlinePill(Comment comment, List<SectionImage> sectionImages, ref Point drawPos, Point defaultPos, SectionRenderContext ctx)
        {
            if (!renderOptions.RenderHypeChat || comment.event_data?.hype_chat is null)
            {
                return;
            }

            string label = FormatHypeChatCurrency(comment.event_data.hype_chat);
            SKColor pillColor = GetHypeChatLevelColor(comment.event_data.hype_chat.level);

            using var pillTextFont = ctx.NameFont.Clone();
            pillTextFont.TextSize = (float)Math.Max(8, renderOptions.FontSize * 0.85);
            pillTextFont.Color = SKColors.White;

            float textWidth = pillTextFont.MeasureText(label);
            int padX = Math.Max(4, (int)(6 * renderOptions.ReferenceScale));
            int pillWidth = (int)Math.Ceiling(textWidth) + padX * 2;
            int pillHeight = Math.Max(12, (int)(renderOptions.SectionHeight * 0.66));

            if (drawPos.X + pillWidth > renderOptions.ChatWidth - renderOptions.SidePadding * 2)
            {
                AddImageSection(sectionImages, ref drawPos, defaultPos);
            }

            var canvas = sectionImages[^1].Canvas;
            float pillTop = (renderOptions.SectionHeight - pillHeight) / 2f;
            var bgPaint = GetCachedPaint(pillColor, ctx.PaintCache);
            float radius = pillHeight * 0.3f;
            canvas.DrawRoundRect(drawPos.X, pillTop, pillWidth, pillHeight, radius, radius, bgPaint);

            SKRect bounds = new();
            pillTextFont.MeasureText("0", ref bounds);
            float textY = pillTop + (pillHeight + bounds.Height) / 2f;
            canvas.DrawText(label, drawPos.X + padX, textY, pillTextFont);

            drawPos.X += pillWidth + renderOptions.WordSpacing;
        }

        private TwitchEmote GetOrCreateGigantifiedEmote(TwitchEmote baseEmote)
        {
            if (baseEmote is null || baseEmote.ImageData is null || baseEmote.ImageData.Length == 0)
            {
                return baseEmote;
            }

            string cacheKey = $"{baseEmote.EmoteProvider}:{baseEmote.Id}:{baseEmote.Name}";
            return _gigantifiedEmoteCache.GetOrAdd(cacheKey, _ =>
            {
                var giant = new TwitchEmote(
                    baseEmote.ImageData,
                    null,
                    baseEmote.EmoteProvider,
                    baseEmote.ImageScale,
                    baseEmote.Id + "_giant",
                    baseEmote.Name,
                    baseEmote.IsZeroWidth);

                int snapThreshold = (int)Math.Round(4 * renderOptions.ReferenceScale);
                double newScale = 4.0 / giant.ImageScale * renderOptions.ReferenceScale * renderOptions.EmoteScale;
                giant.SnapScale(newScale, snapThreshold, snapThreshold);
                return giant;
            });
        }

        private static bool IsGigantifiedEmoteFragment(Comment comment, Fragment fragment, ChatRenderOptions options)
        {
            if (!options.RenderPowerUps || comment.event_data?.power_up?.type != "gigantified-emote-message")
            {
                return false;
            }

            string targetEmoteId = comment.event_data.power_up.emote_id;
            if (!string.IsNullOrEmpty(targetEmoteId) && fragment.emoticon != null)
            {
                return string.Equals(fragment.emoticon.emoticon_id, targetEmoteId, StringComparison.Ordinal);
            }

            return false;
        }

        private int GetReplyHeaderHeight(Comment comment)
        {
            if (!renderOptions.RenderReplies || comment.event_data?.reply is null)
            {
                return 0;
            }

            return Math.Max(12, (int)Math.Round(renderOptions.SectionHeight * 0.66));
        }

        private void DrawReplyHeaderOnCombinedCanvas(SKCanvas canvas, Comment comment, int replyHeaderHeight, bool hasLeftAccentBar, SectionRenderContext ctx)
        {
            var reply = comment.event_data?.reply;
            if (reply is null || replyHeaderHeight <= 0)
            {
                return;
            }

            string parentName = !string.IsNullOrEmpty(reply.parent_display_name)
                ? reply.parent_display_name
                : (reply.parent_user_login ?? "User");
            string parentBody = (reply.parent_msg_body ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
            string fullReplyLine = string.IsNullOrEmpty(parentBody)
                ? $"Replying to @{parentName}"
                : $"@{parentName}: {parentBody}";

            using var replyFont = ctx.MessageFont.Clone();
            replyFont.TextSize = (float)Math.Max(8, renderOptions.FontSize * 0.80);
            replyFont.Color = renderOptions.MessageColor.WithAlpha(165);

            int startX = renderOptions.SidePadding + (hasLeftAccentBar ? renderOptions.AccentIndentWidth - renderOptions.AccentStrokeWidth / 2 : 0);
            SKRect bounds = new();
            replyFont.MeasureText("Ag", ref bounds);
            float baselineY = (replyHeaderHeight + bounds.Height) / 2f;

            // Draw crisp vector reply turn-arrow icon before the reply text
            float iconSize = Math.Max(8f, replyFont.TextSize * 0.85f);
            float iconPad = Math.Max(3f, replyFont.TextSize * 0.25f);
            DrawVectorReplyIcon(canvas, startX, baselineY - bounds.Height * 0.5f, iconSize, replyFont.Color, ctx);

            float textStartX = startX + iconSize + iconPad;
            int maxWidth = Math.Max(20, renderOptions.ChatWidth - (int)textStartX - renderOptions.SidePadding);

            ReadOnlySpan<char> lineSpan = fullReplyLine.AsSpan();
            string finalText = fullReplyLine;
            if (replyFont.MeasureText(lineSpan) > maxWidth)
            {
                int breakCount = (int)replyFont.BreakText(lineSpan, Math.Max(10, maxWidth - replyFont.MeasureText("...")));
                if (breakCount > 0 && breakCount < fullReplyLine.Length)
                {
                    finalText = string.Concat(lineSpan[..breakCount].TrimEnd(), "...");
                }
            }

            DrawSingleLineTextDirect(canvas, finalText, textStartX, baselineY, replyFont, ctx);
        }

        private void DrawVectorReplyIcon(SKCanvas canvas, float x, float centerY, float size, SKColor color, SectionRenderContext ctx)
        {
            float strokeW = Math.Max(1.4f, size * 0.14f);
            float leftX = x + size * 0.15f;
            float rightX = x + size * 0.85f;
            float bottomY = centerY + size * 0.32f;
            float topY = centerY - size * 0.18f;
            float headSize = Math.Max(2.5f, size * 0.28f);

            using var path = new SKPath();
            path.MoveTo(leftX, bottomY);
            path.LineTo(leftX, topY);
            path.LineTo(rightX, topY);
            path.MoveTo(rightX - headSize, topY - headSize * 0.8f);
            path.LineTo(rightX, topY);
            path.LineTo(rightX - headSize, topY + headSize * 0.8f);

            if (renderOptions.Outline && ctx?.OutlinePaint != null)
            {
                using var outlinePaint = new SKPaint
                {
                    Color = ctx.OutlinePaint.Color,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = strokeW + ctx.OutlinePaint.StrokeWidth,
                    StrokeCap = SKStrokeCap.Round,
                    StrokeJoin = SKStrokeJoin.Round,
                    IsAntialias = true
                };
                canvas.DrawPath(path, outlinePaint);
            }

            using var paint = new SKPaint
            {
                Color = color,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = strokeW,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
                IsAntialias = true
            };
            canvas.DrawPath(path, paint);
        }

        private void ApplyStrikethroughModeration(SKCanvas canvas, SKImageInfo bitmapInfo, List<EmotePosition> emoteSectionList, int replyHeaderHeight)
        {
            // Flatten static frame of any emotes onto the section canvas so animated emotes don't draw at 100% alpha over the strikethrough
            foreach (var (drawPoint, emote) in emoteSectionList)
            {
                var frames = emote.EmoteFrames;
                if (frames is { Length: > 0 } && frames[0] != null)
                {
                    canvas.DrawImage(frames[0], drawPoint.X, drawPoint.Y);
                }
            }
            emoteSectionList.Clear();

            // Dim entire section to ~45% opacity using DstIn alpha multiplier
            using (var dimPaint = new SKPaint
            {
                Color = new SKColor(0, 0, 0, 115),
                BlendMode = SKBlendMode.DstIn
            })
            {
                canvas.DrawRect(0, 0, bitmapInfo.Width, bitmapInfo.Height, dimPaint);
            }

            // Draw crisp horizontal strikethrough line across each SectionHeight row
            using var strikePaint = new SKPaint
            {
                Color = renderOptions.MessageColor.WithAlpha(185),
                StrokeWidth = (float)Math.Max(1.5, 2.0 * renderOptions.ReferenceScale),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke
            };

            int rowCount = Math.Max(1, (bitmapInfo.Height - replyHeaderHeight) / Math.Max(1, renderOptions.SectionHeight));
            float startX = renderOptions.SidePadding;
            float endX = Math.Max(startX + 20, bitmapInfo.Width - renderOptions.SidePadding * 2);

            for (int r = 0; r < rowCount; r++)
            {
                float centerY = replyHeaderHeight + r * renderOptions.SectionHeight + renderOptions.SectionHeight * 0.52f;
                canvas.DrawLine(startX, centerY, endX, centerY, strikePaint);
            }
        }

        /// <summary>
        /// Renders active Sticky Top Overlay widgets (Predictions, Polls, Pinned Chat, Hype Train) onto <paramref name="frameCanvas"/>
        /// respecting the 25% ChatHeight coexistence cap and strict priority order:
        /// Open/Resolved Prediction &gt; Poll &gt; Pinned Chat &gt; Closed/Locked Prediction &gt; Hype Train.
        /// </summary>
        private int DrawStickyTopOverlays(SKCanvas frameCanvas, StickyOverlayTracker tracker, double currentTimeSeconds, SectionRenderContext ctx)
        {
            if (tracker is null || !tracker.HasAnyActive)
            {
                return 0;
            }

            int baseRowH = Math.Max(18, renderOptions.SectionHeight);
            int maxAllowedHeight = Math.Max(baseRowH, (int)(renderOptions.ChatHeight * STICKY_MAX_HEIGHT_RATIO));

            var candidates = new List<(StickyWidgetKind Kind, int Height)>(4);

            if (tracker.Prediction != null)
            {
                bool isLocked = tracker.Prediction.Status == "LOCKED";
                int h = (int)Math.Round(baseRowH * 1.45);
                candidates.Add((isLocked ? StickyWidgetKind.PredictionLocked : StickyWidgetKind.PredictionOpenOrResolved, h));
            }

            if (tracker.Poll != null)
            {
                int choiceCount = Math.Clamp(tracker.Poll.Choices.Count, 1, 5);
                int h = (int)Math.Round(baseRowH * (0.82 + choiceCount * 0.56));
                candidates.Add((StickyWidgetKind.Poll, h));
            }

            if (tracker.PinnedChat != null)
            {
                int h = (int)Math.Round(baseRowH * 1.20);
                candidates.Add((StickyWidgetKind.PinnedChat, h));
            }

            if (tracker.HypeTrain != null)
            {
                int h = (int)Math.Round(baseRowH * 1.15);
                candidates.Add((StickyWidgetKind.HypeTrain, h));
            }

            if (candidates.Count == 0)
            {
                return 0;
            }

            candidates.Sort(static (a, b) => a.Kind.CompareTo(b.Kind));

            var selected = new List<(StickyWidgetKind Kind, int Height)>(candidates.Count);
            int totalHeight = 0;

            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (i == 0 || totalHeight + c.Height <= maxAllowedHeight)
                {
                    int clampedH = i == 0 ? Math.Min(c.Height, Math.Max(baseRowH, (int)(renderOptions.ChatHeight * 0.42))) : c.Height;
                    selected.Add((c.Kind, clampedH));
                    totalHeight += clampedH;
                }
            }

            // Align totalHeight to an even pixel row so YUV420P 2x2 chroma subsampling blocks align cleanly
            totalHeight = (totalHeight + 1) & ~1;

            int currentY = 0;
            for (int i = 0; i < selected.Count; i++)
            {
                var (kind, widgetHeight) = selected[i];
                if (i == selected.Count - 1)
                {
                    widgetHeight = totalHeight - currentY;
                }

                switch (kind)
                {
                    case StickyWidgetKind.PredictionOpenOrResolved:
                    case StickyWidgetKind.PredictionLocked:
                        DrawStickyPredictionWidget(frameCanvas, tracker.Prediction, currentY, widgetHeight, currentTimeSeconds, ctx);
                        break;
                    case StickyWidgetKind.Poll:
                        DrawStickyPollWidget(frameCanvas, tracker.Poll, currentY, widgetHeight, currentTimeSeconds, ctx);
                        break;
                    case StickyWidgetKind.PinnedChat:
                        DrawStickyPinnedChatWidget(frameCanvas, tracker.PinnedChat, currentY, widgetHeight, ctx);
                        break;
                    case StickyWidgetKind.HypeTrain:
                        DrawStickyHypeTrainWidget(frameCanvas, tracker.HypeTrain, currentY, widgetHeight, ctx);
                        break;
                }

                currentY += widgetHeight;
            }

            return totalHeight;
        }

        private void DrawStickyPredictionWidget(SKCanvas canvas, ActivePredictionState pred, int topY, int height, double currentTimeSeconds, SectionRenderContext ctx)
        {
            int width = renderOptions.ChatWidth;
            int padX = Math.Max(6, renderOptions.SidePadding);

            DrawStickyCardBackground(canvas, topY, width, height, new SKColor(0xFF387AFF));

            using var titleFont = ctx.NameFont.Clone();
            titleFont.TextSize = (float)Math.Max(9, renderOptions.FontSize * 0.82);
            titleFont.Color = SKColors.White;

            using var subFont = ctx.MessageFont.Clone();
            subFont.TextSize = (float)Math.Max(8, renderOptions.FontSize * 0.74);
            subFont.Color = new SKColor(0xFFADADB8);

            string statusTag = pred.Status switch
            {
                "LOCKED" => "LOCKED",
                "RESOLVED" => "RESOLVED",
                "CANCELED" => "CANCELED",
                _ => FormatCountdownSeconds(Math.Max(0, (int)Math.Ceiling(pred.WindowSeconds - (currentTimeSeconds - pred.StartedOffsetSeconds))))
            };

            float statusWidth = subFont.MeasureText(statusTag) + padX;
            float row1Baseline = topY + height * 0.40f;
            DrawSingleLineTextDirect(canvas, statusTag, width - padX - subFont.MeasureText(statusTag), row1Baseline, subFont, ctx);

            string headerText = $"🔮 {pred.Title}";
            string clippedHeader = TruncateSingleLine(headerText, titleFont, Math.Max(30, width - padX * 2 - (int)statusWidth));
            DrawSingleLineTextDirect(canvas, clippedHeader, padX + 4, row1Baseline, titleFont, ctx);

            // Outcome bar & percentages on Row 2
            long totalPoints = 0;
            foreach (var o in pred.Outcomes) totalPoints += o.TotalPoints;

            var o1 = pred.Outcomes.Count > 0 ? pred.Outcomes[0] : new ActivePredictionOutcome { Id = "1", Title = "Yes", TotalPoints = 0 };
            var o2 = pred.Outcomes.Count > 1 ? pred.Outcomes[1] : new ActivePredictionOutcome { Id = "2", Title = "No", TotalPoints = 0 };

            int pct1 = totalPoints > 0 ? (int)Math.Round(o1.TotalPoints * 100.0 / totalPoints) : 50;
            int pct2 = totalPoints > 0 ? 100 - pct1 : 50;

            float barTop = topY + height * 0.54f;
            float barHeight = Math.Max(8f, height * 0.34f);
            float barLeft = padX + 4;
            float barTotalWidth = Math.Max(20f, width - barLeft - padX);

            using var bluePaint = new SKPaint { Color = new SKColor(0xFF387AFF), IsAntialias = true };
            using var pinkPaint = new SKPaint { Color = new SKColor(0xFFF5009B), IsAntialias = true };

            float splitWidth = barTotalWidth * (pct1 / 100f);
            canvas.DrawRoundRect(barLeft, barTop, barTotalWidth, barHeight, 4f, 4f, pinkPaint);
            if (splitWidth > 1f)
            {
                canvas.Save();
                canvas.ClipRect(new SKRect(barLeft, barTop, barLeft + splitWidth, barTop + barHeight));
                canvas.DrawRoundRect(barLeft, barTop, barTotalWidth, barHeight, 4f, 4f, bluePaint);
                canvas.Restore();
            }

            subFont.Color = SKColors.White;
            bool o1Won = pred.Status == "RESOLVED" && string.Equals(pred.WinningOutcomeId, o1.Id, StringComparison.Ordinal);
            bool o2Won = pred.Status == "RESOLVED" && string.Equals(pred.WinningOutcomeId, o2.Id, StringComparison.Ordinal);

            string leftLabel = $"{(o1Won ? "✓ " : "")}{o1.Title} ({pct1}%)";
            string rightLabel = $"{(o2Won ? "✓ " : "")}{o2.Title} ({pct2}%)";

            float barTextBaseline = barTop + barHeight * 0.76f;
            string clippedLeft = TruncateSingleLine(leftLabel, subFont, (int)(barTotalWidth * 0.48f));
            string clippedRight = TruncateSingleLine(rightLabel, subFont, (int)(barTotalWidth * 0.48f));

            DrawSingleLineTextDirect(canvas, clippedLeft, barLeft + 5, barTextBaseline, subFont, ctx);
            DrawSingleLineTextDirect(canvas, clippedRight, barLeft + barTotalWidth - subFont.MeasureText(clippedRight) - 5, barTextBaseline, subFont, ctx);
        }

        private void DrawStickyPollWidget(SKCanvas canvas, ActivePollState poll, int topY, int height, double currentTimeSeconds, SectionRenderContext ctx)
        {
            int width = renderOptions.ChatWidth;
            int padX = Math.Max(6, renderOptions.SidePadding);

            DrawStickyCardBackground(canvas, topY, width, height, new SKColor(0xFF00DB84));

            using var titleFont = ctx.NameFont.Clone();
            titleFont.TextSize = (float)Math.Max(9, renderOptions.FontSize * 0.80);
            titleFont.Color = SKColors.White;

            using var subFont = ctx.MessageFont.Clone();
            subFont.TextSize = (float)Math.Max(8, renderOptions.FontSize * 0.72);
            subFont.Color = new SKColor(0xFFADADB8);

            bool isEnded = poll.Status is "COMPLETED" or "TERMINATED";
            string statusTag = isEnded
                ? "POLL ENDED"
                : FormatCountdownSeconds(Math.Max(0, (int)Math.Ceiling(poll.DurationSeconds - (currentTimeSeconds - poll.StartedOffsetSeconds))));

            int choiceCount = Math.Clamp(poll.Choices.Count, 1, 5);
            float headerRegionH = Math.Max(14f, height * (0.76f / (0.76f + choiceCount * 0.56f)));
            float row1Baseline = topY + headerRegionH * 0.74f;

            DrawSingleLineTextDirect(canvas, statusTag, width - padX - subFont.MeasureText(statusTag), row1Baseline, subFont, ctx);

            string headerText = $"📊 {poll.Title}";
            string clippedHeader = TruncateSingleLine(headerText, titleFont, Math.Max(30, width - padX * 2 - (int)subFont.MeasureText(statusTag) - padX));
            DrawSingleLineTextDirect(canvas, clippedHeader, padX + 4, row1Baseline, titleFont, ctx);

            if (poll.Choices.Count == 0)
            {
                return;
            }

            // Populate each poll choice vertically on its own full-width bar row
            int maxVotes = 0;
            for (int i = 0; i < poll.Choices.Count; i++)
            {
                if (poll.Choices[i].TotalVoters > maxVotes)
                {
                    maxVotes = poll.Choices[i].TotalVoters;
                }
            }

            float choicesAreaTop = topY + headerRegionH;
            float choicesAreaHeight = Math.Max(12f, height - headerRegionH - 3f);
            float slotHeight = choicesAreaHeight / choiceCount;
            float barHeight = Math.Max(8f, slotHeight * 0.82f);
            float barLeft = padX + 4;
            float barTotalWidth = Math.Max(20f, width - barLeft - padX);

            using var trackPaint = new SKPaint { Color = new SKColor(0xFF2D2D35), IsAntialias = true };
            using var activeFillPaint = new SKPaint { Color = Purple, IsAntialias = true };
            using var winnerFillPaint = new SKPaint { Color = new SKColor(0xFF00DB84), IsAntialias = true };
            using var endedOtherFillPaint = new SKPaint { Color = new SKColor(0xFF53535F), IsAntialias = true };

            SKRect textBounds = new();
            subFont.MeasureText("Ag", ref textBounds);

            for (int i = 0; i < choiceCount; i++)
            {
                var choice = poll.Choices[i];
                int pct = poll.TotalVoters > 0 ? (int)Math.Round(choice.TotalVoters * 100.0 / poll.TotalVoters) : 0;
                bool isWinner = isEnded && maxVotes > 0 && choice.TotalVoters == maxVotes;

                float slotTop = choicesAreaTop + i * slotHeight;
                float barTop = slotTop + (slotHeight - barHeight) * 0.5f;

                canvas.DrawRoundRect(barLeft, barTop, barTotalWidth, barHeight, 3f, 3f, trackPaint);

                if (pct > 0)
                {
                    float fillW = barTotalWidth * Math.Clamp(pct / 100f, 0.02f, 1.0f);
                    var fillPaint = isEnded ? (isWinner ? winnerFillPaint : endedOtherFillPaint) : activeFillPaint;
                    canvas.DrawRoundRect(barLeft, barTop, fillW, barHeight, 3f, 3f, fillPaint);
                }

                string rightPctText = $"{pct}%";
                float rightPctWidth = subFont.MeasureText(rightPctText);
                float textBaseline = barTop + (barHeight + textBounds.Height) * 0.5f - 1f;

                subFont.Color = isWinner ? SKColors.Black : SKColors.White;
                string leftTitle = isWinner ? $"✓ {choice.Title}" : choice.Title;
                int maxTitleWidth = Math.Max(20, (int)(barTotalWidth - rightPctWidth - 16));
                string clippedTitle = TruncateSingleLine(leftTitle, subFont, maxTitleWidth);

                DrawSingleLineTextDirect(canvas, clippedTitle, barLeft + 6, textBaseline, subFont, ctx);
                DrawSingleLineTextDirect(canvas, rightPctText, barLeft + barTotalWidth - rightPctWidth - 6, textBaseline, subFont, ctx);
            }
        }

        private void DrawStickyPinnedChatWidget(SKCanvas canvas, ActivePinnedChatState pin, int topY, int height, SectionRenderContext ctx)
        {
            int width = renderOptions.ChatWidth;
            int padX = Math.Max(6, renderOptions.SidePadding);

            DrawStickyCardBackground(canvas, topY, width, height, Purple);

            using var nameFontLocal = ctx.NameFont.Clone();
            nameFontLocal.TextSize = (float)Math.Max(9, renderOptions.FontSize * 0.82);
            nameFontLocal.Color = SKColor.TryParse(pin.SenderColor, out var sc) ? sc : Purple;

            using var msgFontLocal = ctx.MessageFont.Clone();
            msgFontLocal.TextSize = (float)Math.Max(9, renderOptions.FontSize * 0.82);
            msgFontLocal.Color = SKColors.White;

            float baselineY = topY + height * 0.62f;
            float curX = padX + 4;

            string prefix = $"📌 {pin.SenderDisplayName}: ";
            curX = DrawSingleLineTextDirect(canvas, prefix, curX, baselineY, nameFontLocal, ctx);

            int remWidth = Math.Max(20, width - (int)curX - padX);
            string clippedMsg = TruncateSingleLine(pin.MessageText, msgFontLocal, remWidth);
            DrawSingleLineTextDirect(canvas, clippedMsg, curX, baselineY, msgFontLocal, ctx);
        }

        private void DrawStickyHypeTrainWidget(SKCanvas canvas, ActiveHypeTrainState ht, int topY, int height, SectionRenderContext ctx)
        {
            int width = renderOptions.ChatWidth;
            int padX = Math.Max(6, renderOptions.SidePadding);

            DrawStickyCardBackground(canvas, topY, width, height, new SKColor(0xFFFFB31A));

            using var titleFont = ctx.NameFont.Clone();
            titleFont.TextSize = (float)Math.Max(9, renderOptions.FontSize * 0.82);
            titleFont.Color = SKColors.White;

            int pct = ht.Goal > 0 ? Math.Clamp((int)Math.Round(ht.Progress * 100.0 / ht.Goal), 0, 100) : 100;
            string label = ht.IsEnded
                ? $"🚂 HYPE TRAIN ENDED • LVL {ht.Level}"
                : $"🚂 HYPE TRAIN LVL {ht.Level} • {pct}%";

            float baselineY = topY + height * 0.56f;
            string clipped = TruncateSingleLine(label, titleFont, width - padX * 2 - 8);
            DrawSingleLineTextDirect(canvas, clipped, padX + 4, baselineY, titleFont, ctx);

            // Progress bar along bottom of Hype Train card
            float barHeight = Math.Max(4f, height * 0.16f);
            float barTop = topY + height - barHeight - 2;
            float barLeft = padX + 4;
            float barW = Math.Max(10f, width - barLeft - padX);

            using var trackPaint = new SKPaint { Color = new SKColor(0xFF2D2D35) };
            using var fillPaint = new SKPaint { Color = new SKColor(0xFFFFB31A) };
            canvas.DrawRect(barLeft, barTop, barW, barHeight, trackPaint);
            canvas.DrawRect(barLeft, barTop, barW * (ht.IsEnded ? 1f : pct / 100f), barHeight, fillPaint);
        }

        private void DrawStickyCardBackground(SKCanvas canvas, int topY, int width, int height, SKColor accentColor)
        {
            // Clear underlying scrolling chat area to solid dark card surface so sticky header is 100% readable
            using var cardBgPaint = new SKPaint
            {
                Color = new SKColor(0xF518181B),
                BlendMode = SKBlendMode.Src
            };
            canvas.DrawRect(0, topY, width, height, cardBgPaint);

            using var accentPaint = new SKPaint { Color = accentColor };
            int strokeW = Math.Max(3, renderOptions.AccentStrokeWidth / 2);
            canvas.DrawRect(0, topY, strokeW, height, accentPaint);

            using var dividerPaint = new SKPaint { Color = new SKColor(0x38FFFFFF) };
            canvas.DrawRect(0, topY + height - 1, width, 1, dividerPaint);
        }

        private static string FormatCountdownSeconds(int totalSeconds)
        {
            int mins = totalSeconds / 60;
            int secs = totalSeconds % 60;
            return $"{mins}:{secs:D2}";
        }

        private static string TruncateSingleLine(string text, SKPaint font, int maxWidth)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0)
            {
                return "";
            }

            if (font.MeasureText(text) <= maxWidth)
            {
                return text;
            }

            float ellipsisW = font.MeasureText("…");
            int count = (int)font.BreakText(text.AsSpan(), Math.Max(4, maxWidth - ellipsisW));
            if (count <= 0)
            {
                return "…";
            }

            return string.Concat(text.AsSpan(0, Math.Min(count, text.Length)).TrimEnd(), "…");
        }
    }
}

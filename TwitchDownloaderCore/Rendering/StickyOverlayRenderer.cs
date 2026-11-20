#nullable enable
using SkiaSharp;
using TwitchDownloaderCore.Chat.Timeline;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Options;

namespace TwitchDownloaderCore.Rendering
{
    internal enum StickyWidgetKind
    {
        PredictionOpenOrResolved = 1,
        Poll = 2,
        PinnedChat = 3,
        PredictionLocked = 4,
        HypeTrain = 5
    }

    internal sealed class ActivePredictionState
    {
        public string PredictionId { get; init; } = "";
        public string Title { get; init; } = "Prediction";
        public string Status { get; set; } = "ACTIVE";
        public int WindowSeconds { get; init; } = 120;
        public double StartedOffsetSeconds { get; init; }
        public double LastUpdatedOffsetSeconds { get; set; }
        public double? ResolvedOffsetSeconds { get; set; }
        public string? WinningOutcomeId { get; set; }
        public IReadOnlyList<StickyPredictionOutcome> Outcomes { get; init; } = [];
    }

    internal sealed class ActivePollState
    {
        public string PollId { get; init; } = "";
        public string Title { get; init; } = "Poll";
        public string Status { get; set; } = "ACTIVE";
        public int DurationSeconds { get; init; } = 60;
        public double StartedOffsetSeconds { get; init; }
        public double LastUpdatedOffsetSeconds { get; set; }
        public double? CompletedOffsetSeconds { get; set; }
        public int TotalVoters { get; set; }
        public IReadOnlyList<StickyPollChoice> Choices { get; init; } = [];
    }

    internal sealed class ActivePinnedChatState
    {
        public string PinId { get; init; } = "";
        public string Type { get; init; } = "MOD_PIN";
        public string MessageText { get; set; } = "";
        public string SenderDisplayName { get; set; } = "Chatter";
        public string SenderColor { get; set; } = "#9146FF";
        public string PinnedByDisplayName { get; init; } = "";
        public double StartedOffsetSeconds { get; init; }
        public double? ExpiresOffsetSeconds { get; set; }
    }

    internal sealed class ActiveHypeTrainState
    {
        public int Level { get; set; } = 1;
        public int Goal { get; set; } = 1000;
        public int Total { get; set; }
        public int Progress { get; set; }
        public double StartedOffsetSeconds { get; init; }
        public double LastUpdatedOffsetSeconds { get; set; }
        public double? EndedOffsetSeconds { get; set; }
        public bool IsEnded { get; set; }
    }

    /// <summary>
    /// Deterministic state machine over pre-compiled <see cref="StickyOverlayEvent"/> records (Predictions, Polls,
    /// Pinned Chat, and Hype Trains). Performs zero <c>JsonElement</c> parsing during frame rendering.
    /// </summary>
    internal sealed class StickyOverlayTracker
    {
        public const double StickyResolvedLingerSeconds = 15.0;

        private readonly IReadOnlyList<StickyOverlayEvent> _events;
        private readonly ChatRenderOptions _options;
        private readonly Dictionary<string, double> _pollCompletedAtById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, double> _predictionResolvedAtById = new(StringComparer.Ordinal);
        private int _eventCursor;

        public ActivePredictionState? Prediction { get; private set; }
        public ActivePollState? Poll { get; private set; }
        public ActivePinnedChatState? PinnedChat { get; private set; }
        public ActiveHypeTrainState? HypeTrain { get; private set; }

        public bool HasAnyActive =>
            Prediction is not null || Poll is not null || PinnedChat is not null || HypeTrain is not null;

        public StickyOverlayTracker(IReadOnlyList<StickyOverlayEvent> events, ChatRenderOptions options)
        {
            _events = events ?? [];
            _options = options;
        }

        public void AdvanceTo(double currentTimeSeconds)
        {
            while (_eventCursor < _events.Count && _events[_eventCursor].OffsetSeconds <= currentTimeSeconds + 0.0001)
            {
                var ev = _events[_eventCursor];
                ExpireStaleStates(ev.OffsetSeconds);
                ApplyEvent(ev);
                _eventCursor++;
            }

            ExpireStaleStates(currentTimeSeconds);
        }

        private void ApplyEvent(StickyOverlayEvent ev)
        {
            switch (ev)
            {
                case PredictionOverlayEvent pred when _options.PredictionMode == OverlayWidgetMode.StickyTop:
                    ApplyPredictionEvent(pred);
                    break;
                case PollOverlayEvent poll when _options.PollMode == OverlayWidgetMode.StickyTop:
                    ApplyPollEvent(poll);
                    break;
                case PinnedChatOverlayEvent pin when _options.PinnedChatMode == OverlayWidgetMode.StickyTop:
                    ApplyPinnedChatEvent(pin);
                    break;
                case HypeTrainOverlayEvent ht when _options.HypeTrainMode == OverlayWidgetMode.StickyTop:
                    ApplyHypeTrainEvent(ht);
                    break;
            }
        }

        private void ApplyPredictionEvent(PredictionOverlayEvent ev)
        {
            string predId = ev.PredictionId ?? "";
            string title = ev.Title ?? "Prediction";
            string status = (ev.Status ?? "ACTIVE").ToUpperInvariant();
            int windowSec = ev.WindowSeconds;
            string? winningId = ev.WinningOutcomeId;

            bool isEndedStatus = status is "RESOLVED" or "CANCELED";

            if (ev.CarriedOver && isEndedStatus)
            {
                if (!string.IsNullOrEmpty(predId))
                {
                    _predictionResolvedAtById.TryAdd(predId, -StickyResolvedLingerSeconds);
                }
                return;
            }

            double? priorResolvedAt = null;
            if (!string.IsNullOrEmpty(predId) && _predictionResolvedAtById.TryGetValue(predId, out double recordedResAt))
            {
                if (ev.OffsetSeconds - recordedResAt >= StickyResolvedLingerSeconds)
                {
                    return;
                }
                priorResolvedAt = recordedResAt;
            }

            IReadOnlyList<StickyPredictionOutcome> outcomes = ev.Outcomes;
            bool isSamePrediction = Prediction is not null && string.Equals(Prediction.PredictionId, predId, StringComparison.Ordinal);
            if (isSamePrediction && Prediction is not null)
            {
                if (outcomes.Count == 0 && Prediction.Outcomes.Count > 0)
                {
                    outcomes = Prediction.Outcomes;
                }
                if ((string.IsNullOrWhiteSpace(title) || title == "Prediction") && !string.IsNullOrWhiteSpace(Prediction.Title))
                {
                    title = Prediction.Title;
                }
            }

            double startedOffset = isSamePrediction && Prediction is not null ? Prediction.StartedOffsetSeconds : ev.OffsetSeconds;
            double? resolvedOffset = priorResolvedAt ?? (isSamePrediction ? Prediction?.ResolvedOffsetSeconds : null);

            if (isEndedStatus && resolvedOffset is null)
            {
                resolvedOffset = ev.OffsetSeconds;
            }

            if (resolvedOffset is double resVal && !string.IsNullOrEmpty(predId))
            {
                _predictionResolvedAtById.TryAdd(predId, resVal);
            }

            Prediction = new ActivePredictionState
            {
                PredictionId = predId,
                Title = title,
                Status = status,
                WindowSeconds = isSamePrediction && windowSec <= 0 && Prediction is not null ? Prediction.WindowSeconds : Math.Max(5, windowSec),
                StartedOffsetSeconds = startedOffset,
                LastUpdatedOffsetSeconds = ev.OffsetSeconds,
                ResolvedOffsetSeconds = resolvedOffset,
                WinningOutcomeId = winningId ?? (isSamePrediction ? Prediction?.WinningOutcomeId : null),
                Outcomes = outcomes
            };
        }

        private void ApplyPollEvent(PollOverlayEvent ev)
        {
            string pollId = ev.PollId ?? "";
            string pubsubType = (ev.PubsubType ?? "").ToUpperInvariant();
            string status = (ev.Status ?? "ACTIVE").ToUpperInvariant();

            if (status == "ARCHIVED" || pubsubType == "POLL_ARCHIVE")
            {
                if (!string.IsNullOrEmpty(pollId))
                {
                    _pollCompletedAtById.TryAdd(pollId, ev.OffsetSeconds);
                }

                if (Poll is not null && (string.IsNullOrEmpty(pollId) || string.Equals(Poll.PollId, pollId, StringComparison.Ordinal)))
                {
                    Poll.Status = "COMPLETED";
                    Poll.CompletedOffsetSeconds ??= ev.OffsetSeconds;
                }
                return;
            }

            bool isEndedStatus = status != "ACTIVE" || pubsubType is "POLL_COMPLETE" or "POLL_TERMINATE";

            if (ev.CarriedOver && isEndedStatus)
            {
                if (!string.IsNullOrEmpty(pollId))
                {
                    _pollCompletedAtById.TryAdd(pollId, -StickyResolvedLingerSeconds);
                }
                return;
            }

            double? priorCompletedAt = null;
            if (!string.IsNullOrEmpty(pollId) && _pollCompletedAtById.TryGetValue(pollId, out double recordedCompAt))
            {
                if (ev.OffsetSeconds - recordedCompAt >= StickyResolvedLingerSeconds)
                {
                    return;
                }
                priorCompletedAt = recordedCompAt;
            }

            int sumChoiceVotes = 0;
            for (int i = 0; i < ev.Choices.Count; i++)
            {
                sumChoiceVotes += ev.Choices[i].TotalVoters;
            }

            bool isSamePoll = Poll is not null && string.Equals(Poll.PollId, pollId, StringComparison.Ordinal);
            double startedOffset = isSamePoll && Poll is not null ? Poll.StartedOffsetSeconds : ev.OffsetSeconds;
            double? completedOffset = priorCompletedAt ?? (isSamePoll ? Poll?.CompletedOffsetSeconds : null);

            if (isEndedStatus || completedOffset is not null)
            {
                status = "COMPLETED";
                completedOffset ??= ev.OffsetSeconds;
                if (!string.IsNullOrEmpty(pollId))
                {
                    _pollCompletedAtById.TryAdd(pollId, completedOffset.Value);
                }
            }

            Poll = new ActivePollState
            {
                PollId = pollId,
                Title = ev.Title ?? "Poll",
                Status = status,
                DurationSeconds = Math.Max(5, ev.DurationSeconds),
                StartedOffsetSeconds = startedOffset,
                LastUpdatedOffsetSeconds = ev.OffsetSeconds,
                CompletedOffsetSeconds = completedOffset,
                TotalVoters = Math.Max(ev.TotalVoters, sumChoiceVotes),
                Choices = ev.Choices
            };
        }

        private void ApplyPinnedChatEvent(PinnedChatOverlayEvent ev)
        {
            string action = (ev.Action ?? "pin-message").ToLowerInvariant();
            if (action is "unpin-message" or "unpin_message")
            {
                PinnedChat = null;
                return;
            }

            string pinId = ev.PinId ?? "";
            string msgText = (ev.RawMessageText ?? "").Trim();
            string? rawSenderName = ev.RawSenderDisplayName;
            string? rawSenderColor = ev.RawSenderColor;

            if (action is "update-message" or "update_pinned_message" or "update_message")
            {
                if (PinnedChat is not null && (string.IsNullOrEmpty(pinId) || string.Equals(PinnedChat.PinId, pinId, StringComparison.Ordinal)))
                {
                    if (!string.IsNullOrWhiteSpace(msgText))
                    {
                        PinnedChat.MessageText = msgText;
                    }
                    if (!string.IsNullOrWhiteSpace(rawSenderName))
                    {
                        PinnedChat.SenderDisplayName = rawSenderName;
                        if (!string.IsNullOrWhiteSpace(rawSenderColor))
                        {
                            PinnedChat.SenderColor = rawSenderColor;
                        }
                    }
                    if (ev.EndsAt > ev.StartsAt && ev.StartsAt > 0)
                    {
                        PinnedChat.ExpiresOffsetSeconds = ev.OffsetSeconds + (ev.EndsAt - ev.StartsAt);
                    }
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(msgText))
            {
                return;
            }

            string senderName = !string.IsNullOrWhiteSpace(rawSenderName) ? rawSenderName : "Chatter";
            string senderColor = !string.IsNullOrWhiteSpace(rawSenderColor) ? rawSenderColor : "#9146FF";
            double expiresOffset = ev.EndsAt > ev.StartsAt && ev.StartsAt > 0
                ? ev.OffsetSeconds + (ev.EndsAt - ev.StartsAt)
                : ev.OffsetSeconds + 1200.0;

            PinnedChat = new ActivePinnedChatState
            {
                PinId = pinId,
                Type = ev.Type ?? "MOD_PIN",
                MessageText = msgText,
                SenderDisplayName = senderName,
                SenderColor = senderColor,
                PinnedByDisplayName = ev.PinnedByDisplayName ?? "",
                StartedOffsetSeconds = ev.OffsetSeconds,
                ExpiresOffsetSeconds = expiresOffset
            };
        }

        private void ApplyHypeTrainEvent(HypeTrainOverlayEvent ev)
        {
            string pubsubType = (ev.PubsubType ?? "").ToLowerInvariant();
            if (pubsubType == "hype-train-approaching")
            {
                return;
            }

            if (pubsubType == "hype-train-conductor-update" && HypeTrain is null)
            {
                return;
            }

            int rawLevel = ev.RawLevel ?? (HypeTrain?.Level ?? 1);
            int rawGoal = ev.RawGoal ?? (HypeTrain?.Goal ?? 1000);
            int rawTotal = ev.RawTotal ?? (HypeTrain?.Total ?? 0);
            int rawProgress = ev.RawProgress ?? (HypeTrain?.Progress ?? 0);

            bool isDeltaOrEndWithoutProgress = (pubsubType is "hype-train-conductor-update" or "hype-train-end" || rawGoal <= 0)
                && HypeTrain is not null;

            int level = Math.Max(1, rawLevel > 0 ? rawLevel : (HypeTrain?.Level ?? 1));
            int goal = isDeltaOrEndWithoutProgress && rawGoal <= 0 && HypeTrain is not null
                ? HypeTrain.Goal
                : Math.Max(1, rawGoal);
            int total = isDeltaOrEndWithoutProgress && rawTotal <= 0 && HypeTrain is not null
                ? HypeTrain.Total
                : rawTotal;
            int progress = isDeltaOrEndWithoutProgress && rawProgress <= 0 && HypeTrain is not null
                ? HypeTrain.Progress
                : rawProgress;

            bool isEnded = pubsubType == "hype-train-end";
            double startedOffset = HypeTrain is { IsEnded: false } ? HypeTrain.StartedOffsetSeconds : ev.OffsetSeconds;
            double? endedOffset = isEnded ? (HypeTrain?.EndedOffsetSeconds ?? ev.OffsetSeconds) : null;

            HypeTrain = new ActiveHypeTrainState
            {
                Level = level,
                Goal = goal,
                Total = total,
                Progress = progress,
                StartedOffsetSeconds = startedOffset,
                LastUpdatedOffsetSeconds = ev.OffsetSeconds,
                EndedOffsetSeconds = endedOffset,
                IsEnded = isEnded
            };
        }

        private void ExpireStaleStates(double currentTimeSeconds)
        {
            if (Prediction is not null)
            {
                if (Prediction.ResolvedOffsetSeconds is double resAt)
                {
                    if (currentTimeSeconds - resAt >= StickyResolvedLingerSeconds)
                    {
                        if (!string.IsNullOrEmpty(Prediction.PredictionId))
                        {
                            _predictionResolvedAtById.TryAdd(Prediction.PredictionId, resAt);
                        }
                        Prediction = null;
                    }
                }
                else if (Prediction.Status == "ACTIVE" && currentTimeSeconds - Prediction.StartedOffsetSeconds > Prediction.WindowSeconds + 5)
                {
                    Prediction.Status = "LOCKED";
                }
            }

            if (Poll is not null)
            {
                if (Poll.CompletedOffsetSeconds is double compAt)
                {
                    if (currentTimeSeconds - compAt >= StickyResolvedLingerSeconds)
                    {
                        if (!string.IsNullOrEmpty(Poll.PollId))
                        {
                            _pollCompletedAtById.TryAdd(Poll.PollId, compAt);
                        }
                        Poll = null;
                    }
                }
                else if (Poll.Status != "ACTIVE")
                {
                    if (currentTimeSeconds - Poll.LastUpdatedOffsetSeconds >= StickyResolvedLingerSeconds)
                    {
                        if (!string.IsNullOrEmpty(Poll.PollId))
                        {
                            _pollCompletedAtById.TryAdd(Poll.PollId, Poll.LastUpdatedOffsetSeconds);
                        }
                        Poll = null;
                    }
                }
                else if (currentTimeSeconds - Poll.StartedOffsetSeconds > Poll.DurationSeconds + StickyResolvedLingerSeconds)
                {
                    if (!string.IsNullOrEmpty(Poll.PollId))
                    {
                        _pollCompletedAtById.TryAdd(Poll.PollId, Poll.StartedOffsetSeconds + Poll.DurationSeconds);
                    }
                    Poll = null;
                }
            }

            if (PinnedChat is not null && PinnedChat.ExpiresOffsetSeconds is double expAt && currentTimeSeconds >= expAt)
            {
                PinnedChat = null;
            }

            if (HypeTrain is not null)
            {
                if (HypeTrain.EndedOffsetSeconds is double endAt)
                {
                    if (currentTimeSeconds - endAt >= StickyResolvedLingerSeconds)
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

        public long ComputeSignature(double currentTimeSeconds)
        {
            if (!HasAnyActive)
            {
                return 0;
            }

            HashCode hc = new();
            if (Prediction is not null)
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
                for (int i = 0; i < Prediction.Outcomes.Count; i++)
                {
                    hc.Add(Prediction.Outcomes[i].TotalPoints);
                }
            }

            if (Poll is not null)
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
                for (int i = 0; i < Poll.Choices.Count; i++)
                {
                    hc.Add(Poll.Choices[i].TotalVoters);
                }
            }

            if (PinnedChat is not null)
            {
                hc.Add(3);
                hc.Add(PinnedChat.PinId);
                hc.Add(PinnedChat.MessageText);
                hc.Add(PinnedChat.SenderDisplayName);
                hc.Add(PinnedChat.SenderColor);
            }

            if (HypeTrain is not null)
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

    /// <summary>
    /// Renders sticky top broadcast overlay cards (Predictions, Polls, Pinned Chat, Hype Trains) onto the update frame canvas.
    /// </summary>
    internal sealed class StickyOverlayRenderer
    {
        private const double StickyMaxHeightRatio = 0.25;

        private readonly ChatRenderOptions _options;
        private readonly AssetCatalog _assets;
        private readonly TextLayoutEngine _layout;

        public StickyOverlayRenderer(ChatRenderOptions options, AssetCatalog assets, TextLayoutEngine layout)
        {
            _options = options;
            _assets = assets;
            _layout = layout;
        }

        public int DrawStickyTopOverlays(SKCanvas frameCanvas, StickyOverlayTracker? tracker, double currentTimeSeconds, SectionRenderContext ctx)
        {
            if (tracker is null || !tracker.HasAnyActive)
            {
                return 0;
            }

            int baseRowH = Math.Max(18, _options.SectionHeight);
            int maxAllowedHeight = Math.Max(baseRowH, (int)(_options.ChatHeight * StickyMaxHeightRatio));

            var candidates = new List<(StickyWidgetKind Kind, int Height)>(4);

            if (tracker.Prediction is not null)
            {
                bool isLocked = tracker.Prediction.Status == "LOCKED";
                bool isOpen = tracker.Prediction.Status == "ACTIVE";
                int outcomeCount = Math.Clamp(tracker.Prediction.Outcomes.Count, 2, 10);
                int h = isOpen
                    ? (int)Math.Round(baseRowH * (0.82 + outcomeCount * 0.56))
                    : (int)Math.Round(baseRowH * 1.45);
                candidates.Add((isLocked ? StickyWidgetKind.PredictionLocked : StickyWidgetKind.PredictionOpenOrResolved, h));
                if (isOpen)
                {
                    maxAllowedHeight = Math.Max(maxAllowedHeight, h);
                }
            }

            if (tracker.Poll is not null)
            {
                int choiceCount = Math.Clamp(tracker.Poll.Choices.Count, 1, 5);
                int h = (int)Math.Round(baseRowH * (0.82 + choiceCount * 0.56));
                candidates.Add((StickyWidgetKind.Poll, h));
            }

            List<string>? pinnedChatWrappedLines = null;
            if (tracker.PinnedChat is not null)
            {
                pinnedChatWrappedLines = LayoutStickyPinnedChat(tracker.PinnedChat, ctx, out int pinHeight);
                candidates.Add((StickyWidgetKind.PinnedChat, pinHeight));
                maxAllowedHeight = Math.Max(maxAllowedHeight, pinHeight);
            }

            if (tracker.HypeTrain is not null)
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
                if (i == 0 || c.Kind == StickyWidgetKind.PinnedChat || totalHeight + c.Height <= maxAllowedHeight)
                {
                    int maxSingleCap = c.Kind == StickyWidgetKind.PinnedChat
                        ? Math.Max(baseRowH, (int)(_options.ChatHeight * 0.65))
                        : Math.Max(baseRowH, (int)(_options.ChatHeight * 0.42));
                    int clampedH = i == 0 || c.Kind == StickyWidgetKind.PinnedChat ? Math.Min(c.Height, maxSingleCap) : c.Height;
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
                        if (tracker.Prediction is not null)
                        {
                            DrawStickyPredictionWidget(frameCanvas, tracker.Prediction, currentY, widgetHeight, currentTimeSeconds, ctx);
                        }
                        break;
                    case StickyWidgetKind.Poll:
                        if (tracker.Poll is not null)
                        {
                            DrawStickyPollWidget(frameCanvas, tracker.Poll, currentY, widgetHeight, currentTimeSeconds, ctx);
                        }
                        break;
                    case StickyWidgetKind.PinnedChat:
                        if (tracker.PinnedChat is not null)
                        {
                            DrawStickyPinnedChatWidget(frameCanvas, tracker.PinnedChat, pinnedChatWrappedLines, currentY, widgetHeight, ctx);
                        }
                        break;
                    case StickyWidgetKind.HypeTrain:
                        if (tracker.HypeTrain is not null)
                        {
                            DrawStickyHypeTrainWidget(frameCanvas, tracker.HypeTrain, currentY, widgetHeight, ctx);
                        }
                        break;
                }

                currentY += widgetHeight;
            }

            return totalHeight;
        }

        private void DrawStickyPredictionWidget(SKCanvas canvas, ActivePredictionState pred, int topY, int height, double currentTimeSeconds, SectionRenderContext ctx)
        {
            int width = _options.ChatWidth;
            int padX = Math.Max(6, _options.SidePadding);

            DrawStickyCardBackground(canvas, topY, width, height, new SKColor(0xFF387AFF));

            using var titleFont = ctx.NameFont.Clone();
            titleFont.TextSize = (float)Math.Max(9, _options.FontSize * 0.82);
            titleFont.Color = SKColors.White;

            using var subFont = ctx.MessageFont.Clone();
            subFont.TextSize = (float)Math.Max(8, _options.FontSize * 0.74);
            subFont.Color = new SKColor(0xFFADADB8);

            bool isOpen = pred.Status == "ACTIVE";
            string statusTag = pred.Status switch
            {
                "LOCKED" => "LOCKED",
                "RESOLVED" => "RESOLVED",
                "CANCELED" => "CANCELED",
                _ => FormatCountdownSeconds(Math.Max(0, (int)Math.Ceiling(pred.WindowSeconds - (currentTimeSeconds - pred.StartedOffsetSeconds))))
            };

            long totalPoints = 0;
            for (int i = 0; i < pred.Outcomes.Count; i++)
            {
                totalPoints += pred.Outcomes[i].TotalPoints;
            }

            if (isOpen)
            {
                int outcomeCount = Math.Clamp(pred.Outcomes.Count, 2, 10);
                float headerRegionH = Math.Max(14f, height * (0.76f / (0.76f + outcomeCount * 0.56f)));
                float openRow1Baseline = topY + headerRegionH * 0.74f;

                _layout.DrawSingleLineTextDirect(canvas, statusTag, width - padX - subFont.MeasureText(statusTag), openRow1Baseline, subFont, ctx, _assets);

                string openHeaderText = $"🔮 {pred.Title}";
                string openClippedHeader = TextLayoutEngine.TruncateSingleLine(openHeaderText, titleFont, Math.Max(30, width - padX * 2 - (int)subFont.MeasureText(statusTag) - padX));
                _layout.DrawSingleLineTextDirect(canvas, openClippedHeader, padX + 4, openRow1Baseline, titleFont, ctx, _assets);

                float outcomesAreaTop = topY + headerRegionH;
                float outcomesAreaHeight = Math.Max(12f, height - headerRegionH - 3f);
                float slotHeight = outcomesAreaHeight / outcomeCount;
                float openBarHeight = Math.Max(8f, slotHeight * 0.82f);
                float openBarLeft = padX + 4;
                float openBarTotalWidth = Math.Max(20f, width - openBarLeft - padX);

                using var trackPaint = new SKPaint { Color = new SKColor(0xFF2D2D35), IsAntialias = true };
                using var blueFillPaint = new SKPaint { Color = new SKColor(0xFF387AFF), IsAntialias = true };
                using var pinkFillPaint = new SKPaint { Color = new SKColor(0xFFF5009B), IsAntialias = true };

                SKRect textBounds = new();
                subFont.MeasureText("Ag", ref textBounds);
                subFont.Color = SKColors.White;

                for (int i = 0; i < outcomeCount; i++)
                {
                    var outcome = i < pred.Outcomes.Count
                        ? pred.Outcomes[i]
                        : new StickyPredictionOutcome((i + 1).ToString(), i == 0 ? "Yes" : "No", i == 0 ? "BLUE" : "PINK", 0, 0);

                    int pct = totalPoints > 0 ? (int)Math.Round(outcome.TotalPoints * 100.0 / totalPoints) : 0;
                    bool isPink = string.Equals(outcome.Color, "PINK", StringComparison.OrdinalIgnoreCase)
                        || (i % 2 == 1 && !string.Equals(outcome.Color, "BLUE", StringComparison.OrdinalIgnoreCase));

                    float slotTop = outcomesAreaTop + i * slotHeight;
                    float curBarTop = slotTop + (slotHeight - openBarHeight) * 0.5f;

                    canvas.DrawRoundRect(openBarLeft, curBarTop, openBarTotalWidth, openBarHeight, 3f, 3f, trackPaint);

                    if (pct > 0)
                    {
                        float fillW = openBarTotalWidth * Math.Clamp(pct / 100f, 0.02f, 1.0f);
                        canvas.DrawRoundRect(openBarLeft, curBarTop, fillW, openBarHeight, 3f, 3f, isPink ? pinkFillPaint : blueFillPaint);
                    }

                    string rightPctText = $"{pct}%";
                    float rightPctWidth = subFont.MeasureText(rightPctText);
                    float textBaseline = curBarTop + (openBarHeight + textBounds.Height) * 0.5f - 1f;

                    int maxTitleWidth = Math.Max(20, (int)(openBarTotalWidth - rightPctWidth - 16));
                    string clippedTitle = TextLayoutEngine.TruncateSingleLine(outcome.Title, subFont, maxTitleWidth);

                    _layout.DrawSingleLineTextDirect(canvas, clippedTitle, openBarLeft + 6, textBaseline, subFont, ctx, _assets);
                    _layout.DrawSingleLineTextDirect(canvas, rightPctText, openBarLeft + openBarTotalWidth - rightPctWidth - 6, textBaseline, subFont, ctx, _assets);
                }

                return;
            }

            float statusWidth = subFont.MeasureText(statusTag) + padX;
            float row1Baseline = topY + height * 0.40f;
            _layout.DrawSingleLineTextDirect(canvas, statusTag, width - padX - subFont.MeasureText(statusTag), row1Baseline, subFont, ctx, _assets);

            string headerText = $"🔮 {pred.Title}";
            string clippedHeader = TextLayoutEngine.TruncateSingleLine(headerText, titleFont, Math.Max(30, width - padX * 2 - (int)statusWidth));
            _layout.DrawSingleLineTextDirect(canvas, clippedHeader, padX + 4, row1Baseline, titleFont, ctx, _assets);

            var o1 = pred.Outcomes.Count > 0 ? pred.Outcomes[0] : new StickyPredictionOutcome("1", "Yes", "BLUE", 0, 0);
            var o2 = pred.Outcomes.Count > 1 ? pred.Outcomes[1] : new StickyPredictionOutcome("2", "No", "PINK", 0, 0);

            if (pred.Status == "RESOLVED" && !string.IsNullOrEmpty(pred.WinningOutcomeId)
                && !string.Equals(o1.Id, pred.WinningOutcomeId, StringComparison.Ordinal)
                && !string.Equals(o2.Id, pred.WinningOutcomeId, StringComparison.Ordinal))
            {
                var winningOutcome = pred.Outcomes.FirstOrDefault(o => string.Equals(o.Id, pred.WinningOutcomeId, StringComparison.Ordinal));
                if (winningOutcome is not null)
                {
                    o1 = winningOutcome;
                }
            }

            int pct1 = totalPoints > 0 ? (int)Math.Round(o1.TotalPoints * 100.0 / totalPoints) : 50;
            int pct2 = totalPoints > 0 ? (pred.Outcomes.Count <= 2 ? 100 - pct1 : (int)Math.Round(o2.TotalPoints * 100.0 / totalPoints)) : 50;

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
            string clippedLeft = TextLayoutEngine.TruncateSingleLine(leftLabel, subFont, (int)(barTotalWidth * 0.48f));
            string clippedRight = TextLayoutEngine.TruncateSingleLine(rightLabel, subFont, (int)(barTotalWidth * 0.48f));

            _layout.DrawSingleLineTextDirect(canvas, clippedLeft, barLeft + 5, barTextBaseline, subFont, ctx, _assets);
            _layout.DrawSingleLineTextDirect(canvas, clippedRight, barLeft + barTotalWidth - subFont.MeasureText(clippedRight) - 5, barTextBaseline, subFont, ctx, _assets);
        }

        private void DrawStickyPollWidget(SKCanvas canvas, ActivePollState poll, int topY, int height, double currentTimeSeconds, SectionRenderContext ctx)
        {
            int width = _options.ChatWidth;
            int padX = Math.Max(6, _options.SidePadding);

            DrawStickyCardBackground(canvas, topY, width, height, new SKColor(0xFF00DB84));

            using var titleFont = ctx.NameFont.Clone();
            titleFont.TextSize = (float)Math.Max(9, _options.FontSize * 0.80);
            titleFont.Color = SKColors.White;

            using var subFont = ctx.MessageFont.Clone();
            subFont.TextSize = (float)Math.Max(8, _options.FontSize * 0.72);
            subFont.Color = new SKColor(0xFFADADB8);

            bool isEnded = poll.Status != "ACTIVE";
            string statusTag = isEnded
                ? "POLL ENDED"
                : FormatCountdownSeconds(Math.Max(0, (int)Math.Ceiling(poll.DurationSeconds - (currentTimeSeconds - poll.StartedOffsetSeconds))));

            int choiceCount = Math.Clamp(poll.Choices.Count, 1, 5);
            float headerRegionH = Math.Max(14f, height * (0.76f / (0.76f + choiceCount * 0.56f)));
            float row1Baseline = topY + headerRegionH * 0.74f;

            _layout.DrawSingleLineTextDirect(canvas, statusTag, width - padX - subFont.MeasureText(statusTag), row1Baseline, subFont, ctx, _assets);

            string headerText = $"📊 {poll.Title}";
            string clippedHeader = TextLayoutEngine.TruncateSingleLine(headerText, titleFont, Math.Max(30, width - padX * 2 - (int)subFont.MeasureText(statusTag) - padX));
            _layout.DrawSingleLineTextDirect(canvas, clippedHeader, padX + 4, row1Baseline, titleFont, ctx, _assets);

            if (poll.Choices.Count == 0)
            {
                return;
            }

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
            using var activeFillPaint = new SKPaint { Color = AssetCatalog.Purple, IsAntialias = true };
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
                string clippedTitle = TextLayoutEngine.TruncateSingleLine(leftTitle, subFont, maxTitleWidth);

                _layout.DrawSingleLineTextDirect(canvas, clippedTitle, barLeft + 6, textBaseline, subFont, ctx, _assets);
                _layout.DrawSingleLineTextDirect(canvas, rightPctText, barLeft + barTotalWidth - rightPctWidth - 6, textBaseline, subFont, ctx, _assets);
            }
        }

        private List<string> LayoutStickyPinnedChat(ActivePinnedChatState pin, SectionRenderContext ctx, out int totalHeight)
        {
            int width = _options.ChatWidth;
            int padX = Math.Max(6, _options.SidePadding);
            int baseRowH = Math.Max(18, _options.SectionHeight);

            using var nameFontLocal = ctx.NameFont.Clone();
            nameFontLocal.TextSize = (float)Math.Max(9, _options.FontSize * 0.82);

            using var msgFontLocal = ctx.MessageFont.Clone();
            msgFontLocal.TextSize = (float)Math.Max(9, _options.FontSize * 0.82);

            float leftX = padX + 4;
            string prefix = $"📌 {pin.SenderDisplayName}: ";
            float afterPrefixX = _layout.DrawSingleLineTextDirect(null, prefix, leftX, 0f, nameFontLocal, ctx, _assets);

            float firstLineMaxW = Math.Max(20f, width - afterPrefixX - padX);
            float fullLineMaxW = Math.Max(20f, width - leftX - padX);

            var lines = WrapPinnedChatMessageLines(pin.MessageText, msgFontLocal, firstLineMaxW, fullLineMaxW, ctx);
            int lineCount = Math.Max(1, lines.Count);
            float lineStep = Math.Max(12f, Math.Max(msgFontLocal.TextSize * 1.28f, baseRowH * 0.72f));

            totalHeight = (int)Math.Round(baseRowH * 1.20 + (lineCount - 1) * lineStep);
            return lines;
        }

        private List<string> WrapPinnedChatMessageLines(string rawText, SKPaint msgFont, float firstLineMaxW, float fullLineMaxW, SectionRenderContext ctx)
        {
            string normalized = (rawText ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (string.IsNullOrEmpty(normalized))
            {
                return [""];
            }

            var lines = new List<string>(2);
            ReadOnlySpan<char> rem = normalized.AsSpan();

            while (!rem.IsEmpty)
            {
                float maxW = lines.Count == 0 ? firstLineMaxW : fullLineMaxW;
                string remStr = rem.ToString();
                if (_layout.DrawSingleLineTextDirect(null, remStr, 0f, 0f, msgFont, ctx, _assets) <= maxW)
                {
                    lines.Add(remStr);
                    break;
                }

                int breakCount = _layout.MeasureFittingCharCount(rem, maxW, msgFont, ctx, _assets);
                int lastSpace = rem[..breakCount].LastIndexOf(' ');
                if (lastSpace > 0)
                {
                    lines.Add(rem[..lastSpace].TrimEnd().ToString());
                    rem = rem[(lastSpace + 1)..].TrimStart();
                    continue;
                }

                if (lines.Count == 0 && firstLineMaxW < fullLineMaxW)
                {
                    int nextSpace = rem.IndexOf(' ');
                    ReadOnlySpan<char> firstWord = nextSpace >= 0 ? rem[..nextSpace] : rem;
                    if (_layout.DrawSingleLineTextDirect(null, firstWord.ToString(), 0f, 0f, msgFont, ctx, _assets) <= fullLineMaxW)
                    {
                        lines.Add("");
                        continue;
                    }
                }

                int splitAt = breakCount;
                int minPunctIdx = Math.Max(1, breakCount / 2);
                for (int k = breakCount - 1; k >= minPunctIdx; k--)
                {
                    char ch = rem[k];
                    if (ch is '/' or '-' or '_' or '?' or '&' or '=' or '.')
                    {
                        splitAt = k + 1;
                        break;
                    }
                }

                lines.Add(rem[..splitAt].ToString());
                rem = rem[splitAt..].TrimStart();
            }

            if (lines.Count == 0)
            {
                lines.Add("");
            }

            return lines;
        }

        private void DrawStickyPinnedChatWidget(SKCanvas canvas, ActivePinnedChatState pin, List<string>? wrappedLines, int topY, int height, SectionRenderContext ctx)
        {
            int width = _options.ChatWidth;
            int padX = Math.Max(6, _options.SidePadding);
            int baseRowH = Math.Max(18, _options.SectionHeight);

            DrawStickyCardBackground(canvas, topY, width, height, AssetCatalog.Purple);

            using var nameFontLocal = ctx.NameFont.Clone();
            nameFontLocal.TextSize = (float)Math.Max(9, _options.FontSize * 0.82);
            nameFontLocal.Color = TextLayoutEngine.ResolveUsernameColor(pin.SenderColor, pin.SenderDisplayName, SKColors.White);

            using var msgFontLocal = ctx.MessageFont.Clone();
            msgFontLocal.TextSize = (float)Math.Max(9, _options.FontSize * 0.82);
            msgFontLocal.Color = SKColors.White;

            wrappedLines ??= LayoutStickyPinnedChat(pin, ctx, out _);
            int lineCount = Math.Max(1, wrappedLines.Count);

            SKRect capBounds = new();
            msgFontLocal.MeasureText("H", ref capBounds);
            float capH = capBounds.Height > 1f ? capBounds.Height : msgFontLocal.TextSize * 0.72f;
            float lineStep = Math.Max(12f, Math.Max(msgFontLocal.TextSize * 1.28f, baseRowH * 0.72f));
            float totalTextSpan = capH + (lineCount - 1) * lineStep;
            float firstBaselineY = topY + (height - totalTextSpan) * 0.5f + capH;

            float leftX = padX + 4;
            string prefix = $"📌 {pin.SenderDisplayName}: ";
            float curX = _layout.DrawSingleLineTextDirect(canvas, prefix, leftX, firstBaselineY, nameFontLocal, ctx, _assets);

            if (!string.IsNullOrEmpty(wrappedLines[0]))
            {
                _layout.DrawSingleLineTextDirect(canvas, wrappedLines[0], curX, firstBaselineY, msgFontLocal, ctx, _assets);
            }

            for (int i = 1; i < lineCount; i++)
            {
                float lineBaselineY = firstBaselineY + i * lineStep;
                _layout.DrawSingleLineTextDirect(canvas, wrappedLines[i], leftX, lineBaselineY, msgFontLocal, ctx, _assets);
            }
        }

        private void DrawStickyHypeTrainWidget(SKCanvas canvas, ActiveHypeTrainState ht, int topY, int height, SectionRenderContext ctx)
        {
            int width = _options.ChatWidth;
            int padX = Math.Max(6, _options.SidePadding);

            DrawStickyCardBackground(canvas, topY, width, height, new SKColor(0xFFFFB31A));

            using var titleFont = ctx.NameFont.Clone();
            titleFont.TextSize = (float)Math.Max(9, _options.FontSize * 0.82);
            titleFont.Color = SKColors.White;

            int pct = ht.Goal > 0 ? Math.Clamp((int)Math.Round(ht.Progress * 100.0 / ht.Goal), 0, 100) : 100;
            string label = ht.IsEnded
                ? $"🚂 HYPE TRAIN ENDED • LVL {ht.Level}"
                : $"🚂 HYPE TRAIN LVL {ht.Level} • {pct}%";

            float baselineY = topY + height * 0.56f;
            string clipped = TextLayoutEngine.TruncateSingleLine(label, titleFont, width - padX * 2 - 8);
            _layout.DrawSingleLineTextDirect(canvas, clipped, padX + 4, baselineY, titleFont, ctx, _assets);

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
            using var cardBgPaint = new SKPaint
            {
                Color = new SKColor(0xF518181B),
                BlendMode = SKBlendMode.Src
            };
            canvas.DrawRect(0, topY, width, height, cardBgPaint);

            using var accentPaint = new SKPaint { Color = accentColor };
            int strokeW = Math.Max(3, _options.AccentStrokeWidth / 2);
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
    }
}

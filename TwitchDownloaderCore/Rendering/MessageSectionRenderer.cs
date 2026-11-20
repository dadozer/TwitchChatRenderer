#nullable enable
using SkiaSharp;
using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using TwitchDownloaderCore.Chat;
using TwitchDownloaderCore.Chat.Timeline;
using TwitchDownloaderCore.Extensions;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Models.Render;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Tools;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.Rendering
{
    /// <summary>
    /// Pure, stateless section renderer that rasterizes immutable <see cref="TimelineMessage"/> records
    /// into pooled <see cref="CommentSection"/> surfaces without mutating input messages or calling <c>Comment.Clone()</c>.
    /// </summary>
    internal sealed partial class MessageSectionRenderer
    {
        private const char ZERO_WIDTH_JOINER = '\u200D';

        private static readonly SearchValues<char> DigitChars = SearchValues.Create("0123456789");

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

        private readonly ChatRenderOptions _options;
        private readonly AssetCatalog _assets;
        private readonly TextLayoutEngine _layout;

        public MessageSectionRenderer(ChatRenderOptions options, AssetCatalog assets, TextLayoutEngine layout)
        {
            _options = options;
            _assets = assets;
            _layout = layout;
        }

        public CommentSection? RenderSection(
            IReadOnlyList<TimelineMessage> messages,
            int messageIndex,
            SectionRenderContext ctx,
            bool isModerated = false)
        {
            if ((uint)messageIndex >= (uint)messages.Count)
            {
                return null;
            }

            var message = messages[messageIndex];
            return RenderMessage(message, messageIndex, ctx, isModerated);
        }

        public CommentSection? RenderMessage(
            TimelineMessage message,
            int messageIndex,
            SectionRenderContext ctx,
            bool isModerated = false)
        {
            ctx.PendingGlyphCount = 0;
            ctx.PendingCanvas = null;

            bool replaceDeleted = isModerated && _options.ModerationMode == ModerationDisplayMode.ReplaceDeleted;
            var activeContent = replaceDeleted ? TimelineMessageContent.Deleted : message.Content;
            var notice = message.Notice;

            var emoteSectionList = new List<EmotePosition>();
            var sectionImages = new List<SectionImage>();
            var drawPos = new Point();
            var defaultPos = new Point
            {
                X = _options.SidePadding,
                Y = _layout.SectionDefaultYPos
            };

            _layout.AddImageSection(sectionImages, _assets.SectionImageCache, ref drawPos, defaultPos);
            drawPos.Y = defaultPos.Y;

            var highlightType = HighlightType.None;
            SKColor? customAccentColor = notice.CustomAccentColor;

            switch (notice.Kind)
            {
                case TimelineNoticeKind.CompactSystemNotice:
                {
                    drawPos.X = _options.SidePadding;
                    defaultPos.X = drawPos.X;
                    if (_options.Timestamp)
                    {
                        DrawTimestamp(message.AppearSeconds, sectionImages, ref drawPos, ref defaultPos, ctx);
                    }

                    using var mutedFont = ctx.MessageFont.Clone();
                    mutedFont.Color = _options.MessageColor.WithAlpha(155);
                    _layout.DrawText(notice.SystemText ?? activeContent.Body, mutedFont, false, sectionImages, _assets.SectionImageCache, ref drawPos, defaultPos, false, ctx);
                    FlushAllSections(sectionImages);
                    break;
                }

                case TimelineNoticeKind.AccentedSystemNotice:
                {
                    customAccentColor ??= new SKColor(0xFF9146FF);
                    drawPos.X += _options.AccentIndentWidth - _options.AccentStrokeWidth / 2;
                    defaultPos.X = drawPos.X;

                    if (_options.Timestamp)
                    {
                        DrawTimestamp(message.AppearSeconds, sectionImages, ref drawPos, ref defaultPos, ctx);
                    }

                    DrawMessageContent(message, activeContent, sectionImages, emoteSectionList, false, ref drawPos, defaultPos, ctx);
                    FlushAllSections(sectionImages);
                    break;
                }

                case TimelineNoticeKind.Announcement:
                {
                    customAccentColor ??= new SKColor(0xFF9146FF);
                    drawPos.X += _options.AccentIndentWidth - _options.AccentStrokeWidth / 2;
                    defaultPos.X = drawPos.X;

                    using var badgeFont = ctx.NameFont.Clone();
                    badgeFont.Color = customAccentColor.Value;
                    _layout.DrawText("[Announcement]", badgeFont, true, sectionImages, _assets.SectionImageCache, ref drawPos, defaultPos, false, ctx);
                    DrawNonAccentedMessage(message, activeContent, sectionImages, emoteSectionList, false, messageIndex, ref drawPos, ref defaultPos, ctx);
                    break;
                }

                case TimelineNoticeKind.ExtendedUserNotice:
                {
                    if (!_options.SubMessages && notice.NoticeType is "sub" or "resub" or "subgift" or "submysterygift" or "viewermilestone")
                    {
                        ReturnAllSections(sectionImages);
                        return null;
                    }

                    customAccentColor ??= notice.NoticeType == "viewermilestone" ? new SKColor(0xFF00C7AC) : AssetCatalog.Purple;
                    drawPos.X += _options.AccentIndentWidth - _options.AccentStrokeWidth / 2;
                    defaultPos.X = drawPos.X;

                    var highlightIcon = _assets.HighlightIcons.GetHighlightIcon(notice.IconType, ctx.MessageFont.Color);
                    int iconY = (int)((_options.SectionHeight - highlightIcon.Height) / 2.0);
                    var iconPoint = new Point { X = drawPos.X, Y = iconY };
                    var messageLineStart = drawPos;

                    bool hasCustomUserMessage = notice.CustomUserContent is not null;
                    string sysMsg = notice.SystemText ?? "";

                    if (notice.NoticeType is "sub" or "resub" or "viewermilestone")
                    {
                        string compactSummary = notice.NoticeType == "viewermilestone"
                            ? BuildCompactWatchStreakSummary(notice, sysMsg)
                            : BuildCompactSubSummary(notice, sysMsg);

                        DrawCompactSubOrStreakHeaderLine(
                            message.Author,
                            sectionImages,
                            ref drawPos,
                            highlightIcon,
                            iconPoint,
                            compactSummary,
                            includeUsername: !hasCustomUserMessage,
                            ctx);
                    }
                    else if (notice.NoticeType is "subgift" or "submysterygift")
                    {
                        DrawCompactGiftMessageLine(message.Author, notice, sysMsg, sectionImages, ref drawPos, highlightIcon, iconPoint, ctx);
                    }
                    else
                    {
                        var canvas = sectionImages[^1].Canvas;
                        canvas.DrawImage(highlightIcon, iconPoint.X, iconPoint.Y);
                        drawPos.X += highlightIcon.Width + _options.WordSpacing;
                        defaultPos.X = drawPos.X;

                        using var sysFont = ctx.MessageFont.Clone();
                        sysFont.Color = _options.MessageColor.WithAlpha(220);
                        _layout.DrawText(sysMsg, sysFont, false, sectionImages, _assets.SectionImageCache, ref drawPos, defaultPos, false, ctx);
                    }

                    if (hasCustomUserMessage)
                    {
                        _layout.AddImageSection(sectionImages, _assets.SectionImageCache, ref drawPos, messageLineStart);
                        drawPos = messageLineStart;
                        defaultPos = messageLineStart;
                        var userPart = replaceDeleted ? TimelineMessageContent.Deleted : notice.CustomUserContent!;
                        DrawNonAccentedMessage(message, userPart, sectionImages, emoteSectionList, false, messageIndex, ref drawPos, ref defaultPos, ctx);
                    }
                    else
                    {
                        FlushAllSections(sectionImages);
                    }
                    break;
                }

                case TimelineNoticeKind.LegacyHighlight:
                {
                    highlightType = notice.IconType;
                    if (highlightType is not HighlightType.ChannelPointHighlight && !_options.SubMessages)
                    {
                        ReturnAllSections(sectionImages);
                        return null;
                    }

                    DrawLegacyAccentedMessage(message, activeContent, notice, sectionImages, emoteSectionList, highlightType, messageIndex, replaceDeleted, ref drawPos, defaultPos, ctx);
                    break;
                }

                default:
                {
                    bool hasLeftAccentBar = (_options.RenderHypeChat && message.HypeChat is not null)
                        || (_options.HighlightFirstTimeChatter && (message.IsFirstMessage || message.IsReturningChatter));

                    if (hasLeftAccentBar)
                    {
                        drawPos.X += _options.AccentIndentWidth - _options.AccentStrokeWidth / 2;
                        defaultPos.X = drawPos.X;
                    }

                    DrawNonAccentedMessage(message, activeContent, sectionImages, emoteSectionList, false, messageIndex, ref drawPos, ref defaultPos, ctx);
                    break;
                }
            }

            int replyHeaderHeight = message.IsSynthetic ? 0 : GetReplyHeaderHeight(message);
            if (replyHeaderHeight > 0 && emoteSectionList.Count > 0)
            {
                for (int i = 0; i < emoteSectionList.Count; i++)
                {
                    var ep = emoteSectionList[i];
                    emoteSectionList[i] = new EmotePosition(new Point { X = ep.DrawPoint.X, Y = ep.DrawPoint.Y + replyHeaderHeight }, ep.Emote);
                }
            }

            var combinedImage = CombineImages(
                sectionImages,
                highlightType,
                message,
                messageIndex,
                ctx,
                replyHeaderHeight,
                customAccentColor,
                emoteSectionList,
                isModerated);

            int combinedHeight = combinedImage.Info.Height;
            bool sectionHasAnimated = false;
            int maxTopOverhang = 0;
            int maxBottomOverhang = 0;

            for (int i = 0; i < emoteSectionList.Count; i++)
            {
                var (dp, em) = emoteSectionList[i];
                if (em.FrameCount > 1)
                {
                    sectionHasAnimated = true;
                }

                if (dp.Y < 0 && -dp.Y > maxTopOverhang)
                {
                    maxTopOverhang = -dp.Y;
                }

                int bottomOver = dp.Y + em.Height - combinedHeight;
                if (bottomOver > maxBottomOverhang)
                {
                    maxBottomOverhang = bottomOver;
                }
            }

            return new CommentSection
            {
                Image = combinedImage,
                Emotes = emoteSectionList,
                CommentIndex = messageIndex,
                IsModeratedRendered = isModerated,
                HasAnimatedEmotes = sectionHasAnimated,
                MaxTopOverhang = maxTopOverhang,
                MaxBottomOverhang = maxBottomOverhang
            };
        }

        private void ReturnAllSections(List<SectionImage> sectionImages)
        {
            for (int i = 0; i < sectionImages.Count; i++)
            {
                _assets.SectionImageCache.Return(sectionImages[i]);
            }
            sectionImages.Clear();
        }

        private static void FlushAllSections(List<SectionImage> sectionImages)
        {
            for (int i = 0; i < sectionImages.Count; i++)
            {
                sectionImages[i].Flush();
            }
        }

        private SectionImage CombineImages(
            List<SectionImage> sectionImages,
            HighlightType highlightType,
            TimelineMessage message,
            int messageIndex,
            SectionRenderContext ctx,
            int replyHeaderHeight,
            SKColor? customAccentColor,
            List<EmotePosition> emoteSectionList,
            bool isModerated)
        {
            bool hasAccentOrCardUnderlay = customAccentColor.HasValue
                || (_options.RenderHypeChat && message.HypeChat is not null)
                || (_options.HighlightFirstTimeChatter && (message.IsFirstMessage || message.IsReturningChatter))
                || (highlightType is not HighlightType.None and not HighlightType.Unknown);

            if (sectionImages.Count == 1 && replyHeaderHeight == 0 && !hasAccentOrCardUnderlay)
            {
                var directSection = sectionImages[0];
                sectionImages.Clear();

                if (isModerated && _options.ModerationMode == ModerationDisplayMode.Strikethrough)
                {
                    ApplyStrikethroughModeration(directSection.Canvas, directSection.Info, emoteSectionList, 0);
                }
                else if (isModerated && _options.ModerationMode == ModerationDisplayMode.ReplaceDeleted)
                {
                    using var dimPaint = new SKPaint
                    {
                        Color = new SKColor(0, 0, 0, 155),
                        BlendMode = SKBlendMode.DstIn
                    };
                    directSection.Canvas.DrawRect(0, 0, directSection.Info.Width, directSection.Info.Height, dimPaint);
                }

                directSection.Flush();
                return directSection;
            }

            int totalHeight = replyHeaderHeight;
            for (int i = 0; i < sectionImages.Count; i++)
            {
                totalHeight += sectionImages[i].Info.Height;
            }

            var rentedImage = _assets.SectionImageCache.Rent(_options.ChatWidth, Math.Max(1, totalHeight));
            var bitmapInfo = rentedImage.Info;
            var canvas = rentedImage.Canvas;
            bool hasLeftAccentBar = false;

            if (customAccentColor.HasValue)
            {
                hasLeftAccentBar = true;
                var bgPaint = TextLayoutEngine.GetCachedPaint(customAccentColor.Value.WithAlpha(28), ctx.PaintCache);
                canvas.DrawRect(_options.SidePadding, 0, bitmapInfo.Width - _options.SidePadding * 2, bitmapInfo.Height, bgPaint);

                var accentPaint = TextLayoutEngine.GetCachedPaint(customAccentColor.Value, ctx.PaintCache);
                canvas.DrawRect(_options.SidePadding, 0, _options.AccentStrokeWidth, bitmapInfo.Height, accentPaint);
            }
            else if (_options.RenderHypeChat && message.HypeChat is not null)
            {
                hasLeftAccentBar = true;
                var hypeColor = GetHypeChatLevelColor(message.HypeChat.Level);
                var bgPaint = TextLayoutEngine.GetCachedPaint(hypeColor.WithAlpha(36), ctx.PaintCache);
                canvas.DrawRect(_options.SidePadding, 0, bitmapInfo.Width - _options.SidePadding * 2, bitmapInfo.Height, bgPaint);

                var accentPaint = TextLayoutEngine.GetCachedPaint(hypeColor, ctx.PaintCache);
                canvas.DrawRect(_options.SidePadding, 0, _options.AccentStrokeWidth, bitmapInfo.Height, accentPaint);
            }
            else if (highlightType is HighlightType.ChannelPointHighlight)
            {
                hasLeftAccentBar = true;
                var highlightColor = new SKColor(0xFF6B9E82);
                var bgPaint = TextLayoutEngine.GetCachedPaint(highlightColor.WithAlpha(28), ctx.PaintCache);
                canvas.DrawRect(_options.SidePadding, 0, bitmapInfo.Width - _options.SidePadding * 2, bitmapInfo.Height, bgPaint);

                var accentPaint = TextLayoutEngine.GetCachedPaint(highlightColor, ctx.PaintCache);
                canvas.DrawRect(_options.SidePadding, 0, _options.AccentStrokeWidth, bitmapInfo.Height, accentPaint);
            }
            else if (_options.HighlightFirstTimeChatter && (message.IsFirstMessage || message.IsReturningChatter))
            {
                hasLeftAccentBar = true;
                var chatterColor = message.IsFirstMessage ? new SKColor(0xFFD97706) : new SKColor(0xFF00A8FC);
                var bgPaint = TextLayoutEngine.GetCachedPaint(chatterColor.WithAlpha(28), ctx.PaintCache);
                canvas.DrawRect(_options.SidePadding, 0, bitmapInfo.Width - _options.SidePadding * 2, bitmapInfo.Height, bgPaint);

                var accentPaint = TextLayoutEngine.GetCachedPaint(chatterColor, ctx.PaintCache);
                canvas.DrawRect(_options.SidePadding, 0, _options.AccentStrokeWidth, bitmapInfo.Height, accentPaint);
            }
            else if (highlightType is HighlightType.PayingForward or HighlightType.WatchStreak or HighlightType.Combo)
            {
                hasLeftAccentBar = true;
                var accentColor = highlightType is HighlightType.PayingForward
                    ? new SKColor(0xFF26262C)
                    : new SKColor(0xFF80808C);

                var paint = TextLayoutEngine.GetCachedPaint(accentColor, ctx.PaintCache);
                canvas.DrawRect(_options.SidePadding, 0, _options.AccentStrokeWidth, bitmapInfo.Height, paint);
            }
            else if (highlightType is not HighlightType.None and not HighlightType.Unknown)
            {
                hasLeftAccentBar = true;
                const int OPAQUE_THRESHOLD = 245;
                var messageBackground = _layout.GetMessageBackground(message, messageIndex, ctx, out _);
                if (messageBackground.Alpha < OPAQUE_THRESHOLD)
                {
                    var backgroundColor = new SKColor(0x1A6B6B6E);
                    var backgroundPaint = TextLayoutEngine.GetCachedPaint(backgroundColor, ctx.PaintCache);
                    canvas.DrawRect(_options.SidePadding, 0, bitmapInfo.Width - _options.SidePadding * 2, bitmapInfo.Height, backgroundPaint);
                }

                var accentPaint = TextLayoutEngine.GetCachedPaint(AssetCatalog.Purple, ctx.PaintCache);
                canvas.DrawRect(_options.SidePadding, 0, _options.AccentStrokeWidth, bitmapInfo.Height, accentPaint);
            }

            if (replyHeaderHeight > 0 && message.Reply is not null)
            {
                DrawReplyHeaderOnCombinedCanvas(canvas, message.Reply, replyHeaderHeight, hasLeftAccentBar, ctx);
            }

            for (int i = 0; i < sectionImages.Count; i++)
            {
                canvas.DrawBitmap(sectionImages[i].Bitmap, 0, replyHeaderHeight + i * _options.SectionHeight);
                _assets.SectionImageCache.Return(sectionImages[i]);
            }
            sectionImages.Clear();

            if (isModerated && _options.ModerationMode == ModerationDisplayMode.Strikethrough)
            {
                ApplyStrikethroughModeration(canvas, bitmapInfo, emoteSectionList, replyHeaderHeight);
            }
            else if (isModerated && _options.ModerationMode == ModerationDisplayMode.ReplaceDeleted)
            {
                using var dimPaint = new SKPaint
                {
                    Color = new SKColor(0, 0, 0, 155),
                    BlendMode = SKBlendMode.DstIn
                };
                canvas.DrawRect(0, 0, bitmapInfo.Width, bitmapInfo.Height, dimPaint);
            }

            rentedImage.Flush();
            return rentedImage;
        }

        private void DrawNonAccentedMessage(
            TimelineMessage message,
            TimelineMessageContent content,
            List<SectionImage> sectionImages,
            List<EmotePosition> emotePositionList,
            bool highlightWords,
            int messageIndex,
            ref Point drawPos,
            ref Point defaultPos,
            SectionRenderContext ctx)
        {
            if (_options.Timestamp)
            {
                DrawTimestamp(message.AppearSeconds, sectionImages, ref drawPos, ref defaultPos, ctx);
            }
            if (_options.RenderUserAvatars)
            {
                DrawAvatar(message.Author, sectionImages, ref drawPos);
            }
            if (_options.ChatBadges)
            {
                DrawBadges(message.Author, sectionImages, ref drawPos);
            }
            DrawSharedChatInlineBadge(message.SharedChatSourceRoomId, sectionImages, ref drawPos, ctx);
            DrawUsername(message, sectionImages, ref drawPos, defaultPos, ctx, appendColon: true, colorOverride: null, messageIndex: messageIndex);
            DrawHypeChatInlinePill(message.HypeChat, sectionImages, ref drawPos, defaultPos, ctx);
            DrawMessageContent(message, content, sectionImages, emotePositionList, highlightWords, ref drawPos, defaultPos, ctx);

            FlushAllSections(sectionImages);
        }

        private void DrawLegacyAccentedMessage(
            TimelineMessage message,
            TimelineMessageContent content,
            TimelineNoticeInfo notice,
            List<SectionImage> sectionImages,
            List<EmotePosition> emotePositionList,
            HighlightType highlightType,
            int messageIndex,
            bool replaceDeleted,
            ref Point drawPos,
            Point defaultPos,
            SectionRenderContext ctx)
        {
            drawPos.X += highlightType is HighlightType.ChannelPointHighlight
                ? _options.AccentIndentWidth - _options.AccentStrokeWidth / 2
                : _options.AccentIndentWidth;
            defaultPos.X = drawPos.X;

            var highlightIcon = _assets.HighlightIcons.GetHighlightIcon(highlightType, ctx.MessageFont.Color);
            var iconPoint = new Point
            {
                X = drawPos.X,
                Y = (int)((_options.SectionHeight - highlightIcon.Height) / 2.0)
            };

            switch (highlightType)
            {
                case HighlightType.SubscribedTier:
                case HighlightType.SubscribedPrime:
                {
                    var customMessagePos = drawPos;
                    bool hasCustomMessage = notice.CustomUserContent is not null;
                    string compactSummary = BuildCompactSubSummary(notice, notice.SystemText ?? "");

                    DrawCompactSubOrStreakHeaderLine(
                        message.Author,
                        sectionImages,
                        ref drawPos,
                        highlightIcon,
                        iconPoint,
                        compactSummary,
                        includeUsername: !hasCustomMessage,
                        ctx);

                    if (hasCustomMessage)
                    {
                        _layout.AddImageSection(sectionImages, _assets.SectionImageCache, ref drawPos, customMessagePos);
                        drawPos = customMessagePos;
                        defaultPos = customMessagePos;
                        var userPart = replaceDeleted ? TimelineMessageContent.Deleted : notice.CustomUserContent!;
                        DrawNonAccentedMessage(message, userPart, sectionImages, emotePositionList, false, messageIndex, ref drawPos, ref defaultPos, ctx);
                    }
                    break;
                }

                case HighlightType.WatchStreak:
                {
                    var customMessagePos = drawPos;
                    bool hasCustomMessage = notice.CustomUserContent is not null;
                    string compactSummary = BuildCompactWatchStreakSummary(notice, notice.SystemText ?? "");

                    DrawCompactSubOrStreakHeaderLine(
                        message.Author,
                        sectionImages,
                        ref drawPos,
                        highlightIcon,
                        iconPoint,
                        compactSummary,
                        includeUsername: !hasCustomMessage,
                        ctx);

                    if (hasCustomMessage)
                    {
                        _layout.AddImageSection(sectionImages, _assets.SectionImageCache, ref drawPos, customMessagePos);
                        drawPos = customMessagePos;
                        defaultPos = customMessagePos;
                        var userPart = replaceDeleted ? TimelineMessageContent.Deleted : notice.CustomUserContent!;
                        DrawNonAccentedMessage(message, userPart, sectionImages, emotePositionList, false, messageIndex, ref drawPos, ref defaultPos, ctx);
                    }
                    break;
                }

                case HighlightType.BitBadgeTierNotification:
                {
                    var canvas = sectionImages[^1].Canvas;
                    canvas.DrawImage(highlightIcon, iconPoint.X, iconPoint.Y);
                    drawPos.X += highlightIcon.Width + _options.WordSpacing;
                    defaultPos.X = drawPos.X;

                    bool appendColon = content.Fragments.Count != 1;
                    DrawUsername(message, sectionImages, ref drawPos, defaultPos, ctx, appendColon: appendColon, colorOverride: ctx.MessageFont.Color, messageIndex: messageIndex);
                    DrawMessageContent(message, content, sectionImages, emotePositionList, false, ref drawPos, defaultPos, ctx);
                    break;
                }

                case HighlightType.CharityDonation:
                {
                    var canvas = sectionImages[^1].Canvas;
                    canvas.DrawImage(highlightIcon, iconPoint.X, iconPoint.Y);

                    drawPos.X += highlightIcon.Width + _options.WordSpacing;
                    defaultPos.X = drawPos.X;

                    DrawUsername(message, sectionImages, ref drawPos, defaultPos, ctx, appendColon: false, colorOverride: AssetCatalog.Purple, messageIndex: messageIndex);
                    _layout.AddImageSection(sectionImages, _assets.SectionImageCache, ref drawPos, defaultPos);
                    DrawMessageContent(message, content, sectionImages, emotePositionList, false, ref drawPos, defaultPos, ctx);
                    break;
                }

                case HighlightType.GiftedMany:
                case HighlightType.GiftedSingle:
                case HighlightType.GiftedAnonymous:
                case HighlightType.ContinuingGift:
                case HighlightType.ContinuingAnonymousGift:
                case HighlightType.PayingForward:
                    DrawCompactGiftMessageLine(message.Author, notice, notice.SystemText ?? content.Body, sectionImages, ref drawPos, highlightIcon, iconPoint, ctx);
                    break;

                case HighlightType.ChannelPointHighlight:
                    DrawNonAccentedMessage(message, content, sectionImages, emotePositionList, false, messageIndex, ref drawPos, ref defaultPos, ctx);
                    break;

                case HighlightType.Raid:
                case HighlightType.Combo:
                default:
                    DrawMessageContent(message, content, sectionImages, emotePositionList, false, ref drawPos, defaultPos, ctx);
                    break;
            }

            FlushAllSections(sectionImages);
        }

        private void DrawMessageContent(
            TimelineMessage message,
            TimelineMessageContent content,
            List<SectionImage> sectionImages,
            List<EmotePosition> emotePositionList,
            bool highlightWords,
            ref Point drawPos,
            Point defaultPos,
            SectionRenderContext ctx)
        {
            if (GiphyResolver.TryParseAltText(content.Body, out var gifTitle))
            {
                if (_options.GiphyGifs && _assets.GiphyGifs.TryGetValue(gifTitle, out var gif))
                {
                    DrawChatGif(sectionImages, emotePositionList, ref drawPos, defaultPos, gif);
                }
                else
                {
                    DrawChatGifAltText(content.Body, sectionImages, emotePositionList, highlightWords, ref drawPos, defaultPos, ctx);
                }
                return;
            }

            int bitsCount = content.BitsSpent;
            for (int f = 0; f < content.Fragments.Count; f++)
            {
                var fragment = content.Fragments[f];
                if (!string.IsNullOrEmpty(fragment.EmoticonId))
                {
                    _layout.FlushPendingLineText(ctx);
                    DrawFirstPartyEmote(message, sectionImages, emotePositionList, ref drawPos, defaultPos, fragment, highlightWords, ctx);
                    continue;
                }

                var fragmentSpan = fragment.Text.AsSpan();
                if (!TextLayoutEngine.NeedsRtlWordReordering(fragmentSpan))
                {
                    foreach (var range in fragmentSpan.SplitAny(TextLayoutEngine.WhiteSpaceChars))
                    {
                        if (range.Start.Value == range.End.Value) continue;
                        DrawFragmentPart(sectionImages, emotePositionList, ref drawPos, defaultPos, bitsCount, fragmentSpan[range], highlightWords, ctx);
                    }
                    continue;
                }

                var spaceCount = fragmentSpan.CountAny(TextLayoutEngine.WhiteSpaceChars);
                var fragmentParts = ArrayPool<Range>.Shared.Rent(spaceCount + 1);
                try
                {
                    var written = TextLayoutEngine.SwapRightToLeft(fragmentSpan.SplitAny(TextLayoutEngine.WhiteSpaceChars), fragmentParts);
                    for (int r = 0; r < written; r++)
                    {
                        var fragmentPart = fragmentSpan[fragmentParts[r]];
                        DrawFragmentPart(sectionImages, emotePositionList, ref drawPos, defaultPos, bitsCount, fragmentPart, highlightWords, ctx);
                    }
                }
                finally
                {
                    ArrayPool<Range>.Shared.Return(fragmentParts);
                }
            }

            _layout.FlushPendingLineText(ctx);
        }

        private void DrawChatGifAltText(
            string body,
            List<SectionImage> sectionImages,
            List<EmotePosition> emotePositionList,
            bool highlightWords,
            ref Point drawPos,
            Point defaultPos,
            SectionRenderContext ctx)
        {
            var bodySpan = body.AsSpan();
            if (!TextLayoutEngine.NeedsRtlWordReordering(bodySpan))
            {
                foreach (var range in bodySpan.SplitAny(TextLayoutEngine.WhiteSpaceChars))
                {
                    if (range.Start.Value == range.End.Value) continue;
                    DrawFragmentPart(sectionImages, emotePositionList, ref drawPos, defaultPos, 0, bodySpan[range], highlightWords, ctx, skipThird: true);
                }
                _layout.FlushPendingLineText(ctx);
                return;
            }

            var spaceCount = bodySpan.CountAny(TextLayoutEngine.WhiteSpaceChars);
            var bodyParts = ArrayPool<Range>.Shared.Rent(spaceCount + 1);
            try
            {
                var written = TextLayoutEngine.SwapRightToLeft(bodySpan.SplitAny(TextLayoutEngine.WhiteSpaceChars), bodyParts);
                for (int r = 0; r < written; r++)
                {
                    DrawFragmentPart(sectionImages, emotePositionList, ref drawPos, defaultPos, 0, bodySpan[bodyParts[r]], highlightWords, ctx, skipThird: true);
                }
            }
            finally
            {
                ArrayPool<Range>.Shared.Return(bodyParts);
            }

            _layout.FlushPendingLineText(ctx);
        }

        private void DrawChatGif(
            List<SectionImage> sectionImages,
            List<EmotePosition> emotePositionList,
            ref Point drawPos,
            Point defaultPos,
            TwitchEmote gif)
        {
            var gifInfo = gif.Info;
            var sectionsNeeded = Math.Max(1, (int)Math.Ceiling(gifInfo.Height / (double)_options.SectionHeight));
            for (int i = 0; i < sectionsNeeded; i++)
            {
                _layout.AddImageSection(sectionImages, _assets.SectionImageCache, ref drawPos, defaultPos);
            }

            var gifTop = (sectionImages.Count - sectionsNeeded) * _options.SectionHeight;
            emotePositionList.Add(new EmotePosition(new Point { X = _options.SidePadding, Y = gifTop }, gif));
            drawPos.X = _options.SidePadding + gifInfo.Width;
        }

        private void DrawFragmentPart(
            List<SectionImage> sectionImages,
            List<EmotePosition> emotePositionList,
            ref Point drawPos,
            Point defaultPos,
            int bitsCount,
            ReadOnlySpan<char> fragmentPart,
            bool highlightWords,
            SectionRenderContext ctx,
            bool skipThird = false,
            bool skipEmoji = false,
            bool skipNonFont = false)
        {
            var thirdLookup = _assets.ThirdPartyEmotes.GetAlternateLookup<ReadOnlySpan<char>>();
            if (!skipThird && thirdLookup.TryGetValue(fragmentPart, out var emote) && emote is not null)
            {
                _layout.FlushPendingLineText(ctx);
                DrawThirdPartyEmote(sectionImages, emotePositionList, ref drawPos, defaultPos, emote, highlightWords, ctx);
            }
            else if (bitsCount > 0 && TryFlushAndDrawBits(sectionImages, emotePositionList, ref drawPos, defaultPos, bitsCount, fragmentPart, ctx))
            {
                // Drawn cheermote
            }
            else if (!skipEmoji && _options.EmojiVendor != EmojiVendor.None && _assets.ContainsEmoji(fragmentPart, out var firstEmoji))
            {
                _layout.FlushPendingLineText(ctx);
                DrawEmojiMessage(sectionImages, emotePositionList, ref drawPos, defaultPos, bitsCount, fragmentPart, highlightWords, ctx, firstEmoji);
            }
            else if (!skipNonFont && (!ctx.MessageFont.ContainsGlyphs(fragmentPart) || fragmentPart.LengthInTextElements() < fragmentPart.Length))
            {
                _layout.FlushPendingLineText(ctx);
                DrawNonFontMessage(sectionImages, ref drawPos, defaultPos, fragmentPart, highlightWords, ctx);
            }
            else
            {
                _layout.DrawOrBufferPlainMessageWord(fragmentPart, sectionImages, _assets.SectionImageCache, ref drawPos, defaultPos, highlightWords, ctx);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryFlushAndDrawBits(
            List<SectionImage> sectionImages,
            List<EmotePosition> emotePositionList,
            ref Point drawPos,
            Point defaultPos,
            int bitsCount,
            ReadOnlySpan<char> fragmentPart,
            SectionRenderContext ctx)
        {
            _layout.FlushPendingLineText(ctx);
            return TryDrawBits(sectionImages, emotePositionList, ref drawPos, defaultPos, bitsCount, fragmentPart);
        }

        private bool TryDrawBits(
            List<SectionImage> sectionImages,
            List<EmotePosition> emotePositionList,
            ref Point drawPos,
            Point defaultPos,
            int bitsCount,
            ReadOnlySpan<char> fragmentString)
        {
            if (bitsCount < 1)
            {
                return false;
            }

            var bitsIndex = fragmentString.IndexOfAny(DigitChars);
            var cheermoteLookup = _assets.Cheermotes.GetAlternateLookup<ReadOnlySpan<char>>();
            if (bitsIndex > 0
                && int.TryParse(fragmentString[bitsIndex..], out var bitsAmount)
                && cheermoteLookup.TryGetValue(fragmentString[..bitsIndex], out var currentCheerEmote)
                && currentCheerEmote is not null)
            {
                var tieredEmote = currentCheerEmote.GetTier(bitsAmount).Value;
                var emoteImageInfo = tieredEmote.Info;
                if (drawPos.X + emoteImageInfo.Width > _options.ChatWidth - _options.SidePadding * 2)
                {
                    _layout.AddImageSection(sectionImages, _assets.SectionImageCache, ref drawPos, defaultPos);
                }

                var emotePoint = new Point
                {
                    X = drawPos.X,
                    Y = (int)((sectionImages.Count - 1) * _options.SectionHeight + ((_options.SectionHeight - emoteImageInfo.Height) / 2.0))
                };
                emotePositionList.Add(new EmotePosition(emotePoint, tieredEmote));
                drawPos.X += emoteImageInfo.Width + _options.EmoteSpacing;
                return true;
            }

            return false;
        }

        private void DrawFirstPartyEmote(
            TimelineMessage message,
            List<SectionImage> sectionImages,
            List<EmotePosition> emotePositionList,
            ref Point drawPos,
            Point defaultPos,
            TimelineFragment fragment,
            bool highlightWords,
            SectionRenderContext ctx)
        {
            var emoteLookup = _assets.FirstPartyEmotes.GetAlternateLookup<ReadOnlySpan<char>>();
            if (!string.IsNullOrEmpty(fragment.EmoticonId)
                && emoteLookup.TryGetValue(fragment.EmoticonId, out var emote)
                && emote is not null)
            {
                bool isGigantified = _options.RenderPowerUps
                    && message.PowerUp?.Type == "gigantified-emote-message"
                    && !string.IsNullOrEmpty(message.PowerUp.EmoteId)
                    && string.Equals(fragment.EmoticonId, message.PowerUp.EmoteId, StringComparison.Ordinal);

                if (isGigantified)
                {
                    emote = _assets.GetOrCreateGigantifiedEmote(emote);
                }

                SKImageInfo emoteInfo = emote.Info;
                if (drawPos.X + emoteInfo.Width > _options.ChatWidth - _options.SidePadding * 2)
                {
                    _layout.AddImageSection(sectionImages, _assets.SectionImageCache, ref drawPos, defaultPos);
                }

                Point emotePoint;
                if (isGigantified && emoteInfo.Height > _options.SectionHeight)
                {
                    int emoteX = drawPos.X;
                    int startSectionIdx = sectionImages.Count - 1;
                    int sectionsSpanned = Math.Max(1, (int)Math.Ceiling(emoteInfo.Height / (double)_options.SectionHeight));
                    for (int s = 1; s < sectionsSpanned; s++)
                    {
                        _layout.AddImageSection(sectionImages, _assets.SectionImageCache, ref drawPos, defaultPos);
                    }

                    int spanHeight = sectionsSpanned * _options.SectionHeight;
                    emotePoint = new Point
                    {
                        X = emoteX,
                        Y = (int)(startSectionIdx * _options.SectionHeight + ((spanHeight - emoteInfo.Height) / 2.0))
                    };
                    drawPos.X = emoteX + emoteInfo.Width + _options.EmoteSpacing;
                }
                else
                {
                    emotePoint = new Point
                    {
                        X = drawPos.X,
                        Y = (int)((sectionImages.Count - 1) * _options.SectionHeight + ((_options.SectionHeight - emoteInfo.Height) / 2.0))
                    };

                    if (highlightWords)
                    {
                        var canvas = sectionImages[^1].Canvas;
                        var paint = TextLayoutEngine.GetCachedPaint(AssetCatalog.Purple, ctx.PaintCache);
                        canvas.DrawRect(drawPos.X, 0, emoteInfo.Width + _options.EmoteSpacing, _options.SectionHeight, paint);
                    }

                    drawPos.X += emoteInfo.Width + _options.EmoteSpacing;
                }

                emotePositionList.Add(new EmotePosition(emotePoint, emote));
            }
            else
            {
                _layout.DrawText(fragment.Text, ctx.MessageFont, true, sectionImages, _assets.SectionImageCache, ref drawPos, defaultPos, highlightWords, ctx);
            }
        }

        private void DrawThirdPartyEmote(
            List<SectionImage> sectionImages,
            List<EmotePosition> emotePositionList,
            ref Point drawPos,
            Point defaultPos,
            TwitchEmote twitchEmote,
            bool highlightWords,
            SectionRenderContext ctx)
        {
            SKImageInfo emoteInfo = twitchEmote.Info;
            var emotePoint = new Point();
            if (!twitchEmote.IsZeroWidth)
            {
                if (drawPos.X + emoteInfo.Width > _options.ChatWidth - _options.SidePadding * 2)
                {
                    _layout.AddImageSection(sectionImages, _assets.SectionImageCache, ref drawPos, defaultPos);
                }

                if (highlightWords)
                {
                    var canvas = sectionImages[^1].Canvas;
                    var paint = TextLayoutEngine.GetCachedPaint(AssetCatalog.Purple, ctx.PaintCache);
                    canvas.DrawRect(drawPos.X, 0, emoteInfo.Width + _options.EmoteSpacing, _options.SectionHeight, paint);
                }

                emotePoint.X = drawPos.X;
                drawPos.X += emoteInfo.Width + _options.EmoteSpacing;
            }
            else
            {
                emotePoint.X = drawPos.X - _options.EmoteSpacing - emoteInfo.Width;
            }

            emotePoint.Y = (int)((sectionImages.Count - 1) * _options.SectionHeight + ((_options.SectionHeight - emoteInfo.Height) / 2.0));
            emotePositionList.Add(new EmotePosition(emotePoint, twitchEmote));
        }

        private void DrawEmojiMessage(
            List<SectionImage> sectionImages,
            List<EmotePosition> emotePositionList,
            ref Point drawPos,
            Point defaultPos,
            int bitsCount,
            ReadOnlySpan<char> fragment,
            bool highlightWords,
            SectionRenderContext ctx,
            int firstEmoji = -1)
        {
            var emojiMatches = new List<string>();
            Span<char> stackSpace = stackalloc char[16];

            var fragmentSlice = fragment;
            var nonEmojiStart = 0;
            var nonEmojiLen = 0;
            if (firstEmoji > 0)
            {
                nonEmojiLen = firstEmoji;
                fragmentSlice = fragmentSlice[firstEmoji..];
            }

            while (true)
            {
                var elementLength = StringInfo.GetNextTextElementLength(fragmentSlice);
                if (elementLength == 0)
                {
                    break;
                }

                if (elementLength == 1 && char.IsAscii(fragmentSlice[0]))
                {
                    nonEmojiLen += elementLength;
                    fragmentSlice = fragmentSlice[elementLength..];
                    continue;
                }

                var textElement = fragmentSlice[..elementLength];
                fragmentSlice = fragmentSlice[elementLength..];

                var lookupKey = elementLength <= stackSpace.Length ? stackSpace : new char[elementLength];
                var written = textElement.CopyToExcept(lookupKey, AssetCatalog.EmojiExcludeChars);
                if (!_assets.TryGetEmojiImage(lookupKey[..written], out var emojiImage))
                {
                    emojiImage = SplitZwjEmoji(sectionImages, emotePositionList, ref drawPos, defaultPos, bitsCount, highlightWords, textElement, ctx);
                    emojiImage ??= _assets.LookupEmojiSlow(textElement, emojiMatches);
                    if (emojiImage is null)
                    {
                        nonEmojiLen += elementLength;
                        continue;
                    }
                }

                if (nonEmojiLen > 0)
                {
                    DrawFragmentPart(sectionImages, emotePositionList, ref drawPos, defaultPos, bitsCount, fragment.Slice(nonEmojiStart, nonEmojiLen), highlightWords, ctx, true, true);
                    _layout.FlushPendingLineText(ctx);
                    nonEmojiStart += nonEmojiLen;
                    nonEmojiLen = 0;
                }

                SKImageInfo emojiImageInfo = emojiImage.Info;
                if (drawPos.X + emojiImageInfo.Width > _options.ChatWidth - _options.SidePadding * 2)
                {
                    _layout.AddImageSection(sectionImages, _assets.SectionImageCache, ref drawPos, defaultPos);
                }

                var emotePoint = new Point
                {
                    X = drawPos.X + (int)Math.Ceiling(_options.EmoteSpacing / 2d),
                    Y = (int)((_options.SectionHeight - emojiImageInfo.Height) / 2.0)
                };

                var canvas = sectionImages[^1].Canvas;
                if (highlightWords)
                {
                    var paint = TextLayoutEngine.GetCachedPaint(AssetCatalog.Purple, ctx.PaintCache);
                    canvas.DrawRect((int)(emotePoint.X - _options.EmoteSpacing / 2d), 0, emojiImageInfo.Width + _options.EmoteSpacing, _options.SectionHeight, paint);
                }

                canvas.DrawImage(emojiImage, emotePoint.X, emotePoint.Y);
                nonEmojiStart += elementLength;
                drawPos.X += emojiImageInfo.Width + _options.EmoteSpacing;
            }

            if (nonEmojiLen > 0)
            {
                DrawFragmentPart(sectionImages, emotePositionList, ref drawPos, defaultPos, bitsCount, fragment.Slice(nonEmojiStart, nonEmojiLen), highlightWords, ctx, true, true);
                _layout.FlushPendingLineText(ctx);
            }
        }

        private SKImage? SplitZwjEmoji(
            List<SectionImage> sectionImages,
            List<EmotePosition> emotePositionList,
            ref Point drawPos,
            Point defaultPos,
            int bitsCount,
            bool highlightWords,
            ReadOnlySpan<char> textElement,
            SectionRenderContext ctx)
        {
            if (!textElement.Contains(ZERO_WIDTH_JOINER))
            {
                return null;
            }

            Span<char> stackSpace = stackalloc char[16];
            ReadOnlySpan<char> subEmoji = default;
            foreach (var range in textElement.Split(ZERO_WIDTH_JOINER))
            {
                if (!subEmoji.IsEmpty)
                {
                    DrawEmojiMessage(sectionImages, emotePositionList, ref drawPos, defaultPos, bitsCount, subEmoji, highlightWords, ctx);
                }

                subEmoji = textElement[range];
            }

            var lookupKey = subEmoji.Length <= stackSpace.Length ? stackSpace : new char[subEmoji.Length];
            var written = subEmoji.CopyToExcept(lookupKey, AssetCatalog.EmojiExcludeChars);
            return _assets.TryGetEmojiImage(lookupKey[..written], out var emojiImage) ? emojiImage : null;
        }

        private void DrawNonFontMessage(
            List<SectionImage> sectionImages,
            ref Point drawPos,
            Point defaultPos,
            ReadOnlySpan<char> fragment,
            bool highlightWords,
            SectionRenderContext ctx)
        {
            fragment = fragment.Trim('\uFE0F');

            if (fragment.ContainsAnyInRange('\u2500', '\u28FF') && TextLayoutEngine.BlockArtRegex.IsMatch(fragment))
            {
                var textWidth = (int)(fragment.Length * _layout.BlockArtCharWidth);
                if (_layout.BlockArtPreWrap && drawPos.X + textWidth > _layout.BlockArtPreWrapWidth)
                {
                    _layout.AddImageSection(sectionImages, _assets.SectionImageCache, ref drawPos, defaultPos);
                }
            }

            var inFontBuffer = new StringBuilder();
            var nonFontBuffer = new StringBuilder();
            for (int j = 0; j < fragment.Length; j++)
            {
                if (char.IsHighSurrogate(fragment[j]) && j + 1 < fragment.Length && char.IsLowSurrogate(fragment[j + 1]))
                {
                    if (inFontBuffer.Length > 0)
                    {
                        _layout.DrawText(inFontBuffer.ToString(), ctx.MessageFont, false, sectionImages, _assets.SectionImageCache, ref drawPos, defaultPos, highlightWords, ctx);
                        inFontBuffer.Clear();
                    }
                    if (nonFontBuffer.Length > 0)
                    {
                        var nonFontFallbackFont = _layout.GetFallbackFont(nonFontBuffer[0], ctx.FallbackFontCache);
                        nonFontFallbackFont.Color = _options.MessageColor;
                        _layout.DrawText(nonFontBuffer.ToString(), nonFontFallbackFont, false, sectionImages, _assets.SectionImageCache, ref drawPos, defaultPos, highlightWords, ctx);
                        nonFontBuffer.Clear();
                    }

                    var utf32Char = char.ConvertToUtf32(fragment[j], fragment[j + 1]);
                    if (utf32Char != 0xE0000)
                    {
                        var highSurrogateFallbackFont = _layout.GetFallbackFont(utf32Char, ctx.FallbackFontCache);
                        highSurrogateFallbackFont.Color = _options.MessageColor;
                        _layout.DrawText(fragment.Slice(j, 2), highSurrogateFallbackFont, false, sectionImages, _assets.SectionImageCache, ref drawPos, defaultPos, highlightWords, ctx);
                    }
                    j++;
                }
                else if (!ctx.MessageFont.ContainsGlyphs(fragment.Slice(j, 1)) || fragment.Slice(j, 1).LengthInTextElements() == 0)
                {
                    if (inFontBuffer.Length > 0)
                    {
                        _layout.DrawText(inFontBuffer.ToString(), ctx.MessageFont, false, sectionImages, _assets.SectionImageCache, ref drawPos, defaultPos, highlightWords, ctx);
                        inFontBuffer.Clear();
                    }

                    nonFontBuffer.Append(fragment[j]);
                }
                else
                {
                    if (nonFontBuffer.Length > 0)
                    {
                        var fallbackFont = _layout.GetFallbackFont(nonFontBuffer[0], ctx.FallbackFontCache);
                        fallbackFont.Color = _options.MessageColor;
                        _layout.DrawText(nonFontBuffer.ToString(), fallbackFont, false, sectionImages, _assets.SectionImageCache, ref drawPos, defaultPos, highlightWords, ctx);
                        nonFontBuffer.Clear();
                    }

                    inFontBuffer.Append(fragment[j]);
                }
            }

            if (nonFontBuffer.Length > 0)
            {
                var fallbackFont = _layout.GetFallbackFont(nonFontBuffer[0], ctx.FallbackFontCache);
                fallbackFont.Color = _options.MessageColor;
                _layout.DrawText(nonFontBuffer.ToString(), fallbackFont, true, sectionImages, _assets.SectionImageCache, ref drawPos, defaultPos, highlightWords, ctx);
                nonFontBuffer.Clear();
            }

            if (inFontBuffer.Length > 0)
            {
                _layout.DrawText(inFontBuffer.ToString(), ctx.MessageFont, true, sectionImages, _assets.SectionImageCache, ref drawPos, defaultPos, highlightWords, ctx);
                inFontBuffer.Clear();
            }
        }

        private void DrawUsername(
            TimelineMessage message,
            List<SectionImage> sectionImages,
            ref Point drawPos,
            Point defaultPos,
            SectionRenderContext ctx,
            bool appendColon = true,
            SKColor? colorOverride = null,
            int messageIndex = 0)
        {
            string displayName = message.Author.DisplayName;
            SKColor userColor;
            string userName;
            SKPaint userPaint;

            if (colorOverride.HasValue)
            {
                userColor = colorOverride.Value;
                userName = appendColon ? string.Concat(displayName, ":") : displayName;
                int firstNonAscii = -1;
                foreach (char ch in displayName)
                {
                    if (ch > 127)
                    {
                        firstNonAscii = ch;
                        break;
                    }
                }
                userPaint = firstNonAscii >= 0
                    ? _layout.GetFallbackFont(firstNonAscii, ctx.FallbackFontCache)
                    : ctx.NameFont;
            }
            else
            {
                var backgroundColor = _options.AdjustUsernameVisibility
                    ? _layout.GetMessageBackground(message, messageIndex, ctx, out _)
                    : default;
                var resolved = _layout.ResolveAndCacheUsername(
                    displayName,
                    message.Author.RawColorHex,
                    backgroundColor,
                    appendColon,
                    ctx.OutlinePaint,
                    ctx.UsernameCache);

                userColor = resolved.Color;
                userName = resolved.FormattedLabel;
                userPaint = resolved.FirstNonAsciiCodepoint >= 0
                    ? _layout.GetFallbackFont(resolved.FirstNonAsciiCodepoint, ctx.FallbackFontCache)
                    : ctx.NameFont;
            }

            userPaint.Color = userColor;

            int savedY = drawPos.Y;
            drawPos.Y = _layout.UsernameCenteredY;
            _layout.DrawText(userName, userPaint, true, sectionImages, _assets.SectionImageCache, ref drawPos, defaultPos, false, ctx);
            drawPos.Y = savedY;
        }

        private void DrawAvatar(TimelineAuthor author, List<SectionImage> sectionImages, ref Point drawPos)
        {
            if (!_assets.TryResolveAvatarImage(author.AvatarUrl, author.DisplayName, out var avatarImage))
            {
                return;
            }

            var canvas = sectionImages[^1].Canvas;
            var avatarY = (float)((_options.SectionHeight - avatarImage.Height) / 2.0);
            canvas.DrawImage(avatarImage, drawPos.X, avatarY);
            drawPos.X += avatarImage.Width + _options.WordSpacing;
        }

        private void DrawBadges(TimelineAuthor author, List<SectionImage> sectionImages, ref Point drawPos)
        {
            if (author.Badges.Count == 0)
            {
                return;
            }

            var canvas = sectionImages[^1].Canvas;
            for (int i = 0; i < author.Badges.Count; i++)
            {
                var badge = author.Badges[i];
                if (!_assets.Badges.TryGetValue(badge.Id, out var cachedBadge) || cachedBadge is null)
                    continue;

                if ((_options.ChatBadgeMask & cachedBadge.Type) != 0)
                    continue;

                if (!cachedBadge.Versions.TryGetValue(badge.Version, out var badgeImage) || badgeImage is null)
                    continue;

                float badgeY = (float)((_options.SectionHeight - badgeImage.Height) / 2.0);
                canvas.DrawImage(badgeImage, drawPos.X, badgeY);
                drawPos.X += badgeImage.Width + _options.WordSpacing / 2;
            }
        }

        private void DrawTimestamp(
            double offsetSeconds,
            List<SectionImage> sectionImages,
            ref Point drawPos,
            ref Point defaultPos,
            SectionRenderContext ctx)
        {
            var canvas = sectionImages[^1].Canvas;
            var timestamp = new TimeSpan(0, 0, Math.Max(0, (int)offsetSeconds));

            const int MAX_TIMESTAMP_LENGTH = 8;
            Span<char> timestampStackSpace = stackalloc char[MAX_TIMESTAMP_LENGTH];
            ReadOnlySpan<char> formattedTimestamp = timestamp.Ticks switch
            {
                >= 24 * TimeSpan.TicksPerHour => TimeSpanHFormat.ReusableInstance.Format(@"HH\:mm\:ss", timestamp),
                >= 1 * TimeSpan.TicksPerHour => timestamp.TryFormat(timestampStackSpace, out var charsWritten, @"h\:mm\:ss")
                    ? timestampStackSpace[..charsWritten]
                    : timestamp.ToString(@"h\:mm\:ss"),
                _ => timestamp.TryFormat(timestampStackSpace, out var charsWritten, @"m\:ss")
                    ? timestampStackSpace[..charsWritten]
                    : timestamp.ToString(@"m\:ss")
            };

            if (_options.Outline)
            {
                TextLayoutEngine.DrawStrokedTextSpan(canvas, formattedTimestamp, drawPos.X, drawPos.Y, ctx.MessageFont, ctx.OutlinePaint);
            }

            canvas.DrawText(formattedTimestamp, drawPos.X, drawPos.Y, ctx.MessageFont);

            var widths = _layout.TimestampWidths;
            var textWidth = widths.Length >= 4
                ? timestamp.Ticks switch
                {
                    >= 10 * TimeSpan.TicksPerHour => widths[3],
                    >= 1 * TimeSpan.TicksPerHour => widths[2],
                    >= 10 * TimeSpan.TicksPerMinute => widths[1],
                    _ => widths[0]
                }
                : (int)ctx.MessageFont.MeasureText(formattedTimestamp);

            drawPos.X += textWidth + _options.WordSpacing * 2;
            defaultPos.X = drawPos.X;
        }

        private void DrawSharedChatInlineBadge(string? sourceRoomId, List<SectionImage> sectionImages, ref Point drawPos, SectionRenderContext ctx)
        {
            if (!_options.RenderSharedChatBadge || string.IsNullOrEmpty(sourceRoomId))
            {
                return;
            }

            var canvas = sectionImages[^1].Canvas;
            int badgeSize = Math.Max(10, (int)Math.Round(30 * _options.ReferenceScale * _options.BadgeScale));
            float badgeY = (_options.SectionHeight - badgeSize) / 2f;
            float badgeX = drawPos.X;

            var bgPaint = TextLayoutEngine.GetCachedPaint(AssetCatalog.Purple, ctx.PaintCache);
            float cornerRadius = Math.Max(2f, badgeSize * 0.22f);
            canvas.DrawRoundRect(badgeX, badgeY, badgeSize, badgeSize, cornerRadius, cornerRadius, bgPaint);

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

            drawPos.X += badgeSize + Math.Max(2, _options.WordSpacing / 2);
        }

        private void DrawHypeChatInlinePill(
            TimelineHypeChatInfo? hypeChat,
            List<SectionImage> sectionImages,
            ref Point drawPos,
            Point defaultPos,
            SectionRenderContext ctx)
        {
            if (!_options.RenderHypeChat || hypeChat is null)
            {
                return;
            }

            string label = FormatHypeChatCurrency(hypeChat);
            SKColor pillColor = GetHypeChatLevelColor(hypeChat.Level);

            using var pillTextFont = ctx.NameFont.Clone();
            pillTextFont.TextSize = (float)Math.Max(8, _options.FontSize * 0.85);
            pillTextFont.Color = SKColors.White;

            float textWidth = pillTextFont.MeasureText(label);
            int padX = Math.Max(4, (int)(6 * _options.ReferenceScale));
            int pillWidth = (int)Math.Ceiling(textWidth) + padX * 2;
            int pillHeight = Math.Max(12, (int)(_options.SectionHeight * 0.66));

            if (drawPos.X + pillWidth > _options.ChatWidth - _options.SidePadding * 2)
            {
                _layout.AddImageSection(sectionImages, _assets.SectionImageCache, ref drawPos, defaultPos);
            }

            var canvas = sectionImages[^1].Canvas;
            float pillTop = (_options.SectionHeight - pillHeight) / 2f;
            var bgPaint = TextLayoutEngine.GetCachedPaint(pillColor, ctx.PaintCache);
            float radius = pillHeight * 0.3f;
            canvas.DrawRoundRect(drawPos.X, pillTop, pillWidth, pillHeight, radius, radius, bgPaint);

            SKRect bounds = new();
            pillTextFont.MeasureText("0", ref bounds);
            float textY = pillTop + (pillHeight + bounds.Height) / 2f;
            canvas.DrawText(label, drawPos.X + padX, textY, pillTextFont);

            drawPos.X += pillWidth + _options.WordSpacing;
        }

        private int GetReplyHeaderHeight(TimelineMessage message)
        {
            if (!_options.RenderReplies || message.Reply is null)
            {
                return 0;
            }

            return Math.Max(12, (int)Math.Round(_options.SectionHeight * 0.66));
        }

        private void DrawReplyHeaderOnCombinedCanvas(
            SKCanvas canvas,
            TimelineReplyInfo reply,
            int replyHeaderHeight,
            bool hasLeftAccentBar,
            SectionRenderContext ctx)
        {
            string parentName = !string.IsNullOrEmpty(reply.ParentDisplayName)
                ? reply.ParentDisplayName
                : (reply.ParentUserLogin ?? "User");
            string parentBody = (reply.ParentMsgBody ?? "")
                .Replace(@"\s", " ")
                .Replace('\n', ' ')
                .Replace('\r', ' ')
                .Trim();
            string fullReplyLine = string.IsNullOrEmpty(parentBody)
                ? $"Replying to @{parentName}"
                : $"@{parentName}: {parentBody}";

            using var replyFont = ctx.MessageFont.Clone();
            replyFont.TextSize = (float)Math.Max(8, _options.FontSize * 0.80);
            replyFont.Color = _options.MessageColor.WithAlpha(165);

            int startX = _options.SidePadding + (hasLeftAccentBar ? _options.AccentIndentWidth - _options.AccentStrokeWidth / 2 : 0);
            SKRect bounds = new();
            replyFont.MeasureText("Ag", ref bounds);
            float baselineY = (replyHeaderHeight + bounds.Height) / 2f;

            float iconSize = Math.Max(8f, replyFont.TextSize * 0.85f);
            float iconPad = Math.Max(3f, replyFont.TextSize * 0.25f);
            _layout.DrawVectorReplyIcon(canvas, startX, baselineY - bounds.Height * 0.5f, iconSize, replyFont.Color, ctx);

            float textStartX = startX + iconSize + iconPad;
            int maxWidth = Math.Max(20, _options.ChatWidth - (int)textStartX - _options.SidePadding);

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

            _layout.DrawSingleLineTextDirect(canvas, finalText, textStartX, baselineY, replyFont, ctx, _assets);
        }

        private void ApplyStrikethroughModeration(
            SKCanvas canvas,
            SKImageInfo bitmapInfo,
            List<EmotePosition> emoteSectionList,
            int replyHeaderHeight)
        {
            foreach (var (drawPoint, emote) in emoteSectionList)
            {
                var frames = emote.EmoteFrames;
                if (frames is { Length: > 0 } && frames[0] is not null)
                {
                    canvas.DrawImage(frames[0], drawPoint.X, drawPoint.Y);
                }
            }
            emoteSectionList.Clear();

            using (var dimPaint = new SKPaint
            {
                Color = new SKColor(0, 0, 0, 115),
                BlendMode = SKBlendMode.DstIn
            })
            {
                canvas.DrawRect(0, 0, bitmapInfo.Width, bitmapInfo.Height, dimPaint);
            }

            using var strikePaint = new SKPaint
            {
                Color = _options.MessageColor.WithAlpha(185),
                StrokeWidth = (float)Math.Max(1.5, 2.0 * _options.ReferenceScale),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke
            };

            int rowCount = Math.Max(1, (bitmapInfo.Height - replyHeaderHeight) / Math.Max(1, _options.SectionHeight));
            float startX = _options.SidePadding;
            float endX = Math.Max(startX + 20, bitmapInfo.Width - _options.SidePadding * 2);

            for (int r = 0; r < rowCount; r++)
            {
                float centerY = replyHeaderHeight + r * _options.SectionHeight + _options.SectionHeight * 0.52f;
                canvas.DrawLine(startX, centerY, endX, centerY, strikePaint);
            }
        }

        private static string? GetNoticeParam(TimelineNoticeInfo notice, string key)
        {
            if (notice.NoticeParams is not null && notice.NoticeParams.TryGetValue(key, out var val) && !string.IsNullOrEmpty(val))
            {
                return val;
            }
            return null;
        }

        private static string BuildCompactSubSummary(TimelineNoticeInfo notice, string rawSystemText)
        {
            rawSystemText ??= "";

            string? subPlanCode = notice.SubPlan ?? GetNoticeParam(notice, "msg-param-sub-plan");
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

            int months = notice.CumulativeMonths;
            if (months <= 0)
            {
                string? paramMonths = GetNoticeParam(notice, "msg-param-cumulative-months") ?? GetNoticeParam(notice, "msg-param-months");
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

            int streak = notice.StreakMonths;
            if (streak <= 0)
            {
                string? shareStreak = GetNoticeParam(notice, "msg-param-should-share-streak");
                string? paramStreak = GetNoticeParam(notice, "msg-param-streak-months");
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

        private static string BuildCompactWatchStreakSummary(TimelineNoticeInfo notice, string rawSystemText)
        {
            rawSystemText ??= "";

            int streakCount = notice.MilestoneValue;
            if (streakCount <= 0)
            {
                string? paramVal = GetNoticeParam(notice, "msg-param-value");
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
            TimelineAuthor author,
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

            float curX = iconPoint.X + highlightIcon.Width + Math.Max(4, _options.WordSpacing / 2);
            float maxRightX = _options.ChatWidth - _options.SidePadding;
            float availableWidth = Math.Max(40f, maxRightX - curX);

            using var msgFont = ctx.MessageFont.Clone();
            if (!includeUsername)
            {
                float measured = msgFont.MeasureText(compactSummary);
                if (measured > availableWidth && measured > 1f)
                {
                    msgFont.TextSize = Math.Max(7f, msgFont.TextSize * (availableWidth / measured));
                }

                _layout.DrawSingleLineTextDirect(canvas, compactSummary, curX, drawPos.Y, msgFont, ctx, _assets);
                drawPos.X = (int)Math.Ceiling(curX + msgFont.MeasureText(compactSummary));
            }
            else
            {
                string displayName = author.DisplayName;
                string tailSummary = $" • {compactSummary}";

                using var nameFont = ctx.NameFont.Clone();
                nameFont.Color = AssetCatalog.Purple;

                float totalWidth = nameFont.MeasureText(displayName) + msgFont.MeasureText(tailSummary);
                if (totalWidth > availableWidth && totalWidth > 1f)
                {
                    float scale = availableWidth / totalWidth;
                    msgFont.TextSize = Math.Max(7f, msgFont.TextSize * scale);
                    nameFont.TextSize = Math.Max(7f, nameFont.TextSize * scale);
                }

                _layout.DrawSingleLineTextDirect(canvas, displayName, curX, drawPos.Y, nameFont, ctx, _assets);
                curX += nameFont.MeasureText(displayName);

                _layout.DrawSingleLineTextDirect(canvas, tailSummary, curX, drawPos.Y, msgFont, ctx, _assets);
                curX += msgFont.MeasureText(tailSummary);

                drawPos.X = (int)Math.Ceiling(curX);
            }
        }

        private void DrawCompactGiftMessageLine(
            TimelineAuthor author,
            TimelineNoticeInfo notice,
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

            float curX = iconPoint.X + highlightIcon.Width + Math.Max(4, _options.WordSpacing / 2);
            float maxRightX = _options.ChatWidth - _options.SidePadding;
            float availableWidth = Math.Max(40f, maxRightX - curX);

            bool isAnonymous = author.Id is "274598607"
                || rawSystemText.StartsWith("An anonymous", StringComparison.OrdinalIgnoreCase);
            string gifterName = isAnonymous ? "Anonymous" : author.DisplayName;

            string? subPlanCode = notice.SubPlan ?? GetNoticeParam(notice, "msg-param-sub-plan");
            string tierShort = subPlanCode switch
            {
                "2000" => "Tier 2",
                "3000" => "Tier 3",
                _ => rawSystemText.Contains("Tier 3", StringComparison.OrdinalIgnoreCase) ? "Tier 3"
                    : rawSystemText.Contains("Tier 2", StringComparison.OrdinalIgnoreCase) ? "Tier 2"
                    : "Tier 1"
            };

            int totalGifts = notice.SenderTotalGifts;
            if (totalGifts <= 0)
            {
                string? paramTotal = GetNoticeParam(notice, "msg-param-sender-count");
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

            int massCount = notice.MassGiftCount;
            if (massCount <= 0)
            {
                string? paramMass = GetNoticeParam(notice, "msg-param-mass-gift-count");
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
            nameFont.Color = AssetCatalog.Purple;
            using var msgFont = ctx.MessageFont.Clone();

            if (massCount > 0 || notice.NoticeType == "submysterygift")
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

                _layout.DrawSingleLineTextDirect(canvas, gifterName, curX, drawPos.Y, nameFont, ctx, _assets);
                curX += nameFont.MeasureText(gifterName);
                _layout.DrawSingleLineTextDirect(canvas, summary, curX, drawPos.Y, msgFont, ctx, _assets);
                curX += msgFont.MeasureText(summary);
                drawPos.X = (int)Math.Ceiling(curX);
                return;
            }

            string? recipient = notice.RecipientDisplayName
                ?? GetNoticeParam(notice, "msg-param-recipient-display-name")
                ?? GetNoticeParam(notice, "msg-param-recipient-user-name");

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
                float arrowSlotW = TextLayoutEngine.MeasureVectorRightArrowSlot(msgFont);
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
                    arrowSlotW = TextLayoutEngine.MeasureVectorRightArrowSlot(msgFont);
                }

                _layout.DrawSingleLineTextDirect(canvas, gifterName, curX, drawPos.Y, nameFont, ctx, _assets);
                curX += nameFont.MeasureText(gifterName);

                _layout.DrawVectorRightArrow(canvas, curX, drawPos.Y, msgFont, ctx);
                curX += arrowSlotW;

                _layout.DrawSingleLineTextDirect(canvas, recipient, curX, drawPos.Y, nameFont, ctx, _assets);
                curX += nameFont.MeasureText(recipient);

                _layout.DrawSingleLineTextDirect(canvas, tail, curX, drawPos.Y, msgFont, ctx, _assets);
                curX += msgFont.MeasureText(tail);

                drawPos.X = (int)Math.Ceiling(curX);
                return;
            }

            if (rawSystemText.Contains("continuing the Gift Sub", StringComparison.OrdinalIgnoreCase))
            {
                var cm = GiftContinuingFromRegex.Match(rawSystemText);
                string? fromUser = cm.Success && !rawSystemText.Contains("anonymous user", StringComparison.OrdinalIgnoreCase)
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

                _layout.DrawSingleLineTextDirect(canvas, gifterName, curX, drawPos.Y, nameFont, ctx, _assets);
                curX += nameFont.MeasureText(gifterName);
                _layout.DrawSingleLineTextDirect(canvas, tail, curX, drawPos.Y, msgFont, ctx, _assets);
                curX += msgFont.MeasureText(tail);
                drawPos.X = (int)Math.Ceiling(curX);
                return;
            }

            float rawW = msgFont.MeasureText(rawSystemText);
            if (rawW > availableWidth && rawW > 1f)
            {
                msgFont.TextSize = Math.Max(7f, msgFont.TextSize * (availableWidth / rawW));
            }
            _layout.DrawSingleLineTextDirect(canvas, rawSystemText, curX, drawPos.Y, msgFont, ctx, _assets);
            drawPos.X = (int)Math.Ceiling(curX + msgFont.MeasureText(rawSystemText));
        }

        private static SKColor GetHypeChatLevelColor(string? level)
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

        private static string FormatHypeChatCurrency(TimelineHypeChatInfo hypeChat)
        {
            double val = hypeChat.EffectiveDecimalAmount;
            string curr = (hypeChat.Currency ?? "USD").ToUpperInvariant();
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
    }
}

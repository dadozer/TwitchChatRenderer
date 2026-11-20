#nullable enable
using SkiaSharp;
using TwitchDownloaderCore.Chat;
using TwitchDownloaderCore.Chat.Timeline;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Models.Render;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Services;
using TwitchDownloaderCore.Tools;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.Rendering
{
    /// <summary>
    /// Renders live preview bitmaps for the GUI using the exact production <see cref="TextLayoutEngine"/> and
    /// <see cref="MessageSectionRenderer"/> pipeline over synthesized in-memory assets (zero network I/O).
    /// Eliminates drift between live preview and actual encoded chat output.
    /// </summary>
    public static class ChatPreviewRenderer
    {
        public static SKBitmap RenderPreviewBitmap(ChatRenderOptions sourceOptions, int previewWidth, int previewHeight)
        {
            previewWidth = Math.Max(16, previewWidth);
            previewHeight = Math.Max(16, previewHeight);

            var previewOptions = CloneForPreview(sourceOptions, previewWidth, previewHeight);
            var bitmap = new SKBitmap(previewWidth, previewHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);

            // 1. Draw transparency checkerboard so alpha channel settings are clearly visible
            using (var darkTile = new SKPaint { Color = new SKColor(0xFF1C1C20) })
            using (var lightTile = new SKPaint { Color = new SKColor(0xFF28282E) })
            {
                canvas.Clear(darkTile.Color);
                const int tileSize = 8;
                for (int y = 0; y < previewHeight; y += tileSize)
                {
                    for (int x = ((y / tileSize) & 1) * tileSize; x < previewWidth; x += tileSize * 2)
                    {
                        canvas.DrawRect(x, y, tileSize, tileSize, lightTile);
                    }
                }
            }

            // 2. Blend configured chat background color over checkerboard
            using (var bgPaint = new SKPaint { Color = previewOptions.BackgroundColor, BlendMode = SKBlendMode.SrcOver })
            {
                canvas.DrawRect(0, 0, previewWidth, previewHeight, bgPaint);
            }

            string cacheDir = CacheDirectoryService.GetCacheDirectory(previewOptions.TempFolder);
            using var layout = new TextLayoutEngine(previewOptions);
            using var assets = new AssetCatalog(previewOptions, cacheDir, layout.BaseOutlinePaint);
            SeedPreviewAssets(assets, previewOptions);

            var sectionRenderer = new MessageSectionRenderer(previewOptions, assets, layout);
            var ctx = layout.GetWorkerContext();
            var sampleMessages = BuildPreviewTimelineMessages(previewOptions);

            var renderedSections = new List<CommentSection>(sampleMessages.Count);
            try
            {
                for (int i = 0; i < sampleMessages.Count; i++)
                {
                    var sec = sectionRenderer.RenderSection(sampleMessages, i, ctx);
                    if (sec is not null)
                    {
                        renderedSections.Add(sec);
                    }
                }

                int cursorY = previewHeight;
                int verticalPadding = Math.Max(2, previewOptions.VerticalPadding);
                for (int i = renderedSections.Count - 1; i >= 0; i--)
                {
                    var sec = renderedSections[i];
                    int secHeight = sec.Image.Info.Height;
                    cursorY -= secHeight + verticalPadding;
                    if (cursorY < -secHeight)
                    {
                        break;
                    }

                    var msg = sampleMessages[sec.CommentIndex];
                    var rowBg = layout.GetMessageBackground(msg, sec.CommentIndex, ctx, out _);
                    if (rowBg != previewOptions.BackgroundColor)
                    {
                        using var rowBgPaint = new SKPaint { Color = rowBg, BlendMode = SKBlendMode.SrcOver };
                        canvas.DrawRect(0, cursorY - verticalPadding / 2f, previewWidth, secHeight + verticalPadding, rowBgPaint);
                    }

                    canvas.DrawBitmap(sec.Image.Bitmap, 0, cursorY);

                    foreach (var (drawPoint, emote) in sec.Emotes)
                    {
                        if (emote.FrameCount >= 1)
                        {
                            canvas.DrawImage(emote.EmoteFrames[0], drawPoint.X, drawPoint.Y + cursorY);
                        }
                    }
                }
            }
            finally
            {
                for (int i = 0; i < renderedSections.Count; i++)
                {
                    renderedSections[i].Release(assets.SectionImageCache);
                }
            }

            canvas.Flush();
            return bitmap;
        }

        private static ChatRenderOptions CloneForPreview(ChatRenderOptions src, int width, int height)
        {
            return new ChatRenderOptions
            {
                ChatWidth = width,
                ChatHeight = height,
                BackgroundColor = src.BackgroundColor,
                AlternateBackgroundColor = src.AlternateBackgroundColor,
                AlternateMessageBackgrounds = src.AlternateMessageBackgrounds,
                MessageColor = src.MessageColor,
                Font = string.IsNullOrWhiteSpace(src.Font) ? "Inter Embedded" : src.Font,
                FontSize = Math.Max(6.0, src.FontSize),
                UsernameFontScale = src.UsernameFontScale <= 0 ? 1.0 : src.UsernameFontScale,
                MessageFontStyle = src.MessageFontStyle,
                UsernameFontStyle = src.UsernameFontStyle,
                EmoteScale = src.EmoteScale <= 0 ? 1.0 : src.EmoteScale,
                BadgeScale = src.BadgeScale <= 0 ? 1.0 : src.BadgeScale,
                EmojiScale = src.EmojiScale <= 0 ? 1.0 : src.EmojiScale,
                AvatarScale = src.AvatarScale <= 0 ? 1.0 : src.AvatarScale,
                VerticalSpacingScale = src.VerticalSpacingScale <= 0 ? 1.0 : src.VerticalSpacingScale,
                SidePaddingScale = src.SidePaddingScale <= 0 ? 1.0 : src.SidePaddingScale,
                SectionHeightScale = src.SectionHeightScale <= 0 ? 1.0 : src.SectionHeightScale,
                WordSpacingScale = src.WordSpacingScale <= 0 ? 1.0 : src.WordSpacingScale,
                EmoteSpacingScale = src.EmoteSpacingScale <= 0 ? 1.0 : src.EmoteSpacingScale,
                AccentStrokeScale = src.AccentStrokeScale <= 0 ? 1.0 : src.AccentStrokeScale,
                AccentIndentScale = src.AccentIndentScale <= 0 ? 1.0 : src.AccentIndentScale,
                Outline = src.Outline,
                OutlineSize = Math.Max(0.5, src.OutlineSize),
                Timestamp = src.Timestamp,
                ChatBadges = src.ChatBadges,
                ChatBadgeMask = src.ChatBadgeMask,
                RenderUserAvatars = src.RenderUserAvatars,
                SubMessages = src.SubMessages,
                AdjustUsernameVisibility = src.AdjustUsernameVisibility,
                HighlightUsersArray = src.HighlightUsersArray ?? [],
                HighlightUserColor = src.HighlightUserColor,
                HighlightFirstTimeChatter = src.HighlightFirstTimeChatter,
                RenderReplies = src.RenderReplies,
                RenderHypeChat = src.RenderHypeChat,
                RenderPowerUps = src.RenderPowerUps,
                EmojiVendor = EmojiVendor.None,
                Offline = true,
                TempFolder = src.TempFolder
            };
        }

        private static void SeedPreviewAssets(AssetCatalog assets, ChatRenderOptions options)
        {
            double refScale = Math.Max(0.25, options.ReferenceScale);

            if (options.ChatBadges)
            {
                int badgeSize = Math.Max(8, (int)Math.Round(36 * refScale * options.BadgeScale));
                assets.Badges["broadcaster"] = CreateSyntheticBadge("broadcaster", "1", badgeSize, AssetCatalog.Purple);
                assets.Badges["moderator"] = CreateSyntheticBadge("moderator", "1", badgeSize, new SKColor(0xFF00AD03));
            }

            if (options.RenderUserAvatars)
            {
                int avatarDiameter = Math.Max(8, (int)Math.Round(36 * refScale * options.AvatarScale));
                assets.Avatars["preview://streamfan"] = CreateSyntheticAvatar(avatarDiameter, new SKColor(0xFF1E90FF));
                assets.Avatars["preview://nightbot"] = CreateSyntheticAvatar(avatarDiameter, new SKColor(0xFF00FF7F));
                assets.Avatars["preview://vip"] = CreateSyntheticAvatar(avatarDiameter, new SKColor(0xFFFF69B4));
            }

            int emoteSize = Math.Max(10, (int)Math.Round(28 * refScale * options.EmoteScale));
            assets.FirstPartyEmotes["preview_pog"] = CreateSyntheticEmote("preview_pog", "PogChamp", emoteSize);
        }

        private static ChatBadge CreateSyntheticBadge(string name, string version, int size, SKColor color)
        {
            using var bmp = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(SKColors.Transparent);
                using var bgPaint = new SKPaint { Color = color, IsAntialias = true };
                float r = Math.Max(2f, size * 0.22f);
                canvas.DrawRoundRect(0, 0, size, size, r, r, bgPaint);

                using var iconPaint = new SKPaint
                {
                    Color = SKColors.White,
                    Style = SKPaintStyle.Fill,
                    IsAntialias = true
                };
                canvas.DrawCircle(size * 0.5f, size * 0.5f, size * 0.24f, iconPaint);
                canvas.Flush();
            }

            using var encoded = bmp.Encode(SKEncodedImageFormat.Png, 100);
            var dict = new Dictionary<string, ChatBadgeData>(StringComparer.Ordinal)
            {
                [version] = new ChatBadgeData
                {
                    bytes = encoded.ToArray(),
                    title = name
                }
            };
            return new ChatBadge(name, dict);
        }

        private static SKImage CreateSyntheticAvatar(int diameter, SKColor color)
        {
            using var bmp = new SKBitmap(diameter, diameter, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(SKColors.Transparent);
                float radius = diameter / 2f;
                using var fill = new SKPaint { Color = color, IsAntialias = true };
                canvas.DrawCircle(radius, radius, radius, fill);

                using var headPaint = new SKPaint { Color = SKColors.White.WithAlpha(220), IsAntialias = true };
                canvas.DrawCircle(radius, diameter * 0.38f, diameter * 0.18f, headPaint);
                canvas.DrawOval(radius, diameter * 0.78f, diameter * 0.30f, diameter * 0.20f, headPaint);

                using var maskPath = new SKPath();
                maskPath.AddCircle(radius, radius, radius);
                canvas.ClipPath(maskPath, SKClipOperation.Difference, true);
                canvas.Clear();
                canvas.Flush();
            }
            bmp.SetImmutable();
            return SKImage.FromBitmap(bmp);
        }

        private static TwitchEmote CreateSyntheticEmote(string id, string name, int size)
        {
            using var bmp = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(SKColors.Transparent);
                float c = size * 0.5f;
                using var fill = new SKPaint { Color = new SKColor(0xFFFFB800), IsAntialias = true };
                canvas.DrawCircle(c, c, c * 0.92f, fill);

                using var featurePaint = new SKPaint { Color = new SKColor(0xFF2B1B00), IsAntialias = true };
                canvas.DrawCircle(size * 0.36f, size * 0.40f, Math.Max(1.2f, size * 0.08f), featurePaint);
                canvas.DrawCircle(size * 0.64f, size * 0.40f, Math.Max(1.2f, size * 0.08f), featurePaint);
                canvas.DrawOval(c, size * 0.66f, Math.Max(2f, size * 0.15f), Math.Max(2f, size * 0.18f), featurePaint);
                canvas.Flush();
            }

            using var encoded = bmp.Encode(SKEncodedImageFormat.Png, 100);
            return new TwitchEmote(encoded.ToArray(), null, EmoteProvider.FirstParty, 1, id, name);
        }

        private static List<TimelineMessage> BuildPreviewTimelineMessages(ChatRenderOptions options)
        {
            var baseTime = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            string highlightedLogin = options.HighlightUsersArray is { Length: > 0 } ? options.HighlightUsersArray[0] : "nightbot";
            string highlightedDisplay = options.HighlightUsersArray is { Length: > 0 } ? options.HighlightUsersArray[0] : "Nightbot";

            var row0 = new TimelineMessage(
                Id: "preview-1",
                CreatedAt: baseTime.AddSeconds(84),
                AppearSeconds: 84,
                ModeratedAtSeconds: null,
                IsSynthetic: false,
                Author: new TimelineAuthor(
                    Id: "1001",
                    Login: "streamfan",
                    DisplayName: "StreamFan",
                    RawColorHex: "#1E90FF",
                    AvatarUrl: "preview://streamfan",
                    Badges: [new TimelineBadge("broadcaster", "1")]),
                Content: new TimelineMessageContent(
                    Body: "PogChamp nice render!",
                    Fragments:
                    [
                        new TimelineFragment("PogChamp", "preview_pog"),
                        new TimelineFragment(" nice render!", null)
                    ],
                    EmoteCount: 1,
                    BitsSpent: 0),
                Notice: TimelineNoticeInfo.Regular);

            var row1 = new TimelineMessage(
                Id: "preview-2",
                CreatedAt: baseTime.AddSeconds(85),
                AppearSeconds: 85,
                ModeratedAtSeconds: null,
                IsSynthetic: false,
                Author: new TimelineAuthor(
                    Id: "1002",
                    Login: highlightedLogin,
                    DisplayName: highlightedDisplay,
                    RawColorHex: "#00FF7F",
                    AvatarUrl: "preview://nightbot",
                    Badges: [new TimelineBadge("moderator", "1")]),
                Content: new TimelineMessageContent(
                    Body: "Welcome to the chat!",
                    Fragments: [new TimelineFragment("Welcome to the chat!", null)],
                    EmoteCount: 0,
                    BitsSpent: 0),
                Notice: TimelineNoticeInfo.Regular);

            TimelineMessage row2;
            if (options.SubMessages)
            {
                row2 = new TimelineMessage(
                    Id: "preview-3",
                    CreatedAt: baseTime.AddSeconds(86),
                    AppearSeconds: 86,
                    ModeratedAtSeconds: null,
                    IsSynthetic: false,
                    Author: new TimelineAuthor(
                        Id: "1003",
                        Login: "vip_viewer",
                        DisplayName: "VIP_Viewer",
                        RawColorHex: "#FF69B4",
                        AvatarUrl: "preview://vip",
                        Badges: []),
                    Content: new TimelineMessageContent(
                        Body: "VIP_Viewer subscribed at Tier 1.",
                        Fragments: [new TimelineFragment("VIP_Viewer subscribed at Tier 1.", null)],
                        EmoteCount: 0,
                        BitsSpent: 0),
                    Notice: new TimelineNoticeInfo(
                        Kind: TimelineNoticeKind.ExtendedUserNotice,
                        IconType: HighlightType.SubscribedTier,
                        SystemText: "VIP_Viewer subscribed at Tier 1.",
                        NoticeType: "sub",
                        SubPlan: "1000",
                        CumulativeMonths: 1));
            }
            else
            {
                row2 = new TimelineMessage(
                    Id: "preview-3",
                    CreatedAt: baseTime.AddSeconds(86),
                    AppearSeconds: 86,
                    ModeratedAtSeconds: null,
                    IsSynthetic: false,
                    Author: new TimelineAuthor(
                        Id: "1003",
                        Login: "vip_viewer",
                        DisplayName: "VIP_Viewer",
                        RawColorHex: "#FF69B4",
                        AvatarUrl: "preview://vip",
                        Badges: []),
                    Content: new TimelineMessageContent(
                        Body: "Let's go!!",
                        Fragments: [new TimelineFragment("Let's go!!", null)],
                        EmoteCount: 0,
                        BitsSpent: 0),
                    Notice: TimelineNoticeInfo.Regular);
            }

            return [row0, row1, row2];
        }
    }
}

using SkiaSharp;
using TwitchDownloaderCLI.Modes.Arguments;
using TwitchDownloaderCLI.Tools;
using TwitchDownloaderCore;
using TwitchDownloaderCore.Chat;
using TwitchDownloaderCore.Interfaces;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCLI.Modes
{
    internal static class RenderChat
    {
        internal static void Render(ChatRenderArgs inputOptions)
        {
            using var progress = new CliTaskProgress(inputOptions.LogLevel);

            FfmpegHandler.DetectFfmpeg(inputOptions.FfmpegPath, progress);

            var collisionHandler = new FileCollisionHandler(inputOptions, progress);
            var renderOptions = GetRenderOptions(inputOptions, collisionHandler, progress);

            using var cts = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, e) =>
            {
                e.Cancel = true;
                try { cts.Cancel(); } catch { }
            };
            Console.CancelKeyPress += cancelHandler;

            try
            {
                using var chatRenderer = new ChatRenderer(renderOptions, progress);
                chatRenderer.ParseJsonAsync(cts.Token).GetAwaiter().GetResult();
                chatRenderer.RenderVideoAsync(cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                progress.LogInfo("Render canceled by user.");
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        }

        private static ChatRenderOptions GetRenderOptions(ChatRenderArgs inputOptions, FileCollisionHandler collisionHandler, ITaskLogger logger)
        {
            ChatRenderOptions renderOptions = new()
            {
                InputFile = inputOptions.InputFile,
                OutputFile = inputOptions.OutputFile,
                BackgroundColor = SKColor.Parse(inputOptions.BackgroundColor),
                AlternateBackgroundColor = SKColor.Parse(inputOptions.AlternateBackgroundColor),
                HighlightUserColor = SKColor.Parse(inputOptions.HighlightUserColor),
                MessageColor = SKColor.Parse(inputOptions.MessageColor),
                ChatHeight = inputOptions.ChatHeight,
                ChatWidth = inputOptions.ChatWidth,
                StartOverride = (int)((TimeSpan)inputOptions.TrimBeginningTime).TotalSeconds,
                EndOverride = (int)((TimeSpan)inputOptions.TrimEndingTime).TotalSeconds,
                BttvEmotes = (bool)inputOptions.BttvEmotes!,
                FfzEmotes = (bool)inputOptions.FfzEmotes!,
                StvEmotes = (bool)inputOptions.StvEmotes!,
                GiphyGifs = (bool)inputOptions.GiphyGifs!,
                GifMaxHeightPercent = inputOptions.GifMaxHeightPercent,
                ImageCacheMb = inputOptions.ImageCacheMb,
                AllowUnlistedEmotes = (bool)inputOptions.AllowUnlistedEmotes!,
                Outline = inputOptions.Outline,
                OutlineSize = inputOptions.OutlineSize,
                Font = inputOptions.Font,
                FontSize = inputOptions.FontSize,
                ChatBadgeMask = (ChatBadgeType)inputOptions.BadgeFilterMask,
                MessageFontStyle = ChatRenderOptions.ParseFontStyle(inputOptions.MessageFontStyle, "message font style", throwOnInvalid: true),
                UsernameFontStyle = ChatRenderOptions.ParseFontStyle(inputOptions.UsernameFontStyle, "username font style", throwOnInvalid: true),
                UpdateRate = inputOptions.UpdateRate,
                Framerate = inputOptions.Framerate,
                GenerateMask = inputOptions.GenerateMask,
                Sharpening = inputOptions.Sharpening,
                InputArgs = inputOptions.InputArgs,
                OutputArgs = inputOptions.OutputArgs,
                FfmpegPath = string.IsNullOrWhiteSpace(inputOptions.FfmpegPath) ? FfmpegHandler.FfmpegExecutableName : Path.GetFullPath(inputOptions.FfmpegPath),
                TempFolder = inputOptions.TempFolder,
                SubMessages = (bool)inputOptions.SubMessages!,
                ChatBadges = (bool)inputOptions.ChatBadges!,
                RenderUserAvatars = inputOptions.RenderUserAvatars,
                Timestamp = inputOptions.Timestamp,
                Offline = inputOptions.Offline,
                EmojiVendor = ChatRenderOptions.ParseEmojiVendor(inputOptions.EmojiVendor),
                SkipDriveWaiting = inputOptions.SkipDriveWaiting,
                EmoteScale = inputOptions.ScaleEmote,
                BadgeScale = inputOptions.ScaleBadge,
                EmojiScale = inputOptions.ScaleEmoji,
                AvatarScale = inputOptions.ScaleAvatar,
                UsernameFontScale = inputOptions.ScaleUsername,
                VerticalSpacingScale = inputOptions.ScaleVertical,
                SidePaddingScale = inputOptions.ScaleLeft,
                SectionHeightScale = inputOptions.ScaleSectionHeight,
                WordSpacingScale = inputOptions.ScaleWordSpace,
                EmoteSpacingScale = inputOptions.ScaleEmoteSpace,
                AccentIndentScale = inputOptions.ScaleAccentIndent,
                AccentStrokeScale = inputOptions.ScaleAccentStroke,
                DisperseCommentOffsets = inputOptions.DisperseCommentOffsets,
                AlternateMessageBackgrounds = inputOptions.AlternateMessageBackgrounds,
                AdjustUsernameVisibility = (bool)inputOptions.AdjustUsernameVisibility!,
                ParallelJobs = inputOptions.ParallelJobs,
                PredictionMode = ChatRenderOptions.ParseOverlayWidgetMode(inputOptions.PredictionMode, "--prediction-mode"),
                PollMode = ChatRenderOptions.ParseOverlayWidgetMode(inputOptions.PollMode, "--poll-mode"),
                PinnedChatMode = ChatRenderOptions.ParseOverlayWidgetMode(inputOptions.PinnedChatMode, "--pinned-chat-mode"),
                HypeTrainMode = ChatRenderOptions.ParseOverlayWidgetMode(inputOptions.HypeTrainMode, "--hype-train-mode"),
                ModerationMode = ChatRenderOptions.ParseModerationMode(inputOptions.ModerationMode),
                ShowModerationNotices = (bool)inputOptions.ShowModerationNotices!,
                EnableClearAll = inputOptions.EnableClearAll,
                ShowRoomstateNotices = (bool)inputOptions.ShowRoomstateNotices!,
                RenderReplies = (bool)inputOptions.RenderReplies!,
                HighlightFirstTimeChatter = (bool)inputOptions.HighlightFirstTimeChatter!,
                RenderAnnouncements = (bool)inputOptions.RenderAnnouncements!,
                RenderRaidNotices = (bool)inputOptions.RenderRaidNotices!,
                RenderChannelPointRedemptions = (bool)inputOptions.RenderChannelPointRedemptions!,
                RenderHypeChat = (bool)inputOptions.RenderHypeChat!,
                RenderPowerUps = (bool)inputOptions.RenderPowerUps!,
                RenderSharedChatBadge = (bool)inputOptions.RenderSharedChatBadge!,
                IgnoreUsersArray = ChatRenderOptions.ParseCsvList(inputOptions.IgnoreUsersString),
                BannedWordsArray = ChatRenderOptions.ParseCsvList(inputOptions.BannedWordsString),
                HighlightUsersArray = ChatRenderOptions.ParseCsvList(inputOptions.HighlightUsersString),
                FileCollisionCallback = collisionHandler.HandleCollisionCallback,
            };

            if (renderOptions.GenerateMask && renderOptions.BackgroundColor.Alpha == 255 && !(renderOptions.AlternateMessageBackgrounds! && renderOptions.AlternateBackgroundColor.Alpha != 255))
            {
                logger.LogWarning("Generate mask option has been selected with an opaque background. You most likely want to set a transparent background with --background-color \"#00000000\"");
            }

            if (renderOptions.EnsureEvenDimensions())
            {
                logger.LogWarning("Width and Height MUST be even, rounding up to the nearest even number to prevent errors");
            }

            return renderOptions;
        }
    }
}

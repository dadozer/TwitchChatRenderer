using CommandLine;
using TwitchDownloaderCLI.Models;

namespace TwitchDownloaderCLI.Modes.Arguments
{
    [Verb("chatrender", HelpText = "Renders a chat JSON as a video")]
    internal sealed class ChatRenderArgs : IFileCollisionArgs, ITwitchDownloaderArgs
    {
        [Option('i', "input", Required = true, HelpText = "Path to JSON chat file input.")]
        public string InputFile { get; set; }

        [Option('o', "output", Required = true, HelpText = "File the program will output to.")]
        public string OutputFile { get; set; }

        [Option("background-color", Default = "#111111", HelpText = "The render background color in the string format of '#RRGGBB' or '#AARRGGBB' in hexadecimal.")]
        public string BackgroundColor { get; set; }

        [Option("alt-background-color", Default = "#191919", HelpText = "The alternate message background color in the string format of '#RRGGBB' or '#AARRGGBB' in hexadecimal. Requires --alternate-backgrounds")]
        public string AlternateBackgroundColor { get; set; }

        [Option("highlight-user-color", Default = "#C8FF0059", HelpText = "The highlighted user message background color in the string format of '#RRGGBB' or '#AARRGGBB' in hexadecimal. Requires --highlight-users")]
        public string HighlightUserColor { get; set; }

        [Option("message-color", Default = "#ffffff", HelpText = "The message text color in the string format of '#RRGGBB' or '#AARRGGBB' in hexadecimal.")]
        public string MessageColor { get; set; }

        [Option('w', "chat-width", Default = 350, HelpText = "Width of chat render.")]
        public int ChatWidth { get; set; }

        [Option('h', "chat-height", Default = 600, HelpText = "Height of chat render.")]
        public int ChatHeight { get; set; }

        [Option('b', "beginning", HelpText = "Time to trim the beginning of the render. Can be milliseconds (#ms), seconds (#s), minutes (#m), hours (#h), or time (##:##:##).")]
        public TimeDuration TrimBeginningTime { get; set; } = TimeDuration.MinusOneSeconds;

        [Option('e', "ending", HelpText = "Time to trim the ending of the render. Can be milliseconds (#ms), seconds (#s), minutes (#m), hours (#h), or time (##:##:##).")]
        public TimeDuration TrimEndingTime { get; set; } = TimeDuration.MinusOneSeconds;

        [Option("bttv", Default = true, HelpText = "Enable BTTV emotes.")]
        public bool? BttvEmotes { get; set; }

        [Option("ffz", Default = true, HelpText = "Enable FFZ emotes.")]
        public bool? FfzEmotes { get; set; }

        [Option("stv", Default = true, HelpText = "Enable 7TV emotes.")]
        public bool? StvEmotes { get; set; }

        [Option("giphy", Default = true, HelpText = "Enable Giphy GIFs posted in chat, fetched from Giphy when not embedded in the input json. Unresolved GIFs stay as their bracketed alt text.")]
        public bool? GiphyGifs { get; set; }

        [Option("gif-max-height-percent", Default = 33, HelpText = "Cap on the height of a GIF posted in chat, as a percentage of the chat height. 100 lets one GIF fill the chat.")]
        public int GifMaxHeightPercent { get; set; }

        [Option("image-cache-mb", Default = 256, HelpText = "Megabytes of off-screen emote and GIF imagery to retain decoded. On-screen images are always kept. Lower it on constrained machines, raise it to re-decode less often.")]
        public int ImageCacheMb { get; set; }

        [Option("allow-unlisted-emotes", Default = true, HelpText = "Allow unlisted 7TV emotes in the render.")]
        public bool? AllowUnlistedEmotes { get; set; }

        [Option("sub-messages", Default = true, HelpText = "Enable sub/re-sub messages.")]
        public bool? SubMessages { get; set; }

        [Option("badges", Default = true, HelpText = "Enable chat badges.")]
        public bool? ChatBadges { get; set; }

        [Option("outline", Default = false, HelpText = "Enable outline around chat messages.")]
        public bool Outline { get; set; }

        [Option("outline-size", Default = 4, HelpText = "Size of outline if outline is enabled.")]
        public double OutlineSize { get; set; }

        [Option('f', "font", Default = "Inter Embedded", HelpText = "Font to use.")]
        public string Font { get; set; }

        [Option("font-size", Default = 12, HelpText = "Font size.")]
        public double FontSize { get; set; }

        [Option("message-fontstyle", Default = "normal", HelpText = "Font style of messages. Valid values are normal, bold, italic, and bolditalic.")]
        public string MessageFontStyle { get; set; }

        [Option("username-fontstyle", Default = "bold", HelpText = "Font style of usernames. Valid values are normal, bold, italic, and bolditalic.")]
        public string UsernameFontStyle { get; set; }

        [Option("timestamp", Default = false, HelpText = "Enable timestamps to the left of messages, similar to VOD chat on Twitch.")]
        public bool Timestamp { get; set; }

        [Option("generate-mask", Default = false, HelpText = "Generates a mask file of the chat in addition to the rendered chat.")]
        public bool GenerateMask { get; set; }

        [Option("sharpening", Default = false, HelpText = "Appends a smartblur sharpening filter to the input-args. Works best with font-size 24 or larger.")]
        public bool Sharpening { get; set; }

        [Option("framerate", Default = 30, HelpText = "Framerate of the render.")]
        public int Framerate { get; set; }

        [Option("update-rate", Default = 0.2, HelpText = "Time in seconds to update chat render output.")]
        public double UpdateRate { get; set; }

        [Option("input-args", Default = "-framerate {fps} -f rawvideo -analyzeduration {max_int} -probesize {max_int} -pix_fmt {pix_fmt} -video_size {width}x{height} -i -", HelpText = "Input arguments for FFmpeg chat render.")]
        public string InputArgs { get; set; }

        [Option("output-args", Default = "-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p \"{save_path}\"", HelpText = "Output arguments for FFmpeg chat render.")]
        public string OutputArgs { get; set; }

        [Option("ignore-users", Default = "", HelpText = "List of usernames to ignore when rendering, separated by commas. Not case-sensitive.")]
        public string IgnoreUsersString { get; set; }

        [Option("ban-words", Default = "", HelpText = "List of words or phrases to ignore when rendering, separated by commas. Not case-sensitive.")]
        public string BannedWordsString { get; set; }

        [Option("highlight-users", Default = "", HelpText = "List of users whose message backgrounds will be drawn in the chosen highlight color, separated by commas. Not case-sensitive.")]
        public string HighlightUsersString { get; set; }

        [Option("badge-filter", Default = 0, HelpText = "Bitmask of types of Chat Badges to filter out. Add the numbers of the types of badges you want to filter. For example, 6 = no broadcaster or moderator badges.\r\nKey: Other = 1, Broadcaster = 2, Moderator = 4, VIP = 8, Subscriber = 16, Predictions = 32, NoAudio/NoVideo = 64, PrimeGaming = 128")]
        public int BadgeFilterMask { get; set; }

        [Option("dispersion", Default = false, HelpText = "In November 2022 a Twitch API change made chat messages download only in whole seconds. This option uses additional metadata to attempt to restore messages to when they were actually sent. This may result in a different comment order. Requires an update rate less than 1.0 for effective results.")]
        public bool DisperseCommentOffsets { get; set; }

        [Option("alternate-backgrounds", Default = false, HelpText = "Alternates the background color of every other chat message to help tell them apart.")]
        public bool AlternateMessageBackgrounds { get; set; }

        [Option("readable-colors", Default = true, HelpText = "Increases the contrast of usernames against the background or outline color.")]
        public bool? AdjustUsernameVisibility { get; set; }

        [Option("avatars", Default = false, HelpText = "Renders the avatars of users next to their username and badges.")]
        public bool RenderUserAvatars { get; set; }

        [Option("offline", Default = false, HelpText = "Render completely offline using only embedded emotes, badges, bits, and Giphy GIFs from the input json.")]
        public bool Offline { get; set; }

        [Option("emoji-vendor", Default = "notocolor", HelpText = "The emoji vendor used for rendering emojis. Valid values are: 'twitter' / 'twemoji', 'google' / 'notocolor', and 'system' / 'none'.")]
        public string EmojiVendor { get; set; }

        [Option("ffmpeg-path", HelpText = "Path to FFmpeg executable.")]
        public string FfmpegPath { get; set; }

        [Option("temp-path", Default = "", HelpText = "Path to temporary folder to use for cache.")]
        public string TempFolder { get; set; }

        [Option("skip-drive-waiting", Default = false, HelpText = "Do not wait for the output drive to transmit a ready signal before writing the next frame. Waiting is usually only necessary on low-end USB drives.")]
        public bool SkipDriveWaiting { get; set; }

        [Option("scale-emote", Default = 1.0, HelpText = "Number to scale emote images.")]
        public double ScaleEmote { get; set; }

        [Option("scale-badge", Default = 1.0, HelpText = "Number to scale badge images.")]
        public double ScaleBadge { get; set; }

        [Option("scale-emoji", Default = 1.0, HelpText = "Number to scale emoji images.")]
        public double ScaleEmoji { get; set; }

        [Option("scale-avatar", Default = 1.0, HelpText = "Number to scale avatar images.")]
        public double ScaleAvatar { get; set; }

        [Option("scale-username", Default = 1.0, HelpText = "Number to scale username font relative to the message font size.")]
        public double ScaleUsername { get; set; }

        [Option("scale-vertical", Default = 1.0, HelpText = "Number to scale vertical padding.")]
        public double ScaleVertical { get; set; }

        [Option("scale-side-padding", Default = 1.0, HelpText = "Number to scale side padding.")]
        public double ScaleLeft { get; set; }

        [Option("scale-section-height", Default = 1.0, HelpText = "Number to scale section height of comments.")]
        public double ScaleSectionHeight { get; set; }

        [Option("scale-word-space", Default = 1.0, HelpText = "Number to scale spacing between words.")]
        public double ScaleWordSpace { get; set; }

        [Option("scale-emote-space", Default = 1.0, HelpText = "Number to scale spacing between emotes.")]
        public double ScaleEmoteSpace { get; set; }

        [Option("scale-highlight-stroke", Default = 1.0, HelpText = "Number to scale highlight stroke size (sub messages).")]
        public double ScaleAccentStroke { get; set; }

        [Option("scale-highlight-indent", Default = 1.0, HelpText = "Number to scale highlight indent size (sub messages).")]
        public double ScaleAccentIndent { get; set; }

        [Option('j', "jobs", Default = 0, HelpText = "Number of parallel FFmpeg encoding segments to run simultaneously. 0 = automatic scaling based on CPU core count (typically 2-3 workers), 1 = single-process rendering.")]
        public int ParallelJobs { get; set; }

        [Option("prediction-mode", Default = "sticky", HelpText = "How to render Channel Point Predictions from v2.0-extended logs. Valid values are 'sticky' (top overlay), 'scroll' (chat messages), and 'off'.")]
        public string PredictionMode { get; set; }

        [Option("poll-mode", Default = "sticky", HelpText = "How to render Polls from v2.0-extended logs. Valid values are 'sticky' (top overlay), 'scroll' (chat messages), and 'off'.")]
        public string PollMode { get; set; }

        [Option("pinned-chat-mode", Default = "sticky", HelpText = "How to render Pinned Chat messages from v2.0-extended logs. Valid values are 'sticky' (top overlay), 'scroll' (chat messages), and 'off'.")]
        public string PinnedChatMode { get; set; }

        [Option("hype-train-mode", Default = "scroll", HelpText = "How to render Hype Trains from v2.0-extended logs. Valid values are 'sticky' (top overlay), 'scroll' (chat messages), and 'off'.")]
        public string HypeTrainMode { get; set; }

        [Option("moderation-mode", Default = "strikethrough", HelpText = "How to handle moderated messages (timeouts, bans, deleted messages) in v2.0-extended logs. Valid values are 'strikethrough' (dim and strikethrough at moderation time), 'deleted' (replace with <message deleted>), 'hide' (never render), and 'off'.")]
        public string ModerationMode { get; set; }

        [Option("moderation-notices", Default = true, HelpText = "Show compact system notices in chat when a user is timed out, banned, or has a message deleted.")]
        public bool? ShowModerationNotices { get; set; }

        [Option("clear-all", Default = false, HelpText = "Enable applying moderation mode to all prior messages when a moderator clears the entire chat room.")]
        public bool EnableClearAll { get; set; }

        [Option("roomstate-notices", Default = true, HelpText = "Show compact system notices in chat when room modes change (emote-only, sub-only, slow, followers-only, R9K).")]
        public bool? ShowRoomstateNotices { get; set; }

        [Option("replies", Default = true, HelpText = "Render compact threaded reply headers above reply messages.")]
        public bool? RenderReplies { get; set; }

        [Option("first-chatter", Default = true, HelpText = "Highlight First-Time Chatter and Returning Chatter messages.")]
        public bool? HighlightFirstTimeChatter { get; set; }

        [Option("announcements", Default = true, HelpText = "Render moderator /announce messages with their accent color bar.")]
        public bool? RenderAnnouncements { get; set; }

        [Option("raids", Default = true, HelpText = "Render incoming and outgoing raid notices.")]
        public bool? RenderRaidNotices { get; set; }

        [Option("channel-points", Default = true, HelpText = "Render custom Channel Point reward redemptions in chat.")]
        public bool? RenderChannelPointRedemptions { get; set; }

        [Option("hype-chat", Default = true, HelpText = "Render Paid Hype Chat currency badges and row highlights.")]
        public bool? RenderHypeChat { get; set; }

        [Option("power-ups", Default = true, HelpText = "Render Bits Power-Up effects such as Gigantified Emotes.")]
        public bool? RenderPowerUps { get; set; }

        [Option("shared-chat-badge", Default = true, HelpText = "Render a small inline Shared Chat badge on messages originating from another channel in a Shared Chat session.")]
        public bool? RenderSharedChatBadge { get; set; }

        // Interface args
        public OverwriteBehavior OverwriteBehavior { get; set; }
        public bool? ShowBanner { get; set; }
        public LogLevel LogLevel { get; set; }
    }
}
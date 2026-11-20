#nullable enable
using SkiaSharp;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using TwitchDownloaderCore.Chat;
using TwitchDownloaderCore.Extensions;
using TwitchDownloaderCore.Interfaces;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Models.Render;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Tools;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.Rendering
{
    /// <summary>
    /// Owns all decoded/scaled visual assets, LRU memory budgets, pooled section surfaces, and planar YUV emote caches
    /// for a render session. Isolates network/cache asset acquisition from timeline compilation and frame compositing.
    /// </summary>
    internal sealed class AssetCatalog : IDisposable
    {
        private const char ZERO_WIDTH_JOINER = '\u200D';

        public static readonly SKColor Purple = new(0xFF7B2CF2);

        internal static readonly string[] DefaultAvatarUrls =
        [
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/75305d54-c7cc-40d1-bb9c-91fbe85943c7-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/ebe4cd89-b4f4-4cd9-adac-2f30151b4209-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/215b7342-def9-11e9-9a66-784f43822e80-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/cdd517fe-def4-11e9-948e-784f43822e80-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/41780b5a-def8-11e9-94d9-784f43822e80-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/13e5fa74-defa-11e9-809c-784f43822e80-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/de130ab0-def7-11e9-b668-784f43822e80-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/ead5c8b2-a4c9-4724-b1dd-9f00b46cbd3d-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/ce57700a-def9-11e9-842d-784f43822e80-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/998f01ae-def8-11e9-b95c-784f43822e80-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/dbdc9198-def8-11e9-8681-784f43822e80-profile_image-70x70.png",
            "https://static-cdn.jtvnw.net/user-default-pictures-uv/294c98b5-e34d-42cd-a8f0-140b72fba9b0-profile_image-70x70.png",
        ];

        internal static readonly SearchValues<char> EmojiExcludeChars = SearchValues.Create('\uFE0F');

        private readonly ChatRenderOptions _options;
        private readonly string _cacheDir;
        private readonly ITaskLogger? _logger;
        private readonly ConcurrentDictionary<string, TwitchEmote> _gigantifiedEmotes = new(StringComparer.Ordinal);
        private readonly DisposableDictionary<string, SKImage> _emojiCache = [];
        private ZipArchive? _emojiArchive;
        private Dictionary<string, ZipArchiveEntry> _emojiEntries = [];
        private Dictionary<int, string[]>? _allEmojiSequences;
        private int _emojiTargetHeight;
        private bool _disposed;

        public DisposableDictionary<string, ChatBadge> Badges { get; } = [];
        public DisposableDictionary<string, TwitchEmote> FirstPartyEmotes { get; } = [];
        public DisposableDictionary<string, TwitchEmote> ThirdPartyEmotes { get; } = [];
        public DisposableDictionary<string, TwitchEmote> GiphyGifs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public DisposableDictionary<string, CheerEmote> Cheermotes { get; } = [];
        public DisposableDictionary<string, SKImage> Avatars { get; } = [];

        public HighlightIcons HighlightIcons { get; }
        public SectionImageCache SectionImageCache { get; } = new();
        public ImageMemoryBudget ImageBudget { get; }
        public EmoteSurfaceCache EmoteSurfaceCache { get; } = new();

        private Dictionary<int, string[]> AllEmojiSequences => _allEmojiSequences ??=
            _emojiEntries.Keys
                .GroupBy(static x => Rune.GetRuneAt(x, 0).Value)
                .ToDictionary(static x => x.Key, static x => x.ToArray());

        public AssetCatalog(ChatRenderOptions options, string cacheDir, SKPaint baseOutlinePaint, ITaskLogger? logger = null)
        {
            _options = options;
            _cacheDir = cacheDir;
            _logger = logger;
            _emojiTargetHeight = Math.Max(1, (int)Math.Round(36 * options.ReferenceScale * options.EmojiScale));
            ImageBudget = new ImageMemoryBudget(Math.Max(1, options.ImageCacheMb) * 1024L * 1024L);
            HighlightIcons = new HighlightIcons(options, cacheDir, Purple, baseOutlinePaint);
        }

        public void InitializeInMemoryEmojis(EmojiVendor vendor, CancellationToken cancellationToken = default)
        {
            _emojiTargetHeight = Math.Max(1, (int)Math.Round(36 * _options.ReferenceScale * _options.EmojiScale));
            (_emojiArchive, _emojiEntries) = OpenInMemoryEmojiArchive(vendor, cancellationToken);
            _ = AllEmojiSequences;
        }

        public async Task LoadAsync(ChatRoot chatRoot, ITaskProgress progress, CancellationToken cancellationToken)
        {
            chatRoot.comments ??= [];

            // Optional online streamer metadata enrichment (strictly gated on !Offline)
            if (!_options.Offline
                && chatRoot.streamer is { id: > 0 } streamer
                && (string.IsNullOrEmpty(streamer.name) || string.IsNullOrEmpty(streamer.login)))
            {
                try
                {
                    var userInfo = await TwitchHelper.GetUserInfo([streamer.id.ToString()]).ConfigureAwait(false);
                    var resolved = userInfo?.data?.users?.FirstOrDefault();
                    if (resolved is not null)
                    {
                        if (string.IsNullOrEmpty(streamer.name))
                        {
                            streamer.name = resolved.displayName;
                        }
                        if (string.IsNullOrEmpty(streamer.login))
                        {
                            streamer.login = resolved.login;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    progress.LogVerbose($"Optional streamer metadata lookup skipped: {ex.Message}");
                }
            }

            int streamerId = chatRoot.streamer?.id ?? 0;
            var embeddedData = chatRoot.embeddedData;

            var badgeTask = LoadScaledBadgesAsync(chatRoot.comments, streamerId, embeddedData, progress, cancellationToken);
            var emoteTask = LoadScaledEmotesAsync(chatRoot.comments, embeddedData, progress, cancellationToken);
            var emoteThirdTask = LoadScaledThirdEmotesAsync(chatRoot.comments, streamerId, embeddedData, progress, cancellationToken);
            var cheerTask = LoadScaledBitsAsync(chatRoot.comments, streamerId, embeddedData, progress, cancellationToken);
            var emojiTask = Task.Run(() =>
            {
                _emojiTargetHeight = Math.Max(1, (int)Math.Round(36 * _options.ReferenceScale * _options.EmojiScale));
                return OpenInMemoryEmojiArchive(_options.EmojiVendor, cancellationToken);
            }, cancellationToken);
            var avatarTask = _options.RenderUserAvatars
                ? LoadScaledAvatarsAsync(chatRoot.comments, progress, cancellationToken)
                : Task.FromResult(new Dictionary<string, SKImage>());
            var gifTask = _options.GiphyGifs
                ? LoadScaledGiphyGifsAsync(chatRoot.comments, embeddedData, progress, cancellationToken)
                : Task.FromResult(new List<TwitchEmote>());

            await Task.WhenAll(badgeTask, emoteTask, emoteThirdTask, cheerTask, emojiTask, avatarTask, gifTask).ConfigureAwait(false);

            // Release embedded Base64 buffers once decoded into asset caches
            chatRoot.embeddedData = null;

            Badges.AddRange(badgeTask.Result, static x => x.Name, static x => x);
            FirstPartyEmotes.AddRange(emoteTask.Result, static x => x.Id, static x => x);
            ThirdPartyEmotes.AddRange(emoteThirdTask.Result, static x => x.Name, static x => x);
            Cheermotes.AddRange(cheerTask.Result, static x => x.prefix, static x => x);
            (_emojiArchive, _emojiEntries) = emojiTask.Result;
            Avatars.AddRange(avatarTask.Result);
            GiphyGifs.AddRange(gifTask.Result, static x => x.Name, static x => x);

            // Pre-initialize grouped multi-codepoint lookup table before parallel workers start
            _ = AllEmojiSequences;
        }

        internal static (ZipArchive? Archive, Dictionary<string, ZipArchiveEntry> Entries) OpenInMemoryEmojiArchive(
            EmojiVendor emojiVendor,
            CancellationToken cancellationToken = default)
        {
            if (emojiVendor == EmojiVendor.None)
            {
                return (null, []);
            }

            var archive = new ZipArchive(emojiVendor.MemoryStream(), ZipArchiveMode.Read, leaveOpen: false);
            try
            {
                var emojiAssetsPath = emojiVendor.AssetPath();
                var sep = emojiVendor.UnicodeSequenceSeparator();
                var index = new Dictionary<string, ZipArchiveEntry>(emojiVendor.EmojiCount(), StringComparer.Ordinal);
                var sb = new StringBuilder(8);

                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(entry.Name)
                        || !entry.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                        || Path.GetDirectoryName(entry.FullName) is not { } dir
                        || !dir.EndsWith(emojiAssetsPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var nameSpan = Path.GetFileNameWithoutExtension(entry.Name.AsSpan());
                    sb.Clear();
                    foreach (var range in nameSpan.Split(sep))
                    {
                        var hexPart = nameSpan[range];
                        if (hexPart.IsEmpty) continue;
                        if (!uint.TryParse(hexPart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var num))
                            continue;
                        if (num == 0xFE0F) continue;

                        sb.Append(new Rune(num));
                    }

                    if (sb.Length > 0)
                    {
                        index[sb.ToString()] = entry;
                    }
                }

                return (archive, index);
            }
            catch
            {
                archive.Dispose();
                throw;
            }
        }

        public bool ContainsEmoji(ReadOnlySpan<char> text, out int firstEmoji)
        {
            if (!text.ContainsAnyExceptInRange('\0', '\u007F') || _emojiEntries.Count == 0)
            {
                firstEmoji = -1;
                return false;
            }

            var emojiEntryLookup = _emojiEntries.GetAlternateLookup<ReadOnlySpan<char>>();
            Span<char> stackSpace = stackalloc char[16];

            var fragmentSlice = text;
            var index = 0;
            while (true)
            {
                var elementLength = StringInfo.GetNextTextElementLength(fragmentSlice);
                if (elementLength == 0)
                {
                    firstEmoji = -1;
                    return false;
                }

                if (elementLength == 1 && char.IsAscii(fragmentSlice[0]))
                {
                    fragmentSlice = fragmentSlice[elementLength..];
                    index += elementLength;
                    continue;
                }

                var textElement = fragmentSlice[..elementLength];
                foreach (var range in textElement.Split(ZERO_WIDTH_JOINER))
                {
                    var subEmoji = textElement[range];
                    var lookupKey = subEmoji.Length <= stackSpace.Length ? stackSpace : new char[subEmoji.Length];
                    var written = subEmoji.CopyToExcept(lookupKey, EmojiExcludeChars);
                    if (emojiEntryLookup.ContainsKey(lookupKey[..written]))
                    {
                        firstEmoji = index;
                        return true;
                    }
                }

                fragmentSlice = fragmentSlice[elementLength..];
                index += elementLength;
            }
        }

        public bool TryGetEmojiImage(ReadOnlySpan<char> keySpan, [MaybeNullWhen(false)] out SKImage emojiImage)
        {
            if (_emojiEntries.Count == 0)
            {
                emojiImage = null;
                return false;
            }

            var entryLookup = _emojiEntries.GetAlternateLookup<ReadOnlySpan<char>>();
            if (!entryLookup.TryGetValue(keySpan, out var emojiEntry))
            {
                emojiImage = null;
                return false;
            }

            lock (_emojiCache)
            {
                var cacheLookup = _emojiCache.GetAlternateLookup<ReadOnlySpan<char>>();
                if (cacheLookup.TryGetValue(keySpan, out emojiImage))
                {
                    return emojiImage is not null;
                }

                using var entryStream = emojiEntry.Open();
                using var bitmap = SKBitmap.Decode(entryStream);
                if (bitmap is null)
                {
                    _logger?.LogVerbose($"Failed to decode emoji {emojiEntry.Name}, skipping.");
                    emojiImage = null;
                    return false;
                }

                SKImageInfo oldEmojiInfo = bitmap.Info;
                int targetW = Math.Max(1, (int)(_emojiTargetHeight / (double)oldEmojiInfo.Height * oldEmojiInfo.Width));
                SKImageInfo imageInfo = new(targetW, _emojiTargetHeight);
                using var newBitmap = new SKBitmap(imageInfo);
                bitmap.ScalePixels(newBitmap, SKFilterQuality.High);
                newBitmap.SetImmutable();

                emojiImage = SKImage.FromBitmap(newBitmap);
                _emojiCache[keySpan.ToString()] = emojiImage;
                return true;
            }
        }

        public SKImage? LookupEmojiSlow(ReadOnlySpan<char> textElement, List<string> emojiMatches)
        {
            if (textElement.IsEmpty || _emojiEntries.Count == 0)
            {
                return null;
            }

            var firstCodepoint = textElement.Length > 1 && char.IsHighSurrogate(textElement[0]) && char.IsLowSurrogate(textElement[1])
                ? char.ConvertToUtf32(textElement[0], textElement[1])
                : textElement[0];

            emojiMatches.Clear();
            if (AllEmojiSequences.TryGetValue(firstCodepoint, out var matches))
            {
                foreach (var emoji in matches)
                {
                    if (textElement.StartsWith(emoji))
                    {
                        emojiMatches.Add(emoji);
                    }
                }
            }

            if (emojiMatches.Count == 0)
            {
                return null;
            }

            var selectedEmoji = emojiMatches.MaxBy(static x => x.Length);
            return selectedEmoji is not null && TryGetEmojiImage(selectedEmoji, out var emojiImage)
                ? emojiImage
                : null;
        }

        public TwitchEmote GetOrCreateGigantifiedEmote(TwitchEmote baseEmote)
        {
            if (baseEmote.ImageData is null || baseEmote.ImageData.Length == 0)
            {
                return baseEmote;
            }

            string cacheKey = $"{baseEmote.EmoteProvider}:{baseEmote.Id}:{baseEmote.Name}";
            return _gigantifiedEmotes.GetOrAdd(cacheKey, _ =>
            {
                var giant = new TwitchEmote(
                    baseEmote.ImageData,
                    null,
                    baseEmote.EmoteProvider,
                    baseEmote.ImageScale,
                    baseEmote.Id + "_giant",
                    baseEmote.Name,
                    baseEmote.IsZeroWidth);

                int snapThreshold = (int)Math.Round(4 * _options.ReferenceScale);
                double newScale = 4.0 / giant.ImageScale * _options.ReferenceScale * _options.EmoteScale;
                giant.SnapScale(newScale, snapThreshold, snapThreshold);
                return giant;
            });
        }

        public bool TryResolveAvatarImage(string? avatarUrl, string displayName, [MaybeNullWhen(false)] out SKImage avatarImage)
        {
            if (!string.IsNullOrWhiteSpace(avatarUrl) && Avatars.TryGetValue(avatarUrl, out var cached) && cached is not null)
            {
                avatarImage = cached;
                return true;
            }

            string safeName = displayName ?? string.Empty;
            uint idx = (uint)Math.Abs((long)safeName.GetHashCode()) % (uint)DefaultAvatarUrls.Length;
            var fallbackUrl = DefaultAvatarUrls[idx];
            if (Avatars.TryGetValue(fallbackUrl, out var fallbackImg) && fallbackImg is not null)
            {
                avatarImage = fallbackImg;
                return true;
            }

            avatarImage = null;
            return false;
        }

        private async Task<List<ChatBadge>> LoadScaledBadgesAsync(
            List<Comment> comments,
            int streamerId,
            EmbeddedData? embeddedData,
            ITaskProgress progress,
            CancellationToken cancellationToken)
        {
            if (!_options.ChatBadges)
            {
                return [];
            }

            var badges = await TwitchHelper.GetChatBadges(comments, streamerId, _cacheDir, progress, embeddedData, _options.Offline, cancellationToken).ConfigureAwait(false);
            var newHeight = (int)Math.Round(36 * _options.ReferenceScale * _options.BadgeScale);
            var snapThreshold = (int)Math.Round(1 * _options.ReferenceScale);

            foreach (var badge in badges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                badge.SnapResize(newHeight, snapThreshold, snapThreshold);
            }

            return badges;
        }

        private async Task<List<TwitchEmote>> LoadScaledEmotesAsync(
            List<Comment> comments,
            EmbeddedData? embeddedData,
            ITaskProgress progress,
            CancellationToken cancellationToken)
        {
            var emotes = await TwitchHelper.GetEmotes(comments, _cacheDir, progress, embeddedData, _options.Offline, cancellationToken).ConfigureAwait(false);
            var snapThreshold = (int)Math.Round(4 * _options.ReferenceScale);

            foreach (var emote in emotes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var newScale = 2.0 / emote.ImageScale * _options.ReferenceScale * _options.EmoteScale;
                emote.SnapScale(newScale, snapThreshold, snapThreshold);
            }

            return emotes;
        }

        private async Task<List<TwitchEmote>> LoadScaledThirdEmotesAsync(
            List<Comment> comments,
            int streamerId,
            EmbeddedData? embeddedData,
            ITaskProgress progress,
            CancellationToken cancellationToken)
        {
            var emotes = await TwitchHelper.GetThirdPartyEmotes(
                comments,
                streamerId,
                _cacheDir,
                progress,
                embeddedData,
                _options.BttvEmotes,
                _options.FfzEmotes,
                _options.StvEmotes,
                _options.AllowUnlistedEmotes,
                _options.Offline,
                cancellationToken).ConfigureAwait(false);

            var snapThreshold = (int)Math.Round(4 * _options.ReferenceScale);
            foreach (var emote in emotes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var newScale = 2.0 / emote.ImageScale * _options.ReferenceScale * _options.EmoteScale;
                emote.SnapScale(newScale, snapThreshold, snapThreshold);
            }

            return emotes;
        }

        private async Task<List<TwitchEmote>> LoadScaledGiphyGifsAsync(
            List<Comment> comments,
            EmbeddedData? embeddedData,
            ITaskProgress progress,
            CancellationToken cancellationToken)
        {
            List<TwitchEmote> gifs;
            try
            {
                gifs = await TwitchHelper.GetGiphyGifs(comments, _cacheDir, progress, embeddedData, _options.Offline, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                progress.LogWarning($"Unable to load chat GIFs: {ex.Message} Rendering them as text instead.");
                return [];
            }

            var maxWidth = Math.Max(1, _options.ChatWidth - _options.SidePadding * 2);
            var maxHeight = Math.Max(1, _options.ChatHeight * Math.Max(1, _options.GifMaxHeightPercent) / 100);
            foreach (var gif in gifs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                gif.Scale(Math.Min(maxWidth / (double)gif.Width, maxHeight / (double)gif.Height));
            }

            return gifs;
        }

        private async Task<List<CheerEmote>> LoadScaledBitsAsync(
            List<Comment> comments,
            int streamerId,
            EmbeddedData? embeddedData,
            ITaskProgress progress,
            CancellationToken cancellationToken)
        {
            var cheers = await TwitchHelper.GetBits(comments, _cacheDir, streamerId.ToString(), progress, embeddedData, _options.Offline, cancellationToken).ConfigureAwait(false);
            var snapThreshold = (int)Math.Round(4 * _options.ReferenceScale);

            foreach (var cheer in cheers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var imageScale = cheer.tierList.FirstOrDefault().Value?.ImageScale ?? 2;
                var newScale = 2.0 / imageScale * _options.ReferenceScale * _options.EmoteScale;
                cheer.SnapScale(newScale, snapThreshold, snapThreshold);
            }

            return cheers;
        }

        private async Task<Dictionary<string, SKImage>> LoadScaledAvatarsAsync(
            List<Comment> comments,
            ITaskProgress progress,
            CancellationToken cancellationToken)
        {
            var avatars = await TwitchHelper.GetAvatars(comments, DefaultAvatarUrls, _cacheDir, progress, _options.Offline, cancellationToken).ConfigureAwait(false);
            var newHeight = Math.Max(2, (int)Math.Round(36 * _options.ReferenceScale * _options.AvatarScale));

            using var maskPath = new SKPath();
            var radius = newHeight / 2f;
            maskPath.AddCircle(radius, radius, radius);

            return avatars.Keys.ToDictionary(x => x, x =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var oldBitmap = avatars[x];
                var oldImageInfo = oldBitmap.Info;
                int newWidth = Math.Max(1, (int)(newHeight / (double)oldImageInfo.Height * oldImageInfo.Width));
                var imageInfo = new SKImageInfo(newWidth, newHeight);
                using var newBitmap = new SKBitmap(imageInfo);
                oldBitmap.ScalePixels(newBitmap, SKFilterQuality.High);

                using (var canvas = new SKCanvas(newBitmap))
                {
                    canvas.ClipPath(maskPath, SKClipOperation.Difference, true);
                    canvas.Clear();
                }

                newBitmap.SetImmutable();
                return SKImage.FromBitmap(newBitmap);
            });
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            Badges.Dispose();
            FirstPartyEmotes.Dispose();
            ThirdPartyEmotes.Dispose();
            GiphyGifs.Dispose();

            foreach (var giant in _gigantifiedEmotes.Values)
            {
                giant?.Dispose();
            }
            _gigantifiedEmotes.Clear();

            Cheermotes.Dispose();
            _emojiCache.Dispose();
            _emojiArchive?.Dispose();
            Avatars.Dispose();
            SectionImageCache.Dispose();
            EmoteSurfaceCache.Dispose();
            HighlightIcons.Dispose();

            Badges.Clear();
            FirstPartyEmotes.Clear();
            ThirdPartyEmotes.Clear();
            GiphyGifs.Clear();
            Cheermotes.Clear();
            _emojiCache.Clear();
            _emojiEntries.Clear();
            Avatars.Clear();
        }
    }
}

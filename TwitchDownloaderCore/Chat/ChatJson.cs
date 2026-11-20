using System.Buffers;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using TwitchDownloaderCore.Tools;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.Chat
{
    [JsonSourceGenerationOptions(
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString)]
    [JsonSerializable(typeof(ChatRoot))]
    [JsonSerializable(typeof(ChatRootInfo))]
    [JsonSerializable(typeof(Streamer))]
    [JsonSerializable(typeof(Video))]
    [JsonSerializable(typeof(Comment))]
    [JsonSerializable(typeof(List<Comment>))]
    [JsonSerializable(typeof(List<ChatBroadcastEvent>))]
    [JsonSerializable(typeof(EmbeddedData))]
    [JsonSerializable(typeof(LegacyEmbeddedData))]
    internal partial class ChatJsonContext : JsonSerializerContext;

    public static class ChatJson
    {
        private static readonly ChatJsonContext _chatJsonContext = new(new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        /// <summary>
        /// Asynchronously deserializes a chat JSON or <c>.json.gz</c> file in a single streaming pass without constructing
        /// an intermediate <see cref="JsonDocument"/> DOM or performing hidden network requests.
        /// Supports UTF-8 (with or without BOM), GZip-compressed streams, UTF-16 LE/BE, and UTF-32 LE/BE.
        /// </summary>
        /// <returns>A <see cref="ChatRoot"/> representation of the deserialized chat JSON file.</returns>
        /// <exception cref="IOException">The file does not exist.</exception>
        /// <exception cref="NotSupportedException">The file is not a valid chat format.</exception>
        public static async Task<ChatRoot> DeserializeAsync(
            string filePath,
            bool getComments = true,
            bool onlyFirstAndLastComments = false,
            bool getEmbeds = true,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(filePath);

            if (!File.Exists(filePath))
            {
                throw new IOException("Json file does not exist");
            }

            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536, useAsync: true);
            ChatRoot chatRoot = await DeserializeFromStreamAsync(fs, filePath, cancellationToken).ConfigureAwait(false);

            chatRoot ??= new ChatRoot();
            chatRoot.FileInfo ??= new ChatRootInfo();

            if (!getComments)
            {
                chatRoot.comments = null;
                chatRoot.events = [];
            }
            else
            {
                chatRoot.comments ??= [];
                if (onlyFirstAndLastComments)
                {
                    chatRoot.events = [];
                    if (chatRoot.comments.Count > 2)
                    {
                        chatRoot.comments = [chatRoot.comments[0], chatRoot.comments[^1]];
                    }
                }
                else
                {
                    chatRoot.events ??= [];
                }
            }

            if (!getEmbeds)
            {
                chatRoot.embeddedData = null;
            }

            UpgradeChatJson(chatRoot, getComments);
            return chatRoot;
        }

        private static async Task<ChatRoot> DeserializeFromStreamAsync(Stream stream, string filePath, CancellationToken cancellationToken)
        {
            if (!stream.CanSeek)
            {
                return await JsonSerializer.DeserializeAsync(stream, _chatJsonContext.ChatRoot, cancellationToken).ConfigureAwait(false);
            }

            const int RENT_LENGTH = 4;
            var rentedBuffer = ArrayPool<byte>.Shared.Rent(RENT_LENGTH);
            try
            {
                int totalRead = 0;
                while (totalRead < RENT_LENGTH)
                {
                    int n = await stream.ReadAsync(rentedBuffer.AsMemory(totalRead, RENT_LENGTH - totalRead), cancellationToken).ConfigureAwait(false);
                    if (n == 0)
                    {
                        throw new EndOfStreamException($"{Path.GetFileName(filePath)} is not a valid chat format.");
                    }
                    totalRead += n;
                }

                switch (rentedBuffer)
                {
                    case [0x1F, 0x8B, ..]:
                    {
                        stream.Seek(-RENT_LENGTH, SeekOrigin.Current);
                        await using var gs = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
                        return await JsonSerializer.DeserializeAsync(gs, _chatJsonContext.ChatRoot, cancellationToken).ConfigureAwait(false);
                    }
                    case [0x00, 0x00, 0xFE, 0xFF]: // UTF-32 BE (4-byte BOM already consumed)
                    {
                        var utf32Be = new UTF32Encoding(bigEndian: true, byteOrderMark: false);
                        await using var transcoding = Encoding.CreateTranscodingStream(stream, utf32Be, Encoding.UTF8, leaveOpen: true);
                        return await JsonSerializer.DeserializeAsync(transcoding, _chatJsonContext.ChatRoot, cancellationToken).ConfigureAwait(false);
                    }
                    case [0xFF, 0xFE, 0x00, 0x00]: // UTF-32 LE (4-byte BOM already consumed)
                    {
                        var utf32Le = new UTF32Encoding(bigEndian: false, byteOrderMark: false);
                        await using var transcoding = Encoding.CreateTranscodingStream(stream, utf32Le, Encoding.UTF8, leaveOpen: true);
                        return await JsonSerializer.DeserializeAsync(transcoding, _chatJsonContext.ChatRoot, cancellationToken).ConfigureAwait(false);
                    }
                    case [0xFE, 0xFF, ..]: // UTF-16 BE (seek back 2 bytes after 2-byte BOM)
                    {
                        stream.Seek(-2, SeekOrigin.Current);
                        await using var transcoding = Encoding.CreateTranscodingStream(stream, Encoding.BigEndianUnicode, Encoding.UTF8, leaveOpen: true);
                        return await JsonSerializer.DeserializeAsync(transcoding, _chatJsonContext.ChatRoot, cancellationToken).ConfigureAwait(false);
                    }
                    case [0xFF, 0xFE, ..]: // UTF-16 LE (seek back 2 bytes after 2-byte BOM)
                    {
                        stream.Seek(-2, SeekOrigin.Current);
                        await using var transcoding = Encoding.CreateTranscodingStream(stream, Encoding.Unicode, Encoding.UTF8, leaveOpen: true);
                        return await JsonSerializer.DeserializeAsync(transcoding, _chatJsonContext.ChatRoot, cancellationToken).ConfigureAwait(false);
                    }
                    case [0xEF, 0xBB, 0xBF, ..]: // UTF-8 BOM (skip 3-byte BOM)
                    {
                        stream.Seek(-1, SeekOrigin.Current);
                        return await JsonSerializer.DeserializeAsync(stream, _chatJsonContext.ChatRoot, cancellationToken).ConfigureAwait(false);
                    }
                    case [(byte)'{' or (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n', ..]:
                    {
                        stream.Seek(-RENT_LENGTH, SeekOrigin.Current);
                        return await JsonSerializer.DeserializeAsync(stream, _chatJsonContext.ChatRoot, cancellationToken).ConfigureAwait(false);
                    }
                    default:
                    {
                        throw new NotSupportedException($"{Path.GetFileName(filePath)} is not a valid chat format.");
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }

#pragma warning disable CS0618 // Type or member is obsolete
        private static void UpgradeChatJson(ChatRoot chatRoot, bool hasComments = true)
        {
            const int MAX_STREAM_LENGTH = 172_800; // 48 hours in seconds. https://help.twitch.tv/s/article/broadcast-guidelines

            var comments = chatRoot.comments;
            var firstComment = comments is { Count: > 0 } ? comments[0] : null;
            var lastComment = comments is { Count: > 0 } ? comments[^1] : null;

            if (chatRoot.video is null)
            {
                double startSec = 0;
                double endSec = hasComments ? 0 : MAX_STREAM_LENGTH;

                if (firstComment is not null && lastComment is not null)
                {
                    startSec = Math.Floor(firstComment.content_offset_seconds);
                    endSec = Math.Ceiling(lastComment.content_offset_seconds);
                }
                else if (chatRoot.events is { Count: > 0 })
                {
                    startSec = Math.Floor(chatRoot.events.Min(static e => e?.content_offset_seconds ?? 0));
                    endSec = Math.Ceiling(chatRoot.events.Max(static e => e?.content_offset_seconds ?? 0));
                }

                chatRoot.video = new Video
                {
                    start = Math.Max(0, startSec),
                    end = Math.Max(Math.Max(0, startSec), endSec)
                };
            }

            chatRoot.video.id ??= firstComment?.content_id;

            if (chatRoot.video.created_at == default
                && firstComment is not null
                && firstComment.created_at != default
                && (firstComment.created_at - DateTime.MinValue).TotalSeconds >= firstComment.content_offset_seconds)
            {
                chatRoot.video.created_at = firstComment.created_at - TimeSpan.FromSeconds(firstComment.content_offset_seconds);
            }

            if (chatRoot.streamer is null)
            {
                var broadcasterComment = comments?
                    .Where(static x => x?.message?.user_badges != null)
                    .FirstOrDefault(static x => x.message.user_badges.Any(static b => b?._id == "broadcaster"));

                if (!int.TryParse(chatRoot.video.user_id, out var assumedId))
                {
                    if (comments?.FirstOrDefault(x => x is not null && int.TryParse(x.channel_id, out assumedId)) is null)
                    {
                        if (!int.TryParse(broadcasterComment?.commenter?._id, out assumedId))
                        {
                            assumedId = 0;
                        }
                    }
                }

                var assumedName = chatRoot.video.user_name ?? broadcasterComment?.commenter?.display_name;
                var assumedLogin = broadcasterComment?.commenter?.name;

                chatRoot.streamer = new Streamer
                {
                    name = assumedName,
                    login = assumedLogin,
                    id = assumedId
                };
            }

            if (chatRoot.video.user_name is not null)
            {
                chatRoot.video.user_name = null;
            }

            if (chatRoot.video.user_id is not null)
            {
                chatRoot.video.user_id = null;
            }

            if (chatRoot.video.duration is not null)
            {
                chatRoot.video.length = UrlTimeCode.Parse(chatRoot.video.duration).TotalSeconds;
                chatRoot.video.end = chatRoot.video.length;
                chatRoot.video.duration = null;
            }
            else if (chatRoot.video.end <= 0 && chatRoot.video.length > 0)
            {
                chatRoot.video.end = chatRoot.video.length;
            }

            if (comments is not { Count: > 0 })
            {
                return;
            }

            foreach (var comment in comments)
            {
                if (comment?.message is { bits_spent: 0 })
                {
                    if (comment.bits_spent > 0)
                    {
                        comment.message.bits_spent = comment.bits_spent;
                    }
                    else if (comment.event_data is { bits_spent: > 0 })
                    {
                        comment.message.bits_spent = comment.event_data.bits_spent;
                    }
                }
            }

            // Fix incorrect bits_spent value on chats between v5 shutdown and the lay295#520 fix
            if (comments.All(static c => (c?.message?.bits_spent ?? 0) == 0))
            {
                foreach (var comment in comments)
                {
                    if (comment?.message?.body is not { Length: > 0 } body)
                    {
                        continue;
                    }

                    var bitMatch = TwitchRegex.BitsRegex.Match(body);
                    if (bitMatch.Success && int.TryParse(bitMatch.ValueSpan, out var result))
                    {
                        comment.message.bits_spent = result;
                    }
                }
            }

            // 300x300 avatars are overkill for chat rendering
            foreach (var comment in comments)
            {
                if (comment?.commenter?.logo?.Contains("300x300") ?? false)
                {
                    comment.commenter.logo = comment.commenter.logo.Replace("300x300", "70x70");
                }
            }
        }
#pragma warning restore CS0618 // Type or member is obsolete

        /// <summary>
        /// Asynchronously serializes a chat json file.
        /// </summary>
        public static Task SerializeAsync(Stream outputStream, ChatRoot chatRoot, CancellationToken cancellationToken)
        {
            return JsonSerializer.SerializeAsync(outputStream, chatRoot, _chatJsonContext.ChatRoot, cancellationToken);
        }
    }
}
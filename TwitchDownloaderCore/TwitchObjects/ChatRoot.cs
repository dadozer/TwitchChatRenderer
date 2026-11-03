using System.Diagnostics;
using System.Text.Json.Serialization;

namespace TwitchDownloaderCore.TwitchObjects
{
    public class Streamer
    {
        public string name { get; set; }
        public string login { get; set; }
        public int id { get; set; }
    }

    public class Clipper
    {
        public string name { get; set; }
        public string login { get; set; }
        public int id { get; set; }
    }

    [DebuggerDisplay("{display_name}")]
    public class Commenter
    {
        public string display_name { get; set; }
        public string _id { get; set; }
        public string name { get; set; }
        public string bio { get; set; }
        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }
        public string logo { get; set; }

        public Commenter Clone()
        {
            return new Commenter()
            {
                display_name = display_name,
                _id = _id,
                name = name,
                bio = bio,
                created_at = created_at,
                updated_at = updated_at,
                logo = logo
            };
        }
    }

    [DebuggerDisplay("{emoticon_id}")]
    public class Emoticon
    {
        public string emoticon_id { get; set; }
    }

    [DebuggerDisplay("{text}")]
    public class Fragment
    {
        public string text { get; set; }
        public Emoticon emoticon { get; set; }

        public Fragment Clone()
        {
            return new Fragment()
            {
                text = text,
                emoticon = emoticon
            };
        }
    }

    [DebuggerDisplay("{_id}")]
    public class UserBadge
    {
        public string _id { get; set; }
        public string version { get; set; }

        public UserBadge Clone()
        {
            return new UserBadge()
            {
                _id = _id,
                version = version
            };
        }
    }

    [DebuggerDisplay("{_id}")]
    public class Emoticon2
    {
        public string _id { get; set; }
        public int begin { get; set; }
        public int end { get; set; }

        public Emoticon2 Clone()
        {
            return new Emoticon2()
            {
                _id = _id,
                begin = begin,
                end = end
            };
        }
    }

    [DebuggerDisplay("{body}")]
    public class Message
    {
        public string body { get; set; }
        public int bits_spent { get; set; }
        public List<Fragment> fragments { get; set; }
        public List<UserBadge> user_badges { get; set; }
        public string user_color { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public UserNoticeParams user_notice_params { get; set; }
        public List<Emoticon2> emoticons { get; set; }

        public Message Clone()
        {
            var newMessage = new Message()
            {
                body = body,
                bits_spent = bits_spent,
                fragments = fragments is null ? null : new List<Fragment>(fragments.Capacity),
                user_badges = new List<UserBadge>(user_badges?.Capacity ?? 0),
                user_color = user_color,
                user_notice_params = user_notice_params?.Clone(),
                emoticons = new List<Emoticon2>(emoticons?.Capacity ?? 0)
            };
            for (int i = 0; i < fragments?.Count; i++)
            {
                newMessage.fragments.Add(fragments[i].Clone());
            }
            for (int i = 0; i < user_badges?.Count; i++)
            {
                newMessage.user_badges.Add(user_badges[i].Clone());
            }
            for (int i = 0; i < emoticons?.Count; i++)
            {
                newMessage.emoticons.Add(emoticons[i].Clone());
            }
            return newMessage;
        }
    }

    [DebuggerDisplay("{msg_id}")]
    public class UserNoticeParams
    {
        public string msg_id { get; set; }

        [JsonPropertyName("msg-id")]
        public string msg_id_hyphen
        {
            get => null;
            set
            {
                if (string.IsNullOrEmpty(msg_id))
                {
                    msg_id = value;
                }
            }
        }

        public UserNoticeParams Clone()
        {
            return new UserNoticeParams()
            {
                msg_id = msg_id
            };
        }
    }

    public class CommentReplyInfo
    {
        public string parent_msg_id { get; set; }
        public string parent_user_id { get; set; }
        public string parent_user_login { get; set; }
        public string parent_display_name { get; set; }
        public string parent_msg_body { get; set; }
        public string thread_parent_msg_id { get; set; }
        public string thread_user_login { get; set; }

        public CommentReplyInfo Clone()
        {
            return new CommentReplyInfo
            {
                parent_msg_id = parent_msg_id,
                parent_user_id = parent_user_id,
                parent_user_login = parent_user_login,
                parent_display_name = parent_display_name,
                parent_msg_body = parent_msg_body,
                thread_parent_msg_id = thread_parent_msg_id,
                thread_user_login = thread_user_login
            };
        }
    }

    public class CommentPowerUpInfo
    {
        public string type { get; set; }
        public string emote_id { get; set; }
        public string animation_id { get; set; }

        public CommentPowerUpInfo Clone()
        {
            return new CommentPowerUpInfo
            {
                type = type,
                emote_id = emote_id,
                animation_id = animation_id
            };
        }
    }

    public class CommentHypeChatInfo
    {
        public int amount { get; set; }
        public int exponent { get; set; }
        public double DecimalAmount { get; set; }
        public string currency { get; set; }
        public string level { get; set; }
        public bool is_system_message { get; set; }

        [JsonIgnore]
        public double EffectiveDecimalAmount =>
            DecimalAmount > 0 ? DecimalAmount : (amount > 0 ? amount / Math.Pow(10, Math.Max(0, exponent)) : 0);

        public CommentHypeChatInfo Clone()
        {
            return new CommentHypeChatInfo
            {
                amount = amount,
                exponent = exponent,
                DecimalAmount = DecimalAmount,
                currency = currency,
                level = level,
                is_system_message = is_system_message
            };
        }
    }

    public class CommentSharedChatInfo
    {
        public string source_room_id { get; set; }
        public string source_msg_id { get; set; }
        public string source_badges { get; set; }

        public CommentSharedChatInfo Clone()
        {
            return new CommentSharedChatInfo
            {
                source_room_id = source_room_id,
                source_msg_id = source_msg_id,
                source_badges = source_badges
            };
        }
    }

    public class CommentEventData
    {
        public string msg_id { get; set; }
        public bool is_first_message { get; set; }
        public bool is_returning_chatter { get; set; }
        public bool is_highlighted { get; set; }
        public bool is_subscriber { get; set; }
        public bool is_mod { get; set; }
        public bool is_vip { get; set; }
        public int bits_spent { get; set; }
        public string custom_reward_id { get; set; }
        public CommentReplyInfo reply { get; set; }
        public CommentPowerUpInfo power_up { get; set; }
        public CommentHypeChatInfo hype_chat { get; set; }
        public CommentSharedChatInfo shared_chat { get; set; }

        // USERNOTICE fields
        public string notice_type { get; set; }
        public string system_message { get; set; }
        public string user_message { get; set; }
        public string sub_plan { get; set; }
        public string sub_plan_name { get; set; }
        public int? cumulative_months { get; set; }
        public int? streak_months { get; set; }
        public string recipient_id { get; set; }
        public string recipient_user_name { get; set; }
        public string recipient_display_name { get; set; }
        public int? mass_gift_count { get; set; }
        public int? sender_total_gifts { get; set; }
        public int? raid_viewer_count { get; set; }
        public string raid_display_name { get; set; }
        public string announcement_color { get; set; }
        public string milestone_category { get; set; }
        public int? milestone_value { get; set; }
        public int? milestone_reward { get; set; }
        [JsonPropertyName("params")]
        public Dictionary<string, string> notice_params { get; set; }

        public CommentEventData Clone()
        {
            return new CommentEventData
            {
                msg_id = msg_id,
                is_first_message = is_first_message,
                is_returning_chatter = is_returning_chatter,
                is_highlighted = is_highlighted,
                is_subscriber = is_subscriber,
                is_mod = is_mod,
                is_vip = is_vip,
                bits_spent = bits_spent,
                custom_reward_id = custom_reward_id,
                reply = reply?.Clone(),
                power_up = power_up?.Clone(),
                hype_chat = hype_chat?.Clone(),
                shared_chat = shared_chat?.Clone(),
                notice_type = notice_type,
                system_message = system_message,
                user_message = user_message,
                sub_plan = sub_plan,
                sub_plan_name = sub_plan_name,
                cumulative_months = cumulative_months,
                streak_months = streak_months,
                recipient_id = recipient_id,
                recipient_user_name = recipient_user_name,
                recipient_display_name = recipient_display_name,
                mass_gift_count = mass_gift_count,
                sender_total_gifts = sender_total_gifts,
                raid_viewer_count = raid_viewer_count,
                raid_display_name = raid_display_name,
                announcement_color = announcement_color,
                milestone_category = milestone_category,
                milestone_value = milestone_value,
                milestone_reward = milestone_reward,
                notice_params = notice_params != null ? new Dictionary<string, string>(notice_params) : null
            };
        }
    }

    public class ChatBroadcastEvent
    {
        public string _id { get; set; }
        public string created_at { get; set; }
        public double content_offset_seconds { get; set; }
        public string source { get; set; }
        public string event_type { get; set; }
        public bool carried_over { get; set; }
        public System.Text.Json.JsonElement data { get; set; }
    }

    [DebuggerDisplay("{commenter} {message}")]
    public class Comment
    {
        public string _id { get; set; }
        public DateTime created_at { get; set; }
        public string channel_id { get; set; }
        public string content_type { get; set; }
        public string content_id { get; set; }
        public double content_offset_seconds { get; set; }
        public Commenter commenter { get; set; }
        public string source { get; set; }
        public string state { get; set; }
        public int bits_spent { get; set; }
        public Message message { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, string> user_notice_params { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string event_type { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public CommentEventData event_data { get; set; }

        // Runtime rendering metadata (not serialized to JSON)
        [JsonIgnore]
        public double? ModeratedAtSeconds { get; set; }
        [JsonIgnore]
        public bool IsSyntheticNotice { get; set; }
        [JsonIgnore]
        public bool IsSyntheticCompactNotice { get; set; }
        [JsonIgnore]
        public uint? SyntheticAccentColor { get; set; }

        public Comment Clone()
        {
            return new Comment()
            {
                _id = _id,
                created_at = created_at,
                channel_id = channel_id,
                content_type = content_type,
                content_id = content_id,
                content_offset_seconds = content_offset_seconds,
                commenter = commenter?.Clone(),
                source = source,
                state = state,
                bits_spent = bits_spent,
                message = message?.Clone(),
                user_notice_params = user_notice_params != null ? new Dictionary<string, string>(user_notice_params) : null,
                event_type = event_type,
                event_data = event_data?.Clone(),
                ModeratedAtSeconds = ModeratedAtSeconds,
                IsSyntheticNotice = IsSyntheticNotice,
                IsSyntheticCompactNotice = IsSyntheticCompactNotice,
                SyntheticAccentColor = SyntheticAccentColor
            };
        }
    }

    public class VideoChapter
    {
        public string id { get; set; }
        public int startMilliseconds { get; set; }
        public int lengthMilliseconds { get; set; }
        [JsonPropertyName("type")]
        public string _type { get; set; }
        public string description { get; set; }
        public string subDescription { get; set; }
        public string thumbnailUrl { get; set; }
        public string gameId { get; set; }
        public string gameDisplayName { get; set; }
        public string gameBoxArtUrl { get; set; }
    }

    public class Video
    {
        public string title { get; set; }
        public string description { get; set; }
        public string id { get; set; }
        public DateTime created_at { get; set; }
        public double start { get; set; }
        public double end { get; set; }
        public double length { get; set; } = -1;
        public int viewCount { get; set; }
        public string game { get; set; }
        public List<VideoChapter> chapters { get; set; } = new();

#region DeprecatedProperties
        [Obsolete("Used only by chats from before 8d521f7a78222bec187b56c3c747909d240add21.")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string duration { get; set; } = null;
        [Obsolete("Used only by chats from before 8d521f7a78222bec187b56c3c747909d240add21.")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string user_id { get; set; } = null;
        [Obsolete("Used only by chats from before 8d521f7a78222bec187b56c3c747909d240add21.")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string user_name { get; set; } = null;
#endregion
    }

    [DebuggerDisplay("{name}")]
    public class EmbedEmoteData
    {
        public string id { get; set; }
        public int imageScale { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public byte[] data { get; set; }
        public string name { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string url { get; set; }
        public int width { get; set; }
        public int height { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? isZeroWidth { get; set; }
    }

    [DebuggerDisplay("{name}")]
    public class EmbedChatBadge
    {
        public string name { get; set; }
        public Dictionary<string, ChatBadgeData> versions { get; set; }
    }

    [DebuggerDisplay("{name}")]
    public class LegacyEmbedChatBadge
    {
        public string name { get; set; }
        public Dictionary<string, byte[]> versions { get; set; }
    }

    [DebuggerDisplay("{prefix}")]
    public class EmbedCheerEmote
    {
        public string prefix { get; set; }
        public Dictionary<int, EmbedEmoteData> tierList { get; set; }
    }

    public class EmbeddedData
    {
        public List<EmbedEmoteData> thirdParty { get; set; } = new();
        public List<EmbedEmoteData> firstParty { get; set; } = new();
        public List<EmbedChatBadge> twitchBadges { get; set; } = new();
        public List<EmbedCheerEmote> twitchBits { get; set; } = new();
        /// <summary>GIFs posted in chat, keyed by <see cref="EmbedEmoteData.name"/> holding the Giphy title.</summary>
        public List<EmbedEmoteData> gifs { get; set; } = new();
    }

    public class LegacyEmbeddedData
    {
        public List<EmbedEmoteData> thirdParty { get; set; } = new();
        public List<EmbedEmoteData> firstParty { get; set; } = new();
        public List<LegacyEmbedChatBadge> twitchBadges { get; set; } = new();
        public List<EmbedCheerEmote> twitchBits { get; set; } = new();
    }

    public class ChatRoot
    {
        public ChatRootInfo FileInfo { get; set; } = new();
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string schemaVersion { get; set; }
        public Streamer streamer { get; set; }
        public Clipper clipper { get; set; }
        public Video video { get; set; }
        public List<Comment> comments { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ChatBroadcastEvent> events { get; set; } = new();
        public EmbeddedData embeddedData { get; set; }
    }
}
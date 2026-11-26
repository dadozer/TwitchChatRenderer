using SkiaSharp;
using System.IO.Compression;
using System.Text;
using TwitchDownloaderCore.Chat;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Rendering;

namespace TwitchDownloaderCore.Tests.ModelTests
{
    public class Wave3RenderOptimizationTests
    {
        [Fact]
        public async Task StreamingDeserializeAsync_SupportsModern_Gzip_Utf8Bom_LegacyBadges_AndV1Emotes()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ChatRenderWave3Tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                byte[] sampleBadgeBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
                string b64Badge = Convert.ToBase64String(sampleBadgeBytes);

                // 1. Modern v1.4.0 JSON with object ChatBadgeData, trailing commas, and UTF-8 BOM
                string modernJson = $$"""
                {
                  "FileInfo": { "Version": { "Major": 1, "Minor": 4, "Patch": 0 } },
                  "streamer": { "name": "TestBroadcaster", "login": "testbroadcaster", "id": 12345 },
                  "video": { "start": 0, "end": 60, "length": 60 },
                  "comments": [
                    {
                      "_id": "c1",
                      "content_offset_seconds": 12.5,
                      "commenter": { "display_name": "ChatterOne", "name": "chatterone", "_id": "99" },
                      "message": { "body": "PogChamp hello!", "bits_spent": "0", },
                    }
                  ],
                  "embeddedData": {
                    "twitchBadges": [
                      {
                        "name": "broadcaster",
                        "versions": {
                          "1": { "title": "Broadcaster", "description": "Channel Broadcaster", "bytes": "{{b64Badge}}" }
                        }
                      }
                    ]
                  }
                }
                """;

                string bomPath = Path.Combine(tempDir, "modern_bom.json");
                await File.WriteAllTextAsync(bomPath, modernJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

                var modernRoot = await ChatJson.DeserializeAsync(bomPath, getComments: true, onlyFirstAndLastComments: false, getEmbeds: true);
                Assert.Single(modernRoot.comments);
                Assert.Equal("ChatterOne", modernRoot.comments[0].commenter.display_name);
                Assert.Single(modernRoot.embeddedData.twitchBadges);
                Assert.Equal(sampleBadgeBytes, modernRoot.embeddedData.twitchBadges[0].versions["1"].bytes);
                Assert.Equal("Broadcaster", modernRoot.embeddedData.twitchBadges[0].versions["1"].title);

                // 2. GZip-compressed .json.gz streaming load
                string gzPath = Path.Combine(tempDir, "modern.json.gz");
                await using (var fs = File.Create(gzPath))
                await using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                {
                    byte[] utf8 = Encoding.UTF8.GetBytes(modernJson);
                    await gz.WriteAsync(utf8);
                }

                var gzRoot = await ChatJson.DeserializeAsync(gzPath, getComments: true, onlyFirstAndLastComments: false, getEmbeds: true);
                Assert.Single(gzRoot.comments);
                Assert.Equal(sampleBadgeBytes, gzRoot.embeddedData.twitchBadges[0].versions["1"].bytes);

                // 3. Legacy v1.2.0 schema where twitchBadges.versions maps version keys directly to Base64 byte[] strings
                string legacyBadgeJson = $$"""
                {
                  "FileInfo": { "Version": { "Major": 1, "Minor": 2, "Patch": 0 } },
                  "streamer": { "name": "OldBroadcaster", "id": 42 },
                  "video": { "start": 0, "end": 30 },
                  "comments": [
                    { "_id": "legacy1", "content_offset_seconds": 5, "commenter": { "display_name": "OldViewer" }, "message": { "body": "hi" } }
                  ],
                  "embeddedData": {
                    "twitchBadges": [
                      {
                        "name": "subscriber",
                        "versions": {
                          "0": "{{b64Badge}}"
                        }
                      }
                    ]
                  }
                }
                """;

                string legacyPath = Path.Combine(tempDir, "legacy_v120.json");
                await File.WriteAllTextAsync(legacyPath, legacyBadgeJson, new UTF8Encoding(false));

                var legacyRoot = await ChatJson.DeserializeAsync(legacyPath, getComments: true, onlyFirstAndLastComments: false, getEmbeds: true);
                Assert.Single(legacyRoot.embeddedData.twitchBadges);
                Assert.Equal(sampleBadgeBytes, legacyRoot.embeddedData.twitchBadges[0].versions["0"].bytes);

                // 4. Legacy v1.0 schema with top-level "emotes" instead of "embeddedData", plus clean round-trip serialization
                string v1Json = $$"""
                {
                  "streamer": { "name": "V1Streamer", "id": 7 },
                  "video": { "start": 0, "end": 10 },
                  "comments": [],
                  "emotes": {
                    "firstParty": [
                      { "id": "25", "imageScale": 1, "name": "Kappa", "width": 28, "height": 28, "data": "{{b64Badge}}" }
                    ]
                  }
                }
                """;

                string v1Path = Path.Combine(tempDir, "legacy_v100.json");
                await File.WriteAllTextAsync(v1Path, v1Json, new UTF8Encoding(false));

                var v1Root = await ChatJson.DeserializeAsync(v1Path, getComments: true, onlyFirstAndLastComments: false, getEmbeds: true);
                Assert.NotNull(v1Root.embeddedData);
                Assert.Single(v1Root.embeddedData.firstParty);
                Assert.Equal("Kappa", v1Root.embeddedData.firstParty[0].name);

                using var outMs = new MemoryStream();
                await ChatJson.SerializeAsync(outMs, v1Root, CancellationToken.None);
                string serialized = Encoding.UTF8.GetString(outMs.ToArray());
                Assert.DoesNotContain("\"emotes\":", serialized);
                Assert.Contains("\"embeddedData\":", serialized);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [Theory]
        [InlineData(EmojiVendor.TwitterTwemoji)]
        [InlineData(EmojiVendor.GoogleNotoColor)]
        public void OpenInMemoryEmojiArchive_IndexesAndDecodesWithoutTempFiles(EmojiVendor vendor)
        {
            var (archive, entries) = AssetCatalog.OpenInMemoryEmojiArchive(vendor);
            using (archive)
            {
                Assert.NotNull(archive);
                Assert.True(entries.Count >= vendor.EmojiCount(), $"Expected at least {vendor.EmojiCount()} emojis, got {entries.Count}.");

                // U+1F600 GRINNING FACE must exist and decode cleanly directly from its in-memory ZipArchiveEntry
                Assert.True(entries.TryGetValue("\U0001F600", out var grinningEntry));
                using var stream = grinningEntry.Open();
                using var bitmap = SKBitmap.Decode(stream);
                Assert.NotNull(bitmap);
                Assert.Equal(72, bitmap.Width);
                Assert.Equal(72, bitmap.Height);
            }
        }

        [Fact]
        public void NeedsRtlWordReordering_SkipsArrayPoolOnLtrCjkAndEmoji_DetectsHebrewAndArabic()
        {
            Assert.False(TextLayoutEngine.NeedsRtlWordReordering("Hello world PogChamp 123!".AsSpan()));
            Assert.False(TextLayoutEngine.NeedsRtlWordReordering("日本語テスト 한국어 中文 \U0001F680".AsSpan()));
            Assert.True(TextLayoutEngine.NeedsRtlWordReordering("שלום עולם".AsSpan()));
            Assert.True(TextLayoutEngine.NeedsRtlWordReordering("مرحبا بالعالم".AsSpan()));
        }

        [Fact]
        public void ResolveAndCacheUsername_CachesStringAndVisibilityAdjustedColorAcrossRepeatMessages()
        {
            var options = new ChatRenderOptions
            {
                BackgroundColor = new SKColor(0xFF18181B),
                MessageColor = SKColors.White,
                Outline = false,
                AdjustUsernameVisibility = true,
                ChatWidth = 350,
                ChatHeight = 600,
                FontSize = 24
            };

            using var layout = new TextLayoutEngine(options);
            using var outlinePaint = new SKPaint { Color = SKColors.Black };
            var cache = new Dictionary<UsernameCacheKey, ResolvedUsernameEntry>();

            var first = layout.ResolveAndCacheUsername("RepeatChatter", "#000033", options.BackgroundColor, appendColon: true, outlinePaint, cache);
            var second = layout.ResolveAndCacheUsername("RepeatChatter", "#000033", options.BackgroundColor, appendColon: true, outlinePaint, cache);

            Assert.Single(cache);
            Assert.Equal("RepeatChatter:", first.FormattedLabel);
            Assert.Same(first.FormattedLabel, second.FormattedLabel);
            Assert.Equal(-1, first.FirstNonAsciiCodepoint);
            Assert.Equal(first.Color, second.Color);

            // Very dark #000033 on dark #18181B background must be lightened by AdjustUsernameVisibility
            first.Color.ToHsl(out _, out _, out float adjustedLightness);
            Assert.True(adjustedLightness >= 39.5f);

            // Non-ASCII username records first non-ASCII codepoint for instant fallback font lookup
            var cjk = layout.ResolveAndCacheUsername("視聴者名", "#FF69B4", options.BackgroundColor, appendColon: false, outlinePaint, cache);
            Assert.Equal("視聴者名", cjk.FormattedLabel);
            Assert.Equal('視', cjk.FirstNonAsciiCodepoint);
        }

        [Fact]
        public async Task StreamingDeserializeAsync_LoadsBroadcastEvents_SupportsTypeAliasAndNullableCarriedOver_AndRendersStickyOverlay()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ChatRenderStickyEventsTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                string eventsJson = """
                {
                  "schemaVersion": "2.0-extended",
                  "streamer": { "name": "TestBroadcaster", "login": "testbroadcaster", "id": 12345 },
                  "video": { "start": 0, "end": 120, "length": 120 },
                  "comments": [],
                  "events": [
                    {
                      "_id": "ev-pred-1",
                      "content_offset_seconds": 10.0,
                      "event_type": "prediction",
                      "carried_over": null,
                      "data": {
                        "prediction_id": "pred-1",
                        "title": "Will they beat the boss?",
                        "status": "ACTIVE",
                        "prediction_window_seconds": 120,
                        "outcomes": [
                          { "id": "o1", "title": "Yes", "color": "BLUE", "total_points": 5000, "total_users": 10 },
                          { "id": "o2", "title": "No", "color": "PINK", "total_points": 2500, "total_users": 5 }
                        ]
                      }
                    },
                    {
                      "_id": "ev-poll-1",
                      "content_offset_seconds": 12.0,
                      "type": "poll",
                      "data": {
                        "poll_id": "poll-1",
                        "title": "Next game?",
                        "status": "ACTIVE",
                        "duration_seconds": 60,
                        "total_voters": 20,
                        "choices": [
                          { "choice_id": "c1", "title": "Zelda", "total_voters": 14 },
                          { "choice_id": "c2", "title": "Mario", "total_voters": 6 }
                        ]
                      }
                    },
                    {
                      "_id": "ev-pin-1",
                      "content_offset_seconds": 15.0,
                      "event_type": "PINNED_CHAT",
                      "data": {
                        "action": "pin-message",
                        "pin_id": "pin-1",
                        "message_text": "Welcome to the run!",
                        "sender_display_name": "ModUser",
                        "sender_color": "#00FF7F"
                      }
                    }
                  ]
                }
                """;

                string jsonPath = Path.Combine(tempDir, "sticky_events.json");
                await File.WriteAllTextAsync(jsonPath, eventsJson, new UTF8Encoding(false));

                var root = await ChatJson.DeserializeAsync(jsonPath, getComments: true, onlyFirstAndLastComments: false, getEmbeds: true);
                Assert.Equal(3, root.events.Count);
                Assert.Equal("prediction", root.events[0].event_type);
                Assert.Null(root.events[0].carried_over);
                Assert.Equal("poll", root.events[1].event_type);
                Assert.Equal("PINNED_CHAT", root.events[2].event_type);

                using (var outMs = new MemoryStream())
                {
                    await ChatJson.SerializeAsync(outMs, root, CancellationToken.None);
                    string serialized = Encoding.UTF8.GetString(outMs.ToArray());
                    Assert.Contains("\"event_type\":\"poll\"", serialized);
                    Assert.DoesNotContain("\"type\":\"poll\"", serialized);
                }

                var options = new ChatRenderOptions
                {
                    ChatWidth = 640,
                    ChatHeight = 1008,
                    BackgroundColor = new SKColor(0x33000000),
                    MessageColor = SKColors.White,
                    Font = "Inter Embedded",
                    FontSize = 24,
                    PredictionMode = OverlayWidgetMode.StickyTop,
                    PollMode = OverlayWidgetMode.StickyTop,
                    PinnedChatMode = OverlayWidgetMode.StickyTop
                };

                var timeline = TwitchDownloaderCore.Chat.Timeline.TimelineCompiler.Compile(root, options);
                Assert.Equal(3, timeline.StickyEvents.Count);
                Assert.IsType<TwitchDownloaderCore.Chat.Timeline.PredictionOverlayEvent>(timeline.StickyEvents[0]);
                Assert.IsType<TwitchDownloaderCore.Chat.Timeline.PollOverlayEvent>(timeline.StickyEvents[1]);
                Assert.IsType<TwitchDownloaderCore.Chat.Timeline.PinnedChatOverlayEvent>(timeline.StickyEvents[2]);

                var tracker = new StickyOverlayTracker(timeline.StickyEvents, options);
                tracker.AdvanceTo(20.0);
                Assert.True(tracker.HasAnyActive);
                Assert.NotNull(tracker.Prediction);
                Assert.NotNull(tracker.Poll);
                Assert.NotNull(tracker.PinnedChat);

                using var layout = new TextLayoutEngine(options);
                using var assets = new AssetCatalog(options, tempDir, layout.BaseOutlinePaint);
                var stickyRenderer = new StickyOverlayRenderer(options, assets, layout);

                using var bitmap = new SKBitmap(options.ChatWidth, options.ChatHeight);
                using var canvas = new SKCanvas(bitmap);
                canvas.Clear(options.BackgroundColor);

                int stickyHeight = stickyRenderer.DrawStickyTopOverlays(canvas, tracker, 20.0, layout.GetWorkerContext());
                canvas.Flush();

                Assert.True(stickyHeight > 0);
                Assert.Equal(0, stickyHeight & 1);
                // Top sticky card paints an opaque/near-opaque card over the translucent background
                Assert.NotEqual(options.BackgroundColor, bitmap.GetPixel(options.ChatWidth / 2, 10));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }
    }
}

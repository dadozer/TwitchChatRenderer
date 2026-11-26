using SkiaSharp;
using System.Text.Json;
using TwitchDownloaderCore.Chat.Timeline;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Rendering;
using TwitchDownloaderCore.Rendering.Encoding;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.Tests.ModelTests
{
    public class PipelineArchitectureTests
    {
        private const string PNG_2X2 = "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVR4nGP8z8Dwn4GBgYGJAQoAHxcCAk+Uzr4AAAAASUVORK5CYII=";

        [Fact]
        public void TimelineCompiler_DoesNotMutateInputChatRoot_PairsModeration_AndSplitsSubCustomMessages()
        {
            var root = new ChatRoot
            {
                streamer = new Streamer { id = 100, name = "ChannelHost", login = "channelhost" },
                video = new Video { start = 0, end = 60, length = 60 },
                comments =
                [
                    new Comment
                    {
                        _id = "msg-1",
                        content_offset_seconds = 10.75,
                        commenter = new Commenter { _id = "201", name = "alice", display_name = "Alice" },
                        message = new Message
                        {
                            body = "hello chat!",
                            fragments = [new Fragment { text = "hello chat!" }]
                        }
                    },
                    new Comment
                    {
                        _id = "sub-1",
                        content_offset_seconds = 20.4,
                        event_type = "usernotice",
                        commenter = new Commenter { _id = "202", name = "bob", display_name = "Bob" },
                        message = new Message
                        {
                            body = "love the stream!",
                            fragments = [new Fragment { text = "love the stream!" }]
                        },
                        event_data = new CommentEventData
                        {
                            notice_type = "resub",
                            system_message = "Bob subscribed at Tier 1. They've subscribed for 6 months!",
                            user_message = "love the stream!",
                            sub_plan = "1000",
                            cumulative_months = 6
                        }
                    }
                ],
                events =
                [
                    new ChatBroadcastEvent
                    {
                        _id = "ev-mod-1",
                        event_type = "clearmsg",
                        content_offset_seconds = 15.2,
                        data = JsonDocument.Parse("""{"target_msg_id":"msg-1","target_user_login":"alice"}""").RootElement.Clone()
                    },
                    new ChatBroadcastEvent
                    {
                        _id = "ev-room-base",
                        event_type = "roomstate",
                        content_offset_seconds = 0,
                        data = JsonDocument.Parse("""{"slow_seconds":0,"subs_only":false}""").RootElement.Clone()
                    },
                    new ChatBroadcastEvent
                    {
                        _id = "ev-room-slow",
                        event_type = "roomstate",
                        content_offset_seconds = 25.9,
                        data = JsonDocument.Parse("""{"slow_seconds":30,"subs_only":false}""").RootElement.Clone()
                    }
                ]
            };

            var options = new ChatRenderOptions
            {
                UpdateRate = 0.5,
                Framerate = 60,
                DisperseCommentOffsets = false,
                SubMessages = true,
                ModerationMode = ModerationDisplayMode.Strikethrough,
                ShowModerationNotices = true,
                ShowRoomstateNotices = true
            };

            var timeline = TimelineCompiler.Compile(root, options);

            // 1. Original ChatRoot comments and fractional offsets must remain completely untouched
            Assert.Equal(2, root.comments.Count);
            Assert.Equal(10.75, root.comments[0].content_offset_seconds);
            Assert.Equal("hello chat!", root.comments[0].message.body);
            Assert.Equal(20.4, root.comments[1].content_offset_seconds);

            // 2. Compiled timeline has floored offsets, paired ModeratedAtSeconds, custom sub content, mod notice, and roomstate transition
            var msg1 = Assert.Single(timeline.Messages, m => m.Id == "msg-1");
            Assert.Equal(10.5, msg1.AppearSeconds);
            Assert.Equal(15.0, msg1.ModeratedAtSeconds);

            var resubMsg = Assert.Single(timeline.Messages, m => m.Notice.Kind == TimelineNoticeKind.ExtendedUserNotice && m.Notice.NoticeType == "resub");
            Assert.NotNull(resubMsg.Notice.CustomUserContent);
            Assert.Equal("love the stream!", resubMsg.Notice.CustomUserContent!.Body);

            Assert.Contains(timeline.Messages, m => m.Notice.Kind == TimelineNoticeKind.CompactSystemNotice && m.Content.Body.Contains("alice"));
            Assert.Contains(timeline.Messages, m => m.Notice.Kind == TimelineNoticeKind.CompactSystemNotice && m.Content.Body.Contains("30"));
        }

        [Fact]
        public void FfmpegPipelineBuilder_SharpeningComposesIntoVfWithoutBreakingPlanarYuvOrNutPipe()
        {
            var options = new ChatRenderOptions
            {
                ChatWidth = 420,
                ChatHeight = 1080,
                Framerate = 60,
                Sharpening = true,
                InputArgs = FfmpegCodecPresets.StandardRawVideoInputArgs,
                OutputArgs = FfmpegCodecPresets.Vp9.OutputArgs
            };

            var mainPlan = FfmpegPipelineBuilder.BuildEncodingPlan(
                options,
                savePath: "/tmp/out.webm",
                isMask: false,
                parallelJobs: 2);

            Assert.Equal(PipePixelFormat.Yuva420p, mainPlan.PipeFormat);
            Assert.Equal("yuva420p", mainPlan.PipePixFmt);
            Assert.True(mainPlan.UseNutPipe);
            Assert.Contains("-f nut", mainPlan.InputArgs);
            Assert.DoesNotContain("-filter_complex", mainPlan.InputArgs);
            Assert.Contains("smartblur=lr=1:ls=-1.0,fps=60", mainPlan.OutputArgs);

            // Mask stream must omit smartblur while also preserving planar yuv420p + NUT VFR piping
            var maskPlan = FfmpegPipelineBuilder.BuildEncodingPlan(
                options,
                savePath: "/tmp/out_mask.webm",
                isMask: true,
                parallelJobs: 2);

            Assert.Equal("gray", maskPlan.PipePixFmt);
            Assert.True(maskPlan.UseNutPipe);
            Assert.DoesNotContain("smartblur", maskPlan.OutputArgs);
            Assert.Contains("-vf format=yuva420p,fps=60", maskPlan.OutputArgs);
        }

        [Fact]
        public void EmoteSurfaceCache_RetainsMultipleRowBackgroundVariants_AndInvalidatesOnReleaseFrames()
        {
            bool isBgra = SKImageInfo.PlatformColorType == SKColorType.Bgra8888;
            uint standardBg = SimdPixelConverter.SampleClearedBackgroundPixel(new SKColor(18, 18, 20, 255));
            uint alternateBg = SimdPixelConverter.SampleClearedBackgroundPixel(new SKColor(32, 32, 38, 255));
            uint highlightBg = SimdPixelConverter.SampleClearedBackgroundPixel(new SKColor(90, 24, 52, 255));

            using var emote = new TwitchEmote(Convert.FromBase64String(PNG_2X2), null, EmoteProvider.ThirdParty, 1, "e-multi", "MultiBgEmote");
            emote.Scale(4.0);

            using var cache = new EmoteSurfaceCache();

            var stdFrames = cache.GetOrCreateYuvFrames(emote, standardBg, includeAlpha: false, isBgra);
            var altFrames = cache.GetOrCreateYuvFrames(emote, alternateBg, includeAlpha: false, isBgra);
            var hlFrames = cache.GetOrCreateYuvFrames(emote, highlightBg, includeAlpha: false, isBgra);

            // All 3 row backgrounds must coexist simultaneously with zero cache thrashing
            Assert.Equal(3, cache.CachedVariantCount);
            Assert.Same(stdFrames, cache.GetOrCreateYuvFrames(emote, standardBg, includeAlpha: false, isBgra));
            Assert.Same(altFrames, cache.GetOrCreateYuvFrames(emote, alternateBg, includeAlpha: false, isBgra));
            Assert.Same(hlFrames, cache.GetOrCreateYuvFrames(emote, highlightBg, includeAlpha: false, isBgra));

            // Releasing decoded frames (LRU eviction) advances DecodeGeneration and regenerates fresh surfaces cleanly
            int genBefore = emote.DecodeGeneration;
            emote.ReleaseFrames();
            Assert.True(emote.DecodeGeneration > genBefore);

            var rebuiltStd = cache.GetOrCreateYuvFrames(emote, standardBg, includeAlpha: false, isBgra);
            Assert.NotSame(stdFrames, rebuiltStd);
            Assert.Equal(stdFrames[0].Planes, rebuiltStd[0].Planes);
        }

        [Fact]
        public void StickyOverlayTracker_PreservesHypeTrainStateAcrossDeltaUpdates_SkipsCarriedOverEndedPoll_AndExpiresAfterLinger()
        {
            var events = new List<StickyOverlayEvent>
            {
                // Carried-over ended poll at t=0 must be skipped
                new PollOverlayEvent(
                    OffsetSeconds: 0,
                    CarriedOver: true,
                    PollId: "poll-old",
                    PubsubType: "poll-complete",
                    Status: "COMPLETED",
                    Title: "Old Poll",
                    DurationSeconds: 60,
                    TotalVoters: 100,
                    Choices: [new StickyPollChoice("c1", "Yes", 100)]),
                new HypeTrainOverlayEvent(
                    OffsetSeconds: 10,
                    CarriedOver: false,
                    PubsubType: "hype-train-start",
                    RawLevel: 2,
                    RawGoal: 1000,
                    RawTotal: 1500,
                    RawProgress: 450),
                new HypeTrainOverlayEvent(
                    OffsetSeconds: 14,
                    CarriedOver: false,
                    PubsubType: "hype-train-conductor-update",
                    RawLevel: null,
                    RawGoal: null,
                    RawTotal: null,
                    RawProgress: null),
                new HypeTrainOverlayEvent(
                    OffsetSeconds: 20,
                    CarriedOver: false,
                    PubsubType: "hype-train-end",
                    RawLevel: null,
                    RawGoal: 0,
                    RawTotal: 0,
                    RawProgress: 0)
            };

            var options = new ChatRenderOptions
            {
                PollMode = OverlayWidgetMode.StickyTop,
                HypeTrainMode = OverlayWidgetMode.StickyTop
            };

            var tracker = new StickyOverlayTracker(events, options);

            tracker.AdvanceTo(5.0);
            Assert.False(tracker.HasAnyActive);
            Assert.Null(tracker.Poll);

            tracker.AdvanceTo(15.0);
            Assert.True(tracker.HasAnyActive);
            Assert.NotNull(tracker.HypeTrain);
            Assert.Equal(2, tracker.HypeTrain!.Level);
            // Delta preservation: Goal/Total/Progress from t=10 remain intact across conductor delta at t=14
            Assert.Equal(1000, tracker.HypeTrain.Goal);
            Assert.Equal(1500, tracker.HypeTrain.Total);
            Assert.Equal(450, tracker.HypeTrain.Progress);

            // At t=25 (within 15s resolution linger window of t=20 end), widget is still visible with preserved progress
            tracker.AdvanceTo(25.0);
            Assert.True(tracker.HasAnyActive);
            Assert.NotNull(tracker.HypeTrain);
            Assert.True(tracker.HypeTrain!.IsEnded);
            Assert.Equal(450, tracker.HypeTrain.Progress);

            // At t=36 (> 20 + 15s linger), widget expires cleanly
            tracker.AdvanceTo(36.0);
            Assert.False(tracker.HasAnyActive);
        }

        [Fact]
        public void ChatPreviewRenderer_RendersProductionPipelineWithoutMutatingCallerOptions()
        {
            var callerOptions = new ChatRenderOptions
            {
                ChatWidth = 1920,
                ChatHeight = 1080,
                BackgroundColor = new SKColor(18, 18, 22, 180),
                AlternateBackgroundColor = new SKColor(30, 30, 38, 200),
                AlternateMessageBackgrounds = true,
                MessageColor = SKColors.White,
                HighlightUsersArray = ["nightbot"],
                HighlightUserColor = new SKColor(120, 20, 60, 220),
                Font = "Inter Embedded",
                FontSize = 18,
                ChatBadges = true,
                RenderUserAvatars = true,
                SubMessages = true,
                Timestamp = true,
                Outline = true,
                OutlineSize = 4
            };

            using var preview = ChatPreviewRenderer.RenderPreviewBitmap(callerOptions, 240, 185);

            Assert.Equal(240, preview.Width);
            Assert.Equal(185, preview.Height);
            // Caller's production render dimensions must not be mutated by preview rendering
            Assert.Equal(1920, callerOptions.ChatWidth);
            Assert.Equal(1080, callerOptions.ChatHeight);

            // Verify rendered bitmap has rich non-uniform pixel content (checkerboard + text + badges + avatars + emote)
            var distinctColors = new HashSet<uint>();
            var pixels = preview.Pixels;
            for (int i = 0; i < pixels.Length; i += 7)
            {
                distinctColors.Add((uint)pixels[i]);
            }
            Assert.True(distinctColors.Count > 12, $"Expected rich multi-color preview output, got {distinctColors.Count} distinct colors.");
        }

        [Fact]
        public void CanonicalTimeline_GetTickRange_SupportsIndependentStartAndEndOverrides()
        {
            var timeline = new CanonicalTimeline(
                new StreamerIdentity("Host", "host", 1),
                new VideoTimelineBounds("1", "Test VOD", DateTime.UtcNow, StartSeconds: 10.0, EndSeconds: 100.0, LengthSeconds: 90.0),
                [],
                []);

            // Neither override set -> falls back to video.start..video.end
            var defaultRange = timeline.GetTickRange(new ChatRenderOptions { Framerate = 60, StartOverride = -1, EndOverride = -1 });
            Assert.Equal((600, 6000, 5400), defaultRange);

            // Standalone -e without -b -> uses video.start to EndOverride
            var endOnlyRange = timeline.GetTickRange(new ChatRenderOptions { Framerate = 60, StartOverride = -1, EndOverride = 250 });
            Assert.Equal((600, 15000, 14400), endOnlyRange);

            // Standalone -b without -e -> uses StartOverride to video.end
            var startOnlyRange = timeline.GetTickRange(new ChatRenderOptions { Framerate = 60, StartOverride = 30, EndOverride = -1 });
            Assert.Equal((1800, 6000, 4200), startOnlyRange);

            // Both -b and -e set
            var bothRange = timeline.GetTickRange(new ChatRenderOptions { Framerate = 60, StartOverride = 20, EndOverride = 50 });
            Assert.Equal((1200, 3000, 1800), bothRange);
        }

        [Fact]
        public void StickyOverlayRenderer_TreatsResolvePendingPredictionAsLocked()
        {
            var options = new ChatRenderOptions
            {
                ChatWidth = 640,
                ChatHeight = 1008,
                BackgroundColor = new SKColor(0x33000000),
                MessageColor = SKColors.White,
                Font = "Inter Embedded",
                FontSize = 24,
                PredictionMode = OverlayWidgetMode.StickyTop
            };

            StickyPredictionOutcome[] outcomes =
            [
                new StickyPredictionOutcome("o1", "Yes", "BLUE", 1000, 2),
                new StickyPredictionOutcome("o2", "No", "PINK", 500, 1)
            ];

            var lockedTracker = new StickyOverlayTracker(
                [new PredictionOverlayEvent(10, false, "p1", "Test Pred", "LOCKED", 120, null, outcomes)],
                options);
            lockedTracker.AdvanceTo(15);

            var pendingTracker = new StickyOverlayTracker(
                [new PredictionOverlayEvent(10, false, "p1", "Test Pred", "RESOLVE_PENDING", 120, null, outcomes)],
                options);
            pendingTracker.AdvanceTo(15);

            string tempDir = Path.Combine(Path.GetTempPath(), "ChatRenderPendingPredTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                using var layout = new TextLayoutEngine(options);
                using var assets = new AssetCatalog(options, tempDir, layout.BaseOutlinePaint);
                var renderer = new StickyOverlayRenderer(options, assets, layout);

                using var lockedBmp = new SKBitmap(options.ChatWidth, options.ChatHeight);
                using var lockedCanvas = new SKCanvas(lockedBmp);
                lockedCanvas.Clear(options.BackgroundColor);
                int lockedHeight = renderer.DrawStickyTopOverlays(lockedCanvas, lockedTracker, 15, layout.GetWorkerContext());
                lockedCanvas.Flush();

                using var pendingBmp = new SKBitmap(options.ChatWidth, options.ChatHeight);
                using var pendingCanvas = new SKCanvas(pendingBmp);
                pendingCanvas.Clear(options.BackgroundColor);
                int pendingHeight = renderer.DrawStickyTopOverlays(pendingCanvas, pendingTracker, 15, layout.GetWorkerContext());
                pendingCanvas.Flush();

                Assert.True(lockedHeight > 0);
                Assert.Equal(lockedHeight, pendingHeight);
                Assert.Equal(lockedBmp.GetPixelSpan().ToArray(), pendingBmp.GetPixelSpan().ToArray());
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

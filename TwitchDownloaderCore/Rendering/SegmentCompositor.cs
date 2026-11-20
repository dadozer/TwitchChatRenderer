#nullable enable
using SkiaSharp;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using TwitchDownloaderCore.Chat.Timeline;
using TwitchDownloaderCore.Interfaces;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Models.Render;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Rendering.Encoding;
using TwitchDownloaderCore.Tools;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.Rendering
{
    internal struct AnimEmoteSlot
    {
        public TwitchEmote Emote;
        public int DrawX;
        public int DrawY;
        public int Width;
        public int Height;
        public int LastFrameIndex;
        public bool CanDirectYuvBlit;
        public uint BgPixel;
    }

    internal sealed class RenderWorkerState : IDisposable
    {
        public required int WorkerId { get; init; }
        public required int TotalWorkers { get; init; }
        public required int MaxCommentIndex { get; init; }
        public required SectionImageCache SectionImageCache { get; init; }
        public required StickyOverlayTracker StickyTracker { get; init; }
        public ConcurrentDictionary<int, CommentSection?> PreRenderedSections { get; } = new();
        public int ConsumedCommentIndex = -1;
        public int InitialTargetIndex = -1;
        public int ProducerNextIndex = 0;
        public AutoResetEvent LookaheadWakeSignal { get; } = new(initialState: true);
        public CancellationTokenSource? LookaheadCts { get; set; }
        public Task? LookaheadTask { get; set; }
        public HashSet<TwitchEmote> OnScreenEmoteSet { get; } = [];
        public SKBitmap? AnimComposedFrame { get; set; }
        public SKCanvas? AnimCanvas { get; set; }
        public int AnimByteSize { get; set; }
        public bool HasValidAnimComposite { get; set; }
        public long AnimComposedVersion { get; set; } = -1;
        public long AnimBaseUpdateVersion { get; set; } = -1;
        public long AnimNextChangeTickMs { get; set; } = -1;
        public long AnimLastTickMs { get; set; } = -1;
        public long FrameVersionCounter { get; set; }
        public List<AnimEmoteSlot> AnimSlots { get; } = [];
        public List<DirtyRect> AnimDirtyRects { get; } = [];
        public List<DirectYuvEmoteBlit> AnimDirectYuvBlits { get; } = [];

        public void InvalidateAnimCache()
        {
            HasValidAnimComposite = false;
            AnimBaseUpdateVersion = -1;
            AnimNextChangeTickMs = -1;
            AnimLastTickMs = -1;
            AnimSlots.Clear();
            AnimDirtyRects.Clear();
            AnimDirectYuvBlits.Clear();
        }

        public void StopLookahead()
        {
            try { LookaheadCts?.Cancel(); } catch { /* ignore */ }
            try { LookaheadWakeSignal.Set(); } catch { /* ignore */ }

            if (LookaheadTask is not null)
            {
                try { LookaheadTask.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { /* expected on cancel */ }
                catch { /* ignore background teardown errors */ }
                LookaheadTask = null;
            }
        }

        public void Dispose()
        {
            StopLookahead();

            foreach (var kvp in PreRenderedSections)
            {
                kvp.Value?.Release(SectionImageCache);
            }
            PreRenderedSections.Clear();
            try { LookaheadCts?.Dispose(); } catch { /* ignore */ }
            try { LookaheadWakeSignal.Dispose(); } catch { /* ignore */ }
            AnimCanvas?.Dispose();
            AnimComposedFrame?.Dispose();
        }
    }

    /// <summary>
    /// Unified 1-or-N segment compositor over an immutable <see cref="CanonicalTimeline"/>.
    /// Orchestrates sliding-window <see cref="CommentSection"/> lookahead production, incremental upward pixel &amp; planar YUV scrolling,
    /// <c>O(1)</c> animated emote skip scheduling, 2D dirty-rect compositing, multi-background direct planar YUV emote blitting,
    /// and coalesced frame enqueueing into FFmpeg pipe writers.
    /// </summary>
    internal sealed class SegmentCompositor
    {
        private readonly ChatRenderOptions _options;
        private readonly CanonicalTimeline _timeline;
        private readonly AssetCatalog _assets;
        private readonly TextLayoutEngine _layout;
        private readonly MessageSectionRenderer _sectionRenderer;
        private readonly StickyOverlayRenderer _stickyRenderer;
        private readonly ITaskProgress _progress;
        private readonly object _progressLock = new();
        private long _lastProgressReportMs = -1000;

        public SegmentCompositor(
            ChatRenderOptions options,
            CanonicalTimeline timeline,
            AssetCatalog assets,
            TextLayoutEngine layout,
            MessageSectionRenderer sectionRenderer,
            StickyOverlayRenderer stickyRenderer,
            ITaskProgress progress)
        {
            _options = options;
            _timeline = timeline;
            _assets = assets;
            _layout = layout;
            _sectionRenderer = sectionRenderer;
            _stickyRenderer = stickyRenderer;
            _progress = progress;
        }

        public void RenderSegment(
            int workerId,
            int totalWorkers,
            int startTick,
            int endTick,
            int totalRenderTicks,
            ref long completedTicks,
            Stopwatch stopwatch,
            FfmpegProcess ffmpegProcess,
            EncodingPlan mainPlan,
            FfmpegProcess? maskProcess,
            CancellationToken cancellationToken)
        {
            var ffmpegStream = ffmpegProcess.StandardInput.BaseStream;
            var maskStream = maskProcess?.StandardInput.BaseStream;

            DriveInfo? outputDrive = DriveHelper.GetOutputDrive(ffmpegProcess.SavePath);

            var pipeFormat = mainPlan.PipeFormat;
            var segmentTickCount = Math.Max(0, endTick - startTick);
            var pixelCount = _options.ChatWidth * _options.ChatHeight;
            var frameByteLength = pipeFormat switch
            {
                PipePixelFormat.Yuva420p => pixelCount * 5 / 2,
                PipePixelFormat.Yuv420p => pixelCount * 3 / 2,
                _ => pixelCount * 4
            };
            var maskByteLength = pixelCount;
            var hasMask = maskProcess is not null;
            var isBgra = SKImageInfo.PlatformColorType == SKColorType.Bgra8888;
            var yuvOnlyCompositing = pipeFormat != PipePixelFormat.BgraOrRgba && (!hasMask || pipeFormat == PipePixelFormat.Yuva420p);
            var expectedBgPixel = SimdPixelConverter.SampleClearedBackgroundPixel(_options.BackgroundColor);

            var messages = _timeline.Messages;
            var maxSegmentTimeSeconds = Math.Max(0, (endTick - 1) / (double)_options.Framerate);
            var maxCommentIdx = messages.Count == 0
                ? -1
                : GetNewestMessageIndex(-1, maxSegmentTimeSeconds);

            var stickyTracker = new StickyOverlayTracker(_timeline.StickyEvents, _options);
            stickyTracker.AdvanceTo(startTick / (double)_options.Framerate);

            var firstSegmentTimeSeconds = Math.Max(0, startTick / (double)_options.Framerate);
            var firstNewestIdx = messages.Count == 0
                ? -1
                : GetNewestMessageIndex(-1, firstSegmentTimeSeconds);
            var initialOldIdx = firstNewestIdx > 100 ? firstNewestIdx - 100 : -1;

            using var workerState = new RenderWorkerState
            {
                WorkerId = workerId,
                TotalWorkers = totalWorkers,
                MaxCommentIndex = maxCommentIdx,
                SectionImageCache = _assets.SectionImageCache,
                StickyTracker = stickyTracker,
                ConsumedCommentIndex = initialOldIdx,
                InitialTargetIndex = firstNewestIdx,
                ProducerNextIndex = initialOldIdx + 1
            };

            if (maxCommentIdx >= workerState.ProducerNextIndex)
            {
                workerState.LookaheadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var producerToken = workerState.LookaheadCts.Token;
                workerState.LookaheadTask = Task.Run(
                    () => RunLookaheadProducerLoop(workerState, producerToken),
                    CancellationToken.None);
            }

            var frameChannel = Channel.CreateBounded<QueuedFrame>(new BoundedChannelOptions(32)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });

            using var cancelReg = cancellationToken.Register(() =>
            {
                try { if (!ffmpegProcess.HasExited) ffmpegProcess.Kill(entireProcessTree: true); } catch { /* ignore */ }
                try { if (maskProcess is { HasExited: false }) maskProcess.Kill(entireProcessTree: true); } catch { /* ignore */ }
            });

            var pipeWriterTask = FfmpegEncodeCoordinator.StartPipeWriterTask(
                frameChannel.Reader,
                ffmpegStream,
                maskStream,
                mainPlan,
                _options,
                segmentTickCount,
                outputDrive,
                _progress,
                cancellationToken);

            UpdateFrame? latestUpdate = null;
            QueuedFrame? pendingFrame = null;
            byte[]? cachedUpdateYuvBuffer = pipeFormat != PipePixelFormat.BgraOrRgba
                ? ArrayPool<byte>.Shared.Rent(frameByteLength)
                : null;
            byte[]? cachedAnimYuvBuffer = pipeFormat != PipePixelFormat.BgraOrRgba
                ? ArrayPool<byte>.Shared.Rent(frameByteLength)
                : null;
            long cachedUpdateYuvVersion = -1;
            long cachedAnimBaseUpdateVersion = -1;

            try
            {
                for (int currentTick = startTick; currentTick < endTick; currentTick++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (latestUpdate is null || currentTick % _options.UpdateFrame == 0)
                    {
                        latestUpdate = GenerateUpdateFrame(workerState, currentTick, latestUpdate);
                    }

                    var (frame, version, isAnimatedComposite) = GetFrameFromTickWithVersion(workerState, currentTick, latestUpdate, yuvOnlyCompositing, expectedBgPixel);

                    if (pendingFrame is not null && pendingFrame.Version == version)
                    {
                        pendingFrame.RepeatCount++;
                    }
                    else
                    {
                        if (pendingFrame is not null)
                        {
                            FfmpegEncodeCoordinator.EnqueueFrameSync(frameChannel.Writer, pendingFrame, cancellationToken);
                        }

                        var frameSpan = frame.GetPixelSpan();
                        var rentedFrame = ArrayPool<byte>.Shared.Rent(frameByteLength);

                        if (pipeFormat == PipePixelFormat.BgraOrRgba)
                        {
                            frameSpan.CopyTo(rentedFrame);
                        }
                        else
                        {
                            var includeAlpha = pipeFormat == PipePixelFormat.Yuva420p;
                            if (latestUpdate is not null && cachedUpdateYuvBuffer is not null && cachedUpdateYuvVersion != latestUpdate.Version)
                            {
                                bool canShiftYuv = latestUpdate.IncrementalBaseVersion >= 0
                                    && cachedUpdateYuvVersion == latestUpdate.IncrementalBaseVersion
                                    && latestUpdate.ScrollDeltaY > 0
                                    && (latestUpdate.ScrollDeltaY & 1) == 0
                                    && (latestUpdate.StickyTopHeight & 1) == 0
                                    && (latestUpdate.DirtyTopY & 1) == 0
                                    && latestUpdate.DirtyTopY > latestUpdate.StickyTopHeight;

                                if (canShiftYuv)
                                {
                                    SimdPixelConverter.ShiftYuvPlanesUp(
                                        cachedUpdateYuvBuffer.AsSpan(0, frameByteLength),
                                        _options.ChatWidth,
                                        _options.ChatHeight,
                                        latestUpdate.StickyTopHeight,
                                        latestUpdate.DirtyTopY - latestUpdate.StickyTopHeight,
                                        latestUpdate.ScrollDeltaY,
                                        includeAlpha);

                                    SimdPixelConverter.ConvertFrameToYuv420p(
                                        latestUpdate.Image.GetPixelSpan(),
                                        cachedUpdateYuvBuffer.AsSpan(0, frameByteLength),
                                        _options.ChatWidth,
                                        _options.ChatHeight,
                                        latestUpdate.DirtyTopY,
                                        _options.ChatHeight,
                                        includeAlpha,
                                        isBgra);
                                }
                                else
                                {
                                    SimdPixelConverter.ConvertFrameToYuv420p(
                                        latestUpdate.Image.GetPixelSpan(),
                                        cachedUpdateYuvBuffer.AsSpan(0, frameByteLength),
                                        _options.ChatWidth,
                                        _options.ChatHeight,
                                        0,
                                        _options.ChatHeight,
                                        includeAlpha,
                                        isBgra);
                                }

                                cachedUpdateYuvVersion = latestUpdate.Version;
                            }

                            if (latestUpdate is not null && cachedUpdateYuvBuffer is not null && !isAnimatedComposite)
                            {
                                Buffer.BlockCopy(cachedUpdateYuvBuffer, 0, rentedFrame, 0, frameByteLength);
                            }
                            else if (latestUpdate is not null && cachedUpdateYuvBuffer is not null && cachedAnimYuvBuffer is not null && isAnimatedComposite)
                            {
                                if (cachedAnimBaseUpdateVersion != latestUpdate.Version)
                                {
                                    Buffer.BlockCopy(cachedUpdateYuvBuffer, 0, cachedAnimYuvBuffer, 0, frameByteLength);
                                    cachedAnimBaseUpdateVersion = latestUpdate.Version;
                                }

                                var dstYuvSpan = cachedAnimYuvBuffer.AsSpan(0, frameByteLength);

                                if (workerState.AnimDirtyRects.Count > 0 && workerState.AnimComposedFrame is not null)
                                {
                                    var animSpan = workerState.AnimComposedFrame.GetPixelSpan();
                                    foreach (var rect in workerState.AnimDirtyRects)
                                    {
                                        SimdPixelConverter.ConvertRectToYuv420p(
                                            animSpan,
                                            dstYuvSpan,
                                            _options.ChatWidth,
                                            _options.ChatHeight,
                                            rect.Left,
                                            rect.Right,
                                            rect.Top,
                                            rect.Bottom,
                                            includeAlpha,
                                            isBgra);
                                    }
                                }

                                if (workerState.AnimDirectYuvBlits.Count > 0)
                                {
                                    foreach (var blit in workerState.AnimDirectYuvBlits)
                                    {
                                        _assets.EmoteSurfaceCache.BlitEmoteYuvFrame(
                                            blit,
                                            dstYuvSpan,
                                            _options.ChatWidth,
                                            _options.ChatHeight,
                                            expectedBgPixel,
                                            includeAlpha,
                                            isBgra);
                                    }
                                }

                                Buffer.BlockCopy(cachedAnimYuvBuffer, 0, rentedFrame, 0, frameByteLength);
                            }
                            else
                            {
                                SimdPixelConverter.ConvertFrameToYuv420p(
                                    frameSpan,
                                    rentedFrame.AsSpan(0, frameByteLength),
                                    _options.ChatWidth,
                                    _options.ChatHeight,
                                    0,
                                    _options.ChatHeight,
                                    includeAlpha,
                                    isBgra);
                            }
                        }

                        byte[]? rentedMask = null;
                        if (hasMask)
                        {
                            rentedMask = ArrayPool<byte>.Shared.Rent(maskByteLength);
                            if (pipeFormat == PipePixelFormat.Yuva420p)
                            {
                                rentedFrame.AsSpan(pixelCount * 3 / 2, maskByteLength).CopyTo(rentedMask);
                            }
                            else
                            {
                                SimdPixelConverter.SetFrameMask(frameSpan, rentedMask.AsSpan(0, maskByteLength));
                            }
                        }

                        pendingFrame = new QueuedFrame
                        {
                            FrameBuffer = rentedFrame,
                            FrameLength = frameByteLength,
                            MaskBuffer = rentedMask,
                            MaskLength = maskByteLength,
                            Pts = currentTick - startTick,
                            RepeatCount = 1,
                            Version = version
                        };
                    }

                    long doneTicks = Interlocked.Increment(ref completedTicks);
                    if (doneTicks % (totalWorkers > 1 ? 12 : 3) == 0 && totalRenderTicks > 0)
                    {
                        long elapsedMs = stopwatch.ElapsedMilliseconds;
                        long prevReportMs = Volatile.Read(ref _lastProgressReportMs);
                        if (elapsedMs - prevReportMs >= 100 || doneTicks >= totalRenderTicks)
                        {
                            Interlocked.Exchange(ref _lastProgressReportMs, elapsedMs);
                            var percent = doneTicks / (double)totalRenderTicks * 100;
                            if (percent > 0)
                            {
                                var elapsedSeconds = elapsedMs / 1000.0;
                                var secondsLeft = unchecked((int)(100 / percent * elapsedSeconds - elapsedSeconds));
                                lock (_progressLock)
                                {
                                    _progress.ReportProgress(
                                        (int)Math.Round(percent),
                                        TimeSpan.FromSeconds((long)elapsedSeconds),
                                        TimeSpan.FromSeconds(Math.Max(0, secondsLeft)));
                                }
                            }
                        }
                    }
                }

                if (pendingFrame is not null)
                {
                    FfmpegEncodeCoordinator.EnqueueFrameSync(frameChannel.Writer, pendingFrame, cancellationToken);
                    pendingFrame = null;
                }

                frameChannel.Writer.Complete();
                pipeWriterTask.GetAwaiter().GetResult();

                ffmpegStream.Dispose();
                maskStream?.Dispose();

                FfmpegEncodeCoordinator.WaitForExit(ffmpegProcess, cancellationToken);
                if (maskProcess is not null)
                {
                    FfmpegEncodeCoordinator.WaitForExit(maskProcess, cancellationToken);
                }

                if (ffmpegProcess.HasExited && ffmpegProcess.ExitCode != 0)
                {
                    throw new InvalidOperationException($"FFmpeg process for segment {workerId} exited with code {ffmpegProcess.ExitCode}.");
                }
                if (maskProcess is not null && maskProcess.HasExited && maskProcess.ExitCode != 0)
                {
                    throw new InvalidOperationException($"FFmpeg mask process for segment {workerId} exited with code {maskProcess.ExitCode}.");
                }
            }
            catch
            {
                if (pendingFrame is not null)
                {
                    ArrayPool<byte>.Shared.Return(pendingFrame.FrameBuffer);
                    if (pendingFrame.MaskBuffer is not null)
                    {
                        ArrayPool<byte>.Shared.Return(pendingFrame.MaskBuffer);
                    }
                }
                frameChannel.Writer.TryComplete();
                try { if (!ffmpegProcess.HasExited) ffmpegProcess.Kill(entireProcessTree: true); } catch { /* ignore */ }
                try { if (maskProcess is { HasExited: false }) maskProcess.Kill(entireProcessTree: true); } catch { /* ignore */ }
                try { pipeWriterTask.GetAwaiter().GetResult(); } catch { /* propagate primary exception */ }
                throw;
            }
            finally
            {
                workerState.StopLookahead();

                if (cachedUpdateYuvBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(cachedUpdateYuvBuffer);
                }
                if (cachedAnimYuvBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(cachedAnimYuvBuffer);
                }

                try { ffmpegStream?.Dispose(); } catch { /* ignore */ }
                try { maskStream?.Dispose(); } catch { /* ignore */ }

                latestUpdate?.Canvas?.Dispose();
                latestUpdate?.Image?.Dispose();
                latestUpdate?.Comments?.ForEach(x => x?.Release(_assets.SectionImageCache));
                _assets.ImageBudget.UnregisterWorker(workerId);
            }
        }

        private (SKBitmap Frame, long Version, bool IsAnimatedComposite) GetFrameFromTickWithVersion(
            RenderWorkerState workerState,
            int currentTick,
            UpdateFrame? currentFrame,
            bool yuvOnlyCompositing,
            uint expectedBgPixel)
        {
            currentFrame ??= GenerateUpdateFrame(workerState, currentTick, null);
            return DrawAnimatedEmotes(workerState, currentFrame, currentTick, yuvOnlyCompositing, expectedBgPixel);
        }

        private (SKBitmap Frame, long Version, bool IsAnimatedComposite) DrawAnimatedEmotes(
            RenderWorkerState workerState,
            UpdateFrame currentFrame,
            int currentTick,
            bool yuvOnlyCompositing,
            uint expectedBgPixel)
        {
            var updateFrame = currentFrame.Image;
            if (!currentFrame.HasAnimatedEmotes)
            {
                workerState.InvalidateAnimCache();
                return (updateFrame, currentFrame.Version, false);
            }

            var currentTickMs = (long)(currentTick / (double)_options.Framerate * 1000);

            if (workerState.HasValidAnimComposite
                && currentFrame.Version == workerState.AnimBaseUpdateVersion
                && currentTickMs >= workerState.AnimLastTickMs
                && currentTickMs < workerState.AnimNextChangeTickMs)
            {
                workerState.AnimLastTickMs = currentTickMs;
                return (workerState.AnimComposedFrame ?? updateFrame, workerState.AnimComposedVersion, true);
            }

            if (workerState.HasValidAnimComposite
                && currentFrame.Version == workerState.AnimBaseUpdateVersion
                && AnimCacheIsValid(workerState, currentTickMs, out long nextValidTickMs))
            {
                workerState.AnimNextChangeTickMs = nextValidTickMs;
                workerState.AnimLastTickMs = currentTickMs;
                return (workerState.AnimComposedFrame ?? updateFrame, workerState.AnimComposedVersion, true);
            }

            ComposeAnimatedFrame(
                workerState,
                updateFrame,
                currentFrame.Version,
                currentFrame.Comments,
                currentTickMs,
                currentFrame.StickyTopHeight,
                yuvOnlyCompositing,
                expectedBgPixel,
                out long nextChangeTickMs);

            workerState.HasValidAnimComposite = true;
            workerState.AnimNextChangeTickMs = nextChangeTickMs;
            workerState.AnimLastTickMs = currentTickMs;
            workerState.AnimComposedVersion = ++workerState.FrameVersionCounter;
            return (workerState.AnimComposedFrame ?? updateFrame, workerState.AnimComposedVersion, true);
        }

        private void ComposeAnimatedFrame(
            RenderWorkerState workerState,
            SKBitmap updateFrame,
            long updateVersion,
            List<CommentSection> comments,
            long currentTickMs,
            int stickyTopHeight,
            bool yuvOnlyCompositing,
            uint expectedBgPixel,
            out long nextChangeTickMs)
        {
            bool isIncremental = workerState.HasValidAnimComposite && workerState.AnimBaseUpdateVersion == updateVersion;
            int chatWidth = _options.ChatWidth;
            int chatHeight = _options.ChatHeight;
            int strideBytes = chatWidth * 4;
            nextChangeTickMs = long.MaxValue;

            workerState.AnimDirtyRects.Clear();
            workerState.AnimDirectYuvBlits.Clear();

            var slots = workerState.AnimSlots;

            if (!isIncremental)
            {
                slots.Clear();
                int frameHeight = chatHeight;
                int verticalPadding = _options.VerticalPadding;

                for (int c = comments.Count - 1; c >= 0; c--)
                {
                    var comment = comments[c];
                    frameHeight -= comment.Image.Info.Height + verticalPadding;
                    if (!comment.HasAnimatedEmotes)
                    {
                        continue;
                    }

                    foreach (var (drawPoint, emote) in comment.Emotes)
                    {
                        if (emote.FrameCount <= 1)
                        {
                            continue;
                        }

                        int drawX = drawPoint.X;
                        int drawY = drawPoint.Y + frameHeight;
                        int w = emote.Width;
                        int h = emote.Height;

                        if (drawY + h <= stickyTopHeight || drawY >= chatHeight || drawX + w <= 0 || drawX >= chatWidth)
                        {
                            continue;
                        }

                        slots.Add(new AnimEmoteSlot
                        {
                            Emote = emote,
                            DrawX = drawX,
                            DrawY = drawY,
                            Width = w,
                            Height = h,
                            LastFrameIndex = -1,
                            CanDirectYuvBlit = false,
                            BgPixel = expectedBgPixel
                        });
                    }
                }

                if (yuvOnlyCompositing && slots.Count > 0)
                {
                    unsafe
                    {
                        byte* pSrc = (byte*)updateFrame.GetPixels();
                        var slotsSpan = CollectionsMarshal.AsSpan(slots);
                        for (int s = 0; s < slotsSpan.Length; s++)
                        {
                            ref var slot = ref slotsSpan[s];
                            if ((slot.DrawX & 1) != 0
                                || (slot.DrawY & 1) != 0
                                || (slot.Width & 1) != 0
                                || (slot.Height & 1) != 0
                                || slot.DrawX < 0
                                || slot.DrawX + slot.Width > chatWidth
                                || slot.DrawY < stickyTopHeight
                                || slot.DrawY + slot.Height > chatHeight
                                || slot.Emote.FrameCount > 256)
                            {
                                continue;
                            }

                            bool overlapsOther = false;
                            for (int o = 0; o < slotsSpan.Length; o++)
                            {
                                if (o == s) continue;
                                ref readonly var other = ref slotsSpan[o];
                                if (slot.DrawX < other.DrawX + other.Width
                                    && slot.DrawX + slot.Width > other.DrawX
                                    && slot.DrawY < other.DrawY + other.Height
                                    && slot.DrawY + slot.Height > other.DrawY)
                                {
                                    overlapsOther = true;
                                    break;
                                }
                            }

                            if (!overlapsOther)
                            {
                                // Sample the actual row background pixel under the emote slot (supports standard,
                                // alternating row, and highlighted-user row backgrounds simultaneously without thrashing).
                                uint candidateBgPixel = *(uint*)(pSrc + (long)slot.DrawY * strideBytes + (long)slot.DrawX * 4);
                                if (SimdPixelConverter.IsRectUniformPixel(pSrc, strideBytes, slot.DrawX, slot.DrawY, slot.Width, slot.Height, candidateBgPixel))
                                {
                                    slot.CanDirectYuvBlit = true;
                                    slot.BgPixel = candidateBgPixel;
                                }
                            }
                        }
                    }
                }
            }

            var activeSlots = CollectionsMarshal.AsSpan(slots);
            for (int s = 0; s < activeSlots.Length; s++)
            {
                ref var slot = ref activeSlots[s];
                int frameIdx = slot.Emote.GetFrameIndexAndNextChangeMs(currentTickMs, out long emoteNextChangeMs);
                if (emoteNextChangeMs < nextChangeTickMs)
                {
                    nextChangeTickMs = emoteNextChangeMs;
                }

                bool changed = !isIncremental || slot.LastFrameIndex != frameIdx;
                slot.LastFrameIndex = frameIdx;

                if (!changed)
                {
                    continue;
                }

                if (yuvOnlyCompositing && slot.CanDirectYuvBlit)
                {
                    workerState.AnimDirectYuvBlits.Add(new DirectYuvEmoteBlit(slot.Emote, frameIdx, slot.DrawX, slot.DrawY, slot.BgPixel));
                }
                else
                {
                    int left = Math.Max(0, slot.DrawX) & ~1;
                    int top = Math.Max(stickyTopHeight, slot.DrawY) & ~1;
                    int right = Math.Min(chatWidth, (slot.DrawX + slot.Width + 1) & ~1);
                    int bottom = Math.Min(chatHeight, (slot.DrawY + slot.Height + 1) & ~1);
                    SimdPixelConverter.AppendAndCoalesceDirtyRect(workerState.AnimDirtyRects, new DirtyRect(left, top, right, bottom));
                }
            }

            workerState.AnimBaseUpdateVersion = updateVersion;

            bool needsBgraCanvas = !yuvOnlyCompositing || workerState.AnimDirtyRects.Count > 0;
            if (!needsBgraCanvas)
            {
                return;
            }

            if (workerState.AnimComposedFrame is null || workerState.AnimCanvas is null)
            {
                workerState.AnimComposedFrame = new SKBitmap(updateFrame.Info);
                workerState.AnimByteSize = updateFrame.Info.BytesSize;
                workerState.AnimCanvas = new SKCanvas(workerState.AnimComposedFrame);
            }

            unsafe
            {
                byte* pSrc = (byte*)updateFrame.GetPixels();
                byte* pDst = (byte*)workerState.AnimComposedFrame.GetPixels();

                if (!isIncremental && !yuvOnlyCompositing)
                {
                    Buffer.MemoryCopy(pSrc, pDst, workerState.AnimByteSize, workerState.AnimByteSize);
                }
                else
                {
                    foreach (var rect in workerState.AnimDirtyRects)
                    {
                        int rowBytes = (rect.Right - rect.Left) * 4;
                        for (int y = rect.Top; y < rect.Bottom; y++)
                        {
                            long offset = (long)y * strideBytes + (long)rect.Left * 4;
                            Buffer.MemoryCopy(pSrc + offset, pDst + offset, rowBytes, rowBytes);
                        }
                    }
                }
            }

            workerState.AnimComposedFrame.NotifyPixelsChanged();

            if (workerState.AnimDirtyRects.Count == 0)
            {
                return;
            }

            var animCanvas = workerState.AnimCanvas;
            foreach (var rect in workerState.AnimDirtyRects)
            {
                animCanvas.Save();
                try
                {
                    animCanvas.ClipRect(new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom));
                    for (int s = 0; s < activeSlots.Length; s++)
                    {
                        ref readonly var slot = ref activeSlots[s];
                        if (slot.DrawX < rect.Right
                            && slot.DrawX + slot.Width > rect.Left
                            && slot.DrawY < rect.Bottom
                            && slot.DrawY + slot.Height > rect.Top)
                        {
                            var emoteImg = slot.Emote.EmoteFrames[slot.LastFrameIndex];
                            animCanvas.DrawImage(emoteImg, slot.DrawX, slot.DrawY);
                        }
                    }
                }
                finally
                {
                    animCanvas.Restore();
                }
            }

            animCanvas.Flush();
        }

        private static bool AnimCacheIsValid(RenderWorkerState workerState, long currentTickMs, out long nextChangeTickMs)
        {
            nextChangeTickMs = long.MaxValue;
            var slots = CollectionsMarshal.AsSpan(workerState.AnimSlots);
            for (int i = 0; i < slots.Length; i++)
            {
                ref readonly var slot = ref slots[i];
                if (slot.Emote.GetFrameIndexAndNextChangeMs(currentTickMs, out long emoteNextMs) != slot.LastFrameIndex)
                {
                    return false;
                }
                if (emoteNextMs < nextChangeTickMs)
                {
                    nextChangeTickMs = emoteNextMs;
                }
            }

            return true;
        }

        private int GetNewestMessageIndex(int lastIndex, double currentTimeSeconds)
        {
            var messages = _timeline.Messages;
            int count = messages.Count;
            for (int i = lastIndex + 1; i < count; i++)
            {
                if (messages[i].AppearSeconds > currentTimeSeconds)
                {
                    return i - 1;
                }
            }

            return count - 1;
        }

        private void RunLookaheadProducerLoop(RenderWorkerState workerState, CancellationToken producerToken)
        {
            int messageCount = _timeline.Messages.Count;
            var maxCommentIdx = workerState.MaxCommentIndex >= 0
                ? Math.Min(workerState.MaxCommentIndex, messageCount - 1)
                : messageCount - 1;

            if (maxCommentIdx < 0)
            {
                return;
            }

            var windowSize = Math.Clamp(Environment.ProcessorCount * 8 / Math.Max(1, workerState.TotalWorkers), 32, 128);
            var chunkSize = Math.Clamp(Environment.ProcessorCount * 2 / Math.Max(1, workerState.TotalWorkers), 8, 32);
            var maxParallel = Math.Clamp(Environment.ProcessorCount / Math.Max(1, workerState.TotalWorkers), 1, 8);
            var parallelOpts = new ParallelOptions
            {
                MaxDegreeOfParallelism = maxParallel,
                CancellationToken = producerToken
            };
            WaitHandle[] waitHandles = [workerState.LookaheadWakeSignal, producerToken.WaitHandle];

            while (!producerToken.IsCancellationRequested)
            {
                int consumed = Volatile.Read(ref workerState.ConsumedCommentIndex);
                int nextIdx = Math.Max(workerState.ProducerNextIndex, consumed + 1);
                if (nextIdx > maxCommentIdx)
                {
                    break;
                }

                int baselineIdx = Math.Max(consumed, workerState.InitialTargetIndex);
                int targetMaxIdx = Math.Min(maxCommentIdx, baselineIdx + windowSize);
                if (nextIdx > targetMaxIdx)
                {
                    WaitHandle.WaitAny(waitHandles, 50);
                    continue;
                }

                int batchEnd = Math.Min(targetMaxIdx, nextIdx + chunkSize - 1);
                int count = batchEnd - nextIdx + 1;

                if (count == 1 || maxParallel <= 1)
                {
                    for (int idx = nextIdx; idx <= batchEnd && !producerToken.IsCancellationRequested; idx++)
                    {
                        ProduceSingleLookaheadSection(workerState, idx);
                    }
                }
                else
                {
                    int startIdx = nextIdx;
                    Parallel.For(0, count, parallelOpts, offset =>
                    {
                        ProduceSingleLookaheadSection(workerState, startIdx + offset);
                    });
                }

                workerState.ProducerNextIndex = batchEnd + 1;
            }
        }

        private void ProduceSingleLookaheadSection(RenderWorkerState workerState, int messageIdx)
        {
            if (Volatile.Read(ref workerState.ConsumedCommentIndex) >= messageIdx
                || workerState.PreRenderedSections.ContainsKey(messageIdx))
            {
                return;
            }

            var ctx = _layout.GetWorkerContext();
            var section = _sectionRenderer.RenderSection(_timeline.Messages, messageIdx, ctx);
            if (section is not null)
            {
                _assets.ImageBudget.MarkDrawn(section.Emotes, workerState.WorkerId);
                foreach (var (_, emote) in section.Emotes)
                {
                    _ = emote.EmoteFrames;
                }
            }

            if (!workerState.PreRenderedSections.TryAdd(messageIdx, section))
            {
                section?.Release(_assets.SectionImageCache);
                return;
            }

            if (Volatile.Read(ref workerState.ConsumedCommentIndex) >= messageIdx)
            {
                if (workerState.PreRenderedSections.TryRemove(messageIdx, out var stale))
                {
                    stale?.Release(_assets.SectionImageCache);
                }
            }
        }

        private UpdateFrame GenerateUpdateFrame(RenderWorkerState workerState, int currentTick, UpdateFrame? lastUpdate)
        {
            var currentTimeSeconds = currentTick / (double)_options.Framerate;
            workerState.StickyTracker.AdvanceTo(currentTimeSeconds);
            var stickySig = workerState.StickyTracker.ComputeSignature(currentTimeSeconds);
            var messages = _timeline.Messages;
            var ctx = _layout.GetWorkerContext();

            var anyModeratedChanged = false;
            if (lastUpdate?.Comments is { Count: > 0 } existingComments
                && _options.ModerationMode is ModerationDisplayMode.Strikethrough or ModerationDisplayMode.ReplaceDeleted)
            {
                for (int i = 0; i < existingComments.Count; i++)
                {
                    var sec = existingComments[i];
                    if (!sec.IsModeratedRendered
                        && messages[sec.CommentIndex].ModeratedAtSeconds is double modAt
                        && currentTimeSeconds >= modAt)
                    {
                        var reRendered = _sectionRenderer.RenderSection(messages, sec.CommentIndex, ctx, isModerated: true);
                        if (reRendered is not null)
                        {
                            sec.Release(_assets.SectionImageCache);
                            _assets.ImageBudget.MarkDrawn(reRendered.Emotes, workerState.WorkerId);
                            existingComments[i] = reRendered;
                            anyModeratedChanged = true;
                        }
                    }
                }
            }

            var newestCommentIndex = GetNewestMessageIndex(lastUpdate?.CommentIndex ?? -1, currentTimeSeconds);
            if (lastUpdate is not null
                && newestCommentIndex == lastUpdate.CommentIndex
                && !anyModeratedChanged
                && stickySig == lastUpdate.StickySignature)
            {
                return lastUpdate;
            }

            var isNewUpdateFrame = false;
            if (lastUpdate is null)
            {
                var bitmap = new SKBitmap(_options.ChatWidth, _options.ChatHeight);
                lastUpdate = new UpdateFrame
                {
                    Image = bitmap,
                    Canvas = new SKCanvas(bitmap)
                };
                isNewUpdateFrame = true;
            }

            var commentList = lastUpdate.Comments ?? [];
            int prevCommentCount = commentList.Count;
            long prevUpdateVersion = lastUpdate.Version;

            int oldCommentIndex = -1;
            if (commentList.Count > 0)
            {
                oldCommentIndex = commentList[^1].CommentIndex;
            }
            else if (newestCommentIndex > 100)
            {
                oldCommentIndex = newestCommentIndex - 100;
            }

            var anyCommentAdded = false;
            if (newestCommentIndex > oldCommentIndex)
            {
                Volatile.Write(ref workerState.ConsumedCommentIndex, newestCommentIndex);
                workerState.LookaheadWakeSignal.Set();

                for (int currentIndex = oldCommentIndex + 1; currentIndex <= newestCommentIndex; currentIndex++)
                {
                    bool alreadyModerated = _options.ModerationMode is ModerationDisplayMode.Strikethrough or ModerationDisplayMode.ReplaceDeleted
                        && messages[currentIndex].ModeratedAtSeconds is double modAt
                        && currentTimeSeconds >= modAt;

                    if (alreadyModerated)
                    {
                        if (workerState.PreRenderedSections.TryRemove(currentIndex, out var unmoderated))
                        {
                            unmoderated?.Release(_assets.SectionImageCache);
                        }
                        var modComment = _sectionRenderer.RenderSection(messages, currentIndex, ctx, isModerated: true);
                        if (modComment is not null)
                        {
                            _assets.ImageBudget.MarkDrawn(modComment.Emotes, workerState.WorkerId);
                            commentList.Add(modComment);
                            anyCommentAdded = true;
                        }
                    }
                    else
                    {
                        if (!workerState.PreRenderedSections.TryRemove(currentIndex, out var comment))
                        {
                            comment = _sectionRenderer.RenderSection(messages, currentIndex, ctx);
                        }

                        if (comment is not null)
                        {
                            _assets.ImageBudget.MarkDrawn(comment.Emotes, workerState.WorkerId);
                            commentList.Add(comment);
                            anyCommentAdded = true;
                        }
                    }
                }
            }

            if (!anyCommentAdded && !isNewUpdateFrame && !anyModeratedChanged && stickySig == lastUpdate.StickySignature)
            {
                lastUpdate.CommentIndex = newestCommentIndex;
                return lastUpdate;
            }

            var frameCanvas = lastUpdate.Canvas;
            int commentsDrawn = 0;
            int commentListIndex = commentList.Count - 1;
            int frameHeight = _options.ChatHeight;
            var frameWidth = lastUpdate.Image.Width;
            var hasAnimatedEmotes = false;
            int stickyTopHeight;
            bool usedIncrementalScroll = false;

            int addedCommentCount = commentList.Count - prevCommentCount;
            if (!isNewUpdateFrame
                && prevCommentCount > 0
                && addedCommentCount > 0
                && !anyModeratedChanged
                && stickySig == lastUpdate.StickySignature)
            {
                int scrollDeltaY = 0;
                for (int i = prevCommentCount; i < commentList.Count; i++)
                {
                    scrollDeltaY += commentList[i].Image.Info.Height + _options.VerticalPadding;
                }

                stickyTopHeight = lastUpdate.StickyTopHeight;
                int halfPad = (_options.VerticalPadding + 1) / 2 + 1;
                int minNewVisualTop = _options.ChatHeight - scrollDeltaY;
                int scanY = _options.ChatHeight;
                for (int i = commentList.Count - 1; i >= prevCommentCount; i--)
                {
                    var newSec = commentList[i];
                    scanY -= newSec.Image.Info.Height + _options.VerticalPadding;
                    int vTop = scanY - Math.Max(halfPad, newSec.MaxTopOverhang);
                    if (vTop < minNewVisualTop)
                    {
                        minNewVisualTop = vTop;
                    }
                }

                int dirtyTopY = Math.Min(_options.ChatHeight - scrollDeltaY, minNewVisualTop) & ~1;
                if (scrollDeltaY > 0 && dirtyTopY > stickyTopHeight + 2 && dirtyTopY < _options.ChatHeight)
                {
                    usedIncrementalScroll = true;
                    frameCanvas.Flush();

                    unsafe
                    {
                        byte* pPixels = (byte*)lastUpdate.Image.GetPixels();
                        long strideBytes = (long)frameWidth * 4;
                        long srcOffset = (long)(stickyTopHeight + scrollDeltaY) * strideBytes;
                        long dstOffset = (long)stickyTopHeight * strideBytes;
                        long copyBytes = (long)(dirtyTopY - stickyTopHeight) * strideBytes;
                        Buffer.MemoryCopy(pPixels + srcOffset, pPixels + dstOffset, copyBytes, copyBytes);
                    }
                    lastUpdate.Image.NotifyPixelsChanged();

                    frameCanvas.Save();
                    try
                    {
                        frameCanvas.ClipRect(new SKRect(0, dirtyTopY, frameWidth, _options.ChatHeight));
                        frameCanvas.Clear(_options.BackgroundColor);

                        while (commentListIndex >= 0 && frameHeight > -_options.VerticalPadding)
                        {
                            var comment = commentList[commentListIndex];
                            var commentHeight = comment.Image.Info.Height;
                            frameHeight -= commentHeight + _options.VerticalPadding;

                            if (comment.HasAnimatedEmotes)
                            {
                                hasAnimatedEmotes = true;
                            }

                            int visualTop = frameHeight - Math.Max(halfPad, comment.MaxTopOverhang);
                            int visualBottom = frameHeight + commentHeight + Math.Max(halfPad, comment.MaxBottomOverhang);

                            if (visualBottom > dirtyTopY && visualTop < _options.ChatHeight)
                            {
                                var backgroundColor = _layout.GetMessageBackground(messages[comment.CommentIndex], comment.CommentIndex, ctx, out var backgroundPaint);
                                if (backgroundColor != _options.BackgroundColor && backgroundPaint is not null)
                                {
                                    frameCanvas.DrawRect(0, frameHeight - _options.VerticalPadding / 2f, frameWidth, commentHeight + _options.VerticalPadding, backgroundPaint);
                                }

                                frameCanvas.DrawBitmap(comment.Image.Bitmap, 0, frameHeight);

                                foreach (var (drawPoint, emote) in comment.Emotes)
                                {
                                    if (emote.FrameCount == 1)
                                    {
                                        frameCanvas.DrawImage(emote.EmoteFrames[0], drawPoint.X, drawPoint.Y + frameHeight);
                                    }
                                }
                            }

                            commentsDrawn++;
                            commentListIndex--;
                        }
                    }
                    finally
                    {
                        frameCanvas.Restore();
                    }

                    frameCanvas.Flush();
                    lastUpdate.IncrementalBaseVersion = prevUpdateVersion;
                    lastUpdate.ScrollDeltaY = scrollDeltaY;
                    lastUpdate.DirtyTopY = dirtyTopY;
                }
                else
                {
                    stickyTopHeight = 0;
                }
            }
            else
            {
                stickyTopHeight = 0;
            }

            if (!usedIncrementalScroll)
            {
                frameCanvas.Clear(_options.BackgroundColor);

                while (commentListIndex >= 0 && frameHeight > -_options.VerticalPadding)
                {
                    var comment = commentList[commentListIndex];
                    var commentHeight = comment.Image.Info.Height;
                    frameHeight -= commentHeight + _options.VerticalPadding;

                    var backgroundColor = _layout.GetMessageBackground(messages[comment.CommentIndex], comment.CommentIndex, ctx, out var backgroundPaint);
                    if (backgroundColor != _options.BackgroundColor && backgroundPaint is not null)
                    {
                        frameCanvas.DrawRect(0, frameHeight - _options.VerticalPadding / 2f, frameWidth, commentHeight + _options.VerticalPadding, backgroundPaint);
                    }

                    frameCanvas.DrawBitmap(comment.Image.Bitmap, 0, frameHeight);

                    if (comment.HasAnimatedEmotes)
                    {
                        hasAnimatedEmotes = true;
                    }

                    foreach (var (drawPoint, emote) in comment.Emotes)
                    {
                        if (emote.FrameCount == 1)
                        {
                            frameCanvas.DrawImage(emote.EmoteFrames[0], drawPoint.X, drawPoint.Y + frameHeight);
                        }
                    }

                    commentsDrawn++;
                    commentListIndex--;
                }

                stickyTopHeight = _stickyRenderer.DrawStickyTopOverlays(frameCanvas, workerState.StickyTracker, currentTimeSeconds, ctx);
                frameCanvas.Flush();
                lastUpdate.IncrementalBaseVersion = -1;
                lastUpdate.ScrollDeltaY = 0;
                lastUpdate.DirtyTopY = 0;
            }

            int removeCount = commentList.Count - commentsDrawn;
            for (int i = 0; i < removeCount; i++)
            {
                commentList[i].Release(_assets.SectionImageCache);
            }
            commentList.RemoveRange(0, removeCount);

            if (removeCount > 0 || anyModeratedChanged)
            {
                PopulateOnScreenImages(commentList, workerState.PreRenderedSections, workerState.OnScreenEmoteSet);
                if (_assets.ImageBudget.ReleaseUnusedTracked(workerState.OnScreenEmoteSet, workerState.WorkerId))
                {
                    workerState.InvalidateAnimCache();
                }
            }

            lastUpdate.Comments = commentList;
            lastUpdate.CommentIndex = newestCommentIndex;
            lastUpdate.HasAnimatedEmotes = hasAnimatedEmotes;
            lastUpdate.StickyTopHeight = stickyTopHeight;
            lastUpdate.StickySignature = stickySig;
            lastUpdate.Version = ++workerState.FrameVersionCounter;
            return lastUpdate;
        }

        private static void PopulateOnScreenImages(
            List<CommentSection> visibleComments,
            ConcurrentDictionary<int, CommentSection?> preRenderedSections,
            HashSet<TwitchEmote> destination)
        {
            destination.Clear();

            for (int i = 0; i < visibleComments.Count; i++)
            {
                var emotes = visibleComments[i].Emotes;
                for (int e = 0; e < emotes.Count; e++)
                {
                    destination.Add(emotes[e].Emote);
                }
            }

            foreach (var kvp in preRenderedSections)
            {
                var sec = kvp.Value;
                if (sec is null) continue;
                for (int e = 0; e < sec.Emotes.Count; e++)
                {
                    destination.Add(sec.Emotes[e].Emote);
                }
            }
        }
    }
}

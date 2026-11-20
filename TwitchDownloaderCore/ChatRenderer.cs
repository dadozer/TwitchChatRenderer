#nullable enable
using SkiaSharp;
using System.Diagnostics;
using TwitchDownloaderCore.Chat;
using TwitchDownloaderCore.Chat.Timeline;
using TwitchDownloaderCore.Interfaces;
using TwitchDownloaderCore.Models.Render;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Rendering;
using TwitchDownloaderCore.Rendering.Encoding;
using TwitchDownloaderCore.Services;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore
{
    /// <summary>
    /// High-level pipeline coordinator for Twitch chat video rendering.
    /// Orchestrates JSON parsing (<see cref="ChatJson"/>), asset catalog acquisition (<see cref="AssetCatalog"/>),
    /// non-mutating timeline compilation (<see cref="TimelineCompiler"/>), cost-weighted segment partitioning
    /// (<see cref="SegmentPartitioner"/>), unified 1-or-N segment compositing (<see cref="SegmentCompositor"/>),
    /// and FFmpeg encoding/concatenation (<see cref="FfmpegEncodeCoordinator"/>).
    /// </summary>
    public sealed class ChatRenderer : IDisposable
    {
        private readonly ChatRenderOptions _renderOptions;
        private readonly ITaskProgress _progress;
        private readonly string _cacheDir;
        private TextLayoutEngine? _layout;
        private AssetCatalog? _assets;

        public bool Disposed { get; private set; }
        public ChatRoot chatRoot { get; private set; } = new();

        public ChatRenderer(ChatRenderOptions chatRenderOptions, ITaskProgress progress)
        {
            _renderOptions = chatRenderOptions ?? throw new ArgumentNullException(nameof(chatRenderOptions));
            _progress = progress ?? throw new ArgumentNullException(nameof(progress));
            _cacheDir = CacheDirectoryService.GetCacheDirectory(_renderOptions.TempFolder);
        }

        public async Task<ChatRoot> ParseJsonAsync(CancellationToken cancellationToken = default)
        {
            chatRoot = await ChatJson.DeserializeAsync(_renderOptions.InputFile, true, false, true, cancellationToken).ConfigureAwait(false);
            EnsureVideoBounds(chatRoot);
            return chatRoot;
        }

        public static SKBitmap RenderPreviewBitmap(ChatRenderOptions options, int previewWidth, int previewHeight)
            => ChatPreviewRenderer.RenderPreviewBitmap(options, previewWidth, previewHeight);

        public async Task RenderVideoAsync(CancellationToken cancellationToken)
        {
            var outputFileInfo = TwitchHelper.ClaimFile(_renderOptions.OutputFile, _renderOptions.FileCollisionCallback, _progress);
            _renderOptions.OutputFile = outputFileInfo.FullName;
            var maskFileInfo = _renderOptions.GenerateMask
                ? TwitchHelper.ClaimFile(_renderOptions.MaskFile, _renderOptions.FileCollisionCallback, _progress)
                : null;

            await using var outputFs = outputFileInfo.Open(FileMode.Create, FileAccess.Write, FileShare.Read);
            await using var maskFs = maskFileInfo?.Open(FileMode.Create, FileAccess.Write, FileShare.Read);

            try
            {
                await RenderAsyncImpl(outputFileInfo, outputFs, maskFileInfo, maskFs, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);

                TwitchHelper.CleanUpClaimedFile(outputFileInfo, outputFs, _progress);
                if (maskFileInfo is not null && maskFs is not null)
                {
                    TwitchHelper.CleanUpClaimedFile(maskFileInfo, maskFs, _progress);
                }

                throw;
            }
        }

        private async Task RenderAsyncImpl(
            FileInfo outputFileInfo,
            FileStream outputFs,
            FileInfo? maskFileInfo,
            FileStream? maskFs,
            CancellationToken cancellationToken)
        {
            EnsureVideoBounds(chatRoot);

            _layout?.Dispose();
            _assets?.Dispose();

            _layout = new TextLayoutEngine(_renderOptions, _progress);
            _assets = new AssetCatalog(_renderOptions, _cacheDir, _layout.BaseOutlinePaint, _progress);

            _progress.SetStatus("Fetching Images [1/2]");
            await _assets.LoadAsync(chatRoot, _progress, cancellationToken).ConfigureAwait(false);

            var timeline = TimelineCompiler.Compile(chatRoot, _renderOptions);
            var (startTick, endTick, totalTicks) = timeline.GetTickRange(_renderOptions);
            var segments = SegmentPartitioner.PlanSegments(timeline, _renderOptions, startTick, endTick);

            // Close placeholder handles before spawning FFmpeg output writers
            outputFs.Close();
            outputFileInfo.Refresh();
            if (outputFileInfo.Exists)
            {
                outputFileInfo.Delete();
            }

            maskFs?.Close();
            maskFileInfo?.Refresh();
            if (_renderOptions.GenerateMask && maskFileInfo is { Exists: true })
            {
                maskFileInfo.Delete();
            }

            var sectionRenderer = new MessageSectionRenderer(_renderOptions, _assets, _layout);
            var stickyRenderer = new StickyOverlayRenderer(_renderOptions, _assets, _layout);
            var compositor = new SegmentCompositor(
                _renderOptions,
                timeline,
                _assets,
                _layout,
                sectionRenderer,
                stickyRenderer,
                _progress);

            _progress.SetTemplateStatus(@"Rendering Video {0}% ({1:h\hm\ms\s} Elapsed | {2:h\hm\ms\s} Remaining)", 0, TimeSpan.Zero, TimeSpan.Zero);

            await ExecuteUnifiedSegmentsAsync(
                compositor,
                segments,
                outputFileInfo,
                maskFileInfo,
                totalTicks,
                cancellationToken).ConfigureAwait(false);
        }

        private async Task ExecuteUnifiedSegmentsAsync(
            SegmentCompositor compositor,
            List<(int StartTick, int EndTick)> segments,
            FileInfo outputFileInfo,
            FileInfo? maskFileInfo,
            int totalTicks,
            CancellationToken cancellationToken)
        {
            int workerCount = Math.Max(1, segments.Count);
            bool isMultiSegment = workerCount > 1;
            string runId = isMultiSegment ? Guid.NewGuid().ToString("N")[..8] : string.Empty;
            string outputDir = outputFileInfo.DirectoryName ?? "";
            string maskDir = maskFileInfo?.DirectoryName ?? outputDir;

            var targetOutputFiles = new FileInfo[workerCount];
            var targetMaskFiles = _renderOptions.GenerateMask ? new FileInfo[workerCount] : null;
            var ffmpegProcesses = new FfmpegProcess?[workerCount];
            var maskProcesses = _renderOptions.GenerateMask ? new FfmpegProcess?[workerCount] : null;
            var mainPlans = new EncodingPlan[workerCount];

            Stopwatch stopwatch = Stopwatch.StartNew();
            long completedTicks = 0;

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var workerToken = linkedCts.Token;
            var workerTasks = new Task?[workerCount];

            try
            {
                for (int i = 0; i < workerCount; i++)
                {
                    if (isMultiSegment)
                    {
                        targetOutputFiles[i] = new FileInfo(Path.Combine(outputDir, $".chatrender_seg_{runId}_{i}{outputFileInfo.Extension}"));
                        if (targetOutputFiles[i].Exists)
                        {
                            targetOutputFiles[i].Delete();
                        }
                    }
                    else
                    {
                        targetOutputFiles[i] = outputFileInfo;
                    }

                    ffmpegProcesses[i] = FfmpegEncodeCoordinator.StartEncoderProcess(
                        _renderOptions,
                        targetOutputFiles[i],
                        isMask: false,
                        parallelJobs: workerCount,
                        _progress,
                        out mainPlans[i]);

                    if (_renderOptions.GenerateMask && maskFileInfo is not null)
                    {
                        if (isMultiSegment)
                        {
                            targetMaskFiles![i] = new FileInfo(Path.Combine(maskDir, $".chatrender_mask_seg_{runId}_{i}{maskFileInfo.Extension}"));
                            if (targetMaskFiles[i].Exists)
                            {
                                targetMaskFiles[i].Delete();
                            }
                        }
                        else
                        {
                            targetMaskFiles![i] = maskFileInfo;
                        }

                        maskProcesses![i] = FfmpegEncodeCoordinator.StartEncoderProcess(
                            _renderOptions,
                            targetMaskFiles[i],
                            isMask: true,
                            parallelJobs: workerCount,
                            _progress,
                            out _);
                    }
                }

                for (int i = 0; i < workerCount; i++)
                {
                    int workerId = i;
                    var (segStart, segEnd) = segments[i];
                    var segFfmpeg = ffmpegProcesses[i]!;
                    var segPlan = mainPlans[i];
                    var segMask = maskProcesses?[i];

                    workerTasks[i] = Task.Run(
                        () =>
                        {
                            try
                            {
                                compositor.RenderSegment(
                                    workerId,
                                    workerCount,
                                    segStart,
                                    segEnd,
                                    totalTicks,
                                    ref completedTicks,
                                    stopwatch,
                                    segFfmpeg,
                                    segPlan,
                                    segMask,
                                    workerToken);
                            }
                            catch
                            {
                                try { linkedCts.Cancel(); } catch { /* ignore */ }
                                throw;
                            }
                        },
                        CancellationToken.None);
                }

                await Task.WhenAll(workerTasks.Where(static t => t is not null).Select(static t => t!)).ConfigureAwait(false);

                for (int i = 0; i < workerCount; i++)
                {
                    FfmpegEncodeCoordinator.TerminateAndDispose(ffmpegProcesses[i], killIfRunning: false);
                    ffmpegProcesses[i] = null;
                    if (maskProcesses is not null)
                    {
                        FfmpegEncodeCoordinator.TerminateAndDispose(maskProcesses[i], killIfRunning: false);
                        maskProcesses[i] = null;
                    }
                }

                if (isMultiSegment)
                {
                    _progress.SetStatus("Stitching Video Segments...");
                    FfmpegEncodeCoordinator.ConcatSegments(_renderOptions, targetOutputFiles, outputFileInfo, runId, isMask: false, _progress, cancellationToken);
                    if (_renderOptions.GenerateMask && targetMaskFiles is not null && maskFileInfo is not null)
                    {
                        FfmpegEncodeCoordinator.ConcatSegments(_renderOptions, targetMaskFiles, maskFileInfo, runId, isMask: true, _progress, cancellationToken);
                    }
                }

                stopwatch.Stop();
                _progress.ReportProgress(100, stopwatch.Elapsed, TimeSpan.Zero);

                double speed = totalTicks / (double)Math.Max(1, _renderOptions.Framerate) / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
                if (isMultiSegment)
                {
                    _progress.LogInfo($"FINISHED. RENDER TIME: {stopwatch.Elapsed.TotalSeconds:F1}s SPEED: {speed:F2}x ({workerCount} parallel workers)");
                }
                else
                {
                    _progress.LogInfo($"FINISHED. RENDER TIME: {stopwatch.Elapsed.TotalSeconds:F1}s SPEED: {speed:F2}x");
                }
            }
            catch
            {
                try { linkedCts.Cancel(); } catch { /* ignore */ }
                throw;
            }
            finally
            {
                for (int i = 0; i < workerCount; i++)
                {
                    FfmpegEncodeCoordinator.TerminateAndDispose(ffmpegProcesses[i], killIfRunning: true);
                    if (maskProcesses is not null)
                    {
                        FfmpegEncodeCoordinator.TerminateAndDispose(maskProcesses[i], killIfRunning: true);
                    }
                }

                try
                {
                    var activeTasks = workerTasks.Where(static t => t is not null).Select(static t => t!).ToArray();
                    if (activeTasks.Length > 0)
                    {
                        await Task.WhenAll(activeTasks).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Worker exceptions are already propagated by the main try block; ensure all segment file handles are released.
                }

                if (isMultiSegment)
                {
                    for (int i = 0; i < workerCount; i++)
                    {
                        FfmpegEncodeCoordinator.DeleteTempFileWithRetry(targetOutputFiles[i]);
                        if (targetMaskFiles?[i] is not null)
                        {
                            FfmpegEncodeCoordinator.DeleteTempFileWithRetry(targetMaskFiles[i]);
                        }
                    }
                }
            }
        }

        private static void EnsureVideoBounds(ChatRoot? root)
        {
            if (root is null)
            {
                return;
            }

            root.comments ??= [];
            root.events ??= [];

            if (root.video is null)
            {
                double minOffset = double.MaxValue;
                double maxOffset = 0;

                foreach (var c in root.comments)
                {
                    if (c is null) continue;
                    if (c.content_offset_seconds < minOffset) minOffset = c.content_offset_seconds;
                    if (c.content_offset_seconds > maxOffset) maxOffset = c.content_offset_seconds;
                }

                foreach (var ev in root.events)
                {
                    if (ev is null) continue;
                    if (ev.content_offset_seconds < minOffset) minOffset = ev.content_offset_seconds;
                    if (ev.content_offset_seconds > maxOffset) maxOffset = ev.content_offset_seconds;
                }

                if (minOffset == double.MaxValue)
                {
                    minOffset = 0;
                }

                root.video = new Video
                {
                    start = Math.Max(0, Math.Floor(minOffset)),
                    end = Math.Max(Math.Max(0, Math.Floor(minOffset)), Math.Ceiling(maxOffset))
                };
            }
            else if (root.video.end <= 0 && root.video.length > 0)
            {
                root.video.end = root.video.length;
            }
        }

        public void Dispose()
        {
            if (Disposed)
            {
                return;
            }

            Disposed = true;
            _assets?.Dispose();
            _assets = null;
            _layout?.Dispose();
            _layout = null;
            chatRoot = null!;
        }
    }
}

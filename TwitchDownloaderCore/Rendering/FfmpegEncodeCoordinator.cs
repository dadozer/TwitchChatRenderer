#nullable enable
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using TwitchDownloaderCore.Interfaces;
using TwitchDownloaderCore.Models.Render;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Rendering.Encoding;
using TwitchDownloaderCore.Tools;

namespace TwitchDownloaderCore.Rendering
{
    internal sealed class QueuedFrame
    {
        public required byte[] FrameBuffer { get; init; }
        public required int FrameLength { get; init; }
        public byte[]? MaskBuffer { get; init; }
        public int MaskLength { get; init; }
        public long Pts { get; init; }
        public int RepeatCount { get; set; } = 1;
        public long Version { get; init; }
    }

    /// <summary>
    /// Coordinates FFmpeg encoder subprocess lifecycle, asynchronous NUT VFR / rawvideo pipe streaming,
    /// drive backpressure throttling, and lossless segment concatenation.
    /// </summary>
    internal static class FfmpegEncodeCoordinator
    {
        private static readonly ConcurrentDictionary<Process, byte> ActiveProcesses = new();

        static FfmpegEncodeCoordinator()
        {
            AppDomain.CurrentDomain.ProcessExit += static (_, _) => KillAllActiveProcesses();
        }

        private static void RegisterActiveProcess(Process? process)
        {
            if (process is not null)
            {
                ActiveProcesses.TryAdd(process, 0);
            }
        }

        private static void UnregisterActiveProcess(Process? process)
        {
            if (process is not null)
            {
                ActiveProcesses.TryRemove(process, out _);
            }
        }

        private static void KillAllActiveProcesses()
        {
            foreach (var kvp in ActiveProcesses)
            {
                try
                {
                    if (!kvp.Key.HasExited)
                    {
                        kvp.Key.Kill(entireProcessTree: true);
                    }
                }
                catch { /* ignore */ }
            }
            ActiveProcesses.Clear();
        }

        public static FfmpegProcess StartEncoderProcess(
            ChatRenderOptions options,
            FileInfo targetFile,
            bool isMask,
            int parallelJobs,
            ITaskProgress progress,
            out EncodingPlan plan)
        {
            string savePath = targetFile.FullName;
            plan = FfmpegPipelineBuilder.BuildEncodingPlan(options, savePath, isMask, parallelJobs);

            var process = new FfmpegProcess
            {
                StartInfo =
                {
                    FileName = options.FfmpegPath,
                    Arguments = plan.FullArguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
                SavePath = savePath
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    progress.LogFfmpeg(e.Data);
                }
            };

            progress.LogVerbose($"Running \"{options.FfmpegPath}\" in \"{process.StartInfo.WorkingDirectory}\" with args: {process.StartInfo.Arguments}");

            process.Start();
            RegisterActiveProcess(process);
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            return process;
        }

        public static Task StartPipeWriterTask(
            ChannelReader<QueuedFrame> reader,
            Stream ffmpegStream,
            Stream? maskStream,
            EncodingPlan mainPlan,
            ChatRenderOptions options,
            int segmentTickCount,
            DriveInfo? outputDrive,
            ITaskProgress progress,
            CancellationToken cancellationToken)
        {
            bool hasMask = maskStream is not null;
            bool useNutPipe = mainPlan.UseNutPipe;
            string mainPixFmt = mainPlan.PipePixFmt;

            return Task.Run(async () =>
            {
                long lastDriveCheckMs = 0;
                NutPipeMuxer? mainNutMuxer = null;
                NutPipeMuxer? maskNutMuxer = null;

                if (useNutPipe)
                {
                    if (!options.SkipDriveWaiting)
                    {
                        DriveHelper.WaitForDriveThrottled(outputDrive, progress, ref lastDriveCheckMs);
                    }

                    mainNutMuxer = new NutPipeMuxer();
                    mainNutMuxer.WriteHeader(
                        ffmpegStream,
                        options.ChatWidth,
                        options.ChatHeight,
                        options.Framerate,
                        NutPipeMuxer.GetFourCc(mainPixFmt));

                    if (hasMask && maskStream is not null)
                    {
                        maskNutMuxer = new NutPipeMuxer();
                        maskNutMuxer.WriteHeader(
                            maskStream,
                            options.ChatWidth,
                            options.ChatHeight,
                            options.Framerate,
                            NutPipeMuxer.GetFourCc("gray"));
                    }
                }

                await foreach (var item in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        if (useNutPipe && mainNutMuxer is not null)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            if (!options.SkipDriveWaiting)
                            {
                                DriveHelper.WaitForDriveThrottled(outputDrive, progress, ref lastDriveCheckMs);
                            }

                            mainNutMuxer.WriteFrame(ffmpegStream, item.Pts, item.FrameBuffer.AsSpan(0, item.FrameLength));

                            if (hasMask && maskNutMuxer is not null && maskStream is not null && item.MaskBuffer is not null)
                            {
                                maskNutMuxer.WriteFrame(maskStream, item.Pts, item.MaskBuffer.AsSpan(0, item.MaskLength));
                            }

                            if (item.RepeatCount > 1 && item.Pts + item.RepeatCount >= segmentTickCount)
                            {
                                long finalPts = item.Pts + item.RepeatCount - 1;
                                mainNutMuxer.WriteFrame(ffmpegStream, finalPts, item.FrameBuffer.AsSpan(0, item.FrameLength));
                                if (hasMask && maskNutMuxer is not null && maskStream is not null && item.MaskBuffer is not null)
                                {
                                    maskNutMuxer.WriteFrame(maskStream, finalPts, item.MaskBuffer.AsSpan(0, item.MaskLength));
                                }
                            }
                        }
                        else
                        {
                            for (int r = 0; r < item.RepeatCount; r++)
                            {
                                cancellationToken.ThrowIfCancellationRequested();

                                if (!options.SkipDriveWaiting)
                                {
                                    DriveHelper.WaitForDriveThrottled(outputDrive, progress, ref lastDriveCheckMs);
                                }

                                ffmpegStream.Write(item.FrameBuffer, 0, item.FrameLength);

                                if (hasMask && maskStream is not null && item.MaskBuffer is not null)
                                {
                                    if (!options.SkipDriveWaiting)
                                    {
                                        DriveHelper.WaitForDriveThrottled(outputDrive, progress, ref lastDriveCheckMs);
                                    }

                                    maskStream.Write(item.MaskBuffer, 0, item.MaskLength);
                                }
                            }
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(item.FrameBuffer);
                        if (item.MaskBuffer is not null)
                        {
                            ArrayPool<byte>.Shared.Return(item.MaskBuffer);
                        }
                    }
                }
            }, cancellationToken);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EnqueueFrameSync(ChannelWriter<QueuedFrame> writer, QueuedFrame frame, CancellationToken cancellationToken)
        {
            if (!writer.TryWrite(frame))
            {
                writer.WriteAsync(frame, cancellationToken).AsTask().GetAwaiter().GetResult();
            }
        }

        public static void WaitForExit(FfmpegProcess? process, CancellationToken cancellationToken)
        {
            if (process is null)
            {
                return;
            }

            const int pollMs = 200;
            const int maxWaitMs = 120_000;
            int waitedMs = 0;

            while (!process.WaitForExit(pollMs))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                    cancellationToken.ThrowIfCancellationRequested();
                }

                waitedMs += pollMs;
                if (waitedMs >= maxWaitMs)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                    throw new TimeoutException($"FFmpeg process did not exit within {maxWaitMs / 1000}s after closing input stream.");
                }
            }

            process.WaitForExit();
        }

        public static void TerminateAndDispose(FfmpegProcess? process, bool killIfRunning)
        {
            if (process is null)
            {
                return;
            }

            UnregisterActiveProcess(process);

            try
            {
                try
                {
                    process.StandardInput?.BaseStream?.Close();
                }
                catch { /* ignore if already closed or disposed */ }

                if (!process.HasExited)
                {
                    if (killIfRunning)
                    {
                        try
                        {
                            process.Kill(entireProcessTree: true);
                        }
                        catch { /* ignore if process exited concurrently */ }
                    }

                    try
                    {
                        process.WaitForExit(5000);
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                            process.WaitForExit(2000);
                        }
                    }
                    catch { /* ignore */ }
                }
            }
            catch { /* ignore if process was never started or already disposed */ }
            finally
            {
                try { process.Dispose(); } catch { /* ignore */ }
            }
        }

        public static void ConcatSegments(
            ChatRenderOptions options,
            IReadOnlyList<FileInfo> segmentFiles,
            FileInfo finalOutputFile,
            string runId,
            bool isMask,
            ITaskProgress progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string dir = finalOutputFile.DirectoryName ?? "";
            string listFilePath = Path.Combine(dir, $".chatrender_concat_{(isMask ? "mask_" : "")}{runId}.txt");

            try
            {
                var sb = new StringBuilder();
                foreach (var seg in segmentFiles)
                {
                    seg.Refresh();
                    if (!seg.Exists || seg.Length == 0)
                    {
                        throw new InvalidOperationException($"Parallel render segment file '{seg.FullName}' is missing or empty.");
                    }

                    string escapedName = seg.Name.Replace("'", "'\\''");
                    sb.Append("file '").Append(escapedName).Append("'\n");
                }

                File.WriteAllText(listFilePath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                using var concatProcess = new Process
                {
                    StartInfo =
                    {
                        FileName = options.FfmpegPath,
                        Arguments = $"-y -hide_banner -loglevel warning -f concat -safe 0 -i \"{listFilePath}\" -c copy \"{finalOutputFile.FullName}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    }
                };

                concatProcess.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is not null)
                    {
                        progress.LogFfmpeg(e.Data);
                    }
                };

                progress.LogVerbose($"Running FFmpeg segment concat with args: {concatProcess.StartInfo.Arguments}");
                concatProcess.Start();
                RegisterActiveProcess(concatProcess);

                try
                {
                    concatProcess.BeginErrorReadLine();
                    concatProcess.BeginOutputReadLine();

                    using var cancelReg = cancellationToken.Register(() =>
                    {
                        try
                        {
                            if (!concatProcess.HasExited)
                            {
                                concatProcess.Kill(entireProcessTree: true);
                            }
                        }
                        catch { /* ignore */ }
                    });

                    while (!concatProcess.WaitForExit(200))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            try { concatProcess.Kill(entireProcessTree: true); } catch { /* ignore */ }
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                    }

                    concatProcess.WaitForExit();
                    if (concatProcess.ExitCode != 0)
                    {
                        throw new InvalidOperationException($"FFmpeg segment concatenation failed with exit code {concatProcess.ExitCode}.");
                    }
                }
                finally
                {
                    UnregisterActiveProcess(concatProcess);
                    try
                    {
                        if (!concatProcess.HasExited)
                        {
                            concatProcess.Kill(entireProcessTree: true);
                        }
                    }
                    catch { /* ignore */ }
                }
            }
            finally
            {
                DeleteTempFileWithRetry(new FileInfo(listFilePath));
            }
        }

        public static void DeleteTempFileWithRetry(FileInfo? fileInfo)
        {
            if (fileInfo is null)
            {
                return;
            }

            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    fileInfo.Refresh();
                    if (!fileInfo.Exists)
                    {
                        return;
                    }

                    fileInfo.Delete();
                    return;
                }
                catch (IOException) when (attempt < 9)
                {
                    Thread.Sleep(50);
                }
                catch (UnauthorizedAccessException) when (attempt < 9)
                {
                    Thread.Sleep(50);
                }
                catch
                {
                    return;
                }
            }
        }
    }
}

#nullable enable
using SkiaSharp;
using System.Text;
using System.Text.RegularExpressions;
using TwitchDownloaderCore.Options;

namespace TwitchDownloaderCore.Rendering.Encoding
{
    /// <summary>
    /// Resolves pipe pixel formats, NUT VFR framing eligibility, VP9 parallel/tile optimizations, and complete FFmpeg
    /// CLI filter chains (including <c>--sharpening</c> <c>smartblur</c> without defeating planar YUV or NUT VFR piping).
    /// </summary>
    public static partial class FfmpegPipelineBuilder
    {
        private const string SharpeningFilterExpr = "smartblur=lr=1:ls=-1.0";

        [GeneratedRegex(@"(?:-cpu-used|-speed)\s+[0-7]\b", RegexOptions.IgnoreCase)]
        private static partial Regex Vp9SlowCpuUsedRegex { get; }

        [GeneratedRegex(@"-threads\s+\d+\b", RegexOptions.IgnoreCase)]
        private static partial Regex FfmpegThreadsRegex { get; }

        [GeneratedRegex(@"-pix_fmt\s+(?<fmt>\S+)", RegexOptions.IgnoreCase)]
        private static partial Regex FfmpegPixFmtRegex { get; }

        [GeneratedRegex(@"(?:-framerate|-pix_fmt|-video_size)\s+\S+\s*", RegexOptions.IgnoreCase)]
        private static partial Regex RawVideoStripFlagsRegex { get; }

        [GeneratedRegex(@"-f\s+rawvideo\b", RegexOptions.IgnoreCase)]
        private static partial Regex RawVideoFormatFlagRegex { get; }

        public static PipePixelFormat ResolvePipePixelFormat(ChatRenderOptions options)
        {
            if (options.ChatWidth <= 0
                || options.ChatHeight <= 0
                || (options.ChatWidth & 1) != 0
                || (options.ChatHeight & 1) != 0)
            {
                return PipePixelFormat.BgraOrRgba;
            }

            string inputArgs = options.InputArgs ?? string.Empty;
            string outputArgs = options.OutputArgs ?? string.Empty;

            if (!inputArgs.Contains("{pix_fmt}", StringComparison.Ordinal)
                || HasExplicitUserFilter(inputArgs)
                || HasExplicitUserFilter(outputArgs))
            {
                return PipePixelFormat.BgraOrRgba;
            }

            if (outputArgs.Contains("-pix_fmt yuva420p", StringComparison.OrdinalIgnoreCase))
            {
                return PipePixelFormat.Yuva420p;
            }

            if (outputArgs.Contains("-pix_fmt yuv420p", StringComparison.OrdinalIgnoreCase))
            {
                return PipePixelFormat.Yuv420p;
            }

            return PipePixelFormat.BgraOrRgba;
        }

        public static bool CanUseNutPipe(ChatRenderOptions options)
        {
            if (options.Framerate <= 0 || options.ChatWidth <= 0 || options.ChatHeight <= 0)
            {
                return false;
            }

            string inputArgs = options.InputArgs ?? string.Empty;
            string outputArgs = options.OutputArgs ?? string.Empty;

            return RawVideoFormatFlagRegex.IsMatch(inputArgs)
                && inputArgs.Contains("-i -", StringComparison.OrdinalIgnoreCase)
                && !HasExplicitUserFilter(inputArgs)
                && !HasExplicitUserFilter(outputArgs);
        }

        public static string GetPipePixFmtString(PipePixelFormat pipeFormat, bool isMask)
        {
            if (isMask)
            {
                return "gray";
            }

            return pipeFormat switch
            {
                PipePixelFormat.Yuva420p => "yuva420p",
                PipePixelFormat.Yuv420p => "yuv420p",
                _ => SKImageInfo.PlatformColorType == SKColorType.Bgra8888 ? "bgra" : "rgba"
            };
        }

        public static EncodingPlan BuildEncodingPlan(ChatRenderOptions options, string savePath, bool isMask = false, int parallelJobs = 1)
        {
            var pipeFormat = ResolvePipePixelFormat(options);
            string pixFmt = GetPipePixFmtString(pipeFormat, isMask);
            bool useNutPipe = CanUseNutPipe(options);
            bool applySharpening = options.Sharpening && !isMask;

            string inputArgs = new StringBuilder(options.InputArgs ?? string.Empty)
                .Replace("-analyzeduration {max_int} -probesize {max_int}", "-analyzeduration 0 -probesize 32 -thread_queue_size 1024")
                .Replace("{fps}", options.Framerate.ToString())
                .Replace("{height}", options.ChatHeight.ToString())
                .Replace("{width}", options.ChatWidth.ToString())
                .Replace("{save_path}", savePath)
                .Replace("{max_int}", int.MaxValue.ToString())
                .Replace("{pix_fmt}", pixFmt)
                .ToString();

            string rawOutputArgs = OptimizeFfmpegOutputArgs(options.OutputArgs ?? string.Empty, options.Framerate, parallelJobs, options.ChatWidth);
            string outputArgs = new StringBuilder(rawOutputArgs)
                .Replace("{fps}", options.Framerate.ToString())
                .Replace("{height}", options.ChatHeight.ToString())
                .Replace("{width}", options.ChatWidth.ToString())
                .Replace("{save_path}", savePath)
                .Replace("{max_int}", int.MaxValue.ToString())
                .ToString();

            if (useNutPipe)
            {
                inputArgs = RawVideoStripFlagsRegex.Replace(inputArgs, string.Empty);
                inputArgs = RawVideoFormatFlagRegex.Replace(inputArgs, "-f nut").Trim();

                // Place format=<outFmt> and optional smartblur BEFORE fps={fps} so swscale and sharpening
                // execute once per unique frame instead of once per duplicated CFR tick.
                var filterParts = new List<string>(3);
                var outPixMatch = FfmpegPixFmtRegex.Match(outputArgs);
                if (outPixMatch.Success && !string.Equals(outPixMatch.Groups["fmt"].Value, pixFmt, StringComparison.OrdinalIgnoreCase))
                {
                    filterParts.Add($"format={outPixMatch.Groups["fmt"].Value}");
                }

                if (applySharpening)
                {
                    filterParts.Add(SharpeningFilterExpr);
                }

                filterParts.Add($"fps={options.Framerate}");
                outputArgs = $"-vf {string.Join(',', filterParts)} {outputArgs}";
            }
            else if (applySharpening
                && !inputArgs.Contains("smartblur", StringComparison.OrdinalIgnoreCase)
                && !outputArgs.Contains("smartblur", StringComparison.OrdinalIgnoreCase)
                && !HasExplicitUserFilter(inputArgs)
                && !HasExplicitUserFilter(outputArgs))
            {
                outputArgs = $"-vf {SharpeningFilterExpr} {outputArgs}";
            }

            return new EncodingPlan(
                pipeFormat,
                pixFmt,
                useNutPipe,
                inputArgs,
                outputArgs,
                $"{inputArgs} {outputArgs}".Trim());
        }

        /// <summary>
        /// Optimizes libvpx-vp9 realtime FFmpeg output flags for high-core-count multi-threaded alpha encoding:
        /// switches from slow RD/realtime modes (-cpu-used 0..7) to fastest Non-RD screen/overlay realtime mode (-cpu-used 8),
        /// disables lookahead buffering (-lag-in-frames 0), sets width-aware tile columns (VP9 minimum tile width is 256px),
        /// enables row/frame parallelism, and sets explicit thread scaling across parallel segment workers.
        /// </summary>
        public static string OptimizeFfmpegOutputArgs(string outputArgs, int framerate = 30, int parallelJobs = 1, int chatWidth = 0)
        {
            if (string.IsNullOrWhiteSpace(outputArgs)
                || !outputArgs.Contains("libvpx-vp9", StringComparison.OrdinalIgnoreCase)
                || outputArgs.Contains("-deadline best", StringComparison.OrdinalIgnoreCase)
                || outputArgs.Contains("-deadline good", StringComparison.OrdinalIgnoreCase)
                || outputArgs.Contains("-quality best", StringComparison.OrdinalIgnoreCase)
                || outputArgs.Contains("-quality good", StringComparison.OrdinalIgnoreCase))
            {
                return outputArgs;
            }

            var optimized = Vp9SlowCpuUsedRegex.Replace(outputArgs, "-cpu-used 8");
            var threadsPerJob = parallelJobs > 1
                ? Math.Clamp(Environment.ProcessorCount / parallelJobs, 2, 16)
                : Math.Clamp(Environment.ProcessorCount, 4, 16);

            if (FfmpegThreadsRegex.IsMatch(optimized))
            {
                optimized = FfmpegThreadsRegex.Replace(optimized, $"-threads {threadsPerJob}");
            }

            var extraFlags = new StringBuilder();
            if (!optimized.Contains("-deadline", StringComparison.OrdinalIgnoreCase)
                && !optimized.Contains("-quality", StringComparison.OrdinalIgnoreCase))
            {
                extraFlags.Append(" -deadline realtime");
            }
            if (!optimized.Contains("-cpu-used", StringComparison.OrdinalIgnoreCase)
                && !optimized.Contains("-speed", StringComparison.OrdinalIgnoreCase))
            {
                extraFlags.Append(" -cpu-used 8");
            }
            if (!optimized.Contains("-threads", StringComparison.OrdinalIgnoreCase))
            {
                extraFlags.Append($" -threads {threadsPerJob}");
            }
            if (!optimized.Contains("-row-mt", StringComparison.OrdinalIgnoreCase))
            {
                extraFlags.Append(" -row-mt 1");
            }
            if (!optimized.Contains("-lag-in-frames", StringComparison.OrdinalIgnoreCase))
            {
                extraFlags.Append(" -lag-in-frames 0");
            }
            if (!optimized.Contains("-tile-columns", StringComparison.OrdinalIgnoreCase))
            {
                int tileColumnsLog2 = chatWidth <= 0 ? 2 : (chatWidth < 512 ? 0 : (chatWidth < 1024 ? 1 : 2));
                extraFlags.Append($" -tile-columns {tileColumnsLog2} -tile-rows 1");
            }
            if (!optimized.Contains("-frame-parallel", StringComparison.OrdinalIgnoreCase))
            {
                extraFlags.Append(" -frame-parallel 1");
            }
            if (!optimized.Contains("-static-thresh", StringComparison.OrdinalIgnoreCase))
            {
                extraFlags.Append(" -static-thresh 1000");
            }
            if (!optimized.Contains("-aq-mode", StringComparison.OrdinalIgnoreCase))
            {
                extraFlags.Append(" -aq-mode 3");
            }
            if (!optimized.Contains("-g ", StringComparison.OrdinalIgnoreCase))
            {
                int gopSize = Math.Clamp(Math.Max(1, framerate) * 8, 120, 600);
                extraFlags.Append($" -g {gopSize}");
            }
            if (!optimized.Contains("-undershoot-pct", StringComparison.OrdinalIgnoreCase))
            {
                extraFlags.Append(" -undershoot-pct 100");
            }

            if (extraFlags.Length > 0)
            {
                var savePathIdx = optimized.IndexOf("\"{save_path}\"", StringComparison.Ordinal);
                if (savePathIdx < 0)
                {
                    savePathIdx = optimized.IndexOf("{save_path}", StringComparison.Ordinal);
                }

                if (savePathIdx > 0)
                {
                    optimized = optimized.Insert(savePathIdx, extraFlags.ToString().TrimStart() + " ");
                }
            }

            return optimized;
        }

        private static bool HasExplicitUserFilter(string args)
        {
            return args.Contains("-vf ", StringComparison.OrdinalIgnoreCase)
                || args.Contains("-filter:v", StringComparison.OrdinalIgnoreCase)
                || args.Contains("-filter_complex", StringComparison.OrdinalIgnoreCase);
        }
    }
}

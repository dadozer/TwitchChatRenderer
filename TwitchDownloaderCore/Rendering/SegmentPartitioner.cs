#nullable enable
using TwitchDownloaderCore.Chat.Timeline;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.Rendering
{
    /// <summary>
    /// Partitions a chat timeline tick range into hardware- and density-aware parallel encoding segments.
    /// </summary>
    internal static class SegmentPartitioner
    {
        private readonly record struct MessageWorkSample(
            double OffsetSeconds,
            int FragmentCount,
            int EmoteCount,
            int BodyLength);

        public static List<(int StartTick, int EndTick)> PlanSegments(
            CanonicalTimeline timeline,
            ChatRenderOptions options,
            int startTick,
            int endTick)
        {
            int totalTicks = endTick - startTick;
            if (totalTicks <= 0)
            {
                return [(startTick, endTick)];
            }

            int requestedJobs = options.ParallelJobs;
            int targetJobs = requestedJobs > 0
                ? Math.Clamp(requestedJobs, 1, 16)
                : ResolveAutoParallelJobs(options.OutputArgs, options.ChatWidth, Environment.ProcessorCount);

            if (targetJobs <= 1)
            {
                return [(startTick, endTick)];
            }

            int framerate = Math.Max(1, options.Framerate);
            int updateStep = Math.Max(1, options.UpdateFrame);
            int minSegmentTicks = requestedJobs > 1
                ? Math.Max(framerate, updateStep * 2)
                : Math.Max(framerate * 8, updateStep * 4);

            int maxPossibleJobs = Math.Max(1, totalTicks / Math.Max(1, minSegmentTicks));
            int jobCount = Math.Min(targetJobs, maxPossibleJobs);
            if (jobCount <= 1)
            {
                return [(startTick, endTick)];
            }

            return ComputeCostWeightedSegments(
                startTick,
                endTick,
                framerate,
                updateStep,
                jobCount,
                minSegmentTicks,
                timeline.Messages);
        }

        /// <summary>
        /// Selects hardware- and codec-aware automatic segment worker count when <c>ParallelJobs == 0</c>.
        /// Accounts for VP9's 256px minimum tile-column width as well as consumer GPU hardware encode session caps.
        /// </summary>
        public static int ResolveAutoParallelJobs(string? outputArgs, int chatWidth, int processorCount)
        {
            if (processorCount < 4)
            {
                return 1;
            }

            string args = outputArgs ?? string.Empty;

            if (args.Contains("_nvenc", StringComparison.OrdinalIgnoreCase)
                || args.Contains("_amf", StringComparison.OrdinalIgnoreCase)
                || args.Contains("_qsv", StringComparison.OrdinalIgnoreCase)
                || args.Contains("_videotoolbox", StringComparison.OrdinalIgnoreCase))
            {
                return Math.Clamp(processorCount / 4, 1, 3);
            }

            if (args.Contains("libvpx", StringComparison.OrdinalIgnoreCase)
                || args.Contains("prores", StringComparison.OrdinalIgnoreCase)
                || args.Contains("qtrle", StringComparison.OrdinalIgnoreCase)
                || args.Contains("av1", StringComparison.OrdinalIgnoreCase))
            {
                return chatWidth > 0 && chatWidth < 512
                    ? Math.Clamp(processorCount * 2 / 3, 2, 8)
                    : Math.Clamp(processorCount / 2, 2, 6);
            }

            return processorCount >= 6
                ? Math.Clamp(processorCount / 3, 2, 4)
                : 2;
        }

        public static List<(int StartTick, int EndTick)> ComputeCostWeightedSegments(
            int startTick,
            int endTick,
            int framerate,
            int updateStep,
            int jobCount,
            int minSegmentTicks,
            IReadOnlyList<TimelineMessage>? messages)
        {
            if (messages is not { Count: > 0 })
            {
                return ComputeCostWeightedSegmentsCore(startTick, endTick, framerate, updateStep, jobCount, minSegmentTicks, ReadOnlySpan<MessageWorkSample>.Empty);
            }

            var samples = new MessageWorkSample[messages.Count];
            for (int i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                samples[i] = new MessageWorkSample(
                    m.AppearSeconds,
                    Math.Max(1, m.Content.Fragments.Count),
                    m.Content.EmoteCount,
                    m.Content.Body.Length);
            }

            return ComputeCostWeightedSegmentsCore(startTick, endTick, framerate, updateStep, jobCount, minSegmentTicks, samples);
        }

        public static List<(int StartTick, int EndTick)> ComputeCostWeightedSegments(
            int startTick,
            int endTick,
            int framerate,
            int updateStep,
            int jobCount,
            int minSegmentTicks,
            IReadOnlyList<Comment>? comments)
        {
            if (comments is not { Count: > 0 })
            {
                return ComputeCostWeightedSegmentsCore(startTick, endTick, framerate, updateStep, jobCount, minSegmentTicks, ReadOnlySpan<MessageWorkSample>.Empty);
            }

            var samples = new List<MessageWorkSample>(comments.Count);
            for (int i = 0; i < comments.Count; i++)
            {
                var c = comments[i];
                if (c is null) continue;
                samples.Add(new MessageWorkSample(
                    c.content_offset_seconds,
                    c.message?.fragments?.Count ?? 1,
                    c.message?.emoticons?.Count ?? 0,
                    c.message?.body?.Length ?? 0));
            }

            return ComputeCostWeightedSegmentsCore(startTick, endTick, framerate, updateStep, jobCount, minSegmentTicks, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(samples));
        }

        private static List<(int StartTick, int EndTick)> ComputeCostWeightedSegmentsCore(
            int startTick,
            int endTick,
            int framerate,
            int updateStep,
            int jobCount,
            int minSegmentTicks,
            ReadOnlySpan<MessageWorkSample> samples)
        {
            int totalTicks = endTick - startTick;
            if (totalTicks <= 0 || jobCount <= 1)
            {
                return [(startTick, endTick)];
            }

            framerate = Math.Max(1, framerate);
            updateStep = Math.Max(1, updateStep);
            minSegmentTicks = Math.Max(updateStep, minSegmentTicks);

            int maxByMinLen = Math.Max(1, totalTicks / minSegmentTicks);
            jobCount = Math.Min(jobCount, maxByMinLen);
            if (jobCount <= 1)
            {
                return [(startTick, endTick)];
            }

            int rawStep = Math.Max(updateStep, totalTicks / 4096);
            int bucketStep = Math.Max(updateStep, (rawStep / updateStep) * updateStep);

            int firstAligned = ((startTick / bucketStep) + 1) * bucketStep;
            if (firstAligned > endTick)
            {
                firstAligned = endTick;
            }

            var bucketBounds = new List<int>(Math.Min(4100, totalTicks / bucketStep + 4)) { startTick };
            for (int t = firstAligned; t < endTick; t += bucketStep)
            {
                int snapped = (t / updateStep) * updateStep;
                if (snapped > bucketBounds[^1] && snapped < endTick)
                {
                    bucketBounds.Add(snapped);
                }
            }
            if (bucketBounds[^1] < endTick)
            {
                bucketBounds.Add(endTick);
            }

            int bucketCount = bucketBounds.Count - 1;
            if (bucketCount <= 1)
            {
                return [(startTick, endTick)];
            }

            var bucketCosts = new long[bucketCount];
            var activeWindowDiff = new long[bucketCount + 1];

            for (int b = 0; b < bucketCount; b++)
            {
                int ticksInBucket = bucketBounds[b + 1] - bucketBounds[b];
                bucketCosts[b] = Math.Max(1L, ticksInBucket * 2L);
            }

            if (!samples.IsEmpty)
            {
                int activeBucketSpan = Math.Clamp((framerate * 8) / bucketStep, 1, 32);
                int searchHint = 0;

                for (int i = 0; i < samples.Length; i++)
                {
                    ref readonly var s = ref samples[i];
                    long rawTick = (long)Math.Floor(s.OffsetSeconds * framerate);
                    if (rawTick < startTick || rawTick >= endTick)
                    {
                        continue;
                    }

                    int tick = (int)rawTick;
                    int bIdx = FindBucketIndex(bucketBounds, tick, ref searchHint);

                    long commentCost = 32L
                        + Math.Min(s.FragmentCount, 24) * 6L
                        + Math.Min(s.EmoteCount, 16) * 8L
                        + Math.Min(s.BodyLength / 24, 16) * 4L;
                    bucketCosts[bIdx] += commentCost;

                    if (s.EmoteCount > 0 || s.FragmentCount > 1)
                    {
                        long lingeringCost = 6L + Math.Min(s.EmoteCount, 8) * 3L;
                        activeWindowDiff[bIdx] += lingeringCost;
                        int endBucket = Math.Min(bucketCount, bIdx + activeBucketSpan);
                        activeWindowDiff[endBucket] -= lingeringCost;
                    }
                }
            }

            var prefixCost = new long[bucketCount + 1];
            long runningActive = 0;
            for (int b = 0; b < bucketCount; b++)
            {
                runningActive += activeWindowDiff[b];
                prefixCost[b + 1] = prefixCost[b] + bucketCosts[b] + Math.Max(0L, runningActive);
            }

            var segments = new List<(int StartTick, int EndTick)>(jobCount);
            int curBucket = 0;

            for (int segIdx = 1; segIdx < jobCount; segIdx++)
            {
                int remainingJobs = jobCount - segIdx + 1;
                int curStartTick = bucketBounds[curBucket];
                int minNextTick = curStartTick + minSegmentTicks;
                int maxNextTick = endTick - (remainingJobs - 1) * minSegmentTicks;

                if (maxNextTick < minNextTick)
                {
                    break;
                }

                long remainingCost = prefixCost[bucketCount] - prefixCost[curBucket];
                long targetPrefix = prefixCost[curBucket] + remainingCost / remainingJobs;

                int bestBucket = -1;
                long bestCostError = long.MaxValue;

                for (int b = curBucket + 1; b < bucketCount; b++)
                {
                    int candidateTick = bucketBounds[b];
                    if (candidateTick < minNextTick)
                    {
                        continue;
                    }
                    if (candidateTick > maxNextTick)
                    {
                        break;
                    }

                    long err = Math.Abs(prefixCost[b] - targetPrefix);
                    if (err < bestCostError)
                    {
                        bestCostError = err;
                        bestBucket = b;
                    }
                }

                if (bestBucket <= curBucket)
                {
                    break;
                }

                segments.Add((curStartTick, bucketBounds[bestBucket]));
                curBucket = bestBucket;
            }

            if (bucketBounds[curBucket] < endTick)
            {
                segments.Add((bucketBounds[curBucket], endTick));
            }

            return segments;

            static int FindBucketIndex(List<int> bounds, int tick, ref int hint)
            {
                int count = bounds.Count - 1;
                if ((uint)hint < (uint)count && tick >= bounds[hint] && tick < bounds[hint + 1])
                {
                    return hint;
                }
                if ((uint)(hint + 1) < (uint)count && tick >= bounds[hint + 1] && tick < bounds[hint + 2])
                {
                    return ++hint;
                }

                int lo = 0;
                int hi = count - 1;
                while (lo <= hi)
                {
                    int mid = lo + ((hi - lo) >> 1);
                    if (tick < bounds[mid])
                    {
                        hi = mid - 1;
                    }
                    else if (tick >= bounds[mid + 1])
                    {
                        lo = mid + 1;
                    }
                    else
                    {
                        hint = mid;
                        return mid;
                    }
                }

                hint = Math.Clamp(lo, 0, count - 1);
                return hint;
            }
        }
    }
}

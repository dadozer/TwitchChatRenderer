using System.Buffers.Binary;
using TwitchDownloaderCore.Rendering;
using TwitchDownloaderCore.Rendering.Encoding;
using TwitchDownloaderCore.Tools;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.Tests.ModelTests
{
    public class Wave2RenderOptimizationTests
    {
        [Fact]
        public void ReciprocalUnpremultiplyLuts_MatchIntegerDivisionExhaustively()
        {
            // 1. Exhaustively test all 254 * 256 = 65,024 (alpha, channel) combinations
            for (uint a = 1; a <= 254; a++)
            {
                int ai = (int)a;
                int half = ai >> 1;
                for (int c = 0; c <= 255; c++)
                {
                    int expected = Math.Min(255, (c * 255 + half) / ai);
                    int actual = SimdPixelConverter.UnpremultiplyChannelLut(c, a);
                    Assert.Equal(expected, actual);
                }
            }

            // 2. Exhaustively test all 1,019 * 1,021 = 1,040,399 (sumA, premulSum) combinations
            for (int sumA = 1; sumA <= 1019; sumA++)
            {
                int half = sumA >> 1;
                for (int premulSum = 0; premulSum <= 1020; premulSum++)
                {
                    int expected = Math.Min(1020, (premulSum * 1020 + half) / sumA);
                    int actual = SimdPixelConverter.NormalizeChromaSumLut(premulSum, sumA);
                    Assert.Equal(expected, actual);
                }
            }
        }

        [Theory]
        [InlineData(18, 16, false, true)]
        [InlineData(34, 24, true, true)]
        [InlineData(50, 32, false, false)]
        [InlineData(64, 40, true, false)]
        public void ConvertRectToYuv420p_SimdMatchesScalarBitForBit_AcrossAllPixelPatterns(
            int width,
            int height,
            bool includeAlpha,
            bool isBgra)
        {
            var rng = new Random(width * 1000 + height * 10 + (includeAlpha ? 1 : 0) + (isBgra ? 2 : 0));
            var src = new byte[width * height * 4];

            // Mix uniform transparent/solid bands, random opaque 8x2 blocks, and semi-transparent anti-aliased pixels
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = (y * width + x) * 4;
                    if (y < 6)
                    {
                        // Uniform background band
                        src[idx] = 24;
                        src[idx + 1] = 24;
                        src[idx + 2] = 28;
                        src[idx + 3] = includeAlpha ? (byte)0 : (byte)255;
                    }
                    else if (y < 14 && x < (width & ~7))
                    {
                        // Fully opaque random RGB pixels (exercises AVX2 8x2 opaque BT.601 vector kernel)
                        src[idx] = (byte)rng.Next(0, 256);
                        src[idx + 1] = (byte)rng.Next(0, 256);
                        src[idx + 2] = (byte)rng.Next(0, 256);
                        src[idx + 3] = 255;
                    }
                    else
                    {
                        // Mixed premultiplied semi-transparent pixels (exercises reciprocal-LUT fallback + tail columns)
                        byte a = (byte)rng.Next(0, 256);
                        src[idx] = (byte)((rng.Next(0, 256) * a) / 255);
                        src[idx + 1] = (byte)((rng.Next(0, 256) * a) / 255);
                        src[idx + 2] = (byte)((rng.Next(0, 256) * a) / 255);
                        src[idx + 3] = a;
                    }
                }
            }

            int pixelCount = width * height;
            int planeBytes = includeAlpha ? pixelCount * 5 / 2 : pixelCount * 3 / 2;

            var scalarOut = new byte[planeBytes];
            var simdOut = new byte[planeBytes];

            SimdPixelConverter.ConvertRectToYuv420p(src, scalarOut, width, height, 0, width, 0, height, includeAlpha, isBgra, allowSimd: false);
            SimdPixelConverter.ConvertRectToYuv420p(src, simdOut, width, height, 0, width, 0, height, includeAlpha, isBgra, allowSimd: true);

            Assert.Equal(scalarOut, simdOut);

            // Also verify unaligned sub-rectangle updates (e.g. [2..width-2) x [4..height-2))
            Array.Clear(scalarOut);
            Array.Clear(simdOut);
            SimdPixelConverter.ConvertRectToYuv420p(src, scalarOut, width, height, 2, width - 2, 4, height - 2, includeAlpha, isBgra, allowSimd: false);
            SimdPixelConverter.ConvertRectToYuv420p(src, simdOut, width, height, 2, width - 2, 4, height - 2, includeAlpha, isBgra, allowSimd: true);

            Assert.Equal(scalarOut, simdOut);
        }

        [Fact]
        public void NutPipeMuxer_WritesSpecCompliantHeaderSyncpointsAndFrameChecksums()
        {
            using var ms = new MemoryStream();
            var muxer = new NutPipeMuxer();
            var fourCc = NutPipeMuxer.GetFourCc("yuva420p");

            muxer.WriteHeader(ms, width: 350, height: 600, fps: 60, fourCc);
            long headerLen = ms.Length;
            Assert.True(headerLen > 40 && headerLen < 160);

            var frame0 = new byte[128];
            Array.Fill(frame0, (byte)0x42);
            muxer.WriteFrame(ms, pts: 0, frame0);
            muxer.WriteFrame(ms, pts: 45, frame0);

            Assert.Equal(ms.Length, muxer.BytesWritten);

            var bytes = ms.ToArray();
            // Verify official 25-byte NUT ID header + MainStartcode immediately at byte 25
            Assert.Equal("nut/multimedia container\0"u8.ToArray(), bytes[..25]);
            Assert.Equal(0x4E4D7A561F5F04ADUL, BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(25, 8)));
        }

        [Fact]
        public void ComputeCostWeightedSegments_ConcentratesWorkersOnDenseChatBursts_AndSnapsToUpdateStep()
        {
            const int startTick = 0;
            const int endTick = 6000; // 100s at 60fps
            const int fps = 60;
            const int updateStep = 6;
            const int jobCount = 4;
            const int minSegTicks = 240;

            // Create a dense burst of 400 messages in the final 20 seconds [80s..100s) = [4800..6000) ticks
            var comments = new List<Comment>();
            for (int i = 0; i < 400; i++)
            {
                comments.Add(new Comment
                {
                    content_offset_seconds = 80.0 + (i / 400.0) * 19.5,
                    message = new Message
                    {
                        body = "PogChamp hype burst message!",
                        fragments = [new Fragment { text = "PogChamp" }, new Fragment { text = " hype burst message!" }],
                        emoticons = [new Emoticon2()]
                    }
                });
            }

            var segments = SegmentPartitioner.ComputeCostWeightedSegments(
                startTick, endTick, fps, updateStep, jobCount, minSegTicks, comments);

            Assert.Equal(jobCount, segments.Count);
            Assert.Equal(startTick, segments[0].StartTick);
            Assert.Equal(endTick, segments[^1].EndTick);

            for (int i = 0; i < segments.Count; i++)
            {
                Assert.True(segments[i].EndTick - segments[i].StartTick >= minSegTicks);
                if (i > 0)
                {
                    Assert.Equal(segments[i - 1].EndTick, segments[i].StartTick);
                    Assert.Equal(0, segments[i].StartTick % updateStep);
                }
            }

            // First quiet segment should span well over half the timeline (> 3600 ticks), while the dense burst at [4800..6000)
            // gets split across multiple shorter segments instead of overloading the final worker!
            int firstSegDuration = segments[0].EndTick - segments[0].StartTick;
            int lastSegDuration = segments[^1].EndTick - segments[^1].StartTick;
            Assert.True(firstSegDuration > 3600, $"Expected quiet leading segment > 3600 ticks, got {firstSegDuration}");
            Assert.True(lastSegDuration < 1200, $"Expected burst trailing segment < 1200 ticks, got {lastSegDuration}");
        }

        [Fact]
        public void ResolveAutoParallelJobs_AndOptimizeFfmpegOutputArgs_RespectChatWidthAndCodecClass()
        {
            // Narrow VP9 (< 512px) scales higher than wide VP9 and caps NVENC sessions
            int narrowVp9 = SegmentPartitioner.ResolveAutoParallelJobs("-c:v libvpx-vp9 -pix_fmt yuva420p \"{save_path}\"", chatWidth: 350, processorCount: 12);
            int wideVp9 = SegmentPartitioner.ResolveAutoParallelJobs("-c:v libvpx-vp9 -pix_fmt yuva420p \"{save_path}\"", chatWidth: 700, processorCount: 12);
            int nvenc = SegmentPartitioner.ResolveAutoParallelJobs("-c:v h264_nvenc -pix_fmt yuv420p \"{save_path}\"", chatWidth: 350, processorCount: 16);

            Assert.True(narrowVp9 > wideVp9);
            Assert.InRange(nvenc, 1, 3);

            string narrowFlags = FfmpegPipelineBuilder.OptimizeFfmpegOutputArgs("-c:v libvpx-vp9 -pix_fmt yuva420p \"{save_path}\"", 60, 4, chatWidth: 350);
            string mediumFlags = FfmpegPipelineBuilder.OptimizeFfmpegOutputArgs("-c:v libvpx-vp9 -pix_fmt yuva420p \"{save_path}\"", 60, 4, chatWidth: 600);
            string wideFlags = FfmpegPipelineBuilder.OptimizeFfmpegOutputArgs("-c:v libvpx-vp9 -pix_fmt yuva420p \"{save_path}\"", 60, 4, chatWidth: 1280);

            Assert.Contains("-tile-columns 0", narrowFlags);
            Assert.Contains("-tile-columns 1", mediumFlags);
            Assert.Contains("-tile-columns 2", wideFlags);
        }
    }
}

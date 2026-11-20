using SkiaSharp;
using TwitchDownloaderCore.Models.Render;
using TwitchDownloaderCore.Rendering;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.Tests.ModelTests
{
    public class Wave1RenderOptimizationTests
    {
        // 2x2 RGBA PNG for TwitchEmote tests
        private const string PNG_2X2 = "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVR4nGP8z8Dwn4GBgYGJAQoAHxcCAk+Uzr4AAAAASUVORK5CYII=";

        [Fact]
        public void CommentSectionRelease_IsIdempotent_AndSectionImageCacheRespectsCap()
        {
            using var cache = new SectionImageCache();
            var rented = cache.Rent(200, 40);

            var section = new CommentSection
            {
                Image = rented,
                Emotes = [],
                CommentIndex = 0
            };

            Assert.False(section.IsReleased);
            section.Release(cache);
            Assert.True(section.IsReleased);
            Assert.Equal(1, cache.GetPooledCount(200, 40));

            // Releasing a second time must not double-return the same bitmap into the pool
            section.Release(cache);
            Assert.Equal(1, cache.GetPooledCount(200, 40));

            // Tall multi-line sections should cap at 48 pooled instances
            var tallItems = new List<SectionImage>();
            for (int i = 0; i < 60; i++)
            {
                tallItems.Add(cache.Rent(200, 160));
            }
            foreach (var item in tallItems)
            {
                cache.Return(item);
            }

            Assert.Equal(48, cache.GetPooledCount(200, 160));
        }

        [Fact]
        public void AppendAndCoalesceDirtyRect_MergesTransitiveOverlapsIntoDisjointSet()
        {
            var rects = new List<DirtyRect>();

            SimdPixelConverter.AppendAndCoalesceDirtyRect(rects, new DirtyRect(10, 10, 30, 30));
            SimdPixelConverter.AppendAndCoalesceDirtyRect(rects, new DirtyRect(50, 10, 70, 30));
            SimdPixelConverter.AppendAndCoalesceDirtyRect(rects, new DirtyRect(100, 100, 128, 128));
            Assert.Equal(3, rects.Count);

            // Bridge rect [28..52] overlaps both first and second rects; must coalesce transitively into [10, 10, 70, 30]
            SimdPixelConverter.AppendAndCoalesceDirtyRect(rects, new DirtyRect(28, 12, 52, 28));

            Assert.Equal(2, rects.Count);
            Assert.Contains(new DirtyRect(10, 10, 70, 30), rects);
            Assert.Contains(new DirtyRect(100, 100, 128, 128), rects);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ConvertRectToYuv420p_MatchesFullFrameConversionBitForBit(bool includeAlpha)
        {
            const int width = 64;
            const int height = 64;
            bool isBgra = SKImageInfo.PlatformColorType == SKColorType.Bgra8888;
            int pixelCount = width * height;
            int planeBytes = includeAlpha ? pixelCount * 5 / 2 : pixelCount * 3 / 2;

            using var baseBmp = new SKBitmap(width, height);
            using (var c = new SKCanvas(baseBmp))
            {
                c.Clear(new SKColor(18, 18, 22, includeAlpha ? (byte)0 : (byte)255));
                using var p = new SKPaint { Color = new SKColor(120, 200, 80, 220), IsAntialias = true };
                c.DrawCircle(20, 20, 8, p);
                c.Flush();
            }

            using var updatedBmp = baseBmp.Copy();
            using (var c = new SKCanvas(updatedBmp))
            {
                using var p = new SKPaint { Color = new SKColor(240, 60, 150, 190), IsAntialias = true };
                c.DrawRect(34, 26, 12, 14, p);
                c.Flush();
            }

            var expected = new byte[planeBytes];
            SimdPixelConverter.ConvertFrameToYuv420p(updatedBmp.GetPixelSpan(), expected, width, height, 0, height, includeAlpha, isBgra);

            var actual = new byte[planeBytes];
            SimdPixelConverter.ConvertFrameToYuv420p(baseBmp.GetPixelSpan(), actual, width, height, 0, height, includeAlpha, isBgra);
            SimdPixelConverter.ConvertRectToYuv420p(updatedBmp.GetPixelSpan(), actual, width, height, 32, 48, 24, 42, includeAlpha, isBgra);

            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ShiftYuvPlanesUp_MatchesFullFrameScrollConversionBitForBit(bool includeAlpha)
        {
            const int width = 48;
            const int height = 64;
            const int stickyTop = 8;
            const int scrollDeltaY = 12;
            const int dirtyTopY = height - scrollDeltaY;
            bool isBgra = SKImageInfo.PlatformColorType == SKColorType.Bgra8888;
            int pixelCount = width * height;
            int planeBytes = includeAlpha ? pixelCount * 5 / 2 : pixelCount * 3 / 2;

            using var frame0 = new SKBitmap(width, height);
            using (var c = new SKCanvas(frame0))
            {
                c.Clear(new SKColor(16, 16, 20, includeAlpha ? (byte)0 : (byte)255));
                using var stickyPaint = new SKPaint { Color = new SKColor(40, 80, 200, 245), BlendMode = SKBlendMode.Src };
                c.DrawRect(0, 0, width, stickyTop, stickyPaint);

                using var rowPaint = new SKPaint { Color = new SKColor(220, 180, 60, 255) };
                c.DrawRect(4, 24, 36, 10, rowPaint);
                c.DrawRect(6, 44, 28, 12, rowPaint);
                c.Flush();
            }

            using var frame1 = new SKBitmap(width, height);
            using (var c = new SKCanvas(frame1))
            {
                c.Clear(new SKColor(16, 16, 20, includeAlpha ? (byte)0 : (byte)255));
                using var rowPaint = new SKPaint { Color = new SKColor(220, 180, 60, 255) };
                c.DrawRect(4, 24 - scrollDeltaY, 36, 10, rowPaint);
                c.DrawRect(6, 44 - scrollDeltaY, 28, 12, rowPaint);

                using var newRowPaint = new SKPaint { Color = new SKColor(90, 230, 140, 200) };
                c.DrawRect(4, 54, 30, 8, newRowPaint);

                using var stickyPaint = new SKPaint { Color = new SKColor(40, 80, 200, 245), BlendMode = SKBlendMode.Src };
                c.DrawRect(0, 0, width, stickyTop, stickyPaint);
                c.Flush();
            }

            var expected = new byte[planeBytes];
            SimdPixelConverter.ConvertFrameToYuv420p(frame1.GetPixelSpan(), expected, width, height, 0, height, includeAlpha, isBgra);

            var actual = new byte[planeBytes];
            SimdPixelConverter.ConvertFrameToYuv420p(frame0.GetPixelSpan(), actual, width, height, 0, height, includeAlpha, isBgra);
            SimdPixelConverter.ShiftYuvPlanesUp(actual, width, height, stickyTop, dirtyTopY - stickyTop, scrollDeltaY, includeAlpha);
            SimdPixelConverter.ConvertFrameToYuv420p(frame1.GetPixelSpan(), actual, width, height, dirtyTopY, height, includeAlpha, isBgra);

            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void BlitEmoteYuvFrame_MatchesSkiaCanvasDrawImageBitForBit(bool includeAlpha)
        {
            const int width = 32;
            const int height = 32;
            bool isBgra = SKImageInfo.PlatformColorType == SKColorType.Bgra8888;
            int pixelCount = width * height;
            int planeBytes = includeAlpha ? pixelCount * 5 / 2 : pixelCount * 3 / 2;
            var bgColor = includeAlpha ? SKColors.Transparent : new SKColor(24, 24, 27, 255);
            uint bgPixel = SimdPixelConverter.SampleClearedBackgroundPixel(bgColor);

            using var emote = new TwitchEmote(Convert.FromBase64String(PNG_2X2), null, EmoteProvider.ThirdParty, 1, "e1", "e1");
            emote.Scale(4.0); // 8x8 even-sized emote

            using var skiaComposed = new SKBitmap(width, height);
            using (var c = new SKCanvas(skiaComposed))
            {
                c.Clear(bgColor);
                c.DrawImage(emote.EmoteFrames[0], 12, 10);
                c.Flush();
            }

            var expected = new byte[planeBytes];
            SimdPixelConverter.ConvertFrameToYuv420p(skiaComposed.GetPixelSpan(), expected, width, height, 0, height, includeAlpha, isBgra);

            using var blankBase = new SKBitmap(width, height);
            using (var c = new SKCanvas(blankBase))
            {
                c.Clear(bgColor);
                c.Flush();
            }

            using var surfaceCache = new EmoteSurfaceCache();
            var actual = new byte[planeBytes];
            SimdPixelConverter.ConvertFrameToYuv420p(blankBase.GetPixelSpan(), actual, width, height, 0, height, includeAlpha, isBgra);
            surfaceCache.BlitEmoteYuvFrame(
                new DirectYuvEmoteBlit(emote, 0, 12, 10, bgPixel),
                actual,
                width,
                height,
                bgPixel,
                includeAlpha,
                isBgra);

            Assert.Equal(expected, actual);
        }
    }
}

#nullable enable
using SkiaSharp;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore.Rendering
{
    internal readonly struct EmoteYuvFrame(byte[] planes, int width, int height, bool hasAlpha)
    {
        public readonly byte[] Planes = planes;
        public readonly int Width = width;
        public readonly int Height = height;
        public readonly bool HasAlpha = hasAlpha;
    }

    internal readonly record struct DirectYuvEmoteBlit(
        TwitchEmote Emote,
        int FrameIndex,
        int DrawX,
        int DrawY,
        uint BgPixel = 0);

    /// <summary>
    /// Thread-safe multi-background planar YUV420P / YUVA420P surface cache for animated emotes.
    /// Eliminates upward coupling from <see cref="TwitchEmote"/> to the renderer and supports multiple simultaneous
    /// row background colors (standard, alternating row, highlighted user) without cache thrashing.
    /// Automatically invalidates entries when <see cref="TwitchEmote.DecodeGeneration"/> advances on LRU eviction or resize.
    /// </summary>
    internal sealed class EmoteSurfaceCache : IDisposable
    {
        private readonly record struct SurfaceCacheKey(
            TwitchEmote Emote,
            uint BgPixel,
            bool IncludeAlpha,
            bool IsBgra)
        {
            public bool Equals(SurfaceCacheKey other) =>
                ReferenceEquals(Emote, other.Emote)
                && BgPixel == other.BgPixel
                && IncludeAlpha == other.IncludeAlpha
                && IsBgra == other.IsBgra;

            public override int GetHashCode() =>
                HashCode.Combine(RuntimeHelpers.GetHashCode(Emote), BgPixel, IncludeAlpha, IsBgra);
        }

        private sealed class CachedSurfaceSet
        {
            public readonly object SyncLock = new();
            public volatile EmoteYuvFrame[]? Frames;
            public volatile int Generation = -1;
        }

        private readonly ConcurrentDictionary<SurfaceCacheKey, CachedSurfaceSet> _entries = new();

        public int CachedVariantCount => _entries.Count;

        public EmoteYuvFrame[] GetOrCreateYuvFrames(TwitchEmote emote, uint bgPixel, bool includeAlpha, bool isBgra)
        {
            var key = new SurfaceCacheKey(emote, bgPixel, includeAlpha, isBgra);
            var holder = _entries.GetOrAdd(key, static _ => new CachedSurfaceSet());

            int expectedGen = emote.DecodeGeneration;
            var existing = holder.Frames;
            if (existing is not null && holder.Generation == expectedGen)
            {
                return existing;
            }

            lock (holder.SyncLock)
            {
                expectedGen = emote.DecodeGeneration;
                if (holder.Frames is not null && holder.Generation == expectedGen)
                {
                    return holder.Frames;
                }

                var skFrames = emote.EmoteFrames;
                expectedGen = emote.DecodeGeneration;
                int w = emote.Width;
                int h = emote.Height;
                int pixelCount = w * h;
                int planeBytes = includeAlpha ? pixelCount * 5 / 2 : pixelCount * 3 / 2;

                var yuvFrames = new EmoteYuvFrame[skFrames.Length];
                using var scratch = new SKBitmap(w, h);
                using var canvas = new SKCanvas(scratch);

                for (int i = 0; i < skFrames.Length; i++)
                {
                    unsafe
                    {
                        new Span<uint>((void*)scratch.GetPixels(), pixelCount).Fill(bgPixel);
                    }
                    scratch.NotifyPixelsChanged();

                    canvas.DrawImage(skFrames[i], 0, 0);
                    canvas.Flush();

                    var planes = new byte[planeBytes];
                    SimdPixelConverter.ConvertRectToYuv420p(scratch.GetPixelSpan(), planes, w, h, 0, w, 0, h, includeAlpha, isBgra);
                    yuvFrames[i] = new EmoteYuvFrame(planes, w, h, includeAlpha);
                }

                holder.Generation = expectedGen;
                holder.Frames = yuvFrames;
                return yuvFrames;
            }
        }

        public unsafe void BlitEmoteYuvFrame(
            DirectYuvEmoteBlit blit,
            Span<byte> dstPlanes,
            int frameWidth,
            int frameHeight,
            uint fallbackBgPixel,
            bool includeAlpha,
            bool isBgra)
        {
            uint effectiveBgPixel = blit.BgPixel != 0 || fallbackBgPixel == 0 ? blit.BgPixel : fallbackBgPixel;
            var yuvFrames = GetOrCreateYuvFrames(blit.Emote, effectiveBgPixel, includeAlpha, isBgra);
            if ((uint)blit.FrameIndex >= (uint)yuvFrames.Length)
            {
                return;
            }

            var yuv = yuvFrames[blit.FrameIndex];
            int ew = yuv.Width;
            int eh = yuv.Height;
            int dx = blit.DrawX;
            int dy = blit.DrawY;

            int framePixels = frameWidth * frameHeight;
            int frameUvWidth = frameWidth >> 1;
            int frameUvSize = frameUvWidth * (frameHeight >> 1);

            int ePixels = ew * eh;
            int ewUv = ew >> 1;
            int ehUv = eh >> 1;
            int eUvSize = ewUv * ehUv;
            int dxUv = dx >> 1;
            int dyUv = dy >> 1;

            fixed (byte* pSrc = yuv.Planes)
            fixed (byte* pDst = dstPlanes)
            {
                byte* pSrcY = pSrc;
                byte* pSrcU = pSrc + ePixels;
                byte* pSrcV = pSrcU + eUvSize;

                byte* pDstY = pDst + (long)dy * frameWidth + dx;
                byte* pDstU = pDst + framePixels + (long)dyUv * frameUvWidth + dxUv;
                byte* pDstV = pDst + framePixels + frameUvSize + (long)dyUv * frameUvWidth + dxUv;

                for (int r = 0; r < eh; r++)
                {
                    Buffer.MemoryCopy(pSrcY + (long)r * ew, pDstY + (long)r * frameWidth, ew, ew);
                }

                for (int r = 0; r < ehUv; r++)
                {
                    Buffer.MemoryCopy(pSrcU + (long)r * ewUv, pDstU + (long)r * frameUvWidth, ewUv, ewUv);
                    Buffer.MemoryCopy(pSrcV + (long)r * ewUv, pDstV + (long)r * frameUvWidth, ewUv, ewUv);
                }

                if (includeAlpha && yuv.HasAlpha)
                {
                    byte* pSrcA = pSrcV + eUvSize;
                    byte* pDstA = pDst + framePixels + frameUvSize * 2 + (long)dy * frameWidth + dx;
                    for (int r = 0; r < eh; r++)
                    {
                        Buffer.MemoryCopy(pSrcA + (long)r * ew, pDstA + (long)r * frameWidth, ew, ew);
                    }
                }
            }
        }

        public void Clear() => _entries.Clear();

        public void Dispose() => _entries.Clear();
    }
}

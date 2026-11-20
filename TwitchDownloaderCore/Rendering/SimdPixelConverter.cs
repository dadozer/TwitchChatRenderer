#nullable enable
using SkiaSharp;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace TwitchDownloaderCore.Rendering
{
    internal readonly record struct DirtyRect(int Left, int Top, int Right, int Bottom);

    /// <summary>
    /// Hardware-accelerated SIMD pixel format conversion, planar YUV420P/YUVA420P sub-rectangle conversion,
    /// chroma-aligned planar scrolling, 8-bit alpha mask extraction, and 2D dirty-rectangle coalescing.
    /// </summary>
    internal static class SimdPixelConverter
    {
        private static readonly (ulong[] Mul, ulong[] Bias) AlphaUnpremulLut = BuildRecipLut(maxDivisor: 254, scale: 255UL);
        private static readonly (ulong[] Mul, ulong[] Bias) SumAUnpremulLut = BuildRecipLut(maxDivisor: 1019, scale: 1020UL);

        private static (ulong[] Mul, ulong[] Bias) BuildRecipLut(int maxDivisor, ulong scale)
        {
            var mul = new ulong[maxDivisor + 2];
            var bias = new ulong[maxDivisor + 2];
            for (int d = 1; d <= maxDivisor; d++)
            {
                ulong ud = (ulong)d;
                ulong recip = ((1UL << 32) + ud - 1UL) / ud;
                mul[d] = scale * recip;
                bias[d] = (ulong)(d >> 1) * recip;
            }
            return (mul, bias);
        }

        public static uint SampleClearedBackgroundPixel(SKColor color)
        {
            using var sample = new SKBitmap(1, 1);
            using var canvas = new SKCanvas(sample);
            canvas.Clear(color);
            canvas.Flush();
            return MemoryMarshal.Read<uint>(sample.GetPixelSpan());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int UnpremultiplyChannelLut(int c, uint a)
        {
            if (a is 0 or >= 255)
            {
                return c;
            }

            int res = (int)(((ulong)c * AlphaUnpremulLut.Mul[a] + AlphaUnpremulLut.Bias[a]) >> 32);
            return res > 255 ? 255 : res;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int NormalizeChromaSumLut(int premulSum, int sumA)
        {
            if (sumA <= 0 || sumA >= 1020)
            {
                return premulSum;
            }

            int res = (int)(((ulong)premulSum * SumAUnpremulLut.Mul[sumA] + SumAUnpremulLut.Bias[sumA]) >> 32);
            return res > 1020 ? 1020 : res;
        }

        /// <summary>
        /// Appends <paramref name="candidate"/> into <paramref name="rects"/>, transitively merging any overlapping or
        /// edge-touching rectangles so the resulting list contains mutually disjoint 2x2-aligned boxes.
        /// </summary>
        public static void AppendAndCoalesceDirtyRect(List<DirtyRect> rects, DirtyRect candidate)
        {
            if (candidate.Right <= candidate.Left || candidate.Bottom <= candidate.Top)
            {
                return;
            }

            bool mergedAny;
            do
            {
                mergedAny = false;
                for (int i = rects.Count - 1; i >= 0; i--)
                {
                    var existing = rects[i];
                    if (candidate.Left <= existing.Right
                        && candidate.Right >= existing.Left
                        && candidate.Top <= existing.Bottom
                        && candidate.Bottom >= existing.Top)
                    {
                        candidate = new DirtyRect(
                            Math.Min(candidate.Left, existing.Left),
                            Math.Min(candidate.Top, existing.Top),
                            Math.Max(candidate.Right, existing.Right),
                            Math.Max(candidate.Bottom, existing.Bottom));
                        rects.RemoveAt(i);
                        mergedAny = true;
                    }
                }
            } while (mergedAny);

            rects.Add(candidate);
        }

        public static unsafe bool IsRectUniformPixel(byte* pSrc, int strideBytes, int x, int y, int width, int height, uint expectedPixel)
        {
            ulong expectedPair = expectedPixel | ((ulong)expectedPixel << 32);
            int pairCount = width >> 1;
            bool hasOddTail = (width & 1) != 0;

            for (int r = 0; r < height; r++)
            {
                byte* pRow = pSrc + (long)(y + r) * strideBytes + (long)x * 4;
                ulong* pPairs = (ulong*)pRow;
                for (int i = 0; i < pairCount; i++)
                {
                    if (pPairs[i] != expectedPair)
                    {
                        return false;
                    }
                }

                if (hasOddTail && *((uint*)(pPairs + pairCount)) != expectedPixel)
                {
                    return false;
                }
            }

            return true;
        }

        public static unsafe void SetFrameMask(ReadOnlySpan<byte> frame, Span<byte> maskBytes)
        {
            Debug.Assert(frame.Length == maskBytes.Length * 4);

            int produced = 0;
            int outCount = maskBytes.Length;

            fixed (byte* pFrame = frame)
            fixed (byte* pMask = maskBytes)
            {
                if (Avx512Vbmi.IsSupported && Avx512F.IsSupported)
                {
                    var permuteIdx = Vector512.Create(
                        (byte)3, 7, 11, 15, 19, 23, 27, 31, 35, 39, 43, 47, 51, 55, 59, 63,
                        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
                    );

                    for (; produced + 16 <= outCount; produced += 16)
                    {
                        var vec = Avx512F.LoadVector512(pFrame + produced * 4);
                        var permuted = Avx512Vbmi.PermuteVar64x8(vec, permuteIdx);
                        Sse2.Store(pMask + produced, permuted.GetLower().GetLower());
                    }
                }
                else if (Avx2.IsSupported)
                {
                    var shuffleMask = Vector256.Create(
                        (byte)3, 7, 11, 15, 0x80, 0x80, 0x80, 0x80,
                        0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80,
                        3, 7, 11, 15, 0x80, 0x80, 0x80, 0x80,
                        0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80
                    );

                    for (; produced + 8 <= outCount; produced += 8)
                    {
                        var vec = Avx.LoadVector256(pFrame + produced * 4);
                        var shuffle = Avx2.Shuffle(vec, shuffleMask);

                        var lo = shuffle.GetLower().AsUInt32().ToScalar();
                        var hi = shuffle.GetUpper().AsUInt32().ToScalar();
                        *(ulong*)(pMask + produced) = lo | ((ulong)hi << 32);
                    }
                }
                else if (Ssse3.IsSupported)
                {
                    var shuffleMask = Vector128.Create(
                        (byte)3, 7, 11, 15,
                        0x80, 0x80, 0x80, 0x80,
                        0x80, 0x80, 0x80, 0x80,
                        0x80, 0x80, 0x80, 0x80
                    );

                    for (; produced + 4 <= outCount; produced += 4)
                    {
                        var vec = Sse2.LoadVector128(pFrame + produced * 4);
                        var shuffle = Ssse3.Shuffle(vec, shuffleMask);
                        *(uint*)(pMask + produced) = shuffle.AsUInt32().ToScalar();
                    }
                }
                else if (AdvSimd.Arm64.IsSupported)
                {
                    var tableIdx = Vector128.Create(
                        (byte)3, 7, 11, 15,
                        0xFF, 0xFF, 0xFF, 0xFF,
                        0xFF, 0xFF, 0xFF, 0xFF,
                        0xFF, 0xFF, 0xFF, 0xFF
                    );

                    for (; produced + 4 <= outCount; produced += 4)
                    {
                        var vec = AdvSimd.LoadVector128(pFrame + produced * 4);
                        var shuffle = AdvSimd.Arm64.VectorTableLookup(vec, tableIdx);
                        *(uint*)(pMask + produced) = shuffle.AsUInt32().ToScalar();
                    }
                }

                byte* pF = pFrame + produced * 4 + 3;
                byte* pM = pMask + produced;
                byte* frameEnd = pFrame + frame.Length;
                for (; pF < frameEnd; pF += 4)
                {
                    *pM++ = *pF;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ConvertFrameToYuv420p(
            ReadOnlySpan<byte> srcFrame,
            Span<byte> dstPlanes,
            int width,
            int height,
            int startY,
            int endY,
            bool includeAlpha,
            bool isBgra)
        {
            ConvertRectToYuv420p(srcFrame, dstPlanes, width, height, 0, width, startY, endY, includeAlpha, isBgra);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<sbyte> CreateRgbaWeights256(sbyte c0, sbyte c1, sbyte c2, sbyte c3)
        {
            uint packed = (byte)c0
                | ((uint)(byte)c1 << 8)
                | ((uint)(byte)c2 << 16)
                | ((uint)(byte)c3 << 24);
            return Vector256.Create(packed).AsSByte();
        }

        /// <summary>
        /// Converts a 2x2-aligned sub-rectangle <c>[startX..endX) x [startY..endY)</c> of a 32-bpp BGRA/RGBA frame
        /// into planar BT.601 YUV420P or YUVA420P using AVX2 (8x2 blocks), Vector128 (4x2 blocks), and LUT-accelerated
        /// scalar fallback on semi-transparent anti-aliased edges.
        /// </summary>
        public static unsafe void ConvertRectToYuv420p(
            ReadOnlySpan<byte> srcFrame,
            Span<byte> dstPlanes,
            int width,
            int height,
            int startX,
            int endX,
            int startY,
            int endY,
            bool includeAlpha,
            bool isBgra,
            bool allowSimd = true)
        {
            startX = Math.Max(0, startX) & ~1;
            endX = Math.Min(width, (endX + 1) & ~1) & ~1;
            startY = Math.Max(0, startY) & ~1;
            endY = Math.Min(height, (endY + 1) & ~1) & ~1;
            if (endX <= startX || endY <= startY)
            {
                return;
            }

            int pixelCount = width * height;
            int uvWidth = width >> 1;
            int uvSize = uvWidth * (height >> 1);

            fixed (byte* pSrc = srcFrame)
            fixed (byte* pDst = dstPlanes)
            {
                byte* pYPlane = pDst;
                byte* pUPlane = pDst + pixelCount;
                byte* pVPlane = pUPlane + uvSize;
                byte* pAPlane = includeAlpha ? pVPlane + uvSize : null;

                int strideBytes = width * 4;

                uint cachedPixel = *(uint*)(pSrc + (long)startY * strideBytes + (long)startX * 4);
                ulong cachedPair = cachedPixel | ((ulong)cachedPixel << 32);
                ComputeUniformBlockYuv(cachedPixel, isBgra, includeAlpha, out ushort cachedY2, out ushort cachedA2, out byte cachedU, out byte cachedV);

                uint cachedY4 = cachedY2 | ((uint)cachedY2 << 16);
                ulong cachedY8 = cachedY4 | ((ulong)cachedY4 << 32);
                uint cachedA4 = cachedA2 | ((uint)cachedA2 << 16);
                ulong cachedA8 = cachedA4 | ((ulong)cachedA4 << 32);
                ushort cachedU2 = (ushort)(cachedU | (cachedU << 8));
                uint cachedU4 = cachedU2 | ((uint)cachedU2 << 16);
                ushort cachedV2 = (ushort)(cachedV | (cachedV << 8));
                uint cachedV4 = cachedV2 | ((uint)cachedV2 << 16);

                bool useAvx2 = allowSimd && Avx2.IsSupported && Sse2.IsSupported;
                bool useVec128 = allowSimd && !useAvx2 && Vector128.IsHardwareAccelerated;

                Vector256<byte> cachedVec256 = default;
                Vector256<byte> alphaMask256 = default;
                Vector256<sbyte> wY1 = default;
                Vector256<sbyte> wY2 = default;
                Vector256<sbyte> wU = default;
                Vector256<sbyte> wV = default;
                Vector256<short> ones16 = default;
                Vector256<ushort> roundY128 = default;
                Vector256<ushort> biasY16 = default;
                Vector256<int> roundUv512 = default;
                Vector256<int> biasUv128 = default;

                if (useAvx2)
                {
                    cachedVec256 = Vector256.Create(cachedPixel).AsByte();
                    alphaMask256 = Vector256.Create(0xFF000000u).AsByte();
                    wY1 = isBgra ? CreateRgbaWeights256(25, 62, 66, 0) : CreateRgbaWeights256(66, 62, 25, 0);
                    wY2 = CreateRgbaWeights256(0, 67, 0, 0);
                    wU = isBgra ? CreateRgbaWeights256(112, -74, -38, 0) : CreateRgbaWeights256(-38, -74, 112, 0);
                    wV = isBgra ? CreateRgbaWeights256(-18, -94, 112, 0) : CreateRgbaWeights256(112, -94, -18, 0);
                    ones16 = Vector256.Create((short)1);
                    roundY128 = Vector256.Create((ushort)128);
                    biasY16 = Vector256.Create((ushort)16);
                    roundUv512 = Vector256.Create(512);
                    biasUv128 = Vector256.Create(128);
                }

                Vector128<byte> cachedVec128 = useVec128 ? Vector128.Create(cachedPixel).AsByte() : default;

                for (int y = startY; y < endY; y += 2)
                {
                    byte* pRow0 = pSrc + (long)y * strideBytes;
                    byte* pRow1 = pRow0 + strideBytes;

                    byte* pY0 = pYPlane + (long)y * width;
                    byte* pY1 = pY0 + width;

                    int uvRowOffset = (y >> 1) * uvWidth;
                    byte* pU = pUPlane + uvRowOffset;
                    byte* pV = pVPlane + uvRowOffset;

                    byte* pA0 = includeAlpha ? pAPlane + (long)y * width : null;
                    byte* pA1 = includeAlpha ? pA0 + width : null;

                    int x = startX;

                    if (useAvx2)
                    {
                        for (; x + 8 <= endX; x += 8)
                        {
                            var row0 = Avx.LoadVector256(pRow0 + x * 4);
                            var row1 = Avx.LoadVector256(pRow1 + x * 4);

                            int eq0 = Avx2.MoveMask(Avx2.CompareEqual(row0, cachedVec256));
                            int eq1 = Avx2.MoveMask(Avx2.CompareEqual(row1, cachedVec256));
                            if ((eq0 & eq1) == -1)
                            {
                                *(ulong*)(pY0 + x) = cachedY8;
                                *(ulong*)(pY1 + x) = cachedY8;
                                int uvIdx = x >> 1;
                                *(uint*)(pU + uvIdx) = cachedU4;
                                *(uint*)(pV + uvIdx) = cachedV4;
                                if (includeAlpha)
                                {
                                    *(ulong*)(pA0 + x) = cachedA8;
                                    *(ulong*)(pA1 + x) = cachedA8;
                                }
                                continue;
                            }

                            uint pFirst = *(uint*)(pRow0 + x * 4);
                            if (Avx2.MoveMask(Avx2.CompareEqual(row0, row1)) == -1)
                            {
                                var candidateVec = Vector256.Create(pFirst).AsByte();
                                if (Avx2.MoveMask(Avx2.CompareEqual(row0, candidateVec)) == -1)
                                {
                                    cachedPixel = pFirst;
                                    cachedPair = pFirst | ((ulong)pFirst << 32);
                                    cachedVec256 = candidateVec;
                                    ComputeUniformBlockYuv(pFirst, isBgra, includeAlpha, out cachedY2, out cachedA2, out cachedU, out cachedV);
                                    cachedY4 = cachedY2 | ((uint)cachedY2 << 16);
                                    cachedY8 = cachedY4 | ((ulong)cachedY4 << 32);
                                    cachedA4 = cachedA2 | ((uint)cachedA2 << 16);
                                    cachedA8 = cachedA4 | ((ulong)cachedA4 << 32);
                                    cachedU2 = (ushort)(cachedU | (cachedU << 8));
                                    cachedU4 = cachedU2 | ((uint)cachedU2 << 16);
                                    cachedV2 = (ushort)(cachedV | (cachedV << 8));
                                    cachedV4 = cachedV2 | ((uint)cachedV2 << 16);

                                    *(ulong*)(pY0 + x) = cachedY8;
                                    *(ulong*)(pY1 + x) = cachedY8;
                                    int uvIdx = x >> 1;
                                    *(uint*)(pU + uvIdx) = cachedU4;
                                    *(uint*)(pV + uvIdx) = cachedV4;
                                    if (includeAlpha)
                                    {
                                        *(ulong*)(pA0 + x) = cachedA8;
                                        *(ulong*)(pA1 + x) = cachedA8;
                                    }
                                    continue;
                                }
                            }

                            bool allOpaque = !includeAlpha
                                || Avx2.MoveMask(Avx2.CompareEqual(Avx2.And(Avx2.And(row0, row1), alphaMask256), alphaMask256)) == -1;

                            if (allOpaque)
                            {
                                var y0Pairs = Avx2.Add(Avx2.MultiplyAddAdjacent(row0, wY1), Avx2.MultiplyAddAdjacent(row0, wY2));
                                var y1Pairs = Avx2.Add(Avx2.MultiplyAddAdjacent(row1, wY1), Avx2.MultiplyAddAdjacent(row1, wY2));
                                var ySum16 = Avx2.HorizontalAdd(y0Pairs, y1Pairs).AsUInt16();
                                var yVal16 = Avx2.Add(Avx2.ShiftRightLogical(Avx2.Add(ySum16, roundY128), 8), biasY16);
                                var yPerm = Avx2.Permute4x64(yVal16.AsUInt64(), 0xD8);
                                var yPacked = Sse2.PackUnsignedSaturate(yPerm.GetLower().AsInt16(), yPerm.GetUpper().AsInt16());

                                *(ulong*)(pY0 + x) = yPacked.AsUInt64().ToScalar();
                                *(ulong*)(pY1 + x) = yPacked.AsUInt64().GetElement(1);

                                var uColSum = Avx2.Add(
                                    Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(row0, wU), ones16),
                                    Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(row1, wU), ones16));
                                var vColSum = Avx2.Add(
                                    Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(row0, wV), ones16),
                                    Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(row1, wV), ones16));

                                var uvSum32 = Avx2.HorizontalAdd(uColSum, vColSum);
                                var uvVal32 = Avx2.Add(Avx2.ShiftRightArithmetic(Avx2.Add(uvSum32, roundUv512), 10), biasUv128);
                                var uvPerm = Avx2.Permute4x64(uvVal32.AsUInt64(), 0xD8).AsInt32();
                                var uv16 = Sse2.PackSignedSaturate(uvPerm.GetLower(), uvPerm.GetUpper());
                                ulong uvPacked = Sse2.PackUnsignedSaturate(uv16, uv16).AsUInt64().ToScalar();

                                int uvIdx = x >> 1;
                                *(uint*)(pU + uvIdx) = (uint)uvPacked;
                                *(uint*)(pV + uvIdx) = (uint)(uvPacked >> 32);

                                if (includeAlpha)
                                {
                                    *(ulong*)(pA0 + x) = ulong.MaxValue;
                                    *(ulong*)(pA1 + x) = ulong.MaxValue;
                                }
                                continue;
                            }

                            for (int subX = x; subX < x + 8; subX += 2)
                            {
                                ConvertBlock2x2Scalar(
                                    pRow0, pRow1, pY0, pY1, pU, pV, pA0, pA1, subX, includeAlpha, isBgra,
                                    ref cachedPixel, ref cachedPair, ref cachedY2, ref cachedA2, ref cachedU, ref cachedV);
                            }
                            cachedVec256 = Vector256.Create(cachedPixel).AsByte();
                            cachedY4 = cachedY2 | ((uint)cachedY2 << 16);
                            cachedY8 = cachedY4 | ((ulong)cachedY4 << 32);
                            cachedA4 = cachedA2 | ((uint)cachedA2 << 16);
                            cachedA8 = cachedA4 | ((ulong)cachedA4 << 32);
                            cachedU2 = (ushort)(cachedU | (cachedU << 8));
                            cachedU4 = cachedU2 | ((uint)cachedU2 << 16);
                            cachedV2 = (ushort)(cachedV | (cachedV << 8));
                            cachedV4 = cachedV2 | ((uint)cachedV2 << 16);
                        }
                    }
                    else if (useVec128)
                    {
                        for (; x + 4 <= endX; x += 4)
                        {
                            var row0 = Vector128.Load(pRow0 + x * 4);
                            var row1 = Vector128.Load(pRow1 + x * 4);
                            if (row0 == cachedVec128 && row1 == cachedVec128)
                            {
                                *(uint*)(pY0 + x) = cachedY4;
                                *(uint*)(pY1 + x) = cachedY4;
                                int uvIdx = x >> 1;
                                *(ushort*)(pU + uvIdx) = cachedU2;
                                *(ushort*)(pV + uvIdx) = cachedV2;
                                if (includeAlpha)
                                {
                                    *(uint*)(pA0 + x) = cachedA4;
                                    *(uint*)(pA1 + x) = cachedA4;
                                }
                                continue;
                            }

                            ConvertBlock2x2Scalar(
                                pRow0, pRow1, pY0, pY1, pU, pV, pA0, pA1, x, includeAlpha, isBgra,
                                ref cachedPixel, ref cachedPair, ref cachedY2, ref cachedA2, ref cachedU, ref cachedV);
                            ConvertBlock2x2Scalar(
                                pRow0, pRow1, pY0, pY1, pU, pV, pA0, pA1, x + 2, includeAlpha, isBgra,
                                ref cachedPixel, ref cachedPair, ref cachedY2, ref cachedA2, ref cachedU, ref cachedV);

                            cachedVec128 = Vector128.Create(cachedPixel).AsByte();
                            cachedY4 = cachedY2 | ((uint)cachedY2 << 16);
                            cachedA4 = cachedA2 | ((uint)cachedA2 << 16);
                            cachedU2 = (ushort)(cachedU | (cachedU << 8));
                            cachedV2 = (ushort)(cachedV | (cachedV << 8));
                        }
                    }

                    for (; x < endX; x += 2)
                    {
                        ConvertBlock2x2Scalar(
                            pRow0, pRow1, pY0, pY1, pU, pV, pA0, pA1, x, includeAlpha, isBgra,
                            ref cachedPixel, ref cachedPair, ref cachedY2, ref cachedA2, ref cachedU, ref cachedV);
                    }

                    if (useAvx2)
                    {
                        cachedVec256 = Vector256.Create(cachedPixel).AsByte();
                        cachedY4 = cachedY2 | ((uint)cachedY2 << 16);
                        cachedY8 = cachedY4 | ((ulong)cachedY4 << 32);
                        cachedA4 = cachedA2 | ((uint)cachedA2 << 16);
                        cachedA8 = cachedA4 | ((ulong)cachedA4 << 32);
                        cachedU2 = (ushort)(cachedU | (cachedU << 8));
                        cachedU4 = cachedU2 | ((uint)cachedU2 << 16);
                        cachedV2 = (ushort)(cachedV | (cachedV << 8));
                        cachedV4 = cachedV2 | ((uint)cachedV2 << 16);
                    }
                    else if (useVec128)
                    {
                        cachedVec128 = Vector128.Create(cachedPixel).AsByte();
                        cachedY4 = cachedY2 | ((uint)cachedY2 << 16);
                        cachedA4 = cachedA2 | ((uint)cachedA2 << 16);
                        cachedU2 = (ushort)(cachedU | (cachedU << 8));
                        cachedV2 = (ushort)(cachedV | (cachedV << 8));
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void ConvertBlock2x2Scalar(
            byte* pRow0,
            byte* pRow1,
            byte* pY0,
            byte* pY1,
            byte* pU,
            byte* pV,
            byte* pA0,
            byte* pA1,
            int x,
            bool includeAlpha,
            bool isBgra,
            ref uint cachedPixel,
            ref ulong cachedPair,
            ref ushort cachedY2,
            ref ushort cachedA2,
            ref byte cachedU,
            ref byte cachedV)
        {
            ulong pair0 = *(ulong*)(pRow0 + x * 4);
            ulong pair1 = *(ulong*)(pRow1 + x * 4);

            if (((pair0 ^ cachedPair) | (pair1 ^ cachedPair)) == 0)
            {
                *(ushort*)(pY0 + x) = cachedY2;
                *(ushort*)(pY1 + x) = cachedY2;
                int uvIdx = x >> 1;
                pU[uvIdx] = cachedU;
                pV[uvIdx] = cachedV;
                if (includeAlpha)
                {
                    *(ushort*)(pA0 + x) = cachedA2;
                    *(ushort*)(pA1 + x) = cachedA2;
                }
                return;
            }

            uint p00 = (uint)pair0;
            uint p01 = (uint)(pair0 >> 32);
            if (pair0 == pair1 && p00 == p01)
            {
                cachedPixel = p00;
                cachedPair = pair0;
                ComputeUniformBlockYuv(p00, isBgra, includeAlpha, out cachedY2, out cachedA2, out cachedU, out cachedV);

                *(ushort*)(pY0 + x) = cachedY2;
                *(ushort*)(pY1 + x) = cachedY2;
                int uvIdx = x >> 1;
                pU[uvIdx] = cachedU;
                pV[uvIdx] = cachedV;
                if (includeAlpha)
                {
                    *(ushort*)(pA0 + x) = cachedA2;
                    *(ushort*)(pA1 + x) = cachedA2;
                }
                return;
            }

            uint p10 = (uint)pair1;
            uint p11 = (uint)(pair1 >> 32);

            int r00, g00 = (int)((p00 >> 8) & 0xFF), b00;
            int r01, g01 = (int)((p01 >> 8) & 0xFF), b01;
            int r10, g10 = (int)((p10 >> 8) & 0xFF), b10;
            int r11, g11 = (int)((p11 >> 8) & 0xFF), b11;

            if (isBgra)
            {
                b00 = (int)(p00 & 0xFF); r00 = (int)((p00 >> 16) & 0xFF);
                b01 = (int)(p01 & 0xFF); r01 = (int)((p01 >> 16) & 0xFF);
                b10 = (int)(p10 & 0xFF); r10 = (int)((p10 >> 16) & 0xFF);
                b11 = (int)(p11 & 0xFF); r11 = (int)((p11 >> 16) & 0xFF);
            }
            else
            {
                r00 = (int)(p00 & 0xFF); b00 = (int)((p00 >> 16) & 0xFF);
                r01 = (int)(p01 & 0xFF); b01 = (int)((p01 >> 16) & 0xFF);
                r10 = (int)(p10 & 0xFF); b10 = (int)((p10 >> 16) & 0xFF);
                r11 = (int)(p11 & 0xFF); b11 = (int)((p11 >> 16) & 0xFF);
            }

            int sumR, sumG, sumB;
            if (includeAlpha)
            {
                uint a00 = p00 >> 24;
                uint a01 = p01 >> 24;
                uint a10 = p10 >> 24;
                uint a11 = p11 >> 24;
                int sumA = (int)(a00 + a01 + a10 + a11);

                int premulSumR = r00 + r01 + r10 + r11;
                int premulSumG = g00 + g01 + g10 + g11;
                int premulSumB = b00 + b01 + b10 + b11;

                if (sumA != 1020 && sumA > 0)
                {
                    UnpremultiplyRgb(ref r00, ref g00, ref b00, a00);
                    UnpremultiplyRgb(ref r01, ref g01, ref b01, a01);
                    UnpremultiplyRgb(ref r10, ref g10, ref b10, a10);
                    UnpremultiplyRgb(ref r11, ref g11, ref b11, a11);

                    ulong sumMul = SumAUnpremulLut.Mul[sumA];
                    ulong sumBias = SumAUnpremulLut.Bias[sumA];
                    int nr = (int)(((ulong)premulSumR * sumMul + sumBias) >> 32);
                    int ng = (int)(((ulong)premulSumG * sumMul + sumBias) >> 32);
                    int nb = (int)(((ulong)premulSumB * sumMul + sumBias) >> 32);
                    sumR = nr > 1020 ? 1020 : nr;
                    sumG = ng > 1020 ? 1020 : ng;
                    sumB = nb > 1020 ? 1020 : nb;
                }
                else
                {
                    sumR = premulSumR;
                    sumG = premulSumG;
                    sumB = premulSumB;
                }

                *(ushort*)(pA0 + x) = (ushort)(a00 | (a01 << 8));
                *(ushort*)(pA1 + x) = (ushort)(a10 | (a11 << 8));

                if (sumA == 0)
                {
                    *(ushort*)(pY0 + x) = 0x1010;
                    *(ushort*)(pY1 + x) = 0x1010;
                    int zeroUvOffset = x >> 1;
                    pU[zeroUvOffset] = 128;
                    pV[zeroUvOffset] = 128;
                    return;
                }
            }
            else
            {
                sumR = r00 + r01 + r10 + r11;
                sumG = g00 + g01 + g10 + g11;
                sumB = b00 + b01 + b10 + b11;
            }

            int y00 = ((66 * r00 + 129 * g00 + 25 * b00 + 128) >> 8) + 16;
            int y01 = ((66 * r01 + 129 * g01 + 25 * b01 + 128) >> 8) + 16;
            int y10 = ((66 * r10 + 129 * g10 + 25 * b10 + 128) >> 8) + 16;
            int y11 = ((66 * r11 + 129 * g11 + 25 * b11 + 128) >> 8) + 16;

            *(ushort*)(pY0 + x) = (ushort)(y00 | (y01 << 8));
            *(ushort*)(pY1 + x) = (ushort)(y10 | (y11 << 8));

            int uvOffset = x >> 1;
            pU[uvOffset] = (byte)(((-38 * sumR - 74 * sumG + 112 * sumB + 512) >> 10) + 128);
            pV[uvOffset] = (byte)(((112 * sumR - 94 * sumG - 18 * sumB + 512) >> 10) + 128);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void UnpremultiplyRgb(ref int r, ref int g, ref int b, uint a)
        {
            if (a is > 0 and < 255)
            {
                ulong mul = AlphaUnpremulLut.Mul[a];
                ulong bias = AlphaUnpremulLut.Bias[a];
                int nr = (int)(((ulong)r * mul + bias) >> 32);
                int ng = (int)(((ulong)g * mul + bias) >> 32);
                int nb = (int)(((ulong)b * mul + bias) >> 32);
                r = nr > 255 ? 255 : nr;
                g = ng > 255 ? 255 : ng;
                b = nb > 255 ? 255 : nb;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ComputeUniformBlockYuv(uint pixel, bool isBgra, bool includeAlpha, out ushort y2, out ushort a2, out byte u, out byte v)
        {
            uint a = pixel >> 24;
            a2 = (ushort)(a | (a << 8));

            if (includeAlpha && a == 0)
            {
                y2 = 0x1010;
                u = 128;
                v = 128;
                return;
            }

            int g = (int)((pixel >> 8) & 0xFF);
            int r, b;
            if (isBgra)
            {
                b = (int)(pixel & 0xFF);
                r = (int)((pixel >> 16) & 0xFF);
            }
            else
            {
                r = (int)(pixel & 0xFF);
                b = (int)((pixel >> 16) & 0xFF);
            }

            if (includeAlpha)
            {
                UnpremultiplyRgb(ref r, ref g, ref b, a);
            }

            int yVal = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
            y2 = (ushort)(yVal | (yVal << 8));

            int sumR = r << 2;
            int sumG = g << 2;
            int sumB = b << 2;
            u = (byte)(((-38 * sumR - 74 * sumG + 112 * sumB + 512) >> 10) + 128);
            v = (byte)(((112 * sumR - 94 * sumG - 18 * sumB + 512) >> 10) + 128);
        }

        /// <summary>
        /// Shifts planar Y, U, V, and optional A planes upward in-place by <paramref name="scrollDeltaY"/> rows
        /// when <paramref name="scrollDeltaY"/>, <paramref name="dstTopY"/>, and <paramref name="copyRowsY"/> are even.
        /// </summary>
        public static unsafe void ShiftYuvPlanesUp(
            Span<byte> planes,
            int width,
            int height,
            int dstTopY,
            int copyRowsY,
            int scrollDeltaY,
            bool includeAlpha)
        {
            if (copyRowsY <= 0 || scrollDeltaY <= 0)
            {
                return;
            }

            int srcTopY = dstTopY + scrollDeltaY;
            int pixelCount = width * height;
            int uvWidth = width >> 1;
            int uvSize = uvWidth * (height >> 1);

            fixed (byte* pBase = planes)
            {
                byte* pY = pBase;
                byte* pU = pBase + pixelCount;
                byte* pV = pU + uvSize;

                long yBytes = (long)copyRowsY * width;
                Buffer.MemoryCopy(pY + (long)srcTopY * width, pY + (long)dstTopY * width, yBytes, yBytes);

                int dstTopUv = dstTopY >> 1;
                int srcTopUv = srcTopY >> 1;
                int copyRowsUv = copyRowsY >> 1;
                long uvBytes = (long)copyRowsUv * uvWidth;
                Buffer.MemoryCopy(pU + (long)srcTopUv * uvWidth, pU + (long)dstTopUv * uvWidth, uvBytes, uvBytes);
                Buffer.MemoryCopy(pV + (long)srcTopUv * uvWidth, pV + (long)dstTopUv * uvWidth, uvBytes, uvBytes);

                if (includeAlpha)
                {
                    byte* pA = pV + uvSize;
                    Buffer.MemoryCopy(pA + (long)srcTopY * width, pA + (long)dstTopY * width, yBytes, yBytes);
                }
            }
        }
    }
}

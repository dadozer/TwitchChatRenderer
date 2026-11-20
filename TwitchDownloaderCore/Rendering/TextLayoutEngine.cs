#nullable enable
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using TwitchDownloaderCore.Chat.Timeline;
using TwitchDownloaderCore.Extensions;
using TwitchDownloaderCore.Interfaces;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Models.Render;
using TwitchDownloaderCore.Options;

namespace TwitchDownloaderCore.Rendering
{
    internal readonly record struct UsernameCacheKey(
        string DisplayName,
        string RawColorHex,
        uint BackgroundColor,
        bool AppendColon);

    internal readonly record struct ResolvedUsernameEntry(
        SKColor Color,
        string FormattedLabel,
        int FirstNonAsciiCodepoint);

    /// <summary>
    /// Thread-local rendering context for parallel section producers and segment compositors.
    /// Isolates mutable SkiaSharp <see cref="SKPaint"/> / <see cref="SKFont"/> instances, fallback typefaces,
    /// username contrast caches, and horizontal glyph run buffers per worker thread.
    /// </summary>
    internal sealed class SectionRenderContext : IDisposable
    {
        public required SKPaint MessageFont { get; init; }
        public required SKFont MessageSkFont { get; init; }
        public required SKPaint NameFont { get; init; }
        public required SKPaint OutlinePaint { get; init; }
        public required SKPaint AlternateBackgroundPaint { get; init; }
        public required SKPaint AlternateBackgroundHighlightUserPaint { get; init; }
        public DisposableDictionary<int, SKPaint> FallbackFontCache { get; } = [];
        public DisposableDictionary<SKColor, SKPaint> PaintCache { get; } = [];
        public Dictionary<UsernameCacheKey, ResolvedUsernameEntry> UsernameCache { get; } = new(512);
        public ushort[] LineGlyphBuffer { get; set; } = new ushort[256];
        public float[] LinePosBuffer { get; set; } = new float[256];
        public int PendingGlyphCount { get; set; }
        public int PendingBaselineY { get; set; }
        public SKCanvas? PendingCanvas { get; set; }

        public void Dispose()
        {
            PendingCanvas = null;
            PendingGlyphCount = 0;
            UsernameCache.Clear();
            MessageSkFont?.Dispose();
            MessageFont?.Dispose();
            NameFont?.Dispose();
            OutlinePaint?.Dispose();
            AlternateBackgroundPaint?.Dispose();
            AlternateBackgroundHighlightUserPaint?.Dispose();
            FallbackFontCache.Dispose();
            PaintCache.Dispose();
        }
    }

    /// <summary>
    /// Encapsulates typography initialization, font fallback matching, HarfBuzz RTL shaping,
    /// horizontal <see cref="SKTextBlob"/> glyph batching, username WCAG contrast resolution,
    /// and vector icon drawing without mutating <see cref="ChatRenderOptions"/>.
    /// </summary>
    internal sealed partial class TextLayoutEngine : IDisposable
    {
        private static readonly SKColor[] DefaultUsernameColors =
        [
            new(0xFFFF0000), new(0xFF0000FF), new(0xFF00FF00), new(0xFFB22222), new(0xFFFF7F50),
            new(0xFF9ACD32), new(0xFFFF4500), new(0xFF2E8B57), new(0xFFDAA520), new(0xFFD2691E),
            new(0xFF5F9EA0), new(0xFF1E90FF), new(0xFFFF69B4), new(0xFF8A2BE2), new(0xFF00FF7F)
        ];

        internal static readonly SearchValues<char> WhiteSpaceChars = SearchValues.Create(
            "\t\n\v\f\r\u0020\u0085\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000");

        private static readonly SearchValues<char> DrawTextDelimiters = SearchValues.Create("?-");

        [GeneratedRegex("[\u0591-\u07FF\uFB1D-\uFDFD\uFE70-\uFEFC]")]
        internal static partial Regex RtlRegex { get; }

        [GeneratedRegex("[\u2500-\u257F\u2580-\u259F\u2800-\u28FF]")]
        internal static partial Regex BlockArtRegex { get; }

        private readonly SKFontManager _fontManager = SKFontManager.CreateDefault();
        private readonly DisposableDictionary<int, SKPaint> _sharedInitFallbackCache = [];
        private readonly ThreadLocal<SectionRenderContext> _workerContexts;
        private readonly HashSet<string>? _highlightUsersSet;
        private readonly ITaskLogger? _logger;
        private int _noFallbackFontLogged;
        private bool _disposed;

        public ChatRenderOptions Options { get; }
        public SKPaint BaseMessageFont { get; }
        public SKPaint BaseNameFont { get; }
        public SKPaint BaseOutlinePaint { get; }
        public int UsernameCenteredY { get; }
        public int SectionDefaultYPos { get; }
        public int[] TimestampWidths { get; }
        public float BlockArtCharWidth { get; }
        public double BlockArtPreWrapWidth { get; }
        public bool BlockArtPreWrap { get; }

        public TextLayoutEngine(ChatRenderOptions options, ITaskLogger? logger = null)
        {
            Options = options;
            _logger = logger;

            if (options.HighlightUsersArray is { Length: > 0 })
            {
                _highlightUsersSet = new HashSet<string>(options.HighlightUsersArray, StringComparer.OrdinalIgnoreCase);
            }

            BlockArtPreWrapWidth = 29.166 * options.FontSize - options.SidePadding * 2;
            BlockArtPreWrap = options.ChatWidth > BlockArtPreWrapWidth;
            options.BlockArtPreWrapWidth = BlockArtPreWrapWidth;
            options.BlockArtPreWrap = BlockArtPreWrap;

            BaseOutlinePaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)(options.OutlineSize * options.ReferenceScale),
                StrokeJoin = SKStrokeJoin.Round,
                Color = SKColors.Black,
                IsAntialias = true,
                IsAutohinted = true,
                LcdRenderText = false,
                SubpixelText = true,
                HintingLevel = SKPaintHinting.Full,
                FilterQuality = SKFilterQuality.High
            };

            BaseNameFont = new SKPaint
            {
                LcdRenderText = false,
                SubpixelText = true,
                TextSize = (float)Math.Max(6.0, options.EffectiveUsernameFontSize),
                IsAntialias = true,
                IsAutohinted = true,
                HintingLevel = SKPaintHinting.Full,
                FilterQuality = SKFilterQuality.High
            };

            BaseMessageFont = new SKPaint
            {
                LcdRenderText = false,
                SubpixelText = true,
                TextSize = (float)Math.Max(6.0, options.FontSize),
                IsAntialias = true,
                IsAutohinted = true,
                HintingLevel = SKPaintHinting.Full,
                FilterQuality = SKFilterQuality.High,
                Color = options.MessageColor
            };

            var nameStyle = options.UsernameFontStyle ?? SKFontStyle.Bold;
            var msgStyle = options.MessageFontStyle ?? SKFontStyle.Normal;

            if (ShouldUseEmbeddedInter(options.Font))
            {
                BaseNameFont.Typeface = GetInterTypeface(nameStyle);
                BaseMessageFont.Typeface = GetInterTypeface(msgStyle);
            }
            else
            {
                BaseNameFont.Typeface = SKTypeface.FromFamilyName(options.Font, nameStyle) ?? GetInterTypeface(nameStyle);
                BaseMessageFont.Typeface = SKTypeface.FromFamilyName(options.Font, msgStyle) ?? GetInterTypeface(msgStyle);
            }

            TimestampWidths = !options.Timestamp
                ? Array.Empty<int>()
                :
                [
                    (int)BaseMessageFont.MeasureText("0:00"),
                    (int)BaseMessageFont.MeasureText("00:00"),
                    (int)BaseMessageFont.MeasureText("0:00:00"),
                    (int)BaseMessageFont.MeasureText("00:00:00")
                ];
            options.TimestampWidths = TimestampWidths;

            SKRect usernameBounds = new();
            BaseNameFont.MeasureText("ABC123", ref usernameBounds);
            UsernameCenteredY = (int)(((options.SectionHeight - usernameBounds.Height) / 2.0) + usernameBounds.Height);

            SKRect sampleTextBounds = new();
            BaseMessageFont.MeasureText("ABC123", ref sampleTextBounds);
            SectionDefaultYPos = (int)(((options.SectionHeight - sampleTextBounds.Height) / 2.0) + sampleTextBounds.Height);

            BlockArtCharWidth = GetFallbackFont('█', _sharedInitFallbackCache).MeasureText("█");
            options.BlockArtCharWidth = BlockArtCharWidth;

            _workerContexts = new ThreadLocal<SectionRenderContext>(CreateWorkerContext, trackAllValues: true);
        }

        public SectionRenderContext GetWorkerContext() => _workerContexts.Value!;

        public SectionRenderContext CreateWorkerContext()
        {
            var clonedMessageFont = BaseMessageFont.Clone();
            var altPaint = new SKPaint
            {
                Color = Options.AlternateBackgroundColor,
                BlendMode = SKBlendMode.Src
            };
            var altHighlightPaint = new SKPaint
            {
                Color = Options.HighlightUserColor.CompositeOver(Options.AlternateBackgroundColor),
                BlendMode = SKBlendMode.Src
            };

            return new SectionRenderContext
            {
                MessageFont = clonedMessageFont,
                MessageSkFont = clonedMessageFont.ToFont(),
                NameFont = BaseNameFont.Clone(),
                OutlinePaint = BaseOutlinePaint.Clone(),
                AlternateBackgroundPaint = altPaint,
                AlternateBackgroundHighlightUserPaint = altHighlightPaint
            };
        }

        public bool ShouldUseEmbeddedInter(string? fontName)
        {
            if (string.IsNullOrWhiteSpace(fontName) || fontName.Equals("Inter Embedded", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (fontName.Equals("Inter", StringComparison.OrdinalIgnoreCase))
            {
                using var matched = _fontManager.MatchFamily("Inter");
                return matched is null || !matched.FamilyName.Equals("Inter", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        public static SKTypeface GetInterTypeface(SKFontStyle fontStyle)
        {
            MemoryStream? stream = null;
            try
            {
                if (fontStyle == SKFontStyle.Bold)
                    stream = new MemoryStream(Properties.Resources.InterBold);
                else if (fontStyle == SKFontStyle.Italic)
                    stream = new MemoryStream(Properties.Resources.InterItalic);
                else if (fontStyle == SKFontStyle.BoldItalic)
                    stream = new MemoryStream(Properties.Resources.InterBoldItalic);
                else
                    stream = new MemoryStream(Properties.Resources.Inter);

                return SKTypeface.FromStream(stream);
            }
            finally
            {
                stream?.Dispose();
            }
        }

        public SKColor GetMessageBackground(TimelineMessage message, int messageIndex, SectionRenderContext ctx, out SKPaint? paint)
        {
            var author = message.Author;
            bool isHighlighted = _highlightUsersSet is not null
                && ((!string.IsNullOrEmpty(author.Login) && _highlightUsersSet.Contains(author.Login))
                    || (!string.IsNullOrEmpty(author.DisplayName) && _highlightUsersSet.Contains(author.DisplayName)));

            if (isHighlighted)
            {
                if (Options.AlternateMessageBackgrounds && (messageIndex & 1) == 1)
                {
                    paint = ctx.AlternateBackgroundHighlightUserPaint;
                    return paint.Color;
                }

                paint = GetCachedPaint(Options.HighlightUserColor, ctx.PaintCache);
                return Options.HighlightUserColor;
            }

            if (Options.AlternateMessageBackgrounds && (messageIndex & 1) == 1)
            {
                paint = ctx.AlternateBackgroundPaint;
                return Options.AlternateBackgroundColor;
            }

            paint = null;
            return Options.BackgroundColor;
        }

        public static SKPaint GetCachedPaint(SKColor color, DisposableDictionary<SKColor, SKPaint> paintCache)
        {
            ref var paint = ref CollectionsMarshal.GetValueRefOrAddDefault(paintCache, color, out var alreadyExists);
            if (alreadyExists && paint is not null)
            {
                return paint;
            }

            paint = new SKPaint { Color = color };
            return paint;
        }

        public SKPaint GetFallbackFont(int codepoint, DisposableDictionary<int, SKPaint> fallbackCache)
        {
            ref var fallbackPaint = ref CollectionsMarshal.GetValueRefOrAddDefault(fallbackCache, codepoint, out bool alreadyExists);
            if (alreadyExists && fallbackPaint is not null)
            {
                return fallbackPaint;
            }

            var newPaint = new SKPaint
            {
                Typeface = _fontManager.MatchCharacter(codepoint),
                LcdRenderText = false,
                TextSize = (float)Math.Max(6.0, Options.FontSize),
                IsAntialias = true,
                SubpixelText = true,
                IsAutohinted = true,
                HintingLevel = SKPaintHinting.Full,
                FilterQuality = SKFilterQuality.High
            };

            if (newPaint.Typeface is null)
            {
                newPaint.Typeface = SKTypeface.Default;
                if (Interlocked.Exchange(ref _noFallbackFontLogged, 1) == 0)
                {
                    _logger?.LogWarning("No valid typefaces were found for some messages.");
                    _logger?.LogVerbose($"Could not find typeface for codepoint: {codepoint}");
                }
            }

            fallbackPaint = newPaint;
            return newPaint;
        }

        public void AddImageSection(List<SectionImage> sectionImages, SectionImageCache sectionImageCache, ref Point drawPos, Point defaultPos)
        {
            drawPos.X = defaultPos.X;
            drawPos.Y = defaultPos.Y;
            sectionImages.Add(sectionImageCache.Rent(Options.ChatWidth, Math.Max(1, Options.SectionHeight)));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool NeedsRtlWordReordering(ReadOnlySpan<char> text)
        {
            return text.ContainsAnyInRange('\u0591', '\u07FF');
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsRightToLeft(ReadOnlySpan<char> message)
        {
            return message.Length > 0 && message[0] >= '\u0591' && message[0] <= '\u07FF';
        }

        internal static int SwapRightToLeft(MemoryExtensions.SpanSplitEnumerator<char> enumerator, Span<Range> destination)
        {
            var source = enumerator.Source;
            var rtlStack = ArrayPool<Range>.Shared.Rent(destination.Length);
            var rtlStackPos = 0;
            var destPos = 0;

            try
            {
                foreach (var range in enumerator)
                {
                    if (range.Start.Value == range.End.Value) continue;

                    if (IsRightToLeft(source[range]))
                    {
                        rtlStack[rtlStackPos] = range;
                        rtlStackPos++;
                    }
                    else
                    {
                        EmptyRtlStack(rtlStack, ref rtlStackPos, destination, ref destPos);
                        destination[destPos] = range;
                        destPos++;
                    }
                }

                EmptyRtlStack(rtlStack, ref rtlStackPos, destination, ref destPos);
                return destPos;
            }
            finally
            {
                ArrayPool<Range>.Shared.Return(rtlStack);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void EmptyRtlStack(Span<Range> rtlStack, ref int rtlStackPos, Span<Range> destination, ref int destPos)
            {
                while (rtlStackPos > 0)
                {
                    destination[destPos] = rtlStack[rtlStackPos - 1];
                    rtlStackPos--;
                    destPos++;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SyncOutlinePaintFont(SKPaint sourceFont, SKPaint outlinePaint)
        {
            if (!ReferenceEquals(outlinePaint.Typeface, sourceFont.Typeface))
            {
                outlinePaint.Typeface = sourceFont.Typeface;
            }
            outlinePaint.TextSize = sourceFont.TextSize;
            outlinePaint.TextEncoding = sourceFont.TextEncoding;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void DrawStrokedTextSpan(SKCanvas canvas, ReadOnlySpan<char> text, float x, float y, SKPaint sourceFont, SKPaint outlinePaint)
        {
            SyncOutlinePaintFont(sourceFont, outlinePaint);
            canvas.DrawText(text, x, y, outlinePaint);
        }

        public void FlushPendingLineText(SectionRenderContext ctx)
        {
            int count = ctx.PendingGlyphCount;
            if (count <= 0 || ctx.PendingCanvas is null)
            {
                ctx.PendingGlyphCount = 0;
                ctx.PendingCanvas = null;
                return;
            }

            var canvas = ctx.PendingCanvas;
            int baselineY = ctx.PendingBaselineY;
            ctx.PendingGlyphCount = 0;
            ctx.PendingCanvas = null;

            using var builder = new SKTextBlobBuilder();
            builder.AddHorizontalRun(
                ctx.LineGlyphBuffer.AsSpan(0, count),
                ctx.MessageSkFont,
                ctx.LinePosBuffer.AsSpan(0, count),
                baselineY);

            using var blob = builder.Build();
            if (blob is null)
            {
                return;
            }

            if (Options.Outline)
            {
                SyncOutlinePaintFont(ctx.MessageFont, ctx.OutlinePaint);
                canvas.DrawText(blob, 0, 0, ctx.OutlinePaint);
            }

            canvas.DrawText(blob, 0, 0, ctx.MessageFont);
        }

        public void DrawOrBufferPlainMessageWord(
            ReadOnlySpan<char> word,
            List<SectionImage> sectionImages,
            SectionImageCache sectionImageCache,
            ref Point drawPos,
            Point defaultPos,
            bool highlightWords,
            SectionRenderContext ctx)
        {
            if (word.IsEmpty)
            {
                return;
            }

            if (highlightWords
                || IsRightToLeft(word)
                || (word.ContainsAnyInRange('\u0591', '\uFEFC') && RtlRegex.IsMatch(word)))
            {
                FlushPendingLineText(ctx);
                DrawText(word, ctx.MessageFont, true, sectionImages, sectionImageCache, ref drawPos, defaultPos, highlightWords, ctx);
                return;
            }

            float textWidth = ctx.MessageFont.MeasureText(word);
            int effectiveChatWidth = Options.ChatWidth - Options.SidePadding - defaultPos.X;
            if (textWidth > effectiveChatWidth)
            {
                FlushPendingLineText(ctx);
                DrawText(word, ctx.MessageFont, true, sectionImages, sectionImageCache, ref drawPos, defaultPos, highlightWords, ctx);
                return;
            }

            if (drawPos.X + textWidth > Options.ChatWidth - Options.SidePadding * 2)
            {
                FlushPendingLineText(ctx);
                AddImageSection(sectionImages, sectionImageCache, ref drawPos, defaultPos);
            }

            var targetCanvas = sectionImages[^1].Canvas;
            if (!ReferenceEquals(ctx.PendingCanvas, targetCanvas) || ctx.PendingBaselineY != drawPos.Y)
            {
                FlushPendingLineText(ctx);
                ctx.PendingCanvas = targetCanvas;
                ctx.PendingBaselineY = drawPos.Y;
            }

            int glyphCount = ctx.MessageSkFont.CountGlyphs(word);
            if (glyphCount > 0)
            {
                int requiredLen = ctx.PendingGlyphCount + glyphCount;
                if (requiredLen > ctx.LineGlyphBuffer.Length)
                {
                    int newCapacity = Math.Max(ctx.LineGlyphBuffer.Length * 2, requiredLen);
                    var newGlyphs = new ushort[newCapacity];
                    var newPositions = new float[newCapacity];
                    ctx.LineGlyphBuffer.AsSpan(0, ctx.PendingGlyphCount).CopyTo(newGlyphs);
                    ctx.LinePosBuffer.AsSpan(0, ctx.PendingGlyphCount).CopyTo(newPositions);
                    ctx.LineGlyphBuffer = newGlyphs;
                    ctx.LinePosBuffer = newPositions;
                }

                var glyphSlice = ctx.LineGlyphBuffer.AsSpan(ctx.PendingGlyphCount, glyphCount);
                var posSlice = ctx.LinePosBuffer.AsSpan(ctx.PendingGlyphCount, glyphCount);
                ctx.MessageSkFont.GetGlyphs(word, glyphSlice);
                ctx.MessageSkFont.GetGlyphOffsets(glyphSlice, posSlice, drawPos.X);
                ctx.PendingGlyphCount = requiredLen;
            }

            drawPos.X += (int)Math.Floor(textWidth + Options.WordSpacing);
        }

        public void DrawText(
            ReadOnlySpan<char> drawText,
            SKPaint textFont,
            bool padding,
            List<SectionImage> sectionImages,
            SectionImageCache sectionImageCache,
            ref Point drawPos,
            Point defaultPos,
            bool highlightWords,
            SectionRenderContext ctx,
            bool noWrap = false)
        {
            bool isRtl = IsRightToLeft(drawText);
            float textWidth = MeasureText(drawText, textFont, isRtl);
            int effectiveChatWidth = Math.Max(1, Options.ChatWidth - Options.SidePadding - defaultPos.X);

            while (!noWrap && textWidth > effectiveChatWidth && !drawText.IsEmpty)
            {
                var newDrawText = SubstringToTextWidth(drawText, textFont, effectiveChatWidth, isRtl, DrawTextDelimiters);
                var overrideWrap = false;

                if (newDrawText.Length == 0)
                {
                    overrideWrap = true;
                    var charCount = Math.Max(StringInfo.GetNextTextElementLength(drawText), 1);
                    newDrawText = drawText[..charCount];
                }

                DrawText(newDrawText, textFont, padding, sectionImages, sectionImageCache, ref drawPos, defaultPos, highlightWords, ctx, overrideWrap);

                drawText = drawText[newDrawText.Length..];
                textWidth = MeasureText(drawText, textFont, isRtl);
            }

            if (drawPos.X + textWidth > Options.ChatWidth - Options.SidePadding * 2)
            {
                AddImageSection(sectionImages, sectionImageCache, ref drawPos, defaultPos);
            }

            bool needsShaping = isRtl || (drawText.ContainsAnyInRange('\u0591', '\uFEFC') && RtlRegex.IsMatch(drawText));
            var canvas = sectionImages[^1].Canvas;
            if (highlightWords)
            {
                var paint = GetCachedPaint(AssetCatalog.Purple, ctx.PaintCache);
                canvas.DrawRect(drawPos.X, 0, textWidth + (padding ? Options.WordSpacing : 0), Options.SectionHeight, paint);
            }

            if (Options.Outline)
            {
                if (needsShaping)
                {
                    using var outlinePath = textFont.GetShapedTextPath(drawText, drawPos.X, drawPos.Y);
                    canvas.DrawPath(outlinePath, ctx.OutlinePaint);
                }
                else
                {
                    DrawStrokedTextSpan(canvas, drawText, drawPos.X, drawPos.Y, textFont, ctx.OutlinePaint);
                }
            }

            if (needsShaping)
            {
                canvas.DrawShapedText(drawText.ToString(), drawPos.X, drawPos.Y, textFont);
            }
            else
            {
                canvas.DrawText(drawText, drawPos.X, drawPos.Y, textFont);
            }

            drawPos.X += (int)Math.Floor(textWidth + (padding ? Options.WordSpacing : 0));
        }

        public float DrawSingleLineTextDirect(
            SKCanvas? canvas,
            string? text,
            float x,
            float y,
            SKPaint font,
            SectionRenderContext? ctx,
            AssetCatalog? assets = null)
        {
            if (string.IsNullOrEmpty(text))
            {
                return x;
            }

            if (font.ContainsGlyphs(text))
            {
                if (canvas is not null)
                {
                    if (Options.Outline && ctx?.OutlinePaint is not null)
                    {
                        DrawStrokedTextSpan(canvas, text.AsSpan(), x, y, font, ctx.OutlinePaint);
                    }

                    canvas.DrawText(text, x, y, font);
                }
                return x + font.MeasureText(text);
            }

            Span<char> emojiKeyStack = stackalloc char[16];
            float curX = x;
            int i = 0;
            while (i < text.Length)
            {
                int elemLen = StringInfo.GetNextTextElementLength(text.AsSpan(i));
                if (elemLen <= 0) elemLen = 1;
                ReadOnlySpan<char> elem = text.AsSpan(i, elemLen);

                if (!font.ContainsGlyphs(elem) && assets is not null && elemLen <= emojiKeyStack.Length)
                {
                    int written = elem.CopyToExcept(emojiKeyStack, AssetCatalog.EmojiExcludeChars);
                    if (written > 0 && assets.TryGetEmojiImage(emojiKeyStack[..written], out var emojiImg))
                    {
                        float targetSize = Math.Max(10f, font.TextSize * 1.05f);
                        if (canvas is not null)
                        {
                            float topY = y - targetSize * 0.82f;
                            var destRect = new SKRect(curX, topY, curX + targetSize, topY + targetSize);
                            canvas.DrawImage(emojiImg, destRect);
                        }
                        curX += targetSize + Math.Max(1f, font.TextSize * 0.08f);
                        i += elemLen;
                        continue;
                    }
                }

                SKPaint activeFont = font;
                SKPaint? clonedFallback = null;
                if (!font.ContainsGlyphs(elem) && ctx?.FallbackFontCache is not null)
                {
                    int codepoint = elemLen >= 2 && char.IsHighSurrogate(elem[0]) && char.IsLowSurrogate(elem[1])
                        ? char.ConvertToUtf32(elem[0], elem[1])
                        : elem[0];
                    var fb = GetFallbackFont(codepoint, ctx.FallbackFontCache);
                    clonedFallback = fb.Clone();
                    clonedFallback.TextSize = font.TextSize;
                    clonedFallback.Color = font.Color;
                    activeFont = clonedFallback;
                }

                using (clonedFallback)
                {
                    if (canvas is not null)
                    {
                        if (Options.Outline && ctx?.OutlinePaint is not null)
                        {
                            DrawStrokedTextSpan(canvas, elem, curX, y, activeFont, ctx.OutlinePaint);
                        }

                        canvas.DrawText(elem, curX, y, activeFont);
                    }
                    curX += activeFont.MeasureText(elem);
                }

                i += elemLen;
            }

            return curX;
        }

        public int MeasureFittingCharCount(ReadOnlySpan<char> span, float maxW, SKPaint font, SectionRenderContext ctx, AssetCatalog? assets = null)
        {
            if (span.IsEmpty)
            {
                return 0;
            }

            if (font.ContainsGlyphs(span))
            {
                int count = (int)font.BreakText(span, maxW);
                count = Math.Clamp(count, 1, span.Length);
                if (count > 1 && count < span.Length && char.IsHighSurrogate(span[count - 1]))
                {
                    count--;
                }
                return count;
            }

            string full = span.ToString();
            float accumW = 0f;
            int idx = 0;
            while (idx < full.Length)
            {
                int elemLen = StringInfo.GetNextTextElementLength(full.AsSpan(idx));
                if (elemLen <= 0) elemLen = 1;
                string elemStr = full.Substring(idx, elemLen);
                float elemW = DrawSingleLineTextDirect(null, elemStr, 0f, 0f, font, ctx, assets);
                if (idx > 0 && accumW + elemW > maxW)
                {
                    break;
                }
                accumW += elemW;
                idx += elemLen;
                if (accumW >= maxW)
                {
                    break;
                }
            }

            return Math.Clamp(idx, 1, span.Length);
        }

        public static string TruncateSingleLine(string? text, SKPaint font, int maxWidth)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0)
            {
                return "";
            }

            if (font.MeasureText(text) <= maxWidth)
            {
                return text;
            }

            float ellipsisW = font.MeasureText("…");
            int count = (int)font.BreakText(text.AsSpan(), Math.Max(4, maxWidth - ellipsisW));
            if (count <= 0)
            {
                return "…";
            }

            return string.Concat(text.AsSpan(0, Math.Min(count, text.Length)).TrimEnd(), "…");
        }

        public static float MeasureVectorRightArrowSlot(SKPaint font)
        {
            float padX = Math.Max(4f, font.TextSize * 0.34f);
            float arrowW = Math.Max(8f, font.TextSize * 0.62f);
            return padX * 2f + arrowW;
        }

        public void DrawVectorRightArrow(SKCanvas canvas, float x, float baselineY, SKPaint font, SectionRenderContext ctx)
        {
            float padX = Math.Max(4f, font.TextSize * 0.34f);
            float arrowW = Math.Max(8f, font.TextSize * 0.62f);

            SKRect capBounds = new();
            font.MeasureText("H", ref capBounds);
            float centerY = baselineY - (capBounds.Height > 1f ? capBounds.Height * 0.50f : font.TextSize * 0.36f);

            float startX = x + padX;
            float endX = startX + arrowW;
            float headLen = Math.Max(3.5f, arrowW * 0.42f);
            float headHalfH = Math.Max(3.0f, arrowW * 0.36f);
            float strokeW = Math.Max(1.8f, font.TextSize * 0.11f);

            using var path = new SKPath();
            path.MoveTo(startX, centerY);
            path.LineTo(endX - strokeW * 0.25f, centerY);
            path.MoveTo(endX - headLen, centerY - headHalfH);
            path.LineTo(endX, centerY);
            path.LineTo(endX - headLen, centerY + headHalfH);

            if (Options.Outline && ctx.OutlinePaint is not null)
            {
                using var outlineArrowPaint = new SKPaint
                {
                    Color = ctx.OutlinePaint.Color,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = strokeW + ctx.OutlinePaint.StrokeWidth,
                    StrokeCap = SKStrokeCap.Round,
                    StrokeJoin = SKStrokeJoin.Round,
                    IsAntialias = true
                };
                canvas.DrawPath(path, outlineArrowPaint);
            }

            using var arrowPaint = new SKPaint
            {
                Color = font.Color,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = strokeW,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
                IsAntialias = true
            };
            canvas.DrawPath(path, arrowPaint);
        }

        public void DrawVectorReplyIcon(SKCanvas canvas, float x, float centerY, float size, SKColor color, SectionRenderContext? ctx)
        {
            float strokeW = Math.Max(1.4f, size * 0.14f);
            float leftX = x + size * 0.15f;
            float rightX = x + size * 0.85f;
            float bottomY = centerY + size * 0.32f;
            float topY = centerY - size * 0.18f;
            float headSize = Math.Max(2.5f, size * 0.28f);

            using var path = new SKPath();
            path.MoveTo(leftX, bottomY);
            path.LineTo(leftX, topY);
            path.LineTo(rightX, topY);
            path.MoveTo(rightX - headSize, topY - headSize * 0.8f);
            path.LineTo(rightX, topY);
            path.LineTo(rightX - headSize, topY + headSize * 0.8f);

            if (Options.Outline && ctx?.OutlinePaint is not null)
            {
                using var outlinePaint = new SKPaint
                {
                    Color = ctx.OutlinePaint.Color,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = strokeW + ctx.OutlinePaint.StrokeWidth,
                    StrokeCap = SKStrokeCap.Round,
                    StrokeJoin = SKStrokeJoin.Round,
                    IsAntialias = true
                };
                canvas.DrawPath(path, outlinePaint);
            }

            using var paint = new SKPaint
            {
                Color = color,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = strokeW,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
                IsAntialias = true
            };
            canvas.DrawPath(path, paint);
        }

        internal ResolvedUsernameEntry ResolveAndCacheUsername(
            string? displayName,
            string? rawColorHex,
            SKColor backgroundColor,
            bool appendColon,
            SKPaint currentOutlinePaint,
            Dictionary<UsernameCacheKey, ResolvedUsernameEntry>? cache)
        {
            string safeDisplayName = displayName ?? string.Empty;
            string safeHex = rawColorHex ?? string.Empty;
            uint bgKey = Options.AdjustUsernameVisibility ? (uint)backgroundColor : 0u;
            var key = new UsernameCacheKey(safeDisplayName, safeHex, bgKey, appendColon);

            if (cache is not null && cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var resolvedColor = ResolveUsernameColor(safeHex, safeDisplayName, Options.MessageColor);
            if (Options.AdjustUsernameVisibility)
            {
                resolvedColor = AdjustUsernameVisibility(resolvedColor, backgroundColor, currentOutlinePaint);
            }

            int firstNonAscii = -1;
            foreach (char ch in safeDisplayName)
            {
                if (ch > 127)
                {
                    firstNonAscii = ch;
                    break;
                }
            }

            string label = appendColon ? string.Concat(safeDisplayName, ":") : safeDisplayName;
            var entry = new ResolvedUsernameEntry(resolvedColor, label, firstNonAscii);

            if (cache is not null)
            {
                if (cache.Count >= 8192)
                {
                    cache.Clear();
                }
                cache[key] = entry;
            }

            return entry;
        }

        public static SKColor ResolveUsernameColor(string? rawColorHex, string? displayName, SKColor messageColor)
        {
            if (string.IsNullOrWhiteSpace(rawColorHex) || !SKColor.TryParse(rawColorHex, out var userColor))
            {
                return GetDefaultUsernameColor(displayName);
            }

            if (messageColor.RelativeLuminance() > 0.7)
            {
                if (userColor.Red == 255 && userColor.Green == 255 && userColor.Blue == 255)
                {
                    return GetDefaultUsernameColor(displayName);
                }

                userColor.ToHsl(out var h, out var s, out var l);
                if (s <= 25f && l > 72f)
                {
                    userColor = SKColor.FromHsl(h, s, 72f);
                }
                else if (l > 76f)
                {
                    userColor = SKColor.FromHsl(h, Math.Max(s, 45f), 76f);
                }
            }

            return userColor;
        }

        private static SKColor GetDefaultUsernameColor(string? displayName)
        {
            if (string.IsNullOrEmpty(displayName))
            {
                return DefaultUsernameColors[0];
            }

            uint hash = (uint)displayName.GetHashCode();
            return DefaultUsernameColors[hash % (uint)DefaultUsernameColors.Length];
        }

        public SKColor AdjustUsernameVisibility(SKColor userColor, SKColor backgroundColor, SKPaint currentOutlinePaint)
        {
            const byte OPAQUE_THRESHOLD = byte.MaxValue / 2;
            if (!Options.Outline && backgroundColor.Alpha < OPAQUE_THRESHOLD)
            {
                return userColor;
            }

            var newUserColor = AdjustColorVisibility(userColor, Options.Outline ? currentOutlinePaint.Color : backgroundColor);

            return Options.Outline || backgroundColor.Alpha == byte.MaxValue
                ? newUserColor
                : userColor.Lerp(newUserColor, (float)backgroundColor.Alpha / byte.MaxValue);
        }

        public static SKColor AdjustColorVisibility(SKColor foreground, SKColor background)
        {
            background.ToHsl(out var bgHue, out var bgSat, out _);
            foreground.ToHsl(out var fgHue, out var fgSat, out var fgLight);

            if (background.RelativeLuminance() > 0.5)
            {
                if (fgLight > 60)
                {
                    fgLight = 60;
                }

                if (bgSat <= 28)
                {
                    fgHue = fgHue switch
                    {
                        > 48 and < 90 => AdjustHue(fgHue, 48, 90),
                        > 164 and < 186 => AdjustHue(fgHue, 164, 186),
                        _ => fgHue
                    };
                }
            }
            else
            {
                if (fgLight < 40)
                {
                    fgLight = 40;
                }

                if (bgSat <= 28)
                {
                    fgHue = fgHue switch
                    {
                        > 224 and < 263 => AdjustHue(fgHue, 224, 264),
                        _ => fgHue
                    };
                }
            }

            if (bgSat > 28 && fgSat > 28)
            {
                const float HUE_WIDTH = 360;
                const int ADJUST_THRESHOLD = 35;
                Debug.Assert(ADJUST_THRESHOLD < HUE_WIDTH / 2);

                var hueDiff1 = fgHue - (bgHue > HUE_WIDTH / 2 ? bgHue - HUE_WIDTH : bgHue);
                var hueDiff2 = fgHue - (bgHue > HUE_WIDTH / 2 ? bgHue : bgHue + HUE_WIDTH);

                float hueDiff;
                if (Math.Abs(hueDiff1) <= ADJUST_THRESHOLD) hueDiff = hueDiff1;
                else if (Math.Abs(hueDiff2) <= ADJUST_THRESHOLD) hueDiff = hueDiff2;
                else goto SkipHueAdjust;

                var diffSign = hueDiff < 0 ? -1 : 1;
                fgHue = bgHue + ADJUST_THRESHOLD * diffSign;

                if (fgHue < 0) fgHue += HUE_WIDTH;
                fgHue %= HUE_WIDTH;

                SkipHueAdjust:;
            }

            return SKColor.FromHsl(fgHue, Math.Min(fgSat, 90), fgLight);

            static float AdjustHue(float hue, float lowerClamp, float upperClamp)
            {
                var midpoint = (upperClamp + lowerClamp) / 2;
                return hue >= midpoint ? upperClamp : lowerClamp;
            }
        }

        private static ReadOnlySpan<char> SubstringToTextWidth(
            ReadOnlySpan<char> text,
            SKPaint textFont,
            int maxWidth,
            bool isRtl,
            SearchValues<char> delimiters)
        {
            if (!isRtl && text.IndexOfAny(delimiters) == -1)
            {
                return SubstringToTextWidth(text, textFont, maxWidth);
            }

            using var shaper = isRtl ? new SKShaper(textFont.Typeface) : null;

            if (MeasureText(text, textFont, isRtl, shaper) <= maxWidth)
            {
                return text;
            }

            var length = text.Length;
            do
            {
                length /= 2;
            }
            while (MeasureText(text[..length], textFont, isRtl, shaper) > maxWidth);

            do
            {
                length++;
            }
            while (MeasureText(text[..length], textFont, isRtl, shaper) < maxWidth);
            text = text[..(length - 1)];

            var delimiterIndex = text.LastIndexOfAny(delimiters);
            if (delimiterIndex != -1)
            {
                return text[..(delimiterIndex + 1)];
            }

            return text;
        }

        private static ReadOnlySpan<char> SubstringToTextWidth(ReadOnlySpan<char> text, SKPaint textFont, int maxWidth)
        {
            var length = (int)textFont.BreakText(text, maxWidth);
            return text[..length];
        }

        private static float MeasureText(ReadOnlySpan<char> text, SKPaint textFont, bool? isRtl = null, SKShaper? shaper = null)
        {
            isRtl ??= IsRightToLeft(text);
            if (isRtl == false)
            {
                return textFont.MeasureText(text);
            }

            if (shaper is null)
            {
                using var localShaper = new SKShaper(textFont.Typeface);
                return MeasureRtlText(text, textFont, localShaper);
            }

            return MeasureRtlText(text, textFont, shaper);
        }

        private static float MeasureRtlText(ReadOnlySpan<char> rtlText, SKPaint textFont, SKShaper shaper)
        {
            using var buffer = new HarfBuzzSharp.Buffer();
            buffer.Add(rtlText, textFont.TextEncoding);
            SKShaper.Result measure = shaper.Shape(buffer, textFont);
            return measure.Width;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            foreach (var ctx in _workerContexts.Values)
            {
                ctx?.Dispose();
            }
            _workerContexts.Dispose();

            _sharedInitFallbackCache.Dispose();
            _fontManager.Dispose();
            BaseNameFont.Dispose();
            BaseMessageFont.Dispose();
            BaseOutlinePaint.Dispose();
        }
    }
}

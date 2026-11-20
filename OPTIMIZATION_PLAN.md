# ChatRenderCLI & ChatRenderGUI — Clean-Slate Compiler & Compositor Architecture (Completed)

This document serves as the architectural reference for `TwitchDownloaderCore`, `ChatRenderCLI`, and `ChatRenderGUI`. Both the multi-wave throughput engineering (**Waves 1–3**) and the 5-phase clean-slate modular compiler/compositor refactoring (**Architecture Phases 1–5**) are **COMPLETED**, preserving **100% visual parity**, **100% TwitchDownloader CLI & JSON drop-in compatibility** (`v1.0`–`v1.4.0` + `v2.0-extended`, `.json`, `.json.gz`, UTF-8/16/32 BOM), and **full Giphy + 7TV/BTTV/FFZ/Twitch emote support**.

---

## Core Architectural Pillars

1. **Pure Streaming Ingestion & Non-Mutating `CanonicalTimeline` IR (`ChatJson` -> `TimelineCompiler`)**:
   - `ChatJson.DeserializeAsync` streams `.json` and `.json.gz` files in a single pass via source-generated `ChatJsonContext` (`DeserializeFromStreamAsync`) with zero intermediate `JsonDocument` DOM and zero network calls (guaranteeing strict `--offline` purity).
   - `TimelineCompiler.Compile` lowers `ChatRoot` + `ChatRenderOptions` into an immutable, `#nullable enable` `CanonicalTimeline` (`TimelineMessage`, `StickyOverlayEvent` records) without mutating `ChatRoot` or allocating per-frame `Comment.Clone()` copies.
2. **Decoupled Asset Catalog & Multi-Background `EmoteSurfaceCache`**:
   - `AssetCatalog` isolates all network/cache fetching, on-demand in-memory `ZipArchive` emoji indexing, gigantified `2.0x` emote variants, pooled `SectionImageCache`, and `ImageMemoryBudget` LRU tracking.
   - `TwitchEmote` is pure domain media state with monotonic `DecodeGeneration` tracking; `EmoteSurfaceCache` caches pre-composited planar `YUV420P`/`YUVA420P` emote frames keyed by `(Emote, BgPixel, IncludeAlpha, IsBgra)`, eliminating cache thrashing across alternating and highlighted rows.
3. **Structured `EncodingProfile` & `FfmpegPipelineBuilder`**:
   - `FfmpegCodecPresets` provides a single canonical registry of container/codec presets shared by CLI and WPF (`GuiPreferences`), eliminating duplicate codec strings and fragile `Settings.Default` migration hacks.
   - `FfmpegPipelineBuilder.BuildEncodingPlan` composes `--sharpening` (`smartblur=lr=1:ls=-1.0`) directly into the output `-vf` chain *before* `fps={fps}`, preserving direct planar `yuv420p`/`yuva420p` and zero-duplicate `-f nut` piping.
4. **Stateless Layout/Section Renderers & Unified `1..N` `SegmentCompositor`**:
   - `TextLayoutEngine`, `MessageSectionRenderer`, and `StickyOverlayRenderer` (`StickyOverlayTracker`) render immutable `TimelineMessage` and `StickyOverlayEvent` records using thread-local `SectionRenderContext` buffers, same-line `SKTextBlob` horizontal runs, strike-cached `A8` outlines, and zero render-time `JsonElement` parsing.
   - `ChatPreviewRenderer` drives the WPF live preview directly through `TextLayoutEngine` + `MessageSectionRenderer` over synthetic in-memory assets so preview styling never drifts from encoded video.
   - `ChatRenderer` (~390 LOC) executes a single unified `1..N` segment path (`SegmentPartitioner` -> `SegmentCompositor` -> `FfmpegEncodeCoordinator`), eliminating duplicate single-worker vs multi-worker loops and forced `GC.Collect()` pauses.

---

## Modular Pipeline Architecture

```
┌──────────────────────────────────────────────────────────────────────────────────┐
│                     PHASE 1: PURE INGESTION & TIMELINE IR                        │
│  ┌─────────────────────────┐    ┌──────────────────────────┐    ┌─────────────┐  │
│  │ ChatJson.cs             │    │ TimelineCompiler.cs      │    │ Canonical   │  │
│  │ • Single-Pass Streamer  │───▶│ • Non-Mutating Lowering  │───▶│ Timeline IR │  │
│  │ • Strict Offline Purity │    │ • Mod/Notice/Event Split │    │ (Immutable) │  │
│  └─────────────────────────┘    └──────────────────────────┘    └──────┬──────┘  │
└────────────────────────────────────────────────────────────────────────┼─────────┘
                                                                         │
                                                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│               PHASE 2–3: ASSET CATALOG, LAYOUT & ENCODING PLANNER                │
│  ┌─────────────────────────┐    ┌──────────────────────────┐    ┌─────────────┐  │
│  │ AssetCatalog.cs         │    │ TextLayoutEngine.cs      │    │ FfmpegPipe  │  │
│  │ • In-Memory Emoji Zip   │    │ • ThreadLocal Contexts   │    │ Builder.cs  │  │
│  │ • Multi-BG EmoteYuvCache│    │ • SKTextBlob + Username  │    │ • NUT + YUV │  │
│  └────────────┬────────────┘    └────────────┬─────────────┘    └──────┬──────┘  │
└───────────────┼──────────────────────────────┼─────────────────────────┼─────────┘
                └──────────────────────────────┼─────────────────────────┘
                                               ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│           PHASE 4–5: UNIFIED 1..N SEGMENT COMPOSITOR & ENCODE COORDINATOR        │
│  ┌──────────────────────────────┐  ┌──────────────────────────────────────────┐  │
│  │ MessageSectionRenderer.cs    │  │ SegmentCompositor.cs + SimdPixelConverter│  │
│  │ StickyOverlayRenderer.cs     │─▶│ • Async Sliding-Window Lookahead         │  │
│  │ ChatPreviewRenderer.cs (GUI) │  │ • Incremental Scroll + AVX2 8x2 BT.601   │  │
│  └──────────────────────────────┘  └────────────────────┬─────────────────────┘  │
│                                                         ▼                        │
│                                    ┌──────────────────────────────────────────┐  │
│                                    │ FfmpegEncodeCoordinator.cs               │  │
│                                    │ • Async Bounded Channel + NutPipeMuxer   │  │
│                                    │ • Lossless Multi-Segment -f concat Mux   │  │
│                                    └──────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────────────────┘
```

---

## Module Responsibility Matrix

| Module | Location | Responsibility |
| :--- | :--- | :--- |
| **`ChatJson`** | `Chat/ChatJson.cs` | Single-pass streaming `.json` / `.json.gz` / UTF-8/16/32 BOM deserialization via `ChatJsonContext` with zero network I/O and guarded `DateTime` math. |
| **`CanonicalTimeline`** | `Chat/Timeline/CanonicalTimeline.cs` | `#nullable enable` immutable records for `TimelineMessage`, `TimelineNoticeInfo`, and typed `StickyOverlayEvent`s (`Prediction`, `Poll`, `PinnedChat`, `HypeTrain`). |
| **`TimelineCompiler`** | `Chat/Timeline/TimelineCompiler.cs` | Pure compiler handling filtering, moderation pairing (`clearmsg`/`clearchat`), `roomstate` transitions, sub/streak splitting, dispersion, and single-pass flooring. |
| **`AssetCatalog`** | `Rendering/AssetCatalog.cs` | Badges, 1P/3P emotes, Giphy GIFs, cheermotes, avatars, gigantified emotes, in-memory `ZipArchive` emojis, `SectionImageCache`, `ImageMemoryBudget`, and `EmoteSurfaceCache`. |
| **`EmoteSurfaceCache`** | `Rendering/EmoteSurfaceCache.cs` | Thread-safe multi-background planar `YUV420P`/`YUVA420P` emote cache keyed by `(Emote, BgPixel, IncludeAlpha, IsBgra)` with `DecodeGeneration` invalidation. |
| **`SimdPixelConverter`** | `Rendering/SimdPixelConverter.cs` | `DirtyRect` coalescing, `AVX-512`/`AVX2`/`AdvSimd` alpha masking, `Avx2` `8x2` / `Vector128` `4x2` BT.601 conversion, zero-div reciprocal LUTs, and `ShiftYuvPlanesUp`. |
| **`EncodingProfile` & `FfmpegPipelineBuilder`** | `Rendering/Encoding/*` | Canonical `FfmpegCodecPresets`, width-aware VP9 tile tuning, NUT pipe negotiation, and `-vf smartblur` sharpening composition without breaking planar YUV/NUT. |
| **`TextLayoutEngine`** | `Rendering/TextLayoutEngine.cs` | Smart `"Inter"` resolution, `SectionRenderContext` per-thread state, `SKTextBlob` horizontal run batching, strike-cached outlines, and WCAG username caching. |
| **`MessageSectionRenderer`** | `Rendering/MessageSectionRenderer.cs` | Stateless rasterizer for regular chat rows, compact 1-line sub/gift/streak headers, threaded replies, Hype Chat pills, announcements, and moderation states. |
| **`StickyOverlayRenderer`** | `Rendering/StickyOverlayRenderer.cs` | Deterministic `StickyOverlayTracker` state machine and Twitch-authentic top widget renderer (Predictions, Polls, Pinned Chat, Hype Trains). |
| **`SegmentPartitioner` & `SegmentCompositor`** | `Rendering/Segment*.cs` | Cost-weighted timeline partitioning (`1..N` segments), background lookahead producer loop, O(1) `AnimNextChangeTickMs` skip, incremental scroll, and multi-BG YUV blitting. |
| **`FfmpegEncodeCoordinator`** | `Rendering/FfmpegEncodeCoordinator.cs` | Process lifecycle registry, bounded async pipe writer (`NutPipeMuxer` / `rawvideo`), lossless `-f concat -c copy` stitching, and deterministic temp cleanup. |
| **`ChatPreviewRenderer` & `GuiPreferences`** | `Rendering/ChatPreviewRenderer.cs`, `WPF/Services/GuiPreferences.cs` | Live WPF preview driven by `MessageSectionRenderer` over synthetic in-memory assets, plus clean JSON-persisted FFmpeg preset overrides. |

---

## Verification Suite Summary

| Suite | Coverage Focus | File |
| :--- | :--- | :--- |
| **Wave 1 Suite** | Pooled `SectionImageCache`, transitive `DirtyRect` coalescing, sub-rect & scroll-shifted planar YUV bit-exactness, `EmoteSurfaceCache` blitting | `Wave1RenderOptimizationTests.cs` |
| **Wave 2 Suite** | Exhaustive $1.105 \times 10^6$ reciprocal LUT verification, `Avx2`/`Vector128` vs scalar YUV parity, `NutPipeMuxer` framing, cost-weighted segments, VP9 width tuning | `Wave2RenderOptimizationTests.cs` |
| **Wave 3 Suite** | Streaming `.json`/`.json.gz`/BOM/legacy `v1.0`–`v1.4` deserialization, zero-disk in-memory emoji archives, RTL detection, repeat-chatter username caching | `Wave3RenderOptimizationTests.cs` |
| **Architecture Suite** | Non-mutating `TimelineCompiler` IR, `--sharpening` NUT/YUV pipeline preservation, multi-background `EmoteSurfaceCache` + generation invalidation, `StickyOverlayTracker`, live `ChatPreviewRenderer` | `PipelineArchitectureTests.cs` |

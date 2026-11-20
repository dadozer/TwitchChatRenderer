# ChatRenderGUI (`TwitchDownloaderWPF`)

**ChatRenderGUI** (`ChatRenderGUI.exe`) is a dedicated, fast-launching Windows WPF desktop frontend for the optimized `TwitchDownloaderCore` chat rendering engine.

It preserves the full TwitchDownloader **Chat Render** visual configurator and real-time SkiaSharp preview while stripping away non-render subsystems (VOD/Clip downloaders, chat downloaders/updaters, queue dialogs, and theme/localization bloat) and exposing all new high-performance rendering capabilities.

---

## Key Features & Additions Over Upstream TwitchDownloaderWPF

1. **Shared 10x–20x Faster `TwitchDownloaderCore` Compiler & Compositor Pipeline**:
   - Because `ChatRenderGUI.exe` calls directly into the modular `TwitchDownloaderCore` pipeline (`TimelineCompiler` -> `CanonicalTimeline` -> `SegmentCompositor` -> `FfmpegEncodeCoordinator`), GUI renders automatically benefit from **Cost-Weighted Parallel Segment Partitioning**, **Direct Planar YUV (`yuv420p` / `yuva420p`) + `-f nut` VFR Pipes**, **Multi-Background Planar YUV Emote Blitting (`EmoteSurfaceCache`)**, **O(1) Next-Transition Short-Circuiting**, **Incremental Scroll Shifting**, **In-Memory `ZipArchive` Emoji Decoding**, and **Giphy GIF Rendering**.
2. **Parallel Segments (`0 = Auto`) Control**:
   - Located in the **Encoding** tab (`numParallelJobs`), allowing you to choose how many concurrent FFmpeg encoding segments (`0` = Auto-scale by CPU core count, chat width, codec class, and duration; `1` = Single-stream; `2`–`16` = Explicit cost-weighted parallel segments) are used and stitched losslessly via `ffmpeg -f concat -c copy`.
3. **Canonical Codec Registry & Width-Aware VP9 Alpha Preset**:
   - Shares the canonical `FfmpegCodecPresets` registry with the CLI. Selecting **WEBM** (or **MKV**) -> **VP9** automatically uses `-deadline realtime -cpu-used 8 -row-mt 1 -lag-in-frames 0 -frame-parallel 1` with width-aware `-tile-columns 0/1/2`, dynamic GOP scaling, and direct `yuva420p` + `-f nut` piping.
   - Custom per-container/codec FFmpeg input and output overrides are persisted cleanly in `%AppData%\ChatRenderGUI\ffmpeg-presets.json` (`GuiPreferences`).
4. **Direct Multi-File Batch Rendering (No Queue Window Needed)**:
   - Click **Browse** and select **multiple** `.json` or `.json.gz` files at once.
   - When multiple files are loaded, clicking **Render** prompts you once for an **Output Folder** (`OpenFolderDialog`) and sequentially renders each chat file (`<input_name>.<ext>`) using your configured settings, updating progress and supporting clean cancellation (automatically terminating child FFmpeg processes and cleaning up `.chatrender` temp files) at any time.
5. **Partial Timeline Rendering**:
   - Use the split **Render** button dropdown -> **Partial Render** to render a specific start/end timestamp window (`HH:MM:SS`) of a chat archive.
6. **Extended Broadcast & Chat Event Schema (`v2.0-extended`)**:
   - Full GUI configuration in the **Events & Overlays** tab for Twitch Predictions, Polls, Pinned Chat, Hype Trains, dynamic message moderation (`Strikethrough`, `ReplaceDeleted`, `Hide`, `Off`), roomstate changes, threaded replies, first-time chatter highlights, `/announce` banners, raids, channel point redemptions, Hype Chat pills, gigantified emotes, and Shared Chat badges.
7. **Unified Production SkiaSharp Live Preview (`ChatPreviewRenderer`)**:
   - The right-hand **LIVE PREVIEW** panel renders sample messages directly through the production `TextLayoutEngine` and `MessageSectionRenderer` over a transparency checkerboard—updating immediately as you adjust fonts, sizes, styles, colors, highlighted users, outlines, timestamps, alternating backgrounds, subscription headers, avatars, badges, or layout scaling.

---

## Configuration Tabs Overview

- **General**: Canvas width (`Width`), height (`Height`), font family (`Font`), font size (`Font Size`), font style (`Normal`, `Bold`, `Italic`, `BoldItalic`), base/alternate/message/highlight colors, timestamps, outlines, and subscription notices.
- **Rendering**: Toggle **BTTV**, **FFZ**, **7TV**, **Giphy GIFs**, **Avatars**, **Chat Badges**, **Emoji Vendor** (`Google Noto Color`, `Twitter Twemoji`, or `None`), **Offline Mode**, **Dispersion**, **Adjust Username Visibility**, **GIF Max Height (%)** (`1`–`100%`, default `33%`), and **Image Cache (MB)** (`>= 16` MB, default `256` MB). Filter specific badges via the **Filter Chat Badges** checklist or hide/highlight specific users and banned words.
- **Events & Overlays**:
  - **Top Overlay Widgets**: Independently set **Predictions** (default `Sticky Top`), **Polls** (default `Sticky Top`, with full-width vertically stacked choice rows), **Pinned Chat** (default `Sticky Top`), and **Hype Train** (default `Chat Scroll`) to `Sticky Top`, `Chat Scroll`, or `Off`. Sticky widgets coexist up to 25% of the frame height and prioritize *Open/Resolved Prediction > Poll > Pinned Chat > Closed Prediction > Hype Train*, lingering 15s after resolution.
  - **Moderation & Roomstate**: Choose **Moderated Messages** behavior (`Dim / Strikethrough` default, `Replace (<message deleted>)`, `Hide Completely`, or `Off`), toggle **Show Moderation System Notices** (default `On`), toggle **Apply Clear All** (default `Off`), and toggle **Show Roomstate Mode Notices** (default `On`, automatically suppressing initial channel-join baseline snapshots).
  - **Chat Enrichments & Compact Sub/Gift/Streak Headers**: Individually toggle **Threaded Replies (`↱ @User`)**, **First-Time / Returning Chatter Highlight** (`#FFB800` gold / `#00C8FF` sky blue accent bar + 11% tint), **Announcements (`/announce`)**, **Raid Notices**, **Channel Point Redemptions** (including `#6B9E82` gray-green highlighted messages), **Paid Hype Chat Pill**, **Bits Power-Ups / Gigantified Emotes**, and **Shared Chat Small Badge** (all default `On`), alongside compact 1-line Subscription, Gift Subscription (`Gifter → Recipient`), and Watch Streak headers.
- **Scaling**: Fine-grained multipliers for Emotes, Badges, Emojis, Avatars, Vertical Spacing, Side Padding, Section Height, Word Space, Emote Space, Accent Stroke, Accent Indent, Outline Scale, and Username Scale.
- **Encoding**: Container format (`MP4`, `MOV`, `WEBM`, `MKV`), Video Codec (`H264`, `H264 NVIDIA`, `H264 AMD`, `H265`, `H265 NVIDIA`, `H265 AMD`, `VP8`, `VP9`, `ProRes`, `RLE` — with native alpha transparency supported on `VP8`, `VP9`, `ProRes`, and `RLE`), Framerate (`FPS`), Update Rate, **Parallel Segments (`0 = Auto`)**, Alpha Mask generation (`Generate Mask`), Sharpening (`smartblur` composed into `-vf` without disabling planar YUV/NUT piping), and custom FFmpeg input/output arguments.

---

## Global Settings (`Settings` Button)

Clicking **Settings** in the top-right opens the streamlined global preferences dialog:
- **Temp Cache Folder**: Browse or clear the temporary cache folder storing downloaded emotes, badges, and Giphy resolutions.
- **Log Verbosity**: Configure which log categories (`Status`, `Verbose`, `Info`, `Warning`, `Error`, `Ffmpeg`) appear in the status log.

---

## Building `ChatRenderGUI.exe`

Run `build-win-x64.bat` or `./build-win-x64.ps1` from the repository root, or publish directly via the .NET 10 SDK:

```bash
dotnet publish TwitchDownloaderWPF/TwitchDownloaderWPF.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:IncludeAllContentForSelfExtract=true \
  -o ./dist/win-x64
```
This outputs a standalone single-file Windows desktop executable at `dist/win-x64/ChatRenderGUI.exe`.

#nullable enable

namespace TwitchDownloaderCore.Rendering.Encoding
{
    public enum PipePixelFormat
    {
        BgraOrRgba = 0,
        Yuv420p = 1,
        Yuva420p = 2
    }

    public sealed record FfmpegCodecPreset(
        string Name,
        string InputArgs,
        string OutputArgs,
        bool SupportsTransparency = false);

    public sealed record FfmpegContainerPreset(
        string Extension,
        IReadOnlyList<FfmpegCodecPreset> Codecs);

    public sealed record EncodingPlan(
        PipePixelFormat PipeFormat,
        string PipePixFmt,
        bool UseNutPipe,
        string InputArgs,
        string OutputArgs,
        string FullArguments);

    /// <summary>
    /// Single canonical registry of container and codec encoding presets shared by CLI defaults and WPF dropdowns.
    /// </summary>
    public static class FfmpegCodecPresets
    {
        public const string StandardRawVideoInputArgs =
            "-framerate {fps} -f rawvideo -analyzeduration {max_int} -probesize {max_int} -pix_fmt {pix_fmt} -video_size {width}x{height} -i -";

        public static readonly FfmpegCodecPreset H264 = new(
            "H264",
            StandardRawVideoInputArgs,
            "-c:v libx264 -preset:v veryfast -crf 18 -pix_fmt yuv420p \"{save_path}\"");

        public static readonly FfmpegCodecPreset H264Nvenc = new(
            "H264 NVIDIA",
            StandardRawVideoInputArgs,
            "-c:v h264_nvenc -preset:v p4 -cq 20 -pix_fmt yuv420p \"{save_path}\"");

        public static readonly FfmpegCodecPreset H264Amf = new(
            "H264 AMD",
            StandardRawVideoInputArgs,
            "-c:v h264_amf -quality balanced -rc cqp -qp_i 20 -qp_p 20 -qp_b 20 -pix_fmt yuv420p \"{save_path}\"");

        public static readonly FfmpegCodecPreset H265 = new(
            "H265",
            StandardRawVideoInputArgs,
            "-c:v libx265 -preset:v veryfast -crf 18 -pix_fmt yuv420p \"{save_path}\"");

        public static readonly FfmpegCodecPreset H265Nvenc = new(
            "H265 NVIDIA",
            StandardRawVideoInputArgs,
            "-c:v hevc_nvenc -preset:v p4 -cq 21 -pix_fmt yuv420p \"{save_path}\"");

        public static readonly FfmpegCodecPreset H265Amf = new(
            "H265 AMD",
            StandardRawVideoInputArgs,
            "-c:v hevc_amf -quality balanced -rc cqp -qp_i 21 -qp_p 21 -qp_b 21 -pix_fmt yuv420p \"{save_path}\"");

        public static readonly FfmpegCodecPreset Vp8 = new(
            "VP8",
            StandardRawVideoInputArgs,
            "-c:v libvpx -crf 18 -b:v 2M -pix_fmt yuva420p -auto-alt-ref 0 \"{save_path}\"",
            SupportsTransparency: true);

        public static readonly FfmpegCodecPreset Vp9 = new(
            "VP9",
            StandardRawVideoInputArgs,
            "-c:v libvpx-vp9 -crf 18 -b:v 0 -deadline realtime -cpu-used 8 -row-mt 1 -lag-in-frames 0 -frame-parallel 1 -static-thresh 1000 -aq-mode 3 -undershoot-pct 100 -pix_fmt yuva420p \"{save_path}\"",
            SupportsTransparency: true);

        public static readonly FfmpegCodecPreset Rle = new(
            "RLE",
            StandardRawVideoInputArgs,
            "-c:v qtrle -pix_fmt argb \"{save_path}\"",
            SupportsTransparency: true);

        public static readonly FfmpegCodecPreset ProRes = new(
            "ProRes",
            StandardRawVideoInputArgs,
            "-c:v prores_ks -profile:v 4 -qscale:v 11 -pix_fmt yuva444p10le \"{save_path}\"",
            SupportsTransparency: true);

        public static readonly IReadOnlyList<FfmpegContainerPreset> Containers =
        [
            new FfmpegContainerPreset("MP4", [H264, H264Nvenc, H264Amf, H265, H265Nvenc, H265Amf]),
            new FfmpegContainerPreset("MOV", [H264, H264Nvenc, H264Amf, H265, H265Nvenc, H265Amf, Rle, ProRes]),
            new FfmpegContainerPreset("WEBM", [Vp8, Vp9]),
            new FfmpegContainerPreset("MKV", [H264, H264Nvenc, H264Amf, H265, H265Nvenc, H265Amf, Vp8, Vp9])
        ];
    }
}

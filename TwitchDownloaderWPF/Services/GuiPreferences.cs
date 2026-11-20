using System.IO;
using System.Text.Json;
using TwitchDownloaderCore.Rendering.Encoding;
using TwitchDownloaderWPF.Properties;

namespace TwitchDownloaderWPF.Services
{
    public sealed class CustomFfmpegPresetEntry
    {
        public string ContainerName { get; set; } = string.Empty;
        public string CodecName { get; set; } = string.Empty;
        public string InputArgs { get; set; } = string.Empty;
        public string OutputArgs { get; set; } = string.Empty;
    }

    /// <summary>
    /// Clean JSON-backed storage for user-customized FFmpeg container/codec arguments.
    /// Isolates legacy <see cref="Settings.Default"/> migration away from UI code-behind.
    /// </summary>
    internal static class GuiPreferences
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        private static string GetPreferencesFilePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData))
            {
                appData = Path.GetTempPath();
            }

            string folder = Path.Combine(appData, "ChatRenderGUI");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, "ffmpeg-presets.json");
        }

        public static (string InputArgs, string OutputArgs) LoadCustomArgs(
            string containerName,
            Codec selectedCodec)
        {
            var entries = ReadEntries();
            foreach (var entry in entries)
            {
                if (string.Equals(entry.ContainerName, containerName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.CodecName, selectedCodec.Name, StringComparison.OrdinalIgnoreCase))
                {
                    string input = string.IsNullOrWhiteSpace(entry.InputArgs) ? selectedCodec.InputArgs : entry.InputArgs;
                    string output = string.IsNullOrWhiteSpace(entry.OutputArgs) ? selectedCodec.OutputArgs : entry.OutputArgs;
                    return (input, output);
                }
            }

            return (selectedCodec.InputArgs, selectedCodec.OutputArgs);
        }

        public static void SaveCustomArgs(
            string containerName,
            Codec selectedCodec,
            string inputArgs,
            string outputArgs)
        {
            var entries = ReadEntries();
            entries.RemoveAll(e =>
                string.Equals(e.ContainerName, containerName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.CodecName, selectedCodec.Name, StringComparison.OrdinalIgnoreCase));

            // Only persist overrides that actually differ from the canonical preset
            if (!string.Equals(inputArgs, selectedCodec.InputArgs, StringComparison.Ordinal)
                || !string.Equals(outputArgs, selectedCodec.OutputArgs, StringComparison.Ordinal))
            {
                entries.Add(new CustomFfmpegPresetEntry
                {
                    ContainerName = containerName,
                    CodecName = selectedCodec.Name,
                    InputArgs = inputArgs,
                    OutputArgs = outputArgs
                });
            }

            WriteEntries(entries);
        }

        private static List<CustomFfmpegPresetEntry> ReadEntries()
        {
            try
            {
                string path = GetPreferencesFilePath();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<List<CustomFfmpegPresetEntry>>(json, JsonOptions) ?? [];
                }

                // One-time seed from legacy Settings.Default.FfmpegArguments if present
                if (!string.IsNullOrWhiteSpace(Settings.Default.FfmpegArguments))
                {
                    var seeded = JsonSerializer.Deserialize<List<CustomFfmpegPresetEntry>>(Settings.Default.FfmpegArguments) ?? [];
                    foreach (var item in seeded)
                    {
                        if (!string.IsNullOrEmpty(item.InputArgs))
                        {
                            item.InputArgs = item.InputArgs.Replace("-pix_fmt bgra", "-pix_fmt {pix_fmt}", StringComparison.OrdinalIgnoreCase);
                        }
                    }

                    // Drop obsolete unmodified legacy defaults so canonical FfmpegCodecPresets take effect cleanly
                    seeded.RemoveAll(static item =>
                        (item.CodecName == "VP9" && item.OutputArgs is
                            "-c:v libvpx-vp9 -crf 18 -b:v 2M -pix_fmt yuva420p \"{save_path}\"" or
                            "-c:v libvpx-vp9 -crf 18 -b:v 2M -deadline realtime -quality realtime -speed 4 -row-mt 1 -threads 4 -pix_fmt yuva420p \"{save_path}\"")
                        || (item.CodecName == "ProRes" && item.OutputArgs == "-c:v prores_ks -qscale:v 62 -pix_fmt argb \"{save_path}\""));

                    WriteEntries(seeded);
                    return seeded;
                }
            }
            catch
            {
                // Ignore corrupted preference files and fall back to canonical presets
            }

            return [];
        }

        private static void WriteEntries(List<CustomFfmpegPresetEntry> entries)
        {
            try
            {
                string path = GetPreferencesFilePath();
                File.WriteAllText(path, JsonSerializer.Serialize(entries, JsonOptions));
            }
            catch
            {
                // Non-fatal if user profile is read-only
            }
        }
    }
}

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using TwitchDownloaderCore.Extensions;

namespace TwitchDownloaderCore.Services
{
    public static partial class FilenameService
    {
        private static readonly SearchValues<char> CommonInvalidChars = SearchValues.Create("""*":<>?|/\""");
        private static readonly SearchValues<char> FilenameInvalidChars = SearchValues.Create(Path.GetInvalidFileNameChars());

        [GeneratedRegex("""(?<=\d):(?=\d\d)""")]
        private static partial Regex TimestampRegex { get; }

        [return: NotNullIfNotNull(nameof(filename))]
        public static string ReplaceInvalidFilenameChars([AllowNull] string filename)
        {
            if (string.IsNullOrEmpty(filename))
            {
                return filename;
            }

            var newName = TimestampRegex.Replace(filename, "_");

            if (newName.AsSpan().IndexOfAny(CommonInvalidChars) != -1)
            {
                newName = string.Create(filename.Length, filename, static (span, str) =>
                {
                    const int FULL_WIDTH_OFFSET = 0xFEE0; // https://en.wikipedia.org/wiki/Halfwidth_and_Fullwidth_Forms_(Unicode_block)
                    for (var i = 0; i < str.Length; i++)
                    {
                        var ch = str[i];
                        span[i] = ch switch
                        {
                            '*' or '"' or ':' or '<' or '>' or '?' or '|' or '/' or '\\' => (char)(ch + FULL_WIDTH_OFFSET),
                            _ => ch
                        };
                    }
                });
            }

            // In case there are additional invalid chars such as control codes
            return newName.ReplaceAny(FilenameInvalidChars, '_');
        }

        public static FileInfo GetNonCollidingName(FileInfo fileInfo)
        {
            fileInfo.Refresh();
            var fi = fileInfo;

            var parentDir = Path.GetDirectoryName(fi.FullName)!;
            var oldName = Path.GetFileNameWithoutExtension(fi.Name.AsSpan());
            var extension = Path.GetExtension(fi.Name.AsSpan());

            var i = 1;
            while (fi.Exists)
            {
                var newName = Path.Combine(parentDir, $"{oldName} ({i}){extension}");
                fi = new FileInfo(newName);
                i++;
            }

            return fi;
        }
    }
}
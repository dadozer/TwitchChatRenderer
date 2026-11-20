using TwitchDownloaderCLI.Models;

namespace TwitchDownloaderCLI.Tools
{
    internal static class PreParseArgs
    {
        internal static string[] Parse(string[] args, string processFileName)
        {
            if (args.Any(x => x is "-m" or "--mode" or "--silent" or "--verbose-ffmpeg"))
            {
                // A legacy syntax was used, convert to new syntax
                return Process(ConvertFromOldSyntax(args, processFileName));
            }

            return Process(args);
        }

        private static string[] Process(string[] args)
        {
            if (args.Length > 0)
            {
                var firstArgLower = args[0].ToLowerInvariant();
                if (firstArgLower is "chatrender" or "ffmpeg" or "cache" or "help" or "--help" or "version" or "--version")
                {
                    args[0] = firstArgLower;
                    return args;
                }

                var prependedArgs = new string[args.Length + 1];
                prependedArgs[0] = "chatrender";
                Array.Copy(args, 0, prependedArgs, 1, args.Length);
                return prependedArgs;
            }

            return args;
        }

        /// <summary>
        /// Converts an argument <see cref="string"/>[] using any legacy syntax to the current syntax and prints corresponding warning messages
        /// </summary>
        /// <returns>An argument <see cref="string"/>[] using current syntax that represent the intentions of the legacy syntax</returns>
        private static string[] ConvertFromOldSyntax(string[] args, string processFileName)
        {
            var processedArgs = args.ToList();

            for (var i = 0; i < processedArgs.Count; i++)
            {
                switch (processedArgs[i])
                {
                    case "-m" or "--mode":
                        Console.WriteLine("[WARNING] --mode <mode> has been deprecated in favor of verbs (like 'git <verb>') and may be removed. Run '{0} help' for more information.", processFileName);
                        ConvertModeSyntax(processedArgs, ref i);
                        break;
                    case "--silent":
                        Console.WriteLine("[WARNING] --silent has been deprecated in favor of log levels. '--log-level None' will be applied to the current session and may be removed. Run '{0} help' for more information.", processFileName);
                        ConvertSilentSyntax(processedArgs, i);
                        break;
                    case "--verbose-ffmpeg":
                        Console.WriteLine("[WARNING] --verbose-ffmpeg has been deprecated in favor of log levels and may be removed. '--log-level Status,Info,Warning,Error,Ffmpeg' will be applied to the current session. Run '{0} help' for more information.", processFileName);
                        ConvertVerboseFfmpegSyntax(processedArgs, i);
                        break;
                }
            }

            return processedArgs.ToArray();
        }

        // Added at 2022-11-13T06:02:21Z (1668319341000)
        private static void ConvertModeSyntax(IList<string> args, ref int index)
        {
            // --mode
            args.RemoveAt(index);
            if (index >= args.Count)
            {
                index--;
                return;
            }

            // run-mode (e.g. ChatRender)
            var runMode = args[index];
            args.RemoveAt(index);
            args.Insert(0, runMode);
        }

        // Added at 2024-03-31T20:09:53Z (1711915793000)
        private static void ConvertSilentSyntax(IList<string> args, int index)
        {
            args[index] = "--log-level";
            args.Insert(index + 1, nameof(LogLevel.None));
        }

        // Added at 2024-04-06T04:18:40Z (1712377120000)
        private static void ConvertVerboseFfmpegSyntax(IList<string> args, int index)
        {
            // If the user is still using --verbose-ffmpeg they probably aren't using log levels yet, so its safe to assume that there won't be a double-parse error
            args[index] = "--log-level";

            var logLevels = Enum.GetNames(typeof(LogLevel))
                .Where(x => x is not nameof(LogLevel.None) and not nameof(LogLevel.Verbose));

            args.Insert(index + 1, string.Join(',', logLevels));
        }
    }
}

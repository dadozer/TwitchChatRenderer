using System.Diagnostics;
using System.Runtime.CompilerServices;
using TwitchDownloaderCore.Interfaces;

namespace TwitchDownloaderCore.Tools
{
    public static class DriveHelper
    {
        public static DriveInfo GetOutputDrive(string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return null;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(outputPath);
            }
            catch
            {
                return null;
            }

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            DriveInfo outputDrive = null;
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (fullPath.StartsWith(drive.Name, comparison))
                {
                    // In Linux, the root drive is '/' while mounted drives are located in '/mnt/' or '/run/media/'
                    // So we need to do a length check to not misinterpret a mounted drive as the root drive
                    if (outputDrive is not null && drive.Name.Length < outputDrive.Name.Length)
                    {
                        continue;
                    }

                    outputDrive = drive;
                }
            }

            return outputDrive;
        }

        /// <summary>
        /// Throttled wrapper around <see cref="WaitForDrive"/> that polls <see cref="DriveInfo.IsReady"/> at most once
        /// every <paramref name="minIntervalMs"/> milliseconds on healthy drives, eliminating tens of thousands of
        /// redundant Win32 volume-status syscalls during high-framerate pipe writes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WaitForDriveThrottled(DriveInfo drive, ITaskLogger logger, ref long lastCheckTickMs, long minIntervalMs = 500)
        {
            if (drive is null)
            {
                return;
            }

            long now = Environment.TickCount64;
            if (lastCheckTickMs != 0 && now - lastCheckTickMs < minIntervalMs)
            {
                return;
            }

            WaitForDrive(drive, logger);
            lastCheckTickMs = Environment.TickCount64;
        }

        /// <summary>
        /// Blocks the thread until the <paramref name="drive"/> is ready.
        /// Mostly only needed by slow USB drives or hard drives getting pegged at 100% by other processes.
        /// </summary>
        public static void WaitForDrive(DriveInfo drive, ITaskLogger logger)
        {
            if (drive is null)
            {
                return;
            }

            var waitStart = Stopwatch.GetTimestamp();
            var logMillis = 250d;
            while (!drive.IsReady)
            {
                var millis = Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
                if (millis >= logMillis)
                {
                    logger?.LogInfo($"Waiting for output drive ({millis:F0}ms)");
                    logMillis += 250;
                }

                Thread.Sleep(10);

                if (millis >= 10_000)
                {
                    throw new DriveNotFoundException("The output drive disconnected for 10 or more consecutive seconds.");
                }
            }
        }
    }
}
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SmartGameCache.Core.Interop;
using SmartGameCache.Core.Models;

namespace SmartGameCache.Core.Prefetch;

public class RamPrefetchEngine
{
    private const int BufferSize = 1024 * 1024; // 1 MB streaming buffer for fast Windows cache loading
    public long LastPrefetchedBytes { get; private set; }

    public async Task<CachePreparationProgress> PrefetchAsync(
        IEnumerable<CachedFileEntry> filesToPreload,
        long maxRamBudgetBytes,
        IProgress<CachePreparationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var prepProgress = new CachePreparationProgress
        {
            Phase = "RAM Warming"
        };

        var eligibleList = new List<CachedFileEntry>();
        long plannedBytes = 0;

        foreach (var file in filesToPreload)
        {
            if (!file.IsValid || !File.Exists(file.CacheFilePath)) continue;

            if (plannedBytes + file.FileSizeBytes > maxRamBudgetBytes && eligibleList.Count > 0)
            {
                // Reached RAM budget limit
                break;
            }

            eligibleList.Add(file);
            plannedBytes += file.FileSizeBytes;
        }

        prepProgress.TotalFiles = eligibleList.Count;
        prepProgress.TotalBytes = plannedBytes;
        progress?.Report(prepProgress);

        if (eligibleList.Count == 0)
        {
            prepProgress.IsCompleted = true;
            prepProgress.Phase = "Ready";
            progress?.Report(prepProgress);
            LastPrefetchedBytes = 0;
            return prepProgress;
        }

        var buffer = new byte[BufferSize];
        var stopwatch = Stopwatch.StartNew();
        long bytesProcessedInBatch = 0;

        foreach (var entry in eligibleList)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                prepProgress.IsCancelled = true;
                prepProgress.Phase = "Cancelled";
                progress?.Report(prepProgress);
                return prepProgress;
            }

            // Memory Pressure Check: If under extreme memory pressure (>85% load or <1.5GB available), abort prefetch
            if (NativeMemory.GetMemoryStatus().IsUnderPressure)
            {
                prepProgress.ErrorMessage = "RAM prefetching paused: Operating system memory pressure exceeds safety threshold (>85% or <1.5GB available).";
                prepProgress.Phase = "Pressure Backoff";
                progress?.Report(prepProgress);
                break;
            }

            prepProgress.CurrentFileName = Path.GetFileName(entry.RelativePath);
            progress?.Report(prepProgress);

            try
            {
                // Open with FILE_FLAG_SEQUENTIAL_SCAN to instruct Windows Cache Manager (Cc) to populate standby pages
                using var fs = new FileStream(
                    entry.CacheFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    BufferSize,
                    FileOptions.SequentialScan | FileOptions.Asynchronous);

                int read;
                while ((read = await fs.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    prepProgress.ProcessedBytes += read;
                    bytesProcessedInBatch += read;

                    // Calculate speed every 250ms
                    var elapsedSec = stopwatch.Elapsed.TotalSeconds;
                    if (elapsedSec >= 0.25)
                    {
                        prepProgress.SpeedBytesPerSec = bytesProcessedInBatch / elapsedSec;
                        var remainingBytes = Math.Max(0, prepProgress.TotalBytes - prepProgress.ProcessedBytes);
                        if (prepProgress.SpeedBytesPerSec > 0)
                        {
                            prepProgress.EstimatedTimeRemaining = TimeSpan.FromSeconds(remainingBytes / prepProgress.SpeedBytesPerSec);
                        }

                        stopwatch.Restart();
                        bytesProcessedInBatch = 0;
                        progress?.Report(prepProgress);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                prepProgress.IsCancelled = true;
                prepProgress.Phase = "Cancelled";
                progress?.Report(prepProgress);
                return prepProgress;
            }
            catch (Exception ex)
            {
                // Non-fatal: if one file fails to read, continue with remaining
                Debug.WriteLine($"Failed to prefetch {entry.CacheFilePath}: {ex.Message}");
            }

            prepProgress.ProcessedFiles++;
            progress?.Report(prepProgress);
        }

        LastPrefetchedBytes = prepProgress.ProcessedBytes;
        prepProgress.IsCompleted = true;
        prepProgress.Phase = "Ready";
        progress?.Report(prepProgress);
        return prepProgress;
    }
}

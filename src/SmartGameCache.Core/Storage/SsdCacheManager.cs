using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SmartGameCache.Core.Database;
using SmartGameCache.Core.Models;

namespace SmartGameCache.Core.Storage;

public class SsdCacheManager
{
    public const int DefaultChunkSize = 128 * 1024; // 128 KB chunk size optimal for game assets
    private readonly DatabaseService _db;
    private readonly SemaphoreSlim _ioLock = new(4, 4); // Limit concurrent disk copies to prevent HDD thrashing

    public SsdCacheManager(DatabaseService db)
    {
        _db = db;
    }

    public async Task<string> GetEffectiveCacheDirectoryAsync()
    {
        var settings = await _db.GetSettingsAsync();
        var dir = settings.SsdCacheDirectory;
        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartGameCache", "Storage");
        }

        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        return dir;
    }

    public async Task<long> GetAvailableSsdSpaceAsync()
    {
        var dir = await GetEffectiveCacheDirectoryAsync();
        var root = Path.GetPathRoot(dir);
        if (string.IsNullOrEmpty(root)) return 0;

        var drive = new DriveInfo(root);
        return drive.AvailableFreeSpace;
    }

    public async Task<bool> ValidateCacheEntryAsync(CachedFileEntry entry, string originalInstallDir)
    {
        var originalPath = Path.Combine(originalInstallDir, entry.RelativePath);
        if (!File.Exists(originalPath) || !File.Exists(entry.CacheFilePath))
        {
            return false;
        }

        var origInfo = new FileInfo(originalPath);
        var cacheInfo = new FileInfo(entry.CacheFilePath);

        if (origInfo.Length != entry.FileSizeBytes || cacheInfo.Length != entry.FileSizeBytes)
        {
            return false;
        }

        if (Math.Abs((origInfo.LastWriteTimeUtc - entry.OriginalLastWriteTimeUtc).TotalSeconds) > 2)
        {
            return false;
        }

        return true;
    }

    public async Task<bool> ValidateChunkAsync(CachedChunkEntry chunk, string originalInstallDir)
    {
        var originalPath = Path.Combine(originalInstallDir, chunk.RelativePath);
        if (!File.Exists(originalPath) || !File.Exists(chunk.CacheFilePath))
        {
            return false;
        }

        var cacheInfo = new FileInfo(chunk.CacheFilePath);
        return cacheInfo.Length == chunk.ChunkLength;
    }

    public async Task<CachedChunkEntry?> CacheChunkAsync(
        int gameId,
        string installDir,
        string relativePath,
        int chunkIndex,
        int chunkLength = DefaultChunkSize,
        double priorityScore = 0.5,
        CancellationToken cancellationToken = default)
    {
        var sourcePath = Path.Combine(installDir, relativePath);
        if (!File.Exists(sourcePath)) return null;

        var sourceInfo = new FileInfo(sourcePath);
        long offset = (long)chunkIndex * chunkLength;
        if (offset >= sourceInfo.Length) return null;

        int bytesToRead = (int)Math.Min((long)chunkLength, sourceInfo.Length - offset);
        var cacheBaseDir = await GetEffectiveCacheDirectoryAsync();
        var chunksDir = Path.Combine(cacheBaseDir, "Chunks", $"Game_{gameId}");
        Directory.CreateDirectory(chunksDir);

        var safeFileName = HashPath(relativePath) + $"_chk_{chunkIndex}.chunk";
        var targetChunkPath = Path.Combine(chunksDir, safeFileName);

        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            var buffer = new byte[bytesToRead];
            using (var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess))
            {
                sourceStream.Seek(offset, SeekOrigin.Begin);
                int read = await sourceStream.ReadAsync(buffer, 0, bytesToRead, cancellationToken);
                if (read <= 0) return null;

                using (var destStream = new FileStream(targetChunkPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
                {
                    await destStream.WriteAsync(buffer, 0, read, cancellationToken);
                }
            }

            var chunk = new CachedChunkEntry
            {
                GameId = gameId,
                RelativePath = relativePath,
                ChunkIndex = chunkIndex,
                ChunkOffset = offset,
                ChunkLength = bytesToRead,
                CacheFilePath = targetChunkPath,
                CachedAt = DateTime.UtcNow,
                LastAccessedAt = DateTime.UtcNow,
                PriorityScore = priorityScore,
                IsValid = true
            };

            await _db.InsertOrUpdateCachedChunkAsync(chunk);
            return chunk;
        }
        catch (OperationCanceledException)
        {
            if (File.Exists(targetChunkPath))
            {
                try { File.Delete(targetChunkPath); } catch { }
            }
            throw;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<CachedFileEntry?> CacheFileAsync(
        int gameId, 
        string installDir, 
        string relativePath, 
        double priorityScore, 
        IProgress<long>? bytesCopiedProgress = null, 
        CancellationToken cancellationToken = default)
    {
        var sourcePath = Path.Combine(installDir, relativePath);
        if (!File.Exists(sourcePath)) return null;

        var sourceInfo = new FileInfo(sourcePath);
        var cacheBaseDir = await GetEffectiveCacheDirectoryAsync();
        var gameCacheDir = Path.Combine(cacheBaseDir, $"Game_{gameId}");
        Directory.CreateDirectory(gameCacheDir);

        var safeFileName = HashPath(relativePath) + "_" + Path.GetFileName(relativePath);
        var targetCachePath = Path.Combine(gameCacheDir, safeFileName);

        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            const int bufferSize = 1024 * 512;
            var buffer = new byte[bufferSize];

            using (var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous))
            using (var destStream = new FileStream(targetCachePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous))
            {
                int bytesRead;
                while ((bytesRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    await destStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                    bytesCopiedProgress?.Report(bytesRead);
                }
            }

            var entry = new CachedFileEntry
            {
                GameId = gameId,
                RelativePath = relativePath,
                CacheFilePath = targetCachePath,
                FileSizeBytes = sourceInfo.Length,
                OriginalLastWriteTimeUtc = sourceInfo.LastWriteTimeUtc,
                CachedAt = DateTime.UtcNow,
                LastAccessedAt = DateTime.UtcNow,
                PriorityScore = priorityScore,
                IsValid = true
            };

            await _db.InsertOrUpdateCachedFileAsync(entry);
            return entry;
        }
        catch (OperationCanceledException)
        {
            if (File.Exists(targetCachePath))
            {
                try { File.Delete(targetCachePath); } catch { }
            }
            throw;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task EnforceCacheQuotaAsync(long maxBudgetBytes, CancellationToken cancellationToken = default)
    {
        var totalCached = await _db.GetTotalCachedBytesAsync();
        if (totalCached <= maxBudgetBytes) return;

        var games = await _db.GetAllGamesAsync();
        var allCached = new List<CachedFileEntry>();
        var allChunks = new List<CachedChunkEntry>();

        foreach (var g in games)
        {
            var files = await _db.GetCachedFilesForGameAsync(g.Id);
            allCached.AddRange(files);
            var chunks = await _db.GetCachedChunksForGameAsync(g.Id);
            allChunks.AddRange(chunks);
        }

        // Sort chunks and files by eviction score (lowest priority score first, age penalty)
        var currentTotal = totalCached;

        // Evict chunks first if available
        var evictChunks = allChunks
            .OrderBy(c => c.PriorityScore / (1.0 + (DateTime.UtcNow - c.LastAccessedAt).TotalDays))
            .ToList();

        foreach (var candidate in evictChunks)
        {
            if (currentTotal <= maxBudgetBytes || cancellationToken.IsCancellationRequested) break;

            if (File.Exists(candidate.CacheFilePath))
            {
                try { File.Delete(candidate.CacheFilePath); } catch { }
            }

            await _db.RemoveCachedChunkAsync(candidate.GameId, candidate.RelativePath, candidate.ChunkIndex);
            currentTotal -= candidate.ChunkLength;
        }

        if (currentTotal > maxBudgetBytes)
        {
            var evictFiles = allCached
                .OrderBy(c => c.PriorityScore / (1.0 + (DateTime.UtcNow - c.LastAccessedAt).TotalDays))
                .ToList();

            foreach (var candidate in evictFiles)
            {
                if (currentTotal <= maxBudgetBytes || cancellationToken.IsCancellationRequested) break;

                if (File.Exists(candidate.CacheFilePath))
                {
                    try { File.Delete(candidate.CacheFilePath); } catch { }
                }

                await _db.RemoveCachedFileAsync(candidate.GameId, candidate.RelativePath);
                currentTotal -= candidate.FileSizeBytes;
            }
        }
    }

    public async Task ClearGameCacheAsync(int gameId)
    {
        var cached = await _db.GetCachedFilesForGameAsync(gameId);
        foreach (var c in cached)
        {
            if (File.Exists(c.CacheFilePath))
            {
                try { File.Delete(c.CacheFilePath); } catch { }
            }
        }

        var chunks = await _db.GetCachedChunksForGameAsync(gameId);
        foreach (var ch in chunks)
        {
            if (File.Exists(ch.CacheFilePath))
            {
                try { File.Delete(ch.CacheFilePath); } catch { }
            }
        }

        await _db.ClearCacheForGameAsync(gameId);
    }

    private static string HashPath(string path)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..12];
    }
}

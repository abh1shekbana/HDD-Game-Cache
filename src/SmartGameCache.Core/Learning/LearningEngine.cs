using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SmartGameCache.Core.Database;
using SmartGameCache.Core.Models;

namespace SmartGameCache.Core.Learning;

public class CacheProfileExportModel
{
    public string GameName { get; set; } = string.Empty;
    public string LauncherSource { get; set; } = string.Empty;
    public DateTime ExportedAt { get; set; } = DateTime.UtcNow;
    public int LearnedSessionsCount { get; set; }
    public double EstimatedHitRate { get; set; }
    public List<ObservedFileExportItem> HotAssets { get; set; } = new();
}

public class ObservedFileExportItem
{
    public string RelativePath { get; set; } = string.Empty;
    public int AccessCount { get; set; }
    public long TotalBytesRead { get; set; }
    public bool IsStartupFile { get; set; }
    public double PriorityScore { get; set; }
    public long FileSizeBytes { get; set; }
}

public class LearningEngine
{
    private readonly DatabaseService _db;
    private readonly FrequencySketch _sketch = new(65536);

    public LearningEngine(DatabaseService db)
    {
        _db = db;
    }

    public async Task ProcessSessionLearningAsync(int gameId, GamingSession session, IEnumerable<ObservedFile> sessionAccessEvents)
    {
        var rawEvents = sessionAccessEvents.ToList();
        if (rawEvents.Count == 0) return;

        // Fetch existing observed files to combine multi-session patterns
        var existingFiles = await _db.GetObservedFilesForGameAsync(gameId, 5000);
        var existingDict = existingFiles.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);

        // Aggregate by relative path
        var aggregated = new Dictionary<string, ObservedFile>(StringComparer.OrdinalIgnoreCase);

        foreach (var ev in rawEvents)
        {
            if (string.IsNullOrWhiteSpace(ev.RelativePath)) continue;

            // Record into Count-Min 4-bit sketch
            _sketch.RecordAccess(ev.RelativePath);

            if (aggregated.TryGetValue(ev.RelativePath, out var agg))
            {
                agg.AccessCount += ev.AccessCount;
                agg.TotalBytesRead += ev.TotalBytesRead;
                agg.IsStartupFile = agg.IsStartupFile || ev.IsStartupFile;
                agg.LastAccessedAt = ev.LastAccessedAt > agg.LastAccessedAt ? ev.LastAccessedAt : agg.LastAccessedAt;
                if (agg.FileSizeBytes == 0 && ev.FileSizeBytes > 0) agg.FileSizeBytes = ev.FileSizeBytes;
            }
            else
            {
                aggregated[ev.RelativePath] = new ObservedFile
                {
                    GameId = gameId,
                    RelativePath = ev.RelativePath,
                    FullPath = ev.FullPath,
                    AccessCount = ev.AccessCount,
                    TotalBytesRead = ev.TotalBytesRead,
                    IsStartupFile = ev.IsStartupFile,
                    FirstSeenAt = ev.FirstSeenAt,
                    LastAccessedAt = ev.LastAccessedAt,
                    FileSizeBytes = ev.FileSizeBytes > 0 ? ev.FileSizeBytes : (File.Exists(ev.FullPath) ? new FileInfo(ev.FullPath).Length : 0)
                };
            }
        }

        var now = DateTime.UtcNow;

        foreach (var file in aggregated.Values)
        {
            // Retrieve frequency from Count-Min Sketch (0 to 15)
            int sketchFreq = _sketch.EstimateFrequency(file.RelativePath);
            double normFreq = Math.Min(1.0, (double)sketchFreq / 15.0);

            // Startup files receive high priority weighting
            double startupWeight = file.IsStartupFile ? 1.0 : 0.0;

            // Recency decay (half-life of 7 days)
            double daysSinceAccess = Math.Max(0.0, (now - file.LastAccessedAt).TotalDays);
            double recencyWeight = Math.Exp(-daysSinceAccess / 7.0);

            // Size efficiency: prefer files that are read frequently relative to their size
            double sizeEff = 0.5;
            if (file.FileSizeBytes > 0)
            {
                double ratio = (double)file.TotalBytesRead / file.FileSizeBytes;
                sizeEff = Math.Min(1.0, ratio / 5.0);
            }

            // Weighted multi-factor priority score:
            // 35% Frequency, 25% Startup importance, 25% Recency, 15% Size efficiency
            file.PriorityScore = Math.Round(
                (0.35 * normFreq) +
                (0.25 * startupWeight) +
                (0.25 * recencyWeight) +
                (0.15 * sizeEff), 4);
        }

        // Save aggregated files to database
        await _db.UpsertObservedFilesAsync(aggregated.Values);

        // Update GameProfile session count and estimated hit rate
        var profile = await _db.GetProfileAsync(gameId);
        profile.LearnedSessionsCount++;
        profile.LastUpdated = DateTime.UtcNow;

        // Estimate potential hit rate based on learned file access coverage
        var allObserved = await _db.GetObservedFilesForGameAsync(gameId, 5000);
        long totalObservedBytes = allObserved.Sum(f => f.TotalBytesRead);
        long topCachedBytes = allObserved
            .OrderByDescending(f => f.PriorityScore)
            .TakeWhile((f, i) => f.PriorityScore > 0.3)
            .Sum(f => f.TotalBytesRead);

        if (totalObservedBytes > 0)
        {
            profile.EstimatedHitRate = Math.Min(95.0, Math.Round((double)topCachedBytes / totalObservedBytes * 100.0, 1));
        }

        await _db.UpdateProfileAsync(profile);
    }

    public async Task<string> ExportProfileJsonAsync(int gameId, string outputPath)
    {
        var game = await _db.GetGameByIdAsync(gameId);
        if (game == null) throw new ArgumentException("Game not found.");

        var profile = await _db.GetProfileAsync(gameId);
        var observed = await _db.GetObservedFilesForGameAsync(gameId, 1000);

        var export = new CacheProfileExportModel
        {
            GameName = game.Name,
            LauncherSource = game.LauncherSource,
            ExportedAt = DateTime.UtcNow,
            LearnedSessionsCount = profile.LearnedSessionsCount,
            EstimatedHitRate = profile.EstimatedHitRate,
            HotAssets = observed.Select(o => new ObservedFileExportItem
            {
                RelativePath = o.RelativePath,
                AccessCount = o.AccessCount,
                TotalBytesRead = o.TotalBytesRead,
                IsStartupFile = o.IsStartupFile,
                PriorityScore = o.PriorityScore,
                FileSizeBytes = o.FileSizeBytes
            }).ToList()
        };

        var json = JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(outputPath, json);
        return outputPath;
    }

    public async Task<int> ImportProfileJsonAsync(int gameId, string jsonFilePath)
    {
        var game = await _db.GetGameByIdAsync(gameId);
        if (game == null) throw new ArgumentException("Game not found.");

        if (!File.Exists(jsonFilePath)) throw new FileNotFoundException("Profile file not found.");

        var json = await File.ReadAllTextAsync(jsonFilePath);
        var export = JsonSerializer.Deserialize<CacheProfileExportModel>(json);
        if (export == null || export.HotAssets == null) throw new FormatException("Invalid profile format.");

        // Validate each asset exists on the local filesystem
        var validAssets = new List<ObservedFile>();
        foreach (var item in export.HotAssets)
        {
            var fullPath = Path.Combine(game.InstallDirectory, item.RelativePath);
            if (File.Exists(fullPath))
            {
                var fi = new FileInfo(fullPath);
                validAssets.Add(new ObservedFile
                {
                    GameId = gameId,
                    RelativePath = item.RelativePath,
                    FullPath = fullPath,
                    AccessCount = item.AccessCount,
                    TotalBytesRead = item.TotalBytesRead,
                    IsStartupFile = item.IsStartupFile,
                    PriorityScore = item.PriorityScore,
                    FileSizeBytes = fi.Length,
                    FirstSeenAt = DateTime.UtcNow,
                    LastAccessedAt = DateTime.UtcNow
                });
            }
        }

        if (validAssets.Count > 0)
        {
            await _db.UpsertObservedFilesAsync(validAssets);
            var profile = await _db.GetProfileAsync(gameId);
            profile.LearnedSessionsCount = Math.Max(profile.LearnedSessionsCount, export.LearnedSessionsCount);
            profile.EstimatedHitRate = export.EstimatedHitRate;
            profile.LastUpdated = DateTime.UtcNow;
            await _db.UpdateProfileAsync(profile);
        }

        return validAssets.Count;
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SmartGameCache.Core.Database;
using SmartGameCache.Core.Detection;
using SmartGameCache.Core.Learning;
using SmartGameCache.Core.Models;
using SmartGameCache.Core.Monitoring;
using SmartGameCache.Core.Prefetch;
using SmartGameCache.Core.Storage;

namespace SmartGameCache.Core.Services;

public class GameCacheOrchestrator : IDisposable
{
    public DatabaseService Database { get; }
    public GameDetectionService Detection { get; }
    public SsdCacheManager SsdCache { get; }
    public RamPrefetchEngine RamPrefetch { get; }
    public LearningEngine Learning { get; }
    public GameSessionMonitor SessionMonitor { get; }
    public AnalyticsService Analytics { get; }

    public event Action<string>? OnStatusMessage;

    public GameCacheOrchestrator(string? dbPath = null)
    {
        Database = new DatabaseService(dbPath);
        Detection = new GameDetectionService();
        SsdCache = new SsdCacheManager(Database);
        RamPrefetch = new RamPrefetchEngine();
        Learning = new LearningEngine(Database);
        SessionMonitor = new GameSessionMonitor(Database, Learning);
        Analytics = new AnalyticsService(Database);
    }

    public async Task InitializeAsync()
    {
        await Database.InitializeAsync();
    }

    public async Task<List<GameInfo>> ScanAndSyncGamesAsync()
    {
        OnStatusMessage?.Invoke("Scanning for installed games (Steam, Epic, Rockstar)...");
        var detected = Detection.DetectAllInstalledGames();

        foreach (var g in detected)
        {
            await Database.InsertOrUpdateGameAsync(g);
        }

        var allGames = await Database.GetAllGamesAsync();
        OnStatusMessage?.Invoke($"Found {allGames.Count} game(s) in library.");
        return allGames;
    }

    public async Task<int> AddManualGameAsync(string executablePath, string? customName = null)
    {
        var game = Detection.CreateManualGame(executablePath, customName);
        if (game == null) throw new InvalidOperationException("Invalid game executable path.");

        var id = await Database.InsertOrUpdateGameAsync(game);
        OnStatusMessage?.Invoke($"Added {game.Name} to library.");
        return id;
    }

    public async Task<CachePreparationProgress> PrepareCacheAsync(
        int gameId,
        IProgress<CachePreparationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var game = await Database.GetGameByIdAsync(gameId);
        if (game == null) throw new ArgumentException("Game not found.");

        var profile = await Database.GetProfileAsync(gameId);
        var observed = await Database.GetObservedFilesForGameAsync(gameId, 5000);

        var prep = new CachePreparationProgress
        {
            Phase = "Analyzing Cache Profile"
        };
        progress?.Report(prep);

        // Filter top priority observed files
        var candidateFiles = observed
            .Where(f => File.Exists(f.FullPath))
            .OrderByDescending(f => f.PriorityScore)
            .ToList();

        // Step 1: Ensure top files are in SSD Cache up to configured SSD capacity
        prep.Phase = "Populating SSD Cache";
        long totalSsdBudget = profile.SsdCacheAllocatedBytes;
        long currentSsdUsed = 0;

        var cachedEntries = await Database.GetCachedFilesForGameAsync(gameId);
        var cachedMap = cachedEntries.ToDictionary(c => c.RelativePath, StringComparer.OrdinalIgnoreCase);

        var validCachedFiles = new List<CachedFileEntry>();

        foreach (var file in candidateFiles)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                prep.IsCancelled = true;
                prep.Phase = "Cancelled";
                progress?.Report(prep);
                return prep;
            }

            if (currentSsdUsed + file.FileSizeBytes > totalSsdBudget && validCachedFiles.Count > 0)
            {
                break;
            }

            if (cachedMap.TryGetValue(file.RelativePath, out var existing))
            {
                bool isValid = await SsdCache.ValidateCacheEntryAsync(existing, game.InstallDirectory);
                if (isValid)
                {
                    validCachedFiles.Add(existing);
                    currentSsdUsed += existing.FileSizeBytes;
                    continue;
                }
            }

            // Copy to SSD
            prep.CurrentFileName = Path.GetFileName(file.RelativePath);
            progress?.Report(prep);

            var newEntry = await SsdCache.CacheFileAsync(
                gameId,
                game.InstallDirectory,
                file.RelativePath,
                file.PriorityScore,
                cancellationToken: cancellationToken
            );

            if (newEntry != null)
            {
                validCachedFiles.Add(newEntry);
                currentSsdUsed += newEntry.FileSizeBytes;
            }
        }

        // Step 2: Warm Windows Cache via RAM Prefetch Engine
        prep.Phase = "Warming RAM Cache";
        progress?.Report(prep);

        var finalResult = await RamPrefetch.PrefetchAsync(
            validCachedFiles,
            profile.RamBudgetBytes,
            progress,
            cancellationToken
        );

        OnStatusMessage?.Invoke($"Cache prepared: {validCachedFiles.Count} files ({validCachedFiles.Sum(f => f.FileSizeBytes) / (1024 * 1024)} MB).");
        return finalResult;
    }

    public async Task<Process?> LaunchGameAsync(
        int gameId,
        bool prepareCache = true,
        IProgress<CachePreparationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var game = await Database.GetGameByIdAsync(gameId);
        if (game == null) throw new ArgumentException("Game not found.");

        if (prepareCache)
        {
            await PrepareCacheAsync(gameId, progress, cancellationToken);
        }

        if (!File.Exists(game.ExecutablePath))
        {
            throw new FileNotFoundException($"Game executable not found: {game.ExecutablePath}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = game.ExecutablePath,
            WorkingDirectory = game.InstallDirectory,
            UseShellExecute = true
        };

        var process = Process.Start(startInfo);
        if (process != null)
        {
            game.LastPlayedAt = DateTime.UtcNow;
            await Database.InsertOrUpdateGameAsync(game);

            // Attach monitoring
            await SessionMonitor.StartMonitoringAsync(game, process);
            OnStatusMessage?.Invoke($"Launched and monitoring {game.Name} (PID: {process.Id}).");
        }

        return process;
    }

    public async Task<bool> AttachToRunningProcessAsync(int gameId, int processId)
    {
        var game = await Database.GetGameByIdAsync(gameId);
        if (game == null) return false;

        var process = Process.GetProcessById(processId);
        if (process.HasExited) return false;

        await SessionMonitor.StartMonitoringAsync(game, process);
        OnStatusMessage?.Invoke($"Attached monitoring to {game.Name} (PID: {process.Id}).");
        return true;
    }

    public void Dispose()
    {
        SessionMonitor.Dispose();
        Database.Dispose();
    }
}


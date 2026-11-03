using System;

namespace SmartGameCache.Core.Models;

public enum StorageMediaType
{
    Unknown,
    HDD,
    SSD,
    NVMe
}

public enum GameSessionStatus
{
    Running,
    Completed,
    Aborted,
    Failed
}

public class GameInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string InstallDirectory { get; set; } = string.Empty;
    public string DriveLetter { get; set; } = string.Empty;
    public StorageMediaType StorageType { get; set; } = StorageMediaType.Unknown;
    public string LauncherSource { get; set; } = "Manual"; // Steam, Epic, Rockstar, Manual
    public long TotalInstallSizeBytes { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastPlayedAt { get; set; }
    public string? IconPath { get; set; }
}

public class GameProfile
{
    public int GameId { get; set; }
    public int LearnedSessionsCount { get; set; }
    public long SsdCacheAllocatedBytes { get; set; } = 30L * 1024 * 1024 * 1024; // 30 GB default
    public long RamBudgetBytes { get; set; } = 2L * 1024 * 1024 * 1024; // 2 GB default
    public bool AutoPreloadEnabled { get; set; } = true;
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public double EstimatedHitRate { get; set; }
    public long TotalHddReadsAvoided { get; set; }
}

public class GamingSession
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
    public int GameId { get; set; }
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public DateTime? EndTime { get; set; }
    public double DurationSeconds { get; set; }
    public long TotalBytesRead { get; set; }
    public long ReadOperationsCount { get; set; }
    public bool ColdLaunch { get; set; }
    public double StartupDurationSeconds { get; set; }
    public GameSessionStatus Status { get; set; } = GameSessionStatus.Running;
    public long DroppedEventsCount { get; set; }
}

public class ObservedFile
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public long TotalBytesRead { get; set; }
    public int AccessCount { get; set; }
    public bool IsStartupFile { get; set; }
    public double PriorityScore { get; set; }
    public long FileSizeBytes { get; set; }
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;
    public bool IsInSsdCache { get; set; }
}

public class CachedFileEntry
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string CacheFilePath { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public DateTime OriginalLastWriteTimeUtc { get; set; }
    public DateTime CachedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;
    public double PriorityScore { get; set; }
    public bool IsValid { get; set; } = true;
}

public class CachedChunkEntry
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public int ChunkIndex { get; set; }
    public long ChunkOffset { get; set; }
    public int ChunkLength { get; set; }
    public string CacheFilePath { get; set; } = string.Empty;
    public DateTime CachedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;
    public double PriorityScore { get; set; }
    public bool IsValid { get; set; } = true;
}

public class CacheStatisticRecord
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public long SsdUsedBytes { get; set; }
    public long SsdMaxBytes { get; set; }
    public long RamBudgetBytes { get; set; }
    public long RamUtilizedBytes { get; set; }
    public long TotalHddReadsObserved { get; set; }
    public long VerifiedCacheHits { get; set; }
    public double HitRatePercent { get; set; }
}

public class AppSettings
{
    public long RamBudgetBytes { get; set; } = 2L * 1024 * 1024 * 1024; // 2 GB
    public long SsdCapacityBytes { get; set; } = 30L * 1024 * 1024 * 1024; // 30 GB
    public string SsdCacheDirectory { get; set; } = string.Empty;
    public string EvictionPolicy { get; set; } = "Hybrid (ARC + TinyLFU)";
    public bool StartWithWindows { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public bool AutoDetectGames { get; set; } = true;
    public bool AutoMonitoring { get; set; } = true;
    public bool AutoPreload { get; set; } = true;
    public string Theme { get; set; } = "Dark";
    public string MonitoringVerbosity { get; set; } = "Standard";
    public int ChunkSizeKb { get; set; } = 128; // 128 KB optimal chunk size
}

public class CachePreparationProgress
{
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public long TotalBytes { get; set; }
    public long ProcessedBytes { get; set; }
    public double SpeedBytesPerSec { get; set; }
    public TimeSpan? EstimatedTimeRemaining { get; set; }
    public string CurrentFileName { get; set; } = string.Empty;
    public string Phase { get; set; } = "Preparing"; // SSD Copying, RAM Warming, Ready
    public bool IsCancelled { get; set; }
    public bool IsCompleted { get; set; }
    public string? ErrorMessage { get; set; }

    public double PercentComplete => TotalBytes > 0 
        ? Math.Min(100.0, Math.Max(0.0, (double)ProcessedBytes / TotalBytes * 100.0))
        : (TotalFiles > 0 ? (double)ProcessedFiles / TotalFiles * 100.0 : 0.0);
}

public class HardwareBenchmarkReport
{
    public string ReportId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public string GameName { get; set; } = string.Empty;
    public string GameInstallPath { get; set; } = string.Empty;
    public string DriveLetter { get; set; } = string.Empty;
    public string HddModel { get; set; } = string.Empty;
    public string HddMediaType { get; set; } = string.Empty;
    public bool IsRotationalHdd { get; set; }
    public double HddReadThroughputMbSec { get; set; }
    public long TotalObservedReadBytes { get; set; }
    public long SsdCacheUsedBytes { get; set; }
    public long RamPrefetchUsedBytes { get; set; }
    public long VerifiedCacheHitsBytes { get; set; }
    public long AvoidedHddReadsBytes { get; set; }
    public double HitRatePercent { get; set; }
    public double BaselineColdLoadTimeSec { get; set; }
    public double AcceleratedWarmLoadTimeSec { get; set; }
    public double LoadTimeImprovementPercent { get; set; }
    public double SessionDurationSeconds { get; set; }
    public long DroppedEventsCount { get; set; }
    public string ActiveAccelerationMode { get; set; } = "User-Mode Preheating";
}

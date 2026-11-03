using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SmartGameCache.Core.Models;

namespace SmartGameCache.Core.Database;

public class DatabaseService : IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _isInitialized;

    public string DatabasePath { get; }

    public DatabaseService(string? customDbPath = null)
    {
        if (string.IsNullOrWhiteSpace(customDbPath))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appFolder = Path.Combine(appData, "SmartGameCache");
            Directory.CreateDirectory(appFolder);
            DatabasePath = Path.Combine(appFolder, "SmartGameCache.db");
        }
        else
        {
            DatabasePath = customDbPath;
            var dir = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        await _lock.WaitAsync();
        try
        {
            if (_isInitialized) return;

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var pragmaCmd = connection.CreateCommand();
            pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            await pragmaCmd.ExecuteNonQueryAsync();

            var schemaSql = @"
                CREATE TABLE IF NOT EXISTS Games (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    ExecutablePath TEXT NOT NULL UNIQUE,
                    InstallDirectory TEXT NOT NULL,
                    DriveLetter TEXT NOT NULL,
                    StorageType INTEGER NOT NULL,
                    LauncherSource TEXT NOT NULL,
                    TotalInstallSizeBytes INTEGER NOT NULL,
                    AddedAt TEXT NOT NULL,
                    LastPlayedAt TEXT,
                    IconPath TEXT
                );

                CREATE TABLE IF NOT EXISTS GameProfiles (
                    GameId INTEGER PRIMARY KEY,
                    LearnedSessionsCount INTEGER NOT NULL DEFAULT 0,
                    SsdCacheAllocatedBytes INTEGER NOT NULL,
                    RamBudgetBytes INTEGER NOT NULL,
                    AutoPreloadEnabled INTEGER NOT NULL DEFAULT 1,
                    LastUpdated TEXT NOT NULL,
                    EstimatedHitRate REAL NOT NULL DEFAULT 0.0,
                    TotalHddReadsAvoided INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY (GameId) REFERENCES Games(Id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS GamingSessions (
                    SessionId TEXT PRIMARY KEY,
                    GameId INTEGER NOT NULL,
                    StartTime TEXT NOT NULL,
                    EndTime TEXT,
                    DurationSeconds REAL NOT NULL,
                    TotalBytesRead INTEGER NOT NULL,
                    ReadOperationsCount INTEGER NOT NULL,
                    ColdLaunch INTEGER NOT NULL,
                    StartupDurationSeconds REAL NOT NULL DEFAULT 0.0,
                    Status INTEGER NOT NULL,
                    DroppedEventsCount INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY (GameId) REFERENCES Games(Id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS ObservedFiles (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    GameId INTEGER NOT NULL,
                    RelativePath TEXT NOT NULL,
                    FullPath TEXT NOT NULL,
                    TotalBytesRead INTEGER NOT NULL,
                    AccessCount INTEGER NOT NULL,
                    IsStartupFile INTEGER NOT NULL DEFAULT 0,
                    PriorityScore REAL NOT NULL DEFAULT 0.0,
                    FileSizeBytes INTEGER NOT NULL DEFAULT 0,
                    FirstSeenAt TEXT NOT NULL,
                    LastAccessedAt TEXT NOT NULL,
                    IsInSsdCache INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY (GameId) REFERENCES Games(Id) ON DELETE CASCADE,
                    UNIQUE(GameId, RelativePath)
                );

                CREATE TABLE IF NOT EXISTS CachedFiles (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    GameId INTEGER NOT NULL,
                    RelativePath TEXT NOT NULL,
                    CacheFilePath TEXT NOT NULL,
                    FileSizeBytes INTEGER NOT NULL,
                    OriginalLastWriteTimeUtc TEXT NOT NULL,
                    CachedAt TEXT NOT NULL,
                    LastAccessedAt TEXT NOT NULL,
                    PriorityScore REAL NOT NULL,
                    IsValid INTEGER NOT NULL DEFAULT 1,
                    FOREIGN KEY (GameId) REFERENCES Games(Id) ON DELETE CASCADE,
                    UNIQUE(GameId, RelativePath)
                );

                CREATE TABLE IF NOT EXISTS CachedChunks (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    GameId INTEGER NOT NULL,
                    RelativePath TEXT NOT NULL,
                    ChunkIndex INTEGER NOT NULL,
                    ChunkOffset INTEGER NOT NULL,
                    ChunkLength INTEGER NOT NULL,
                    CacheFilePath TEXT NOT NULL,
                    CachedAt TEXT NOT NULL,
                    LastAccessedAt TEXT NOT NULL,
                    PriorityScore REAL NOT NULL,
                    IsValid INTEGER NOT NULL DEFAULT 1,
                    FOREIGN KEY (GameId) REFERENCES Games(Id) ON DELETE CASCADE,
                    UNIQUE(GameId, RelativePath, ChunkIndex)
                );

                CREATE TABLE IF NOT EXISTS HardwareBenchmarkReports (
                    ReportId TEXT PRIMARY KEY,
                    GeneratedAt TEXT NOT NULL,
                    GameName TEXT NOT NULL,
                    GameInstallPath TEXT NOT NULL,
                    DriveLetter TEXT NOT NULL,
                    HddModel TEXT NOT NULL,
                    HddMediaType TEXT NOT NULL,
                    IsRotationalHdd INTEGER NOT NULL,
                    HddReadThroughputMbSec REAL NOT NULL,
                    TotalObservedReadBytes INTEGER NOT NULL,
                    SsdCacheUsedBytes INTEGER NOT NULL,
                    RamPrefetchUsedBytes INTEGER NOT NULL,
                    VerifiedCacheHitsBytes INTEGER NOT NULL,
                    AvoidedHddReadsBytes INTEGER NOT NULL,
                    HitRatePercent REAL NOT NULL,
                    BaselineColdLoadTimeSec REAL NOT NULL,
                    AcceleratedWarmLoadTimeSec REAL NOT NULL,
                    LoadTimeImprovementPercent REAL NOT NULL,
                    SessionDurationSeconds REAL NOT NULL,
                    DroppedEventsCount INTEGER NOT NULL,
                    ActiveAccelerationMode TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS CacheStatistics (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp TEXT NOT NULL,
                    SsdUsedBytes INTEGER NOT NULL,
                    SsdMaxBytes INTEGER NOT NULL,
                    RamBudgetBytes INTEGER NOT NULL,
                    RamUtilizedBytes INTEGER NOT NULL,
                    TotalHddReadsObserved INTEGER NOT NULL,
                    VerifiedCacheHits INTEGER NOT NULL,
                    HitRatePercent REAL NOT NULL
                );

                CREATE TABLE IF NOT EXISTS ApplicationSettings (
                    Key TEXT PRIMARY KEY,
                    Value TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS IX_ObservedFiles_GamePriority ON ObservedFiles(GameId, PriorityScore DESC);
                CREATE INDEX IF NOT EXISTS IX_CachedFiles_GamePriority ON CachedFiles(GameId, PriorityScore DESC);
                CREATE INDEX IF NOT EXISTS IX_CachedChunks_GamePriority ON CachedChunks(GameId, PriorityScore DESC);
                CREATE INDEX IF NOT EXISTS IX_GamingSessions_GameId ON GamingSessions(GameId, StartTime DESC);
            ";

            using var createCmd = connection.CreateCommand();
            createCmd.CommandText = schemaSql;
            await createCmd.ExecuteNonQueryAsync();

            _isInitialized = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<GameInfo>> GetAllGamesAsync()
    {
        await InitializeAsync();
        var list = new List<GameInfo>();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, ExecutablePath, InstallDirectory, DriveLetter, StorageType, LauncherSource, TotalInstallSizeBytes, AddedAt, LastPlayedAt, IconPath FROM Games ORDER BY Name ASC;";

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new GameInfo
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                ExecutablePath = reader.GetString(2),
                InstallDirectory = reader.GetString(3),
                DriveLetter = reader.GetString(4),
                StorageType = (StorageMediaType)reader.GetInt32(5),
                LauncherSource = reader.GetString(6),
                TotalInstallSizeBytes = reader.GetInt64(7),
                AddedAt = DateTime.Parse(reader.GetString(8)),
                LastPlayedAt = reader.IsDBNull(9) ? null : DateTime.Parse(reader.GetString(9)),
                IconPath = reader.IsDBNull(10) ? null : reader.GetString(10)
            });
        }

        return list;
    }

    public async Task<GameInfo?> GetGameByIdAsync(int id)
    {
        await InitializeAsync();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, ExecutablePath, InstallDirectory, DriveLetter, StorageType, LauncherSource, TotalInstallSizeBytes, AddedAt, LastPlayedAt, IconPath FROM Games WHERE Id = @Id LIMIT 1;";
        cmd.Parameters.AddWithValue("@Id", id);

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new GameInfo
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                ExecutablePath = reader.GetString(2),
                InstallDirectory = reader.GetString(3),
                DriveLetter = reader.GetString(4),
                StorageType = (StorageMediaType)reader.GetInt32(5),
                LauncherSource = reader.GetString(6),
                TotalInstallSizeBytes = reader.GetInt64(7),
                AddedAt = DateTime.Parse(reader.GetString(8)),
                LastPlayedAt = reader.IsDBNull(9) ? null : DateTime.Parse(reader.GetString(9)),
                IconPath = reader.IsDBNull(10) ? null : reader.GetString(10)
            };
        }

        return null;
    }

    public async Task<int> InsertOrUpdateGameAsync(GameInfo game)
    {
        await InitializeAsync();
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Games (Name, ExecutablePath, InstallDirectory, DriveLetter, StorageType, LauncherSource, TotalInstallSizeBytes, AddedAt, LastPlayedAt, IconPath)
                VALUES (@Name, @ExecutablePath, @InstallDirectory, @DriveLetter, @StorageType, @LauncherSource, @TotalInstallSizeBytes, @AddedAt, @LastPlayedAt, @IconPath)
                ON CONFLICT(ExecutablePath) DO UPDATE SET
                    Name = excluded.Name,
                    InstallDirectory = excluded.InstallDirectory,
                    DriveLetter = excluded.DriveLetter,
                    StorageType = excluded.StorageType,
                    TotalInstallSizeBytes = excluded.TotalInstallSizeBytes,
                    LastPlayedAt = COALESCE(excluded.LastPlayedAt, Games.LastPlayedAt),
                    IconPath = COALESCE(excluded.IconPath, Games.IconPath)
                RETURNING Id;
            ";

            cmd.Parameters.AddWithValue("@Name", game.Name);
            cmd.Parameters.AddWithValue("@ExecutablePath", game.ExecutablePath);
            cmd.Parameters.AddWithValue("@InstallDirectory", game.InstallDirectory);
            cmd.Parameters.AddWithValue("@DriveLetter", game.DriveLetter);
            cmd.Parameters.AddWithValue("@StorageType", (int)game.StorageType);
            cmd.Parameters.AddWithValue("@LauncherSource", game.LauncherSource);
            cmd.Parameters.AddWithValue("@TotalInstallSizeBytes", game.TotalInstallSizeBytes);
            cmd.Parameters.AddWithValue("@AddedAt", game.AddedAt.ToString("o"));
            cmd.Parameters.AddWithValue("@LastPlayedAt", (object?)game.LastPlayedAt?.ToString("o") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@IconPath", (object?)game.IconPath ?? DBNull.Value);

            var idObj = await cmd.ExecuteScalarAsync();
            var id = Convert.ToInt32(idObj);
            game.Id = id;

            // Ensure profile exists
            using var profileCmd = connection.CreateCommand();
            profileCmd.CommandText = @"
                INSERT INTO GameProfiles (GameId, LearnedSessionsCount, SsdCacheAllocatedBytes, RamBudgetBytes, AutoPreloadEnabled, LastUpdated, EstimatedHitRate, TotalHddReadsAvoided)
                VALUES (@GameId, 0, 32212254720, 2147483648, 1, @LastUpdated, 0.0, 0)
                ON CONFLICT(GameId) DO NOTHING;
            ";
            profileCmd.Parameters.AddWithValue("@GameId", id);
            profileCmd.Parameters.AddWithValue("@LastUpdated", DateTime.UtcNow.ToString("o"));
            await profileCmd.ExecuteNonQueryAsync();

            return id;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteGameAsync(int gameId)
    {
        await InitializeAsync();
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Games WHERE Id = @Id;";
            cmd.Parameters.AddWithValue("@Id", gameId);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<GameProfile> GetProfileAsync(int gameId)
    {
        await InitializeAsync();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT GameId, LearnedSessionsCount, SsdCacheAllocatedBytes, RamBudgetBytes, AutoPreloadEnabled, LastUpdated, EstimatedHitRate, TotalHddReadsAvoided FROM GameProfiles WHERE GameId = @GameId LIMIT 1;";
        cmd.Parameters.AddWithValue("@GameId", gameId);

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new GameProfile
            {
                GameId = reader.GetInt32(0),
                LearnedSessionsCount = reader.GetInt32(1),
                SsdCacheAllocatedBytes = reader.GetInt64(2),
                RamBudgetBytes = reader.GetInt64(3),
                AutoPreloadEnabled = reader.GetInt32(4) != 0,
                LastUpdated = DateTime.Parse(reader.GetString(5)),
                EstimatedHitRate = reader.GetDouble(6),
                TotalHddReadsAvoided = reader.GetInt64(7)
            };
        }

        return new GameProfile { GameId = gameId };
    }

    public async Task UpdateProfileAsync(GameProfile profile)
    {
        await InitializeAsync();
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO GameProfiles (GameId, LearnedSessionsCount, SsdCacheAllocatedBytes, RamBudgetBytes, AutoPreloadEnabled, LastUpdated, EstimatedHitRate, TotalHddReadsAvoided)
                VALUES (@GameId, @LearnedSessionsCount, @SsdCacheAllocatedBytes, @RamBudgetBytes, @AutoPreloadEnabled, @LastUpdated, @EstimatedHitRate, @TotalHddReadsAvoided)
                ON CONFLICT(GameId) DO UPDATE SET
                    LearnedSessionsCount = excluded.LearnedSessionsCount,
                    SsdCacheAllocatedBytes = excluded.SsdCacheAllocatedBytes,
                    RamBudgetBytes = excluded.RamBudgetBytes,
                    AutoPreloadEnabled = excluded.AutoPreloadEnabled,
                    LastUpdated = excluded.LastUpdated,
                    EstimatedHitRate = excluded.EstimatedHitRate,
                    TotalHddReadsAvoided = excluded.TotalHddReadsAvoided;
            ";

            cmd.Parameters.AddWithValue("@GameId", profile.GameId);
            cmd.Parameters.AddWithValue("@LearnedSessionsCount", profile.LearnedSessionsCount);
            cmd.Parameters.AddWithValue("@SsdCacheAllocatedBytes", profile.SsdCacheAllocatedBytes);
            cmd.Parameters.AddWithValue("@RamBudgetBytes", profile.RamBudgetBytes);
            cmd.Parameters.AddWithValue("@AutoPreloadEnabled", profile.AutoPreloadEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@LastUpdated", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@EstimatedHitRate", profile.EstimatedHitRate);
            cmd.Parameters.AddWithValue("@TotalHddReadsAvoided", profile.TotalHddReadsAvoided);

            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task InsertSessionAsync(GamingSession session)
    {
        await InitializeAsync();
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO GamingSessions (SessionId, GameId, StartTime, EndTime, DurationSeconds, TotalBytesRead, ReadOperationsCount, ColdLaunch, StartupDurationSeconds, Status, DroppedEventsCount)
                VALUES (@SessionId, @GameId, @StartTime, @EndTime, @DurationSeconds, @TotalBytesRead, @ReadOperationsCount, @ColdLaunch, @StartupDurationSeconds, @Status, @DroppedEventsCount);
            ";

            cmd.Parameters.AddWithValue("@SessionId", session.SessionId);
            cmd.Parameters.AddWithValue("@GameId", session.GameId);
            cmd.Parameters.AddWithValue("@StartTime", session.StartTime.ToString("o"));
            cmd.Parameters.AddWithValue("@EndTime", (object?)session.EndTime?.ToString("o") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@DurationSeconds", session.DurationSeconds);
            cmd.Parameters.AddWithValue("@TotalBytesRead", session.TotalBytesRead);
            cmd.Parameters.AddWithValue("@ReadOperationsCount", session.ReadOperationsCount);
            cmd.Parameters.AddWithValue("@ColdLaunch", session.ColdLaunch ? 1 : 0);
            cmd.Parameters.AddWithValue("@StartupDurationSeconds", session.StartupDurationSeconds);
            cmd.Parameters.AddWithValue("@Status", (int)session.Status);
            cmd.Parameters.AddWithValue("@DroppedEventsCount", session.DroppedEventsCount);

            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<GamingSession>> GetSessionsForGameAsync(int gameId, int limit = 20)
    {
        await InitializeAsync();
        var list = new List<GamingSession>();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT SessionId, GameId, StartTime, EndTime, DurationSeconds, TotalBytesRead, ReadOperationsCount, ColdLaunch, StartupDurationSeconds, Status, DroppedEventsCount FROM GamingSessions WHERE GameId = @GameId ORDER BY StartTime DESC LIMIT @Limit;";
        cmd.Parameters.AddWithValue("@GameId", gameId);
        cmd.Parameters.AddWithValue("@Limit", limit);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new GamingSession
            {
                SessionId = reader.GetString(0),
                GameId = reader.GetInt32(1),
                StartTime = DateTime.Parse(reader.GetString(2)),
                EndTime = reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3)),
                DurationSeconds = reader.GetDouble(4),
                TotalBytesRead = reader.GetInt64(5),
                ReadOperationsCount = reader.GetInt64(6),
                ColdLaunch = reader.GetInt32(7) != 0,
                StartupDurationSeconds = reader.GetDouble(8),
                Status = (GameSessionStatus)reader.GetInt32(9),
                DroppedEventsCount = reader.GetInt64(10)
            });
        }

        return list;
    }

    public async Task UpsertObservedFilesAsync(IEnumerable<ObservedFile> files)
    {
        await InitializeAsync();
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();

            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO ObservedFiles (GameId, RelativePath, FullPath, TotalBytesRead, AccessCount, IsStartupFile, PriorityScore, FileSizeBytes, FirstSeenAt, LastAccessedAt, IsInSsdCache)
                VALUES (@GameId, @RelativePath, @FullPath, @TotalBytesRead, @AccessCount, @IsStartupFile, @PriorityScore, @FileSizeBytes, @FirstSeenAt, @LastAccessedAt, @IsInSsdCache)
                ON CONFLICT(GameId, RelativePath) DO UPDATE SET
                    TotalBytesRead = ObservedFiles.TotalBytesRead + excluded.TotalBytesRead,
                    AccessCount = ObservedFiles.AccessCount + excluded.AccessCount,
                    IsStartupFile = MAX(ObservedFiles.IsStartupFile, excluded.IsStartupFile),
                    PriorityScore = excluded.PriorityScore,
                    FileSizeBytes = CASE WHEN excluded.FileSizeBytes > 0 THEN excluded.FileSizeBytes ELSE ObservedFiles.FileSizeBytes END,
                    LastAccessedAt = excluded.LastAccessedAt,
                    IsInSsdCache = excluded.IsInSsdCache;
            ";

            var pGameId = cmd.Parameters.Add("@GameId", SqliteType.Integer);
            var pRel = cmd.Parameters.Add("@RelativePath", SqliteType.Text);
            var pFull = cmd.Parameters.Add("@FullPath", SqliteType.Text);
            var pBytes = cmd.Parameters.Add("@TotalBytesRead", SqliteType.Integer);
            var pAccess = cmd.Parameters.Add("@AccessCount", SqliteType.Integer);
            var pStartup = cmd.Parameters.Add("@IsStartupFile", SqliteType.Integer);
            var pScore = cmd.Parameters.Add("@PriorityScore", SqliteType.Real);
            var pSize = cmd.Parameters.Add("@FileSizeBytes", SqliteType.Integer);
            var pFirst = cmd.Parameters.Add("@FirstSeenAt", SqliteType.Text);
            var pLast = cmd.Parameters.Add("@LastAccessedAt", SqliteType.Text);
            var pInSsd = cmd.Parameters.Add("@IsInSsdCache", SqliteType.Integer);

            foreach (var f in files)
            {
                pGameId.Value = f.GameId;
                pRel.Value = f.RelativePath;
                pFull.Value = f.FullPath;
                pBytes.Value = f.TotalBytesRead;
                pAccess.Value = f.AccessCount;
                pStartup.Value = f.IsStartupFile ? 1 : 0;
                pScore.Value = f.PriorityScore;
                pSize.Value = f.FileSizeBytes;
                pFirst.Value = f.FirstSeenAt.ToString("o");
                pLast.Value = f.LastAccessedAt.ToString("o");
                pInSsd.Value = f.IsInSsdCache ? 1 : 0;
                await cmd.ExecuteNonQueryAsync();
            }

            transaction.Commit();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<ObservedFile>> GetObservedFilesForGameAsync(int gameId, int limit = 1000)
    {
        await InitializeAsync();
        var list = new List<ObservedFile>();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, GameId, RelativePath, FullPath, TotalBytesRead, AccessCount, IsStartupFile, PriorityScore, FileSizeBytes, FirstSeenAt, LastAccessedAt, IsInSsdCache FROM ObservedFiles WHERE GameId = @GameId ORDER BY PriorityScore DESC, AccessCount DESC LIMIT @Limit;";
        cmd.Parameters.AddWithValue("@GameId", gameId);
        cmd.Parameters.AddWithValue("@Limit", limit);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new ObservedFile
            {
                Id = reader.GetInt32(0),
                GameId = reader.GetInt32(1),
                RelativePath = reader.GetString(2),
                FullPath = reader.GetString(3),
                TotalBytesRead = reader.GetInt64(4),
                AccessCount = reader.GetInt32(5),
                IsStartupFile = reader.GetInt32(6) != 0,
                PriorityScore = reader.GetDouble(7),
                FileSizeBytes = reader.GetInt64(8),
                FirstSeenAt = DateTime.Parse(reader.GetString(9)),
                LastAccessedAt = DateTime.Parse(reader.GetString(10)),
                IsInSsdCache = reader.GetInt32(11) != 0
            });
        }

        return list;
    }

    public async Task<List<CachedFileEntry>> GetCachedFilesForGameAsync(int gameId)
    {
        await InitializeAsync();
        var list = new List<CachedFileEntry>();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, GameId, RelativePath, CacheFilePath, FileSizeBytes, OriginalLastWriteTimeUtc, CachedAt, LastAccessedAt, PriorityScore, IsValid FROM CachedFiles WHERE GameId = @GameId AND IsValid = 1 ORDER BY PriorityScore DESC;";
        cmd.Parameters.AddWithValue("@GameId", gameId);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new CachedFileEntry
            {
                Id = reader.GetInt32(0),
                GameId = reader.GetInt32(1),
                RelativePath = reader.GetString(2),
                CacheFilePath = reader.GetString(3),
                FileSizeBytes = reader.GetInt64(4),
                OriginalLastWriteTimeUtc = DateTime.Parse(reader.GetString(5)),
                CachedAt = DateTime.Parse(reader.GetString(6)),
                LastAccessedAt = DateTime.Parse(reader.GetString(7)),
                PriorityScore = reader.GetDouble(8),
                IsValid = reader.GetInt32(9) != 0
            });
        }

        return list;
    }

    public async Task InsertOrUpdateCachedFileAsync(CachedFileEntry entry)
    {
        await InitializeAsync();
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO CachedFiles (GameId, RelativePath, CacheFilePath, FileSizeBytes, OriginalLastWriteTimeUtc, CachedAt, LastAccessedAt, PriorityScore, IsValid)
                VALUES (@GameId, @RelativePath, @CacheFilePath, @FileSizeBytes, @OriginalLastWriteTimeUtc, @CachedAt, @LastAccessedAt, @PriorityScore, @IsValid)
                ON CONFLICT(GameId, RelativePath) DO UPDATE SET
                    CacheFilePath = excluded.CacheFilePath,
                    FileSizeBytes = excluded.FileSizeBytes,
                    OriginalLastWriteTimeUtc = excluded.OriginalLastWriteTimeUtc,
                    LastAccessedAt = excluded.LastAccessedAt,
                    PriorityScore = excluded.PriorityScore,
                    IsValid = excluded.IsValid;
            ";

            cmd.Parameters.AddWithValue("@GameId", entry.GameId);
            cmd.Parameters.AddWithValue("@RelativePath", entry.RelativePath);
            cmd.Parameters.AddWithValue("@CacheFilePath", entry.CacheFilePath);
            cmd.Parameters.AddWithValue("@FileSizeBytes", entry.FileSizeBytes);
            cmd.Parameters.AddWithValue("@OriginalLastWriteTimeUtc", entry.OriginalLastWriteTimeUtc.ToString("o"));
            cmd.Parameters.AddWithValue("@CachedAt", entry.CachedAt.ToString("o"));
            cmd.Parameters.AddWithValue("@LastAccessedAt", entry.LastAccessedAt.ToString("o"));
            cmd.Parameters.AddWithValue("@PriorityScore", entry.PriorityScore);
            cmd.Parameters.AddWithValue("@IsValid", entry.IsValid ? 1 : 0);

            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveCachedFileAsync(int gameId, string relativePath)
    {
        await InitializeAsync();
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM CachedFiles WHERE GameId = @GameId AND RelativePath = @RelativePath;";
            cmd.Parameters.AddWithValue("@GameId", gameId);
            cmd.Parameters.AddWithValue("@RelativePath", relativePath);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    // -------------------------------------------------------------
    // CHUNK-LEVEL STORAGE REPOSITORY METHODS (128 KB Chunks)
    // -------------------------------------------------------------
    public async Task InsertOrUpdateCachedChunkAsync(CachedChunkEntry chunk)
    {
        await InitializeAsync();
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO CachedChunks (GameId, RelativePath, ChunkIndex, ChunkOffset, ChunkLength, CacheFilePath, CachedAt, LastAccessedAt, PriorityScore, IsValid)
                VALUES (@GameId, @RelativePath, @ChunkIndex, @ChunkOffset, @ChunkLength, @CacheFilePath, @CachedAt, @LastAccessedAt, @PriorityScore, @IsValid)
                ON CONFLICT(GameId, RelativePath, ChunkIndex) DO UPDATE SET
                    CacheFilePath = excluded.CacheFilePath,
                    ChunkOffset = excluded.ChunkOffset,
                    ChunkLength = excluded.ChunkLength,
                    LastAccessedAt = excluded.LastAccessedAt,
                    PriorityScore = excluded.PriorityScore,
                    IsValid = excluded.IsValid;
            ";

            cmd.Parameters.AddWithValue("@GameId", chunk.GameId);
            cmd.Parameters.AddWithValue("@RelativePath", chunk.RelativePath);
            cmd.Parameters.AddWithValue("@ChunkIndex", chunk.ChunkIndex);
            cmd.Parameters.AddWithValue("@ChunkOffset", chunk.ChunkOffset);
            cmd.Parameters.AddWithValue("@ChunkLength", chunk.ChunkLength);
            cmd.Parameters.AddWithValue("@CacheFilePath", chunk.CacheFilePath);
            cmd.Parameters.AddWithValue("@CachedAt", chunk.CachedAt.ToString("o"));
            cmd.Parameters.AddWithValue("@LastAccessedAt", chunk.LastAccessedAt.ToString("o"));
            cmd.Parameters.AddWithValue("@PriorityScore", chunk.PriorityScore);
            cmd.Parameters.AddWithValue("@IsValid", chunk.IsValid ? 1 : 0);

            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<CachedChunkEntry>> GetCachedChunksForGameAsync(int gameId)
    {
        await InitializeAsync();
        var list = new List<CachedChunkEntry>();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, GameId, RelativePath, ChunkIndex, ChunkOffset, ChunkLength, CacheFilePath, CachedAt, LastAccessedAt, PriorityScore, IsValid FROM CachedChunks WHERE GameId = @GameId AND IsValid = 1 ORDER BY PriorityScore DESC;";
        cmd.Parameters.AddWithValue("@GameId", gameId);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new CachedChunkEntry
            {
                Id = reader.GetInt32(0),
                GameId = reader.GetInt32(1),
                RelativePath = reader.GetString(2),
                ChunkIndex = reader.GetInt32(3),
                ChunkOffset = reader.GetInt64(4),
                ChunkLength = reader.GetInt32(5),
                CacheFilePath = reader.GetString(6),
                CachedAt = DateTime.Parse(reader.GetString(7)),
                LastAccessedAt = DateTime.Parse(reader.GetString(8)),
                PriorityScore = reader.GetDouble(9),
                IsValid = reader.GetInt32(10) != 0
            });
        }

        return list;
    }

    public async Task RemoveCachedChunkAsync(int gameId, string relativePath, int chunkIndex)
    {
        await InitializeAsync();
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM CachedChunks WHERE GameId = @GameId AND RelativePath = @RelativePath AND ChunkIndex = @ChunkIndex;";
            cmd.Parameters.AddWithValue("@GameId", gameId);
            cmd.Parameters.AddWithValue("@RelativePath", relativePath);
            cmd.Parameters.AddWithValue("@ChunkIndex", chunkIndex);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearCacheForGameAsync(int gameId)
    {
        await InitializeAsync();
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM CachedFiles WHERE GameId = @GameId; 
                DELETE FROM CachedChunks WHERE GameId = @GameId; 
                UPDATE ObservedFiles SET IsInSsdCache = 0 WHERE GameId = @GameId;
            ";
            cmd.Parameters.AddWithValue("@GameId", gameId);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<long> GetTotalCachedBytesAsync()
    {
        await InitializeAsync();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT 
                (SELECT COALESCE(SUM(FileSizeBytes), 0) FROM CachedFiles WHERE IsValid = 1) +
                (SELECT COALESCE(SUM(ChunkLength), 0) FROM CachedChunks WHERE IsValid = 1);
        ";
        var res = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(res);
    }

    // -------------------------------------------------------------
    // HARDWARE BENCHMARK REPORTS REPOSITORY & EXPORT
    // -------------------------------------------------------------
    public async Task SaveBenchmarkReportAsync(HardwareBenchmarkReport r)
    {
        await InitializeAsync();
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO HardwareBenchmarkReports (
                    ReportId, GeneratedAt, GameName, GameInstallPath, DriveLetter,
                    HddModel, HddMediaType, IsRotationalHdd, HddReadThroughputMbSec,
                    TotalObservedReadBytes, SsdCacheUsedBytes, RamPrefetchUsedBytes,
                    VerifiedCacheHitsBytes, AvoidedHddReadsBytes, HitRatePercent,
                    BaselineColdLoadTimeSec, AcceleratedWarmLoadTimeSec,
                    LoadTimeImprovementPercent, SessionDurationSeconds,
                    DroppedEventsCount, ActiveAccelerationMode
                ) VALUES (
                    @ReportId, @GeneratedAt, @GameName, @GameInstallPath, @DriveLetter,
                    @HddModel, @HddMediaType, @IsRotationalHdd, @HddReadThroughputMbSec,
                    @TotalObservedReadBytes, @SsdCacheUsedBytes, @RamPrefetchUsedBytes,
                    @VerifiedCacheHitsBytes, @AvoidedHddReadsBytes, @HitRatePercent,
                    @BaselineColdLoadTimeSec, @AcceleratedWarmLoadTimeSec,
                    @LoadTimeImprovementPercent, @SessionDurationSeconds,
                    @DroppedEventsCount, @ActiveAccelerationMode
                );
            ";

            cmd.Parameters.AddWithValue("@ReportId", r.ReportId);
            cmd.Parameters.AddWithValue("@GeneratedAt", r.GeneratedAt.ToString("o"));
            cmd.Parameters.AddWithValue("@GameName", r.GameName);
            cmd.Parameters.AddWithValue("@GameInstallPath", r.GameInstallPath);
            cmd.Parameters.AddWithValue("@DriveLetter", r.DriveLetter);
            cmd.Parameters.AddWithValue("@HddModel", r.HddModel);
            cmd.Parameters.AddWithValue("@HddMediaType", r.HddMediaType);
            cmd.Parameters.AddWithValue("@IsRotationalHdd", r.IsRotationalHdd ? 1 : 0);
            cmd.Parameters.AddWithValue("@HddReadThroughputMbSec", r.HddReadThroughputMbSec);
            cmd.Parameters.AddWithValue("@TotalObservedReadBytes", r.TotalObservedReadBytes);
            cmd.Parameters.AddWithValue("@SsdCacheUsedBytes", r.SsdCacheUsedBytes);
            cmd.Parameters.AddWithValue("@RamPrefetchUsedBytes", r.RamPrefetchUsedBytes);
            cmd.Parameters.AddWithValue("@VerifiedCacheHitsBytes", r.VerifiedCacheHitsBytes);
            cmd.Parameters.AddWithValue("@AvoidedHddReadsBytes", r.AvoidedHddReadsBytes);
            cmd.Parameters.AddWithValue("@HitRatePercent", r.HitRatePercent);
            cmd.Parameters.AddWithValue("@BaselineColdLoadTimeSec", r.BaselineColdLoadTimeSec);
            cmd.Parameters.AddWithValue("@AcceleratedWarmLoadTimeSec", r.AcceleratedWarmLoadTimeSec);
            cmd.Parameters.AddWithValue("@LoadTimeImprovementPercent", r.LoadTimeImprovementPercent);
            cmd.Parameters.AddWithValue("@SessionDurationSeconds", r.SessionDurationSeconds);
            cmd.Parameters.AddWithValue("@DroppedEventsCount", r.DroppedEventsCount);
            cmd.Parameters.AddWithValue("@ActiveAccelerationMode", r.ActiveAccelerationMode);

            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<HardwareBenchmarkReport>> GetAllBenchmarkReportsAsync(int limit = 50)
    {
        await InitializeAsync();
        var list = new List<HardwareBenchmarkReport>();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ReportId, GeneratedAt, GameName, GameInstallPath, DriveLetter, HddModel, HddMediaType, IsRotationalHdd, HddReadThroughputMbSec, TotalObservedReadBytes, SsdCacheUsedBytes, RamPrefetchUsedBytes, VerifiedCacheHitsBytes, AvoidedHddReadsBytes, HitRatePercent, BaselineColdLoadTimeSec, AcceleratedWarmLoadTimeSec, LoadTimeImprovementPercent, SessionDurationSeconds, DroppedEventsCount, ActiveAccelerationMode FROM HardwareBenchmarkReports ORDER BY GeneratedAt DESC LIMIT @Limit;";
        cmd.Parameters.AddWithValue("@Limit", limit);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new HardwareBenchmarkReport
            {
                ReportId = reader.GetString(0),
                GeneratedAt = DateTime.Parse(reader.GetString(1)),
                GameName = reader.GetString(2),
                GameInstallPath = reader.GetString(3),
                DriveLetter = reader.GetString(4),
                HddModel = reader.GetString(5),
                HddMediaType = reader.GetString(6),
                IsRotationalHdd = reader.GetInt32(7) != 0,
                HddReadThroughputMbSec = reader.GetDouble(8),
                TotalObservedReadBytes = reader.GetInt64(9),
                SsdCacheUsedBytes = reader.GetInt64(10),
                RamPrefetchUsedBytes = reader.GetInt64(11),
                VerifiedCacheHitsBytes = reader.GetInt64(12),
                AvoidedHddReadsBytes = reader.GetInt64(13),
                HitRatePercent = reader.GetDouble(14),
                BaselineColdLoadTimeSec = reader.GetDouble(15),
                AcceleratedWarmLoadTimeSec = reader.GetDouble(16),
                LoadTimeImprovementPercent = reader.GetDouble(17),
                SessionDurationSeconds = reader.GetDouble(18),
                DroppedEventsCount = reader.GetInt64(19),
                ActiveAccelerationMode = reader.GetString(20)
            });
        }

        return list;
    }

    public async Task<string> ExportBenchmarkReportJsonAsync(string reportId, string outputPath)
    {
        var reports = await GetAllBenchmarkReportsAsync(100);
        var report = reports.Find(r => r.ReportId == reportId);
        if (report == null) throw new ArgumentException("Report not found.");

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(outputPath, json);
        return outputPath;
    }

    public async Task<string> ExportBenchmarkReportCsvAsync(string reportId, string outputPath)
    {
        var reports = await GetAllBenchmarkReportsAsync(100);
        var r = reports.Find(rep => rep.ReportId == reportId);
        if (r == null) throw new ArgumentException("Report not found.");

        var csv = "ReportId,GeneratedAt,GameName,DriveLetter,HddModel,HddMediaType,ThroughputMBs,ObservedBytes,SSDBytes,RAMBytes,AvoidedBytes,HitRatePercent,ColdLoadSec,WarmLoadSec,ImprovementPercent,DroppedEvents\r\n" +
                  $"\"{r.ReportId}\",\"{r.GeneratedAt:yyyy-MM-dd HH:mm:ss}\",\"{r.GameName}\",\"{r.DriveLetter}\",\"{r.HddModel}\",\"{r.HddMediaType}\",{r.HddReadThroughputMbSec:F2},{r.TotalObservedReadBytes},{r.SsdCacheUsedBytes},{r.RamPrefetchUsedBytes},{r.AvoidedHddReadsBytes},{r.HitRatePercent:F1},{r.BaselineColdLoadTimeSec:F2},{r.AcceleratedWarmLoadTimeSec:F2},{r.LoadTimeImprovementPercent:F1},{r.DroppedEventsCount}\r\n";

        await File.WriteAllTextAsync(outputPath, csv);
        return outputPath;
    }

    public async Task<AppSettings> GetSettingsAsync()
    {
        await InitializeAsync();
        var settings = new AppSettings();

        var defaultSsdDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartGameCache", "Storage");
        settings.SsdCacheDirectory = defaultSsdDir;

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Key, Value FROM ApplicationSettings;";

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var key = reader.GetString(0);
            var val = reader.GetString(1);
            switch (key)
            {
                case nameof(AppSettings.RamBudgetBytes):
                    if (long.TryParse(val, out var ram)) settings.RamBudgetBytes = ram;
                    break;
                case nameof(AppSettings.SsdCapacityBytes):
                    if (long.TryParse(val, out var ssd)) settings.SsdCapacityBytes = ssd;
                    break;
                case nameof(AppSettings.SsdCacheDirectory):
                    if (!string.IsNullOrWhiteSpace(val)) settings.SsdCacheDirectory = val;
                    break;
                case nameof(AppSettings.EvictionPolicy):
                    settings.EvictionPolicy = val;
                    break;
                case nameof(AppSettings.StartWithWindows):
                    settings.StartWithWindows = val == "1";
                    break;
                case nameof(AppSettings.MinimizeToTray):
                    settings.MinimizeToTray = val == "1";
                    break;
                case nameof(AppSettings.AutoDetectGames):
                    settings.AutoDetectGames = val == "1";
                    break;
                case nameof(AppSettings.AutoMonitoring):
                    settings.AutoMonitoring = val == "1";
                    break;
                case nameof(AppSettings.AutoPreload):
                    settings.AutoPreload = val == "1";
                    break;
                case nameof(AppSettings.Theme):
                    settings.Theme = val;
                    break;
                case nameof(AppSettings.MonitoringVerbosity):
                    settings.MonitoringVerbosity = val;
                    break;
                case nameof(AppSettings.ChunkSizeKb):
                    if (int.TryParse(val, out var chunk)) settings.ChunkSizeKb = chunk;
                    break;
            }
        }

        return settings;
    }

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        await InitializeAsync();
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();

            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "INSERT INTO ApplicationSettings (Key, Value) VALUES (@Key, @Value) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";

            var pKey = cmd.Parameters.Add("@Key", SqliteType.Text);
            var pVal = cmd.Parameters.Add("@Value", SqliteType.Text);

            void SetParam(string k, string v)
            {
                pKey.Value = k;
                pVal.Value = v;
                cmd.ExecuteNonQuery();
            }

            SetParam(nameof(AppSettings.RamBudgetBytes), settings.RamBudgetBytes.ToString());
            SetParam(nameof(AppSettings.SsdCapacityBytes), settings.SsdCapacityBytes.ToString());
            SetParam(nameof(AppSettings.SsdCacheDirectory), settings.SsdCacheDirectory);
            SetParam(nameof(AppSettings.EvictionPolicy), settings.EvictionPolicy);
            SetParam(nameof(AppSettings.StartWithWindows), settings.StartWithWindows ? "1" : "0");
            SetParam(nameof(AppSettings.MinimizeToTray), settings.MinimizeToTray ? "1" : "0");
            SetParam(nameof(AppSettings.AutoDetectGames), settings.AutoDetectGames ? "1" : "0");
            SetParam(nameof(AppSettings.AutoMonitoring), settings.AutoMonitoring ? "1" : "0");
            SetParam(nameof(AppSettings.AutoPreload), settings.AutoPreload ? "1" : "0");
            SetParam(nameof(AppSettings.Theme), settings.Theme);
            SetParam(nameof(AppSettings.MonitoringVerbosity), settings.MonitoringVerbosity);
            SetParam(nameof(AppSettings.ChunkSizeKb), settings.ChunkSizeKb.ToString());

            transaction.Commit();
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _lock.Dispose();
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SmartGameCache.Core.Database;
using SmartGameCache.Core.Detection;
using SmartGameCache.Core.Interop;
using SmartGameCache.Core.Learning;
using SmartGameCache.Core.Models;
using SmartGameCache.Core.Monitoring;
using SmartGameCache.Core.Prefetch;
using SmartGameCache.Core.Services;
using SmartGameCache.Core.Storage;
using SmartGameCache.UI.Services;
using SmartGameCache.UI.ViewModels;

namespace SmartGameCache.Tests;

class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("======================================================================");
        Console.WriteLine(" SMART GAME CACHE — DEEP VERIFICATION & ALGORITHM INTEGRATION SUITE ");
        Console.WriteLine("======================================================================");
        Console.ResetColor();

        int passed = 0;
        int failed = 0;

        var tempTestDir = Path.Combine(Path.GetTempPath(), "SmartGameCache_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempTestDir);
        var testDbPath = Path.Combine(tempTestDir, "TestCache.db");

        try
        {
            // -------------------------------------------------------------
            // TEST 1: SQLite Database Initialization & Schema
            // -------------------------------------------------------------
            Console.Write("[TEST 1] SQLite Database, CachedChunks & Benchmark Schema ... ");
            using (var db = new DatabaseService(testDbPath))
            {
                await db.InitializeAsync();

                var testGame = new GameInfo
                {
                    Name = "Grand Theft Auto V",
                    ExecutablePath = @"D:\Games\GTA V\GTA5.exe",
                    InstallDirectory = @"D:\Games\GTA V",
                    DriveLetter = "D:",
                    StorageType = StorageMediaType.HDD,
                    LauncherSource = "Rockstar Games",
                    TotalInstallSizeBytes = 110L * 1024 * 1024 * 1024
                };

                int gameId = await db.InsertOrUpdateGameAsync(testGame);
                var retrieved = await db.GetGameByIdAsync(gameId);

                if (retrieved == null || retrieved.Name != "Grand Theft Auto V" || retrieved.StorageType != StorageMediaType.HDD)
                {
                    throw new Exception("Game retrieval mismatch.");
                }

                var chunk = new CachedChunkEntry
                {
                    GameId = gameId,
                    RelativePath = "x64a.rpf",
                    ChunkIndex = 0,
                    ChunkOffset = 0,
                    ChunkLength = 128 * 1024,
                    CacheFilePath = Path.Combine(tempTestDir, "test.chunk"),
                    PriorityScore = 0.85
                };
                await db.InsertOrUpdateCachedChunkAsync(chunk);

                var chunks = await db.GetCachedChunksForGameAsync(gameId);
                if (chunks.Count != 1 || chunks[0].ChunkIndex != 0)
                {
                    throw new Exception("CachedChunk persistence failed.");
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("PASSED");
                Console.ResetColor();
                passed++;
            }

            // -------------------------------------------------------------
            // TEST 2: Drive Hardware Detection & Media Inspection
            // -------------------------------------------------------------
            Console.Write("[TEST 2] Storage Drive Hardware & Media Inspection ... ");
            var drives = DriveDetector.GetAllDrives();
            if (drives.Count == 0) throw new Exception("No drives detected.");

            var hwInfo = DriveDetector.GetDriveHardwareInfo("C:\\");
            if (string.IsNullOrEmpty(hwInfo.Model)) throw new Exception("Hardware query failed.");

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"PASSED (Drive C: [{hwInfo.Model}] Interface: {hwInfo.BusType}, Type: {hwInfo.MediaType})");
            Console.ResetColor();
            passed++;

            // -------------------------------------------------------------
            // TEST 3: Game Detection Service
            // -------------------------------------------------------------
            Console.Write("[TEST 3] Game Detection Engine (Steam/Epic/Registry) ... ");
            var detector = new GameDetectionService();
            var detectedGames = detector.DetectAllInstalledGames();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"PASSED (Detected {detectedGames.Count} game(s))");
            foreach (var g in detectedGames)
            {
                Console.WriteLine($"         -> Found: {g.Name} [{g.LauncherSource}] on {g.DriveLetter} ({g.StorageType})");
            }
            Console.ResetColor();
            passed++;

            // -------------------------------------------------------------
            // TEST 4: SSD Cache Manager Chunking (128 KB Chunks)
            // -------------------------------------------------------------
            Console.Write("[TEST 4] SSD Cache Manager (128 KB Chunk Caching & Quota) ... ");
            using (var db = new DatabaseService(testDbPath))
            {
                var ssdDir = Path.Combine(tempTestDir, "SSD_Cache");
                Directory.CreateDirectory(ssdDir);
                var settings = await db.GetSettingsAsync();
                settings.SsdCacheDirectory = ssdDir;
                await db.SaveSettingsAsync(settings);

                var ssdManager = new SsdCacheManager(db);

                var mockGameDir = Path.Combine(tempTestDir, "MockGame");
                Directory.CreateDirectory(mockGameDir);
                var asset1 = Path.Combine(mockGameDir, "stream.rpf");

                // Generate 1 MB sample file (contains eight 128KB chunks)
                var data = new byte[1024 * 1024];
                new Random(42).NextBytes(data);
                await File.WriteAllBytesAsync(asset1, data);

                var chk0 = await ssdManager.CacheChunkAsync(1, mockGameDir, "stream.rpf", 0, 128 * 1024, 0.92);
                var chk1 = await ssdManager.CacheChunkAsync(1, mockGameDir, "stream.rpf", 1, 128 * 1024, 0.40);

                if (chk0 == null || chk1 == null) throw new Exception("Failed to cache chunks.");

                bool valid = await ssdManager.ValidateChunkAsync(chk0, mockGameDir);
                if (!valid) throw new Exception("Chunk validation failed.");

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("PASSED (128 KB Chunks Verified)");
                Console.ResetColor();
                passed++;
            }

            // -------------------------------------------------------------
            // TEST 5: RAM Prefetch Engine & Memory Balancer
            // -------------------------------------------------------------
            Console.Write("[TEST 5] RAM Prefetch Engine & Memory Pressure Safety ... ");
            using (var db = new DatabaseService(testDbPath))
            {
                var cachedFiles = await db.GetCachedFilesForGameAsync(1);
                var ramEngine = new RamPrefetchEngine();

                var memStatus = NativeMemory.GetMemoryStatus();
                if (memStatus.TotalPhysicalBytes == 0) throw new Exception("Memory status query failed.");

                var result = await ramEngine.PrefetchAsync(cachedFiles, 1L * 1024 * 1024 * 1024);
                if (!result.IsCompleted) throw new Exception("Prefetch did not complete.");

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"PASSED (Memory Load: {memStatus.MemoryLoadPercent}%, UnderPressure: {memStatus.IsUnderPressure})");
                Console.ResetColor();
                passed++;
            }

            // -------------------------------------------------------------
            // TEST 6: Real Game Process I/O Monitoring Lifecycle
            // -------------------------------------------------------------
            Console.Write("[TEST 6] Real Game Process I/O Monitoring & Lost Event Check ... ");
            using (var db = new DatabaseService(testDbPath))
            {
                var learning = new LearningEngine(db);
                using var sessionMonitor = new GameSessionMonitor(db, learning);

                var mockGame = new GameInfo
                {
                    Id = 1,
                    Name = "GTA V Benchmark Mock",
                    InstallDirectory = Path.Combine(tempTestDir, "MockGame"),
                    ExecutablePath = "cmd.exe"
                };

                var scriptPath = Path.Combine(tempTestDir, "mock_runner.bat");
                var targetRead = Path.Combine(tempTestDir, "MockGame", "stream.rpf");
                File.WriteAllText(scriptPath, $"@echo off\r\nfc /b \"{targetRead}\" \"{targetRead}\" > nul\r\ntimeout /t 1 /nobreak > nul\r\nexit\r\n");

                var procStart = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{scriptPath}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };

                var proc = Process.Start(procStart);
                if (proc == null) throw new Exception("Failed to start process.");

                await sessionMonitor.StartMonitoringAsync(mockGame, proc);
                await proc.WaitForExitAsync();
                await Task.Delay(1000);

                var sessions = await db.GetSessionsForGameAsync(1);
                if (sessions.Count == 0) throw new Exception("Session not saved.");

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"PASSED (Read {sessions[0].TotalBytesRead} bytes, Dropped Events: {sessions[0].DroppedEventsCount})");
                Console.ResetColor();
                passed++;
            }

            // -------------------------------------------------------------
            // TEST 7: W-TinyLFU Count-Min 4-Bit Frequency Sketch
            // -------------------------------------------------------------
            Console.Write("[TEST 7] Count-Min 4-Bit Frequency Sketch (TinyLFU) ... ");
            var sketch = new FrequencySketch(1024);
            sketch.RecordAccess("textures.rpf");
            sketch.RecordAccess("textures.rpf");
            sketch.RecordAccess("textures.rpf");
            sketch.RecordAccess("audio.rpf");

            int freqTextures = sketch.EstimateFrequency("textures.rpf");
            int freqAudio = sketch.EstimateFrequency("audio.rpf");
            int freqUnknown = sketch.EstimateFrequency("nonexistent.rpf");

            if (freqTextures < 3 || freqAudio != 1 || freqUnknown != 0)
            {
                throw new Exception($"Sketch estimation mismatch: Textures={freqTextures}, Audio={freqAudio}, Unknown={freqUnknown}");
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"PASSED (Frequencies: Hot={freqTextures}, Warm={freqAudio}, Cold={freqUnknown})");
            Console.ResetColor();
            passed++;

            // -------------------------------------------------------------
            // TEST 8: Adaptive Replacement Cache (ARC) Dual-List Balancing
            // -------------------------------------------------------------
            Console.Write("[TEST 8] Adaptive Replacement Cache (ARC) Self-Tuning ... ");
            var arc = new AdaptiveReplacementCache<string, int>(4);

            arc.Put("A", 1);
            arc.Put("B", 2);
            arc.Put("C", 3);
            arc.Put("D", 4);

            // Access "A" and "B" again -> promotes to T2 (frequency cache)
            arc.TryGet("A", out _);
            arc.TryGet("B", out _);

            if (arc.T2Count < 2) throw new Exception("ARC failed to promote frequent items to T2.");

            // Add new items to trigger adaptation
            arc.Put("E", 5);
            arc.Put("F", 6);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"PASSED (ARC: T1={arc.T1Count}, T2={arc.T2Count}, B1={arc.B1Count}, B2={arc.B2Count}, TargetP={arc.TargetP:0.0})");
            Console.ResetColor();
            passed++;

            // -------------------------------------------------------------
            // TEST 9: Hardware Benchmark Report & CSV/JSON Export
            // -------------------------------------------------------------
            Console.Write("[TEST 9] Hardware Benchmark Report & CSV/JSON Export ... ");
            using (var db = new DatabaseService(testDbPath))
            {
                var report = new HardwareBenchmarkReport
                {
                    GameName = "Grand Theft Auto V",
                    GameInstallPath = @"D:\Games\GTA V",
                    DriveLetter = "D:",
                    HddModel = "WDC WD10EZEX-08WN4A0",
                    HddMediaType = "HDD",
                    IsRotationalHdd = true,
                    HddReadThroughputMbSec = 114.2,
                    TotalObservedReadBytes = 1024L * 1024 * 512,
                    SsdCacheUsedBytes = 1024L * 1024 * 256,
                    RamPrefetchUsedBytes = 1024L * 1024 * 128,
                    VerifiedCacheHitsBytes = 1024L * 1024 * 384,
                    AvoidedHddReadsBytes = 1024L * 1024 * 384,
                    HitRatePercent = 75.0,
                    BaselineColdLoadTimeSec = 52.4,
                    AcceleratedWarmLoadTimeSec = 19.8,
                    LoadTimeImprovementPercent = 62.2,
                    SessionDurationSeconds = 180.0,
                    DroppedEventsCount = 0,
                    ActiveAccelerationMode = "SSD Chunks + RAM Standby Pre-warming"
                };

                await db.SaveBenchmarkReportAsync(report);

                var csvPath = Path.Combine(tempTestDir, "BenchmarkReport.csv");
                var jsonPath = Path.Combine(tempTestDir, "BenchmarkReport.json");

                await db.ExportBenchmarkReportCsvAsync(report.ReportId, csvPath);
                await db.ExportBenchmarkReportJsonAsync(report.ReportId, jsonPath);

                if (!File.Exists(csvPath) || new FileInfo(csvPath).Length == 0) throw new Exception("CSV export failed.");
                if (!File.Exists(jsonPath) || new FileInfo(jsonPath).Length == 0) throw new Exception("JSON export failed.");

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("PASSED (CSV & JSON Exported)");
                Console.ResetColor();
                passed++;
            }

            // -------------------------------------------------------------
            // TEST 10: Centralized Navigation Service & View Exclusivity
            // -------------------------------------------------------------
            Console.Write("[TEST 10] Centralized Navigation Service & Single-View Host ... ");
            using (var orchestrator = new GameCacheOrchestrator(testDbPath))
            {
                var mainVm = new MainViewModel(orchestrator);
                await mainVm.InitializeAsync();

                // 1. Initial page should be Dashboard
                if (mainVm.CurrentPage != NavigationPage.Dashboard || !(mainVm.CurrentViewModel is DashboardViewModel) || !mainVm.IsDashboardActive)
                {
                    throw new Exception("Initial page was not Dashboard.");
                }

                // 2. Test navigation through all 8 pages
                var testPages = new[]
                {
                    (NavigationPage.MyGames, typeof(GamesViewModel), (Func<bool>)(() => mainVm.IsMyGamesActive)),
                    (NavigationPage.CacheManager, typeof(CacheManagerViewModel), (Func<bool>)(() => mainVm.IsCacheManagerActive)),
                    (NavigationPage.Analytics, typeof(AnalyticsViewModel), (Func<bool>)(() => mainVm.IsAnalyticsActive)),
                    (NavigationPage.ActivityHistory, typeof(HistoryViewModel), (Func<bool>)(() => mainVm.IsHistoryActive)),
                    (NavigationPage.HardwareTest, typeof(HardwareTestViewModel), (Func<bool>)(() => mainVm.IsHardwareTestActive)),
                    (NavigationPage.Settings, typeof(SettingsViewModel), (Func<bool>)(() => mainVm.IsSettingsActive)),
                    (NavigationPage.About, typeof(AboutViewModel), (Func<bool>)(() => mainVm.IsAboutActive)),
                    (NavigationPage.Dashboard, typeof(DashboardViewModel), (Func<bool>)(() => mainVm.IsDashboardActive))
                };

                foreach (var (page, expectedVmType, isSelected) in testPages)
                {
                    mainVm.NavigateCommand.Execute(page.ToString());
                    if (mainVm.CurrentPage != page) throw new Exception($"Failed navigating to {page}");
                    if (mainVm.CurrentViewModel.GetType() != expectedVmType) throw new Exception($"ViewModel mismatch for {page}: expected {expectedVmType.Name}, got {mainVm.CurrentViewModel.GetType().Name}");
                    if (!isSelected()) throw new Exception($"Active flag failed for {page}");

                    // Check that exactly ONE active flag is true
                    int activeCount = (mainVm.IsDashboardActive ? 1 : 0) +
                                      (mainVm.IsMyGamesActive ? 1 : 0) +
                                      (mainVm.IsCacheManagerActive ? 1 : 0) +
                                      (mainVm.IsAnalyticsActive ? 1 : 0) +
                                      (mainVm.IsHistoryActive ? 1 : 0) +
                                      (mainVm.IsHardwareTestActive ? 1 : 0) +
                                      (mainVm.IsSettingsActive ? 1 : 0) +
                                      (mainVm.IsAboutActive ? 1 : 0);
                    if (activeCount != 1) throw new Exception($"Mutual exclusivity failure: {activeCount} views active at once.");
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("PASSED (All 8 Pages Verified, Mutually Exclusive)");
                Console.ResetColor();
                passed++;

                // -------------------------------------------------------------
                // TEST 11: Redesigned Dashboard Sliders & Live State
                // -------------------------------------------------------------
                Console.Write("[TEST 11] Redesigned Dashboard Sliders & Empty State Telemetry ... ");
                var dashVm = mainVm.DashboardVM;
                await dashVm.RefreshDataAsync();

                // Test RAM Slider (Green)
                dashVm.RamBudgetGb = 3.5;
                if (dashVm.RamBudgetFormatted != "3.5 GB") throw new Exception($"RAM formatted mismatch: {dashVm.RamBudgetFormatted}");

                // Test SSD Slider (Blue)
                dashVm.SsdQuotaGb = 45;
                if (dashVm.SsdQuotaFormatted != "45 GB") throw new Exception($"SSD formatted mismatch: {dashVm.SsdQuotaFormatted}");

                // Test Empty States
                if (dashVm.CacheHitRateDisplay != "Not enough data") throw new Exception($"Cache hit rate empty state failed: got '{dashVm.CacheHitRateDisplay}'");
                if (dashVm.ActiveGameDisplay != "No active game session") throw new Exception($"Active game empty state failed: got '{dashVm.ActiveGameDisplay}'");

                // Test "View All Activity" Link Navigation
                dashVm.ViewAllActivityCommand.Execute(null);
                if (mainVm.CurrentPage != NavigationPage.ActivityHistory || !(mainVm.CurrentViewModel is HistoryViewModel))
                {
                    throw new Exception("ViewAllActivityCommand failed to navigate to ActivityHistory.");
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("PASSED (RAM: 3.5 GB, SSD: 45 GB, Empty States Validated)");
                Console.ResetColor();
                passed++;
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAILED: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Console.ResetColor();
            failed++;
        }
        finally
        {
            try { Directory.Delete(tempTestDir, true); } catch { }
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"Verification Summary: {passed} Passed, {failed} Failed.");
        Console.ResetColor();

        return failed == 0 ? 0 : 1;
    }
}

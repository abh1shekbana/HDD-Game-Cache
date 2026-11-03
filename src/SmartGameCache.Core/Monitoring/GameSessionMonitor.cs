using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SmartGameCache.Core.Database;
using SmartGameCache.Core.Learning;
using SmartGameCache.Core.Models;

namespace SmartGameCache.Core.Monitoring;

public class GameSessionMonitor : IDisposable
{
    private readonly DatabaseService _db;
    private readonly LearningEngine _learningEngine;

    private EtwFileIoMonitor? _etwMonitor;
    private ProcessFileIoMonitor? _processMonitor;
    private readonly ConcurrentBag<FileAccessEvent> _sessionEvents = new();
    private readonly ConcurrentQueue<FileAccessEvent> _recentEvents = new();
    private CancellationTokenSource? _sessionCts;

    public GameInfo? ActiveGame { get; private set; }
    public GamingSession? CurrentSession { get; private set; }
    public bool IsActive => CurrentSession != null && CurrentSession.Status == GameSessionStatus.Running;
    public string ActiveMonitoringMode { get; private set; } = "Idle";

    public long TotalBytesReadInSession { get; private set; }
    public long TotalReadOpsInSession { get; private set; }
    public double CurrentReadSpeedBytesPerSec { get; private set; }
    public long DroppedEventsCount => _etwMonitor?.EventsLost ?? 0;

    public event Action<FileAccessEvent>? OnFileAccessObserved;
    public event Action<GamingSession>? OnSessionFinished;

    public GameSessionMonitor(DatabaseService db, LearningEngine learningEngine)
    {
        _db = db;
        _learningEngine = learningEngine;
    }

    public async Task<bool> StartMonitoringAsync(GameInfo game, Process process, bool isColdLaunch = false)
    {
        if (IsActive)
        {
            await StopMonitoringAsync();
        }

        ActiveGame = game;
        _sessionEvents.Clear();
        while (_recentEvents.TryDequeue(out _)) { }

        TotalBytesReadInSession = 0;
        TotalReadOpsInSession = 0;
        CurrentReadSpeedBytesPerSec = 0;

        CurrentSession = new GamingSession
        {
            GameId = game.Id,
            StartTime = DateTime.UtcNow,
            ColdLaunch = isColdLaunch,
            Status = GameSessionStatus.Running
        };

        _sessionCts = new CancellationTokenSource();

        // Check if ETW is available (administrator)
        bool etwStarted = false;
        if (ElevationHelper.IsAdministrator())
        {
            _etwMonitor = new EtwFileIoMonitor(process.Id, game.InstallDirectory, HandleFileAccess);
            etwStarted = _etwMonitor.Start();
        }

        if (etwStarted)
        {
            ActiveMonitoringMode = "Kernel ETW (Elevated)";
        }
        else
        {
            // Graceful fallback: Process I/O monitoring
            _processMonitor = new ProcessFileIoMonitor(
                process.Id, 
                game.InstallDirectory, 
                HandleFileAccess,
                (bytes, ops) =>
                {
                    TotalBytesReadInSession += bytes;
                    TotalReadOpsInSession += ops;
                });
            _processMonitor.Start();
            ActiveMonitoringMode = "Process I/O Monitor (Standard)";
        }

        // Background task to watch for process exit
        _ = Task.Run(async () =>
        {
            try
            {
                process.EnableRaisingEvents = true;
                await process.WaitForExitAsync(_sessionCts.Token);
            }
            catch { }
            finally
            {
                await StopMonitoringAsync();
            }
        });

        return true;
    }

    private void HandleFileAccess(FileAccessEvent ev)
    {
        TotalBytesReadInSession += ev.ReadSize;
        TotalReadOpsInSession++;

        _sessionEvents.Add(ev);

        _recentEvents.Enqueue(ev);
        while (_recentEvents.Count > 50)
        {
            _recentEvents.TryDequeue(out _);
        }

        OnFileAccessObserved?.Invoke(ev);
    }

    public IReadOnlyList<FileAccessEvent> GetRecentEvents()
    {
        return _recentEvents.ToArray();
    }

    public async Task StopMonitoringAsync()
    {
        if (CurrentSession == null || CurrentSession.Status != GameSessionStatus.Running)
        {
            return;
        }

        CurrentSession.EndTime = DateTime.UtcNow;
        CurrentSession.DurationSeconds = (CurrentSession.EndTime.Value - CurrentSession.StartTime).TotalSeconds;
        CurrentSession.TotalBytesRead = TotalBytesReadInSession;
        CurrentSession.ReadOperationsCount = TotalReadOpsInSession;
        CurrentSession.DroppedEventsCount = _etwMonitor?.EventsLost ?? 0;
        CurrentSession.Status = GameSessionStatus.Completed;

        _etwMonitor?.Stop();
        _etwMonitor?.Dispose();
        _etwMonitor = null;

        _processMonitor?.Stop();
        _processMonitor?.Dispose();
        _processMonitor = null;

        ActiveMonitoringMode = "Idle";

        // Save session
        await _db.InsertSessionAsync(CurrentSession);

        // Process learning
        if (ActiveGame != null)
        {
            var observedList = _sessionEvents.Select(e => new ObservedFile
            {
                GameId = ActiveGame.Id,
                RelativePath = e.RelativePath,
                FullPath = e.FilePath,
                AccessCount = 1,
                TotalBytesRead = e.ReadSize,
                IsStartupFile = e.IsStartup,
                FirstSeenAt = e.Timestamp,
                LastAccessedAt = e.Timestamp,
                FileSizeBytes = File.Exists(e.FilePath) ? new FileInfo(e.FilePath).Length : 0
            });

            await _learningEngine.ProcessSessionLearningAsync(ActiveGame.Id, CurrentSession, observedList);
        }

        var completed = CurrentSession;
        CurrentSession = null;
        ActiveGame = null;

        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = null;

        OnSessionFinished?.Invoke(completed);
    }

    public void Dispose()
    {
        StopMonitoringAsync().GetAwaiter().GetResult();
    }
}

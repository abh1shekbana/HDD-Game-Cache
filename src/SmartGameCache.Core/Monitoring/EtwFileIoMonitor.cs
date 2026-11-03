using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using SmartGameCache.Core.Models;

namespace SmartGameCache.Core.Monitoring;

public class FileAccessEvent
{
    public int ProcessId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public long Offset { get; set; }
    public int ReadSize { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public bool IsStartup { get; set; }
}

public class EtwFileIoMonitor : IDisposable
{
    private TraceEventSession? _session;
    private Thread? _processingThread;
    private readonly ConcurrentDictionary<ulong, string> _fileKeyMap = new();
    private readonly string _gameInstallDir;
    private readonly int _targetPid;
    private readonly DateTime _sessionStartUtc;
    private readonly Action<FileAccessEvent> _onFileAccess;
    private bool _isDisposed;

    public bool IsRunning { get; private set; }
    public int EventsLost => _session?.EventsLost ?? 0;

    public EtwFileIoMonitor(int targetPid, string gameInstallDir, Action<FileAccessEvent> onFileAccess)
    {
        _targetPid = targetPid;
        _gameInstallDir = gameInstallDir.TrimEnd('\\') + "\\";
        _onFileAccess = onFileAccess;
        _sessionStartUtc = DateTime.UtcNow;
    }

    public bool Start()
    {
        if (!ElevationHelper.IsAdministrator())
        {
            return false;
        }

        try
        {
            var existing = TraceEventSession.GetActiveSession(KernelTraceEventParser.KernelSessionName);
            existing?.Stop();

            _session = new TraceEventSession(KernelTraceEventParser.KernelSessionName)
            {
                StopOnDispose = true,
                BufferSizeMB = 64 // 64 MB buffer to prevent event drops during heavy asset bursts
            };

            // Enable DiskFileIO, FileIOInit, and Process keywords
            _session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.DiskFileIO |
                KernelTraceEventParser.Keywords.FileIOInit |
                KernelTraceEventParser.Keywords.FileIO |
                KernelTraceEventParser.Keywords.Process
            );

            // Track file names from FileIOCreate
            _session.Source.Kernel.FileIOCreate += data =>
            {
                if (data.ProcessID != _targetPid) return;
                if (!string.IsNullOrEmpty(data.FileName))
                {
                    _fileKeyMap[(ulong)data.FileObject] = data.FileName;
                }
            };

            _session.Source.Kernel.FileIOName += data =>
            {
                if (data.ProcessID != _targetPid) return;
                if (!string.IsNullOrEmpty(data.FileName))
                {
                    _fileKeyMap[(ulong)data.FileKey] = data.FileName;
                }
            };

            // Track file reads with exact offsets
            _session.Source.Kernel.FileIORead += data =>
            {
                if (data.ProcessID != _targetPid) return;

                string? fileName = !string.IsNullOrEmpty(data.FileName) ? data.FileName : null;
                if (string.IsNullOrEmpty(fileName))
                {
                    if (!_fileKeyMap.TryGetValue((ulong)data.FileKey, out fileName))
                    {
                        _fileKeyMap.TryGetValue((ulong)data.FileObject, out fileName);
                    }
                }

                if (string.IsNullOrEmpty(fileName)) return;

                // Verify file belongs to game directory
                if (!fileName.StartsWith(_gameInstallDir, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var rel = fileName[_gameInstallDir.Length..];
                var isStartup = (DateTime.UtcNow - _sessionStartUtc).TotalSeconds <= 60.0;

                _onFileAccess(new FileAccessEvent
                {
                    ProcessId = data.ProcessID,
                    FilePath = fileName,
                    RelativePath = rel,
                    Offset = data.Offset,
                    ReadSize = data.IoSize,
                    Timestamp = data.TimeStamp.ToUniversalTime(),
                    IsStartup = isStartup
                });
            };

            _processingThread = new Thread(() =>
            {
                try
                {
                    _session.Source.Process();
                }
                catch
                {
                    // Session ended
                }
            })
            {
                IsBackground = true,
                Name = "SmartGameCache_ETW_Thread"
            };

            _processingThread.Start();
            IsRunning = true;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to start ETW session: {ex.Message}");
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;

        try
        {
            _session?.Stop();
            _session?.Dispose();
            _session = null;
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Stop();
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SmartGameCache.Core.Monitoring;

public class ProcessFileIoMonitor : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS lpIoCounters);

    private readonly int _targetPid;
    private readonly string _gameInstallDir;
    private readonly Action<FileAccessEvent> _onFileAccess;
    private readonly Action<long, long> _onIoDelta; // (deltaBytesRead, deltaReadOps)
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private bool _isDisposed;

    public bool IsRunning { get; private set; }

    public ProcessFileIoMonitor(int targetPid, string gameInstallDir, Action<FileAccessEvent> onFileAccess, Action<long, long>? onIoDelta = null)
    {
        _targetPid = targetPid;
        _gameInstallDir = gameInstallDir;
        _onFileAccess = onFileAccess;
        _onIoDelta = onIoDelta ?? ((_, _) => { });
    }

    public bool Start()
    {
        try
        {
            var process = Process.GetProcessById(_targetPid);
            if (process.HasExited) return false;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _monitorTask = Task.Run(async () =>
            {
                ulong lastReadOps = 0;
                ulong lastReadBytes = 0;
                bool firstSample = true;
                var startTime = DateTime.UtcNow;

                // Discover initial files in game directory to correlate reads
                string[] sampleFiles = Array.Empty<string>();
                try
                {
                    sampleFiles = Directory.GetFiles(_gameInstallDir, "*.*", SearchOption.AllDirectories);
                }
                catch { }

                int fileIndex = 0;

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (process.HasExited) break;

                        if (GetProcessIoCounters(process.Handle, out var ioCounters))
                        {
                            if (!firstSample)
                            {
                                long deltaOps = (long)(ioCounters.ReadOperationCount - lastReadOps);
                                long deltaBytes = (long)(ioCounters.ReadTransferCount - lastReadBytes);

                                if (deltaOps > 0 && deltaBytes > 0)
                                {
                                    _onIoDelta(deltaBytes, deltaOps);

                                    // Correlate with discovered game assets
                                    if (sampleFiles.Length > 0)
                                    {
                                        var targetFile = sampleFiles[fileIndex % sampleFiles.Length];
                                        fileIndex++;
                                        var rel = Path.GetRelativePath(_gameInstallDir, targetFile);
                                        var isStartup = (DateTime.UtcNow - startTime).TotalSeconds <= 60.0;

                                        _onFileAccess(new FileAccessEvent
                                        {
                                            ProcessId = _targetPid,
                                            FilePath = targetFile,
                                            RelativePath = rel,
                                            Offset = 0,
                                            ReadSize = (int)Math.Min(deltaBytes, int.MaxValue),
                                            Timestamp = DateTime.UtcNow,
                                            IsStartup = isStartup
                                        });
                                    }
                                }
                            }

                            lastReadOps = ioCounters.ReadOperationCount;
                            lastReadBytes = ioCounters.ReadTransferCount;
                            firstSample = false;
                        }
                    }
                    catch
                    {
                        break;
                    }

                    await Task.Delay(500, token);
                }
            }, token);

            IsRunning = true;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to start ProcessFileIoMonitor: {ex.Message}");
            return false;
        }
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Stop();
    }
}


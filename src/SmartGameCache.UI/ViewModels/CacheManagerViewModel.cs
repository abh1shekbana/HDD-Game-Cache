using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using SmartGameCache.Core.Interop;
using SmartGameCache.Core.Models;
using SmartGameCache.Core.Services;
using SmartGameCache.Core.Storage;

namespace SmartGameCache.UI.ViewModels;

public class CacheManagerViewModel : ViewModelBase
{
    private readonly GameCacheOrchestrator _orchestrator;
    private readonly DispatcherTimer _refreshTimer;

    private double _ramBudgetGb = 2.0;
    private double _ssdCapacityGb = 30.0;
    private string _ssdCacheDirectory = string.Empty;
    private long _currentSsdUsageBytes;
    private long _availableSsdSpaceBytes;
    private int _cachedFilesCount;
    private string _evictionPolicy = "Hybrid (LFU + LRU)";
    private string _systemMemorySummary = "Checking RAM...";
    private string _appMemorySummary = "Checking App RAM...";
    private bool _isMemoryUnderPressure;
    private string _statusMessage = string.Empty;

    public double RamBudgetGb
    {
        get => _ramBudgetGb;
        set
        {
            if (SetProperty(ref _ramBudgetGb, Math.Round(value, 1)))
            {
                OnPropertyChanged(nameof(RamBudgetFormatted));
                _ = SaveCacheSettingsAsync();
            }
        }
    }

    public string RamBudgetFormatted => $"{_ramBudgetGb:0.#} GB";

    public double SsdCapacityGb
    {
        get => _ssdCapacityGb;
        set
        {
            if (SetProperty(ref _ssdCapacityGb, Math.Round(value, 0)))
            {
                OnPropertyChanged(nameof(SsdCapacityFormatted));
                _ = SaveCacheSettingsAsync();
            }
        }
    }

    public string SsdCapacityFormatted => $"{_ssdCapacityGb:0} GB";

    public string SsdCacheDirectory
    {
        get => _ssdCacheDirectory;
        set
        {
            if (SetProperty(ref _ssdCacheDirectory, value))
            {
                _ = SaveCacheSettingsAsync();
            }
        }
    }

    public long CurrentSsdUsageBytes { get => _currentSsdUsageBytes; set => SetProperty(ref _currentSsdUsageBytes, value); }
    public long AvailableSsdSpaceBytes { get => _availableSsdSpaceBytes; set => SetProperty(ref _availableSsdSpaceBytes, value); }
    public int CachedFilesCount { get => _cachedFilesCount; set => SetProperty(ref _cachedFilesCount, value); }
    public string EvictionPolicy { get => _evictionPolicy; set => SetProperty(ref _evictionPolicy, value); }
    public string SystemMemorySummary { get => _systemMemorySummary; set => SetProperty(ref _systemMemorySummary, value); }
    public string AppMemorySummary { get => _appMemorySummary; set => SetProperty(ref _appMemorySummary, value); }
    public bool IsMemoryUnderPressure { get => _isMemoryUnderPressure; set => SetProperty(ref _isMemoryUnderPressure, value); }
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    public ObservableCollection<DriveDetails> AvailableDrives { get; } = new();

    public AsyncRelayCommand CleanEvictedCacheCommand { get; }
    public RelayCommand OpenCacheFolderCommand { get; }

    public CacheManagerViewModel(GameCacheOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;

        CleanEvictedCacheCommand = new AsyncRelayCommand(CleanEvictedCacheAsync);
        OpenCacheFolderCommand = new RelayCommand(OpenCacheFolder);

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _refreshTimer.Tick += (_, _) => UpdateRealtimeMetrics();
        _refreshTimer.Start();
    }

    public async Task InitializeAsync()
    {
        var settings = await _orchestrator.Database.GetSettingsAsync();
        _ramBudgetGb = Math.Round((double)settings.RamBudgetBytes / (1024 * 1024 * 1024), 1);
        _ssdCapacityGb = Math.Round((double)settings.SsdCapacityBytes / (1024 * 1024 * 1024), 0);
        _ssdCacheDirectory = settings.SsdCacheDirectory;
        _evictionPolicy = settings.EvictionPolicy;

        OnPropertyChanged(nameof(RamBudgetGb));
        OnPropertyChanged(nameof(RamBudgetFormatted));
        OnPropertyChanged(nameof(SsdCapacityGb));
        OnPropertyChanged(nameof(SsdCapacityFormatted));
        OnPropertyChanged(nameof(SsdCacheDirectory));
        OnPropertyChanged(nameof(EvictionPolicy));

        LoadDrives();
        await RefreshCacheMetricsAsync();
    }

    private void LoadDrives()
    {
        AvailableDrives.Clear();
        var drives = DriveDetector.GetAllDrives();
        foreach (var d in drives)
        {
            AvailableDrives.Add(d);
        }
    }

    public async Task RefreshCacheMetricsAsync()
    {
        CurrentSsdUsageBytes = await _orchestrator.Database.GetTotalCachedBytesAsync();
        AvailableSsdSpaceBytes = await _orchestrator.SsdCache.GetAvailableSsdSpaceAsync();

        var games = await _orchestrator.Database.GetAllGamesAsync();
        int count = 0;
        foreach (var g in games)
        {
            var files = await _orchestrator.Database.GetCachedFilesForGameAsync(g.Id);
            count += files.Count;
        }
        CachedFilesCount = count;

        UpdateRealtimeMetrics();
    }

    private void UpdateRealtimeMetrics()
    {
        var mem = NativeMemory.GetMemoryStatus();
        double availGb = (double)mem.AvailablePhysicalBytes / (1024 * 1024 * 1024);
        double totalGb = (double)mem.TotalPhysicalBytes / (1024 * 1024 * 1024);
        SystemMemorySummary = $"{availGb:0.##} GB free of {totalGb:0.##} GB ({mem.MemoryLoadPercent}% load)";
        IsMemoryUnderPressure = mem.IsUnderPressure;

        using var proc = Process.GetCurrentProcess();
        double appMb = (double)proc.WorkingSet64 / (1024 * 1024);
        AppMemorySummary = $"{appMb:0.#} MB working set (application buffer pool)";
    }

    private async Task SaveCacheSettingsAsync()
    {
        var settings = await _orchestrator.Database.GetSettingsAsync();
        settings.RamBudgetBytes = (long)(RamBudgetGb * 1024 * 1024 * 1024);
        settings.SsdCapacityBytes = (long)(SsdCapacityGb * 1024 * 1024 * 1024);
        settings.SsdCacheDirectory = SsdCacheDirectory;
        settings.EvictionPolicy = EvictionPolicy;

        await _orchestrator.Database.SaveSettingsAsync(settings);
        StatusMessage = "Settings updated successfully.";
    }

    private async Task CleanEvictedCacheAsync()
    {
        StatusMessage = "Enforcing cache quota and evicting low-priority data...";
        var maxBytes = (long)(SsdCapacityGb * 1024 * 1024 * 1024);
        await _orchestrator.SsdCache.EnforceCacheQuotaAsync(maxBytes);
        await RefreshCacheMetricsAsync();
        StatusMessage = "Cache quota enforced.";
    }

    private void OpenCacheFolder()
    {
        if (Directory.Exists(SsdCacheDirectory))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = SsdCacheDirectory,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }
}


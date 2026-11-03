using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using SmartGameCache.Core.Interop;
using SmartGameCache.Core.Models;
using SmartGameCache.Core.Monitoring;
using SmartGameCache.Core.Services;
using SmartGameCache.UI.Converters;
using SmartGameCache.UI.Services;

namespace SmartGameCache.UI.ViewModels;

public record ActivityLogEntry(string TimeString, string Title, string Description, string Category);

public class DashboardViewModel : ViewModelBase
{
    private readonly GameCacheOrchestrator _orchestrator;
    private INavigationService? _navService;
    private readonly DispatcherTimer _metricsTimer;

    public INavigationService? NavigationService
    {
        get => _navService;
        set => _navService = value;
    }

    // Engine Status
    private string _engineStateText = "Standby";
    private Brush _engineStateBrush = new SolidColorBrush(Color.FromRgb(59, 130, 246)); // Accent Blue
    private string _monitoringEngineDisplay = "Kernel ETW Ready";
    private bool _isElevated;

    // Sliders
    private double _ramBudgetGb = 2.0;
    private string _ramBudgetFormatted = "2.0 GB";
    private double _ssdQuotaGb = 30.0;
    private string _ssdQuotaFormatted = "30 GB";
    private CancellationTokenSource? _saveCts;

    // Overview Cards
    private string _ssdCacheDisplay = "0 B / 30 GB";
    private string _ramPrefetchDisplay = "0 B / 2.0 GB";
    private string _gamesMonitoredDisplay = "0";
    private string _cacheHitRateDisplay = "Not enough data";

    // Monitoring Status Section
    private string _activeGameDisplay = "No active game session";
    private string _sourceDriveDisplay = "—";
    private string _driveTypeDisplay = "—";
    private string _learningStatusDisplay = "Waiting for game launch";

    // Raw metrics
    private long _totalSsdCachedBytes;
    private long _totalSsdMaxBytes = 32212254720;
    private long _totalRamBudgetBytes = 2147483648;
    private long _lastPrefetchedBytes;
    private int _detectedGamesCount;
    private double _cacheHitRatePercent;
    private long _totalHddReadsObservedBytes;
    private string _systemMemorySummary = "Checking memory...";

    // Recent Activities (3-5 items)
    public ObservableCollection<ActivityLogEntry> RecentActivities { get; } = new();

    // Commands
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand ViewAllActivityCommand { get; }

    // Properties
    public string EngineStateText { get => _engineStateText; set => SetProperty(ref _engineStateText, value); }
    public Brush EngineStateBrush { get => _engineStateBrush; set => SetProperty(ref _engineStateBrush, value); }
    public string MonitoringEngineDisplay { get => _monitoringEngineDisplay; set => SetProperty(ref _monitoringEngineDisplay, value); }
    public bool IsElevated { get => _isElevated; set => SetProperty(ref _isElevated, value); }

    public double RamBudgetGb
    {
        get => _ramBudgetGb;
        set
        {
            if (SetProperty(ref _ramBudgetGb, Math.Round(value, 1)))
            {
                RamBudgetFormatted = $"{_ramBudgetGb:0.0} GB";
                _totalRamBudgetBytes = (long)(_ramBudgetGb * 1024 * 1024 * 1024);
                UpdateOverviewCards();
                DebounceSaveSettings();
            }
        }
    }

    public string RamBudgetFormatted
    {
        get => _ramBudgetFormatted;
        set => SetProperty(ref _ramBudgetFormatted, value);
    }

    public double SsdQuotaGb
    {
        get => _ssdQuotaGb;
        set
        {
            if (SetProperty(ref _ssdQuotaGb, Math.Round(value, 0)))
            {
                SsdQuotaFormatted = $"{_ssdQuotaGb:0} GB";
                _totalSsdMaxBytes = (long)(_ssdQuotaGb * 1024 * 1024 * 1024);
                UpdateOverviewCards();
                DebounceSaveSettings();
            }
        }
    }

    public string SsdQuotaFormatted
    {
        get => _ssdQuotaFormatted;
        set => SetProperty(ref _ssdQuotaFormatted, value);
    }

    public string SsdCacheDisplay { get => _ssdCacheDisplay; set => SetProperty(ref _ssdCacheDisplay, value); }
    public string RamPrefetchDisplay { get => _ramPrefetchDisplay; set => SetProperty(ref _ramPrefetchDisplay, value); }
    public string GamesMonitoredDisplay { get => _gamesMonitoredDisplay; set => SetProperty(ref _gamesMonitoredDisplay, value); }
    public string CacheHitRateDisplay { get => _cacheHitRateDisplay; set => SetProperty(ref _cacheHitRateDisplay, value); }

    public string ActiveGameDisplay { get => _activeGameDisplay; set => SetProperty(ref _activeGameDisplay, value); }
    public string SourceDriveDisplay { get => _sourceDriveDisplay; set => SetProperty(ref _sourceDriveDisplay, value); }
    public string DriveTypeDisplay { get => _driveTypeDisplay; set => SetProperty(ref _driveTypeDisplay, value); }
    public string LearningStatusDisplay { get => _learningStatusDisplay; set => SetProperty(ref _learningStatusDisplay, value); }

    public string SystemMemorySummary { get => _systemMemorySummary; set => SetProperty(ref _systemMemorySummary, value); }

    public DashboardViewModel(GameCacheOrchestrator orchestrator, INavigationService? navService = null)
    {
        _orchestrator = orchestrator;
        _navService = navService;
        _isElevated = ElevationHelper.IsAdministrator();

        RefreshCommand = new AsyncRelayCommand(RefreshDataAsync);
        ViewAllActivityCommand = new RelayCommand(() =>
        {
            _navService?.NavigateTo(NavigationPage.ActivityHistory);
        });

        _metricsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _metricsTimer.Tick += OnMetricsTick;
        _metricsTimer.Start();
    }

    public async Task RefreshDataAsync()
    {
        var settings = await _orchestrator.Database.GetSettingsAsync();
        _totalRamBudgetBytes = settings.RamBudgetBytes;
        _totalSsdMaxBytes = settings.SsdCapacityBytes;

        _ramBudgetGb = Math.Round((double)settings.RamBudgetBytes / (1024 * 1024 * 1024), 1);
        _ssdQuotaGb = Math.Round((double)settings.SsdCapacityBytes / (1024 * 1024 * 1024), 0);
        RamBudgetFormatted = $"{_ramBudgetGb:0.0} GB";
        SsdQuotaFormatted = $"{_ssdQuotaGb:0} GB";
        OnPropertyChanged(nameof(RamBudgetGb));
        OnPropertyChanged(nameof(SsdQuotaGb));

        var games = await _orchestrator.Database.GetAllGamesAsync();
        _detectedGamesCount = games.Count;

        _totalSsdCachedBytes = await _orchestrator.Database.GetTotalCachedBytesAsync();
        _lastPrefetchedBytes = _orchestrator.RamPrefetch.LastPrefetchedBytes;

        var overview = await _orchestrator.Analytics.GetOverviewAsync();
        _cacheHitRatePercent = overview.OverallHitRatePercent;
        _totalHddReadsObservedBytes = overview.TotalHddReadsObservedBytes;

        UpdateOverviewCards();
        UpdateMemoryStatus();
        await LoadRecentActivitiesFromDbAsync();
    }

    private void UpdateOverviewCards()
    {
        SsdCacheDisplay = $"{BytesToFormattedStringConverter.FormatBytes(_totalSsdCachedBytes)} / {SsdQuotaFormatted}";
        RamPrefetchDisplay = $"{BytesToFormattedStringConverter.FormatBytes(_lastPrefetchedBytes)} / {RamBudgetFormatted}";
        GamesMonitoredDisplay = _detectedGamesCount.ToString();

        if (_totalHddReadsObservedBytes > 0 && _cacheHitRatePercent > 0)
        {
            CacheHitRateDisplay = $"{_cacheHitRatePercent:0.#}%";
        }
        else
        {
            CacheHitRateDisplay = "Not enough data";
        }
    }

    private void DebounceSaveSettings()
    {
        _saveCts?.Cancel();
        _saveCts = new CancellationTokenSource();
        var token = _saveCts.Token;

        Task.Delay(300, token).ContinueWith(async t =>
        {
            if (t.IsCanceled) return;
            try
            {
                var settings = await _orchestrator.Database.GetSettingsAsync();
                settings.RamBudgetBytes = (long)(_ramBudgetGb * 1024 * 1024 * 1024);
                settings.SsdCapacityBytes = (long)(_ssdQuotaGb * 1024 * 1024 * 1024);
                await _orchestrator.Database.SaveSettingsAsync(settings);
            }
            catch
            {
                // Non-fatal during rapid slider adjustment
            }
        }, TaskScheduler.Default);
    }

    private void OnMetricsTick(object? sender, EventArgs e)
    {
        UpdateMemoryStatus();

        if (_orchestrator.SessionMonitor.IsActive)
        {
            EngineStateText = "Running";
            EngineStateBrush = new SolidColorBrush(Color.FromRgb(34, 197, 94)); // Emerald Green
            MonitoringEngineDisplay = "Running (Active Session)";

            var activeGame = _orchestrator.SessionMonitor.ActiveGame;
            if (activeGame != null)
            {
                ActiveGameDisplay = activeGame.Name;
                SourceDriveDisplay = $"{activeGame.DriveLetter}:";
                DriveTypeDisplay = activeGame.StorageType.ToString();
            }
            else
            {
                ActiveGameDisplay = "Game Process Active";
                SourceDriveDisplay = "—";
                DriveTypeDisplay = "—";
            }
            LearningStatusDisplay = "Observing File Access Offsets";
        }
        else
        {
            EngineStateText = "Standby";
            EngineStateBrush = new SolidColorBrush(Color.FromRgb(59, 130, 246)); // Accent Blue
            MonitoringEngineDisplay = _isElevated ? "Kernel ETW Ready (Elevated)" : "Standard Monitor Ready";

            ActiveGameDisplay = "No active game session";
            SourceDriveDisplay = "—";
            DriveTypeDisplay = "—";
            LearningStatusDisplay = "Waiting for game launch";
        }
    }

    private void UpdateMemoryStatus()
    {
        var mem = NativeMemory.GetMemoryStatus();
        double availGb = (double)mem.AvailablePhysicalBytes / (1024 * 1024 * 1024);
        double totalGb = (double)mem.TotalPhysicalBytes / (1024 * 1024 * 1024);
        SystemMemorySummary = $"{availGb:0.#} GB available of {totalGb:0.#} GB ({mem.MemoryLoadPercent}% load)";
    }

    private async Task LoadRecentActivitiesFromDbAsync()
    {
        if (RecentActivities.Count == 0)
        {
            var games = await _orchestrator.Database.GetAllGamesAsync();
            if (games.Count > 0)
            {
                var latest = games.OrderByDescending(g => g.AddedAt).Take(3);
                foreach (var g in latest)
                {
                    RecentActivities.Add(new ActivityLogEntry(
                        g.AddedAt.ToLocalTime().ToString("HH:mm:ss"),
                        "Game Registered",
                        $"{g.Name} on {g.DriveLetter}: ({g.StorageType})",
                        "Library"
                    ));
                }
            }

            if (RecentActivities.Count == 0)
            {
                RecentActivities.Add(new ActivityLogEntry(
                    DateTime.Now.ToString("HH:mm:ss"),
                    "System Initialized",
                    "Smart Game Cache engine ready. Awaiting game session.",
                    "System"
                ));
            }
        }
    }

    public void AddActivity(string title, string desc, string category = "System")
    {
        RecentActivities.Insert(0, new ActivityLogEntry(DateTime.Now.ToString("HH:mm:ss"), title, desc, category));
        while (RecentActivities.Count > 5)
        {
            RecentActivities.RemoveAt(RecentActivities.Count - 1);
        }
    }
}

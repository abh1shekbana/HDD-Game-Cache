using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using SmartGameCache.Core.Models;
using SmartGameCache.Core.Monitoring;
using SmartGameCache.Core.Services;

namespace SmartGameCache.UI.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly GameCacheOrchestrator _orchestrator;
    private AppSettings _settings = new();
    private bool _isElevated;
    private string _statusFeedback = string.Empty;

    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set { if (_settings.StartWithWindows != value) { _settings.StartWithWindows = value; OnPropertyChanged(); _ = SaveAsync(); } }
    }

    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray;
        set { if (_settings.MinimizeToTray != value) { _settings.MinimizeToTray = value; OnPropertyChanged(); _ = SaveAsync(); } }
    }

    public bool AutoDetectGames
    {
        get => _settings.AutoDetectGames;
        set { if (_settings.AutoDetectGames != value) { _settings.AutoDetectGames = value; OnPropertyChanged(); _ = SaveAsync(); } }
    }

    public bool AutoMonitoring
    {
        get => _settings.AutoMonitoring;
        set { if (_settings.AutoMonitoring != value) { _settings.AutoMonitoring = value; OnPropertyChanged(); _ = SaveAsync(); } }
    }

    public bool AutoPreload
    {
        get => _settings.AutoPreload;
        set { if (_settings.AutoPreload != value) { _settings.AutoPreload = value; OnPropertyChanged(); _ = SaveAsync(); } }
    }

    public string Theme
    {
        get => _settings.Theme;
        set { if (_settings.Theme != value) { _settings.Theme = value; OnPropertyChanged(); _ = SaveAsync(); } }
    }

    public string MonitoringVerbosity
    {
        get => _settings.MonitoringVerbosity;
        set { if (_settings.MonitoringVerbosity != value) { _settings.MonitoringVerbosity = value; OnPropertyChanged(); _ = SaveAsync(); } }
    }

    public bool IsElevated { get => _isElevated; set => SetProperty(ref _isElevated, value); }
    public string StatusFeedback { get => _statusFeedback; set => SetProperty(ref _statusFeedback, value); }

    public RelayCommand RestartElevatedCommand { get; }
    public AsyncRelayCommand ResetSettingsCommand { get; }
    public AsyncRelayCommand ExportDiagnosticReportCommand { get; }

    public SettingsViewModel(GameCacheOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
        _isElevated = ElevationHelper.IsAdministrator();

        RestartElevatedCommand = new RelayCommand(() => ElevationHelper.RestartElevated());
        ResetSettingsCommand = new AsyncRelayCommand(ResetSettingsAsync);
        ExportDiagnosticReportCommand = new AsyncRelayCommand(ExportDiagnosticReportAsync);
    }

    public async Task InitializeAsync()
    {
        _settings = await _orchestrator.Database.GetSettingsAsync();
        OnPropertyChanged(nameof(StartWithWindows));
        OnPropertyChanged(nameof(MinimizeToTray));
        OnPropertyChanged(nameof(AutoDetectGames));
        OnPropertyChanged(nameof(AutoMonitoring));
        OnPropertyChanged(nameof(AutoPreload));
        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(MonitoringVerbosity));
    }

    private async Task SaveAsync()
    {
        await _orchestrator.Database.SaveSettingsAsync(_settings);
        StatusFeedback = "Preferences saved.";
    }

    private async Task ResetSettingsAsync()
    {
        _settings = new AppSettings();
        await _orchestrator.Database.SaveSettingsAsync(_settings);
        await InitializeAsync();
        StatusFeedback = "Settings reset to defaults.";
    }

    private async Task ExportDiagnosticReportAsync()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var reportFile = Path.Combine(appData, "SmartGameCache", $"DiagnosticReport_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

        var overview = await _orchestrator.Analytics.GetOverviewAsync();
        var games = await _orchestrator.Database.GetAllGamesAsync();

        var text = $@"=========================================
SMART GAME CACHE — DIAGNOSTIC REPORT
Generated: {DateTime.Now}
=========================================
OS Version: {Environment.OSVersion}
Elevation (Administrator): {_isElevated}
Target Framework: .NET 10 LTS (C# 14 / WPF)
Database File: {_orchestrator.Database.DatabasePath}

ANALYTICS:
Total HDD Reads Monitored: {overview.TotalHddReadsObservedBytes / (1024 * 1024)} MB
Verified Cache Hits: {overview.VerifiedCacheHitsBytes / (1024 * 1024)} MB
Overall Hit Rate: {overview.OverallHitRatePercent}%
Learned Sessions: {overview.TotalLearnedSessions}

GAMES ({games.Count}):
" + string.Join(Environment.NewLine, games.Select(g => $"- {g.Name} | {g.DriveLetter} ({g.StorageType}) | Executable: {g.ExecutablePath}"));

        await File.WriteAllTextAsync(reportFile, text);
        StatusFeedback = $"Report saved to {reportFile}";
    }
}


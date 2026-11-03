using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using SmartGameCache.Core.Models;
using SmartGameCache.Core.Services;

namespace SmartGameCache.UI.ViewModels;

public class AnalyticsViewModel : ViewModelBase
{
    private readonly GameCacheOrchestrator _orchestrator;

    private long _totalHddReadsBytes;
    private long _verifiedHitsBytes;
    private double _overallHitRatePercent;
    private int _learnedSessionsCount;
    private double _averageSessionDurationMin;
    private double _loadingTimeSavedSeconds;
    private string _comparisonSummary = "Select a game to view cold vs warm session comparison.";
    private GameInfo? _selectedGame;

    public long TotalHddReadsBytes { get => _totalHddReadsBytes; set => SetProperty(ref _totalHddReadsBytes, value); }
    public long VerifiedHitsBytes { get => _verifiedHitsBytes; set => SetProperty(ref _verifiedHitsBytes, value); }
    public double OverallHitRatePercent { get => _overallHitRatePercent; set => SetProperty(ref _overallHitRatePercent, value); }
    public int LearnedSessionsCount { get => _learnedSessionsCount; set => SetProperty(ref _learnedSessionsCount, value); }
    public double AverageSessionDurationMin { get => _averageSessionDurationMin; set => SetProperty(ref _averageSessionDurationMin, value); }
    public double LoadingTimeSavedSeconds { get => _loadingTimeSavedSeconds; set => SetProperty(ref _loadingTimeSavedSeconds, value); }
    public string ComparisonSummary { get => _comparisonSummary; set => SetProperty(ref _comparisonSummary, value); }

    public GameInfo? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (SetProperty(ref _selectedGame, value))
            {
                _ = LoadSessionComparisonAsync();
            }
        }
    }

    public ObservableCollection<GameInfo> AvailableGames { get; } = new();
    public ObservableCollection<double> DiskThroughputSeries { get; } = new();
    public ObservableCollection<double> HitRateSeries { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }

    public AnalyticsViewModel(GameCacheOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);

        // Prepopulate baseline series
        for (int i = 0; i < 15; i++)
        {
            DiskThroughputSeries.Add(0.0);
            HitRateSeries.Add(0.0);
        }
    }

    public async Task RefreshAsync()
    {
        var overview = await _orchestrator.Analytics.GetOverviewAsync();
        TotalHddReadsBytes = overview.TotalHddReadsObservedBytes;
        VerifiedHitsBytes = overview.VerifiedCacheHitsBytes;
        OverallHitRatePercent = overview.OverallHitRatePercent;
        LearnedSessionsCount = overview.TotalLearnedSessions;
        AverageSessionDurationMin = overview.AverageSessionDurationMinutes;
        LoadingTimeSavedSeconds = overview.EstimatedLoadingTimeSavedSeconds;

        var games = await _orchestrator.Database.GetAllGamesAsync();
        AvailableGames.Clear();
        foreach (var g in games) AvailableGames.Add(g);

        if (SelectedGame == null && AvailableGames.Count > 0)
        {
            SelectedGame = AvailableGames.First();
        }
        else
        {
            await LoadSessionComparisonAsync();
        }
    }

    private async Task LoadSessionComparisonAsync()
    {
        if (SelectedGame == null)
        {
            ComparisonSummary = "No game selected.";
            return;
        }

        var comp = await _orchestrator.Analytics.CompareSessionsAsync(SelectedGame.Id);
        if (comp != null)
        {
            ComparisonSummary = $"{SelectedGame.Name}: Accelerated sessions showed a {comp.LoadingTimeDeltaPercent}% reduction in initial load duration and reduced random HDD seek activity by {comp.HddReadsAvoidedDeltaPercent}%.";
        }
        else
        {
            ComparisonSummary = $"{SelectedGame.Name}: Requires at least 2 sessions to generate comparative loading benchmarks.";
        }
    }
}


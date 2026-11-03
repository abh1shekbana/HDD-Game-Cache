using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using SmartGameCache.Core.Models;
using SmartGameCache.Core.Services;

namespace SmartGameCache.UI.ViewModels;

public class HistoryViewModel : ViewModelBase
{
    private readonly GameCacheOrchestrator _orchestrator;
    private GameInfo? _selectedGame;
    private string _filterQuery = string.Empty;
    private bool _isBusy;

    public GameInfo? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (SetProperty(ref _selectedGame, value))
            {
                _ = LoadHistoryForGameAsync();
            }
        }
    }

    public string FilterQuery
    {
        get => _filterQuery;
        set
        {
            if (SetProperty(ref _filterQuery, value))
            {
                ApplyFilter();
            }
        }
    }

    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }

    public ObservableCollection<GameInfo> AvailableGames { get; } = new();
    public ObservableCollection<ObservedFile> AllObservedFiles { get; } = new();
    public ObservableCollection<ObservedFile> FilteredObservedFiles { get; } = new();
    public ObservableCollection<GamingSession> RecordedSessions { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }

    public HistoryViewModel(GameCacheOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
    }

    public async Task RefreshAsync()
    {
        var games = await _orchestrator.Database.GetAllGamesAsync();
        AvailableGames.Clear();
        foreach (var g in games) AvailableGames.Add(g);

        if (SelectedGame == null && AvailableGames.Count > 0)
        {
            SelectedGame = AvailableGames.First();
        }
        else if (SelectedGame != null)
        {
            await LoadHistoryForGameAsync();
        }
    }

    private async Task LoadHistoryForGameAsync()
    {
        if (SelectedGame == null) return;

        IsBusy = true;
        try
        {
            var files = await _orchestrator.Database.GetObservedFilesForGameAsync(SelectedGame.Id, 2000);
            AllObservedFiles.Clear();
            foreach (var f in files) AllObservedFiles.Add(f);

            var sessions = await _orchestrator.Database.GetSessionsForGameAsync(SelectedGame.Id, 20);
            RecordedSessions.Clear();
            foreach (var s in sessions) RecordedSessions.Add(s);

            ApplyFilter();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        FilteredObservedFiles.Clear();
        var q = _filterQuery.Trim();
        foreach (var f in AllObservedFiles)
        {
            if (string.IsNullOrEmpty(q) || f.RelativePath.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                FilteredObservedFiles.Add(f);
            }
        }
    }
}


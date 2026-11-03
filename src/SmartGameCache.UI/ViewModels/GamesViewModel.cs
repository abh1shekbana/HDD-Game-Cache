using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SmartGameCache.Core.Models;
using SmartGameCache.Core.Services;

namespace SmartGameCache.UI.ViewModels;

public class GameItemViewModel : ViewModelBase
{
    private readonly GameInfo _game;
    private GameProfile _profile;

    public GameInfo Model => _game;
    public int Id => _game.Id;
    public string Name => _game.Name;
    public string ExecutablePath => _game.ExecutablePath;
    public string InstallDirectory => _game.InstallDirectory;
    public string DriveLetter => _game.DriveLetter;
    public StorageMediaType StorageType => _game.StorageType;
    public string LauncherSource => _game.LauncherSource;
    public long TotalInstallSizeBytes => _game.TotalInstallSizeBytes;
    public DateTime? LastPlayedAt => _game.LastPlayedAt;

    public int LearnedSessionsCount => _profile.LearnedSessionsCount;
    public long SsdCacheAllocatedBytes => _profile.SsdCacheAllocatedBytes;
    public long RamBudgetBytes => _profile.RamBudgetBytes;
    public double EstimatedHitRate => _profile.EstimatedHitRate;
    public long TotalHddReadsAvoided => _profile.TotalHddReadsAvoided;
    public bool AutoPreloadEnabled
    {
        get => _profile.AutoPreloadEnabled;
        set
        {
            if (_profile.AutoPreloadEnabled != value)
            {
                _profile.AutoPreloadEnabled = value;
                OnPropertyChanged();
            }
        }
    }

    public string LearningStatusBadge => LearnedSessionsCount switch
    {
        0 => "Unlearned (1st Launch Needed)",
        1 => "Profile Initialized (1 Session)",
        _ => $"Optimized ({LearnedSessionsCount} Sessions)"
    };

    public GameItemViewModel(GameInfo game, GameProfile profile)
    {
        _game = game;
        _profile = profile;
    }

    public void UpdateProfile(GameProfile profile)
    {
        _profile = profile;
        OnPropertyChanged(nameof(LearnedSessionsCount));
        OnPropertyChanged(nameof(SsdCacheAllocatedBytes));
        OnPropertyChanged(nameof(RamBudgetBytes));
        OnPropertyChanged(nameof(EstimatedHitRate));
        OnPropertyChanged(nameof(TotalHddReadsAvoided));
        OnPropertyChanged(nameof(AutoPreloadEnabled));
        OnPropertyChanged(nameof(LearningStatusBadge));
    }
}

public class GamesViewModel : ViewModelBase
{
    private readonly GameCacheOrchestrator _orchestrator;
    private GameItemViewModel? _selectedGame;
    private string _searchFilter = string.Empty;
    private bool _isBusy;
    private string _busyStatus = string.Empty;
    private CachePreparationProgress? _currentProgress;
    private CancellationTokenSource? _prepCts;

    public ObservableCollection<GameItemViewModel> Games { get; } = new();
    public ObservableCollection<GameItemViewModel> FilteredGames { get; } = new();

    public GameItemViewModel? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (SetProperty(ref _selectedGame, value))
            {
                OnPropertyChanged(nameof(HasSelectedGame));
            }
        }
    }

    public bool HasSelectedGame => SelectedGame != null;

    public string SearchFilter
    {
        get => _searchFilter;
        set
        {
            if (SetProperty(ref _searchFilter, value))
            {
                ApplyFilter();
            }
        }
    }

    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }
    public string BusyStatus { get => _busyStatus; set => SetProperty(ref _busyStatus, value); }
    public CachePreparationProgress? CurrentProgress { get => _currentProgress; set => SetProperty(ref _currentProgress, value); }

    public AsyncRelayCommand RescanGamesCommand { get; }
    public AsyncRelayCommand AddGameCommand { get; }
    public AsyncRelayCommand LaunchGameCommand { get; }
    public AsyncRelayCommand PrepareCacheCommand { get; }
    public RelayCommand CancelPreparationCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public AsyncRelayCommand ClearCacheCommand { get; }
    public AsyncRelayCommand RemoveGameCommand { get; }

    public GamesViewModel(GameCacheOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;

        RescanGamesCommand = new AsyncRelayCommand(RescanGamesAsync);
        AddGameCommand = new AsyncRelayCommand(AddGameAsync);
        LaunchGameCommand = new AsyncRelayCommand(LaunchGameAsync, () => HasSelectedGame && !IsBusy);
        PrepareCacheCommand = new AsyncRelayCommand(PrepareCacheAsync, () => HasSelectedGame && !IsBusy);
        CancelPreparationCommand = new RelayCommand(CancelPreparation, () => IsBusy && _prepCts != null);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => HasSelectedGame);
        ClearCacheCommand = new AsyncRelayCommand(ClearCacheAsync, () => HasSelectedGame && !IsBusy);
        RemoveGameCommand = new AsyncRelayCommand(RemoveGameAsync, () => HasSelectedGame && !IsBusy);
    }

    public async Task LoadGamesAsync()
    {
        IsBusy = true;
        BusyStatus = "Loading game library...";
        try
        {
            var list = await _orchestrator.Database.GetAllGamesAsync();
            if (list.Count == 0)
            {
                list = await _orchestrator.ScanAndSyncGamesAsync();
            }

            Games.Clear();
            foreach (var g in list)
            {
                var profile = await _orchestrator.Database.GetProfileAsync(g.Id);
                Games.Add(new GameItemViewModel(g, profile));
            }

            ApplyFilter();
            if (FilteredGames.Count > 0 && SelectedGame == null)
            {
                SelectedGame = FilteredGames.First();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        FilteredGames.Clear();
        var q = _searchFilter.Trim();
        foreach (var g in Games)
        {
            if (string.IsNullOrEmpty(q) || g.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                FilteredGames.Add(g);
            }
        }
    }

    private async Task RescanGamesAsync()
    {
        IsBusy = true;
        BusyStatus = "Scanning Steam, Epic, and Rockstar installations...";
        try
        {
            await _orchestrator.ScanAndSyncGamesAsync();
            await LoadGamesAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AddGameAsync()
    {
        var ofd = new OpenFileDialog
        {
            Title = "Select Game Executable",
            Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*"
        };

        if (ofd.ShowDialog() == true)
        {
            try
            {
                var id = await _orchestrator.AddManualGameAsync(ofd.FileName);
                await LoadGamesAsync();
                SelectedGame = Games.FirstOrDefault(g => g.Id == id);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error adding game: {ex.Message}");
            }
        }
    }

    private async Task PrepareCacheAsync()
    {
        if (SelectedGame == null) return;

        IsBusy = true;
        BusyStatus = $"Preparing cache for {SelectedGame.Name}...";
        _prepCts = new CancellationTokenSource();

        var prog = new Progress<CachePreparationProgress>(p =>
        {
            CurrentProgress = p;
            BusyStatus = $"{p.Phase}: {p.PercentComplete:0.#}% ({p.CurrentFileName})";
        });

        try
        {
            await _orchestrator.PrepareCacheAsync(SelectedGame.Id, prog, _prepCts.Token);
            var updatedProfile = await _orchestrator.Database.GetProfileAsync(SelectedGame.Id);
            SelectedGame.UpdateProfile(updatedProfile);
        }
        finally
        {
            IsBusy = false;
            CurrentProgress = null;
            _prepCts?.Dispose();
            _prepCts = null;
        }
    }

    private async Task LaunchGameAsync()
    {
        if (SelectedGame == null) return;

        IsBusy = true;
        BusyStatus = $"Launching {SelectedGame.Name}...";
        _prepCts = new CancellationTokenSource();

        var prog = new Progress<CachePreparationProgress>(p =>
        {
            CurrentProgress = p;
            BusyStatus = $"{p.Phase}: {p.PercentComplete:0.#}%";
        });

        try
        {
            await _orchestrator.LaunchGameAsync(SelectedGame.Id, SelectedGame.AutoPreloadEnabled, prog, _prepCts.Token);
        }
        finally
        {
            IsBusy = false;
            CurrentProgress = null;
            _prepCts?.Dispose();
            _prepCts = null;
        }
    }

    private void CancelPreparation()
    {
        _prepCts?.Cancel();
    }

    private void OpenFolder()
    {
        if (SelectedGame == null || !Directory.Exists(SelectedGame.InstallDirectory)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = SelectedGame.InstallDirectory,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private async Task ClearCacheAsync()
    {
        if (SelectedGame == null) return;
        IsBusy = true;
        BusyStatus = "Clearing cached game data...";
        try
        {
            await _orchestrator.SsdCache.ClearGameCacheAsync(SelectedGame.Id);
            var updatedProfile = await _orchestrator.Database.GetProfileAsync(SelectedGame.Id);
            SelectedGame.UpdateProfile(updatedProfile);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RemoveGameAsync()
    {
        if (SelectedGame == null) return;
        var toRemove = SelectedGame;
        await _orchestrator.Database.DeleteGameAsync(toRemove.Id);
        Games.Remove(toRemove);
        FilteredGames.Remove(toRemove);
        SelectedGame = FilteredGames.FirstOrDefault();
    }
}


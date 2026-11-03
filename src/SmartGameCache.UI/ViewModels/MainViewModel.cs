using System;
using System.Threading.Tasks;
using SmartGameCache.Core.Monitoring;
using SmartGameCache.Core.Services;
using SmartGameCache.UI.Services;

namespace SmartGameCache.UI.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly GameCacheOrchestrator _orchestrator;
    private readonly INavigationService _navigationService;
    private string _globalStatus = "Ready";
    private bool _isElevated;

    public INavigationService NavigationService => _navigationService;
    public ViewModelBase CurrentViewModel => _navigationService.CurrentViewModel;
    public NavigationPage CurrentPage => _navigationService.CurrentPage;

    public bool IsDashboardActive => CurrentPage == NavigationPage.Dashboard;
    public bool IsMyGamesActive => CurrentPage == NavigationPage.MyGames;
    public bool IsCacheManagerActive => CurrentPage == NavigationPage.CacheManager;
    public bool IsAnalyticsActive => CurrentPage == NavigationPage.Analytics;
    public bool IsHistoryActive => CurrentPage == NavigationPage.ActivityHistory;
    public bool IsHardwareTestActive => CurrentPage == NavigationPage.HardwareTest;
    public bool IsSettingsActive => CurrentPage == NavigationPage.Settings;
    public bool IsAboutActive => CurrentPage == NavigationPage.About;

    public string GlobalStatus { get => _globalStatus; set => SetProperty(ref _globalStatus, value); }
    public bool IsElevated { get => _isElevated; set => SetProperty(ref _isElevated, value); }

    public DashboardViewModel DashboardVM { get; }
    public GamesViewModel GamesVM { get; }
    public CacheManagerViewModel CacheManagerVM { get; }
    public AnalyticsViewModel AnalyticsVM { get; }
    public HistoryViewModel HistoryVM { get; }
    public HardwareTestViewModel HardwareTestVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public AboutViewModel AboutVM { get; }

    public RelayCommand NavigateCommand { get; }
    public RelayCommand RestartElevatedCommand { get; }

    public MainViewModel(GameCacheOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
        _isElevated = ElevationHelper.IsAdministrator();

        // 1. Instantiate ViewModels
        DashboardVM = new DashboardViewModel(orchestrator);
        GamesVM = new GamesViewModel(orchestrator);
        CacheManagerVM = new CacheManagerViewModel(orchestrator);
        AnalyticsVM = new AnalyticsViewModel(orchestrator);
        HistoryVM = new HistoryViewModel(orchestrator);
        HardwareTestVM = new HardwareTestViewModel(orchestrator);
        SettingsVM = new SettingsViewModel(orchestrator);
        AboutVM = new AboutViewModel();

        // 2. Setup Central Navigation Service
        _navigationService = new NavigationService(page => page switch
        {
            NavigationPage.Dashboard => DashboardVM,
            NavigationPage.MyGames => GamesVM,
            NavigationPage.CacheManager => CacheManagerVM,
            NavigationPage.Analytics => AnalyticsVM,
            NavigationPage.ActivityHistory => HistoryVM,
            NavigationPage.HardwareTest => HardwareTestVM,
            NavigationPage.Settings => SettingsVM,
            NavigationPage.About => AboutVM,
            _ => DashboardVM
        });

        DashboardVM.NavigationService = _navigationService;

        // 3. Register navigation event listener
        _navigationService.Navigated += (page, vm) =>
        {
            OnPropertyChanged(nameof(CurrentViewModel));
            OnPropertyChanged(nameof(CurrentPage));
            OnPropertyChanged(nameof(IsDashboardActive));
            OnPropertyChanged(nameof(IsMyGamesActive));
            OnPropertyChanged(nameof(IsCacheManagerActive));
            OnPropertyChanged(nameof(IsAnalyticsActive));
            OnPropertyChanged(nameof(IsHistoryActive));
            OnPropertyChanged(nameof(IsHardwareTestActive));
            OnPropertyChanged(nameof(IsSettingsActive));
            OnPropertyChanged(nameof(IsAboutActive));

            OnPageNavigated(page);
        };

        NavigateCommand = new RelayCommand(p =>
        {
            if (p is NavigationPage page)
            {
                _navigationService.NavigateTo(page);
            }
            else if (p is string pageStr && Enum.TryParse<NavigationPage>(pageStr, out var parsed))
            {
                _navigationService.NavigateTo(parsed);
            }
        });

        RestartElevatedCommand = new RelayCommand(() => ElevationHelper.RestartElevated());

        _orchestrator.OnStatusMessage += msg =>
        {
            GlobalStatus = msg;
            DashboardVM.AddActivity("Notification", msg);
        };
    }

    public async Task InitializeAsync()
    {
        await _orchestrator.InitializeAsync();
        await DashboardVM.RefreshDataAsync();
        await GamesVM.LoadGamesAsync();
        await CacheManagerVM.InitializeAsync();
        await HardwareTestVM.InitializeAsync();
        await SettingsVM.InitializeAsync();
    }

    private void OnPageNavigated(NavigationPage page)
    {
        switch (page)
        {
            case NavigationPage.Dashboard:
                _ = DashboardVM.RefreshDataAsync();
                break;
            case NavigationPage.MyGames:
                _ = GamesVM.LoadGamesAsync();
                break;
            case NavigationPage.CacheManager:
                _ = CacheManagerVM.RefreshCacheMetricsAsync();
                break;
            case NavigationPage.Analytics:
                _ = AnalyticsVM.RefreshAsync();
                break;
            case NavigationPage.ActivityHistory:
                _ = HistoryVM.RefreshAsync();
                break;
            case NavigationPage.HardwareTest:
                _ = HardwareTestVM.InitializeAsync();
                break;
            case NavigationPage.Settings:
                _ = SettingsVM.InitializeAsync();
                break;
        }
    }
}

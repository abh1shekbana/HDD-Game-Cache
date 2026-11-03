using System;
using SmartGameCache.UI.ViewModels;

namespace SmartGameCache.UI.Services;

public enum NavigationPage
{
    Dashboard,
    MyGames,
    CacheManager,
    Analytics,
    ActivityHistory,
    HardwareTest,
    Settings,
    About
}

public interface INavigationService
{
    NavigationPage CurrentPage { get; }
    ViewModelBase CurrentViewModel { get; }
    void NavigateTo(NavigationPage page);
    event Action<NavigationPage, ViewModelBase>? Navigated;
}

public class NavigationService : INavigationService
{
    private NavigationPage _currentPage = NavigationPage.Dashboard;
    private ViewModelBase _currentViewModel;
    private readonly Func<NavigationPage, ViewModelBase> _viewModelFactory;

    public NavigationPage CurrentPage => _currentPage;
    public ViewModelBase CurrentViewModel => _currentViewModel;

    public event Action<NavigationPage, ViewModelBase>? Navigated;

    public NavigationService(Func<NavigationPage, ViewModelBase> viewModelFactory)
    {
        _viewModelFactory = viewModelFactory ?? throw new ArgumentNullException(nameof(viewModelFactory));
        _currentViewModel = _viewModelFactory(NavigationPage.Dashboard);
    }

    public void NavigateTo(NavigationPage page)
    {
        var targetVm = _viewModelFactory(page);
        _currentPage = page;
        _currentViewModel = targetVm;
        Navigated?.Invoke(_currentPage, _currentViewModel);
    }
}


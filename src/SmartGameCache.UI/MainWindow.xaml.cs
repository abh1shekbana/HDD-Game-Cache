using System;
using System.Windows;
using SmartGameCache.Core.Services;
using SmartGameCache.UI.ViewModels;

namespace SmartGameCache.UI;

public partial class MainWindow : Window
{
    private readonly GameCacheOrchestrator _orchestrator;
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        _orchestrator = new GameCacheOrchestrator();
        _viewModel = new MainViewModel(_orchestrator);
        DataContext = _viewModel;

        Loaded += async (s, e) =>
        {
            await _viewModel.InitializeAsync();
        };

        Closed += (s, e) =>
        {
            _orchestrator.Dispose();
        };
    }
}

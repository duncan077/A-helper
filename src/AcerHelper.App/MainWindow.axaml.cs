// SPDX-License-Identifier: GPL-3.0-or-later

using AcerHelper.App.ViewModels;
using Avalonia.Controls;

namespace AcerHelper.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    // Parameterless constructor exists only for the XAML previewer.
    public MainWindow() : this(new MainViewModel()) { }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Hide rather than close, so the poll loop and any engaged fan guard
        // keep running. App.ShutdownRequested performs the real teardown.
        if (_viewModel.MinimiseToTray && !e.IsProgrammatic)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }
}

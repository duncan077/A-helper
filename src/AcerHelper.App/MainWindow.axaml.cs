// SPDX-License-Identifier: GPL-3.0-or-later

using AcerHelper.App.ViewModels;
using Avalonia.Controls;

namespace AcerHelper.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel();
        DataContext = _viewModel;
    }

    protected override void OnClosed(EventArgs e)
    {
        // Disposing the view model releases the fan guard, which returns the
        // fans to EC control. Skipping this would leave them in Custom mode.
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}

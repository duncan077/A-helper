// SPDX-License-Identifier: GPL-3.0-or-later
//
// The view model is owned here, not by MainWindow.
//
// With minimise-to-tray the window is closed and recreated freely, but the
// hardware channels, the fan guard and the poll loop must survive that. Tying
// their lifetime to a window would drop fan control the first time the user
// clicked the X - and worse, would leave the fans in Custom mode, since the
// guard's release runs on dispose.

using System.ComponentModel;
using AcerHelper.App.ViewModels;
using AcerHelper.Hardware;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

namespace AcerHelper.App;

public partial class App : Application
{
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _profileHeader;
    private NativeMenu? _menu;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing the window hides it; only the tray's Exit really quits.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _viewModel = new MainViewModel();
            _window = new MainWindow(_viewModel);
            desktop.MainWindow = _window;
            _window.Show();

            SetUpTray(desktop);

            desktop.ShutdownRequested += (_, _) =>
            {
                _trayIcon?.Dispose();
                _viewModel?.Dispose();   // releases the fan guard, restoring Auto
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetUpTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://AcerHelper.App/Assets/tray.ico"));

            _menu = new NativeMenu();

            var show = new NativeMenuItem("Show AcerHelper");
            show.Click += (_, _) => ShowWindow();
            _menu.Add(show);
            _menu.Add(new NativeMenuItemSeparator());

            _profileHeader = new NativeMenuItem("Thermal profile") { Menu = new NativeMenu() };
            _menu.Add(_profileHeader);

            var coolBoost = new NativeMenuItem("Toggle CoolBoost");
            coolBoost.Click += (_, _) => _viewModel?.ToggleCoolBoostCommand.Execute(null);
            _menu.Add(coolBoost);

            _menu.Add(new NativeMenuItemSeparator());

            var exit = new NativeMenuItem("Exit");
            exit.Click += (_, _) => desktop.Shutdown();
            _menu.Add(exit);

            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(stream),
                ToolTipText = "AcerHelper",
                IsVisible = true,
                Menu = _menu,
            };
            _trayIcon.Clicked += (_, _) => ShowWindow();

            TrayIcon.SetIcons(this, [_trayIcon]);

            if (_viewModel is not null)
            {
                // The supported set is discovered asynchronously, so the menu is
                // built from whatever the firmware actually reports.
                _viewModel.SupportedProfiles.CollectionChanged += (_, _) => RebuildProfileMenu();
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
                RebuildProfileMenu();
            }
        }
        catch (Exception ex)
        {
            // A missing tray is a degraded app, not a dead one.
            Diagnostics.WriteException("tray setup", ex);
        }
    }

    private void RebuildProfileMenu()
    {
        if (_profileHeader?.Menu is not { } submenu || _viewModel is null) return;

        submenu.Items.Clear();
        foreach (var profile in _viewModel.SupportedProfiles)
        {
            var captured = profile;
            var item = new NativeMenuItem(profile.ToString());
            item.Click += (_, _) => _viewModel.SetProfileCommand.Execute(captured);
            submenu.Add(item);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_trayIcon is null || _viewModel is null) return;

        // Keep the hover tooltip useful without opening the window.
        if (e.PropertyName is nameof(MainViewModel.CpuTemp)
                           or nameof(MainViewModel.CurrentProfileName)
                           or nameof(MainViewModel.PowerSourceText))
        {
            _trayIcon.ToolTipText =
                $"AcerHelper — {_viewModel.CurrentProfileName}\n"
                + $"CPU {_viewModel.CpuTemp} · {_viewModel.PowerSourceText}";
        }
    }

    private void ShowWindow()
    {
        if (_window is null) return;

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }
}

using GseTechPro.App.ViewModels;
using GseTechPro.Data;
using GseTechPro.Devices.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GseTechPro.App;

public sealed partial class MainWindow : Window
{
    private ResourceDictionary? _sunlightDictionary;

    public MainWindow(DataServices dataServices, IDiagnosticAdapterFactory adapterFactory)
    {
        ViewModel = new MainShellViewModel(dataServices, adapterFactory, DispatcherQueue);
        InitializeComponent();

        // Hand the view model the live XamlRoot once loaded so it can host confirmation
        // and error dialogs (e.g. the Clear DTCs confirmation).
        RootGrid.Loaded += (_, _) => ViewModel.DialogXamlRoot = RootGrid.XamlRoot;

        RootGrid.ActualThemeChanged += (_, _) => SyncThemeStateToViewModel();
        SyncThemeStateToViewModel();
    }

    public MainShellViewModel ViewModel { get; }

    public Task InitializeShellAsync() => ViewModel.InitializeAsync();

    private void OnToolbarButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string actionKey })
        {
            ViewModel.SelectToolbarAction(actionKey);
        }
    }

    private async void OnNavRailButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string navKey })
            return;

        try
        {
            await ViewModel.SelectNavRailItem(navKey);
        }
        catch (Exception ex)
        {
            ViewModel.ReportError("Navigation / refresh failed", ex);
        }
    }

    private void OnBreadcrumbRootClick(object sender, RoutedEventArgs e) =>
        ViewModel.SelectToolbarAction("Assets");

    private void OnNewWorkOrderMenuClick(object sender, RoutedEventArgs e) =>
        ViewModel.SelectToolbarAction("New");

    private void OnOpenWorkOrdersMenuClick(object sender, RoutedEventArgs e) =>
        ViewModel.SelectToolbarAction("WorkOrders");

    private void OnOpenAssetsMenuClick(object sender, RoutedEventArgs e) =>
        ViewModel.SelectToolbarAction("Assets");

    private async void OnReloadDbMenuClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.ReloadDatabaseRecordsAsync();
        }
        catch (Exception ex)
        {
            ViewModel.ReportError("Reload database failed", ex);
        }
    }

    private void OnNotesMenuClick(object sender, RoutedEventArgs e) =>
        ViewModel.SelectToolbarAction("Notes");

    private async void OnConnectSimulatorClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.ConnectSimulatorAsync();
        }
        catch (Exception ex)
        {
            ViewModel.ReportError("Connect simulator failed", ex);
        }
    }

    private async void OnDisconnectSimulatorClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.DisconnectSimulatorAsync();
        }
        catch (Exception ex)
        {
            ViewModel.ReportError("Disconnect simulator failed", ex);
        }
    }

    private async void OnRefreshDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.RefreshDiagnosticsAsync();
        }
        catch (Exception ex)
        {
            ViewModel.ReportError("Read DTCs failed", ex);
        }
    }

    private async void OnClearDtcsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.ClearDtcsAsync();
        }
        catch (Exception ex)
        {
            ViewModel.ReportError("Clear DTCs failed", ex);
        }
    }

    private void OnToggleUnitsMenuClick(object sender, RoutedEventArgs e) =>
        ViewModel.ToggleUnitSystem();

    private void OnFollowSystemThemeClick(object sender, RoutedEventArgs e)
    {
        SetSunlightMode(false);
        RootGrid.RequestedTheme = ElementTheme.Default;
        SyncThemeStateToViewModel();
    }

    private void OnLightThemeClick(object sender, RoutedEventArgs e)
    {
        SetSunlightMode(false);
        RootGrid.RequestedTheme = ElementTheme.Light;
        SyncThemeStateToViewModel();
    }

    private void OnDarkThemeClick(object sender, RoutedEventArgs e)
    {
        SetSunlightMode(false);
        RootGrid.RequestedTheme = ElementTheme.Dark;
        SyncThemeStateToViewModel();
    }

    private void OnToggleSunlightMenuClick(object sender, RoutedEventArgs e)
    {
        var nextState = !(SunlightToggleButton.IsChecked ?? false);
        SetSunlightMode(nextState);
    }

    private void OnSunlightToggleButtonClick(object sender, RoutedEventArgs e)
    {
        var isChecked = SunlightToggleButton.IsChecked ?? false;
        SetSunlightMode(isChecked);
    }

    private void SetSunlightMode(bool enabled)
    {
        SunlightToggleButton.IsChecked = enabled;

        if (enabled)
        {
            if (_sunlightDictionary is null)
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Themes", "SunlightTheme.xaml");
                _sunlightDictionary = (ResourceDictionary)Microsoft.UI.Xaml.Markup.XamlReader.Load(File.ReadAllText(path));
            }

            if (!RootGrid.Resources.MergedDictionaries.Contains(_sunlightDictionary))
            {
                RootGrid.Resources.MergedDictionaries.Add(_sunlightDictionary);
            }

            RootGrid.RequestedTheme = ElementTheme.Light;
        }
        else
        {
            if (_sunlightDictionary is not null)
            {
                RootGrid.Resources.MergedDictionaries.Remove(_sunlightDictionary);
            }
            RootGrid.RequestedTheme = ElementTheme.Default;
        }

        SyncThemeStateToViewModel();
    }

    private void SyncThemeStateToViewModel()
    {
        var isSunlight = SunlightToggleButton?.IsChecked ?? false;
        var isDark = !isSunlight && RootGrid.ActualTheme == ElementTheme.Dark;
        ViewModel.UpdateThemeState(isDark, isSunlight);
    }

    private async void OnAboutMenuClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "GSE Tech Pro — Offline Diagnostic Suite",
            Content = "Ground Support Equipment & Heavy-Duty Fleet Diagnostics.\n" +
                      "by Commercial Truck and Auto\n\n" +
                      "Phase 1 Shell: WinUI 3 (Windows App SDK), Local SQLite + Simulator.",
            CloseButtonText = "Close",
            XamlRoot = RootGrid.XamlRoot,
        };
        await dialog.ShowAsync();
    }
}

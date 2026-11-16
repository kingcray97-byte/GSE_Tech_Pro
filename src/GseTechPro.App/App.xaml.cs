using GseTechPro.Data;
using GseTechPro.Devices;
using GseTechPro.Devices.Abstractions;
using Microsoft.UI.Xaml;

namespace GseTechPro.App;

/// <summary>
/// Application composition root for GSE Tech Pro (Phase 1).
/// Wires <see cref="DataServices"/> (SQLite repositories) and <see cref="DiagnosticAdapterFactory"/>
/// (simulator and future 32-bit device host adapters) into the main shell window.
/// </summary>
public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        DataServices = new DataServices();
        AdapterFactory = new DiagnosticAdapterFactory();
    }

    public static new App Current => (App)Application.Current;

    public DataServices DataServices { get; }

    public IDiagnosticAdapterFactory AdapterFactory { get; }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(DataServices, AdapterFactory);
        _window.Activate();
        await _window.InitializeShellAsync();
    }
}

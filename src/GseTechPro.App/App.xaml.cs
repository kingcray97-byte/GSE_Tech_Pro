using GseTechPro.App.Services;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Data;
using GseTechPro.Devices;
using GseTechPro.Devices.Abstractions;
using GseTechPro.Scope;
using Microsoft.UI.Xaml;

namespace GseTechPro.App;

/// <summary>
/// Application composition root for GSE Tech Pro (Phases 1–6).
/// Wires <see cref="DataServices"/> (SQLite repositories), <see cref="DiagnosticAdapterFactory"/>
/// (simulator and 32-bit device host adapters), <see cref="ScopeDeviceFactory"/> (in-process x64
/// oscilloscope simulator, PicoScope, and Hantek 6074BE per ADR-011), and <see cref="AiServiceClient"/> /
/// <see cref="AiServiceLauncher"/> (local FastAPI AI service over 127.0.0.1 HTTP per ADR-008) into the main shell window.
/// </summary>
public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        DataServices = new DataServices();
        AdapterFactory = new DiagnosticAdapterFactory();
        ScopeDeviceFactory = new ScopeDeviceFactory();
        var oemModules = new OemModuleRegistry();
        // Specific OEM modules register before this; the generic fallback is always last.
        oemModules.Register(new GenericOemModule());
        OemModules = oemModules;
        AiLauncher = new AiServiceLauncher();
        AiClient = new AiServiceClient(
            new AiServiceConfig { BaseUrl = AiLauncher.BaseUrl },
            AiLauncher.Token);
    }

    public static new App Current => (App)Application.Current;

    public DataServices DataServices { get; }

    public IDiagnosticAdapterFactory AdapterFactory { get; }

    public IScopeDeviceFactory ScopeDeviceFactory { get; }

    public IOemModuleRegistry OemModules { get; }

    public AiServiceLauncher AiLauncher { get; }

    public IAiClient AiClient { get; }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(DataServices, AdapterFactory, AiClient, AiLauncher, ScopeDeviceFactory, OemModules);
        _window.Activate();
        await _window.InitializeShellAsync();
    }
}

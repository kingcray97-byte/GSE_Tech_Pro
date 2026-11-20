using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GseTechPro.Core.Models;
using Microsoft.UI.Xaml;

namespace GseTechPro.App.ViewModels;

/// <summary>
/// Phase 10B.3: the shared diagnostic module frame (Shop Foreman Pro's diagnostic screens).
/// Scan (Diagnostics / J2534 / Heavy Duty / OBD II), Scope, CAN Capture and the guided
/// troubleshooting pages all get the same frame: the navy module menu on the left, a
/// breadcrumb whose first part links back to Equipment Selection, and a connection strip
/// ("No Equipment Connection") under it.
/// </summary>
public sealed partial class MainShellViewModel
{
    /// <summary>Pages outside the Scan / Scope / CAN modules that still use the diagnostic frame.</summary>
    private static readonly HashSet<string> GuidedFramePages = new(StringComparer.Ordinal) { "GuidedTree", "Readings" };

    /// <summary>Properties that change what the frame shows.</summary>
    private static readonly HashSet<string> ModuleFrameInputs = new(StringComparer.Ordinal)
    {
        nameof(ActiveTool),
        nameof(ActiveSectionKey),
        nameof(IsConnected),
        nameof(ConnectionStatusText),
        nameof(AdapterDeviceName),
        nameof(IsScopeConnected),
        nameof(IsScopeSimulated),
        nameof(ScopeDeviceName),
        nameof(ActiveWorkOrder),
        nameof(IsCanMonitoring),
        nameof(IsCanSimulated),
        nameof(CanMonitorName),
    };

    /// <summary>True on screens that use the diagnostic module frame.</summary>
    public bool IsModuleFrameVisible =>
        CurrentSidebarSectionKey is "Diagnostics" or "Scope" or "CanCapture"
        || GuidedFramePages.Contains(ActiveSectionKey);

    public Visibility ModuleFrameVisibility => IsModuleFrameVisible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The Scope module reports the scope; every other module reports the diagnostic adapter.</summary>
    public bool IsModuleConnected => CurrentSidebarSectionKey switch
    {
        "Scope" => IsScopeConnected,
        "CanCapture" => IsCanMonitoring,
        _ => IsConnected,
    };

    public Visibility ModuleConnectedVisibility => IsModuleConnected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ModuleDisconnectedVisibility => IsModuleConnected ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Connection strip text, e.g. "No Equipment Connection" or "Connected (SIMULATED) · Simulator".</summary>
    public string ModuleConnectionText
    {
        get
        {
            if (CurrentSidebarSectionKey == "CanCapture")
                return CanModuleConnectionText;

            if (CurrentSidebarSectionKey == "Scope")
            {
                if (!IsScopeConnected)
                    return "No Scope Connection";
                return IsScopeSimulated
                    ? $"Scope connected (SIMULATED) · {ScopeDeviceName}"
                    : $"Scope connected (REAL HARDWARE) · {ScopeDeviceName}";
            }

            return IsConnected
                ? $"{ConnectionStatusText} · {AdapterDeviceName}"
                : "No Equipment Connection";
        }
    }

    /// <summary>Right side of the connection strip: the work order readings and captures are saved to.</summary>
    public string ModuleWorkOrderText => ActiveWorkOrder is WorkOrder wo
        ? $"Saving to {WorkOrderBrowserRowViewModel.FormatWorkOrderNumber(wo)}"
        : "No open work order";

    /// <summary>The breadcrumb's first part (SFP's "Vehicle Selection" link) opens Equipment Selection.</summary>
    [RelayCommand]
    private void OpenBreadcrumbRoot() => SelectToolbarAction("Assets");

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is null || !ModuleFrameInputs.Contains(e.PropertyName))
            return;

        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsModuleFrameVisible)));
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ModuleFrameVisibility)));
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsModuleConnected)));
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ModuleConnectedVisibility)));
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ModuleDisconnectedVisibility)));
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ModuleConnectionText)));
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ModuleWorkOrderText)));
    }
}

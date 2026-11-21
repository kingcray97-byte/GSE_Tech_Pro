using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;

namespace GseTechPro.App.ViewModels;

/// <summary>
/// Scope full screen (Phase 10D.1j): the window goes full screen and the app chrome (menu bar,
/// icon toolbar, work order tabs, module menu, page header, status bar) and the scope's left
/// channel/tool column are hidden, so the trace gets the whole screen. The scope toolbar (Run/Stop,
/// timebase, trigger, buffer), the safety warnings and the status line always stay visible.
/// Exit with the toolbar button, Esc or F11; leaving the Scope pages exits too.
/// MainWindow switches the window presenter when <see cref="IsScopeFullScreen"/> changes.
/// </summary>
public sealed partial class MainShellViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AppChromeVisibility))]
    [NotifyPropertyChangedFor(nameof(ModuleNavVisibility))]
    [NotifyPropertyChangedFor(nameof(TabStripVisibility))]
    [NotifyPropertyChangedFor(nameof(ScopeSidePanelVisibility))]
    [NotifyPropertyChangedFor(nameof(ScopeSidePanelWidth))]
    [NotifyPropertyChangedFor(nameof(ScopeFullScreenButtonText))]
    [NotifyPropertyChangedFor(nameof(ScopeFullScreenGlyph))]
    private bool _isScopeFullScreen;

    /// <summary>Menu bar, toolbar, page header, transport bar and status bar.</summary>
    public Visibility AppChromeVisibility =>
        IsScopeFullScreen ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Channel tiles and tool palette column on the scope screen.</summary>
    public Visibility ScopeSidePanelVisibility =>
        IsScopeFullScreen ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Width of the scope's left column (0 in full screen so the trace uses it).</summary>
    public GridLength ScopeSidePanelWidth =>
        IsScopeFullScreen ? new GridLength(0) : new GridLength(200);

    public string ScopeFullScreenButtonText => IsScopeFullScreen ? "Exit full screen" : "Full screen";

    /// <summary>Segoe Fluent: FullScreen / BackToWindow.</summary>
    public string ScopeFullScreenGlyph => IsScopeFullScreen ? "" : "";

    /// <summary>Enters full screen (only from a Scope page) or leaves it.</summary>
    [RelayCommand]
    public void ToggleScopeFullScreen()
    {
        if (IsScopeFullScreen)
            IsScopeFullScreen = false;
        else if (IsScopeViewVisible)
            IsScopeFullScreen = true;
    }

    /// <summary>Leaves full screen (Esc); returns whether it was on, so the key can be marked handled.</summary>
    public bool ExitScopeFullScreen()
    {
        if (!IsScopeFullScreen)
            return false;
        IsScopeFullScreen = false;
        return true;
    }

    /// <summary>Full screen belongs to the scope: leaving its pages restores the normal window.</summary>
    private void SyncScopeFullScreenToSection()
    {
        if (IsScopeFullScreen && !IsScopeViewVisible)
            IsScopeFullScreen = false;
    }
}

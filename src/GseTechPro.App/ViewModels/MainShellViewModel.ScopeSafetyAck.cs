using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;

namespace GseTechPro.App.ViewModels;

/// <summary>
/// Safety warning acknowledgement on the scope screen (Phase 10D.2 follow-up, rule 6). A guided
/// test's warnings are always shown in full first. Only after the technician taps "I have read
/// these warnings" does the banner shrink to a one-line summary, which stays on screen (never
/// hidden) and can be opened again. Choosing a test resets it, so new warnings are shown in full.
/// Acknowledging never skips or changes a warning or a procedure.
/// </summary>
public sealed partial class MainShellViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeSafetyWarningsFullVisibility))]
    [NotifyPropertyChangedFor(nameof(ScopeSafetyWarningsSummaryVisibility))]
    private bool _areScopeSafetyWarningsAcknowledged;

    [ObservableProperty]
    private string _scopeSafetyAcknowledgedText = "";

    /// <summary>Full warning list and the acknowledge button: until acknowledged.</summary>
    public Visibility ScopeSafetyWarningsFullVisibility =>
        HasScopeSafetyWarnings && !AreScopeSafetyWarningsAcknowledged ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>One-line summary with "Show warnings": after acknowledging.</summary>
    public Visibility ScopeSafetyWarningsSummaryVisibility =>
        HasScopeSafetyWarnings && AreScopeSafetyWarningsAcknowledged ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The technician confirms they have read the warnings; the banner shrinks to a summary.</summary>
    [RelayCommand]
    public void AcknowledgeScopeSafetyWarnings()
    {
        if (!HasScopeSafetyWarnings)
            return;

        int n = ScopeSafetyWarnings.Count;
        string test = SelectedGuidedTest?.Name ?? _scopeOneTapSafetyTestName ?? "this test";
        string who = string.IsNullOrWhiteSpace(SelectedTechnician) ? "the technician" : SelectedTechnician;
        string at = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
        ScopeSafetyAcknowledgedText =
            $"{n} safety warning{(n == 1 ? "" : "s")} for '{test}' read by {who} at {at}. Keep following them while you work.";
        AreScopeSafetyWarningsAcknowledged = true;

        // The one-tap panel still asked to read the warnings (found in the app check); move it on to the next step.
        if (SelectedGuidedTest is null && _scopeOneTapSafetyTestName is { } oneTap
            && ScopeOneTapStatusText.StartsWith($"{oneTap}: read the ", StringComparison.Ordinal))
            ScopeOneTapStatusText = $"{oneTap}: safety warnings read. Enter what you know, then tap Start.";
    }

    /// <summary>Opens the full warning list again.</summary>
    [RelayCommand]
    public void ShowScopeSafetyWarnings() => AreScopeSafetyWarningsAcknowledged = false;

    partial void OnHasScopeSafetyWarningsChanged(bool value)
    {
        OnPropertyChanged(nameof(ScopeSafetyWarningsFullVisibility));
        OnPropertyChanged(nameof(ScopeSafetyWarningsSummaryVisibility));
    }

    /// <summary>Status text when a capture is refused because the warnings are not yet read.</summary>
    public const string ScopeSafetyBlockedMessage =
        "Read the safety warnings above and tap 'I have read these warnings' before capturing.";

    /// <summary>
    /// True (and says why on the status line) when the selected guided test has safety warnings the
    /// technician has not yet acknowledged. Run, repeat run, Run guided test &amp; compare and Auto setup
    /// all check this before they touch the probe. Manual setups with no guided test have no
    /// warnings list, so they are not blocked.
    /// </summary>
    public bool IsScopeCaptureBlockedBySafety()
    {
        if (!HasScopeSafetyWarnings || AreScopeSafetyWarningsAcknowledged)
            return false;

        ScopeStatusMessage = ScopeSafetyBlockedMessage;
        StatusBannerMessage = ScopeSafetyBlockedMessage;
        return true;
    }

    /// <summary>A newly chosen test always shows its warnings in full.</summary>
    private void ResetScopeSafetyAcknowledgement()
    {
        AreScopeSafetyWarningsAcknowledged = false;
        ScopeSafetyAcknowledgedText = "";
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GseTechPro.Scope;

namespace GseTechPro.App.ViewModels;

/// <summary>
/// Capture notes (Phase 10D.1i): free text the technician writes about the capture (what was
/// connected, engine state, what was seen). Kept across Runs, saved with Save to WO in
/// <c>waveforms.notes</c>, and restored when a saved capture is opened. Display text only.
/// </summary>
public sealed partial class MainShellViewModel
{
    /// <summary>Longest note the Notes box accepts.</summary>
    public int ScopeNotesMaxLength => CaptureNotes.MaxLength;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeNotesCountText))]
    [NotifyPropertyChangedFor(nameof(HasScopeNotes))]
    private string _scopeNotes = "";

    public bool HasScopeNotes => CaptureNotes.Normalize(ScopeNotes) is not null;

    /// <summary>"120 / 2000 characters".</summary>
    public string ScopeNotesCountText =>
        $"{(ScopeNotes ?? "").Length} / {CaptureNotes.MaxLength} characters";

    [RelayCommand]
    public void ClearScopeNotes() => ScopeNotes = "";
}

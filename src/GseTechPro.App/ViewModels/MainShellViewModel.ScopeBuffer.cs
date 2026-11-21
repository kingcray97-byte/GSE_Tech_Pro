using CommunityToolkit.Mvvm.Input;
using GseTechPro.Core.Contracts;
using GseTechPro.Scope;

namespace GseTechPro.App.ViewModels;

/// <summary>
/// Phase 10D.1 — scope waveform buffer (PicoScope 7 "Waveform buffer n of 64"): every capture is kept with its
/// measurements and reference comparison, and the top-bar stepper shows any of them again (render, rulers,
/// measurements, Save to WO all follow the capture on screen). Bounded by count and memory
/// (<see cref="WaveformBuffer{T}"/>).
/// </summary>
public sealed partial class MainShellViewModel
{
    /// <summary>One buffered capture with the results computed when it was taken.</summary>
    public sealed record BufferedScopeCapture(ScopeCapture Capture, ScopeComparisonResult Comparison, bool IsSimulated);

    private readonly WaveformBuffer<BufferedScopeCapture> _scopeBuffer = new();

    /// <summary>Top-bar text, e.g. "3 / 12" (or "— / 64" when empty).</summary>
    public string ScopeBufferPositionText => _scopeBuffer.IsEmpty
        ? $"— / {_scopeBuffer.Capacity}"
        : _scopeBuffer.PositionText;

    /// <summary>Tooltip / status line for the buffer tile.</summary>
    public string ScopeBufferDetailText => _scopeBuffer.IsEmpty
        ? $"Waveform buffer: empty (keeps the last {_scopeBuffer.Capacity} captures)"
        : $"Waveform buffer: capture {_scopeBuffer.Position + 1} of {_scopeBuffer.Count}" +
          (_scopeBuffer.CanStepForward ? " (older capture on screen; Run adds a new one at the end)" : " (newest)") +
          $" · {_scopeBuffer.BytesHeld / (1024.0 * 1024.0):0.#} MB of {_scopeBuffer.MemoryBudgetBytes / (1024 * 1024)} MB";

    public int ScopeBufferCount => _scopeBuffer.Count;

    public int ScopeBufferPosition => _scopeBuffer.Position;

    /// <summary>Stores a finished capture as the newest buffer entry (called from the capture path).</summary>
    private void PushScopeBuffer(ScopeCapture capture, ScopeComparisonResult comparison, bool isSimulated)
    {
        _scopeBuffer.Add(
            new BufferedScopeCapture(capture, comparison, isSimulated),
            WaveformBuffer<BufferedScopeCapture>.EstimateCaptureBytes(capture.Channels.Count, capture.SampleCount));
        RaiseScopeBufferChanged();
    }

    [RelayCommand(CanExecute = nameof(CanScopeBufferStepBack))]
    public async Task ScopeBufferPreviousAsync()
    {
        if (_scopeBuffer.StepBack())
            await ShowScopeBufferEntryAsync();
    }

    [RelayCommand(CanExecute = nameof(CanScopeBufferStepForward))]
    public async Task ScopeBufferNextAsync()
    {
        if (_scopeBuffer.StepForward())
            await ShowScopeBufferEntryAsync();
    }

    [RelayCommand]
    public async Task ScopeBufferFirstAsync()
    {
        if (_scopeBuffer.MoveFirst())
            await ShowScopeBufferEntryAsync();
    }

    [RelayCommand]
    public async Task ScopeBufferLastAsync()
    {
        if (_scopeBuffer.MoveLast())
            await ShowScopeBufferEntryAsync();
    }

    [RelayCommand]
    public void ClearScopeBuffer()
    {
        _scopeBuffer.Clear();
        RaiseScopeBufferChanged();
        ScopeStatusMessage = "Waveform buffer cleared. The capture on screen is kept until the next Run.";
    }

    private bool CanScopeBufferStepBack() => _scopeBuffer.CanStepBack && !IsScopeCapturing;

    private bool CanScopeBufferStepForward() => _scopeBuffer.CanStepForward && !IsScopeCapturing;

    private async Task ShowScopeBufferEntryAsync()
    {
        var entry = _scopeBuffer.Current;
        if (entry is null)
            return;

        IsScopeSimulated = entry.IsSimulated;
        LastScopeCapture = entry.Capture;
        // The verdicts are the ones computed when this capture was taken (against the reference active then).
        ApplyScopeComparisonResult(entry.Comparison, ActiveReferenceExpectations);
        await RefreshScopeWaveformRenderAsync();
        RaiseScopeBufferChanged();
        ScopeStatusMessage = $"Showing buffered capture {_scopeBuffer.PositionText} · {ScopeOverallVerdictDisplay}.";
    }

    private void RaiseScopeBufferChanged()
    {
        OnPropertyChanged(nameof(ScopeBufferPositionText));
        OnPropertyChanged(nameof(ScopeBufferDetailText));
        OnPropertyChanged(nameof(ScopeBufferCount));
        OnPropertyChanged(nameof(ScopeBufferPosition));
        ScopeBufferPreviousCommand.NotifyCanExecuteChanged();
        ScopeBufferNextCommand.NotifyCanExecuteChanged();
    }
}

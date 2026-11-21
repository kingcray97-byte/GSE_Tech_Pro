using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GseTechPro.Core.Contracts;
using GseTechPro.Scope;

namespace GseTechPro.App.ViewModels;

/// <summary>
/// Phase 10D.1 — Auto setup on the scope top bar: one probe capture at the widest range with no trigger, then
/// <see cref="AutoSetup.Suggest"/> sets each enabled channel's range and, when a repeating signal is found, the
/// timebase, sample rate and a rising trigger at its mid level; then a normal Run shows the result.
/// Device I/O and the analysis run off the UI thread.
/// </summary>
public sealed partial class MainShellViewModel
{
    /// <summary>Simulator only: the signal Auto setup measured, kept for later Runs until a guided test or preset is chosen.</summary>
    private SimulatedSignalPreset? _scopeAutoSetupPinnedPreset;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunScopeAutoSetupCommand))]
    private bool _isScopeAutoSetupRunning;

    /// <summary>Last Auto setup result (for tests and the status line).</summary>
    public AutoSetupResult? LastScopeAutoSetup { get; private set; }

    partial void OnSelectedSimulatorPresetChanged(SimulatedSignalPreset value) => _scopeAutoSetupPinnedPreset = null;

    private bool CanRunScopeAutoSetup() => !IsScopeAutoSetupRunning;

    [RelayCommand(CanExecute = nameof(CanRunScopeAutoSetup))]
    public async Task RunScopeAutoSetupAsync()
    {
        if (IsScopeAutoSetupRunning)
            return;
        if (IsScopeCapturing)
            await StopScopeCaptureAsync();

        IsScopeAutoSetupRunning = true;
        try
        {
            if (_activeScopeDevice is null || !_activeScopeDevice.IsConnected)
                await OpenSelectedScopeDeviceAsync();
            var device = _activeScopeDevice
                ?? throw new InvalidOperationException("No scope device is open.");

            var current = BuildScopeCaptureRequest();
            ConfigureSimulatorPresetFor(device, current);
            if (device is SimulatedScopeDevice sim && SelectedGuidedTest is null)
                _scopeAutoSetupPinnedPreset = sim.CurrentPreset;

            var ranges = ScopeChannelSetupViewModel.StandardVoltageRanges;
            var timebases = ScopeTimebaseOptions.Select(o => o.SecondsPerDivision).ToArray();
            var rates = ScopeSampleRateOptions.Select(o => o.SampleRateHz).ToArray();
            var probe = AutoSetup.BuildProbeRequest(current, ranges, rates);

            ScopeStatusMessage = "Auto setup: measuring the signal at the widest range…";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var ct = cts.Token;
            var result = await Task.Run(async () =>
            {
                foreach (var ch in probe.Channels)
                    await device.ConfigureChannelAsync(ch.Channel, ch, ct).ConfigureAwait(false);
                await device.StartCaptureAsync(probe, ct).ConfigureAwait(false);
                var cap = await device.ReadCaptureAsync(ct).ConfigureAwait(false);
                return AutoSetup.Suggest(cap, ranges, timebases, rates);
            }, ct);

            ApplyScopeAutoSetup(result);
            LastScopeAutoSetup = result;
        }
        catch (Exception ex)
        {
            ReportError("Scope auto setup failed", ex);
            ScopeStatusMessage = $"Auto setup failed: {ex.Message}";
            IsScopeAutoSetupRunning = false;
            return;
        }

        IsScopeAutoSetupRunning = false;
        await StartScopeCaptureAsync();
        if (LastScopeAutoSetup is { } done)
        {
            ScopeStatusMessage = done.Summary;
            StatusBannerMessage = done.Summary;
        }
    }

    /// <summary>Applies an Auto setup result to the channel tiles, timebase, sample rate and trigger.</summary>
    public void ApplyScopeAutoSetup(AutoSetupResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        foreach (var ch in result.Channels)
        {
            var tile = ScopeChannels.FirstOrDefault(c => c.Channel == ch.Channel);
            if (tile is not null)
                tile.VoltageRangeVolts = ch.RangeVolts;
        }

        if (result.TimebaseSecondsPerDivision is double tb)
            ScopeTimebaseSecondsPerDivision = tb;
        if (result.SampleRateHz is int rate)
            ScopeSampleRateHz = rate;
        if (result.TriggerChannel is int trigCh && result.TriggerLevelVolts is double level)
        {
            ScopeTriggerMode = ScopeTriggerMode.Rising;
            ScopeTriggerChannel = trigCh;
            ScopeTriggerLevelVolts = level;
        }
    }
}

using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GseTechPro.Core.Contracts;
using GseTechPro.Scope;

namespace GseTechPro.App.ViewModels;

/// <summary>
/// Phase 10D.1 — repeat run and mask-fail Actions (PicoScope 7 "Actions" workflow, our own implementation).
/// With Repeat on, Run keeps capturing until Stop. When a new capture fails the mask, the chosen actions run:
/// beep, save the failing capture to the work order (at most <see cref="MaxScopeActionSavesPerRun"/> per run),
/// and stop a repeat run so the failing capture stays on screen.
/// </summary>
public sealed partial class MainShellViewModel
{
    /// <summary>Upper bound on captures saved by the Save action in one Run, so a bad connection cannot fill the disk.</summary>
    public const int MaxScopeActionSavesPerRun = 20;

    /// <summary>Pause between repeat captures so the screen and touch input stay responsive.</summary>
    private static readonly TimeSpan ScopeRepeatGap = TimeSpan.FromMilliseconds(50);

    private CancellationTokenSource? _scopeRepeatCts;
    private int _scopeActionSavesThisRun;

    [ObservableProperty]
    private bool _isScopeRepeatEnabled;

    [ObservableProperty]
    private bool _isScopeRepeatRunning;

    [ObservableProperty]
    private bool _scopeActionBeepOnFail;

    [ObservableProperty]
    private bool _scopeActionSaveOnFail;

    [ObservableProperty]
    private bool _scopeActionStopOnFail = true;

    [ObservableProperty]
    private string _scopeActionsStatusText = "No actions taken.";

    /// <summary>Plays the alarm sound. Replaceable for tests.</summary>
    public Action ScopeAlarmSound { get; set; } = PlayAlarmSound;

    /// <summary>Number of captures run by the last Run (1 for a single capture).</summary>
    public int LastScopeRunCaptureCount { get; private set; }

    partial void OnIsScopeRepeatRunningChanged(bool value) => OnIsScopeCapturingChanged(IsScopeCapturing);

    /// <summary>Run button: one capture, or with Repeat on, captures until Stop or a stop-on-fail action.</summary>
    [RelayCommand]
    public async Task RunScopeAsync()
    {
        if (IsScopeCapturing || IsScopeRepeatRunning)
            return;

        _scopeActionSavesThisRun = 0;
        if (!IsScopeRepeatEnabled)
        {
            LastScopeRunCaptureCount = 1;
            await StartScopeCaptureAsync();
            return;
        }

        _scopeRepeatCts?.Dispose();
        _scopeRepeatCts = new CancellationTokenSource();
        var ct = _scopeRepeatCts.Token;
        IsScopeRepeatRunning = true;
        LastScopeRunCaptureCount = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var before = LastScopeCapture;
                await StartScopeCaptureAsync();
                if (LastScopeCapture is null || ReferenceEquals(before, LastScopeCapture))
                    break; // capture failed or was stopped: do not spin on errors
                LastScopeRunCaptureCount++;
                if (ct.IsCancellationRequested)
                    break;
                await Task.Delay(ScopeRepeatGap, ct).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsScopeRepeatRunning = false;
        }
    }

    /// <summary>Ends a repeat run after the capture in progress.</summary>
    public void CancelScopeRepeat()
    {
        try { _scopeRepeatCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Called once per new capture that fails the mask.</summary>
    private async Task RunScopeMaskFailActionsAsync(MaskTestResult result)
    {
        var done = new List<string>();
        if (ScopeActionBeepOnFail)
        {
            try { ScopeAlarmSound(); } catch { /* sound is best effort */ }
            done.Add("beeped");
        }

        if (ScopeActionSaveOnFail)
        {
            if (_scopeActionSavesThisRun < MaxScopeActionSavesPerRun)
            {
                var saved = await SaveScopeCaptureToWorkOrderAsync();
                if (saved is not null)
                {
                    _scopeActionSavesThisRun++;
                    done.Add($"saved to the work order ({_scopeActionSavesThisRun}/{MaxScopeActionSavesPerRun} this run)");
                }
            }
            else
            {
                done.Add($"not saved (limit of {MaxScopeActionSavesPerRun} saves this run reached)");
            }
        }

        if (ScopeActionStopOnFail && IsScopeRepeatRunning)
        {
            CancelScopeRepeat();
            done.Add("stopped the repeat run");
        }

        string time = DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        ScopeActionsStatusText = done.Count == 0
            ? $"{time} mask fail: no actions are on."
            : $"{time} mask fail: {string.Join(", ", done)}.";
        StatusBannerMessage = $"{result.Message} Actions: {ScopeActionsStatusText}";
    }

    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint type);

    private static void PlayAlarmSound() => MessageBeep(0x30); // MB_ICONEXCLAMATION
}

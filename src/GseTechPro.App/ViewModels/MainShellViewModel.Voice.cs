using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GseTechPro.App.Services;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using Microsoft.UI.Xaml;

namespace GseTechPro.App.ViewModels;

/// <summary>
/// Phase 7.5a push-to-talk in Tech Chat: tap Talk, speak, tap again (or it stops itself at <see cref="Voice.MaxSeconds"/>);
/// the clip goes to the AI service's speech to text on the tablet, and the words are put in the question box. Nothing is
/// sent until the technician checks the words and taps Send. The simulated microphone speaks typed text with a Windows
/// voice so the whole path works without a microphone (rule 4).
/// </summary>
public sealed partial class MainShellViewModel
{
    private IMicrophone? _openMicrophone;
    private CancellationTokenSource? _voiceLimitCts;

    public ObservableCollection<MicrophoneInfo> Microphones { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SimulatedVoicePhraseVisibility))]
    private MicrophoneInfo? _selectedMicrophone;

    /// <summary>What the simulated microphone says.</summary>
    [ObservableProperty]
    private string _simulatedVoicePhrase = "What is the main relief valve pressure on the 600i?";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TalkButtonText))]
    [NotifyPropertyChangedFor(nameof(TalkButtonGlyph))]
    [NotifyPropertyChangedFor(nameof(VoiceListeningVisibility))]
    private bool _isVoiceListening;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TalkButtonText))]
    [NotifyCanExecuteChangedFor(nameof(ToggleTalkCommand))]
    private bool _isVoiceTranscribing;

    [ObservableProperty]
    private string _voiceStatusText = "Tap Talk, speak, then tap again. Check the words, then tap Send.";

    public Visibility SimulatedVoicePhraseVisibility =>
        SelectedMicrophone is { IsSimulated: true } ? Visibility.Visible : Visibility.Collapsed;

    public Visibility VoiceListeningVisibility => IsVoiceListening ? Visibility.Visible : Visibility.Collapsed;

    public string TalkButtonText => IsVoiceTranscribing ? "Working…" : IsVoiceListening ? "Stop" : "Talk";

    /// <summary>Microphone, or Stop while recording.</summary>
    public string TalkButtonGlyph => IsVoiceListening ? "" : "";

    public async Task RefreshMicrophonesAsync()
    {
        IReadOnlyList<MicrophoneInfo> found;
        try
        {
            found = await _microphoneFactory.DiscoverAsync();
        }
        catch (Exception ex)
        {
            VoiceStatusText = "Could not list microphones: " + ex.Message;
            return;
        }
        string? keep = SelectedMicrophone?.Id;
        Microphones.Clear();
        foreach (var m in found)
            Microphones.Add(m);
        SelectedMicrophone = Microphones.FirstOrDefault(m => m.Id == keep) ?? Microphones.FirstOrDefault();
    }

    partial void OnSelectedMicrophoneChanged(MicrophoneInfo? value)
    {
        // Close the old microphone; the new one opens on the next Talk.
        var old = _openMicrophone;
        _openMicrophone = null;
        if (IsVoiceListening)
        {
            _voiceLimitCts?.Cancel();
            IsVoiceListening = false;
            VoiceStatusText = "Recording stopped: the microphone was changed.";
        }
        if (old is not null)
            _ = old.DisposeAsync().AsTask();
    }

    private async Task CloseMicrophoneAsync()
    {
        _voiceLimitCts?.Cancel();
        var mic = _openMicrophone;
        _openMicrophone = null;
        if (mic is not null)
            await mic.DisposeAsync();
    }

    private bool CanToggleTalk() => !IsVoiceTranscribing;

    /// <summary>Tap once to start recording, again to stop and transcribe.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleTalk))]
    public async Task ToggleTalkAsync()
    {
        if (IsVoiceListening)
        {
            await StopTalkAndTranscribeAsync();
            return;
        }

        if (Microphones.Count == 0)
            await RefreshMicrophonesAsync();
        var info = SelectedMicrophone;
        if (info is null)
        {
            VoiceStatusText = "No microphone found.";
            return;
        }
        try
        {
            _openMicrophone ??= await _microphoneFactory.OpenAsync(info);
            if (_openMicrophone is ISimulatedMicrophone sim)
                sim.Phrase = SimulatedVoicePhrase;
            await _openMicrophone.StartAsync();
        }
        catch (Exception ex)
        {
            VoiceStatusText = ex.Message;
            return;
        }
        IsVoiceListening = true;
        VoiceStatusText = info.IsSimulated
            ? "Simulated microphone: tap Stop and it will say the typed words."
            : $"Listening… tap Stop when you are done ({Voice.MaxSeconds:0} s at most).";

        _voiceLimitCts?.Cancel();
        var limit = new CancellationTokenSource();
        _voiceLimitCts = limit;
        _ = StopAtTimeLimitAsync(limit.Token);
    }

    private async Task StopAtTimeLimitAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Voice.MaxSeconds), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        void Stop()
        {
            if (IsVoiceListening && !token.IsCancellationRequested)
                _ = StopTalkAndTranscribeAsync();
        }
        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
            _dispatcherQueue.TryEnqueue(Stop);
        else
            Stop();
    }

    private async Task StopTalkAndTranscribeAsync()
    {
        _voiceLimitCts?.Cancel();
        var mic = _openMicrophone;
        IsVoiceListening = false;
        if (mic is null)
            return;

        IsVoiceTranscribing = true;
        try
        {
            if (mic is ISimulatedMicrophone sim)
                sim.Phrase = SimulatedVoicePhrase; // what is in the box when Stop is tapped
            VoiceStatusText = "Writing down what was said…";
            var audio = await mic.StopAsync();
            if (audio.Wav.Length > Voice.MaxAudioBytes)
            {
                VoiceStatusText = "The recording is too large to send. Keep it under 30 seconds.";
                return;
            }
            var response = await _aiClient.TranscribeAsync(new VoiceTranscriptionRequest
            {
                AudioBase64 = Convert.ToBase64String(audio.Wav),
                IsSimulated = audio.IsSimulated,
            });
            string heard = (response.Text ?? "").Trim();
            string notes = string.Join(" ", response.Notes.Where(n => !n.StartsWith("SIMULATED", StringComparison.Ordinal)));
            if (heard.Length == 0)
            {
                VoiceStatusText = notes.Length > 0 ? notes : "Nothing usable was heard. Try again.";
                return;
            }
            ChatInputText = string.IsNullOrWhiteSpace(ChatInputText) ? heard : ChatInputText.TrimEnd() + " " + heard;
            VoiceStatusText = (audio.IsSimulated ? "SIMULATED microphone. " : "") +
                              $"Heard: “{heard}”. Check the words in the box, fix them if needed, then tap Send.";
        }
        catch (AiServiceException ex)
        {
            VoiceStatusText = string.IsNullOrWhiteSpace(ex.Error.Message) ? $"Voice input failed ({ex.Code})." : ex.Error.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            VoiceStatusText = "The AI service did not answer. Check AI status in Settings, then try again.";
        }
        catch (Exception ex)
        {
            VoiceStatusText = ex.Message;
        }
        finally
        {
            IsVoiceTranscribing = false;
        }
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Speech;

namespace GseTechPro.App.ViewModels;

/// <summary>Voice preferences saved on this tablet (<c>app_settings</c> key <see cref="MainShellViewModel.VoicePreferencesKey"/>).</summary>
public sealed record VoicePreferences
{
    /// <summary>Read Tech Chat answers aloud. Off by default: a shop floor may not want the tablet talking.</summary>
    public bool ReadChatAnswersAloud { get; init; }

    /// <summary>Speak guided-step read-backs (7.5b). On by default.</summary>
    public bool ReadBackSpoken { get; init; } = true;

    /// <summary>Key that works like the Talk button, e.g. "F9"; "Off" for none.</summary>
    public string PushToTalkKey { get; init; } = MainShellViewModel.DefaultPushToTalkKey;
}

/// <summary>
/// Phase 7.5c spoken answers and hands-free Talk:
/// <list type="bullet">
/// <item>"Read answers aloud" (Tech Chat and Settings): each new Tech Chat answer is spoken with Windows' offline voice through
///   <see cref="SpokenAnswer.Prepare"/> (safety lines first, sources at the end); the answer is always on screen too.</item>
/// <item>A push-to-talk key (Settings) that works like the Talk button on Tech Chat and the guided step. The 7220's
///   programmable buttons are set (in Dell's rugged control software) to send that key. Press once to start, again to stop:
///   rugged-tablet buttons send a key press, not a hold.</item>
/// <item>Talk stops any speech first, so the tablet never records its own voice.</item>
/// </list>
/// </summary>
public sealed partial class MainShellViewModel
{
    public const string VoicePreferencesKey = "voice.preferences";
    public const string PushToTalkOff = "Off";
    public const string DefaultPushToTalkKey = "F9";

    /// <summary>Keys offered for push-to-talk. F13–F24 are not on a keyboard, so a tablet button set to one never clashes with typing.</summary>
    public static IReadOnlyList<string> PushToTalkKeys { get; } =
        [PushToTalkOff, "F8", "F9", "F10", "F12", "F13", "F14", "F15", "F16", "F17", "F18", "F19", "F20", "F21", "F22", "F23", "F24"];

    /// <summary>For the Settings list (x:Bind needs an instance property).</summary>
    public IReadOnlyList<string> PushToTalkKeyOptions => PushToTalkKeys;

    private bool _loadingVoicePreferences;

    [ObservableProperty]
    private bool _isChatAnswerSpoken;

    [ObservableProperty]
    private string _pushToTalkKey = DefaultPushToTalkKey;

    public int SelectedPushToTalkKeyIndex
    {
        get => Math.Max(0, PushToTalkKeys.ToList().IndexOf(PushToTalkKey));
        set
        {
            if (value >= 0 && value < PushToTalkKeys.Count)
                PushToTalkKey = PushToTalkKeys[value];
        }
    }

    public string PushToTalkHintText => PushToTalkKey == PushToTalkOff
        ? "No push-to-talk key. Use the Talk button."
        : $"Press {PushToTalkKey} on Tech Chat or a guided step to start talking, and again to stop. Set a 7220 programmable button to send {PushToTalkKey}.";

    partial void OnIsChatAnswerSpokenChanged(bool value)
    {
        if (!value)
            _speechOutput.Stop();
        _ = SaveVoicePreferencesAsync();
    }

    partial void OnIsVoiceReadBackSpokenChanged(bool value) => _ = SaveVoicePreferencesAsync();

    partial void OnPushToTalkKeyChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedPushToTalkKeyIndex));
        OnPropertyChanged(nameof(PushToTalkHintText));
        _ = SaveVoicePreferencesAsync();
    }

    public async Task LoadVoicePreferencesAsync()
    {
        VoicePreferences prefs;
        try
        {
            prefs = await _dataServices.Settings.GetAsync(VoicePreferencesKey, new VoicePreferences());
        }
        catch
        {
            prefs = new VoicePreferences();
        }
        _loadingVoicePreferences = true;
        try
        {
            IsChatAnswerSpoken = prefs.ReadChatAnswersAloud;
            IsVoiceReadBackSpoken = prefs.ReadBackSpoken;
            PushToTalkKey = PushToTalkKeys.Contains(prefs.PushToTalkKey) ? prefs.PushToTalkKey : DefaultPushToTalkKey;
        }
        finally
        {
            _loadingVoicePreferences = false;
        }
    }

    private async Task SaveVoicePreferencesAsync()
    {
        if (_loadingVoicePreferences)
            return;
        try
        {
            await _dataServices.Settings.SetAsync(VoicePreferencesKey, new VoicePreferences
            {
                ReadChatAnswersAloud = IsChatAnswerSpoken,
                ReadBackSpoken = IsVoiceReadBackSpoken,
                PushToTalkKey = PushToTalkKey,
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GSE Tech Pro] voice preferences not saved: {ex.Message}");
        }
    }

    /// <summary>
    /// The push-to-talk key (from the window's key handler; <paramref name="keyName"/> is the <c>VirtualKey</c> name).
    /// True when it was used. Key repeat is ignored, and the key does nothing off the Tech Chat and guided pages.
    /// </summary>
    public bool HandlePushToTalkKey(string keyName, bool isRepeat)
    {
        if (PushToTalkKey == PushToTalkOff || !string.Equals(keyName, PushToTalkKey, StringComparison.OrdinalIgnoreCase))
            return false;
        if (ActiveSectionKey is not ("AiChat" or "GuidedTree"))
            return false;
        if (!isRepeat && ToggleTalkCommand.CanExecute(null))
            ToggleTalkCommand.Execute(null);
        return true;
    }

    /// <summary>Speaks a new Tech Chat answer when "Read answers aloud" is on.</summary>
    private void SpeakChatAnswer(ChatResponse response)
    {
        if (!IsChatAnswerSpoken)
            return;
        string words = SpokenAnswer.Prepare(response.Text, response.Citations.Select(c => c.DisplayText), response.IsSuggestion);
        if (words.Length > 0)
            _ = _speechOutput.SpeakAsync(words);
    }

    [RelayCommand]
    public void StopSpeaking() => _speechOutput.Stop();
}

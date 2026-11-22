using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using GseTechPro.Core.Speech;
using GseTechPro.Core.Units;
using Microsoft.UI.Xaml;

namespace GseTechPro.App.ViewModels;

/// <summary>What a spoken reading or note waits on before it is saved (Phase 7.5b).</summary>
public sealed record PendingVoiceItem(VoiceCommandKind Kind, double? Value, string? Unit, string? Text, bool IsSimulated);

/// <summary>
/// Phase 7.5b voice commands on the guided troubleshooting step. Talk on that page sends the words to
/// <see cref="VoiceCommands.Parse"/> instead of the Tech Chat box:
/// <list type="bullet">
/// <item>"reading is 2,300 psi": checked against the step's reading type, read back aloud and on screen, and saved only after
///   "yes" or a tap on Save (then it is recorded like a typed reading, with <c>readings.source = 'voice'</c>, and the step goes on);</item>
/// <item>"add note …": shown back and saved to the work order only after "yes" or Save;</item>
/// <item>"next step": the Record &amp; Next button;</item>
/// <item>a question: put in the Tech Chat box (answers come with manual pages; nothing is answered from voice).</item>
/// </list>
/// Simulated-microphone readings are saved as simulated. Nothing is ever saved straight from what was heard.
/// </summary>
public sealed partial class MainShellViewModel
{
    /// <summary>Set while a confirmed voice reading is being recorded, so it is saved with <c>source = 'voice'</c>.</summary>
    private (bool Voice, bool Simulated) _guidedReadingVoice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VoiceConfirmVisibility))]
    [NotifyPropertyChangedFor(nameof(VoiceConfirmText))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmVoiceItemCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelVoiceItemCommand))]
    private PendingVoiceItem? _pendingVoiceItem;

    /// <summary>Speak read-backs aloud (the same words are always on screen).</summary>
    [ObservableProperty]
    private bool _isVoiceReadBackSpoken = true;

    public Visibility VoiceConfirmVisibility => PendingVoiceItem is null ? Visibility.Collapsed : Visibility.Visible;

    public string VoiceConfirmText => PendingVoiceItem switch
    {
        { Kind: VoiceCommandKind.Reading, Value: double v, Unit: string u } p =>
            $"{(p.IsSimulated ? "SIMULATED " : "")}Heard reading: {VoiceCommands.Describe(v, u)}. Save it?",
        { Kind: VoiceCommandKind.AddNote, Text: string t } p =>
            $"{(p.IsSimulated ? "SIMULATED " : "")}Heard note: “{t}”. Save it to the work order?",
        _ => "",
    };

    public const string GuidedVoiceHint =
        "Tap Talk and say: “reading is 2,300 psi”, “next step”, “add note …” or a question. Every reading and note is read back before it is saved.";

    public const string ChatVoiceHint = "Tap Talk, speak, then tap again. Check the words, then tap Send.";

    /// <summary>Idle hint for the page Talk is on (the status line is shared by Tech Chat and the guided step).</summary>
    private void ShowVoiceHintFor(string sectionKey)
    {
        if (IsVoiceListening || IsVoiceTranscribing || PendingVoiceItem is not null)
            return;
        VoiceStatusText = sectionKey == "GuidedTree" ? GuidedVoiceHint : ChatVoiceHint;
    }

    private void Speak(string text)
    {
        if (IsVoiceReadBackSpoken)
            _ = _speechOutput.SpeakAsync(text);
    }

    private void SetGuidedVoiceStatus(string text, string? spoken = null)
    {
        VoiceStatusText = text;
        GuidedStatusMessage = text;
        StatusBannerMessage = text;
        if (spoken is not null)
            Speak(spoken);
    }

    /// <summary>Acts on one transcript heard on the guided step.</summary>
    public async Task HandleGuidedVoiceAsync(string heard, bool isSimulated)
    {
        var cmd = VoiceCommands.Parse(heard);
        string sim = isSimulated ? "SIMULATED microphone. " : "";
        switch (cmd.Kind)
        {
            case VoiceCommandKind.Confirm:
                if (PendingVoiceItem is null)
                    SetGuidedVoiceStatus(sim + "Heard “yes”, but there is nothing waiting to be saved.");
                else
                    await ConfirmVoiceItemAsync();
                break;

            case VoiceCommandKind.Cancel:
                if (PendingVoiceItem is null)
                {
                    SetGuidedVoiceStatus(sim + "Heard “no”: nothing was waiting.");
                }
                else
                {
                    CancelVoiceItem();
                }
                break;

            case VoiceCommandKind.Reading:
                OfferVoiceReading(cmd, isSimulated, sim);
                break;

            case VoiceCommandKind.AddNote:
                PendingVoiceItem = new PendingVoiceItem(VoiceCommandKind.AddNote, null, null, cmd.Text, isSimulated);
                SetGuidedVoiceStatus(VoiceConfirmText + " Say “yes” or tap Save; “no” to throw it away.",
                    $"Save note: {cmd.Text}? Say yes or no.");
                break;

            case VoiceCommandKind.NextStep:
                if (PendingVoiceItem is not null)
                {
                    SetGuidedVoiceStatus(sim + "Say “yes” or “no” to what was heard first.", "Say yes or no first.");
                    break;
                }
                if (CurrentGuidedStep is not { Available: true } nextStep || !string.IsNullOrWhiteSpace(nextStep.TerminalResult))
                {
                    SetGuidedVoiceStatus(sim + "Heard “next step”, but there is no step to go on from.");
                    break;
                }
                if (nextStep.ReadingType is null or ReadingType.Other)
                {
                    // An inspection step is answered PASS / FAIL / N/A: never by voice, so a misheard word cannot branch the tree.
                    SetGuidedVoiceStatus(sim + "Heard “next step”. This step needs an answer: tap PASS, FAIL or N/A.",
                        "This step needs pass or fail. Tap a button.");
                    break;
                }
                await NextGuidedStepAsync(null);
                VoiceStatusText = sim + "Heard “next step”. " + GuidedStatusMessage;
                break;

            case VoiceCommandKind.Ask:
                ChatInputText = string.IsNullOrWhiteSpace(ChatInputText) ? cmd.Text! : ChatInputText.TrimEnd() + " " + cmd.Text;
                SetGuidedVoiceStatus(sim + $"Question put in the Tech Chat box: “{cmd.Text}”. Open Tech Chat and tap Send; answers come with manual pages.",
                    "Your question is in Tech Chat. Open it and tap Send.");
                break;

            default:
                SetGuidedVoiceStatus(sim + $"Heard “{cmd.Heard}”: not a command. Say “reading is 2,300 psi”, “next step”, “add note …” or ask a question.",
                    "Sorry, that is not a command.");
                break;
        }
    }

    private void OfferVoiceReading(VoiceCommand cmd, bool isSimulated, string sim)
    {
        var step = CurrentGuidedStep;
        string said = VoiceCommands.Describe(cmd.Value!.Value, cmd.Unit!);
        if (step is null || !step.Available)
        {
            SetGuidedVoiceStatus(sim + $"Heard {said}, but no troubleshooting step is open. Start a guided test first.");
            return;
        }
        if (step.ReadingType is not ReadingType rt || rt == ReadingType.Other)
        {
            SetGuidedVoiceStatus(sim + $"Heard {said}, but this step takes no measurement. Use PASS / FAIL / N/A.",
                "This step takes no measurement.");
            return;
        }
        if (!UnitConverter.IsKnownUnit(cmd.Unit))
        {
            SetGuidedVoiceStatus(sim + $"Heard {said}, but the app has no {cmd.Unit} unit. Say it in {string.Join(", ", GuidedAvailableUnits)}.",
                "Please say that again in another unit.");
            return;
        }
        string? need = TroubleshootingUnitHelper.TryGetCanonicalUnit(rt);
        if (need is not null && !string.Equals(UnitConverter.CanonicalUnit(cmd.Unit!), need, StringComparison.Ordinal))
        {
            SetGuidedVoiceStatus(sim + $"Heard {said}, but this step needs a {rt.ToString().ToLowerInvariant()} reading ({string.Join(", ", GuidedAvailableUnits)}). Say it again.",
                "That unit does not fit this step. Please say it again.");
            return;
        }
        PendingVoiceItem = new PendingVoiceItem(VoiceCommandKind.Reading, cmd.Value, cmd.Unit, null, isSimulated);
        SetGuidedVoiceStatus(VoiceConfirmText + " Say “yes” or tap Save; “no” to throw it away.",
            VoiceCommands.SpeakReading(cmd.Value!.Value, cmd.Unit!));
    }

    private bool HasPendingVoiceItem() => PendingVoiceItem is not null;

    /// <summary>Saves what was read back (the "yes" button).</summary>
    [RelayCommand(CanExecute = nameof(HasPendingVoiceItem))]
    public async Task ConfirmVoiceItemAsync()
    {
        var item = PendingVoiceItem;
        if (item is null)
            return;
        PendingVoiceItem = null;

        if (item.Kind == VoiceCommandKind.Reading)
        {
            GuidedReadingValue = item.Value;
            GuidedReadingUnit = item.Unit!;
            _guidedReadingVoice = (true, item.IsSimulated);
            try
            {
                await NextGuidedStepAsync(null);
            }
            finally
            {
                _guidedReadingVoice = default;
            }
            SetGuidedVoiceStatus($"Saved {VoiceCommands.Describe(item.Value!.Value, item.Unit!)} (spoken). {GuidedStatusMessage}", "Saved.");
            return;
        }

        var wo = await ResolveActiveWorkOrderAsync();
        if (wo is null)
        {
            PendingVoiceItem = item; // keep it until a work order is open
            SetGuidedVoiceStatus("Open a work order first, then say “yes” again to save the note.");
            return;
        }
        await _dataServices.Notes.AddAsync(new Note
        {
            Scope = NoteScope.WorkOrder,
            TargetId = wo.Id,
            Body = (item.IsSimulated ? "SIMULATED voice note: " : "Voice note: ") + item.Text,
        });
        SetGuidedVoiceStatus($"Note saved to WO #{wo.Number ?? wo.Id}: “{item.Text}”.", "Note saved.");
    }

    /// <summary>Throws away what was read back (the "no" button).</summary>
    [RelayCommand(CanExecute = nameof(HasPendingVoiceItem))]
    public void CancelVoiceItem()
    {
        if (PendingVoiceItem is null)
            return;
        PendingVoiceItem = null;
        SetGuidedVoiceStatus("Thrown away: nothing was saved.", "Thrown away.");
    }
}

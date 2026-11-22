using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GseTechPro.App.Services;
using GseTechPro.App.ViewModels;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Core.Speech;
using GseTechPro.Core.Units;
using GseTechPro.Data;
using GseTechPro.Devices;
using Microsoft.UI.Xaml;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Phase 7.5b: voice commands on the guided troubleshooting step; nothing is saved before "yes" or Save.</summary>
public sealed class GuidedVoiceTests
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly GuidedStep PressureStep = new()
    {
        SessionId = "s1", Available = true, TreeId = "t", StepId = "relief", StepOrder = 3,
        Instruction = "Read the main relief pressure at the test port.", ReadingType = ReadingType.HydraulicPressure,
        TestPointId = null, SpecSource = "TODO: needs spec from manual", // no seeded test point in a fresh database
    };

    /// <summary>Answers /guided/next with the next step and /voice/transcribe with scripted transcripts.</summary>
    private sealed class Handler : HttpMessageHandler
    {
        public Queue<string> Transcripts { get; } = new();
        public List<JsonObject> GuidedNextBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? "";
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            string json;
            if (path == AiServiceProtocol.GuidedNextPath)
            {
                GuidedNextBodies.Add(JsonNode.Parse(body)!.AsObject());
                json = JsonSerializer.Serialize(PressureStep with { StepId = "after", StepOrder = 4, Instruction = "Next check." }, AiServiceJson.Options);
            }
            else if (path == AiServiceProtocol.VoiceTranscribePath)
            {
                json = JsonSerializer.Serialize(new { text = Transcripts.Dequeue(), notes = Array.Empty<string>(), audio_seconds = 1.0, route = "tablet" });
            }
            else
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakeSpeech : ISpeechOutput
    {
        public List<string> Said { get; } = new();
        public Task SpeakAsync(string text, CancellationToken cancellationToken = default) { Said.Add(text); return Task.CompletedTask; }
        public void Stop() { }
    }

    private sealed class SimMicFactory : IMicrophoneFactory
    {
        private static readonly MicrophoneInfo Sim = new("sim", "Simulated microphone", true);

        public Task<IReadOnlyList<MicrophoneInfo>> DiscoverAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MicrophoneInfo>>(new[] { Sim });

        public Task<IMicrophone> OpenAsync(MicrophoneInfo info, CancellationToken cancellationToken = default) =>
            Task.FromResult<IMicrophone>(new Mic());

        private sealed class Mic : ISimulatedMicrophone
        {
            public MicrophoneInfo Info => Sim;
            public string Phrase { get; set; } = "";
            public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task<RecordedAudio> StopAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(new RecordedAudio { Wav = new byte[64], Seconds = 1, IsSimulated = true });
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static async Task<(MainShellViewModel Vm, DataServices Data, Handler Http, FakeSpeech Speech)> CreateAsync(bool openWorkOrder = true)
    {
        var http = new Handler();
        var speech = new FakeSpeech();
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-guided-voice-{Guid.NewGuid():N}", "gse.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var data = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await data.InitializeAsync();
        var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory(),
            aiClient: new AiServiceClient(new HttpClient(http), Token), microphoneFactory: new SimMicFactory(), speechOutput: speech);
        await vm.ReloadDatabaseRecordsAsync();
        if (openWorkOrder)
            await vm.CreateActiveWorkOrderAsync();
        vm.CurrentGuidedStep = PressureStep;
        vm.GuidedReadingUnit = UnitConverter.Psi;
        return (vm, data, http, speech);
    }

    [Fact]
    public async Task SpokenReading_IsReadBack_SavedOnlyAfterYes_AsAVoiceReading()
    {
        var (vm, data, http, speech) = await CreateAsync();
        await using var _ = vm;

        await vm.HandleGuidedVoiceAsync("Reading is 2,300 psi.", isSimulated: false);
        Assert.Equal(Visibility.Visible, vm.VoiceConfirmVisibility);
        Assert.Equal("Heard reading: 2,300 psi. Save it?", vm.VoiceConfirmText);
        Assert.Equal("I heard 2300 P S I. Say yes to save it, or no.", speech.Said.Last());
        Assert.Empty(await data.Readings.GetAllAsync()); // nothing saved yet
        Assert.Empty(http.GuidedNextBodies);

        // "next step" before answering is refused.
        await vm.HandleGuidedVoiceAsync("Next step.", false);
        Assert.Contains("Say “yes” or “no”", vm.VoiceStatusText);
        Assert.Empty(http.GuidedNextBodies);

        await vm.HandleGuidedVoiceAsync("Yes. Save it.", false);
        Assert.Null(vm.PendingVoiceItem);
        var reading = Assert.Single(await data.Readings.GetAllAsync());
        Assert.Equal(ReadingSource.Voice, reading.Source);
        Assert.False(reading.IsSimulated);
        Assert.Equal(2300, reading.EnteredValue);
        Assert.Equal(UnitConverter.Psi, reading.EnteredUnit);
        Assert.Equal(2300, (double)http.GuidedNextBodies.Single()["reading_value"]!);
        Assert.Equal("after", vm.CurrentGuidedStep!.StepId); // advanced like Record & Next
        Assert.StartsWith("Saved 2,300 psi (spoken).", vm.VoiceStatusText);
    }

    [Fact]
    public async Task No_ThrowsItAway_AndATypedReadingIsStillManual()
    {
        var (vm, data, _, _) = await CreateAsync();
        await using var __ = vm;
        await vm.HandleGuidedVoiceAsync("Reading is 185 psi.", false);
        await vm.HandleGuidedVoiceAsync("No, that's wrong.", false);
        Assert.Null(vm.PendingVoiceItem);
        Assert.Contains("nothing was saved", vm.VoiceStatusText);
        Assert.Empty(await data.Readings.GetAllAsync());

        vm.GuidedReadingValue = 190;
        await vm.NextGuidedStepAsync(null);
        Assert.Equal(ReadingSource.Manual, Assert.Single(await data.Readings.GetAllAsync()).Source);
    }

    [Fact]
    public async Task WrongUnit_NoMeasurementStep_AndNoStep_AreRefused()
    {
        var (vm, data, _, _) = await CreateAsync();
        await using var __ = vm;
        await vm.HandleGuidedVoiceAsync("Reading 12.4 volts.", false);
        Assert.Null(vm.PendingVoiceItem);
        Assert.Contains("needs a hydraulicpressure reading", vm.VoiceStatusText);

        vm.CurrentGuidedStep = PressureStep with { ReadingType = null, Question = "Is the hose wet?" };
        await vm.HandleGuidedVoiceAsync("Reading is 2300 psi.", false);
        Assert.Null(vm.PendingVoiceItem);
        Assert.Contains("takes no measurement", vm.VoiceStatusText);

        vm.CurrentGuidedStep = null;
        await vm.HandleGuidedVoiceAsync("Reading is 2300 psi.", false);
        Assert.Contains("no troubleshooting step is open", vm.VoiceStatusText);
        Assert.Empty(await data.Readings.GetAllAsync());
    }

    [Fact]
    public async Task SimulatedReading_IsSavedAsSimulated_ViaTheSaveButton()
    {
        var (vm, data, _, _) = await CreateAsync();
        await using var __ = vm;
        await vm.HandleGuidedVoiceAsync("Reading is 2300 psi.", isSimulated: true);
        Assert.StartsWith("SIMULATED Heard reading", vm.VoiceConfirmText);
        Assert.True(vm.ConfirmVoiceItemCommand.CanExecute(null));
        await vm.ConfirmVoiceItemAsync();
        var reading = Assert.Single(await data.Readings.GetAllAsync());
        Assert.True(reading.IsSimulated);
        Assert.Equal(ReadingSource.Voice, reading.Source);
    }

    [Fact]
    public async Task SpokenNote_IsSavedToTheWorkOrderAfterYes()
    {
        var (vm, data, _, speech) = await CreateAsync();
        await using var __ = vm;
        await vm.HandleGuidedVoiceAsync("Add note: Left lift cylinder hose is wet at the lower fitting.", false);
        Assert.Equal("Heard note: “Left lift cylinder hose is wet at the lower fitting”. Save it to the work order?", vm.VoiceConfirmText);
        Assert.Empty(await data.Notes.GetAllAsync());
        Assert.StartsWith("Save note:", speech.Said.Last());

        await vm.HandleGuidedVoiceAsync("yes", false);
        var note = Assert.Single(await data.Notes.GetAllAsync());
        Assert.Equal(NoteScope.WorkOrder, note.Scope);
        Assert.Equal(vm.ActiveWorkOrder!.Id, note.TargetId);
        Assert.Equal("Voice note: Left lift cylinder hose is wet at the lower fitting", note.Body);
        Assert.Contains("Note saved to WO #", vm.VoiceStatusText);
    }

    [Fact]
    public async Task Question_GoesToTheTechChatBox_AndUnknownIsSaidBack()
    {
        var (vm, data, http, _) = await CreateAsync();
        await using var __ = vm;
        await vm.HandleGuidedVoiceAsync("What is the spec for the main relief valve pressure?", false);
        Assert.Equal("What is the spec for the main relief valve pressure?", vm.ChatInputText);
        Assert.Contains("Tech Chat", vm.VoiceStatusText);
        Assert.Null(vm.PendingVoiceItem);

        await vm.HandleGuidedVoiceAsync("The lift is slow today", false);
        Assert.Contains("not a command", vm.VoiceStatusText);
        await vm.HandleGuidedVoiceAsync("yes", false);
        Assert.Contains("nothing waiting", vm.VoiceStatusText);
        Assert.Empty(await data.Readings.GetAllAsync());
        Assert.Empty(http.GuidedNextBodies);
    }

    [Fact]
    public async Task NextStep_OnAPassFailStep_AsksForAButton_AndCallsNothing()
    {
        // Found in the in-app check: "next step" on an inspection step sent an empty answer and stayed put.
        var (vm, _, http, _) = await CreateAsync();
        await using var __ = vm;
        vm.CurrentGuidedStep = PressureStep with { ReadingType = null, Question = "Is the area clear?" };
        await vm.HandleGuidedVoiceAsync("Next step.", false);
        Assert.Contains("tap PASS, FAIL or N/A", vm.VoiceStatusText);
        Assert.Empty(http.GuidedNextBodies);
    }

    [Fact]
    public async Task SimulatedStatus_SaysSimulatedOnce()
    {
        var (vm, _, _, _) = await CreateAsync();
        await using var __ = vm;
        await vm.HandleGuidedVoiceAsync("Add note: hose wet", isSimulated: true);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(vm.VoiceStatusText, "SIMULATED").Count);
    }

    [Fact]
    public async Task EachPage_ShowsItsOwnIdleHint()
    {
        var (vm, _, _, _) = await CreateAsync();
        await using var __ = vm;
        await vm.SelectNavRailItem("GuidedTree");
        Assert.Equal(MainShellViewModel.GuidedVoiceHint, vm.VoiceStatusText);
        await vm.SelectNavRailItem("AiChat");
        Assert.Equal(MainShellViewModel.ChatVoiceHint, vm.VoiceStatusText);
    }

    [Fact]
    public async Task ReadBackAloud_CanBeTurnedOff()
    {
        var (vm, _, _, speech) = await CreateAsync();
        await using var __ = vm;
        vm.IsVoiceReadBackSpoken = false;
        await vm.HandleGuidedVoiceAsync("Reading is 2300 psi.", false);
        Assert.Empty(speech.Said);
        Assert.Equal(Visibility.Visible, vm.VoiceConfirmVisibility); // still on screen
    }

    [Fact]
    public async Task TalkOnTheGuidedPage_SendsTheWordsToTheCommands_NotTheChatBox()
    {
        var (vm, _, http, _) = await CreateAsync();
        await using var __ = vm;
        vm.ActiveSectionKey = "GuidedTree";
        await vm.RefreshMicrophonesAsync();
        http.Transcripts.Enqueue("Reading is 2300 psi.");
        await vm.ToggleTalkAsync();
        await vm.ToggleTalkAsync();
        Assert.Equal("", vm.ChatInputText);
        Assert.Equal(VoiceCommandKind.Reading, vm.PendingVoiceItem!.Kind);
        Assert.True(vm.PendingVoiceItem.IsSimulated);
    }
}

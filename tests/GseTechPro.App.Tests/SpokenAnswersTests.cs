using System.Net;
using System.Text;
using System.Text.Json;
using GseTechPro.App.Services;
using GseTechPro.App.ViewModels;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Data;
using GseTechPro.Devices;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Phase 7.5c: Tech Chat answers read aloud (switch, off by default) and a push-to-talk key.</summary>
public sealed class SpokenAnswersTests
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private sealed class Handler : HttpMessageHandler
    {
        public int TranscribeCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? "";
            string json;
            if (path == AiServiceProtocol.ChatPath)
            {
                json = JsonSerializer.Serialize(new ChatResponse
                {
                    Text = "**Main relief:** TODO: needs spec from manual.\n- WARNING: bleed off stored pressure first.",
                    IsSuggestion = true,
                    Citations = [new Citation { DisplayText = "TUG 660-28 Maintenance Manual, page 78", ManualId = "m", PageStart = 78 }],
                }, AiServiceJson.Options);
            }
            else if (path == AiServiceProtocol.VoiceTranscribePath)
            {
                TranscribeCalls++;
                json = JsonSerializer.Serialize(new { text = "What is the relief pressure?", notes = Array.Empty<string>(), audio_seconds = 1.0, route = "tablet" });
            }
            else
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class FakeSpeech : ISpeechOutput
    {
        public List<string> Said { get; } = new();
        public int Stops { get; private set; }
        public Task SpeakAsync(string text, CancellationToken cancellationToken = default) { Said.Add(text); return Task.CompletedTask; }
        public void Stop() => Stops++;
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

    private static async Task<(MainShellViewModel Vm, DataServices Data, Handler Http, FakeSpeech Speech, string DbPath)> CreateAsync(string? dbPath = null)
    {
        var http = new Handler();
        var speech = new FakeSpeech();
        dbPath ??= Path.Combine(Path.GetTempPath(), $"gse-spoken-{Guid.NewGuid():N}", "gse.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var data = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await data.InitializeAsync();
        var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory(),
            aiClient: new AiServiceClient(new HttpClient(http), Token), microphoneFactory: new SimMicFactory(), speechOutput: speech);
        await vm.ReloadDatabaseRecordsAsync();
        return (vm, data, http, speech, dbPath);
    }

    [Fact]
    public async Task ReadAloudIsOffByDefault_AnswerIsNotSpoken()
    {
        var (vm, _, _, speech, _) = await CreateAsync();
        await using var _ = vm;
        Assert.False(vm.IsChatAnswerSpoken);
        await vm.SendQuickPromptAsync("Relief pressure?");
        Assert.Contains(vm.ChatMessages, m => m.Role == AiChatRoles.Assistant);
        Assert.Empty(speech.Said);
    }

    [Fact]
    public async Task ReadAloudOn_SpeaksSafetyFirstThenSource()
    {
        var (vm, _, _, speech, _) = await CreateAsync();
        await using var _ = vm;
        vm.IsChatAnswerSpoken = true;
        await vm.SendQuickPromptAsync("Relief pressure?");
        string said = Assert.Single(speech.Said);
        Assert.Equal(
            "AI suggestion. WARNING: bleed off stored pressure first. Main relief: TODO: needs spec from manual. Source: TUG 660-28 Maintenance Manual, page 78.",
            said);
    }

    [Fact]
    public async Task Preferences_AreSavedAndLoaded()
    {
        var (vm, _, _, _, dbPath) = await CreateAsync();
        await using (vm)
        {
            Assert.Equal(MainShellViewModel.DefaultPushToTalkKey, vm.PushToTalkKey);
            vm.IsChatAnswerSpoken = true;
            vm.IsVoiceReadBackSpoken = false;
            vm.SelectedPushToTalkKeyIndex = MainShellViewModel.PushToTalkKeys.ToList().IndexOf("F13");
            Assert.Equal("F13", vm.PushToTalkKey);
            await Task.Delay(200); // saves are fire-and-forget
        }

        var (vm2, _, _, _, _) = await CreateAsync(dbPath);
        await using var __ = vm2;
        Assert.True(vm2.IsChatAnswerSpoken);
        Assert.False(vm2.IsVoiceReadBackSpoken);
        Assert.Equal("F13", vm2.PushToTalkKey);
        Assert.Contains("F13", vm2.PushToTalkHintText);
    }

    [Fact]
    public async Task PushToTalkKey_TogglesTalk_OnlyOnChatAndGuidedPages()
    {
        var (vm, _, http, speech, _) = await CreateAsync();
        await using var _ = vm;
        await vm.RefreshMicrophonesAsync();

        vm.ActiveSectionKey = "Diagnostics";
        Assert.False(vm.HandlePushToTalkKey("F9", isRepeat: false));
        Assert.False(vm.IsVoiceListening);

        vm.ActiveSectionKey = "AiChat";
        Assert.False(vm.HandlePushToTalkKey("F8", isRepeat: false)); // not the chosen key
        Assert.True(vm.HandlePushToTalkKey("F9", isRepeat: false));
        await WaitForAsync(() => vm.IsVoiceListening);
        Assert.True(speech.Stops > 0); // Talk silences the tablet first

        Assert.True(vm.HandlePushToTalkKey("F9", isRepeat: true)); // key repeat: used, but does nothing
        Assert.True(vm.IsVoiceListening);

        Assert.True(vm.HandlePushToTalkKey("F9", isRepeat: false));
        await WaitForAsync(() => !vm.IsVoiceListening && !vm.IsVoiceTranscribing);
        Assert.Equal(1, http.TranscribeCalls);
        Assert.Equal("What is the relief pressure?", vm.ChatInputText); // in the box, not sent

        vm.PushToTalkKey = MainShellViewModel.PushToTalkOff;
        Assert.False(vm.HandlePushToTalkKey("F9", isRepeat: false));
    }

    [Fact]
    public async Task TurningReadAloudOff_StopsSpeech()
    {
        var (vm, _, _, speech, _) = await CreateAsync();
        await using var _ = vm;
        vm.IsChatAnswerSpoken = true;
        int before = speech.Stops;
        vm.IsChatAnswerSpoken = false;
        Assert.Equal(before + 1, speech.Stops);
        vm.StopSpeakingCommand.Execute(null);
        Assert.Equal(before + 2, speech.Stops);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition());
    }
}

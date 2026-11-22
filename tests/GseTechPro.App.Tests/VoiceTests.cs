using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GseTechPro.App.Services;
using GseTechPro.App.ViewModels;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Data;
using GseTechPro.Devices;
using Microsoft.UI.Xaml;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Phase 7.5a: push-to-talk in Tech Chat (POST /voice/transcribe) through the real HTTP client.</summary>
public sealed class VoiceTests
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private sealed class Handler(Func<HttpStatusCode> status, Func<string> body) : HttpMessageHandler
    {
        public JsonObject? LastBody { get; private set; }
        public string? LastPath { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            if (request.Content is not null)
                LastBody = JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))?.AsObject();
            return new HttpResponseMessage(status()) { Content = new StringContent(body(), Encoding.UTF8, "application/json") };
        }
    }

    /// <summary>Records Start/Stop and returns a fixed clip.</summary>
    private sealed class FakeMicrophone(MicrophoneInfo info, bool simulated) : ISimulatedMicrophone
    {
        public MicrophoneInfo Info => info;
        public string Phrase { get; set; } = "";
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public static readonly byte[] Clip = MakeWav(1.0);

        public Task StartAsync(CancellationToken cancellationToken = default) { Starts++; return Task.CompletedTask; }

        public Task<RecordedAudio> StopAsync(CancellationToken cancellationToken = default)
        {
            Stops++;
            return Task.FromResult(new RecordedAudio { Wav = Clip, Seconds = 1.0, IsSimulated = simulated });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeFactory : IMicrophoneFactory
    {
        public static readonly MicrophoneInfo Sim = new("sim", "Simulated microphone", true);
        public static readonly MicrophoneInfo Real = new("real", "Tablet microphone", false);
        public FakeMicrophone? Last { get; private set; }

        public Task<IReadOnlyList<MicrophoneInfo>> DiscoverAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MicrophoneInfo>>(new[] { Sim, Real });

        public Task<IMicrophone> OpenAsync(MicrophoneInfo info, CancellationToken cancellationToken = default)
        {
            Last = new FakeMicrophone(info, info.IsSimulated);
            return Task.FromResult<IMicrophone>(Last);
        }
    }

    private static byte[] MakeWav(double seconds, int rate = 16000)
    {
        int dataLen = (int)(seconds * rate) * 2;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray()); w.Write(36 + dataLen); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2);
        w.Write((short)2); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(dataLen); w.Write(new byte[dataLen]);
        return ms.ToArray();
    }

    private static async Task<(MainShellViewModel Vm, Handler Handler, FakeFactory Mics)> CreateAsync(
        Func<HttpStatusCode>? status = null, Func<string>? body = null)
    {
        var handler = new Handler(status ?? (() => HttpStatusCode.OK),
            body ?? (() => """{"text":"Reading is 2300 psi.","notes":["SIMULATED microphone: the words were spoken by a Windows voice from typed text."],"audio_seconds":1.0,"route":"tablet","model":"lfm3-omni"}"""));
        var mics = new FakeFactory();
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-voice-{Guid.NewGuid():N}", "gse.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var data = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await data.InitializeAsync();
        var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory(),
            aiClient: new AiServiceClient(new HttpClient(handler), Token), microphoneFactory: mics);
        await vm.ReloadDatabaseRecordsAsync();
        vm.SelectToolbarAction("AiTechChat");
        await vm.RefreshMicrophonesAsync();
        return (vm, handler, mics);
    }

    [Fact]
    public async Task TapTalkTwice_TranscribesIntoTheQuestionBox_AndSendsNothing()
    {
        var (vm, handler, mics) = await CreateAsync();
        await using var _ = vm;
        Assert.Equal(2, vm.Microphones.Count);
        Assert.True(vm.SelectedMicrophone!.IsSimulated); // simulator first
        Assert.Equal(Visibility.Visible, vm.SimulatedVoicePhraseVisibility);
        vm.SimulatedVoicePhrase = "Reading is 2300 PSI";
        int messagesBefore = vm.ChatMessages.Count;

        await vm.ToggleTalkAsync();
        Assert.True(vm.IsVoiceListening);
        Assert.Equal("Stop", vm.TalkButtonText);
        Assert.Equal(Visibility.Visible, vm.VoiceListeningVisibility);
        Assert.Equal(1, mics.Last!.Starts);

        await vm.ToggleTalkAsync();
        Assert.False(vm.IsVoiceListening);
        Assert.False(vm.IsVoiceTranscribing);
        Assert.Equal("Talk", vm.TalkButtonText);
        Assert.Equal("Reading is 2300 PSI", mics.Last.Phrase);
        Assert.Equal(AiServiceProtocol.VoiceTranscribePath, handler.LastPath);
        Assert.Equal(Convert.ToBase64String(FakeMicrophone.Clip), (string?)handler.LastBody!["audio_base64"]);
        Assert.Equal("wav", (string?)handler.LastBody["format"]);
        Assert.True((bool?)handler.LastBody["is_simulated"]);

        Assert.Equal("Reading is 2300 psi.", vm.ChatInputText);
        Assert.Contains("SIMULATED microphone", vm.VoiceStatusText);
        Assert.Contains("then tap Send", vm.VoiceStatusText);
        Assert.Equal(messagesBefore, vm.ChatMessages.Count); // nothing was sent

        // A second clip is added after what is already in the box.
        await vm.ToggleTalkAsync();
        await vm.ToggleTalkAsync();
        Assert.Equal("Reading is 2300 psi. Reading is 2300 psi.", vm.ChatInputText);
    }

    [Fact]
    public async Task NothingHeard_ShowsTheServiceNote_AndLeavesTheBoxAlone()
    {
        var (vm, _, _) = await CreateAsync(body: () =>
            """{"text":"","notes":["The transcript was longer than the recording could hold, so it was thrown away (the model may have answered instead of transcribing). Try again."],"audio_seconds":1.0,"route":"tablet"}""");
        await using var __ = vm;
        vm.ChatInputText = "typed already";
        await vm.ToggleTalkAsync();
        await vm.ToggleTalkAsync();
        Assert.Equal("typed already", vm.ChatInputText);
        Assert.Contains("thrown away", vm.VoiceStatusText);
    }

    [Fact]
    public async Task ServiceUnavailable_ShowsItsMessage()
    {
        const string message = "Voice input is unavailable: the audio part of the local model is not set up on this tablet. Type instead.";
        var (vm, _, _) = await CreateAsync(status: () => HttpStatusCode.ServiceUnavailable, body: () =>
            JsonSerializer.Serialize(new AiErrorEnvelope
            {
                Error = new AiError { Code = AiErrorCodes.AiUnavailable, Message = message, Detail = "voice_unavailable" },
            }, AiServiceJson.Options));
        await using var __ = vm;
        await vm.ToggleTalkAsync();
        await vm.ToggleTalkAsync();
        Assert.Equal(message, vm.VoiceStatusText);
        Assert.Equal("", vm.ChatInputText);
        Assert.True(vm.ToggleTalkCommand.CanExecute(null));
    }

    [Fact]
    public async Task ChangingMicrophone_StopsRecording_AndHidesThePhraseBoxForARealOne()
    {
        var (vm, _, mics) = await CreateAsync();
        await using var __ = vm;
        await vm.ToggleTalkAsync();
        Assert.True(vm.IsVoiceListening);
        vm.SelectedMicrophone = FakeFactory.Real;
        Assert.False(vm.IsVoiceListening);
        Assert.Contains("microphone was changed", vm.VoiceStatusText);
        Assert.Equal(Visibility.Collapsed, vm.SimulatedVoicePhraseVisibility);
        await vm.ToggleTalkAsync();
        Assert.False(mics.Last!.Info.IsSimulated);
    }

    [Fact]
    public async Task SimulatedMicrophone_SpeaksTheTypedWordsIntoA16BitWav()
    {
        await using var mic = new WindowsMicrophoneFactory.SimulatedMicrophone { Phrase = "Reading is two thousand three hundred P S I" };
        await mic.StartAsync();
        var audio = await mic.StopAsync();
        Assert.True(audio.IsSimulated);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(audio.Wav, 0, 4));
        Assert.InRange(audio.Seconds, 1.0, Voice.MaxSeconds);
        Assert.True(audio.Wav.Length < Voice.MaxAudioBytes);

        await using var empty = new WindowsMicrophoneFactory.SimulatedMicrophone { Phrase = "  " };
        await Assert.ThrowsAsync<InvalidOperationException>(() => empty.StopAsync());
    }

    [Fact]
    public void WavSeconds_ReadsTheHeader()
    {
        Assert.Equal(2.5, WindowsMicrophoneFactory.WavSeconds(MakeWav(2.5)), 3);
        Assert.Equal(1.0, WindowsMicrophoneFactory.WavSeconds(MakeWav(1.0, 22050)), 3);
        Assert.Equal(0, WindowsMicrophoneFactory.WavSeconds(new byte[10]));
    }
}

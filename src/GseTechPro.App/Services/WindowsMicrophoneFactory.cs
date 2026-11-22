using System.Runtime.InteropServices.WindowsRuntime;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace GseTechPro.App.Services;

/// <summary>
/// Microphones for push-to-talk (Phase 7.5a): the simulator first, then every audio-capture device Windows reports.
/// The simulator speaks typed text with a Windows offline voice, so the real speech-to-text path is exercised without a
/// microphone. Real clips are 16 kHz 16-bit mono WAV from <see cref="MediaCapture"/>. Offline: nothing here uses the network.
/// </summary>
public sealed class WindowsMicrophoneFactory : IMicrophoneFactory
{
    public static readonly MicrophoneInfo SimulatorInfo =
        new("simulator", "Simulated microphone (speaks typed words)", IsSimulated: true);

    public async Task<IReadOnlyList<MicrophoneInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<MicrophoneInfo> { SimulatorInfo };
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture).AsTask(cancellationToken);
            list.AddRange(devices.Where(d => d.IsEnabled).Select(d => new MicrophoneInfo(d.Id, d.Name, IsSimulated: false)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No audio API (or blocked by privacy settings): the simulator still works.
            System.Diagnostics.Debug.WriteLine($"[GSE Tech Pro] microphone discovery: {ex.Message}");
        }
        return list;
    }

    public async Task<IMicrophone> OpenAsync(MicrophoneInfo info, CancellationToken cancellationToken = default)
    {
        if (info.IsSimulated)
            return new SimulatedMicrophone();

        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                AudioDeviceId = info.Id,
                StreamingCaptureMode = StreamingCaptureMode.Audio,
            }).AsTask(cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            capture.Dispose();
            throw new InvalidOperationException(
                "Windows blocked the microphone. Turn on Settings › Privacy & security › Microphone › Let desktop apps access your microphone.");
        }
        catch
        {
            capture.Dispose();
            throw;
        }
        return new WindowsMicrophone(info, capture);
    }

    /// <summary>Length of a 16-bit PCM WAV in seconds, from its fmt and data chunks (0 when it cannot be read).</summary>
    public static double WavSeconds(byte[] wav)
    {
        if (wav.Length < 44 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F')
            return 0;
        int pos = 12, rate = 0, channels = 0, bits = 0;
        while (pos + 8 <= wav.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            int size = BitConverter.ToInt32(wav, pos + 4);
            int body = pos + 8;
            if (id == "fmt " && size >= 16)
            {
                channels = BitConverter.ToInt16(wav, body + 2);
                rate = BitConverter.ToInt32(wav, body + 4);
                bits = BitConverter.ToInt16(wav, body + 14);
            }
            else if (id == "data")
            {
                int len = Math.Min(size < 0 ? int.MaxValue : size, wav.Length - body);
                return rate > 0 && channels > 0 && bits > 0 ? len / (double)(rate * channels * (bits / 8)) : 0;
            }
            if (size < 0)
                break;
            pos = body + size + (size & 1);
        }
        return 0;
    }

    private static async Task<byte[]> ReadAllAsync(IRandomAccessStream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None).AsTask(cancellationToken);
        return bytes;
    }

    /// <summary>Simulator: on Stop, a Windows offline voice speaks <see cref="Phrase"/> into a WAV.</summary>
    public sealed class SimulatedMicrophone : ISimulatedMicrophone
    {
        public MicrophoneInfo Info => SimulatorInfo;
        public string Phrase { get; set; } = "";

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<RecordedAudio> StopAsync(CancellationToken cancellationToken = default)
        {
            string phrase = (Phrase ?? "").Trim();
            if (phrase.Length == 0)
                throw new InvalidOperationException("Type what the simulated microphone should say, then tap Talk.");
            using var synth = new SpeechSynthesizer();
            using var stream = await synth.SynthesizeTextToStreamAsync(phrase).AsTask(cancellationToken);
            var wav = await ReadAllAsync(stream, cancellationToken);
            return new RecordedAudio { Wav = wav, Seconds = WavSeconds(wav), IsSimulated = true };
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class WindowsMicrophone(MicrophoneInfo info, MediaCapture capture) : IMicrophone
    {
        private InMemoryRandomAccessStream? _stream;

        public MicrophoneInfo Info => info;

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            _stream?.Dispose();
            _stream = new InMemoryRandomAccessStream();
            var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Auto);
            profile.Audio = AudioEncodingProperties.CreatePcm(Voice.SampleRate, 1, 16);
            await capture.StartRecordToStreamAsync(profile, _stream).AsTask(cancellationToken);
        }

        public async Task<RecordedAudio> StopAsync(CancellationToken cancellationToken = default)
        {
            if (_stream is null)
                throw new InvalidOperationException("Recording was not started.");
            await capture.StopRecordAsync().AsTask(cancellationToken);
            var wav = await ReadAllAsync(_stream, cancellationToken);
            _stream.Dispose();
            _stream = null;
            return new RecordedAudio { Wav = wav, Seconds = WavSeconds(wav), IsSimulated = false };
        }

        public ValueTask DisposeAsync()
        {
            _stream?.Dispose();
            capture.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

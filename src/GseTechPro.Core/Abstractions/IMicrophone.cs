namespace GseTechPro.Core.Abstractions;

/// <summary>A microphone the app can record from (Phase 7.5a). The simulator is always listed first.</summary>
public sealed record MicrophoneInfo(string Id, string Name, bool IsSimulated);

/// <summary>One recorded clip: 16-bit PCM WAV.</summary>
public sealed record RecordedAudio
{
    public byte[] Wav { get; init; } = Array.Empty<byte>();
    public double Seconds { get; init; }
    /// <summary>True for the simulated microphone: a Windows voice spoke typed text.</summary>
    public bool IsSimulated { get; init; }
}

/// <summary>
/// Push-to-talk recording (Phase 7.5a): <see cref="StartAsync"/> when Talk is tapped, <see cref="StopAsync"/> when it is
/// tapped again (or at the time limit). Implementations: the simulator and the tablet microphone (Windows
/// <c>MediaCapture</c>, in the App). Never blocks the UI thread.
/// </summary>
public interface IMicrophone : IAsyncDisposable
{
    MicrophoneInfo Info { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops and returns the clip.</summary>
    Task<RecordedAudio> StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>The simulated microphone: on Stop it returns <see cref="Phrase"/> spoken by a Windows voice.</summary>
public interface ISimulatedMicrophone : IMicrophone
{
    string Phrase { get; set; }
}

/// <summary>Lists microphones (simulator first) and opens one.</summary>
public interface IMicrophoneFactory
{
    Task<IReadOnlyList<MicrophoneInfo>> DiscoverAsync(CancellationToken cancellationToken = default);

    Task<IMicrophone> OpenAsync(MicrophoneInfo info, CancellationToken cancellationToken = default);
}

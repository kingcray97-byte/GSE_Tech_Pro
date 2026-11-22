using GseTechPro.Core.Enums;

namespace GseTechPro.Core.Contracts;

/// <summary>
/// Phase 7.5a speech to text (<c>voice_transcribe</c>, ARCHITECTURE §4.4): the app records a short WAV clip and the local AI
/// service transcribes it with LFM3-Omni's audio input (<see cref="AiServiceProtocol.VoiceTranscribePath"/>). Rules:
/// <list type="bullet">
/// <item>Voice never leaves the tablet: the service never uses a remote route for it.</item>
/// <item>A transcript is only a suggestion: in Tech Chat it fills the question box and is sent only when the technician taps
///   Send; spoken readings (7.5b) are read back and confirmed before anything is saved.</item>
/// <item>The service throws away a transcript longer than the clip could hold (the model answered instead of transcribing).</item>
/// </list>
/// </summary>
public static class Voice
{
    /// <summary>Longest clip, in seconds; recording stops by itself at this length.</summary>
    public const double MaxSeconds = 30.0;

    /// <summary>Sample rate the tablet microphone records at (16-bit mono PCM WAV).</summary>
    public const int SampleRate = 16000;

    /// <summary>Largest WAV the app sends (bytes).</summary>
    public const int MaxAudioBytes = 3 * 1024 * 1024;

    public const string FormatWav = "wav";
}

/// <summary>POST /voice/transcribe request.</summary>
public sealed record VoiceTranscriptionRequest
{
    /// <summary>16-bit PCM WAV, base64.</summary>
    public string AudioBase64 { get; init; } = "";

    public string Format { get; init; } = Voice.FormatWav;

    /// <summary>The clip came from the simulated microphone (said in the answer; never hidden).</summary>
    public bool IsSimulated { get; init; }
}

/// <summary>POST /voice/transcribe response.</summary>
public sealed record VoiceTranscriptionResponse
{
    /// <summary>What was said, or "" when nothing usable was heard (see <see cref="Notes"/>).</summary>
    public string Text { get; init; } = "";

    /// <summary>Rule-based notes from the service (simulated microphone, nothing heard, transcript thrown away).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public double AudioSeconds { get; init; }

    /// <summary>Always the tablet for voice.</summary>
    public AiRoute? Route { get; init; }

    public string? Model { get; init; }
}

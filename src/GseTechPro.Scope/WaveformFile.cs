using System.Buffers.Binary;
using System.Text;
using GseTechPro.Core.Contracts;

namespace GseTechPro.Scope;

/// <summary>
/// Reader and writer for the binary <c>.wfm</c> waveform sample format defined in ADR-011 §6.
/// <para>
/// Layout (all integers and IEEE-754 doubles are little-endian):
/// <list type="bullet">
/// <item><c>byte[8]</c> magic <c>GSEWAV1\0</c> (ASCII)</item>
/// <item><c>int32</c> version = 1</item>
/// <item><c>int32</c> channelCount</item>
/// <item><c>int32</c> sampleCount (per channel)</item>
/// <item><c>double</c> sampleRateHz</item>
/// <item><c>double</c> timebaseSecondsPerDivision</item>
/// <item>Repeated <c>channelCount</c> times: <c>int32</c> channelIndex (1-based) followed by
/// <c>sampleCount × double</c> (probe-corrected volts)</item>
/// </list>
/// </para>
/// </summary>
public static class WaveformFile
{
    public const int FormatVersion = 1;
    public const int MaxChannels = 4;
    /// <summary>ADR-011 §6 cap: 4 channels × depth ≤ 4,000,000 samples (32 MB).</summary>
    public const int MaxTotalSamples = 4_000_000;

    private static ReadOnlySpan<byte> MagicBytes => "GSEWAV1\0"u8;
    private const int HeaderSizeBytes = 8 + 4 + 4 + 4 + 8 + 8; // 36 bytes

    /// <summary>
    /// Writes a <see cref="ScopeCapture"/> to a file path in the ADR-011 §6 <c>.wfm</c> format.
    /// Creates parent directories if they do not exist.
    /// </summary>
    public static async Task WriteAsync(
        string path,
        ScopeCapture capture,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(capture);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using var fileStream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 65536,
            useAsync: true);

        await WriteAsync(fileStream, capture, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a <see cref="ScopeCapture"/> to <paramref name="stream"/> in the ADR-011 §6 <c>.wfm</c> format.
    /// </summary>
    public static async Task WriteAsync(
        Stream stream,
        ScopeCapture capture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(capture);
        cancellationToken.ThrowIfCancellationRequested();

        int channelCount = capture.Channels.Count;
        int sampleCount = capture.SampleCount;

        if (channelCount < 0 || channelCount > MaxChannels)
            throw new ArgumentOutOfRangeException(nameof(capture), $"Channel count ({channelCount}) must be between 0 and {MaxChannels}.");
        if (sampleCount < 0)
            throw new ArgumentOutOfRangeException(nameof(capture), "Sample count cannot be negative.");
        if ((long)channelCount * sampleCount > MaxTotalSamples)
            throw new ArgumentOutOfRangeException(nameof(capture), $"Total sample count ({(long)channelCount * sampleCount}) exceeds the 4M sample cap.");
        if (!double.IsFinite(capture.SampleRateHz) || capture.SampleRateHz <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(capture), "SampleRateHz must be a finite positive number.");
        if (!double.IsFinite(capture.TimebaseSecondsPerDivision) || capture.TimebaseSecondsPerDivision <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(capture), "TimebaseSecondsPerDivision must be a finite positive number.");

        var orderedChannels = capture.Channels
            .OrderBy(kv => kv.Key)
            .ToArray();

        foreach (var (chIndex, samples) in orderedChannels)
        {
            if (chIndex < 1)
                throw new ArgumentException($"Channel index {chIndex} must be 1-based (>= 1).", nameof(capture));
            if (samples is null || samples.Length != sampleCount)
                throw new ArgumentException($"Channel {chIndex} sample array length ({samples?.Length ?? 0}) does not match SampleCount ({sampleCount}).", nameof(capture));
        }

        byte[] header = new byte[HeaderSizeBytes];
        MagicBytes.CopyTo(header.AsSpan(0, 8));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), channelCount);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), sampleCount);
        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(20, 8), capture.SampleRateHz);
        BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(28, 8), capture.TimebaseSecondsPerDivision);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);

        const int ChunkSamples = 4096;
        byte[] chHeader = new byte[4];
        byte[] sampleBuffer = new byte[Math.Min(sampleCount, ChunkSamples) * sizeof(double)];

        foreach (var (chIndex, samples) in orderedChannels)
        {
            BinaryPrimitives.WriteInt32LittleEndian(chHeader, chIndex);
            await stream.WriteAsync(chHeader, cancellationToken).ConfigureAwait(false);

            int offset = 0;
            while (offset < sampleCount)
            {
                int count = Math.Min(ChunkSamples, sampleCount - offset);
                Span<byte> span = sampleBuffer.AsSpan(0, count * sizeof(double));
                for (int i = 0; i < count; i++)
                {
                    BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(i * sizeof(double), sizeof(double)), samples[offset + i]);
                }

                await stream.WriteAsync(sampleBuffer.AsMemory(0, count * sizeof(double)), cancellationToken).ConfigureAwait(false);
                offset += count;
            }
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a <see cref="ScopeCapture"/> from a <c>.wfm</c> file on disk.
    /// </summary>
    public static async Task<ScopeCapture> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var fileStream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65536,
            useAsync: true);

        return await ReadAsync(fileStream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a <see cref="ScopeCapture"/> from <paramref name="stream"/> in the ADR-011 §6 <c>.wfm</c> format.
    /// </summary>
    public static async Task<ScopeCapture> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();

        byte[] header = new byte[HeaderSizeBytes];
        await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);

        if (!header.AsSpan(0, 8).SequenceEqual(MagicBytes))
        {
            string actual = Encoding.ASCII.GetString(header, 0, 8);
            throw new InvalidDataException($"Invalid .wfm magic header '{actual}'. Expected 'GSEWAV1\\0'.");
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));
        if (version != FormatVersion)
            throw new InvalidDataException($"Unsupported .wfm version {version}. Expected {FormatVersion}.");

        int channelCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12, 4));
        int sampleCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16, 4));
        double sampleRateHz = BinaryPrimitives.ReadDoubleLittleEndian(header.AsSpan(20, 8));
        double timebase = BinaryPrimitives.ReadDoubleLittleEndian(header.AsSpan(28, 8));

        if (channelCount < 0 || channelCount > MaxChannels)
            throw new InvalidDataException($"Invalid channel count {channelCount} in .wfm header.");
        if (sampleCount < 0)
            throw new InvalidDataException($"Invalid sample count {sampleCount} in .wfm header.");
        if ((long)channelCount * sampleCount > MaxTotalSamples)
            throw new InvalidDataException($"Total sample count {(long)channelCount * sampleCount} exceeds maximum allowed ({MaxTotalSamples}).");
        if (!double.IsFinite(sampleRateHz) || sampleRateHz <= 0.0)
            throw new InvalidDataException($"Invalid sample rate {sampleRateHz} in .wfm header.");
        if (!double.IsFinite(timebase) || timebase <= 0.0)
            throw new InvalidDataException($"Invalid timebase {timebase} in .wfm header.");

        var channels = new Dictionary<int, double[]>(channelCount);
        const int ChunkSamples = 4096;
        byte[] chHeader = new byte[4];
        byte[] sampleBuffer = new byte[Math.Min(sampleCount, ChunkSamples) * sizeof(double)];

        for (int c = 0; c < channelCount; c++)
        {
            await ReadExactAsync(stream, chHeader, cancellationToken).ConfigureAwait(false);
            int channelIndex = BinaryPrimitives.ReadInt32LittleEndian(chHeader);
            if (channelIndex < 1)
                throw new InvalidDataException($"Invalid 1-based channel index {channelIndex} in .wfm file.");

            double[] samples = new double[sampleCount];
            int offset = 0;
            while (offset < sampleCount)
            {
                int count = Math.Min(ChunkSamples, sampleCount - offset);
                int bytesToRead = count * sizeof(double);
                await ReadExactAsync(stream, sampleBuffer.AsMemory(0, bytesToRead), cancellationToken).ConfigureAwait(false);

                ReadOnlySpan<byte> span = sampleBuffer.AsSpan(0, bytesToRead);
                for (int i = 0; i < count; i++)
                {
                    samples[offset + i] = BinaryPrimitives.ReadDoubleLittleEndian(span.Slice(i * sizeof(double), sizeof(double)));
                }
                offset += count;
            }

            channels[channelIndex] = samples;
        }

        return new ScopeCapture
        {
            SampleCount = sampleCount,
            SampleRateHz = sampleRateHz,
            TimebaseSecondsPerDivision = timebase,
            Channels = channels,
        };
    }

    private static async Task ReadExactAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.Slice(totalRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException($"Unexpected end of .wfm stream after {totalRead} of {buffer.Length} bytes.");
            totalRead += read;
        }
    }
}
